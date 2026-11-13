using System.CommandLine;
using Arrow.Business.UseCases;
using Arrow.Cli.Prompts;
using Arrow.Common.Errors;

namespace Arrow.Cli.Commands;

/// <summary>
/// <c>arrow setup</c> (P08 §2.5), the model part (GĐ3): provider, key, model, a trial call, the privacy notice, a review, then <c>arrow doctor</c>.
/// The Java and tools steps (4 and 5) are named but come with GĐ5 and GĐ8; <c>doctor</c> shows the JDKs and Maven/Gradle found.
/// </summary>
internal static class SetupCommand
{
    public static Command Build(CliApp app)
    {
        var command = new Command("setup", "Thiết lập lần đầu: nhà cung cấp model, khoá API, model, quyền riêng tư");
        command.SetAction((parse, ct) => app.Execute(parse, ct, RunAsync));
        return command;
    }

    private static async Task<int> RunAsync(CommandContext context, CancellationToken ct)
    {
        if (!context.Console.HasKeyboard || context.Renderer.Mode == Rendering.RenderMode.Json)
        {
            throw new ArrowException(ErrorCodes.CliNoTty, "arrow setup cần bàn phím. Không có bàn phím thì dùng: arrow config set llm.providers.<tên> '<json>', arrow config set llm.models.<tên> '<json>', arrow config set llm.default_model <tên> và arrow secrets set <tên> --stdin");
        }

        var renderer = context.Renderer;
        renderer.Line("ARROW setup. Mỗi bước nói rõ nó làm gì; gõ < hoặc chọn \"Quay lại\" để về bước trước.");
        var draft = await new ModelWizard(context).CollectAsync(ct).ConfigureAwait(false);
        if (draft is null)
        {
            renderer.Warn("Đã huỷ, chưa lưu gì.");
            return ExitCodes.Cancelled;
        }

        var ask = context.Get<IWizardPrompter>();
        var local = draft.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) || draft.BaseUrl.Contains("127.0.0.1", StringComparison.Ordinal);
        renderer.Line("Bước 3. Quyền riêng tư: mỗi lượt gọi gửi tới nhà cung cấp mã nguồn của class cần test, thông tin project (tên dependency, đường dẫn, phiên bản Java), mã test và lỗi build.");
        renderer.Line(local
            ? "Model chạy trên máy này (Ollama): mã không rời khỏi máy."
            : $"Nhà cung cấp hiện chọn: {new Uri(draft.BaseUrl).Host}. ARROW không gửi khoá API, biến môi trường, tên máy hay tên người dùng.");
        if (!ask.Confirm("Đồng ý?", true))
        {
            renderer.Warn("Không đồng ý nên chưa lưu gì.");
            return ExitCodes.Cancelled;
        }

        renderer.Line("Bước 4 (Java và build) và bước 5 (công cụ đo): ARROW tự tìm JDK, Maven và Gradle; kết quả nằm ở arrow doctor bên dưới. Đặt đường dẫn riêng bằng: arrow config set java.jdk_homes.17 <đường dẫn>.");

        renderer.Table(
            ["Mục", "Giá trị"],
            [
                ["Nhà cung cấp", $"{draft.ProviderName} ({draft.BaseUrl})"],
                ["Khoá", draft.ApiKey is { } key ? $"{draft.SecretRef} ({key.Hint})" : draft.SecretRef is null ? "không cần" : $"{draft.SecretRef} (giữ khoá đã có)"],
                ["Model", $"{draft.ModelName} → {draft.ModelId}"],
                ["Giá (vào / ra)", draft.Price is { } price ? $"{price.Input} / {price.Output}" : "-"],
                ["Kiểm tra model id", draft.ExpectedPrefix ?? "không"],
                ["Mặc định", draft.MakeDefault ? "có" : "không"],
            ]);
        if (!ask.Confirm("Lưu?", true))
        {
            renderer.Warn("Chưa lưu gì.");
            return ExitCodes.Cancelled;
        }

        var models = context.Get<IModelsUseCase>();
        models.Save(draft, context.ProjectRoot);
        renderer.Ok($"Đã lưu vào {context.Get<ConfigUseCase>().Paths(context.ProjectRoot).UserFile}{(draft.ApiKey is null ? "" : " và kho khoá")}");

        return await DoctorCommand.RunAsync(context, ct).ConfigureAwait(false);
    }
}
