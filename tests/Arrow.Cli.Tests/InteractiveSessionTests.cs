using Arrow.Business.UseCases;
using Arrow.Cli.Rendering;
using Arrow.Cli.Repl;
using Arrow.Common.Errors;

namespace Arrow.Cli.Tests;

public sealed class InteractiveSessionTests
{
    private sealed class ScriptedKeys(params ConsoleKeyInfo[] keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public ConsoleKeyInfo ReadKey() => _keys.Count > 0 ? _keys.Dequeue() : Key(ConsoleKey.D, '\u0004', control: true);
    }

    private sealed class FakeSession(SessionProject? project, IReadOnlyList<SessionClass>? classes = null) : ISessionUseCase
    {
        public int Listed { get; private set; }

        public string HistoryFile => "";

        public SessionProject? FindProject(string directory) => directory.Contains("other", StringComparison.Ordinal)
            ? new SessionProject(directory, "other", "Gradle", "17", "JUnit 5")
            : project;

        public SessionModel Model(string? projectRoot) => new("main", "api.example.com", 5, ["main", "small"]);

        public Task<IReadOnlyList<SessionClass>> ListClassesAsync(string projectRoot, CancellationToken ct)
        {
            Listed++;
            return classes is null ? throw new ArrowException("B1_HELPER_FAILED") : Task.FromResult(classes);
        }
    }

    private sealed class FakePicker(IReadOnlyList<string>? chosen) : IClassPicker
    {
        public IReadOnlyList<SessionClass>? Offered { get; private set; }

        public IReadOnlyList<string>? Pick(IReadOnlyList<SessionClass> classes)
        {
            Offered = classes;
            return chosen;
        }
    }

    private static readonly SessionProject Shop = new(Path.Combine(Path.GetTempPath(), "shop"), "shop", "Maven", "11", "JUnit 4");

    private static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0', bool control = false) => new(c, key, false, false, control);

    private static ConsoleKeyInfo[] Type(string text) =>
        [.. text.Select(c => Key(c == ' ' ? ConsoleKey.Spacebar : ConsoleKey.A, c)), Key(ConsoleKey.Enter, '\r')];

    private static (InteractiveSession Session, List<string[]> Runs, StringWriter Output) Create(
        FakeSession fake, IClassPicker? picker = null, ConsoleKeyInfo[]? keys = null, Func<DateTimeOffset>? now = null,
        Func<string[], CancellationToken, Task<int>>? run = null)
    {
        var output = new StringWriter();
        var runs = new List<string[]>();
        var editor = new LineEditor(new ScriptedKeys(keys ?? []), output, null, _ => []);
        var session = new InteractiveSession(fake, editor, new Renderer(RenderMode.Plain, output, output), picker ?? new FakePicker(null),
            run ?? ((args, _) =>
            {
                runs.Add(args);
                return Task.FromResult(0);
            }),
            Shop.Root, "0.1.0", now);
        return (session, runs, output);
    }

    [Fact]
    public async Task Banner_shows_project_and_model_then_exits_on_ctrl_d()
    {
        var (session, _, output) = Create(new FakeSession(Shop));

        Assert.Equal(ExitCodes.Ok, await session.RunAsync(TestContext.Current.CancellationToken));

        var text = output.ToString();
        Assert.Contains("shop  (Maven · Java 11 · JUnit 4)", text, StringComparison.Ordinal);
        Assert.Contains("main · api.example.com · repair adaptive N=5", text, StringComparison.Ordinal);
        Assert.Contains("Tạm biệt.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Banner_without_project_says_how_to_choose_one()
    {
        var (session, _, output) = Create(new FakeSession(null));

        await session.RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("chưa chọn project · dùng /project <thư mục>", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typed_lines_run_commands_until_exit()
    {
        var keys = new[] { Type("/doctor"), Type("history --limit 3"), Type("/exit"), Type("/doctor") }.SelectMany(k => k).ToArray();
        var (session, runs, _) = Create(new FakeSession(Shop), keys: keys);

        await session.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal([["doctor"], ["history", "--limit", "3"]], runs);
    }

    [Fact]
    public async Task Gen_with_a_class_adds_the_project_of_the_session()
    {
        var (session, runs, _) = Create(new FakeSession(Shop));

        await session.HandleAsync("/gen OrderService --no-metrics", TestContext.Current.CancellationToken);
        await session.HandleAsync("/gen Cart --project /elsewhere", TestContext.Current.CancellationToken);
        await session.HandleAsync("/inspect Cart", TestContext.Current.CancellationToken);

        Assert.Equal(["gen", "OrderService", "--no-metrics", "--project", Shop.Root], runs[0]);
        Assert.Equal(["gen", "Cart", "--project", "/elsewhere"], runs[1]);
        Assert.Equal(["inspect", "Cart", "--project", Shop.Root], runs[2]);
    }

    [Fact]
    public async Task Model_override_is_passed_to_gen_unless_the_line_has_one()
    {
        var (session, runs, output) = Create(new FakeSession(Shop));

        await session.HandleAsync("/model missing", TestContext.Current.CancellationToken);
        Assert.Null(session.Model);
        await session.HandleAsync("/model small", TestContext.Current.CancellationToken);
        await session.HandleAsync("/gen A", TestContext.Current.CancellationToken);
        await session.HandleAsync("/gen B --model main", TestContext.Current.CancellationToken);

        Assert.Equal("small", session.Model);
        Assert.Contains("Không có model missing", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(["gen", "A", "--project", Shop.Root, "--model", "small"], runs[0]);
        Assert.Equal(["gen", "B", "--model", "main", "--project", Shop.Root], runs[1]);
    }

    [Fact]
    public async Task Gen_without_a_class_opens_the_picker_once_per_project()
    {
        var classes = new List<SessionClass> { new("com.shop.Cart", true), new("com.shop.Order", false) };
        var fake = new FakeSession(Shop, classes);
        var picker = new FakePicker(["com.shop.Order"]);
        var (session, runs, _) = Create(fake, picker);

        await session.HandleAsync("/gen --no-metrics", TestContext.Current.CancellationToken);
        await session.HandleAsync("/gen", TestContext.Current.CancellationToken);

        Assert.Same(classes, picker.Offered);
        Assert.Equal(1, fake.Listed);
        Assert.Equal(["gen", "com.shop.Order", "--no-metrics", "--project", Shop.Root], runs[0]);
        Assert.Contains("Cart", session.Complete("/gen "));
        Assert.Contains("--no-metrics", session.Complete("/gen "));

        await session.HandleAsync("/project other", TestContext.Current.CancellationToken);
        await session.HandleAsync("/gen", TestContext.Current.CancellationToken);
        Assert.Equal(2, fake.Listed);
        Assert.Equal("other", session.Project?.Name);
    }

    [Fact]
    public async Task Gen_without_a_class_and_no_choice_runs_nothing()
    {
        var (session, runs, output) = Create(new FakeSession(Shop, []), new FakePicker([]));

        await session.HandleAsync("/gen", TestContext.Current.CancellationToken);

        Assert.Empty(runs);
        Assert.Contains("Chưa chọn class nào.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gen_without_project_or_listing_failure_warns()
    {
        var (noProject, runs, output) = Create(new FakeSession(null));
        await noProject.HandleAsync("/gen A", TestContext.Current.CancellationToken);
        Assert.Empty(runs);
        Assert.Contains("Chưa chọn project", output.ToString(), StringComparison.Ordinal);

        var (failing, _, failed) = Create(new FakeSession(Shop, null));
        await failing.HandleAsync("/gen", TestContext.Current.CancellationToken);
        Assert.Contains("Không liệt kê được class (B1_HELPER_FAILED)", failed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_command_warns_and_the_session_goes_on()
    {
        var (session, runs, output) = Create(new FakeSession(Shop));

        Assert.True(await session.HandleAsync("/nope", TestContext.Current.CancellationToken));
        Assert.False(await session.HandleAsync("/quit", TestContext.Current.CancellationToken));

        Assert.Empty(runs);
        Assert.Contains("Không có lệnh /nope", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interrupt_cancels_the_running_command_and_twice_leaves()
    {
        var time = DateTimeOffset.UnixEpoch;
        InteractiveSession? session = null;
        var interrupted = new List<bool>();
        var (created, _, _) = Create(new FakeSession(Shop), now: () => time, run: async (_, ct) =>
        {
            interrupted.Add(session!.Interrupt());
            Assert.True(ct.IsCancellationRequested);
            return await Task.FromResult(3);
        });
        session = created;

        Assert.False(session.Interrupt());
        time += TimeSpan.FromSeconds(10);
        await session.HandleAsync("/doctor", TestContext.Current.CancellationToken);
        Assert.Equal([true], interrupted);

        time += TimeSpan.FromSeconds(1);
        Assert.True(session.Interrupt());
    }

    [Fact]
    public void Split_keeps_quoted_spaces()
    {
        Assert.Equal(["/project", "C:/My Projects/shop", "x"], InteractiveSession.Split("  /project \"C:/My Projects/shop\"  'x'"));
        Assert.Empty(InteractiveSession.Split("   "));
    }

    [Fact]
    public void Complete_offers_commands_for_the_first_word()
    {
        var (session, _, _) = Create(new FakeSession(Shop));

        Assert.Contains("/gen", session.Complete("/g"));
        Assert.Empty(session.Complete("/doctor "));
    }
}

public sealed class LineEditorTests
{
    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public ConsoleKeyInfo ReadKey() => _keys.Dequeue();
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0', bool control = false) => new(c, key, false, false, control);

    private static IEnumerable<ConsoleKeyInfo> Chars(string text) => text.Select(c => Key(ConsoleKey.A, c));

    private static ConsoleKeyInfo Enter => Key(ConsoleKey.Enter, '\r');

    [Fact]
    public void Typing_backspace_and_escape_edit_the_line()
    {
        var keys = new ScriptedKeys([.. Chars("gem"), Key(ConsoleKey.Backspace, '\b'), .. Chars("n"), Enter,
            .. Chars("junk"), Key(ConsoleKey.Escape, '\u001b'), .. Chars("ok"), Enter]);
        var editor = new LineEditor(keys, new StringWriter(), null, _ => []);

        Assert.Equal((LineEnd.Enter, "gen"), editor.ReadLine("> "));
        Assert.Equal((LineEnd.Enter, "ok"), editor.ReadLine("> "));
    }

    [Fact]
    public void Ctrl_c_clears_a_line_and_leaves_on_an_empty_one()
    {
        var keys = new ScriptedKeys([.. Chars("abc"), Key(ConsoleKey.C, '\u0003', true), Key(ConsoleKey.C, '\u0003', true)]);
        var editor = new LineEditor(keys, new StringWriter(), null, _ => []);

        Assert.Equal(LineEnd.Exit, editor.ReadLine("> ").End);
    }

    [Fact]
    public void History_is_browsed_saved_and_capped()
    {
        var file = Path.Combine(Path.GetTempPath(), "arrow-repl-" + Guid.NewGuid().ToString("N"), "repl_history");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllLines(file, Enumerable.Range(0, LineEditor.MaxHistory + 20).Select(i => "old " + i));
            var keys = new ScriptedKeys([.. Chars("/doctor"), Enter, .. Chars("/doctor"), Enter,
                Key(ConsoleKey.UpArrow), Key(ConsoleKey.UpArrow), Key(ConsoleKey.DownArrow), Enter]);
            var editor = new LineEditor(keys, new StringWriter(), file, _ => []);
            Assert.Equal("old 519", editor.History[^1]);

            editor.ReadLine("> ");
            editor.ReadLine("> ");
            Assert.Equal((LineEnd.Enter, "/doctor"), editor.ReadLine("> "));

            var saved = File.ReadAllLines(file);
            Assert.Equal(LineEditor.MaxHistory, saved.Length);
            Assert.Equal("/doctor", saved[^1]);
            Assert.Equal("old 519", saved[^2]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }

    [Fact]
    public void Tab_completes_one_candidate_and_lists_several()
    {
        var output = new StringWriter();
        var keys = new ScriptedKeys([.. Chars("/ge"), Key(ConsoleKey.Tab, '\t'), Enter,
            .. Chars("/gen Or"), Key(ConsoleKey.Tab, '\t'), Enter]);
        var editor = new LineEditor(keys, output, null, line => line.StartsWith("/gen ", StringComparison.Ordinal)
            ? ["OrderService", "OrderLine", "Cart"]
            : ["/gen", "/help"]);

        Assert.Equal("/gen", editor.ReadLine("> ").Text);
        Assert.Equal("/gen Order", editor.ReadLine("> ").Text);
        Assert.Contains("OrderService  OrderLine", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Slash_on_an_empty_line_lists_the_commands()
    {
        var output = new StringWriter();
        var keys = new ScriptedKeys([.. Chars("/doctor"), Enter]);
        var editor = new LineEditor(keys, output, null, _ => ["/gen", "/doctor"]);

        Assert.Equal("/doctor", editor.ReadLine("> ").Text);
        Assert.Contains("/gen  /doctor", output.ToString(), StringComparison.Ordinal);
    }
}
