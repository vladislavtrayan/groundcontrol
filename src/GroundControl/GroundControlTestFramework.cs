using System.Reflection;
using Xunit.Sdk;
using Xunit.v3;

namespace GroundControl;

// Runs tests within the tests container of a Docker Compose project wherever they are started from (IDE, command line, pipeline),
// so tests address services by their Docker Compose names.
public class GroundControlTestFramework : XunitTestFramework
{
    public GroundControlTestFramework()
    {
    }

    public GroundControlTestFramework(string? configFileName)
        : base(configFileName)
    {
    }

    // The test process within the tests container is given the local test cases to run (see TestCaseSerialization).
    protected override ITestFrameworkExecutor CreateExecutor(Assembly assembly) =>
        Environment.GetEnvironmentVariable(TestCaseSerialization.FileVariable) is { Length: > 0 } testCasesPath
            ? new LocalTestCasesExecutor(base.CreateExecutor(assembly), testCasesPath)
            : new ContainerTestExecutor(assembly);

    // Runs the local test cases rather than the ones discovered by the remote test process.
    private sealed class LocalTestCasesExecutor(ITestFrameworkExecutor executor, string testCasesPath) : ITestFrameworkExecutor, IAsyncDisposable
    {
        public ValueTask RunTestCases(
            IReadOnlyCollection<ITestCase> testCases,
            IMessageSink executionMessageSink,
            ITestFrameworkExecutionOptions executionOptions,
            CancellationToken? cancellationToken) =>
            executor.RunTestCases(TestCaseSerialization.Load(testCasesPath), executionMessageSink, executionOptions, cancellationToken);

        public ValueTask DisposeAsync() => executor is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }
}
