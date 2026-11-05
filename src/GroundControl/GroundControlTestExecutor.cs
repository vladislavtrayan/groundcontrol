using System.Diagnostics;
using System.Reflection;
using GroundControl.Configuration;
using GroundControl.Debugging;
using GroundControl.Docker;
using Xunit.Runner.Common;
using Xunit.Sdk;
using Xunit.v3;

namespace GroundControl;

// Runs the test cases by the same test assembly within the tests container and reports its results as of the local test cases.
internal class GroundControlTestExecutor(Assembly assembly) : ITestFrameworkExecutor
{
    public async ValueTask RunTestCases(
        IReadOnlyCollection<ITestCase> testCases,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions,
        CancellationToken? cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken ?? CancellationToken.None);
        var mapper = new GroundControlMessageMapper(assembly.Location, testCases);
        try
        {
            await this.RunAsync(testCases, mapper, executionMessageSink, executionOptions, cancellation);
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested)
        {
            // Reported as messages: test runners (e.g., VSTest adapter) wait for the test assembly to finish rather than for an exception.
            executionMessageSink.OnMessage(Xunit.v3.ErrorMessage.FromException(e));
            var notStarted = ReportNotRun(mapper.NotStarted, e, executionMessageSink);
            if (testCases.FirstOrDefault()?.TestCollection.TestAssembly is { } testAssembly)
            {
                executionMessageSink.OnMessage(new Xunit.v3.TestAssemblyFinished
                {
                    AssemblyUniqueID = testAssembly.UniqueID,
                    ExecutionTime = 0,
                    FinishTime = DateTimeOffset.UtcNow,
                    TestsFailed = notStarted,
                    TestsNotRun = 0,
                    TestsSkipped = 0,
                    TestsTotal = testCases.Count,
                });
            }
        }
    }

    // Reports the test cases as failed by the cause and returns their number: test runners count the results only,
    // so test cases without results are not run silently (e.g., dotnet test succeeds).
    private static int ReportNotRun(IEnumerable<ITestCase> testCases, Exception cause, IMessageSink executionMessageSink)
    {
        var (exceptionTypes, messages, stackTraces, exceptionParentIndices, failureCause) = ExceptionUtility.ExtractMetadata(cause);
        var now = DateTimeOffset.UtcNow;
        var count = 0;
        foreach (var testCase in testCases)
        {
            count++;
            var assemblyUniqueId = testCase.TestCollection.TestAssembly.UniqueID;
            var testUniqueId = UniqueIDGenerator.ForTest(testCase.UniqueID, 0);
            executionMessageSink.OnMessage(new Xunit.v3.TestCaseStarting
            {
                AssemblyUniqueID = assemblyUniqueId,
                TestCollectionUniqueID = testCase.TestCollection.UniqueID,
                TestClassUniqueID = testCase.TestClass?.UniqueID,
                TestMethodUniqueID = testCase.TestMethod?.UniqueID,
                TestCaseUniqueID = testCase.UniqueID,
                Explicit = testCase.Explicit,
                SkipReason = testCase.SkipReason,
                SourceFilePath = testCase.SourceFilePath,
                SourceLineNumber = testCase.SourceLineNumber,
                TestCaseDisplayName = testCase.TestCaseDisplayName,
                TestClassMetadataToken = testCase.TestClassMetadataToken,
                TestClassName = testCase.TestClassName,
                TestClassNamespace = testCase.TestClassNamespace,
                TestClassSimpleName = testCase.TestClassSimpleName,
                TestMethodMetadataToken = testCase.TestMethodMetadataToken,
                TestMethodName = testCase.TestMethodName,
                TestMethodParameterTypesVSTest = testCase.TestMethodParameterTypesVSTest,
                TestMethodReturnTypeVSTest = testCase.TestMethodReturnTypeVSTest,
                Traits = testCase.Traits,
            });
            executionMessageSink.OnMessage(new Xunit.v3.TestStarting
            {
                AssemblyUniqueID = assemblyUniqueId,
                TestCollectionUniqueID = testCase.TestCollection.UniqueID,
                TestClassUniqueID = testCase.TestClass?.UniqueID,
                TestMethodUniqueID = testCase.TestMethod?.UniqueID,
                TestCaseUniqueID = testCase.UniqueID,
                TestUniqueID = testUniqueId,
                TestDisplayName = testCase.TestCaseDisplayName,
                Explicit = testCase.Explicit,
                StartTime = now,
                Timeout = 0,
                Traits = testCase.Traits,
            });
            executionMessageSink.OnMessage(new Xunit.v3.TestFailed
            {
                AssemblyUniqueID = assemblyUniqueId,
                TestCollectionUniqueID = testCase.TestCollection.UniqueID,
                TestClassUniqueID = testCase.TestClass?.UniqueID,
                TestMethodUniqueID = testCase.TestMethod?.UniqueID,
                TestCaseUniqueID = testCase.UniqueID,
                TestUniqueID = testUniqueId,
                Cause = failureCause,
                ExceptionParentIndices = exceptionParentIndices,
                ExceptionTypes = exceptionTypes,
                Messages = messages,
                StackTraces = stackTraces,
                ExecutionTime = 0,
                FinishTime = now,
                Output = string.Empty,
                Warnings = null,
            });
            executionMessageSink.OnMessage(new Xunit.v3.TestFinished
            {
                AssemblyUniqueID = assemblyUniqueId,
                TestCollectionUniqueID = testCase.TestCollection.UniqueID,
                TestClassUniqueID = testCase.TestClass?.UniqueID,
                TestMethodUniqueID = testCase.TestMethod?.UniqueID,
                TestCaseUniqueID = testCase.UniqueID,
                TestUniqueID = testUniqueId,
                ExecutionTime = 0,
                FinishTime = now,
                Output = string.Empty,
                Warnings = null,
                Attachments = new Dictionary<string, TestAttachment>(),
            });
            executionMessageSink.OnMessage(new Xunit.v3.TestCaseFinished
            {
                AssemblyUniqueID = assemblyUniqueId,
                TestCollectionUniqueID = testCase.TestCollection.UniqueID,
                TestClassUniqueID = testCase.TestClass?.UniqueID,
                TestMethodUniqueID = testCase.TestMethod?.UniqueID,
                TestCaseUniqueID = testCase.UniqueID,
                ExecutionTime = 0,
                TestsFailed = 1,
                TestsNotRun = 0,
                TestsSkipped = 0,
                TestsTotal = 1,
            });
        }

        return count;
    }

    private async Task RunAsync(
        IReadOnlyCollection<ITestCase> testCases,
        GroundControlMessageMapper mapper,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions,
        CancellationTokenSource cancellation)
    {
        var options = GroundControlOptions.Load(assembly);
        var docker = new DockerClient(options.Docker, options.DockerContext);
        var project = ComposeProject.Find(docker, options, assembly.Location);
        var container = await project.GetTestContainerAsync(assembly.Location, cancellation.Token);
        await project.WaitUntilReadyAsync(container, cancellation.Token);

        var arguments = new List<string> { "-automated", "async", "-noLogo", "-noColor" };
        if (executionOptions.GetExplicitOption() is { } explicitOption)
        {
            arguments.AddRange(["-explicit", explicitOption.ToString().ToLowerInvariant()]);
        }

        if (executionOptions.GetDisableParallelization() == true)
        {
            arguments.AddRange(["-parallel", "none"]);
        }

        if (executionOptions.GetCulture() is { } culture)
        {
            arguments.AddRange(["-culture", culture.Length == 0 ? "invariant" : culture]);
        }

        // The debugger attaches to the remote test process, the tests wait for it.
        if (Debugger.IsAttached)
        {
            arguments.Add("-waitForDebugger");
        }

        // The local test cases are run as they are (see GroundControlTestCases); the remote test process discovers only their methods,
        // as test cases are run once discovered.
        var remoteAssemblyPath = container.MapPath(assembly.Location);
        var serializations = testCases.Select(testCase => GroundControlTestCases.Serialize(testCase, remoteAssemblyPath)).ToList();
        var filterArguments = testCases
            .Where(testCase => testCase.TestClassName is not null && testCase.TestMethodName is not null)
            .Select(testCase => $"{testCase.TestClassName}.{testCase.TestMethodName}")
            .Distinct()
            .SelectMany(method => new[] { "-method", method })
            .ToList();

        var debuggers = Debugger.IsAttached ? GroundControlDebuggers.Create(options) : [];
        var finished = false;
        var output = new Queue<string>();
        var errors = await GroundControlProcess.RunAsync(
            docker,
            container,
            remoteAssemblyPath,
            arguments,
            filterArguments,
            GroundControlTestCases.FileVariable,
            serializations,
            processId =>
            {
                if (debuggers.Count > 0)
                {
                    var target = new GroundControlDebugTarget(container.Id, container.Name, processId, project.Directory);
                    _ = GroundControlDebuggers.AttachAsync(debuggers, target, cancellation.Token);
                }
            },
            line =>
            {
                var assemblyFinished = line.Contains("\"test-assembly-finished\"");
                finished |= assemblyFinished;
                output.Enqueue(line);
                if (output.Count > 20)
                {
                    output.Dequeue();
                }

                // Requested test cases the remote test process has not run are reported before the test assembly finishes.
                if (assemblyFinished)
                {
                    ReportNotRun(
                        mapper.NotStarted.ToList(),
                        new InvalidOperationException($"The test case has not run in container {container.Name}."),
                        executionMessageSink);
                }

                if (mapper.Map(line) is { } json
                    && MessageSinkMessageDeserializer.Deserialize(json, null) is { } message
                    && !executionMessageSink.OnMessage(message))
                {
                    cancellation.Cancel();
                }
            },
            cancellation.Token);

        if (!finished && !cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Tests have not finished in container {container.Name}:{Environment.NewLine}{string.Join(Environment.NewLine, output)}{Environment.NewLine}{errors}");
        }
    }
}
