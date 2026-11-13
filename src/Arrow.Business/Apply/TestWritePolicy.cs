using System.Security.Cryptography;
using Arrow.Business.Analysis;
using Arrow.Business.Repair;
using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Verification;
using Arrow.DataAccess.Apply;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Apply;

/// <summary>The write step of one CUT after the checks of P06 §3–4.2, before the question.</summary>
/// <param name="Target">The finished target.</param>
/// <param name="Decided">The decision when there is nothing to ask (not VALID, remote, dry run, blocked); null when the user is asked.</param>
/// <param name="Path">Where the file goes, from the project root.</param>
/// <param name="TestFqn">FQN of the test class to write.</param>
/// <param name="Code">The code to write.</param>
/// <param name="NameIndex">k of <c>_Arrow&lt;k&gt;Test</c>.</param>
/// <param name="RenamedAfterRun">The name appeared during the run, so the class was renamed and verified again (P06 §4.2 b).</param>
/// <param name="ExistingName">File name that is taken, for the question of P06 §4.2; null when the first name is free.</param>
/// <param name="ProjectChanged">The CUT changed since the run read it (<c>W_PROJECT_CHANGED</c>).</param>
/// <param name="Error">The error behind a blocked decision, or <c>W_PROJECT_CHANGED</c>.</param>
/// <param name="Tags">Warning tags of <c>apply.json</c>.</param>
/// <param name="Eol">Line endings of the CUT.</param>
public sealed record ApplyPlan(
    TargetRun Target,
    ApplyDecision? Decided,
    string Path,
    string TestFqn,
    string Code,
    int NameIndex,
    bool RenamedAfterRun,
    string? ExistingName,
    bool ProjectChanged,
    ErrorInfo? Error,
    IReadOnlyList<string> Tags,
    LineEndings Eol)
{
    /// <summary><c>6 test · line 78% · mutation 61%</c>.</summary>
    public string Details
    {
        get
        {
            var metrics = Target.Result.Metrics;
            return $"{Target.Final?.Candidate.TestMethodCount ?? 0} test"
                   + (metrics?.Line is { } line ? $" · line {(line * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture)}%" : "")
                   + (metrics?.Mutation is { } mutation ? $" · mutation {(mutation * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture)}%" : "");
        }
    }

    /// <summary>The line of the multi-select list (P06 §4.3).</summary>
    public string Label =>
        $"{System.IO.Path.GetFileName(Path),-34} {Details}"
        + (ExistingName is null ? "" : $"   (đã có {ExistingName})")
        + (ProjectChanged ? "   [!] CUT đã thay đổi" : "");
}

/// <summary>Decides what to write into the project (P06 §3–4, §6) and writes it with <see cref="IProjectFileWriter"/>.</summary>
public interface ITestWritePolicy
{
    /// <summary>The checks of P06 §3 and the name of §4.2; renames and verifies again when the name appeared during the run.</summary>
    Task<ApplyPlan> PlanAsync(TargetRun target, ApplyContext context, CancellationToken ct);

    /// <summary>Writes the file unless <paramref name="notWritten"/> says why not, and writes <c>apply.json</c>.</summary>
    Task<ApplySummary> ApplyAsync(ApplyPlan plan, ApplyDecision? notWritten, ApplyContext context, List<string> warnings, CancellationToken ct);
}

/// <summary>
/// <see cref="ITestWritePolicy"/>. The only code that writes into the user's project: VALID suites only, never outside
/// <c>test_root</c>, never over a file. A name that appeared during the run gets the next <c>_Arrow&lt;k&gt;Test</c>, java-helper
/// <c>rename_class</c>, and one more verification (P04 §8); a suite that is not VALID after renaming is not written.
/// </summary>
public sealed class TestWritePolicy(IProjectFileWriter writer, IVerifier verifier, IJavaHelperProvider javaHelpers, IRunStore runs, ErrorCatalog catalog) : ITestWritePolicy
{
    public async Task<ApplyPlan> PlanAsync(TargetRun target, ApplyContext context, CancellationToken ct)
    {
        var final = target.Final!;
        var m = target.Metadata!;
        var tags = new List<string>();
        if (!m.Paths.NameMatchesTestIncludes || final.GeneratedTestRanInModule == false)
        {
            tags.Add("surefire_includes_mismatch");
        }

        var plan = new ApplyPlan(target, null, final.Candidate.TestPath, final.Candidate.TestFqn, final.Candidate.Code ?? "", m.Paths.NameIndex, false,
            m.Paths.NameIndex > 1 ? TestPathPlanner.TestName(m.Cut.SimpleName, 1) + ".java" : null, false, null, tags, LineEndings.Lf);

        // P06 §3 steps 1–3: nothing to ask.
        if (!final.Success)
        {
            return plan with { Decided = ApplyDecision.NotValid };
        }

        if (context.ProjectRoot is not { } root)
        {
            return plan with { Decided = ApplyDecision.RemoteProject };
        }

        if (context.DryRun)
        {
            return plan with { Decided = ApplyDecision.DryRun };
        }

        // Step 4: inside the project and under test_root.
        if (Outside(root, plan.Path, m) is { } outside)
        {
            return plan with { Decided = ApplyDecision.Blocked, Error = outside };
        }

        // Step 5: the CUT did not change since the copy.
        var cutFile = System.IO.Path.Combine(root, m.Paths.CutPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (File.Exists(cutFile))
        {
            var bytes = await File.ReadAllBytesAsync(cutFile, ct).ConfigureAwait(false);
            plan = plan with { Eol = ProjectFileWriter.EndingsOf(System.Text.Encoding.UTF8.GetString(bytes)) };
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != m.Cut.SourceSha256)
            {
                plan = plan with { ProjectChanged = true, Error = catalog.Describe(ErrorCodes.WProjectChanged) };
            }
        }

        // Step 6: the name is still free now; otherwise P06 §4.2 (b).
        if (Taken(root, plan.Path, m))
        {
            plan = await RenameAsync(plan, root, context, ct).ConfigureAwait(false);
        }

        if (plan.Decided is null)
        {
            Report(context, ArrowEventTypes.ApplyPlanned, m.Cut.Fqn, $"Sẽ ghi {plan.Path}", ("path", plan.Path));
        }

        return plan;
    }

    public async Task<ApplySummary> ApplyAsync(ApplyPlan plan, ApplyDecision? notWritten, ApplyContext context, List<string> warnings, CancellationToken ct)
    {
        var m = plan.Target.Metadata!;
        var decision = plan.Decided ?? notWritten;
        var error = plan.Error;
        string? written = null;
        if (decision is null)
        {
            // Just before writing, check the place again (P06 §3: steps 4 and 6 again).
            var root = context.ProjectRoot!;
            if (Taken(root, plan.Path, m))
            {
                plan = await RenameAsync(plan, root, context, ct).ConfigureAwait(false);
            }

            for (var attempt = 1; plan.Decided is null; attempt++)
            {
                try
                {
                    written = await writer.WriteNewAsync(root, plan.Path, plan.Code, plan.Eol, ct).ConfigureAwait(false);
                    decision = ApplyDecision.Written;
                    error = null;
                    Report(context, ArrowEventTypes.ApplyWritten, m.Cut.Fqn, $"Đã ghi {plan.Path}", ("path", plan.Path));
                    break;
                }
                catch (ArrowException ex) when (ex.Code == ErrorCodes.WTargetExists && attempt == 1)
                {
                    // The file appeared between the check and the write: the next name, once (P06 §5 step 3).
                    plan = await RenameAsync(plan, root, context, ct).ConfigureAwait(false);
                }
                catch (ArrowException ex)
                {
                    decision = ApplyDecision.Blocked;
                    error = catalog.Describe(ex);
                    break;
                }
            }

            decision ??= plan.Decided;
            error = decision == ApplyDecision.Written ? null : error ?? plan.Error;
        }

        if (decision != ApplyDecision.Written)
        {
            Report(context, ArrowEventTypes.ApplySkipped, m.Cut.Fqn, $"Không ghi {System.IO.Path.GetFileName(plan.Path)} ({Name(decision!.Value)})", ("decision", decision));
        }

        AddWarnings(plan, decision!.Value, warnings);
        runs.WriteJson(context.Run.Run, $"{ProjectAnalyzer.TargetDir(m.Cut.Fqn)}/apply.json", new ApplyRecord(
            decision.Value,
            decision is ApplyDecision.Written or ApplyDecision.DryRun or ApplyDecision.Declined or ApplyDecision.SkippedNonInteractive ? plan.Path : null,
            plan.TestFqn, plan.NameIndex, plan.RenamedAfterRun, plan.RenamedAfterRun,
            plan.Eol == LineEndings.Crlf ? "crlf" : "lf", plan.Tags, error, DateTimeOffset.UtcNow));
        return new ApplySummary(decision.Value, decision is ApplyDecision.Written or ApplyDecision.DryRun ? plan.Path : null);
    }

    /// <summary><c>written</c>, <c>not_valid</c>… as in <c>apply.json</c>.</summary>
    public static string Name(ApplyDecision decision) => decision switch
    {
        ApplyDecision.NotValid => "not_valid",
        ApplyDecision.RemoteProject => "remote_project",
        ApplyDecision.DryRun => "dry_run",
        ApplyDecision.SkippedNonInteractive => "skipped_non_interactive",
        _ => decision.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// P06 §4.2 (b): the smallest free <c>_Arrow&lt;k&gt;Test</c>, the class renamed by java-helper, and one more verification on a
    /// fresh copy (P04 §8). Not VALID gives <c>W_REVERIFY_FAILED</c>; no free name gives <c>B1_TEST_NAME_EXHAUSTED</c>.
    /// </summary>
    private async Task<ApplyPlan> RenameAsync(ApplyPlan plan, string root, ApplyContext context, CancellationToken ct)
    {
        var m = plan.Target.Metadata!;
        var oldName = System.IO.Path.GetFileName(plan.Path);
        var next = TestPathPlanner.Plan(m.Paths.TestRoot, m.Cut.Fqn, (path, _) => Taken(root, path, m));
        if (next is null)
        {
            return plan with { Decided = ApplyDecision.Blocked, Error = catalog.Describe(ErrorCodes.B1TestNameExhausted) };
        }

        var simple = next.Fqn[(next.Fqn.LastIndexOf('.') + 1)..];
        string code;
        try
        {
            using var lease = await javaHelpers.RentAsync(ct).ConfigureAwait(false);
            code = (await lease.Helper.RenameClassAsync(plan.Code, simple, m.Cut.Package.Length == 0 ? null : m.Cut.Package, ct).ConfigureAwait(false)).Code;
        }
        catch (Exception ex) when (ex is ArrowException or JavaHelperCommandException)
        {
            return plan with { Decided = ApplyDecision.Blocked, Error = catalog.Describe(ErrorCodes.WReverifyFailed, ex.Message) };
        }

        Report(context, ArrowEventTypes.ApplyRenamed, m.Cut.Fqn, $"Đã có {oldName}; đổi tên thành {simple}", ("k", next.NameIndex), ("name", simple));
        var dir = $"{ProjectAnalyzer.TargetDir(m.Cut.Fqn)}/apply";
        runs.WriteText(context.Run.Run, $"{dir}/{simple}.java", code);
        if (plan.Target.Task is not { } task)
        {
            // A target kept from a resumed run has no snapshot left to verify the renamed suite on.
            return plan with { Decided = ApplyDecision.Blocked, Error = catalog.Describe(ErrorCodes.WReverifyFailed, "không còn bản sao để xác minh lại") };
        }

        var final = plan.Target.Final!;
        var result = await verifier.VerifyAsync(
            SuiteFiles.Single(next.Fqn, next.Path, code), task,
            new VerifyOptions($"{dir}/reverify", 0, context.StepTimeout, context.InfraAttempts, context.InfraWait, false, final.Candidate.CodeHash, final.Candidate.AssertionCount),
            ct).ConfigureAwait(false);
        runs.WriteJson(context.Run.Run, $"{dir}/reverify.json", result);
        var valid = result.State == VerificationState.Valid;
        Report(context, ArrowEventTypes.ApplyReverified, m.Cut.Fqn, valid ? $"{simple}: VALID" : $"{simple}: {(result.State?.ToString().ToUpperInvariant() ?? "INFRA")}",
            ("valid", valid), ("state", result.State?.ToString().ToUpperInvariant()));
        var renamed = plan with { Path = next.Path, TestFqn = next.Fqn, Code = code, NameIndex = next.NameIndex, RenamedAfterRun = true, ExistingName = oldName };
        return valid ? renamed : renamed with { Decided = ApplyDecision.Blocked, Error = catalog.Describe(ErrorCodes.WReverifyFailed) };
    }

    /// <summary>The file exists in the project, or a test class of the project has that simple name.</summary>
    private static bool Taken(string root, string path, RepositoryMetadata m)
    {
        if (File.Exists(System.IO.Path.Combine(root, path.Replace('/', System.IO.Path.DirectorySeparatorChar))))
        {
            return true;
        }

        var file = System.IO.Path.GetFileName(path);
        var testRoot = System.IO.Path.Combine(root, m.Paths.TestRoot.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return Directory.Exists(testRoot) && Directory.EnumerateFiles(testRoot, file, SearchOption.AllDirectories).Any();
    }

    /// <summary><c>W_TARGET_OUTSIDE_PROJECT</c> when the path leaves the project or <c>test_root</c>; null when it is fine.</summary>
    private ErrorInfo? Outside(string root, string path, RepositoryMetadata m)
    {
        var testRoot = m.Paths.TestRoot.TrimEnd('/') + "/";
        try
        {
            ProjectFileWriter.Target(root, path);
            return path.Replace('\\', '/').StartsWith(testRoot, StringComparison.Ordinal) ? null : catalog.Describe(ErrorCodes.WTargetOutsideProject, $"{path} không nằm dưới {m.Paths.TestRoot}");
        }
        catch (ArrowException ex)
        {
            return catalog.Describe(ex);
        }
    }

    /// <summary>The warnings of P06 §4.4.</summary>
    private static void AddWarnings(ApplyPlan plan, ApplyDecision decision, List<string> warnings)
    {
        var name = System.IO.Path.GetFileName(plan.Path);
        if (decision == ApplyDecision.Written && plan.Tags.Contains("surefire_includes_mismatch"))
        {
            var simple = plan.TestFqn[(plan.TestFqn.LastIndexOf('.') + 1)..];
            warnings.Add($"mvn test của project sẽ không tự chạy {name}. Hãy thêm mẫu tên vào cấu hình Surefire, hoặc chạy riêng bằng -Dtest={simple}");
        }

        if (decision == ApplyDecision.Blocked && plan.Error is { } error)
        {
            warnings.Add($"Không ghi {name}: {error.Code} · {error.Message}");
        }
    }

    private static void Report(ApplyContext context, string type, string cut, string message, params (string Key, object? Value)[] data) =>
        context.Run.Progress.Report(new ArrowEvent(DateTimeOffset.UtcNow, type, cut, null, null, EventLevel.Info, message, data.ToDictionary(d => d.Key, d => d.Value)));
}
