using Arrow.Business.Analysis;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;

namespace Arrow.Business.UseCases;

/// <summary>The class a query names, or why there is none (P01 §3.3).</summary>
/// <param name="Cut">The class; null when blocked.</param>
/// <param name="Warning">Warning of the locator (e.g. an abstract class).</param>
/// <param name="Blocked">The BLOCKED outcome when no class can be used.</param>
/// <param name="Suggestions">Close names, for <c>B1_CUT_MISSING</c>.</param>
public sealed record PickedCut(ClassCandidate? Cut, string? Warning, BlockOutcome? Blocked, IReadOnlyList<string> Suggestions);

/// <summary>Finds the CUT of a query for <c>inspect</c> and <c>gen</c>; several matches are asked through <see cref="IConfirmation"/>.</summary>
public static class CutPicker
{
    /// <summary>The class of <paramref name="query"/> in <paramref name="workspace"/>.</summary>
    public static async Task<PickedCut> PickAsync(IProjectAnalyzer analyzer, ProjectWorkspace workspace, string query, ErrorCatalog catalog, IConfirmation confirm, CancellationToken ct)
    {
        query = query.Trim();
        var args = new Dictionary<string, string> { ["query"] = query };
        var located = await analyzer.LocateAsync(workspace, query, ct).ConfigureAwait(false);
        if (located is LocateResult.Ambiguous ambiguous)
        {
            var chosen = await confirm.ChooseOneAsync(
                $"Có {ambiguous.Candidates.Count} class tên {query}. Chọn một class:",
                ambiguous.Candidates.Select(c => $"{c.Fqn}  ({c.Path})").ToList(),
                ct).ConfigureAwait(false);
            if (chosen is not { } index)
            {
                return Blocked(catalog, ErrorCodes.B1CutAmbiguous, "Có nhiều class tên " + query + ": " + string.Join(", ", ambiguous.Candidates.Select(c => c.Fqn)), [], args);
            }

            located = ClassLocator.Check(ambiguous.Candidates[index]);
        }

        return located switch
        {
            LocateResult.Found found => new PickedCut(found.Class, found.Warning, null, []),
            LocateResult.Failed failed => Blocked(catalog, failed.Code, failed.Detail, failed.Suggestions, args),
            _ => throw new InvalidOperationException("Unexpected locate result " + located.GetType().Name),
        };
    }

    private static PickedCut Blocked(ErrorCatalog catalog, string code, string detail, IReadOnlyList<string> suggestions, IReadOnlyDictionary<string, string> args)
    {
        var info = catalog.Describe(code, detail, args);
        return new PickedCut(null, null, new BlockOutcome(BlockOutcomeKind.Blocked, code, info.Message, info.Hint, null, null, 0), suggestions);
    }
}
