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
