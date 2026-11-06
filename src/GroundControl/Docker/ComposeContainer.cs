using Docker.DotNet.Models;

namespace GroundControl.Docker;

/// <summary>The labels Docker Compose gives its containers.</summary>
internal static class ComposeLabels
{
    public const string Project = "com.docker.compose.project";
    public const string Service = "com.docker.compose.service";
    public const string ProjectDirectory = "com.docker.compose.project.working_dir";
    public const string OneOff = "com.docker.compose.oneoff";

    /// <summary>The hash of the configuration of a container Docker Compose has created (for a service of the project), not of others.</summary>
    public const string ConfigHash = "com.docker.compose.config-hash";

    public static string? Get(IDictionary<string, string>? labels, string name) => labels is not null && labels.TryGetValue(name, out var value) ? value : null;
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
            ComposeLabels.Get(labels, ComposeLabels.ConfigHash) is not null,
            container.Mounts?
                .Where(mount => mount.Type == "bind")
                .Select(mount => (HostPath.FromMount(mount.Source, windowsHost), mount.Destination))
                .ToList() ?? []);
    }
}
