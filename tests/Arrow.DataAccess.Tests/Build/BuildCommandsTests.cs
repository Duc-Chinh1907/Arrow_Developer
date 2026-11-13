using System.Xml.Linq;
using Arrow.Common.Analysis;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.Processes;

namespace Arrow.DataAccess.Tests.Build;

/// <summary>
/// Command lines, failure classification and report reading (P01 §4.2, §6). Parity tests rewritten from
/// <c>ARROW_Framework/tests/Arrow.Service.Tests/Block1Tests.cs</c>.
/// </summary>
public class BuildCommandsTests
{
    private static BuildSetup Maven(BuildMode mode, string? module) =>
        new(BuildToolKind.Maven, "mvn", false, ".", mode, module, BuildCommands.MavenCommonFlags("3.9.6", windows: false), null);

    // Parity: Block1Tests.BuildsMavenCommandsWithSelector
    [Fact]
    public void BuildsMavenCommandsWithSelector()
    {
        var command = BuildCommands.Command(Maven(BuildMode.Reactor, "shop-core"), "/r", BuildCommands.MavenModuleTest);
        Assert.Equal(["mvn", "-f", "pom.xml", "-B", "-ntp"], command.Take(5));
        Assert.Equal(["-pl", "shop-core", "-am", "-DfailIfNoTests=false"], command.SkipWhile(c => c != "-pl").Take(4));
        Assert.Equal("test", command[^1]);
        Assert.DoesNotContain("-ntp", BuildCommands.MavenCommonFlags("3.5.4", windows: false));
        Assert.Contains("-Dsurefire.useManifestOnlyJar=false", BuildCommands.MavenCommonFlags("3.9.6", windows: true));
        Assert.EndsWith("3.1.2:list", BuildCommands.MavenCompileWithDependencies("3.5.4")[2], StringComparison.Ordinal);
        Assert.EndsWith("3.8.1:list", BuildCommands.MavenCompileWithDependencies("3.9.6")[2], StringComparison.Ordinal);

        var standalone = BuildCommands.Command(Maven(BuildMode.Standalone, null), "/r", ["-v"]);
        Assert.DoesNotContain("-pl", standalone);

        // A wrapper is resolved against each copy of the project.
        var copy = Path.Combine(Path.GetTempPath(), "copy1");
        var wrapper = Maven(BuildMode.Standalone, null) with { Executable = "sub/mvnw", ExecutableInRepo = true };
        Assert.Equal(Path.Combine(copy, "sub", "mvnw"), BuildCommands.Command(wrapper, copy, ["-v"])[0]);

        var gradleSetup = new BuildSetup(BuildToolKind.Gradle, "gradlew", false, ".", BuildMode.Reactor, ":core", BuildCommands.GradleCommonFlags, "/t/arrow-info.gradle");
        var gradle = BuildCommands.Command(gradleSetup, "/r", BuildCommands.GradleModuleTest(":core"));
        Assert.Equal(["gradlew", "--no-daemon", "--console=plain", "-Dorg.gradle.configuration-cache=false", "-Dorg.gradle.unsafe.configuration-cache=false", "--init-script", "/t/arrow-info.gradle", ":core:test", "--continue"], gradle);
    }

    [Fact]
    public void PluginVersionsFollowMavenVersion()
    {
        Assert.EndsWith(":3.4.1:effective-pom", BuildCommands.MavenEffectivePom("3.9.9", "/w/pom.xml")[0], StringComparison.Ordinal);
        Assert.EndsWith(":3.2.0:effective-pom", BuildCommands.MavenEffectivePom("3.6.2", "/w/pom.xml")[0], StringComparison.Ordinal);
        Assert.True(BuildCommands.IsModernMaven(null));
        Assert.True(BuildCommands.CompareVersions("3.10.0", "3.9.9") > 0);
        Assert.Equal(0, BuildCommands.CompareVersions("3.6", "3.6.0"));
        Assert.Equal(["-pl", "a/b", "-am"], BuildCommands.MavenSelector(Maven(BuildMode.Reactor, "a/b")));
        Assert.Empty(BuildCommands.MavenSelector(Maven(BuildMode.Standalone, null)));
    }

    private static ProcessResult R(int? exit, string output, bool timedOut = false) => new(exit, timedOut, output, TimeSpan.Zero);

    // Parity: Block1Tests.ClassifiesBuildFailures (build part; the git part moves to GitClientTests).
    [Fact]
    public void ClassifiesBuildFailures()
    {
        Assert.Equal(BuildFailureKind.None, BuildFailureClassifier.Classify(R(0, "BUILD SUCCESS")));
        Assert.Equal(BuildFailureKind.Timeout, BuildFailureClassifier.Classify(R(null, "", timedOut: true)));
        Assert.Equal(BuildFailureKind.Network, BuildFailureClassifier.Classify(R(1, "Could not transfer artifact x: Connection reset")));
        Assert.Equal(BuildFailureKind.Network, BuildFailureClassifier.Classify(R(1, "Could not GET 'https://repo/x.pom'. Received status code 502 from server")));
        Assert.Equal(BuildFailureKind.Build, BuildFailureClassifier.Classify(R(1, "[ERROR] COMPILATION ERROR : cannot find symbol")));
        Assert.Equal(BuildFailureKind.Build, BuildFailureClassifier.Classify(R(1, "Could not transfer artifact x: status code: 404")));
        Assert.Equal("3.9.6", BuildFailureClassifier.ToolVersion("Apache Maven 3.9.6 (bc0240f3c744dd6b6ec2920b3cd08dcc295161ae)"));
        Assert.Equal("8.5", BuildFailureClassifier.ToolVersion("\n------\nGradle 8.5\n------"));
    }

    [Fact]
    public void FindsMissingArtifactsAndFirstError()
    {
        Assert.True(BuildFailureClassifier.IsMissingArtifact("[ERROR] Failed to execute goal on project x: Could not resolve dependencies for project a:b:jar:1: Could not find artifact c:d:jar:9"));
        Assert.False(BuildFailureClassifier.IsMissingArtifact("[ERROR] COMPILATION ERROR"));

        const string log = """
            [INFO] Compiling 3 source files
            [ERROR] COMPILATION ERROR :
            [ERROR] /w/src/main/java/a/Order.java:[3,24] package javax.annotation does not exist
            [ERROR] Failed to execute goal
            """;
        Assert.Equal("[ERROR] /w/src/main/java/a/Order.java:[3,24] package javax.annotation does not exist", BuildFailureClassifier.FirstError(log));
        Assert.Equal("[ERROR] Failed to execute goal x", BuildFailureClassifier.FirstError("[INFO] a\n[ERROR] Failed to execute goal x\n"));
        Assert.Null(BuildFailureClassifier.FirstError(""));
    }

    // Parity: Block1Tests.RemovesOnlyCorruptJarsInsideBuildCaches, with the cache folder passed in instead of the real ~/.m2.
    [Fact]
    public void RemovesOnlyCorruptJarsInsideBuildCaches()
    {
        using var temp = new TempDir();
        var cache = Path.Combine(temp.Root, ".m2", "repository");
        var inCache = Path.Combine(cache, "g", "x-1.0.jar");
        var valid = Path.Combine(cache, "g", "ok-1.0.jar");
        var outside = Path.Combine(temp.Root, "outside.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(inCache)!);
        File.WriteAllText(inCache, "<!doctype html><html></html>");
        File.WriteAllText(outside, "<!doctype html><html></html>");
        using (var zip = System.IO.Compression.ZipFile.Open(valid, System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("META-INF/MANIFEST.MF");
        }

        var log = $"[ERROR] error reading {inCache}; zip END header not found\n[ERROR] error reading {outside}; error in opening zip file\n[ERROR] error reading {valid}; x";
        Assert.Equal(1, BuildFailureClassifier.RemoveCorruptCachedJars(log, [cache]));
        Assert.False(File.Exists(inCache));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(valid));
        Assert.Equal(0, BuildFailureClassifier.RemoveCorruptCachedJars("BUILD SUCCESS", [cache]));
        Assert.Equal(Path.Combine("/h", ".m2", "repository"), BuildFailureClassifier.UserCaches("/h")[0]);
    }

    // Parity: Block1Tests.ReadsSurefireXml
    [Fact]
    public void ReadsSurefireXml()
    {
        var xml = XDocument.Parse("""
            <testsuite name="a.UtilTest" tests="4">
              <testcase name="ok" classname="a.UtilTest" time="0.01"/>
              <testcase name="fails" classname="a.UtilTest" time="1,234.5"><failure message="expected:&lt;1&gt;" type="java.lang.AssertionError"/></testcase>
              <testcase name="throws" classname="a.UtilTest$Inner"><error message="boom" type="java.lang.IllegalStateException"/></testcase>
              <testcase name="skip"><skipped/></testcase>
            </testsuite>
            """);
        var tests = TestReportReader.Parse(xml);
        Assert.Equal([TestCaseStatus.Passed, TestCaseStatus.Failed, TestCaseStatus.Error, TestCaseStatus.Skipped], tests.Select(t => t.Status));
        Assert.Equal("a.UtilTest$Inner#throws", tests[2].Id);
        Assert.Equal("a.UtilTest", tests[2].OuterClassName);
        Assert.Equal("a.UtilTest", tests[3].ClassName);
        Assert.Equal("java.lang.AssertionError", tests[1].ExceptionType);

        // Surefire 3.5 + JUnit 5: classname is the method name (pilot samples 9577793, 93861612).
        var junit5 = TestReportReader.Parse(XDocument.Parse("""
            <testsuite name="amforeas.client.RequestParamsTest">
              <testcase name="testInsert" classname="testInsert()"/>
              <testcase name="checkMode" classname="checkMode"/>
              <testcase name="nested" classname="amforeas.client.RequestParamsTest$Inner"/>
              <testcase name="shows" classname="Request params work"/>
            </testsuite>
            """));
        Assert.Equal(["amforeas.client.RequestParamsTest", "amforeas.client.RequestParamsTest", "amforeas.client.RequestParamsTest$Inner", "amforeas.client.RequestParamsTest"], junit5.Select(t => t.ClassName));
        Assert.Equal("DefaultTest", TestReportReader.ClassNameOf("DefaultTest", "DefaultTest"));

        // TestNG writes copies into junitreports/: only files directly in surefire-reports/ or test-results/<task>/ are read.
        var sep = Path.DirectorySeparatorChar;
        Assert.True(TestReportReader.IsReport($"{sep}r{sep}m{sep}target{sep}surefire-reports{sep}TEST-a.ATest.xml"));
        Assert.False(TestReportReader.IsReport($"{sep}r{sep}m{sep}target{sep}surefire-reports{sep}junitreports{sep}TEST-a.ATest.xml"));
        Assert.True(TestReportReader.IsReport($"{sep}r{sep}core{sep}build{sep}test-results{sep}test{sep}TEST-a.ATest.xml"));
        Assert.False(TestReportReader.IsReport($"{sep}r{sep}src{sep}test{sep}resources{sep}TEST-a.ATest.xml"));
        Assert.Equal("adds", TestReportReader.MethodName("adds()"));
    }

    [Fact]
    public void ReadsEveryModuleSkipsJunitReportsAndBrokenFiles()
    {
        using var temp = new TempDir();
        void Write(string relative, string content)
        {
            var path = Path.Combine(temp.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Write("a/target/surefire-reports/TEST-a.ATest.xml", """<testsuite name="a.ATest"><testcase name="x" classname="a.ATest"/></testsuite>""");
        Write("a/target/surefire-reports/junitreports/TEST-a.ATest.xml", """<testsuite name="a.ATest"><testcase name="x" classname="a.ATest"/></testsuite>""");
        Write("b/build/test-results/test/TEST-b.BTest.xml", """<testsuite name="b.BTest"><testcase name="y()" classname="b.BTest"/></testsuite>""");
        Write("b/target/surefire-reports/TEST-broken.xml", "<testsuite name=");

        var tests = TestReportReader.Read(temp.Root);
        Assert.Equal(["a.ATest#x", "b.BTest#y"], tests.Select(t => t.Id));
        Assert.Empty(TestReportReader.Read(Path.Combine(temp.Root, "missing")));
    }
}
