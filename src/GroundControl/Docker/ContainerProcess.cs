namespace GroundControl.Docker;

/// <summary>The test process run within the tests container.</summary>
internal static class ContainerProcess
{
    /// <summary>
    /// Runs the assembly by dotnet with the arguments, with the input lines in a file (its path in the given environment variable),
    /// and reports the process ID and its output lines.
    /// </summary>
    /// <returns>The end of the standard error of the process.</returns>
    /// <remarks>
    /// Each test run has a directory of its own within the container (shared by test runs, and kept up if declared): its input lines
    /// and its lease, which the test host renews while the test run is in progress, so the test process is killed if the test host dies.
    /// </remarks>
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
        var run = $"/tmp/groundcontrol/{Guid.NewGuid():N}";
        var inputLinesPath = $"{run}/test-cases.txt";
        var lease = $"{run}/lease";
        using var renewing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewAsync(docker, container, lease, renewing.Token);
        try
        {
            var (_, error) = await docker.RunAsync(
                container,
                container.Exec(assemblyPath[..assemblyPath.LastIndexOf('/')], run, ["dotnet", assemblyPath, .. arguments], [$"{inputLinesVariable}={inputLinesPath}"]),
                new Dictionary<string, string> { [inputLinesPath] = string.Concat(inputLines.Select(line => line + '\n')), [lease] = "0" },
                onStarted,
                onOutput,
                cancellationToken);
            return error;
        }
        finally
        {
            await renewing.CancelAsync();
            await renewal;
        }
    }

    /// <summary>Renews the lease of the test run (a counter) until cancelled; a renewal failing (e.g., the container not started yet) is retried.</summary>
    private static async Task RenewAsync(DockerEngine docker, TestContainer container, string lease, CancellationToken cancellationToken)
    {
        for (var renewal = 1; !cancellationToken.IsCancellationRequested; renewal++)
        {
            try
            {
                await Task.Delay(TestContainer.LeaseRenewal, cancellationToken);
                await docker.WriteFilesAsync(container.Name, new Dictionary<string, string> { [lease] = renewal.ToString() }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is global::Docker.DotNet.DockerApiException or HttpRequestException or IOException or TimeoutException)
            {
                // Retried.
            }
        }
    }
}
