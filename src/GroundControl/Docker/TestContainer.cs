using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GroundControl.Docker;

// The container of the test runs on the network of the Docker Compose project, kept running: the test runs of every test assembly
// run within it (docker exec), and the services reach the tests by its name. The project directory is mounted into it.
internal sealed class TestContainer(string name, string image, string network, string projectDirectory)
{
    private const string Workspace = "/workspace";

    // Rider deploys its debugger tools into the container on the first attach, kept in a volume so a recreated container has them.
    private const string DebuggerToolsVolume = "groundcontrol-debugger-tools";
    private const string DebuggerToolsDirectory = "/root/.RiderDebuggerTools";

    private static readonly Regex DesktopMount = new("^/(?:run/desktop/mnt/host|mnt)/([A-Za-z])(?:/(.*))?$", RegexOptions.Compiled);

    public string Name => name;

    // The .NET image of the test assembly runtime: ASP.NET Core if the test assembly runs on it, otherwise .NET.
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

    // Docker Desktop on Windows reports bind mount sources as mounted into its virtual machine (/run/desktop/mnt/host/c/..., /mnt/c/... by WSL).
    internal static string ToHostPath(string source, bool windowsHost) =>
        windowsHost && DesktopMount.Match(source) is { Success: true } mount
            ? $"{mount.Groups[1].Value.ToUpperInvariant()}:\\{mount.Groups[2].Value.Replace('/', '\\')}"
            : source;

    // Starts the container on the network, unless it is running with the image and the project directory (recreated otherwise).
    public async Task StartAsync(DockerClient docker, CancellationToken cancellationToken)
    {
        var state = await this.InspectAsync(docker, cancellationToken);
        if (state is not null && !(state.Running && state.Image == image && state.ProjectDirectory is { } mounted && SamePath(mounted, projectDirectory)))
        {
            await docker.GetOutputAsync(["rm", "--force", name], cancellationToken);
            state = null;
        }

        if (state is null)
        {
            await docker.GetOutputAsync(this.Create(), cancellationToken);
        }
        else if (!state.Networks.Contains(network))
        {
            await docker.GetOutputAsync(["network", "connect", network, name], cancellationToken);
        }
    }

    // Docker arguments creating the container, kept running by sleep.
    public IReadOnlyList<string> Create() =>
    [
        "run", "--detach", "--init", "--name", name, "--hostname", name, "--network", network,
        "--mount", $"type=bind,source={projectDirectory},target={Workspace},readonly",
        "--mount", $"type=volume,source={DebuggerToolsVolume},target={DebuggerToolsDirectory}",
        image, "sleep", "infinity",
    ];

    // Docker arguments running a process within the container (given the container name), followed by the command of the process.
    public IReadOnlyList<string> Run(string workingDirectory) =>
        ["exec", "--interactive", "--env", $"GROUNDCONTROL_CONTAINER={name}", "--workdir", workingDirectory, name];

    // Docker arguments killing the process run within the container, once it is known.
    public IReadOnlyList<string>? Stop(int? processId) => processId is { } running ? ["exec", name, "kill", running.ToString()] : null;

    // Maps a path of the host within the project directory to the container.
    public string MapPath(string path)
    {
        var relative = Path.GetRelativePath(projectDirectory, path);
        return relative != path && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}") && !Path.IsPathRooted(relative)
            ? $"{Workspace}/{relative.Replace('\\', '/')}"
            : throw new InvalidOperationException($"{path} is not within the Docker Compose project directory {projectDirectory} mounted into the tests container.");
    }

    private async Task<ContainerState?> InspectAsync(DockerClient docker, CancellationToken cancellationToken)
    {
        var output = new List<string>();
        await docker.RunAsync(["container", "inspect", "--format", "{{json .}}", name], [], output.Add, cancellationToken, throwOnError: false);
        var json = string.Concat(output).Trim();
        return json.Length > 0 && json != "[]" ? ContainerState.Parse(JsonDocument.Parse(json).RootElement, Workspace, OperatingSystem.IsWindows()) : null;
    }

    private static bool SamePath(string path, string other) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

// The state of the tests container (docker container inspect): the directory mounted as the workspace, if any, and the networks it is on.
internal sealed record ContainerState(bool Running, string Image, string? ProjectDirectory, IReadOnlyList<string> Networks)
{
    public static ContainerState Parse(JsonElement inspection, string workspace, bool windowsHost) => new(
        inspection.GetProperty("State").GetProperty("Running").GetBoolean(),
        inspection.GetProperty("Config").GetProperty("Image").GetString() ?? string.Empty,
        inspection.GetProperty("Mounts").EnumerateArray()
            .Where(mount => mount.GetProperty("Destination").GetString() == workspace)
            .Select(mount => TestContainer.ToHostPath(mount.GetProperty("Source").GetString() ?? string.Empty, windowsHost))
            .FirstOrDefault(),
        inspection.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject().Select(network => network.Name).ToList());
}
