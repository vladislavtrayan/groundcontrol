using GroundControl.Docker;
using Xunit;

namespace GroundControl.Tests;

public class TestContainerTests
{
    private static readonly string Project = Path.Combine(Path.GetTempPath(), "project");

    // The tests service mounts the directories anywhere; the closest mount of a path maps it.
    [Fact]
    public void MapsPathsByMounts()
    {
        var container = new TestContainer("sample-groundcontrol-1", "groundcontrol", "network", [(Project, "/src"), (Path.Combine(Project, "bin"), "/bin/")]);

        Assert.Equal("/bin/Debug/Tests.dll", container.MapPath(Path.Combine(Project, "bin", "Debug", "Tests.dll")));
        Assert.Equal("/src/tests/Tests.cs", container.MapPath(Path.Combine(Project, "tests", "Tests.cs")));
        Assert.Equal("/src", container.MapPath(Project));
        Assert.Contains("does not mount", Assert.Throws<InvalidOperationException>(() => container.MapPath(Path.Combine(Path.GetTempPath(), "Tests.dll"))).Message);
    }

    // The test process runs under run.sh, which reports its process ID within the container (for the debugger) and kills it once its lease
    // is not renewed (the test host has died).
    [Fact]
    public void RunsWithinContainerUnderLease()
    {
        var container = new TestContainer("sample-groundcontrol-1", "groundcontrol", "sample_default", [(Project, "/tests")]);

        var parameters = container.Exec("/tests/bin", "/tmp/groundcontrol/run", ["dotnet", "/tests/bin/Tests.dll"], ["TESTS=/tmp/groundcontrol/run/test-cases.txt"]);

        Assert.Equal(["sh", "-c"], parameters.Cmd.Take(2));
        Assert.Contains("echo $tests >&2", parameters.Cmd[2]);
        Assert.Contains("$run/lease", parameters.Cmd[2]);
        Assert.DoesNotContain("\r", parameters.Cmd[2]);
        Assert.Equal(["sh", "/tmp/groundcontrol/run", "dotnet", "/tests/bin/Tests.dll"], parameters.Cmd.Skip(3));
        Assert.Equal(["GROUNDCONTROL_CONTAINER=groundcontrol", "TESTS=/tmp/groundcontrol/run/test-cases.txt"], parameters.Env);
        Assert.Equal("/tests/bin", parameters.WorkingDir);
        Assert.True(parameters.AttachStdout && parameters.AttachStderr && !parameters.Tty && !parameters.AttachStdin);
    }
}
