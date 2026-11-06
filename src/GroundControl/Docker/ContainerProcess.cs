namespace GroundControl.Docker;

// The test process run within the tests container.
internal static class ContainerProcess
{
    // Runs the assembly by dotnet with the arguments, with the input lines in a file (its path in the given environment variable),
    // reports the process ID and its output lines, and returns the end of its standard error.
    public static async Task<string> RunAsync(
        DockerEngine docker,
        TestContainer container,
        string assemblyPath,
        IEnumerable<string> arguments,
        string inputLinesVariable,
        IReadOnlyCollection<string> inputLines,
        Action<int> onStarted,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        const string inputLinesPath = "/tmp/groundcontrol/input.txt";
        var (_, error) = await docker.RunAsync(
            container.Create(assemblyPath[..assemblyPath.LastIndexOf('/')], ["dotnet", assemblyPath, .. arguments], [$"{inputLinesVariable}={inputLinesPath}"]),
            new Dictionary<string, string> { [inputLinesPath] = string.Concat(inputLines.Select(line => line + '\n')) },
            onStarted,
            onOutput,
            cancellationToken);
        return error;
    }
}
