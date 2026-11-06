namespace GroundControl.Docker;

/// <summary>A test process run within the tests container (see run.sh).</summary>
internal static class ContainerProcess
{
    /// <summary>How often the test host renews the lease of its test run, which run.sh kills the test process once not renewed for 15 seconds.</summary>
    private static readonly TimeSpan LeaseRenewal = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs the assembly by dotnet with the arguments, with the input lines in a file (its path in the given environment variable),
    /// and reports the process ID within the container and the output lines; the process is killed on cancellation.
    /// </summary>
    /// <returns>The end of the standard error of the process.</returns>
    /// <remarks>
    /// Each test run has a directory of its own within the container (shared by test runs, and kept up if declared): its input lines and its
    /// lease, which the test host renews while the test run is in progress, so the test process is killed if the test host dies.
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

        ContainerExec process;
        using (await container.PrepareAsync(docker, cancellationToken))
        {
            await docker.WriteFilesAsync(
                container.Name,
                new Dictionary<string, string> { [inputLinesPath] = string.Concat(inputLines.Select(line => line + '\n')), [lease] = "0" },
                cancellationToken);
            process = await docker.StartExecAsync(
                container.Name,
                container.Exec(assemblyPath[..assemblyPath.LastIndexOf('/')], run, ["dotnet", assemblyPath, .. arguments], [$"{inputLinesVariable}={inputLinesPath}"]),
                cancellationToken);
        }

        using var renewing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewAsync(docker, container.Name, lease, renewing.Token);
        try
        {
            // run.sh writes the process ID first to the standard error.
            var processId = (int?)null;
            var errors = new List<string>();
            await using var registration = cancellationToken.Register(() =>
            {
                if (processId is { } running)
                {
                    _ = docker.StartDetachedAsync(container.Name, ["sh", "-c", $"kill {running}"]);
                }
            });
            await process.ReadAsync(
                onOutput,
                line =>
                {
                    if (processId is null && int.TryParse(line, out var started))
                    {
                        processId = started;
                        onStarted(started);
                    }
                    else
                    {
                        errors.Add(line);
                    }
                },
                cancellationToken);
            return string.Join(Environment.NewLine, errors.TakeLast(50));
        }
        finally
        {
            await renewing.CancelAsync();
            await renewal;
            process.Dispose();
            await container.ReleaseAsync(docker);
        }
    }

    /// <summary>Renews the lease of the test run (a counter) until cancelled; a renewal failing is retried.</summary>
    private static async Task RenewAsync(DockerEngine docker, string container, string lease, CancellationToken cancellationToken)
    {
        for (var renewal = 1; !cancellationToken.IsCancellationRequested; renewal++)
        {
            try
            {
                await Task.Delay(LeaseRenewal, cancellationToken);
                await docker.WriteFilesAsync(container, new Dictionary<string, string> { [lease] = renewal.ToString() }, cancellationToken);
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
