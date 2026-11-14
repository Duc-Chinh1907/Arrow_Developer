using System.Globalization;
using Arrow.Business.Config;
using Arrow.Business.Environment;
using Arrow.Common.Analysis;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;
using Arrow.DataAccess.Tools;

namespace Arrow.Business.UseCases;

/// <summary>Level of a line of <c>arrow doctor</c>: <c>✓</c>, <c>!</c> (works, but look at it) or <c>✗</c> (must be fixed).</summary>
public enum DoctorLevel
{
    Ok,
    Warning,
    Error,
}

/// <summary>One checked item (P08 §2.6).</summary>
/// <param name="Level">Result.</param>
/// <param name="Name">What was checked, e.g. <c>git</c>.</param>
/// <param name="Detail">What was found, or what is wrong.</param>
/// <param name="Hint">How to fix it; only for errors and some warnings.</param>
public sealed record DoctorItem(DoctorLevel Level, string Name, string Detail, string? Hint);

/// <summary>All items and the verdict.</summary>
public sealed record DoctorReport(IReadOnlyList<DoctorItem> Items)
{
    public int Warnings => Items.Count(i => i.Level == DoctorLevel.Warning);

    public int Errors => Items.Count(i => i.Level == DoctorLevel.Error);

    /// <summary>No error (warnings are fine).</summary>
    public bool Ready => Errors == 0;
}

/// <summary><c>arrow doctor</c> (P08 §2.6).</summary>
public interface IDoctorUseCase
{
    /// <summary>Checks the environment; <paramref name="onItem"/> gets each item as soon as it is checked, then the report is returned.</summary>
    Task<DoctorReport> RunAsync(string? projectRoot, Action<DoctorItem>? onItem, CancellationToken ct);
}

/// <summary>Asks java-helper for its version, for the <c>java-helper</c> line of <c>arrow doctor</c>.</summary>
public interface IJavaHelperProbe
{
    /// <summary>The version java-helper answers to <c>ping</c>; raises <c>JH_START_FAILED</c> or <c>JH_VERSION_MISMATCH</c>.</summary>
    Task<string> PingAsync(string javaExecutable, CancellationToken ct);
}

/// <summary><see cref="IJavaHelperProbe"/> with the embedded jar.</summary>
public sealed class JavaHelperProbe(ArrowHome home) : IJavaHelperProbe
{
    public async Task<string> PingAsync(string javaExecutable, CancellationToken ct)
    {
        await using var client = new JavaHelperClient(new JavaHelperOptions(javaExecutable, JavaHelperJar.Ensure(home)));
        return await client.PingAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// <see cref="IDoctorUseCase"/>: <c>ARROW_HOME</c>, configuration, git, JDKs, Maven and Gradle, java-helper, the default model (GĐ3) and,
/// the JaCoCo/PIT/tsDetect files of <c>ARROW_HOME/tools</c> and, on Windows, long paths.
/// </summary>
public sealed class DoctorUseCase(
    IConfigStore config,
    ArrowHome home,
    ToolDiscovery tools,
    IProcessRunner runner,
    IJavaHelperProbe javaHelper,
    Func<string, long?>? freeBytes = null,
    Func<bool?>? longPaths = null,
    IModelDiagnostics? models = null,
    IToolStore? measuringTools = null) : IDoctorUseCase
{
    private const int NameWidth = 22;

    public async Task<DoctorReport> RunAsync(string? projectRoot, Action<DoctorItem>? onItem, CancellationToken ct)
    {
        var items = new List<DoctorItem>();
        void Add(DoctorLevel level, string name, string detail, string? hint = null)
        {
            var item = new DoctorItem(level, name, detail, hint);
            items.Add(item);
            onItem?.Invoke(item);
        }

        // 1. ARROW_HOME: valid path, free space.
        var catalog = ErrorCatalog.For(null);
        CheckHome(Add, catalog);

        // 2. Configuration. A broken file does not stop the rest: the tools are then searched with the defaults.
        var java = new JavaSettings(new Dictionary<string, string>(), null, null, null);
        try
        {
            var effective = config.Load(projectRoot, new Dictionary<string, string?>());
            catalog = ErrorCatalog.For(effective.Config.Ui.Language);
            java = effective.Config.Java;
            var issues = ConfigValidator.Validate(effective.Config);
            if (issues.Count == 0)
            {
                Add(DoctorLevel.Ok, "Cấu hình", home.ConfigFile);
            }

            foreach (var issue in issues)
            {
                Add(DoctorLevel.Error, "Cấu hình", $"{issue.Key} = {issue.ValueJson}: cần {issue.Rule}", $"Sửa bằng \"arrow config set {issue.Key} <giá trị>\".");
            }
        }
        catch (ArrowException ex)
        {
            var info = catalog.Describe(ex);
            Add(DoctorLevel.Error, "Cấu hình", info.Message, info.Hint);
        }

        var machine = await tools.FindAsync(java, ct).ConfigureAwait(false);

        // 3. git: only --repo needs it.
        if (machine.Git is null)
        {
            Add(DoctorLevel.Warning, "git", "không tìm thấy (chỉ cần cho --repo)", "Cài git, hoặc đặt java.git_path.");
        }
        else
        {
            var git = new GitClient(runner, machine.Git);
            var version = await git.VersionAsync(ct).ConfigureAwait(false);
            if (version is null)
            {
                Add(DoctorLevel.Error, "git", $"{machine.Git} không chạy được", "Cài lại git, hoặc đặt java.git_path.");
            }
            else
            {
                Add(DoctorLevel.Ok, "git", version);
            }
        }

        // 4. JDKs.
        CheckJdks(Add, machine, java, catalog);

        // 5. Maven and Gradle, run with a JDK that exists.
        var probeJdk = machine.BuildJdkHomes.TryGetValue(17, out var seventeen) ? seventeen : machine.BuildJdkHomes.Values.FirstOrDefault();
        var maven = await CheckBuildToolAsync(Add, "Maven", machine.Maven, java.MavenPath, probeJdk, ct).ConfigureAwait(false);
        var gradle = await CheckBuildToolAsync(Add, "Gradle", machine.Gradle, java.GradlePath, probeJdk, ct).ConfigureAwait(false);
        if (maven is null && gradle is null)
        {
            Add(DoctorLevel.Warning, "Maven / Gradle", "máy không có cái nào; chỉ project có wrapper (mvnw, gradlew) chạy được", "Cài Maven hoặc Gradle, hoặc chạy \"arrow setup\".");
        }

        // 6. java-helper.
        await CheckJavaHelperAsync(Add, machine, catalog, ct).ConfigureAwait(false);

        // 7. JaCoCo, PIT and tsDetect (P08 §2.6). Missing or wrong files are only a warning: gen downloads them when it measures.
        if (measuringTools is not null)
        {
            CheckMeasuringTools(Add, measuringTools.List());
        }

        // 8. The default model and, for Ollama, its context window (P03 §8).
        if (models is not null)
        {
            foreach (var item in await models.CheckAsync(projectRoot, ct).ConfigureAwait(false))
            {
                Add(item.Level, item.Name, item.Detail, item.Hint);
            }
        }

        // 9. Windows long paths.
        if ((longPaths ?? WindowsSettings.LongPathsEnabled)() == false)
        {
            Add(DoctorLevel.Warning, "Đường dẫn dài", "Windows chưa bật LongPathsEnabled", "Bật LongPathsEnabled (Local Group Policy hoặc registry), hoặc đặt ARROW_HOME ngắn như D:\\arrow.");
        }

        return new DoctorReport(items);
    }

    /// <summary>Tool groups of the doctor line, in order: the lock ids that start with each prefix.</summary>
    private static readonly (string Name, string Prefix)[] ToolGroups = [("JaCoCo", "jacoco"), ("PIT", "pitest"), ("tsDetect", "tsdetect")];

    private static void CheckMeasuringTools(Action<DoctorLevel, string, string, string?> add, IReadOnlyList<ToolState> states)
    {
        const string name = "JaCoCo / PIT / tsDetect";
        var versions = ToolGroups.Select(g => states.FirstOrDefault(s => s.Entry.Id.StartsWith(g.Prefix, StringComparison.Ordinal)) is { } s ? VersionOf(s.Entry) : "—");
        var missing = states.Count(s => s.Problem == "chưa tải");
        var broken = states.Where(s => !s.Installed && s.Problem != "chưa tải").Select(s => s.Entry.Id).ToList();
        var detail = string.Join(" / ", versions);
        if (broken.Count > 0)
        {
            add(DoctorLevel.Warning, name, $"{detail} · sai sha256: {string.Join(", ", broken)}", "Chạy \"arrow tools install\" để tải lại; nếu vẫn sai thì kiểm tra proxy hoặc ARROW_MAVEN_MIRROR.");
        }
        else if (missing == states.Count)
        {
            add(DoctorLevel.Warning, name, $"{detail} · chưa tải", "Chạy \"arrow tools install\" (hoặc để arrow gen tự tải khi đo).");
        }
        else if (missing > 0)
        {
            // Some files (a PIT plugin for JUnit 5 or TestNG) are needed only by some projects; gen downloads them when it needs them.
            add(DoctorLevel.Ok, name, $"{detail} · {states.Count - missing}/{states.Count} file đã tải, sha256 khớp; phần còn lại tải khi cần", null);
        }
        else
        {
            add(DoctorLevel.Ok, name, $"{detail} · sha256 khớp", null);
        }
    }

    /// <summary>The version of a lock entry: the third part of its Maven coordinates, else the suffix of its folder (<c>tsdetect-2.2</c>).</summary>
    private static string VersionOf(ToolEntry entry)
    {
        if (entry.Maven?.Split(':') is { Length: >= 3 } parts)
        {
            return parts[2];
        }

        var folder = entry.File.Split('/')[0];
        return folder.Contains('-', StringComparison.Ordinal) ? folder[(folder.LastIndexOf('-') + 1)..] : "?";
    }

    private void CheckHome(Action<DoctorLevel, string, string, string?> add, ErrorCatalog catalog)
    {
        if (home.Validate() is { } invalid)
        {
            var info = catalog.Describe(invalid);
            add(DoctorLevel.Error, "ARROW_HOME", info.Message, info.Hint);
            return;
        }

        var free = (freeBytes ?? DiskSpace.FreeBytes)(home.Root);
        if (free is { } bytes && bytes < InspectUseCase.MinimumFreeBytes)
        {
            var info = catalog.Describe(ErrorCodes.CfgDiskFull);
            add(DoctorLevel.Error, "ARROW_HOME", $"{home.Root} · chỉ còn {Gb(bytes)} trống. {info.Message}", info.Hint);
            return;
        }

        add(DoctorLevel.Ok, "ARROW_HOME", free is { } known ? $"{home.Root} · {Gb(known)} trống" : home.Root, null);
    }

    private static void CheckJdks(Action<DoctorLevel, string, string, string?> add, MachineTools machine, JavaSettings java, ErrorCatalog catalog)
    {
        foreach (var broken in machine.Jdks.Where(j => j.Major == 0))
        {
            add(DoctorLevel.Warning, "java.jdk_homes", $"{broken.Home} không phải JDK dùng được (thiếu bin/java hoặc bin/javac)", "Sửa hoặc xoá đường dẫn đó trong java.jdk_homes.");
        }

        var byMajor = JdkLocator.ByMajor(machine.Jdks);
        if (!byMajor.Keys.Any(JavaVersions.Supported.Contains))
        {
            var info = catalog.Describe(ErrorCodes.B1JdkMissing, args: new Dictionary<string, string> { ["jdk"] = "17" });
            add(DoctorLevel.Error, "JDK", "không tìm thấy JDK 8, 11, 17 hay 21", info.Hint);
            return;
        }

        var supported = JavaVersions.Supported;
        add(DoctorLevel.Ok,
            "JDK " + string.Join(" / ", supported),
            string.Join(" / ", supported.Select(m => byMajor.TryGetValue(m, out var jdk) ? (jdk.Version.Length > 0 ? jdk.Version : "?") : "—")),
            null);
        foreach (var major in supported.Where(m => !byMajor.ContainsKey(m)))
        {
            add(DoctorLevel.Warning, $"JDK {major}", $"không tìm thấy (chỉ cần cho project Java {major})", null);
        }

        if (!byMajor.Keys.Any(m => m >= JavaHelperJar.MinimumJdk))
        {
            var info = catalog.Describe(ErrorCodes.JhJdkMissing);
            add(DoctorLevel.Error, "JDK cho java-helper", info.Message, info.Hint);
        }
    }

    private async Task<string?> CheckBuildToolAsync(Action<DoctorLevel, string, string, string?> add, string name, string? path, string? configured, string? jdkHome, CancellationToken ct)
    {
        if (path is null)
        {
            if (configured is not null)
            {
                add(DoctorLevel.Warning, name, $"đường dẫn đã cấu hình không có: {configured}", $"Sửa java.{name.ToLowerInvariant()}_path, hoặc xoá nó để tìm trong PATH.");
            }

            return null;
        }

        var environment = new Dictionary<string, string?> { ["JAVA_TOOL_OPTIONS"] = null, ["MAVEN_ARGS"] = null };
        if (jdkHome is not null)
        {
            environment["JAVA_HOME"] = jdkHome;
        }

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(new ProcessSpec(path, ["-v"], Path.GetTempPath(), environment, TimeSpan.FromSeconds(120)), ct).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            add(DoctorLevel.Warning, name, $"{path} không chạy được: {ex.Message}", $"Cài lại {name}, hoặc sửa java.{name.ToLowerInvariant()}_path.");
            return null;
        }

        var version = BuildFailureClassifier.ToolVersion(result.Output);
        if (version is null)
        {
            add(DoctorLevel.Warning, name, $"{path} không báo phiên bản: {BuildFailureClassifier.FirstError(result.Output)}", $"Cài lại {name}, hoặc sửa java.{name.ToLowerInvariant()}_path.");
            return null;
        }

        add(DoctorLevel.Ok, name, version, null);
        return version;
    }

    private async Task CheckJavaHelperAsync(Action<DoctorLevel, string, string, string?> add, MachineTools machine, ErrorCatalog catalog, CancellationToken ct)
    {
        string java;
        try
        {
            java = JavaHelperJar.FindJava(machine.HelperJdkHomes);
        }
        catch (ArrowException)
        {
            // Already reported by the JDK check.
            return;
        }

        try
        {
            var version = await javaHelper.PingAsync(java, ct).ConfigureAwait(false);
            add(DoctorLevel.Ok, "java-helper", $"{version} (JDK {JavaVersionsOf(machine, java)})", null);
        }
        catch (ArrowException ex)
        {
            var info = catalog.Describe(ex);
            add(DoctorLevel.Error, "java-helper", info.Message, info.Hint);
        }
    }

    private static string JavaVersionsOf(MachineTools machine, string java) =>
        machine.Jdks.FirstOrDefault(j => j.Major > 0 && java.StartsWith(j.Home, StringComparison.OrdinalIgnoreCase))?.Major.ToString(CultureInfo.InvariantCulture) ?? "?";

    private static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0", CultureInfo.InvariantCulture) + " GB";
}
