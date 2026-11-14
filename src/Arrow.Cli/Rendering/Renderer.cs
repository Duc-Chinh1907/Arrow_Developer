using System.Globalization;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Json;
using Spectre.Console;

namespace Arrow.Cli.Rendering;

/// <summary>Output modes of P08 §3.1.</summary>
public enum RenderMode
{
    /// <summary>Colors, symbols <c>✓ ✗ ▶ !</c>, framed tables.</summary>
    Rich,

    /// <summary>No colors, ASCII markers <c>[OK] [X] [&gt;] [!]</c>.</summary>
    Plain,

    /// <summary>stdout carries one JSON object only; messages go to stderr as plain text.</summary>
    Json,
}

/// <summary>Everything a command prints goes through here, so the three modes stay consistent.</summary>
public interface IRenderer
{
    RenderMode Mode { get; }

    /// <summary>A line of text without marker.</summary>
    void Line(string text);

    /// <summary><c>✓</c> / <c>[OK]</c>.</summary>
    void Ok(string text);

    /// <summary><c>!</c> / <c>[!]</c>.</summary>
    void Warn(string text);

    /// <summary><c>▶</c> / <c>[&gt;]</c>: a progress line.</summary>
    void Step(string text);

    /// <summary><c>✗</c> / <c>[X]</c> with <c>→ Cách sửa:</c>; for a failed check that has no error code, such as a line of <c>arrow doctor</c>.</summary>
    void Fail(string text, string? hint);

    /// <summary>
    /// <c>✗ CODE · message</c>, then the hint and the log path (P08 §3.2). In JSON mode the error is also written to
    /// stdout as <c>{ "error": { … } }</c>, so scripts can read it.
    /// </summary>
    void Error(ErrorInfo error);

    /// <summary>A table; rich mode draws a frame, plain mode aligns columns with spaces.</summary>
    void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows);

    /// <summary>The result object of the command; written to stdout in JSON mode only.</summary>
    void Json(object value);

    /// <summary>The Spectre console of rich mode, for live displays; null in plain and JSON mode.</summary>
    IAnsiConsole? Ansi { get; }
}

/// <summary>Picks the mode (P08 §3.1).</summary>
public static class RenderModes
{
    public static RenderMode Select(bool plain, bool json, bool outputRedirected, string? noColor, ColorMode color) =>
        json ? RenderMode.Json
        : plain || !string.IsNullOrEmpty(noColor) || color == ColorMode.Never ? RenderMode.Plain
        : color == ColorMode.Always ? RenderMode.Rich
        : outputRedirected ? RenderMode.Plain
        : RenderMode.Rich;
}

/// <summary><see cref="IRenderer"/> for the three modes; rich output uses Spectre.Console.</summary>
public sealed class Renderer : IRenderer
{
    private readonly TextWriter _out;
    private readonly TextWriter _text;
    private readonly IAnsiConsole? _rich;

    public Renderer(RenderMode mode, TextWriter stdout, TextWriter stderr)
    {
        Mode = mode;
        _out = stdout;
        _text = mode == RenderMode.Json ? stderr : stdout;
        _rich = mode == RenderMode.Rich
            ? AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(stdout), Ansi = AnsiSupport.Detect, ColorSystem = ColorSystemSupport.Detect })
            : null;
    }

    public RenderMode Mode { get; }

    public IAnsiConsole? Ansi => _rich;

    public void Line(string text)
    {
        if (_rich is not null)
        {
            _rich.MarkupLine(Markup.Escape(text));
        }
        else
        {
            _text.WriteLine(text);
        }
    }

    public void Ok(string text) => Marked("green", "✓", "[OK]", text);

    public void Warn(string text) => Marked("yellow", "!", "[!]", text);

    public void Step(string text) => Marked("cyan", "▶", "[>]", text);

    public void Fail(string text, string? hint)
    {
        Marked("red", "✗", "[X]", text);
        if (hint is not null)
        {
            Detail("  → Cách sửa: " + hint);
        }
    }

    public void Error(ErrorInfo error)
    {
        Marked("red", "✗", "[X]", $"{error.Code} · {error.Message}");
        if (error.Hint is not null)
        {
            Detail("  → Cách sửa: " + error.Hint);
        }

        if (error.Log is not null)
        {
            Detail("  Log: " + error.Log);
        }

        if (Mode == RenderMode.Json)
        {
            Json(new { error });
        }
    }

    public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (_rich is not null)
        {
            var table = new Table().Border(TableBorder.Rounded);
            foreach (var header in headers)
            {
                table.AddColumn(new TableColumn(Markup.Escape(header)));
            }

            foreach (var row in rows)
            {
                table.AddRow(row.Select(cell => (Spectre.Console.Rendering.IRenderable)new Text(cell)).ToArray());
            }

            _rich.Write(table);
            return;
        }

        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => i < r.Count ? r[i].Length : 0))).ToArray();
        string Format(IReadOnlyList<string> cells) =>
            string.Join("  ", cells.Select((c, i) => i == cells.Count - 1 ? c : c.PadRight(widths[i]))).TrimEnd();

        _text.WriteLine(Format(headers));
        _text.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
        {
            _text.WriteLine(Format(row));
        }
    }

    public void Json(object value)
    {
        if (Mode == RenderMode.Json)
        {
            _out.Write(ArrowJson.Serialize(value) + "\n");
        }
    }

    private void Marked(string color, string symbol, string ascii, string text)
    {
        if (_rich is not null)
        {
            _rich.MarkupLine(string.Create(CultureInfo.InvariantCulture, $"[{color}]{symbol}[/] {Markup.Escape(text)}"));
        }
        else
        {
            _text.WriteLine(ascii + " " + text);
        }
    }

    private void Detail(string text)
    {
        if (_rich is not null)
        {
            _rich.MarkupLine("[grey]" + Markup.Escape(text) + "[/]");
        }
        else
        {
            _text.WriteLine(text);
        }
    }
}
