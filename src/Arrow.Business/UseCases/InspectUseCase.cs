using Arrow.Business.Analysis;
using Arrow.Business.Config;
using Arrow.Business.Environment;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;
using Arrow.DataAccess.Tools;

namespace Arrow.Business.UseCases;

/// <summary>Result of <c>arrow inspect</c> (P07 §11).</summary>
/// <param name="RunId">The run that holds <c>runs/&lt;run_id&gt;/targets/&lt;cut&gt;/block1/</c>.</param>
/// <param name="RunDir">Absolute folder of that run.</param>
/// <param name="Outcome">OK, BLOCKED or INFRA. A class that cannot be used is BLOCKED, not an exception.</param>
/// <param name="Analysis">Block 1 result; null when the class was not found or not testable.</param>
/// <param name="Suggestions">Names close to the one typed, for <c>B1_CUT_MISSING</c>.</param>
/// <param name="Warnings">Warnings to print.</param>
public sealed record InspectResult(
    string RunId,
    string RunDir,
    BlockOutcome Outcome,
    ProjectAnalysis? Analysis,
    IReadOnlyList<string> Suggestions,
    IReadOnlyList<string> Warnings);

/// <summary><c>arrow inspect</c>: runs block 1 for one CUT and reports it (P07 §11).</summary>
public interface IInspectUseCase
{
    /// <summary>
    /// Checks the environment, gets the code, finds the class and runs block 1. Environment and project problems raise
    /// <see cref="ArrowException"/> (exit 2); a class that is missing or cannot be tested gives a BLOCKED result.
    /// </summary>
    Task<InspectResult> RunAsync(ProjectTarget target, IReadOnlyList<string> command, string? projectRoot, IProgress<ArrowEvent>? progress, IConfirmation confirm, CancellationToken ct);
}

/// <summary>
/// <see cref="IInspectUseCase"/>. Steps of P07 §2 and §3, as far as inspect needs them: configuration, tools, ARROW_HOME, java-helper
/// (no model, no prompt templates); then a run with <c>command = inspect</c>. It never calls an LLM and writes nothing into the project.
/// </summary>
public sealed class InspectUseCase(
    IConfigStore config,
    ArrowHome home,
    ToolDiscovery tools,
    IRunStore runs,
    IAnalyzerHostFactory hosts,
    string arrowVersion,
    Func<string, long?>? freeBytes = null) : IInspectUseCase
{
    /// <summary>Free space ARROW_HOME needs (P07 §2 step 5).</summary>
    public const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;

    private static readonly Dictionary<string, string?> NoOverrides = [];

    public async Task<InspectResult> RunAsync(ProjectTarget target, IReadOnlyList<string> command, string? projectRoot, IProgress<ArrowEvent>? progress, IConfirmation confirm, CancellationToken ct)
    {
        // Steps 1, 2, 4, 5, 6 of P07 §2: nothing is created until they pass.
        var remote = target.Kind == ProjectSourceKind.GitRepository ? GitRemote.Parse(target.Location) : null;
        var effective = config.Load(projectRoot, NoOverrides);
        ConfigValidator.ThrowIfInvalid(effective);
        var settings = effective.Config;
        var catalog = ErrorCatalog.For(settings.Ui.Language);

        var machine = await tools.FindAsync(settings.Java, ct).ConfigureAwait(false);
        if (remote is not null && machine.Git is null)
        {
            throw new ArrowException(ErrorCodes.B1CloneFailed, "Máy không có git, nên không tải được repo. Cài git hoặc đặt java.git_path.");
        }

        var buildJdks = machine.BuildJdkHomes;
        if (buildJdks.Count == 0)
        {
            throw new ArrowException(ErrorCodes.B1JdkMissing, args: new Dictionary<string, string> { ["jdk"] = "17" });
        }

        var java = JavaHelperJar.FindJava(machine.HelperJdkHomes);
        if (home.Validate() is { } invalidHome)
        {
            throw invalidHome;
        }

        home.EnsureCreated();
        if ((freeBytes ?? DiskSpace.FreeBytes)(home.Root) is { } free && free < MinimumFreeBytes)
        {
            throw new ArrowException(ErrorCodes.CfgDiskFull);
        }

        var options = AnalysisOptions.From(settings.Verification, buildJdks, machine.Maven, machine.Gradle);
        await using var host = await hosts.StartAsync(new AnalyzerSetup(java, machine.Git, options, catalog), ct).ConfigureAwait(false);

        var id = runs.NewUniqueRunId(DateTime.Now);
        var started = DateTimeOffset.UtcNow;
        var project = new RunProject(
            target.Kind,
            remote?.RepoPath ?? new DirectoryInfo(target.Location).Name,
            remote is null ? target.Location.Replace('\\', '/') : null,
            remote?.Url,
            target.GitRef,
            null,
            null,
            null);
        var info = new RunInfo(id, arrowVersion, started, null, RunStatus.Running, command, project, null,
            new Dictionary<string, string>(), new Dictionary<string, string>(), [], RunTotals.Zero);
        var handle = runs.Create(info);
        var work = Path.Combine(home.WorkDir, id);
        Directory.CreateDirectory(work);
        var run = new RunContext(id, home.Root, work, handle, new RunProgress(runs, handle, progress));
        run.Progress.Report(new ArrowEvent(DateTimeOffset.UtcNow, ArrowEventTypes.RunStarted, null, null, null, EventLevel.Info, "arrow " + string.Join(' ', command), new Dictionary<string, object?>()));

        ProjectWorkspace? workspace = null;
        try
        {
            workspace = await host.Analyzer.PrepareAsync(target, run, size => ConfirmLargeAsync(confirm, size, ct), ct).ConfigureAwait(false);
            info = info with
            {
                Project = project with
                {
                    Root = workspace.Checkout.ProjectRoot?.Replace('\\', '/') ?? project.Root,
                    GitCommit = workspace.Checkout.GitCommit,
                    Dirty = workspace.Checkout.Dirty,
                    SourceTreeHash = workspace.Checkout.SourceTreeHash,
                },
            };
            runs.Update(handle, info);

            var result = await InspectAsync(host.Analyzer, workspace, target.CutQuery, run, catalog, confirm, ct).ConfigureAwait(false);
            var targets = result.Analysis?.Cut is { } cut ? [new RunTarget(cut.Fqn, TargetState.Done)] : new List<RunTarget>();
            runs.Update(handle, info with { Status = RunStatus.Completed, FinishedAt = DateTimeOffset.UtcNow, Targets = targets });
            return result with { RunId = id, RunDir = handle.Root };
        }
        catch (OperationCanceledException)
        {
            runs.Update(handle, info with { Status = RunStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow });
            throw;
        }
        catch (Exception)
        {
            runs.Update(handle, info with { Status = RunStatus.Failed, FinishedAt = DateTimeOffset.UtcNow });
            throw;
        }
        finally
        {
            if (workspace is not null)
            {
                await host.Analyzer.CleanupAsync(workspace, run, settings.Run.KeepWorkspaces, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                WorkspaceManager.DeleteTree(work);
            }
        }
    }

    private async Task<InspectResult> InspectAsync(IProjectAnalyzer analyzer, ProjectWorkspace workspace, string query, RunContext run, ErrorCatalog catalog, IConfirmation confirm, CancellationToken ct)
    {
        var located = await analyzer.LocateAsync(workspace, query, ct).ConfigureAwait(false);
        ClassCandidate? cut = null;
        string? warning = null;
        switch (located)
        {
            case LocateResult.Found found:
                (cut, warning) = (found.Class, found.Warning);
                break;
            case LocateResult.Ambiguous ambiguous:
                var chosen = await confirm.ChooseOneAsync(
                    $"Có {ambiguous.Candidates.Count} class tên {query}. Chọn một class:",
                    ambiguous.Candidates.Select(c => $"{c.Fqn}  ({c.Path})").ToList(),
                    ct).ConfigureAwait(false);
                if (chosen is not { } index)
                {
                    return Blocked(run, catalog, ErrorCodes.B1CutAmbiguous, "Có nhiều class tên " + query + ": " + string.Join(", ", ambiguous.Candidates.Select(c => c.Fqn)), [], workspace.Warnings,
                        new Dictionary<string, string> { ["query"] = query });
                }

                var picked = ClassLocator.Check(ambiguous.Candidates[index]);
                if (picked is LocateResult.Failed pickedFailure)
                {
                    return Blocked(run, catalog, pickedFailure.Code, pickedFailure.Detail, pickedFailure.Suggestions, workspace.Warnings,
                        new Dictionary<string, string> { ["query"] = query });
                }

                var pickedFound = (LocateResult.Found)picked;
                (cut, warning) = (pickedFound.Class, pickedFound.Warning);
                break;
            case LocateResult.Failed failed:
                return Blocked(run, catalog, failed.Code, failed.Detail, failed.Suggestions, workspace.Warnings,
                    new Dictionary<string, string> { ["query"] = query });
        }

        var analysis = await analyzer.AnalyzeAsync(workspace, cut!, run, ct).ConfigureAwait(false);
        var warnings = warning is null ? analysis.Warnings : [warning, .. analysis.Warnings];
        return new InspectResult(run.RunId, run.Run.Root, analysis.Outcome, analysis, [], warnings);
    }

    private static InspectResult Blocked(RunContext run, ErrorCatalog catalog, string code, string detail, IReadOnlyList<string> suggestions, IReadOnlyList<string> warnings, IReadOnlyDictionary<string, string> args)
    {
        var info = catalog.Describe(code, detail, args);
        var outcome = new BlockOutcome(BlockOutcomeKind.Blocked, code, info.Message, info.Hint, null, null, 0);
        return new InspectResult(run.RunId, run.Run.Root, outcome, null, suggestions, warnings);
    }

    private static async Task<bool> ConfirmLargeAsync(IConfirmation confirm, long bytes, CancellationToken ct) =>
        await confirm.ConfirmAsync(
            $"Project nặng {bytes / (1024 * 1024 * 1024.0):0.0} GB sau khi lọc. Vẫn chép? (không tương tác thì vẫn chép)", defaultAnswer: true, ct).ConfigureAwait(false);

    /// <summary>Reports an event to the caller and appends it to <c>events.jsonl</c> (P07 §10).</summary>
    private sealed class RunProgress(IRunStore runs, RunHandle run, IProgress<ArrowEvent>? inner) : IProgress<ArrowEvent>
    {
        private readonly object _gate = new();

        public void Report(ArrowEvent value)
        {
            lock (_gate)
            {
                runs.AppendEvent(run, value);
            }

            inner?.Report(value);
        }
    }
}
