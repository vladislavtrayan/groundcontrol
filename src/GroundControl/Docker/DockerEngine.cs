using System.Formats.Tar;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using GroundControl.Configuration;

namespace GroundControl.Docker;

/// <summary>
/// The Docker Engine, by its API at the configured host (<see cref="GroundControlOptions.DockerHost"/>, <c>DOCKER_HOST</c>)
/// or at the endpoint of the Docker CLI context (<see cref="GroundControlOptions.DockerContext"/>, <c>DOCKER_CONTEXT</c>, the current one).
/// </summary>
internal sealed class DockerEngine : IDisposable
{
    private readonly string endpoint;
    private readonly DockerClient client;

    public DockerEngine(GroundControlOptions options)
    {
        this.endpoint = DockerEndpoint.Resolve(options.DockerHost, options.DockerContext, DockerEndpoint.ConfigDirectory, OperatingSystem.IsWindows());
        this.client = new DockerClientConfiguration(new Uri(this.endpoint)).CreateClient();
    }

    /// <summary>The containers having the label (<c>name</c>, or <c>name=value</c>), as inspected.</summary>
    public async Task<List<ContainerInspectResponse>> InspectContainersAsync(string label, CancellationToken cancellationToken)
    {
        IList<ContainerListResponse> containers;
        try
        {
            containers = await this.client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true, Filters = new Dictionary<string, IDictionary<string, bool>> { ["label"] = new Dictionary<string, bool> { [label] = true } } },
                cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or IOException)
        {
            throw new InvalidOperationException($"Docker Engine at {this.endpoint} is not reachable: {e.Message}", e);
        }

        var inspected = new List<ContainerInspectResponse>();
        foreach (var container in containers)
        {
            if (await this.InspectContainerAsync(container.ID, cancellationToken) is { } inspection)
            {
                inspected.Add(inspection);
            }
        }

        return inspected;
    }

    /// <summary>The container (by its name or ID), as inspected, or null if absent.</summary>
    public async Task<ContainerInspectResponse?> InspectContainerAsync(string container, CancellationToken cancellationToken)
    {
        try
        {
            return await this.client.Containers.InspectContainerAsync(container, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Writes the files (by their paths, creating their directories) into the container.</summary>
    public Task WriteFilesAsync(string container, IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
    {
        var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path.TrimStart('/')) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
            }
        }

        archive.Position = 0;
        return this.client.Containers.ExtractArchiveToContainerAsync(container, new ContainerPathStatParameters { Path = "/" }, archive, cancellationToken);
    }

    /// <summary>Starts a process within the container, with its output attached.</summary>
    public async Task<ContainerExec> StartExecAsync(string container, ContainerExecCreateParameters parameters, CancellationToken cancellationToken)
    {
        var exec = (await this.client.Exec.ExecCreateContainerAsync(container, parameters, cancellationToken)).ID;
        return new ContainerExec(await this.client.Exec.StartAndAttachContainerExecAsync(exec, false, cancellationToken));
    }

    /// <summary>Runs the command within the container to completion.</summary>
    /// <returns>Its exit code (-1 if it has not started, e.g., without its executable) and its standard output.</returns>
    public async Task<(long ExitCode, string Output)> ExecAsync(string container, IList<string> command, CancellationToken cancellationToken)
    {
        try
        {
            var exec = (await this.client.Exec.ExecCreateContainerAsync(
                container, new ContainerExecCreateParameters { Cmd = command, AttachStdout = true, AttachStderr = true }, cancellationToken)).ID;
            var output = new StringBuilder();
            using (var process = new ContainerExec(await this.client.Exec.StartAndAttachContainerExecAsync(exec, false, cancellationToken)))
            {
                await process.ReadAsync(line => output.AppendLine(line), _ => { }, cancellationToken);
            }

            return ((await this.client.Exec.InspectContainerExecAsync(exec, cancellationToken)).ExitCode, output.ToString());
        }
        catch (DockerApiException)
        {
            return (-1, string.Empty);
        }
    }

    /// <summary>Starts the command within the container without waiting for it, unless the container is gone.</summary>
    public async Task StartDetachedAsync(string container, IList<string> command)
    {
        try
        {
            var exec = await this.client.Exec.ExecCreateContainerAsync(container, new ContainerExecCreateParameters { Cmd = command });
            await this.client.Exec.StartContainerExecAsync(exec.ID);
        }
        catch (DockerApiException)
        {
            // The container has been removed.
        }
    }

    public void Dispose() => this.client.Dispose();
}
