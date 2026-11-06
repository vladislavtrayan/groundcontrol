using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using Version = System.Version;

namespace GroundControl.Docker;

/// <summary>
/// The container the test runs run within, as on a host, on the network of the Docker Compose project, where the services reach the tests
/// by the service name (<see cref="GroundControl.Configuration.GroundControlOptions.Container"/>). It is either declared by the project
/// as a service (kept up with the project), or managed by GroundControl: created for the test runs and removed after the last of them.
/// </summary>
internal sealed class TestContainer
{
    private const string Workspace = "/workspace";

    /// <summary>
    /// The volume keeping the debugger tools Rider deploys into a managed container on the first attach, so the next containers have them.
    /// </summary>
    private const string DebuggerToolsVolume = "groundcontrol-debugger-tools";
    private const string DebuggerToolsDirectory = "/root/.RiderDebuggerTools";

    /// <summary>How often the test host renews the lease of its test run (see <see cref="RunScript"/>).</summary>
    public static readonly TimeSpan LeaseRenewal = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs the command in the background with the directory of the test run (its first argument), writes its process ID first
    /// to its standard error (to attach the debugger), and kills it once the lease of the test run within the directory has not been renewed
    /// for 15 seconds (the test host has died), so it does not hold the ports of its fixtures; the directory is removed once it completes.
    /// </summary>
    /// <remarks>
    /// Every process the script starts is waited for, its watchdog's sleep included, so none is left to the first process of the container
    /// (e.g., <c>sleep infinity</c> of a declared service without an init process), which would not reap it.
    /// </remarks>
    private const string RunScript =
        """
        run=$1; shift
        "$@" & tests=$!
        echo $tests >&2
        (
            trap 'kill $nap 2> /dev/null; wait $nap; exit' TERM
            last=
            while :; do
                sleep 15 & nap=$!
                wait $nap
                current=$(cat "$run/lease" 2> /dev/null)
                [ "$current" != "$last" ] || { kill $tests 2> /dev/null; exit; }
                last=$current
            done
        ) &
        watchdog=$!
        wait $tests; status=$?
        kill $watchdog 2> /dev/null; wait $watchdog
        rm -rf "$run"
        exit $status
        """;

    private static readonly Regex DesktopMount = new("^/(?:run/desktop/mnt/host|mnt)/([A-Za-z])(?:/(.*))?$", RegexOptions.Compiled);

    private readonly string project;
    private readonly string projectDirectory;
    private readonly IReadOnlyList<(string Source, string Destination)> mounts;

    private TestContainer(
        string name, string project, string service, string image, string network, string projectDirectory, IReadOnlyList<(string Source, string Destination)> mounts, bool declared)
    {
        this.Name = name;
        this.project = project;
        this.Service = service;
        this.Image = image;
        this.Network = network;
        this.projectDirectory = projectDirectory;
        this.mounts = mounts;
        this.Declared = declared;
    }

    public string Name { get; }

    public string Service { get; }

    /// <summary>The image of a managed container.</summary>
    public string Image { get; }

    public string Network { get; }

    /// <summary>Whether the project declares the container as a service, so GroundControl neither creates nor removes it.</summary>
    public bool Declared { get; }

    /// <summary>
    /// The container GroundControl manages for the test runs of the project: named as the service in the project, with the project directory
    /// mounted as <c>/workspace</c>.
    /// </summary>
    public static TestContainer Managed(string project, string service, string image, string network, string projectDirectory) =>
        new($"{project}-{service}-1", project, service, image, network, projectDirectory, [(projectDirectory, Workspace)], declared: false);

    /// <summary>The container of the tests service the project declares, with the host directories mounted into it.</summary>
    public static TestContainer OfService(string name, string service, string network, IReadOnlyList<(string Source, string Destination)> mounts) =>
        new(name, string.Empty, service, string.Empty, network, string.Empty, mounts, declared: true);

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

    /// <summary>The shared frameworks the test assembly runs on (its runtime configuration), with their versions.</summary>
    public static List<(string Name, Version Version)> GetFrameworks(string assemblyPath)
    {
        var runtimeConfig = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        if (!File.Exists(runtimeConfig))
        {
            return [];
        }

        var options = JsonNode.Parse(File.ReadAllText(runtimeConfig))?["runtimeOptions"];
        IEnumerable<JsonNode?> frameworks = options?["frameworks"] is JsonArray array
            ? array
            : options?["framework"] is { } framework ? new[] { framework } : Array.Empty<JsonNode?>();
        return frameworks
            .Select(framework => (Name: framework?["name"]?.GetValue<string>(), Version: ParseVersion(framework?["version"]?.GetValue<string>())))
            .Where(framework => framework.Name is not null && framework.Version is not null)
            .Select(framework => (framework.Name!, framework.Version!))
            .ToList();
    }

    /// <summary>
    /// The frameworks (e.g., <c>Microsoft.AspNetCore.App 10.0</c>) the runtimes (the output of <c>dotnet --list-runtimes</c>) do not provide:
    /// a runtime of the framework of the same major version, at least the one of the test assembly (as .NET rolls forward by default).
    /// </summary>
    public static List<string> MissingFrameworks(IEnumerable<(string Name, Version Version)> frameworks, string runtimes)
    {
        var available = runtimes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' '))
            .Where(parts => parts.Length >= 2 && ParseVersion(parts[1]) is not null)
            .Select(parts => (Name: parts[0], Version: ParseVersion(parts[1])!))
            .ToList();
        return frameworks
            .Where(framework => !available.Any(runtime =>
                runtime.Name == framework.Name && runtime.Version.Major == framework.Version.Major && runtime.Version >= framework.Version))
            .Select(framework => $"{framework.Name} {framework.Version.Major}.{framework.Version.Minor}")
            .ToList();
    }

    /// <summary>
    /// Docker Desktop on Windows reports bind mount sources as mounted into its virtual machine (<c>/run/desktop/mnt/host/c/...</c>,
    /// <c>/mnt/c/...</c> by WSL).
    /// </summary>
    public static string ToHostPath(string source, bool windowsHost) =>
        windowsHost && DesktopMount.Match(source) is { Success: true } mount
            ? $"{mount.Groups[1].Value.ToUpperInvariant()}:\\{mount.Groups[2].Value.Replace('/', '\\')}"
            : source;

    /// <summary>The managed container, kept running (by sleep) while test runs are in progress within it.</summary>
    public CreateContainerParameters Create() => new()
    {
        Name = this.Name,
        Image = this.Image,
        Cmd = ["sleep", "infinity"],
        Hostname = this.Service,
        Labels = new Dictionary<string, string>
        {
            [DockerEngine.ProjectLabel] = this.project,
            [DockerEngine.ServiceLabel] = this.Service,
            [DockerEngine.ProjectDirectoryLabel] = this.projectDirectory,
            ["com.docker.compose.oneoff"] = "False",
        },
        HostConfig = new HostConfig
        {
            Init = true,
            Mounts =
            [
                new Mount { Type = "bind", Source = this.projectDirectory, Target = Workspace, ReadOnly = true },
                new Mount { Type = "volume", Source = DebuggerToolsVolume, Target = DebuggerToolsDirectory },
            ],
        },
        NetworkingConfig = new NetworkingConfig
        {
            EndpointsConfig = new Dictionary<string, EndpointSettings> { [this.Network] = new EndpointSettings { Aliases = [this.Service] } },
        },
    };

    /// <summary>
    /// The process running the command of a test run within the container (see <see cref="RunScript"/>) with the directory of the test run
    /// and the environment (given the name the services reach the tests by).
    /// </summary>
    public ContainerExecCreateParameters Exec(string workingDirectory, string run, IEnumerable<string> command, IEnumerable<string> environment) => new()
    {
        // The line endings of the source file depend on its checkout.
        Cmd = ["sh", "-c", RunScript.ReplaceLineEndings("\n"), "sh", run, .. command],
        Env = [$"GROUNDCONTROL_CONTAINER={this.Service}", .. environment],
        WorkingDir = workingDirectory,
        AttachStdout = true,
        AttachStderr = true,
    };

    /// <summary>Whether the existing container serves the managed one: running, with its image and its project directory.</summary>
    public bool Fits(ContainerInspectResponse container) =>
        container.State?.Running == true
        && container.Config?.Image == this.Image
        && DockerEngine.Label(container.Config.Labels, DockerEngine.ProjectDirectoryLabel) is { } mounted
        && DockerEngine.SamePath(mounted, this.projectDirectory);

    /// <summary>Maps a path of the host to the container, by the mount of a directory containing it (the closest one).</summary>
    public string MapPath(string path)
    {
        foreach (var (source, destination) in this.mounts.OrderByDescending(mount => mount.Source.Length))
        {
            var relative = Path.GetRelativePath(source, path);
            if (relative != path && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}") && !Path.IsPathRooted(relative))
            {
                return relative == "." ? destination : $"{destination.TrimEnd('/')}/{relative.Replace('\\', '/')}";
            }
        }

        throw new InvalidOperationException(this.Declared
            ? $"The {this.Service} service does not mount the directory of {path}: mount the project directory into it, e.g., '- .:/workspace:ro' in its volumes."
            : $"{path} is not within the Docker Compose project directory {this.projectDirectory} mounted into the tests container.");
    }

    private static Version? ParseVersion(string? value) => value is not null && Version.TryParse(value.Split('-')[0], out var version) ? version : null;
}
