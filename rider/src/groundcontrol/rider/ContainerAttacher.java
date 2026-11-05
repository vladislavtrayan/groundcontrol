package groundcontrol.rider;

import com.intellij.execution.process.ProcessInfo;
import com.intellij.openapi.application.ApplicationManager;
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
import kotlinx.coroutines.GlobalScope;

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
            .newInstance(container, GlobalScope.INSTANCE, project);

        // Processes are listed by the debugger tools, which are deployed into the container on the first attach.
        Debuggers debuggers;
        try {
            debuggers = findDebuggers(project, provider, host, pid);
        } catch (Exception e) {
            LOG.info("Unable to find the process in container " + containerName + ": " + e.getMessage());
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

    private static Debuggers findDebuggers(Project project, XAttachDebuggerProvider provider, XAttachHost host, int pid) throws Exception {
        var getProcessList = host.getClass().getMethod("getProcessListAsync", Continuation.class);
        @SuppressWarnings("unchecked")
        var processes = (List<ProcessInfo>) await(continuation -> getProcessList.invoke(host, continuation));
        var process = processes.stream()
            .filter(info -> info.getPid() == pid)
            .findFirst()
            .orElseThrow(() -> new IllegalStateException("Process " + pid + " is not found in the container."));
        return new Debuggers(process, provider.getAvailableDebuggers(project, host, process, new UserDataHolderBase()));
    }

    // Connects Docker servers configured in the IDE and finds the running container by its name.
    private static Object findContainer(Project project, String containerName, ClassLoader loader) throws Exception {
        var serversManagerClass = loader.loadClass("com.intellij.remoteServer.configuration.RemoteServersManager");
        var serversManager = serversManagerClass.getMethod("getInstance").invoke(null);
        var runtimesManagerClass = loader.loadClass("com.intellij.docker.DockerServerRuntimesManager");
        var runtimesManager = runtimesManagerClass.getMethod("getInstance", Project.class).invoke(null, project);
        var remoteServerClass = loader.loadClass("com.intellij.remoteServer.configuration.RemoteServer");
        var getOrCreateConnection = runtimesManagerClass.getMethod("getOrCreateConnection", remoteServerClass);

        var connections = new ArrayList<CompletableFuture<?>>();
        for (var server : (Collection<?>) serversManagerClass.getMethod("getServers").invoke(serversManager)) {
            var type = server.getClass().getMethod("getType").invoke(server);
            if (type.getClass().getName().contains("Docker")) {
                connections.add((CompletableFuture<?>) getOrCreateConnection.invoke(runtimesManager, server));
            }
        }

        if (connections.isEmpty()) {
            throw new IllegalStateException("No Docker connection is configured in the IDE.");
        }

        for (var connection : connections) {
            connection.get(30, TimeUnit.SECONDS);
        }

        // Containers are loaded asynchronously after connecting.
        var deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(30);
        var getConnectedManagers = runtimesManagerClass.getMethod("getConnectedManagers", Project.class);
        while (System.nanoTime() < deadline) {
            for (var manager : (Collection<?>) getConnectedManagers.invoke(null, project)) {
                var containers = (Map<?, ?>) manager.getClass().getMethod("getContainers").invoke(manager);
                for (var container : containers.values()) {
                    var name = (String) container.getClass().getMethod("getContainerName").invoke(container);
                    if (name != null && name.replaceFirst("^/", "").equals(containerName)) {
                        return container;
                    }
                }
            }

            Thread.sleep(500);
        }

        throw new IllegalStateException("Container " + containerName + " is not found by the IDE Docker connections.");
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
}
