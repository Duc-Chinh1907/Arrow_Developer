namespace Arrow.Common;

/// <summary>
/// One run of <c>arrow gen</c> or <c>arrow inspect</c>: the content of <c>runs/&lt;run_id&gt;/run.json</c> (P09 §5.3).
/// It is rewritten atomically whenever a target changes state, and never holds an API key or a full <c>base_url</c>.
/// </summary>
/// <param name="RunId"><c>&lt;yyyyMMdd-HHmmss&gt;-&lt;4 random [a-z0-9]&gt;</c> in local time (P09 §5.1).</param>
/// <param name="ArrowVersion">Version of ARROW that created the run.</param>
/// <param name="StartedAt">Start time (UTC).</param>
/// <param name="FinishedAt">End time (UTC); null while running.</param>
/// <param name="Status">Run status.</param>
/// <param name="Command">Command line arguments, e.g. <c>["gen", "OrderService", "--rounds", "5"]</c>.</param>
/// <param name="Project">The project of the run.</param>
/// <param name="Config">Settings that change results; a resumed run keeps these (P07 §5.2).</param>
/// <param name="PromptsSha256">sha256 of each prompt template used, keyed by its path under <c>Prompts/</c> (P02 §3.1).</param>
/// <param name="Tools">Versions of the external tools, e.g. <c>"jacoco": "0.8.12"</c>.</param>
/// <param name="Targets">The CUTs of the run, in the order the user gave them.</param>
/// <param name="Totals">Totals over all targets.</param>
public sealed record RunInfo(
    string RunId,
    string ArrowVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    RunStatus Status,
    IReadOnlyList<string> Command,
    RunProject Project,
    RunConfig Config,
    IReadOnlyDictionary<string, string> PromptsSha256,
    IReadOnlyDictionary<string, string> Tools,
    IReadOnlyList<RunTarget> Targets,
    RunTotals Totals);

/// <summary>Project of a run. Local projects have <c>Root</c>; GitHub projects have <c>Url</c> and <c>GitRef</c>.</summary>
/// <param name="Kind">Local folder or GitHub repository.</param>
/// <param name="PathName">Folder name, or <c>&lt;owner&gt;/&lt;repo&gt;</c> (the <c>project_id</c> of P00 §3).</param>
/// <param name="Root">Absolute project root with <c>/</c> separators; null for GitHub projects.</param>
/// <param name="Url">Repository URL without credentials; null for local projects.</param>
/// <param name="GitRef">Requested ref; null for local projects or the default branch.</param>
/// <param name="GitCommit">Commit that was analysed, when the project is a git working tree or a GitHub repository.</param>
/// <param name="Dirty">True when a local git working tree had uncommitted changes.</param>
/// <param name="SourceTreeHash">Hash of the copied working tree, used to check a resumed run (P07 §5.2); null for GitHub projects.</param>
public sealed record RunProject(
    ProjectSourceKind Kind,
    string PathName,
    string? Root,
    string? Url,
    string? GitRef,
    string? GitCommit,
    bool? Dirty,
    string? SourceTreeHash);

/// <summary>Settings that change results (P07 §5.2).</summary>
/// <param name="Model">Model name from the user config.</param>
/// <param name="ProviderHost">Host name of the provider endpoint only, never the full URL.</param>
/// <param name="Rounds">Repair budget N.</param>
/// <param name="EarlyStopP">P of the early stop rule; null when off.</param>
/// <param name="Repair">False with <c>--no-repair</c>.</param>
/// <param name="Quality">False with <c>--no-metrics</c> or <c>quality.enabled = false</c>.</param>
public sealed record RunConfig(
    string Model,
    string ProviderHost,
    int Rounds,
    int? EarlyStopP,
    bool Repair,
    bool Quality);

/// <summary>One CUT of a run and its progress.</summary>
public sealed record RunTarget(string CutFqn, TargetState State);

/// <summary>Totals over all targets of a run.</summary>
public sealed record RunTotals(int LlmCalls, long TokensIn, long TokensOut, double? CostUsd)
{
    public static RunTotals Zero { get; } = new(0, 0, 0, 0);
}
