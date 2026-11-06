using Docker.DotNet.Models;

namespace GroundControl.Docker;

/// <summary>
/// The tests container GroundControl manages for the test runs of a project which does not declare the tests service: named and labeled
/// as the service, with the project directory mounted as <c>/workspace</c>, created by a test run and removed by the last of them.
/// </summary>
internal sealed class ManagedTestContainer(string project, string service, string image, string network, string projectDirectory)
    : TestContainer($"{project}-{service}-1", service, network, [(projectDirectory, Workspace)])
{
    private const string Workspace = "/workspace";

    /// <summary>The volume keeping the debugger tools Rider deploys into the container on the first attach, so the next containers have them.</summary>
    private const string DebuggerToolsVolume = "groundcontrol-debugger-tools";
    private const string DebuggerToolsDirectory = "/root/.RiderDebuggerTools";

    public string Image => image;

    /// <summary>
    /// Starts the container unless it runs with the image and the project directory; otherwise, once no test run is in progress within it,
    /// it is replaced (e.g., stopped, or of another runtime of the tests). The handle is the lock of the container among the test processes
    /// of the host, so a test run does not remove it while another one is starting within it.
    /// </summary>
    public override async Task<IDisposable> PrepareAsync(DockerEngine docker, CancellationToken cancellationToken)
    {
        await docker.PullImageAsync(image, cancellationToken);
        var @lock = await this.LockAsync(cancellationToken);
        try
        {
            while (true)
            {
                var existing = await docker.InspectContainerAsync(this.Name, cancellationToken);
                if (existing is null)
                {
                    if (await docker.CreateContainerAsync(this.Create(), cancellationToken))
                    {
                        await docker.StartContainerAsync(this.Name, cancellationToken);
                        return @lock;
                    }
                }
                else if (this.Fits(existing))
                {
                    if (existing.NetworkSettings?.Networks?.ContainsKey(this.Network) != true)
                    {
                        await docker.ConnectNetworkAsync(this.Network, this.Name, [this.Service], cancellationToken);
                    }

                    return @lock;
                }
                else if (await docker.CountRunningExecsAsync(existing, cancellationToken) == 0)
                {
                    await docker.RemoveContainerAsync(existing.ID, cancellationToken);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
        }
        catch
        {
            @lock.Dispose();
            throw;
        }
    }

    /// <summary>Removes the container once no test run is in progress within it.</summary>
    public override async Task ReleaseAsync(DockerEngine docker)
    {
        using var @lock = await this.LockAsync(CancellationToken.None);
        if (await docker.InspectContainerAsync(this.Name, CancellationToken.None) is { } existing
            && await docker.CountRunningExecsAsync(existing, CancellationToken.None) == 0)
        {
            await docker.RemoveContainerAsync(existing.ID, CancellationToken.None);
        }
    }

    /// <summary>The container, kept running (by sleep) while test runs are in progress within it.</summary>
    public CreateContainerParameters Create() => new()
    {
        Name = this.Name,
        Image = image,
        Cmd = ["sleep", "infinity"],
        Hostname = this.Service,
        Labels = new Dictionary<string, string>
        {
            [ComposeLabels.Project] = project,
            [ComposeLabels.Service] = this.Service,
            [ComposeLabels.ProjectDirectory] = projectDirectory,
            [ComposeLabels.OneOff] = "False",
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
            EndpointsConfig = new Dictionary<string, EndpointSettings> { [this.Network] = new EndpointSettings { Aliases = [this.Service] } },
        },
    };

    /// <summary>Whether the existing container serves as this one: running, with its image and its project directory.</summary>
    public bool Fits(ContainerInspectResponse container) =>
        container.State?.Running == true
        && container.Config?.Image == image
        && ComposeLabels.Get(container.Config.Labels, ComposeLabels.ProjectDirectory) is { } mounted
        && HostPath.Same(mounted, projectDirectory);

    protected override string NotMounted(string path) =>
        $"{path} is not within the Docker Compose project directory {projectDirectory} mounted into the tests container.";

    /// <summary>The lock of the container among the test processes of the host: a file opened exclusively.</summary>
    private async Task<IDisposable> LockAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{this.Name}.lock");
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }
}
