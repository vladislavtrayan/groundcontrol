using System.Diagnostics;

namespace GroundControl.Debugging;

// Attaches the debugger to a test process within the tests container, which waits for it when tests are debugged:
// by the IDE integration (Rider), otherwise manually (the process is reported in the debug output of the IDE).
internal static class Debuggers
{
    public static async Task AttachAsync(DebugTarget target, CancellationToken cancellationToken)
    {
        try
        {
            if (await RiderDebugger.AttachAsync(target, cancellationToken))
            {
                return;
            }
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            Log($"Rider has failed to attach the debugger: {e.Message}");
        }

        Log($"Tests wait for the debugger: attach to process {target.ProcessId} in container {target.ContainerName} (e.g., Rider: Run | Attach to Process, Docker).");
    }

    public static void Log(string message) => Debugger.Log(0, nameof(GroundControlTestFramework), message + Environment.NewLine);
}

// The test process to attach the debugger to, the Docker Compose project directory of the tests and the test assembly (e.g., to find the IDE project).
internal sealed record DebugTarget(string ContainerId, string ContainerName, int ProcessId, string ProjectDirectory, string AssemblyPath);
