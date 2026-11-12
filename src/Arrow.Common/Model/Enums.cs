using System.Text.Json.Serialization;

namespace Arrow.Common;

/// <summary>Where the project comes from (P01 §2.1). JSON: <c>local</c> or <c>git</c>.</summary>
public enum ProjectSourceKind
{
    [JsonStringEnumMemberName("local")]
    LocalDirectory,

    [JsonStringEnumMemberName("git")]
    GitRepository,
}

/// <summary>
/// Verification states in increasing order of quality (P00 §3, R03 §6.3):
/// <c>COMPILE &lt; UNKNOWN &lt; DISCOVERY &lt; RUNTIME &lt; ASSERTION &lt; REGRESSION &lt; VALID</c>.
/// The numeric values keep that order, so states can be compared with <c>&lt;</c>.
/// </summary>
public enum VerificationState
{
    Compile = 0,
    Unknown = 1,
    Discovery = 2,
    Runtime = 3,
    Assertion = 4,
    Regression = 5,
    Valid = 6,
}

/// <summary>Final result of one CUT, the <c>result</c> field of <c>result.json</c> (P07 §6).</summary>
public enum TargetOutcome
{
    Valid,
    Compile,
    Unknown,
    Discovery,
    Runtime,
    Assertion,
    Regression,
    InvalidOutput,
    Infra,
    Blocked,
    Cancelled,
}

/// <summary>Status of a run in <c>run.json</c> (P09 §5.3).</summary>
public enum RunStatus
{
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>Progress of one target in <c>run.json</c> (P09 §5.3). The result itself is in <c>result.json</c>.</summary>
public enum TargetState
{
    Pending,
    Running,
    Done,
}

/// <summary>Why the repair chain stopped (P04 §6.2).</summary>
public enum StopReason
{
    [JsonStringEnumMemberName("valid")]
    Valid,

    [JsonStringEnumMemberName("budget")]
    Budget,

    [JsonStringEnumMemberName("infra")]
    Infra,

    [JsonStringEnumMemberName("stagnation")]
    Stagnation,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    [JsonStringEnumMemberName("stopped")]
    Stopped,
}

/// <summary>What happened when writing the test into the project (P06 §6).</summary>
public enum ApplyDecision
{
    [JsonStringEnumMemberName("written")]
    Written,

    [JsonStringEnumMemberName("declined")]
    Declined,

    [JsonStringEnumMemberName("not_valid")]
    NotValid,

    [JsonStringEnumMemberName("remote_project")]
    RemoteProject,

    [JsonStringEnumMemberName("dry_run")]
    DryRun,

    [JsonStringEnumMemberName("skipped_non_interactive")]
    SkippedNonInteractive,

    [JsonStringEnumMemberName("blocked")]
    Blocked,
}

/// <summary>Result of one quality tool (P05 §7).</summary>
public enum ToolStatus
{
    Ok,
    Failed,
    Skipped,
}

/// <summary>Kind of a top-level Java type (P01 §3.3, P10 <c>list_classes</c>).</summary>
public enum CutKind
{
    [JsonStringEnumMemberName("class")]
    Class,

    [JsonStringEnumMemberName("interface")]
    Interface,

    [JsonStringEnumMemberName("enum")]
    Enum,

    [JsonStringEnumMemberName("record")]
    Record,

    [JsonStringEnumMemberName("annotation")]
    Annotation,
}

/// <summary>Severity of an <see cref="Events.ArrowEvent"/> (P07 §10).</summary>
public enum EventLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>Conversions between the state and outcome enums.</summary>
public static class OutcomeExtensions
{
    /// <summary>The <c>result</c> of a CUT whose final suite ended in <paramref name="state"/>.</summary>
    public static TargetOutcome ToOutcome(this VerificationState state) => state switch
    {
        VerificationState.Compile => TargetOutcome.Compile,
        VerificationState.Unknown => TargetOutcome.Unknown,
        VerificationState.Discovery => TargetOutcome.Discovery,
        VerificationState.Runtime => TargetOutcome.Runtime,
        VerificationState.Assertion => TargetOutcome.Assertion,
        VerificationState.Regression => TargetOutcome.Regression,
        VerificationState.Valid => TargetOutcome.Valid,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}
