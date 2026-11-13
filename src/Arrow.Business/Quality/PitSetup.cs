namespace Arrow.Business.Quality;

/// <summary>Choices of P05 §5 that are pure code: the JDK of PIT, its classpath and its command.</summary>
public static class PitSetup
{
    /// <summary>PIT 1.17 runs on Java 11 or newer.</summary>
    public const int MinimumJdk = 11;

    /// <summary>The PIT jars started with the command (tool ids of <c>tools.lock.json</c>).</summary>
    public static readonly IReadOnlyList<string> CoreTools = ["pitest", "pitest-entry", "pitest-command-line", "pitest-html-report", "commons-text", "commons-lang3", "jopt-simple"];

    /// <summary>Jars of the test framework that the launcher of PIT needs next to its plugin.</summary>
    // from ARROW_Framework/src/Arrow.Service/Block4/QualityTools.cs
    private static readonly string[] FrameworkJarPrefixes = ["junit-platform-", "junit-jupiter-", "opentest4j-", "apiguardian-", "testng-", "jcommander-"];

    /// <summary>
    /// The JDK of PIT: the project's when its major is 11 or more, otherwise the smallest JDK ≥ 11 on the machine; null when there
    /// is none (<c>B4_PIT_FAILED</c>, "PIT cần JDK 11+").
    /// </summary>
    public static string? ChooseJdk(int projectMajor, string projectHome, IReadOnlyDictionary<int, string> jdkHomes) =>
        projectMajor >= MinimumJdk
            ? projectHome
            : jdkHomes.Where(j => j.Key >= MinimumJdk).OrderBy(j => j.Key).Select(j => j.Value).FirstOrDefault();

    /// <summary>The plugin tool for a test framework of M: <c>pitest-junit5-plugin</c>, <c>pitest-testng-plugin</c>, or none for JUnit 4.</summary>
    public static string? PluginTool(string? framework) => framework switch
    {
        "junit5" => "pitest-junit5-plugin",
        "testng" => "pitest-testng-plugin",
        _ => null,
    };

    /// <summary>
    /// The version of the JUnit Platform of the module, from the name of <c>junit-platform-engine-&lt;v&gt;.jar</c> or
    /// <c>junit-platform-commons-&lt;v&gt;.jar</c> on the classpath; null when absent.
    /// </summary>
    public static string? PlatformVersion(IReadOnlyList<string> classpath)
    {
        foreach (var prefix in new[] { "junit-platform-engine-", "junit-platform-commons-" })
        {
            var jar = classpath.Select(Path.GetFileNameWithoutExtension).FirstOrDefault(n => n is not null && n.StartsWith(prefix, StringComparison.Ordinal));
            if (jar is not null)
            {
                return jar[prefix.Length..];
            }
        }

        return null;
    }

    /// <summary>True when the classpath already has <c>junit-platform-launcher</c>.</summary>
    public static bool HasLauncher(IReadOnlyList<string> classpath) =>
        classpath.Any(c => Path.GetFileName(c).StartsWith("junit-platform-launcher", StringComparison.Ordinal));

    /// <summary>
    /// The classpath of <c>--classPathFile</c> (P05 §5 step 3): the classes just compiled, then <c>arrow-cp.txt</c> without
    /// repeats, then <paramref name="extra"/> (<c>junit-platform-launcher</c> when it was missing).
    /// </summary>
    public static IReadOnlyList<string> Classpath(string productionClasses, string testClasses, IReadOnlyList<string> moduleClasspath, IReadOnlyList<string> extra)
    {
        var list = new List<string> { productionClasses, testClasses };
        foreach (var entry in moduleClasspath.Concat(extra))
        {
            if (entry.Length > 0 && !list.Contains(entry, StringComparer.Ordinal))
            {
                list.Add(entry);
            }
        }

        return list;
    }

    /// <summary>
    /// The classpath of the <c>java -cp</c> that starts PIT: its jars, the plugin, and the framework jars of the module. The full
    /// classpath goes through <c>--classPathFile</c>, because a Windows command line is limited to 32,767 characters.
    /// </summary>
    public static IReadOnlyList<string> LaunchClasspath(IReadOnlyList<string> pitJars, IReadOnlyList<string> classpath) =>
        [.. pitJars, .. classpath.Where(c => FrameworkJarPrefixes.Any(p => Path.GetFileName(c).StartsWith(p, StringComparison.Ordinal)))];

    /// <summary>The arguments after <c>-cp &lt;launch classpath&gt;</c> (P05 §5 step 4).</summary>
    public static IReadOnlyList<string> Arguments(string cutFqn, string testFqn, string reportDir, string classPathFile, string sourceRoot) =>
    [
        "org.pitest.mutationtest.commandline.MutationCoverageReport",
        "--reportDir", reportDir,
        "--targetClasses", $"{cutFqn},{cutFqn}$*",
        "--targetTests", testFqn,
        "--classPathFile", classPathFile,
        "--mutators", "DEFAULTS",
        "--threads", "1",
        "--timestampedReports=false",
        "--outputFormats", "XML",
        "--failWhenNoMutations=false",
        "--sourceDirs", sourceRoot,
    ];
}

/// <summary>Choices of P05 §4 that are pure code.</summary>
public static class JacocoSetup
{
    /// <summary>
    /// The JaCoCo agent option (P05 §4 step 1): <c>-javaagent:&lt;agent&gt;=destfile=&lt;exec&gt;,append=true,includes=&lt;FQN&gt;:&lt;FQN&gt;$*</c>.
    /// The two exact patterns never match a class with a longer name (<c>Order</c> is not <c>OrderService</c>).
    /// </summary>
    public static string AgentOption(string agentJar, string execFile, string cutFqn) =>
        $"-javaagent:{agentJar}=destfile={execFile},append=true,includes={cutFqn}:{cutFqn}$* -Djdk.attach.allowAttachSelf=true";
}
