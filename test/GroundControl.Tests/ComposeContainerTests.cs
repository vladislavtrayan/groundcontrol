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
            Config = new Config { Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "sample", ["com.docker.compose.service"] = "web" } },
            State = new ContainerState { Status = "running", Health = new Health { Status = "healthy" }, ExitCode = 0 },
            NetworkSettings = new NetworkSettings { Networks = new Dictionary<string, EndpointSettings> { ["sample_default"] = new() } },
        };

        var container = ComposeContainer.From(inspection);

        Assert.Equal(("abc", "sample-web-1", "sample", "web", "running", "healthy", 0L), (container.Id, container.Name, container.Project, container.Service, container.State, container.Health, container.ExitCode));
        Assert.Equal(["sample_default"], container.Networks);
        Assert.Equal("running, healthy", container.Status);
    }

    [Fact]
    public void ReadsInspectionWithoutHealthcheckOrLabels()
    {
        var container = ComposeContainer.From(new ContainerInspectResponse { ID = "abc", Name = "/other", State = new ContainerState { Status = "exited", ExitCode = 1 } });

        Assert.Equal(("", "", "exited", "", 1L), (container.Project, container.Service, container.State, container.Health, container.ExitCode));
        Assert.Empty(container.Networks);
        Assert.Equal("exited with 1", container.Status);
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
        var container = new ComposeContainer("id", "name", "project", "service", state, health, exitCode, []);

        Assert.Equal(ready, container.IsReady);
        Assert.Equal(failed, container.HasFailed);
    }
}
