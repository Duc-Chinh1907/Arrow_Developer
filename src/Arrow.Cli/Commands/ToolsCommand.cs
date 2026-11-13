using System.CommandLine;
using Arrow.Business.UseCases;
using Arrow.Cli.Rendering;

namespace Arrow.Cli.Commands;

/// <summary><c>arrow tools list</c> and <c>arrow tools install [id…]</c> (P09 §6).</summary>
internal static class ToolsCommand
{
    public static Command Build(CliApp app)
    {
        var command = new Command("tools", "Công cụ đo chất lượng (JaCoCo, PIT, tsDetect): xem và tải");
        var list = new Command("list", "Liệt kê công cụ và trạng thái");
        list.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(List(context))));

        var ids = new Argument<string[]>("id") { Description = "Công cụ cần tải (mặc định: tất cả)", Arity = ArgumentArity.ZeroOrMore };
        var install = new Command("install", "Tải công cụ từ Maven Central (hoặc ARROW_MAVEN_MIRROR) và kiểm tra sha256") { ids };
        install.SetAction((parse, ct) => app.Execute(parse, ct, (context, token) => InstallAsync(context, parse.GetValue(ids) ?? [], token)));

        command.Subcommands.Add(list);
        command.Subcommands.Add(install);
        command.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(List(context))));
        return command;
    }

    private static int List(CommandContext context)
    {
        var rows = context.Get<IToolsUseCase>().List();
        context.Renderer.Json(new { tools = rows });
        if (context.Renderer.Mode != RenderMode.Json)
        {
            context.Renderer.Table(
                ["Công cụ", "Nguồn", "Trạng thái"],
                rows.Select(r => (IReadOnlyList<string>)[r.Id, r.Source, r.Installed ? "✓ đã có" : r.Problem ?? "chưa có"]).ToList());
            if (rows.Any(r => !r.Installed))
            {
                context.Renderer.Line("Tải các công cụ còn thiếu: arrow tools install");
            }
        }

        return ExitCodes.Ok;
    }

    private static async Task<int> InstallAsync(CommandContext context, string[] ids, CancellationToken ct)
    {
        var results = await context.Get<IToolsUseCase>().InstallAsync(ids, new SyncProgress(context.Renderer), ct).ConfigureAwait(false);
        context.Renderer.Json(new { tools = results });
        foreach (var result in results)
        {
            if (result.Error is { } error)
            {
                context.Renderer.Fail($"{result.Id}: {error.Code} · {error.Message}", error.Hint);
            }
            else
            {
                context.Renderer.Ok($"{result.Id}: {result.Path}");
            }
        }

        return results.All(r => r.Error is null) ? ExitCodes.Ok : ExitCodes.Usage;
    }

    /// <summary>Prints progress on the calling thread, in order.</summary>
    private sealed class SyncProgress(IRenderer renderer) : IProgress<string>
    {
        public void Report(string value) => renderer.Step(value);
    }
}
