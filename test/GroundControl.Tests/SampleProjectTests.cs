using Xunit;

namespace GroundControl.Tests;

// Runs GroundControl.Tests.Sample by GroundControl within its Docker Compose project, as test runners run test assemblies.
[Trait("Category", "Docker")]
public class SampleProjectTests(SampleProject sample) : IClassFixture<SampleProject>
{
    // Tests of GroundControl.Tests.Sample run by default (its explicit test is not).
    private const int Tests = 9;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunsTestsWithinContainer(bool preEnumerateTheories)
    {
        var run = await sample.RunAsync(preEnumerateTheories ? ["-preEnumerateTheories"] : []);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Of("error"));
        Assert.Equal(Tests, run.Of("test-passed").Count);
        Assert.Empty(run.Of("test-failed"));
        var finished = Assert.Single(run.Of("test-assembly-finished"));
        Assert.Equal(Tests, finished.GetProperty("TestsTotal").GetInt32() - finished.GetProperty("TestsNotRun").GetInt32());
    }

    // The tests container is kept for the runs, on the network of the project, where the services reach it by its name.
    [Fact]
    public async Task KeepsContainerReachedByName()
    {
        const string test = "GroundControl.Tests.Sample.NetworkTests.RunsWithinContainer";

        Assert.Equal(0, (await sample.RunAsync(["-method", test])).ExitCode);
        var container = await sample.DockerAsync("inspect", "--format", "{{.Id}} {{.State.Running}}", "groundcontrol");
        Assert.Equal(0, (await sample.RunAsync(["-method", test])).ExitCode);

        Assert.Equal(container.Output, (await sample.DockerAsync("inspect", "--format", "{{.Id}} {{.State.Running}}", "groundcontrol")).Output);
        Assert.EndsWith(" true", Assert.Single(container.Output));
        var resolution = await sample.DockerAsync("exec", "groundcontrol-sample-web-1", "nslookup", "groundcontrol");
        Assert.True(resolution.ExitCode == 0, $"groundcontrol is not resolved by the web service:{Environment.NewLine}{resolution.Error}");
    }

    [Fact]
    public async Task ReportsTestsByLocalAssembly()
    {
        var run = await sample.RunAsync(["-preEnumerateTheories"]);

        var assembly = Assert.Single(run.Messages.Select(message => message.GetProperty("AssemblyUniqueID").GetString()).Distinct());
        var starting = Assert.Single(run.Of("test-assembly-starting"));
        Assert.Equal(assembly, starting.GetProperty("AssemblyUniqueID").GetString());
        Assert.Equal(sample.AssemblyPath, starting.GetProperty("AssemblyPath").GetString());
    }

    [Fact]
    public async Task KeepsDisplayNames()
    {
        var run = await sample.RunAsync(["-preEnumerateTheories"]);

        var names = run.Of("test-starting").Select(message => message.GetProperty("TestDisplayName").GetString()).ToList();
        Assert.Contains("GroundControl.Tests.Sample.TheoryTests.NonAsciiRows(value: \"Привет, Zürich, 東京\")", names);
        Assert.Contains(
            names,
            name => name!.StartsWith("GroundControl.Tests.Sample.TheoryTests.NonAsciiRows(value: \"Mozilla/5.0") && name.EndsWith("\"···)"));
    }

    [Fact]
    public async Task ReportsFailuresWithControlCharacters()
    {
        var run = await sample.RunAsync(["-method", "GroundControl.Tests.Sample.ExplicitTests.Fails", "-explicit", "only"]);

        Assert.Empty(run.Of("error"));
        var failed = Assert.Single(run.Of("test-failed"));
        Assert.Contains("Expected failure, with a device control \u0011.", failed.GetProperty("Messages")[0].GetString());
        Assert.Single(run.Of("test-assembly-finished"));
        Assert.NotEqual(0, run.ExitCode);
    }

    [Fact]
    public async Task ReportsTestsNotRunAsFailed()
    {
        var run = await sample.RunAsync([], new Dictionary<string, string> { ["GROUNDCONTROL_NETWORK"] = "missing" });

        Assert.NotEmpty(run.Of("error"));
        Assert.Empty(run.Of("test-passed"));
        Assert.NotEmpty(run.Of("test-failed"));
        Assert.NotEqual(0, run.ExitCode);
    }
}
