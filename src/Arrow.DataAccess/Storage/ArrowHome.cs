using Arrow.Common.Errors;

namespace Arrow.DataAccess.Storage;

/// <summary>
/// The data folder of ARROW and its layout (P09 §2). Every file ARROW keeps on the user's machine lives here,
/// except <c>&lt;project&gt;/.arrow.json</c> and the test files written into the project.
/// </summary>
public sealed class ArrowHome
{
    public ArrowHome(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>Absolute path of the folder.</summary>
    public string Root { get; }

    /// <summary><c>config.json</c>: layer 2 of the configuration (P09 §3).</summary>
    public string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary><c>secrets/</c>: API keys, readable by the owner only (P09 §4).</summary>
    public string SecretsDir => Path.Combine(Root, "secrets");

    /// <summary><c>tools/</c>: downloaded tools and <c>tools.installed.json</c> (P09 §6).</summary>
    public string ToolsDir => Path.Combine(Root, "tools");

    /// <summary><c>mirrors/</c>: shared git mirrors (P01 §3.2).</summary>
    public string MirrorsDir => Path.Combine(Root, "mirrors");

    /// <summary><c>work/</c>: temporary workspaces, one folder per run.</summary>
    public string WorkDir => Path.Combine(Root, "work");

    /// <summary><c>runs/</c>: results, one folder per run (P09 §5).</summary>
    public string RunsDir => Path.Combine(Root, "runs");

    /// <summary><c>logs/</c>: the tool log <c>arrow-&lt;yyyyMMdd&gt;.log</c> (P09 §7).</summary>
    public string LogsDir => Path.Combine(Root, "logs");

    /// <summary><c>history.jsonl</c>: one line per finished run (P09 §5.4).</summary>
    public string HistoryFile => Path.Combine(Root, "history.jsonl");

    /// <summary><c>repl_history</c>: commands of the interactive session (P08 §5.2).</summary>
    public string ReplHistoryFile => Path.Combine(Root, "repl_history");

    /// <summary>
    /// Finds the folder (P09 §2): <c>ARROW_HOME</c>, then <c>%LOCALAPPDATA%\arrow</c> on Windows,
    /// then <c>$XDG_DATA_HOME/arrow</c> or <c>~/.arrow</c> elsewhere. An existing folder that is a link is replaced by its target.
    /// </summary>
    /// <param name="getEnvironment">Reads an environment variable; tests pass their own.</param>
    /// <param name="isWindows">Platform; tests pass their own.</param>
    /// <param name="userProfile">Home folder; tests pass their own.</param>
    public static ArrowHome Resolve(Func<string, string?>? getEnvironment = null, bool? isWindows = null, string? userProfile = null)
    {
        getEnvironment ??= Environment.GetEnvironmentVariable;
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var root = NonEmpty(getEnvironment("ARROW_HOME"))
            ?? (windows
                ? Path.Combine(NonEmpty(getEnvironment("LOCALAPPDATA")) ?? Path.Combine(home, "AppData", "Local"), "arrow")
                : NonEmpty(getEnvironment("XDG_DATA_HOME")) is { } xdg ? Path.Combine(xdg, "arrow") : Path.Combine(home, ".arrow"));

        return new ArrowHome(RealPath(Path.GetFullPath(root)));
    }

    /// <summary>
    /// Checks the condition of P09 §2: no spaces and only ASCII characters, because Maven and Gradle on Windows
    /// break on such paths. Returns the error to report, or null when the path is fine.
    /// </summary>
    public ArrowException? Validate() => IsUsablePath(Root)
        ? null
        : new ArrowException(ErrorCodes.CfgHomePath, args: new Dictionary<string, string> { ["path"] = Root });

    /// <summary>True when <paramref name="path"/> has no whitespace and only ASCII characters.</summary>
    public static bool IsUsablePath(string path) => path.All(c => c is > ' ' and < (char)127);

    /// <summary>Creates the folder and its sub-folders that must exist before a run.</summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, RunsDir, WorkDir, LogsDir, ToolsDir })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string RealPath(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.LinkTarget is not null
            ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path
            : path;
    }
}
