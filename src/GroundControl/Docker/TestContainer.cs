using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GroundControl.Docker;

// A container running the test processes, known to the services of the Docker Compose project by the Service name
// (given to the tests in the GROUNDCONTROL_SERVICE variable), with the test assemblies of the host mounted into it.
internal abstract class TestContainer(string name, string service, IReadOnlyList<(string Source, string Destination)> mounts)
{
    public string Name => name;

    public string Service => service;

    // Docker arguments running a process within the container, followed by the command of the process.
    public abstract IReadOnlyList<string> Run(string workingDirectory);

    // Docker arguments stopping the process run within the container, if it can be stopped.
    public abstract IReadOnlyList<string>? Stop(int? processId);

    // Maps a path of the host to the container, by the mount of a directory containing it.
    public string MapPath(string path)
    {
        foreach (var (source, destination) in mounts.OrderByDescending(mount => mount.Source.Length))
        {
            var relative = Path.GetRelativePath(source, path);
            if (relative != path && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}") && !Path.IsPathRooted(relative))
            {
                return $"{destination.TrimEnd('/')}/{relative.Replace('\\', '/')}";
            }
        }

        throw new InvalidOperationException($"{path} is not mounted into the tests container {name}.");
    }

    // Docker Desktop on Windows reports bind mount sources as mounted into its virtual machine (/run/desktop/mnt/host/c/..., /mnt/c/... by WSL).
    internal static string ToHostPath(string source, bool windowsHost) =>
        windowsHost && DesktopMount.Match(source) is { Success: true } mount
            ? $"{mount.Groups[1].Value.ToUpperInvariant()}:\\{mount.Groups[2].Value.Replace('/', '\\')}"
            : source;

    protected string[] Environment => ["--env", $"GROUNDCONTROL_SERVICE={service}"];

    private static readonly Regex DesktopMount = new("^/(?:run/desktop/mnt/host|mnt)/([A-Za-z])(?:/(.*))?$", RegexOptions.Compiled);
}

// The container of the Service of the project, kept running: the test runs of every test assembly run within it (docker exec).
internal sealed class ServiceTestContainer(string id, string name, string service, IReadOnlyList<(string Source, string Destination)> mounts)
    : TestContainer(name, service, mounts)
{
    public static async Task<ServiceTestContainer> InspectAsync(DockerClient docker, string id, string name, string service, CancellationToken cancellationToken)
    {
        var output = await docker.GetOutputAsync(["inspect", "--format", "{{json .Mounts}}", id], cancellationToken);
        var mounts = JsonDocument.Parse(string.Concat(output)).RootElement.EnumerateArray()
            .Where(mount => mount.GetProperty("Type").GetString() == "bind")
            .Select(mount => (ToHostPath(mount.GetProperty("Source").GetString()!, OperatingSystem.IsWindows()), mount.GetProperty("Destination").GetString()!))
            .ToList();
        return new ServiceTestContainer(id, name, service, mounts);
    }

    public override IReadOnlyList<string> Run(string workingDirectory) =>
        ["exec", "--interactive", .. this.Environment, "--workdir", workingDirectory, id];

    public override IReadOnlyList<string>? Stop(int? processId) => processId is { } running ? ["exec", id, "kill", running.ToString()] : null;
}

// A container created for a test run on the network of the project (by the Service name), with the project directory mounted,
// and removed once the run completes: the test process is its main process (docker run --rm).
internal sealed class TransientTestContainer(string image, string network, string service, string projectDirectory)
    : TestContainer($"groundcontrol-{Guid.NewGuid():N}"[..25], service, [(projectDirectory, Workspace)])
{
    private const string Workspace = "/workspace";

    // Rider deploys its debugger tools into the container on the first attach, kept in a volume so later containers have them.
    private const string DebuggerToolsVolume = "groundcontrol-debugger-tools";
    private const string DebuggerToolsDirectory = "/root/.RiderDebuggerTools";

    // The .NET image of the test assembly runtime: ASP.NET Core if the test assembly runs on it, otherwise .NET.
    public static string GetImage(string assemblyPath)
    {
        var runtimeConfig = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        var options = File.Exists(runtimeConfig) ? JsonNode.Parse(File.ReadAllText(runtimeConfig))?["runtimeOptions"] : null;
        var version = options?["tfm"]?.GetValue<string>() is { } tfm && tfm.StartsWith("net") ? tfm["net".Length..] : $"{System.Environment.Version.Major}.0";
        var frameworks = options?["frameworks"]?.AsArray().Select(framework => framework?["name"]?.GetValue<string>()) ?? [options?["framework"]?["name"]?.GetValue<string>()];
        return frameworks.Contains("Microsoft.AspNetCore.App")
            ? $"mcr.microsoft.com/dotnet/aspnet:{version}"
            : $"mcr.microsoft.com/dotnet/runtime:{version}";
    }

    public override IReadOnlyList<string> Run(string workingDirectory) =>
    [
        "run", "--rm", "--interactive", "--init", "--name", this.Name,
        "--network", network, "--network-alias", this.Service, "--hostname", this.Service, .. this.Environment,
        "--mount", $"type=bind,source={projectDirectory},target={Workspace},readonly",
        "--mount", $"type=volume,source={DebuggerToolsVolume},target={DebuggerToolsDirectory}",
        "--workdir", workingDirectory, image,
    ];

    public override IReadOnlyList<string> Stop(int? processId) => ["rm", "--force", this.Name];
}
