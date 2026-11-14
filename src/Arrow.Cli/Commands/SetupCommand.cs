using System.CommandLine;
using Arrow.Business.UseCases;
using Arrow.Cli.Prompts;
using Arrow.Common.Errors;

namespace Arrow.Cli.Commands;

/// <summary>
/// <c>arrow setup</c> (P08 §2.5): 1–2 provider, key and model with a trial call, 3 the privacy notice, 4 the JDKs and Maven/Gradle found
/// (the user may correct a path), 5 the measuring tools; then a review, <c>Lưu?</c>, the download of the tools and <c>arrow doctor</c>.
/// "Quay lại" goes to the previous step. Nothing is written before <c>Lưu?</c>.
/// </summary>
internal static class SetupCommand
{
    private enum Step
    {
        Model,
        Privacy,
        Machine,
        Tools,
        Review,
    }

    public static Command Build(CliApp app)
    {
        var command = new Command("setup", "Thiết lập lần đầu: model, khoá API, quyền riêng tư, JDK và Maven/Gradle, công cụ đo");
        command.SetAction((parse, ct) => app.Execute(parse, ct, RunAsync));
        return command;
    }

    public static async Task<int> RunAsync(CommandContext context, CancellationToken ct)
    {
        if (!context.Console.HasKeyboard || context.Renderer.Mode == Rendering.RenderMode.Json)
        {
            throw new ArrowException(ErrorCodes.CliNoTty, "arrow setup cần bàn phím. Không có bàn phím thì dùng: arrow config set llm.providers.<tên> '<json>', arrow config set llm.models.<tên> '<json>', arrow config set llm.default_model <tên> và arrow secrets set <tên> --stdin");
        }

        var renderer = context.Renderer;
        var ask = context.Get<IWizardPrompter>();
        renderer.Line("ARROW setup. Mỗi bước nói rõ nó làm gì; gõ < hoặc chọn \"Quay lại\" để về bước trước.");

        ModelDraft? draft = null;
        var choice = MachineChoice.None;
        var download = false;
        var missingTools = 0;
        var step = Step.Model;
        while (step != Step.Review)
        {
            switch (step)
            {
                case Step.Model:
                    draft = await new ModelWizard(context).CollectAsync(ct).ConfigureAwait(false);
                    if (draft is null)
                    {
                        renderer.Warn("Đã huỷ, chưa lưu gì.");
                        return ExitCodes.Cancelled;
                    }

                    step = Step.Privacy;
                    break;
                case Step.Privacy:
                    if (!Privacy(context, draft!))
                    {
                        renderer.Warn("Không đồng ý nên chưa lưu gì.");
                        return ExitCodes.Cancelled;
                    }

                    step = Step.Machine;
                    break;
                case Step.Machine:
                    var chosen = await MachineAsync(context, choice, ct).ConfigureAwait(false);
                    (choice, step) = chosen is null ? (choice, Step.Privacy) : (chosen, Step.Tools);
                    break;
                case Step.Tools:
                    missingTools = context.Get<IToolsUseCase>().List().Count(r => !r.Installed);
                    var answer = Tools(context, missingTools);
                    (download, step) = answer is null ? (download, Step.Machine) : (answer.Value, Step.Review);
                    break;
            }
        }

        renderer.Table(
            ["Mục", "Giá trị"],
            [
                ["Nhà cung cấp", $"{draft!.ProviderName} ({draft.BaseUrl})"],
                ["Khoá", draft.ApiKey is { } key ? $"{draft.SecretRef} ({key.Hint})" : draft.SecretRef is null ? "không cần" : $"{draft.SecretRef} (giữ khoá đã có)"],
                ["Model", $"{draft.ModelName} → {draft.ModelId}"],
                ["Giá (vào / ra)", draft.Price is { } price ? $"{price.Input} / {price.Output}" : "-"],
                ["Kiểm tra model id", draft.ExpectedPrefix ?? "không"],
                ["Mặc định", draft.MakeDefault ? "có" : "không"],
                ["JDK, Maven, Gradle", choice.IsEmpty ? "ARROW tự tìm" : string.Join(", ", ChoiceText(choice))],
                ["Công cụ đo", missingTools == 0 ? "đã có đủ" : download ? $"tải {missingTools} file sau khi lưu" : "để sau (arrow tools install)"],
            ]);
        if (!ask.Confirm("Lưu?", true))
        {
            renderer.Warn("Chưa lưu gì.");
            return ExitCodes.Cancelled;
        }

        context.Get<IModelsUseCase>().Save(draft, context.ProjectRoot);
        context.Get<IMachineSetupUseCase>().Save(choice, context.ProjectRoot);
        renderer.Ok($"Đã lưu vào {context.Get<ConfigUseCase>().Paths(context.ProjectRoot).UserFile}{(draft.ApiKey is null ? "" : " và kho khoá")}");
        if (download)
        {
            // A failed download is reported here and again by doctor; it does not undo the saved settings.
            await ToolsCommand.InstallAsync(context, [], ct).ConfigureAwait(false);
        }

        return await DoctorCommand.RunAsync(context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The first <c>arrow</c> or <c>arrow gen</c> without a user <c>config.json</c> (P08 §2.5): asks <c>Chưa thiết lập. Chạy arrow setup
    /// ngay?</c> and runs it. Null when nothing was asked or the user said no; otherwise the exit code of <c>setup</c>.
    /// </summary>
    public static async Task<int?> OfferFirstRunAsync(CommandContext context, CancellationToken ct)
    {
        if (!context.Console.HasKeyboard || context.Renderer.Mode == Rendering.RenderMode.Json || context.Globals.Yes
            || File.Exists(context.Get<ConfigUseCase>().Paths(context.ProjectRoot).UserFile))
        {
            return null;
        }

        return context.Get<IWizardPrompter>().Confirm("Chưa thiết lập. Chạy arrow setup ngay?", true)
            ? await RunAsync(context, ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>Step 3: what leaves the machine; false when the user does not agree.</summary>
    private static bool Privacy(CommandContext context, ModelDraft draft)
    {
        var renderer = context.Renderer;
        var local = draft.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) || draft.BaseUrl.Contains("127.0.0.1", StringComparison.Ordinal);
        renderer.Line("Bước 3. Quyền riêng tư: mỗi lượt gọi gửi tới nhà cung cấp mã nguồn của class cần test, thông tin project (tên dependency, đường dẫn, phiên bản Java), mã test và lỗi build.");
        renderer.Line(local
            ? "Model chạy trên máy này (Ollama): mã không rời khỏi máy."
            : $"Nhà cung cấp hiện chọn: {new Uri(draft.BaseUrl).Host}. ARROW không gửi khoá API, biến môi trường, tên máy hay tên người dùng.");
        return context.Get<IWizardPrompter>().Confirm("Đồng ý?", true);
    }

    /// <summary>Step 4: the JDKs and build tools found; the user may correct a path. Null to go back.</summary>
    private static async Task<MachineChoice?> MachineAsync(CommandContext context, MachineChoice choice, CancellationToken ct)
    {
        var renderer = context.Renderer;
        var ask = context.Get<IWizardPrompter>();
        var machine = context.Get<IMachineSetupUseCase>();
        renderer.Line("Bước 4. Java và build: ARROW build project của anh bằng JDK đúng phiên bản (8, 11, 17, 21) và Maven hoặc Gradle. Đây là những gì tìm được trên máy.");
        while (true)
        {
            renderer.Step("Đang tìm JDK, Maven và Gradle…");
            var found = await machine.FindAsync(choice, context.ProjectRoot, ct).ConfigureAwait(false);
            var rows = found.Jdks
                .Select(j => (IReadOnlyList<string>)[$"JDK {j.Major}", j.Version ?? "—", j.Home is null ? "không tìm thấy" : $"{j.Home} ({j.Source})"])
                .Append(["Maven", "", found.Maven ?? "không tìm thấy"])
                .Append(["Gradle", "", found.Gradle ?? "không tìm thấy"])
                .ToList();
            renderer.Table(["Mục", "Phiên bản", "Đường dẫn"], rows);
            foreach (var broken in found.Broken)
            {
                renderer.Warn($"{broken}: không chạy được java -version, ARROW bỏ qua đường dẫn này.");
            }

            if (found.Maven is null && found.Gradle is null)
            {
                renderer.Warn("Không có Maven hay Gradle: chỉ project có wrapper (mvnw, gradlew) build được.");
            }

            string[] options = ["Dùng như trên", .. found.Jdks.Select(j => $"Sửa JDK {j.Major}"), "Sửa Maven", "Sửa Gradle"];
            var picked = ask.Select("Java và build", options, canGoBack: true);
            if (picked < 0)
            {
                return null;
            }

            if (picked == 0)
            {
                return choice;
            }

            var isJdk = picked <= found.Jdks.Count;
            var label = isJdk ? $"JDK {found.Jdks[picked - 1].Major}" : picked == found.Jdks.Count + 1 ? "Maven" : "Gradle";
            var path = ask.Ask(isJdk ? $"Thư mục của {label} (thư mục chứa bin/java)" : $"Đường dẫn file chạy {label} (ví dụ …/bin/{label.ToLowerInvariant()})");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            path = Path.GetFullPath(path.Trim().Trim('"'));
            if (isJdk ? !Directory.Exists(path) : !File.Exists(path))
            {
                renderer.Warn($"Không thấy {path}.");
                continue;
            }

            choice = isJdk
                ? choice with { JdkHomes = new Dictionary<string, string>(choice.JdkHomes) { [found.Jdks[picked - 1].Major.ToString(System.Globalization.CultureInfo.InvariantCulture)] = path } }
                : label == "Maven" ? choice with { MavenPath = path } : choice with { GradlePath = path };
        }
    }

    /// <summary>Step 5: whether to download the measuring tools after the save; null to go back.</summary>
    private static bool? Tools(CommandContext context, int missing)
    {
        if (missing == 0)
        {
            context.Renderer.Ok("Bước 5. Công cụ đo (JaCoCo, PIT, tsDetect): đã có đủ.");
            return false;
        }

        context.Renderer.Line($"Bước 5. Công cụ đo: JaCoCo, PIT và tsDetect đo độ phủ, mutation và test smell của test sinh ra. ARROW tải {missing} file từ Maven Central (hoặc ARROW_MAVEN_MIRROR) và kiểm sha256.");
        return context.Get<IWizardPrompter>().Select("Tải công cụ đo?", ["Tải ngay sau khi lưu", "Để sau (arrow tools install)"], canGoBack: true) switch
        {
            < 0 => null,
            0 => true,
            _ => false,
        };
    }

    private static IEnumerable<string> ChoiceText(MachineChoice choice) =>
        choice.JdkHomes.OrderBy(j => j.Key.Length).ThenBy(j => j.Key, StringComparer.Ordinal).Select(j => $"JDK {j.Key} = {j.Value}")
            .Concat(choice.MavenPath is { } m ? [$"Maven = {m}"] : [])
            .Concat(choice.GradlePath is { } g ? [$"Gradle = {g}"] : []);
}
