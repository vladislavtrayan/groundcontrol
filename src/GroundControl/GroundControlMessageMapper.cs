using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Sdk;

namespace GroundControl;

// Maps messages of the remote test process to the local test cases. The remote test process runs the local test cases as they are
// (see GroundControlTestCases), so it reports their unique IDs; only the test assembly path differs.
internal class GroundControlMessageMapper
{
    // xUnit.net JSON deserializer does not support escaped characters (e.g., '+' as '+').
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string assemblyPath;
    private readonly string? assemblyUniqueId;
    private readonly string? configFilePath;
    private readonly Dictionary<string, ITestCase> requested;
    private readonly HashSet<string> started = [];

    public GroundControlMessageMapper(string assemblyPath, IReadOnlyCollection<ITestCase> testCases)
    {
        this.assemblyPath = assemblyPath;
        this.assemblyUniqueId = testCases.FirstOrDefault()?.TestCollection.TestAssembly.UniqueID;
        this.configFilePath = testCases.FirstOrDefault()?.TestCollection.TestAssembly.ConfigFilePath;
        this.requested = testCases.ToDictionary(testCase => testCase.UniqueID);
    }

    // Requested test cases the remote test process has not run.
    public IEnumerable<ITestCase> NotStarted => this.requested.Values.Where(testCase => !this.started.Contains(testCase.UniqueID));

    // Returns the message mapped to the local test cases, or null if it is not a message (e.g., the output of the tests)
    // or it is not reported locally.
    public string? Map(string line)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(Escape(line)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (message?["$type"]?.GetValue<string>() is not { } type)
        {
            return null;
        }

        // Test cases are discovered locally, and only requested test cases are reported (methods are requested as a whole).
        if (type.StartsWith("discovery-"))
        {
            return null;
        }

        if (message["TestCaseUniqueID"]?.GetValue<string>() is { } testCase)
        {
            if (!this.requested.ContainsKey(testCase))
            {
                return null;
            }

            if (type == "test-case-starting")
            {
                this.started.Add(testCase);
            }
        }

        // The remote test process reports the test assembly by its own unique ID (computed from its path within the container),
        // while the test cases refer to the local one; test runners (e.g., the VSTest adapter) look up test metadata by it.
        if (this.assemblyUniqueId is not null && message["AssemblyUniqueID"] is not null)
        {
            message["AssemblyUniqueID"] = this.assemblyUniqueId;
        }

        if (type == "test-assembly-starting")
        {
            message["AssemblyPath"] = this.assemblyPath;
            message.Remove("ConfigFilePath");
            if (this.configFilePath is not null)
            {
                message["ConfigFilePath"] = this.configFilePath;
            }
        }

        return message.ToJsonString(JsonOptions);
    }

    // Control characters are not escaped by the remote test process (e.g., in the output of a test), while JSON does not allow them
    // within strings; a message is a line, so they occur within strings only.
    private static string Escape(string line)
    {
        if (!line.Any(character => character < ' '))
        {
            return line;
        }

        return string.Concat(line.Select(character => character < ' ' ? $"\\u{(int)character:x4}" : character.ToString()));
    }
}
