using System.Text;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace GroundControl.Tests;

public class GroundControlTestCasesTests
{
    private const string RemotePath = "/workspace/bin/Debug/net8.0/GroundControl.Tests.dll";

    [Fact]
    public async Task ReplacesAssemblyPathOnly()
    {
        var testCase = (await TestCases.DiscoverAsync(typeof(DiscoveredTests), preEnumerateTheories: true))[0];
        var local = SerializationHelper.Instance.Serialize(testCase)!;

        var remote = GroundControlTestCases.Serialize(testCase, RemotePath);

        Assert.Equal([RemotePath], Members(remote, "ap"));
        Assert.Equal([typeof(DiscoveredTests).Assembly.Location], Members(local, "ap"));
        Assert.Equal(Members(local, "id"), Members(remote, "id"));
        Assert.Equal(Members(local, "dn"), Members(remote, "dn"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsTestCases(bool preEnumerateTheories)
    {
        var testCases = await TestCases.DiscoverAsync(typeof(DiscoveredTests), preEnumerateTheories);
        var path = Path.Combine(Path.GetTempPath(), $"groundcontrol-{Guid.NewGuid():N}.txt");
        try
        {
            // The local path, so the assembly is loaded by deserialization.
            File.WriteAllLines(path, testCases.Select(testCase => GroundControlTestCases.Serialize(testCase, typeof(DiscoveredTests).Assembly.Location)));

            var loaded = GroundControlTestCases.Load(path);

            Assert.Equal(testCases.Select(testCase => testCase.UniqueID), loaded.Select(testCase => testCase.UniqueID));
            Assert.Equal(testCases.Select(testCase => testCase.TestCaseDisplayName), loaded.Select(testCase => testCase.TestCaseDisplayName));
            Assert.Equal(
                testCases.Select(testCase => (testCase as XunitTestCase)?.TestMethodArguments?.Length),
                loaded.Select(testCase => (testCase as XunitTestCase)?.TestMethodArguments?.Length));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KeepsOtherSerializations() =>
        Assert.Equal("4:dGV4dA==", GroundControlTestCases.ReplaceAssemblyPath("4:dGV4dA==", RemotePath));

    // Values of the members of the given key, within the serialized object and the objects it contains.
    private static List<string> Members(string serialization, string key)
    {
        var values = new List<string>();

        void Walk(string type, string members)
        {
            foreach (var line in Decode(members).Split('\n'))
            {
                switch (line.Split(':', 3))
                {
                    case [var name, "-3", var value] when value.Split(':', 2) is [var memberType, var memberMembers]:
                        Walk(memberType, memberMembers);
                        break;
                    case [var name, _, var value] when name == key:
                        values.Add(Decode(value));
                        break;
                }
            }
        }

        var parts = serialization.Split(':', 3);
        Walk(parts[1], parts[2]);
        return values;
    }

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
