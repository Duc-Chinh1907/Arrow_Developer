using System.Text.Json.Serialization;

namespace Arrow.Common.Analysis;

/// <summary>
/// Repository Metadata M: the content of <c>block1/metadata.json</c> (P01 §5.1).
/// Paths are relative to the project root and use <c>/</c>. A group that could not be determined is null,
/// with its reason in <see cref="MissingReasons"/>.
/// </summary>
/// <param name="Source">Where the code came from; extra information, not one of the six groups.</param>
/// <param name="BuildTool">Group 1: build tool, build root and module selector.</param>
/// <param name="Java">Group 2: declared Java level and chosen JDK.</param>
/// <param name="Dependencies">Group 3: direct dependencies.</param>
/// <param name="TestFramework">Group 4: test framework, mocking and assertion libraries.</param>
/// <param name="ProjectStructure">Group 5: modules and packages.</param>
/// <param name="Paths">Group 6: source, test and generated test paths.</param>
/// <param name="Cut">The class under test; extra information, not one of the six groups.</param>
/// <param name="MissingReasons">Why a group is null or partial, keyed by group name (e.g. <c>java</c>).</param>
public sealed record RepositoryMetadata(
    SourceMetadata Source,
    BuildToolMetadata BuildTool,
    JavaMetadata? Java,
    DependencyMetadata? Dependencies,
    TestFrameworkMetadata? TestFramework,
    ProjectStructureMetadata ProjectStructure,
    PathsMetadata Paths,
    CutMetadata Cut,
    IReadOnlyDictionary<string, string> MissingReasons);

/// <summary><c>source</c> of M.</summary>
/// <param name="Kind">Local folder or git repository.</param>
/// <param name="PathName">Folder name of the project root, or the repository name.</param>
/// <param name="Url">Repository URL without credentials; null for a local folder.</param>
/// <param name="GitCommit">Commit checked out, or HEAD of the local repository; null without git.</param>
/// <param name="Dirty">Local folder only: uncommitted changes exist; null without git.</param>
public sealed record SourceMetadata(ProjectSourceKind Kind, string PathName, string? Url, string? GitCommit, bool? Dirty);

/// <summary>Maven or Gradle.</summary>
public enum BuildToolKind
{
    [JsonStringEnumMemberName("maven")]
    Maven,

    [JsonStringEnumMemberName("gradle")]
    Gradle,
}

/// <summary>How the module of the CUT is built (P01 §4.1).</summary>
public enum BuildMode
{
    /// <summary>From an aggregator above the module, with <c>-pl &lt;module&gt; -am</c> or a Gradle project path.</summary>
    [JsonStringEnumMemberName("reactor")]
    Reactor,

    /// <summary>In the module folder itself.</summary>
    [JsonStringEnumMemberName("standalone")]
    Standalone,
}

/// <summary><c>build_tool</c> of M (P01 §4.1, §4.2, §4.7).</summary>
/// <param name="Kind">Maven or Gradle.</param>
/// <param name="Version">Version reported by <c>-v</c>; empty when unknown.</param>
/// <param name="Wrapper">The project wrapper is used.</param>
/// <param name="WrapperFallback">The wrapper failed and the machine's tool is used instead.</param>
/// <param name="BuildRoot">Folder every build command runs from.</param>
/// <param name="BuildMode">Reactor or standalone.</param>
/// <param name="BuildFile">Build file at the build root, e.g. <c>pom.xml</c>.</param>
/// <param name="ReactorModule">Maven <c>-pl</c> path or Gradle project path; null for a Maven standalone build.</param>
/// <param name="BuildRootSource"><c>detected:reactor</c> or <c>detected:standalone</c>.</param>
/// <param name="TestIncludes">Test name patterns of Surefire or of the Gradle <c>test</c> task.</param>
/// <param name="TestExcludes">Excluded test name patterns.</param>
public sealed record BuildToolMetadata(
    BuildToolKind Kind,
    string Version,
    bool Wrapper,
    bool WrapperFallback,
    string BuildRoot,
    BuildMode BuildMode,
    string BuildFile,
    string? ReactorModule,
    string BuildRootSource,
    IReadOnlyList<string> TestIncludes,
    IReadOnlyList<string> TestExcludes);

/// <summary><c>java</c> of M (P01 §4.3).</summary>
/// <param name="Declared">Level as written in the build, e.g. <c>1.8</c>; null when not declared.</param>
/// <param name="DeclaredSource">Where it was read, e.g. <c>maven.compiler.release</c>.</param>
/// <param name="Major">Declared level as a major number; null when not declared.</param>
/// <param name="JdkMajor">Major of the JDK used to build.</param>
/// <param name="JdkChoiceReason"><c>declared</c>, <c>default</c> or <c>fallback:&lt;JDKs tried&gt;</c>.</param>
public sealed record JavaMetadata(string? Declared, string? DeclaredSource, int? Major, int JdkMajor, string JdkChoiceReason);

/// <summary><c>dependencies</c> of M (P01 §4.4).</summary>
/// <param name="Direct">Declared dependencies with scope compile, runtime or provided.</param>
/// <param name="Test">Declared dependencies with scope test.</param>
/// <param name="ResolvedCount">Number of resolved artifacts on the test classpath.</param>
public sealed record DependencyMetadata(IReadOnlyList<DependencyRef> Direct, IReadOnlyList<DependencyRef> Test, int ResolvedCount);

/// <summary>A dependency coordinate. <see cref="Version"/> is empty when the build file leaves it to a BOM.</summary>
public sealed record DependencyRef(string Group, string Artifact, string Version, string Scope)
{
    /// <summary><c>group:artifact:version</c>, or <c>group:artifact</c> without a version.</summary>
    [JsonIgnore]
    public string Coordinates => Version.Length == 0 ? $"{Group}:{Artifact}" : $"{Group}:{Artifact}:{Version}";
}

/// <summary><c>test_framework</c> of M (P01 §4.5).</summary>
/// <param name="Name"><c>junit5</c>, <c>junit4</c> or <c>testng</c>; null when none was found.</param>
/// <param name="Version">Version of the framework artifact; empty when unknown.</param>
/// <param name="Vintage">JUnit 5 with <c>junit-vintage-engine</c>.</param>
/// <param name="Mocking">Mocking libraries as <c>group:artifact:version</c>.</param>
/// <param name="AssertionLibs">Assertion libraries as <c>group:artifact:version</c>.</param>
/// <param name="Evidence">Why this framework was chosen, e.g. <c>dependency junit:junit (test)</c>.</param>
public sealed record TestFrameworkMetadata(
    string? Name,
    string Version,
    bool Vintage,
    IReadOnlyList<string> Mocking,
    IReadOnlyList<string> AssertionLibs,
    string Evidence);

/// <summary><c>project_structure</c> of M (P01 §4.6).</summary>
/// <param name="MultiModule">The build has more than one module or project.</param>
/// <param name="Modules">Module paths from the project root.</param>
/// <param name="TargetModule">Module of the CUT, from the project root.</param>
/// <param name="ModuleRoot">Folder holding the source root of the CUT.</param>
/// <param name="Packages">Up to 30 packages of the source root, the CUT package and its relatives first.</param>
public sealed record ProjectStructureMetadata(
    bool MultiModule,
    IReadOnlyList<string> Modules,
    string TargetModule,
    string ModuleRoot,
    IReadOnlyList<string> Packages);

/// <summary><c>paths</c> of M (P01 §4.7).</summary>
/// <param name="SourceRoot">Source root of the CUT.</param>
/// <param name="TestRoot">Test root of the module.</param>
/// <param name="CutPath">Source file of the CUT.</param>
/// <param name="GeneratedTestPath">Where the generated test goes.</param>
/// <param name="GeneratedTestFqn">FQN of the generated test class.</param>
/// <param name="NameIndex">k of <c>_Arrow&lt;k&gt;Test</c>; 1 for <c>_ArrowTest</c>.</param>
/// <param name="NameMatchesTestIncludes">The generated test matches the project's test name patterns, so <c>mvn test</c> runs it.</param>
public sealed record PathsMetadata(
    string SourceRoot,
    string TestRoot,
    string CutPath,
    string GeneratedTestPath,
    string GeneratedTestFqn,
    int NameIndex,
    bool NameMatchesTestIncludes);

/// <summary><c>cut</c> of M.</summary>
public sealed record CutMetadata(string Fqn, string SimpleName, string Package, CutKind Kind, string SourceSha256);
