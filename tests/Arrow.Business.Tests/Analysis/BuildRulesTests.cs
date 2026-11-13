using Arrow.Business.Analysis;
using Arrow.Common.Analysis;
using Arrow.DataAccess.Build;

namespace Arrow.Business.Tests.Analysis;

/// <summary>
/// Java levels, JDK choice and build root detection (P01 §4.1, §4.3). Parity tests rewritten from
/// <c>ARROW_Framework/tests/Arrow.Service.Tests/Block1Tests.cs</c> and <c>…/IntegrationTests/Block1IntegrationTests.cs</c>.
/// </summary>
public class BuildRulesTests
{
    // Parity: Block1Tests.ParsesManifestJavaMajor; the input is now the output of `java -version` on the machine (doctor, setup).
    [Theory]
    [InlineData("openjdk version \"1.8.0_504\"", 8)]
    [InlineData("openjdk version \"17.0.20.1\" 2026-08-18 LTS", 17)]
    [InlineData("openjdk version \"11.0.32.1\" 2026-07-21", 11)]
    [InlineData("openjdk version \"21\" 2023-09-19", 21)]
    [InlineData("java version \"1.8.0_421\"\nJava(TM) SE Runtime Environment", 8)]
    [InlineData("", null)]
    [InlineData("không rõ", null)]
    public void ParsesRuntimeJavaMajor(string text, int? expected) => Assert.Equal(expected, JavaVersions.ParseRuntimeMajor(text));

    // Parity: Block1Tests.ParsesDeclaredLevels
    [Theory]
    [InlineData("1.7", 7)]
    [InlineData("1.8", 8)]
    [InlineData("11", 11)]
    [InlineData("17.0.2", 17)]
    [InlineData("JavaVersion.VERSION_1_8", 8)]
    [InlineData("VERSION_17", 17)]
    [InlineData("${java.version}", null)]
    public void ParsesDeclaredLevels(string text, int? expected) => Assert.Equal(expected, JavaVersions.ParseLevel(text));

    // Parity: Block1Tests.SelectsSmallestJdkAtLeastDeclared. Expected orders changed by P01 §4.3: there is no manifest JDK any more;
    // without a declared level the choice is JDK 17, then the largest ("default"); the fallbacks are the other JDKs in ascending order.
    [Fact]
    public void SelectsSmallestJdkAtLeastDeclared()
    {
        int[] all = [8, 11, 17, 21];
        Assert.Equal([8, 11, 17, 21], JdkSelector.Select(7, all).Order);
        Assert.Equal("declared", JdkSelector.Select(7, all).Reason);
        Assert.Equal([11, 8, 17, 21], JdkSelector.Select(11, all).Order);
        Assert.Equal([17, 8, 11, 21], JdkSelector.Select(null, all).Order);
        Assert.Equal("default", JdkSelector.Select(null, all).Reason);
        Assert.Equal([21, 8, 11, 17], JdkSelector.Select(19, all).Order);
        Assert.Equal([17, 8], JdkSelector.Select(11, [8, 17]).Order);
        Assert.Equal("fallback:17,8", JdkSelector.FallbackReason([17, 8]));
    }

    [Fact]
    public void JdkChoiceWithoutAGoodEnoughOrAnyJdk()
    {
        var plan = JdkSelector.Select(21, [8, 11, 17]);
        Assert.Equal([17, 8, 11], plan.Order);
        Assert.True(plan.BelowDeclared);
        Assert.False(JdkSelector.Select(17, [8, 17]).BelowDeclared);
        Assert.Equal([11, 8], JdkSelector.Select(null, [8, 11]).Order);
        Assert.Equal(("none", 0), (JdkSelector.Select(8, []).Reason, JdkSelector.Select(8, []).Order.Count));
    }

    // Parity: Block1IntegrationTests.RecognizesTestJvmStartFailures
    [Fact]
    public void RecognizesTestJvmStartFailures()
    {
        Assert.True(JdkSelector.IsJvmStartFailure("[ERROR] The forked VM terminated without properly saying goodbye. VM crash or System.exit called?", 0));
        Assert.True(JdkSelector.IsJvmStartFailure("The stack size specified is too small, Specify at least 640k\nError: Could not create the Java Virtual Machine.", 0));
        Assert.False(JdkSelector.IsJvmStartFailure("[ERROR] The forked VM terminated without properly saying goodbye.", 12));
        Assert.False(JdkSelector.IsJvmStartFailure("[INFO] Tests run: 0, Failures: 0", 0));
        Assert.True(JdkSelector.IsJvmStartFailure("Unrecognized VM option 'UseConcMarkSweepGC'\nError occurred during initialization of VM", 0));
    }

    // Parity: Block1Tests.NoSuccessfulAttemptGivesNull (only the RelativeTo cases are kept).
    [Fact]
    public void RelativePaths()
    {
        Assert.Null(BuildRootResolver.RelativeTo("/w", "/other"));
        Assert.Equal("a/b", BuildRootResolver.RelativeTo(@"C:\w", @"C:\w\a\b"));
        Assert.Equal(".", BuildRootResolver.RelativeTo("/w/", "/w"));
        Assert.Equal("a", BuildRootResolver.RelativeTo(".", "a"));
    }

    // Parity: Block1Tests.GradleOwnerIsDeepestProjectContainingCut
    [Fact]
    public void GradleOwnerIsDeepestProjectContainingCut()
    {
        GradleProject[] projects = [new(":", "/ws/b"), new(":dbfit-java", "/ws/b/dbfit-java"), new(":dbfit-java:core", "/ws/b/dbfit-java/core"), new(":api", "/ws/b/api")];
        Assert.Equal(":dbfit-java:core", BuildRootResolver.GradleOwner(projects, "/ws/b", "dbfit-java/core/src/main/java"));
        Assert.Equal(":api", BuildRootResolver.GradleOwner(projects, "/ws/b", "api/src/main/java"));
        Assert.Equal(":", BuildRootResolver.GradleOwner(projects, "/ws/b", "src/main/java"));
        Assert.Equal(":", BuildRootResolver.GradleOwner(projects, "/ws/b", "apix/src/main/java"));
    }

    // Parity: Block1Tests.ComputesPaths (module part).
    [Fact]
    public void ModuleRootsFromSourceRoots()
    {
        const string cut = "3rdParty/libsodium-jni-2.0.1/src/main/java/org/libsodium/jni/crypto/Util.java";
        var sourceRoot = ProjectPaths.SourceRoot(cut, "org.libsodium.jni.crypto.Util");
        Assert.Equal("3rdParty/libsodium-jni-2.0.1/src/main/java", sourceRoot);
        Assert.Equal("3rdParty/libsodium-jni-2.0.1", BuildRootResolver.ModuleRootFromSourceRoot(sourceRoot));
        Assert.True(BuildRootResolver.ModuleMatches(sourceRoot, "3rdParty/libsodium-jni-2.0.1"));
        Assert.False(BuildRootResolver.ModuleMatches(sourceRoot, "."));
        Assert.True(BuildRootResolver.ModuleMatches("src/java", "."));
        Assert.Equal(".", ProjectPaths.SourceRoot("A.java", "A"));

        // A declared source root: the nearest folder above it with a build file.
        var layout = Layout(Dir(".", pom: true, modules: ["lib"]), Dir("lib", pom: true));
        Assert.Equal("lib", BuildRootResolver.ModuleRoot("lib/src/java", layout));
        Assert.Equal(".", BuildRootResolver.ModuleRoot("java", layout));
        Assert.Null(BuildRootResolver.ModuleRoot("java", Layout()));
    }

    private static BuildDir Dir(string path, bool pom = false, bool gradle = false, bool settings = false, string[]? modules = null, string[]? includes = null) =>
        new(path, pom, gradle, settings, modules ?? [], includes ?? []);

    private static BuildLayout Layout(params BuildDir[] dirs) => new(dirs);

    // P01 §11: reactor, standalone, build root in a sub-folder, module outside the reactor.
    [Fact]
    public void DetectsReactorThenStandalone()
    {
        // maven-multi-junit5: shop-core is a module of the root; samples/standalone is not.
        var layout = Layout(
            Dir(".", pom: true, modules: ["shop-api", "shop-core"]),
            Dir("shop-api", pom: true),
            Dir("shop-core", pom: true),
            Dir("samples/standalone", pom: true));

        var core = BuildRootResolver.Plans(layout, "shop-core");
        Assert.Equal(2, core.Count);
        Assert.Equal(new BuildPlan(BuildToolKind.Maven, ".", BuildMode.Reactor, "shop-core", "detected:reactor"), core[0]);
        Assert.Equal(new BuildPlan(BuildToolKind.Maven, "shop-core", BuildMode.Standalone, null, "detected:standalone"), core[1]);
        Assert.Equal("shop-core", core[0].ExpectedModuleRoot);
        Assert.Equal("shop-core", core[1].ExpectedModuleRoot);

        var outside = Assert.Single(BuildRootResolver.Plans(layout, "samples/standalone"));
        Assert.Equal(("samples/standalone", BuildMode.Standalone), (outside.BuildRoot, outside.Mode));

        // maven-subdir-junit4: the build root is lib/, the project root has no build file.
        var subdir = Assert.Single(BuildRootResolver.Plans(Layout(Dir("lib", pom: true)), "lib"));
        Assert.Equal(("lib", BuildMode.Standalone, "detected:standalone"), (subdir.BuildRoot, subdir.Mode, subdir.Source));

        Assert.Empty(BuildRootResolver.Plans(Layout(), "x"));
    }

    [Fact]
    public void ReactorRootIsTheHighestAggregator()
    {
        // The root declares "modules", which declares "modules/shop-core": the root is the reactor, -pl is the full path.
        var layout = Layout(
            Dir(".", pom: true, modules: ["modules"]),
            Dir("modules", pom: true, modules: ["modules/shop-core"]),
            Dir("modules/shop-core", pom: true));
        Assert.Equal(new BuildPlan(BuildToolKind.Maven, ".", BuildMode.Reactor, "modules/shop-core", "detected:reactor"), BuildRootResolver.Plans(layout, "modules/shop-core")[0]);
        Assert.Equal(["modules", "modules/shop-core"], BuildRootResolver.Modules(layout, ".", BuildToolKind.Maven));

        // A parent that skips a level still counts: the root declares "a/b" directly and "a" has no POM.
        var direct = Layout(Dir(".", pom: true, modules: ["a/b"]), Dir("a/b", pom: true));
        Assert.Equal(".", BuildRootResolver.ReactorRoot(direct, "a/b", BuildToolKind.Maven));
    }

    [Fact]
    public void GradleProjectsAndMixedBuilds()
    {
        // gradle-junit5: settings.gradle includes 'core'.
        var layout = Layout(Dir(".", gradle: true, settings: true, includes: ["core"]), Dir("core", gradle: true));
        var plans = BuildRootResolver.Plans(layout, "core");
        Assert.Equal(new BuildPlan(BuildToolKind.Gradle, ".", BuildMode.Reactor, ":core", "detected:reactor"), plans[0]);
        Assert.Equal(new BuildPlan(BuildToolKind.Gradle, "core", BuildMode.Standalone, ":", "detected:standalone"), plans[1]);
        Assert.Equal("core", plans[0].ExpectedModuleRoot);

        // A subproject without its own build file is configured from the root.
        var noOwnBuild = Layout(Dir(".", gradle: true, settings: true, includes: ["lib"]));
        Assert.Equal(":lib", Assert.Single(BuildRootResolver.Plans(noOwnBuild, "lib")).Module);

        // Both build systems at the build root: Maven, with a warning.
        var warnings = new List<string>();
        var mixed = BuildRootResolver.Plans(Layout(Dir(".", pom: true, gradle: true)), ".", warnings);
        Assert.Equal(BuildToolKind.Maven, Assert.Single(mixed).Tool);
        Assert.Single(warnings);
    }
}
