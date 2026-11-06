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
        var container = new ManagedTestContainer("sample", "groundcontrol", "image", "network", Project);

        Assert.Equal("/workspace/bin/Debug/Tests.dll", container.MapPath(Path.Combine(Project, "bin", "Debug", "Tests.dll")));
    }

    [Fact]
    public void RejectsOtherPaths()
    {
        var container = new ManagedTestContainer("sample", "groundcontrol", "image", "network", Project);

        Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "project-other", "Tests.dll")));
        Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "Tests.dll")));
    }

    // A declared service mounts the directories anywhere; the closest mount of a path maps it.
    [Fact]
    public void MapsPathsByMountsOfService()
    {
        var container = new ServiceTestContainer("sample-groundcontrol-1", "groundcontrol", "network", [(Project, "/src"), (Path.Combine(Project, "bin"), "/bin/")]);

        Assert.Equal("/bin/Debug/Tests.dll", container.MapPath(Path.Combine(Project, "bin", "Debug", "Tests.dll")));
        Assert.Equal("/src/tests/Tests.cs", container.MapPath(Path.Combine(Project, "tests", "Tests.cs")));
        Assert.Equal("/src", container.MapPath(Project));
        Assert.Contains("does not mount", Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "Tests.dll"))).Message);
    }

    [Fact]
    public void CreatesContainerOfProjectOnNetworkByName()
    {
        var container = new ManagedTestContainer("sample", "groundcontrol", "mcr.microsoft.com/dotnet/runtime:8.0", "sample_default", Project);

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
        Assert.DoesNotContain(ComposeLabels.ConfigHash, parameters.Labels.Keys);
        Assert.True(parameters.HostConfig.Init);
        Assert.Equal(["groundcontrol"], Assert.Single(parameters.NetworkingConfig.EndpointsConfig, endpoint => endpoint.Key == "sample_default").Value.Aliases);
        Assert.Collection(
            parameters.HostConfig.Mounts,
            mount => Assert.Equal(("bind", Project, "/workspace", true), (mount.Type, mount.Source, mount.Target, mount.ReadOnly)),
            mount => Assert.Equal(("volume", "groundcontrol-debugger-tools", "/root/.RiderDebuggerTools", false), (mount.Type, mount.Source, mount.Target, mount.ReadOnly)));
    }

    // The test process runs under run.sh, which reports its process ID within the container (for the debugger) and kills it once its lease
    // is not renewed (the test host has died).
    [Fact]
    public void RunsWithinContainerUnderLease()
    {
        var container = new ServiceTestContainer("sample-groundcontrol-1", "groundcontrol", "sample_default", [(Project, "/tests")]);

        var parameters = container.Exec("/tests/bin", "/tmp/groundcontrol/run", ["dotnet", "/tests/bin/Tests.dll"], ["TESTS=/tmp/groundcontrol/run/test-cases.txt"]);

        Assert.Equal(["sh", "-c"], parameters.Cmd.Take(2));
        Assert.Contains("echo $tests >&2", parameters.Cmd[2]);
        Assert.Contains("$run/lease", parameters.Cmd[2]);
        Assert.DoesNotContain("\r", parameters.Cmd[2]);
        Assert.Equal(["sh", "/tmp/groundcontrol/run", "dotnet", "/tests/bin/Tests.dll"], parameters.Cmd.Skip(3));
        Assert.Equal(["GROUNDCONTROL_CONTAINER=groundcontrol", "TESTS=/tmp/groundcontrol/run/test-cases.txt"], parameters.Env);
        Assert.Equal("/tests/bin", parameters.WorkingDir);
        Assert.True(parameters.AttachStdout && parameters.AttachStderr && !parameters.Tty && !parameters.AttachStdin);
    }

    // An existing container serves as the managed one if it runs with the image and the project directory; otherwise it is replaced.
    [Theory]
    [InlineData(true, "image", true)]
    [InlineData(false, "image", false)]
    [InlineData(true, "other-image", false)]
    public void FitsRunningContainerOfImageAndProject(bool running, string image, bool fits)
    {
        var container = new ManagedTestContainer("sample", "groundcontrol", "image", "sample_default", Project);
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
