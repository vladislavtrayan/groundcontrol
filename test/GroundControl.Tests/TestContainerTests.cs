using Docker.DotNet.Models;
using GroundControl.Docker;
using Xunit;
using Version = System.Version;

namespace GroundControl.Tests;

public class TestContainerTests
{
    private static readonly string Project = Path.Combine(Path.GetTempPath(), "project");

    [Fact]
    public void MapsMountedPaths()
    {
        var container = TestContainer.Managed("sample", "groundcontrol", "image", "network", Project);

        Assert.Equal("/workspace/bin/Debug/Tests.dll", container.MapPath(Path.Combine(Project, "bin", "Debug", "Tests.dll")));
    }

    [Fact]
    public void RejectsOtherPaths()
    {
        var container = TestContainer.Managed("sample", "groundcontrol", "image", "network", Project);

        Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "project-other", "Tests.dll")));
        Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "Tests.dll")));
    }

    // A declared service mounts the directories anywhere; the closest mount of a path maps it.
    [Fact]
    public void MapsPathsByMountsOfService()
    {
        var container = TestContainer.OfService("sample-groundcontrol-1", "groundcontrol", "network", [(Project, "/src"), (Path.Combine(Project, "bin"), "/bin/")]);

        Assert.Equal("/bin/Debug/Tests.dll", container.MapPath(Path.Combine(Project, "bin", "Debug", "Tests.dll")));
        Assert.Equal("/src/tests/Tests.cs", container.MapPath(Path.Combine(Project, "tests", "Tests.cs")));
        Assert.Equal("/src", container.MapPath(Project));
        Assert.Contains("does not mount", Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "Tests.dll"))).Message);
    }

    [Theory]
    [InlineData("/run/desktop/mnt/host/c/Work/project", true, @"C:\Work\project")]
    [InlineData("/run/desktop/mnt/host/c", true, @"C:\")]
    [InlineData("/mnt/d/Work/project", true, @"D:\Work\project")]
    [InlineData(@"C:\Work\project", true, @"C:\Work\project")]
    [InlineData("/home/user/project", true, "/home/user/project")]
    [InlineData("/mnt/d/Work/project", false, "/mnt/d/Work/project")]
    public void MapsMountSourcesToHostPaths(string source, bool windowsHost, string hostPath) =>
        Assert.Equal(hostPath, TestContainer.ToHostPath(source, windowsHost));

    [Theory]
    [InlineData("""{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}""", "mcr.microsoft.com/dotnet/runtime:8.0")]
    [InlineData(
        """{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}""",
        "mcr.microsoft.com/dotnet/aspnet:10.0")]
    public void SelectsImageOfRuntime(string runtimeConfig, string image)
    {
        var directory = Directory.CreateTempSubdirectory("groundcontrol-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "Tests.runtimeconfig.json"), runtimeConfig);

            Assert.Equal(image, TestContainer.GetImage(Path.Combine(directory.FullName, "Tests.dll")));
            Assert.NotEmpty(TestContainer.GetFrameworks(Path.Combine(directory.FullName, "Tests.dll")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // The runtimes the tests container has (dotnet --list-runtimes) provide the frameworks of the test assembly of the same major version,
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

        Assert.Equal(missing.Length == 0 ? [] : [missing], TestContainer.MissingFrameworks([(framework, Version.Parse(version))], runtimes));
    }

    [Fact]
    public void FindsAllFrameworksMissingWithoutRuntimes() =>
        Assert.Equal(["Microsoft.NETCore.App 8.0"], TestContainer.MissingFrameworks([("Microsoft.NETCore.App", new Version(8, 0, 0))], string.Empty));

    [Fact]
    public void CreatesContainerOfProjectOnNetworkByName()
    {
        var container = TestContainer.Managed("sample", "groundcontrol", "mcr.microsoft.com/dotnet/runtime:8.0", "sample_default", Project);

        var parameters = container.Create();

        Assert.Equal("sample-groundcontrol-1", container.Name);
        Assert.False(container.Declared);
        Assert.Equal(container.Name, parameters.Name);
        Assert.Equal("mcr.microsoft.com/dotnet/runtime:8.0", parameters.Image);
        Assert.Equal(["sleep", "infinity"], parameters.Cmd);
        Assert.Equal("groundcontrol", parameters.Hostname);
        Assert.Equal("sample", parameters.Labels["com.docker.compose.project"]);
        Assert.Equal("groundcontrol", parameters.Labels["com.docker.compose.service"]);
        Assert.Equal(Project, parameters.Labels["com.docker.compose.project.working_dir"]);
        Assert.Equal("False", parameters.Labels["com.docker.compose.oneoff"]);
        Assert.DoesNotContain(ComposeContainer.ConfigHashLabel, parameters.Labels.Keys);
        Assert.True(parameters.HostConfig.Init);
        Assert.Equal(["groundcontrol"], Assert.Single(parameters.NetworkingConfig.EndpointsConfig, endpoint => endpoint.Key == "sample_default").Value.Aliases);
        Assert.Collection(
            parameters.HostConfig.Mounts,
            mount => Assert.Equal(("bind", Project, "/workspace", true), (mount.Type, mount.Source, mount.Target, mount.ReadOnly)),
            mount => Assert.Equal(("volume", "groundcontrol-debugger-tools", "/root/.RiderDebuggerTools", false), (mount.Type, mount.Source, mount.Target, mount.ReadOnly)));
    }

    // The test process runs under a shell which reports its process ID within the container (for the debugger) and kills it once its lease
    // is not renewed (the test host has died).
    [Fact]
    public void RunsWithinContainerUnderLease()
    {
        var container = TestContainer.OfService("sample-groundcontrol-1", "groundcontrol", "sample_default", [(Project, "/tests")]);

        var parameters = container.Exec("/tests/bin", "/tmp/groundcontrol/run", ["dotnet", "/tests/bin/Tests.dll"], ["TESTS=/tmp/groundcontrol/run/test-cases.txt"]);

        Assert.True(container.Declared);
        Assert.Equal(["sh", "-c"], parameters.Cmd.Take(2));
        Assert.Contains("echo $tests >&2", parameters.Cmd[2]);
        Assert.Contains("$run/lease", parameters.Cmd[2]);
        Assert.DoesNotContain("\r", parameters.Cmd[2]);
        Assert.Equal(["sh", "/tmp/groundcontrol/run", "dotnet", "/tests/bin/Tests.dll"], parameters.Cmd.Skip(3));
        Assert.Equal(["GROUNDCONTROL_CONTAINER=groundcontrol", "TESTS=/tmp/groundcontrol/run/test-cases.txt"], parameters.Env);
        Assert.Equal("/tests/bin", parameters.WorkingDir);
        Assert.True(parameters.AttachStdout && parameters.AttachStderr && !parameters.Tty && !parameters.AttachStdin);
    }

    // An existing container serves as the managed one if it runs with the image and the project directory; otherwise it is replaced.
    [Theory]
    [InlineData(true, "image", true)]
    [InlineData(false, "image", false)]
    [InlineData(true, "other-image", false)]
    public void FitsRunningContainerOfImageAndProject(bool running, string image, bool fits)
    {
        var container = TestContainer.Managed("sample", "groundcontrol", "image", "sample_default", Project);
        var existing = new ContainerInspectResponse
        {
            State = new ContainerState { Running = running },
            Config = new Config { Image = image, Labels = new Dictionary<string, string> { ["com.docker.compose.project.working_dir"] = Project } },
        };

        Assert.Equal(fits, container.Fits(existing));
        Assert.False(container.Fits(new ContainerInspectResponse
        {
            State = new ContainerState { Running = true },
            Config = new Config { Image = "image", Labels = new Dictionary<string, string> { ["com.docker.compose.project.working_dir"] = Path.Combine(Project, "other") } },
        }));
    }
}
