using GroundControl.Configuration;
using Xunit;

namespace GroundControl.Tests;

public class GroundControlOptionsTests
{
    [Fact]
    public void HasDefaults()
    {
        var options = GroundControlOptions.Load([], _ => null);

        Assert.Equal(new GroundControlOptions(), options);
        Assert.Equal("groundcontrol", options.Container);
        Assert.Null(options.Network);
        Assert.Null(options.DockerHost);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ReadinessTimeout);
    }

    [Fact]
    public void ReadsAssemblyMetadata()
    {
        var options = GroundControlOptions.Load(
            [("GroundControl.Network", "backend"), ("groundcontrol.projectdirectory", "/src"), ("Other.Network", "other")],
            _ => null);

        Assert.Equal("backend", options.Network);
        Assert.Equal("/src", options.ProjectDirectory);
    }

    [Fact]
    public void PrefersEnvironmentVariables()
    {
        var environment = new Dictionary<string, string> { ["GROUNDCONTROL_NETWORK"] = "ci", ["GROUNDCONTROL_DOCKER_CONTEXT"] = "remote" };

        var options = GroundControlOptions.Load([("GroundControl.Network", "backend")], environment.GetValueOrDefault);

        Assert.Equal("ci", options.Network);
        Assert.Equal("remote", options.DockerContext);
    }

    [Fact]
    public void IgnoresEmptyValues()
    {
        var options = GroundControlOptions.Load([("GroundControl.Network", "backend")], name => name == "GROUNDCONTROL_NETWORK" ? string.Empty : null);

        Assert.Equal("backend", options.Network);
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("00:02:00", 120)]
    [InlineData("0", 0)]
    public void ParsesReadinessTimeout(string value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), GroundControlOptions.Load([("GroundControl.ReadinessTimeout", value)], _ => null).ReadinessTimeout);
}
