using System.Text.RegularExpressions;

namespace GroundControl.Docker;

/// <summary>Paths of the host, as Docker reports them.</summary>
internal static class HostPath
{
    private static readonly Regex DesktopMount = new("^/(?:run/desktop/mnt/host|mnt)/([A-Za-z])(?:/(.*))?$", RegexOptions.Compiled);

    /// <summary>
    /// The host path of a bind mount source: Docker Desktop on Windows reports them as mounted into its virtual machine
    /// (<c>/run/desktop/mnt/host/c/...</c>, <c>/mnt/c/...</c> by WSL).
    /// </summary>
    public static string FromMount(string source, bool windowsHost) =>
        windowsHost && DesktopMount.Match(source) is { Success: true } mount
            ? $"{mount.Groups[1].Value.ToUpperInvariant()}:\\{mount.Groups[2].Value.Replace('/', '\\')}"
            : source;

    public static bool Same(string path, string other) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(path),
            Path.TrimEndingDirectorySeparator(other),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The path relative to the directory if it is within it, otherwise null.</summary>
    public static string? Within(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == path || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}") || Path.IsPathRooted(relative) ? null : relative;
    }
}
