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

        var starting = Parse(mapper.Map("""{"$type":"test-assembly-starting","AssemblyUniqueID":"remote-assembly","AssemblyPath":"/workspace/Tests.dll"}"""));
        var finished = Parse(mapper.Map("""{"$type":"test-assembly-finished","AssemblyUniqueID":"remote-assembly"}"""));
        var passed = Parse(mapper.Map(Message("test-passed", testCases[0].UniqueID)));

        Assert.Equal(LocalPath, starting["AssemblyPath"]!.GetValue<string>());
        Assert.All([starting, finished, passed], message => Assert.Equal(assembly.UniqueID, message["AssemblyUniqueID"]!.GetValue<string>()));
    }

    // xUnit.net writes control characters within messages as they are (e.g., in failure messages), and reads them so only.
    [Fact]
    public async Task KeepsControlCharacters()
    {
        var (mapper, testCases) = await CreateAsync();
        var line = $"{{\"$type\":\"test-failed\",\"AssemblyUniqueID\":\"{testCases[0].TestCollection.TestAssembly.UniqueID}\","
            + $"\"TestCaseUniqueID\":\"{testCases[0].UniqueID}\",\"Messages\":[\"device control \u0011, not one \\\\u0011\"]}}";

        Assert.Equal(line, mapper.Map(line));
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
    public async Task TracksTestCasesNotStarted()
    {
        var (mapper, testCases) = await CreateAsync();

        mapper.Map(Message("test-case-starting", testCases[0].UniqueID));

        Assert.Equal(testCases.Skip(1).Select(testCase => testCase.UniqueID), mapper.NotStarted.Select(testCase => testCase.UniqueID));
    }

    private static async Task<(MessageMapper Mapper, IReadOnlyList<ITestCase> TestCases)> CreateAsync()
    {
        var testCases = await TestCaseDiscovery.DiscoverAsync(typeof(DiscoveredTests), preEnumerateTheories: true);
        return (new MessageMapper(LocalPath, testCases.ToList()), testCases);
    }

    private static string Message(string type, string testCase) =>
        $$"""{"$type":"{{type}}","AssemblyUniqueID":"remote-assembly","TestCaseUniqueID":"{{testCase}}"}""";

    private static JsonObject Parse(string? json) => Assert.IsType<JsonObject>(JsonNode.Parse(Assert.IsType<string>(json)));
}
