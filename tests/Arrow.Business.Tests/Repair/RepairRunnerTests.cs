using System.Text.Json;
using Arrow.Business.Analysis;
using Arrow.Business.Generation;
using Arrow.Business.Llm;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Generation;
using Arrow.Common.Json;
using Arrow.Common.Llm;
using Arrow.Common.Verification;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Tests.Repair;

/// <summary>The repair chain with a fake block 2 and a fake verifier (P04 §6–7, §12 item 3), and the files it writes.</summary>
public sealed class RepairRunnerTests : IDisposable
{
    private const string Cut = "com.acme.billing.PriceCalculator";
    private const string TestFqn = Cut + "_ArrowTest";
    private const string TestPath = "src/test/java/com/acme/billing/PriceCalculator_ArrowTest.java";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-runner", Guid.NewGuid().ToString("N")[..8]);
    private readonly List<ArrowEvent> _events = [];
    private readonly ArrowHome _home;
    private readonly RunStore _runs;
    private readonly RunHandle _run;

    public RepairRunnerTests()
    {
        _home = new ArrowHome(_root);
        _home.EnsureCreated();
        _runs = new RunStore(_home);
        _run = _runs.Create(new RunInfo("r1", "test", DateTimeOffset.UtcNow, null, RunStatus.Running, ["gen"],
            new RunProject(ProjectSourceKind.LocalDirectory, "billing", null, null, null, null, null, null), null,
            new Dictionary<string, string>(), new Dictionary<string, string>(), [], RunTotals.Zero));
    }

    public void Dispose()
    {
        try
        {
            WorkspaceManager.DeleteTree(_root);
        }
        catch (IOException)
        {
        }
    }

    private TaskContext Task() => new(
        new RunContext("r1", _root, Path.Combine(_root, "work", "r1"), _run, new ListProgress(_events)),
        Cut,
        new RepositoryMetadata(
            new SourceMetadata(ProjectSourceKind.LocalDirectory, "billing", null, null, null),
            new BuildToolMetadata(BuildToolKind.Maven, "3.9.6", false, false, ".", BuildMode.Standalone, "pom.xml", null, "detected:standalone", [], []),
            null, null, new TestFrameworkMetadata("junit4", "4.13.2", false, [], [], "x"),
            new ProjectStructureMetadata(false, [], ".", ".", ["com.acme.billing"]),
            new PathsMetadata("src/main/java", "src/test/java", "src/main/java/com/acme/billing/PriceCalculator.java", TestPath, TestFqn, 1, true),
            new CutMetadata(Cut, "PriceCalculator", "com.acme.billing", CutKind.Class, "abc"),
            new Dictionary<string, string>()),
        new BaselineInfo(2, new BaselineCommands([], []), ".", [], [], [], new BaselineDurations(0, 0, [])),
        new WorkspaceInfo("work/r1/base", "work/r1/snapshot", null, 8, "/jdk", BuildToolKind.Maven, "mvn", false, ".", BuildMode.Standalone, "detected:standalone", [], null, [], []),
        "package com.acme.billing; public class PriceCalculator {}",
        "targets/" + Cut,
        []);

    private static readonly ResolvedModel Model = new("fake", "local", ProviderKind.OpenAiCompatible, new Uri("http://localhost:9/v1"), null, "fake-model",
        new Dictionary<string, JsonElement>(), new ModelPrice(1, 2), null);

    private static RepairOptions Options(int rounds = 5, int? p = 3, bool enabled = true) =>
        new(enabled, rounds, p, Model, "- Build tool: Maven", TimeSpan.FromMinutes(1), 3, TimeSpan.Zero, false);

    private static CandidateTest C(string code) =>
        new(true, null, null, code, TestFqn, TestPath, "sha256:" + code, 2, 2, [], [], "stop");

    private static VerifyResult V(VerificationState state, string? signature = null, int passed = 0, params string[] failing) => new()
    {
        State = state,
        ErrorSignature = signature ?? (state == VerificationState.Valid ? null : $"{state.ToString().ToUpperInvariant()} | x | -"),
        TestMethods = new TestMethodCounts(2, passed, 2 - passed),
        TestsPassed = passed,
        AssertionCount = 2,
        FailingMethods = failing,
        GeneratedTestRanInModule = state == VerificationState.Valid ? true : null,
    };

    /// <summary>Answers each round from a list: a code, or an exception.</summary>
    private sealed class FakeGenerator(params object[] answers) : ITestGenerator
    {
        public List<RepairRequest> Requests { get; } = [];

        public Task<GenerationResult> GenerateAsync(GenerationContext context, CancellationToken ct) => throw new NotSupportedException();

        public Task<GenerationResult> RepairAsync(GenerationContext context, RepairRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var answer = answers[request.Round - 1];
            if (answer is Exception ex)
            {
                return System.Threading.Tasks.Task.FromException<GenerationResult>(ex);
            }

            var candidate = answer as CandidateTest ?? C((string)answer);
            var response = new LlmResponse($"repair/{Cut}/round_{request.Round}", candidate.Code ?? "", "stop", "fake-model", 1000, 300, false, 2.5, 2, false, 0.001, default);
            return System.Threading.Tasks.Task.FromResult(new GenerationResult(candidate, response, LlmUsageRecord.From(response, Model),
                new PromptRecord(response.CallId, [], new Dictionary<string, JsonElement>(), [], "none")));
        }
    }

    /// <summary>Verifies by code; cancels the token at a chosen code.</summary>
    private sealed class FakeVerifier(Dictionary<string, VerifyResult> results, CancellationTokenSource? cancelAt = null, string? cancelCode = null) : IVerifier
    {
        public List<VerifyOptions> Calls { get; } = [];

        public Task<VerifyResult> VerifyAsync(SuiteFiles suite, TaskContext task, VerifyOptions options, CancellationToken ct)
        {
            Calls.Add(options);
            var code = suite.Files.Values.Single();
            if (code == cancelCode)
            {
                cancelAt!.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return System.Threading.Tasks.Task.FromResult(results[code] with { BuildSeconds = 4 });
        }
    }

    private RepairRunner Runner(ITestGenerator generator, IVerifier verifier, FakeHelper? helper = null) =>
        new(generator, verifier, new FailureDiagnoser(), new ExtraContextBuilder(new FakeHelperProvider(helper ?? new FakeHelper())),
            new FakeHelperProvider(helper ?? new FakeHelper()), _runs, ErrorCatalog.For("vi"));

    private T Read<T>(string path) => _runs.ReadJson<T>(_run, $"targets/{Cut}/block3/{path}")!;

    // P04 §12 item 3: R03 §14.5, O8 → O3 → O4 → O7 → O1, success in round 5.
    [Fact]
    public async Task TheChainOfSection14_5WritesEveryRound()
    {
        const string assertion = "ASSERTION | AssertionError | adds";
        var verifier = new FakeVerifier(new Dictionary<string, VerifyResult>
        {
            ["r1"] = V(VerificationState.Compile, "COMPILE | constructor cannot be applied | -", failing: "adds"),
            ["r3"] = V(VerificationState.Assertion, assertion, 1, "adds"),
            ["r4"] = V(VerificationState.Assertion, assertion, 1, "adds"),
            ["r5"] = V(VerificationState.Valid, passed: 2),
        });
        var generator = new FakeGenerator("r1", "r1", "r3", "r4", "r5");
        var c0 = C("c0");
        var final = await Runner(generator, verifier).RunChainAsync(c0, V(VerificationState.Compile, "COMPILE | cannot find symbol | method", failing: "adds"), Task(), Options(), TestContext.Current.CancellationToken);

        Assert.Equal((true, 5, 5, StopReason.Valid, VerificationState.Valid, 5), (final.Success, final.SuccessRound!.Value, final.RoundsRun, final.StopReason!.Value, final.FinalState, final.FinalRound));
        Assert.Equal("r5", final.Candidate.Code);
        Assert.Equal([1, 1, 2, 1, 2], generator.Requests.Select(r => r.Level));
        Assert.Equal(new RepairCounters(DuplicateCode: 1, RepeatedSignature: 1, LevelUp: 2, StateChange: 2), final.Counters);
        Assert.Equal((new TokenUsage(5000, 1500), 10, 0.005), (final.Tokens, final.LlmCalls, Math.Round(final.CostUsd!.Value, 6)));
        Assert.Equal(4, verifier.Calls.Count); // round 2 was a duplicate
        Assert.Equal("targets/" + Cut + "/block3/round_3", verifier.Calls[1].OutputDir);

        var decisions = Enumerable.Range(1, 5).Select(k => Read<RoundDecision>($"round_{k}/decision.json")).ToList();
        Assert.Equal([Comparison.Equal, null, Comparison.Better, Comparison.Equal, Comparison.Better], decisions.Select(d => d.Comparison));
        Assert.Equal([1, 2, 1, 2, 1], decisions.Select(d => d.LevelAfter));
        Assert.True(decisions[1].DuplicateCode);
        Assert.Equal(1, decisions[1].LlmRetries);
        Assert.Equal(2.5, decisions[0].LlmSeconds);
        Assert.Null(_runs.ReadText(_run, $"targets/{Cut}/block3/round_2/verify.json"));
        Assert.NotNull(_runs.ReadText(_run, $"targets/{Cut}/block3/round_3/verify.json"));
        Assert.Equal(2, Read<RepairRequest>("round_3/request.json").Level);
        Assert.Equal("r5", _runs.ReadText(_run, $"targets/{Cut}/block3/final.java"));
        var json = _runs.ReadText(_run, $"targets/{Cut}/block3/final.json")!;
        Assert.Contains("\"stop_reason\": \"valid\"", json, StringComparison.Ordinal);
        Assert.Contains("\"success\": true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate", json, StringComparison.Ordinal);
        Assert.Equal(5, _events.Count(e => e.Type == ArrowEventTypes.B3Round));
        Assert.Contains(_events, e => e.Type == ArrowEventTypes.B3Final);
    }

    [Fact]
    public async Task AValidC0OrNoRepairRunsNoRound()
    {
        var generator = new FakeGenerator();
        var valid = await Runner(generator, new FakeVerifier([])).RunChainAsync(C("c0"), V(VerificationState.Valid, passed: 2), Task(), Options(), TestContext.Current.CancellationToken);
        Assert.Equal((true, 0, 0, (StopReason?)null), (valid.Success, valid.SuccessRound!.Value, valid.RoundsRun, valid.StopReason));
        Assert.Equal("c0", _runs.ReadText(_run, $"targets/{Cut}/block3/final.java"));

        var off = await Runner(generator, new FakeVerifier([])).RunChainAsync(C("c0"), V(VerificationState.Assertion, passed: 1), Task(), Options(enabled: false), TestContext.Current.CancellationToken);
        Assert.Equal((false, (int?)null, VerificationState.Assertion, 0), (off.Success, off.SuccessRound, off.FinalState, off.RoundsRun));
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public async Task TheBudgetKeepsTheCheckpoint()
    {
        var verifier = new FakeVerifier(new Dictionary<string, VerifyResult>
        {
            ["r1"] = V(VerificationState.Runtime, "RUNTIME | NullPointerException | A.b", 0, "adds"),
            ["r2"] = V(VerificationState.Compile, "COMPILE | other | -"),
        });
        var final = await Runner(new FakeGenerator("r1", "r2"), verifier)
            .RunChainAsync(C("c0"), V(VerificationState.Compile), Task(), Options(rounds: 2, p: null), TestContext.Current.CancellationToken);
        Assert.Equal((StopReason.Budget, VerificationState.Runtime, 1, 2, (int?)null), (final.StopReason!.Value, final.FinalState, final.FinalRound, final.RoundsRun, final.SuccessRound));
        Assert.Equal("r1", final.Candidate.Code);
        Assert.Equal(1, final.Counters.Rollback);
    }

    [Fact]
    public async Task ThreeRoundsWithoutImprovementStopTheChain()
    {
        var verifier = new FakeVerifier(new Dictionary<string, VerifyResult>
        {
            ["r1"] = V(VerificationState.Compile, "COMPILE | a | -"),
            ["r2"] = V(VerificationState.Compile, "COMPILE | b | -"),
        });
        var final = await Runner(new FakeGenerator("r1", "r2", "r2", "r9", "r9"), verifier)
            .RunChainAsync(C("c0"), V(VerificationState.Compile, "COMPILE | c | -"), Task(), Options(), TestContext.Current.CancellationToken);
        Assert.Equal((StopReason.Stagnation, 3, 0), (final.StopReason!.Value, final.RoundsRun, final.FinalRound));
    }

    [Fact]
    public async Task InvalidOutputIsARoundWithoutVerification()
    {
        var invalid = new CandidateTest(false, InvalidReasons.NoCode, null, null, TestFqn, TestPath, null, 0, 0, [], [], "stop");
        var verifier = new FakeVerifier(new Dictionary<string, VerifyResult> { ["r2"] = V(VerificationState.Valid, passed: 2) });
        var final = await Runner(new FakeGenerator(invalid, "r2"), verifier)
            .RunChainAsync(C("c0"), V(VerificationState.Compile), Task(), Options(), TestContext.Current.CancellationToken);
        Assert.Equal((StopReason.Valid, 2, 1), (final.StopReason!.Value, final.SuccessRound!.Value, final.Counters.InvalidOutput));
        var first = Read<RoundDecision>("round_1/decision.json");
        Assert.Equal((true, (VerificationState?)null, 2), (first.InvalidOutput, first.State, first.LevelAfter));
    }

    [Fact]
    public async Task AnInfraVerificationStopsTheChainWithTheCheckpoint()
    {
        var verifier = new FakeVerifier(new Dictionary<string, VerifyResult>
        {
            ["r1"] = V(VerificationState.Runtime, "RUNTIME | X | -", 0, "adds"),
            ["r2"] = new VerifyResult { InfraReason = ErrorCodes.B3Timeout, BuildErrorExcerpt = "run_target: quá 20 s sau 3 lần thử" },
        });
        var final = await Runner(new FakeGenerator("r1", "r2"), verifier)
            .RunChainAsync(C("c0"), V(VerificationState.Compile), Task(), Options(), TestContext.Current.CancellationToken);
        Assert.Equal((StopReason.Infra, VerificationState.Runtime, 1, 2), (final.StopReason!.Value, final.FinalState, final.FinalRound, final.RoundsRun));
        Assert.Equal(ErrorCodes.B3Timeout, final.Error!.Code);
        Assert.True(Read<VerifyResult>("round_2/verify.json").IsInfra);
        Assert.Null(_runs.ReadText(_run, $"targets/{Cut}/block3/round_2/decision.json"));
    }

    [Fact]
    public async Task AStopOfTheRunEndsTheChainAsStopped()
    {
        var final = await Runner(new FakeGenerator(new ArrowException(ErrorCodes.B2TokenBudget)), new FakeVerifier([]))
            .RunChainAsync(C("c0"), V(VerificationState.Compile), Task(), Options(), TestContext.Current.CancellationToken);
        Assert.Equal((StopReason.Stopped, 0, VerificationState.Compile), (final.StopReason!.Value, final.RoundsRun, final.FinalState));
        Assert.Equal(ErrorCodes.B2TokenBudget, final.Error!.Code);

        var unavailable = await Runner(new FakeGenerator(new ArrowException(ErrorCodes.B2LlmUnavailable)), new FakeVerifier([]))
            .RunChainAsync(C("c0"), V(VerificationState.Compile), Task(), Options(), TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.Infra, unavailable.StopReason);
    }

    [Fact]
    public async Task CancellingKeepsTheCheckpoint()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var verifier = new FakeVerifier(new Dictionary<string, VerifyResult> { ["r1"] = V(VerificationState.Runtime, "RUNTIME | X | -", 0, "adds") }, cts, "r2");
        var final = await Runner(new FakeGenerator("r1", "r2"), verifier).RunChainAsync(C("c0"), V(VerificationState.Compile), Task(), Options(), cts.Token);
        Assert.Equal((StopReason.Cancelled, 1, 1), (final.StopReason!.Value, final.RoundsRun, final.FinalRound));
        Assert.Equal("r1", _runs.ReadText(_run, $"targets/{Cut}/block3/final.java"));
    }

    [Fact]
    public async Task LevelTwoGetsExtraContextAndDroppedMethodsAreReported()
    {
        var helper = new FakeHelper
        {
            Called = new CalledMethodsResult(true, ["add"], [new MethodSourceEntry("add", "int add(int a, int b) { return a + b; }")], [], []),
            TestMethods = code => code == "c0" ? ["adds", "divides"] : ["adds"],
        };
        var verifier = new FakeVerifier(new Dictionary<string, VerifyResult>
        {
            ["r1"] = V(VerificationState.Assertion, "ASSERTION | AssertionError | adds", 1, "adds"),
            ["r2"] = V(VerificationState.Valid, passed: 1),
        });
        var generator = new FakeGenerator("r1", "r2");
        var final = await Runner(generator, verifier, helper)
            .RunChainAsync(C("c0"), V(VerificationState.Assertion, "ASSERTION | AssertionError | adds", 1, "adds"), Task(), Options(), TestContext.Current.CancellationToken);
        Assert.Null(generator.Requests[0].ExtraContext);
        Assert.Contains("int add(int a, int b)", generator.Requests[1].ExtraContext, StringComparison.Ordinal);
        Assert.Equal(["divides"], final.DroppedMethods);
    }

    [Fact]
    public void RoundLinesFollowTheCliExample()
    {
        var d = new RoundDecision { Round = 3, Level = 2, RequestedLevel = 2, State = VerificationState.Assertion, TestsPassed = 4, Improved = true, LevelAfter = 1 };
        Assert.Equal("vòng 3 · mức 2 → ASSERTION 4 pass · tốt hơn · checkpoint", RepairRunner.RoundText(d));
        Assert.Equal("vòng 2 · mức 1 → mã trùng vòng 1 · lên mức 2", RepairRunner.RoundText(new RoundDecision { Round = 2, Level = 1, RequestedLevel = 1, DuplicateCode = true, DuplicateOfRound = 1, LevelAfter = 2 }));
        Assert.Equal("vòng 1 · mức 1 → COMPILE · giữ mức", RepairRunner.RoundText(new RoundDecision { Round = 1, Level = 1, RequestedLevel = 1, State = VerificationState.Compile, LevelAfter = 1 }));
        Assert.Equal("vòng 4 · mức 2 (yêu cầu 3) → COMPILE · tệ hơn · rollback, lên mức 3",
            RepairRunner.RoundText(new RoundDecision { Round = 4, Level = 2, RequestedLevel = 3, State = VerificationState.Compile, Rollback = true, LevelAfter = 3 }));
    }

    private sealed class ListProgress(List<ArrowEvent> events) : IProgress<ArrowEvent>
    {
        public void Report(ArrowEvent value) => events.Add(value);
    }
}
