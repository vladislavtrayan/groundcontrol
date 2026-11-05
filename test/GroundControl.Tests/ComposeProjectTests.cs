using GroundControl.Configuration;
using GroundControl.Docker;
using Xunit;

namespace GroundControl.Tests;

public class ComposeProjectTests
{
    private static readonly DockerClient Docker = new("docker", null);

    [Fact]
    public void FindsClosestComposeFile()
    {
        using var repository = new TemporaryDirectory();
        var project = repository.CreateSubdirectory("project");
        File.WriteAllText(Path.Combine(repository.Path, "compose.yaml"), string.Empty);
        File.WriteAllText(Path.Combine(project.FullName, "docker-compose.yml"), string.Empty);
        var assemblyPath = Path.Combine(project.FullName, "bin", "Debug", "net8.0", "Tests.dll");

        Assert.Equal(project.FullName, ComposeProject.Find(Docker, new GroundControlOptions(), assemblyPath).Directory);
    }

    [Fact]
    public void RequiresComposeFile()
    {
        using var repository = new TemporaryDirectory();
        var assemblyPath = Path.Combine(repository.Path, "bin", "Tests.dll");

        Assert.Throws<InvalidOperationException>(() => ComposeProject.Find(Docker, new GroundControlOptions(), assemblyPath));
    }

    // The configured directory is relative to the test assembly, not to the working directory of the test process (an IDE, dotnet test).
    [Fact]
    public void ResolvesProjectDirectoryByAssembly()
    {
        using var repository = new TemporaryDirectory();
        var assemblyPath = Path.Combine(repository.Path, "tests", "bin", "Debug", "net8.0", "Tests.dll");
        var options = new GroundControlOptions { ProjectDirectory = Path.Combine("..", "..", "..", "..", "docker") };

        Assert.Equal(Path.Combine(repository.Path, "docker"), ComposeProject.Find(Docker, options, assemblyPath).Directory);
    }

    [Fact]
    public void KeepsAbsoluteProjectDirectory()
    {
        using var repository = new TemporaryDirectory();
        var options = new GroundControlOptions { ProjectDirectory = repository.Path };

        Assert.Equal(repository.Path, ComposeProject.Find(Docker, options, Path.Combine(Path.GetTempPath(), "Tests.dll")).Directory);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("groundcontrol-");

        public string Path => this.directory.FullName;

        public DirectoryInfo CreateSubdirectory(string name) => this.directory.CreateSubdirectory(name);

        public void Dispose() => this.directory.Delete(recursive: true);
    }
}
