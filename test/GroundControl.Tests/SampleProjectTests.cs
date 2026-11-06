using Xunit;

namespace GroundControl.Tests;

// Runs GroundControl.Tests.Sample by GroundControl within its Docker Compose project, as test runners run test assemblies:
// within the groundcontrol service of the project by default, or within a container created for the run (named tests) when the service is not.
[Trait("Category", "Docker")]
public class SampleProjectTests(SampleProject sample) : IClassFixture<SampleProject>
{
    // Tests of GroundControl.Tests.Sample run by default (its explicit test is not).
    private const int Tests = 9;

    private static readonly Dictionary<string, string> TransientContainer = new() { ["GROUNDCONTROL_SERVICE"] = "tests" };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunsTestsWithinContainer(bool transientContainer, bool preEnumerateTheories)
    {
        var run = await sample.RunAsync(preEnumerateTheories ? ["-preEnumerateTheories"] : [], transientContainer ? TransientContainer : null);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Of("error"));
        Assert.Equal(Tests, run.Of("test-passed").Count);
        Assert.Empty(run.Of("test-failed"));
        var finished = Assert.Single(run.Of("test-assembly-finished"));
        Assert.Equal(Tests, finished.GetProperty("TestsTotal").GetInt32() - finished.GetProperty("TestsNotRun").GetInt32());
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
        var run = await sample.RunAsync([], new Dictionary<string, string>(TransientContainer) { ["GROUNDCONTROL_NETWORK"] = "missing" });

        Assert.NotEmpty(run.Of("error"));
        Assert.Empty(run.Of("test-passed"));
        Assert.NotEmpty(run.Of("test-failed"));
        Assert.NotEqual(0, run.ExitCode);
    }
}
