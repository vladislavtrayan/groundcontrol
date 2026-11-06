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

    // Services reach the tests (e.g., a web server hosted by a test fixture) by the name given to them.
    [Fact]
    public async Task IsReachedByName()
    {
        var name = Environment.GetEnvironmentVariable("GROUNDCONTROL_SERVICE");

        Assert.False(string.IsNullOrEmpty(name));
        Assert.NotEmpty(await Dns.GetHostAddressesAsync(name, TestContext.Current.CancellationToken));
    }
}
