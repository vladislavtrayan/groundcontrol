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
}
