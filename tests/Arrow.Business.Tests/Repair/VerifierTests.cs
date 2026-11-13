using Arrow.Business.Analysis;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Verification;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Tests.Repair;

/// <summary>The 5 steps, the copies and the infrastructure retries of the verifier (P04 §3), with a fake build tool.</summary>
public sealed class VerifierTests : IDisposable
{
    private const string Fqn = "com.acme.billing.PriceCalculator_ArrowTest";
    private const string TestPath = "src/test/java/com/acme/billing/PriceCalculator_ArrowTest.java";
    private const string Code = "package com.acme.billing;\n\npublic class PriceCalculator_ArrowTest {\n    @org.junit.Test\n    public void adds() {\n        long x = calc.total(1);\n    }\n}\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-verify-unit", Guid.NewGuid().ToString("N")[..8]);
    private readonly List<ArrowEvent> _events = [];

    public VerifierTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "home", "work", "r1", "snapshot", "src", "main", "java"));
        File.WriteAllText(Path.Combine(_root, "home", "work", "r1", "snapshot", "pom.xml"), "<project/>");
        Directory.CreateDirectory(Path.Combine(_root, "home", "runs", "r1"));
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

    private string Home => Path.Combine(_root, "home");

    private string WorkDir => Path.Combine(Home, "work", "r1");

    private TaskContext NewTask(IReadOnlyList<string>? passing = null) => new(
        new RunContext("r1", Home, WorkDir, new RunHandle("r1", Path.Combine(Home, "runs", "r1")), new ListProgress(_events)),
        "com.acme.billing.PriceCalculator",
        Metadata(),
        new BaselineInfo(2, new BaselineCommands([], []), ".", passing ?? ["com.acme.billing.TotalsTest#appliesDefaultRate"], [], [], new BaselineDurations(0, 0, [])),
        new WorkspaceInfo("work/r1/base", "work/r1/snapshot", null, 8, "/jdk8", BuildToolKind.Maven, "/usr/bin/mvn", false, ".", BuildMode.Standalone,
            "detected:standalone", [], null, ["-B"], []),
        "package com.acme.billing; public class PriceCalculator {}",
        "targets/com.acme.billing.PriceCalculator",
        []);

    private static RepositoryMetadata Metadata() => new(
        new SourceMetadata(ProjectSourceKind.LocalDirectory, "billing", null, null, null),
        new BuildToolMetadata(BuildToolKind.Maven, "3.9.6", false, false, ".", BuildMode.Standalone, "pom.xml", null, "detected:standalone", [], []),
        null,
        null,
        new TestFrameworkMetadata("junit4", "4.13.2", false, [], [], "dependency junit:junit (test)"),
        new ProjectStructureMetadata(false, [], ".", ".", ["com.acme.billing"]),
        new PathsMetadata("src/main/java", "src/test/java", "src/main/java/com/acme/billing/PriceCalculator.java", TestPath, Fqn, 1, true),
        new CutMetadata("com.acme.billing.PriceCalculator", "PriceCalculator", "com.acme.billing", CutKind.Class, "abc"),
        new Dictionary<string, string>());

    private static VerifyOptions Options(int attempts = 3) =>
        new("targets/com.acme.billing.PriceCalculator/block3/round_1", 1, TimeSpan.FromSeconds(20), attempts, TimeSpan.FromSeconds(30), false, "sha256:abc", 4);

    private Verifier NewVerifier(FakeBuild build, FakeHelper? helper = null) =>
        new(build, new WorkspaceManager(), new FakeHelperProvider(helper ?? new FakeHelper()), new ImmediateTime());

    private Task<VerifyResult> VerifyAsync(Verifier verifier, TaskContext? task = null, VerifyOptions? options = null) =>
        verifier.VerifyAsync(SuiteFiles.Single(Fqn, TestPath, Code), task ?? NewTask(), options ?? Options(), TestContext.Current.CancellationToken);

    private const string Pass = """<testcase name="adds" classname="com.acme.billing.PriceCalculator_ArrowTest"/>""";
    private const string Totals = """<testcase name="appliesDefaultRate" classname="com.acme.billing.TotalsTest"/>""";

    [Fact]
    public async Task AValidSuiteRunsFiveStepsOnTwoCopiesAndDeletesThem()
    {
        var build = new FakeBuild()
            .On("test-compile", _ => Ok())
            .On("-Dtest=", copy => Report(copy, Fqn, Pass))
            .On("test", copy => Report(copy, "com.acme.billing.TotalsTest", Totals, Report(copy, Fqn, Pass)));
        var result = await VerifyAsync(NewVerifier(build));

        Assert.Equal(VerificationState.Valid, result.State);
        Assert.Equal((1, 1, 4, "sha256:abc"), (result.TestMethods.Total, result.TestsPassed, result.AssertionCount, result.CodeHash));
        Assert.True(result.GeneratedTestRanInModule);
        Assert.Equal(TestPath, result.TestFile);
        Assert.Equal([VerifySteps.Compile, VerifySteps.RunTarget, VerifySteps.ModuleTests], result.Steps.Select(s => s.Step));
        Assert.All(result.Steps, s => Assert.True(s.Ok));
        Assert.Equal("logs/compile.log", result.Steps[0].Log);

        // Steps 1–2 share a copy; step 5 has its own. Each copy had the suite written in it; all are deleted.
        Assert.Equal(build.Copies[0], build.Copies[1]);
        Assert.NotEqual(build.Copies[0], build.Copies[2]);
        Assert.Equal(Code, build.WrittenCode[0]);
        Assert.All(build.Copies.Distinct(), c => Assert.False(Directory.Exists(c)));
        Assert.Equal(["-Dtest=PriceCalculator_ArrowTest", "-Dsurefire.failIfNoSpecifiedTests=false", "-DfailIfNoTests=false", "-Dmaven.test.failure.ignore=true", "test"], build.Arguments[1]);
        Assert.Contains(_events, e => e.Type == ArrowEventTypes.B3Verified && (string?)e.Data!["state"] == "VALID");
    }

    [Fact]
    public async Task CompileErrorInTheGeneratedFileIsCompileWithItsMethods()
    {
        var build = new FakeBuild().On("test-compile", copy => Fail(
            $"[ERROR] {Path.Combine(copy, "src", "test", "java", "com", "acme", "billing", "PriceCalculator_ArrowTest.java")}:[6,22] cannot find symbol\n" +
            "[ERROR]   symbol:   method total(int)\n[ERROR]   location: variable calc of type com.acme.billing.PriceCalculator\n"));
        var helper = new FakeHelper { MethodsAtLines = new Dictionary<int, string?> { [6] = "adds" } };
        var result = await VerifyAsync(NewVerifier(build, helper));

        Assert.Equal(VerificationState.Compile, result.State);
        Assert.Equal("COMPILE | cannot find symbol | method", result.ErrorSignature);
        Assert.Equal(["adds"], result.FailingMethods);
        Assert.Equal(TestPath, Assert.Single(result.CompileErrors).File);
        Assert.Single(result.Steps);
    }

    [Fact]
    public async Task WarningsUnderWerrorAreUnknown()
    {
        var build = new FakeBuild().On("test-compile", copy => Fail(
            $"[WARNING] {copy}/src/test/java/com/acme/billing/PriceCalculator_ArrowTest.java:[3,8] [deprecation] x has been deprecated\n" +
            "[ERROR] COMPILATION ERROR :\n[ERROR] warnings found and -Werror specified\n" +
            $"[ERROR] {copy}/src/test/java/com/acme/billing/PriceCalculator_ArrowTest.java:[3,8] [deprecation] x has been deprecated\n" +
            "[ERROR] Failed to execute goal org.apache.maven.plugins:maven-compiler-plugin:3.13.0:testCompile (default-testCompile) on project billing: Compilation failure\n"));
        var result = await VerifyAsync(NewVerifier(build));

        Assert.Equal(VerificationState.Unknown, result.State);
        Assert.Equal("UNKNOWN | maven-compiler-plugin | -", result.ErrorSignature);
        Assert.DoesNotContain(WorkDir, result.BuildErrorExcerpt!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACrashOfTheTestJvmIsRuntime()
    {
        var build = new FakeBuild()
            .On("test-compile", _ => Ok())
            .On("-Dtest=", _ => Fail("[ERROR] Failed to execute goal org.apache.maven.plugins:maven-surefire-plugin:3.2.5:test (default-test) on project billing: There are test failures.\n[ERROR] The forked VM terminated without properly saying goodbye. VM crash or System.exit called?\n"));
        var result = await VerifyAsync(NewVerifier(build));
        Assert.Equal(VerificationState.Runtime, result.State);
        Assert.Equal("RUNTIME | maven-surefire-plugin | -", result.ErrorSignature);
        Assert.Contains("forked VM terminated", result.BuildErrorExcerpt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoTestFoundIsDiscovery()
    {
        var build = new FakeBuild()
            .On("test-compile", _ => Ok())
            .On("-Dtest=", _ => Fail("[ERROR] Failed to execute goal org.apache.maven.plugins:maven-surefire-plugin:3.2.5:test: No tests were executed!"));
        Assert.Equal(VerificationState.Discovery, (await VerifyAsync(NewVerifier(build))).State);
    }

    [Fact]
    public async Task FailuresAreClassifiedWithoutWorkspacePaths()
    {
        var build = new FakeBuild()
            .On("test-compile", _ => Ok())
            .On("-Dtest=", copy => Report(copy, Fqn, $"""
                <testcase name="adds" classname="{Fqn}">
                  <error message="{copy}/data.txt (No such file)" type="java.io.FileNotFoundException">java.io.FileNotFoundException: {copy}/data.txt (No such file)
                	at com.acme.billing.PriceCalculator.load(PriceCalculator.java:9)</error>
                </testcase>
                """));
        var result = await VerifyAsync(NewVerifier(build));
        Assert.Equal(VerificationState.Runtime, result.State);
        Assert.Equal("RUNTIME | FileNotFoundException | PriceCalculator.load", result.ErrorSignature);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("data.txt (No such file)", failure.Message);
        Assert.DoesNotContain(WorkDir, failure.TraceHead!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["adds"], result.FailingMethods);
    }

    [Fact]
    public async Task ABrokenBaselineTestIsARegressionAndReportsAreKept()
    {
        var build = new FakeBuild()
            .On("test-compile", _ => Ok())
            .On("-Dtest=", copy => Report(copy, Fqn, Pass))
            .On("test", copy => Report(copy, "com.acme.billing.TotalsTest",
                """<testcase name="appliesDefaultRate" classname="com.acme.billing.TotalsTest"><failure message="expected:&lt;110&gt; but was:&lt;120&gt;" type="java.lang.AssertionError"/></testcase>""",
                Report(copy, Fqn, Pass)));
        var task = NewTask();
        var result = await VerifyAsync(NewVerifier(build), task);
        Assert.Equal(VerificationState.Regression, result.State);
        Assert.Equal("REGRESSION | existing tests fail | TotalsTest", result.ErrorSignature);
        Assert.Equal(RegressionKind.Failed, Assert.Single(result.Regressions).Kind);
        Assert.True(File.Exists(Path.Combine(task.Run.Run.Root, "targets", "com.acme.billing.PriceCalculator", "block3", "round_1", "reports", "module_tests", "TEST-com.acme.billing.TotalsTest.xml")));
        Assert.True(File.Exists(Path.Combine(task.Run.Run.Root, "targets", "com.acme.billing.PriceCalculator", "block3", "round_1", "reports", "run_target", $"TEST-{Fqn}.xml")));
    }

    [Fact]
    public async Task AGeneratedTestThatDoesNotRunInTheModuleIsValidWithAWarningFlag()
    {
        var build = new FakeBuild()
            .On("test-compile", _ => Ok())
            .On("-Dtest=", copy => Report(copy, Fqn, Pass))
            .On("test", copy => Report(copy, "com.acme.billing.TotalsTest", Totals));
        var result = await VerifyAsync(NewVerifier(build));
        Assert.Equal((VerificationState.Valid, false), (result.State, result.GeneratedTestRanInModule!.Value));
    }

    // P04 §3.6: three attempts on new copies, 30 s apart, then INFRA with the code.
    [Fact]
    public async Task ATimeoutIsRetriedOnNewCopiesThenInfra()
    {
        var build = new FakeBuild()
            .On("test-compile", _ => Ok())
            .On("-Dtest=", _ => new ProcessResult(null, true, "", TimeSpan.FromSeconds(20)));
        var time = new ImmediateTime();
        var verifier = new Verifier(build, new WorkspaceManager(), new FakeHelperProvider(new FakeHelper()), time);
        var result = await VerifyAsync(verifier);

        Assert.True(result.IsInfra);
        Assert.Equal(ErrorCodes.B3Timeout, result.InfraReason);
        var step = result.Steps[^1];
        Assert.Equal((VerifySteps.RunTarget, false, 3, "logs/run_target-try3.log"), (step.Step, step.Ok, step.Attempts, step.Log));
        Assert.Equal(3, build.Arguments.Count(a => a[0].StartsWith("-Dtest=", StringComparison.Ordinal)));
        Assert.Equal(3, build.Copies.Skip(1).Distinct().Count()); // attempt 1 reuses the compiled copy; 2 and 3 get new ones
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)], time.Delays);
        Assert.Equal(2, _events.Count(e => e.Type == ArrowEventTypes.B3InfraRetry));
        Assert.All(build.Copies.Distinct(), c => Assert.False(Directory.Exists(c)));
    }

    [Fact]
    public async Task ATimeoutThatPassesOnRetryIsNotInfra()
    {
        var calls = 0;
        var build = new FakeBuild()
            .On("test-compile", _ => ++calls == 1 ? new ProcessResult(null, true, "", TimeSpan.FromSeconds(20)) : Ok())
            .On("-Dtest=", copy => Report(copy, Fqn, Pass))
            .On("test", copy => Report(copy, "com.acme.billing.TotalsTest", Totals, Report(copy, Fqn, Pass)));
        var result = await VerifyAsync(NewVerifier(build));
        Assert.Equal(VerificationState.Valid, result.State);
        Assert.Equal(2, result.Steps[0].Attempts);
        Assert.Equal(build.Copies[1], build.Copies[2]); // step 2 reuses the copy of the successful compile attempt
    }

    [Theory]
    [InlineData("[ERROR] Failed to execute goal on project billing: Could not resolve dependencies for project com.acme:billing:jar:1.0", 1, ErrorCodes.B3Dependency)]
    [InlineData("Exception in thread \"main\" java.lang.OutOfMemoryError: Java heap space", 1, ErrorCodes.B3ToolError)]
    [InlineData("killed", -9, ErrorCodes.B3ToolError)]
    public async Task InfrastructureFailuresHaveTheirCodes(string output, int exitCode, string code)
    {
        var build = new FakeBuild().On("test-compile", _ => new ProcessResult(exitCode, false, output, TimeSpan.FromSeconds(1)));
        var result = await VerifyAsync(NewVerifier(build), options: Options(attempts: 2));
        Assert.Equal(code, result.InfraReason);
        Assert.Equal(2, result.Steps[0].Attempts);
    }

    [Fact]
    public async Task ABuildToolThatCannotStartIsAToolError()
    {
        var build = new FakeBuild().On("test-compile", _ => throw new System.ComponentModel.Win32Exception("The system cannot find the file specified"));
        var result = await VerifyAsync(NewVerifier(build), options: Options(attempts: 2));
        Assert.Equal(ErrorCodes.B3ToolError, result.InfraReason);
        Assert.Contains("không chạy được công cụ build", result.BuildErrorExcerpt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingSnapshotIsAWorkspaceError()
    {
        WorkspaceManager.DeleteTree(Path.Combine(WorkDir, "snapshot"));
        var result = await VerifyAsync(NewVerifier(new FakeBuild()), options: Options(attempts: 1));
        Assert.Equal(ErrorCodes.B3Workspace, result.InfraReason);
    }

    [Fact]
    public async Task KeptWorkspacesAreNotDeleted()
    {
        var build = new FakeBuild().On("test-compile", _ => Fail("[ERROR] Failed to execute goal org.apache.maven.plugins:maven-enforcer-plugin:3.0.0:enforce"));
        var result = await VerifyAsync(NewVerifier(build), options: Options() with { KeepWorkspaces = true });
        Assert.Equal(VerificationState.Unknown, result.State);
        Assert.True(Directory.Exists(build.Copies[0]));
    }

    [Fact]
    public void GradleStepsUseTheProjectAndTheTestFilter()
    {
        var setup = new BuildSetup(BuildToolKind.Gradle, "gradlew", true, ".", BuildMode.Reactor, ":core", [], "/init.gradle");
        Assert.Equal([":core:testClasses"], Verifier.CompileArguments(setup));
        Assert.Equal([":core:test", "--tests", Fqn, "--continue"], Verifier.RunTargetArguments(setup, Fqn));
        var maven = Verifier.SetupOf(new WorkspaceInfo("b", "s", null, 11, "/jdk", BuildToolKind.Maven, "mvnw", true, ".", BuildMode.Reactor, "detected:reactor",
            ["-pl", "shop-core", "-am"], null, ["-B"], []), "/home");
        Assert.Equal("shop-core", maven.Module);
        Assert.Equal(["-pl", "shop-core", "-am"], BuildCommands.MavenSelector(maven));
    }

    // ── fakes ───────────────────────────────────────────────────────────

    private static ProcessResult Ok(string output = "BUILD SUCCESS") => new(0, false, output, TimeSpan.FromSeconds(1));

    private static ProcessResult Fail(string output) => new(1, false, output, TimeSpan.FromSeconds(1));

    /// <summary>Writes a Surefire report of <paramref name="className"/> into the copy and returns <paramref name="result"/> (or success).</summary>
    private static ProcessResult Report(string copy, string className, string cases, ProcessResult? result = null)
    {
        var dir = Path.Combine(copy, "target", "surefire-reports");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"TEST-{className}.xml"), $"""<?xml version="1.0"?><testsuite name="{className}">{cases}</testsuite>""");
        return result ?? Ok();
    }

    private sealed class FakeBuild : IBuildExecutor
    {
        private readonly List<(string Marker, Func<string, ProcessResult> Run)> _rules = [];

        public List<string> Copies { get; } = [];

        public List<IReadOnlyList<string>> Arguments { get; } = [];

        public List<string> WrittenCode { get; } = [];

        /// <summary>The first rule whose marker starts an argument answers; <c>test</c> must equal the last argument.</summary>
        public FakeBuild On(string marker, Func<string, ProcessResult> run)
        {
            _rules.Add((marker, run));
            return this;
        }

        public Task<BuildResult> RunAsync(BuildSetup setup, string projectRoot, IReadOnlyList<string> arguments, string jdkHome, TimeSpan timeout, string? logFile, CancellationToken ct)
        {
            Copies.Add(projectRoot);
            Arguments.Add(arguments);
            var file = Path.Combine(projectRoot, TestPath.Replace('/', Path.DirectorySeparatorChar));
            WrittenCode.Add(File.Exists(file) ? File.ReadAllText(file) : "");
            var rule = _rules.FirstOrDefault(r => r.Marker == "test"
                ? arguments.Count > 0 && arguments[^1] == "test" && !arguments.Any(a => a.StartsWith("-Dtest=", StringComparison.Ordinal))
                : arguments.Any(a => a.StartsWith(r.Marker, StringComparison.Ordinal)));
            var result = rule.Run?.Invoke(projectRoot) ?? Fail("no rule");
            if (logFile is not null)
            {
                File.WriteAllText(logFile, result.Output);
            }

            return Task.FromResult(new BuildResult(["mvn", .. arguments], projectRoot, result, BuildFailureClassifier.Classify(result), 0));
        }
    }

    private sealed class ImmediateTime : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Delays.Add(dueTime);
                callback(state);
            }

            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ListProgress(List<ArrowEvent> events) : IProgress<ArrowEvent>
    {
        public void Report(ArrowEvent value) => events.Add(value);
    }
}

/// <summary>A java-helper that answers only what block 3 asks, from fixed data.</summary>
internal sealed class FakeHelper : IJavaHelper
{
    public IReadOnlyDictionary<int, string?> MethodsAtLines { get; init; } = new Dictionary<int, string?>();

    public Func<IReadOnlyList<string>, IReadOnlyList<string>, ClassApiResult>? ClassApi { get; init; }

    public CalledMethodsResult? Called { get; init; }

    public IReadOnlyList<MethodSourceEntry> MethodSources { get; init; } = [];

    public Exception? Failure { get; init; }

    public List<string> Calls { get; } = [];

    public Task<IReadOnlyDictionary<int, string?>> MethodAtLineAsync(string code, IReadOnlyList<int> lines, CancellationToken ct) => Answer(nameof(MethodAtLineAsync), MethodsAtLines);

    public Task<ClassApiResult> ClassApiAsync(IReadOnlyList<string> sources, IReadOnlyList<string> classes, IReadOnlyList<string> classpath, CancellationToken ct) =>
        Answer(nameof(ClassApiAsync), ClassApi?.Invoke(sources, classes) ?? new ClassApiResult([], []));

    public Task<IReadOnlyList<MethodSourceEntry>> MethodSourceAsync(string code, IReadOnlyList<string> names, CancellationToken ct) =>
        Answer(nameof(MethodSourceAsync), (IReadOnlyList<MethodSourceEntry>)MethodSources.Where(m => names.Contains(m.Name)).ToList());

    public Task<CalledMethodsResult> CalledMethodsAsync(string testCode, IReadOnlyList<string> testMethods, string cutCode, CancellationToken ct) =>
        Answer(nameof(CalledMethodsAsync), Called ?? new CalledMethodsResult(true, [], [], [], []));

    private Task<T> Answer<T>(string call, T value)
    {
        Calls.Add(call);
        return Failure is not null ? Task.FromException<T>(Failure) : Task.FromResult(value);
    }

    public Task<string> PingAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<JavaParseResult> ParseAsync(string code, CancellationToken ct) => throw new NotSupportedException();

    public Task<RenameResult> RenameClassAsync(string code, string newName, string? package, CancellationToken ct) => throw new NotSupportedException();

    public Task<HashResult> StripCommentsHashAsync(string code, CancellationToken ct) => throw new NotSupportedException();

    public Task<ShrinkResult> ShrinkSourceAsync(string code, int maxChars, CancellationToken ct) => throw new NotSupportedException();

    public Task<SpliceResult> SpliceMethodsAsync(string baseCode, string response, IReadOnlyList<string> methods, CancellationToken ct) => throw new NotSupportedException();

    public Func<string, IReadOnlyList<string>>? TestMethods { get; init; }

    public Task<IReadOnlyList<string>> ListTestMethodsAsync(string code, CancellationToken ct) => Answer(nameof(ListTestMethodsAsync), TestMethods?.Invoke(code) ?? (IReadOnlyList<string>)[]);

    public Task<AssertionCount> CountAssertionsAsync(string code, CancellationToken ct) => throw new NotSupportedException();

    public Task<ListClassesResult> ListClassesAsync(IReadOnlyList<string> roots, int maxFiles, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeHelperProvider(IJavaHelper helper) : IJavaHelperProvider
{
    private sealed class Lease(IJavaHelper helper) : IJavaHelperLease
    {
        public IJavaHelper Helper { get; } = helper;

        public void Dispose()
        {
        }
    }

    public Task<IJavaHelperLease> RentAsync(CancellationToken ct) => Task.FromResult<IJavaHelperLease>(new Lease(helper));
}
