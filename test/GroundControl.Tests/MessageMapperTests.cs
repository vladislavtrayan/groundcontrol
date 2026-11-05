using System.Text.Json.Nodes;
using Xunit;
using Xunit.Sdk;

namespace GroundControl.Tests;

public class MessageMapperTests
{
    private const string LocalPath = @"C:\Work\Tests\bin\Tests.dll";

    [Fact]
    public async Task DropsDiscoveryMessages()
    {
        var (mapper, _) = await CreateAsync();

        Assert.Null(mapper.Map("""{"$type":"discovery-starting","AssemblyUniqueID":"remote-assembly"}"""));
        Assert.Null(mapper.Map("""{"$type":"discovery-complete","AssemblyUniqueID":"remote-assembly"}"""));
    }

    [Fact]
    public async Task DropsTestCasesNotRequested()
    {
        var (mapper, _) = await CreateAsync();

        Assert.Null(mapper.Map(Message("test-case-starting", "other-test-case")));
        Assert.Null(mapper.Map(Message("test-passed", "other-test-case")));
    }

    [Fact]
    public async Task ReportsTestAssemblyAsLocal()
    {
        var (mapper, testCases) = await CreateAsync();
        var assembly = testCases[0].TestCollection.TestAssembly;

        var starting = Parse(mapper.Map("""{"$type":"test-assembly-starting","AssemblyUniqueID":"remote-assembly","AssemblyPath":"/workspace/Tests.dll","ConfigFilePath":"/workspace/xunit.runner.json"}"""));
        var finished = Parse(mapper.Map("""{"$type":"test-assembly-finished","AssemblyUniqueID":"remote-assembly"}"""));
        var passed = Parse(mapper.Map(Message("test-passed", testCases[0].UniqueID)));

        Assert.Equal(LocalPath, starting["AssemblyPath"]!.GetValue<string>());
        Assert.Equal(assembly.ConfigFilePath, starting["ConfigFilePath"]?.GetValue<string>());
        Assert.All([starting, finished, passed], message => Assert.Equal(assembly.UniqueID, message["AssemblyUniqueID"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ReportsMessageTypes()
    {
        var (mapper, testCases) = await CreateAsync();

        Assert.Equal("test-assembly-finished", mapper.Map("""{"$type":"test-assembly-finished","AssemblyUniqueID":"remote-assembly"}""")?.Type);
        Assert.Equal("test-passed", mapper.Map(Message("test-passed", testCases[0].UniqueID))?.Type);
    }

    // xUnit.net writes control characters within messages as they are (e.g., in failure messages), and reads them so only.
    [Fact]
    public async Task KeepsControlCharacters()
    {
        var (mapper, testCases) = await CreateAsync();
        var line = $"{{\"$type\":\"test-failed\",\"AssemblyUniqueID\":\"remote-assembly\",\"TestCaseUniqueID\":\"{testCases[0].UniqueID}\","
            + "\"Messages\":[\"device control \u0011, not one \\\\u0011\"]}";

        Assert.Equal(line.Replace("remote-assembly", testCases[0].TestCollection.TestAssembly.UniqueID), mapper.Map(line)?.Json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Test output written to the console.")]
    [InlineData("""{"Message":"JSON without a type"}""")]
    [InlineData("""{"$type":"test-starting",""")]
    public async Task IgnoresOtherOutput(string line)
    {
        var (mapper, _) = await CreateAsync();

        Assert.Null(mapper.Map(line));
    }

    [Fact]
    public async Task TracksTestCasesNotFinished()
    {
        var (mapper, testCases) = await CreateAsync();
        var running = testCases[0];
        var finished = testCases[1];

        mapper.Map(Message("test-case-starting", running.UniqueID));
        mapper.Map(Message("test-starting", running.UniqueID, "\"TestUniqueID\":\"test-1\""));
        mapper.Map(Message("test-finished", running.UniqueID, "\"TestUniqueID\":\"test-1\""));
        mapper.Map(Message("test-starting", running.UniqueID, "\"TestUniqueID\":\"test-2\""));
        mapper.Map(Message("test-case-starting", finished.UniqueID));
        mapper.Map(Message("test-case-finished", finished.UniqueID));

        var pending = mapper.Pending.ToList();

        Assert.DoesNotContain(pending, test => test.TestCase.UniqueID == finished.UniqueID);
        Assert.Equal(new PendingTestCase(running, true, 2, "test-2"), Assert.Single(pending, test => test.TestCase.UniqueID == running.UniqueID));
        Assert.All(pending.Where(test => test.TestCase.UniqueID != running.UniqueID), test => Assert.Equal(new PendingTestCase(test.TestCase, false, 0, null), test));
    }

    [Fact]
    public async Task CountsResults()
    {
        var (mapper, testCases) = await CreateAsync();

        mapper.Map(Message("test-passed", testCases[0].UniqueID));
        mapper.Map(Message("test-failed", testCases[0].UniqueID));
        mapper.Map(Message("test-skipped", testCases[0].UniqueID));
        mapper.Map(Message("test-not-run", testCases[0].UniqueID));
        mapper.Map(Message("test-not-run", "other-test-case"));

        Assert.Equal((4, 1, 1, 1), (mapper.TestsTotal, mapper.TestsFailed, mapper.TestsSkipped, mapper.TestsNotRun));
    }

    private static async Task<(MessageMapper Mapper, IReadOnlyList<ITestCase> TestCases)> CreateAsync()
    {
        var testCases = await TestCaseDiscovery.DiscoverAsync(typeof(DiscoveredTests), preEnumerateTheories: true);
        Assert.True(testCases.Count > 1);
        return (new MessageMapper(LocalPath, testCases.ToList()), testCases);
    }

    private static string Message(string type, string testCase, string? members = null) =>
        $$"""{"$type":"{{type}}","AssemblyUniqueID":"remote-assembly","TestCaseUniqueID":"{{testCase}}"{{(members is null ? "" : "," + members)}}}""";

    private static JsonObject Parse(MappedMessage? message) => Assert.IsType<JsonObject>(JsonNode.Parse(Assert.IsType<MappedMessage>(message).Json));
}
