namespace Arrow.Common;

/// <summary>One line of <c>ARROW_HOME/history.jsonl</c>, appended when a run ends (P09 §5.4).</summary>
/// <param name="RunId">The run.</param>
/// <param name="StartedAt">Start time (UTC).</param>
/// <param name="ProjectName">Folder name or <c>&lt;owner&gt;/&lt;repo&gt;</c>.</param>
/// <param name="Cuts">FQNs of the CUTs of the run.</param>
/// <param name="Valid">Number of CUTs that reached VALID.</param>
/// <param name="Written">Number of test files written into the project.</param>
/// <param name="Status">Final status of the run.</param>
public sealed record HistoryEntry(
    string RunId,
    DateTimeOffset StartedAt,
    string ProjectName,
    IReadOnlyList<string> Cuts,
    int Valid,
    int Written,
    RunStatus Status);
