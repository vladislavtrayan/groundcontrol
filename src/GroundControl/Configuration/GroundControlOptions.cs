using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GroundControl.Configuration;

// Settings of remote test runs, all optional: assembly metadata of the test assembly, e.g.,
// <AssemblyMetadata Include="GroundControl.Network" Value="backend" /> in the test project, overridden by environment variables,
// e.g., GROUNDCONTROL_NETWORK=backend (GROUNDCONTROL_ followed by the setting name in upper snake case).
// GROUNDCONTROL_TEST_CASES and GROUNDCONTROL_CONTAINER are not settings: they are given to the remote test process.
public sealed record GroundControlOptions
{
    private const string MetadataPrefix = "GroundControl.";
    private const string VariablePrefix = "GROUNDCONTROL_";

    // Docker Compose project name; by default, resolved by Docker Compose (COMPOSE_PROJECT_NAME, .env, Compose files).
    public string? Project { get; init; }

    // Docker Compose project directory (relative to the test assembly); by default, the closest directory of the test assembly
    // containing a Compose file.
    public string? ProjectDirectory { get; init; }

    // Name of the tests container as a service of the project (one test run at a time), which services reach the tests by;
    // the tests are given it in GROUNDCONTROL_CONTAINER.
    public string Container { get; init; } = "groundcontrol";

    // Image of the tests container; by default, the .NET (or ASP.NET Core) image of the test assembly runtime.
    public string? Image { get; init; }

    // Network of the tests container; by default, the one most services of the project are on.
    public string? Network { get; init; }

    // How long to wait for the project to be ready (every service healthy, or running if it has no healthcheck, one-off services,
    // e.g., migrations, completed successfully, and no other test run in progress): seconds, or a time span (e.g., 00:05:00); 0 not to wait.
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromMinutes(5);

    // Docker Engine API endpoint (e.g., tcp://host:2375); by default, DOCKER_HOST, otherwise the one of the Docker context.
    public string? DockerHost { get; init; }

    // Docker CLI context (its endpoint is read from the context store); by default, DOCKER_CONTEXT, otherwise the current one.
    public string? DockerContext { get; init; }

    public static GroundControlOptions Load(Assembly assembly) =>
        Load(
            assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Select(attribute => (attribute.Key, attribute.Value)),
            Environment.GetEnvironmentVariable);

    // Loads the settings from the assembly metadata (key and value pairs) and the environment variables (by name).
    internal static GroundControlOptions Load(IEnumerable<(string Key, string? Value)> assemblyMetadata, Func<string, string?> environment)
    {
        var metadata = assemblyMetadata
            .Where(attribute => attribute.Key.StartsWith(MetadataPrefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(attribute => attribute.Key[MetadataPrefix.Length..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

        string? Get(string name) =>
            environment(VariablePrefix + Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", "_").ToUpperInvariant()) is { Length: > 0 } variable
                ? variable
                : metadata.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;

        var defaults = new GroundControlOptions();
        return new GroundControlOptions
        {
            Project = Get(nameof(Project)),
            ProjectDirectory = Get(nameof(ProjectDirectory)),
            Container = Get(nameof(Container)) ?? defaults.Container,
            Image = Get(nameof(Image)),
            Network = Get(nameof(Network)),
            ReadinessTimeout = Get(nameof(ReadinessTimeout)) is { } timeout
                ? int.TryParse(timeout, out var seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Parse(timeout, CultureInfo.InvariantCulture)
                : defaults.ReadinessTimeout,
            DockerHost = Get(nameof(DockerHost)),
            DockerContext = Get(nameof(DockerContext)),
        };
    }
}
