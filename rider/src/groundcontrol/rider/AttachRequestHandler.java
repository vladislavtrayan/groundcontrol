package groundcontrol.rider;

import com.intellij.openapi.application.ApplicationManager;
import com.intellij.openapi.diagnostic.Logger;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.project.ProjectManager;
import io.netty.channel.ChannelHandlerContext;
import io.netty.handler.codec.http.FullHttpRequest;
import io.netty.handler.codec.http.HttpResponseStatus;
import io.netty.handler.codec.http.QueryStringDecoder;
import org.jetbrains.ide.HttpRequestHandler;
import org.jetbrains.io.Responses;

import java.nio.file.Path;
import java.util.Arrays;
import java.util.List;

// Attaches the debugger to a test process within a container on request of Remote Test Framework:
// GET /groundcontrol/attach?repository=<path>&container=<name>&pid=<process ID>
public final class AttachRequestHandler extends HttpRequestHandler {
    private static final Logger LOG = Logger.getInstance(AttachRequestHandler.class);
    private static final String PREFIX = "groundcontrol";

    @Override
    public boolean isSupported(FullHttpRequest request) {
        return checkPrefix(request.uri(), PREFIX);
    }

    @Override
    public boolean process(QueryStringDecoder decoder, FullHttpRequest request, ChannelHandlerContext context) {
        if (!decoder.path().equals("/" + PREFIX + "/attach")) {
            return false;
        }

        var repository = parameter(decoder, "repository");
        var container = parameter(decoder, "container");
        var pid = parameter(decoder, "pid");
        var project = repository == null ? null : findProject(Path.of(repository));
        if (project == null || container == null || pid == null) {
            // Another IDE instance may have the repository open.
            Responses.sendPlainText(HttpResponseStatus.NOT_FOUND, context.channel(), request, "Repository is not open: " + repository);
            return true;
        }

        request.retain();
        ApplicationManager.getApplication().executeOnPooledThread(() -> {
            try {
                ContainerAttacher.attach(project, container, Integer.parseInt(pid));
                Responses.sendPlainText(HttpResponseStatus.OK, context.channel(), request, "Attached");
            } catch (Throwable e) {
                LOG.warn("Unable to attach to process " + pid + " in container " + container, e);
                Responses.sendPlainText(HttpResponseStatus.INTERNAL_SERVER_ERROR, context.channel(), request, e.toString());
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

    // The open project within the repository (the solution directory may be the repository itself or within it).
    private static Project findProject(Path repository) {
        return Arrays.stream(ProjectManager.getInstance().getOpenProjects())
            .filter(project -> project.getBasePath() != null)
            .filter(project -> {
                var basePath = Path.of(project.getBasePath()).toAbsolutePath().normalize();
                var path = repository.toAbsolutePath().normalize();
                return basePath.toString().equalsIgnoreCase(path.toString()) || basePath.startsWith(path) || path.startsWith(basePath);
            })
            .findFirst()
            .orElse(null);
    }
}
