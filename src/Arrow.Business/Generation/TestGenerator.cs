using Arrow.Business.Llm;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Events;
using Arrow.Common.Generation;
using Arrow.Common.Llm;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Generation;

/// <summary>What block 2 needs for one CUT (P02 §2.1).</summary>
/// <param name="Run">The results folder of the run.</param>
/// <param name="CutFqn">The CUT.</param>
/// <param name="Metadata">M (P01 §5.1).</param>
/// <param name="MetadataPrompt">The text of M, <c>metadata_prompt.txt</c>.</param>
/// <param name="CutSource">The source of the CUT as it is in the project.</param>
/// <param name="Model">The model of the run, with its parameters.</param>
/// <param name="ResultDir">Folder of this call below the run: <c>targets/&lt;cut&gt;/block2</c>, or <c>targets/&lt;cut&gt;/block3/round_&lt;k&gt;</c>.</param>
/// <param name="Progress">Progress events (P02 §8); must not block.</param>
public sealed record GenerationContext(
    RunHandle Run,
    string CutFqn,
    RepositoryMetadata Metadata,
    string MetadataPrompt,
    string CutSource,
    ResolvedModel Model,
    string ResultDir,
    IProgress<ArrowEvent>? Progress = null);

/// <summary>What block 2 made from one call (P02 §6).</summary>
/// <param name="Candidate">The candidate test; <see cref="CandidateTest.ValidOutput"/> is false for <c>INVALID_OUTPUT</c>.</param>
/// <param name="Response">The answer of the model.</param>
/// <param name="Usage">The record written to <c>usage.json</c>.</param>
/// <param name="Prompt">The record written to <c>prompt.json</c>.</param>
public sealed record GenerationResult(CandidateTest Candidate, LlmResponse Response, LlmUsageRecord Usage, PromptRecord Prompt);

/// <summary><c>candidate.json</c> (P02 §6).</summary>
public sealed record CandidateFile(
    string CutFqn,
    string Mode,
    int Round,
    int? Level,
    string Model,
    bool ValidOutput,
    string? InvalidReason,
    string? ParseError,
    string TestFqn,
    string TestPath,
    string? CodeHash,
    int TestMethodCount,
    int AssertionCount,
    IReadOnlyList<string> Normalization,
    IReadOnlyList<string> DroppedMethods,
    string? FinishReason);

/// <summary>Block 2 (P02 §8).</summary>
public interface ITestGenerator
{
    /// <summary>The first generation, c0. Model errors (<c>B2_LLM_*</c>, <c>B2_WRONG_MODEL</c>, <c>B2_TOKEN_BUDGET</c>) reach the caller as <c>ArrowException</c>.</summary>
    Task<GenerationResult> GenerateAsync(GenerationContext context, CancellationToken ct);

    /// <summary>One repair round (P04).</summary>
    Task<GenerationResult> RepairAsync(GenerationContext context, RepairRequest request, CancellationToken ct);
}

/// <summary>
/// Block 2 (P02): builds the prompt, calls the model through <see cref="ILlmClient"/>, takes the code out of the answer, checks that it
/// parses and has one type, names it, splices methods (level 3) and counts. It writes <c>prompt.json</c> before the call and then
/// <c>response.json</c>, <c>usage.json</c>, <c>candidate.java</c> and <c>candidate.json</c>. An answer that cannot be used is a result
/// (<c>valid_output = false</c>), not an exception.
/// </summary>
public sealed class TestGenerator(ILlmClient llm, IJavaHelperProvider javaHelper, IPromptTemplateStore templates, IRunStore runs, TimeProvider? time = null) : ITestGenerator
{
    /// <summary>A CUT longer than this many characters is shortened before it goes into the prompt (P02 §3.3).</summary>
    public const int MaxCutChars = 48_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary><c>gen/&lt;cut_fqn&gt;</c> (P02 §4).</summary>
    public static string GenerateCallId(string cutFqn) => $"gen/{cutFqn}";

    /// <summary><c>repair/&lt;cut_fqn&gt;/round_&lt;k&gt;</c> (P02 §4).</summary>
    public static string RepairCallId(string cutFqn, int round) => $"repair/{cutFqn}/round_{round}";

    public async Task<GenerationResult> GenerateAsync(GenerationContext context, CancellationToken ct)
    {
        var (cut, truncation) = await PrepareCutAsync(context, ct).ConfigureAwait(false);
        var built = PromptBuilder.Generation(templates, Input(context, cut));
        return await CallAsync(context, GenerateCallId(context.CutFqn), "GENERATE", 0, null, built, truncation, null, ct).ConfigureAwait(false);
    }

    public async Task<GenerationResult> RepairAsync(GenerationContext context, RepairRequest request, CancellationToken ct)
    {
        var (cut, truncation) = await PrepareCutAsync(context, ct).ConfigureAwait(false);
        var built = PromptBuilder.Repair(templates, Input(context, cut), request);
        return await CallAsync(context, RepairCallId(context.CutFqn, request.Round), "REPAIR", request.Round, request.Level, built, truncation, request, ct).ConfigureAwait(false);
    }

    private static PromptInput Input(GenerationContext context, string cut) => new(
        context.Metadata.Cut.Fqn,
        context.Metadata.Paths.CutPath,
        context.Metadata.Cut.Package,
        PromptBuilder.TestClassName(context.Metadata.Paths.GeneratedTestFqn),
        context.MetadataPrompt,
        cut);

    /// <summary>The CUT for the prompt: as it is, or shortened by java-helper when it is over 48,000 characters (P02 §3.3).</summary>
    private async Task<(string Code, string Truncation)> PrepareCutAsync(GenerationContext context, CancellationToken ct)
    {
        var text = context.CutSource.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.Length <= MaxCutChars)
        {
            return (text, "none");
        }

        using var lease = await javaHelper.RentAsync(ct).ConfigureAwait(false);
        var shrunk = await lease.Helper.ShrinkSourceAsync(text, MaxCutChars, ct).ConfigureAwait(false);
        Report(context, ArrowEventTypes.B2SourceTruncated, EventLevel.Warning,
            $"{context.CutFqn} dài {text.Length:N0} ký tự, đã rút gọn ({shrunk.Truncation}).",
            ("truncation", shrunk.Truncation), ("original_chars", text.Length));
        return (shrunk.Code, shrunk.Truncation);
    }

    private async Task<GenerationResult> CallAsync(
        GenerationContext context, string callId, string mode, int round, int? level, BuiltPrompt built, string truncation, RepairRequest? repair, CancellationToken ct)
    {
        var request = new LlmRequest(callId, context.Model.Name, built.Messages, context.Model.Params);
        var prompt = new PromptRecord(callId, request.Messages, request.Params, built.Templates, truncation);
        runs.WriteJson(context.Run, Path(context, "prompt.json"), prompt);
        Report(context, ArrowEventTypes.B2Prompt, EventLevel.Info, $"Prompt {callId}: {built.Messages.Sum(m => m.Content.Length):N0} ký tự",
            ("call_id", callId), ("chars", built.Messages.Sum(m => m.Content.Length)), ("source_truncation", truncation));

        Report(context, ArrowEventTypes.B2Calling, EventLevel.Info, $"Gọi model {context.Model.Name} ({callId})", ("call_id", callId), ("model", context.Model.Name));
        var response = await llm.CompleteAsync(request, ct).ConfigureAwait(false);
        runs.WriteJson(context.Run, Path(context, "response.json"), response.RawBody);
        var usage = LlmUsageRecord.From(response, context.Model);
        runs.WriteJson(context.Run, Path(context, "usage.json"), usage);
        Report(context, ArrowEventTypes.B2Response, EventLevel.Info,
            $"{response.PromptTokens:N0} + {response.CompletionTokens:N0} token, {response.LatencySeconds:0.0} s{(response.Replayed ? " (phát lại)" : "")}",
            ("prompt_tokens", response.PromptTokens), ("completion_tokens", response.CompletionTokens), ("latency_seconds", response.LatencySeconds), ("finish_reason", response.FinishReason), ("replayed", response.Replayed));
        if (response.FinishReason == "length")
        {
            Report(context, ArrowEventTypes.B2Truncated, EventLevel.Warning,
                "Câu trả lời bị cắt vì hết max_tokens. Tăng llm.models.<tên>.params.max_tokens nếu lỗi này lặp lại.", ("max_tokens", context.Model.MaxTokens));
        }

        var candidate = (await NormalizeAsync(response.Content, context.Metadata, repair, ct).ConfigureAwait(false)) with { FinishReason = response.FinishReason };
        if (candidate.Code is not null)
        {
            runs.WriteText(context.Run, Path(context, "candidate.java"), candidate.Code);
        }

        runs.WriteJson(context.Run, Path(context, "candidate.json"), new CandidateFile(
            context.CutFqn, mode, round, level, context.Model.Name, candidate.ValidOutput, candidate.InvalidReason, candidate.ParseError,
            candidate.TestFqn, candidate.TestPath, candidate.CodeHash, candidate.TestMethodCount, candidate.AssertionCount,
            candidate.Normalization, candidate.DroppedMethods, candidate.FinishReason));
        Report(context, ArrowEventTypes.B2Candidate, candidate.ValidOutput ? EventLevel.Info : EventLevel.Warning,
            candidate.ValidOutput ? $"Candidate hợp lệ: {candidate.TestMethodCount} test, {candidate.AssertionCount} assertion" : $"Câu trả lời không dùng được: {candidate.InvalidReason}",
            ("valid", candidate.ValidOutput), ("invalid_reason", candidate.InvalidReason), ("normalization", candidate.Normalization));
        return new GenerationResult(candidate, response, usage, prompt);
    }

    /// <summary>
    /// Steps 5.1 to 5.5 of P02 on an answer: take the code, parse it (one top-level type), drop repeated package and import lines, set the
    /// package and the class name, splice the methods of level 3, then hash and count the final code. No import is added or changed.
    /// </summary>
    public async Task<CandidateTest> NormalizeAsync(string answer, RepositoryMetadata metadata, RepairRequest? repair, CancellationToken ct)
    {
        var testFqn = metadata.Paths.GeneratedTestFqn;
        CandidateTest Invalid(string reason, IReadOnlyList<string> normalization, string? parseError = null) =>
            new(false, reason, parseError, null, testFqn, metadata.Paths.GeneratedTestPath, null, 0, 0, normalization, [], null);

        var extracted = CodeExtractor.Extract(answer);
        var actions = extracted.Normalization.ToList();
        if (extracted.Code is null)
        {
            return Invalid(InvalidReasons.NoCode, actions);
        }

        using var lease = await javaHelper.RentAsync(ct).ConfigureAwait(false);
        var helper = lease.Helper;
        var parsed = await helper.ParseAsync(extracted.Code, ct).ConfigureAwait(false);
        if (!parsed.Parsed)
        {
            return Invalid(InvalidReasons.ParseError, actions, parsed.Error);
        }

        if (parsed.TopLevelTypes.Count != 1)
        {
            return Invalid(parsed.TopLevelTypes.Count == 0 ? InvalidReasons.NoCode : InvalidReasons.MultipleClasses, actions);
        }

        var (noDuplicatePackage, packageActions) = CodeExtractor.RemoveDuplicatePackages(extracted.Code);
        var (noDuplicateImports, importActions) = CodeExtractor.RemoveDuplicateImports(noDuplicatePackage);
        actions.AddRange(packageActions);
        actions.AddRange(importActions);
        var renamed = await helper.RenameClassAsync(noDuplicateImports, PromptBuilder.TestClassName(testFqn), metadata.Cut.Package, ct).ConfigureAwait(false);
        actions.AddRange(renamed.Actions);
        var code = renamed.Code;

        var dropped = new List<string>();
        if (repair is { Level: 3, RewriteMethods.Count: > 0 })
        {
            var splice = await helper.SpliceMethodsAsync(repair.BaseCode, code, repair.RewriteMethods, ct).ConfigureAwait(false);
            code = splice.Code;
            dropped.AddRange(splice.Dropped);
            actions.Add($"splice: replaced {splice.Replaced.Count}, dropped {splice.Dropped.Count}, added imports {splice.AddedImports.Count}");
        }

        code = code.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";

        // The hash is of the spliced code: block 3 compares seen hashes on that text (P02 §5.5, R03 §6.4).
        var hash = await helper.StripCommentsHashAsync(code, ct).ConfigureAwait(false);
        var tests = await helper.ListTestMethodsAsync(code, ct).ConfigureAwait(false);
        var assertions = await helper.CountAssertionsAsync(code, ct).ConfigureAwait(false);
        return new CandidateTest(true, null, null, code, testFqn, metadata.Paths.GeneratedTestPath, hash.CodeHash, tests.Count, assertions.Total ?? 0, actions, dropped, null);
    }

    private static string Path(GenerationContext context, string file) => context.ResultDir.TrimEnd('/') + "/" + file;

    private void Report(GenerationContext context, string type, EventLevel level, string message, params (string Key, object? Value)[] data) =>
        context.Progress?.Report(new ArrowEvent(_time.GetUtcNow(), type, context.CutFqn, null, null, level, message, data.ToDictionary(d => d.Key, d => d.Value)));
}
