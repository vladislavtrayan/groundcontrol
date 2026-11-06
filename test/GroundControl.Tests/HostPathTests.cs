using GroundControl.Docker;
using Xunit;

namespace GroundControl.Tests;

public class HostPathTests
{
    [Theory]
    [InlineData("/run/desktop/mnt/host/c/Work/project", true, @"C:\Work\project")]
    [InlineData("/run/desktop/mnt/host/c", true, @"C:\")]
    [InlineData("/mnt/d/Work/project", true, @"D:\Work\project")]
    [InlineData(@"C:\Work\project", true, @"C:\Work\project")]
    [InlineData("/home/user/project", true, "/home/user/project")]
    [InlineData("/mnt/d/Work/project", false, "/mnt/d/Work/project")]
    public void MapsMountSourcesToHostPaths(string source, bool windowsHost, string hostPath) =>
        Assert.Equal(hostPath, HostPath.FromMount(source, windowsHost));

    [Fact]
    public void FindsPathsWithinDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "project");

        Assert.Equal(Path.Combine("bin", "Tests.dll"), HostPath.Within(directory, Path.Combine(directory, "bin", "Tests.dll")));
        Assert.Equal(".", HostPath.Within(directory, directory));
        Assert.Null(HostPath.Within(directory, Path.Combine(Path.GetTempPath(), "project-other")));
        Assert.Null(HostPath.Within(directory, Path.GetTempPath()));
    }
}
