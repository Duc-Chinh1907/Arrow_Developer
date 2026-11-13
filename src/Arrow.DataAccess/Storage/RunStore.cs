using System.Globalization;
using System.Security.Cryptography;
using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Json;
using Arrow.DataAccess.IO;
using Arrow.DataAccess.Security;

namespace Arrow.DataAccess.Storage;

/// <summary>An open run folder <c>ARROW_HOME/runs/&lt;run_id&gt;/</c>.</summary>
public sealed record RunHandle(string RunId, string Root);

/// <summary>History lines read back, and how many lines were damaged and skipped (P09 §5.4).</summary>
public sealed record HistoryContent(IReadOnlyList<HistoryEntry> Entries, int SkippedLines);

/// <summary>Results of the runs (P09 §5). Every text written through it is redacted first (P00 §5).</summary>
public interface IRunStore
{
    /// <summary>A run id that no folder in <c>runs/</c> uses yet (P09 §5.1).</summary>
    string NewUniqueRunId(DateTime localTime);

    /// <summary>Creates <c>runs/&lt;run_id&gt;/</c> and writes <c>run.json</c>. <see cref="RunInfo.RunId"/> must be unused.</summary>
    RunHandle Create(RunInfo info);

    /// <summary>Opens a run by its id or by a prefix that matches exactly one run.</summary>
    RunHandle Open(string runIdOrPrefix);

    /// <summary>The newest <paramref name="limit"/> runs with a readable <c>run.json</c>, newest first.</summary>
    IReadOnlyList<(RunHandle Handle, RunInfo Info)> Recent(int limit);

    /// <summary>Rewrites <c>run.json</c> atomically.</summary>
    void Update(RunHandle run, RunInfo info);

    /// <summary>The newest <paramref name="limit"/> lines of <c>history.jsonl</c>, newest first.</summary>
    HistoryContent History(int limit);

    /// <summary>Appends a line to <c>history.jsonl</c> when a run ends.</summary>
    void AppendHistory(HistoryEntry entry);

    /// <summary>Appends a progress event to <c>events.jsonl</c>.</summary>
    void AppendEvent(RunHandle run, ArrowEvent value);

    /// <summary>Writes a JSON file below the run folder.</summary>
    void WriteJson<T>(RunHandle run, string relativePath, T value);

    /// <summary>Reads a JSON file below the run folder; null when it does not exist.</summary>
    T? ReadJson<T>(RunHandle run, string relativePath);

    /// <summary>Writes a text file below the run folder.</summary>
    void WriteText(RunHandle run, string relativePath, string text);

    /// <summary>Reads a text file below the run folder; null when it does not exist.</summary>
    string? ReadText(RunHandle run, string relativePath);

    /// <summary>Deletes a file or folder below the run folder, e.g. <c>targets/&lt;cut&gt;/block3</c> before a resume.</summary>
    void Delete(RunHandle run, string relativePath);

    /// <summary>True when the file or folder exists below the run folder.</summary>
    bool Exists(RunHandle run, string relativePath);
}

/// <summary>File implementation of <see cref="IRunStore"/> under <see cref="ArrowHome.RunsDir"/>.</summary>
public sealed class RunStore : IRunStore
{
    public const string RunFile = "run.json";
    public const string EventsFile = "events.jsonl";

    private const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly object CreateLock = new();

    private readonly ArrowHome _home;

    public RunStore(ArrowHome home)
    {
        _home = home;
    }

    /// <summary>A new run id, <c>&lt;yyyyMMdd-HHmmss&gt;-&lt;4 random [a-z0-9]&gt;</c> in local time (P09 §5.1).</summary>
    public static string NewRunId(DateTime localTime) =>
        localTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
        string.Create(4, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)];
            }
        });

    /// <summary>A run id that no folder in <c>runs/</c> uses yet.</summary>
    public string NewUniqueRunId(DateTime localTime)
    {
        lock (CreateLock)
        {
            string id;
            do
            {
                id = NewRunId(localTime);
            }
            while (Directory.Exists(Path.Combine(_home.RunsDir, id)));

            return id;
        }
    }

    public RunHandle Create(RunInfo info)
    {
        var root = Path.Combine(_home.RunsDir, info.RunId);
        lock (CreateLock)
        {
            if (Directory.Exists(root))
            {
                throw new InvalidOperationException($"Run {info.RunId} already exists");
            }

            Directory.CreateDirectory(root);
        }

        var run = new RunHandle(info.RunId, root);
        Update(run, info);
        return run;
    }

    public RunHandle Open(string runIdOrPrefix)
    {
        var args = new Dictionary<string, string> { ["run_id"] = runIdOrPrefix };
        if (string.IsNullOrWhiteSpace(runIdOrPrefix) || runIdOrPrefix.IndexOfAny(['/', '\\', '.']) >= 0 || !Directory.Exists(_home.RunsDir))
        {
            throw new ArrowException(ErrorCodes.GenResumeNotFound, args: args);
        }

        var matches = Directory.GetDirectories(_home.RunsDir)
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith(runIdOrPrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        return matches.Count switch
        {
            0 => throw new ArrowException(ErrorCodes.GenResumeNotFound, args: args),
            1 => new RunHandle(matches[0]!, Path.Combine(_home.RunsDir, matches[0]!)),
            _ => matches.Contains(runIdOrPrefix)
                ? new RunHandle(runIdOrPrefix, Path.Combine(_home.RunsDir, runIdOrPrefix))
                : throw new ArrowException(ErrorCodes.CliArgs, $"Tiền tố {runIdOrPrefix} khớp {matches.Count} run: {string.Join(", ", matches.Take(5))}",
                    new Dictionary<string, string> { ["help_command"] = "arrow history" }),
        };
    }

    public IReadOnlyList<(RunHandle Handle, RunInfo Info)> Recent(int limit)
    {
        if (!Directory.Exists(_home.RunsDir))
        {
            return [];
        }

        var list = new List<(RunHandle, RunInfo)>();
        foreach (var dir in Directory.GetDirectories(_home.RunsDir).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            var handle = new RunHandle(Path.GetFileName(dir), dir);
            try
            {
                if (ReadJson<RunInfo>(handle, RunFile) is { } info)
                {
                    list.Add((handle, info));
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A damaged run.json is skipped.
            }

            if (list.Count >= limit)
            {
                break;
            }
        }

        return list;
    }

    public void Update(RunHandle run, RunInfo info) => WriteJson(run, RunFile, info);

    public HistoryContent History(int limit)
    {
        var content = JsonlFile.ReadAll<HistoryEntry>(_home.HistoryFile);
        return new HistoryContent(content.Items.Reverse().Take(limit).ToList(), content.SkippedLines);
    }

    public void AppendHistory(HistoryEntry entry) => JsonlFile.Append(_home.HistoryFile, entry, Redactor.Redact);

    public void AppendEvent(RunHandle run, ArrowEvent value) => JsonlFile.Append(Resolve(run, EventsFile), value, Redactor.Redact);

    public void WriteJson<T>(RunHandle run, string relativePath, T value) =>
        AtomicFile.WriteAllText(Resolve(run, relativePath), Redactor.Redact(ArrowJson.Serialize(value)) + "\n");

    public T? ReadJson<T>(RunHandle run, string relativePath)
    {
        var text = ReadText(run, relativePath);
        return text is null ? default : ArrowJson.Deserialize<T>(text);
    }

    public void WriteText(RunHandle run, string relativePath, string text) =>
        AtomicFile.WriteAllText(Resolve(run, relativePath), Redactor.Redact(text));

    public string? ReadText(RunHandle run, string relativePath)
    {
        var path = Resolve(run, relativePath);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Delete(RunHandle run, string relativePath)
    {
        var path = Resolve(run, relativePath);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public bool Exists(RunHandle run, string relativePath)
    {
        var path = Resolve(run, relativePath);
        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>
    /// Resolves a path below the run folder. Absolute paths and paths that leave the folder (<c>..</c>) are refused,
    /// so a name that comes from a model answer or a class name can never write elsewhere.
    /// </summary>
    internal static string Resolve(RunHandle run, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException($"Path must be relative to the run folder: {relativePath}", nameof(relativePath));
        }

        var root = Path.GetFullPath(run.Root);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new ArgumentException($"Path leaves the run folder: {relativePath}", nameof(relativePath));
        }

        return full;
    }
}
