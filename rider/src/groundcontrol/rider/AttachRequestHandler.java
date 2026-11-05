package groundcontrol.rider;

import com.intellij.openapi.application.ApplicationManager;
import com.intellij.openapi.diagnostic.Logger;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.project.ProjectManager;
import com.intellij.openapi.roots.ProjectRootManager;
import com.intellij.openapi.util.Computable;
import com.intellij.openapi.vfs.VirtualFile;
import com.intellij.util.ExceptionUtil;
import io.netty.channel.ChannelHandlerContext;
import io.netty.handler.codec.http.FullHttpRequest;
import io.netty.handler.codec.http.HttpMethod;
import io.netty.handler.codec.http.HttpResponseStatus;
import io.netty.handler.codec.http.QueryStringDecoder;
import org.jetbrains.ide.HttpRequestHandler;
import org.jetbrains.io.Responses;

import java.nio.file.Files;
import java.nio.file.InvalidPathException;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

// Attaches the debugger to a test process within a container on request of GroundControl:
// GET /groundcontrol/attach?repository=<Docker Compose project directory>&assembly=<test assembly path>&container=<name>&pid=<process ID>
public final class AttachRequestHandler extends HttpRequestHandler {
    private static final Logger LOG = Logger.getInstance(AttachRequestHandler.class);
    private static final String PREFIX = "groundcontrol";
    private static final List<String> COMPOSE_FILES = List.of("compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml");

    @Override
    public boolean isSupported(FullHttpRequest request) {
        return request.method().equals(HttpMethod.GET) && checkPrefix(request.uri(), PREFIX);
    }

    @Override
    public boolean process(QueryStringDecoder decoder, FullHttpRequest request, ChannelHandlerContext context) {
        if (!decoder.path().equals("/" + PREFIX + "/attach")) {
            return false;
        }

        var repository = path(decoder, "repository");
        var assembly = path(decoder, "assembly");
        var container = parameter(decoder, "container");
        var pid = parameter(decoder, "pid");
        if (repository == null || container == null || pid == null || !pid.matches("[0-9]+")) {
            Responses.sendPlainText(HttpResponseStatus.BAD_REQUEST, context.channel(), request, "Parameters repository, container and pid (a number) are required.");
            return true;
        }

        // Off the I/O thread: attaching takes long.
        request.retain();
        ApplicationManager.getApplication().executeOnPooledThread(() -> {
            try {
                var project = findProject(repository, assembly);
                if (project == null) {
                    // Another IDE instance may have the repository open.
                    Responses.sendPlainText(HttpResponseStatus.NOT_FOUND, context.channel(), request, "Repository is not open: " + repository);
                    return;
                }

                ContainerAttacher.attach(project, container, Integer.parseInt(pid));
                Responses.sendPlainText(HttpResponseStatus.OK, context.channel(), request, "Attached");
            } catch (Throwable e) {
                LOG.warn("Unable to attach to process " + pid + " in container " + container, e);
                Responses.sendPlainText(HttpResponseStatus.INTERNAL_SERVER_ERROR, context.channel(), request, ExceptionUtil.getRootCause(e).toString());
            } finally {
                request.release();
            }
        });
        return true;
    }

    private static String parameter(QueryStringDecoder decoder, String name) {
        List<String> values = decoder.parameters().get(name);
        return values == null || values.isEmpty() ? null : values.get(0);
    }

    private static Path path(QueryStringDecoder decoder, String name) {
        var value = parameter(decoder, name);
        try {
            return value == null ? null : Path.of(value).toAbsolutePath().normalize();
        } catch (InvalidPathException e) {
            return null;
        }
    }

    // The open project whose roots contain the test assembly or the Compose project directory, or which lies within the Compose
    // project directory (one holding a Compose file, so not any ancestor of the solution).
    private static Project findProject(Path repository, Path assembly) {
        var candidates = new ArrayList<Path>();
        if (assembly != null) {
            candidates.add(assembly);
        }

        candidates.add(repository);
        var repositoryHasComposeFile = COMPOSE_FILES.stream().anyMatch(file -> Files.isRegularFile(repository.resolve(file)));
        return Arrays.stream(ProjectManager.getInstance().getOpenProjects())
            .filter(project -> project.getBasePath() != null)
            .filter(project -> {
                var roots = roots(project);
                return candidates.stream().anyMatch(candidate -> roots.stream().anyMatch(candidate::startsWith))
                    || (repositoryHasComposeFile && roots.stream().anyMatch(root -> root.startsWith(repository)));
            })
            .findFirst()
            .orElse(null);
    }

    private static List<Path> roots(Project project) {
        var roots = new ArrayList<Path>();
        roots.add(Path.of(project.getBasePath()).toAbsolutePath().normalize());
        var contentRoots = ApplicationManager.getApplication()
            .runReadAction((Computable<VirtualFile[]>) () -> ProjectRootManager.getInstance(project).getContentRoots());
        for (var root : contentRoots) {
            if (root.isInLocalFileSystem()) {
                roots.add(root.toNioPath().toAbsolutePath().normalize());
            }
        }

        return roots;
    }
}
