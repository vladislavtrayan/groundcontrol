using Xunit;
using Xunit.Sdk;

namespace GroundControl.Tests;

public class TestCaseSerializationTests
{
    private const string RemotePath = "/workspace/bin/Debug/net8.0/GroundControl.Tests.dll";

    private static readonly string LocalPath = typeof(DiscoveredTests).Assembly.Location;

    // Replacing the remote path back by the local one gives the serialization of xUnit.net, so nothing but the path is changed.
    [Fact]
    public async Task ReplacesAssemblyPathOnly()
    {
        var testCase = (await TestCaseDiscovery.DiscoverAsync(typeof(DiscoveredTests), preEnumerateTheories: true))[0];

        var remote = TestCaseSerialization.Serialize(testCase, RemotePath);

        Assert.NotEqual(SerializationHelper.Instance.Serialize(testCase), remote);
        Assert.Equal(SerializationHelper.Instance.Serialize(testCase), TestCaseSerialization.ReplaceAssemblyPath(remote, LocalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadsTestCases(bool preEnumerateTheories)
    {
        var testCases = await TestCaseDiscovery.DiscoverAsync(typeof(DiscoveredTests), preEnumerateTheories);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, testCases.Select(testCase => TestCaseSerialization.Serialize(testCase, LocalPath)));

            var loaded = TestCaseSerialization.Load(path);

            Assert.Equal(testCases.Select(testCase => testCase.UniqueID), loaded.Select(testCase => testCase.UniqueID));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KeepsOtherSerializations() =>
        Assert.Equal("4:dGV4dA==", TestCaseSerialization.ReplaceAssemblyPath("4:dGV4dA==", RemotePath));
}
