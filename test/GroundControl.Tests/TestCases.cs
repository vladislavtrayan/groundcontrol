using Xunit;
using Xunit.Runner.Common;
using Xunit.Sdk;
using Xunit.v3;

namespace GroundControl.Tests;

// Test cases discovered by xUnit.net, as test runners pass them to test frameworks.
internal static class TestCases
{
    public static async Task<IReadOnlyList<ITestCase>> DiscoverAsync(Type type, bool preEnumerateTheories)
    {
        var testCases = new List<ITestCase>();
        var options = TestFrameworkOptions.ForDiscovery(new TestAssemblyConfiguration { PreEnumerateTheories = preEnumerateTheories });
        await using var framework = new XunitTestFramework();
        await framework.GetDiscoverer(type.Assembly).Find(
            testCase =>
            {
                testCases.Add(testCase);
                return new ValueTask<bool>(true);
            },
            options,
            [type],
            TestContext.Current.CancellationToken);
        return testCases;
    }
}

// Test cases discovered by the tests, not run themselves.
public class DiscoveredTests
{
    [Fact(Explicit = true)]
    public void Fact()
    {
    }

    [Theory(Explicit = true)]
    [InlineData(1, "a")]
    [InlineData(2, "Привет")]
    public void Theory(int number, string text)
    {
        _ = (number, text);
    }
}
