using System.Reflection;
using Xunit.Sdk;
using Xunit.v3;

namespace GroundControl;

/// <summary>
/// The xUnit.net test framework running tests within a container of the Docker Compose project of the tests, wherever they are started from
/// (IDE, command line, pipeline), so tests address services by their Docker Compose names.
/// </summary>
public class GroundControlTestFramework : XunitTestFramework
{
    /// <summary>Creates the test framework with the configuration file of the test assembly.</summary>
    public GroundControlTestFramework()
    {
    }

    /// <summary>Creates the test framework with the given configuration file.</summary>
    /// <param name="configFileName">The configuration file (e.g., <c>xunit.runner.json</c>); by default, the one of the test assembly.</param>
    public GroundControlTestFramework(string? configFileName)
        : base(configFileName)
    {
    }

    /// <summary>
    /// Creates the executor running the test cases within the tests container; within it, the test process is given the local test cases to run.
    /// </summary>
    /// <param name="assembly">The test assembly.</param>
    /// <returns>The executor.</returns>
    protected override ITestFrameworkExecutor CreateExecutor(Assembly assembly) =>
        Environment.GetEnvironmentVariable(TestCaseSerialization.FileVariable) is { Length: > 0 } testCasesPath
            ? new LocalTestCasesExecutor(base.CreateExecutor(assembly), testCasesPath)
            : new ContainerTestExecutor(assembly);

    /// <summary>Runs the local test cases rather than the ones discovered by the remote test process.</summary>
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
