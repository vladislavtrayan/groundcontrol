using System.Text;
using Xunit.Sdk;
using Xunit.v3;

namespace GroundControl;

/// <summary>
/// The local test cases run by the remote test process as they are: their serializations keep the unique IDs of the local test cases
/// (and their theory data rows), so the remote test process reports them as of the local test cases. Serializations contain
/// the path of the test assembly, which is replaced by its path within the tests container.
/// </summary>
internal static class TestCaseSerialization
{
    /// <summary>The environment variable of the file of the serializations of the test cases (a line each) within the tests container.</summary>
    internal const string FileVariable = "GROUNDCONTROL_TEST_CASES";

    private const string ObjectType = "-3";
    private const string AssemblyPathKey = "ap";
    private static readonly string TestAssemblyType = typeof(XunitTestAssembly).FullName + ",";

    public static string Serialize(ITestCase testCase, string remoteAssemblyPath) =>
        SerializationHelper.Instance.Serialize(testCase) is { } serialization
            ? ReplaceAssemblyPath(serialization, remoteAssemblyPath)
            : throw new InvalidOperationException($"Test case {testCase.TestCaseDisplayName} ({testCase.GetType()}) is not serializable.");

    public static IReadOnlyCollection<ITestCase> Load(string path) =>
        File.ReadLines(path)
            .Where(line => line.Length > 0)
            .Select(line => SerializationHelper.Instance.Deserialize<ITestCase>(line)
                ?? throw new InvalidOperationException($"Test case is not deserialized: {line}"))
            .ToList();

    /// <summary>Replaces the path of the test assembly in the serialization of a test case.</summary>
    /// <remarks>
    /// A serialized object is <c>-3:&lt;type name&gt;:&lt;members&gt;</c> (Base64-encoded), its members are lines
    /// <c>&lt;key&gt;:&lt;type&gt;:&lt;value&gt;</c>, where values of objects are <c>&lt;type name&gt;:&lt;members&gt;</c>;
    /// the assembly path is the <c>ap</c> member of the test assembly.
    /// </remarks>
    internal static string ReplaceAssemblyPath(string serialization, string assemblyPath)
    {
        var parts = serialization.Split(':', 3);
        return parts is [ObjectType, var type, var members]
            ? $"{ObjectType}:{ReplaceAssemblyPath(type, members, assemblyPath)}"
            : serialization;
    }

    private static string ReplaceAssemblyPath(string type, string members, string assemblyPath)
    {
        var isTestAssembly = Decode(type).StartsWith(TestAssemblyType, StringComparison.Ordinal);
        var lines = Decode(members).Split('\n').Select(line =>
        {
            var parts = line.Split(':', 3);
            return parts switch
            {
                [AssemblyPathKey, var valueType, _] when isTestAssembly => $"{AssemblyPathKey}:{valueType}:{Encode(assemblyPath)}",
                [var key, ObjectType, var value] when value.Split(':', 2) is [var memberType, var memberMembers] =>
                    $"{key}:{ObjectType}:{ReplaceAssemblyPath(memberType, memberMembers, assemblyPath)}",
                _ => line,
            };
        });
        return $"{type}:{Encode(string.Join('\n', lines))}";
    }

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
