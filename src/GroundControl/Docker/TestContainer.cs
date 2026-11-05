using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GroundControl.Docker;

// A container running test processes, with the test assemblies of the host mounted into it.
internal abstract class TestContainer(string name, IReadOnlyList<(string Source, string Destination)> mounts)
{
    public abstract string Id { get; }

    public string Name => name;

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

    // The host path of a bind mount source as reported by Docker: Docker Desktop on Windows reports Windows paths
    // as mounted into its virtual machine (/run/desktop/mnt/host/c/..., or /mnt/c/... by its WSL integration).
    internal static string ToHostPath(string source, bool windowsHost) =>
        windowsHost && DesktopMount.Match(source) is { Success: true } mount
            ? $"{mount.Groups[1].Value.ToUpperInvariant()}:\\{mount.Groups[2].Value.Replace('/', '\\')}"
            : source;

    private static readonly Regex DesktopMount = new("^/(?:run/desktop/mnt/host|mnt)/([A-Za-z])(?:/(.*))?$", RegexOptions.Compiled);
}

// The container of a Docker Compose service (Service setting), kept running between test runs: processes are run by docker exec.
internal sealed class ServiceTestContainer(string id, string name, IReadOnlyList<(string Source, string Destination)> mounts)
    : TestContainer(name, mounts)
{
    public override string Id => id;

    public static async Task<ServiceTestContainer> InspectAsync(DockerClient docker, string id, string name, CancellationToken cancellationToken)
    {
        var output = await docker.GetOutputAsync(["inspect", "--format", "{{json .Mounts}}", id], cancellationToken);
        var mounts = JsonDocument.Parse(string.Concat(output)).RootElement.EnumerateArray()
            .Where(mount => mount.GetProperty("Type").GetString() == "bind")
            .Select(mount => (ToHostPath(mount.GetProperty("Source").GetString()!, OperatingSystem.IsWindows()), mount.GetProperty("Destination").GetString()!))
            .ToList();
        return new ServiceTestContainer(id, name, mounts);
    }

    public override IReadOnlyList<string> Run(string workingDirectory) =>
        ["exec", "--interactive", "--workdir", workingDirectory, id];

    public override IReadOnlyList<string>? Stop(int? processId) => processId is { } running ? ["exec", id, "kill", running.ToString()] : null;
}

// A container created for a test run on the network of the Docker Compose project, with the project directory mounted,
// and removed once the run completes: the test process is its main process (docker run --rm).
internal sealed class TransientTestContainer(string image, string network, string projectDirectory)
    : TestContainer($"groundcontrol-{Guid.NewGuid():N}"[..25], [(projectDirectory, Workspace)])
{
    private const string Workspace = "/workspace";

    public override string Id => this.Name;

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
        "run", "--rm", "--interactive", "--init", "--name", this.Name, "--network", network,
        "--mount", $"type=bind,source={projectDirectory},target={Workspace},readonly",
        "--workdir", workingDirectory, image,
    ];

    public override IReadOnlyList<string> Stop(int? processId) => ["rm", "--force", this.Name];
}
