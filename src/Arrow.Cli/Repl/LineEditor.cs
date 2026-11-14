using System.Text;

namespace Arrow.Cli.Repl;

/// <summary>Where the editor reads keys; the console in the CLI, a script in tests.</summary>
internal interface IKeySource
{
    /// <summary>The next key, not echoed.</summary>
    ConsoleKeyInfo ReadKey();
}

/// <summary>Keys of the console; Ctrl+C is read as a key while a line is edited (P08 §5.4).</summary>
internal sealed class ConsoleKeySource : IKeySource
{
    public ConsoleKeyInfo ReadKey()
    {
        // While a line is edited Ctrl+C is a key of the editor; while a command runs it cancels the command (P08 §5.4).
        Console.TreatControlCAsInput = true;
        try
        {
            return Console.ReadKey(intercept: true);
        }
        finally
        {
            Console.TreatControlCAsInput = false;
        }
    }
}

/// <summary>What a line read ended with.</summary>
internal enum LineEnd
{
    /// <summary>Enter.</summary>
    Enter,

    /// <summary>Ctrl+C on an empty line, or Ctrl+D: leave the session.</summary>
    Exit,
}

/// <summary>
/// The prompt line of the interactive session (P08 §5.2): typing and Backspace at the end of the line, Up/Down through the
/// history (kept in <c>ARROW_HOME/repl_history</c>, at most 500 lines), Tab to complete the last word, Esc or Ctrl+C to clear the line,
/// Ctrl+C on an empty line or Ctrl+D to leave.
/// </summary>
internal sealed class LineEditor(IKeySource keys, TextWriter output, string? historyFile, Func<string, IReadOnlyList<string>> complete)
{
    /// <summary>Lines kept in the history file.</summary>
    public const int MaxHistory = 500;

    private readonly List<string> _history = Load(historyFile);

    /// <summary>The history, oldest first.</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>Reads one line after <paramref name="prompt"/>.</summary>
    public (LineEnd End, string Text) ReadLine(string prompt)
    {
        var text = new StringBuilder();
        var index = _history.Count;
        var shown = 0;
        void Redraw()
        {
            var line = prompt + text;
            output.Write("\r" + line + new string(' ', Math.Max(0, shown - line.Length)) + "\r" + line);
            shown = line.Length;
        }

        Redraw();
        while (true)
        {
            var key = keys.ReadKey();
            var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
            if (control && key.Key == ConsoleKey.C)
            {
                if (text.Length == 0)
                {
                    output.WriteLine();
                    return (LineEnd.Exit, "");
                }

                text.Clear();
                Redraw();
                continue;
            }

            if (control && key.Key == ConsoleKey.D && text.Length == 0)
            {
                output.WriteLine();
                return (LineEnd.Exit, "");
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    output.WriteLine();
                    var line = text.ToString().Trim();
                    Remember(line);
                    return (LineEnd.Enter, line);
                case ConsoleKey.Backspace:
                    if (text.Length > 0)
                    {
                        text.Length--;
                    }

                    break;
                case ConsoleKey.Escape:
                    text.Clear();
                    break;
                case ConsoleKey.UpArrow when index > 0:
                    index--;
                    text.Clear().Append(_history[index]);
                    break;
                case ConsoleKey.DownArrow when index < _history.Count:
                    index++;
                    text.Clear().Append(index < _history.Count ? _history[index] : "");
                    break;
                case ConsoleKey.Tab:
                    Complete(text, output, prompt);
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        text.Append(key.KeyChar);
                        if (text.Length == 1 && key.KeyChar == '/')
                        {
                            // "/" on an empty line lists the commands (P08 §5.2).
                            Complete(text, output, prompt);
                        }
                    }

                    break;
            }

            Redraw();
        }
    }

    /// <summary>One candidate replaces the last word; several are listed under the line.</summary>
    private void Complete(StringBuilder text, TextWriter writer, string prompt)
    {
        var current = text.ToString();
        var start = current.LastIndexOf(' ') + 1;
        var word = current[start..];
        var candidates = complete(current).Where(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToList();
        if (candidates.Count == 1)
        {
            text.Length = start;
            text.Append(candidates[0]).Append(' ');
        }
        else if (candidates.Count > 1)
        {
            writer.WriteLine();
            writer.WriteLine(string.Join("  ", candidates.Take(30)) + (candidates.Count > 30 ? $"  … ({candidates.Count - 30} nữa)" : ""));
            var common = candidates.Aggregate((a, b) => new string(a.Zip(b).TakeWhile(p => char.ToLowerInvariant(p.First) == char.ToLowerInvariant(p.Second)).Select(p => p.First).ToArray()));
            if (common.Length > word.Length)
            {
                text.Length = start;
                text.Append(common);
            }

            writer.Write(prompt);
        }
    }

    private void Remember(string line)
    {
        if (line.Length == 0 || (_history.Count > 0 && _history[^1] == line))
        {
            return;
        }

        _history.Add(line);
        if (_history.Count > MaxHistory)
        {
            _history.RemoveRange(0, _history.Count - MaxHistory);
        }

        if (historyFile is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyFile)!);
            File.WriteAllLines(historyFile, _history, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The history is a convenience; losing it must not stop the session.
        }
    }

    private static List<string> Load(string? file)
    {
        try
        {
            return file is not null && File.Exists(file) ? File.ReadAllLines(file).Where(l => l.Length > 0).TakeLast(MaxHistory).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
