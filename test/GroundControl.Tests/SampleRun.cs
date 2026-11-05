using System.Text.Json;

namespace GroundControl.Tests;

// A run of GroundControl.Tests.Sample: its exit code and the messages it reported.
public sealed record SampleRun(int ExitCode, IReadOnlyList<JsonElement> Messages)
{
    public List<JsonElement> Of(string type) => this.Messages.Where(message => message.GetProperty("$type").GetString() == type).ToList();
}
