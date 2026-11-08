using Docker.DotNet.Models;

namespace GroundControl.Docker;

/// <summary>
/// The container of the tests service the Docker Compose project declares, which the test runs run within as on a host, kept up with
/// the project (GroundControl neither creates nor removes it), on the network of the project, where the services reach the tests by
/// the service name (<see cref="GroundControl.Configuration.GroundControlOptions.Container"/>); with the project directory (for the debugger).
/// </summary>
internal sealed class TestContainer(string name, string service, string network, IReadOnlyList<(string Source, string Destination)> mounts, string projectDirectory)
{
    /// <summary>The script running a test process within the container (see run.sh).</summary>
    private static readonly Lazy<string> RunScript = new(() =>
    {
        using var script = new StreamReader(typeof(TestContainer).Assembly.GetManifestResourceStream("GroundControl.Docker.run.sh")!);
        return script.ReadToEnd().ReplaceLineEndings("\n");
    });

    public string Name => name;

    public string Service => service;

    public string Network => network;

    /// <summary>The Docker Compose project directory: as found on the host, otherwise as the Docker host sees it (labeled by Docker Compose).</summary>
    public string ProjectDirectory => projectDirectory;

    /// <summary>
    /// Checks that the container runs the tests of the assembly: it mounts the assembly, and has <c>sh</c> (which test processes are run by)
    /// and the runtimes of the assembly; otherwise, the image is suggested.
    /// </summary>
    public async Task CheckAsync(DockerEngine docker, string assemblyPath, string image, CancellationToken cancellationToken)
    {
        this.MapPath(assemblyPath);
        if ((await docker.ExecAsync(this.Name, ["sh", "-c", "exit 0"], cancellationToken)).ExitCode != 0)
        {
            throw new InvalidOperationException($"The {service} service has no sh, which GroundControl runs the tests by: use an image with it, such as {image}.");
        }

        var runtimes = await docker.ExecAsync(this.Name, ["sh", "-c", "dotnet --list-runtimes"], cancellationToken);

        if (DotNetRuntimes.MissingFrameworks(DotNetRuntimes.GetFrameworks(assemblyPath), runtimes.Output) is { Count: > 0 } missing)
        {
            throw new InvalidOperationException($"The {service} service has no {string.Join(" and ", missing)}: use {image}.");
        }
    }

    /// <summary>
    /// The process running the command of a test run within the container (see run.sh), with the directory of the test run
    /// and the environment (given the name the services reach the tests by).
    /// </summary>
    public ContainerExecCreateParameters Exec(string workingDirectory, string run, IEnumerable<string> command, IEnumerable<string> environment) => new()
    {
        Cmd = ["sh", "-c", RunScript.Value, "sh", run, .. command],
        Env = [$"GROUNDCONTROL_CONTAINER={service}", .. environment],
        WorkingDir = workingDirectory,
        AttachStdout = true,
        AttachStderr = true,
    };

    /// <summary>Maps a path of the host to the container, by the mount of a directory containing it (the closest one).</summary>
    public string MapPath(string path)
    {
        foreach (var (source, destination) in mounts.OrderByDescending(mount => mount.Source.Length))
        {
            if (HostPath.Within(source, path) is { } relative)
            {
                return relative == "." ? destination : $"{destination.TrimEnd('/')}/{relative.Replace('\\', '/')}";
            }
        }

        throw new InvalidOperationException(
            $"The {service} service does not mount the directory of {path}: mount the project directory into it, e.g., '- .:/workspace:ro' in its volumes.");
    }
}
