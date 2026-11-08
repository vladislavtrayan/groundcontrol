using Docker.DotNet.Models;

namespace GroundControl.Docker;

/// <summary>The labels Docker Compose gives its containers.</summary>
internal static class ComposeLabels
{
    public const string Project = "com.docker.compose.project";
    public const string Service = "com.docker.compose.service";
    public const string ProjectDirectory = "com.docker.compose.project.working_dir";

    public static string? Get(IDictionary<string, string>? labels, string name) => labels is not null && labels.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A container of the Docker Compose project, as inspected, with the host directories mounted into it (bind mounts) and their paths within it.
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
    IReadOnlyList<(string Source, string Destination)> Mounts)
{
    /// <summary>Healthy, running without a healthcheck, or completed successfully (one-off services).</summary>
    public bool IsReady =>
        this.Health == "healthy" || (this.State == "running" && this.Health.Length == 0) || (this.State == "exited" && this.ExitCode == 0);

    public bool HasFailed => this.Health == "unhealthy" || (this.State is "exited" or "dead" && this.ExitCode != 0);

    public string Status => this.Health.Length > 0 ? $"{this.State}, {this.Health}" : this.State == "exited" ? $"exited with {this.ExitCode}" : this.State;

    public static ComposeContainer From(ContainerInspectResponse container, bool windowsHost)
    {
        var labels = container.Config?.Labels;
        return new ComposeContainer(
            container.ID,
            container.Name.TrimStart('/'),
            ComposeLabels.Get(labels, ComposeLabels.Project) ?? string.Empty,
            ComposeLabels.Get(labels, ComposeLabels.Service) ?? string.Empty,
            container.State?.Status ?? string.Empty,
            container.State?.Health?.Status is { } health && health != "none" ? health : string.Empty,
            container.State?.ExitCode ?? 0,
            container.NetworkSettings?.Networks?.Keys.ToList() ?? [],
            container.Mounts?
                .Where(mount => mount.Type == "bind")
                .Select(mount => (HostPath.FromMount(mount.Source, windowsHost), mount.Destination))
                .ToList() ?? []);
    }
}
