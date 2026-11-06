using System.Text.Json.Nodes;
using Docker.DotNet.Models;

namespace GroundControl.Docker;

/// <summary>
/// A container created for a test run within the Docker Compose project, named and labeled as a one-off of its service
/// (<see cref="GroundControl.Configuration.GroundControlOptions.Container"/>), on its network, where the services reach the tests
/// by the service name. The test process is its main process, and it is removed once the run completes. The project directory is mounted into it.
/// </summary>
internal sealed class TestContainer(string project, string service, string image, string network, string projectDirectory)
{
    private const string Workspace = "/workspace";

    /// <summary>
    /// The volume keeping the debugger tools Rider deploys into the container on the first attach, so the containers of later runs have them.
    /// </summary>
    private const string DebuggerToolsVolume = "groundcontrol-debugger-tools";
    private const string DebuggerToolsDirectory = "/root/.RiderDebuggerTools";

    public string Name { get; } = $"{project}-{service}-run-{Guid.NewGuid():N}"[..(project.Length + service.Length + 14)];

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

    /// <summary>The container running the command with the environment, given the name the services reach it by.</summary>
    public CreateContainerParameters Create(string workingDirectory, IList<string> command, IEnumerable<string> environment) => new()
    {
        Name = this.Name,
        Image = image,
        Cmd = command,
        WorkingDir = workingDirectory,
        Hostname = service,
        Env = [$"GROUNDCONTROL_CONTAINER={service}", .. environment],
        Labels = new Dictionary<string, string>
        {
            [DockerEngine.ProjectLabel] = project,
            [DockerEngine.ServiceLabel] = service,
            ["com.docker.compose.oneoff"] = "True",
        },
        AttachStdout = true,
        AttachStderr = true,
        Tty = false,
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

    /// <summary>Maps a path of the host within the project directory to the container.</summary>
    public string MapPath(string path)
    {
        var relative = Path.GetRelativePath(projectDirectory, path);
        return relative != path && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}") && !Path.IsPathRooted(relative)
            ? $"{Workspace}/{relative.Replace('\\', '/')}"
            : throw new InvalidOperationException($"{path} is not within the Docker Compose project directory {projectDirectory} mounted into the tests container.");
    }
}
