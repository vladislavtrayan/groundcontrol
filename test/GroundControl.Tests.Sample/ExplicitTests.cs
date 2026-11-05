using Xunit;

namespace GroundControl.Tests.Sample;

public class ExplicitTests
{
    // Failure messages are reported with control characters as they are, while JSON strings do not allow them.
    [Fact(Explicit = true)]
    public void Fails() => Assert.Fail("Expected failure, with a device control \u0011.");
}
