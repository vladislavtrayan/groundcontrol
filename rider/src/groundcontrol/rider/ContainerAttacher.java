package groundcontrol.rider;

import com.intellij.execution.process.ProcessInfo;
import com.intellij.openapi.application.ApplicationManager;
import com.intellij.openapi.components.Service;
import com.intellij.openapi.diagnostic.Logger;
import com.intellij.openapi.extensions.ExtensionPointName;
import com.intellij.openapi.progress.EmptyProgressIndicator;
import com.intellij.openapi.progress.ProgressIndicator;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.util.UserDataHolderBase;
import com.intellij.xdebugger.attach.XAttachDebugger;
import com.intellij.xdebugger.attach.XAttachDebuggerProvider;
import com.intellij.xdebugger.attach.XAttachHost;
import kotlin.coroutines.Continuation;
import kotlin.coroutines.EmptyCoroutineContext;
import kotlinx.coroutines.BuildersKt;
import kotlinx.coroutines.CoroutineScope;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.Collection;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

// Attaches Rider's container debugger as "Run | Attach to Process" does for a process within a Docker container.
// Rider's Docker attach support is internal, so it is used by reflection via the class loader of its debugger provider.
final class ContainerAttacher {
    private static final Logger LOG = Logger.getInstance(ContainerAttacher.class);
    private static final ExtensionPointName<XAttachDebuggerProvider> DEBUGGER_PROVIDERS =
        ExtensionPointName.create("com.intellij.xdebugger.attachDebuggerProvider");
    private static final String ATTACH = "com.jetbrains.rider.plugins.appender.docker.attach.";

    private ContainerAttacher() {
    }

    static void attach(Project project, String containerName, int pid) throws Exception {
        var provider = DEBUGGER_PROVIDERS.getExtensionList().stream()
            .filter(extension -> extension.getClass().getName().equals(ATTACH + "RiderContainerAttachDebuggerProvider"))
            .findFirst()
            .orElseThrow(() -> new IllegalStateException("Rider container debugger is not available."));
        var loader = provider.getClass().getClassLoader();

        var container = findContainer(project, containerName, loader);
        var hostClass = loader.loadClass(ATTACH + "attachHosts.RiderDockerContainerAttachHost");
        var host = (XAttachHost) hostClass
            .getConstructor(loader.loadClass("com.intellij.docker.runtimes.DockerApplicationRuntime"), CoroutineScope.class, Project.class)
            .newInstance(container, project.getService(ProjectScope.class).scope, project);

        // Processes are listed by the debugger tools, which are deployed into the container on the first attach.
        Debuggers debuggers;
        try {
            debuggers = findDebuggers(project, provider, host, pid);
        } catch (ProcessNotFoundException e) {
            throw e;
        } catch (Exception e) {
            LOG.info("Unable to list the processes of container " + containerName + ": " + e.getMessage());
            debuggers = null;
        }

        if (debuggers == null || debuggers.debuggers().isEmpty()) {
            LOG.info("Deploying debugger tools into container " + containerName);
            var deployTools = loader.loadClass(ATTACH + "AttachToContainerUtilsKt")
                .getMethod("deployTools", Project.class, XAttachHost.class, ProgressIndicator.class, Continuation.class);
            await(continuation -> deployTools.invoke(null, project, host, new EmptyProgressIndicator(), continuation));
            debuggers = findDebuggers(project, provider, host, pid);
        }

        if (debuggers.debuggers().isEmpty()) {
            throw new IllegalStateException("No debugger is available for process " + pid + " in container " + containerName + ".");
        }

        var debugger = debuggers.debuggers().get(0);
        var process = debuggers.process();
        LOG.info("Attaching " + debugger.getDebuggerDisplayName() + " to process " + pid + " in container " + containerName);
        var failure = new AtomicReference<Exception>();
        ApplicationManager.getApplication().invokeAndWait(() -> {
            try {
                debugger.attachDebugSession(project, host, process);
            } catch (Exception e) {
                failure.set(e);
            }
        });

        if (failure.get() != null) {
            throw failure.get();
        }
    }

    private record Debuggers(ProcessInfo process, List<? extends XAttachDebugger> debuggers) {
    }

    // The process is not among the ones listed (the debugger tools are deployed), so deploying them does not help.
    private static final class ProcessNotFoundException extends IllegalStateException {
        ProcessNotFoundException(int pid) {
            super("Process " + pid + " is not found in the container.");
        }
    }

    private static Debuggers findDebuggers(Project project, XAttachDebuggerProvider provider, XAttachHost host, int pid) throws Exception {
        var getProcessList = method(host, "getProcessListAsync", Continuation.class);
        @SuppressWarnings("unchecked")
        var processes = (List<ProcessInfo>) await(continuation -> getProcessList.invoke(host, continuation));
        var process = processes.stream()
            .filter(info -> info.getPid() == pid)
            .findFirst()
            .orElseThrow(() -> new ProcessNotFoundException(pid));
        return new Debuggers(process, provider.getAvailableDebuggers(project, host, process, new UserDataHolderBase()));
    }

    // Connects Docker servers configured in the IDE (the ones that connect) and finds the running container by its name.
    private static Object findContainer(Project project, String containerName, ClassLoader loader) throws Exception {
        var serversManagerClass = loader.loadClass("com.intellij.remoteServer.configuration.RemoteServersManager");
        var serversManager = serversManagerClass.getMethod("getInstance").invoke(null);
        var runtimesManagerClass = loader.loadClass("com.intellij.docker.DockerServerRuntimesManager");
        var runtimesManager = runtimesManagerClass.getMethod("getInstance", Project.class).invoke(null, project);
        var remoteServerClass = loader.loadClass("com.intellij.remoteServer.configuration.RemoteServer");
        var getType = remoteServerClass.getMethod("getType");
        var getOrCreateConnection = runtimesManagerClass.getMethod("getOrCreateConnection", remoteServerClass);

        var connections = new ArrayList<CompletableFuture<?>>();
        for (var server : (Collection<?>) serversManagerClass.getMethod("getServers").invoke(serversManager)) {
            var type = getType.invoke(server);
            if (type.getClass().getName().contains("Docker")) {
                connections.add((CompletableFuture<?>) getOrCreateConnection.invoke(runtimesManager, server));
            }
        }

        if (connections.isEmpty()) {
            throw new IllegalStateException("No Docker connection is configured in the IDE.");
        }

        // A connection may fail (e.g., a remote Docker which is not reachable): the container is looked up by the other ones.
        var connected = 0;
        Exception failure = null;
        for (var connection : connections) {
            try {
                connection.get(30, TimeUnit.SECONDS);
                connected++;
            } catch (Exception e) {
                LOG.info("Unable to connect a Docker server configured in the IDE: " + e.getMessage());
                failure = e;
            }
        }

        if (connected == 0) {
            throw new IllegalStateException("No Docker connection configured in the IDE is connected.", failure);
        }

        // Containers are loaded asynchronously after connecting.
        var deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(30);
        var getConnectedManagers = runtimesManagerClass.getMethod("getConnectedManagers", Project.class);
        while (System.nanoTime() < deadline) {
            for (var manager : (Collection<?>) getConnectedManagers.invoke(null, project)) {
                var containers = (Map<?, ?>) method(manager, "getContainers").invoke(manager);
                for (var container : containers.values()) {
                    var name = (String) method(container, "getContainerName").invoke(container);
                    if (name != null && name.replaceFirst("^/", "").equals(containerName)) {
                        return container;
                    }
                }
            }

            Thread.sleep(500);
        }

        throw new IllegalStateException("Container " + containerName + " is not found by the IDE Docker connections.");
    }

    // A public method of the object, callable even if its class is not public (e.g., a private Kotlin class).
    private static Method method(Object target, String name, Class<?>... parameterTypes) throws NoSuchMethodException {
        var method = target.getClass().getMethod(name, parameterTypes);
        method.setAccessible(true);
        return method;
    }

    @FunctionalInterface
    private interface Suspending {
        Object invoke(Continuation<Object> continuation) throws Exception;
    }

    // Runs a Kotlin suspend function (called by reflection) on the current thread.
    @SuppressWarnings("unchecked")
    private static Object await(Suspending function) throws Exception {
        return BuildersKt.runBlocking(EmptyCoroutineContext.INSTANCE, (scope, continuation) -> {
            try {
                return function.invoke((Continuation<Object>) continuation);
            } catch (InvocationTargetException e) {
                throw sneaky(e.getCause());
            } catch (Exception e) {
                throw sneaky(e);
            }
        });
    }

    @SuppressWarnings("unchecked")
    private static <T extends Throwable> RuntimeException sneaky(Throwable e) throws T {
        throw (T) e;
    }

    // The coroutine scope of the attach hosts of a project, cancelled with the project (or the plugin), unlike a global one.
    @Service(Service.Level.PROJECT)
    static final class ProjectScope {
        final CoroutineScope scope;

        ProjectScope(Project project, CoroutineScope scope) {
            this.scope = scope;
        }
    }
}
