using System.Text;
using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.Common.Generation;
using Arrow.Common.Llm;

namespace Arrow.Business.Generation;

/// <summary>The values every prompt of a CUT shares (P02 §3.2).</summary>
/// <param name="CutFqn">FQN of the CUT.</param>
/// <param name="CutPath">Path of the CUT file from the project root.</param>
/// <param name="TestPackage">Package of the CUT; empty for the default package.</param>
/// <param name="TestClassName">Short name of the test class, e.g. <c>OrderService_Arrow2Test</c>.</param>
/// <param name="MetadataPrompt">The text of M, <c>metadata_prompt.txt</c>, in full for every prompt (decision Q4).</param>
/// <param name="CutSource">Source of the CUT, after shrinking when it is long.</param>
public sealed record PromptInput(string CutFqn, string CutPath, string TestPackage, string TestClassName, string MetadataPrompt, string CutSource);

/// <summary>The two messages of a call and the templates behind them.</summary>
public sealed record BuiltPrompt(IReadOnlyList<LlmMessage> Messages, IReadOnlyList<TemplateRef> Templates);

/// <summary>
/// Chooses the templates and fills their placeholders (P02 §3). Pure: it reads no file and calls no model. GENERATE uses
/// <c>zero_shot_project_aware</c>; REPAIR uses <c>repair/system.md</c> and the user template of the level.
/// </summary>
public static class PromptBuilder
{
    /// <summary>Attempts shown with every detail; older ones show their round and signature only (P02 §3.4, R03 §6.4).</summary>
    public const int DetailedAttempts = 3;

    /// <summary>Value of an empty <c>extra_context</c>, <c>failed_attempts</c> or <c>error_block</c>.</summary>
    public const string None = "(none)";

    /// <summary>Path of the user template of a repair level.</summary>
    public static string RepairUserPath(int level) => level switch
    {
        1 => "repair/user/level1.md",
        2 => "repair/user/level2.md",
        3 => "repair/user/level3.md",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Mức sửa phải là 1, 2 hoặc 3."),
    };

    /// <summary>The prompt of the first generation (c0).</summary>
    public static BuiltPrompt Generation(IPromptTemplateStore templates, PromptInput input)
    {
        var values = Shared(input);
        return Build(templates.Get("generation/system/zero_shot_project_aware.md"), templates.Get("generation/user/project_aware.md"), values);
    }

    /// <summary>
    /// The prompt of a repair round: the failing code is <see cref="RepairRequest.BaseCode"/>; levels 2 and 3 add the extra context and the
    /// failed attempts, level 3 the methods to rewrite.
    /// </summary>
    public static BuiltPrompt Repair(IPromptTemplateStore templates, PromptInput input, RepairRequest request)
    {
        var values = Shared(input);
        values["failure_summary"] = FailureSummary(templates.Get("repair/failure_summaries.md"), request.State);
        values["error_block"] = OrNone(request.ErrorBlock);
        values["test_source"] = Lf(request.BaseCode).TrimEnd('\n');
        if (request.Level >= 2)
        {
            values["extra_context"] = OrNone(request.ExtraContext);
            values["failed_attempts"] = FormatAttempts(request.FailedAttempts);
        }

        if (request.Level == 3)
        {
            // Inline, as the sentence of level3.md reads: "…in these test methods: `a`, `b`." (P02 §3.2 said one per line; see its note.)
            values["failing_methods"] = string.Join(", ", request.RewriteMethods.Select(m => $"`{m}`"));
        }

        return Build(templates.Get("repair/system.md"), templates.Get(RepairUserPath(request.Level)), values);
    }

    /// <summary>Short name of a test class from its FQN: <c>a.b.OrderService_ArrowTest</c> → <c>OrderService_ArrowTest</c>.</summary>
    public static string TestClassName(string generatedTestFqn) => generatedTestFqn[(generatedTestFqn.LastIndexOf('.') + 1)..];

    /// <summary>
    /// <c>{{failed_attempts}}</c> (P02 §3.4): in order of round; the last <see cref="DetailedAttempts"/> attempts have their first error and
    /// changed code, older ones only <c>Round r · signature</c>. The heading "Approaches that already failed" is in the template.
    /// </summary>
    public static string FormatAttempts(IReadOnlyList<FailedAttempt> attempts)
    {
        if (attempts.Count == 0)
        {
            return None;
        }

        var ordered = attempts.OrderBy(a => a.Round).ToList();
        var text = new StringBuilder();
        for (var i = 0; i < ordered.Count; i++)
        {
            var attempt = ordered[i];
            text.Append("- Round ").Append(attempt.Round).Append(" · ").Append(attempt.Signature).Append('\n');
            if (i < ordered.Count - DetailedAttempts)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(attempt.FirstError))
            {
                text.Append("  First error: ").Append(attempt.FirstError).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(attempt.Change))
            {
                text.Append("  Changed code:\n");
                foreach (var line in Lf(attempt.Change).TrimEnd('\n').Split('\n'))
                {
                    text.Append("    ").Append(line).Append('\n');
                }
            }
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>The sentence of <c>repair/failure_summaries.md</c> for a state (a line <c>STATE: sentence</c>).</summary>
    public static string FailureSummary(PromptTemplate summaries, VerificationState state)
    {
        var key = state.ToString().ToUpperInvariant() + ":";
        var line = summaries.Body.Split('\n').FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal))
                   ?? throw new ArrowException(ErrorCodes.B2TemplateInvalid, $"{summaries.Path}: không có câu cho trạng thái {state.ToString().ToUpperInvariant()}.");
        return line[key.Length..].Trim();
    }

    private static Dictionary<string, string> Shared(PromptInput input) => new(StringComparer.Ordinal)
    {
        ["cut_fqn"] = input.CutFqn,
        ["cut_path"] = input.CutPath,
        ["cut_source"] = Lf(input.CutSource).TrimEnd('\n'),
        ["test_package"] = input.TestPackage,
        ["test_class_name"] = input.TestClassName,
        ["repository_configuration"] = Lf(input.MetadataPrompt).TrimEnd('\n'),
    };

    /// <summary>Each template gets the values it has a placeholder for; values are put in as they are (P02 §3.2).</summary>
    private static BuiltPrompt Build(PromptTemplate system, PromptTemplate user, Dictionary<string, string> values)
    {
        string Fill(PromptTemplate template) => PromptTemplateStore.Render(template, values.Where(v => template.Placeholders.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal));
        return new BuiltPrompt(
            [new LlmMessage("system", Fill(system)), new LlmMessage("user", Fill(user))],
            [new TemplateRef(system.Id, system.Version, system.Sha256), new TemplateRef(user.Id, user.Version, user.Sha256)]);
    }

    private static string OrNone(string? value) => string.IsNullOrWhiteSpace(value) ? None : Lf(value).TrimEnd('\n');

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
