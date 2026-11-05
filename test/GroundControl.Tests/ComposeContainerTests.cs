using GroundControl.Docker;
using Xunit;

namespace GroundControl.Tests;

public class ComposeContainerTests
{
    [Fact]
    public void ParsesLines()
    {
        var container = Assert.Single(ComposeContainer.Parse(
            """{"ID":"abc","Name":"sample-web-1","Service":"web","State":"running","Health":"healthy","ExitCode":0,"Status":"Up 5 seconds (healthy)","Networks":"sample_default"}"""));

        Assert.Equal(new ComposeContainer("abc", "sample-web-1", "running", "healthy", 0, "Up 5 seconds (healthy)", "sample_default"), container);
    }

    [Fact]
    public void ParsesArrays() =>
        Assert.Equal(["web", "db"], ComposeContainer.Parse("""[{"Name":"web"},{"Name":"db"}]""").Select(container => container.Name));

    [Fact]
    public void IgnoresEmptyLines() => Assert.Empty(ComposeContainer.Parse(" "));

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
        var container = new ComposeContainer("id", "name", state, health, exitCode, string.Empty, string.Empty);

        Assert.Equal(ready, container.IsReady);
        Assert.Equal(failed, container.HasFailed);
    }
}
