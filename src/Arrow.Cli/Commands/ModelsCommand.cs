using System.CommandLine;
using System.Globalization;
using Arrow.Business.UseCases;
using Arrow.Common.Errors;

namespace Arrow.Cli.Commands;

/// <summary><c>arrow models</c>, <c>models test [name]</c>, <c>models add</c> and <c>models default &lt;name&gt;</c> (P03 §10).</summary>
internal static class ModelsCommand
{
    public static Command Build(CliApp app)
    {
        var command = new Command("models", "Các model đã cấu hình: xem, thử, thêm, đặt mặc định (P03 §10)");
        command.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(List(context))));

        var testName = new Argument<string?>("name") { Description = "Tên model (mặc định: model mặc định)", Arity = ArgumentArity.ZeroOrOne };
        var test = new Command("test", "Gửi \"Reply with exactly: OK\" (tối đa 16 token) và in kết quả") { testName };
        test.SetAction((parse, ct) => app.Execute(parse, ct, (context, token) => TestAsync(context, parse.GetValue(testName), token)));

        var add = new Command("add", "Thêm một model: nhà cung cấp, khoá, model_id (như bước 1–2 của arrow setup)");
        add.SetAction((parse, ct) => app.Execute(parse, ct, AddAsync));

        var defaultName = new Argument<string>("name") { Description = "Tên model cần đặt làm mặc định" };
        var setDefault = new Command("default", "Đặt llm.default_model") { defaultName };
        setDefault.SetAction((parse, ct) => app.Execute(parse, ct, (context, _) => Task.FromResult(SetDefault(context, parse.GetValue(defaultName)!))));

        command.Subcommands.Add(test);
        command.Subcommands.Add(add);
        command.Subcommands.Add(setDefault);
        return command;
    }

    private static int List(CommandContext context)
    {
        var listing = context.Get<IModelsUseCase>().List(context.ProjectRoot);
        context.Renderer.Json(new { default_model = listing.DefaultModel, models = listing.Rows });
        if (context.Renderer.Mode != Rendering.RenderMode.Json)
        {
            if (listing.Rows.Count == 0)
            {
                context.Renderer.Line("Chưa có model nào. Chạy: arrow setup");
            }
            else
            {
                context.Renderer.Table(
                    ["Tên", "Provider", "Host", "model_id", "Tham số", "Giá (vào / ra)", "Khoá", "Mặc định"],
                    listing.Rows.Select(r => (IReadOnlyList<string>)[r.Name, r.Provider, r.Host, r.ModelId, r.Params, r.Price ?? "-", r.Key, r.IsDefault ? "✓" : ""]).ToList());
            }
        }

        return ExitCodes.Ok;
    }

    private static async Task<int> TestAsync(CommandContext context, string? name, CancellationToken ct)
    {
        var result = await context.Get<IModelsUseCase>().TestAsync(name, context.ProjectRoot, ct).ConfigureAwait(false);
        context.Renderer.Json(new { result.Model, provider_host = result.ProviderHost, result.Content, model_id = result.ModelId, expected_prefix = result.ExpectedPrefix, prefix_matches = result.PrefixMatches, result.PromptTokens, result.CompletionTokens, result.Seconds, result.CostUsd });
        if (context.Renderer.Mode != Rendering.RenderMode.Json)
        {
            context.Renderer.Line($"Model {result.Model} ({result.ProviderHost})");
            new ModelWizard(context).Show(result);
        }

        // A model id that does not match the prefix is a failed check, as in doctor (exit 2).
        return result.PrefixMatches == false ? ExitCodes.Usage : ExitCodes.Ok;
    }

    private static async Task<int> AddAsync(CommandContext context, CancellationToken ct)
    {
        if (!context.Console.HasKeyboard || context.Renderer.Mode == Rendering.RenderMode.Json)
        {
            throw new ArrowException(ErrorCodes.CliNoTty, "arrow models add cần bàn phím. Không có bàn phím thì dùng: arrow config set llm.providers.<tên> '<json>', arrow config set llm.models.<tên> '<json>' và arrow secrets set <tên> --stdin");
        }

        var draft = await new ModelWizard(context).CollectAsync(ct).ConfigureAwait(false);
        if (draft is null)
        {
            context.Renderer.Warn("Đã huỷ, chưa lưu gì.");
            return ExitCodes.Cancelled;
        }

        context.Get<IModelsUseCase>().Save(draft, context.ProjectRoot);
        context.Renderer.Ok($"Đã lưu model {draft.ModelName}{(draft.MakeDefault ? " (mặc định)" : "")}{(draft.ApiKey is { } key ? $" và khoá {draft.SecretRef} ({key.Hint})" : "")}");
        return ExitCodes.Ok;
    }

    private static int SetDefault(CommandContext context, string name)
    {
        context.Get<IModelsUseCase>().SetDefault(name, context.ProjectRoot);
        context.Renderer.Json(new { default_model = name });
        context.Renderer.Ok($"Model mặc định: {name}");
        return ExitCodes.Ok;
    }

    /// <summary>Formats a cost for the table of results.</summary>
    internal static string Usd(double? cost) => cost is { } value ? value.ToString("0.########", CultureInfo.InvariantCulture) + " USD" : "chưa có giá";
}
