namespace GroundControl.Debugging;

// Attaches a debugger to a test process within the tests container, which waits for it when tests are debugged.
// Implementations integrate IDEs; one is chosen by the Debugger setting (see GroundControlOptions).
public interface IGroundControlDebugger
{
    // Returns false if the debugger is not available (e.g., its IDE is not running), so the next one is tried.
    Task<bool> AttachAsync(GroundControlDebugTarget target, CancellationToken cancellationToken);
}

// The test process to attach the debugger to, and the Docker Compose project directory of the tests (e.g., to find the IDE project).
public sealed record GroundControlDebugTarget(string ContainerId, string ContainerName, int ProcessId, string ProjectDirectory);
