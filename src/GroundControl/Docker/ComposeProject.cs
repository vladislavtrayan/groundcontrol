using GroundControl.Configuration;

namespace GroundControl.Docker;

/// <summary>
/// The Docker Compose project of the tests: the containers labeled by Docker Compose with the project name if configured, otherwise with
/// the project directory (the closest directory of the test assembly containing a Compose file, unless configured).
/// </summary>
internal sealed class ComposeProject(DockerEngine docker, GroundControlOptions options, string? directory)
{
    private static readonly string[] ComposeFiles = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    /// <summary>The project name, once known (configured, or of the containers of the project directory), to list the containers by.</summary>
    private string? name = options.Project;

    /// <summary>The project directory, if known: configured, or found by a Compose file (optional if the project name is configured).</summary>
    public string? Directory => directory;

    /// <summary>
    /// The project of the configured directory (relative to the test assembly), otherwise of the closest directory of the test assembly
    /// containing a Compose file; without one, the project of the configured name.
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

        return current is not null || options.Project is not null
            ? new ComposeProject(docker, options, current)
            : throw new InvalidOperationException($"Docker Compose file ({string.Join(", ", ComposeFiles)}) is not found in directories of {assemblyPath}.");
    }

    /// <summary>
    /// The container of the tests service the project declares, once the project is ready: checked to run the tests, and to be on the network
    /// of the project (the one most services are on, the default network of the project).
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
            ?? throw new InvalidOperationException($"{this} is not running, start it: docker compose up --detach --build");
        containers = await this.WaitUntilReadyAsync(containers, cancellationToken);

        var image = DotNetRuntimes.GetImage(assemblyPath);
        var declared = containers.Where(container => container.Service == service).ToList();
        if (declared.FirstOrDefault(container => !container.Declared) is { } stale)
        {
            throw new InvalidOperationException(
                $"Container {stale.Name} of the {service} service has not been created by Docker Compose (e.g., left by GroundControl 0.3): remove it: docker rm --force {stale.Name}");
        }

        switch (declared)
        {
            case []:
                throw new InvalidOperationException(
                    $"{this} does not declare the {service} service, which the tests run within: declare it, e.g., "
                    + $"'{service}: {{ image: {image}, command: [ \"sleep\", \"infinity\" ], volumes: [ \".:/workspace:ro\" ] }}' in its services.");

            case [{ State: "running" } running] when running.Networks.Contains(network):
                var container = new TestContainer(running.Name, service, network, running.Mounts, directory ?? running.ProjectDirectory);
                await container.CheckAsync(docker, assemblyPath, image, cancellationToken);
                return container;

            case [{ State: "running" }]:
                throw new InvalidOperationException(
                    $"The {service} service of the {this} is not on the {network} network of the services: add it to the network.");

            case [var stopped]:
                throw new InvalidOperationException(
                    $"The {service} service of the {this} is not running ({stopped.Status}), start it: docker compose up --detach {service}");

            case var several:
                throw new InvalidOperationException(
                    $"The {service} service of the {this} has {several.Count} containers: scale it to one, as the services reach the tests by its name.");
        }
    }

    public override string ToString() => options.Project is { } project ? $"Docker Compose project {project}" : $"Docker Compose project in {directory}";

    /// <summary>
    /// Waits until the services of the project are healthy and its one-off services (e.g., migrations) have completed, as services do not
    /// wait for each other: Docker Compose dependency conditions disable debugging of services in IDEs.
    /// </summary>
    /// <returns>The containers of the project once ready.</returns>
    /// <remarks>
    /// The tests service is waited for to run, not to be healthy; stopped (exited or dead), it does not start on its own, so it is not waited for.
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
                .Where(container => container.Service == service ? container.State != "running" : !container.IsReady)
                .ToList();
            if (waiting.Count == 0)
            {
                return containers;
            }

            var failed = waiting.Where(container => container.Service == service ? container.State is "exited" or "dead" : container.HasFailed).ToList();
            if (failed.Count > 0 || DateTime.UtcNow > deadline)
            {
                var reported = failed.Count > 0 ? failed : waiting;
                var hint = reported.Any(container => container.Service == service)
                    ? $" Start the {service} service: docker compose up --detach {service}"
                    : string.Empty;
                throw new InvalidOperationException(
                    $"{this} is not ready: {string.Join(", ", reported.Select(container => $"{container.Name} ({container.Status})"))}.{hint}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            containers = await this.GetContainersAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The containers of the services of the project (not one-off ones, of <c>docker compose run</c>): by the project name if known, otherwise
    /// the ones of the project directory, whose project name is then known.
    /// </summary>
    private async Task<List<ComposeContainer>> GetContainersAsync(CancellationToken cancellationToken)
    {
        var label = this.name is { } known ? $"{ComposeLabels.Project}={known}" : ComposeLabels.Project;
        var containers = (await docker.InspectContainersAsync(label, cancellationToken))
            .Select(container => ComposeContainer.From(container, OperatingSystem.IsWindows()))
            .Where(container => !container.OneOff
                && (options.Project is { } project ? container.Project == project : directory is { } known && HostPath.Same(container.ProjectDirectory, known)))
            .ToList();
        this.name ??= containers.Select(container => container.Project).FirstOrDefault(project => project.Length > 0);
        return containers;
    }
}
