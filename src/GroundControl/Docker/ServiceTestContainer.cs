namespace GroundControl.Docker;

/// <summary>
/// The container of the tests service the Docker Compose project declares, kept up with the project: the test runs run within it
/// as it is, GroundControl neither creating nor removing it.
/// </summary>
internal sealed class ServiceTestContainer(string name, string service, string network, IReadOnlyList<(string Source, string Destination)> mounts)
    : TestContainer(name, service, network, mounts)
{
    /// <summary>
    /// Checks that the container runs the tests of the assembly: it mounts the assembly, and has <c>sh</c> (which test processes are run by)
    /// and the runtimes of the assembly; otherwise, the image is suggested.
    /// </summary>
    public async Task CheckAsync(DockerEngine docker, string assemblyPath, string image, CancellationToken cancellationToken)
    {
        this.MapPath(assemblyPath);
        if ((await docker.ExecAsync(this.Name, ["sh", "-c", "exit 0"], cancellationToken)).ExitCode != 0)
        {
            throw new InvalidOperationException($"The {this.Service} service has no sh, which GroundControl runs the tests by: use an image with it, such as {image}.");
        }

        var runtimes = await docker.ExecAsync(this.Name, ["sh", "-c", "dotnet --list-runtimes"], cancellationToken);
        if (DotNetRuntimes.MissingFrameworks(DotNetRuntimes.GetFrameworks(assemblyPath), runtimes.Output) is { Count: > 0 } missing)
        {
            throw new InvalidOperationException($"The {this.Service} service has no {string.Join(" and ", missing)}: use {image}.");
        }
    }

    protected override string NotMounted(string path) =>
        $"The {this.Service} service does not mount the directory of {path}: mount the project directory into it, e.g., '- .:/workspace:ro' in its volumes.";
}
