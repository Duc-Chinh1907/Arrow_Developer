using System.CommandLine;
using System.Globalization;
using Arrow.Business.UseCases;
using Arrow.Cli.Rendering;
using Arrow.Common;
using Arrow.Common.Analysis;

namespace Arrow.Cli.Commands;

/// <summary><c>arrow history [--limit n]</c> and <c>arrow show &lt;run&gt; [&lt;class&gt;]</c> (P08 §2.4, §2.7).</summary>
internal static class ShowCommand
{
    public static Command BuildHistory(CliApp app)
    {
        var limit = new Option<int>("--limit") { Description = "Số dòng, mới nhất trước", DefaultValueFactory = _ => 20 };
        var command = new Command("history", "Các run đã chạy: thời gian, project, số class VALID, số file đã ghi") { limit };
        command.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(History(context, parse.GetValue(limit)))));
        return command;
    }

    public static Command BuildShow(CliApp app)
    {
        var run = new Argument<string>("run") { Description = "Id hoặc tiền tố của id run (xem arrow history)" };
        var cls = new Argument<string?>("class") { Description = "Một class của run: in từng vòng sửa và chỉ số", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("show", "Chi tiết một run, hoặc một class của run") { run, cls };
        command.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(
            parse.GetValue(cls) is { } c ? ShowTarget(context, parse.GetValue(run)!, c) : Show(context, parse.GetValue(run)!))));
        return command;
    }

    private static int History(CommandContext context, int limit)
    {
        var history = context.Get<IShowRunUseCase>().History(Math.Max(1, limit));
        context.Renderer.Json(new { runs = history.Entries, skipped_lines = history.SkippedLines });
        if (context.Renderer.Mode == RenderMode.Json)
        {
            return ExitCodes.Ok;
        }

        if (history.SkippedLines > 0)
        {
            context.Renderer.Warn($"Bỏ qua {history.SkippedLines} dòng hỏng của history.jsonl");
        }

        if (history.Entries.Count == 0)
        {
            context.Renderer.Line("Chưa có run nào. Chạy: arrow gen <class>");
            return ExitCodes.Ok;
        }

        context.Renderer.Table(
            ["Run", "Thời gian", "Project", "Class", "VALID", "Đã ghi", "Trạng thái"],
            history.Entries.Select(e => (IReadOnlyList<string>)
            [
                e.RunId, e.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), e.ProjectName,
                e.Cuts.Count.ToString(CultureInfo.InvariantCulture), e.Valid.ToString(CultureInfo.InvariantCulture),
                e.Written.ToString(CultureInfo.InvariantCulture), e.Status.ToString().ToUpperInvariant(),
            ]).ToList());
        return ExitCodes.Ok;
    }

    private static int Show(CommandContext context, string run)
    {
        var view = context.Get<IShowRunUseCase>().Show(run);
        context.Renderer.Json(new { run = view.Run, run_dir = view.Dir, targets = view.Targets.Select(t => new { cut_fqn = t.Target.CutFqn, state = t.Target.State, result = t.Result }) });
        if (context.Renderer.Mode == RenderMode.Json)
        {
            return ExitCodes.Ok;
        }

        var r = view.Run;
        context.Renderer.Line($"Run {r.RunId} · {r.Status.ToString().ToUpperInvariant()} · {r.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        context.Renderer.Line($"Project {r.Project.PathName}{(r.Project.Root is { } root ? $" ({root})" : r.Project.Url is { } url ? $" ({url} @ {r.Project.GitCommit})" : "")}");
        if (r.Config is { } c)
        {
            context.Renderer.Line($"Model {c.Model} ({c.ProviderHost}) · N={c.Rounds} · {(c.Repair ? "có sửa" : "không sửa")} · {(c.Quality ? "có đo" : "không đo")}");
        }

        context.Renderer.Table(
            ["Class", "c0", "Kết quả", "Vòng", "Line", "Branch", "Mutation", "Smell", "Token", "File đã ghi"],
            view.Targets.Select(t => (IReadOnlyList<string>)
            [
                t.Target.CutFqn[(t.Target.CutFqn.LastIndexOf('.') + 1)..],
                t.Result?.C0State?.ToString().ToUpperInvariant() ?? "-",
                t.Result?.Result.ToString().ToUpperInvariant() ?? t.Target.State.ToString().ToUpperInvariant(),
                t.Result is { } res ? res.RoundsRun.ToString(CultureInfo.InvariantCulture) : "-",
                Percent(t.Result?.Metrics?.Line), Percent(t.Result?.Metrics?.Branch), Percent(t.Result?.Metrics?.Mutation),
                t.Result?.Metrics?.SmellDensity is { } d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "-",
                t.Result is { } tr ? $"{(tr.Tokens.In + tr.Tokens.Out) / 1000.0:0.0}k" : "-",
                t.Result?.Apply is { Decision: ApplyDecision.Written, Path: { } p } ? p : "-",
            ]).ToList());
        context.Renderer.Line($"Tổng: {r.Totals.LlmCalls} lượt gọi model · {(r.Totals.TokensIn + r.Totals.TokensOut) / 1000.0:0.0}k token"
            + (r.Totals.CostUsd is { } usd ? $" · {usd.ToString("0.####", CultureInfo.InvariantCulture)} USD" : ""));
        context.Renderer.Line($"Xem một class: arrow show {r.RunId} <class>");
        return ExitCodes.Ok;
    }

    private static int ShowTarget(CommandContext context, string run, string cls)
    {
        var view = context.Get<IShowRunUseCase>().ShowTarget(run, cls);
        context.Renderer.Json(view);
        if (context.Renderer.Mode == RenderMode.Json)
        {
            return ExitCodes.Ok;
        }

        var renderer = context.Renderer;
        renderer.Line($"{view.Cut}: {view.Result?.Result.ToString().ToUpperInvariant() ?? "chưa xong"}");
        if (view.Metadata is { } m)
        {
            var b = m.BuildTool;
            renderer.Line($"Khối 1  {(b.Kind == BuildToolKind.Gradle ? "Gradle" : "Maven")} {b.Version} · build root {b.BuildRoot} · JDK {m.Java?.JdkMajor.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                + $" · {m.TestFramework?.Name ?? "không rõ framework"} · test: {m.Paths.GeneratedTestPath}");
        }
        else if (view.Block1 is { Code: { } code } block1)
        {
            renderer.Line($"Khối 1  {code} · {block1.Message}");
        }

        if (view.Rounds.Count > 0)
        {
            renderer.Table(
                ["Vòng", "Mức", "Kết quả", "So với checkpoint", "Quyết định"],
                view.Rounds.Select(round => (IReadOnlyList<string>)
                [
                    round.Round == 0 ? "c0" : round.Round.ToString(CultureInfo.InvariantCulture),
                    round.Level?.ToString(CultureInfo.InvariantCulture) ?? "-",
                    round.Result,
                    round.Comparison switch { Business.Repair.Comparison.Better => "tốt hơn", Business.Repair.Comparison.Worse => "tệ hơn", Business.Repair.Comparison.Equal => "ngang", _ => "-" },
                    round.Decision,
                ]).ToList());
        }

        if (view.Coverage is not null || view.Mutation is not null || view.Smells is not null)
        {
            renderer.Line($"Khối 4  line {Percent(view.Coverage?.Line)} · branch {Percent(view.Coverage?.Branch)} · method {Percent(view.Coverage?.Method)}"
                + $" · mutation {Percent(view.Mutation?.Score)} · test strength {Percent(view.Mutation?.TestStrength)}"
                + $" · smell {(view.Smells?.Density is { } d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "-")}");
            if (view.Mutation is { SurvivorsSample.Count: > 0 } pit)
            {
                renderer.Line("Mutant còn sống (test chưa bắt được):");
                foreach (var s in pit.SurvivorsSample)
                {
                    renderer.Line($"  {s.Method}{(s.Line is { } l ? $" dòng {l}" : "")} · {s.Mutator}");
                }
            }

            if (view.Smells is { Smells.Count: > 0 } smells)
            {
                renderer.Line("Test smell: " + string.Join(", ", smells.Smells.Select(s => $"{s.Key} ×{s.Value}")));
            }
        }

        if (view.Result?.Error is { } error)
        {
            renderer.Error(error);
        }

        foreach (var (name, path) in view.Files)
        {
            renderer.Line($"{name,-11} {path}");
        }

        return ExitCodes.Ok;
    }

    private static string Percent(double? ratio) => ratio is { } r ? (r * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "-";
}
