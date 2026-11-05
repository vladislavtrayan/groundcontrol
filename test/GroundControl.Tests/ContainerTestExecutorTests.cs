using Xunit;
using Xunit.Runner.Common;
using Xunit.Sdk;

namespace GroundControl.Tests;

public class ContainerTestExecutorTests
{
    [Fact]
    public void PassesNoArgumentsByDefault() =>
        Assert.Empty(ContainerTestExecutor.Arguments(TestFrameworkOptions.ForExecution(new TestAssemblyConfiguration())));

    // Options of the local test run are forwarded to the remote one, so runs are reproducible (e.g., by their seed) and stop as asked.
    [Fact]
    public void PassesExecutionOptions()
    {
        var configuration = new TestAssemblyConfiguration
        {
            Culture = string.Empty,
            ExplicitOption = ExplicitOption.Only,
            MaxParallelThreads = 4,
            ParallelAlgorithm = ParallelAlgorithm.Aggressive,
            ParallelizeTestCollections = false,
            Seed = 12345,
            ShowLiveOutput = true,
            StopOnFail = true,
        };

        var arguments = ContainerTestExecutor.Arguments(TestFrameworkOptions.ForExecution(configuration));

        Assert.Equal(
            [
                "-explicit", "only", "-parallel", "none", "-parallelAlgorithm", "aggressive", "-maxThreads", "4", "-culture", "invariant",
                "-seed", "12345", "-stopOnFail", "-showLiveOutput",
            ],
            arguments);
    }

    [Theory]
    [InlineData(-1, "unlimited")]
    [InlineData(8, "8")]
    public void PassesMaxThreads(int maxParallelThreads, string argument)
    {
        var arguments = ContainerTestExecutor.Arguments(TestFrameworkOptions.ForExecution(new TestAssemblyConfiguration { MaxParallelThreads = maxParallelThreads }));

        Assert.Equal(["-maxThreads", argument], arguments);
    }
}
