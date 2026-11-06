using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GroundControl.Configuration;

/// <summary>
/// Settings of remote test runs, all optional: assembly metadata of the test assembly, e.g.,
/// <c>&lt;AssemblyMetadata Include="GroundControl.Network" Value="backend" /&gt;</c> in the test project, overridden by environment variables,
/// e.g., <c>GROUNDCONTROL_NETWORK=backend</c> (<c>GROUNDCONTROL_</c> followed by the setting name in upper snake case).
/// </summary>
/// <remarks>
/// <c>GROUNDCONTROL_TEST_CASES</c> and <c>GROUNDCONTROL_CONTAINER</c> are not settings: they are given to the remote test process.
/// </remarks>
public sealed record GroundControlOptions
{
    private const string MetadataPrefix = "GroundControl.";
    private const string VariablePrefix = "GROUNDCONTROL_";

    /// <summary>
    /// The Docker Compose project name; by default, the containers of the project are found by the project directory.
    /// </summary>
    public string? Project { get; init; }

    /// <summary>
    /// The Docker Compose project directory, relative to the test assembly; by default, the closest directory of the test assembly
    /// containing a Compose file.
    /// </summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>
    /// The service of the tests container, which the services reach the tests by (the tests are given it in <c>GROUNDCONTROL_CONTAINER</c>).
    /// The test runs in progress share the container, as a host. If the project declares the service, its container runs the tests (kept up
    /// with the project); otherwise, GroundControl creates one for the test runs and removes it after the last of them.
    /// </summary>
    public string Container { get; init; } = "groundcontrol";

    /// <summary>
    /// The image of the tests container GroundControl creates (not of a declared tests service), with <c>sh</c>, <c>sleep</c>, <c>cat</c>
    /// and <c>rm</c> (as the .NET images have); by default, the .NET (or ASP.NET Core) image of the test assembly runtime.
    /// </summary>
    public string? Image { get; init; }

    /// <summary>The network of the tests container; by default, the one most services of the project are on.</summary>
    public string? Network { get; init; }

    /// <summary>
    /// How long to wait for the project to be ready: every service healthy (or running if it has no healthcheck), and one-off services
    /// (e.g., migrations) completed successfully. Seconds, or a time span (e.g., <c>00:05:00</c>); <c>0</c> not to wait.
    /// </summary>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The Docker Engine API endpoint (e.g., <c>tcp://host:2375</c>); by default, <c>DOCKER_HOST</c>, otherwise the one of the Docker context.
    /// </summary>
    public string? DockerHost { get; init; }

    /// <summary>
    /// The Docker CLI context, whose endpoint is read from the context store; by default, <c>DOCKER_CONTEXT</c>, otherwise the current one.
    /// </summary>
    public string? DockerContext { get; init; }

    /// <summary>Loads the settings of the test assembly: its metadata, overridden by the environment variables.</summary>
    /// <param name="assembly">The test assembly.</param>
    /// <returns>The settings, with the defaults of the ones not set.</returns>
    public static GroundControlOptions Load(Assembly assembly) =>
        Load(
            assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Select(attribute => (attribute.Key, attribute.Value)),
            Environment.GetEnvironmentVariable);

    /// <summary>Loads the settings from the assembly metadata (key and value pairs) and the environment variables (by name).</summary>
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
