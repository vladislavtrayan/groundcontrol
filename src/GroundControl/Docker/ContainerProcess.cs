namespace GroundControl.Docker;

// A process run within the tests container (by a bash script, so its image needs bash).
internal static class ContainerProcess
{
    // Runs the assembly by dotnet with the arguments, with the input lines in a file (its path in the given environment variable),
    // reports the process ID and its output lines, and returns the end of its standard error.
    public static async Task<string> RunAsync(
        DockerClient docker,
        TestContainer container,
        string assemblyPath,
        IEnumerable<string> arguments,
        string inputLinesVariable,
        IReadOnlyCollection<string> inputLines,
        Action<int> onStarted,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        // The input lines are passed by the standard input (null-separated, ending with an empty value). The standard input is kept open
        // while the process runs: once it is closed (e.g., the test process is killed), the process is killed, so the container does not
        // outlive the run (background commands read /dev/null unless redirected, so the standard input is kept as descriptor 3).
        // The process ID is reported first, to attach the debugger.
        var script =
            $$"""
            export {{inputLinesVariable}}=$(mktemp)
            while IFS= read -r -d '' value && [ -n "$value" ]; do printf '%s\n' "$value" >> "${{inputLinesVariable}}"; done
            exec 3<&0
            dotnet "$@" & tests=$!
            echo $tests >&2
            (cat <&3 > /dev/null; kill $tests 2> /dev/null) &
            wait $tests; status=$?
            rm -f "${{inputLinesVariable}}"
            exit $status
            """;
        string[] command =
        [
            .. container.Run(assemblyPath[..assemblyPath.LastIndexOf('/')]),

            // The line endings of the source file depend on its checkout.
            "bash", "-c", script.ReplaceLineEndings("\n"), "bash", assemblyPath, .. arguments,
        ];

        await using var registration = cancellationToken.Register(
            () => _ = docker.RunAsync(container.Stop(), [], _ => { }, CancellationToken.None, throwOnError: false));

        var started = false;
        return await docker.RunAsync(
            command,
            [.. inputLines, string.Empty],
            onOutput,
            cancellationToken,
            line =>
            {
                if (!started && int.TryParse(line, out var processId))
                {
                    started = true;
                    onStarted(processId);
                    return true;
                }

                return false;
            },
            throwOnError: false,
            keepInputOpen: true);
    }
}
