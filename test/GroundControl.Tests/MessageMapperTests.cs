using System.Text.Json.Nodes;
using Xunit;
using Xunit.Sdk;

namespace GroundControl.Tests;

public class MessageMapperTests
{
    private const string LocalPath = @"C:\Work\Tests\bin\Tests.dll";
    private const string RemoteAssembly = "remote-assembly";

    [Fact]
    public async Task DropsDiscoveryMessages()
    {
        var mapper = await CreateAsync();

        Assert.Null(mapper.Map("""{"$type":"discovery-starting","AssemblyUniqueID":"remote-assembly"}"""));
        Assert.Null(mapper.Map("""{"$type":"discovery-complete","AssemblyUniqueID":"remote-assembly"}"""));
    }

    [Fact]
    public async Task DropsTestCasesNotRequested()
    {
        var mapper = await CreateAsync();

        Assert.Null(mapper.Map(Message("test-case-starting", "other-test-case")));
        Assert.Null(mapper.Map(Message("test-passed", "other-test-case")));
    }

    [Fact]
    public async Task ReportsTestAssemblyAsLocal()
    {
        var (mapper, testCases) = await CreateWithTestCasesAsync();
        var assembly = testCases[0].TestCollection.TestAssembly;

        var starting = Parse(mapper.Map("""{"$type":"test-assembly-starting","AssemblyUniqueID":"remote-assembly","AssemblyPath":"/workspace/Tests.dll","ConfigFilePath":"/workspace/xunit.runner.json"}"""));
        var finished = Parse(mapper.Map("""{"$type":"test-assembly-finished","AssemblyUniqueID":"remote-assembly"}"""));
        var passed = Parse(mapper.Map(Message("test-passed", testCases[0].UniqueID)));

        Assert.Equal(LocalPath, starting["AssemblyPath"]!.GetValue<string>());
        Assert.Equal(assembly.ConfigFilePath, starting["ConfigFilePath"]?.GetValue<string>());
        Assert.All([starting, finished, passed], message => Assert.Equal(assembly.UniqueID, message["AssemblyUniqueID"]!.GetValue<string>()));
    }

    // xUnit.net writes control characters as they are (e.g., in failure messages), and reads them so only.
    [Fact]
    public async Task KeepsControlCharacters()
    {
        var (mapper, testCases) = await CreateWithTestCasesAsync();
        string[] messages = ["bell \u0007, device control \u0011, new line \n", @"not a control character: \u0011, \\u0011"];
        var failed = new Xunit.v3.TestFailed
        {
            AssemblyUniqueID = RemoteAssembly,
            TestCollectionUniqueID = testCases[0].TestCollection.UniqueID,
            TestClassUniqueID = testCases[0].TestClass?.UniqueID,
            TestMethodUniqueID = testCases[0].TestMethod?.UniqueID,
            TestCaseUniqueID = testCases[0].UniqueID,
            TestUniqueID = "test",
            Cause = FailureCause.Assertion,
            ExceptionParentIndices = [-1, -1],
            ExceptionTypes = ["Exception", "Exception"],
            Messages = messages,
            StackTraces = [null, null],
            ExecutionTime = 0,
            FinishTime = DateTimeOffset.UtcNow,
            Output = "device control \u0011",
            Warnings = null,
        };

        var json = Assert.IsType<string>(mapper.Map(failed.ToJson()!));

        var mapped = Assert.IsAssignableFrom<ITestFailed>(Xunit.Runner.Common.MessageSinkMessageDeserializer.Deserialize(json, null));
        Assert.Equal(messages, mapped.Messages);
        Assert.Equal("device control \u0011", mapped.Output);
        Assert.Equal(testCases[0].TestCollection.TestAssembly.UniqueID, mapped.AssemblyUniqueID);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Test output written to the console.")]
    [InlineData("""{"Message":"JSON without a type"}""")]
    [InlineData("""{"$type":"test-starting",""")]
    public async Task IgnoresOtherOutput(string line)
    {
        var mapper = await CreateAsync();

        Assert.Null(mapper.Map(line));
    }

    [Fact]
    public async Task TracksTestCasesNotStarted()
    {
        var (mapper, testCases) = await CreateWithTestCasesAsync();

        mapper.Map(Message("test-case-starting", testCases[0].UniqueID));

        Assert.Equal(testCases.Skip(1).Select(testCase => testCase.UniqueID), mapper.NotStarted.Select(testCase => testCase.UniqueID));
    }

    private static async Task<MessageMapper> CreateAsync() => (await CreateWithTestCasesAsync()).Mapper;

    private static async Task<(MessageMapper Mapper, IReadOnlyList<ITestCase> TestCases)> CreateWithTestCasesAsync()
    {
        var testCases = await TestCases.DiscoverAsync(typeof(DiscoveredTests), preEnumerateTheories: true);
        Assert.True(testCases.Count > 1);
        return (new MessageMapper(LocalPath, testCases.ToList()), testCases);
    }

    private static string Message(string type, string testCase) =>
        $$"""{"$type":"{{type}}","AssemblyUniqueID":"{{RemoteAssembly}}","TestCaseUniqueID":"{{testCase}}"}""";

    private static JsonObject Parse(string? json) => Assert.IsType<JsonObject>(JsonNode.Parse(Assert.IsType<string>(json)));
}
