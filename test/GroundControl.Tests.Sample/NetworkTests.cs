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
}
