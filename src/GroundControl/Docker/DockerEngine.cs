using System.Formats.Tar;
using System.Net;
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

    /// <summary>The containers having the label, as inspected.</summary>
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

    /// <summary>Creates the container.</summary>
    /// <returns>False if a container of its name exists.</returns>
    public async Task<bool> CreateContainerAsync(CreateContainerParameters parameters, CancellationToken cancellationToken)
    {
        try
        {
            await this.client.Containers.CreateContainerAsync(parameters, cancellationToken);
            return true;
        }
        catch (DockerApiException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public Task StartContainerAsync(string container, CancellationToken cancellationToken) =>
        this.client.Containers.StartContainerAsync(container, new ContainerStartParameters(), cancellationToken);

    /// <summary>Removes the container, killing its processes, unless it is removed already (or being removed).</summary>
    public async Task RemoveContainerAsync(string container, CancellationToken cancellationToken)
    {
        try
        {
            await this.client.Containers.RemoveContainerAsync(container, new ContainerRemoveParameters { Force = true }, cancellationToken);
        }
        catch (DockerApiException e) when (e is DockerContainerNotFoundException || e.StatusCode == HttpStatusCode.Conflict)
        {
            // Removed already, or being removed.
        }
    }

    public Task ConnectNetworkAsync(string network, string container, IList<string> aliases, CancellationToken cancellationToken) =>
        this.client.Networks.ConnectNetworkAsync(
            network, new NetworkConnectParameters { Container = container, EndpointConfig = new EndpointSettings { Aliases = aliases } }, cancellationToken);

    /// <summary>Pulls the image unless present.</summary>
    public async Task PullImageAsync(string image, CancellationToken cancellationToken)
    {
        try
        {
            await this.client.Images.InspectImageAsync(image, cancellationToken);
        }
        catch (DockerImageNotFoundException)
        {
            var tag = image.LastIndexOf(':') is var colon && colon > image.LastIndexOf('/') ? colon : -1;
            await this.client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = tag < 0 ? image : image[..tag], Tag = tag < 0 ? "latest" : image[(tag + 1)..] },
                null,
                new Progress<JSONMessage>(),
                cancellationToken);
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

    /// <summary>The number of processes running within the container by docker exec.</summary>
    public async Task<int> CountRunningExecsAsync(ContainerInspectResponse container, CancellationToken cancellationToken)
    {
        var running = 0;
        foreach (var exec in container.ExecIDs ?? [])
        {
            try
            {
                if ((await this.client.Exec.InspectContainerExecAsync(exec, cancellationToken)).Running)
                {
                    running++;
                }
            }
            catch (DockerApiException)
            {
                // Completed meanwhile.
            }
        }

        return running;
    }

    public void Dispose() => this.client.Dispose();
}
