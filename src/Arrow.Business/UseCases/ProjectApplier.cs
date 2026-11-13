using System.Security.Cryptography;
using Arrow.Business.Analysis;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.DataAccess.Apply;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.UseCases;

/// <summary><c>apply.json</c> (P06 §6).</summary>
public sealed record ApplyRecord(
    ApplyDecision Decision,
    string? Path,
    string TestFqn,
    int NameIndex,
    bool RenamedAfterRun,
    bool Reverified,
    string LineEndings,
    IReadOnlyList<string> Warnings,
    ErrorInfo? Error,
    DateTimeOffset At);

/// <summary>What the write step needs to know about the run.</summary>
/// <param name="Run">The run.</param>
/// <param name="ProjectRoot">The user's project; null for a repository.</param>
/// <param name="Yes"><c>--yes</c>.</param>
/// <param name="DryRun"><c>--dry-run</c>.</param>
/// <param name="ConfirmSetting"><c>apply.confirm</c>.</param>
public sealed record ApplyContext(RunContext Run, string? ProjectRoot, bool Yes, bool DryRun, bool ConfirmSetting);

/// <summary>
/// The write step of milestone M1 (P06 §3, §4.1, §5), one target at a time: VALID only, a local project, the CUT unchanged,
/// never overwrite. Renaming when the name appeared during the run, and one question for several classes, come with
/// <c>TestWritePolicy</c> (P06 §4.2–4.3).
/// </summary>
public sealed class ProjectApplier(IProjectFileWriter writer, IRunStore runs, ErrorCatalog catalog)
{
    /// <summary>Asks (unless told not to), writes, and records <c>apply.json</c>.</summary>
    public async Task<ApplySummary> ApplyAsync(RepositoryMetadata m, FinalTestSuite final, ApplyContext context, IConfirmation confirm, List<string> warnings, CancellationToken ct)
    {
        var path = final.Candidate.TestPath;
        var decision = ApplyDecision.Blocked;
        ErrorInfo? error = null;
        string? written = null;
        var eol = LineEndings.Lf;
        var tags = new List<string>();
        if (final.Success && (!m.Paths.NameMatchesTestIncludes || final.GeneratedTestRanInModule == false))
        {
            tags.Add("surefire_includes_mismatch");
            warnings.Add($"mvn test của project sẽ không tự chạy {Path.GetFileName(path)}. Hãy thêm mẫu tên vào cấu hình Surefire, hoặc chạy riêng bằng -Dtest={m.Paths.GeneratedTestFqn[(m.Paths.GeneratedTestFqn.LastIndexOf('.') + 1)..]}");
        }

        var root = context.ProjectRoot;
        if (!final.Success)
        {
            decision = ApplyDecision.NotValid;
        }
        else if (root is null)
        {
            decision = ApplyDecision.RemoteProject;
        }
        else if (context.DryRun)
        {
            decision = ApplyDecision.DryRun;
        }
        else
        {
            var cutFile = Path.Combine(root, m.Paths.CutPath.Replace('/', Path.DirectorySeparatorChar));
            var current = File.Exists(cutFile) ? Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(cutFile, ct).ConfigureAwait(false))) : null;
            eol = current is null ? LineEndings.Lf : ProjectFileWriter.EndingsOf(await File.ReadAllTextAsync(cutFile, ct).ConfigureAwait(false));
            bool write;
            if (current != m.Cut.SourceSha256)
            {
                error = catalog.Describe(ErrorCodes.WProjectChanged);
                write = !context.Yes && confirm.CanAsk
                        && await confirm.ConfirmAsync($"{m.Cut.SimpleName} đã thay đổi sau khi ARROW đọc nó; test được xác minh trên bản cũ. Vẫn ghi?", false, ct).ConfigureAwait(false);
            }
            else if (context.Yes || !context.ConfirmSetting)
            {
                write = true;
            }
            else if (!confirm.CanAsk)
            {
                write = false;
                decision = ApplyDecision.SkippedNonInteractive;
                warnings.Add("Bỏ qua ghi file: cần --yes khi chạy không tương tác");
            }
            else
            {
                write = await confirm.ConfirmAsync($"Ghi {path} ({final.Candidate.TestMethodCount} test)?", true, ct).ConfigureAwait(false);
                decision = write ? decision : ApplyDecision.Declined;
            }

            if (write)
            {
                try
                {
                    written = await writer.WriteNewAsync(root, path, final.Candidate.Code!, eol, ct).ConfigureAwait(false);
                    decision = ApplyDecision.Written;
                    error = null;
                    Report(context.Run, ArrowEventTypes.ApplyWritten, m.Cut.Fqn, $"Đã ghi {path}", ("path", path));
                }
                catch (ArrowException ex)
                {
                    decision = ApplyDecision.Blocked;
                    error = catalog.Describe(ex);
                }
            }
        }

        if (decision != ApplyDecision.Written)
        {
            Report(context.Run, ArrowEventTypes.ApplySkipped, m.Cut.Fqn, $"Không ghi file ({decision.ToString().ToLowerInvariant()})", ("decision", decision));
        }

        runs.WriteJson(context.Run.Run, $"{ProjectAnalyzer.TargetDir(m.Cut.Fqn)}/apply.json", new ApplyRecord(
            decision, decision is ApplyDecision.Written or ApplyDecision.DryRun or ApplyDecision.Declined or ApplyDecision.SkippedNonInteractive ? path : null,
            final.Candidate.TestFqn, m.Paths.NameIndex, false, false,
            eol == LineEndings.Crlf ? "crlf" : "lf", tags, error, DateTimeOffset.UtcNow));
        return new ApplySummary(decision, written is null ? (decision == ApplyDecision.DryRun ? path : null) : path);
    }

    private static void Report(RunContext run, string type, string cut, string message, params (string Key, object? Value)[] data) =>
        run.Progress.Report(new ArrowEvent(DateTimeOffset.UtcNow, type, cut, null, null, EventLevel.Info, message, data.ToDictionary(d => d.Key, d => d.Value)));
}
