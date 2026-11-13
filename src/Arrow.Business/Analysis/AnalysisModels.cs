using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Config;
using Arrow.Common.Events;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Analysis;

/// <summary>What block 1 needs from the run (P01 §2.3).</summary>
/// <param name="RunId">Run id.</param>
/// <param name="HomeRoot"><c>ARROW_HOME</c>; paths in <c>workspace.json</c> are relative to it.</param>
/// <param name="WorkDir"><c>ARROW_HOME/work/&lt;run_id&gt;</c>.</param>
/// <param name="Run">Results folder <c>runs/&lt;run_id&gt;</c>.</param>
/// <param name="Progress">Progress events (P07 §10); must not block.</param>
public sealed record RunContext(string RunId, string HomeRoot, string WorkDir, RunHandle Run, IProgress<ArrowEvent> Progress);

/// <summary>Settings of block 1, from the configuration (P01 §2.2) and what was found on the machine.</summary>
/// <param name="JdkHomes">JDK homes by major.</param>
/// <param name="Maven">Absolute path of the machine's Maven; null when there is none.</param>
/// <param name="Gradle">Absolute path of the machine's Gradle; null when there is none.</param>
/// <param name="BaselineRuns"><c>verification.baseline_runs</c>.</param>
/// <param name="StepTimeout"><c>verification.step_timeout_seconds</c>.</param>
/// <param name="InfraAttempts"><c>verification.infra_attempts</c>.</param>
/// <param name="InfraWait"><c>verification.infra_wait_seconds</c>.</param>
public sealed record AnalysisOptions(
    IReadOnlyDictionary<int, string> JdkHomes,
    string? Maven,
    string? Gradle,
    int BaselineRuns,
    TimeSpan StepTimeout,
    int InfraAttempts,
    TimeSpan InfraWait)
{
    /// <summary>Options from the configuration and the tools found on the machine.</summary>
    public static AnalysisOptions From(VerificationSettings verification, IReadOnlyDictionary<int, string> jdkHomes, string? maven, string? gradle) =>
        new(jdkHomes, maven, gradle, verification.BaselineRuns, TimeSpan.FromSeconds(verification.StepTimeoutSeconds),
            verification.InfraAttempts, TimeSpan.FromSeconds(verification.InfraWaitSeconds));
}

/// <summary>Seconds spent in each step of block 1, shown by <c>arrow inspect</c> (P08 §2.3).</summary>
/// <param name="Checkout">Getting the code (shared by the run).</param>
/// <param name="Locate">Listing the classes (shared by the run).</param>
/// <param name="Plan">Build tool version, effective POM or Gradle probe.</param>
/// <param name="Compile">Compiling with the JDKs tried.</param>
/// <param name="Baseline">Each baseline run, copy included.</param>
/// <param name="Total">Whole block for this CUT.</param>
public sealed record StepTimings(double Checkout, double Locate, double Plan, double Compile, IReadOnlyList<double> Baseline, double Total);

/// <summary>The code of the run and its classes, made once per run (P07 §3 step 3).</summary>
/// <param name="Checkout">The working copy <c>work/&lt;run_id&gt;/base</c>.</param>
/// <param name="Layout">Build files of the copy.</param>
/// <param name="Classes">Top-level classes of every main source root.</param>
/// <param name="TestRoots">Standard test roots (<c>…/src/test/java</c>) that exist, relative to the project root.</param>
/// <param name="Warnings">Warnings of the checkout and the class listing.</param>
/// <param name="CheckoutSeconds">Time to get the code.</param>
/// <param name="LocateSeconds">Time to list the classes.</param>
public sealed record ProjectWorkspace(
    SourceCheckout Checkout,
    BuildLayout Layout,
    IReadOnlyList<ClassCandidate> Classes,
    IReadOnlyList<string> TestRoots,
    IReadOnlyList<string> Warnings,
    double CheckoutSeconds,
    double LocateSeconds);

/// <summary>Result of block 1 for one CUT (P01 §8).</summary>
/// <param name="Outcome">OK, BLOCKED or INFRA; never throws for a broken project.</param>
/// <param name="Metadata">M; null when the module could not be analysed.</param>
/// <param name="Baseline">Baseline; null unless OK.</param>
/// <param name="Workspace">How to build the module; null unless OK.</param>
/// <param name="Cut">The CUT and its test path; null when the CUT was not found.</param>
/// <param name="Warnings"><c>[!]</c> warnings for the CLI.</param>
/// <param name="Timings">Seconds per step.</param>
/// <param name="SharedFrom">FQN of the CUT whose module results this CUT reuses (P01 §7); null for the first CUT of a module.</param>
public sealed record ProjectAnalysis(
    BlockOutcome Outcome,
    RepositoryMetadata? Metadata,
    BaselineInfo? Baseline,
    WorkspaceInfo? Workspace,
    CutInfo? Cut,
    IReadOnlyList<string> Warnings,
    StepTimings Timings,
    string? SharedFrom);

/// <summary>Block 1 (P01 §10).</summary>
public interface IProjectAnalyzer
{
    /// <summary>Gets the code once for the run and lists its classes (P01 §3, §3.3 steps 1–2). Known failures raise <c>ArrowException</c>.</summary>
    Task<ProjectWorkspace> PrepareAsync(ProjectTarget target, RunContext run, Func<long, Task<bool>>? confirmLargeCopy, CancellationToken ct);

    /// <summary>Matches a CUT query against the classes of the run (P01 §3.3 steps 3–5).</summary>
    Task<LocateResult> LocateAsync(ProjectWorkspace workspace, string query, CancellationToken ct);

    /// <summary>Analyses the module of <paramref name="cut"/>, sharing module results within the run (P01 §4–§8). Throws only <see cref="OperationCanceledException"/>.</summary>
    Task<ProjectAnalysis> AnalyzeAsync(ProjectWorkspace workspace, ClassCandidate cut, RunContext run, CancellationToken ct);

    /// <summary>
    /// Deletes the working copies of the run: the git worktree (when the code came from a repository) and <c>work/&lt;run_id&gt;</c>
    /// (P09 §8). Does nothing when <paramref name="keep"/> is true (<c>run.keep_workspaces</c>).
    /// </summary>
    Task CleanupAsync(ProjectWorkspace workspace, RunContext run, bool keep, CancellationToken ct);
}
