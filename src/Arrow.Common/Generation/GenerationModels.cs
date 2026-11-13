using System.Text.Json.Serialization;
using Arrow.Common.Llm;

namespace Arrow.Common.Generation;

/// <summary>An approach that already failed, shown to the model so that it does not repeat it (P02 §2.2, §3.4).</summary>
/// <param name="Round">Round of the attempt.</param>
/// <param name="Signature">Failure signature, e.g. <c>COMPILE | cannot find symbol | method</c>, or <c>duplicate code (same as round 2)</c>.</param>
/// <param name="FirstError">First error of the build or the test run; empty when there is none.</param>
/// <param name="Change">The changed lines of the attempt (a line diff); empty when there is none.</param>
public sealed record FailedAttempt(int Round, string Signature, string? FirstError, string? Change);

/// <summary>What block 3 asks block 2 to repair (P02 §2.2). Block 2 only builds the prompt from it; levels and contents are P04's.</summary>
/// <param name="CutFqn">The CUT.</param>
/// <param name="Round">Repair round, from 1.</param>
/// <param name="Level">Level of the prompt actually used: 1, 2 or 3.</param>
/// <param name="RequestedLevel">Level the policy asked for; 3 may become 2 when no failing method is known.</param>
/// <param name="State">Verification state of the checkpoint.</param>
/// <param name="BaseCode">Code of the checkpoint to repair.</param>
/// <param name="BaseRound">Round that made the checkpoint; 0 for c0.</param>
/// <param name="ErrorBlock">Failure output.</param>
/// <param name="ExtraContext">Signatures and sources of classes named in the error (level 2 and 3).</param>
/// <param name="FailedAttempts">Attempts so far, by round.</param>
/// <param name="RewriteMethods">Test methods to rewrite (level 3).</param>
public sealed record RepairRequest(
    string CutFqn,
    int Round,
    int Level,
    int RequestedLevel,
    VerificationState State,
    string BaseCode,
    int BaseRound,
    string ErrorBlock,
    string? ExtraContext,
    IReadOnlyList<FailedAttempt> FailedAttempts,
    IReadOnlyList<string> RewriteMethods);

/// <summary>Why an answer could not be used (P02 §5.2). Only these three exist.</summary>
public static class InvalidReasons
{
    /// <summary>The answer has no Java type declaration.</summary>
    public const string NoCode = "no_code";

    /// <summary>The code does not parse.</summary>
    public const string ParseError = "parse_error";

    /// <summary>The code declares more than one top-level type.</summary>
    public const string MultipleClasses = "multiple_classes";
}

/// <summary>The candidate test T made from an answer (P02 §6).</summary>
/// <param name="ValidOutput">The answer gave usable code; false means <c>INVALID_OUTPUT</c>.</param>
/// <param name="InvalidReason">One of <see cref="InvalidReasons"/>; null when valid.</param>
/// <param name="ParseError">Error of the parser, for <c>parse_error</c>.</param>
/// <param name="Code">The normalized code; null when invalid. Not written to <c>candidate.json</c>: it is in <c>candidate.java</c>.</param>
/// <param name="TestFqn">FQN the test class has after normalization.</param>
/// <param name="TestPath">Where the test goes, from the project root.</param>
/// <param name="CodeHash"><c>sha256:…</c> of the code without comments and extra white space, after splicing.</param>
/// <param name="TestMethodCount">Test methods in the code.</param>
/// <param name="AssertionCount">Assertions in the code.</param>
/// <param name="Normalization">What was changed: <c>fence removed</c>, <c>prose trimmed</c>, <c>package: added</c>…</param>
/// <param name="DroppedMethods">Methods of the answer that level 3 did not take.</param>
/// <param name="FinishReason">Finish reason of the model call.</param>
public sealed record CandidateTest(
    bool ValidOutput,
    string? InvalidReason,
    string? ParseError,
    [property: JsonIgnore] string? Code,
    string TestFqn,
    string TestPath,
    string? CodeHash,
    int TestMethodCount,
    int AssertionCount,
    IReadOnlyList<string> Normalization,
    IReadOnlyList<string> DroppedMethods,
    string? FinishReason);

/// <summary>A template that went into a prompt (P02 §3.1): its id, version and the sha256 of the file.</summary>
public sealed record TemplateRef(string Id, string Version, string Sha256);

/// <summary><c>prompt.json</c> (P02 §6), written before the model is called.</summary>
/// <param name="CallId">The call.</param>
/// <param name="Messages">The system and user messages as sent.</param>
/// <param name="Params">The parameters of the call.</param>
/// <param name="Templates">The templates used.</param>
/// <param name="SourceTruncation"><c>none</c>, <c>comments_removed</c> or <c>signatures_only</c> (P02 §3.3).</param>
public sealed record PromptRecord(
    string CallId,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement> Params,
    IReadOnlyList<TemplateRef> Templates,
    string SourceTruncation);
