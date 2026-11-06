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

    // Test runs of the project run one at a time, each within a container of its own, removed once the run completes.
    [Fact]
    public async Task RunsOneAtATimeWithinOwnContainers()
    {
        string[] test = ["-method", "GroundControl.Tests.Sample.NetworkTests.IsReachedByName"];

        var runs = await Task.WhenAll(sample.RunAsync(test), sample.RunAsync(test), sample.RunAsync(test));

        Assert.All(runs, run => Assert.Equal(0, run.ExitCode));
        Assert.All(runs, run => Assert.Single(run.Of("test-passed")));
        Assert.Empty((await sample.DockerAsync("ps", "--all", "--filter", "label=com.docker.compose.service=groundcontrol", "--format", "{{.Names}}")).Output);
    }

    // The services of the project call the tests by the name of the tests container: the sample test waits for the call of the web service,
    // so the run is in progress until the call reaches it.
    [Fact]
    public async Task IsCalledByNameFromServices()
    {
        var run = sample.RunAsync(["-method", "GroundControl.Tests.Sample.NetworkTests.IsCalledByService", "-explicit", "only"]);
        var call = await this.CallAsync("http://groundcontrol:8080/", run);

        var result = await run;
        Assert.True(
            call.ExitCode == 0,
            $"The web service has not called the tests:{Environment.NewLine}{string.Join(Environment.NewLine, call.Output)}{Environment.NewLine}{call.Error}");
        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Of("test-passed"));
    }

    // Calls the URL from the web service until it succeeds or the run completes (the tests container exists once the run has started).
    private async Task<(int ExitCode, List<string> Output, string Error)> CallAsync(string url, Task<SampleRun> run)
    {
        var call = (ExitCode: -1, Output: new List<string>(), Error: "The run has completed before the call.");
        while (!run.IsCompleted)
        {
            call = await sample.DockerAsync("exec", "groundcontrol-sample-web-1", "wget", "-q", "-O", "/dev/null", "-T", "2", url);
            if (call.ExitCode == 0)
            {
                break;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        return call;
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
