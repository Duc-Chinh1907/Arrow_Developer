using Arrow.Business.Analysis;
using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Errors;

namespace Arrow.Business.Apply;

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
/// <param name="StepTimeout">Timeout of a verification step, for the verification after renaming.</param>
/// <param name="InfraAttempts">Attempts for an infrastructure failure.</param>
/// <param name="InfraWait">Wait between them.</param>
public sealed record ApplyContext(
    RunContext Run,
    string? ProjectRoot,
    bool Yes,
    bool DryRun,
    bool ConfirmSetting,
    TimeSpan StepTimeout,
    int InfraAttempts,
    TimeSpan InfraWait);

/// <summary>
/// P06 §4: one question for the run. One class is asked yes/no (§4.1), or name-or-skip when its name is taken (§4.2); several
/// classes get one multi-select list with every safe class ticked (§4.3). <c>--yes</c> and <c>apply.confirm = false</c> write the
/// ticked ones; without a keyboard nothing is written.
/// </summary>
public static class ApplyStep
{
    /// <summary>Plans, asks once, writes; returns the summary of each target.</summary>
    public static async Task<IReadOnlyDictionary<string, ApplySummary>> RunAsync(
        ITestWritePolicy policy, IReadOnlyList<TargetRun> targets, ApplyContext context, IConfirmation confirm, List<string> warnings, CancellationToken ct)
    {
        var plans = new List<ApplyPlan>();
        foreach (var target in targets.Where(t => t.Final is not null && t.Metadata is not null))
        {
            plans.Add(await policy.PlanAsync(target, context, ct).ConfigureAwait(false));
        }

        var open = plans.Where(p => p.Decided is null).ToList();
        var answers = await AskAsync(open, context, confirm, warnings, ct).ConfigureAwait(false);
        var summaries = new Dictionary<string, ApplySummary>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            var notWritten = plan.Decided is null ? answers[plan] : null;
            summaries[plan.Target.Result.CutFqn] = await policy.ApplyAsync(plan, notWritten, context, warnings, ct).ConfigureAwait(false);
        }

        return summaries;
    }

    /// <summary>For each open plan: null to write it, or why it is not written.</summary>
    private static async Task<Dictionary<ApplyPlan, ApplyDecision?>> AskAsync(
        IReadOnlyList<ApplyPlan> open, ApplyContext context, IConfirmation confirm, List<string> warnings, CancellationToken ct)
    {
        var answers = new Dictionary<ApplyPlan, ApplyDecision?>();
        if (open.Count == 0)
        {
            return answers;
        }

        // A changed CUT is never written without asking (P06 §3 step 5).
        if (context.Yes || !context.ConfirmSetting)
        {
            foreach (var plan in open)
            {
                answers[plan] = plan.ProjectChanged ? ApplyDecision.Blocked : null;
            }

            return answers;
        }

        if (!confirm.CanAsk)
        {
            warnings.Add("Bỏ qua ghi file: cần --yes khi chạy không tương tác");
            foreach (var plan in open)
            {
                answers[plan] = plan.ProjectChanged ? ApplyDecision.Blocked : ApplyDecision.SkippedNonInteractive;
            }

            return answers;
        }

        if (open.Count == 1)
        {
            var plan = open[0];
            bool write;
            if (plan.ProjectChanged)
            {
                write = await confirm.ConfirmAsync(
                    $"{plan.Target.Metadata!.Cut.SimpleName} đã thay đổi sau khi ARROW đọc nó; test được xác minh trên bản cũ. Vẫn ghi?", false, ct).ConfigureAwait(false);
            }
            else if (plan.ExistingName is { } existing)
            {
                var choice = await confirm.ChooseOneAsync(
                    $"Đã có {existing} trong project. Ghi bản mới thành:",
                    [$"1. {Path.GetFileName(plan.Path)} (giữ nguyên file cũ)", "2. Không ghi, chỉ lưu trong runs/"],
                    ct).ConfigureAwait(false);
                write = choice == 0;
            }
            else
            {
                write = await confirm.ConfirmAsync($"Ghi {plan.Path} ({plan.Details})?", true, ct).ConfigureAwait(false);
            }

            answers[plan] = write ? null : ApplyDecision.Declined;
            return answers;
        }

        var chosen = await confirm.ChooseManyAsync(
            "Chọn file test để ghi vào project",
            open.Select(p => p.Label).ToList(),
            open.Select(p => !p.ProjectChanged).ToList(),
            ct).ConfigureAwait(false) ?? [];
        for (var i = 0; i < open.Count; i++)
        {
            answers[open[i]] = chosen.Contains(i) ? null : ApplyDecision.Declined;
        }

        return answers;
    }
}
