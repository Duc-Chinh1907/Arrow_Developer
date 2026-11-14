using System.Text;
using Arrow.Business.UseCases;
using Arrow.Cli.Rendering;
using Arrow.Common.Errors;
using Spectre.Console;

namespace Arrow.Cli.Repl;

/// <summary>Lets the user pick classes of the project for <c>/gen</c> (P08 §5.3); null when nothing was chosen.</summary>
internal interface IClassPicker
{
    /// <summary>The chosen FQNs.</summary>
    IReadOnlyList<string>? Pick(IReadOnlyList<SessionClass> classes);
}

/// <summary>
/// The interactive session <c>arrow</c> (P08 §5): a banner with the project and the model, then a prompt. Slash commands map to
/// the commands of the CLI, with <c>--project</c> set to the project of the session; a line without <c>/</c> is an
/// <c>arrow</c> command line. Ctrl+C cancels the command that is running and comes back to the prompt; twice within 2 s, or on an
/// empty prompt, it leaves the session.
/// </summary>
internal sealed class InteractiveSession(
    ISessionUseCase session,
    LineEditor editor,
    IRenderer renderer,
    IClassPicker picker,
    Func<string[], CancellationToken, Task<int>> run,
    string startDirectory,
    string version,
    Func<DateTimeOffset>? now = null)
{
    /// <summary>The slash commands, for <c>/help</c> and Tab.</summary>
    public static readonly IReadOnlyList<(string Name, string Help)> Commands =
    [
        ("/gen", "Sinh test: /gen [class…] [tuỳ chọn]; không có class thì mở ô chọn class"),
        ("/inspect", "Phân tích project và một class: /inspect <class>"),
        ("/history", "Các run đã chạy"),
        ("/show", "Chi tiết một run: /show <run> [class]"),
        ("/model", "Xem model; /model <tên> đổi model cho phiên này"),
        ("/project", "Đổi project của phiên: /project <thư mục>"),
        ("/setup", "Thiết lập model và khoá"),
        ("/doctor", "Kiểm tra máy"),
        ("/config", "Xem hoặc đổi cấu hình: /config list | get | set"),
        ("/tools", "Công cụ đo: /tools list | install"),
        ("/help", "Danh sách lệnh"),
        ("/exit", "Thoát (hoặc Ctrl+D)"),
    ];

    private static readonly string[] GenOptions =
        ["--package", "--model", "--rounds", "--no-repair", "--no-metrics", "--dry-run", "--jobs", "--max-tokens", "--resume", "--yes"];

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private CancellationTokenSource? _running;
    private DateTimeOffset _lastInterrupt = DateTimeOffset.MinValue;
    private bool _leave;
    private SessionProject? _project = session.FindProject(startDirectory);
    private string? _model;
    private IReadOnlyList<SessionClass>? _classes;

    /// <summary>The project of the session; null until one is found or chosen.</summary>
    public SessionProject? Project => _project;

    /// <summary>The model chosen with <c>/model</c> for this session; null for the default.</summary>
    public string? Model => _model;

    /// <summary>
    /// Ctrl+C from the console while a command runs: cancels it. A second Ctrl+C within 2 s also leaves the session. Returns false
    /// when no command runs, so that the caller handles the key.
    /// </summary>
    public bool Interrupt()
    {
        lock (_gate)
        {
            var time = _now();
            if (time - _lastInterrupt <= TimeSpan.FromSeconds(2))
            {
                _leave = true;
            }

            _lastInterrupt = time;
            if (_running is null)
            {
                return _leave;
            }

            _running.Cancel();
            return true;
        }
    }

    /// <summary>Runs the session until <c>/exit</c>, Ctrl+D, or Ctrl+C on an empty prompt.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        Banner();
        while (!_leave && !ct.IsCancellationRequested)
        {
            var (end, line) = editor.ReadLine("arrow> ");
            if (end == LineEnd.Exit)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (!await HandleAsync(line, ct).ConfigureAwait(false))
            {
                break;
            }
        }

        renderer.Line("Tạm biệt.");
        return ExitCodes.Ok;
    }

    /// <summary>Handles one line; false to leave the session.</summary>
    public async Task<bool> HandleAsync(string line, CancellationToken ct)
    {
        var words = Split(line);
        if (words.Count == 0)
        {
            return true;
        }

        if (!words[0].StartsWith('/'))
        {
            await RunAsync([.. words], ct).ConfigureAwait(false);
            return true;
        }

        var rest = words.Skip(1).ToList();
        switch (words[0])
        {
            case "/exit":
            case "/quit":
                return false;
            case "/":
            case "/help":
                Help();
                return true;
            case "/project":
                ChangeProject(rest);
                return true;
            case "/model":
                ChangeModel(rest);
                return true;
            case "/gen":
                await GenAsync(rest, ct).ConfigureAwait(false);
                return true;
            case "/inspect":
                await RunAsync(["inspect", .. rest, .. ProjectOption(rest)], ct).ConfigureAwait(false);
                return true;
            case "/history":
            case "/show":
            case "/setup":
            case "/doctor":
            case "/config":
            case "/tools":
                await RunAsync([words[0][1..], .. rest], ct).ConfigureAwait(false);
                return true;
            default:
                renderer.Warn($"Không có lệnh {words[0]}. Gõ /help để xem lệnh.");
                return true;
        }
    }

    /// <summary>Candidates for Tab: commands, the options of <c>/gen</c>, and the simple names of the classes once listed.</summary>
    public IReadOnlyList<string> Complete(string line)
    {
        var words = Split(line);
        if (words.Count == 0 || (words.Count == 1 && !line.EndsWith(' ')))
        {
            return Commands.Select(c => c.Name).ToList();
        }

        if (words[0] is "/gen" or "gen" or "/inspect" or "inspect")
        {
            var classes = _classes?.Select(c => c.Fqn[(c.Fqn.LastIndexOf('.') + 1)..]) ?? [];
            return [.. GenOptions, .. classes];
        }

        return [];
    }

    private void Banner()
    {
        var model = session.Model(_project?.Root);
        var lines = new[]
        {
            "project  " + (_project is { } p
                ? $"{p.Name}  ({string.Join(" · ", new[] { p.BuildTool, p.Java is null ? null : "Java " + p.Java, p.Framework }.OfType<string>())})"
                : "chưa chọn project · dùng /project <thư mục>"),
            "model    " + (model.Name is { } name ? $"{_model ?? name} · {model.Host} · repair adaptive N={model.Rounds}" : "chưa có model · dùng /setup"),
        };
        renderer.Line($" ARROW · LLM unit test generation for Java · v{version}");
        if (renderer.Ansi is { } ansi)
        {
            ansi.Write(new Panel(new Text(string.Join('\n', lines))).RoundedBorder().Padding(1, 0));
        }
        else
        {
            foreach (var line in lines)
            {
                renderer.Line(" " + line);
            }
        }

        renderer.Line(" Gõ /help để xem lệnh · Tab để gợi ý · Ctrl+C để huỷ việc đang chạy");
    }

    private void Help()
    {
        foreach (var (name, help) in Commands)
        {
            renderer.Line($"  {name,-10} {help}");
        }

        renderer.Line("  Gõ không có / ở đầu thì chạy như lệnh arrow, ví dụ: gen OrderService --no-metrics");
    }

    private void ChangeProject(IReadOnlyList<string> rest)
    {
        if (rest.Count == 0)
        {
            renderer.Line(_project is { } p ? $"Project: {p.Root}" : "Chưa chọn project.");
            return;
        }

        var found = session.FindProject(Path.GetFullPath(rest[0], _project?.Root ?? startDirectory));
        if (found is null)
        {
            renderer.Warn($"Không thấy file build (pom.xml, build.gradle) ở {rest[0]} hay thư mục cha.");
            return;
        }

        (_project, _classes) = (found, null);
        renderer.Ok($"Project: {found.Name} ({found.Root})");
    }

    private void ChangeModel(IReadOnlyList<string> rest)
    {
        var model = session.Model(_project?.Root);
        if (rest.Count == 0)
        {
            foreach (var name in model.Models)
            {
                renderer.Line($"  {(name == (_model ?? model.Name) ? "●" : " ")} {name}");
            }

            if (model.Models.Count == 0)
            {
                renderer.Line("Chưa có model. Chạy /setup");
            }

            return;
        }

        if (!model.Models.Contains(rest[0], StringComparer.Ordinal))
        {
            renderer.Warn($"Không có model {rest[0]}. Có: {string.Join(", ", model.Models)}");
            return;
        }

        _model = rest[0];
        renderer.Ok($"Phiên này dùng model {_model}.");
    }

    private async Task GenAsync(List<string> rest, CancellationToken ct)
    {
        if (_project is null)
        {
            renderer.Warn("Chưa chọn project. Dùng /project <thư mục>.");
            return;
        }

        var hasTarget = rest.Any(w => !w.StartsWith('-')) || rest.Contains("--package") || rest.Contains("--resume");
        if (!hasTarget)
        {
            try
            {
                renderer.Line("Đang liệt kê class của project…");
                _classes ??= await session.ListClassesAsync(_project.Root, ct).ConfigureAwait(false);
            }
            catch (ArrowException ex)
            {
                renderer.Warn($"Không liệt kê được class ({ex.Code}). Gõ tên class: /gen <class>");
                return;
            }

            if (picker.Pick(_classes) is not { Count: > 0 } chosen)
            {
                renderer.Line("Chưa chọn class nào.");
                return;
            }

            rest.InsertRange(0, chosen);
        }

        var model = _model is not null && !rest.Contains("--model") ? new[] { "--model", _model } : [];
        await RunAsync(["gen", .. rest, .. ProjectOption(rest), .. model], ct).ConfigureAwait(false);
    }

    private IEnumerable<string> ProjectOption(IReadOnlyList<string> rest) =>
        _project is { } p && !rest.Contains("--project") && !rest.Contains("--repo") ? ["--project", p.Root] : [];

    /// <summary>Runs a command of the CLI; Ctrl+C cancels only it.</summary>
    private async Task RunAsync(string[] args, CancellationToken ct)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _running = running;
        }

        try
        {
            await run(args, running.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
            }
        }
    }

    /// <summary>Splits a line into words; quotes keep spaces.</summary>
    public static List<string> Split(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quote = '\0';
        foreach (var c in line)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (word.Length > 0)
                {
                    words.Add(word.ToString());
                    word.Clear();
                }
            }
            else
            {
                word.Append(c);
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }
}
