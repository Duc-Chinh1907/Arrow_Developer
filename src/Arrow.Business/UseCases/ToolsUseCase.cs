using Arrow.Common.Errors;
using Arrow.DataAccess.Tools;

namespace Arrow.Business.UseCases;

/// <summary>A row of <c>arrow tools list</c>.</summary>
/// <param name="Id">Tool id.</param>
/// <param name="Source">Maven coordinates or URL.</param>
/// <param name="Path">Where the file is, or would be.</param>
/// <param name="Installed">Present with the locked sha256.</param>
/// <param name="Problem">Why it is not installed; null when it is.</param>
public sealed record ToolRow(string Id, string Source, string Path, bool Installed, string? Problem);

/// <summary>What <c>arrow tools install</c> did for one tool.</summary>
/// <param name="Id">Tool id.</param>
/// <param name="Path">Path of the file; null on failure.</param>
/// <param name="Error">Why it failed; null on success.</param>
public sealed record ToolInstallResult(string Id, string? Path, ErrorInfo? Error);

/// <summary><c>arrow tools list|install</c> (P09 §6).</summary>
public interface IToolsUseCase
{
    /// <summary>Every downloadable tool and its state.</summary>
    IReadOnlyList<ToolRow> List();

    /// <summary>Installs <paramref name="ids"/> (all when empty); one failure does not stop the others.</summary>
    Task<IReadOnlyList<ToolInstallResult>> InstallAsync(IReadOnlyList<string> ids, IProgress<string>? progress, CancellationToken ct);
}

/// <summary><see cref="IToolsUseCase"/> on <see cref="IToolStore"/>.</summary>
public sealed class ToolsUseCase(IToolStore store, ErrorCatalog catalog) : IToolsUseCase
{
    public IReadOnlyList<ToolRow> List() =>
        store.List().Select(s => new ToolRow(s.Entry.Id, s.Entry.Maven ?? s.Entry.Url ?? "", s.Path, s.Installed, s.Problem)).ToList();

    public async Task<IReadOnlyList<ToolInstallResult>> InstallAsync(IReadOnlyList<string> ids, IProgress<string>? progress, CancellationToken ct)
    {
        var known = store.List().Select(s => s.Entry.Id).ToList();
        var unknown = ids.Except(known, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ArrowException(ErrorCodes.CliArgs, $"Không có công cụ: {string.Join(", ", unknown)}. Có: {string.Join(", ", known)}.");
        }

        var results = new List<ToolInstallResult>();
        foreach (var id in ids.Count == 0 ? known : ids)
        {
            try
            {
                results.Add(new ToolInstallResult(id, await store.EnsureAsync(id, progress, ct).ConfigureAwait(false), null));
            }
            catch (ArrowException ex)
            {
                results.Add(new ToolInstallResult(id, null, catalog.Describe(ex)));
            }
        }

        return results;
    }
}
