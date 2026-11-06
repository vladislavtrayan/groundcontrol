using Xunit;

namespace GroundControl.Tests;

// Runs GroundControl.Tests.Sample within the tests service its Docker Compose project declares (compose.groundcontrol.yaml),
// kept up with the project: GroundControl runs the tests within it, and neither creates nor removes it.
[Trait("Category", "Docker")]
[Collection("Docker")]
public class DeclaredServiceTests(DeclaredSampleProject sample) : IClassFixture<DeclaredSampleProject>
{
    private const string Container = "groundcontrol-sample-groundcontrol-1";

    [Fact]
    public async Task RunsWithinServiceAndKeepsIt()
    {
        var before = await this.InspectAsync();

        var run = await sample.RunAsync(["-method", "GroundControl.Tests.Sample.NetworkTests.RunsWithinContainer"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Single(run.Of("test-passed"));
        Assert.EndsWith(" true", before);
        Assert.Equal(before, await this.InspectAsync());
        Assert.Empty(await this.ZombiesAsync());
    }

    // The services resolve the name of the tests service between test runs: a call fails at once as nothing listens, not on the name.
    [Fact]
    public async Task IsResolvedBetweenRuns()
    {
        var call = await sample.DockerAsync("exec", "groundcontrol-sample-web-1", "wget", "-q", "-O", "/dev/null", "-T", "5", "http://groundcontrol:8080/");

        Assert.NotEqual(0, call.ExitCode);
        Assert.Contains("refused", call.Error);
    }

    // Test runs started together share the tests service, as they share a host.
    [Fact]
    public async Task SharesServiceAmongRuns()
    {
        await sample.DockerAsync("exec", Container, "rm", "-rf", "/tmp/groundcontrol-meeting");
        string[] test = ["-method", "GroundControl.Tests.Sample.NetworkTests.MeetsOtherRuns", "-explicit", "only"];

        var runs = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => sample.RunAsync(test)));

        Assert.All(runs, run => Assert.Empty(run.Of("test-failed")));
        Assert.All(runs, run => Assert.Single(run.Of("test-passed")));
        Assert.Equal(
            [Container],
            (await sample.DockerAsync("ps", "--filter", "label=com.docker.compose.project=groundcontrol-sample", "--filter", "label=com.docker.compose.service=groundcontrol", "--format", "{{.Names}}")).Output);
    }

    // The test process of a test host which dies is killed once its lease is not renewed, so it does not hold the ports of its fixtures
    // in the tests service, which lives on.
    [Fact]
    public async Task KillsTestsOfDeadHost()
    {
        using var host = sample.Start(["-method", "GroundControl.Tests.Sample.NetworkTests.RunsLong", "-explicit", "only"]);
        Assert.True(await WaitAsync(async () => await this.CountTestProcessesAsync() > 0), "The test process has not started within the tests service.");

        host.Kill(entireProcessTree: true);

        Assert.True(await WaitAsync(async () => await this.CountTestProcessesAsync() == 0), "The test process of the dead test host still runs within the tests service.");
        Assert.Empty(await this.ZombiesAsync());
    }

    // A declared tests service which does not mount the test assembly, or lacks its runtime, fails the test run with how to fix it.
    [Theory]
    [InlineData("SAMPLE_TESTS_MOUNT", "./obj", "does not mount")]
    [InlineData("SAMPLE_TESTS_IMAGE", "mcr.microsoft.com/dotnet/runtime-deps:8.0", "has no Microsoft.NETCore.App 8.0: use mcr.microsoft.com/dotnet/runtime:8.0")]
    public async Task FailsOnServiceNotRunningTests(string variable, string value, string message)
    {
        var up = await sample.ComposeAsync(new Dictionary<string, string> { [variable] = value }, "up", "--detach", "--wait", "groundcontrol");
        Assert.True(up.ExitCode == 0, up.Error);
        try
        {
            var run = await sample.RunAsync(["-method", "GroundControl.Tests.Sample.NetworkTests.RunsWithinContainer"]);

            Assert.Contains(message, Assert.Single(run.Of("error")).GetProperty("Messages")[0].GetString());
            Assert.Empty(run.Of("test-passed"));
            Assert.NotEqual(0, run.ExitCode);
        }
        finally
        {
            await sample.ComposeAsync(null, "up", "--detach", "--wait", "groundcontrol");
        }
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private async Task<string> InspectAsync() => string.Concat((await sample.DockerAsync("inspect", "--format", "{{.Id}} {{.State.Running}}", Container)).Output);

    // The processes within the tests service but its own (sleep infinity): a test run leaves none, running or completed but not reaped,
    // as the service has no init process to reap them.
    private async Task<List<string>> ZombiesAsync() =>
        (await sample.DockerAsync("top", Container)).Output.Skip(1).Where(line => !line.Contains("sleep infinity")).ToList();

    // The processes of test runs within the tests service (as the Docker host lists them).
    private async Task<int> CountTestProcessesAsync() =>
        (await sample.DockerAsync("top", Container)).Output.Count(line => line.Contains("GroundControl.Tests.Sample.dll"));
}
