using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using GroundControl.Configuration;
using Version = System.Version;

namespace GroundControl.Docker;

/// <summary>
/// The Docker Engine, by its API at the configured host (<see cref="GroundControlOptions.DockerHost"/>, <c>DOCKER_HOST</c>)
/// or at the endpoint of the Docker CLI context (<see cref="GroundControlOptions.DockerContext"/>, <c>DOCKER_CONTEXT</c>, the current one).
/// </summary>
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

    /// <summary>
    /// The endpoint of the Docker Engine API as the Docker CLI resolves it: the host, otherwise the endpoint of the context in the context store.
    /// </summary>
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

    /// <summary>The containers of the Docker Compose project: by its name if configured, otherwise the ones of the project directory.</summary>
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

    /// <summary>
    /// Runs the command within the tests container, with the files written into it by their paths, and reports the process ID
    /// of the command (written first to its standard error) and its output lines.
    /// </summary>
    /// <returns>The exit code of the command and the end of its standard error.</returns>
    /// <remarks>
    /// The test runs of the project share the container. A managed one is started if absent (see <see cref="StartAsync"/>) and removed
    /// once the last test run completes; a declared one is kept as it is. The command is killed on cancellation.
    /// </remarks>
    public async Task<(long ExitCode, string Error)> RunAsync(
        TestContainer container,
        ContainerExecCreateParameters command,
        IReadOnlyDictionary<string, string> files,
        Action<int> onStarted,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        string exec;
        MultiplexedStream stream;
        if (container.Declared)
        {
            (exec, stream) = await this.StartProcessAsync(container, command, files, cancellationToken);
        }
        else
        {
            await this.PullImageAsync(container.Image, cancellationToken);
            using (await LockAsync(container.Name, cancellationToken))
            {
                await this.StartAsync(container, cancellationToken);
                (exec, stream) = await this.StartProcessAsync(container, command, files, cancellationToken);
            }
        }

        try
        {
            var processId = (int?)null;
            var errors = new List<string>();
            var output = new LineBuffer(onOutput);
            var error = new LineBuffer(line =>
            {
                if (processId is null && int.TryParse(line, out var started))
                {
                    processId = started;
                    onStarted(started);
                }
                else
                {
                    errors.Add(line);
                }
            });
            await using var registration = cancellationToken.Register(() =>
            {
                if (processId is { } running)
                {
                    _ = this.KillAsync(container.Name, running);
                }
            });

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
            var exitCode = cancellationToken.IsCancellationRequested ? -1 : (await this.client.Exec.InspectContainerExecAsync(exec, CancellationToken.None)).ExitCode;
            return (exitCode, string.Join(Environment.NewLine, errors.TakeLast(50)));
        }
        finally
        {
            stream.Dispose();
            if (!container.Declared)
            {
                await this.ReleaseAsync(container, CancellationToken.None);
            }
        }
    }

    /// <summary>Writes the files (by their paths, creating their directories) into the container.</summary>
    public Task WriteFilesAsync(string container, IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken) =>
        this.client.Containers.ExtractArchiveToContainerAsync(container, new ContainerPathStatParameters { Path = "/" }, Archive(files), cancellationToken);

    /// <summary>
    /// Checks that the declared tests container runs the tests: it has <c>sh</c>, which the test process is run by, and the runtimes
    /// of the frameworks of the test assembly; otherwise, the image is suggested.
    /// </summary>
    public async Task CheckAsync(TestContainer container, IReadOnlyList<(string Name, Version Version)> frameworks, string image, CancellationToken cancellationToken)
    {
        if ((await this.ExecAsync(container.Name, ["sh", "-c", "exit 0"], cancellationToken)).ExitCode != 0)
        {
            throw new InvalidOperationException($"The {container.Service} service has no sh, which GroundControl runs the tests by: use an image with it, such as {image}.");
        }

        var runtimes = await this.ExecAsync(container.Name, ["sh", "-c", "dotnet --list-runtimes"], cancellationToken);
        if (TestContainer.MissingFrameworks(frameworks, runtimes.Output) is { Count: > 0 } missing)
        {
            throw new InvalidOperationException($"The {container.Service} service has no {string.Join(" and ", missing)}: use {image}.");
        }
    }

    public void Dispose() => this.client.Dispose();

    private async Task<(string Exec, MultiplexedStream Stream)> StartProcessAsync(
        TestContainer container, ContainerExecCreateParameters command, IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
    {
        await this.WriteFilesAsync(container.Name, files, cancellationToken);
        var exec = (await this.client.Exec.ExecCreateContainerAsync(container.Name, command, cancellationToken)).ID;
        return (exec, await this.client.Exec.StartAndAttachContainerExecAsync(exec, false, cancellationToken));
    }

    /// <summary>Runs the command within the container to completion.</summary>
    /// <returns>Its exit code (-1 if it has not started, e.g., without its executable) and its standard output.</returns>
    private async Task<(long ExitCode, string Output)> ExecAsync(string container, IList<string> command, CancellationToken cancellationToken)
    {
        try
        {
            var exec = (await this.client.Exec.ExecCreateContainerAsync(
                container, new ContainerExecCreateParameters { Cmd = command, AttachStdout = true, AttachStderr = true }, cancellationToken)).ID;
            using var stream = await this.client.Exec.StartAndAttachContainerExecAsync(exec, false, cancellationToken);
            var output = new StringBuilder();
            var buffer = new byte[4096];
            while (await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken) is { EOF: false } read)
            {
                if (read.Target == MultiplexedStream.TargetStream.StandardOut)
                {
                    output.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
                }
            }

            return ((await this.client.Exec.InspectContainerExecAsync(exec, cancellationToken)).ExitCode, output.ToString());
        }
        catch (DockerApiException)
        {
            return (-1, string.Empty);
        }
    }

    public static string? Label(IDictionary<string, string>? labels, string name) => labels is not null && labels.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Starts the managed tests container unless it runs with the image and the project directory of the test run; otherwise, once no test run
    /// is in progress within it, it is replaced (e.g., stopped, or of another runtime of the tests).
    /// </summary>
    private async Task StartAsync(TestContainer container, CancellationToken cancellationToken)
    {
        while (true)
        {
            var existing = await this.InspectAsync(container.Name, cancellationToken);
            if (existing is null)
            {
                try
                {
                    await this.client.Containers.CreateContainerAsync(container.Create(), cancellationToken);
                }
                catch (DockerApiException e) when (e.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // Created meanwhile (by another host).
                    continue;
                }

                await this.client.Containers.StartContainerAsync(container.Name, new ContainerStartParameters(), cancellationToken);
                return;
            }

            if (container.Fits(existing))
            {
                if (existing.NetworkSettings?.Networks?.ContainsKey(container.Network) != true)
                {
                    await this.client.Networks.ConnectNetworkAsync(
                        container.Network,
                        new NetworkConnectParameters { Container = container.Name, EndpointConfig = new EndpointSettings { Aliases = [container.Service] } },
                        cancellationToken);
                }

                return;
            }

            if (await this.CountRunningAsync(existing, cancellationToken) == 0)
            {
                await this.RemoveAsync(existing.ID, cancellationToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>Removes the tests container once no test run is in progress within it.</summary>
    private async Task ReleaseAsync(TestContainer container, CancellationToken cancellationToken)
    {
        using var @lock = await LockAsync(container.Name, cancellationToken);
        if (await this.InspectAsync(container.Name, cancellationToken) is { } existing && await this.CountRunningAsync(existing, cancellationToken) == 0)
        {
            await this.RemoveAsync(existing.ID, cancellationToken);
        }
    }

    /// <summary>The number of processes running within the container by docker exec (the test runs in progress).</summary>
    private async Task<int> CountRunningAsync(ContainerInspectResponse container, CancellationToken cancellationToken)
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

    private async Task<ContainerInspectResponse?> InspectAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await this.client.Containers.InspectContainerAsync(name, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    private async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await this.client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, cancellationToken);
        }
        catch (DockerApiException e) when (e is DockerContainerNotFoundException || e.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Removed already, or being removed.
        }
    }

    /// <summary>Kills the process within the container (by the kill of its shell, as the image may not have a kill executable).</summary>
    private async Task KillAsync(string container, int processId)
    {
        try
        {
            var exec = await this.client.Exec.ExecCreateContainerAsync(container, new ContainerExecCreateParameters { Cmd = ["sh", "-c", $"kill {processId}"] });
            await this.client.Exec.StartContainerExecAsync(exec.ID);
        }
        catch (DockerApiException)
        {
            // The container has been removed.
        }
    }

    /// <summary>
    /// The lock of the tests container among the test processes of the host (a file opened exclusively), so a test run does not remove
    /// the container while another one is starting within it.
    /// </summary>
    private static async Task<IDisposable> LockAsync(string container, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{container}.lock");
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

    /// <summary>The context store writes named pipes as <c>npipe:////./pipe/name</c>, while the client reads <c>npipe://./pipe/name</c>.</summary>
    private static string Normalize(string endpoint) => endpoint.Replace("npipe:////./", "npipe://./", StringComparison.Ordinal);

    internal static bool SamePath(string path, string other) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(path),
            Path.TrimEndingDirectorySeparator(other),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Lines of a stream read in chunks, split before decoding so multi-byte characters are kept whole.</summary>
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
