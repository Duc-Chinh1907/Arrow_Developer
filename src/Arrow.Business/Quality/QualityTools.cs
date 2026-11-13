using System.Diagnostics;
using System.Text;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Quality;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;
using Arrow.DataAccess.Tools;

namespace Arrow.Business.Quality;

/// <summary>The VALID suite to measure (P05 §2).</summary>
/// <param name="TestFqn">FQN of the test class.</param>
/// <param name="TestPath">Path of the test from the project root.</param>
/// <param name="Code">The code of <c>final.java</c>.</param>
public sealed record SuiteToMeasure(string TestFqn, string TestPath, string Code);

/// <summary>Settings of block 4 (P05 §2, P09).</summary>
/// <param name="Tools"><c>quality.tools</c>: <c>jacoco</c>, <c>pit</c>, <c>tsdetect</c>.</param>
/// <param name="JdkHomes">JDK homes by major, for the JDK of PIT.</param>
/// <param name="HelperJava">A <c>java</c> of JDK 17 or newer, for tsDetect.</param>
/// <param name="StepTimeout"><c>verification.step_timeout_seconds</c>: compile and the JaCoCo test run.</param>
/// <param name="PitTimeout"><c>quality.pit_timeout_seconds</c>.</param>
/// <param name="TsDetectTimeout"><c>quality.tsdetect_timeout_seconds</c>.</param>
/// <param name="InfraAttempts">Attempts for an environment failure (P05 §7).</param>
/// <param name="InfraWait">Wait between them.</param>
/// <param name="KeepWorkspaces"><c>run.keep_workspaces</c>.</param>
public sealed record QualityOptions(
    IReadOnlyList<string> Tools,
    IReadOnlyDictionary<int, string> JdkHomes,
    string HelperJava,
    TimeSpan StepTimeout,
    TimeSpan PitTimeout,
    TimeSpan TsDetectTimeout,
    int InfraAttempts,
    TimeSpan InfraWait,
    bool KeepWorkspaces);

/// <summary>A tool failed; <see cref="Retryable"/> failures (timeouts, a tool that did not run) are tried again.</summary>
public sealed class ToolFailure(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;
}

/// <summary>A fresh copy with the suite compiled (P05 §3).</summary>
/// <param name="Copy">Root of the copy.</param>
/// <param name="ModuleDir">Folder of the target module in the copy.</param>
/// <param name="ProductionClasses">Production classes of the module.</param>
/// <param name="TestClasses">Test classes of the module.</param>
/// <param name="SourceRoot">Source root of the CUT in the copy.</param>
/// <param name="Classpath">The test classpath printed by the build (<c>arrow-cp.txt</c> or the Gradle probe).</param>
public sealed record PreparedSuite(string Copy, string ModuleDir, string ProductionClasses, string TestClasses, string SourceRoot, IReadOnlyList<string> Classpath);

/// <summary>
/// P05 §3: a new copy of the compiled snapshot, the suite written into it, then the tests compiled and the classpath printed in one
/// build call. A failure is tried on a new copy, at most 3 times, then <c>B4_SUITE_COMPILE</c>.
/// </summary>
public sealed class SuitePreparer(IBuildExecutor builds, IWorkspaceManager workspaces)
{
    /// <summary>Attempts of the preparation.</summary>
    public const int Attempts = 3;

    /// <summary>The prepared copy; the caller deletes <see cref="PreparedSuite.Copy"/>.</summary>
    public async Task<PreparedSuite> PrepareAsync(SuiteToMeasure suite, TaskContext task, string tool, QualityOptions options, CancellationToken ct)
    {
        var m = task.Metadata;
        var setup = Verifier.SetupOf(task.Workspace, task.Run.HomeRoot);
        var arguments = setup.Tool == BuildToolKind.Gradle
            ? BuildCommands.GradleCompileWithClasspath(setup.GradleProject)
            : ["-DskipTests", "test-compile", $"{BuildCommands.DependencyPlugin(m.BuildTool.Version)}:build-classpath", "-Dmdep.includeScope=test", "-Dmdep.outputFile=" + BuildCommands.MavenClasspathFile];
        string? last = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var copy = Path.Combine(task.Run.WorkDir, $"q-{tool}-{Guid.NewGuid().ToString("N")[..6]}");
            try
            {
                var snapshot = Path.Combine(task.Run.HomeRoot, task.Workspace.CompiledSnapshot.Replace('/', Path.DirectorySeparatorChar));
                await workspaces.CloneAsync(snapshot, copy, ct).ConfigureAwait(false);
                var file = Path.Combine(copy, suite.TestPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, suite.Code, new UTF8Encoding(false), ct).ConfigureAwait(false);

                var log = QualityService.LogPath(task, $"{tool}-prepare{(attempt > 1 ? $"-try{attempt}" : "")}");
                var result = await builds.RunAsync(setup, copy, arguments, task.Workspace.JdkHome, options.StepTimeout, log, ct).ConfigureAwait(false);
                if (result.Failure == BuildFailureKind.None)
                {
                    var moduleDir = Path.Combine(copy, m.ProjectStructure.ModuleRoot.Replace('/', Path.DirectorySeparatorChar));
                    var classpath = setup.Tool == BuildToolKind.Gradle
                        ? GradleProbe.Parse(result.Process.Output).Classpath
                        : ReadMavenClasspath(Path.Combine(moduleDir, BuildCommands.MavenClasspathFile));
                    var gradle = setup.Tool == BuildToolKind.Gradle;
                    return new PreparedSuite(
                        copy,
                        moduleDir,
                        Path.Combine(moduleDir, gradle ? Path.Combine("build", "classes", "java", "main") : Path.Combine("target", "classes")),
                        Path.Combine(moduleDir, gradle ? Path.Combine("build", "classes", "java", "test") : Path.Combine("target", "test-classes")),
                        Path.Combine(copy, m.Paths.SourceRoot.Replace('/', Path.DirectorySeparatorChar)),
                        classpath);
                }

                last = BuildFailureClassifier.FirstError(result.Process.Output) ?? result.Process.Tail(5);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex.Message;
            }

            await QualityService.DeleteQuietlyAsync(workspaces, copy).ConfigureAwait(false);
        }

        throw new ToolFailure(ErrorCodes.B4SuiteCompile, $"Không biên dịch được bộ test để đo ({Attempts} lần): {last}");
    }

    private static IReadOnlyList<string> ReadMavenClasspath(string file) =>
        File.Exists(file) ? File.ReadAllText(file).Trim().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) : [];
}

/// <summary>A tool of block 4 (P05 §9).</summary>
public interface IQualityTool
{
    /// <summary><c>jacoco</c>, <c>pit</c> or <c>tsdetect</c>.</summary>
    string Name { get; }

    /// <summary>Measures the suite; raises <see cref="ToolFailure"/>, or <c>ArrowException</c> for a missing tool.</summary>
    Task<object> MeasureAsync(SuiteToMeasure suite, TaskContext task, QualityOptions options, CancellationToken ct);
}

/// <summary>Runs <c>java</c> of a JDK with the fixed environment of P01 §6 and a log.</summary>
public sealed class JavaRunner(IProcessRunner runner)
{
    /// <summary><c>bin/java</c> of a JDK home.</summary>
    public static string JavaOf(string jdkHome) => Path.Combine(jdkHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");

    /// <summary>Runs <paramref name="java"/> with <paramref name="arguments"/>.</summary>
    public Task<ProcessResult> RunAsync(string java, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, string logFile, CancellationToken ct)
    {
        var home = Path.GetDirectoryName(Path.GetDirectoryName(java))!;
        var environment = new Dictionary<string, string?>
        {
            ["JAVA_HOME"] = home, ["JAVA_TOOL_OPTIONS"] = BuildCommands.JavaToolOptions, ["JDK_JAVA_OPTIONS"] = null, ["_JAVA_OPTIONS"] = null,
        };
        return runner.RunAsync(new ProcessSpec(java, arguments, workingDirectory, environment, timeout, LogFile: logFile), ct);
    }
}

/// <summary>JaCoCo (P05 §4): the test run of step 2 with the agent in <c>JAVA_TOOL_OPTIONS</c>, then <c>jacococli report --csv</c>.</summary>
public sealed class JacocoTool(SuitePreparer preparer, IBuildExecutor builds, IToolStore tools, JavaRunner java, IWorkspaceManager workspaces, IRunStore runs) : IQualityTool
{
    public string Name => "jacoco";

    public async Task<object> MeasureAsync(SuiteToMeasure suite, TaskContext task, QualityOptions options, CancellationToken ct)
    {
        var agent = await tools.EnsureAsync("jacoco-agent", null, ct).ConfigureAwait(false);
        var cli = await tools.EnsureAsync("jacoco-cli", null, ct).ConfigureAwait(false);
        var prepared = await preparer.PrepareAsync(suite, task, Name, options, ct).ConfigureAwait(false);
        try
        {
            var fqn = task.Metadata.Cut.Fqn;
            var exec = Path.Combine(prepared.Copy, "arrow-jacoco.exec");
            var setup = Verifier.SetupOf(task.Workspace, task.Run.HomeRoot);
            var run = await builds.RunAsync(setup, prepared.Copy, Verifier.RunTargetArguments(setup, suite.TestFqn), task.Workspace.JdkHome, options.StepTimeout,
                QualityService.LogPath(task, "jacoco-test"), ct, JacocoSetup.AgentOption(agent, exec, fqn)).ConfigureAwait(false);
            if (run.Failure is BuildFailureKind.Timeout or BuildFailureKind.Network)
            {
                throw new ToolFailure(ErrorCodes.B4JacocoFailed, run.Failure == BuildFailureKind.Timeout ? $"chạy test quá {options.StepTimeout.TotalSeconds:0} s" : "lỗi mạng khi chạy test", retryable: true);
            }

            if (!File.Exists(exec))
            {
                throw new ToolFailure(ErrorCodes.B4JacocoFailed, "không có file .exec: test không chạy dưới agent của JaCoCo");
            }

            var csv = Path.Combine(prepared.Copy, "arrow-jacoco.csv");
            var report = await java.RunAsync(JavaRunner.JavaOf(task.Workspace.JdkHome),
                ["-jar", cli, "report", exec, "--classfiles", prepared.ProductionClasses, "--sourcefiles", prepared.SourceRoot, "--csv", csv],
                prepared.Copy, TimeSpan.FromMinutes(5), QualityService.LogPath(task, "jacoco-report"), ct).ConfigureAwait(false);
            if (!report.Succeeded || !File.Exists(csv))
            {
                throw new ToolFailure(ErrorCodes.B4JacocoFailed, $"jacococli report lỗi: {report.Tail(5)}", retryable: report.TimedOut);
            }

            var text = await File.ReadAllTextAsync(csv, ct).ConfigureAwait(false);
            runs.WriteText(task.Run.Run, $"{task.TargetDir}/block4/jacoco.csv", text);
            return JacocoCsvParser.Parse(text, fqn) ?? throw new ToolFailure(ErrorCodes.B4JacocoFailed, $"báo cáo JaCoCo không có {fqn}");
        }
        finally
        {
            await QualityService.DeleteQuietlyAsync(workspaces, prepared.Copy, options.KeepWorkspaces).ConfigureAwait(false);
        }
    }
}

/// <summary>PIT (P05 §5).</summary>
public sealed class PitTool(SuitePreparer preparer, IToolStore tools, JavaRunner java, IWorkspaceManager workspaces, IRunStore runs) : IQualityTool
{
    /// <summary>JUnit Platform used when the module's version cannot be read.</summary>
    public const string DefaultPlatformVersion = "1.10.2";

    public string Name => "pit";

    public async Task<object> MeasureAsync(SuiteToMeasure suite, TaskContext task, QualityOptions options, CancellationToken ct)
    {
        var jdk = PitSetup.ChooseJdk(task.Workspace.JdkMajor, task.Workspace.JdkHome, options.JdkHomes)
                  ?? throw new ToolFailure(ErrorCodes.B4PitFailed, $"PIT cần JDK {PitSetup.MinimumJdk}+; hãy cài JDK 11 hoặc 17.");
        var launch = new List<string>();
        foreach (var id in PitSetup.CoreTools)
        {
            launch.Add(await tools.EnsureAsync(id, null, ct).ConfigureAwait(false));
        }

        var framework = task.Metadata.TestFramework?.Name;
        if (PitSetup.PluginTool(framework) is { } plugin)
        {
            launch.Add(await tools.EnsureAsync(plugin, null, ct).ConfigureAwait(false));
        }

        var prepared = await preparer.PrepareAsync(suite, task, Name, options, ct).ConfigureAwait(false);
        try
        {
            var extra = new List<string>();
            if (framework == "junit5" && !PitSetup.HasLauncher(prepared.Classpath))
            {
                var version = PitSetup.PlatformVersion(prepared.Classpath) ?? DefaultPlatformVersion;
                extra.Add(await tools.EnsureMavenArtifactAsync("org.junit.platform", "junit-platform-launcher", version, "junit-platform", null, ct).ConfigureAwait(false));
            }

            var classpath = PitSetup.Classpath(prepared.ProductionClasses, prepared.TestClasses, prepared.Classpath, extra);
            var classPathFile = Path.Combine(prepared.Copy, "arrow-pit-classpath.txt");
            await File.WriteAllLinesAsync(classPathFile, classpath, ct).ConfigureAwait(false);
            var reportDir = Path.Combine(prepared.Copy, "arrow-pit-report");
            var fqn = task.Metadata.Cut.Fqn;
            var pit = await java.RunAsync(JavaRunner.JavaOf(jdk),
                ["-cp", string.Join(Path.PathSeparator, PitSetup.LaunchClasspath(launch, classpath)), .. PitSetup.Arguments(fqn, suite.TestFqn, reportDir, classPathFile, prepared.SourceRoot)],
                prepared.ModuleDir, options.PitTimeout, QualityService.LogPath(task, "pit"), ct).ConfigureAwait(false);
            var xmlFile = Path.Combine(reportDir, "mutations.xml");
            if (!pit.Succeeded || !File.Exists(xmlFile))
            {
                throw new ToolFailure(ErrorCodes.B4PitFailed,
                    pit.TimedOut ? $"PIT quá {options.PitTimeout.TotalMinutes:0} phút" : $"PIT lỗi (mã thoát {pit.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}):\n{pit.Tail(20)}",
                    retryable: pit.TimedOut || pit.ExitCode is null);
            }

            var xml = await File.ReadAllTextAsync(xmlFile, ct).ConfigureAwait(false);
            runs.WriteText(task.Run.Run, $"{task.TargetDir}/block4/mutations.xml", xml);
            var (mutation, outOfScope) = PitXmlParser.Parse(xml, fqn);
            if (outOfScope.Count > 0)
            {
                throw new ToolFailure(ErrorCodes.B4PitScope, "PIT tạo mutant cho class ngoài CUT: " + string.Join(", ", outOfScope.Take(5)));
            }

            return mutation;
        }
        finally
        {
            await QualityService.DeleteQuietlyAsync(workspaces, prepared.Copy, options.KeepWorkspaces).ConfigureAwait(false);
        }
    }
}

/// <summary>tsDetect (P05 §6): reads sources only, so a temporary folder with the test and the CUT is enough.</summary>
public sealed class TsDetectTool(IToolStore tools, IJavaHelperProvider javaHelpers, JavaRunner java, IWorkspaceManager workspaces, IRunStore runs) : IQualityTool
{
    public string Name => "tsdetect";

    public async Task<object> MeasureAsync(SuiteToMeasure suite, TaskContext task, QualityOptions options, CancellationToken ct)
    {
        var jar = await tools.EnsureAsync("tsdetect", null, ct).ConfigureAwait(false);
        var dir = Path.Combine(task.Run.WorkDir, "ts-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var testFile = Path.Combine(dir, "test", Path.GetFileName(suite.TestPath));
            var cutFile = Path.Combine(dir, "main", Path.GetFileName(task.Metadata.Paths.CutPath));
            Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cutFile)!);
            await File.WriteAllTextAsync(testFile, suite.Code, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(cutFile, task.CutSource, ct).ConfigureAwait(false);
            var input = Path.Combine(dir, "input.csv");
            await File.WriteAllTextAsync(input, $"arrow,{testFile},{cutFile}\n", ct).ConfigureAwait(false);

            var run = await java.RunAsync(options.HelperJava, ["-jar", jar, input], dir, options.TsDetectTimeout, QualityService.LogPath(task, "tsdetect"), ct).ConfigureAwait(false);
            var output = Directory.GetFiles(dir, "*.csv").Where(f => Path.GetFileName(f) != "input.csv").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (!run.Succeeded || output is null)
            {
                throw new ToolFailure(ErrorCodes.B4TsDetectFailed, run.TimedOut ? $"tsDetect quá {options.TsDetectTimeout.TotalSeconds:0} s" : $"tsDetect lỗi: {run.Tail(5)}", retryable: run.TimedOut);
            }

            var csv = await File.ReadAllTextAsync(output, ct).ConfigureAwait(false);
            runs.WriteText(task.Run.Run, $"{task.TargetDir}/block4/tsdetect.csv", csv);
            int methods;
            using (var lease = await javaHelpers.RentAsync(ct).ConfigureAwait(false))
            {
                methods = (await lease.Helper.ListTestMethodsAsync(suite.Code, ct).ConfigureAwait(false)).Count;
            }

            return TsDetectCsvParser.Parse(csv, methods) ?? throw new ToolFailure(ErrorCodes.B4TsDetectFailed, "CSV của tsDetect không có dòng kết quả");
        }
        finally
        {
            await QualityService.DeleteQuietlyAsync(workspaces, dir, options.KeepWorkspaces).ConfigureAwait(false);
        }
    }
}

/// <summary>Measures a suite with every tool of <c>quality.tools</c> (P05 §9).</summary>
public interface IQualityService
{
    /// <summary>Runs the tools one after another and writes <c>block4/</c>; never raises for a tool failure.</summary>
    Task<QualityResult> MeasureAsync(SuiteToMeasure suite, TaskContext task, QualityOptions options, CancellationToken ct);
}

/// <summary>
/// <see cref="IQualityService"/> (P05 §7): JaCoCo, then PIT, then tsDetect. One tool failing does not stop the others; an
/// environment failure is tried again (at most <c>infra_attempts</c>, <c>infra_wait</c> apart); a tool that cannot be downloaded
/// is SKIPPED with <c>CFG_TOOL_MISSING</c>. Writes <c>block4/&lt;tool&gt;.json</c> and <c>block4/outcome.json</c>.
/// </summary>
public sealed class QualityService(IReadOnlyList<IQualityTool> tools, IRunStore runs, ErrorCatalog catalog, TimeProvider? time = null) : IQualityService
{
    /// <summary>Order of P05 §7.</summary>
    public static readonly IReadOnlyList<string> Order = ["jacoco", "pit", "tsdetect"];

    private static readonly Dictionary<string, string> Versions = new(StringComparer.Ordinal)
    {
        ["jacoco"] = "jacoco 0.8.12", ["pit"] = "pit 1.17.4", ["tsdetect"] = "tsdetect 2.2",
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<QualityResult> MeasureAsync(SuiteToMeasure suite, TaskContext task, QualityOptions options, CancellationToken ct)
    {
        CoverageResult? coverage = null;
        MutationResult? mutation = null;
        SmellResult? smells = null;
        var outcomes = new Dictionary<string, ToolOutcome>(StringComparer.Ordinal);
        foreach (var name in Order.Where(options.Tools.Contains))
        {
            var tool = tools.FirstOrDefault(t => t.Name == name);
            if (tool is null)
            {
                continue;
            }

            Report(task, ArrowEventTypes.B4ToolStart, EventLevel.Info, $"Đo {name}", ("tool", name));
            var watch = Stopwatch.StartNew();
            ToolOutcome outcome;
            object? measured = null;
            var attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    measured = await tool.MeasureAsync(suite, task, options, ct).ConfigureAwait(false);
                    outcome = new ToolOutcome(ToolStatus.Ok, null, null, attempt);
                    break;
                }
                catch (ToolFailure failure) when (failure.Retryable && attempt < Math.Max(1, options.InfraAttempts))
                {
                    Report(task, ArrowEventTypes.B4ToolFailed, EventLevel.Warning, $"{name}: {failure.Message}; thử lại lần {attempt + 1}", ("tool", name), ("code", failure.Code));
                    if (options.InfraWait > TimeSpan.Zero)
                    {
                        await Task.Delay(options.InfraWait, _time, ct).ConfigureAwait(false);
                    }
                }
                catch (ToolFailure failure)
                {
                    outcome = new ToolOutcome(ToolStatus.Failed, failure.Code, failure.Message, attempt);
                    break;
                }
                catch (ArrowException ex) when (ex.Code is ErrorCodes.CfgToolMissing or ErrorCodes.CfgToolChecksum)
                {
                    outcome = new ToolOutcome(ToolStatus.Skipped, ex.Code, catalog.Describe(ex).Message, attempt);
                    break;
                }
                catch (Exception ex) when (ex is ArrowException or JavaHelperCommandException or IOException or UnauthorizedAccessException)
                {
                    outcome = new ToolOutcome(ToolStatus.Failed, ex is ArrowException a ? a.Code : ErrorCodes.CliInternal, ex.Message, attempt);
                    break;
                }
            }

            var seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
            outcome = outcome with { Seconds = seconds };
            outcomes[name] = outcome;
            switch (measured)
            {
                case CoverageResult c:
                    coverage = c with { Tool = Versions[name], Seconds = seconds };
                    runs.WriteJson(task.Run.Run, $"{task.TargetDir}/block4/jacoco.json", coverage);
                    break;
                case MutationResult mr:
                    mutation = mr with { Tool = Versions[name], Seconds = seconds };
                    runs.WriteJson(task.Run.Run, $"{task.TargetDir}/block4/pit.json", mutation);
                    break;
                case SmellResult s:
                    smells = s with { Tool = Versions[name], Seconds = seconds };
                    runs.WriteJson(task.Run.Run, $"{task.TargetDir}/block4/tsdetect.json", smells);
                    break;
            }

            if (outcome.Status == ToolStatus.Ok)
            {
                Report(task, ArrowEventTypes.B4ToolDone, EventLevel.Info, $"{name}: {Summary(coverage, mutation, smells, name)} · {seconds:0.0} s", ("tool", name), ("seconds", seconds));
            }
            else
            {
                Report(task, ArrowEventTypes.B4ToolFailed, EventLevel.Warning, $"{name}: {outcome.Code} {FirstLine(outcome.Message)}", ("tool", name), ("code", outcome.Code));
            }
        }

        runs.WriteJson(task.Run.Run, $"{task.TargetDir}/block4/outcome.json", outcomes);
        var result = new QualityResult(coverage, mutation, smells, outcomes);
        Report(task, ArrowEventTypes.B4Done, EventLevel.Info, "Khối 4 xong", ("tools", outcomes.Count));
        return result;
    }

    private static string Summary(CoverageResult? c, MutationResult? m, SmellResult? s, string tool) => tool switch
    {
        "jacoco" => $"line {Percent(c?.Line)} · branch {Percent(c?.Branch)} · method {Percent(c?.Method)}",
        "pit" => $"mutation {Percent(m?.Score)} ({m?.Killed}/{m?.Total} bị diệt)",
        _ => $"smell {s?.TotalSmells} · mật độ {(s?.Density is { } d ? d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "-")}",
    };

    /// <summary><c>78%</c>, or <c>-</c> for null.</summary>
    public static string Percent(double? ratio) => ratio is { } r ? (r * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%" : "-";

    private static string FirstLine(string? text) => (text ?? "").Split('\n')[0];

    /// <summary>The log file of a step of block 4: <c>targets/&lt;cut&gt;/block4/logs/&lt;name&gt;.log</c>, absolute.</summary>
    public static string LogPath(TaskContext task, string name)
    {
        var path = Path.Combine(task.Run.Run.Root, task.TargetDir.Replace('/', Path.DirectorySeparatorChar), "block4", "logs", name + ".log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Deletes a copy unless <paramref name="keep"/>; a copy that cannot be deleted goes with <c>work/&lt;run_id&gt;</c>.</summary>
    public static async Task DeleteQuietlyAsync(IWorkspaceManager workspaces, string path, bool keep = false)
    {
        if (keep)
        {
            return;
        }

        try
        {
            await workspaces.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed with work/<run_id> at the end of the run.
        }
    }

    private static void Report(TaskContext task, string type, EventLevel level, string message, params (string Key, object? Value)[] data) =>
        task.Run.Progress.Report(new ArrowEvent(DateTimeOffset.UtcNow, type, task.CutFqn, null, null, level, message, data.ToDictionary(d => d.Key, d => d.Value)));
}
