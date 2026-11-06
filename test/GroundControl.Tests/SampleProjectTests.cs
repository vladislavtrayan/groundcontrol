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

    // The tests container of the project is shared by the runs in progress (within the same container, so a quick test only), and dropped
    // by the last of them.
    [Fact]
    public async Task SharesContainerAndDropsItAfterLastRun()
    {
        string[] test = ["-method", "GroundControl.Tests.Sample.NetworkTests.RunsWithinContainer"];

        var runs = await Task.WhenAll(sample.RunAsync(test), sample.RunAsync(test), sample.RunAsync(test));

        Assert.All(runs, run => Assert.Equal(0, run.ExitCode));
        Assert.All(runs, run => Assert.Single(run.Of("test-passed")));
        Assert.NotEqual(0, (await sample.DockerAsync("container", "inspect", "groundcontrol-sample-groundcontrol-1")).ExitCode);
    }

    // The services of the project reach the tests container by its name while a run is in progress.
    [Fact]
    public async Task IsReachedByNameFromServices()
    {
        var run = sample.RunAsync(["-method", "GroundControl.Tests.Sample.NetworkTests.IsReachedByName"]);
        var resolution = await ResolveAsync("groundcontrol", run);

        Assert.Equal(0, (await run).ExitCode);
        Assert.True(resolution.ExitCode == 0, $"groundcontrol is not resolved by the web service:{Environment.NewLine}{resolution.Error}");
    }

    // Resolves the name by the web service while the run is in progress (the tests container exists once the run has started).
    private async Task<(int ExitCode, List<string> Output, string Error)> ResolveAsync(string name, Task<SampleRun> run)
    {
        var resolution = (ExitCode: -1, Output: new List<string>(), Error: "The run has completed before the name was resolved.");
        while (!run.IsCompleted && resolution.ExitCode != 0)
        {
            resolution = await sample.DockerAsync("exec", "groundcontrol-sample-web-1", "nslookup", name);
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        return resolution;
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
