using GroundControl.Docker;
using Xunit;

namespace GroundControl.Tests;

public class TestContainerTests
{
    private static readonly string Project = Path.Combine(Path.GetTempPath(), "project");

    [Fact]
    public void MapsMountedPaths()
    {
        var container = new TransientTestContainer("image", "network", Project);

        Assert.Equal("/workspace/bin/Debug/Tests.dll", container.MapPath(Path.Combine(Project, "bin", "Debug", "Tests.dll")));
    }

    [Fact]
    public void RejectsOtherPaths()
    {
        var container = new TransientTestContainer("image", "network", Project);

        Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "project-other", "Tests.dll")));
        Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "Tests.dll")));
    }

    [Fact]
    public void MapsByLongestMount()
    {
        var container = new ServiceTestContainer("id", "name", [(Project, "/src"), (Path.Combine(Project, "bin"), "/bin")]);

        Assert.Equal("/bin/Tests.dll", container.MapPath(Path.Combine(Project, "bin", "Tests.dll")));
        Assert.Equal("/src/Tests.cs", container.MapPath(Path.Combine(Project, "Tests.cs")));
    }

    [Theory]
    [InlineData("""{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App"}}}""", "mcr.microsoft.com/dotnet/runtime:8.0")]
    [InlineData(
        """{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App"},{"name":"Microsoft.AspNetCore.App"}]}}""",
        "mcr.microsoft.com/dotnet/aspnet:10.0")]
    public void SelectsImageOfRuntime(string runtimeConfig, string image)
    {
        var directory = Directory.CreateTempSubdirectory("groundcontrol-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "Tests.runtimeconfig.json"), runtimeConfig);

            Assert.Equal(image, TransientTestContainer.GetImage(Path.Combine(directory.FullName, "Tests.dll")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunsTransientContainerOnNetwork()
    {
        var container = new TransientTestContainer("mcr.microsoft.com/dotnet/runtime:8.0", "sample_default", Project);

        var arguments = container.Run("/workspace/bin");

        Assert.Equal(["run", "--rm", "--interactive", "--init", "--name", container.Name, "--network", "sample_default"], arguments.Take(8));
        Assert.Contains($"type=bind,source={Project},target=/workspace,readonly", arguments);
        Assert.Equal(["--workdir", "/workspace/bin", "mcr.microsoft.com/dotnet/runtime:8.0"], arguments.TakeLast(3));
        Assert.Equal(["rm", "--force", container.Name], container.Stop(42));
    }
}
