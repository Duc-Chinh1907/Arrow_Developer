using System.Text.Json.Nodes;
using Arrow.Business.UseCases;
using Arrow.Cli.Prompts;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace Arrow.Cli.Tests;

/// <summary><c>arrow models</c> and <c>arrow setup</c> (P03 §10, P08 §2.5), with a fake use case and a scripted prompter.</summary>
public class ModelsSetupCommandTests
{
    private sealed class FakeModels : IModelsUseCase
    {
        public ModelsListing Listing { get; set; } = new(
            [new ModelRow("gpt-x", "teamo", "api.teamorouter.com", "gpt-4.1-mini", "temperature=0, max_tokens=16384", "0.4 / 1.6", "...2a4b", true)], "gpt-x");

        public ModelTestResult Result { get; set; } = new("gpt-x", "api.teamorouter.com", "OK", "gpt-4.1-mini-2025-04-14", "gpt-4.1-mini", true, 12, 1, 0.8, 0.0000064);

        public Exception? TestFails { get; set; }

        public string? TestedName { get; private set; }

        public ModelDraft? Saved { get; private set; }

        public ModelDraft? Drafted { get; private set; }

        public string? DefaultSet { get; private set; }

        public ModelsListing List(string? projectRoot) => Listing;

        public Task<ModelTestResult> TestAsync(string? name, string? projectRoot, CancellationToken ct)
        {
            TestedName = name;
            return TestFails is null ? Task.FromResult(Result) : throw TestFails;
        }

        public Task<ModelTestResult> TestDraftAsync(ModelDraft draft, CancellationToken ct)
        {
            Drafted = draft;
            return TestFails is null ? Task.FromResult(Result) : throw TestFails;
        }

        public void Save(ModelDraft draft, string? projectRoot) => Saved = draft;

        public void SetDefault(string name, string? projectRoot) => DefaultSet = name;
    }

    /// <summary>Answers questions from a script, in order; a leftover or missing answer fails the test.</summary>
    private sealed class Scripted(params object?[] answers) : IWizardPrompter
    {
        private readonly Queue<object?> _answers = new(answers);

        public List<string> Questions { get; } = [];

        private T Next<T>(string question)
        {
            Questions.Add(question);
            return _answers.Count > 0 ? (T)_answers.Dequeue()! : throw new InvalidOperationException("No scripted answer for: " + question);
        }

        public string? Ask(string question, string? defaultValue = null)
        {
            var answer = Next<string?>(question);
            return answer == "" ? defaultValue ?? "" : answer;
        }

        public AskedSecret AskSecret(string question) => Next<AskedSecret>(question);

        public int Select(string question, IReadOnlyList<string> options, bool canGoBack) => Next<int>(question);

        public bool Confirm(string question, bool defaultValue) => Next<bool>(question);

        public int Remaining => _answers.Count;
    }

    private static CliRunner Cli(
        FakeModels models, IWizardPrompter? prompter = null, bool interactive = false, IDoctorUseCase? doctor = null, FakeMachine? machine = null, FakeTools? tools = null)
    {
        var cli = new CliRunner { Interactive = interactive };
        cli.Services = services =>
        {
            services.AddSingleton<IModelsUseCase>(models);
            if (prompter is not null)
            {
                services.AddSingleton(prompter);
            }

            services.AddSingleton(doctor ?? new FakeDoctor());
            services.AddSingleton<IMachineSetupUseCase>(machine ?? new FakeMachine());
            services.AddSingleton<IToolsUseCase>(tools ?? new FakeTools(missing: 0));
        };
        return cli;
    }

    private sealed class FakeMachine : IMachineSetupUseCase
    {
        public List<MachineChoice> Found { get; } = [];

        public MachineChoice? Saved { get; private set; }

        public Task<MachineFound> FindAsync(MachineChoice choice, string? projectRoot, CancellationToken ct)
        {
            Found.Add(choice);
            var jdks = new[] { 8, 11, 17, 21 }
                .Select(m => choice.JdkHomes.TryGetValue(m.ToString(System.Globalization.CultureInfo.InvariantCulture), out var home)
                    ? new MachineJdk(m, m + ".0.1", home, "config")
                    : m == 17 ? new MachineJdk(17, "17.0.12", "/opt/jdk-17", "JAVA_HOME") : new MachineJdk(m, null, null, null))
                .ToList();
            return Task.FromResult(new MachineFound(jdks, choice.MavenPath ?? "/usr/bin/mvn", null, []));
        }

        public void Save(MachineChoice choice, string? projectRoot) => Saved = choice;
    }

    private sealed class FakeTools(int missing) : IToolsUseCase
    {
        public int Installs { get; private set; }

        public IReadOnlyList<ToolRow> List() =>
            [.. Enumerable.Range(0, 12).Select(i => new ToolRow("tool" + i, "central", "/tools/" + i, i >= missing, null))];

        public Task<IReadOnlyList<ToolInstallResult>> InstallAsync(IReadOnlyList<string> ids, IProgress<string>? progress, CancellationToken ct)
        {
            Installs++;
            return Task.FromResult<IReadOnlyList<ToolInstallResult>>([new ToolInstallResult("tool0", "/tools/0", null)]);
        }
    }

    private sealed class FakeDoctor : IDoctorUseCase
    {
        public Task<DoctorReport> RunAsync(string? projectRoot, Action<DoctorItem>? onItem, CancellationToken ct)
        {
            var item = new DoctorItem(DoctorLevel.Ok, "git", "2.46.0", null);
            onItem?.Invoke(item);
            return Task.FromResult(new DoctorReport([item]));
        }
    }

    // ── models ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ModelsPrintsTheTable()
    {
        using var cli = Cli(new FakeModels());
        var run = await cli.RunAsync("models");
        Assert.Equal(0, run.ExitCode);
        foreach (var text in new[] { "Tên", "Provider", "model_id", "Khoá", "Mặc định", "gpt-x", "api.teamorouter.com", "gpt-4.1-mini", "0.4 / 1.6", "...2a4b", "✓" })
        {
            Assert.Contains(text, run.Out, StringComparison.Ordinal);
        }

        var json = JsonNode.Parse((await cli.RunAsync("models", "--json")).Out)!;
        Assert.Equal("gpt-x", (string?)json["default_model"]);
        Assert.Equal("...2a4b", (string?)json["models"]![0]!["key"]);
    }

    [Fact]
    public async Task ModelsWithNoneSaysHowToAddOne()
    {
        using var cli = Cli(new FakeModels { Listing = new ModelsListing([], null) });
        Assert.Contains("arrow setup", (await cli.RunAsync("models")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsTestPrintsTheResultOfP03()
    {
        var models = new FakeModels();
        using var cli = Cli(models);
        var run = await cli.RunAsync("models", "test", "gpt-x");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("gpt-x", models.TestedName);
        Assert.Contains("[OK] model id trả về: gpt-4.1-mini-2025-04-14 (khớp tiền tố)", run.Out, StringComparison.Ordinal);
        Assert.Contains("Trả lời: OK", run.Out, StringComparison.Ordinal);
        Assert.Contains("Token: 12 vào, 1 ra · 0.8 s · 0.0000064 USD", run.Out, StringComparison.Ordinal);

        await cli.RunAsync("models", "test");
        Assert.Null(models.TestedName);
    }

    [Fact]
    public async Task ModelsTestWithAWrongModelIdFailsTheCheck()
    {
        using var cli = Cli(new FakeModels { Result = new ModelTestResult("gpt-x", "h", "OK", "gpt-3.5-turbo", "gpt-4.1-mini", false, 1, 1, 0.1, null) });
        var run = await cli.RunAsync("models", "test");
        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("[X] model id trả về: gpt-3.5-turbo, không bắt đầu bằng gpt-4.1-mini", run.Out, StringComparison.Ordinal);
        Assert.Contains("chưa có giá", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsTestErrorsPrintTheCodeAndTheHint()
    {
        using var cli = Cli(new FakeModels { TestFails = new ArrowException(ErrorCodes.B2LlmConfig, "HTTP 401: Incorrect API key. Khoá API sai hoặc hết hạn: arrow secrets set teamo") });
        var run = await cli.RunAsync("models", "test");
        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("B2_LLM_CONFIG", run.Out, StringComparison.Ordinal);
        Assert.Contains("arrow secrets set teamo", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsDefaultSetsIt()
    {
        var models = new FakeModels();
        using var cli = Cli(models);
        var run = await cli.RunAsync("models", "default", "claude");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("claude", models.DefaultSet);
    }

    // ── setup and models add ────────────────────────────────────────────────────

    [Fact]
    public async Task SetupAndAddNeedAKeyboard()
    {
        using var cli = Cli(new FakeModels());
        foreach (var args in new[] { new[] { "setup" }, ["models", "add"] })
        {
            var run = await cli.RunAsync(args);
            Assert.Equal(ExitCodes.Usage, run.ExitCode);
            Assert.Contains("CLI_NO_TTY", run.Out, StringComparison.Ordinal);
            Assert.Contains("arrow secrets set", run.Out, StringComparison.Ordinal);
        }
    }

    // OpenAI-compatible (index 1), URL, provider name, key, model_id, short name, price, prefix, trial call yes, default yes.
    private static object?[] CustomProviderAnswers(string key = "sk-test-SECRET-123") =>
    [
        1, "https://api.teamorouter.com/v1", "teamo", new AskedSecret(new SecretValue(key), false),
        "gpt-4.1-mini", "gpt-x", "0.4,1.6", "gpt-4.1-mini", true, true,
    ];

    [Fact]
    public async Task SetupCollectsTheModelTriesItSavesAndRunsDoctor()
    {
        var models = new FakeModels();
        var prompter = new Scripted([.. CustomProviderAnswers(), true, 0, true]);   // + privacy "Đồng ý?", "Dùng như trên" and "Lưu?"
        using var cli = Cli(models, prompter, interactive: true);

        var run = await cli.RunAsync("setup");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0, prompter.Remaining);
        var draft = models.Saved!;
        Assert.Equal(("teamo", ProviderKind.OpenAiCompatible, "https://api.teamorouter.com/v1", "teamo"), (draft.ProviderName, draft.Kind, draft.BaseUrl, draft.SecretRef));
        Assert.Equal(("gpt-x", "gpt-4.1-mini", new ModelPrice(0.4, 1.6), "gpt-4.1-mini", true), (draft.ModelName, draft.ModelId, draft.Price, draft.ExpectedPrefix, draft.MakeDefault));
        Assert.Equal("sk-test-SECRET-123", draft.ApiKey!.Reveal());
        Assert.Equal((draft.ModelName, draft.ModelId), (models.Drafted!.ModelName, models.Drafted.ModelId));
        // The trial happened before the save; the key never reaches the screen.
        Assert.DoesNotContain("SECRET", run.All, StringComparison.Ordinal);
        Assert.Contains("Bước 3. Quyền riêng tư", run.Out, StringComparison.Ordinal);
        Assert.Contains("api.teamorouter.com", run.Out, StringComparison.Ordinal);
        Assert.Contains("Đã lưu vào", run.Out, StringComparison.Ordinal);
        Assert.Contains("Kiểm tra môi trường", run.Out, StringComparison.Ordinal);
        Assert.Contains("[OK] git", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupWithOllamaNeedsNoKeyAndSaysTheCodeStaysOnTheMachine()
    {
        var models = new FakeModels();
        // Ollama is preset 3: URL (default), model_id, short name (default), price none, prefix "-", trial no, default yes, privacy yes,
        // machine as found, save yes.
        var prompter = new Scripted(3, "", "qwen3-coder:30b", "", "", "-", false, true, true, 0, true);
        using var cli = Cli(models, prompter, interactive: true);

        var run = await cli.RunAsync("setup");

        Assert.Equal(0, run.ExitCode);
        var draft = models.Saved!;
        Assert.Equal(("ollama", "http://localhost:11434/v1", null, null), (draft.ProviderName, draft.BaseUrl, draft.SecretRef, draft.ApiKey));
        Assert.Equal(("qwen3-coder-30b", "qwen3-coder:30b", null), (draft.ModelName, draft.ModelId, draft.ExpectedPrefix));
        Assert.Contains("mã không rời khỏi máy", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(prompter.Questions, q => q.StartsWith("Khoá API", StringComparison.Ordinal));
        Assert.Null(models.Drafted);
    }

    [Fact]
    public async Task DecliningThePrivacyNoticeOrTheSaveWritesNothing()
    {
        var declinePrivacy = new FakeModels();
        using var cli1 = Cli(declinePrivacy, new Scripted([.. CustomProviderAnswers(), false]), interactive: true);
        var privacy = await cli1.RunAsync("setup");
        Assert.Equal(ExitCodes.Cancelled, privacy.ExitCode);
        Assert.Null(declinePrivacy.Saved);

        var declineSave = new FakeModels();
        using var cli2 = Cli(declineSave, new Scripted([.. CustomProviderAnswers(), true, 0, false]), interactive: true);
        Assert.Equal(ExitCodes.Cancelled, (await cli2.RunAsync("setup")).ExitCode);
        Assert.Null(declineSave.Saved);
    }

    [Fact]
    public async Task ABadTrialCanStillBeSavedOnRequest()
    {
        var models = new FakeModels { TestFails = new ArrowException(ErrorCodes.B2LlmConfig, "HTTP 401: bad key") };
        // Same answers, but the trial fails: "Vẫn lưu model này?" → yes, then the default question.
        var answers = new object?[] { 1, "https://api.teamorouter.com/v1", "teamo", new AskedSecret(new SecretValue("sk-bad"), false), "gpt-4.1-mini", "gpt-x", "", "", true, true, true, true, 0, true };
        using var cli = Cli(models, new Scripted(answers), interactive: true);

        var run = await cli.RunAsync("setup");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("B2_LLM_CONFIG", run.Out, StringComparison.Ordinal);
        Assert.NotNull(models.Saved);
        Assert.Null(models.Saved!.Price);
    }

    [Fact]
    public async Task GoingBackFromTheModelStepAsksForTheProviderAgain()
    {
        var models = new FakeModels();
        // First pass: provider, URL, name, key, then "<" (back) at model_id; second pass: Anthropic and a full model.
        var answers = new object?[]
        {
            1, "https://a.example/v1", "a", new AskedSecret(new SecretValue("sk-first-key-1"), false), null,
            2, "https://api.anthropic.com", new AskedSecret(new SecretValue("sk-ant-test-key"), false), "claude-sonnet-4-5", "claude", "", "-", false, true,
            true, 0, true,
        };
        using var cli = Cli(models, new Scripted(answers), interactive: true);

        var run = await cli.RunAsync("setup");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(("anthropic", ProviderKind.Anthropic, "claude"), (models.Saved!.ProviderName, models.Saved.Kind, models.Saved.ModelName));
    }

    [Fact]
    public async Task SetupLetsTheUserFixAJdkAndDownloadsTheToolsAfterTheSave()
    {
        var models = new FakeModels();
        var machine = new FakeMachine();
        var tools = new FakeTools(missing: 3);
        using var cli = Cli(models, null, interactive: true, machine: machine, tools: tools);
        var jdk8 = Directory.CreateDirectory(Path.Combine(cli.Home, "jdk-8")).FullName;
        // Step 4: "Sửa JDK 8" (index 1), a missing folder, "Sửa JDK 8" again with a real one, then "Dùng như trên"; step 5: download.
        var prompter = new Scripted([.. CustomProviderAnswers(), true, 1, Path.Combine(cli.Home, "nope"), 1, jdk8, 0, 0, true]);
        cli.Services += services => services.AddSingleton<IWizardPrompter>(prompter);

        var run = await cli.RunAsync("setup");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0, prompter.Remaining);
        Assert.Contains("Bước 4. Java và build", run.Out, StringComparison.Ordinal);
        Assert.Contains("/opt/jdk-17 (JAVA_HOME)", run.Out, StringComparison.Ordinal);
        Assert.Contains("Không thấy", run.Out, StringComparison.Ordinal);
        Assert.Contains("ARROW tải 3 file", run.Out, StringComparison.Ordinal);
        Assert.Contains("tải 3 file sau khi lưu", run.Out, StringComparison.Ordinal);
        Assert.Equal(jdk8, machine.Saved!.JdkHomes["8"]);
        Assert.Equal(jdk8, machine.Found[^1].JdkHomes["8"]);
        Assert.Equal(1, tools.Installs);
        Assert.True(run.Out.IndexOf("Đã lưu vào", StringComparison.Ordinal) < run.Out.IndexOf("[OK] tool0", StringComparison.Ordinal));
        Assert.Contains("Kiểm tra môi trường", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackFromTheJavaStepAsksThePrivacyAgainAndToolsCanWait()
    {
        var machine = new FakeMachine();
        var tools = new FakeTools(missing: 12);
        // Privacy yes, back (-1) at step 4, privacy yes again, as found, then "Để sau" (1), save yes.
        var prompter = new Scripted([.. CustomProviderAnswers(), true, -1, true, 0, 1, true]);
        using var cli = Cli(new FakeModels(), prompter, interactive: true, machine: machine, tools: tools);

        var run = await cli.RunAsync("setup");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(2, prompter.Questions.Count(q => q == "Đồng ý?"));
        Assert.Equal(0, tools.Installs);
        Assert.True(machine.Saved!.IsEmpty);
        Assert.Contains("để sau (arrow tools install)", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFirstGenWithoutConfigOffersSetup()
    {
        var prompter = new Scripted(false);
        using var cli = Cli(new FakeModels(), prompter, interactive: true);

        await cli.RunAsync("gen", "Cart", "--project", cli.Home);

        Assert.Equal(["Chưa thiết lập. Chạy arrow setup ngay?"], prompter.Questions);

        using var yes = Cli(new FakeModels(), new Scripted(), interactive: true);
        var run = await yes.RunAsync("gen", "Cart", "--project", yes.Home, "--yes");
        Assert.DoesNotContain("Chưa thiết lập", run.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsAddSavesWithoutThePrivacyStepOrDoctor()
    {
        var models = new FakeModels();
        using var cli = Cli(models, new Scripted(CustomProviderAnswers()), interactive: true);
        var run = await cli.RunAsync("models", "add");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("gpt-x", models.Saved!.ModelName);
        Assert.DoesNotContain("Quyền riêng tư", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Kiểm tra môi trường", run.Out, StringComparison.Ordinal);
        Assert.Contains("Đã lưu model gpt-x (mặc định) và khoá teamo (...-123)", run.Out.Replace("...SECRET", "...", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFakeModelBannerIsPrintedFirst()
    {
        using var cli = Cli(new FakeModels());
        cli.Environment["ARROW_FAKE_LLM"] = Path.Combine(cli.Home, "rules.json");
        var run = await cli.RunAsync("models");
        Assert.StartsWith("[!] FAKE LLM", run.Out, StringComparison.Ordinal);
        Assert.Contains("rules.json", run.Out, StringComparison.Ordinal);

        using var plain = Cli(new FakeModels());
        Assert.DoesNotContain("FAKE LLM", (await plain.RunAsync("models")).Out, StringComparison.Ordinal);
    }
}
