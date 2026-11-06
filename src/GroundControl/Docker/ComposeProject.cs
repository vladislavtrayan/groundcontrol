using Docker.DotNet.Models;
using GroundControl.Configuration;

namespace GroundControl.Docker;

/// <summary>
/// The Docker Compose project of the tests: the containers labeled by Docker Compose with the project directory (the closest directory
/// of the test assembly containing a Compose file, unless configured), or with the project name if configured.
/// </summary>
internal sealed class ComposeProject(DockerEngine docker, GroundControlOptions options, string directory)
{
    private static readonly string[] ComposeFiles = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    public string Directory => directory;

    /// <summary>
    /// The project of the configured directory (relative to the test assembly), otherwise of the closest directory of the test assembly
    /// containing a Compose file.
    /// </summary>
    public static ComposeProject Find(DockerEngine docker, GroundControlOptions options, string assemblyPath)
    {
        var assemblyDirectory = Path.GetDirectoryName(assemblyPath);
        if (options.ProjectDirectory is { } projectDirectory)
        {
            return new ComposeProject(docker, options, assemblyDirectory is null ? Path.GetFullPath(projectDirectory) : Path.GetFullPath(projectDirectory, assemblyDirectory));
        }

        var current = assemblyDirectory;
        while (current is not null && !ComposeFiles.Any(file => File.Exists(Path.Combine(current, file))))
        {
            current = Path.GetDirectoryName(current);
        }

        return current is not null
            ? new ComposeProject(docker, options, current)
            : throw new InvalidOperationException($"Docker Compose file ({string.Join(", ", ComposeFiles)}) is not found in directories of {assemblyPath}.");
    }

    /// <summary>
    /// The container for the test run on the network of the project (the one most services are on, the default network of the project),
    /// once the project is ready and no other test run is in progress.
    /// </summary>
    public async Task<TestContainer> GetTestContainerAsync(string assemblyPath, CancellationToken cancellationToken)
    {
        var containers = await this.GetContainersAsync(cancellationToken);
        var running = containers.Where(container => container.State == "running").ToList();
        var network = options.Network
            ?? running
                .Where(container => container.Service != options.Container)
                .SelectMany(container => container.Networks)
                .GroupBy(name => name)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault()
            ?? throw new InvalidOperationException($"Docker Compose project in {directory} is not running, start it: docker compose up --detach --build");
        await this.WaitUntilReadyAsync(containers, cancellationToken);

        var project = running.Select(container => container.Project).FirstOrDefault(name => name.Length > 0) ?? Path.GetFileName(directory).ToLowerInvariant();
        return new TestContainer(project, options.Container, options.Image ?? TestContainer.GetImage(assemblyPath), network, directory);
    }

    /// <summary>
    /// Waits until the services of the project are healthy and its one-off services (e.g., migrations) have completed, as services do not
    /// wait for each other: Docker Compose dependency conditions disable debugging of services in IDEs.
    /// </summary>
    /// <remarks>
    /// Test runs of the project run one at a time: the services reach the tests container by the service name, so there is one.
    /// </remarks>
    private async Task WaitUntilReadyAsync(List<ComposeContainer> containers, CancellationToken cancellationToken)
    {
        if (options.ReadinessTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var deadline = DateTime.UtcNow + options.ReadinessTimeout;
        while (true)
        {
            var tests = containers.Where(container => container.Service == options.Container).ToList();
            var waiting = containers.Except(tests).Where(container => !container.IsReady).ToList();
            if (waiting.Count == 0 && tests.Count == 0)
            {
                return;
            }

            var failed = waiting.Where(container => container.HasFailed).ToList();
            if (failed.Count > 0 || DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException(
                    $"Docker Compose project in {directory} is not ready: {string.Join(", ", (failed.Count > 0 ? failed : waiting.Concat(tests)).Select(container => $"{container.Name} ({container.Status})"))}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            containers = await this.GetContainersAsync(cancellationToken);
        }
    }

    private Task<List<ComposeContainer>> GetContainersAsync(CancellationToken cancellationToken) =>
        docker.GetComposeContainersAsync(options.Project, directory, cancellationToken);
}

/// <summary>A container of the Docker Compose project, as inspected.</summary>
internal sealed record ComposeContainer(string Id, string Name, string Project, string Service, string State, string Health, long ExitCode, IReadOnlyList<string> Networks)
{
    /// <summary>Healthy, running without a healthcheck, or completed successfully (one-off services).</summary>
    public bool IsReady =>
        this.Health == "healthy" || (this.State == "running" && this.Health.Length == 0) || (this.State == "exited" && this.ExitCode == 0);

    public bool HasFailed => this.Health == "unhealthy" || (this.State is "exited" or "dead" && this.ExitCode != 0);

    public string Status => this.Health.Length > 0 ? $"{this.State}, {this.Health}" : this.State == "exited" ? $"exited with {this.ExitCode}" : this.State;

    public static ComposeContainer From(ContainerInspectResponse container)
    {
        var labels = container.Config?.Labels;
        return new ComposeContainer(
            container.ID,
            container.Name.TrimStart('/'),
            DockerEngine.Label(labels, DockerEngine.ProjectLabel) ?? string.Empty,
            DockerEngine.Label(labels, DockerEngine.ServiceLabel) ?? string.Empty,
            container.State?.Status ?? string.Empty,
            container.State?.Health?.Status is { } health && health != "none" ? health : string.Empty,
            container.State?.ExitCode ?? 0,
            container.NetworkSettings?.Networks?.Keys.ToList() ?? []);
    }
}
