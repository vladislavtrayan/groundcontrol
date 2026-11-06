using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using GroundControl.Configuration;

namespace GroundControl.Docker;

// The Docker Engine, by its API at the endpoint of the Docker CLI context (DockerContext setting, DOCKER_CONTEXT, the current one)
// or the configured host (DockerHost setting, DOCKER_HOST).
internal sealed class DockerEngine : IDisposable
{
    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";
    public const string ProjectDirectoryLabel = "com.docker.compose.project.working_dir";

    private readonly DockerClient client;

    public DockerEngine(GroundControlOptions options)
    {
        this.Endpoint = ResolveEndpoint(options.DockerHost, options.DockerContext, ConfigDirectory, OperatingSystem.IsWindows());
        this.client = new DockerClientConfiguration(new Uri(this.Endpoint)).CreateClient();
    }

    public string Endpoint { get; }

    private static string ConfigDirectory =>
        Environment.GetEnvironmentVariable("DOCKER_CONFIG") is { Length: > 0 } directory
            ? directory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");

    // The endpoint of the Docker Engine API as the Docker CLI resolves it: the host, otherwise the context (its endpoint in the context store).
    internal static string ResolveEndpoint(string? host, string? context, string configDirectory, bool windows)
    {
        if ((host ?? Environment.GetEnvironmentVariable("DOCKER_HOST")) is { Length: > 0 } configured)
        {
            return Normalize(configured);
        }

        var name = context ?? (Environment.GetEnvironmentVariable("DOCKER_CONTEXT") is { Length: > 0 } variable ? variable : CurrentContext(configDirectory));
        if (name is null or "default")
        {
            return windows ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock";
        }

        var meta = Path.Combine(configDirectory, "contexts", "meta", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant(), "meta.json");
        if (!File.Exists(meta))
        {
            throw new InvalidOperationException($"Docker context {name} is not found in {Path.Combine(configDirectory, "contexts")}.");
        }

        return JsonDocument.Parse(File.ReadAllText(meta)).RootElement.GetProperty("Endpoints").GetProperty("docker").GetProperty("Host").GetString() is { Length: > 0 } endpoint
            ? Normalize(endpoint)
            : throw new InvalidOperationException($"Docker context {name} has no Docker endpoint ({meta}).");
    }

    // The containers of the Docker Compose project: by its name if configured, otherwise the ones of the project directory.
    public async Task<List<ComposeContainer>> GetComposeContainersAsync(string? project, string directory, CancellationToken cancellationToken)
    {
        IList<ContainerListResponse> containers;
        try
        {
            containers = await this.client.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>> { ["label"] = new Dictionary<string, bool> { [ProjectLabel] = true } },
                },
                cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or IOException)
        {
            throw new InvalidOperationException($"Docker Engine at {this.Endpoint} is not reachable: {e.Message}", e);
        }

        var result = new List<ComposeContainer>();
        foreach (var container in containers)
        {
            var ofProject = project is not null
                ? Label(container.Labels, ProjectLabel) == project
                : Label(container.Labels, ProjectDirectoryLabel) is { } projectDirectory && SamePath(projectDirectory, directory);
            if (ofProject)
            {
                result.Add(ComposeContainer.From(await this.client.Containers.InspectContainerAsync(container.ID, cancellationToken)));
            }
        }

        return result;
    }

    // Removes the container, killing its process.
    public async Task RemoveContainerAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await this.client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, cancellationToken);
        }
        catch (DockerApiException e) when (e is DockerContainerNotFoundException || e.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Removed already, or being removed (AutoRemove).
        }
    }

    // Runs a container (pulling its image if absent) with the files (by their paths within the container) and reports the process ID
    // of its command and its output lines; returns its exit code and the end of its standard error. The container is removed once it
    // exits (killed on cancellation); if the test host dies, its command runs to completion first.
    public async Task<(long ExitCode, string Error)> RunAsync(
        CreateContainerParameters parameters,
        IReadOnlyDictionary<string, string> files,
        Action<int> onStarted,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        await this.PullImageAsync(parameters.Image, cancellationToken);
        parameters.HostConfig.AutoRemove = true;
        var id = (await this.client.Containers.CreateContainerAsync(parameters, cancellationToken)).ID;
        try
        {
            await this.client.Containers.ExtractArchiveToContainerAsync(id, new ContainerPathStatParameters { Path = "/" }, Archive(files), cancellationToken);

            var errors = new List<string>();
            var output = new LineBuffer(onOutput);
            var error = new LineBuffer(errors.Add);
            using var stream = await this.client.Containers.AttachContainerAsync(
                id, false, new ContainerAttachParameters { Stream = true, Stdout = true, Stderr = true }, cancellationToken);
            await this.client.Containers.StartContainerAsync(id, new ContainerStartParameters(), cancellationToken);
            await using var registration = cancellationToken.Register(() => _ = this.RemoveContainerAsync(id, CancellationToken.None));
            var exit = this.client.Containers.WaitContainerAsync(id, CancellationToken.None);
            if (await this.FindProcessAsync(id, parameters.Cmd[0], cancellationToken) is { } processId)
            {
                onStarted(processId);
            }

            var buffer = new byte[16384];
            try
            {
                while (await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken) is { EOF: false } read)
                {
                    (read.Target == MultiplexedStream.TargetStream.StandardError ? error : output).Append(buffer.AsSpan(0, read.Count));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Killed.
            }

            output.Flush();
            error.Flush();
            long exitCode;
            try
            {
                exitCode = cancellationToken.IsCancellationRequested ? -1 : (await exit).StatusCode;
            }
            catch (DockerApiException)
            {
                exitCode = -1;
            }

            return (exitCode, string.Join(Environment.NewLine, errors.TakeLast(50)));
        }
        finally
        {
            await this.RemoveContainerAsync(id, CancellationToken.None);
        }
    }

    // The process ID of the command of the container (its first process but the init one), once it runs.
    private async Task<int?> FindProcessAsync(string id, string command, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(command);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            ContainerProcessesResponse processes;
            try
            {
                processes = await this.client.Containers.ListProcessesAsync(id, new ContainerListProcessesParameters { PsArgs = "-o pid,comm" }, cancellationToken);
            }
            catch (DockerApiException)
            {
                return null;
            }

            var pid = processes.Titles.IndexOf("PID");
            var comm = processes.Titles.IndexOf("COMMAND");
            if (pid >= 0 && comm >= 0 && processes.Processes.FirstOrDefault(process => process[comm] == name) is { } process && int.TryParse(process[pid], out var processId))
            {
                return processId;
            }

            await Task.Delay(100, cancellationToken);
        }

        return null;
    }

    private static MemoryStream Archive(IReadOnlyDictionary<string, string> files)
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
        return archive;
    }

    public void Dispose() => this.client.Dispose();

    public static string? Label(IDictionary<string, string>? labels, string name) => labels is not null && labels.TryGetValue(name, out var value) ? value : null;

    private async Task PullImageAsync(string image, CancellationToken cancellationToken)
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

    private static string? CurrentContext(string configDirectory)
    {
        var config = Path.Combine(configDirectory, "config.json");
        return File.Exists(config) && JsonDocument.Parse(File.ReadAllText(config)).RootElement.TryGetProperty("currentContext", out var context)
            ? context.GetString()
            : null;
    }

    // The context store writes named pipes as npipe:////./pipe/name, the client reads npipe://./pipe/name.
    private static string Normalize(string endpoint) => endpoint.Replace("npipe:////./", "npipe://./", StringComparison.Ordinal);

    private static bool SamePath(string path, string other) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(path),
            Path.TrimEndingDirectorySeparator(other),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // Lines of a stream read in chunks (split before decoding, so multi-byte characters are kept whole).
    private sealed class LineBuffer(Action<string> onLine)
    {
        private readonly MemoryStream pending = new();

        public void Append(ReadOnlySpan<byte> bytes)
        {
            this.pending.Write(bytes);
            var buffer = this.pending.GetBuffer().AsSpan(0, (int)this.pending.Length);
            var consumed = 0;
            for (var newLine = buffer.IndexOf((byte)'\n'); newLine >= 0; newLine = buffer[consumed..].IndexOf((byte)'\n'))
            {
                onLine(Encoding.UTF8.GetString(buffer[consumed..(consumed + newLine)]).TrimEnd('\r'));
                consumed += newLine + 1;
            }

            var remainder = buffer[consumed..].ToArray();
            this.pending.SetLength(0);
            this.pending.Write(remainder);
        }

        public void Flush()
        {
            if (this.pending.Length > 0)
            {
                onLine(Encoding.UTF8.GetString(this.pending.GetBuffer(), 0, (int)this.pending.Length).TrimEnd('\r'));
                this.pending.SetLength(0);
            }
        }
    }
}
