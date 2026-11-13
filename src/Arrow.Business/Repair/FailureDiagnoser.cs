using System.Text;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Generation;
using Arrow.Common.Verification;

namespace Arrow.Business.Repair;

/// <summary>Builds the <see cref="Diagnosis"/> of a failed verification (P04 §4). Pure code: it runs no tool.</summary>
public interface IFailureDiagnoser
{
    /// <summary>The diagnosis of <paramref name="result"/>, which must not be INFRA.</summary>
    /// <param name="result">The verification.</param>
    /// <param name="candidate">The verified candidate, for its code and hash.</param>
    /// <param name="metadata">M, for the test framework and paths.</param>
    Diagnosis Diagnose(VerifyResult result, CandidateTest candidate, RepositoryMetadata metadata);
}

/// <summary>
/// <see cref="IFailureDiagnoser"/>: the error block for the prompt (at most 60 lines, P04 §4.2) and the first error for the
/// list of failed attempts (at most 200 characters, P04 §4.3). The paths in the result are already relative: the verifier made them so.
/// </summary>
public sealed class FailureDiagnoser : IFailureDiagnoser
{
    /// <summary>Lines of an error block.</summary>
    public const int MaxBlockLines = 60;

    /// <summary>Failing methods or broken tests shown in a block.</summary>
    public const int MaxFailures = 5;

    /// <summary>Characters of a first error.</summary>
    public const int MaxFirstErrorChars = 200;

    /// <inheritdoc />
    public Diagnosis Diagnose(VerifyResult result, CandidateTest candidate, RepositoryMetadata metadata)
    {
        if (result.State is not { } state)
        {
            throw new ArgumentException("An INFRA result has no diagnosis.", nameof(result));
        }

        var (block, first) = state switch
        {
            VerificationState.Compile => CompileBlock(result, candidate.Code ?? ""),
            VerificationState.Unknown => UnknownBlock(result),
            VerificationState.Discovery => DiscoveryBlock(result, candidate, metadata),
            VerificationState.Runtime or VerificationState.Assertion => FailureBlock(result, state),
            VerificationState.Regression => RegressionBlock(result),
            _ => ("", null),
        };

        return new Diagnosis(
            state,
            StateGroups.GroupOf(state),
            result.ErrorSignature,
            candidate.CodeHash ?? result.CodeHash,
            state is VerificationState.Compile or VerificationState.Runtime or VerificationState.Assertion ? [.. result.FailingMethods] : [],
            Limit(block),
            Cut(first, MaxFirstErrorChars));
    }

    /// <summary>COMPILE: javac errors of the generated file (all errors when none is there), with symbol, location and the line of code.</summary>
    private static (string, string?) CompileBlock(VerifyResult result, string code)
    {
        var errors = result.CompileErrors.Where(e => e.InGeneratedFile).ToList();
        if (errors.Count == 0)
        {
            errors = [.. result.CompileErrors];
        }

        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var text = new StringBuilder();
        foreach (var error in errors)
        {
            text.Append($"{error.File}:{error.Line}{(error.Column is { } c ? $":{c}" : "")}: error: {error.Message}\n");
            if (error.Symbol is not null)
            {
                text.Append($"  symbol: {Symbol(error)}\n");
            }

            if (error.Location is not null)
            {
                text.Append($"  location: {error.Location}\n");
            }

            if (error.InGeneratedFile && error.Line >= 1 && error.Line <= lines.Length)
            {
                text.Append($"  code: {lines[error.Line - 1].Trim()}\n");
            }
        }

        var first = errors.FirstOrDefault() is { } e
            ? e.Message + (e.Symbol is null ? "" : ": " + Symbol(e)) + (e.Location is null ? "" : " in " + e.Location)
            : null;
        return (text.ToString(), first);
    }

    private static string Symbol(CompileError error) => (error.SymbolKind is null ? "" : error.SymbolKind + " ") + error.Symbol;

    /// <summary>UNKNOWN: the build error block the verifier extracted.</summary>
    private static (string, string?) UnknownBlock(VerifyResult result)
    {
        var block = result.BuildErrorExcerpt ?? "";
        return (block, FirstBuildError(block));
    }

    /// <summary>The first <c>Failed to execute goal</c> line (or the first line), without <c>[ERROR] </c>.</summary>
    private static string? FirstBuildError(string block)
    {
        var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var first = lines.FirstOrDefault(l => l.Contains("Failed to execute goal", StringComparison.Ordinal)) ?? lines.FirstOrDefault();
        return first is not null && first.StartsWith("[ERROR] ", StringComparison.Ordinal) ? first[8..] : first;
    }

    /// <summary>DISCOVERY: 0 test methods ran, the class and the file written.</summary>
    private static (string, string?) DiscoveryBlock(VerifyResult result, CandidateTest candidate, RepositoryMetadata metadata)
    {
        var framework = metadata.TestFramework?.Name switch { "junit5" => "JUnit 5", "testng" => "TestNG", _ => "JUnit 4" };
        var testClass = candidate.TestFqn.Length > 0 ? candidate.TestFqn : metadata.Paths.GeneratedTestFqn;
        var file = result.TestFile ?? candidate.TestPath;
        return ($"The test runner executed 0 test methods of {testClass} (file {file}). The project runs tests with {framework}.\n", "no tests executed");
    }

    /// <summary>RUNTIME and ASSERTION: each failing method (at most 5) with exception, message and 8 lines of trace.</summary>
    private static (string, string?) FailureBlock(VerifyResult result, VerificationState state)
    {
        if (result.Failures.Count == 0)
        {
            // The run of step 2 failed without a report (P04 §3.2): the build error is all there is.
            var block = result.BuildErrorExcerpt ?? "";
            return (block, FirstBuildError(block));
        }

        var text = new StringBuilder();
        foreach (var failure in result.Failures.Take(MaxFailures))
        {
            text.Append($"Test method: {MethodOf(failure)}\n");
            text.Append($"Exception: {failure.Type}{(string.IsNullOrWhiteSpace(failure.Message) ? "" : ": " + OneLine(failure.Message))}\n");
            if (failure.TraceHead is { Length: > 0 } trace)
            {
                foreach (var line in trace.Split('\n').Skip(1))
                {
                    text.Append("  ").Append(line.Trim()).Append('\n');
                }
            }
        }

        if (result.Failures.Count > MaxFailures)
        {
            text.Append($"… and {result.Failures.Count - MaxFailures} more failing test methods\n");
        }

        // RUNTIME: "method: Exception at Class.method"; ASSERTION: "method: message" (P04 §4.3).
        var f = state == VerificationState.Runtime
            ? result.Failures.FirstOrDefault(x => !TestResultClassifier.IsAssertion(x.Type, x.Message)) ?? result.Failures[0]
            : result.Failures[0];
        var first = state == VerificationState.Runtime
            ? $"{MethodOf(f)}: {TestResultClassifier.ShortName(f.Type)}{(f.Frame is null ? "" : " at " + f.Frame)}"
            : $"{MethodOf(f)}: {(string.IsNullOrWhiteSpace(f.Message) ? TestResultClassifier.ShortName(f.Type) : OneLine(f.Message))}";
        return (text.ToString(), first);
    }

    private static string MethodOf(FailureInfo failure) => failure.Method.Length > 0 ? failure.Method : "(class)";

    /// <summary>
    /// REGRESSION: each broken existing test (at most 5) with exception and message, then the failures of the generated test
    /// run with the module, case (c).
    /// </summary>
    private static (string, string?) RegressionBlock(VerifyResult result)
    {
        var existing = result.Regressions.Where(r => r.Kind != RegressionKind.GeneratedFailedInModule).ToList();
        var generated = result.Regressions.Where(r => r.Kind == RegressionKind.GeneratedFailedInModule).ToList();
        var text = new StringBuilder();
        if (existing.Count > 0)
        {
            text.Append("The generated test passes alone, but running the whole module together with it breaks these existing tests:\n");
            AppendRegressions(text, existing);
        }

        if (generated.Count > 0)
        {
            text.Append(existing.Count > 0
                ? "The generated test failed when run with the module:\n"
                : "The generated test passes alone, but it failed when run with the module:\n");
            AppendRegressions(text, generated);
        }

        var r = existing.FirstOrDefault() ?? generated.FirstOrDefault();
        var first = r is null ? null : $"{r.Test}: {(r.Kind == RegressionKind.Missing ? "not run" : TestResultClassifier.ShortName(r.Type ?? "unknown"))}";
        return (text.ToString(), first);
    }

    private static void AppendRegressions(StringBuilder text, IReadOnlyList<RegressionInfo> regressions)
    {
        foreach (var regression in regressions.Take(MaxFailures))
        {
            var reason = regression.Kind == RegressionKind.Missing
                ? "was not run (missing from the report)"
                : $"{regression.Type ?? "unknown"}{(string.IsNullOrWhiteSpace(regression.Message) ? "" : ": " + OneLine(regression.Message))}";
            text.Append($"- {regression.Test}: {reason}\n");
        }

        if (regressions.Count > MaxFailures)
        {
            text.Append($"… and {regressions.Count - MaxFailures} more\n");
        }
    }

    private static string OneLine(string text)
    {
        var line = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    /// <summary>At most <paramref name="max"/> characters, the last one <c>…</c> when cut.</summary>
    public static string? Cut(string? text, int max) => text is { Length: var n } && n > max ? text[..(max - 1)] + "…" : text;

    /// <summary>At most <paramref name="maxLines"/> lines, the last one <c>… (k more lines)</c> when cut.</summary>
    public static string Limit(string block, int maxLines = MaxBlockLines)
    {
        var lines = block.TrimEnd('\n').Split('\n');
        if (lines.Length <= maxLines)
        {
            return string.Join('\n', lines);
        }

        var kept = maxLines - 1;
        return string.Join('\n', lines.Take(kept)) + $"\n… ({lines.Length - kept} more lines)";
    }
}
