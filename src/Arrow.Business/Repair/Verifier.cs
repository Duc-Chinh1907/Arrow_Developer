using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Arrow.Business.Analysis;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Verification;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Source;

namespace Arrow.Business.Repair;

/// <summary>The files of a suite to verify (P04 §2).</summary>
/// <param name="TestFqn">FQN of the test class, e.g. <c>com.myshop.service.OrderService_ArrowTest</c>.</param>
/// <param name="Files">Path from the project root → content; one file for an ARROW candidate.</param>
/// <param name="ExtraTestClasspath">Always empty in the product.</param>
public sealed record SuiteFiles(string TestFqn, IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> ExtraTestClasspath)
{
    /// <summary>The suite of one test file.</summary>
    public static SuiteFiles Single(string testFqn, string path, string code) => new(testFqn, new Dictionary<string, string> { [path] = code }, []);
}

/// <summary>What block 3 knows about one CUT (P04 §2).</summary>
/// <param name="Run">The run: <c>ARROW_HOME</c>, work folder, results, progress.</param>
/// <param name="CutFqn">The CUT.</param>
/// <param name="Metadata">M.</param>
/// <param name="Baseline"><c>baseline.json</c>.</param>
/// <param name="Workspace"><c>workspace.json</c>.</param>
/// <param name="CutSource">Source of the CUT.</param>
/// <param name="TargetDir"><c>targets/&lt;cut&gt;</c>, relative to the run folder.</param>
/// <param name="TestClasspath">Lines of <c>test_classpath.txt</c>, for <c>class_api</c> and the package list.</param>
public sealed record TaskContext(
    RunContext Run,
    string CutFqn,
    RepositoryMetadata Metadata,
    BaselineInfo Baseline,
    WorkspaceInfo Workspace,
    string CutSource,
    string TargetDir,
    IReadOnlyList<string> TestClasspath);

/// <summary>Settings of one verification.</summary>
/// <param name="OutputDir">Folder of the verification relative to the run folder (<c>targets/&lt;cut&gt;/block3/round_2</c>); logs and reports go below it.</param>
/// <param name="Round">Round of the candidate; 0 for c0.</param>
/// <param name="StepTimeout"><c>verification.step_timeout_seconds</c>.</param>
/// <param name="InfraAttempts"><c>verification.infra_attempts</c>.</param>
/// <param name="InfraWait"><c>verification.infra_wait_seconds</c>.</param>
/// <param name="KeepWorkspaces"><c>run.keep_workspaces</c>: the copies are not deleted.</param>
/// <param name="CodeHash">Hash of the candidate, from block 2.</param>
/// <param name="AssertionCount">Assertions of the candidate, from block 2.</param>
/// <param name="ConfirmRegression"><c>verification.confirm_regression</c>: an existing test that fails in step 5 is checked again before it is a regression (P04 §3.5).</param>
public sealed record VerifyOptions(
    string OutputDir,
    int Round,
    TimeSpan StepTimeout,
    int InfraAttempts,
    TimeSpan InfraWait,
    bool KeepWorkspaces,
    string? CodeHash,
    int AssertionCount,
    bool ConfirmRegression = true);

/// <summary>Verification in 5 steps (P04 §3).</summary>
public interface IVerifier
{
    /// <summary>Verifies <paramref name="suite"/> on fresh copies of the compiled snapshot. Infrastructure failures give an INFRA result, not an exception.</summary>
    Task<VerifyResult> VerifyAsync(SuiteFiles suite, TaskContext task, VerifyOptions options, CancellationToken ct);
}

/// <summary>
/// <see cref="IVerifier"/> (P04 §3). Each verification copies the compiled snapshot: steps 1–4 share one copy, step 5 runs on
/// another. Timeouts, download errors, a build tool that does not run and workspace errors retry the step on a new copy, at
/// most <c>infra_attempts</c> times; then the result is INFRA with the <c>B3_*</c> code. The copies are deleted at the end.
/// </summary>
public sealed class Verifier(IBuildExecutor builds, IWorkspaceManager workspaces, IJavaHelperProvider javaHelpers, TimeProvider? time = null) : IVerifier
{
    /// <summary>Lines of build error kept in <c>build_error_excerpt</c>, enough for the UNKNOWN <c>extra_context</c> (P04 §6.5).</summary>
    public const int ExcerptLines = 120;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>An infrastructure failure that ends the verification.</summary>
    private sealed class InfraStop(string code, string reason) : Exception(reason)
    {
        public string Code { get; } = code;
    }

    /// <summary>State of one verification.</summary>
    private sealed class Job(SuiteFiles suite, TaskContext task, VerifyOptions options, BuildSetup setup, CancellationToken ct)
    {
        public SuiteFiles Suite { get; } = suite;

        public TaskContext Task { get; } = task;

        public VerifyOptions Options { get; } = options;

        public BuildSetup Setup { get; } = setup;

        public CancellationToken Ct { get; } = ct;

        public List<VerifyStepRecord> Steps { get; } = [];

        public List<string> Copies { get; } = [];

        public string OutputPath => Path.Combine(Task.Run.Run.Root, Options.OutputDir.Replace('/', Path.DirectorySeparatorChar));

        public void Report(string type, string message, params (string Key, object? Value)[] data) =>
            Task.Run.Progress.Report(new ArrowEvent(DateTimeOffset.UtcNow, type, Task.CutFqn, null, null, EventLevel.Info, message,
                data.ToDictionary(d => d.Key, d => d.Value)));
    }

    /// <inheritdoc />
    public async Task<VerifyResult> VerifyAsync(SuiteFiles suite, TaskContext task, VerifyOptions options, CancellationToken ct)
    {
        var job = new Job(suite, task, options, SetupOf(task.Workspace, task.Run.HomeRoot), ct);
        var watch = Stopwatch.StartNew();
        VerifyResult result;
        try
        {
            result = await VerifyCoreAsync(job).ConfigureAwait(false);
        }
        catch (InfraStop stop)
        {
            result = new VerifyResult { State = null, InfraReason = stop.Code, BuildErrorExcerpt = stop.Message };
        }
        finally
        {
            if (!options.KeepWorkspaces)
            {
                foreach (var copy in job.Copies)
                {
                    await DeleteQuietlyAsync(copy).ConfigureAwait(false);
                }
            }
        }

        result = result with
        {
            Steps = job.Steps,
            TestFile = MainTestFile(suite),
            CodeHash = options.CodeHash,
            AssertionCount = options.AssertionCount,
            TestsPassed = result.TestMethods.Passed,
            BuildSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
        };
        job.Report(
            ArrowEventTypes.B3Verified,
            $"Vòng {options.Round}: {(result.State is { } s ? s.ToString().ToUpperInvariant() : "INFRA " + result.InfraReason)}{(result.ErrorSignature is null ? "" : " · " + result.ErrorSignature)}",
            ("round", options.Round),
            ("state", result.State?.ToString().ToUpperInvariant() ?? VerifyStateJsonConverter.Infra),
            ("signature", result.ErrorSignature),
            ("tests_passed", result.TestMethods.Passed),
            ("tests_total", result.TestMethods.Total));
        return result;
    }

    private async Task<VerifyResult> VerifyCoreAsync(Job job)
    {
        var fqn = job.Suite.TestFqn;
        var generated = job.Suite.Files.Keys.Select(JavacErrorParser.FileName).ToHashSet(StringComparer.Ordinal);
        var scope = new ProjectScope(job.Task.Metadata.ProjectStructure.Packages, job.Task.Metadata.Cut.Package, fqn);

        // Step 1: compile.
        var (compile, copy) = await RunStepAsync(job, VerifySteps.Compile, CompileArguments(job.Setup), null, generated).ConfigureAwait(false);
        if (compile.Failure != BuildFailureKind.None)
        {
            var output = compile.Process.Output;
            var errors = JavacErrorParser.Parse(output, generated, copy);
            if (errors.Any(e => e.InGeneratedFile))
            {
                var main = JavacErrorParser.FileName(MainTestFile(job.Suite));
                var lines = errors.Where(e => e.InGeneratedFile && JavacErrorParser.FileName(e.File) == main).Select(e => e.Line).Distinct().ToList();
                return new VerifyResult
                {
                    State = VerificationState.Compile,
                    CompileErrors = errors,
                    ErrorSignature = SignatureBuilder.Compile(errors),
                    FailingMethods = await MethodsAtLinesAsync(MainCode(job.Suite), lines, job.Ct).ConfigureAwait(false),
                };
            }

            // No javac error in the generated file: -Werror, a plugin, an annotation processor… (P04 §3.3).
            return new VerifyResult
            {
                State = VerificationState.Unknown,
                CompileErrors = errors,
                BuildErrorExcerpt = Excerpt(output, copy),
                ErrorSignature = SignatureBuilder.Unknown(output),
            };
        }

        // Steps 2–4: the generated class alone, on the copy compiled in step 1.
        var (single, singleCopy) = await RunStepAsync(job, VerifySteps.RunTarget, RunTargetArguments(job.Setup, fqn), copy, generated).ConfigureAwait(false);
        var buildRoot = job.Setup.WorkingDirectory(singleCopy);
        var target = TestResultClassifier.Classify(TestReportReader.Read(buildRoot), fqn, scope);
        SaveReports(job, buildRoot, VerifySteps.RunTarget, f => Path.GetFileName(f).StartsWith("TEST-" + fqn, StringComparison.Ordinal));
        var failures = target.Failures.Select(f => Relative(f, singleCopy)).ToList();
        if (target.FailedState == VerificationState.Discovery && single.Failure == BuildFailureKind.Build && !NoTestsMarker(single.Process.Output))
        {
            // The run failed without a report of the class: the test JVM crashed or called System.exit (P04 §3.2).
            return new VerifyResult
            {
                State = VerificationState.Runtime,
                BuildErrorExcerpt = Excerpt(single.Process.Output, singleCopy),
                ErrorSignature = SignatureBuilder.RuntimeCrash(single.Process.Output),
            };
        }

        switch (target.FailedState)
        {
            case VerificationState.Discovery:
                return new VerifyResult { State = VerificationState.Discovery, ErrorSignature = SignatureBuilder.Discovery() };
            case VerificationState.Runtime:
                return new VerifyResult
                {
                    State = VerificationState.Runtime, TestMethods = target.Counts, Failures = failures,
                    FailingMethods = target.FailingMethods, ErrorSignature = SignatureBuilder.Runtime(failures),
                };
            case VerificationState.Assertion:
                return new VerifyResult
                {
                    State = VerificationState.Assertion, TestMethods = target.Counts, Failures = failures,
                    FailingMethods = target.FailingMethods, ErrorSignature = SignatureBuilder.Assertion(failures),
                };
        }

        // Step 5: the whole module, the command of the baseline, on another fresh copy.
        var (_, moduleCopy) = await RunStepAsync(job, VerifySteps.ModuleTests, BuildCommands.ModuleTest(job.Setup), null, generated).ConfigureAwait(false);
        var moduleRoot = job.Setup.WorkingDirectory(moduleCopy);
        var check = RegressionChecker.Check(job.Task.Baseline.PassingInAllRuns, TestReportReader.Read(moduleRoot), fqn);
        IReadOnlyList<RegressionInfo> regressions = Stripped(check.Regressions, moduleCopy);
        IReadOnlyList<string> flaky = [];
        if (regressions.Count > 0)
        {
            SaveReports(job, moduleRoot, VerifySteps.ModuleTests, _ => true);
            (regressions, flaky) = await ConfirmAsync(job, regressions, fqn, generated).ConfigureAwait(false);
        }

        if (regressions.Count > 0)
        {
            return new VerifyResult
            {
                State = VerificationState.Regression, TestMethods = target.Counts, Regressions = regressions,
                GeneratedTestRanInModule = check.GeneratedTestRan, ErrorSignature = SignatureBuilder.Regression(regressions),
                ModuleFlaky = flaky,
            };
        }

        return new VerifyResult
        {
            State = VerificationState.Valid, TestMethods = target.Counts, GeneratedTestRanInModule = check.GeneratedTestRan, ModuleFlaky = flaky,
        };
    }

    /// <summary>Runs of the existing tests alone when a regression is confirmed (P04 §3.5).</summary>
    public const int IsolatedRuns = 3;

    /// <summary>
    /// P04 §3.5 (Chính, 18/11/2026; khác R03, which takes the first run of step 5): before an existing test that failed with the
    /// generated test is called a regression, it is run alone, without the generated test, <see cref="IsolatedRuns"/> times (all such
    /// tests in one command). A test that fails at least once there is suspected flaky and does not count. A test that passes every
    /// time (or could not be run alone) is checked by one more run of the module with the generated test: it is a regression only if
    /// it fails again. Off with <c>verification.confirm_regression = false</c> or <c>--no-confirm-regression</c>. A missing test (b)
    /// or a generated test failing with the module (c) is a regression at once, so the first run then stands.
    /// </summary>
    private async Task<(IReadOnlyList<RegressionInfo> Regressions, IReadOnlyList<string> Flaky)> ConfirmAsync(
        Job job, IReadOnlyList<RegressionInfo> first, string fqn, IReadOnlyCollection<string> generated)
    {
        if (!job.Options.ConfirmRegression || first.Any(r => r.Kind != RegressionKind.Failed))
        {
            return (first, []);
        }

        // 1. The failing tests alone, without the generated test.
        var failures = first.ToDictionary(r => r.Test, _ => 0, StringComparer.Ordinal);
        for (var run = 1; run <= IsolatedRuns; run++)
        {
            job.Report(ArrowEventTypes.B3VerifyStep, $"Xác nhận regression: chạy riêng {first.Count} test sẵn có, không có test mới (lần {run}/{IsolatedRuns})",
                ("step", VerifySteps.IsolatedTests), ("run", run), ("runs", IsolatedRuns));
            var (_, copy) = await RunStepAsync(job, VerifySteps.IsolatedTests, IsolatedArguments(job.Setup, first.Select(r => r.Test)), null, generated, run, withSuite: false)
                .ConfigureAwait(false);
            var root = job.Setup.WorkingDirectory(copy);
            SaveReports(job, root, $"{VerifySteps.IsolatedTests}-run{run}", _ => true);
            foreach (var test in TestReportReader.Read(root).Where(t => failures.ContainsKey(t.Id) && t.Status is TestCaseStatus.Failed or TestCaseStatus.Error))
            {
                failures[test.Id]++;
            }
        }

        var flaky = new SortedSet<string>(first.Where(r => failures[r.Test] > 0).Select(r => r.Test), StringComparer.Ordinal);
        var suspects = first.Where(r => failures[r.Test] == 0).ToList();
        if (suspects.Count == 0)
        {
            return ([], [.. flaky]);
        }

        // 2. They passed alone: the module once more with the generated test.
        job.Report(ArrowEventTypes.B3VerifyStep, $"Xác nhận regression: chạy lại module cùng test mới cho {suspects.Count} test",
            ("step", VerifySteps.ModuleTests), ("run", 2), ("runs", 2));
        var (_, moduleCopy) = await RunStepAsync(job, VerifySteps.ModuleTests, BuildCommands.ModuleTest(job.Setup), null, generated, 2).ConfigureAwait(false);
        var moduleRoot = job.Setup.WorkingDirectory(moduleCopy);
        SaveReports(job, moduleRoot, $"{VerifySteps.ModuleTests}-run2", _ => true);
        var again = RegressionChecker.Check(job.Task.Baseline.PassingInAllRuns, TestReportReader.Read(moduleRoot), fqn);
        var failingAgain = again.Regressions.Where(r => r.Kind == RegressionKind.Failed).Select(r => r.Test).ToHashSet(StringComparer.Ordinal);
        var confirmed = suspects.Where(r => failingAgain.Contains(r.Test)).ToList();
        flaky.UnionWith(suspects.Where(r => !failingAgain.Contains(r.Test)).Select(r => r.Test));

        // A generated test that fails with the module in this run is a regression too: a flaky test is never written.
        var generatedFailures = Stripped(again.Regressions.Where(r => r.Kind == RegressionKind.GeneratedFailedInModule), moduleCopy);
        return ([.. confirmed, .. generatedFailures], [.. flaky]);
    }

    /// <summary>
    /// Runs only the given tests (<c>&lt;class&gt;#&lt;method&gt;</c>): Maven <c>-Dtest=A#m1+m2,B#m3</c> with simple class names, Gradle one
    /// <c>--tests</c> per method. Parameters after the method name (<c>m[1]</c>, <c>m(int)</c>) are dropped.
    /// </summary>
    public static IReadOnlyList<string> IsolatedArguments(BuildSetup setup, IEnumerable<string> tests)
    {
        var byClass = tests
            .Select(t => (Class: t[..t.IndexOf('#', StringComparison.Ordinal)], Method: MethodName(t[(t.IndexOf('#', StringComparison.Ordinal) + 1)..])))
            .Distinct()
            .GroupBy(t => t.Class, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        if (setup.Tool == BuildToolKind.Gradle)
        {
            var arguments = new List<string> { GradleProbe.Task(setup.GradleProject, "test") };
            foreach (var test in byClass.SelectMany(g => g))
            {
                arguments.AddRange(["--tests", $"{test.Class.Replace('$', '.')}.{test.Method}"]);
            }

            arguments.Add("--continue");
            return arguments;
        }

        var selector = string.Join(",", byClass.Select(g => g.Key[(g.Key.LastIndexOf('.') + 1)..] + "#" + string.Join("+", g.Select(t => t.Method))));
        return ["-Dtest=" + selector, "-Dsurefire.failIfNoSpecifiedTests=false", "-DfailIfNoTests=false", "-Dmaven.test.failure.ignore=true", "test"];

        static string MethodName(string name)
        {
            var end = name.IndexOfAny(['[', '(']);
            return end > 0 ? name[..end] : name;
        }
    }

    private static List<RegressionInfo> Stripped(IEnumerable<RegressionInfo> regressions, string copy) =>
        regressions.Select(r => r with { Message = r.Message is null ? null : BuildErrorExtractor.StripRoot(r.Message, copy) }).ToList();

    /// <summary>
    /// Runs a step and writes its log; an infrastructure failure retries it on a new copy (P04 §3.6). The first attempt of
    /// step 2 reuses the copy of step 1 (<paramref name="reuseCopy"/>).
    /// </summary>
    private async Task<(BuildResult Result, string Copy)> RunStepAsync(
        Job job, string step, IReadOnlyList<string> arguments, string? reuseCopy, IReadOnlyCollection<string> generated, int run = 1, bool withSuite = true)
    {
        var attempts = Math.Max(1, job.Options.InfraAttempts);
        string code = ErrorCodes.B3ToolError, reason = "";
        string? lastLog = null;
        var lastSeconds = 0.0;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                job.Report(ArrowEventTypes.B3InfraRetry, $"{step}: {reason}; thử lại lần {attempt}/{attempts} sau {job.Options.InfraWait.TotalSeconds:0} s",
                    ("step", step), ("attempt", attempt), ("attempts", attempts), ("wait_seconds", job.Options.InfraWait.TotalSeconds), ("code", code));
                if (job.Options.InfraWait > TimeSpan.Zero)
                {
                    await Task.Delay(job.Options.InfraWait, _time, job.Ct).ConfigureAwait(false);
                }
            }

            string copy;
            try
            {
                copy = attempt == 1 && reuseCopy is not null ? reuseCopy : await NewCopyAsync(job, withSuite).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                (code, reason) = (ErrorCodes.B3Workspace, $"không tạo được bản sao ({ex.Message})");
                continue;
            }

            var log = $"logs/{step}{(run > 1 ? $"-run{run}" : "")}{(attempt > 1 ? $"-try{attempt}" : "")}.log";
            var logPath = Path.Combine(job.OutputPath, log.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            job.Report(ArrowEventTypes.B3VerifyStep, $"{step} (lần {attempt})", ("step", step), ("attempt", attempt));
            BuildResult result;
            try
            {
                result = await builds.RunAsync(job.Setup, copy, arguments, job.Task.Workspace.JdkHome, job.Options.StepTimeout, logPath, job.Ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                (code, reason) = (ErrorCodes.B3ToolError, $"không chạy được công cụ build ({ex.Message})");
                await DropCopyAsync(job, copy).ConfigureAwait(false);
                continue;
            }

            if (InfraFailure(result, step, generated, copy) is { } infra)
            {
                (code, reason) = infra;
                (lastLog, lastSeconds) = (log, result.Process.Duration.TotalSeconds);
                await DropCopyAsync(job, copy).ConfigureAwait(false);
                continue;
            }

            // Only the last attempt of a step is listed; the logs of earlier attempts stay in logs/.
            job.Steps.Add(new VerifyStepRecord(step, result.Failure == BuildFailureKind.None, Math.Round(result.Process.Duration.TotalSeconds, 1), attempt, log));
            return (result, copy);
        }

        job.Steps.Add(new VerifyStepRecord(step, false, Math.Round(lastSeconds, 1), attempts, lastLog));
        throw new InfraStop(code, $"{step}: {reason} sau {attempts} lần thử");
    }

    /// <summary>
    /// The infrastructure code of a failed attempt (P04 §3.6), or null when the failure belongs to the candidate. A javac error in
    /// the generated file is never an infrastructure failure.
    /// </summary>
    internal static (string Code, string Reason)? InfraFailure(BuildResult result, string step, IReadOnlyCollection<string> generated, string copy)
    {
        var process = result.Process;
        if (result.Failure == BuildFailureKind.Timeout || process.TimedOut)
        {
            return (ErrorCodes.B3Timeout, $"quá {process.Duration.TotalSeconds:0} s");
        }

        if (result.Failure == BuildFailureKind.None)
        {
            return null;
        }

        if (process.ExitCode is null or < 0)
        {
            return (ErrorCodes.B3ToolError, $"tiến trình build dừng bất thường (mã thoát {process.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "không có"})");
        }

        if (step == VerifySteps.Compile && JavacErrorParser.Parse(process.Output, generated, copy).Any(e => e.InGeneratedFile))
        {
            return null;
        }

        if (result.Failure == BuildFailureKind.Network || DependencyMarkers.Any(m => process.Output.Contains(m, StringComparison.Ordinal)))
        {
            return (ErrorCodes.B3Dependency, "không tải được dependency: " + (BuildFailureClassifier.FirstError(process.Output) ?? process.Tail(1)));
        }

        if (step == VerifySteps.Compile && ToolMarkers.Any(m => process.Output.Contains(m, StringComparison.Ordinal)))
        {
            return (ErrorCodes.B3ToolError, "công cụ build lỗi: " + (BuildFailureClassifier.FirstError(process.Output) ?? process.Tail(1)));
        }

        return null;
    }

    private static readonly string[] DependencyMarkers =
    [
        "Could not resolve dependencies", "Could not transfer artifact", "Connection timed out", "Unknown host", "Read timed out",
    ];

    /// <summary>Signs that Maven or Gradle itself broke while compiling (the test JVM is not involved in step 1).</summary>
    private static readonly string[] ToolMarkers =
    [
        "java.lang.OutOfMemoryError", "Could not create the Java Virtual Machine", "Error occurred during initialization of VM",
    ];

    private static bool NoTestsMarker(string output) =>
        output.Contains("No tests found for given includes", StringComparison.Ordinal)
        || output.Contains("No tests were executed", StringComparison.Ordinal)
        || output.Contains("No tests matching", StringComparison.Ordinal);

    /// <summary>A new copy of the compiled snapshot with the files of the suite (P04 §3.1).</summary>
    private async Task<string> NewCopyAsync(Job job, bool withSuite = true)
    {
        var snapshot = Path.Combine(job.Task.Run.HomeRoot, job.Task.Workspace.CompiledSnapshot.Replace('/', Path.DirectorySeparatorChar));
        var copy = Path.Combine(job.Task.Run.WorkDir, "v-" + Guid.NewGuid().ToString("N")[..8]);
        job.Copies.Add(copy);
        await workspaces.CloneAsync(snapshot, copy, job.Ct).ConfigureAwait(false);
        foreach (var (path, content) in withSuite ? job.Suite.Files : new Dictionary<string, string>())
        {
            var file = Path.Combine(copy, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, content.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false), job.Ct).ConfigureAwait(false);
        }

        return copy;
    }

    private async Task DropCopyAsync(Job job, string copy)
    {
        if (!job.Options.KeepWorkspaces)
        {
            await DeleteQuietlyAsync(copy).ConfigureAwait(false);
        }
    }

    private async Task DeleteQuietlyAsync(string copy)
    {
        try
        {
            await workspaces.DeleteAsync(copy, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A copy that cannot be deleted now is removed with work/<run_id> at the end of the run (P09 §8).
        }
    }

    /// <summary>Copies the reports chosen by <paramref name="keep"/> to <c>reports/&lt;step&gt;/</c> of the verification.</summary>
    private static void SaveReports(Job job, string buildRoot, string step, Func<string, bool> keep)
    {
        if (!Directory.Exists(buildRoot))
        {
            return;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(buildRoot, "TEST-*.xml", options).Where(TestReportReader.IsReport).Where(keep))
        {
            var target = Path.Combine(job.OutputPath, "reports", step, Path.GetFileName(file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private async Task<IReadOnlyList<string>> MethodsAtLinesAsync(string code, IReadOnlyList<int> lines, CancellationToken ct)
    {
        if (lines.Count == 0 || code.Length == 0)
        {
            return [];
        }

        try
        {
            using var lease = await javaHelpers.RentAsync(ct).ConfigureAwait(false);
            var methods = await lease.Helper.MethodAtLineAsync(code, lines, ct).ConfigureAwait(false);
            return lines.Select(l => methods.GetValueOrDefault(l)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        }
        catch (ArrowException)
        {
            return []; // Level 3 then falls back to level 2.
        }
    }

    private static FailureInfo Relative(FailureInfo failure, string copy) => failure with
    {
        Message = failure.Message is null ? null : BuildErrorExtractor.StripRoot(failure.Message, copy),
        TraceHead = failure.TraceHead is null ? null : BuildErrorExtractor.StripRoot(failure.TraceHead, copy),
    };

    private static string Excerpt(string output, string copy) => BuildErrorExtractor.Excerpt(output, copy, ExcerptLines);

    /// <summary>The build setup of <c>workspace.json</c>.</summary>
    public static BuildSetup SetupOf(WorkspaceInfo workspace, string homeRoot)
    {
        var module = workspace.BuildTool == BuildToolKind.Gradle
            ? workspace.GradleProject
            : workspace.MavenModuleSelector.Count >= 2 ? workspace.MavenModuleSelector[1] : null;
        return new BuildSetup(workspace.BuildTool, workspace.Executable, workspace.ExecutableInRepo, workspace.BuildRoot, workspace.BuildMode,
            module, workspace.CommonFlags, workspace.InitScripts.FirstOrDefault());
    }

    /// <summary>Step 1 (P04 §3.2).</summary>
    public static IReadOnlyList<string> CompileArguments(BuildSetup setup) =>
        setup.Tool == BuildToolKind.Gradle ? [GradleProbe.Task(setup.GradleProject, "testClasses")] : ["-DskipTests", "test-compile"];

    /// <summary>Step 2: only the generated class; failing tests do not fail the command (P04 §3.2).</summary>
    public static IReadOnlyList<string> RunTargetArguments(BuildSetup setup, string testFqn) =>
        setup.Tool == BuildToolKind.Gradle
            ? [GradleProbe.Task(setup.GradleProject, "test"), "--tests", testFqn, "--continue"]
            : ["-Dtest=" + testFqn[(testFqn.LastIndexOf('.') + 1)..], "-Dsurefire.failIfNoSpecifiedTests=false", "-DfailIfNoTests=false", "-Dmaven.test.failure.ignore=true", "test"];

    /// <summary>The file of the class <c>test_fqn</c>: the one named like its simple name.</summary>
    public static string MainTestFile(SuiteFiles suite)
    {
        var simple = suite.TestFqn[(suite.TestFqn.LastIndexOf('.') + 1)..] + ".java";
        return suite.Files.Keys.FirstOrDefault(p => JavacErrorParser.FileName(p) == simple) ?? suite.Files.Keys.FirstOrDefault() ?? simple;
    }

    private static string MainCode(SuiteFiles suite) => suite.Files.GetValueOrDefault(MainTestFile(suite)) ?? "";
}
