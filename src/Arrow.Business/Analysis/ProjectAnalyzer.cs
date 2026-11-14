using System.Diagnostics;
using System.Security.Cryptography;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Analysis;

/// <summary>
/// Block 1 (P01): gets the code once per run, lists its classes, and analyses the module of each CUT, sharing module results through
/// <see cref="AnalysisCache{TValue}"/>. Writes <c>targets/&lt;cut&gt;/block1/</c> in the order of P01 §8, <c>outcome.json</c> last.
/// </summary>
/// <param name="source">Gets the code.</param>
/// <param name="javaHelper">Rents a java-helper process for <c>list_classes</c>.</param>
/// <param name="modules">Analyses modules.</param>
/// <param name="workspaces">Deletes the working copies at the end of the run.</param>
/// <param name="runs">Writes the result files.</param>
/// <param name="catalog">Error texts in the UI language.</param>
public sealed class ProjectAnalyzer(
    IProjectSource source,
    Func<CancellationToken, Task<JavaHelperPool.Lease>> javaHelper,
    ModuleAnalyzer modules,
    IWorkspaceManager workspaces,
    IRunStore runs,
    ErrorCatalog catalog) : IProjectAnalyzer
{
    /// <summary>Most files <c>list_classes</c> reads (P10 §4.13).</summary>
    public const int MaxFiles = 20_000;

    private readonly AnalysisCache<ModuleResult> _cache = new();

    public async Task<ProjectWorkspace> PrepareAsync(ProjectTarget target, RunContext run, Func<long, Task<bool>>? confirmLargeCopy, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        Report(run, ArrowEventTypes.B1Checkout, null, target.Kind == ProjectSourceKind.GitRepository ? $"Tải {target.Location}" : $"Chép {target.Location}");
        var checkout = await source.CheckoutAsync(target, Path.Combine(run.WorkDir, "base"), confirmLargeCopy, ct).ConfigureAwait(false);
        var checkoutSeconds = watch.Elapsed.TotalSeconds;

        watch.Restart();
        Report(run, ArrowEventTypes.B1Locate, null, "Liệt kê class");
        var layout = BuildLayout.Scan(checkout.Root);
        var roots = SourceRoots(checkout.Root, layout, "src/main/java");
        var testRoots = SourceRoots(checkout.Root, layout, "src/test/java");

        var warnings = new List<string>(checkout.Warnings);
        var classes = await ListClassesAsync(checkout.Root, roots, warnings, ct).ConfigureAwait(false);
        return new ProjectWorkspace(checkout, layout, classes, testRoots, warnings, checkoutSeconds, watch.Elapsed.TotalSeconds);
    }

    public async Task<LocateResult> LocateAsync(ProjectWorkspace workspace, string query, CancellationToken ct)
    {
        var normalized = NormalizeQuery(workspace, query);
        var result = ClassLocator.Locate(workspace.Classes, normalized, workspace.TestRoots);
        if (result is not LocateResult.Failed { Code: var code } || code != ErrorCodes.B1CutMissing || !normalized.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        // A file in a declared, non-standard source root: list that root too (P01 §3.3 step 1).
        var file = Path.Combine(workspace.Checkout.Root, normalized);
        if (!File.Exists(file))
        {
            return result;
        }

        var package = ProjectPaths.PackageOf(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
        var root = ProjectPaths.SourceRoot(normalized, (package.Length == 0 ? "" : package + ".") + Path.GetFileNameWithoutExtension(normalized));
        var extra = await ListClassesAsync(workspace.Checkout.Root, [root], [], ct).ConfigureAwait(false);
        return ClassLocator.Locate([.. workspace.Classes, .. extra], normalized, workspace.TestRoots);
    }

    public async Task<ProjectAnalysis> AnalyzeAsync(ProjectWorkspace workspace, ClassCandidate cut, RunContext run, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var target = TargetDir(cut.Fqn);
        var warnings = new List<string>(workspace.Warnings);
        var sourceRoot = ProjectPaths.SourceRoot(cut.Path, cut.Fqn);
        var moduleRoot = BuildRootResolver.ModuleRoot(sourceRoot, workspace.Layout);
        if (moduleRoot is null)
        {
            var info = catalog.Describe(ErrorCodes.B1BuildRootNotFound, $"Không tìm thấy file build cho {cut.Path}.");
            return Finish(run, workspace, cut, new BlockOutcome(BlockOutcomeKind.Blocked, info.Code, info.Message, info.Hint, null, null, 0), null, warnings, watch, null);
        }

        var key = AnalysisCache<ModuleResult>.Key(workspace.Checkout.SourceTreeHash ?? workspace.Checkout.GitCommit ?? workspace.Checkout.Root, moduleRoot);
        var logsDir = Path.Combine(run.Run.Root, target, "block1", "logs");
        var (module, created) = await _cache.GetOrCreateAsync(
            key,
            token => modules.AnalyzeAsync(workspace, moduleRoot, sourceRoot, cut.Fqn, run, logsDir, token),
            ct).ConfigureAwait(false);
        warnings.AddRange(module.Warnings);
        var sharedFrom = created ? null : module.FirstCut;

        if (module.Failure is { } failure)
        {
            // The log of a shared failure lives under the first CUT of the module.
            var log = failure.Log is null || created ? failure.Log : $"../../{TargetDir(module.FirstCut)}/{failure.Log}";
            return Finish(run, workspace, cut, failure with { Log = log }, module, warnings, watch, sharedFrom);
        }

        return Finish(run, workspace, cut, null, module, warnings, watch, sharedFrom);
    }

    public async Task CleanupAsync(ProjectWorkspace workspace, RunContext run, bool keep, CancellationToken ct)
    {
        if (keep)
        {
            return;
        }

        await source.CleanupAsync(workspace.Checkout, ct).ConfigureAwait(false);
        await workspaces.DeleteAsync(run.WorkDir, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The existing <c>&lt;module&gt;/<paramref name="standard"/></c> folders of the project, the root first. Module folders are the
    /// folders with a build file plus the folders that settings files include and Maven <c>&lt;modules&gt;</c> name: a Gradle subproject
    /// configured from the root build file has no build file of its own (GĐ7, otroslogviewer).
    /// </summary>
    public static IReadOnlyList<string> SourceRoots(string projectRoot, BuildLayout layout, string standard) =>
        layout.Dirs.Select(d => d.Path)
            .Concat(layout.Dirs.SelectMany(d => d.GradleIncludes))
            .Concat(layout.Dirs.SelectMany(d => d.MavenModules))
            .Select(d => BuildLayout.Join(d, standard))
            .Prepend(standard)
            .Distinct()
            .Where(r => Directory.Exists(Path.Combine(projectRoot, r)))
            .ToList();

    /// <summary>
    /// Folder of a CUT below the run, <c>targets/&lt;cut_fqn&gt;</c> (P09 §5.2). A FQN never has characters a folder name cannot have; a
    /// class the user typed that was not found may (GĐ7: a pasted <c>\r</c> made the whole run fail), so those become <c>_</c>.
    /// </summary>
    public static string TargetDir(string cutFqn) =>
        "targets/" + string.Concat(cutFqn.Select(c => char.IsControl(c) || InvalidNameChars.Contains(c) ? '_' : c));

    // The Windows set on every OS, so that a run folder can be copied between machines.
    private static readonly HashSet<char> InvalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private ProjectAnalysis Finish(RunContext run, ProjectWorkspace workspace, ClassCandidate cut, BlockOutcome? failure, ModuleResult? module, List<string> warnings, Stopwatch watch, string? sharedFrom)
    {
        var dir = TargetDir(cut.Fqn) + "/block1/";
        RepositoryMetadata? metadata = null;
        CutInfo? cutInfo = null;
        var outcome = failure;
        if (outcome is null && module is not null)
        {
            (metadata, cutInfo, outcome) = Describe(workspace, cut, module, warnings);
        }

        var seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
        outcome = (outcome ?? BlockOutcome.Ok(0)) with { Seconds = seconds };

        // P01 §8: metadata, prompt text, dependency files, (snapshot), workspace, baseline, then outcome.json last.
        if (metadata is not null)
        {
            runs.WriteJson(run.Run, dir + "metadata.json", metadata);
            runs.WriteText(run.Run, dir + "metadata_prompt.txt", MetadataPromptRenderer.Render(metadata) + "\n");
        }

        var ok = outcome.Outcome == BlockOutcomeKind.Ok && module is not null;
        if (ok && sharedFrom is null)
        {
            runs.WriteText(run.Run, dir + "dependencies_resolved.txt", string.Concat(module!.Resolved.Select(d => $"{d.Group}:{d.Artifact}:{d.Version}:{d.Scope}\n")));
            runs.WriteText(run.Run, dir + "test_classpath.txt", string.Concat(module.Classpath.Select(c => c + "\n")));
            runs.WriteJson(run.Run, dir + "workspace.json", module.Workspace);
            runs.WriteJson(run.Run, dir + "baseline.json", module.Baseline);
        }
        else if (sharedFrom is not null)
        {
            runs.WriteText(run.Run, dir + "shared_from.txt", sharedFrom + "\n");
        }

        runs.WriteJson(run.Run, dir + "outcome.json", outcome);
        Report(run, ok ? ArrowEventTypes.B1Done : ArrowEventTypes.B1Blocked, cut.Fqn,
            ok ? Summary(metadata, module!.Baseline, seconds, sharedFrom) : $"{outcome.Code}: {outcome.Message}", ok ? EventLevel.Info : EventLevel.Error);

        var timings = new StepTimings(
            Math.Round(workspace.CheckoutSeconds, 1),
            Math.Round(workspace.LocateSeconds, 1),
            Math.Round(module?.PlanSeconds ?? 0, 1),
            Math.Round(module?.CompileSeconds ?? 0, 1),
            module?.BaselineSeconds.Select(s => Math.Round(s, 1)).ToList() ?? [],
            seconds);
        return new ProjectAnalysis(
            outcome,
            metadata,
            ok ? module!.Baseline : null,
            ok ? module!.Workspace : null,
            cutInfo,
            warnings.Distinct().ToList(),
            timings,
            sharedFrom);
    }

    /// <summary>M, the CUT and its test path (P01 §4.6, §4.7, §5). The outcome is set only when the test name is exhausted.</summary>
    private (RepositoryMetadata?, CutInfo?, BlockOutcome?) Describe(ProjectWorkspace workspace, ClassCandidate cut, ModuleResult module, List<string> warnings)
    {
        var checkout = workspace.Checkout;
        var plan = module.Plan!;
        var (package, simple) = ProjectPaths.SplitFqn(cut.Fqn);
        var sourceRoot = ProjectPaths.SourceRoot(cut.Path, cut.Fqn);

        // A name is taken when the file exists in the user's project, or when a test class of the reactor already has that simple name.
        var userRoot = checkout.ProjectRoot ?? checkout.Root;
        var reactorNames = TestClassNames(checkout.Root, workspace.TestRoots.Append(module.TestRoot));
        var planned = TestPathPlanner.Plan(module.TestRoot, cut.Fqn, (path, name) =>
            File.Exists(Path.Combine(userRoot, path)) || File.Exists(Path.Combine(checkout.Root, path)) || reactorNames.Contains(name));
        if (planned is null)
        {
            var info = catalog.Describe(ErrorCodes.B1TestNameExhausted);
            return (null, null, new BlockOutcome(BlockOutcomeKind.Blocked, info.Code, info.Message, info.Hint, null, null, 0));
        }

        if (planned.NameIndex > 1)
        {
            warnings.Add($"Đã có {TestPathPlanner.TestName(simple, 1)}; test mới sẽ tên {Path.GetFileNameWithoutExtension(planned.Path)}.");
        }

        var inTestRoot = BuildRootResolver.RelativeTo(module.TestRoot, planned.Path) ?? planned.Path;
        var matches = TestNamePatterns.Matches(inTestRoot, module.TestIncludes, module.TestExcludes);
        if (!matches)
        {
            warnings.Add($"mvn test của project sẽ không tự chạy file này (mẫu tên test: {string.Join(", ", module.TestIncludes)}).");
        }

        var cutFile = Path.Combine(checkout.Root, cut.Path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(cutFile)));
        var packages = ProjectPaths.RankPackages(
            workspace.Classes.Where(c => c.Path.StartsWith(sourceRoot + "/", StringComparison.Ordinal) || sourceRoot == ".").Select(c => c.Package).Where(p => p.Length > 0),
            package);

        var direct = module.Direct.Where(d => d.Scope is "compile" or "runtime" or "provided" or "system").ToList();
        var tests = module.Direct.Where(d => d.Scope == "test").ToList();
        var missing = new Dictionary<string, string>(module.MissingReasons);
        var metadata = new RepositoryMetadata(
            new SourceMetadata(checkout.Kind, checkout.PathName, checkout.Url, checkout.GitCommit, checkout.Dirty),
            new BuildToolMetadata(
                plan.Tool,
                module.ToolVersion,
                module.Wrapper,
                module.WrapperFallback,
                plan.BuildRoot,
                plan.Mode,
                BuildFile(workspace, plan),
                plan.Module,
                plan.Source,
                module.TestIncludes,
                module.TestExcludes),
            new JavaMetadata(module.Declared, module.DeclaredSource, module.DeclaredMajor, module.JdkMajor, module.JdkReason),
            new DependencyMetadata(direct, tests, module.Resolved.Count),
            module.Framework,
            new ProjectStructureMetadata(module.Modules.Count > 0, module.Modules, module.ModuleRoot, module.ModuleRoot, packages),
            new PathsMetadata(sourceRoot, module.TestRoot, cut.Path, planned.Path, planned.Fqn, planned.NameIndex, matches),
            new CutMetadata(cut.Fqn, simple, package, cut.Kind, sha),
            missing);
        var cutInfo = new CutInfo(cut.Fqn, simple, package, cut.Kind, cut.Path, sha, planned.Path, planned.Fqn, planned.NameIndex);
        return (metadata, cutInfo, null);
    }

    private static string BuildFile(ProjectWorkspace workspace, BuildPlan plan)
    {
        if (plan.Tool == BuildToolKind.Maven)
        {
            return BuildLayout.Join(plan.BuildRoot, "pom.xml");
        }

        var dir = Path.Combine(workspace.Checkout.Root, plan.BuildRoot);
        var file = new[] { "build.gradle.kts", "build.gradle", "settings.gradle.kts", "settings.gradle" }.FirstOrDefault(f => File.Exists(Path.Combine(dir, f))) ?? "build.gradle";
        return BuildLayout.Join(plan.BuildRoot, file);
    }

    /// <summary>Simple names of the test classes under the test roots (file names without <c>.java</c>).</summary>
    private static HashSet<string> TestClassNames(string root, IEnumerable<string> testRoots)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var testRoot in testRoots.Distinct())
        {
            var dir = Path.Combine(root, testRoot);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.java", SearchOption.AllDirectories))
                {
                    names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }

        return names;
    }

    private async Task<List<ClassCandidate>> ListClassesAsync(string root, IReadOnlyList<string> roots, List<string> warnings, CancellationToken ct)
    {
        if (roots.Count == 0)
        {
            return [];
        }

        using var lease = await javaHelper(ct).ConfigureAwait(false);
        var result = await lease.Helper.ListClassesAsync(roots.Select(r => Path.Combine(root, r)).ToList(), MaxFiles, ct).ConfigureAwait(false);
        if (result.Errors.Count > 0)
        {
            warnings.Add($"{result.Errors.Count} file .java không đọc được (ví dụ {Relative(root, result.Errors[0].Path)}: {result.Errors[0].Error}).");
        }

        if (result.Truncated)
        {
            warnings.Add($"Project có hơn {MaxFiles} file .java; chỉ {MaxFiles} file đầu được liệt kê.");
        }

        return result.Classes
            .Select(c => new ClassCandidate(c.Fqn, c.SimpleName, c.Kind, c.IsAbstract, Relative(root, c.Path)))
            .ToList();
    }

    /// <summary>A path query relative to the project root with <c>/</c>; an absolute path inside the project is made relative.</summary>
    private static string NormalizeQuery(ProjectWorkspace workspace, string query)
    {
        var trimmed = query.Trim().Trim('"');
        if (!Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        foreach (var root in new[] { workspace.Checkout.ProjectRoot, workspace.Checkout.Root })
        {
            if (root is not null && BuildRootResolver.RelativeTo(root, Path.GetFullPath(trimmed)) is { } relative)
            {
                return relative;
            }
        }

        return trimmed;
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>The block 1 line of P08 §3.2: <c>Maven 3.9.6 · build root . · JDK 17 · JUnit 5 + Mockito · baseline 42 test ×2 · 38 s</c>.</summary>
    public static string Summary(RepositoryMetadata? m, BaselineInfo? baseline, double seconds, string? sharedFrom)
    {
        if (m is null)
        {
            return $"{seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s";
        }

        var tool = (m.BuildTool.Kind == BuildToolKind.Gradle ? "Gradle" : "Maven") + (m.BuildTool.Version.Length > 0 ? " " + m.BuildTool.Version : "");
        var framework = m.TestFramework?.Name switch { "junit5" => "JUnit 5", "junit4" => "JUnit 4", "testng" => "TestNG", _ => "không rõ test framework" }
                        + (m.TestFramework?.Mocking.Any(x => x.Contains("mockito", StringComparison.OrdinalIgnoreCase)) == true ? " + Mockito" : "");
        var tests = baseline is null ? "" : $" · baseline {baseline.PassingInAllRuns.Count + baseline.Flaky.Count + baseline.FailingInAllRuns.Count} test ×{baseline.Runs}";
        var shared = sharedFrom is null ? "" : $" · dùng chung với {sharedFrom[(sharedFrom.LastIndexOf('.') + 1)..]}";
        return $"{tool} · build root {m.BuildTool.BuildRoot} · JDK {m.Java?.JdkMajor.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} · {framework}{tests}{shared}"
               + $" · {seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s";
    }

    private static void Report(RunContext run, string type, string? cut, string message, EventLevel level = EventLevel.Info) =>
        run.Progress.Report(new ArrowEvent(DateTimeOffset.UtcNow, type, cut, null, null, level, message, new Dictionary<string, object?>()));
}
