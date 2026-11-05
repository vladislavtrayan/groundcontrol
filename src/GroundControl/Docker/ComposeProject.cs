using System.Text.Json;
using GroundControl.Configuration;

namespace GroundControl.Docker;

// The Docker Compose project of the tests, resolved by Docker Compose as `docker compose` does in its directory
// (COMPOSE_PROJECT_NAME, COMPOSE_FILE, .env, the name of the project in Compose files), unless the project is configured.
internal sealed class ComposeProject(DockerClient docker, GroundControlOptions options, string directory)
{
    private static readonly string[] ComposeFiles = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    public string Directory => directory;

    // The configured project directory (relative to the test assembly), otherwise the closest directory of the test assembly containing a Compose file.
    public static ComposeProject Find(DockerClient docker, GroundControlOptions options, string assemblyPath)
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

    // A container for the test run on the network of the project (the one most services are on, the default network of the project),
    // once the project is ready.
    public async Task<TestContainer> GetTestContainerAsync(string assemblyPath, CancellationToken cancellationToken)
    {
        var containers = await this.GetContainersAsync(cancellationToken);
        var network = options.Network
            ?? containers
                .Where(container => container.State == "running")
                .SelectMany(container => container.Networks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .GroupBy(name => name)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault()
            ?? throw new InvalidOperationException($"Docker Compose project in {directory} is not running, start it: docker compose up --detach --build");
        await this.WaitUntilReadyAsync(containers, cancellationToken);
        return new TestContainer(options.Image ?? TestContainer.GetImage(assemblyPath), network, directory);
    }

    // Waits until the services of the project are healthy and its one-off services (e.g., migrations) have completed,
    // as services do not wait for each other: Docker Compose dependency conditions disable debugging of services in IDEs.
    private async Task WaitUntilReadyAsync(List<ComposeContainer> containers, CancellationToken cancellationToken)
    {
        if (options.ReadinessTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var deadline = DateTime.UtcNow + options.ReadinessTimeout;
        while (true)
        {
            var waiting = containers.Where(container => !container.IsReady).ToList();
            if (waiting.Count == 0)
            {
                return;
            }

            var failed = waiting.Where(container => container.HasFailed).ToList();
            if (failed.Count > 0 || DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException(
                    $"Docker Compose project in {directory} is not ready: {string.Join(", ", (failed.Count > 0 ? failed : waiting).Select(container => $"{container.Name} ({container.Status})"))}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            containers = await this.GetContainersAsync(cancellationToken);
        }
    }

    private async Task<List<ComposeContainer>> GetContainersAsync(CancellationToken cancellationToken) =>
        (await docker.GetOutputAsync(this.Compose(["ps", "--all", "--format", "json"]), cancellationToken)).SelectMany(ComposeContainer.Parse).ToList();

    private string[] Compose(string[] arguments) =>
        ["compose", "--project-directory", directory, .. options.Project is { } name ? ["--project-name", name] : Array.Empty<string>(), .. arguments];
}

// A container of the Docker Compose project (docker compose ps --format json).
internal sealed record ComposeContainer(string Id, string Name, string State, string Health, int ExitCode, string Status, string Networks)
{
    // Healthy, running without a healthcheck, or completed successfully (one-off services).
    public bool IsReady =>
        this.Health == "healthy" || (this.State == "running" && this.Health.Length == 0) || (this.State == "exited" && this.ExitCode == 0);

    public bool HasFailed => this.Health == "unhealthy" || (this.State is "exited" or "dead" && this.ExitCode != 0);

    // Docker Compose reports a JSON object per line (a JSON array by its earlier versions).
    public static IEnumerable<ComposeContainer> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        var json = JsonDocument.Parse(line).RootElement;
        return json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().Select(Parse).ToList() : [Parse(json)];
    }

    private static ComposeContainer Parse(JsonElement json) => new(
        Get(json, "ID"),
        Get(json, "Name"),
        Get(json, "State"),
        Get(json, "Health"),
        json.TryGetProperty("ExitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number ? exitCode.GetInt32() : 0,
        Get(json, "Status"),
        Get(json, "Networks"));

    private static string Get(JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
}
