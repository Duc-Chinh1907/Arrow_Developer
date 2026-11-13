using Arrow.Business.Analysis;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.DataAccess.Build;

namespace Arrow.Business.Tests.Analysis;

/// <summary>
/// Test framework, test file name, baseline groups and the text form of M (P01 §4.5, §4.7, §5.2, §6.2). Parity tests rewritten from
/// <c>ARROW_Framework/tests/Arrow.Service.Tests/Block1Tests.cs</c>.
/// </summary>
public class MetadataRulesTests
{
    private static ResolvedDependency Dep(string group, string artifact, string version = "1.0", string scope = "test") => new(group, artifact, version, scope, null);

    // Parity: Block1Tests.DetectsTestFrameworks. The manifest case is replaced by name = null (P01 §4.5).
    [Fact]
    public void DetectsTestFrameworks()
    {
        var junit5 = TestFrameworkDetector.Detect([Dep("org.junit.jupiter", "junit-jupiter-api", "5.9.2"), Dep("org.junit.vintage", "junit-vintage-engine"), Dep("org.mockito", "mockito-core", "4.0.0"), Dep("org.assertj", "assertj-core", "3.24.2")]);
        Assert.Equal(("junit5", "5.9.2", true), (junit5.Name, junit5.Version, junit5.Vintage));
        Assert.Equal(["org.mockito:mockito-core:4.0.0"], junit5.Mocking);
        Assert.Equal(["org.assertj:assertj-core:3.24.2"], junit5.AssertionLibs);

        var junit4 = TestFrameworkDetector.Detect([Dep("junit", "junit", "4.11"), Dep("org.hamcrest", "hamcrest-core", "1.3")]);
        Assert.Equal(("junit4", "dependency junit:junit (test)"), (junit4.Name, junit4.Evidence));
        Assert.Empty(junit4.AssertionLibs);

        // TestNG and JUnit together: the imports of the existing tests decide.
        List<ResolvedDependency> both = [Dep("org.testng", "testng", "7.4.0"), Dep("junit", "junit", "4.12")];
        Assert.Equal("testng", TestFrameworkDetector.Detect(both, ["import org.testng.annotations.Test;\nclass A {}"]).Name);
        Assert.Equal("junit4", TestFrameworkDetector.Detect(both, ["import org.junit.Test;\nclass A {}"]).Name);
        Assert.Equal("testng", TestFrameworkDetector.Detect([Dep("org.testng", "testng")]).Name);

        var none = TestFrameworkDetector.Detect([]);
        Assert.Equal((null, TestFrameworkDetector.NoFrameworkReason), (none.Name, none.Evidence));
    }

    [Fact]
    public void SeveralFrameworksFollowTheMajorityOfImports()
    {
        // JUnit 5 projects often get junit:junit transitively (e.g. from testcontainers): the imports keep junit5.
        List<ResolvedDependency> deps = [Dep("junit", "junit", "4.13.2"), Dep("org.junit.jupiter", "junit-jupiter", "5.10.2")];
        string[] sources =
        [
            "import org.junit.jupiter.api.Test;", "import static org.junit.jupiter.api.Assertions.*;", "import org.junit.Test;",
        ];
        var result = TestFrameworkDetector.Detect(deps, sources);
        Assert.Equal(("junit5", "5.10.2"), (result.Name, result.Version));
        Assert.Contains("imports 2 org.junit.jupiter, 1 org.junit, 0 org.testng", result.Evidence, StringComparison.Ordinal);

        // No test files: junit5, then junit4, then testng.
        Assert.Equal("junit5", TestFrameworkDetector.Detect(deps, []).Name);
        Assert.Equal("junit4", TestFrameworkDetector.Detect([Dep("org.testng", "testng"), Dep("junit", "junit")], []).Name);

        // A declared hamcrest-core is an assertion library even with JUnit 4.
        var declared = TestFrameworkDetector.Detect([Dep("junit", "junit", "4.13.2"), Dep("org.hamcrest", "hamcrest-core", "1.3")], null, [new DependencyRef("org.hamcrest", "hamcrest-core", "1.3", "test")]);
        Assert.Equal(["org.hamcrest:hamcrest-core:1.3"], declared.AssertionLibs);
    }

    // Parity: Block1Tests.ComputesPaths (name part). The collision name changes from B_ArrowTest2 to B_Arrow2Test (P01 §4.7).
    [Fact]
    public void ComputesTestPaths()
    {
        var first = TestPathPlanner.Plan("shop-core/src/test/java", "com.acme.shop.OrderService", (_, _) => false)!;
        Assert.Equal("shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java", first.Path);
        Assert.Equal("com.acme.shop.OrderService_ArrowTest", first.Fqn);
        Assert.Equal(1, first.NameIndex);

        var second = TestPathPlanner.Plan("src/test/java", "a.B", (_, name) => name == "B_ArrowTest")!;
        Assert.Equal(("src/test/java/a/B_Arrow2Test.java", "a.B_Arrow2Test", 2), (second.Path, second.Fqn, second.NameIndex));

        Assert.Equal(["com.acme.shop", "com.acme", "com.acme.shop.repo", "org.other"],
            ProjectPaths.RankPackages(["org.other", "com.acme.shop.repo", "com.acme", "com.acme.shop"], "com.acme.shop"));
        Assert.Equal("org.x", ProjectPaths.PackageOf("/* package wrong; */\npackage org.x;\nclass A {}"));
        Assert.Equal("", ProjectPaths.PackageOf("// package wrong;\nclass A {}"));
    }

    // P01 §11: _ArrowTest, _Arrow2Test when the file exists, _Arrow3Test when both exist, a name used in the reactor, k = 99.
    [Fact]
    public void TestNamesSkipTakenFilesAndReactorNames()
    {
        HashSet<string> files = ["src/test/java/a/B_ArrowTest.java", "src/test/java/a/B_Arrow2Test.java"];
        Assert.Equal("a.B_Arrow3Test", TestPathPlanner.Plan("src/test/java", "a.B", (path, _) => files.Contains(path))!.Fqn);

        // Another module of the reactor already has a test class named B_ArrowTest (any package).
        HashSet<string> reactorNames = ["B_ArrowTest"];
        Assert.Equal(2, TestPathPlanner.Plan("src/test/java", "a.B", (_, name) => reactorNames.Contains(name))!.NameIndex);

        Assert.Equal("Default_ArrowTest", TestPathPlanner.Plan("src/test/java", "Default", (_, _) => false)!.Fqn);
        Assert.Null(TestPathPlanner.Plan("src/test/java", "a.B", (_, _) => true));
        Assert.Equal("B_Arrow99Test", TestPathPlanner.TestName("B", 99));
    }

    // P01 §11: Surefire patterns; defaults; name_matches_test_includes true and false.
    [Fact]
    public void TestNamePatternsMatchLikeSurefire()
    {
        var defaults = EffectivePomReader.DefaultSurefireIncludes;
        Assert.True(TestNamePatterns.Matches("a/b/Order_ArrowTest.java", defaults, []));
        Assert.True(TestNamePatterns.Matches("a/b/Order_Arrow2Test.java", defaults, []));
        Assert.True(TestNamePatterns.Matches("Order_ArrowTest.java", defaults, []));
        Assert.False(TestNamePatterns.Matches("a/Order_ArrowTest.java", ["**/*Spec.java"], []));
        Assert.False(TestNamePatterns.Matches("a/Order_ArrowTest.java", defaults, ["**/*_Arrow*Test.java"]));
        Assert.True(TestNamePatterns.Matches("a/Order_ArrowTest.java", ["*Test"], []));
        Assert.True(TestNamePatterns.Matches("a/Order_ArrowTest.java", ["%regex[.*_Arrow.*Test.*]"], []));
        Assert.True(TestNamePatterns.Matches("a/Order_ArrowTest.java", ["**/*Test.class"], []));
        Assert.True(TestNamePatterns.Matches("a/Order_ArrowTest.java", [], []));
        Assert.False(TestNamePatterns.Matches("a/Order_ArrowTest.java", ["b/**/*Test.java"], []));
    }

    private static TestCaseResult T(string cls, string name, TestCaseStatus status) => new(cls, name, status);

    // Parity: Block1Tests.ClassifiesBaselineRuns
    [Fact]
    public void ClassifiesBaselineRuns()
    {
        IReadOnlyList<TestCaseResult> run1 = [T("a.ATest", "ok", TestCaseStatus.Passed), T("a.ATest", "bad", TestCaseStatus.Failed), T("a.ClockTest", "now", TestCaseStatus.Passed), T("a.ATest", "gone", TestCaseStatus.Passed), T("a.ATest", "ignored", TestCaseStatus.Skipped)];
        IReadOnlyList<TestCaseResult> run2 = [T("a.ATest", "ok", TestCaseStatus.Passed), T("a.ATest", "bad", TestCaseStatus.Error), T("a.ClockTest", "now", TestCaseStatus.Failed), T("a.ATest", "ignored", TestCaseStatus.Skipped)];
        var groups = BaselineEvaluator.Classify([run1, run2]);
        Assert.Equal(["a.ATest#ok"], groups.Passing);
        Assert.Equal(["a.ATest#gone", "a.ClockTest#now"], groups.Flaky);
        Assert.Equal(["a.ATest#bad"], groups.Failing);

        // A test reported twice in one run (TestNG suite and class report) counts once, with its worst status.
        IReadOnlyList<TestCaseResult> duplicated = [T("a.ATest", "x", TestCaseStatus.Passed), T("a.ATest", "x", TestCaseStatus.Passed)];
        Assert.Equal(["a.ATest#x"], BaselineEvaluator.Classify([duplicated, duplicated]).Passing);
        Assert.Empty(BaselineEvaluator.Classify([[], []]).Passing);
    }

    private static RepositoryMetadata Metadata(TestFrameworkMetadata? framework, JavaMetadata? java, BuildToolKind tool = BuildToolKind.Maven, bool multi = true) => new(
        new SourceMetadata(ProjectSourceKind.LocalDirectory, "MyShop", null, null, null),
        new BuildToolMetadata(tool, "3.9.6", false, false, ".", BuildMode.Reactor, "pom.xml", "shop-core", "detected:reactor", [], []),
        java,
        new DependencyMetadata(
            [new DependencyRef("com.google.guava", "guava", "30.1-jre", "compile")],
            [new DependencyRef("junit", "junit", "4.13.2", "test")],
            12),
        framework,
        new ProjectStructureMetadata(multi, ["shop-api", "shop-core"], "shop-core", "shop-core", ["com.acme.shop"]),
        new PathsMetadata("shop-core/src/main/java", "shop-core/src/test/java", "shop-core/src/main/java/com/acme/shop/OrderService.java",
            "shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java", "com.acme.shop.OrderService_ArrowTest", 1, true),
        new CutMetadata("com.acme.shop.OrderService", "OrderService", "com.acme.shop", CutKind.Class, "abc"),
        new Dictionary<string, string>());

    // Parity: Block1Tests.RendersMetadataPromptLikeSpecExample
    [Fact]
    public void RendersMetadataPromptLikeSpecExample()
    {
        var m = Metadata(
            new TestFrameworkMetadata("junit4", "4.13.2", false, ["org.mockito:mockito-core:3.12.4"], [], "dependency junit:junit (test)"),
            new JavaMetadata("1.8", "maven.compiler.source", 8, 8, "declared"));
        Assert.Equal("""
            - Build tool: Maven 3.9.6 (module shop-core, multi-module project, build root .)
            - Java version: 8 (compile with JDK 8; do not use language features or APIs newer than Java 8)
            - Test framework: JUnit 4 (4.13.2); mocking: Mockito 3.12.4; assertions: none beyond JUnit
            - Dependencies (direct): junit:junit:4.13.2, com.google.guava:guava:30.1-jre
            - Project structure: modules shop-api, shop-core; CUT package com.acme.shop
            - Source path: shop-core/src/main/java
            - Test path: shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java
            """.Replace("\r\n", "\n", StringComparison.Ordinal), MetadataPromptRenderer.Render(m));
    }

    [Fact]
    public void RendersMissingGroupsAndGradle()
    {
        var text = MetadataPromptRenderer.Render(Metadata(null, null, BuildToolKind.Gradle) with
        {
            BuildTool = new BuildToolMetadata(BuildToolKind.Gradle, "8.5", true, false, ".", BuildMode.Reactor, "build.gradle", ":core", "detected:reactor", [], []),
        });
        Assert.Contains("- Build tool: Gradle 8.5 (project :core, multi-project build, build root .)", text, StringComparison.Ordinal);
        Assert.Contains("- Java version: unknown", text, StringComparison.Ordinal);
        Assert.Contains("- Test framework: none found in the test dependencies", text, StringComparison.Ordinal);
        Assert.Equal(7, text.Split('\n').Length);
        Assert.Equal("AssertJ 3.24.2", MetadataPromptRenderer.Library("org.assertj:assertj-core:3.24.2"));
    }
}
