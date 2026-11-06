using System.Text.Json.Nodes;

namespace GroundControl.Docker;

// A container created for a test run on the network of the Docker Compose project, with the project directory mounted,
// and removed once the run completes: the test process is its main process (docker run --rm).
internal sealed class TestContainer(string image, string network, string projectDirectory)
{
    private const string Workspace = "/workspace";

    // Rider deploys its debugger tools into the container on the first attach, kept in a volume so later containers have them.
    private const string DebuggerToolsVolume = "groundcontrol-debugger-tools";
    private const string DebuggerToolsDirectory = "/root/.RiderDebuggerTools";

    public string Name { get; } = $"groundcontrol-{Guid.NewGuid():N}"[..25];

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

    // Maps a path of the host within the project directory to the container.
    public string MapPath(string path)
    {
        var relative = Path.GetRelativePath(projectDirectory, path);
        return relative != path && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}") && !Path.IsPathRooted(relative)
            ? $"{Workspace}/{relative.Replace('\\', '/')}"
            : throw new InvalidOperationException($"{path} is not within the Docker Compose project directory {projectDirectory} mounted into the tests container.");
    }

    // Docker arguments running a process within the container, followed by the command of the process.
    public IReadOnlyList<string> Run(string workingDirectory) =>
    [
        "run", "--rm", "--interactive", "--init", "--name", this.Name, "--network", network,
        "--mount", $"type=bind,source={projectDirectory},target={Workspace},readonly",
        "--mount", $"type=volume,source={DebuggerToolsVolume},target={DebuggerToolsDirectory}",
        "--workdir", workingDirectory, image,
    ];

    // Docker arguments removing the container, which kills the process.
    public IReadOnlyList<string> Stop() => ["rm", "--force", this.Name];
}
