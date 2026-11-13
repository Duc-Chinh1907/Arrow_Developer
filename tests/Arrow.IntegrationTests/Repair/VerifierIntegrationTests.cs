using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Verification;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;
using Arrow.IntegrationTests.Analysis;
using Arrow.IntegrationTests.JavaHelper;

namespace Arrow.IntegrationTests.Repair;

/// <summary>
/// The 5 steps of block 3 on the fixtures with the real build tools and java-helper (P04 §12, integration part). Block 1 runs
/// first, as in a real run, and the user's project must stay unchanged.
/// </summary>
[Trait("Category", "Integration")]
public class VerifierIntegrationTests
{
    private const string BillingFqn = "com.acme.billing.PriceCalculator_ArrowTest";
    private const string BillingPath = "src/test/java/com/acme/billing/PriceCalculator_ArrowTest.java";

    private static string Billing(string body, string imports = "") => $$"""
        package com.acme.billing;

        import static org.junit.Assert.assertEquals;

        import org.junit.Test;
        {{imports}}
        public class PriceCalculator_ArrowTest {
        {{body}}
        }
        """;

    /// <summary>Block 1 on a fixture, then a verifier on its snapshot.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly JavaHelperPool _pool;

        private Session(Block1Harness harness, UserProject project, TaskContext task)
        {
            Harness = harness;
            Project = project;
            Task = task;
            _pool = new JavaHelperPool(JavaHelperEnvironment.Options(), 1);
            var builds = new BuildExecutor(new ProcessRunner(), Environment.GetEnvironmentVariable,
                BuildFailureClassifier.UserCaches(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            Verifier = new Verifier(builds, new WorkspaceManager(), _pool);
        }

        public Block1Harness Harness { get; }

        public UserProject Project { get; }

        public TaskContext Task { get; }

        public Verifier Verifier { get; }

        public static async Task<Session> StartAsync(string fixture, string cut, bool gradle = false)
        {
            var harness = await Block1Harness.CreateAsync(gradle);
            var project = new UserProject(fixture);
            var (analysis, run) = await harness.InspectAsync(project.Root, cut);
            Assert.True(analysis.Outcome.Outcome == BlockOutcomeKind.Ok, analysis.Outcome.Message + " " + analysis.Outcome.FirstError);
            var m = analysis.Metadata!;
            var cutSource = File.ReadAllText(Path.Combine(harness.Home.Root, analysis.Workspace!.Base, m.Paths.CutPath));
            var classpath = (harness.Block1File(run, m.Cut.Fqn, "test_classpath.txt") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var task = new TaskContext(run, m.Cut.Fqn, m, analysis.Baseline!, analysis.Workspace, cutSource, $"targets/{m.Cut.Fqn}", classpath);
            return new Session(harness, project, task);
        }

        public Task<VerifyResult> VerifyAsync(string path, string fqn, string code, int round, TimeSpan? timeout = null, int attempts = 2) =>
            Verifier.VerifyAsync(
                SuiteFiles.Single(fqn, path, code),
                Task,
                new VerifyOptions($"{Task.TargetDir}/block3/round_{round}", round, timeout ?? TimeSpan.FromMinutes(10), attempts, TimeSpan.Zero, false, $"sha256:{round}", 1),
                TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            Project.AssertUnchanged();
            await _pool.DisposeAsync();
            await Harness.DisposeAsync();
            Project.Dispose();
        }
    }

    // P04 §12: six candidates on maven-verify-junit4 give every state.
    [Fact]
    public async Task SixCandidatesGiveEveryState()
    {
        await using var session = await Session.StartAsync("maven-verify-junit4", "PriceCalculator");
        Assert.Contains("com.acme.billing.TotalsTest#appliesDefaultRate", session.Task.Baseline.PassingInAllRuns);

        var compile = await session.VerifyAsync(BillingPath, BillingFqn, Billing("""
                @Test
                public void totals() {
                    long t = new PriceCalculator().total(2);
                }
            """), 1);
        Assert.Equal(VerificationState.Compile, compile.State);
        Assert.Equal("COMPILE | cannot find symbol | method", compile.ErrorSignature);
        Assert.Equal(["totals"], compile.FailingMethods);
        Assert.Equal(BillingPath, compile.CompileErrors[0].File);

        var discovery = await session.VerifyAsync(BillingPath, BillingFqn, Billing("""
                public void adds() {
                    assertEquals(5, new PriceCalculator().add(2, 3));
                }
            """), 2);
        Assert.Equal(VerificationState.Discovery, discovery.State);

        var runtime = await session.VerifyAsync(BillingPath, BillingFqn, Billing("""
                @Test
                public void lengthOfNull() {
                    assertEquals(0, new PriceCalculator().length(null));
                }
            """), 3);
        Assert.Equal(VerificationState.Runtime, runtime.State);
        Assert.Equal("RUNTIME | NullPointerException | PriceCalculator.length", runtime.ErrorSignature);
        Assert.Equal(["lengthOfNull"], runtime.FailingMethods);

        var assertion = await session.VerifyAsync(BillingPath, BillingFqn, Billing("""
                @Test
                public void adds() {
                    assertEquals(6, new PriceCalculator().add(2, 3));
                }

                @Test
                public void divides() {
                    assertEquals(2, new PriceCalculator().divide(4, 2));
                }
            """), 4);
        Assert.Equal(VerificationState.Assertion, assertion.State);
        Assert.Equal("ASSERTION | AssertionError | adds", assertion.ErrorSignature);
        Assert.Equal((2, 1), (assertion.TestMethods.Total, assertion.TestsPassed));

        var regression = await session.VerifyAsync(BillingPath, BillingFqn, Billing("""
                @Test
                public void higherRate() {
                    Invoice.setDefaultRate(20);
                    assertEquals(120, new Invoice().total(100));
                }
            """), 5);
        Assert.Equal(VerificationState.Regression, regression.State);
        Assert.Equal("REGRESSION | existing tests fail | TotalsTest", regression.ErrorSignature);
        Assert.Equal(RegressionKind.Failed, Assert.Single(regression.Regressions).Kind);
        Assert.True(regression.GeneratedTestRanInModule);

        var valid = await session.VerifyAsync(BillingPath, BillingFqn, Billing("""
                @Test
                public void adds() {
                    assertEquals(5, new PriceCalculator().add(2, 3));
                }
            """), 6);
        Assert.Equal(VerificationState.Valid, valid.State);
        Assert.Equal([VerifySteps.Compile, VerifySteps.RunTarget, VerifySteps.ModuleTests], valid.Steps.Select(s => s.Step));
        Assert.True(valid.GeneratedTestRanInModule);
        Assert.True(File.Exists(Path.Combine(session.Task.Run.Run.Root, "targets", "com.acme.billing.PriceCalculator", "block3", "round_6", "logs", "module_tests.log")));

        // Every copy was deleted.
        Assert.Empty(Directory.EnumerateDirectories(session.Task.Run.WorkDir, "v-*"));
    }

    // P04 §3.1: each verification runs on its own copy, so a file a test leaves behind is not seen by the next one.
    [Fact]
    public async Task EachVerificationHasItsOwnCopy()
    {
        await using var session = await Session.StartAsync("maven-verify-junit4", "PriceCalculator");
        var code = Billing("""
                @Test
                public void leavesAFile() throws Exception {
                    File marker = new File("arrow-marker.txt");
                    assertEquals(false, marker.exists());
                    assertEquals(true, marker.createNewFile());
                    assertEquals(5, new PriceCalculator().add(2, 3));
                }
            """, "import java.io.File;\n");
        Assert.Equal(VerificationState.Valid, (await session.VerifyAsync(BillingPath, BillingFqn, code, 1)).State);
        Assert.Equal(VerificationState.Valid, (await session.VerifyAsync(BillingPath, BillingFqn, code + "\n", 2)).State);
    }

    // P04 §12: a test that fails at baseline (BrokenTest) is not a regression.
    [Fact]
    public async Task ATestFailingAtBaselineIsNotARegression()
    {
        await using var session = await Session.StartAsync("maven-subdir-junit4", "Util");
        Assert.Contains("com.acme.util.BrokenTest#alwaysFails", session.Task.Baseline.FailingInAllRuns);
        var result = await session.VerifyAsync("lib/src/test/java/com/acme/util/Util_ArrowTest.java", "com.acme.util.Util_ArrowTest", """
            package com.acme.util;

            import static org.junit.Assert.assertEquals;

            import org.junit.Test;

            public class Util_ArrowTest {
                @Test
                public void prependsZeros() {
                    assertEquals("007", Util.prependZeros(2, "7"));
                }
            }
            """, 1);
        Assert.Equal(VerificationState.Valid, result.State);
    }

    // P04 §12: the 5 steps with Gradle.
    [Fact]
    public async Task GradleRunsTheFiveSteps()
    {
        await using var session = await Session.StartAsync("gradle-junit5", "Calculator", gradle: true);
        const string path = "core/src/test/java/com/acme/calc/Calculator_ArrowTest.java";
        const string fqn = "com.acme.calc.Calculator_ArrowTest";
        static string Code(int expected) => $$"""
            package com.acme.calc;

            import static org.junit.jupiter.api.Assertions.assertEquals;

            import org.junit.jupiter.api.Test;

            class Calculator_ArrowTest {
                @Test
                void adds() {
                    assertEquals({{expected}}, new Calculator().add(2, 3));
                }
            }
            """;

        var assertion = await session.VerifyAsync(path, fqn, Code(6), 1);
        Assert.Equal(VerificationState.Assertion, assertion.State);
        Assert.Equal("ASSERTION | AssertionFailedError | adds", assertion.ErrorSignature);

        var valid = await session.VerifyAsync(path, fqn, Code(5), 2);
        Assert.Equal(VerificationState.Valid, valid.State);
        Assert.True(valid.GeneratedTestRanInModule);
    }

    // P04 §12: a test that hangs is killed after step_timeout_seconds (20 s here), 3 times, then INFRA with B3_TIMEOUT.
    [Fact]
    public async Task AHangingTestEndsInInfraTimeout()
    {
        await using var session = await Session.StartAsync("maven-verify-junit4", "PriceCalculator");
        var result = await session.VerifyAsync(BillingPath, BillingFqn, Billing("""
                @Test
                public void hangs() throws Exception {
                    Thread.sleep(600000);
                }
            """), 1, TimeSpan.FromSeconds(20), attempts: 3);
        Assert.True(result.IsInfra);
        Assert.Equal(ErrorCodes.B3Timeout, result.InfraReason);
        var step = result.Steps[^1];
        Assert.Equal((VerifySteps.RunTarget, 3), (step.Step, step.Attempts));
        Assert.Empty(Directory.EnumerateDirectories(session.Task.Run.WorkDir, "v-*"));
    }
}
