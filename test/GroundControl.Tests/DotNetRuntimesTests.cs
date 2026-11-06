using GroundControl.Docker;
using Xunit;

namespace GroundControl.Tests;

public class DotNetRuntimesTests
{
    [Theory]
    [InlineData("""{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}""", "mcr.microsoft.com/dotnet/runtime:8.0")]
    [InlineData(
        """{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}""",
        "mcr.microsoft.com/dotnet/aspnet:10.0")]
    public void ReadsRuntimeOfAssembly(string runtimeConfig, string image)
    {
        var directory = Directory.CreateTempSubdirectory("groundcontrol-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "Tests.runtimeconfig.json"), runtimeConfig);

            Assert.Equal(image, DotNetRuntimes.GetImage(Path.Combine(directory.FullName, "Tests.dll")));
            Assert.Contains(DotNetRuntimes.GetFrameworks(Path.Combine(directory.FullName, "Tests.dll")), framework => framework.Name == "Microsoft.NETCore.App");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // The runtimes of a container (dotnet --list-runtimes) provide the frameworks of the test assembly of the same major version,
    // at least its version, as .NET rolls forward.
    [Theory]
    [InlineData("Microsoft.NETCore.App", "8.0.0", "")]
    [InlineData("Microsoft.NETCore.App", "8.0.5", "")]
    [InlineData("Microsoft.NETCore.App", "8.0.20", "Microsoft.NETCore.App 8.0")]
    [InlineData("Microsoft.AspNetCore.App", "10.0.0", "")]
    [InlineData("Microsoft.AspNetCore.App", "9.0.0", "Microsoft.AspNetCore.App 9.0")]
    [InlineData("Microsoft.WindowsDesktop.App", "8.0.0", "Microsoft.WindowsDesktop.App 8.0")]
    public void FindsFrameworksMissing(string framework, string version, string missing)
    {
        const string runtimes =
            """
            Microsoft.AspNetCore.App 10.0.0-rc.2.25502.107 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]
            Microsoft.NETCore.App 8.0.11 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
            Microsoft.NETCore.App 10.0.0-rc.2.25502.107 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
            """;

        Assert.Equal(missing.Length == 0 ? [] : [missing], DotNetRuntimes.MissingFrameworks([(framework, Version.Parse(version))], runtimes));
    }

    [Fact]
    public void FindsAllFrameworksMissingWithoutRuntimes() =>
        Assert.Equal(["Microsoft.NETCore.App 8.0"], DotNetRuntimes.MissingFrameworks([("Microsoft.NETCore.App", new Version(8, 0, 0))], string.Empty));
}
