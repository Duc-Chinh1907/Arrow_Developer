using System.CommandLine;
using System.Globalization;
using Arrow.Business;
using Arrow.Business.UseCases;
using Arrow.Cli.Rendering;
using Arrow.Common;
using Arrow.Common.Errors;

namespace Arrow.Cli.Commands;

/// <summary>
/// <c>arrow gen &lt;class&gt;</c> (P08 §2.2), the minimal version of milestone M1: one class, blocks 1–3, then the question to write
/// the file. <c>--package</c>, <c>--jobs</c>, <c>--max-tokens</c> estimates and <c>--resume</c> come with GĐ6.
/// </summary>
internal static class GenCommand
{
    public static Command Build(CliApp app)
    {
        var classes = new Argument<string[]>("class") { Description = "Tên ngắn, FQN hoặc đường dẫn file .java", Arity = ArgumentArity.OneOrMore };
        var project = new Option<string?>("--project") { Description = "Thư mục project (mặc định: thư mục hiện tại)" };
        var repo = new Option<string?>("--repo") { Description = "URL https của repo GitHub" };
        var gitRef = new Option<string?>("--ref") { Description = "Nhánh, tag hoặc SHA đầy đủ của repo" };
        var model = new Option<string?>("--model") { Description = "Tên model trong llm.models (mặc định: llm.default_model)" };
        var rounds = new Option<int?>("--rounds") { Description = "Số vòng sửa tối đa, 1–10 (mặc định: repair.rounds)" };
        var noRepair = new Option<bool>("--no-repair") { Description = "Chỉ sinh và xác minh c0" };
        var noMetrics = new Option<bool>("--no-metrics") { Description = "Bỏ khối 4 (đo chất lượng)" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Chạy đủ nhưng không ghi vào project" };
        var maxTokens = new Option<long?>("--max-tokens") { Description = "Ngân sách token của cả run" };
        var command = new Command("gen", "Sinh unit test cho một class, xác minh trên build thật, sửa tự động, rồi hỏi ghi file")
        {
            classes, project, repo, gitRef, model, rounds, noRepair, noMetrics, dryRun, maxTokens,
        };
        command.SetAction((parse, ct) => app.Execute(parse, ct, (context, token) =>
        {
            var options = new GenerateOptions(parse.GetValue(model), parse.GetValue(rounds), parse.GetValue(noRepair), parse.GetValue(noMetrics),
                context.Globals.Yes, parse.GetValue(dryRun), parse.GetValue(maxTokens));
            return RunAsync(context, parse.GetValue(classes)!, parse.GetValue(project), parse.GetValue(repo), parse.GetValue(gitRef), options,
                parse.Tokens.Select(t => t.Value).ToList(), token);
        }));
        return command;
    }

    private static async Task<int> RunAsync(
        CommandContext context, string[] classes, string? project, string? repo, string? gitRef, GenerateOptions options, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        static void Usage(string detail) =>
            throw new ArrowException(ErrorCodes.CliArgs, detail, new Dictionary<string, string> { ["help_command"] = "arrow gen --help" });
        if (repo is not null && project is not null)
        {
            Usage("--repo và --project không dùng cùng nhau.");
        }

        if (gitRef is not null && repo is null)
        {
            Usage("--ref chỉ dùng cùng --repo.");
        }

        if (classes.Length > 1)
        {
            Usage("Bản này sinh test cho một class mỗi lần; chạy arrow gen riêng cho từng class.");
        }

        var cls = classes[0];
        var target = repo is not null
            ? new ProjectTarget(ProjectSourceKind.GitRepository, repo, gitRef, cls)
            : new ProjectTarget(ProjectSourceKind.LocalDirectory, Path.GetFullPath(project ?? ".", context.Console.CurrentDirectory), null, cls);
        var command = new List<string> { "gen" };
        command.AddRange(tokens.Where(t => t != "gen"));

        var result = await context.Get<IGenerateTestsUseCase>().RunAsync(
            target,
            options,
            command,
            repo is null ? target.Location : context.ProjectRoot,
            new EventPrinter(context.Renderer, context.Globals.Verbose),
            context.Get<IConfirmation>(),
            ct).ConfigureAwait(false);

        context.Renderer.Json(new { run_id = result.RunId, run_dir = result.RunDir, status = result.Status, exit_code = result.ExitCode, targets = result.Targets, totals = result.Totals, warnings = result.Warnings });
        if (context.Renderer.Mode != RenderMode.Json)
        {
            Print(context.Renderer, result);
        }
        else if (result.Targets.FirstOrDefault()?.Error is { } error)
        {
            context.Renderer.Fail($"{error.Code} · {error.Message}", error.Hint);
        }

        return result.ExitCode;
    }

    private static string Percent(double? ratio) => ratio is { } r ? (r * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "-";

    private static void Print(IRenderer renderer, GenerateResult result)
    {
        foreach (var warning in result.Warnings.Distinct(StringComparer.Ordinal))
        {
            renderer.Warn(warning);
        }

        foreach (var target in result.Targets)
        {
            if (target.Error is { } error)
            {
                var log = error.Log is null ? null : Path.Combine(result.RunDir, "targets", target.CutFqn, error.Log.Replace('/', Path.DirectorySeparatorChar));
                renderer.Error(error with { Log = log });
            }

            var rounds = target.RoundsRun == 0 ? "" : $" · {target.RoundsRun} vòng sửa";
            var line = $"{target.CutFqn}: {target.Result.ToString().ToUpperInvariant()}{rounds}"
                       + (target.StopReason is { } stop ? $" (dừng: {stop.ToString().ToLowerInvariant()})" : "");
            if (target.Result == TargetOutcome.Valid)
            {
                renderer.Ok(line);
            }
            else
            {
                renderer.Fail(line, target.Result is TargetOutcome.Compile or TargetOutcome.Unknown or TargetOutcome.Discovery or TargetOutcome.Runtime or TargetOutcome.Assertion or TargetOutcome.Regression
                    ? $"Thử tăng --rounds, đổi model, hoặc xem các vòng trong {Path.Combine(result.RunDir, "targets", target.CutFqn, "block3")}"
                    : null);
            }

            if (target.Metrics is { } q)
            {
                renderer.Line($"Chất lượng: line {Percent(q.Line)} · branch {Percent(q.Branch)} · method {Percent(q.Method)} · mutation {Percent(q.Mutation)}"
                    + $" · test strength {Percent(q.TestStrength)} · smell {(q.SmellDensity is { } d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "-")}");
            }

            switch (target.Apply?.Decision)
            {
                case ApplyDecision.Written:
                    renderer.Ok($"Đã ghi {target.Apply.Path}. Gợi ý: git add {target.Apply.Path}");
                    break;
                case ApplyDecision.DryRun:
                    renderer.Line($"--dry-run: không ghi {target.Apply.Path}; bản test ở {Path.Combine(result.RunDir, "targets", target.CutFqn, "block3", "final.java")}");
                    break;
                case ApplyDecision.RemoteProject:
                case ApplyDecision.Declined:
                case ApplyDecision.SkippedNonInteractive:
                    renderer.Line($"Không ghi vào project; bản test ở {Path.Combine(result.RunDir, "targets", target.CutFqn, "block3", "final.java")}");
                    break;
            }
        }

        var totals = result.Totals;
        var cost = totals.CostUsd is { } usd ? " · " + usd.ToString("0.####", CultureInfo.InvariantCulture) + " USD" : "";
        var seconds = result.Targets.Sum(t => t.Seconds.Total);
        renderer.Line($"Tổng: {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s · {totals.LlmCalls} lượt gọi model · {(totals.TokensIn + totals.TokensOut) / 1000.0:0.0}k token{cost}");
        renderer.Line("Kết quả lưu ở " + result.RunDir);
    }
}
