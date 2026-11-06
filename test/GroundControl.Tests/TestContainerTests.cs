using GroundControl.Docker;
using Xunit;

namespace GroundControl.Tests;

public class TestContainerTests
{
    private static readonly string Project = Path.Combine(Path.GetTempPath(), "project");

    [Fact]
    public void MapsMountedPaths()
    {
        var container = new TestContainer("sample", "groundcontrol", "image", "network", Project);

        Assert.Equal("/workspace/bin/Debug/Tests.dll", container.MapPath(Path.Combine(Project, "bin", "Debug", "Tests.dll")));
    }

    [Fact]
    public void RejectsOtherPaths()
    {
        var container = new TestContainer("sample", "groundcontrol", "image", "network", Project);

        Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "project-other", "Tests.dll")));
        Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "Tests.dll")));
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

            Assert.Equal(image, TestContainer.GetImage(Path.Combine(directory.FullName, "Tests.dll")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunsWithinProjectOnNetworkByName()
    {
        var container = new TestContainer("sample", "groundcontrol", "mcr.microsoft.com/dotnet/runtime:8.0", "sample_default", Project);

        var parameters = container.Create("/workspace/bin", ["dotnet", "/workspace/bin/Tests.dll"], ["TESTS=/tmp/tests.txt"]);

        Assert.Matches("^sample-groundcontrol-run-[0-9a-f]{8}$", container.Name);
        Assert.Equal(container.Name, parameters.Name);
        Assert.Equal("mcr.microsoft.com/dotnet/runtime:8.0", parameters.Image);
        Assert.Equal(["dotnet", "/workspace/bin/Tests.dll"], parameters.Cmd);
        Assert.Equal("/workspace/bin", parameters.WorkingDir);
        Assert.Equal("groundcontrol", parameters.Hostname);
        Assert.Equal(["GROUNDCONTROL_CONTAINER=groundcontrol", "TESTS=/tmp/tests.txt"], parameters.Env);
        Assert.Equal("sample", parameters.Labels["com.docker.compose.project"]);
        Assert.Equal("groundcontrol", parameters.Labels["com.docker.compose.service"]);
        Assert.Equal("True", parameters.Labels["com.docker.compose.oneoff"]);
        Assert.True(parameters.AttachStdout && parameters.AttachStderr && !parameters.Tty && !parameters.OpenStdin);
        Assert.True(parameters.HostConfig.Init);
        Assert.Equal(["groundcontrol"], Assert.Single(parameters.NetworkingConfig.EndpointsConfig, endpoint => endpoint.Key == "sample_default").Value.Aliases);
        Assert.Collection(
            parameters.HostConfig.Mounts,
            mount => Assert.Equal(("bind", Project, "/workspace", true), (mount.Type, mount.Source, mount.Target, mount.ReadOnly)),
            mount => Assert.Equal(("volume", "groundcontrol-debugger-tools", "/root/.RiderDebuggerTools", false), (mount.Type, mount.Source, mount.Target, mount.ReadOnly)));
    }
}
