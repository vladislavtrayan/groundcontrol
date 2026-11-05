using System.Net;

namespace GroundControl.Debugging;

// Asks Rider to attach its debugger to the test process (GroundControl Rider plugin, see rider/build.ps1).
internal static class RiderDebugger
{
    // Ports of the IDE built-in server.
    private const int FirstPort = 63342;
    private const int LastPort = 63361;

    // Returns false if Rider is not running (with the plugin and the project open), so the debugger is attached manually.
    public static async Task<bool> AttachAsync(DebugTarget target, CancellationToken cancellationToken)
    {
        // Attaching deploys the debugger tools into the container first.
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var query = $"repository={Uri.EscapeDataString(target.ProjectDirectory)}&assembly={Uri.EscapeDataString(target.AssemblyPath)}"
            + $"&container={Uri.EscapeDataString(target.ContainerName)}&pid={target.ProcessId}";
        for (var port = FirstPort; port <= LastPort; port++)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/groundcontrol/attach?{query}", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    Debuggers.Log($"Debugger has been attached to process {target.ProcessId} in container {target.ContainerName}.");
                    return true;
                }

                // Other IDE instances (and IDEs without the plugin) do not have the project open.
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    Debuggers.Log($"Debugger has not been attached: {await response.Content.ReadAsStringAsync(cancellationToken)}");
                    return false;
                }
            }
            catch (HttpRequestException)
            {
                // No IDE on the port.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Debuggers.Log($"Debugger has not been attached by Rider (port {port}) within {client.Timeout}; attach manually if it is not.");
                return false;
            }
        }

        return false;
    }
}
