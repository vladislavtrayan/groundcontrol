using Docker.DotNet.Models;

namespace GroundControl.Docker;

/// <summary>
/// The container the test runs run within, as on a host, on the network of the Docker Compose project, where the services reach the tests
/// by the service name (<see cref="GroundControl.Configuration.GroundControlOptions.Container"/>): either the one GroundControl manages
/// for the test runs (<see cref="ManagedTestContainer"/>), or the one of the tests service the project declares (<see cref="ServiceTestContainer"/>).
/// </summary>
internal abstract class TestContainer(string name, string service, string network, IReadOnlyList<(string Source, string Destination)> mounts)
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

    /// <summary>Prepares the container for a test run; the handle is held until the test process has started within it.</summary>
    public virtual Task<IDisposable> PrepareAsync(DockerEngine docker, CancellationToken cancellationToken) => Task.FromResult<IDisposable>(NoHandle.Instance);

    /// <summary>Releases the container once a test run has completed within it.</summary>
    public virtual Task ReleaseAsync(DockerEngine docker) => Task.CompletedTask;

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

        throw new InvalidOperationException(this.NotMounted(path));
    }

    /// <summary>The message of a path of the host not mounted into the container.</summary>
    protected abstract string NotMounted(string path);

    private sealed class NoHandle : IDisposable
    {
        public static readonly NoHandle Instance = new();

        public void Dispose()
        {
        }
    }
}
