using System.Text.Json;
using System.Text.Json.Nodes;
using Arrow.Common;
using Arrow.Common.Json;

namespace Arrow.Business.Tests.Common;

/// <summary>JSON conventions of P00 §5 and the file contracts of P07 §6 and P09 §5.3.</summary>
public class JsonConventionsTests
{
    /// <summary>
    /// Parity: <c>ARROW_Framework/tests/Arrow.DataAccess.Tests/FileHelpersTests.JsonUsesSnakeCaseAndEnumNamesFromSpec</c>.
    /// Research types are replaced by product types: <c>WorkspaceInfo.BuildMode</c> (lower-case enum) by
    /// <c>RunProject.Kind</c>, <c>LedgerEntry.Outcome</c> by <c>TargetResult.Result</c>, <c>PairedTestStatus.NoTestRun</c>
    /// by <c>TargetOutcome.InvalidOutput</c> (both multi-word upper-case names).
    /// </summary>
    [Fact]
    public void JsonUsesSnakeCaseAndEnumNamesFromSpec()
    {
        var project = ArrowJson.Serialize(new RunProject(ProjectSourceKind.LocalDirectory, "3rdParty/x", null, null, null, null, true, null), indented: false);
        Assert.Contains("\"kind\":\"local\"", project);
        Assert.Contains("\"path_name\":\"3rdParty/x\"", project);
        Assert.Contains("\"source_tree_hash\":null", project);

        var result = ArrowJson.Serialize(SampleResult() with { Result = TargetOutcome.Infra }, indented: false);
        Assert.Contains("\"result\":\"INFRA\"", result);
        Assert.Contains("\"llm_calls\":5", result);

        Assert.Contains("\"INVALID_OUTPUT\"", ArrowJson.Serialize(TargetOutcome.InvalidOutput));
        Assert.Contains("\"REGRESSION\"", ArrowJson.Serialize(VerificationState.Regression));
        Assert.Equal(VerificationState.Compile, ArrowJson.Deserialize<VerificationState>("\"COMPILE\""));
    }

    /// <summary>Parity: <c>ARROW_Framework/tests/Arrow.DataAccess.Tests/FileHelpersTests.DictionaryKeysAreNotRenamed</c>.</summary>
    [Fact]
    public void DictionaryKeysAreNotRenamed()
    {
        var json = ArrowJson.Serialize(new Dictionary<string, string> { ["8"] = "/jdk8", ["MaxTokens"] = "x" }, indented: false);
        Assert.Equal("{\"8\":\"/jdk8\",\"MaxTokens\":\"x\"}", json);
    }

    [Theory]
    [InlineData(StopReason.Stagnation, "\"stagnation\"")]
    [InlineData(ApplyDecision.SkippedNonInteractive, "\"skipped_non_interactive\"")]
    [InlineData(ApplyDecision.RemoteProject, "\"remote_project\"")]
    [InlineData(CutKind.Annotation, "\"annotation\"")]
    [InlineData(ProjectSourceKind.GitRepository, "\"git\"")]
    [InlineData(ToolStatus.Ok, "\"OK\"")]
    [InlineData(RunStatus.Cancelled, "\"CANCELLED\"")]
    [InlineData(TargetState.Pending, "\"PENDING\"")]
    [InlineData(EventLevel.Warning, "\"WARNING\"")]
    public void EnumsUseTheNamesOfTheSpecs(object value, string expected)
    {
        var type = value.GetType();
        Assert.Equal(expected, JsonSerializer.Serialize(value, type, ArrowJson.Options));
        Assert.Equal(value, JsonSerializer.Deserialize(expected, type, ArrowJson.Options));
    }

    [Fact]
    public void EnumsAreNeverWrittenAsNumbers()
    {
        Assert.Throws<JsonException>(() => ArrowJson.Deserialize<VerificationState>("4"));
    }

    [Fact]
    public void TimestampsAreUtcWithSecondPrecision()
    {
        var local = new DateTimeOffset(2026, 11, 10, 14, 25, 30, 456, TimeSpan.FromHours(7));
        Assert.Equal("\"2026-11-10T07:25:30Z\"", ArrowJson.Serialize(local));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 7, 25, 30, TimeSpan.Zero), ArrowJson.Deserialize<DateTimeOffset>("\"2026-11-10T07:25:30Z\""));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 7, 25, 30, TimeSpan.Zero), ArrowJson.Deserialize<DateTimeOffset>("\"2026-11-10T14:25:30+07:00\""));
    }

    [Fact]
    public void OutputUsesLfAndKeepsVietnameseText()
    {
        var json = ArrowJson.Serialize(new Dictionary<string, string> { ["message"] = "Không biên dịch được <module> & \"x\"" });
        Assert.DoesNotContain("\r", json);
        Assert.Contains("\n", json);
        Assert.Contains("Không biên dịch được <module> &", json);
    }

    [Fact]
    public void ReadsCommentsAndTrailingCommas()
    {
        var map = ArrowJson.Deserialize<Dictionary<string, int>>("{\n  // comment\n  \"a\": 1,\n}");
        Assert.Equal(1, map["a"]);
    }

    /// <summary>The <c>result.json</c> example of P07 §6 reads into <see cref="TargetResult"/> and writes back unchanged.</summary>
    [Fact]
    public void ResultJsonExampleOfP07RoundTrips()
    {
        const string example = """
            {
              "cut_fqn": "com.myshop.service.OrderService",
              "result": "VALID",
              "c0_state": "COMPILE", "rounds_run": 4, "success_round": 4, "stop_reason": "valid",
              "test_fqn": "com.myshop.service.OrderService_ArrowTest",
              "test_path": "shop-core/src/test/java/com/myshop/service/OrderService_ArrowTest.java",
              "final_code": "block3/final.java",
              "metrics": { "line": 0.78, "branch": 0.64, "method": 0.86, "mutation": 0.61, "test_strength": 0.73, "smell_density": 0.5 },
              "metrics_skipped": null,
              "tools": { "jacoco": "OK", "pit": "OK", "tsdetect": "OK" },
              "tokens": { "in": 9800, "out": 2600 }, "cost_usd": 0.006, "llm_calls": 5,
              "seconds": { "block1": 61.2, "block2": 6.2, "block3": 96.3, "block4": 150.0, "total": 313.7 },
              "error": null,
              "warnings": ["dropped_methods: 1"],
              "apply": { "decision": "written", "path": "shop-core/src/test/java/com/myshop/service/OrderService_ArrowTest.java" }
            }
            """;

        var result = ArrowJson.Deserialize<TargetResult>(example);

        Assert.Equal(TargetOutcome.Valid, result.Result);
        Assert.Equal(VerificationState.Compile, result.C0State);
        Assert.Equal(StopReason.Valid, result.StopReason);
        Assert.Equal(0.61, result.Metrics!.Mutation);
        Assert.Equal(ToolStatus.Ok, result.Tools!["pit"]);
        Assert.Equal(new TokenUsage(9800, 2600), result.Tokens);
        Assert.Equal(313.7, result.Seconds.Total);
        Assert.Equal(ApplyDecision.Written, result.Apply!.Decision);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(example), JsonNode.Parse(ArrowJson.Serialize(result))));
    }

    /// <summary>
    /// The <c>run.json</c> example of P09 §5.3 reads into <see cref="RunInfo"/>. The product adds
    /// <c>project.url</c>, <c>project.git_ref</c>, <c>project.source_tree_hash</c> and <c>config.repair</c>, which P07 §5.2 needs to resume.
    /// </summary>
    [Fact]
    public void RunJsonExampleOfP09Reads()
    {
        const string example = """
            {
              "run_id": "20261110-142530-k3f9",
              "arrow_version": "1.0.0",
              "started_at": "2026-11-10T07:25:30Z", "finished_at": "2026-11-10T07:41:02Z",
              "status": "COMPLETED",
              "command": ["gen", "OrderService", "--rounds", "5"],
              "project": { "kind": "local", "path_name": "MyShop", "root": "D:/src/MyShop", "git_commit": "ab12", "dirty": true },
              "config": { "model": "gpt-x", "provider_host": "api.teamorouter.com", "rounds": 5, "early_stop_p": 3, "quality": true },
              "prompts_sha256": { "repair/user/level1.md": "x" },
              "tools": { "java_helper": "sha256:y", "jacoco": "0.8.12", "pit": "1.17.4", "tsdetect": "2.2" },
              "targets": [ { "cut_fqn": "com.myshop.OrderService", "state": "DONE" } ],
              "totals": { "llm_calls": 6, "tokens_in": 24310, "tokens_out": 7010, "cost_usd": 0.0210 }
            }
            """;

        var run = ArrowJson.Deserialize<RunInfo>(example);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 7, 41, 2, TimeSpan.Zero), run.FinishedAt);
        Assert.Equal(ProjectSourceKind.LocalDirectory, run.Project.Kind);
        Assert.Equal("D:/src/MyShop", run.Project.Root);
        Assert.Equal(3, run.Config!.EarlyStopP);
        Assert.Equal(TargetState.Done, Assert.Single(run.Targets).State);
        Assert.Equal(24310, run.Totals.TokensIn);

        var written = JsonNode.Parse(ArrowJson.Serialize(run))!;
        Assert.Equal("2026-11-10T07:25:30Z", written["started_at"]!.GetValue<string>());
        Assert.Equal("api.teamorouter.com", written["config"]!["provider_host"]!.GetValue<string>());
    }

    [Fact]
    public void VerificationStatesAreOrderedAsInP00()
    {
        string[] expected = ["COMPILE", "UNKNOWN", "DISCOVERY", "RUNTIME", "ASSERTION", "REGRESSION", "VALID"];
        var ordered = Enum.GetValues<VerificationState>().Order().Select(s => ArrowJson.Serialize(s).Trim('"'));
        Assert.Equal(expected, ordered);
        Assert.True(VerificationState.Assertion < VerificationState.Regression);
    }

    [Fact]
    public void EveryStateMapsToTheOutcomeWithTheSameName()
    {
        foreach (var state in Enum.GetValues<VerificationState>())
        {
            Assert.Equal(ArrowJson.Serialize(state), ArrowJson.Serialize(state.ToOutcome()));
        }
    }

    private static TargetResult SampleResult() => new(
        "com.acme.A", TargetOutcome.Valid, VerificationState.Compile, 1, 1, StopReason.Valid,
        "com.acme.A_ArrowTest", "src/test/java/com/acme/A_ArrowTest.java", "block3/final.java",
        null, "disabled", null, new TokenUsage(10, 20), null, 5, new BlockSeconds(1, 2, 3, null, 6),
        null, [], null);
}
