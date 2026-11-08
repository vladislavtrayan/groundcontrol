using System.Diagnostics;
using System.Net;

namespace GroundControl.Debugging;

/// <summary>
/// The test process to attach the debugger to, the Docker Compose project directory of the tests and the test assembly (e.g., to find the IDE project).
/// </summary>
internal sealed record DebugTarget(string ContainerName, int ProcessId, string ProjectDirectory, string AssemblyPath);

/// <summary>Asks Rider to attach its debugger to the test process (GroundControl Rider plugin, see rider/build.ps1).</summary>
internal static class RiderDebugger
{
    /// <summary>The first port of the IDE built-in server.</summary>
    private const int FirstPort = 63342;

    /// <summary>The last port of the IDE built-in server.</summary>
    private const int LastPort = 63361;

    /// <summary>Asks the Rider instance having the project open to attach its debugger to the test process.</summary>
    /// <returns>False if Rider is not running with the plugin and the project open, so the debugger is attached manually.</returns>
    /// <remarks>Never throws (called without being awaited): a failure is logged to the debugger instead.</remarks>
    public static async Task<bool> AttachAsync(DebugTarget target, CancellationToken cancellationToken)
    {
        try
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
                    Log($"Debugger has not been attached by Rider (port {port}) within {client.Timeout}; attach manually if it is not.");
                    return false;
                }
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e)
        {
            Log($"Debugger has not been attached: {e}");
            return false;
        }
    }

    public static void Log(string message) => Debugger.Log(
        0, nameof(GroundControlTestFramework), message + Environment.NewLine);
}