using System.CommandLine;
using System.Text.Json.Nodes;
using Arrow.Business.UseCases;
using Arrow.Common.Config;
using Arrow.Common.Json;

namespace Arrow.Cli.Commands;

/// <summary><c>arrow config list|get|set|unset|path</c> (P09 §3.4).</summary>
internal static class ConfigCommand
{
    public static Command Build(CliApp app)
    {
        var command = new Command("config", "Xem và sửa cấu hình (P09 §3)");

        var origin = new Option<bool>("--origin") { Description = "In thêm nguồn của từng khoá: default, user, project, env, cli" };
        var list = new Command("list", "In cấu hình hiệu lực") { origin };
        list.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(List(context, parse.GetValue(origin)))));

        var getKey = new Argument<string>("key") { Description = "Khoá, ví dụ repair.rounds hoặc repair" };
        var get = new Command("get", "In một giá trị") { getKey };
        get.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(Get(context, parse.GetValue(getKey)!))));

        var setKey = new Argument<string>("key") { Description = "Khoá, ví dụ repair.rounds" };
        var setValue = new Argument<string>("value") { Description = "Giá trị, đọc theo kiểu của khoá; null để đặt rỗng" };
        var setProject = new Option<bool>("--project") { Description = "Ghi vào .arrow.json của project thay vì cấu hình người dùng" };
        var set = new Command("set", "Ghi một giá trị") { setKey, setValue, setProject };
        set.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) =>
            Task.FromResult(Set(context, parse.GetValue(setKey)!, parse.GetValue(setValue)!, parse.GetValue(setProject)))));

        var unsetKey = new Argument<string>("key") { Description = "Khoá cần xoá" };
        var unsetProject = new Option<bool>("--project") { Description = "Xoá khỏi .arrow.json của project" };
        var unset = new Command("unset", "Xoá một khoá khỏi một tầng") { unsetKey, unsetProject };
        unset.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) =>
            Task.FromResult(Unset(context, parse.GetValue(unsetKey)!, parse.GetValue(unsetProject)))));

        var path = new Command("path", "In đường dẫn config.json và .arrow.json");
        path.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(Path(context))));

        command.Subcommands.Add(list);
        command.Subcommands.Add(get);
        command.Subcommands.Add(set);
        command.Subcommands.Add(unset);
        command.Subcommands.Add(path);
        return command;
    }

    private static int List(CommandContext context, bool withOrigin)
    {
        var listing = context.Get<ConfigUseCase>().List(context.ProjectRoot);
        var entries = listing.Effective.Entries;
        context.Renderer.Json(new
        {
            entries = entries.Select(e => new { key = e.Key, value = JsonNode.Parse(e.ValueJson), origin = e.Origin }),
            issues = listing.Issues,
        });
        if (context.Renderer.Mode != Rendering.RenderMode.Json)
        {
            IReadOnlyList<string> headers = withOrigin ? ["Khoá", "Giá trị", "Nguồn"] : ["Khoá", "Giá trị"];
            context.Renderer.Table(headers, entries.Select(e => Row(e, withOrigin)).ToList());
            foreach (var issue in listing.Issues)
            {
                context.Renderer.Warn($"{issue.Key} = {issue.ValueJson}: cần {issue.Rule}");
            }
        }

        return ExitCodes.Ok;
    }

    private static int Get(CommandContext context, string key)
    {
        var entries = context.Get<ConfigUseCase>().Get(context.ProjectRoot, key);
        context.Renderer.Json(new { entries = entries.Select(e => new { key = e.Key, value = JsonNode.Parse(e.ValueJson), origin = e.Origin }) });
        if (context.Renderer.Mode != Rendering.RenderMode.Json)
        {
            if (entries is [var single] && single.Key == key)
            {
                context.Renderer.Line(Display(single.ValueJson));
            }
            else
            {
                context.Renderer.Table(["Khoá", "Giá trị"], entries.Select(e => Row(e, withOrigin: false)).ToList());
            }
        }

        return ExitCodes.Ok;
    }

    private static int Set(CommandContext context, string key, string value, bool project)
    {
        var scope = project ? ConfigScope.Project : ConfigScope.User;
        var useCase = context.Get<ConfigUseCase>();
        useCase.Set(scope, key, value, context.ProjectRoot);
        var paths = useCase.Paths(context.ProjectRoot);
        var file = project ? paths.ProjectFile! : paths.UserFile;
        context.Renderer.Json(new { key, value, file });
        context.Renderer.Ok($"Đã ghi {key} = {value} vào {file}");
        return ExitCodes.Ok;
    }

    private static int Unset(CommandContext context, string key, bool project)
    {
        var scope = project ? ConfigScope.Project : ConfigScope.User;
        var removed = context.Get<ConfigUseCase>().Unset(scope, key, context.ProjectRoot);
        context.Renderer.Json(new { key, removed });
        if (removed)
        {
            context.Renderer.Ok($"Đã xoá {key}");
        }
        else
        {
            context.Renderer.Warn($"{key} không được đặt ở tầng này");
        }

        return ExitCodes.Ok;
    }

    private static int Path(CommandContext context)
    {
        var paths = context.Get<ConfigUseCase>().Paths(context.ProjectRoot);
        context.Renderer.Json(new { user = paths.UserFile, project = paths.ProjectFile });
        context.Renderer.Line("Người dùng: " + paths.UserFile);
        if (paths.ProjectFile is not null)
        {
            context.Renderer.Line("Project:    " + paths.ProjectFile);
        }

        return ExitCodes.Ok;
    }

    private static IReadOnlyList<string> Row(ConfigEntry entry, bool withOrigin) => withOrigin
        ? [entry.Key, Display(entry.ValueJson), ArrowJson.Serialize(entry.Origin).Trim('"')]
        : [entry.Key, Display(entry.ValueJson)];

    /// <summary>Strings without quotes; everything else as JSON.</summary>
    private static string Display(string valueJson) =>
        JsonNode.Parse(valueJson) is JsonValue value && value.TryGetValue<string>(out var text) ? text : valueJson;
}
