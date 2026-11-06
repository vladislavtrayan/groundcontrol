namespace GroundControl.Docker;

/// <summary>The test process run within the tests container.</summary>
internal static class ContainerProcess
{
    /// <summary>
    /// Runs the assembly by dotnet with the arguments, with the input lines in a file (its path in the given environment variable),
    /// and reports the process ID and its output lines.
    /// </summary>
    /// <returns>The end of the standard error of the process.</returns>
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
        // The test runs share the container, so each has a file of its own.
        var inputLinesPath = $"/tmp/groundcontrol/{Guid.NewGuid():N}.txt";
        var (_, error) = await docker.RunAsync(
            container,
            container.Exec(assemblyPath[..assemblyPath.LastIndexOf('/')], ["dotnet", assemblyPath, .. arguments], [$"{inputLinesVariable}={inputLinesPath}"]),
            new Dictionary<string, string> { [inputLinesPath] = string.Concat(inputLines.Select(line => line + '\n')) },
            onStarted,
            onOutput,
            cancellationToken);
        return error;
    }
}
