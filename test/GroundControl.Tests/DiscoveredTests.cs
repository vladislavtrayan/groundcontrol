using Xunit;

namespace GroundControl.Tests;

// Test cases discovered by the tests (see TestCaseDiscovery), not run themselves.
public class DiscoveredTests
{
    [Fact(Explicit = true)]
    public void Fact()
    {
    }

    [Theory(Explicit = true)]
    [InlineData(1, "a")]
    [InlineData(2, "Привет")]
    public void Theory(int number, string text)
    {
        _ = (number, text);
    }
}
