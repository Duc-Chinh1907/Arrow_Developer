using Arrow.Common;

namespace Arrow.DataAccess.JavaHelper;

/// <summary>The commands of java-helper (P10 §4), one method each. Implemented by <see cref="JavaHelperClient"/>.</summary>
public interface IJavaHelper : IAsyncDisposable
{
    /// <summary><c>ping</c>: the version of the running java-helper.</summary>
    Task<string> PingAsync(CancellationToken ct);

    /// <summary><c>parse</c> (P10 §4.2).</summary>
    Task<JavaParseResult> ParseAsync(string code, CancellationToken ct);

    /// <summary><c>rename_class</c> (P10 §4.3); <paramref name="package"/> null keeps the package, empty removes it.</summary>
    Task<RenameResult> RenameClassAsync(string code, string newName, string? package, CancellationToken ct);

    /// <summary><c>strip_comments_hash</c> (P10 §4.4).</summary>
    Task<HashResult> StripCommentsHashAsync(string code, CancellationToken ct);

    /// <summary><c>shrink_source</c> (P10 §4.5).</summary>
    Task<ShrinkResult> ShrinkSourceAsync(string code, int maxChars, CancellationToken ct);

    /// <summary><c>splice_methods</c> (P10 §4.6).</summary>
    Task<SpliceResult> SpliceMethodsAsync(string baseCode, string response, IReadOnlyList<string> methods, CancellationToken ct);

    /// <summary><c>list_test_methods</c> (P10 §4.7); empty when the code does not parse.</summary>
    Task<IReadOnlyList<string>> ListTestMethodsAsync(string code, CancellationToken ct);

    /// <summary><c>count_assertions</c> (P10 §4.8).</summary>
    Task<AssertionCount> CountAssertionsAsync(string code, CancellationToken ct);

    /// <summary><c>method_at_line</c> (P10 §4.9): the smallest method containing each line, or null.</summary>
    Task<IReadOnlyDictionary<int, string?>> MethodAtLineAsync(string code, IReadOnlyList<int> lines, CancellationToken ct);

    /// <summary><c>class_api</c> (P10 §4.10).</summary>
    Task<ClassApiResult> ClassApiAsync(IReadOnlyList<string> sources, IReadOnlyList<string> classes, IReadOnlyList<string> classpath, CancellationToken ct);

    /// <summary><c>method_source</c> (P10 §4.11); <c>&lt;init&gt;</c> stands for the constructors.</summary>
    Task<IReadOnlyList<MethodSourceEntry>> MethodSourceAsync(string code, IReadOnlyList<string> names, CancellationToken ct);

    /// <summary><c>called_methods</c> (P10 §4.12); an empty <paramref name="testMethods"/> means every method.</summary>
    Task<CalledMethodsResult> CalledMethodsAsync(string testCode, IReadOnlyList<string> testMethods, string cutCode, CancellationToken ct);

    /// <summary><c>list_classes</c> (P10 §4.13).</summary>
    Task<ListClassesResult> ListClassesAsync(IReadOnlyList<string> roots, int maxFiles, CancellationToken ct);
}

/// <summary>java-helper answered <c>ok: false</c>; the caller decides what it means (P10 §5).</summary>
public sealed class JavaHelperCommandException(string command, string error) : Exception($"java-helper {command}: {error}")
{
    /// <summary>The command that failed.</summary>
    public string Command { get; } = command;

    /// <summary>The <c>error</c> text, <c>&lt;Type&gt;: &lt;message&gt;</c>.</summary>
    public string Error { get; } = error;
}

public sealed record JavaParseResult(bool Parsed, string? Error, string? Package, IReadOnlyList<TopLevelType> TopLevelTypes, int TestMethodCount);

public sealed record TopLevelType(string Name, bool Public);

public sealed record RenameResult(string Code, string OldName, IReadOnlyList<string> Actions);

public sealed record HashResult(bool Parsed, string Normalized, string CodeHash);

/// <summary><c>Truncation</c> is <c>none</c>, <c>comments_removed</c> or <c>signatures_only</c>.</summary>
public sealed record ShrinkResult(string Code, string Truncation);

public sealed record SpliceResult(
    string Code,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Dropped,
    IReadOnlyList<string> AddedImports,
    IReadOnlyList<string> AddedFields,
    IReadOnlyList<string> AddedMethods);

/// <summary><c>Total</c> counts every assertion in the file (null when it does not parse); <c>PerMethod</c> those of each test method.</summary>
public sealed record AssertionCount(bool Parsed, int? Total, IReadOnlyDictionary<string, int> PerMethod);

public sealed record ClassApiResult(IReadOnlyList<ClassApiType> Types, IReadOnlyList<MissingType> Missing);

/// <summary>Non-private signatures of one type; <c>Origin</c> is <c>source</c> or <c>classpath</c>.</summary>
public sealed record ClassApiType(string Name, string Kind, string Origin, IReadOnlyList<string> Members);

public sealed record MissingType(string Name, string Reason);

public sealed record MethodSourceEntry(string Name, string Source);

public sealed record CalledMethodsResult(
    bool Parsed,
    IReadOnlyList<string> Called,
    IReadOnlyList<MethodSourceEntry> Methods,
    IReadOnlyList<string> Constructors,
    IReadOnlyList<string> Fields);

public sealed record ListClassesResult(IReadOnlyList<JavaClassEntry> Classes, IReadOnlyList<ListClassesError> Errors, bool Truncated);

/// <summary>One top-level type found by <c>list_classes</c>; <see cref="Path"/> and <see cref="Root"/> are absolute.</summary>
public sealed record JavaClassEntry(string Fqn, string SimpleName, CutKind Kind, bool IsAbstract, bool IsPublic, string Path, string Root);

public sealed record ListClassesError(string Path, string Error);
