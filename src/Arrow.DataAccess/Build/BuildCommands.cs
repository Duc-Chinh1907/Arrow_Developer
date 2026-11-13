using System.Globalization;
using Arrow.Common.Analysis;

namespace Arrow.DataAccess.Build;

/// <summary>
/// How builds of one module run, the same for blocks 1, 3 and 4 (P01 §4.1, §6). The persisted form is <see cref="WorkspaceInfo"/>.
/// </summary>
/// <param name="Tool">Maven or Gradle.</param>
/// <param name="Executable">Wrapper relative to the project root when <paramref name="ExecutableInRepo"/>, otherwise an absolute path.</param>
/// <param name="ExecutableInRepo">A wrapper, resolved against each copy of the project.</param>
/// <param name="BuildRoot">Folder the commands run from, relative to the project root.</param>
/// <param name="Mode">Reactor or standalone.</param>
/// <param name="Module">Maven <c>-pl</c> path (reactor only) or Gradle project path (<c>:core</c>, <c>:</c> for the root).</param>
/// <param name="CommonFlags">Flags of P01 §6.</param>
/// <param name="InitScript">Absolute path of the Gradle init script; null for Maven.</param>
public sealed record BuildSetup(
    BuildToolKind Tool,
    string Executable,
    bool ExecutableInRepo,
    string BuildRoot,
    BuildMode Mode,
    string? Module,
    IReadOnlyList<string> CommonFlags,
    string? InitScript)
{
    /// <summary>Program to start for a copy of the project at <paramref name="projectRoot"/>.</summary>
    public string ExecutablePath(string projectRoot) =>
        ExecutableInRepo ? Path.GetFullPath(Path.Combine(projectRoot, Executable)) : Executable;

    /// <summary>Folder the commands run in for a copy of the project at <paramref name="projectRoot"/>.</summary>
    public string WorkingDirectory(string projectRoot) => Path.GetFullPath(Path.Combine(projectRoot, BuildRoot));

    /// <summary>Gradle project path, <c>:</c> when none is set.</summary>
    public string GradleProject => Module is { Length: > 0 } module ? module : ":";
}

/// <summary>Maven and Gradle command lines (P01 §4.4, §6), shared by blocks 1, 3 and 4.</summary>
public static class BuildCommands
{
    /// <summary><c>JAVA_TOOL_OPTIONS</c> of every build: fixed encoding, language and time zone, so results do not depend on the machine.</summary>
    public const string JavaToolOptions = "-Dfile.encoding=UTF-8 -Duser.language=en -Duser.country=US -Duser.timezone=UTC";

    /// <summary>File written by <c>dependency:list</c> in each module folder.</summary>
    public const string MavenDependencyFile = "arrow-deps.txt";

    /// <summary>File written by <c>dependency:build-classpath</c> in each module folder.</summary>
    public const string MavenClasspathFile = "arrow-cp.txt";

    /// <summary>Plugins are pinned; Maven older than 3.6.3 gets older plugin versions (P01 §4.3, §4.4).</summary>
    public static bool IsModernMaven(string? version) => version is null || version.Length == 0 || CompareVersions(version, "3.6.3") >= 0;

    public static string DependencyPlugin(string? mavenVersion) =>
        "org.apache.maven.plugins:maven-dependency-plugin:" + (IsModernMaven(mavenVersion) ? "3.8.1" : "3.1.2");

    public static string HelpPlugin(string? mavenVersion) =>
        "org.apache.maven.plugins:maven-help-plugin:" + (IsModernMaven(mavenVersion) ? "3.4.1" : "3.2.0");

    /// <summary>
    /// Common Maven flags (P01 §6): batch mode, no transfer progress (<c>-ntp</c> exists from Maven 3.6.1), no colors, and the
    /// code checking, formatting and signing plugins skipped. Windows adds <c>-Dsurefire.useManifestOnlyJar=false</c>.
    /// </summary>
    public static IReadOnlyList<string> MavenCommonFlags(string? mavenVersion, bool windows)
    {
        var flags = new List<string>
        {
            "-B",
            mavenVersion is null || mavenVersion.Length == 0 || CompareVersions(mavenVersion, "3.6.1") >= 0
                ? "-ntp"
                : "-Dorg.slf4j.simpleLogger.log.org.apache.maven.cli.transfer.Slf4jMavenTransferListener=warn",
            "-Dstyle.color=never",
            "-Dcheckstyle.skip=true", "-Dspotless.check.skip=true", "-Dspotless.apply.skip=true", "-Dspotless.skip=true",
            "-Dformatter.skip=true", "-Dimpsort.skip=true", "-Dlicense.skip=true", "-Drat.skip=true", "-Djacoco.skip=true",
            "-Dpmd.skip=true", "-Dcpd.skip=true", "-Dspotbugs.skip=true", "-Denforcer.skip=true", "-Dmaven.javadoc.skip=true",
            "-Dgpg.skip=true",
        };
        if (windows)
        {
            flags.Add("-Dsurefire.useManifestOnlyJar=false");
        }

        return flags;
    }

    /// <summary>
    /// Common Gradle flags (P01 §6). The configuration cache is off (Gradle 8 may enable it in <c>gradle.properties</c>;
    /// Gradle 7 calls it <c>unsafe</c>), because the init script tasks reference <c>Project</c>.
    /// </summary>
    public static IReadOnlyList<string> GradleCommonFlags { get; } =
        ["--no-daemon", "--console=plain", "-Dorg.gradle.configuration-cache=false", "-Dorg.gradle.unsafe.configuration-cache=false"];

    /// <summary>Full command: program, common flags, module selector (Maven) or init script (Gradle), then <paramref name="arguments"/>.</summary>
    public static IReadOnlyList<string> Command(BuildSetup setup, string projectRoot, IReadOnlyList<string> arguments)
    {
        var command = new List<string> { setup.ExecutablePath(projectRoot) };
        if (setup.Tool == BuildToolKind.Gradle)
        {
            command.AddRange(setup.CommonFlags);
            if (setup.InitScript is not null)
            {
                command.AddRange(["--init-script", setup.InitScript]);
            }
        }
        else
        {
            command.AddRange(["-f", "pom.xml"]);
            command.AddRange(setup.CommonFlags);
            command.AddRange(MavenSelector(setup));
        }

        command.AddRange(arguments);
        return command;
    }

    /// <summary><c>["-pl", m, "-am"]</c> for a Maven reactor build; otherwise empty.</summary>
    public static IReadOnlyList<string> MavenSelector(BuildSetup setup) =>
        setup.Tool == BuildToolKind.Maven && setup.Mode == BuildMode.Reactor && setup.Module is { Length: > 0 } module
            ? ["-pl", module, "-am"]
            : [];

    /// <summary><c>mvn -v</c> / <c>gradle -v</c>.</summary>
    public static IReadOnlyList<string> Version { get; } = ["-v"];

    /// <summary>
    /// Compiles the tests and writes the dependency list and the test classpath in <b>one</b> Maven call (P01 §4.4): Maven resolves
    /// sibling modules from the reactor only when they are built in the same call.
    /// </summary>
    public static IReadOnlyList<string> MavenCompileWithDependencies(string? mavenVersion)
    {
        var plugin = DependencyPlugin(mavenVersion);
        return
        [
            "-DskipTests", "test-compile",
            $"{plugin}:list", "-DincludeScope=test", "-DoutputAbsoluteArtifactFilename=true", "-DoutputFile=" + MavenDependencyFile, "-DappendOutput=false",
            $"{plugin}:build-classpath", "-Dmdep.includeScope=test", "-Dmdep.outputFile=" + MavenClasspathFile,
        ];
    }

    /// <summary>Writes the effective POM to <paramref name="outputFile"/> (P01 §4.3), before compiling.</summary>
    public static IReadOnlyList<string> MavenEffectivePom(string? mavenVersion, string outputFile) =>
        [$"{HelpPlugin(mavenVersion)}:effective-pom", "-Doutput=" + outputFile];

    /// <summary>Runs the module tests (baseline, step 5 of block 3); failing tests do not fail the command (P01 §6.2).</summary>
    public static IReadOnlyList<string> MavenModuleTest { get; } =
        ["-DfailIfNoTests=false", "-Dsurefire.failIfNoSpecifiedTests=false", "-Dmaven.test.failure.ignore=true", "test"];

    /// <summary>Prints the project configuration through the init script.</summary>
    public static IReadOnlyList<string> GradleInfo(string? project) => ["-q", GradleProbe.Task(project, GradleProbe.InfoTask)];

    /// <summary>Compiles the tests, then prints the resolved test classpath.</summary>
    public static IReadOnlyList<string> GradleCompileWithClasspath(string? project) =>
        [GradleProbe.Task(project, "testClasses"), GradleProbe.Task(project, GradleProbe.ClasspathTask)];

    /// <summary>Runs the tests of a project and goes on after failures.</summary>
    public static IReadOnlyList<string> GradleModuleTest(string? project) => [GradleProbe.Task(project, "test"), "--continue"];

    /// <summary>Compile step for <paramref name="setup"/>, with the dependency list and classpath.</summary>
    public static IReadOnlyList<string> CompileWithDependencies(BuildSetup setup, string? toolVersion) =>
        setup.Tool == BuildToolKind.Gradle ? GradleCompileWithClasspath(setup.GradleProject) : MavenCompileWithDependencies(toolVersion);

    /// <summary>Module test step for <paramref name="setup"/>.</summary>
    public static IReadOnlyList<string> ModuleTest(BuildSetup setup) =>
        setup.Tool == BuildToolKind.Gradle ? GradleModuleTest(setup.GradleProject) : MavenModuleTest;

    /// <summary>Compares versions such as <c>3.9.6</c>; non-numeric parts count as 0.</summary>
    public static int CompareVersions(string left, string right)
    {
        static int[] Parts(string v) => v.Split('.', '-')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsAsciiDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .ToArray();
        var a = Parts(left);
        var b = Parts(right);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }
}
