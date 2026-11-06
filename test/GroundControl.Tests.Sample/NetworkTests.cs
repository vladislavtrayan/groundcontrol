using System.Net;
using Xunit;

namespace GroundControl.Tests.Sample;

public class NetworkTests
{
    [Fact]
    public void RunsWithinContainer() =>
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GROUNDCONTROL_TEST_CASES")));

    [Fact]
    public async Task ReachesServiceByName()
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync("http://web/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // A web server hosted by the tests (e.g., a test fixture) is reached by the name of the tests container.
    [Fact]
    public async Task IsReachedByName()
    {
        var name = Environment.GetEnvironmentVariable("GROUNDCONTROL_CONTAINER");
        Assert.False(string.IsNullOrEmpty(name));

        using var listener = new HttpListener();
        listener.Prefixes.Add("http://*:8080/");
        listener.Start();
        using var client = new HttpClient();
        var request = client.GetAsync($"http://{name}:8080/", TestContext.Current.CancellationToken);
        var context = await listener.GetContextAsync();
        context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        context.Response.Close();

        using var response = await request;
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // Waits for the other two test runs started together to run within the same tests container, each leaving a file in it
    // (see SampleProjectTests); explicit, as it waits.
    [Fact(Explicit = true)]
    public async Task MeetsOtherRuns()
    {
        var meeting = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "groundcontrol-meeting"));
        File.WriteAllText(Path.Combine(meeting.FullName, Guid.NewGuid().ToString("N")), string.Empty);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (meeting.GetFiles().Length < 3)
        {
            Assert.True(DateTime.UtcNow < deadline, $"{meeting.GetFiles().Length} of 3 test runs have met within the tests container.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    // Runs long, until killed as its test host dies (see DeclaredServiceTests); explicit, as it waits.
    [Fact(Explicit = true)]
    public Task RunsLong() => Task.Delay(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

    // Waits for a service of the project to call the web server of the tests by the name of the tests container (see SampleProjectTests);
    // explicit, as it waits.
    [Fact(Explicit = true)]
    public async Task IsCalledByService()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://*:8080/");
        listener.Start();
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        context.Response.Close();
    }
}
