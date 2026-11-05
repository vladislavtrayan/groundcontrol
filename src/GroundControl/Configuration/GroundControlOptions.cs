using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GroundControl.Configuration;

// Settings of remote test runs, all optional: assembly metadata of the test assembly, e.g.,
// <AssemblyMetadata Include="GroundControl.Service" Value="tests" /> in the test project, overridden by environment variables,
// e.g., GROUNDCONTROL_SERVICE=tests (GROUNDCONTROL_ followed by the setting name in upper snake case).
// GROUNDCONTROL_TEST_CASES is not a setting: it is set by the remote test process (see GroundControlTestCases).
public sealed record GroundControlOptions
{
    private const string MetadataPrefix = "GroundControl.";
    private const string VariablePrefix = "GROUNDCONTROL_";

    // Docker Compose project name; by default, resolved by Docker Compose (COMPOSE_PROJECT_NAME, .env, Compose files).
    public string? Project { get; init; }

    // Docker Compose project directory; by default, the closest directory of the test assembly containing a Compose file.
    public string? ProjectDirectory { get; init; }

    // Docker Compose service running test processes (kept running, e.g., to keep debugger tools in it);
    // by default, a container is created for each test run on the network of the project.
    public string? Service { get; init; }

    // Image of the containers created for test runs; by default, the .NET (or ASP.NET Core) image of the test assembly runtime.
    public string? Image { get; init; }

    // Network of the containers created for test runs; by default, the one most services of the project are on.
    public string? Network { get; init; }

    // How long to wait for the project to be ready: seconds, or a time span (e.g., 00:05:00).
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public ReadinessPolicy Readiness { get; init; } = ReadinessPolicy.AllServices;

    // Docker CLI executable.
    public string Docker { get; init; } = "docker";

    // Docker context; by default, the current one.
    public string? DockerContext { get; init; }

    // Debugger integration (see GroundControlDebuggers).
    public string? Debugger { get; init; }

    public static GroundControlOptions Load(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key.StartsWith(MetadataPrefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(attribute => attribute.Key[MetadataPrefix.Length..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

        string? Get(string name) =>
            Environment.GetEnvironmentVariable(VariablePrefix + Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", "_").ToUpperInvariant()) is { Length: > 0 } variable
                ? variable
                : metadata.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;

        var defaults = new GroundControlOptions();
        return new GroundControlOptions
        {
            Project = Get(nameof(Project)),
            ProjectDirectory = Get(nameof(ProjectDirectory)),
            Service = Get(nameof(Service)),
            Image = Get(nameof(Image)),
            Network = Get(nameof(Network)),
            ReadinessTimeout = Get(nameof(ReadinessTimeout)) is { } timeout
                ? int.TryParse(timeout, out var seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Parse(timeout, CultureInfo.InvariantCulture)
                : defaults.ReadinessTimeout,
            Readiness = Get(nameof(Readiness)) is { } readiness ? Enum.Parse<ReadinessPolicy>(readiness, ignoreCase: true) : defaults.Readiness,
            Docker = Get(nameof(Docker)) ?? defaults.Docker,
            DockerContext = Get(nameof(DockerContext)),
            Debugger = Get(nameof(Debugger)),
        };
    }
}

public enum ReadinessPolicy
{
    // Every service of the project is healthy, or running if it has no healthcheck,
    // and one-off services (e.g., migrations) have completed successfully.
    AllServices,

    // Tests run without waiting.
    None,
}
