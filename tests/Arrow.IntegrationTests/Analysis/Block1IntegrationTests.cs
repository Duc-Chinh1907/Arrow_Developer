using Arrow.Business.Analysis;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.Processes;

namespace Arrow.IntegrationTests.Analysis;

/// <summary>
/// Block 1 on the fixtures (P01 §11, P11 §2). Parity tests rewritten from
/// <c>ARROW_Framework/tests/Arrow.IntegrationTests/Block1IntegrationTests.cs</c>: the old input was a manifest record with
/// <c>build_attempts</c>; the product detects the build from the folder (P01 §4.1), so the input is the fixture and the class name,
/// and the expected M stays the same. Every test checks that the user's project is unchanged.
/// </summary>
[Trait("Category", "Integration")]
public class Block1IntegrationTests
{
    // Parity: Block1IntegrationTests.SubdirectoryBuildRootJUnit4
    [Fact]
    public async Task SubdirectoryBuildRootJUnit4()
    {
        await using var harness = await Block1Harness.CreateAsync();
        using var project = new UserProject("maven-subdir-junit4");
        var (analysis, run) = await harness.InspectAsync(project.Root, "Util");

        Assert.True(analysis.Outcome.Outcome == BlockOutcomeKind.Ok, analysis.Outcome.Message + " " + analysis.Outcome.FirstError);
        var m = analysis.Metadata!;
        Assert.Equal((BuildToolKind.Maven, "lib", BuildMode.Standalone, "lib/pom.xml"), (m.BuildTool.Kind, m.BuildTool.BuildRoot, m.BuildTool.BuildMode, m.BuildTool.BuildFile));
        Assert.Equal(("1.8", "maven.compiler.source", 8), (m.Java!.Declared, m.Java.DeclaredSource, m.Java.Major));
        Assert.Equal((8, "declared"), (m.Java.JdkMajor, m.Java.JdkChoiceReason));
        Assert.Equal(("junit4", "4.13.2"), (m.TestFramework!.Name, m.TestFramework.Version));
        Assert.Equal("junit", Assert.Single(m.Dependencies!.Test).Artifact);
        Assert.Empty(m.Dependencies.Direct);
        Assert.Equal(("lib", "lib/src/main/java", "lib/src/test/java"), (m.ProjectStructure.ModuleRoot, m.Paths.SourceRoot, m.Paths.TestRoot));
        Assert.Equal(("lib/src/test/java/com/acme/util/Util_ArrowTest.java", "com.acme.util.Util_ArrowTest", 1), (m.Paths.GeneratedTestPath, m.Paths.GeneratedTestFqn, m.Paths.NameIndex));
        Assert.True(m.Paths.NameMatchesTestIncludes);
        Assert.Equal(["com.acme.util"], m.ProjectStructure.Packages);
        Assert.Empty(m.MissingReasons);

        var baseline = analysis.Baseline!;
        Assert.Equal(["com.acme.util.UtilTest#prependsZeros", "com.acme.util.UtilTest#zeroCountKeepsValue"], baseline.PassingInAllRuns);
        Assert.Equal(["com.acme.util.BrokenTest#alwaysFails"], baseline.FailingInAllRuns);
        Assert.Equal(("lib", 2), (baseline.WorkingDir, baseline.DurationsSeconds.ModuleTest.Count));

        // The snapshot is compiled and has not run any test; the dependency files are not left in it.
        var info = analysis.Workspace!;
        var snapshot = Path.Combine(harness.Home.Root, info.CompiledSnapshot);
        Assert.True(Directory.Exists(Path.Combine(snapshot, "lib", "target", "test-classes", "com", "acme", "util")));
        Assert.False(Directory.Exists(Path.Combine(snapshot, "lib", "target", "surefire-reports")));
        Assert.False(File.Exists(Path.Combine(snapshot, "lib", BuildCommands.MavenDependencyFile)));
        Assert.Equal("detected:standalone", info.BuildRootSource);
        Assert.Empty(info.MavenModuleSelector);

        foreach (var file in new[] { "metadata.json", "metadata_prompt.txt", "dependencies_resolved.txt", "test_classpath.txt", "workspace.json", "baseline.json", "outcome.json" })
        {
            Assert.NotNull(harness.Block1File(run, "com.acme.util.Util", file));
        }

        Assert.Contains("junit-4.13.2.jar", harness.Block1File(run, "com.acme.util.Util", "test_classpath.txt"), StringComparison.Ordinal);
        Assert.Contains("- Test path: lib/src/test/java/com/acme/util/Util_ArrowTest.java", harness.Block1File(run, "com.acme.util.Util", "metadata_prompt.txt"), StringComparison.Ordinal);
        Assert.Contains(analysis.Warnings, w => w.Contains("1 test fail sẵn", StringComparison.Ordinal));
        project.AssertUnchanged();
    }

    // Parity: Block1IntegrationTests.ReactorModuleWithSiblingDependencyJUnit5 and DetectsBuildWhenManifestHasNoAttempts
    [Fact]
    public async Task ReactorModuleWithSiblingDependencyJUnit5()
    {
        await using var harness = await Block1Harness.CreateAsync();
        using var project = new UserProject("maven-multi-junit5");
        var (analysis, run) = await harness.InspectAsync(project.Root, "OrderService");

        Assert.True(analysis.Outcome.Outcome == BlockOutcomeKind.Ok, analysis.Outcome.Message + " " + analysis.Outcome.FirstError);
        var m = analysis.Metadata!;
        Assert.Equal((".", BuildMode.Reactor, "shop-core", "detected:reactor"), (m.BuildTool.BuildRoot, m.BuildTool.BuildMode, m.BuildTool.ReactorModule, m.BuildTool.BuildRootSource));
        Assert.Equal(("11", "maven.compiler.release", 11), (m.Java!.Declared, m.Java.DeclaredSource, m.Java.JdkMajor));
        Assert.Equal("junit5", m.TestFramework!.Name);
        Assert.True(m.ProjectStructure.MultiModule);
        Assert.Equal(["shop-api", "shop-core"], m.ProjectStructure.Modules);
        Assert.Equal("shop-core", m.ProjectStructure.ModuleRoot);
        Assert.Equal("shop-api", Assert.Single(m.Dependencies!.Direct).Artifact);
        Assert.Equal(["-pl", "shop-core", "-am"], analysis.Workspace!.MavenModuleSelector);

        // Nested (@Nested) and @DisplayName tests count for their outer test class.
        Assert.Contains("com.acme.shop.OrderServiceTest$WithoutTax#keepsAmount", analysis.Baseline!.PassingInAllRuns);
        Assert.Equal(3, analysis.Baseline.PassingInAllRuns.Count(t => t.StartsWith("com.acme.shop.OrderServiceTest", StringComparison.Ordinal)));

        // Test classpath: sibling class folders are relative to the workspace root.
        var classpath = harness.Block1File(run, "com.acme.shop.OrderService", "test_classpath.txt")!;
        Assert.Contains("shop-api/target/classes\n", classpath, StringComparison.Ordinal);
        Assert.Contains("junit-jupiter-api-5.10.2.jar", classpath, StringComparison.Ordinal);
        project.AssertUnchanged();
    }

    // Parity: Block1IntegrationTests.ModuleOutsideReactorUsesFallbackAndScreensIgnoredTests (the screening part is gone).
    [Fact]
    public async Task ModuleOutsideReactorIsBuiltStandalone()
    {
        await using var harness = await Block1Harness.CreateAsync();
        using var project = new UserProject("maven-multi-junit5");
        var (analysis, _) = await harness.InspectAsync(project.Root, "com.acme.sample.Greeter");

        Assert.True(analysis.Outcome.Outcome == BlockOutcomeKind.Ok, analysis.Outcome.Message + " " + analysis.Outcome.FirstError);
        Assert.Equal(("samples/standalone", BuildMode.Standalone), (analysis.Metadata!.BuildTool.BuildRoot, analysis.Metadata.BuildTool.BuildMode));
        Assert.Equal("detected:standalone", analysis.Workspace!.BuildRootSource);
        project.AssertUnchanged();
    }

    // Parity: Block1IntegrationTests.GradleProjectJUnit5
    [Fact]
    public async Task GradleProjectJUnit5()
    {
        await using var harness = await Block1Harness.CreateAsync(needsGradle: true);
        using var project = new UserProject("gradle-junit5");
        var (analysis, run) = await harness.InspectAsync(project.Root, "Calculator");

        Assert.True(analysis.Outcome.Outcome == BlockOutcomeKind.Ok, analysis.Outcome.Message + " " + analysis.Outcome.FirstError);
        var m = analysis.Metadata!;
        Assert.Equal((BuildToolKind.Gradle, ".", ":core", "core"), (m.BuildTool.Kind, m.BuildTool.BuildRoot, m.BuildTool.ReactorModule, m.ProjectStructure.ModuleRoot));
        Assert.Equal((17, 17), (m.Java!.Major, m.Java.JdkMajor));
        Assert.Equal(("junit5", "5.10.2"), (m.TestFramework!.Name, m.TestFramework.Version));
        Assert.Equal(["test"], m.Dependencies!.Test.Select(d => d.Scope).Distinct());
        Assert.Equal("core/src/test/java", m.Paths.TestRoot);
        Assert.Contains("core", m.ProjectStructure.Modules);
        Assert.Equal(":core", analysis.Workspace!.GradleProject);
        Assert.Single(analysis.Workspace.InitScripts);
        Assert.NotEmpty(analysis.Baseline!.PassingInAllRuns);
        Assert.All(analysis.Baseline.PassingInAllRuns, t => Assert.StartsWith("com.acme.calc.CalculatorTest#", t, StringComparison.Ordinal));
        Assert.Contains("junit-jupiter-api-5.10.2.jar", harness.Block1File(run, "com.acme.calc.Calculator", "test_classpath.txt"), StringComparison.Ordinal);
        project.AssertUnchanged();
    }

    // P01 §11: maven-no-tests → empty baseline, OK.
    [Fact]
    public async Task ModuleWithoutTestsHasAnEmptyBaseline()
    {
        await using var harness = await Block1Harness.CreateAsync();
        using var project = new UserProject("maven-no-tests");
        var (analysis, _) = await harness.InspectAsync(project.Root, "Slugifier");

        Assert.True(analysis.Outcome.Outcome == BlockOutcomeKind.Ok, analysis.Outcome.Message + " " + analysis.Outcome.FirstError);
        Assert.Empty(analysis.Baseline!.PassingInAllRuns);
        Assert.Empty(analysis.Baseline.FailingInAllRuns);
        Assert.Equal("junit5", analysis.Metadata!.TestFramework!.Name);
        Assert.Equal("src/test/java/com/acme/text/Slugifier_ArrowTest.java", analysis.Metadata.Paths.GeneratedTestPath);
        Assert.Contains(analysis.Warnings, w => w.Contains("chưa có test sẵn", StringComparison.Ordinal));
        project.AssertUnchanged();
    }

    // P01 §11: maven-wrapper-junit5 → wrapper: true.
    [Fact]
    public async Task WrapperIsUsed()
    {
        await using var harness = await Block1Harness.CreateAsync();
        using var project = new UserProject("maven-wrapper-junit5");
        var (analysis, _) = await harness.InspectAsync(project.Root, "Distance");

        Assert.True(analysis.Outcome.Outcome == BlockOutcomeKind.Ok, analysis.Outcome.Message + " " + analysis.Outcome.FirstError);
        var build = analysis.Metadata!.BuildTool;
        Assert.Equal((true, false, "3.9.9"), (build.Wrapper, build.WrapperFallback, build.Version));
        Assert.True(analysis.Workspace!.ExecutableInRepo);
        Assert.Equal(OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw", analysis.Workspace.Executable);
        Assert.Single(analysis.Baseline!.PassingInAllRuns, t => t.StartsWith("com.acme.geo.DistanceTest#", StringComparison.Ordinal));
        project.AssertUnchanged();
    }

    // P01 §11: maven-existing-arrowtest → _Arrow2Test, name_index = 2.
    [Fact]
    public async Task ExistingArrowTestGivesArrow2Test()
    {
        await using var harness = await Block1Harness.CreateAsync();
        using var project = new UserProject("maven-existing-arrowtest");
        var (analysis, _) = await harness.InspectAsync(project.Root, "WordCounter");

        Assert.True(analysis.Outcome.Outcome == BlockOutcomeKind.Ok, analysis.Outcome.Message + " " + analysis.Outcome.FirstError);
        var paths = analysis.Metadata!.Paths;
        Assert.Equal(("src/test/java/com/acme/text/WordCounter_Arrow2Test.java", 2, true), (paths.GeneratedTestPath, paths.NameIndex, paths.NameMatchesTestIncludes));
        Assert.Equal(2, analysis.Cut!.NameIndex);
        Assert.Single(analysis.Baseline!.PassingInAllRuns);
        Assert.Contains(analysis.Warnings, w => w.Contains("WordCounter_Arrow2Test", StringComparison.Ordinal));
        project.AssertUnchanged();
    }

    // TestNG writes its reports twice (junitreports/): each test is counted once.
    [Fact]
    public async Task TestNgModule()
    {
        await using var harness = await Block1Harness.CreateAsync();
        using var project = new UserProject("maven-testng");
        var (analysis, _) = await harness.InspectAsync(project.Root, "Stock");

        Assert.True(analysis.Outcome.Outcome == BlockOutcomeKind.Ok, analysis.Outcome.Message + " " + analysis.Outcome.FirstError);
        Assert.Equal("testng", analysis.Metadata!.TestFramework!.Name);
        Assert.True(analysis.Metadata.Java!.JdkMajor >= 11);
        Assert.Equal(2, analysis.Baseline!.PassingInAllRuns.Count);
        project.AssertUnchanged();
    }

    // Parity: Block1IntegrationTests.SwitchesJdkWhenTestJvmCannotStart
    [Fact]
    public async Task SwitchesJdkWhenTestJvmCannotStart()
    {
        await using var harness = await Block1Harness.CreateAsync();
        var jdk8 = harness.Jdks.GetValueOrDefault(8);
        Assert.SkipWhen(jdk8 is null, "JDK 8 is needed: the fixture declares Java 1.8");
        using var project = new UserProject("maven-small-stack");
        var (analysis, run) = await harness.InspectAsync(project.Root, "Util");

        Assert.True(analysis.Outcome.Outcome == BlockOutcomeKind.Ok, analysis.Outcome.Message + " " + analysis.Outcome.FirstError);
        Assert.Equal(2, analysis.Baseline!.PassingInAllRuns.Count(t => t.StartsWith("com.acme.util.UtilTest#", StringComparison.Ordinal)));

        var java = analysis.Metadata!.Java!;
        if (await JvmStartsAsync(jdk8!, "-Xss256k"))
        {
            Assert.Equal("declared", java.JdkChoiceReason);
        }
        else
        {
            Assert.NotEqual(8, java.JdkMajor);
            Assert.StartsWith("fallback:8,", java.JdkChoiceReason, StringComparison.Ordinal);
            Assert.True(harness.Runs.Exists(run.Run, "targets/com.acme.util.Util/block1/logs/compile-jdk8.log"));
        }

        project.AssertUnchanged();
    }

    // P01 §11 and P07 §13: two CUTs of one module, analysed in parallel, run block 1 once.
    [Fact]
    public async Task TwoCutsOfOneModuleShareBlock1()
    {
        await using var harness = await Block1Harness.CreateAsync();
        using var project = new UserProject("maven-verify-junit4");
        var ct = TestContext.Current.CancellationToken;
        var run = harness.NewRun();
        var workspace = await harness.Analyzer.PrepareAsync(new ProjectTarget(ProjectSourceKind.LocalDirectory, project.Root, null, "PriceCalculator"), run, null, ct);
        var price = Assert.IsType<LocateResult.Found>(await harness.Analyzer.LocateAsync(workspace, "PriceCalculator", ct)).Class;
        var invoice = Assert.IsType<LocateResult.Found>(await harness.Analyzer.LocateAsync(workspace, "com.acme.billing.Invoice", ct)).Class;

        var results = await Task.WhenAll(harness.Analyzer.AnalyzeAsync(workspace, price, run, ct), harness.Analyzer.AnalyzeAsync(workspace, invoice, run, ct));

        Assert.All(results, r => Assert.True(r.Outcome.Outcome == BlockOutcomeKind.Ok, r.Outcome.Message));
        Assert.Single(results, r => r.SharedFrom is null);
        var shared = Assert.Single(results, r => r.SharedFrom is not null);
        var first = results.Single(r => r.SharedFrom is null);
        Assert.Equal(first.Cut!.Fqn, shared.SharedFrom);
        Assert.Equal(first.Workspace!.CompiledSnapshot, shared.Workspace!.CompiledSnapshot);
        Assert.NotEqual(first.Metadata!.Paths.GeneratedTestPath, shared.Metadata!.Paths.GeneratedTestPath);
        Assert.Single(harness.Events, e => e.Type == "B1.Compile");
        Assert.Equal(first.Cut.Fqn + "\n", harness.Block1File(run, shared.Cut!.Fqn, "shared_from.txt"));
        Assert.Null(harness.Block1File(run, shared.Cut.Fqn, "baseline.json"));
        Assert.NotNull(harness.Block1File(run, shared.Cut.Fqn, "metadata.json"));
        project.AssertUnchanged();
    }

    // Parity: Block1IntegrationTests.UnusableTasksAreExcludedWithCodes (B1_CUT_MISSING; the manifest codes are gone).
    [Fact]
    public async Task UnknownAndUnusableClasses()
    {
        await using var harness = await Block1Harness.CreateAsync();
        using var project = new UserProject("maven-multi-junit5");
        var ct = TestContext.Current.CancellationToken;
        var run = harness.NewRun();
        var workspace = await harness.Analyzer.PrepareAsync(new ProjectTarget(ProjectSourceKind.LocalDirectory, project.Root, null, "x"), run, null, ct);

        var missing = Assert.IsType<LocateResult.Failed>(await harness.Analyzer.LocateAsync(workspace, "OrderServise", ct));
        Assert.Equal((ErrorCodes.B1CutMissing, "com.acme.shop.OrderService"), (missing.Code, missing.Suggestions[0]));
        Assert.Equal(ErrorCodes.B1CutNotTestable, Assert.IsType<LocateResult.Failed>(
            await harness.Analyzer.LocateAsync(workspace, "shop-core/src/test/java/com/acme/shop/OrderServiceTest.java", ct)).Code);
        var byPath = await harness.Analyzer.LocateAsync(workspace, Path.Combine(project.Root, "shop-core", "src", "main", "java", "com", "acme", "shop", "OrderService.java"), ct);
        Assert.Equal("com.acme.shop.OrderService", Assert.IsType<LocateResult.Found>(byPath).Class.Fqn);
        project.AssertUnchanged();
    }

    private static async Task<bool> JvmStartsAsync(string jdkHome, string option)
    {
        var java = Path.Combine(jdkHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        var result = await new ProcessRunner().RunAsync(new ProcessSpec(java, [option, "-version"], Path.GetTempPath()), CancellationToken.None);
        return result.Succeeded;
    }
}
