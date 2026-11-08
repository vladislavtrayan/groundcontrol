using Xunit;

namespace GroundControl.Tests;

// Runs GroundControl.Tests.Sample by GroundControl within its Docker Compose project, as test runners run test assemblies.
[Trait("Category", "Docker")]
[Collection("Docker")]
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

    // The services of the project call the tests by the name of the tests service: the sample test waits for the call of the web service,
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

    // Calls the URL from the web service until it succeeds or the run completes (the test listens once the run has started).
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

// Runs GroundControl.Tests.Sample within its Docker Compose project without the tests service (compose.yaml only).
[Trait("Category", "Docker")]
[Collection("Docker")]
public class BareSampleProjectTests(BareSampleProject sample) : IClassFixture<BareSampleProject>
{
    // A project not declaring the tests service fails the test run with how to declare it.
    [Fact]
    public async Task FailsWithoutDeclaredService()
    {
        var run = await sample.RunAsync(["-method", "GroundControl.Tests.Sample.NetworkTests.RunsWithinContainer"]);

        var message = Assert.Single(run.Of("error")).GetProperty("Messages")[0].GetString();
        Assert.Contains("does not declare the groundcontrol service", message);
        Assert.Contains("mcr.microsoft.com/dotnet/runtime:8.0", message);
        Assert.Empty(run.Of("test-passed"));
        Assert.NotEqual(0, run.ExitCode);
    }
}
