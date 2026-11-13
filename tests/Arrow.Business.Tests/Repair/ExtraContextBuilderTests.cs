using System.IO.Compression;
using System.Text.RegularExpressions;
using Arrow.Business.Analysis;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Generation;
using Arrow.Common.Verification;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Tests.Repair;

/// <summary><c>extra_context</c> by state (P04 §6.5, §12 item 7), with a fake java-helper.</summary>
public sealed partial class ExtraContextBuilderTests : IDisposable
{
    private const string TestFqn = "org.geo.Polygon_ArrowTest";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-extra", Guid.NewGuid().ToString("N")[..8]);

    public ExtraContextBuilderTests()
    {
        var src = Path.Combine(_root, "work", "r1", "snapshot", "src", "main", "java", "org", "geo");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "Bounds.java"), "package org.geo;\n\npublic class Bounds {\n    public Bounds(double w, double h) {}\n}\n");
        File.WriteAllText(Path.Combine(src, "Shape.java"), "package org.geo;\n\npublic abstract class Shape {}\n");
        var util = Path.Combine(_root, "work", "r1", "snapshot", "src", "main", "java", "org", "geo", "util");
        Directory.CreateDirectory(util);
        File.WriteAllText(Path.Combine(util, "Units.java"), "package org.geo.util;\n\npublic class Units {}\n");
    }

    public void Dispose()
    {
        try
        {
            WorkspaceManager.DeleteTree(_root);
        }
        catch (IOException)
        {
        }
    }

    private TaskContext NewTask(string? framework = "junit4", IReadOnlyList<string>? classpath = null, IReadOnlyList<string>? mocking = null) => new(
        new RunContext("r1", _root, Path.Combine(_root, "work", "r1"), new RunHandle("r1", Path.Combine(_root, "runs", "r1")), new Progress<Arrow.Common.Events.ArrowEvent>()),
        "org.geo.Polygon",
        new RepositoryMetadata(
            new SourceMetadata(ProjectSourceKind.LocalDirectory, "geo", null, null, null),
            new BuildToolMetadata(BuildToolKind.Maven, "3.9.6", false, false, ".", BuildMode.Standalone, "pom.xml", null, "detected:standalone", [], []),
            new JavaMetadata("1.8", "maven.compiler.source", 8, 8, "declared"),
            null,
            framework is null ? null : new TestFrameworkMetadata(framework, "1", false, mocking ?? [], [], "x"),
            new ProjectStructureMetadata(false, [], ".", ".", ["org.geo", "org.geo.util"]),
            new PathsMetadata("src/main/java", "src/test/java", "src/main/java/org/geo/Polygon.java", "src/test/java/org/geo/Polygon_ArrowTest.java", TestFqn, 1, true),
            new CutMetadata("org.geo.Polygon", "Polygon", "org.geo", CutKind.Class, "abc"),
            new Dictionary<string, string>()),
        new BaselineInfo(1, new BaselineCommands([], []), ".", [], [], [], new BaselineDurations(0, 0, [])),
        new WorkspaceInfo("work/r1/base", "work/r1/snapshot", null, 8, "/jdk", BuildToolKind.Maven, "mvn", false, ".", BuildMode.Standalone, "detected:standalone", [], null, [], []),
        "package org.geo;\n\npublic class Polygon {\n    private static int created;\n    public Polygon(Bounds b) {}\n}\n",
        "targets/org.geo.Polygon",
        classpath ?? []);

    private static ChainVersion Best(VerifyResult verify, string code = "class Polygon_ArrowTest {}", params string[] failing)
    {
        var candidate = new CandidateTest(true, null, null, code, TestFqn, "src/test/java/org/geo/Polygon_ArrowTest.java", "sha256:x", 1, 1, [], [], "stop");
        var diagnosis = new Diagnosis(verify.State!.Value, StateGroups.GroupOf(verify.State), verify.ErrorSignature, "sha256:x", failing, "", null);
        return new ChainVersion(0, candidate, verify, diagnosis);
    }

    /// <summary>class_api from the package and class declarations of each source, plus the library classes asked for.</summary>
    private static ClassApiResult ApiOf(IReadOnlyList<string> sources, IReadOnlyList<string> classes) => new(
        sources.Select(s => new ClassApiType(
                PackageLine().Match(s).Groups[1].Value + "." + ClassLine().Match(s).Groups[1].Value, "class", "source", [$"public {ClassLine().Match(s).Groups[1].Value}(…)"]))
            .Concat(classes.Select(c => new ClassApiType(c, "class", "classpath", ["public void " + c.Split('.')[^1].ToLowerInvariant() + "()"])))
            .ToList(),
        []);

    [GeneratedRegex(@"package\s+([\w.]+);")]
    private static partial Regex PackageLine();

    [GeneratedRegex(@"class\s+(\w+)")]
    private static partial Regex ClassLine();

    private static CompileError Error(string message, string? symbolKind = null, string? symbol = null, string? location = null) =>
        new("src/test/java/org/geo/Polygon_ArrowTest.java", 10, null, message, JavacErrorParser.Kind(message), symbolKind, symbol, location, InGeneratedFile: true);

    // P04 §6.5: classes from symbol/location and from the four message forms of R03 §6.5.
    [Theory]
    [InlineData("cannot find symbol", "method", "area()", "variable b of type org.geo.Bounds", "org.geo.Bounds")]
    [InlineData("cannot find symbol", "class", "Units", "class org.geo.Polygon_ArrowTest", "Units")]
    [InlineData("constructor Bounds in class org.geo.Bounds cannot be applied to given types;", null, null, null, "org.geo.Bounds")]
    [InlineData("radius has private access in org.geo.Circle", null, null, null, "org.geo.Circle")]
    [InlineData("org.geo.internal.Cache is not public in org.geo.internal; cannot be accessed from outside package", null, null, null, "org.geo.internal.Cache")]
    [InlineData("Shape is abstract; cannot be instantiated", null, null, null, "Shape")]
    [InlineData("incompatible types: java.util.List<java.lang.String> cannot be converted to long", null, null, "class Bounds", "Bounds")]
    public void FindsTheClassesNamedByAnError(string message, string? symbolKind, string? symbol, string? location, string expected) =>
        Assert.Contains(expected, ExtraContextBuilder.MentionedClasses([Error(message, symbolKind, symbol, location)], "org.geo.Polygon", TestFqn));

    [Fact]
    public void TheCutAndTheGeneratedTestAreNotMentioned()
    {
        var names = ExtraContextBuilder.MentionedClasses(
            [Error("cannot find symbol", "method", "x()", "class org.geo.Polygon_ArrowTest"), Error("cannot find symbol", "method", "y()", "variable p of type org.geo.Polygon")],
            "org.geo.Polygon", TestFqn);
        Assert.Empty(names);
    }

    // P04 §12 item 7: the mvn 2.x log gives "class Bounds" as location, so level 2 shows the API of Bounds.
    [Fact]
    public async Task CompileContextOfTheMavenCompilerPlugin2LogHasTheApiOfBounds()
    {
        const string log = "[ERROR] /w/src/test/java/org/geo/Polygon_ArrowTest.java:[65,44] error: cannot find symbol\n[ERROR]  class Bounds\n";
        var errors = JavacErrorParser.Parse(log, ["Polygon_ArrowTest.java"], "/w");
        var helper = new FakeHelper { ClassApi = ApiOf };
        var context = await new ExtraContextBuilder(new FakeHelperProvider(helper))
            .BuildAsync(Best(new VerifyResult { State = VerificationState.Compile, CompileErrors = errors }), NewTask(), TestContext.Current.CancellationToken);

        Assert.StartsWith("Constructors, methods and fields of org.geo.Polygon:\n  public Polygon(…)\n", context.Text, StringComparison.Ordinal);
        Assert.Contains("Constructors, methods and fields of org.geo.Bounds:\n  public Bounds(…)", context.Text, StringComparison.Ordinal);
        Assert.Empty(context.Warnings);
    }

    [Fact]
    public async Task CompileContextReadsLibraryClassesFromTheClasspathAndSkipsUnknownShortNames()
    {
        var verify = new VerifyResult
        {
            State = VerificationState.Compile,
            CompileErrors =
            [
                Error("cannot find symbol", "method", "of(int)", "class com.google.common.collect.ImmutableList"),
                Error("cannot find symbol", "class", "Nowhere", null),
                Error("Shape is abstract; cannot be instantiated"),
                Error("cannot find symbol", "class", "Units", null),
            ],
        };
        var context = await new ExtraContextBuilder(new FakeHelperProvider(new FakeHelper { ClassApi = ApiOf }))
            .BuildAsync(Best(verify), NewTask(), TestContext.Current.CancellationToken);
        Assert.Contains("of com.google.common.collect.ImmutableList:\n  public void immutablelist()", context.Text, StringComparison.Ordinal);
        Assert.Contains("of org.geo.Shape:", context.Text, StringComparison.Ordinal);
        Assert.Contains("of org.geo.util.Units:", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nowhere", context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageDoesNotExistListsThePackagesOfTheTestJars()
    {
        var jar = Path.Combine(_root, "junit-jupiter-api-5.10.0.jar");
        using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            zip.CreateEntry("org/junit/jupiter/api/Test.class");
            zip.CreateEntry("org/junit/jupiter/api/extension/Extension.class");
            zip.CreateEntry("META-INF/versions/9/module-info.class");
        }

        var verify = new VerifyResult { State = VerificationState.Compile, CompileErrors = [Error("package org.junit does not exist")] };
        var context = await new ExtraContextBuilder(new FakeHelperProvider(new FakeHelper { ClassApi = ApiOf }))
            .BuildAsync(Best(verify), NewTask(classpath: [jar, Path.Combine(_root, "missing.jar"), _root]), TestContext.Current.CancellationToken);
        Assert.EndsWith("Packages that exist in the project's test dependencies:\n  org.junit.jupiter", context.Text, StringComparison.Ordinal);
        Assert.Equal(["org.junit.jupiter"], ExtraContextBuilder.TestPackages([jar]));
    }

    [Fact]
    public async Task AFailingJavaHelperLeavesThePartOutWithAWarning()
    {
        var helper = new FakeHelper { Failure = new JavaHelperCommandException("class_api", "boom") };
        var verify = new VerifyResult { State = VerificationState.Compile, CompileErrors = [Error("package org.junit does not exist")] };
        var context = await new ExtraContextBuilder(new FakeHelperProvider(helper)).BuildAsync(Best(verify), NewTask(), TestContext.Current.CancellationToken);
        Assert.Equal(["class_api: java-helper class_api: boom"], context.Warnings);
        Assert.StartsWith("Packages that exist", context.Text, StringComparison.Ordinal);

        var runtime = await new ExtraContextBuilder(new FakeHelperProvider(new FakeHelper { Failure = new ArrowException(ErrorCodes.JhTimeout, "timeout") }))
            .BuildAsync(Best(new VerifyResult { State = VerificationState.Assertion }), NewTask(), TestContext.Current.CancellationToken);
        Assert.Single(runtime.Warnings);
    }

    [Fact]
    public async Task UnknownShowsTheBuildErrorAndTheBuildConfiguration()
    {
        var excerpt = string.Join('\n', Enumerable.Range(1, 130).Select(i => $"[ERROR] line {i}"));
        var context = await new ExtraContextBuilder(new FakeHelperProvider(new FakeHelper()))
            .BuildAsync(Best(new VerifyResult { State = VerificationState.Unknown, BuildErrorExcerpt = excerpt }), NewTask(), TestContext.Current.CancellationToken);
        Assert.StartsWith("Full build error:\n[ERROR] line 1\n", context.Text, StringComparison.Ordinal);
        Assert.Contains("… (11 more lines)", context.Text, StringComparison.Ordinal);
        Assert.Contains("Build configuration:\n- Build tool: Maven 3.9.6", context.Text, StringComparison.Ordinal);
        Assert.Contains("- Java version: 8", context.Text, StringComparison.Ordinal);
        Assert.Contains("- Test framework: JUnit 4 (1)", context.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("junit4", "import org.junit.Test;", "public class Polygon_ArrowTest")]
    [InlineData("junit5", "import org.junit.jupiter.api.Test;", "    void exampleTest()")]
    [InlineData("testng", "import org.testng.annotations.Test;", "    public void exampleTest()")]
    [InlineData(null, "import org.junit.Test;", "JUnit 4")]
    public async Task DiscoveryShowsASkeletonOfTheFramework(string? framework, string import, string expected)
    {
        var context = await new ExtraContextBuilder(new FakeHelperProvider(new FakeHelper()))
            .BuildAsync(Best(new VerifyResult { State = VerificationState.Discovery }), NewTask(framework), TestContext.Current.CancellationToken);
        Assert.Contains(import, context.Text, StringComparison.Ordinal);
        Assert.Contains(expected, context.Text, StringComparison.Ordinal);
        Assert.Contains("package org.geo;", context.Text, StringComparison.Ordinal);
        Assert.Contains("(file src/test/java/org/geo/Polygon_ArrowTest.java)", context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeShowsProjectMethodsOfTheTraceTheConstructionAndMockito()
    {
        var verify = new VerifyResult
        {
            State = VerificationState.Runtime,
            Failures =
            [
                new FailureInfo("area", "java.lang.NullPointerException", null,
                    "java.lang.NullPointerException\n\tat org.geo.Bounds.width(Bounds.java:9)\n\tat org.geo.Polygon.area(Polygon.java:20)\n\tat org.geo.Polygon_ArrowTest.area(Polygon_ArrowTest.java:5)\n\tat java.base/java.lang.Thread.run(Thread.java:1)"),
            ],
        };
        var helper = new FakeHelper
        {
            MethodSources = [new MethodSourceEntry("width", "double width() { return b.w; }"), new MethodSourceEntry("area", "double area() { return bounds.width(); }")],
            Called = new CalledMethodsResult(true, ["area"], [], ["public Polygon(Bounds b)"], ["private Bounds bounds"]),
        };
        var context = await new ExtraContextBuilder(new FakeHelperProvider(helper))
            .BuildAsync(Best(verify, failing: "area"), NewTask(mocking: ["org.mockito:mockito-core:4.11.0"]), TestContext.Current.CancellationToken);
        Assert.Contains("// org.geo.Bounds\ndouble width() { return b.w; }", context.Text, StringComparison.Ordinal);
        Assert.Contains("// org.geo.Polygon\ndouble area() { return bounds.width(); }", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread", context.Text, StringComparison.Ordinal);
        Assert.Contains("Constructors of org.geo.Polygon:\n```java\npublic Polygon(Bounds b)\n```", context.Text, StringComparison.Ordinal);
        Assert.Contains("  private Bounds bounds\n", context.Text, StringComparison.Ordinal);
        Assert.EndsWith("instead of constructing them.", context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertionShowsExpectedActualAndTheCalledMethods()
    {
        var verify = new VerifyResult
        {
            State = VerificationState.Assertion,
            Failures =
            [
                new FailureInfo("area", "java.lang.AssertionError", "expected:<12.0> but was:<0.0>"),
                new FailureInfo("perimeter", "org.opentest4j.AssertionFailedError", "expected: <4> but was: <5>"),
                new FailureInfo("empty", "org.opentest4j.AssertionFailedError", null),
            ],
        };
        var helper = new FakeHelper { Called = new CalledMethodsResult(true, ["area"], [new MethodSourceEntry("area", "double area() { return 0; }")], [], []) };
        var context = await new ExtraContextBuilder(new FakeHelperProvider(helper))
            .BuildAsync(Best(verify, failing: ["area", "perimeter"]), NewTask(), TestContext.Current.CancellationToken);
        Assert.Contains("- area: expected 12.0, actual 0.0\n- perimeter: expected 4, actual 5\n- empty: AssertionFailedError\n", context.Text, StringComparison.Ordinal);
        Assert.Contains("Source of the Polygon methods called by the failing test methods:\n```java\ndouble area() { return 0; }\n```", context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegressionShowsBrokenTestsStaticFieldsAndSharedState()
    {
        var verify = new VerifyResult
        {
            State = VerificationState.Regression,
            Regressions =
            [
                new RegressionInfo("org.geo.AreaTest#sums", RegressionKind.Failed, "java.lang.AssertionError", "expected:<2> but was:<3>\nmore"),
                new RegressionInfo("org.geo.AreaTest#counts", RegressionKind.Missing),
            ],
        };
        const string code = "import java.io.File;\nclass Polygon_ArrowTest {\n  void t() {\n    System.setProperty(\"geo.unit\", \"m\");\n    Polygon.setDefaultScale(2);\n    new File(\"x.txt\").delete();\n  }\n}";
        var helper = new FakeHelper { Called = new CalledMethodsResult(true, [], [], [], ["private static int created", "private static final int MAX = 3", "private Bounds bounds"]) };
        var context = await new ExtraContextBuilder(new FakeHelperProvider(helper))
            .BuildAsync(Best(verify, code), NewTask(), TestContext.Current.CancellationToken);
        Assert.Contains("- org.geo.AreaTest#sums: java.lang.AssertionError: expected:<2> but was:<3>\n- org.geo.AreaTest#counts: was not run\n", context.Text, StringComparison.Ordinal);
        Assert.Contains("Static fields of org.geo.Polygon (shared by every test in the JVM):\n  private static int created\nCode", context.Text, StringComparison.Ordinal);
        Assert.Contains("  System.setProperty(\"geo.unit\", \"m\");\n  Polygon.setDefaultScale(2);\n  new File(\"x.txt\").delete();", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("import java.io.File", context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextIsCutAt200LinesWithATruncatedLine()
    {
        var text = string.Join('\n', Enumerable.Range(1, 250).Select(i => $"l{i}"));
        var cut = ExtraContextBuilder.Truncate(text).Split('\n');
        Assert.Equal(200, cut.Length);
        Assert.Equal(("l199", "… (truncated)"), (cut[^2], cut[^1]));
        Assert.Equal("a\nb", ExtraContextBuilder.Truncate("a\nb"));
    }
}
