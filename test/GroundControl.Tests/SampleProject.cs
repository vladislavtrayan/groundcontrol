using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GroundControl.Tests;

// The Docker Compose project of GroundControl.Tests.Sample, started for the tests and removed afterwards (with the tests container).
public class SampleProject : IAsyncLifetime
{
    private const string Name = "GroundControl.Tests.Sample";

    private readonly string directory;
    private readonly string[] composeFiles;

    public SampleProject()
        : this([])
    {
    }

    // The project of the given Compose files (by default, compose.yaml).
    protected SampleProject(string[] composeFiles)
    {
        // test/GroundControl.Tests/bin/<configuration>/<framework> → test/GroundControl.Tests.Sample/bin/<configuration>/<framework>
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tests = output.Parent!.Parent!.Parent!;
        this.directory = Path.Combine(tests.Parent!.FullName, Name);
        this.AssemblyPath = Path.Combine(this.directory, "bin", output.Parent.Name, output.Name, $"{Name}.dll");
        this.composeFiles = composeFiles;
    }

    public string AssemblyPath { get; }

    private bool Available { get; set; }

    public async ValueTask InitializeAsync()
    {
        this.Available = (await RunAsync("docker", ["version"], this.directory)).ExitCode == 0;
        if (this.Available)
        {
            var up = await this.RunComposeAsync(null, "up", "--detach", "--wait");
            Assert.True(up.ExitCode == 0, $"docker compose up has failed:{Environment.NewLine}{up.Error}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.Available)
        {
            // The tests container GroundControl creates is labeled as a service of the project: an orphan, if a run has not removed it.
            await this.RunComposeAsync(null, "down", "--remove-orphans");
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

    // Starts a test run without waiting for it (e.g., to kill it as a test host dies).
    public Process Start(IEnumerable<string> arguments)
    {
        Assert.SkipUnless(this.Available, "Docker is not available.");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = this.directory,
        };
        foreach (var argument in (string[])[this.AssemblyPath, "-automated", "-noLogo", "-noColor", .. arguments])
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    // Runs a Docker command and returns its exit code and output lines.
    public Task<(int ExitCode, List<string> Output, string Error)> DockerAsync(params string[] arguments)
    {
        Assert.SkipUnless(this.Available, "Docker is not available.");
        return RunAsync("docker", arguments, this.directory);
    }

    // Runs a Docker Compose command of the project with the environment (e.g., variables of its Compose files).
    public Task<(int ExitCode, List<string> Output, string Error)> ComposeAsync(IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        Assert.SkipUnless(this.Available, "Docker is not available.");
        return this.RunComposeAsync(environment, arguments);
    }

    private Task<(int ExitCode, List<string> Output, string Error)> RunComposeAsync(IReadOnlyDictionary<string, string>? environment, params string[] arguments) =>
        RunAsync("docker", ["compose", .. this.composeFiles.SelectMany(file => new[] { "--file", file }), .. arguments], this.directory, environment);

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

// The Docker Compose project of GroundControl.Tests.Sample declaring the tests service (compose.groundcontrol.yaml).
public sealed class DeclaredSampleProject() : SampleProject(["compose.yaml", "compose.groundcontrol.yaml"]);
