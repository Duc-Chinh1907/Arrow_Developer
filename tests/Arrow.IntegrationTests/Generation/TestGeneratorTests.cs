using System.Text.Json;
using Arrow.Business.Generation;
using Arrow.Business.Llm;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Generation;
using Arrow.Common.Json;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Llm;
using Arrow.DataAccess.Storage;
using Arrow.IntegrationTests.JavaHelper;

namespace Arrow.IntegrationTests.Generation;

/// <summary>
/// Block 2 with the real java-helper and the fake model of P11 §3.1 (P02 §9). Parity tests rewritten from
/// <c>ARROW_Framework/tests/Arrow.IntegrationTests/TestGeneratorTests.cs</c>: the inputs and answers are the old ones; the call ids,
/// the files and the context follow P02 (<c>gen/&lt;cut&gt;</c>, <c>repair/&lt;cut&gt;/round_&lt;k&gt;</c>, files under the run).
/// </summary>
[Trait("Category", "Integration")]
public sealed class TestGeneratorTests(JavaHelperFixture fixture) : IClassFixture<JavaHelperFixture>, IDisposable
{
    private const string CutFqn = "com.acme.shop.OrderService";
    private const string CutSource = "package com.acme.shop;\n\npublic class OrderService {\n    public long totalPrice(long id) { return id * 2; }\n}\n";
    private const string MetadataPrompt = "- Build tool: Maven 3.9.6 (single-module project, build root .)\n- Java version: 8 (compile with JDK 8; do not use language features or APIs newer than Java 8)\n- Test path: src/test/java/com/acme/shop/OrderService_ArrowTest.java";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-gen-tests", Guid.NewGuid().ToString("N")[..10]);
    private readonly List<ArrowEvent> _events = [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class SingleHelper(IJavaHelper helper) : IJavaHelperProvider
    {
        private sealed class Lease(IJavaHelper helper) : IJavaHelperLease
        {
            public IJavaHelper Helper { get; } = helper;

            public void Dispose()
            {
            }
        }

        public Task<IJavaHelperLease> RentAsync(CancellationToken ct) => Task.FromResult<IJavaHelperLease>(new Lease(helper));
    }

    private sealed class ListProgress(List<ArrowEvent> events) : IProgress<ArrowEvent>
    {
        public void Report(ArrowEvent value) => events.Add(value);
    }

    private static RepositoryMetadata Metadata() => new(
        new SourceMetadata(ProjectSourceKind.LocalDirectory, "shop", null, null, null),
        new BuildToolMetadata(BuildToolKind.Maven, "3.9.6", false, false, ".", BuildMode.Standalone, "pom.xml", null, "detected:standalone", [], []),
        new JavaMetadata("1.8", "maven.compiler.source", 8, 8, "declared"),
        null,
        new TestFrameworkMetadata("junit4", "4.13.2", false, [], [], "dependency"),
        new ProjectStructureMetadata(false, [], ".", ".", ["com.acme.shop"]),
        new PathsMetadata("src/main/java", "src/test/java", "src/main/java/com/acme/shop/OrderService.java",
            "src/test/java/com/acme/shop/OrderService_ArrowTest.java", "com.acme.shop.OrderService_ArrowTest", 1, true),
        new CutMetadata(CutFqn, "OrderService", "com.acme.shop", CutKind.Class, "sha"),
        new Dictionary<string, string>());

    private static ResolvedModel Model() => new(
        "gpt-x", "teamo", ProviderKind.OpenAiCompatible, new Uri("https://api.teamorouter.com/v1"), "teamo", "gpt-4.1-mini",
        new Dictionary<string, JsonElement> { ["temperature"] = JsonSerializer.SerializeToElement(0), ["max_tokens"] = JsonSerializer.SerializeToElement(8192) },
        null, null);

    private (TestGenerator Generator, GenerationContext Context, RunStore Runs, FakeLlmClient Llm) Create(string rulesJson, string cutSource = CutSource, string resultDir = "targets/com.acme.shop.OrderService/block2")
    {
        var home = new ArrowHome(_root);
        home.EnsureCreated();
        var runs = new RunStore(home);
        var run = runs.Create(new RunInfo("20261113-120000-gen1", "test", DateTimeOffset.UtcNow, null, RunStatus.Running, ["gen"],
            new RunProject(ProjectSourceKind.LocalDirectory, "shop", null, null, null, null, null, null), null,
            new Dictionary<string, string>(), new Dictionary<string, string>(), [], RunTotals.Zero));
        var rules = Path.Combine(_root, "rules.json");
        File.WriteAllText(rules, rulesJson);
        var llm = new FakeLlmClient(rules, "gpt-4.1-mini");
        var generator = new TestGenerator(llm, new SingleHelper(fixture.Client), new PromptTemplateStore(), runs);
        var context = new GenerationContext(run, CutFqn, Metadata(), MetadataPrompt, cutSource, Model(), resultDir, new ListProgress(_events));
        return (generator, context, runs, llm);
    }

    private static string Rules(string callId, string text, string? finish = null) =>
        JsonSerializer.Serialize(new { rules = new[] { new { when = new { call_id = callId }, respond_text = text, finish_reason = finish } } });

    // Parity: GenerateNormalizesFencedAnswerAndWritesEveryFile (P02 §10 example, with the old test's duplicated import)
    [Fact]
    public async Task GenerateNormalizesAFencedAnswerAndWritesEveryFile()
    {
        const string answer = """
            Here is the test:
            ```java
            import org.junit.Test;
            import static org.junit.Assert.assertEquals;
            import org.junit.Test;
            public class OrderServiceTest {
                @Test
                public void doublesPrice() {
                    OrderServiceTest self = new OrderServiceTest();
                    assertEquals(4L, new OrderService().totalPrice(2L));
                }
            }
            ```
            Hope this helps.
            """;
        var (generator, context, runs, llm) = Create(Rules("gen/" + CutFqn, answer));

        var result = await generator.GenerateAsync(context, TestContext.Current.CancellationToken);

        var candidate = result.Candidate;
        Assert.True(candidate.ValidOutput, candidate.InvalidReason);
        Assert.Equal("com.acme.shop.OrderService_ArrowTest", candidate.TestFqn);
        Assert.StartsWith("package com.acme.shop;", candidate.Code, StringComparison.Ordinal);
        Assert.Contains("public class OrderService_ArrowTest {", candidate.Code, StringComparison.Ordinal);
        Assert.Contains("OrderService_ArrowTest self = new OrderService_ArrowTest();", candidate.Code, StringComparison.Ordinal);
        // The duplicated import is dropped; no import is added (P02 §5.3).
        Assert.Equal(1, candidate.Code!.Split("import org.junit.Test;").Length - 1);
        Assert.Equal(2, candidate.Code.Split('\n').Count(l => l.StartsWith("import ", StringComparison.Ordinal)));
        Assert.Equal(
            ["fence removed", "import: removed duplicate org.junit.Test", "package: added com.acme.shop", "class: OrderServiceTest → OrderService_ArrowTest"],
            candidate.Normalization);
        Assert.Equal((1, 1), (candidate.TestMethodCount, candidate.AssertionCount));
        Assert.StartsWith("sha256:", candidate.CodeHash, StringComparison.Ordinal);
        Assert.Equal(["gen/" + CutFqn], llm.Calls);

        // The five files of P02 §6.
        var run = context.Run;
        var prompt = runs.ReadJson<PromptRecord>(run, "targets/com.acme.shop.OrderService/block2/prompt.json")!;
        Assert.Equal(("gen/" + CutFqn, "none"), (prompt.CallId, prompt.SourceTruncation));
        Assert.Contains("- Java version: 8", prompt.Messages[1].Content, StringComparison.Ordinal);
        Assert.Equal(["generation.system.zero_shot_project_aware", "generation.user.project_aware"], prompt.Templates.Select(t => t.Id));
        Assert.Equal(8192, prompt.Params["max_tokens"].GetInt32());
        Assert.Equal(candidate.Code, runs.ReadText(run, "targets/com.acme.shop.OrderService/block2/candidate.java"));
        var usage = runs.ReadText(run, "targets/com.acme.shop.OrderService/block2/usage.json")!;
        Assert.Contains("\"provider_host\": \"api.teamorouter.com\"", usage, StringComparison.Ordinal);
        Assert.Contains("\"fake\": true", runs.ReadText(run, "targets/com.acme.shop.OrderService/block2/response.json"), StringComparison.Ordinal);
        var file = runs.ReadJson<CandidateFile>(run, "targets/com.acme.shop.OrderService/block2/candidate.json")!;
        Assert.Equal(("GENERATE", 0, null, "gpt-x", true), (file.Mode, file.Round, file.Level, file.Model, file.ValidOutput));
        Assert.Equal(candidate.CodeHash, file.CodeHash);
        Assert.Equal("stop", file.FinishReason);

        Assert.Equal(
            [ArrowEventTypes.B2Prompt, ArrowEventTypes.B2Calling, ArrowEventTypes.B2Response, ArrowEventTypes.B2Candidate],
            _events.Select(e => e.Type));
        Assert.All(_events, e => Assert.Equal(CutFqn, e.CutFqn));
    }

    // Parity: UnusableAnswersAreInvalidOutput
    [Theory]
    [InlineData("I am unable to generate tests for this class.", "no_code")]
    [InlineData("public class A { @Test public void t() {} }\nclass B {}", "multiple_classes")]
    [InlineData("```java\npublic class OrderServiceTest {\n  @Test public void t() {\n    int x =", "parse_error")]
    public async Task UnusableAnswersAreInvalidOutput(string answer, string reason)
    {
        var (generator, context, runs, _) = Create(Rules("gen/" + CutFqn, answer));

        var candidate = (await generator.GenerateAsync(context, TestContext.Current.CancellationToken)).Candidate;

        Assert.False(candidate.ValidOutput);
        Assert.Equal(reason, candidate.InvalidReason);
        Assert.Null(candidate.Code);
        Assert.Null(runs.ReadText(context.Run, "targets/com.acme.shop.OrderService/block2/candidate.java"));
        var file = runs.ReadJson<CandidateFile>(context.Run, "targets/com.acme.shop.OrderService/block2/candidate.json")!;
        Assert.Equal((false, reason), (file.ValidOutput, file.InvalidReason));
        Assert.Equal(reason == "parse_error", candidate.ParseError is not null);
        Assert.Equal(ArrowEventTypes.B2Candidate, _events[^1].Type);
        Assert.Equal(EventLevel.Warning, _events[^1].Level);
    }

    // Parity: Level3SplicesOnlyFailingMethodsIntoCheckpoint (P02 §10, REPAIR level 3)
    [Fact]
    public async Task Level3SplicesOnlyTheFailingMethodsIntoTheCheckpoint()
    {
        const string checkpoint = """
            package com.acme.shop;
            import static org.junit.Assert.assertEquals;
            import org.junit.Test;
            public class OrderService_ArrowTest {
                @Test
                public void keepsThis() {  assertEquals(2L, new OrderService().totalPrice(1L)); }
                @Test
                public void broken() {
                    assertEquals(5L, new OrderService().totalPrice(2L));
                }
                @Test
                public void alsoBroken() {
                    assertEquals(7L, new OrderService().totalPrice(3L));
                }
            }
            """;
        const string answer = """
            package com.acme.shop;
            import static org.junit.Assert.assertEquals;
            import org.junit.Test;
            public class OrderService_ArrowTest {
                private static final long TWO = 2L;
                @Test
                public void keepsThis() { throw new RuntimeException("must be ignored"); }
                @Test
                public void broken() {
                    assertEquals(4L, new OrderService().totalPrice(TWO));
                }
            }
            """;
        var dir = "targets/com.acme.shop.OrderService/block3/round_3";
        var (generator, context, runs, llm) = Create(Rules("repair/" + CutFqn + "/round_3", answer), resultDir: dir);
        var request = new RepairRequest(
            CutFqn, 3, 3, 3, VerificationState.Assertion, checkpoint, 1, "broken: expected:<5> but was:<4>",
            "public long totalPrice(long id) { return id * 2; }",
            [new FailedAttempt(2, "ASSERTION | AssertionError | broken", null, null)],
            ["broken", "alsoBroken"]);

        var result = await generator.RepairAsync(context, request, TestContext.Current.CancellationToken);

        var candidate = result.Candidate;
        Assert.True(candidate.ValidOutput, candidate.InvalidReason);
        Assert.Equal(["repair/" + CutFqn + "/round_3"], llm.Calls);
        Assert.Equal("repair/" + CutFqn + "/round_3", result.Prompt.CallId);
        Assert.Contains("public void keepsThis() {  assertEquals(2L, new OrderService().totalPrice(1L)); }", candidate.Code, StringComparison.Ordinal);
        Assert.Contains("totalPrice(TWO)", candidate.Code, StringComparison.Ordinal);
        Assert.Contains("private static final long TWO = 2L;", candidate.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("must be ignored", candidate.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("alsoBroken", candidate.Code, StringComparison.Ordinal);
        Assert.Equal(["alsoBroken"], candidate.DroppedMethods);
        Assert.Equal(2, candidate.TestMethodCount);
        Assert.Equal(["repair.system", "repair.user.level3"], result.Prompt.Templates.Select(t => t.Id));
        Assert.Contains("splice: replaced 1, dropped 1", string.Join('|', candidate.Normalization), StringComparison.Ordinal);

        // The hash is of the spliced code (P02 §5.5): hashing the final text gives the same value.
        var again = await fixture.Client.StripCommentsHashAsync(candidate.Code!, TestContext.Current.CancellationToken);
        Assert.Equal(again.CodeHash, candidate.CodeHash);
        var file = runs.ReadJson<CandidateFile>(context.Run, dir + "/candidate.json")!;
        Assert.Equal(("REPAIR", 3, 3), (file.Mode, file.Round, file.Level));
        Assert.Equal(["alsoBroken"], file.DroppedMethods);
    }

    // Parity: LongCutIsShrunkBeforePrompting (P02 §3.3)
    [Fact]
    public async Task ALongCutIsShrunkBeforePrompting()
    {
        var comment = "// " + new string('x', 200) + "\n";
        var longCut = "package com.acme.shop;\n\npublic class OrderService {\n" + string.Concat(Enumerable.Repeat(comment, 300)) + "    public long totalPrice(long id) { return id * 2; }\n}\n";
        Assert.True(longCut.Length > TestGenerator.MaxCutChars);
        var (generator, context, _, llm) = Create(Rules("gen/" + CutFqn, "public class OrderServiceTest { @org.junit.Test public void t() {} }"), longCut);

        var result = await generator.GenerateAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("comments_removed", result.Prompt.SourceTruncation);
        Assert.DoesNotContain("xxxxxxxxxx", result.Prompt.Messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("public long totalPrice(long id)", result.Prompt.Messages[1].Content, StringComparison.Ordinal);
        Assert.Single(llm.Calls);
        var warning = Assert.Single(_events, e => e.Type == ArrowEventTypes.B2SourceTruncated);
        Assert.Equal((EventLevel.Warning, "comments_removed"), (warning.Level, warning.Data["truncation"]));
    }

    [Fact]
    public async Task AnAnswerCutByMaxTokensIsReportedAndUsuallyFailsToParse()
    {
        var (generator, context, _, _) = Create(Rules("gen/" + CutFqn, "public class OrderServiceTest {\n  @Test public void t() {\n    int x =", "length"));

        var result = await generator.GenerateAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("parse_error", result.Candidate.InvalidReason);
        Assert.Equal("length", result.Candidate.FinishReason);
        Assert.Equal("length", result.Usage.FinishReason);
        var truncated = Assert.Single(_events, e => e.Type == ArrowEventTypes.B2Truncated);
        Assert.Equal(EventLevel.Warning, truncated.Level);
        Assert.Contains("max_tokens", truncated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptJsonIsWrittenBeforeTheModelIsCalledAndModelErrorsReachTheCaller()
    {
        var (generator, context, runs, _) = Create("""{ "rules": [ { "when": { "call_id": "gen/com.acme.shop.OrderService" }, "error": "401" } ] }""");

        var error = await Assert.ThrowsAsync<ArrowException>(() => generator.GenerateAsync(context, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.B2LlmConfig, error.Code);
        Assert.NotNull(runs.ReadJson<PromptRecord>(context.Run, "targets/com.acme.shop.OrderService/block2/prompt.json"));
        Assert.Null(runs.ReadText(context.Run, "targets/com.acme.shop.OrderService/block2/usage.json"));
        Assert.DoesNotContain(_events, e => e.Type == ArrowEventTypes.B2Response);
    }

    [Fact]
    public async Task EveryFileOfTheRunIsFreeOfApiKeys()
    {
        var (generator, context, _, _) = Create(Rules("gen/" + CutFqn, "public class OrderServiceTest { @org.junit.Test public void t() {} }"));
        await generator.GenerateAsync(context, TestContext.Current.CancellationToken);
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.Contains(files, f => f.EndsWith("prompt.json", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => File.ReadAllText(f).Contains("sk-test-SECRET-123", StringComparison.Ordinal));
        Assert.All(files.Where(f => f.EndsWith(".json", StringComparison.Ordinal) && !f.EndsWith("rules.json", StringComparison.Ordinal)), f => ArrowJson.Deserialize<JsonElement>(File.ReadAllText(f)));
    }
}
