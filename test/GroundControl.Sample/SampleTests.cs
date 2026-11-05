using System.Net;
using Xunit;

namespace GroundControl.Sample;

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

public class TheoryTests
{
    public static TheoryData<Payload> Payloads => [new Payload(1), new Payload(2)];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void InlineRows(int value) => Assert.True(value > 0);

    // Display names truncate long values with "···", and keep non-ASCII characters.
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko)")]
    [InlineData("Привет, Zürich, 東京")]
    public void NonAsciiRows(string value) => Assert.NotEmpty(value);

    // Rows of data which is not serializable are enumerated when the theory runs.
    [Theory]
    [MemberData(nameof(Payloads))]
    public void NonSerializableRows(Payload payload) => Assert.True(payload.Value > 0);

    public sealed record Payload(int Value);
}

public class ExplicitTests
{
    // Failure messages are reported with control characters as they are, while JSON strings do not allow them.
    [Fact(Explicit = true)]
    public void Fails() => Assert.Fail("Expected failure, with a device control \u0011.");
}
