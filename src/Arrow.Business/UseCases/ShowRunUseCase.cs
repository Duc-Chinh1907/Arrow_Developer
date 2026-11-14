using Arrow.Business.Analysis;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Quality;
using Arrow.Common.Verification;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.UseCases;

/// <summary>A run as <c>arrow show &lt;run&gt;</c> prints it (P08 §2.7).</summary>
/// <param name="Run"><c>run.json</c>.</param>
/// <param name="Dir">Folder of the run.</param>
/// <param name="Targets">The <c>result.json</c> of each target; null for a target that has none yet (PENDING, RUNNING).</param>
public sealed record RunView(RunInfo Run, string Dir, IReadOnlyList<(RunTarget Target, TargetResult? Result)> Targets);

/// <summary>One round as <c>arrow show &lt;run&gt; &lt;class&gt;</c> prints it.</summary>
/// <param name="Round">Round; 0 for c0.</param>
/// <param name="Level">Level used; null for c0.</param>
/// <param name="Result">State and signature, or "mã trùng", "output không dùng được".</param>
/// <param name="Comparison">Against the checkpoint; null for c0 and rounds that were not verified.</param>
/// <param name="Decision">What the policy did.</param>
public sealed record RoundView(int Round, int? Level, string Result, Comparison? Comparison, string Decision);

/// <summary>A target as <c>arrow show &lt;run&gt; &lt;class&gt;</c> prints it (P08 §2.7).</summary>
/// <param name="Cut">The CUT.</param>
/// <param name="Result"><c>result.json</c>; null when the target did not finish.</param>
/// <param name="Block1"><c>block1/outcome.json</c>.</param>
/// <param name="Metadata"><c>block1/metadata.json</c>.</param>
/// <param name="Rounds">c0 and each repair round.</param>
/// <param name="Coverage"><c>block4/jacoco.json</c>.</param>
/// <param name="Mutation"><c>block4/pit.json</c>, with the surviving mutants.</param>
/// <param name="Smells"><c>block4/tsdetect.json</c>.</param>
/// <param name="Files">Files of the target in <c>runs/</c>, by name.</param>
public sealed record TargetView(
    string Cut,
    TargetResult? Result,
    BlockOutcome? Block1,
    RepositoryMetadata? Metadata,
    IReadOnlyList<RoundView> Rounds,
    CoverageResult? Coverage,
    MutationResult? Mutation,
    SmellResult? Smells,
    IReadOnlyDictionary<string, string> Files);

/// <summary>The lines of <c>history.jsonl</c> (P09 §5.4).</summary>
/// <param name="Entries">Newest first.</param>
/// <param name="SkippedLines">Damaged lines that were skipped.</param>
public sealed record HistoryView(IReadOnlyList<HistoryEntry> Entries, int SkippedLines);

/// <summary><c>arrow show</c> and <c>arrow history</c> (P08 §2.4, §2.7).</summary>
public interface IShowRunUseCase
{
    /// <summary>The newest <paramref name="limit"/> lines of <c>history.jsonl</c>.</summary>
    HistoryView History(int limit);

    /// <summary>A run by id or unique prefix; <c>GEN_RESUME_NOT_FOUND</c> when there is none.</summary>
    RunView Show(string run);

    /// <summary>One target of a run, by FQN or simple name; <c>B1_CUT_MISSING</c> when the run has no such class.</summary>
    TargetView ShowTarget(string run, string cut);
}

/// <summary><see cref="IShowRunUseCase"/>: reads the files of <c>runs/</c>; it changes nothing.</summary>
public sealed class ShowRunUseCase(IRunStore runs) : IShowRunUseCase
{
    public HistoryView History(int limit)
    {
        var content = runs.History(limit);
        return new HistoryView(content.Entries, content.SkippedLines);
    }

    public RunView Show(string run)
    {
        var (handle, info) = Open(run);
        var targets = info.Targets
            .Select(t => (t, runs.ReadJson<TargetResult>(handle, $"{ProjectAnalyzer.TargetDir(t.CutFqn)}/result.json")))
            .ToList();
        return new RunView(info, handle.Root, targets);
    }

    public TargetView ShowTarget(string run, string cut)
    {
        var (handle, info) = Open(run);
        var fqn = info.Targets.Select(t => t.CutFqn).FirstOrDefault(t => t == cut)
                  ?? info.Targets.Select(t => t.CutFqn).FirstOrDefault(t => t.EndsWith("." + cut, StringComparison.Ordinal))
                  ?? throw new ArrowException(ErrorCodes.B1CutMissing, $"Run {info.RunId} không có class {cut}. Có: {string.Join(", ", info.Targets.Select(t => t.CutFqn))}",
                      new Dictionary<string, string> { ["query"] = cut });
        var dir = ProjectAnalyzer.TargetDir(fqn);
        T? Read<T>(string path) => runs.ReadJson<T>(handle, $"{dir}/{path}");

        var rounds = new List<RoundView>();
        if (Read<VerifyResult>("block3/verify_c0.json") is { } c0)
        {
            rounds.Add(new RoundView(0, null, StateText(c0), null, "checkpoint = c0"));
        }

        for (var k = 1; Read<RoundDecision>($"block3/round_{k}/decision.json") is { } decision; k++)
        {
            var result = decision.InvalidOutput ? "output không dùng được"
                : decision.DuplicateCode ? $"mã trùng vòng {decision.DuplicateOfRound}"
                : decision.State is { } state ? $"{state.ToString().ToUpperInvariant()}{(decision.ErrorSignature is { } s ? " · " + Short(s) : "")}"
                : "INFRA";
            rounds.Add(new RoundView(k, decision.Level, result, decision.Comparison, RepairRunner.DecisionText(decision)));
        }

        var root = Path.Combine(handle.Root, "targets", fqn);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, path) in new[]
                 {
                     ("result", "result.json"), ("metadata", "block1/metadata.json"), ("c0", "block2/candidate.java"),
                     ("final", "block3/final.java"), ("final_json", "block3/final.json"), ("quality", "block4/outcome.json"), ("apply", "apply.json"),
                 })
        {
            if (runs.Exists(handle, $"{dir}/{path}"))
            {
                files[name] = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        return new TargetView(
            fqn,
            Read<TargetResult>("result.json"),
            Read<BlockOutcome>("block1/outcome.json"),
            Read<RepositoryMetadata>("block1/metadata.json"),
            rounds,
            Read<CoverageResult>("block4/jacoco.json"),
            Read<MutationResult>("block4/pit.json"),
            Read<SmellResult>("block4/tsdetect.json"),
            files);
    }

    private (RunHandle Handle, RunInfo Info) Open(string run)
    {
        var handle = runs.Open(run);
        var info = runs.ReadJson<RunInfo>(handle, "run.json")
                   ?? throw new ArrowException(ErrorCodes.GenResumeNotFound, args: new Dictionary<string, string> { ["run_id"] = run });
        return (handle, info);
    }

    private static string StateText(VerifyResult result) =>
        result.State is { } state ? $"{state.ToString().ToUpperInvariant()}{(result.ErrorSignature is { } s ? " · " + Short(s) : "")}" : $"INFRA {result.InfraReason}";

    /// <summary><c>COMPILE | cannot find symbol | method</c> → <c>cannot find symbol | method</c>.</summary>
    private static string Short(string signature)
    {
        var bar = signature.IndexOf(" | ", StringComparison.Ordinal);
        return bar < 0 ? signature : signature[(bar + 3)..];
    }
}
