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
        Assert.Null(options.Service);
        Assert.Equal("docker", options.Docker);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ReadinessTimeout);
        Assert.Equal(ReadinessPolicy.AllServices, options.Readiness);
    }

    [Fact]
    public void ReadsAssemblyMetadata()
    {
        var options = GroundControlOptions.Load(
            [("GroundControl.Service", "tests"), ("groundcontrol.projectdirectory", "/src"), ("Other.Service", "other")],
            _ => null);

        Assert.Equal("tests", options.Service);
        Assert.Equal("/src", options.ProjectDirectory);
    }

    [Fact]
    public void PrefersEnvironmentVariables()
    {
        var environment = new Dictionary<string, string> { ["GROUNDCONTROL_SERVICE"] = "ci", ["GROUNDCONTROL_DOCKER_CONTEXT"] = "remote" };

        var options = GroundControlOptions.Load([("GroundControl.Service", "tests")], environment.GetValueOrDefault);

        Assert.Equal("ci", options.Service);
        Assert.Equal("remote", options.DockerContext);
    }

    [Fact]
    public void IgnoresEmptyValues()
    {
        var options = GroundControlOptions.Load([("GroundControl.Service", "tests")], name => name == "GROUNDCONTROL_SERVICE" ? string.Empty : null);

        Assert.Equal("tests", options.Service);
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("00:02:00", 120)]
    public void ParsesReadinessTimeout(string value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), GroundControlOptions.Load([("GroundControl.ReadinessTimeout", value)], _ => null).ReadinessTimeout);

    [Fact]
    public void ParsesReadiness() =>
        Assert.Equal(ReadinessPolicy.None, GroundControlOptions.Load([("GroundControl.Readiness", "none")], _ => null).Readiness);
}
