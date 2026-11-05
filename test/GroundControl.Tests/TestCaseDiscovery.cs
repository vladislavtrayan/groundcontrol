using Xunit;
using Xunit.Runner.Common;
using Xunit.Sdk;
using Xunit.v3;

namespace GroundControl.Tests;

// Test cases discovered by xUnit.net, as test runners pass them to test frameworks.
internal static class TestCaseDiscovery
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
