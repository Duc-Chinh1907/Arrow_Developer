using System.CommandLine;
using Arrow.Business.UseCases;

namespace Arrow.Cli.Commands;

/// <summary><c>arrow doctor</c> (P08 §2.6): one line per check, printed as soon as it is checked.</summary>
internal static class DoctorCommand
{
    private const int NameWidth = 22;

    public static Command Build(CliApp app)
    {
        var command = new Command("doctor", "Kiểm tra môi trường: ARROW_HOME, cấu hình, git, JDK, Maven/Gradle, java-helper");
        command.SetAction((parse, ct) => app.Execute(parse, ct, RunAsync));
        return command;
    }

    private static async Task<int> RunAsync(CommandContext context, CancellationToken ct)
    {
        var json = context.Renderer.Mode == Rendering.RenderMode.Json;
        if (!json)
        {
            context.Renderer.Line("Kiểm tra môi trường");
        }

        var report = await context.Get<IDoctorUseCase>().RunAsync(context.ProjectRoot, json ? null : item => Print(context, item), ct).ConfigureAwait(false);

        var verdict = report.Ready
            ? report.Warnings == 0 ? "SẴN SÀNG" : $"SẴN SÀNG ({report.Warnings} cảnh báo)"
            : $"CHƯA SẴN SÀNG ({report.Errors} lỗi)";
        context.Renderer.Json(new
        {
            ready = report.Ready,
            warnings = report.Warnings,
            errors = report.Errors,
            items = report.Items.Select(i => new { level = i.Level.ToString().ToUpperInvariant(), name = i.Name, detail = i.Detail, hint = i.Hint }),
        });
        context.Renderer.Line("Kết quả: " + verdict);
        return report.Ready ? ExitCodes.Ok : ExitCodes.Usage;
    }

    private static void Print(CommandContext context, DoctorItem item)
    {
        var text = $"{item.Name.PadRight(NameWidth)} {item.Detail}";
        switch (item.Level)
        {
            case DoctorLevel.Ok:
                context.Renderer.Ok(text);
                break;
            case DoctorLevel.Warning:
                context.Renderer.Warn(text);
                break;
            default:
                context.Renderer.Fail(text, item.Hint);
                break;
        }
    }
}
