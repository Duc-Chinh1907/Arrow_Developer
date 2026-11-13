using System.CommandLine;
using Arrow.Business.UseCases;
using Arrow.Common.Errors;
using Spectre.Console;

namespace Arrow.Cli.Commands;

/// <summary><c>arrow secrets list|set|remove</c> (P09 §4.4). A key is never taken from the command line (P09 §4.2).</summary>
internal static class SecretsCommand
{
    public static Command Build(CliApp app)
    {
        var command = new Command("secrets", "Quản lý khoá API (P09 §4)");

        var list = new Command("list", "In tên khoá và 4 ký tự cuối");
        list.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(List(context))));

        var setRef = new Argument<string>("ref") { Description = "Tên khoá, ví dụ teamo (a-z, 0-9, '-')" };
        var stdin = new Option<bool>("--stdin") { Description = "Đọc khoá từ stdin (dòng đầu), dùng cho script" };
        var set = new Command("set", "Lưu một khoá; khoá được nhập ẩn, hoặc đọc từ stdin với --stdin") { setRef, stdin };
        set.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(Set(context, parse.GetValue(setRef)!, parse.GetValue(stdin)))));

        var removeRef = new Argument<string>("ref") { Description = "Tên khoá cần xoá" };
        var remove = new Command("remove", "Xoá một khoá đã lưu") { removeRef };
        remove.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(Remove(context, parse.GetValue(removeRef)!))));

        command.Subcommands.Add(list);
        command.Subcommands.Add(set);
        command.Subcommands.Add(remove);
        return command;
    }

    private static int List(CommandContext context)
    {
        var secrets = context.Get<SecretsUseCase>().List();
        context.Renderer.Json(new { secrets });
        if (context.Renderer.Mode != Rendering.RenderMode.Json)
        {
            if (secrets.Count == 0)
            {
                context.Renderer.Line("Chưa có khoá nào. Lưu khoá bằng: arrow secrets set <ref>");
            }
            else
            {
                context.Renderer.Table(
                    ["Tên", "Khoá", "Nguồn"],
                    secrets.Select(s => (IReadOnlyList<string>)[s.Reference, s.Hint, Source(s.Source)]).ToList());
            }
        }

        return ExitCodes.Ok;
    }

    private static int Set(CommandContext context, string reference, bool fromStdin)
    {
        string value;
        if (fromStdin)
        {
            value = context.Console.In.ReadLine() ?? "";
        }
        else if (context.Console.HasKeyboard)
        {
            value = AnsiConsole.Prompt(new TextPrompt<string>($"Khoá API cho {reference}:").Secret());
        }
        else
        {
            throw new ArrowException(ErrorCodes.CliNoTty, "Không có bàn phím để nhập khoá. Dùng: arrow secrets set " + reference + " --stdin");
        }

        var hint = context.Get<SecretsUseCase>().Set(reference, value);
        context.Renderer.Json(new { reference, hint });
        context.Renderer.Ok($"Đã lưu khoá {reference} ({hint})");
        return ExitCodes.Ok;
    }

    private static int Remove(CommandContext context, string reference)
    {
        var removed = context.Get<SecretsUseCase>().Remove(reference);
        context.Renderer.Json(new { reference, removed });
        if (removed)
        {
            context.Renderer.Ok($"Đã xoá khoá {reference}");
        }
        else
        {
            context.Renderer.Warn($"Không có khoá {reference} trong kho khoá");
        }

        return ExitCodes.Ok;
    }

    private static string Source(Common.Secrets.SecretSource source) => source switch
    {
        Common.Secrets.SecretSource.ArrowEnvironment => "biến môi trường",
        Common.Secrets.SecretSource.ProviderEnvironment => "biến của provider",
        _ => "kho khoá",
    };
}
