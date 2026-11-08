using Docker.DotNet.Models;
using GroundControl.Docker;
using Xunit;

namespace GroundControl.Tests;

public class ComposeContainerTests
{
    [Fact]
    public void ReadsInspection()
    {
        var inspection = new ContainerInspectResponse
        {
            ID = "abc",
            Name = "/sample-web-1",
            Config = new Config
            {
                Labels = new Dictionary<string, string>
                {
                    ["com.docker.compose.project"] = "sample",
                    ["com.docker.compose.service"] = "web",
                    ["com.docker.compose.project.working_dir"] = "/src/sample",
                    ["com.docker.compose.oneoff"] = "False",
                    ["com.docker.compose.config-hash"] = "4f2b",
                },
            },
            State = new ContainerState { Status = "running", Health = new Health { Status = "healthy" }, ExitCode = 0 },
            NetworkSettings = new NetworkSettings { Networks = new Dictionary<string, EndpointSettings> { ["sample_default"] = new() } },
        };

        var container = ComposeContainer.From(inspection, windowsHost: false);

        Assert.Equal(("abc", "sample-web-1", "sample", "web", "/src/sample", "running", "healthy", 0L), (container.Id, container.Name, container.Project, container.Service, container.ProjectDirectory, container.State, container.Health, container.ExitCode));
        Assert.Equal(["sample_default"], container.Networks);
        Assert.Equal("running, healthy", container.Status);
        Assert.False(container.OneOff);
        Assert.True(container.Declared);
    }

    [Fact]
    public void ReadsInspectionWithoutHealthcheckOrLabels()
    {
        var container = ComposeContainer.From(new ContainerInspectResponse { ID = "abc", Name = "/other", State = new ContainerState { Status = "exited", ExitCode = 1 } }, windowsHost: false);

        Assert.Equal(("", "", "", "exited", "", 1L), (container.Project, container.Service, container.ProjectDirectory, container.State, container.Health, container.ExitCode));
        Assert.Empty(container.Networks);
        Assert.Empty(container.Mounts);
        Assert.Equal("exited with 1", container.Status);
        Assert.False(container.OneOff);
        Assert.False(container.Declared);
    }

    // A container of docker compose run is a one-off (not a service), which Docker Compose labels so.
    [Fact]
    public void TellsOneOffContainers()
    {
        var labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "sample", ["com.docker.compose.service"] = "web", ["com.docker.compose.oneoff"] = "True" };

        var container = ComposeContainer.From(
            new ContainerInspectResponse { ID = "abc", Name = "/sample-web-run-1a2b", Config = new Config { Labels = labels }, State = new ContainerState { Status = "exited", ExitCode = 1 } },
            windowsHost: false);

        Assert.True(container.OneOff);
    }

    // The bind mounts of a container, by their host paths (as Docker Desktop on Windows reports them within its virtual machine).
    [Fact]
    public void ReadsBindMounts()
    {
        var inspection = new ContainerInspectResponse
        {
            ID = "abc",
            Name = "/sample-groundcontrol-1",
            State = new ContainerState { Status = "running" },
            Mounts =
            [
                new MountPoint { Type = "bind", Source = "/run/desktop/mnt/host/c/Work/sample", Destination = "/tests" },
                new MountPoint { Type = "volume", Source = "/var/lib/docker/volumes/data/_data", Destination = "/data" },
            ],
        };

        var container = ComposeContainer.From(inspection, windowsHost: true);

        Assert.Equal([(@"C:\Work\sample", "/tests")], container.Mounts);
    }

    [Theory]
    [InlineData("running", "healthy", 0, true, false)]
    [InlineData("running", "", 0, true, false)]
    [InlineData("running", "starting", 0, false, false)]
    [InlineData("running", "unhealthy", 0, false, true)]
    [InlineData("exited", "", 0, true, false)]
    [InlineData("exited", "", 139, false, true)]
    [InlineData("dead", "", 1, false, true)]
    [InlineData("created", "", 0, false, false)]
    public void ReportsReadiness(string state, string health, int exitCode, bool ready, bool failed)
    {
        var container = new ComposeContainer("id", "name", "project", "service", "/src", state, health, exitCode, [], false, true, []);

        Assert.Equal(ready, container.IsReady);
        Assert.Equal(failed, container.HasFailed);
    }
}
