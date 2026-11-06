using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GroundControl.Docker;

/// <summary>The endpoint of the Docker Engine API, as the Docker CLI resolves it.</summary>
internal static class DockerEndpoint
{
    /// <summary>
    /// The host (<c>DOCKER_HOST</c> by default), otherwise the endpoint of the context (<c>DOCKER_CONTEXT</c>, otherwise the current one)
    /// in the context store of the Docker CLI configuration directory.
    /// </summary>
    public static string Resolve(string? host, string? context, string configDirectory, bool windows)
    {
        if ((host ?? Environment.GetEnvironmentVariable("DOCKER_HOST")) is { Length: > 0 } configured)
        {
            return Normalize(configured);
        }

        var name = context ?? (Environment.GetEnvironmentVariable("DOCKER_CONTEXT") is { Length: > 0 } variable ? variable : CurrentContext(configDirectory));
        if (name is null or "default")
        {
            return windows ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock";
        }

        var meta = Path.Combine(configDirectory, "contexts", "meta", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant(), "meta.json");
        if (!File.Exists(meta))
        {
            throw new InvalidOperationException($"Docker context {name} is not found in {Path.Combine(configDirectory, "contexts")}.");
        }

        return JsonDocument.Parse(File.ReadAllText(meta)).RootElement.GetProperty("Endpoints").GetProperty("docker").GetProperty("Host").GetString() is { Length: > 0 } endpoint
            ? Normalize(endpoint)
            : throw new InvalidOperationException($"Docker context {name} has no Docker endpoint ({meta}).");
    }

    /// <summary>The configuration directory of the Docker CLI: <c>DOCKER_CONFIG</c>, otherwise <c>~/.docker</c>.</summary>
    public static string ConfigDirectory =>
        Environment.GetEnvironmentVariable("DOCKER_CONFIG") is { Length: > 0 } directory
            ? directory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");

    private static string? CurrentContext(string configDirectory)
    {
        var config = Path.Combine(configDirectory, "config.json");
        return File.Exists(config) && JsonDocument.Parse(File.ReadAllText(config)).RootElement.TryGetProperty("currentContext", out var context)
            ? context.GetString()
            : null;
    }

    /// <summary>The context store writes named pipes as <c>npipe:////./pipe/name</c>, while the client reads <c>npipe://./pipe/name</c>.</summary>
    private static string Normalize(string endpoint) => endpoint.Replace("npipe:////./", "npipe://./", StringComparison.Ordinal);
}
