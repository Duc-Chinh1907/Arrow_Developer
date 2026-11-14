using Arrow.Cli.Rendering;
using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Spectre.Console;

namespace Arrow.Cli.Tests;

/// <summary>The progress lines of <c>arrow gen</c> (P08 §3.2) and the bounded queue of P07 §10.</summary>
public class GenEventPrinterTests
{
    /// <summary>Records what is printed; can hold the printer back to fill its queue.</summary>
    private sealed class RecordingRenderer(RenderMode mode = RenderMode.Plain) : IRenderer
    {
        public List<string> Lines { get; } = [];

        public SemaphoreSlim? Gate { get; init; }

        public RenderMode Mode => mode;

        public IAnsiConsole? Ansi => null;

        private void Add(string text)
        {
            Gate?.Wait();
            lock (Lines)
            {
                Lines.Add(text);
            }
        }

        public void Line(string text) => Add(text);

        public void Ok(string text) => Add("[OK] " + text);

        public void Warn(string text) => Add("[!] " + text);

        public void Step(string text) => Add("[>] " + text);

        public void Fail(string text, string? hint) => Add("[X] " + text);

        public void Error(ErrorInfo error) => Add("[X] " + error.Code);

        public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows) => Add("table");

        public void Json(object value)
        {
        }
    }

    private static ArrowEvent E(string type, string message, EventLevel level = EventLevel.Info, int? index = null, int? total = null, params (string Key, object? Value)[] data) =>
        new(DateTimeOffset.UtcNow, type, "com.acme.Order", index, total, level, message, data.ToDictionary(d => d.Key, d => d.Value));

    private static readonly DateTimeOffset Noon = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OneLinePerBlockWithTheTimeInPlainMode()
    {
        var renderer = new RecordingRenderer();
        await using (var printer = new GenEventPrinter(renderer, false, () => Noon))
        {
            printer.Report(E(ArrowEventTypes.RunTargetStarted, "[1/1] com.acme.Order", index: 1, total: 1));
            printer.Report(E(ArrowEventTypes.B1Checkout, "Chép project"));
            printer.Report(E(ArrowEventTypes.B1Done, "Maven 3.9.6 · build root . · JDK 17 · JUnit 5 · baseline 42 test ×2 · 38.0 s"));
            printer.Report(E(ArrowEventTypes.B2Response, "x", data: [("prompt_tokens", 1500), ("completion_tokens", 400), ("latency_seconds", 6.2), ("replayed", false)]));
            printer.Report(E(ArrowEventTypes.B2Candidate, "Candidate hợp lệ: 6 test, 9 assertion"));
            printer.Report(E(ArrowEventTypes.B3Verified, "Vòng 0: COMPILE · COMPILE | cannot find symbol | method", data: [("round", 0), ("state", "COMPILE"), ("signature", "COMPILE | cannot find symbol | method")]));
            printer.Report(E(ArrowEventTypes.B2Candidate, "Candidate hợp lệ: 6 test, 9 assertion"));
            printer.Report(E(ArrowEventTypes.B3Round, "vòng 1 · mức 1 → VALID ✓ · thành công"));
            printer.Report(E(ArrowEventTypes.B3Final, "Kết thúc (valid): VALID sau 1 vòng", data: [("final_state", "VALID")]));
            printer.Report(E(ArrowEventTypes.B4ToolDone, "jacoco: line 78% · 24.8 s"));
            printer.Report(E(ArrowEventTypes.B4ToolDone, "pit: mutation 61% (33/54 bị diệt) · 132.0 s"));
            printer.Report(E(ArrowEventTypes.B4Done, "Khối 4 xong"));
            printer.Report(E(ArrowEventTypes.LlmRetry, "gọi lại sau 20 s (429)", EventLevel.Warning));
        }

        var t = Noon.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
        [
            $"{t} ARROW · Order (com.acme)",
            $"[OK] {t} Khối 1  Maven 3.9.6 · build root . · JDK 17 · JUnit 5 · baseline 42 test ×2 · 38.0 s",
            $"[OK] {t} Khối 2  c0 · 1.9k token · 6.2 s · Candidate hợp lệ: 6 test, 9 assertion",
            $"[>] {t} Khối 3  c0: COMPILE · cannot find symbol | method",
            $"{t}           vòng 1 · mức 1 → VALID ✓ · thành công",
            $"[OK] {t} Khối 3  Kết thúc (valid): VALID sau 1 vòng",
            $"[OK] {t} Khối 4  jacoco: line 78% · 24.8 s · pit: mutation 61% (33/54 bị diệt) · 132.0 s",
            $"[!] {t} gọi lại sau 20 s (429)",
        ], renderer.Lines);
    }

    [Fact]
    public async Task SeveralClassesArePrefixedWithTheirPosition()
    {
        var renderer = new RecordingRenderer(RenderMode.Rich);
        await using (var printer = new GenEventPrinter(renderer, false))
        {
            printer.Report(E(ArrowEventTypes.RunTargetsResolved, "2 class", data: [("targets", new List<string> { "com.acme.Order", "com.acme.Cart" })]));
            printer.Report(E(ArrowEventTypes.RunTargetStarted, "x", index: 2, total: 2));
            printer.Report(E(ArrowEventTypes.B1Done, "Maven", index: 2, total: 2));
        }

        // Without a Spectre console (Ansi is null) there is no live table: the lines are printed with [i/n].
        Assert.Equal(["[2/2] ARROW · Order (com.acme)", "[OK] [2/2] Khối 1  Maven"], renderer.Lines);
    }

    [Fact]
    public async Task VerboseShowsTheStepsInsideABlock()
    {
        var renderer = new RecordingRenderer(RenderMode.Rich);
        await using (var printer = new GenEventPrinter(renderer, true))
        {
            printer.Report(E(ArrowEventTypes.B3VerifyStep, "compile (lần 1)"));
        }

        Assert.Equal(["[>] compile (lần 1)"], renderer.Lines);
    }

    // P07 §10: when the console falls behind, Info events are dropped; warnings and errors are not.
    [Fact]
    public async Task AFullQueueDropsInfoEventsOnly()
    {
        using var gate = new SemaphoreSlim(0);
        var renderer = new RecordingRenderer(RenderMode.Rich) { Gate = gate };
        var printer = new GenEventPrinter(renderer, true, capacity: 2);
        for (var i = 0; i < 20; i++)
        {
            printer.Report(E(ArrowEventTypes.B3VerifyStep, $"step {i}"));
        }

        Assert.True(printer.Dropped >= 15);
        var warning = Task.Run(() => printer.Report(E(ArrowEventTypes.LlmRetry, "giữ lại", EventLevel.Warning)), TestContext.Current.CancellationToken);
        gate.Release(100);
        await warning;
        await printer.DisposeAsync();
        Assert.Contains("[!] giữ lại", renderer.Lines);
    }
}
