using System.Diagnostics;

namespace GroundControl.Debugging;

// Reports the test process to attach the debugger to manually (in the debug output of the IDE).
internal sealed class ManualDebugger : IGroundControlDebugger
{
    public Task<bool> AttachAsync(GroundControlDebugTarget target, CancellationToken cancellationToken)
    {
        Debugger.Log(
            0,
            nameof(GroundControlTestFramework),
            $"Tests wait for the debugger: attach to process {target.ProcessId} in container {target.ContainerName} (e.g., Rider: Run | Attach to Process, Docker).{Environment.NewLine}");
        return Task.FromResult(true);
    }
}
