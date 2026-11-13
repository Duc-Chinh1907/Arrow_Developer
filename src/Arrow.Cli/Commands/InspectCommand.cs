using System.CommandLine;
using System.Globalization;
using Arrow.Business;
using Arrow.Business.UseCases;
using Arrow.Cli.Rendering;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;

namespace Arrow.Cli.Commands;

/// <summary><c>arrow inspect &lt;class&gt; [--project &lt;dir&gt; | --repo &lt;url&gt; [--ref &lt;ref&gt;]]</c> (P08 §2.3): block 1 only, no model.</summary>
internal static class InspectCommand
{
    private const int LabelWidth = 14;

    public static Command Build(CliApp app)
    {
        var cls = new Argument<string>("class") { Description = "Tên ngắn, FQN hoặc đường dẫn file .java" };
        var project = new Option<string?>("--project") { Description = "Thư mục project (mặc định: thư mục hiện tại)" };
        var repo = new Option<string?>("--repo") { Description = "URL https của repo GitHub" };
        var gitRef = new Option<string?>("--ref") { Description = "Nhánh, tag hoặc SHA đầy đủ của repo (mặc định: nhánh mặc định)" };
        var command = new Command("inspect", "Phân tích project và class: build root, JDK, test framework, baseline. Không gọi model.") { cls, project, repo, gitRef };
        command.SetAction((parse, ct) => app.Execute(parse, ct, (context, token) =>
            RunAsync(context, parse.GetValue(cls)!, parse.GetValue(project), parse.GetValue(repo), parse.GetValue(gitRef), parse.Tokens.Select(t => t.Value).ToList(), token)));
        return command;
    }

    private static async Task<int> RunAsync(CommandContext context, string cls, string? project, string? repo, string? gitRef, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        // P08 §2.2: argument checks come first and create nothing.
        static void Usage(string detail) =>
            throw new ArrowException(ErrorCodes.CliArgs, detail, new Dictionary<string, string> { ["help_command"] = "arrow inspect --help" });
        if (repo is not null && project is not null)
        {
            Usage("--repo và --project không dùng cùng nhau.");
        }

        if (gitRef is not null && repo is null)
        {
            Usage("--ref chỉ dùng cùng --repo.");
        }

        var target = repo is not null
            ? new ProjectTarget(ProjectSourceKind.GitRepository, repo, gitRef, cls)
            : new ProjectTarget(ProjectSourceKind.LocalDirectory, Path.GetFullPath(project ?? ".", context.Console.CurrentDirectory), null, cls);
        var command = new List<string> { "inspect" };
        command.AddRange(tokens.Where(t => t != "inspect"));

        var result = await context.Get<IInspectUseCase>().RunAsync(
            target,
            command,
            repo is null ? target.Location : context.ProjectRoot,
            new EventPrinter(context.Renderer, context.Globals.Verbose),
            context.Get<IConfirmation>(),
            ct).ConfigureAwait(false);

        context.Renderer.Json(new { run_id = result.RunId, run_dir = result.RunDir, outcome = result.Outcome, analysis = result.Analysis, suggestions = result.Suggestions, warnings = result.Warnings });
        if (context.Renderer.Mode != RenderMode.Json)
        {
            Print(context, result);
        }
        else if (result.Outcome.Outcome != BlockOutcomeKind.Ok)
        {
            // stdout holds the JSON result above (its outcome has the code, message and hint); the error also goes to stderr as text.
            context.Renderer.Fail($"{result.Outcome.Code} · {result.Outcome.Message}", result.Outcome.Hint);
        }

        return result.Outcome.Outcome == BlockOutcomeKind.Ok ? ExitCodes.Ok : ExitCodes.Usage;
    }

    private static void Print(CommandContext context, InspectResult result)
    {
        var renderer = context.Renderer;
        foreach (var warning in result.Warnings)
        {
            renderer.Warn(warning);
        }

        if (result.Outcome.Outcome != BlockOutcomeKind.Ok || result.Analysis?.Metadata is not { } m)
        {
            if (result.Suggestions.Count > 0)
            {
                renderer.Line("Gần giống: " + string.Join(", ", result.Suggestions));
            }

            if (result.Outcome.FirstError is not null)
            {
                renderer.Line("Lỗi đầu tiên: " + result.Outcome.FirstError);
            }

            var log = result.Outcome.Log is null || result.Analysis?.Cut is not { } cut
                ? null
                : Path.Combine(result.RunDir, "targets", cut.Fqn, result.Outcome.Log.Replace('/', Path.DirectorySeparatorChar));
            renderer.Error(new ErrorInfo(result.Outcome.Code ?? "", result.Outcome.Message ?? "", result.Outcome.Hint, log));
            return;
        }

        var analysis = result.Analysis!;
        renderer.Ok($"{m.Cut.Fqn}: khối 1 xong trong {Seconds(analysis.Timings.Total)}");
        Row(renderer, "Build tool", BuildTool(m));
        Row(renderer, "Build root", BuildRoot(m.BuildTool, analysis.Workspace));
        Row(renderer, "Java", Java(m));
        Row(renderer, "Test", TestFramework(m));
        Row(renderer, "Dependency", m.Dependencies is { } d ? $"{d.ResolvedCount} đã phân giải (khai báo: {d.Direct.Count} chính, {d.Test.Count} test)" : "không rõ");
        Row(renderer, "Source root", m.Paths.SourceRoot);
        Row(renderer, "Test root", m.Paths.TestRoot);
        Row(renderer, "Test mới", m.Paths.GeneratedTestPath + (m.Paths.NameMatchesTestIncludes ? "" : "  (không khớp mẫu tên test của project)"));
        if (analysis.Baseline is { } b)
        {
            Row(renderer, "Baseline", $"{b.PassingInAllRuns.Count} pass · {b.Flaky.Count} flaky · {b.FailingInAllRuns.Count} fail ({b.Runs} lần chạy)");
        }

        var t = analysis.Timings;
        Row(renderer, "Thời gian", $"lấy mã {Seconds(t.Checkout)} · liệt kê class {Seconds(t.Locate)} · tìm cấu hình {Seconds(t.Plan)} · biên dịch {Seconds(t.Compile)}"
            + $" · baseline {string.Join(" + ", t.Baseline.Select(Seconds))}");
        if (analysis.SharedFrom is not null)
        {
            Row(renderer, "Dùng chung", "kết quả module của " + analysis.SharedFrom);
        }

        Row(renderer, "Kết quả lưu ở", result.RunDir);
    }

    private static void Row(IRenderer renderer, string label, string value) => renderer.Line(label.PadRight(LabelWidth) + value);

    private static string Seconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string BuildTool(RepositoryMetadata m)
    {
        var b = m.BuildTool;
        var name = b.Kind == BuildToolKind.Gradle ? "Gradle" : "Maven";
        var wrapper = b.WrapperFallback ? " (wrapper lỗi, dùng bản của máy)" : b.Wrapper ? " (wrapper)" : "";
        return $"{name} {b.Version}{wrapper}".Replace("  ", " ", StringComparison.Ordinal);
    }

    private static string BuildRoot(BuildToolMetadata b, WorkspaceInfo? w)
    {
        var selector = w is null ? "" : w.MavenModuleSelector.Count > 0 ? " · " + string.Join(' ', w.MavenModuleSelector) : w.GradleProject is { } p ? " · project " + p : "";
        return $"{b.BuildRoot} · {(b.BuildMode == BuildMode.Reactor ? "reactor" : "standalone")}{selector}";
    }

    private static string Java(RepositoryMetadata m)
    {
        if (m.Java is not { } j)
        {
            return "không rõ";
        }

        var declared = j.Declared is null ? "không khai báo" : $"khai báo {j.Declared} ({j.DeclaredSource})";
        return $"{declared} · dùng JDK {j.JdkMajor} ({j.JdkChoiceReason})";
    }

    private static string TestFramework(RepositoryMetadata m)
    {
        if (m.TestFramework is not { Name: not null } f)
        {
            return "không thấy JUnit hay TestNG";
        }

        var name = f.Name switch { "junit5" => "JUnit 5", "testng" => "TestNG", _ => "JUnit 4" };
        var mocking = f.Mocking.Count == 0 ? "không" : string.Join(", ", f.Mocking);
        var assertions = f.AssertionLibs.Count == 0 ? "không" : string.Join(", ", f.AssertionLibs);
        return $"{name} {f.Version}{(f.Vintage ? " + vintage" : "")} · mock: {mocking} · assertion: {assertions}";
    }
}
