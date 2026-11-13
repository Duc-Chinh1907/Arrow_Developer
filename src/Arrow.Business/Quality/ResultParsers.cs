using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Arrow.Common.Quality;

namespace Arrow.Business.Quality;

/// <summary>Splits simple CSV lines with quoted cells.</summary>
public static class Csv
{
    /// <summary>The cells of <paramref name="line"/>; <c>""</c> inside quotes is one quote.</summary>
    public static List<string> Split(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }

    /// <summary>The non-empty lines of <paramref name="text"/>.</summary>
    public static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.Trim().Length > 0).ToList();
}

/// <summary>
/// The CSV of <c>jacococli report --csv</c> (P05 §4 step 3): only rows whose <c>PACKAGE</c> is the CUT's package and whose
/// <c>CLASS</c> is the CUT or starts with <c>&lt;CUT&gt;.</c> (nested classes); never a class with a close name (<c>OrderService</c>
/// for <c>Order</c>).
/// </summary>
public static class JacocoCsvParser
{
    private static readonly string[] Kinds = ["LINE", "BRANCH", "METHOD"];

    /// <summary>The coverage of <paramref name="cutFqn"/>; null when the report does not have it.</summary>
    public static CoverageResult? Parse(string csv, string cutFqn)
    {
        var lines = Csv.Lines(csv);
        if (lines.Count == 0)
        {
            return null;
        }

        var header = Csv.Split(lines[0]).Select(h => h.Trim()).ToList();
        var packageColumn = header.IndexOf("PACKAGE");
        var classColumn = header.IndexOf("CLASS");
        if (packageColumn < 0 || classColumn < 0)
        {
            return null;
        }

        var dot = cutFqn.LastIndexOf('.');
        var package = dot < 0 ? "" : cutFqn[..dot];
        var simple = cutFqn[(dot + 1)..];
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        var found = false;
        foreach (var row in lines.Skip(1).Select(Csv.Split).Where(r => r.Count > Math.Max(packageColumn, classColumn)))
        {
            var rowClass = row[classColumn].Trim();
            if (row[packageColumn].Trim() != package || !(rowClass == simple || rowClass.StartsWith(simple + ".", StringComparison.Ordinal)))
            {
                continue;
            }

            found = true;
            foreach (var name in Kinds.SelectMany(k => new[] { k + "_COVERED", k + "_MISSED" }))
            {
                var index = header.IndexOf(name);
                if (index >= 0 && index < row.Count && int.TryParse(row[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    counters[name] = counters.GetValueOrDefault(name) + value;
                }
            }
        }

        if (!found)
        {
            return null;
        }

        double? Ratio(string kind)
        {
            var covered = counters.GetValueOrDefault(kind + "_COVERED");
            var total = covered + counters.GetValueOrDefault(kind + "_MISSED");
            return total == 0 ? null : Math.Round((double)covered / total, 4);
        }

        return new CoverageResult(Ratio("LINE"), Ratio("BRANCH"), Ratio("METHOD"), counters);
    }
}

/// <summary>The <c>mutations.xml</c> of PIT (P05 §5): counts by status, the scope check and the surviving mutants.</summary>
public static class PitXmlParser
{
    /// <summary>Surviving mutants kept in <c>survivors_sample</c>.</summary>
    public const int MaxSurvivors = 10;

    /// <summary>The result and the classes with mutants outside <c>&lt;FQN&gt;</c> and <c>&lt;FQN&gt;$…</c>.</summary>
    public static (MutationResult Result, IReadOnlyList<string> OutOfScope) Parse(string xml, string cutFqn)
    {
        var mutations = XDocument.Parse(xml).Descendants("mutation").ToList();
        int Count(string status) => mutations.Count(m => (string?)m.Attribute("status") == status);
        var outOfScope = mutations
            .Select(m => ((string?)m.Element("mutatedClass") ?? "").Trim())
            .Where(c => c != cutFqn && !c.StartsWith(cutFqn + "$", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var survivors = mutations
            .Where(m => (string?)m.Attribute("status") == "SURVIVED")
            .Take(MaxSurvivors)
            .Select(m => new SurvivorSample(
                ((string?)m.Element("mutatedMethod") ?? "").Trim(),
                int.TryParse((string?)m.Element("lineNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) ? line : null,
                ShortMutator(((string?)m.Element("mutator") ?? "").Trim())))
            .ToList();

        var total = mutations.Count;
        var killed = Count("KILLED");
        var timedOut = Count("TIMED_OUT");
        var memory = Count("MEMORY_ERROR");
        var noCoverage = Count("NO_COVERAGE");
        var result = new MutationResult(
            total, killed, Count("SURVIVED"), noCoverage, timedOut, memory, Count("RUN_ERROR"),
            total == 0 ? null : Math.Round((double)(killed + timedOut + memory) / total, 4),
            total - noCoverage == 0 ? null : Math.Round((double)killed / (total - noCoverage), 4),
            survivors);
        return (result, outOfScope);
    }

    /// <summary><c>org.pitest.mutationtest.engine.gregor.mutators.ConditionalsBoundaryMutator</c> → <c>ConditionalsBoundaryMutator</c>.</summary>
    public static string ShortMutator(string mutator) => mutator[(mutator.LastIndexOf('.') + 1)..];
}

/// <summary>
/// The result CSV of tsDetect (P05 §6): one row for the test file; every column after the information columns is a smell, as a
/// count or, in older versions, <c>true</c>/<c>false</c>.
/// </summary>
public static class TsDetectCsvParser
{
    // from ARROW_Framework/src/Arrow.Service/Block4/ResultParsers.cs
    private static readonly HashSet<string> InfoColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "App", "TestClass", "TestFilePath", "ProductionFilePath", "RelativeTestFilePath", "RelativeProductionFilePath", "NumberOfMethods",
    };

    /// <summary>The smells; null when the CSV has no result row.</summary>
    public static SmellResult? Parse(string csv, int testMethods)
    {
        var lines = Csv.Lines(csv);
        if (lines.Count < 2)
        {
            return null;
        }

        var header = Csv.Split(lines[0]).Select(h => h.Trim()).ToList();
        var row = Csv.Split(lines[1]);
        var smells = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count && i < row.Count; i++)
        {
            if (header[i].Length == 0 || InfoColumns.Contains(header[i]))
            {
                continue;
            }

            var value = row[i].Trim();
            smells[header[i]] = value.Equals("true", StringComparison.OrdinalIgnoreCase) ? 1
                : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
                : 0;
        }

        var total = smells.Values.Sum();
        return new SmellResult(
            testMethods,
            smells.Where(s => s.Value > 0).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal),
            total,
            testMethods == 0 ? null : Math.Round((double)total / testMethods, 4));
    }
}
