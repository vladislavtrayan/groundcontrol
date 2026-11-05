using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Sdk;

namespace GroundControl;

// Maps messages of the remote test process to the local test cases. The remote test process runs the local test cases as they are
// (see TestCaseSerialization), so it reports their unique IDs; only the test assembly path differs.
internal class MessageMapper
{
    // xUnit.net JSON deserializer does not support escaped characters (e.g., '+' as '+').
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string assemblyPath;
    private readonly string? assemblyUniqueId;
    private readonly string? configFilePath;
    private readonly Dictionary<string, ITestCase> requested;
    private readonly Dictionary<string, Progress> started = [];
    private readonly HashSet<string> finished = [];

    public MessageMapper(string assemblyPath, IReadOnlyCollection<ITestCase> testCases)
    {
        this.assemblyPath = assemblyPath;
        this.assemblyUniqueId = testCases.FirstOrDefault()?.TestCollection.TestAssembly.UniqueID;
        this.configFilePath = testCases.FirstOrDefault()?.TestCollection.TestAssembly.ConfigFilePath;
        this.requested = testCases.ToDictionary(testCase => testCase.UniqueID);
    }

    // Requested test cases the remote test process has not finished (e.g., it has crashed running them).
    public IEnumerable<PendingTestCase> Pending =>
        this.requested.Values
            .Where(testCase => !this.finished.Contains(testCase.UniqueID))
            .Select(testCase => this.started.TryGetValue(testCase.UniqueID, out var progress)
                ? new PendingTestCase(testCase, true, progress.Tests, progress.RunningTest)
                : new PendingTestCase(testCase, false, 0, null));

    // Results reported so far, as counted by the test assembly summary.
    public int TestsTotal { get; private set; }

    public int TestsFailed { get; private set; }

    public int TestsSkipped { get; private set; }

    public int TestsNotRun { get; private set; }

    // Returns the message mapped to the local test cases, or null if it is not a message (e.g., the output of the tests)
    // or it is not reported locally.
    public MappedMessage? Map(string line)
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

            this.Track(type, testCase, message);
        }

        // The remote test process reports the test assembly by its own unique ID (computed from its path within the container),
        // while the test cases refer to the local one; test runners (e.g., the VSTest adapter) look up test metadata by it.
        // Messages are passed as they are otherwise: xUnit.net writes control characters within them as they are (e.g., in failure
        // messages) and does not read them escaped.
        if (type == "test-assembly-starting")
        {
            message["AssemblyPath"] = this.assemblyPath;
            message.Remove("ConfigFilePath");
            if (this.configFilePath is not null)
            {
                message["ConfigFilePath"] = this.configFilePath;
            }

            if (this.assemblyUniqueId is not null)
            {
                message["AssemblyUniqueID"] = this.assemblyUniqueId;
            }

            return new MappedMessage(type, message.ToJsonString(JsonOptions));
        }

        return new MappedMessage(
            type,
            this.assemblyUniqueId is not null && message["AssemblyUniqueID"]?.GetValue<string>() is { } remoteAssemblyUniqueId
                ? line.Replace($"\"{remoteAssemblyUniqueId}\"", $"\"{this.assemblyUniqueId}\"")
                : line);
    }

    private void Track(string type, string testCase, JsonObject message)
    {
        switch (type)
        {
            case "test-case-starting":
                this.started.TryAdd(testCase, new Progress());
                break;
            case "test-starting":
                var progress = this.started.TryGetValue(testCase, out var running) ? running : this.started[testCase] = new Progress();
                progress.Tests++;
                progress.RunningTest = message["TestUniqueID"]?.GetValue<string>();
                break;
            case "test-finished":
                if (this.started.TryGetValue(testCase, out var finishing))
                {
                    finishing.RunningTest = null;
                }

                break;
            case "test-case-finished":
                this.finished.Add(testCase);
                break;
            case "test-passed":
                this.TestsTotal++;
                break;
            case "test-failed":
                this.TestsTotal++;
                this.TestsFailed++;
                break;
            case "test-skipped":
                this.TestsTotal++;
                this.TestsSkipped++;
                break;
            case "test-not-run":
                this.TestsTotal++;
                this.TestsNotRun++;
                break;
        }
    }

    // Control characters escaped to read a message (JSON does not allow them). A message is a line, so they occur within strings only.
    private static string Escape(string line) =>
        line.Any(character => character < ' ')
            ? string.Concat(line.Select(character => character < ' ' ? $"\\u{(int)character:x4}" : character.ToString()))
            : line;

    private sealed class Progress
    {
        public int Tests { get; set; }

        public string? RunningTest { get; set; }
    }
}

internal sealed record MappedMessage(string Type, string Json);

// A requested test case not finished: the number of its tests started (the index of the next one) and its running test, if any.
internal sealed record PendingTestCase(ITestCase TestCase, bool Started, int TestsStarted, string? RunningTest);
