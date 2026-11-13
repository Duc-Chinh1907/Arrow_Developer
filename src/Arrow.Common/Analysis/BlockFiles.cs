using System.Text.Json.Serialization;

namespace Arrow.Common.Analysis;

/// <summary>Outcome of a block (P01 §8, P04, P05).</summary>
public enum BlockOutcomeKind
{
    Ok,
    Blocked,
    Infra,
}

/// <summary>
/// <c>block1/outcome.json</c> (P01 §8). Written last: block 1 counts as finished only when this file exists.
/// </summary>
/// <param name="Outcome">OK, BLOCKED or INFRA.</param>
/// <param name="Code">Error code of <c>ErrorCatalog</c>; null when OK.</param>
/// <param name="Message">Message in the UI language.</param>
/// <param name="Hint">What the user can do.</param>
/// <param name="FirstError">First error line of the build log, when a build failed.</param>
/// <param name="Log">Log file relative to the target folder, e.g. <c>block1/logs/compile-jdk17.log</c>.</param>
/// <param name="Seconds">Time spent in block 1.</param>
public sealed record BlockOutcome(
    BlockOutcomeKind Outcome,
    string? Code,
    string? Message,
    string? Hint,
    string? FirstError,
    string? Log,
    double Seconds)
{
    /// <summary>A successful outcome.</summary>
    public static BlockOutcome Ok(double seconds) => new(BlockOutcomeKind.Ok, null, null, null, null, null, seconds);
}

/// <summary><c>block1/baseline.json</c> (P01 §6.3). Test ids are <c>&lt;class&gt;#&lt;method&gt;</c>.</summary>
/// <param name="Runs">Number of baseline runs.</param>
/// <param name="Commands">Compile and module test commands, for the log and for <c>arrow show</c>.</param>
/// <param name="WorkingDir">Build root, relative to the project root.</param>
/// <param name="PassingInAllRuns">Tests that passed in every run.</param>
/// <param name="Flaky">Tests that passed in some runs only.</param>
/// <param name="FailingInAllRuns">Tests that failed in every run; never used to conclude a regression.</param>
/// <param name="DurationsSeconds">Time of each step.</param>
public sealed record BaselineInfo(
    int Runs,
    BaselineCommands Commands,
    string WorkingDir,
    IReadOnlyList<string> PassingInAllRuns,
    IReadOnlyList<string> Flaky,
    IReadOnlyList<string> FailingInAllRuns,
    BaselineDurations DurationsSeconds);

/// <summary>Commands of <see cref="BaselineInfo"/>, program first.</summary>
public sealed record BaselineCommands(IReadOnlyList<string> Compile, IReadOnlyList<string> ModuleTest);

/// <summary>Seconds of the copy, the compile and each module test run.</summary>
public sealed record BaselineDurations(double Copy, double Compile, IReadOnlyList<double> ModuleTest);

/// <summary>
/// <c>block1/workspace.json</c> (P01 §6.4): how blocks 3 and 4 run builds on copies of the compiled snapshot.
/// Paths in <c>work/</c> are relative to <c>ARROW_HOME</c>; <see cref="BuildRoot"/> is relative to the project root.
/// </summary>
/// <param name="Base">Copy of the project, e.g. <c>work/&lt;run_id&gt;/base</c>.</param>
/// <param name="CompiledSnapshot">Compiled copy, e.g. <c>work/&lt;run_id&gt;/snapshot-3f1a</c>.</param>
/// <param name="SourceTreeHash"><c>sha256:…</c> of the copied tree (P01 §3.1); null for a git checkout.</param>
/// <param name="JdkMajor">Major of the JDK used.</param>
/// <param name="JdkHome">Home of that JDK.</param>
/// <param name="BuildTool">Maven or Gradle.</param>
/// <param name="Executable">Wrapper relative to the project root, or the absolute path of the machine's tool.</param>
/// <param name="ExecutableInRepo">True for a wrapper, which is resolved against each copy.</param>
/// <param name="BuildRoot">Folder the commands run from.</param>
/// <param name="BuildMode">Reactor or standalone.</param>
/// <param name="BuildRootSource"><c>detected:reactor</c> or <c>detected:standalone</c>.</param>
/// <param name="MavenModuleSelector"><c>["-pl", "m", "-am"]</c> for a Maven reactor build; otherwise empty.</param>
/// <param name="GradleProject">Gradle project path, e.g. <c>:core</c>; null for Maven.</param>
/// <param name="CommonFlags">Flags of P01 §6, the same for every block.</param>
/// <param name="InitScripts">Gradle init scripts, absolute paths.</param>
public sealed record WorkspaceInfo(
    string Base,
    string CompiledSnapshot,
    string? SourceTreeHash,
    int JdkMajor,
    string JdkHome,
    BuildToolKind BuildTool,
    string Executable,
    bool ExecutableInRepo,
    string BuildRoot,
    BuildMode BuildMode,
    string BuildRootSource,
    IReadOnlyList<string> MavenModuleSelector,
    string? GradleProject,
    IReadOnlyList<string> CommonFlags,
    IReadOnlyList<string> InitScripts);

/// <summary>A dependency resolved on the test classpath (a line of <c>dependency:list</c> or a Gradle artifact).</summary>
/// <param name="Group">groupId.</param>
/// <param name="Artifact">artifactId.</param>
/// <param name="Version">Version.</param>
/// <param name="Scope">Maven scope; <c>test</c> for every Gradle artifact.</param>
/// <param name="Path">Absolute path of the file; null when the tool did not print it.</param>
public sealed record ResolvedDependency(string Group, string Artifact, string Version, string Scope, string? Path)
{
    /// <summary><c>group:artifact:version</c>.</summary>
    [JsonIgnore]
    public string Coordinates => $"{Group}:{Artifact}:{Version}";
}
