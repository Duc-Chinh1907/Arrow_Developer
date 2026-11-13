using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arrow.Common.Verification;

/// <summary>Group of a failure state (P04 §4), for display only: never used to decide.</summary>
public enum ErrorGroup
{
    Build,
    Runtime,
    Assertion,
}

/// <summary>The <see cref="ErrorGroup"/> of each state.</summary>
public static class StateGroups
{
    /// <summary><c>BUILD</c> for COMPILE, UNKNOWN, DISCOVERY; <c>RUNTIME</c> for RUNTIME, REGRESSION; <c>ASSERTION</c>; null for VALID and INFRA.</summary>
    public static ErrorGroup? GroupOf(VerificationState? state) => state switch
    {
        VerificationState.Compile or VerificationState.Unknown or VerificationState.Discovery => ErrorGroup.Build,
        VerificationState.Runtime or VerificationState.Regression => ErrorGroup.Runtime,
        VerificationState.Assertion => ErrorGroup.Assertion,
        _ => null,
    };
}

/// <summary>A javac error of step 1 (P04 §3.3).</summary>
/// <param name="File">File as printed by the build, made relative to the project root when it was under the workspace copy.</param>
/// <param name="Line">Line, from 1.</param>
/// <param name="Column">Column; null in the Gradle format.</param>
/// <param name="Message">Message of javac, without the <c>error:</c> prefix of maven-compiler-plugin 2.x.</param>
/// <param name="Kind">One of the fixed javac kinds of P04 §4.1, <c>other</c> when none matches.</param>
/// <param name="SymbolKind">Kind of the <c>symbol:</c> line (<c>method</c>, <c>class</c>…); null when there is none.</param>
/// <param name="Symbol">The symbol without its kind, e.g. <c>getTotal(long)</c>; null when there is none.</param>
/// <param name="Location">The <c>location:</c> line, e.g. <c>class com.acme.Bounds</c>; null when there is none.</param>
/// <param name="InGeneratedFile">The file is one of the generated suite (compared by file name).</param>
public sealed record CompileError(
    string File,
    int Line,
    [property: JsonPropertyName("col")] int? Column,
    string Message,
    string Kind,
    string? SymbolKind = null,
    string? Symbol = null,
    string? Location = null,
    bool InGeneratedFile = false);

/// <summary>A failing test method of the generated class in step 2–4 (P04 §3.4, §3.8).</summary>
/// <param name="Method">Method name; empty for a failure of the whole class.</param>
/// <param name="Type">Exception class name (P04 §3.4 step 2).</param>
/// <param name="Message">Message of the failure.</param>
/// <param name="TraceHead">First 8 lines of the stack trace.</param>
/// <param name="Frame"><c>Class.method</c> of the first project frame (P04 §4.1); null when the trace has no frame.</param>
public sealed record FailureInfo(string Method, string Type, string? Message = null, string? TraceHead = null, string? Frame = null);

/// <summary>Why a test counts as a regression (P04 §3.5).</summary>
public enum RegressionKind
{
    /// <summary>(a) A test passing in every baseline run fails.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>(b) A test passing in every baseline run is missing from the report.</summary>
    [JsonStringEnumMemberName("missing")]
    Missing,

    /// <summary>(c) The generated test passes alone but fails with the module.</summary>
    [JsonStringEnumMemberName("generated_failed_in_module")]
    GeneratedFailedInModule,
}

/// <summary>One entry of <c>regressions[]</c> (P04 §3.8).</summary>
/// <param name="Test"><c>&lt;class&gt;#&lt;method&gt;</c>.</param>
/// <param name="Kind">(a), (b) or (c).</param>
/// <param name="Type">Exception class; null for a missing test.</param>
/// <param name="Message">Message of the failure; null for a missing test.</param>
public sealed record RegressionInfo(string Test, RegressionKind Kind, string? Type = null, string? Message = null);

/// <summary>Test methods of the generated class in the run of step 2 (P04 §3.8).</summary>
public sealed record TestMethodCounts(int Total, int Passed, int Failed)
{
    /// <summary>No test method ran.</summary>
    public static TestMethodCounts None { get; } = new(0, 0, 0);
}

/// <summary>Names of the steps in <c>verify.json</c>.</summary>
public static class VerifySteps
{
    public const string Compile = "compile";
    public const string RunTarget = "run_target";
    public const string ModuleTests = "module_tests";
}

/// <summary>One step of a verification (P04 §3.8).</summary>
/// <param name="Step">One of <see cref="VerifySteps"/>.</param>
/// <param name="Ok">The step passed.</param>
/// <param name="Seconds">Time of the last attempt.</param>
/// <param name="Attempts">Attempts, more than 1 after an infrastructure error.</param>
/// <param name="Log">Log of the last attempt, relative to the round folder.</param>
public sealed record VerifyStepRecord(string Step, bool Ok, double Seconds, int Attempts, string? Log);

/// <summary><c>verify.json</c> (P04 §3.8): the result of one verification of a candidate.</summary>
public sealed record VerifyResult
{
    /// <summary>The state; null for <c>INFRA</c>, written as <c>"INFRA"</c>.</summary>
    [JsonConverter(typeof(VerifyStateJsonConverter))]
    public VerificationState? State { get; init; }

    /// <summary>Display group of the state.</summary>
    public ErrorGroup? Group => StateGroups.GroupOf(State);

    /// <summary>Error code of an <c>INFRA</c> result (<c>B3_TIMEOUT</c>…); null otherwise.</summary>
    public string? InfraReason { get; init; }

    /// <summary>The steps that ran.</summary>
    public IReadOnlyList<VerifyStepRecord> Steps { get; init; } = [];

    /// <summary>Test methods of the generated class in step 2.</summary>
    public TestMethodCounts TestMethods { get; init; } = TestMethodCounts.None;

    /// <summary>Failing methods of the generated class in step 2–4.</summary>
    public IReadOnlyList<FailureInfo> Failures { get; init; } = [];

    /// <summary>Javac errors of step 1.</summary>
    public IReadOnlyList<CompileError> CompileErrors { get; init; } = [];

    /// <summary>Regressions of step 5.</summary>
    public IReadOnlyList<RegressionInfo> Regressions { get; init; } = [];

    /// <summary>Whether the generated test ran in step 5; null when step 5 did not run (P04 §3.5).</summary>
    public bool? GeneratedTestRanInModule { get; init; }

    /// <summary>The build error block for UNKNOWN (and a crash in step 2), without workspace paths.</summary>
    public string? BuildErrorExcerpt { get; init; }

    /// <summary>Path of the generated test file from the project root.</summary>
    public string? TestFile { get; init; }

    /// <summary>Normalized signature of the first error (P04 §4.1); null for VALID and INFRA.</summary>
    public string? ErrorSignature { get; init; }

    /// <summary>Methods holding the errors (P04 §4).</summary>
    public IReadOnlyList<string> FailingMethods { get; init; } = [];

    /// <summary>Code hash of the candidate.</summary>
    public string? CodeHash { get; init; }

    /// <summary>Methods of the generated class that passed in step 2 (P04 §3.4 step 7).</summary>
    public int TestsPassed { get; init; }

    /// <summary>Assertions of the candidate (P02 §5.5).</summary>
    public int AssertionCount { get; init; }

    /// <summary>Copies and build commands of this verification, in seconds.</summary>
    public double BuildSeconds { get; init; }

    /// <summary>True for an <c>INFRA</c> result.</summary>
    [JsonIgnore]
    public bool IsInfra => State is null;
}

/// <summary>Writes a null <see cref="VerificationState"/> as <c>"INFRA"</c> and reads it back.</summary>
public sealed class VerifyStateJsonConverter : JsonConverter<VerificationState?>
{
    /// <summary>The JSON text of the infrastructure state.</summary>
    public const string Infra = "INFRA";

    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override VerificationState? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
        if (text is null or Infra)
        {
            return null;
        }

        return Enum.TryParse<VerificationState>(text, ignoreCase: true, out var state)
            ? state
            : throw new JsonException($"Unknown verification state '{text}'.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, VerificationState? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value is { } state ? state.ToString().ToUpperInvariant() : Infra);
}

/// <summary>What the repair needs to know about a failed verification (P04 §4).</summary>
/// <param name="State">The state, never VALID or INFRA in practice.</param>
/// <param name="Group">Display group.</param>
/// <param name="ErrorSignature">Normalized signature (P04 §4.1).</param>
/// <param name="CodeHash">Hash of the candidate.</param>
/// <param name="FailingMethods">Methods to rewrite at level 3; empty except for COMPILE, RUNTIME and ASSERTION.</param>
/// <param name="ErrorBlock">At most 60 lines for the prompt (P04 §4.2).</param>
/// <param name="FirstError">At most 200 characters for the list of failed attempts (P04 §4.3).</param>
public sealed record Diagnosis(
    VerificationState State,
    ErrorGroup? Group,
    string? ErrorSignature,
    string? CodeHash,
    IReadOnlyList<string> FailingMethods,
    string ErrorBlock,
    string? FirstError);
