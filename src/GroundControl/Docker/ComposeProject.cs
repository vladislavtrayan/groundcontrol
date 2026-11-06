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
    /// The tests container, once the project is ready: the one of the tests service if the project declares it (checked to run the tests),
    /// otherwise one GroundControl creates on the network of the project (the one most services are on, the default network of the project).
    /// </summary>
    public async Task<TestContainer> GetTestContainerAsync(string assemblyPath, CancellationToken cancellationToken)
    {
        var service = options.Container;
        var containers = await this.GetContainersAsync(cancellationToken);
        var network = options.Network
            ?? containers
                .Where(container => container.State == "running" && container.Service != service)
                .SelectMany(container => container.Networks)
                .GroupBy(name => name)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault()
            ?? throw new InvalidOperationException($"Docker Compose project in {directory} is not running, start it: docker compose up --detach --build");
        containers = await this.WaitUntilReadyAsync(containers, cancellationToken);

        var image = options.Image ?? TestContainer.GetImage(assemblyPath);
        switch (containers.Where(container => container.Service == service && container.Declared).ToList())
        {
            case []:
                var project = containers.Select(container => container.Project).FirstOrDefault(name => name.Length > 0) ?? Path.GetFileName(directory).ToLowerInvariant();
                return TestContainer.Managed(project, service, image, network, directory);

            case [{ State: "running" } declared]:
                if (!declared.Networks.Contains(network))
                {
                    throw new InvalidOperationException(
                        $"The {service} service of the Docker Compose project in {directory} is not on the {network} network of the services: add it to the network.");
                }

                var container = TestContainer.OfService(declared.Name, service, network, declared.Mounts);
                container.MapPath(assemblyPath);
                await docker.CheckAsync(container, TestContainer.GetFrameworks(assemblyPath), image, cancellationToken);
                return container;

            case [var declared]:
                throw new InvalidOperationException(
                    $"The {service} service of the Docker Compose project in {directory} is not running ({declared.Status}), start it: docker compose up --detach {service}");

            case var declared:
                throw new InvalidOperationException(
                    $"The {service} service of the Docker Compose project in {directory} has {declared.Count} containers: scale it to one, as the services reach the tests by its name.");
        }
    }

    /// <summary>
    /// Waits until the services of the project are healthy and its one-off services (e.g., migrations) have completed, as services do not
    /// wait for each other: Docker Compose dependency conditions disable debugging of services in IDEs.
    /// </summary>
    /// <returns>The containers of the project once ready.</returns>
    /// <remarks>
    /// The tests service, if declared, is waited for to run, not to be healthy. The tests container GroundControl creates is not a service.
    /// </remarks>
    private async Task<List<ComposeContainer>> WaitUntilReadyAsync(List<ComposeContainer> containers, CancellationToken cancellationToken)
    {
        if (options.ReadinessTimeout <= TimeSpan.Zero)
        {
            return containers;
        }

        var service = options.Container;
        var deadline = DateTime.UtcNow + options.ReadinessTimeout;
        while (true)
        {
            var waiting = containers
                .Where(container => container.Service == service ? container.Declared && container.State != "running" : !container.IsReady)
                .ToList();
            if (waiting.Count == 0)
            {
                return containers;
            }

            var failed = waiting.Where(container => container.Service != service && container.HasFailed).ToList();
            if (failed.Count > 0 || DateTime.UtcNow > deadline)
            {
                var hint = failed.Count == 0 && waiting.Any(container => container.Service == service)
                    ? $" Start the {service} service: docker compose up --detach {service}"
                    : string.Empty;
                throw new InvalidOperationException(
                    $"Docker Compose project in {directory} is not ready: {string.Join(", ", (failed.Count > 0 ? failed : waiting).Select(container => $"{container.Name} ({container.Status})"))}.{hint}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            containers = await this.GetContainersAsync(cancellationToken);
        }
    }

    private Task<List<ComposeContainer>> GetContainersAsync(CancellationToken cancellationToken) =>
        docker.GetComposeContainersAsync(options.Project, directory, cancellationToken);
}

/// <summary>
/// A container of the Docker Compose project, as inspected: declared if Docker Compose has created it for a service of the project
/// (rather than GroundControl), and the host directories mounted into it (bind mounts), with their paths within it.
/// </summary>
internal sealed record ComposeContainer(
    string Id,
    string Name,
    string Project,
    string Service,
    string State,
    string Health,
    long ExitCode,
    IReadOnlyList<string> Networks,
    bool Declared,
    IReadOnlyList<(string Source, string Destination)> Mounts)
{
    /// <summary>The label Docker Compose gives the containers it creates (the hash of their configuration), which GroundControl does not.</summary>
    public const string ConfigHashLabel = "com.docker.compose.config-hash";

    /// <summary>Healthy, running without a healthcheck, or completed successfully (one-off services).</summary>
    public bool IsReady =>
        this.Health == "healthy" || (this.State == "running" && this.Health.Length == 0) || (this.State == "exited" && this.ExitCode == 0);

    public bool HasFailed => this.Health == "unhealthy" || (this.State is "exited" or "dead" && this.ExitCode != 0);

    public string Status => this.Health.Length > 0 ? $"{this.State}, {this.Health}" : this.State == "exited" ? $"exited with {this.ExitCode}" : this.State;

    public static ComposeContainer From(ContainerInspectResponse container) => From(container, OperatingSystem.IsWindows());

    internal static ComposeContainer From(ContainerInspectResponse container, bool windowsHost)
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
            container.NetworkSettings?.Networks?.Keys.ToList() ?? [],
            DockerEngine.Label(labels, ConfigHashLabel) is not null,
            container.Mounts?
                .Where(mount => mount.Type == "bind")
                .Select(mount => (TestContainer.ToHostPath(mount.Source, windowsHost), mount.Destination))
                .ToList() ?? []);
    }
}
