using Arrow.Business.UseCases;
using Spectre.Console;

namespace Arrow.Cli.Repl;

/// <summary>
/// The class picker of <c>/gen</c> (P08 §5.3): type to filter by simple name or part of the FQN, space to select several, Enter to
/// run. Classes that already have a test are marked <c>(đã có test)</c>.
/// </summary>
internal sealed class SpectreClassPicker : IClassPicker
{
    private const string HasTest = "  (đã có test)";

    public IReadOnlyList<string>? Pick(IReadOnlyList<SessionClass> classes)
    {
        if (classes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]![/] Project không có class nào để sinh test.");
            return null;
        }

        // Spectre 0.57 has no search box in a multi-select list: the filter is asked first (P08 §5.3 "gõ để lọc").
        var filter = AnsiConsole.Prompt(new TextPrompt<string>("Lọc class theo tên (Enter để xem tất cả):").AllowEmpty()).Trim();
        var shown = Filter(classes, filter);
        if (shown.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]![/] Không có class nào khớp {Markup.Escape(filter)}.");
            return null;
        }

        var prompt = new MultiSelectionPrompt<string>()
            .Title($"Chọn class để sinh test ({shown.Count} class)")
            .PageSize(15)
            .NotRequired()
            .MoreChoicesText("[grey](lên/xuống để xem thêm)[/]")
            .InstructionsText("[grey](phím cách để chọn · Enter để chạy)[/]")
            .AddChoices(shown.Select(c => c.Fqn + (c.HasTest ? HasTest : "")));
        var chosen = AnsiConsole.Prompt(prompt);
        return chosen.Select(c => c.EndsWith(HasTest, StringComparison.Ordinal) ? c[..^HasTest.Length] : c).ToList();
    }

    /// <summary>Classes whose simple name or FQN contains <paramref name="filter"/>, ignoring case.</summary>
    public static IReadOnlyList<SessionClass> Filter(IReadOnlyList<SessionClass> classes, string filter) =>
        filter.Length == 0 ? classes : classes.Where(c => c.Fqn.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
}
