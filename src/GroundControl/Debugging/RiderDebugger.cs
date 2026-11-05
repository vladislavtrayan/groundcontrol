using System.Diagnostics;
using System.Net;

namespace GroundControl.Debugging;

// Asks Rider to attach its debugger to the test process (GroundControl Rider plugin, see rider/build.ps1).
internal sealed class RiderDebugger : IGroundControlDebugger
{
    // Ports of the IDE built-in server.
    private const int FirstPort = 63342;
    private const int LastPort = 63361;

    public async Task<bool> AttachAsync(GroundControlDebugTarget target, CancellationToken cancellationToken)
    {
        // Attaching deploys the debugger tools into the container first.
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // The IDE project is found by the Docker Compose project directory or the test assembly (see AttachRequestHandler of the plugin).
        var query = $"repository={Uri.EscapeDataString(target.ProjectDirectory)}&assembly={Uri.EscapeDataString(target.AssemblyPath)}"
            + $"&container={Uri.EscapeDataString(target.ContainerName)}&pid={target.ProcessId}";
        for (var port = FirstPort; port <= LastPort; port++)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/groundcontrol/attach?{query}", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    Log($"Debugger has been attached to process {target.ProcessId} in container {target.ContainerName}.");
                    return true;
                }

                // Other IDE instances (and IDEs without the plugin) do not have the project open.
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    Log($"Debugger has not been attached: {await response.Content.ReadAsStringAsync(cancellationToken)}");
                    return false;
                }
            }
            catch (HttpRequestException)
            {
                // No IDE on the port.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The IDE on the port is attaching (e.g., deploying the debugger tools) but has not done within the timeout.
                Log($"Debugger has not been attached by Rider (port {port}) within {client.Timeout}; attach manually if it is not.");
                return false;
            }
        }

        return false;
    }

    private static void Log(string message) => Debugger.Log(0, nameof(GroundControlTestFramework), message + Environment.NewLine);
}
