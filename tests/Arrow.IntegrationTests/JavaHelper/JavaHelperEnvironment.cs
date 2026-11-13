using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Storage;

namespace Arrow.IntegrationTests.JavaHelper;

/// <summary>
/// What the java-helper tests need: a JDK ≥ 17 (from <c>JAVA_HOME_17_X64</c> / <c>JAVA_HOME_21_X64</c>, the CI convention) and
/// the jar embedded in this build. Missing either one skips the test (CLAUDE.md: tests never pass silently).
/// </summary>
public static class JavaHelperEnvironment
{
    private static readonly Lazy<(JavaHelperOptions? Options, string? Missing)> Prepared = new(Prepare);

    /// <summary>Options for a new client; skips the calling test when java-helper cannot run here.</summary>
    public static JavaHelperOptions Options()
    {
        var (options, missing) = Prepared.Value;
        Assert.SkipWhen(options is null, missing ?? "java-helper unavailable");
        return options!;
    }

    private static (JavaHelperOptions?, string?) Prepare()
    {
        var homes = new[] { "17", "21" }
            .Select(major => (major, home: Environment.GetEnvironmentVariable($"JAVA_HOME_{major}_X64")))
            .Where(j => !string.IsNullOrEmpty(j.home))
            .ToDictionary(j => j.major, j => j.home!);
        if (!JavaHelperJar.IsEmbedded)
        {
            return (null, "java-helper.jar is not embedded: run mvn -q -f tools/java-helper/pom.xml package, then rebuild");
        }

        string java;
        try
        {
            java = JavaHelperJar.FindJava(homes);
        }
        catch (Arrow.Common.Errors.ArrowException)
        {
            return (null, "no JDK >= 17 in JAVA_HOME_17_X64 or JAVA_HOME_21_X64");
        }

        var home = new ArrowHome(Path.Combine(Path.GetTempPath(), "arrow-java-helper-tests"));
        return (new JavaHelperOptions(java, JavaHelperJar.Ensure(home)), null);
    }
}

/// <summary>One java-helper process shared by a test class, as in a real run (one process per run, P10 §5).</summary>
public sealed class JavaHelperFixture : IAsyncDisposable
{
    private JavaHelperClient? _client;

    public JavaHelperClient Client => _client ??= new JavaHelperClient(JavaHelperEnvironment.Options());

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }
}
