using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using GroundControl.Configuration;
using GroundControl.Debugging;
using GroundControl.Docker;
using Xunit.Runner.Common;
using Xunit.Sdk;
using Xunit.v3;

namespace GroundControl;

// Runs the test cases by the same test assembly within the tests container and reports its results as of the local test cases.
internal class ContainerTestExecutor(Assembly assembly) : ITestFrameworkExecutor
{
    public async ValueTask RunTestCases(
        IReadOnlyCollection<ITestCase> testCases,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions,
        CancellationToken? cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken ?? CancellationToken.None);
        var mapper = new MessageMapper(assembly.Location, testCases);
        try
        {
            await this.RunAsync(testCases, mapper, executionMessageSink, executionOptions, cancellation);
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested)
        {
            // Reported as messages: test runners (e.g., VSTest adapter) wait for the test assembly to finish rather than for an exception.
            executionMessageSink.OnMessage(Xunit.v3.ErrorMessage.FromException(e));
            var failed = ReportFailed(mapper.Pending.ToList(), e, executionMessageSink);
            if (testCases.FirstOrDefault()?.TestCollection.TestAssembly is { } testAssembly)
            {
                executionMessageSink.OnMessage(new Xunit.v3.TestAssemblyFinished
                {
                    AssemblyUniqueID = testAssembly.UniqueID,
                    ExecutionTime = 0,
                    FinishTime = DateTimeOffset.UtcNow,
                    TestsFailed = mapper.TestsFailed + failed,
                    TestsNotRun = mapper.TestsNotRun,
                    TestsSkipped = mapper.TestsSkipped,
                    TestsTotal = mapper.TestsTotal + failed,
                });
            }
        }
    }

    // Reports the pending test cases as failed by the cause (continuing what the remote test process has reported of them) and returns
    // their number: test runners count the results only, so test cases without results are not run silently (e.g., dotnet test succeeds).
    private static int ReportFailed(IEnumerable<PendingTestCase> pending, Exception cause, IMessageSink executionMessageSink)
    {
        var (exceptionTypes, messages, stackTraces, exceptionParentIndices, failureCause) = ExceptionUtility.ExtractMetadata(cause);
        var now = DateTimeOffset.UtcNow;
        var count = 0;
        foreach (var (testCase, started, testsStarted, runningTest) in pending)
        {
            count++;
            var assemblyUniqueId = testCase.TestCollection.TestAssembly.UniqueID;
            var testUniqueId = runningTest ?? UniqueIDGenerator.ForTest(testCase.UniqueID, testsStarted);
            if (!started)
            {
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
            }

            if (runningTest is null)
            {
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
            }

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
        MessageMapper mapper,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions,
        CancellationTokenSource cancellation)
    {
        var options = GroundControlOptions.Load(assembly);
        using var docker = new DockerEngine(options);
        var project = ComposeProject.Find(docker, options, assembly.Location);
        var container = await project.GetTestContainerAsync(assembly.Location, cancellation.Token);

        List<string> arguments = ["-automated", "async", "-noLogo", "-noColor", .. Arguments(executionOptions)];

        // The debugger attaches to the remote test process, the tests wait for it.
        if (Debugger.IsAttached)
        {
            arguments.Add("-waitForDebugger");
        }

        // The local test cases are run as they are (see TestCaseSerialization).
        var remoteAssemblyPath = container.MapPath(assembly.Location);
        var serializations = testCases.Select(testCase => TestCaseSerialization.Serialize(testCase, remoteAssemblyPath)).ToList();

        var finished = false;
        var output = new Queue<string>();
        var errors = await ContainerProcess.RunAsync(
            docker,
            container,
            remoteAssemblyPath,
            arguments,
            TestCaseSerialization.FileVariable,
            serializations,
            processId =>
            {
                if (Debugger.IsAttached)
                {
                    _ = Debuggers.AttachAsync(new DebugTarget(container.Name, processId, project.Directory, assembly.Location), cancellation.Token);
                }
            },
            line =>
            {
                output.Enqueue(line);
                if (output.Count > 20)
                {
                    output.Dequeue();
                }

                if (mapper.Map(line) is not { } message)
                {
                    return;
                }

                // Requested test cases the remote test process has not run are reported before the test assembly finishes.
                if (message.Type == "test-assembly-finished")
                {
                    finished = true;
                    ReportFailed(
                        mapper.Pending.ToList(),
                        new InvalidOperationException($"The test case has not run in container {container.Name}."),
                        executionMessageSink);
                }

                if (MessageSinkMessageDeserializer.Deserialize(message.Json, null) is { } deserialized
                    && !executionMessageSink.OnMessage(deserialized))
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

    // The execution options of the local test run as the arguments of the remote one.
    internal static List<string> Arguments(ITestFrameworkExecutionOptions executionOptions)
    {
        var arguments = new List<string>();
        if (executionOptions.GetExplicitOption() is { } explicitOption)
        {
            arguments.AddRange(["-explicit", explicitOption.ToString().ToLowerInvariant()]);
        }

        if (executionOptions.GetDisableParallelization() == true)
        {
            arguments.AddRange(["-parallel", "none"]);
        }

        if (executionOptions.GetParallelAlgorithm() is { } parallelAlgorithm)
        {
            arguments.AddRange(["-parallelAlgorithm", parallelAlgorithm.ToString().ToLowerInvariant()]);
        }

        // 0 is the default, -1 is unlimited.
        if (executionOptions.GetMaxParallelThreads() is { } maxParallelThreads and not 0)
        {
            arguments.AddRange(["-maxThreads", maxParallelThreads < 0 ? "unlimited" : maxParallelThreads.ToString(CultureInfo.InvariantCulture)]);
        }

        if (executionOptions.GetCulture() is { } culture)
        {
            arguments.AddRange(["-culture", culture.Length == 0 ? "invariant" : culture]);
        }

        if (executionOptions.GetSeed() is { } seed)
        {
            arguments.AddRange(["-seed", seed.ToString(CultureInfo.InvariantCulture)]);
        }

        if (executionOptions.GetStopOnTestFail() == true)
        {
            arguments.Add("-stopOnFail");
        }

        if (executionOptions.GetShowLiveOutput() == true)
        {
            arguments.Add("-showLiveOutput");
        }

        return arguments;
    }
}
