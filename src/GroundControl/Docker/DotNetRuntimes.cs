using System.Text.Json.Nodes;

namespace GroundControl.Docker;

/// <summary>The .NET runtimes of a test assembly (by its runtime configuration) and of a container.</summary>
internal static class DotNetRuntimes
{
    /// <summary>The .NET image of the test assembly runtime: ASP.NET Core if the test assembly runs on it, otherwise .NET.</summary>
    public static string GetImage(string assemblyPath)
    {
        var options = RuntimeOptions(assemblyPath);
        var version = options?["tfm"]?.GetValue<string>() is { } tfm && tfm.StartsWith("net") ? tfm["net".Length..] : $"{Environment.Version.Major}.0";
        return Frameworks(options).Any(framework => framework?["name"]?.GetValue<string>() == "Microsoft.AspNetCore.App")
            ? $"mcr.microsoft.com/dotnet/aspnet:{version}"
            : $"mcr.microsoft.com/dotnet/runtime:{version}";
    }

    /// <summary>The shared frameworks the test assembly runs on, with their versions.</summary>
    public static List<(string Name, Version Version)> GetFrameworks(string assemblyPath) =>
        Frameworks(RuntimeOptions(assemblyPath))
            .Select(framework => (Name: framework?["name"]?.GetValue<string>(), Version: ParseVersion(framework?["version"]?.GetValue<string>())))
            .Where(framework => framework.Name is not null && framework.Version is not null)
            .Select(framework => (framework.Name!, framework.Version!))
            .ToList();

    /// <summary>
    /// The frameworks (e.g., <c>Microsoft.AspNetCore.App 10.0</c>) the runtimes (the output of <c>dotnet --list-runtimes</c>) do not provide:
    /// a runtime of the framework of the same major version, at least the one of the test assembly (as .NET rolls forward by default).
    /// </summary>
    public static List<string> MissingFrameworks(IEnumerable<(string Name, Version Version)> frameworks, string runtimes)
    {
        var available = runtimes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' '))
            .Where(parts => parts.Length >= 2 && ParseVersion(parts[1]) is not null)
            .Select(parts => (Name: parts[0], Version: ParseVersion(parts[1])!))
            .ToList();
        return frameworks
            .Where(framework => !available.Any(runtime =>
                runtime.Name == framework.Name && runtime.Version.Major == framework.Version.Major && runtime.Version >= framework.Version))
            .Select(framework => $"{framework.Name} {framework.Version.Major}.{framework.Version.Minor}")
            .ToList();
    }

    private static JsonNode? RuntimeOptions(string assemblyPath)
    {
        var runtimeConfig = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        return File.Exists(runtimeConfig) ? JsonNode.Parse(File.ReadAllText(runtimeConfig))?["runtimeOptions"] : null;
    }

    /// <summary>The frameworks of the runtime options: a framework, or several (e.g., ASP.NET Core).</summary>
    private static IEnumerable<JsonNode?> Frameworks(JsonNode? options)
    {
        if (options?["frameworks"] is JsonArray frameworks)
        {
            return frameworks;
        }

        // Not a collection expression: next to a JsonArray, it would be one, which the node of the options cannot be added to.
        return options?["framework"] is { } framework ? new[] { framework } : Array.Empty<JsonNode?>();
    }

    private static Version? ParseVersion(string? value) => value is not null && Version.TryParse(value.Split('-')[0], out var version) ? version : null;
}
