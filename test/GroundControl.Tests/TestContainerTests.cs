using Docker.DotNet.Models;
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
    public void CreatesContainerOfProjectOnNetworkByName()
    {
        var container = new TestContainer("sample", "groundcontrol", "mcr.microsoft.com/dotnet/runtime:8.0", "sample_default", Project);

        var parameters = container.Create();

        Assert.Equal("sample-groundcontrol-1", container.Name);
        Assert.Equal(container.Name, parameters.Name);
        Assert.Equal("mcr.microsoft.com/dotnet/runtime:8.0", parameters.Image);
        Assert.Equal(["sleep", "infinity"], parameters.Cmd);
        Assert.Equal("groundcontrol", parameters.Hostname);
        Assert.Equal("sample", parameters.Labels["com.docker.compose.project"]);
        Assert.Equal("groundcontrol", parameters.Labels["com.docker.compose.service"]);
        Assert.Equal(Project, parameters.Labels["com.docker.compose.project.working_dir"]);
        Assert.Equal("False", parameters.Labels["com.docker.compose.oneoff"]);
        Assert.True(parameters.HostConfig.Init);
        Assert.Equal(["groundcontrol"], Assert.Single(parameters.NetworkingConfig.EndpointsConfig, endpoint => endpoint.Key == "sample_default").Value.Aliases);
        Assert.Collection(
            parameters.HostConfig.Mounts,
            mount => Assert.Equal(("bind", Project, "/workspace", true), (mount.Type, mount.Source, mount.Target, mount.ReadOnly)),
            mount => Assert.Equal(("volume", "groundcontrol-debugger-tools", "/root/.RiderDebuggerTools", false), (mount.Type, mount.Source, mount.Target, mount.ReadOnly)));
    }

    // The process ID reported is the one within the container (the shell's, replaced by the command), which the debugger attaches to.
    [Fact]
    public void RunsWithinContainerReportingProcessId()
    {
        var container = new TestContainer("sample", "groundcontrol", "image", "sample_default", Project);

        var parameters = container.Exec("/workspace/bin", ["dotnet", "/workspace/bin/Tests.dll"], ["TESTS=/tmp/tests.txt"]);

        Assert.Equal(["sh", "-c", "echo $$ >&2; exec \"$@\"", "sh", "dotnet", "/workspace/bin/Tests.dll"], parameters.Cmd);
        Assert.Equal(["GROUNDCONTROL_CONTAINER=groundcontrol", "TESTS=/tmp/tests.txt"], parameters.Env);
        Assert.Equal("/workspace/bin", parameters.WorkingDir);
        Assert.True(parameters.AttachStdout && parameters.AttachStderr && !parameters.Tty && !parameters.AttachStdin);
    }

    // An existing container serves the test run if it runs with the image and the project directory; otherwise it is replaced.
    [Theory]
    [InlineData(true, "image", true)]
    [InlineData(false, "image", false)]
    [InlineData(true, "other-image", false)]
    public void FitsRunningContainerOfImageAndProject(bool running, string image, bool fits)
    {
        var container = new TestContainer("sample", "groundcontrol", "image", "sample_default", Project);
        var existing = new ContainerInspectResponse
        {
            State = new ContainerState { Running = running },
            Config = new Config { Image = image, Labels = new Dictionary<string, string> { ["com.docker.compose.project.working_dir"] = Project } },
        };

        Assert.Equal(fits, container.Fits(existing));
        Assert.False(container.Fits(new ContainerInspectResponse
        {
            State = new ContainerState { Running = true },
            Config = new Config { Image = "image", Labels = new Dictionary<string, string> { ["com.docker.compose.project.working_dir"] = Path.Combine(Project, "other") } },
        }));
    }
}
