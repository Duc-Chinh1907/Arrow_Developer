using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.Source;

namespace Arrow.Business.Analysis;

/// <summary>Module results shared by every CUT of a module in a run (P01 §7). <see cref="Failure"/> is set when the module cannot be used.</summary>
public sealed class ModuleResult
{
    /// <summary>Why the module cannot be used; null when it can.</summary>
    public BlockOutcome? Failure { get; init; }

    /// <summary>FQN of the CUT that computed this result; the others write <c>shared_from.txt</c>.</summary>
    public required string FirstCut { get; init; }

    public required string ModuleRoot { get; init; }

    public BuildPlan? Plan { get; init; }

    public BuildSetup? Setup { get; init; }

    public string ToolVersion { get; init; } = "";

    public bool Wrapper { get; init; }

    public bool WrapperFallback { get; init; }

    public int JdkMajor { get; init; }

    public string JdkHome { get; init; } = "";

    public string JdkReason { get; init; } = "";

    public string? Declared { get; init; }

    public string? DeclaredSource { get; init; }

    public int? DeclaredMajor { get; init; }

    public IReadOnlyList<DependencyRef> Direct { get; init; } = [];

    public IReadOnlyList<ResolvedDependency> Resolved { get; init; } = [];

    /// <summary>Test classpath: sibling class folders relative to the workspace root, jars absolute (P01 §4.4 step 3).</summary>
    public IReadOnlyList<string> Classpath { get; init; } = [];

    public string TestRoot { get; init; } = "";

    public IReadOnlyList<string> TestIncludes { get; init; } = [];

    public IReadOnlyList<string> TestExcludes { get; init; } = [];

    public TestFrameworkMetadata? Framework { get; init; }

    public IReadOnlyList<string> Modules { get; init; } = [];

    public BaselineInfo? Baseline { get; init; }

    public WorkspaceInfo? Workspace { get; init; }

    public IReadOnlyDictionary<string, string> MissingReasons { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public double PlanSeconds { get; init; }

    public double CompileSeconds { get; init; }

    public IReadOnlyList<double> BaselineSeconds { get; init; } = [];
}

/// <summary>
/// The module part of block 1 (P01 §4.1–§4.5, §6): build plan, build tool, declared Java level, JDK, compile with the dependency list
/// and classpath, <c>compiled_snapshot</c>, and the baseline runs.
/// </summary>
public sealed class ModuleAnalyzer(IBuildExecutor builds, IWorkspaceManager workspaces, AnalysisOptions options, ErrorCatalog catalog)
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    /// <summary>Test files read to choose between several frameworks (P01 §4.5).</summary>
    private const int MaxTestSources = 400;

    /// <summary>
    /// Analyses the module at <paramref name="moduleRoot"/> for the CUT whose source root is <paramref name="sourceRoot"/>.
    /// Logs go to <paramref name="logsDir"/> (absolute). Never throws for a broken project; only <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<ModuleResult> AnalyzeAsync(ProjectWorkspace workspace, string moduleRoot, string sourceRoot, string firstCut, RunContext run, string logsDir, CancellationToken ct)
    {
        var job = new Job(workspace, moduleRoot, sourceRoot, firstCut, run, logsDir, ct);
        try
        {
            return await RunAsync(job).ConfigureAwait(false);
        }
        catch (Stop stop)
        {
            return new ModuleResult { Failure = stop.Outcome, FirstCut = firstCut, ModuleRoot = moduleRoot, Warnings = job.Warnings, MissingReasons = job.Missing };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var outcome = Outcome(BlockOutcomeKind.Infra, ErrorCodes.B1Workspace, $"Lỗi khi chép hay xoá bản sao làm việc: {ex.Message}");
            return new ModuleResult { Failure = outcome, FirstCut = firstCut, ModuleRoot = moduleRoot, Warnings = job.Warnings, MissingReasons = job.Missing };
        }
    }

    private async Task<ModuleResult> RunAsync(Job job)
    {
        var plans = BuildRootResolver.Plans(job.Workspace.Layout, job.ModuleRoot, job.Warnings);
        if (plans.Count == 0)
        {
            throw Stop.Blocked(this, ErrorCodes.B1BuildRootNotFound, $"Không có file build Maven hay Gradle ở {job.ModuleRoot} hay các thư mục cha của nó.");
        }

        if (options.JdkHomes.Count == 0)
        {
            throw new Stop(Outcome(BlockOutcomeKind.Blocked, ErrorCodes.B1JdkMissing, null, args: new Dictionary<string, string> { ["jdk"] = "17" }));
        }

        Stop? last = null;
        foreach (var plan in plans)
        {
            try
            {
                return await TryPlanAsync(job, plan).ConfigureAwait(false);
            }
            catch (Stop stop) when (stop.TryNextPlan && plan != plans[^1])
            {
                last = stop;
                job.Warnings.Add($"Cách build {plan.Source} ({plan.BuildRoot}) không dùng được: {stop.Outcome.Message}; thử cách tiếp theo.");
            }
        }

        throw last ?? Stop.Blocked(this, ErrorCodes.B1BaselineBuildFailed, null);
    }

    private async Task<ModuleResult> TryPlanAsync(Job job, BuildPlan plan)
    {
        job.Report(ArrowEventTypes.B1Plan, $"Build root {plan.BuildRoot} ({(plan.Mode == BuildMode.Reactor ? "reactor" : "standalone")})",
            ("build_root", plan.BuildRoot), ("mode", plan.Mode.ToString().ToLowerInvariant()), ("module", plan.Module));
        var hash = ShortHash(job.ModuleRoot);
        var moduleWork = Path.Combine(job.Run.WorkDir, "block1-" + hash);
        Directory.CreateDirectory(moduleWork);
        var build = Path.Combine(job.Run.WorkDir, "build-" + hash);
        var snapshot = Path.Combine(job.Run.WorkDir, "snapshot-" + hash);

        var watch = Stopwatch.StartNew();
        var copy = await FreshCopyAsync(job.Workspace.Checkout.Root, build, job.Ct).ConfigureAwait(false);

        // Build tool and its version, with the default JDK (P01 §4.2).
        var tool = await ResolveToolAsync(job, plan, build, moduleWork).ConfigureAwait(false);
        var probe = tool.ProbeJdk;
        var setup = tool.Setup;

        // Declared Java level, test root, test patterns and declared dependencies (P01 §4.3, §4.4, §4.7).
        MavenProject? pom = null;
        GradleInfo? gradle = null;
        var moduleRoot = job.ModuleRoot;
        if (plan.Tool == BuildToolKind.Maven)
        {
            pom = await EffectivePomAsync(job, setup, build, moduleWork, probe, tool.Version).ConfigureAwait(false);
        }
        else
        {
            (gradle, plan, setup, moduleRoot) = await GradleInfoAsync(job, plan, setup, build, probe).ConfigureAwait(false);
        }

        var declared = pom?.Declared ?? gradle?.Declared;
        var declaredSource = pom?.DeclaredSource ?? gradle?.DeclaredSource;
        var declaredMajor = JavaVersions.ParseLevel(declared);
        if (declaredMajor is null && !job.Missing.ContainsKey("java"))
        {
            job.Missing["java"] = "file build không khai báo mức Java";
        }

        var planSeconds = watch.Elapsed.TotalSeconds;

        // Compile, snapshot, baseline; switch JDK when the test JVM cannot start (P01 §4.3 step 3).
        var jdkPlan = JdkSelector.Select(declaredMajor, options.JdkHomes.Keys.ToList());
        if (jdkPlan.BelowDeclared)
        {
            job.Warnings.Add($"Project khai báo Java {declaredMajor} nhưng máy không có JDK đủ mức; dùng JDK {jdkPlan.Order[0]}.");
        }

        var skip = new List<int>();
        var lastChance = false;
        var compileSeconds = 0.0;
        while (true)
        {
            Compiled compiled;
            try
            {
                compiled = await CompileAsync(job, plan, setup, build, jdkPlan, skip, tool.Version, copy).ConfigureAwait(false);
            }
            catch (Stop) when (skip.Count > 0)
            {
                // No other JDK compiles: keep the JVM-start problem of the first JDK rather than failing the module.
                job.Warnings.Add($"Không JDK nào khác biên dịch được; giữ JDK {skip[0]}.");
                skip.Clear();
                lastChance = true;
                compiled = await CompileAsync(job, plan, setup, build, jdkPlan, skip, tool.Version, copy).ConfigureAwait(false);
            }

            compileSeconds += compiled.Seconds;
            copy = false;
            var (resolved, classpath) = ReadDependencies(job, plan, compiled, build, moduleRoot);

            await workspaces.DeleteAsync(snapshot, job.Ct).ConfigureAwait(false);
            MoveDirectory(build, snapshot);

            var canSwitch = !lastChance && options.JdkHomes.Keys.Except(skip).Any(j => j != compiled.Jdk);
            var baseline = await BaselineAsync(job, plan, setup, snapshot, hash, compiled.Jdk, canSwitch).ConfigureAwait(false);
            if (baseline is null)
            {
                job.Warnings.Add($"JVM chạy test không khởi động được với JDK {compiled.Jdk}; thử JDK khác.");
                skip.Add(compiled.Jdk);
                await workspaces.DeleteAsync(snapshot, job.Ct).ConfigureAwait(false);
                continue;
            }

            var testRoot = TestRoot(job, plan, pom, gradle, build, moduleRoot);
            var reason = compiled.Tried.Count == 1 && skip.Count == 0 ? jdkPlan.Reason : JdkSelector.FallbackReason(skip.Concat(compiled.Tried));
            var direct = pom?.Dependencies ?? gradle?.Dependencies ?? [];
            var framework = TestFrameworkDetector.Detect(
                resolved.Count > 0 ? resolved : direct.Select(d => new ResolvedDependency(d.Group, d.Artifact, d.Version, d.Scope, null)).ToList(),
                TestSources(job.Workspace.Checkout.Root, testRoot),
                direct);
            if (framework.Name is null)
            {
                job.Missing["test_framework"] = TestFrameworkDetector.NoFrameworkReason;
                job.Warnings.Add("Không thấy JUnit hay TestNG trong dependency test; nên thêm JUnit vào project.");
            }

            var groups = BaselineEvaluator.Classify(baseline.Runs);
            if (groups.Passing.Count + groups.Flaky.Count + groups.Failing.Count == 0)
            {
                job.Warnings.Add("Module chưa có test sẵn; bước 5 chỉ kiểm tra test mới.");
            }
            else if (groups.Failing.Count > 0)
            {
                job.Warnings.Add($"Có {groups.Failing.Count} test fail sẵn trong module; các test này không dùng để kết luận regression.");
            }

            var home = job.Run.HomeRoot;
            var finalSetup = setup;
            var workspaceInfo = new WorkspaceInfo(
                Relative(home, job.Workspace.Checkout.Root),
                Relative(home, snapshot),
                job.Workspace.Checkout.SourceTreeHash,
                compiled.Jdk,
                options.JdkHomes[compiled.Jdk],
                plan.Tool,
                finalSetup.Executable,
                finalSetup.ExecutableInRepo,
                plan.BuildRoot,
                plan.Mode,
                plan.Source,
                BuildCommands.MavenSelector(finalSetup),
                plan.Tool == BuildToolKind.Gradle ? finalSetup.GradleProject : null,
                finalSetup.CommonFlags,
                finalSetup.InitScript is null ? [] : [finalSetup.InitScript]);

            var baselineInfo = new BaselineInfo(
                options.BaselineRuns,
                new BaselineCommands(Shown(finalSetup, BuildCommands.CompileWithDependencies(finalSetup, tool.Version)), Shown(finalSetup, BuildCommands.ModuleTest(finalSetup))),
                plan.BuildRoot,
                groups.Passing,
                groups.Flaky,
                groups.Failing,
                new BaselineDurations(Math.Round(compiled.CopySeconds, 1), Math.Round(compileSeconds, 1), baseline.Seconds.Select(s => Math.Round(s, 1)).ToList()));

            return new ModuleResult
            {
                FirstCut = job.FirstCut,
                ModuleRoot = moduleRoot,
                Plan = plan,
                Setup = finalSetup,
                ToolVersion = tool.Version,
                Wrapper = tool.Wrapper,
                WrapperFallback = tool.Fallback,
                JdkMajor = compiled.Jdk,
                JdkHome = options.JdkHomes[compiled.Jdk],
                JdkReason = reason,
                Declared = declared,
                DeclaredSource = declaredSource,
                DeclaredMajor = declaredMajor,
                Direct = direct,
                Resolved = resolved,
                Classpath = classpath,
                TestRoot = testRoot,
                TestIncludes = pom is not null ? pom.TestIncludes ?? EffectivePomReader.DefaultSurefireIncludes : gradle?.TestIncludes ?? [],
                TestExcludes = pom is not null ? pom.TestExcludes ?? [] : gradle?.TestExcludes ?? [],
                Framework = framework,
                Modules = Modules(job, plan, gradle, build),
                Baseline = baselineInfo,
                Workspace = workspaceInfo,
                MissingReasons = job.Missing,
                Warnings = job.Warnings,
                PlanSeconds = planSeconds,
                CompileSeconds = compileSeconds,
                BaselineSeconds = baseline.Seconds,
            };
        }
    }

    private sealed record Tool(BuildSetup Setup, string Version, bool Wrapper, bool Fallback, int ProbeJdk);

    /// <summary>Wrapper first, the machine's tool otherwise or when the wrapper fails (P01 §4.2).</summary>
    private async Task<Tool> ResolveToolAsync(Job job, BuildPlan plan, string build, string moduleWork)
    {
        var gradle = plan.Tool == BuildToolKind.Gradle;
        var probeOrder = JdkSelector.Select(null, options.JdkHomes.Keys.ToList()).Order;
        var wrapperName = gradle ? (Windows ? "gradlew.bat" : "gradlew") : (Windows ? "mvnw.cmd" : "mvnw");
        var wrapper = new[] { plan.BuildRoot, "." }.Distinct()
            .Select(dir => BuildLayout.Join(dir, wrapperName))
            .FirstOrDefault(rel => File.Exists(Path.Combine(job.Workspace.Checkout.Root, rel)));
        var system = gradle ? options.Gradle : options.Maven;
        string? initScript = null;
        if (gradle)
        {
            initScript = Path.Combine(moduleWork, GradleProbe.FileName);
            File.WriteAllText(initScript, GradleProbe.InitScript.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        }

        IReadOnlyList<string> flags = gradle ? BuildCommands.GradleCommonFlags : ["-B"];
        var name = gradle ? "Gradle" : "Maven";
        if (wrapper is not null)
        {
            MakeExecutable(Path.Combine(build, wrapper));
            var setup = new BuildSetup(plan.Tool, wrapper, true, plan.BuildRoot, plan.Mode, plan.Module, flags, initScript);
            // The wrapper may download an old Gradle or Maven that does not run on the default JDK (Gradle 3.5 on JDK 17: "Could not
            // determine java version"). Try the other JDKs in ascending order before giving up on the wrapper (P01 §4.2 step 3).
            BuildResult? result = null;
            foreach (var jdk in probeOrder)
            {
                result = await RunStepAsync(job, setup, build, BuildCommands.Version, jdk, jdk == probeOrder[0] ? "version-wrapper" : $"version-wrapper-jdk{jdk}").ConfigureAwait(false);
                if (result.Failure == BuildFailureKind.None)
                {
                    return Finish(setup, result, wrapperUsed: true, fallback: false, jdk);
                }
            }

            if (system is null)
            {
                throw Stop.Blocked(this, ErrorCodes.B1BuildToolMissing,
                    $"Wrapper {wrapper} không chạy được ({BuildFailureClassifier.FirstError(result!.Process.Output)}) và máy không có {name}.");
            }

            job.Warnings.Add($"Wrapper {wrapper} không chạy được; dùng {name} của máy.");
            job.Report(ArrowEventTypes.B1Plan, $"Wrapper lỗi, dùng {name} của máy", ("wrapper_fallback", true));
            var fallbackSetup = setup with { Executable = system, ExecutableInRepo = false };
            var again = await RunStepAsync(job, fallbackSetup, build, BuildCommands.Version, probeOrder[0], "version").ConfigureAwait(false);
            if (again.Failure != BuildFailureKind.None)
            {
                throw Stop.Blocked(this, ErrorCodes.B1BuildToolMissing, $"{name} của máy ({system}) không chạy được: {BuildFailureClassifier.FirstError(again.Process.Output)}");
            }

            return Finish(fallbackSetup, again, wrapperUsed: false, fallback: true, probeOrder[0]);
        }

        if (system is null)
        {
            throw Stop.Blocked(this, ErrorCodes.B1BuildToolMissing, null);
        }

        var systemSetup = new BuildSetup(plan.Tool, system, false, plan.BuildRoot, plan.Mode, plan.Module, flags, initScript);
        var version = await RunStepAsync(job, systemSetup, build, BuildCommands.Version, probeOrder[0], "version").ConfigureAwait(false);
        if (version.Failure != BuildFailureKind.None)
        {
            throw Stop.Blocked(this, ErrorCodes.B1BuildToolMissing, $"{name} của máy ({system}) không chạy được: {BuildFailureClassifier.FirstError(version.Process.Output)}");
        }

        return Finish(systemSetup, version, wrapperUsed: false, fallback: false, probeOrder[0]);

        static Tool Finish(BuildSetup setup, BuildResult result, bool wrapperUsed, bool fallback, int probeJdk)
        {
            var version = BuildFailureClassifier.ToolVersion(result.Process.Output) ?? "";
            var flags = setup.Tool == BuildToolKind.Maven ? BuildCommands.MavenCommonFlags(version, Windows) : BuildCommands.GradleCommonFlags;
            return new Tool(setup with { CommonFlags = flags }, version, wrapperUsed, fallback, probeJdk);
        }
    }

    /// <summary>Effective POM of the module, before compiling (P01 §4.3). A failure only leaves <c>missing_reasons.java</c>.</summary>
    private async Task<MavenProject?> EffectivePomAsync(Job job, BuildSetup setup, string build, string moduleWork, int probeJdk, string version)
    {
        var file = Path.Combine(moduleWork, "effective-pom.xml");
        File.Delete(file);
        var result = await RunStepAsync(job, setup, build, BuildCommands.MavenEffectivePom(version, file), probeJdk, "effective-pom").ConfigureAwait(false);
        if (result.Failure != BuildFailureKind.None || !File.Exists(file))
        {
            job.Missing["java"] = "help:effective-pom lỗi: " + BuildFailureClassifier.FirstError(result.Process.Output);
            return null;
        }

        try
        {
            return EffectivePomReader.SelectTarget(EffectivePomReader.Parse(await File.ReadAllTextAsync(file, job.Ct).ConfigureAwait(false)), Path.Combine(build, job.ModuleRoot));
        }
        catch (XmlException ex)
        {
            job.Missing["java"] = "effective POM không đọc được: " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Runs <c>arrowInfo</c>; when the project that owns the source root is not the planned one (a project folder set in the
    /// settings file), switches to it. The module root is the folder Gradle reports, which must contain the CUT.
    /// </summary>
    private async Task<(GradleInfo?, BuildPlan, BuildSetup, string)> GradleInfoAsync(Job job, BuildPlan plan, BuildSetup setup, string build, int probeJdk)
    {
        var result = await RunStepAsync(job, setup, build, BuildCommands.GradleInfo(setup.GradleProject), probeJdk, "gradle-info").ConfigureAwait(false);
        if (result.Failure != BuildFailureKind.None)
        {
            job.Missing["java"] = "init script Gradle lỗi: " + BuildFailureClassifier.FirstError(result.Process.Output);
            return (null, plan, setup, job.ModuleRoot);
        }

        var info = GradleProbe.Parse(result.Process.Output);
        var buildRoot = Path.Combine(build, plan.BuildRoot);
        var fromBuildRoot = BuildRootResolver.RelativeTo(plan.BuildRoot, job.SourceRoot) ?? job.SourceRoot;
        if (info.Projects.Count > 0 && BuildRootResolver.GradleOwner(info.Projects, buildRoot, fromBuildRoot) is { } owner && owner != setup.GradleProject)
        {
            plan = plan with { Module = owner };
            setup = setup with { Module = owner };
            var again = await RunStepAsync(job, setup, build, BuildCommands.GradleInfo(owner), probeJdk, "gradle-info-project").ConfigureAwait(false);
            if (again.Failure == BuildFailureKind.None)
            {
                info = GradleProbe.Parse(again.Process.Output);
            }
        }

        var moduleRoot = info.ProjectDir.Length > 0 && BuildRootResolver.RelativeTo(build, info.ProjectDir) is { } dir ? dir : plan.ExpectedModuleRoot;
        if (!BuildRootResolver.ModuleMatches(job.SourceRoot, moduleRoot))
        {
            throw Stop.Blocked(this, ErrorCodes.B1ModuleMismatch, null, tryNextPlan: true,
                args: new Dictionary<string, string> { ["module_root"] = moduleRoot, ["build_root"] = plan.BuildRoot });
        }

        return (info, plan, setup, moduleRoot);
    }

    private sealed record Compiled(int Jdk, IReadOnlyList<int> Tried, BuildResult Result, double Seconds, double CopySeconds);

    /// <summary>Compiles with the JDKs of <paramref name="plan"/> in order, a fresh copy for each attempt (P01 §4.3 step 3, §6.1).</summary>
    private async Task<Compiled> CompileAsync(Job job, BuildPlan buildPlan, BuildSetup setup, string build, JdkPlan plan, IReadOnlyCollection<int> skip, string version, bool reuseCopy)
    {
        var tried = new List<int>();
        BuildResult? failed = null;
        string? failedLog = null;
        var seconds = 0.0;
        var copySeconds = 0.0;
        foreach (var jdk in plan.Order.Where(j => !skip.Contains(j)))
        {
            if (tried.Count > 0 || !reuseCopy)
            {
                var copyWatch = Stopwatch.StartNew();
                await FreshCopyAsync(job.Workspace.Checkout.Root, build, job.Ct).ConfigureAwait(false);
                copySeconds += copyWatch.Elapsed.TotalSeconds;
            }

            tried.Add(jdk);
            job.Report(ArrowEventTypes.B1Compile, $"Biên dịch với JDK {jdk}", ("jdk", jdk));
            var log = $"compile-jdk{jdk}";
            var result = await RunStepAsync(job, setup, build, BuildCommands.CompileWithDependencies(setup, version), jdk, log).ConfigureAwait(false);
            seconds += result.Process.Duration.TotalSeconds;
            switch (result.Failure)
            {
                case BuildFailureKind.None:
                    return new Compiled(jdk, tried, result, seconds, copySeconds);
                case BuildFailureKind.Timeout:
                    throw new Stop(Outcome(BlockOutcomeKind.Infra, ErrorCodes.B1BaselineTimeout, null, log: job.LogPath(log)));
                case BuildFailureKind.Network:
                    throw new Stop(Outcome(BlockOutcomeKind.Infra, ErrorCodes.B1DependencyResolution,
                        $"Không tải được dependency hay plugin: {BuildFailureClassifier.FirstError(result.Process.Output)}", log: job.LogPath(log)));
                default:
                    // Report the first failure: the JDK that the build declares, not the last one tried.
                    if (failed is null)
                    {
                        (failed, failedLog) = (result, log);
                    }

                    break;
            }
        }

        var output = failed?.Process.Output ?? "";
        var missingArtifact = BuildFailureClassifier.IsMissingArtifact(output);
        var module = job.ModuleRoot;
        throw new Stop(
            Outcome(
                BlockOutcomeKind.Blocked,
                missingArtifact ? ErrorCodes.B1DependencyResolution : ErrorCodes.B1BaselineBuildFailed,
                missingArtifact
                    ? $"Thiếu artifact khi biên dịch module {module}: {BuildFailureClassifier.FirstError(output)}"
                    : $"Không biên dịch được module {module} với JDK {string.Join(", ", tried)}",
                firstError: BuildFailureClassifier.FirstError(output),
                log: failedLog is null ? null : job.LogPath(failedLog)),
            tryNextPlan: true);
    }

    /// <summary>Resolved dependencies and the test classpath written by the compile step (P01 §4.4).</summary>
    private static (IReadOnlyList<ResolvedDependency>, IReadOnlyList<string>) ReadDependencies(Job job, BuildPlan plan, Compiled compiled, string build, string moduleRoot)
    {
        IReadOnlyList<ResolvedDependency> resolved;
        IReadOnlyList<string> classpath;
        if (plan.Tool == BuildToolKind.Gradle)
        {
            var info = GradleProbe.Parse(compiled.Result.Process.Output);
            (resolved, classpath) = (info.Resolved, info.Classpath);
        }
        else
        {
            var moduleDir = Path.Combine(build, moduleRoot);
            var deps = Path.Combine(moduleDir, BuildCommands.MavenDependencyFile);
            var cp = Path.Combine(moduleDir, BuildCommands.MavenClasspathFile);
            resolved = File.Exists(deps) ? MavenDependencyList.Parse(File.ReadAllText(deps)) : [];
            classpath = File.Exists(cp) ? File.ReadAllText(cp).Trim().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) : [];
            if (!File.Exists(deps))
            {
                job.Missing["dependencies"] = "dependency:list không ghi file của module đích";
            }

            foreach (var generated in Directory.EnumerateFiles(Path.Combine(build, plan.BuildRoot), "arrow-*.txt", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 8 }))
            {
                File.Delete(generated);
            }
        }

        return (resolved, classpath.Select(entry => BuildRootResolver.RelativeTo(build, entry) ?? entry.Replace('\\', '/')).ToList());
    }

    private sealed record BaselineRuns(IReadOnlyList<IReadOnlyList<TestCaseResult>> Runs, IReadOnlyList<double> Seconds);

    /// <summary>
    /// <c>baseline_runs</c> runs of the module tests, each on a fresh copy of the snapshot (P01 §6.2). Null when the first run shows that
    /// the test JVM cannot start and <paramref name="canSwitch"/> allows another JDK.
    /// </summary>
    private async Task<BaselineRuns?> BaselineAsync(Job job, BuildPlan plan, BuildSetup setup, string snapshot, string hash, int jdk, bool canSwitch)
    {
        var runs = new List<IReadOnlyList<TestCaseResult>>();
        var seconds = new List<double>();
        for (var i = 1; i <= Math.Max(1, options.BaselineRuns); i++)
        {
            job.Report(ArrowEventTypes.B1Baseline, $"Baseline lần {i}/{options.BaselineRuns}", ("run", i), ("runs", options.BaselineRuns));
            var watch = Stopwatch.StartNew();
            var copy = Path.Combine(job.Run.WorkDir, $"baseline-{hash}-{i}");
            await workspaces.DeleteAsync(copy, job.Ct).ConfigureAwait(false);
            await workspaces.CloneAsync(snapshot, copy, job.Ct).ConfigureAwait(false);
            var log = $"baseline-{i}";
            var result = await RunStepAsync(job, setup, copy, BuildCommands.ModuleTest(setup), jdk, log).ConfigureAwait(false);
            var tests = TestReportReader.Read(Path.Combine(copy, plan.BuildRoot));
            await workspaces.DeleteAsync(copy, job.Ct).ConfigureAwait(false);
            seconds.Add(watch.Elapsed.TotalSeconds);

            switch (result.Failure)
            {
                case BuildFailureKind.Timeout:
                    throw new Stop(Outcome(BlockOutcomeKind.Infra, ErrorCodes.B1BaselineTimeout, null, log: job.LogPath(log)));
                case BuildFailureKind.Network:
                    throw new Stop(Outcome(BlockOutcomeKind.Infra, ErrorCodes.B1DependencyResolution, null, log: job.LogPath(log)));
            }

            if (i == 1 && canSwitch && JdkSelector.IsJvmStartFailure(result.Process.Output, tests.Count))
            {
                return null;
            }

            if (result.Failure == BuildFailureKind.Build && tests.Count == 0)
            {
                job.Warnings.Add($"Baseline lần {i} lỗi và không có báo cáo test: {BuildFailureClassifier.FirstError(result.Process.Output)}");
            }

            runs.Add(tests);
        }

        return new BaselineRuns(runs, seconds);
    }

    /// <summary>Runs a build step, retrying timeouts and network errors <c>infra_attempts</c> times (P01 §6.2 step 7).</summary>
    private async Task<BuildResult> RunStepAsync(Job job, BuildSetup setup, string projectRoot, IReadOnlyList<string> arguments, int jdk, string logName)
    {
        var attempts = Math.Max(1, options.InfraAttempts);
        for (var attempt = 1; ; attempt++)
        {
            var name = attempt == 1 ? logName : $"{logName}-try{attempt}";
            Directory.CreateDirectory(job.LogsDir);
            var result = await builds.RunAsync(setup, projectRoot, arguments, options.JdkHomes[jdk], options.StepTimeout,
                Path.Combine(job.LogsDir, name + ".log"), job.Ct).ConfigureAwait(false);
            if (result.Failure is BuildFailureKind.Timeout or BuildFailureKind.Network && attempt < attempts)
            {
                job.Warnings.Add($"{logName}: {(result.Failure == BuildFailureKind.Timeout ? "quá thời gian" : "lỗi mạng")} (lần {attempt}), thử lại.");
                if (options.InfraWait > TimeSpan.Zero)
                {
                    await Task.Delay(options.InfraWait, job.Ct).ConfigureAwait(false);
                }

                continue;
            }

            return result;
        }
    }

    private string TestRoot(Job job, BuildPlan plan, MavenProject? pom, GradleInfo? gradle, string build, string moduleRoot)
    {
        var declared = plan.Tool == BuildToolKind.Maven ? pom?.TestSourceDirectory : gradle?.TestSourceDirs.FirstOrDefault();
        if (declared is not null && BuildRootResolver.RelativeTo(build, declared) is { } relative)
        {
            return relative;
        }

        if (declared is not null)
        {
            job.Warnings.Add($"Thư mục test khai báo nằm ngoài project ({declared}); dùng {moduleRoot}/src/test/java.");
        }

        return BuildLayout.Join(moduleRoot, "src/test/java");
    }

    private static IReadOnlyList<string> Modules(Job job, BuildPlan plan, GradleInfo? gradle, string build) =>
        plan.Tool == BuildToolKind.Gradle && gradle is { Projects.Count: > 0 }
            ? gradle.Projects.Where(p => p.Path != ":").Select(p => BuildRootResolver.RelativeTo(build, p.Dir) ?? p.Path).Order(StringComparer.Ordinal).ToList()
            : BuildRootResolver.Modules(job.Workspace.Layout, plan.BuildRoot, plan.Tool);

    /// <summary>Sources of the existing tests of the module, for the framework vote (P01 §4.5).</summary>
    private static IEnumerable<string> TestSources(string projectRoot, string testRoot)
    {
        var dir = Path.Combine(projectRoot, testRoot);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return Directory.EnumerateFiles(dir, "*.java", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Take(MaxTestSources).Select(File.ReadAllText).ToList();
    }

    private async Task<bool> FreshCopyAsync(string from, string to, CancellationToken ct)
    {
        await workspaces.DeleteAsync(to, ct).ConfigureAwait(false);
        await workspaces.CloneAsync(from, to, ct).ConfigureAwait(false);
        return true;
    }

    private static void MoveDirectory(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(300 * attempt);
            }
        }
    }

    private static void MakeExecutable(string file)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(file))
        {
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>A command as shown in <c>baseline.json</c>: the wrapper or tool as configured, not the absolute path of a copy.</summary>
    private static IReadOnlyList<string> Shown(BuildSetup setup, IReadOnlyList<string> arguments) =>
        [setup.ExecutableInRepo ? setup.Executable : Path.GetFileName(setup.Executable), .. BuildCommands.Command(setup, ".", arguments).Skip(1)];

    /// <summary>8 hex characters of the sha256 of the module root, used in folder names (<c>snapshot-3f1a…</c>).</summary>
    public static string ShortHash(string moduleRoot) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(moduleRoot)))[..8];

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    internal BlockOutcome Outcome(BlockOutcomeKind kind, string code, string? detail, string? firstError = null, string? log = null, IReadOnlyDictionary<string, string>? args = null)
    {
        var info = catalog.Describe(code, detail, args);
        return new BlockOutcome(kind, code, info.Message, info.Hint, firstError, log, 0);
    }

    /// <summary>Ends the analysis of a plan or of the module with an outcome.</summary>
    private sealed class Stop(BlockOutcome outcome, bool tryNextPlan = false) : Exception(outcome.Message)
    {
        public BlockOutcome Outcome { get; } = outcome;

        /// <summary>The next build plan may still work (compile failure, module mismatch).</summary>
        public bool TryNextPlan { get; } = tryNextPlan;

        public static Stop Blocked(ModuleAnalyzer owner, string code, string? detail, bool tryNextPlan = false, IReadOnlyDictionary<string, string>? args = null) =>
            new(owner.Outcome(BlockOutcomeKind.Blocked, code, detail, args: args), tryNextPlan);
    }

    /// <summary>State of one module analysis.</summary>
    private sealed class Job(ProjectWorkspace workspace, string moduleRoot, string sourceRoot, string firstCut, RunContext run, string logsDir, CancellationToken ct)
    {
        public ProjectWorkspace Workspace { get; } = workspace;

        public string ModuleRoot { get; } = moduleRoot;

        public string SourceRoot { get; } = sourceRoot;

        public string FirstCut { get; } = firstCut;

        public RunContext Run { get; } = run;

        public string LogsDir { get; } = logsDir;

        public CancellationToken Ct { get; } = ct;

        public List<string> Warnings { get; } = [];

        public Dictionary<string, string> Missing { get; } = new(StringComparer.Ordinal);

        /// <summary>Log path relative to the target folder, as written in <c>outcome.json</c>.</summary>
        public string LogPath(string name) => "block1/logs/" + name + ".log";

        public void Report(string type, string message, params (string Key, object? Value)[] data) =>
            Run.Progress.Report(new ArrowEvent(DateTimeOffset.UtcNow, type, FirstCut, null, null, EventLevel.Info, message,
                data.ToDictionary(d => d.Key, d => d.Value)));
    }
}
