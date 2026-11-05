using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GroundControl.Tests;

// Runs GroundControl.Sample by GroundControl within its Docker Compose project, as test runners run test assemblies.
[Trait("Category", "Docker")]
public class SampleTests(SampleProject sample) : IClassFixture<SampleProject>
{
    // Tests of GroundControl.Sample run by default (its explicit test is not).
    private const int Tests = 8;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunsTestsWithinContainer(bool preEnumerateTheories)
    {
        var run = await sample.RunAsync(preEnumerateTheories ? ["-preEnumerateTheories"] : []);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Of("error"));
        Assert.Equal(Tests, run.Of("test-passed").Count);
        Assert.Empty(run.Of("test-failed"));
        var finished = Assert.Single(run.Of("test-assembly-finished"));
        Assert.Equal(Tests, finished.GetProperty("TestsTotal").GetInt32() - finished.GetProperty("TestsNotRun").GetInt32());
    }

    [Fact]
    public async Task ReportsTestsByLocalAssembly()
    {
        var run = await sample.RunAsync(["-preEnumerateTheories"]);

        var assembly = Assert.Single(run.Messages.Select(message => message.GetProperty("AssemblyUniqueID").GetString()).Distinct());
        var starting = Assert.Single(run.Of("test-assembly-starting"));
        Assert.Equal(assembly, starting.GetProperty("AssemblyUniqueID").GetString());
        Assert.Equal(sample.AssemblyPath, starting.GetProperty("AssemblyPath").GetString());
    }

    [Fact]
    public async Task KeepsDisplayNames()
    {
        var run = await sample.RunAsync(["-preEnumerateTheories"]);

        var names = run.Of("test-starting").Select(message => message.GetProperty("TestDisplayName").GetString()).ToList();
        Assert.Contains("GroundControl.Sample.TheoryTests.NonAsciiRows(value: \"Привет, Zürich, 東京\")", names);
        Assert.Contains(names, name => name!.StartsWith("GroundControl.Sample.TheoryTests.NonAsciiRows(value: \"Mozilla/5.0") && name.EndsWith("\"···)"));
    }

    [Fact]
    public async Task ReportsFailuresWithControlCharacters()
    {
        var run = await sample.RunAsync(["-method", "GroundControl.Sample.ExplicitTests.Fails", "-explicit", "only"]);

        Assert.Empty(run.Of("error"));
        var failed = Assert.Single(run.Of("test-failed"));
        Assert.Contains("Expected failure, with a device control \u0011.", failed.GetProperty("Messages")[0].GetString());
        Assert.Single(run.Of("test-assembly-finished"));
        Assert.NotEqual(0, run.ExitCode);
    }

    [Fact]
    public async Task ReportsTestsNotRunAsFailed()
    {
        var run = await sample.RunAsync([], new Dictionary<string, string> { ["GROUNDCONTROL_SERVICE"] = "missing" });

        Assert.NotEmpty(run.Of("error"));
        Assert.Empty(run.Of("test-passed"));
        Assert.NotEmpty(run.Of("test-failed"));
        Assert.NotEqual(0, run.ExitCode);
    }
}

// The Docker Compose project of GroundControl.Sample, started for the tests and removed afterwards.
public sealed class SampleProject : IAsyncLifetime
{
    private readonly string directory;

    public SampleProject()
    {
        // test/GroundControl.Tests/bin/<configuration>/<framework> → test/GroundControl.Sample/bin/<configuration>/<framework>
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tests = output.Parent!.Parent!.Parent!;
        this.directory = Path.Combine(tests.Parent!.FullName, "GroundControl.Sample");
        this.AssemblyPath = Path.Combine(this.directory, "bin", output.Parent.Name, output.Name, "GroundControl.Sample.dll");
    }

    public string AssemblyPath { get; }

    private bool Available { get; set; }

    public async ValueTask InitializeAsync()
    {
        this.Available = (await RunAsync("docker", ["version"], this.directory)).ExitCode == 0;
        if (this.Available)
        {
            var up = await RunAsync("docker", ["compose", "up", "--detach", "--wait"], this.directory);
            Assert.True(up.ExitCode == 0, $"docker compose up has failed:{Environment.NewLine}{up.Error}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.Available)
        {
            await RunAsync("docker", ["compose", "down"], this.directory);
        }
    }

    public async Task<SampleRun> RunAsync(IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        Assert.SkipUnless(this.Available, "Docker is not available.");
        Assert.True(File.Exists(this.AssemblyPath), $"{this.AssemblyPath} is not built.");

        var result = await RunAsync("dotnet", [this.AssemblyPath, "-automated", "-noLogo", "-noColor", .. arguments], this.directory, environment);
        var messages = new List<JsonElement>();
        foreach (var line in result.Output)
        {
            try
            {
                // xUnit.net writes control characters within messages as they are (e.g., in failure messages).
                var json = string.Concat(line.Select(character => character < ' ' ? $"\\u{(int)character:x4}" : character.ToString()));
                if (JsonDocument.Parse(json).RootElement is { ValueKind: JsonValueKind.Object } message && message.TryGetProperty("$type", out _))
                {
                    messages.Add(message);
                }
            }
            catch (JsonException)
            {
                // Not a message.
            }
        }

        return new SampleRun(result.ExitCode, messages);
    }

    private static async Task<(int ExitCode, List<string> Output, string Error)> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = new List<string>();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                output.Add(line);
            }

            await process.WaitForExitAsync();
            return (process.ExitCode, output, await error);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (-1, [], e.Message);
        }
    }
}

public sealed record SampleRun(int ExitCode, IReadOnlyList<JsonElement> Messages)
{
    public List<JsonElement> Of(string type) => this.Messages.Where(message => message.GetProperty("$type").GetString() == type).ToList();
}
