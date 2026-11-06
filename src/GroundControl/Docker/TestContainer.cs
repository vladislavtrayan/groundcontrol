using System.Text.Json.Nodes;
using Docker.DotNet.Models;

namespace GroundControl.Docker;

/// <summary>
/// The container of the test runs within the Docker Compose project, named and labeled as its service
/// (<see cref="GroundControl.Configuration.GroundControlOptions.Container"/>), on its network, where the services reach the tests
/// by the service name. The test runs of the project share it, each running its test process within it, as on a host; the project
/// directory is mounted into it.
/// </summary>
internal sealed class TestContainer(string project, string service, string image, string network, string projectDirectory)
{
    private const string Workspace = "/workspace";

    /// <summary>
    /// The volume keeping the debugger tools Rider deploys into the container on the first attach, so the next containers have them.
    /// </summary>
    private const string DebuggerToolsVolume = "groundcontrol-debugger-tools";
    private const string DebuggerToolsDirectory = "/root/.RiderDebuggerTools";

    /// <summary>The name of the container, the one of the service in the project, so the test runs of the project find it.</summary>
    public string Name { get; } = $"{project}-{service}-1";

    public string Service => service;

    public string Image => image;

    public string Network => network;

    /// <summary>The .NET image of the test assembly runtime: ASP.NET Core if the test assembly runs on it, otherwise .NET.</summary>
    public static string GetImage(string assemblyPath)
    {
        var runtimeConfig = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        var options = File.Exists(runtimeConfig) ? JsonNode.Parse(File.ReadAllText(runtimeConfig))?["runtimeOptions"] : null;
        var version = options?["tfm"]?.GetValue<string>() is { } tfm && tfm.StartsWith("net") ? tfm["net".Length..] : $"{Environment.Version.Major}.0";
        var frameworks = options?["frameworks"]?.AsArray().Select(framework => framework?["name"]?.GetValue<string>()) ?? [options?["framework"]?["name"]?.GetValue<string>()];
        return frameworks.Contains("Microsoft.AspNetCore.App")
            ? $"mcr.microsoft.com/dotnet/aspnet:{version}"
            : $"mcr.microsoft.com/dotnet/runtime:{version}";
    }

    /// <summary>The container, kept running (by sleep) while test runs are in progress within it.</summary>
    public CreateContainerParameters Create() => new()
    {
        Name = this.Name,
        Image = image,
        Cmd = ["sleep", "infinity"],
        Hostname = service,
        Labels = new Dictionary<string, string>
        {
            [DockerEngine.ProjectLabel] = project,
            [DockerEngine.ServiceLabel] = service,
            [DockerEngine.ProjectDirectoryLabel] = projectDirectory,
            ["com.docker.compose.oneoff"] = "False",
        },
        HostConfig = new HostConfig
        {
            Init = true,
            Mounts =
            [
                new Mount { Type = "bind", Source = projectDirectory, Target = Workspace, ReadOnly = true },
                new Mount { Type = "volume", Source = DebuggerToolsVolume, Target = DebuggerToolsDirectory },
            ],
        },
        NetworkingConfig = new NetworkingConfig
        {
            EndpointsConfig = new Dictionary<string, EndpointSettings> { [network] = new EndpointSettings { Aliases = [service] } },
        },
    };

    /// <summary>
    /// The process running the command within the container with the environment (given the name the services reach the tests by).
    /// It writes its process ID within the container first to its standard error (by its shell, replaced by the command).
    /// </summary>
    public ContainerExecCreateParameters Exec(string workingDirectory, IEnumerable<string> command, IEnumerable<string> environment) => new()
    {
        Cmd = ["sh", "-c", "echo $$ >&2; exec \"$@\"", "sh", .. command],
        Env = [$"GROUNDCONTROL_CONTAINER={service}", .. environment],
        WorkingDir = workingDirectory,
        AttachStdout = true,
        AttachStderr = true,
    };

    /// <summary>Whether the existing container serves the test run: running, with its image and its project directory.</summary>
    public bool Fits(ContainerInspectResponse container) =>
        container.State?.Running == true
        && container.Config?.Image == image
        && DockerEngine.Label(container.Config.Labels, DockerEngine.ProjectDirectoryLabel) is { } mounted
        && DockerEngine.SamePath(mounted, projectDirectory);

    /// <summary>Maps a path of the host within the project directory to the container.</summary>
    public string MapPath(string path)
    {
        var relative = Path.GetRelativePath(projectDirectory, path);
        return relative != path && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}") && !Path.IsPathRooted(relative)
            ? $"{Workspace}/{relative.Replace('\\', '/')}"
            : throw new InvalidOperationException($"{path} is not within the Docker Compose project directory {projectDirectory} mounted into the tests container.");
    }
}
