using System.Text.Json;
using GroundControl.Docker;
using Xunit;

namespace GroundControl.Tests;

public class TestContainerTests
{
    private static readonly string Project = Path.Combine(Path.GetTempPath(), "project");

    [Fact]
    public void MapsMountedPaths()
    {
        var container = new TestContainer("groundcontrol", "image", "network", Project);

        Assert.Equal("/workspace/bin/Debug/Tests.dll", container.MapPath(Path.Combine(Project, "bin", "Debug", "Tests.dll")));
    }

    [Fact]
    public void RejectsOtherPaths()
    {
        var container = new TestContainer("groundcontrol", "image", "network", Project);

        Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "project-other", "Tests.dll")));
        Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "Tests.dll")));
    }

    [Theory]
    [InlineData("/run/desktop/mnt/host/c/Work/project", true, @"C:\Work\project")]
    [InlineData("/run/desktop/mnt/host/c", true, @"C:\")]
    [InlineData("/mnt/d/Work/project", true, @"D:\Work\project")]
    [InlineData(@"C:\Work\project", true, @"C:\Work\project")]
    [InlineData("/home/user/project", true, "/home/user/project")]
    [InlineData("/mnt/d/Work/project", false, "/mnt/d/Work/project")]
    public void MapsMountSourcesToHostPaths(string source, bool windowsHost, string hostPath) =>
        Assert.Equal(hostPath, TestContainer.ToHostPath(source, windowsHost));

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
    public void CreatesContainerOnNetworkByName()
    {
        var container = new TestContainer("groundcontrol", "mcr.microsoft.com/dotnet/runtime:8.0", "sample_default", Project);

        var arguments = container.Create();

        Assert.Equal(["run", "--detach", "--init", "--name", "groundcontrol", "--hostname", "groundcontrol", "--network", "sample_default"], arguments.Take(9));
        Assert.Contains($"type=bind,source={Project},target=/workspace,readonly", arguments);
        Assert.Contains("type=volume,source=groundcontrol-debugger-tools,target=/root/.RiderDebuggerTools", arguments);
        Assert.Equal(["mcr.microsoft.com/dotnet/runtime:8.0", "sleep", "infinity"], arguments.TakeLast(3));
    }

    [Fact]
    public void RunsWithinContainer()
    {
        var container = new TestContainer("groundcontrol", "image", "network", Project);

        Assert.Equal(["exec", "--interactive", "--env", "GROUNDCONTROL_CONTAINER=groundcontrol", "--workdir", "/workspace/bin", "groundcontrol"], container.Run("/workspace/bin"));
        Assert.Equal(["exec", "groundcontrol", "kill", "42"], container.Stop(42));
        Assert.Null(container.Stop(null));
    }

    [Fact]
    public void ParsesContainerState()
    {
        const string inspection = """
            {"State":{"Running":true},"Config":{"Image":"mcr.microsoft.com/dotnet/runtime:8.0"},
             "Mounts":[{"Type":"volume","Source":"tools","Destination":"/root/.RiderDebuggerTools"},{"Type":"bind","Source":"/run/desktop/mnt/host/c/Work/project","Destination":"/workspace"}],
             "NetworkSettings":{"Networks":{"sample_default":{},"other":{}}}}
            """;

        var state = ContainerState.Parse(JsonDocument.Parse(inspection).RootElement, "/workspace", windowsHost: true);

        Assert.True(state.Running);
        Assert.Equal("mcr.microsoft.com/dotnet/runtime:8.0", state.Image);
        Assert.Equal(@"C:\Work\project", state.ProjectDirectory);
        Assert.Equal(["sample_default", "other"], state.Networks);
    }
}
