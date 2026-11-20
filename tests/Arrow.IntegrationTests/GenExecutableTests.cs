using System.Text.Json;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Tools;
using Arrow.IntegrationTests.Fixtures;
using Arrow.IntegrationTests.JavaHelper;

namespace Arrow.IntegrationTests;

/// <summary>
/// Milestone M1 (PLAN GĐ4) on the real executable: <c>arrow gen &lt;class&gt; --no-metrics</c> on <c>maven-verify-junit4</c> with the
/// fake model of P11 §3 and the scenarios of <c>tests/scenarios/</c>. The project copy may change only by the new test file.
/// </summary>
[Trait("Category", "E2E")]
public sealed class GenExecutableTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "arrow-gen-e2e", Guid.NewGuid().ToString("N")[..10]);
    private readonly FixtureRepo _project = new("maven-verify-junit4");

    private const string TestDir = "src/test/java/com/acme/billing";

    public GenExecutableTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        _project.Dispose();
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Scenario(string name) => Path.Combine(Path.GetDirectoryName(FixtureRepo.FixturesDir)!, "scenarios", name, "rules.json");

    /// <summary>Skips without Maven or a JDK for java-helper; configures a model that only the fake client answers.</summary>
    private async Task ConfigureAsync()
    {
        _ = JavaHelperEnvironment.Options();
        Assert.SkipWhen(new ToolLocator(Environment.GetEnvironmentVariable).FindOnPath("mvn") is null, "Maven is not on PATH");
        Assert.Equal(0, (await Arrow(null, null, "config", "set", "llm.providers.local", """{"kind":"openai-compatible","base_url":"http://localhost:9/v1","secret_ref":null}""")).ExitCode);
        Assert.Equal(0, (await Arrow(null, null, "config", "set", "llm.models.fake", """{"provider":"local","model_id":"fake-model"}""")).ExitCode);
        Assert.Equal(0, (await Arrow(null, null, "models", "default", "fake")).ExitCode);

        // Tools already downloaded by other tests are copied in, so that Maven Central is not asked again (it answers 429).
        var cache = Path.Combine(Path.GetTempPath(), "arrow-quality-tools", "tools");
        if (Directory.Exists(cache))
        {
            foreach (var file in Directory.GetFiles(cache, "*.jar", SearchOption.AllDirectories))
            {
                var target = Path.Combine(_home, "tools", Path.GetRelativePath(cache, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }

    private async Task<ProcessResult> Gen(string scenario, string? stdin, params string[] extra) =>
        await Arrow(Scenario(scenario), stdin, ["gen", "PriceCalculator", "--project", _project.Root, "--no-metrics", "--plain", .. extra]);

    private async Task<ProcessResult> Arrow(string? rules, string? stdin, params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Arrow.Cli.dll");
        var environment = new Dictionary<string, string?> { ["NO_COLOR"] = "1", ["ARROW_FAKE_LLM"] = rules };
        var spec = new ProcessSpec("dotnet", [dll, .. args, "--home", _home], _home, Environment: environment, Timeout: TimeSpan.FromMinutes(10), StandardInput: stdin);
        return await new ProcessRunner().RunAsync(spec, TestContext.Current.CancellationToken);
    }

    private string TargetFile(string runDir, string name) => Path.Combine(runDir, "targets", "com.acme.billing.PriceCalculator", name);

    private static string RunDir(ProcessResult result)
    {
        var line = result.Output.Split('\n').Last(l => l.StartsWith("Kết quả lưu ở ", StringComparison.Ordinal));
        return line["Kết quả lưu ở ".Length..].Trim();
    }

    private string[] Files() => Directory.GetFiles(Path.Combine(_project.Root, TestDir.Replace('/', Path.DirectorySeparatorChar))).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    // PLAN GĐ4 "xong khi": the file is created and the build passes; a second run makes _Arrow2Test and leaves the first file alone.
    [Fact]
    public async Task M1WritesAValidTestAndNeverOverwrites()
    {
        await ConfigureAsync();
        var before = FixtureRepo.FingerprintOf(_project.Root);

        var first = await Gen("valid-at-c0", null, "--yes");
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.StartsWith("[!] FAKE LLM", first.Output, StringComparison.Ordinal);
        Assert.Contains("[OK] com.acme.billing.PriceCalculator: VALID", first.Output, StringComparison.Ordinal);
        Assert.Equal(["PriceCalculatorTest.java", "PriceCalculator_ArrowTest.java", "TotalsTest.java"], Files());
        var written = Path.Combine(_project.Root, TestDir.Replace('/', Path.DirectorySeparatorChar), "PriceCalculator_ArrowTest.java");
        var firstContent = await File.ReadAllTextAsync(written, TestContext.Current.CancellationToken);
        Assert.Contains("public class PriceCalculator_ArrowTest", firstContent, StringComparison.Ordinal);

        // Only the new file changed in the project.
        var aside = Path.Combine(_home, "aside.java");
        File.Move(written, aside);
        Assert.Equal(before, FixtureRepo.FingerprintOf(_project.Root));
        File.Move(aside, written);

        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(RunDir(first), "result.json"), TestContext.Current.CancellationToken));
        Assert.Equal("VALID", result.RootElement.GetProperty("result").GetString());
        Assert.Equal(0, result.RootElement.GetProperty("success_round").GetInt32());
        Assert.Equal("written", result.RootElement.GetProperty("apply").GetProperty("decision").GetString());
        using var final = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(RunDir(first), "block3/final.json"), TestContext.Current.CancellationToken));
        Assert.True(final.RootElement.GetProperty("generated_test_ran_in_module").GetBoolean());

        var second = await Gen("valid-at-c0", null, "--yes");
        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Contains("PriceCalculator_Arrow2Test.java", second.Output, StringComparison.Ordinal);
        Assert.Equal(["PriceCalculatorTest.java", "PriceCalculator_Arrow2Test.java", "PriceCalculator_ArrowTest.java", "TotalsTest.java"], Files());
        Assert.Equal(firstContent, await File.ReadAllTextAsync(written, TestContext.Current.CancellationToken));
    }

    // P04 §12, P11 §3.3: the chain of R03 §14.5 through the executable.
    [Fact]
    public async Task TheChainOfSection14_5EndsValidInRoundFive()
    {
        await ConfigureAsync();
        var run = await Gen("o8-o3-o4-o7-o1", null, "--yes");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("vòng 2 · mức 1 → mã trùng vòng 1 · lên mức 2", run.Output, StringComparison.Ordinal);
        Assert.Contains("vòng 5 · mức 2 → VALID", run.Output, StringComparison.Ordinal);

        var dir = RunDir(run);
        var levels = new List<int>();
        for (var k = 1; k <= 5; k++)
        {
            using var decision = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(dir, $"block3/round_{k}/decision.json"), TestContext.Current.CancellationToken));
            levels.Add(decision.RootElement.GetProperty("level").GetInt32());
        }

        Assert.Equal([1, 1, 2, 1, 2], levels);
        using var final = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(dir, "block3/final.json"), TestContext.Current.CancellationToken));
        Assert.Equal((5, "valid", "COMPILE"), (final.RootElement.GetProperty("success_round").GetInt32(), final.RootElement.GetProperty("stop_reason").GetString(), final.RootElement.GetProperty("c0_state").GetString()));

        // P08 §2.7: arrow show prints the run and the rounds of the class; arrow history lists the run.
        var id = Path.GetFileName(dir);
        var show = await Arrow(null, null, "show", id[..15], "--plain");
        Assert.True(show.ExitCode == 0, show.Output);
        Assert.Contains($"Run {id} · COMPLETED", show.Output, StringComparison.Ordinal);
        Assert.Contains("PriceCalculator", show.Output, StringComparison.Ordinal);

        var rounds = await Arrow(null, null, "show", id, "PriceCalculator", "--plain");
        Assert.True(rounds.ExitCode == 0, rounds.Output);
        Assert.Contains("checkpoint = c0", rounds.Output, StringComparison.Ordinal);
        Assert.Contains("mã trùng vòng 1", rounds.Output, StringComparison.Ordinal);
        Assert.Contains("tốt hơn · checkpoint", rounds.Output, StringComparison.Ordinal);
        Assert.Contains("thành công", rounds.Output, StringComparison.Ordinal);

        var history = await Arrow(null, null, "history", "--json");
        using var json = JsonDocument.Parse(history.Output[history.Output.IndexOf('{', StringComparison.Ordinal)..]);
        Assert.Equal(id, json.RootElement.GetProperty("runs")[0].GetProperty("run_id").GetString());
    }

    // P05 §10 "nghiệm thu": without --no-metrics, gen measures the VALID suite and prints line, branch, mutation and smell.
    [Fact]
    public async Task GenMeasuresTheValidSuite()
    {
        await ConfigureAsync();
        var run = await Arrow(Scenario("valid-at-c0"), null, "gen", "PriceCalculator", "--project", _project.Root, "--plain", "--yes");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Matches(@"Chất lượng: line \d+% · branch \d+% · method \d+% · mutation \d+% · test strength \d+% · smell \d", run.Output);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(RunDir(run), "result.json"), TestContext.Current.CancellationToken));
        var tools = result.RootElement.GetProperty("tools");
        Assert.Equal(("OK", "OK", "OK"), (tools.GetProperty("jacoco").GetString(), tools.GetProperty("pit").GetString(), tools.GetProperty("tsdetect").GetString()));
        Assert.True(result.RootElement.GetProperty("metrics").GetProperty("line").GetDouble() > 0);
        Assert.True(File.Exists(TargetFile(RunDir(run), "block4/pit.json")));
    }

    // --no-pit (Chính, 15/11/2026): JaCoCo and tsDetect are measured, PIT is not run at all.
    [Fact]
    public async Task GenWithoutPitMeasuresTheOtherTools()
    {
        await ConfigureAsync();
        var run = await Arrow(Scenario("valid-at-c0"), null, "gen", "PriceCalculator", "--project", _project.Root, "--plain", "--yes", "--no-pit", "--dry-run");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Matches(@"Chất lượng: line \d+% · branch \d+% · method \d+% · mutation - ", run.Output);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(RunDir(run), "result.json"), TestContext.Current.CancellationToken));
        var tools = result.RootElement.GetProperty("tools");
        Assert.Equal(("OK", "OK"), (tools.GetProperty("jacoco").GetString(), tools.GetProperty("tsdetect").GetString()));
        Assert.False(tools.TryGetProperty("pit", out _));
        Assert.False(File.Exists(TargetFile(RunDir(run), "block4/pit.json")));
        using var runJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(RunDir(run), "run.json"), TestContext.Current.CancellationToken));
        Assert.True(runJson.RootElement.GetProperty("config").GetProperty("no_pit").GetBoolean());
    }

    // prompts.strategy (Chính, 18/11/2026): c0 is asked with the few_shot templates, without the repository configuration.
    [Fact]
    public async Task GenWithTheFewShotStrategy()
    {
        await ConfigureAsync();
        Assert.Equal(0, (await Arrow(null, null, "config", "set", "prompts.strategy", "few_shot")).ExitCode);
        var run = await Arrow(Scenario("valid-at-c0"), null, "gen", "PriceCalculator", "--project", _project.Root, "--plain", "--yes", "--no-metrics", "--dry-run");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("Quy trình sinh test few_shot", run.Output, StringComparison.Ordinal);

        using var prompt = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(RunDir(run), "block2/prompt.json"), TestContext.Current.CancellationToken));
        Assert.Equal(["generation.system.few_shot", "generation.user.default"],
            prompt.RootElement.GetProperty("templates").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        var user = prompt.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.DoesNotContain("Repository configuration", user, StringComparison.Ordinal);
        using var runJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(RunDir(run), "run.json"), TestContext.Current.CancellationToken));
        Assert.Equal("few_shot", runJson.RootElement.GetProperty("config").GetProperty("strategy").GetString());

        Assert.NotEqual(0, (await Arrow(null, null, "config", "set", "prompts.strategy", "two_shot")).ExitCode);
    }

    // repair.mechanism (Chính, 20/11/2026): the chain of §14.5 with F−. Round 2 repeats round 1 and is verified again; every
    // prompt is level 1 without the repository configuration.
    [Fact]
    public async Task GenWithTheFixedMinusMechanism()
    {
        await ConfigureAsync();
        Assert.Equal(0, (await Arrow(null, null, "config", "set", "repair.mechanism", "fixed_minus")).ExitCode);
        var run = await Gen("o8-o3-o4-o7-o1", null, "--yes");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("cơ chế fixed_minus", run.Output, StringComparison.Ordinal);
        Assert.Contains("vòng 2 · mức 1 → COMPILE · sửa tiếp từ bản này", run.Output, StringComparison.Ordinal);
        Assert.Contains("vòng 5 · mức 1 → VALID", run.Output, StringComparison.Ordinal);

        var dir = RunDir(run);
        Assert.True(File.Exists(TargetFile(dir, "block3/round_2/verify.json")));
        for (var k = 1; k <= 5; k++)
        {
            using var decision = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(dir, $"block3/round_{k}/decision.json"), TestContext.Current.CancellationToken));
            Assert.Equal(("fixed_minus", 1), (decision.RootElement.GetProperty("mechanism").GetString(), decision.RootElement.GetProperty("level").GetInt32()));
        }

        var prompts = Directory.GetFiles(Path.Combine(dir, "targets"), "prompt.json", SearchOption.AllDirectories).Where(p => p.Contains("round_", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, prompts.Count);
        foreach (var path in prompts)
        {
            using var prompt = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var user = prompt.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Assert.DoesNotContain("Repository configuration", user, StringComparison.Ordinal);
            Assert.Contains("Failure output:", user, StringComparison.Ordinal);
        }

        using var runJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "run.json"), TestContext.Current.CancellationToken));
        Assert.Equal("fixed_minus", runJson.RootElement.GetProperty("config").GetProperty("mechanism").GetString());
        var show = await Arrow(null, null, "show", Path.GetFileName(dir), "--plain");
        Assert.Contains("có sửa (fixed_minus)", show.Output, StringComparison.Ordinal);

        Assert.NotEqual(0, (await Arrow(null, null, "config", "set", "repair.mechanism", "fixed")).ExitCode);
    }

    /// <summary>The Java code of a scenario answer, without its fence: what TN1 keeps as c0.</summary>
    private string C0FileOf(string scenario, string answer)
    {
        var lines = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(Scenario(scenario))!, answer)).Where(l => !l.StartsWith("```", StringComparison.Ordinal));
        var path = Path.Combine(_home, $"{scenario}-c0.java");
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        return path;
    }

    // --c0 (P04 §6.8, Chính 20/11/2026): the c0 of the chain of §14.5 comes from a file written by another model; the run makes no
    // generation call, repairs it as before, and records the generation and repair models.
    [Fact]
    public async Task GenRepairsAC0FileOfAnotherModel()
    {
        await ConfigureAsync();
        var c0 = C0FileOf("o8-o3-o4-o7-o1", "c0_compile_missing_method.md");
        var run = await Gen("o8-o3-o4-o7-o1", null, "--yes", "--dry-run", "--c0", c0, "--c0-model", "qwen3coder-or", "--c0-state", "COMPILE");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("c0 lấy từ file o8-o3-o4-o7-o1-c0.java", run.Output, StringComparison.Ordinal);
        Assert.Contains("vòng 5 · mức 2 → VALID", run.Output, StringComparison.Ordinal);

        var dir = RunDir(run);
        Assert.False(File.Exists(TargetFile(dir, "block2/prompt.json")));
        Assert.True(File.Exists(TargetFile(dir, "block2/candidate.java")));
        Assert.Equal(await File.ReadAllTextAsync(c0, TestContext.Current.CancellationToken), await File.ReadAllTextAsync(Path.Combine(dir, "c0_input.java"), TestContext.Current.CancellationToken));
        using var runJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "run.json"), TestContext.Current.CancellationToken));
        var config = runJson.RootElement.GetProperty("config").GetProperty("c0");
        Assert.Equal(("qwen3coder-or", "COMPILE", 64), (config.GetProperty("generation_model").GetString(), config.GetProperty("previous_state").GetString(), config.GetProperty("sha256").GetString()!.Length));
        using var final = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(dir, "block3/final.json"), TestContext.Current.CancellationToken));
        var f = final.RootElement;
        Assert.Equal(("file", "qwen3coder-or", "fake", false), (f.GetProperty("c0_source").GetString(), f.GetProperty("generation_model").GetString(), f.GetProperty("repair_model").GetString(), f.GetProperty("same_model").GetBoolean()));
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(dir, "result.json"), TestContext.Current.CancellationToken));
        Assert.Equal(5, result.RootElement.GetProperty("llm_calls").GetInt32());

        var show = await Arrow(null, null, "show", Path.GetFileName(dir), "--plain");
        Assert.Contains("c0 từ file o8-o3-o4-o7-o1-c0.java (qwen3coder-or)", show.Output, StringComparison.Ordinal);

        // A c0 file needs exactly one class.
        var two = await Arrow(Scenario("o8-o3-o4-o7-o1"), null, "gen", "PriceCalculator", "Invoice", "--project", _project.Root, "--plain", "--c0", c0);
        Assert.Equal(2, two.ExitCode);
        Assert.Contains("đúng một class", two.Output, StringComparison.Ordinal);
    }

    // P04 §6.8: a c0 file that is VALID now is not repaired, whatever its earlier state; no model is called at all.
    [Fact]
    public async Task AC0FileThatIsValidNowIsNotRepaired()
    {
        await ConfigureAsync();
        var c0 = C0FileOf("valid-at-c0", "c0_valid.md");
        var rules = Path.Combine(_home, "no-model.json");
        File.WriteAllText(rules, """{ "rules": [], "default": { "error": "no_rule" } }""");
        var run = await Arrow(rules, null, "gen", "PriceCalculator", "--project", _project.Root, "--no-metrics", "--plain", "--yes", "--dry-run", "--c0", c0, "--c0-state", "ASSERTION");
        Assert.True(run.ExitCode == 0, run.Output);

        using var final = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(RunDir(run), "block3/final.json"), TestContext.Current.CancellationToken));
        var f = final.RootElement;
        Assert.Equal(("VALID", "ASSERTION", 0, 0), (f.GetProperty("c0_state").GetString(), f.GetProperty("c0_previous_state").GetString(), f.GetProperty("success_round").GetInt32(), f.GetProperty("rounds_run").GetInt32()));
        Assert.Equal(JsonValueKind.Null, f.GetProperty("same_model").ValueKind);
    }

    // P07 §7, §13: two classes of one module with --jobs 2; block 1 runs once for the module, both files are written.
    [Fact]
    public async Task TwoClassesOfOneModuleShareBlockOne()
    {
        await ConfigureAsync();
        var run = await Arrow(Scenario("two-classes"), null, "gen", "PriceCalculator", "Invoice", "--project", _project.Root, "--no-metrics", "--plain", "--yes", "--jobs", "2");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("2/2 class VALID", run.Output, StringComparison.Ordinal);
        Assert.Equal(["Invoice_ArrowTest.java", "PriceCalculatorTest.java", "PriceCalculator_ArrowTest.java", "TotalsTest.java"], Files());

        var dir = RunDir(run);
        var targets = Path.Combine(dir, "targets");
        Assert.True(File.Exists(Path.Combine(targets, "com.acme.billing.PriceCalculator", "result.json")));
        Assert.True(File.Exists(Path.Combine(targets, "com.acme.billing.Invoice", "result.json")));

        // One CUT has the baseline; the other names it in shared_from.txt.
        var baselines = Directory.GetFiles(targets, "baseline.json", SearchOption.AllDirectories);
        var shared = Directory.GetFiles(targets, "shared_from.txt", SearchOption.AllDirectories);
        Assert.Equal((1, 1), (baselines.Length, shared.Length));

        using var runJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "run.json"), TestContext.Current.CancellationToken));
        Assert.Equal("COMPLETED", runJson.RootElement.GetProperty("status").GetString());
        Assert.All(runJson.RootElement.GetProperty("targets").EnumerateArray(), t => Assert.Equal("DONE", t.GetProperty("state").GetString()));
    }

    // PLAN GĐ6: three classes of maven-multi-junit5 with --jobs 2; block 1 runs once per module.
    [Fact]
    public async Task ThreeClassesOfAMultiModuleProject()
    {
        await ConfigureAsync();
        using var multi = new FixtureRepo("maven-multi-junit5");
        var run = await Arrow(Scenario("multi-module"), null, "gen", "TaxPolicy", "OrderService", "Greeter", "--project", multi.Root, "--no-metrics", "--plain", "--yes", "--jobs", "2");
        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("2/3 class VALID", run.Output, StringComparison.Ordinal);
        Assert.Contains("[OK] com.acme.shop.OrderService: VALID", run.Output, StringComparison.Ordinal);
        Assert.Contains("[OK] com.acme.sample.Greeter: VALID", run.Output, StringComparison.Ordinal);
        Assert.Contains("[X] com.acme.shop.api.TaxPolicy: COMPILE", run.Output, StringComparison.Ordinal);
        // The warning of the module without a test framework names its class (P01 §4.5).
        Assert.Contains("[!] TaxPolicy: Không thấy JUnit hay TestNG", run.Output, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(multi.Root, "shop-core", "src", "test", "java", "com", "acme", "shop", "OrderService_ArrowTest.java")));
        Assert.True(File.Exists(Path.Combine(multi.Root, "samples", "standalone", "src", "test", "java", "com", "acme", "sample", "Greeter_ArrowTest.java")));
        Assert.False(Directory.Exists(Path.Combine(multi.Root, "shop-api", "src", "test")));

        // Three modules: three block-1 analyses, each with its own baseline, none shared.
        var targets = Path.Combine(RunDir(run), "targets");
        Assert.Equal(3, Directory.GetFiles(targets, "baseline.json", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetFiles(targets, "shared_from.txt", SearchOption.AllDirectories));
        using var tax = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(targets, "com.acme.shop.api.TaxPolicy", "result.json"), TestContext.Current.CancellationToken));
        Assert.Equal("COMPILE", tax.RootElement.GetProperty("result").GetString());
    }

    // P07 §5, §8: P03 stops the run at the second class; --resume keeps the first and runs the second again.
    [Fact]
    public async Task AStoppedRunIsResumed()
    {
        await ConfigureAsync();
        var stopped = await Arrow(Scenario("stop-at-second"), null, "gen", "PriceCalculator", "Invoice", "--project", _project.Root, "--no-metrics", "--plain", "--yes", "--jobs", "1");
        Assert.True(stopped.ExitCode == 2, stopped.Output);
        Assert.Contains("B2_LLM_CONFIG", stopped.Output, StringComparison.Ordinal);
        var dir = RunDir(stopped);
        var id = Path.GetFileName(dir);
        using (var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "run.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal("FAILED", run.RootElement.GetProperty("status").GetString());
            Assert.Equal(["DONE", "RUNNING"], run.RootElement.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("state").GetString()));
        }

        var priceFile = Path.Combine(_project.Root, TestDir.Replace('/', Path.DirectorySeparatorChar), "PriceCalculator_ArrowTest.java");
        var written = File.GetLastWriteTimeUtc(priceFile);

        // GD7: a run killed before its cleanup leaves work/<run> behind; resuming must not trip over it.
        var leftover = Path.Combine(_home, "work", id, "base", "src", "leftover.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "from a killed run", TestContext.Current.CancellationToken);

        var resumed = await Arrow(Scenario("two-classes"), null, "gen", "--resume", id, "--project", _project.Root, "--no-metrics", "--plain", "--yes");
        Assert.True(resumed.ExitCode == 0, resumed.Output);
        Assert.Equal(dir, RunDir(resumed));
        Assert.Contains("giữ 1 class, chạy lại 1 class", resumed.Output, StringComparison.Ordinal);
        Assert.Contains("2/2 class VALID", resumed.Output, StringComparison.Ordinal);
        Assert.Equal(["Invoice_ArrowTest.java", "PriceCalculatorTest.java", "PriceCalculator_ArrowTest.java", "TotalsTest.java"], Files());
        Assert.Equal(written, File.GetLastWriteTimeUtc(priceFile));
        using (var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "run.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal("COMPLETED", run.RootElement.GetProperty("status").GetString());
        }

        // A changed project cannot be resumed.
        await File.AppendAllTextAsync(Path.Combine(_project.Root, "src", "main", "java", "com", "acme", "billing", "Invoice.java"), "\n// changed\n", TestContext.Current.CancellationToken);
        var mismatch = await Arrow(Scenario("two-classes"), null, "gen", "--resume", id, "--project", _project.Root, "--no-metrics", "--plain", "--yes");
        Assert.Equal(2, mismatch.ExitCode);
        Assert.Contains("GEN_RESUME_MISMATCH", mismatch.Output, StringComparison.Ordinal);

        // GD7: without --resume, an unfinished run of a project that changed is not resumed: a new run starts. It used to resume it
        // and stop with GEN_RESUME_MISMATCH on every "arrow gen" of the project for 7 days.
        var stoppedAgain = await Arrow(Scenario("stop-at-second"), null, "gen", "PriceCalculator", "Invoice", "--project", _project.Root, "--no-metrics", "--plain", "--yes", "--jobs", "1");
        Assert.True(stoppedAgain.ExitCode == 2, stoppedAgain.Output);
        await File.AppendAllTextAsync(Path.Combine(_project.Root, "src", "main", "java", "com", "acme", "billing", "Invoice.java"), "// changed again\n", TestContext.Current.CancellationToken);
        var fresh = await Arrow(Scenario("two-classes"), null, "gen", "Invoice", "--project", _project.Root, "--no-metrics", "--plain", "--yes", "--dry-run");
        Assert.DoesNotContain("GEN_RESUME_MISMATCH", fresh.Output, StringComparison.Ordinal);
        Assert.Contains("project đã thay đổi kể từ run đó: bắt đầu run mới", fresh.Output, StringComparison.Ordinal);
        Assert.NotEqual(RunDir(stoppedAgain), RunDir(fresh));
        Assert.True(fresh.ExitCode == 0, fresh.Output);
    }

    // P07 §3 step 5: a query without a class is BLOCKED; the other class still runs, and the exit code is 1.
    [Fact]
    public async Task AMissingClassDoesNotStopTheOthers()
    {
        await ConfigureAsync();
        var run = await Arrow(Scenario("valid-at-c0"), null, "gen", "PriceCalculator", "NoSuchClass", "--project", _project.Root, "--no-metrics", "--plain", "--yes");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("B1_CUT_MISSING", run.Output, StringComparison.Ordinal);
        Assert.Contains("1/2 class VALID", run.Output, StringComparison.Ordinal);

        // GD7: a pasted "\r" at the end of a name, and a missing name with characters a folder cannot have, used to end the run with
        // CLI_INTERNAL. The first is trimmed; the second is blocked like any missing class.
        var pasted = await Arrow(Scenario("valid-at-c0"), null, "gen", "PriceCalculator\r", "No:Such*Class", "--project", _project.Root, "--no-metrics", "--plain", "--yes", "--dry-run");
        Assert.Equal(1, pasted.ExitCode);
        Assert.DoesNotContain("CLI_INTERNAL", pasted.Output, StringComparison.Ordinal);
        Assert.Contains("[OK] com.acme.billing.PriceCalculator: VALID", pasted.Output, StringComparison.Ordinal);
        Assert.Contains("B1_CUT_MISSING", pasted.Output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(RunDir(pasted), "targets", "No_Such_Class")));
    }

    // P06 §9: without a keyboard and without --yes nothing is written; --dry-run never writes.
    [Fact]
    public async Task NothingIsWrittenWithoutYesOrWithDryRun()
    {
        await ConfigureAsync();
        var quiet = await Gen("valid-at-c0", "");
        Assert.True(quiet.ExitCode == 0, quiet.Output);
        Assert.Contains("cần --yes", quiet.Output, StringComparison.Ordinal);
        using (var apply = JsonDocument.Parse(await File.ReadAllTextAsync(TargetFile(RunDir(quiet), "apply.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal("skipped_non_interactive", apply.RootElement.GetProperty("decision").GetString());
        }

        var dry = await Gen("valid-at-c0", null, "--yes", "--dry-run");
        Assert.True(dry.ExitCode == 0, dry.Output);
        Assert.Contains("--dry-run", dry.Output, StringComparison.Ordinal);
        Assert.Equal(["PriceCalculatorTest.java", "TotalsTest.java"], Files());
    }
}
