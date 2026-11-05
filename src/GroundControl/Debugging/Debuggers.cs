using System.Diagnostics;
using GroundControl.Configuration;

namespace GroundControl.Debugging;

// Debugger integrations, tried in order until one attaches the debugger; attaching manually is the last resort.
internal static class Debuggers
{
    // The Debugger setting: Rider, Manual, or the assembly-qualified name of an IGroundControlDebugger implementation;
    // by default, the built-in integrations.
    public static IReadOnlyList<IGroundControlDebugger> Create(GroundControlOptions options) => options.Debugger?.ToLowerInvariant() switch
    {
        null or "rider" => [new RiderDebugger(), new ManualDebugger()],
        "manual" => [new ManualDebugger()],
        _ => [Create(options.Debugger), new ManualDebugger()],
    };

    public static async Task AttachAsync(IReadOnlyList<IGroundControlDebugger> debuggers, GroundControlDebugTarget target, CancellationToken cancellationToken)
    {
        foreach (var debugger in debuggers)
        {
            try
            {
                if (await debugger.AttachAsync(target, cancellationToken))
                {
                    return;
                }
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                Debugger.Log(0, nameof(GroundControlTestFramework), $"{debugger.GetType().Name} has failed: {e.Message}{Environment.NewLine}");
            }
        }
    }

    private static IGroundControlDebugger Create(string typeName) =>
        Activator.CreateInstance(Type.GetType(typeName, throwOnError: true)!) as IGroundControlDebugger
        ?? throw new InvalidOperationException($"{typeName} does not implement {nameof(IGroundControlDebugger)}.");
}
