using Arrow.Business.Generation;
using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.Common.Generation;

namespace Arrow.Business.Tests.Generation;

/// <summary>
/// Template store and prompt builder (P02 §3, §9). Parity tests rewritten from <c>Block2Tests</c> and <c>PromptTests</c> of ARROW_Framework
/// (<c>RepairPromptsByLevelAlwaysCarryFullMetadata</c>, <c>FailedAttemptsShowDetailsOnlyForLastThree</c>,
/// <c>FailureSummaryComesFromDataFile</c>, <c>RenderFillsEveryPlaceholderOnce</c>, <c>RenderRejectsMissingExtraOrUndeclaredValues</c>,
/// <c>LoadsAllRealPromptTemplates</c>, <c>TemplateShaIgnoresLineEndingsAndFrontMatterIsRequired</c>); only the product templates remain.
/// </summary>
public sealed class PromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-prompt-tests", Guid.NewGuid().ToString("N")[..10]);

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

    private static readonly PromptTemplateStore Store = new();

    private const string MetadataPrompt = """
        - Build tool: Maven 3.9.6 (module shop-core, multi-module project, build root .)
        - Java version: 8 (compile with JDK 8; do not use language features or APIs newer than Java 8)
        - Test framework: JUnit 4 (4.13.2); mocking: Mockito 3.12.4; assertions: none beyond JUnit
        - Dependencies (direct): junit:junit:4.13.2
        - Project structure: modules shop-api, shop-core; CUT package com.acme.shop
        - Source path: shop-core/src/main/java
        - Test path: shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java
        """;

    private static PromptInput Input(string source = "package com.acme.shop;\npublic class OrderService { public long totalPrice(long id) { return 0; } }\n") => new(
        "com.acme.shop.OrderService",
        "shop-core/src/main/java/com/acme/shop/OrderService.java",
        "com.acme.shop",
        "OrderService_ArrowTest",
        MetadataPrompt,
        source);

    // ── Templates ────────────────────────────────────────────────────────────────

    // Parity: LoadsAllRealPromptTemplates, RealPromptSetIsConsistent (product templates only)
    [Fact]
    public void LoadsTheSevenEmbeddedTemplates()
    {
        var all = Store.All();
        Assert.Equal(7, all.Count);
        Assert.Equal(PromptTemplateStore.Required.Values.Order(), all.Select(t => t.Id).Order());
        Assert.All(all, t =>
        {
            Assert.NotEmpty(t.Version);
            Assert.DoesNotContain("---", t.Body.Split('\n')[0], StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", t.Sha256);
            Assert.False(t.Overridden);
        });
        Assert.Empty(Store.OverriddenFiles);

        var user = Store.Get("generation/user/project_aware.md");
        Assert.Equal(new[] { "cut_fqn", "cut_path", "test_package", "test_class_name", "repository_configuration", "cut_source" }.Order(), user.Placeholders.Order());
        Assert.StartsWith("Generate a unit test class", user.Body, StringComparison.Ordinal);
        Assert.Empty(Store.Get("repair/system.md").Placeholders);
        Assert.Throws<ArrowException>(() => Store.Get("generation/user/nope.md"));
    }

    private const string Header = "---\nid: repair.system\nversion: v9\nrole: system\nused_by: test\n";

    // Parity: TemplateShaIgnoresLineEndingsAndFrontMatterIsRequired
    [Fact]
    public void ShaIgnoresLineEndingsAndTheFrontMatterIsRequired()
    {
        var lf = PromptTemplateStore.Parse("repair/system.md", Header + "---\nBody line\n", "repair.system", false);
        var crlf = PromptTemplateStore.Parse("repair/system.md", (Header + "---\nBody line\n").Replace("\n", "\r\n", StringComparison.Ordinal), "repair.system", false);
        Assert.Equal(lf.Sha256, crlf.Sha256);
        Assert.Equal("Body line\n", crlf.Body);
        Assert.Equal(("v9", "system"), (lf.Version, lf.Role));

        Assert.Equal(ErrorCodes.B2TemplateInvalid, Assert.Throws<ArrowException>(() => PromptTemplateStore.Parse("x.md", "no front matter", "x", false)).Code);
        Assert.Equal(ErrorCodes.B2TemplateInvalid, Assert.Throws<ArrowException>(() => PromptTemplateStore.Parse("x.md", "---\nid: x\nno closing line", "x", false)).Code);
    }

    [Fact]
    public void PlaceholdersMustMatchInBothDirectionsAndTheIdMustBeRight()
    {
        var extraInBody = Assert.Throws<ArrowException>(() => PromptTemplateStore.Parse("a.md", Header + "---\nHello {{name}}\n", "repair.system", false));
        Assert.Contains("name", extraInBody.Message, StringComparison.Ordinal);
        var missingInBody = Assert.Throws<ArrowException>(() => PromptTemplateStore.Parse("a.md", Header + "placeholders: name\n---\nHello\n", "repair.system", false));
        Assert.Contains("name", missingInBody.Message, StringComparison.Ordinal);
        var wrongId = Assert.Throws<ArrowException>(() => PromptTemplateStore.Parse("a.md", Header + "---\nHello\n", "repair.user.level1", false));
        Assert.Contains("repair.system", wrongId.Message, StringComparison.Ordinal);
        Assert.All(new[] { extraInBody, missingInBody, wrongId }, e => Assert.Equal(ErrorCodes.B2TemplateInvalid, e.Code));
    }

    // Parity: RenderFillsEveryPlaceholderOnce, RenderRejectsMissingExtraOrUndeclaredValues
    [Fact]
    public void RenderFillsEveryPlaceholderInOnePass()
    {
        var template = PromptTemplateStore.Parse("t.md", "---\nid: t\nversion: v1\nrole: user\nplaceholders: a, b\n---\n{{a}} and {{b}} and {{a}}\n", "t", false);
        // A value that itself contains {{b}} (source code can) is not filled again.
        Assert.Equal("{{b}} and 2 and {{b}}\n", PromptTemplateStore.Render(template, new Dictionary<string, string> { ["a"] = "{{b}}", ["b"] = "2" }));

        Assert.Contains("b", Assert.Throws<ArrowException>(() => PromptTemplateStore.Render(template, new Dictionary<string, string> { ["a"] = "1" })).Message, StringComparison.Ordinal);
        Assert.Contains("c", Assert.Throws<ArrowException>(() => PromptTemplateStore.Render(template, new Dictionary<string, string> { ["a"] = "1", ["b"] = "2", ["c"] = "3" })).Message, StringComparison.Ordinal);
    }

    private string WriteOverride(string path, string content)
    {
        var file = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return _root;
    }

    [Fact]
    public void AnOverrideReplacesOnlyItsFileAndKeepsThePlaceholders()
    {
        var embedded = Store.Get("repair/system.md");
        var dir = WriteOverride("repair/system.md", "---\nid: repair.system\nversion: custom-1\nrole: system\n---\nYou are my own repair assistant.\n");

        var store = new PromptTemplateStore(dir);

        Assert.Equal(["repair/system.md"], store.OverriddenFiles);
        var custom = store.Get("repair/system.md");
        Assert.Equal(("custom-1", true, "You are my own repair assistant.\n"), (custom.Version, custom.Overridden, custom.Body));
        Assert.NotEqual(embedded.Sha256, custom.Sha256);
        // Other files keep the embedded text.
        Assert.Equal(Store.Get("repair/user/level1.md").Sha256, store.Get("repair/user/level1.md").Sha256);
        Assert.False(store.Get("repair/user/level1.md").Overridden);
    }

    [Fact]
    public void AnOverrideWithAnotherSetOfPlaceholdersIsRefused()
    {
        var level1 = Store.Get("repair/user/level1.md");
        var fewer = "---\nid: repair.user.level1\nversion: x\nrole: user\nplaceholders: cut_fqn\n---\n{{cut_fqn}}\n";
        var error = Assert.Throws<ArrowException>(() => new PromptTemplateStore(WriteOverride("repair/user/level1.md", fewer)));
        Assert.Equal(ErrorCodes.B2TemplateInvalid, error.Code);
        Assert.Contains("repair/user/level1.md", error.Message, StringComparison.Ordinal);
        Assert.Contains("error_block", error.Message, StringComparison.Ordinal);
        Assert.NotNull(level1);

        // A missing folder is an error too, not a silent fallback.
        Assert.Equal(ErrorCodes.B2TemplateInvalid, Assert.Throws<ArrowException>(() => new PromptTemplateStore(Path.Combine(_root, "nowhere"))).Code);
    }

    // ── Builder ──────────────────────────────────────────────────────────────────

    [Fact]
    public void GenerationPromptHasTheMessagesTemplatesAndFullMetadata()
    {
        var prompt = PromptBuilder.Generation(Store, Input());

        Assert.Equal(["system", "user"], prompt.Messages.Select(m => m.Role));
        Assert.Equal(["generation.system.zero_shot_project_aware", "generation.user.project_aware"], prompt.Templates.Select(t => t.Id));
        Assert.Equal(Store.Get("generation/user/project_aware.md").Sha256, prompt.Templates[1].Sha256);
        var user = prompt.Messages[1].Content;
        Assert.DoesNotContain("{{", user, StringComparison.Ordinal);
        Assert.Contains("- Fully qualified name: com.acme.shop.OrderService", user, StringComparison.Ordinal);
        Assert.Contains("- Source file: shop-core/src/main/java/com/acme/shop/OrderService.java", user, StringComparison.Ordinal);
        Assert.Contains("- Package: com.acme.shop\n- Class name: OrderService_ArrowTest", user, StringComparison.Ordinal);
        Assert.Contains("Repository configuration (compatibility constraints):\n- Build tool: Maven 3.9.6", user, StringComparison.Ordinal);
        Assert.Contains("```java\npackage com.acme.shop;\npublic class OrderService { public long totalPrice(long id) { return 0; } }\n```", user, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", prompt.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceCodeWithBracesAndPlaceholdersIsPutInAsItIs()
    {
        var source = "class A { String s = \"{{cut_fqn}}\"; }";
        Assert.Contains("String s = \"{{cut_fqn}}\";", PromptBuilder.Generation(Store, Input(source)).Messages[1].Content, StringComparison.Ordinal);
    }

    private static RepairRequest Request(int level, IReadOnlyList<FailedAttempt>? attempts = null, IReadOnlyList<string>? rewrite = null, VerificationState state = VerificationState.Compile) => new(
        "com.acme.shop.OrderService", 3, level, level, state,
        "package com.acme.shop;\npublic class OrderService_ArrowTest { }\n", 1,
        "OrderService_ArrowTest.java:[14,30] cannot find symbol\n  symbol:   method getTotal(long)",
        level >= 2 ? "Constructors, methods and fields of com.acme.shop.OrderService:\n  public long totalPrice(long orderId)" : null,
        attempts ?? [], rewrite ?? []);

    // Parity: RepairPromptsByLevelAlwaysCarryFullMetadata
    [Fact]
    public void RepairPromptsByLevelAlwaysCarryFullMetadata()
    {
        FailedAttempt[] attempts = [new(1, "COMPILE | cannot find symbol | method", "cannot find symbol: method getTotal(long)", "-  a\n+  b")];
        var level1 = PromptBuilder.Repair(Store, Input(), Request(1));
        var level2 = PromptBuilder.Repair(Store, Input(), Request(2, attempts));
        var level3 = PromptBuilder.Repair(Store, Input(), Request(3, attempts, ["totalPriceAddsTax", "cancelsTwice"]));

        foreach (var prompt in new[] { level1, level2, level3 })
        {
            Assert.Equal(["repair.system"], prompt.Templates.Take(1).Select(t => t.Id));
            var text = prompt.Messages[1].Content;
            Assert.StartsWith("The test class below failed ARROW's verification. It does not compile.", text, StringComparison.Ordinal);
            Assert.Contains("Repository configuration (compatibility constraints):\n- Build tool: Maven 3.9.6", text, StringComparison.Ordinal);
            Assert.Contains("Failure output:\nOrderService_ArrowTest.java:[14,30] cannot find symbol", text, StringComparison.Ordinal);
            Assert.Contains("Current test class:\n```java\npackage com.acme.shop;\npublic class OrderService_ArrowTest { }\n```", text, StringComparison.Ordinal);
            Assert.Contains("Target CUT com.acme.shop.OrderService (shop-core/src/main/java/com/acme/shop/OrderService.java):", text, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Approaches that already failed", level1.Messages[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Additional information", level1.Messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("Additional information for this failure:\nConstructors, methods and fields of com.acme.shop.OrderService:", level2.Messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("- Round 1 · COMPILE | cannot find symbol | method\n  First error: cannot find symbol: method getTotal(long)\n  Changed code:\n    -  a\n    +  b", level2.Messages[1].Content, StringComparison.Ordinal);
        // P02 §3.2 said one name per line with "- "; level3.md reads "…in these test methods: {{failing_methods}}.", so the list is inline.
        Assert.Contains("The failure is located in these test methods: `totalPriceAddsTax`, `cancelsTwice`.", level3.Messages[1].Content, StringComparison.Ordinal);
        Assert.Equal("repair.user.level3", level3.Templates[1].Id);
    }

    [Fact]
    public void EmptyExtraContextAndAttemptsAreNone()
    {
        var level2 = PromptBuilder.Repair(Store, Input(), Request(2) with { ExtraContext = "  ", ErrorBlock = "" }).Messages[1].Content;
        Assert.Contains("Additional information for this failure:\n(none)", level2, StringComparison.Ordinal);
        Assert.Contains("Failure output:\n(none)", level2, StringComparison.Ordinal);
        Assert.Contains(":\n(none)\n\nCurrent test class", level2, StringComparison.Ordinal);
    }

    // Parity: FailedAttemptsShowDetailsOnlyForLastThree. Empty is "(none)" (P02 §3.4), where the old code wrote "- (none)".
    [Fact]
    public void FailedAttemptsShowDetailsOnlyForTheLastThree()
    {
        const string change = "-        long total = service.getTotal(1L);\n+        long total = service.calcTotal(1L);";
        FailedAttempt[] attempts =
        [
            new(1, "COMPILE | cannot find symbol | method", "cannot find symbol: method getTotal(long, boolean)", "-x\n+y"),
            new(2, "COMPILE | cannot find symbol | method", "cannot find symbol: method calcTotal(long) in class com.acme.shop.OrderService", change),
            new(3, "duplicate code (same as round 2)", null, change),
            new(4, "invalid output: no Java class found", "", null),
        ];

        Assert.Equal("""
            - Round 1 · COMPILE | cannot find symbol | method
            - Round 2 · COMPILE | cannot find symbol | method
              First error: cannot find symbol: method calcTotal(long) in class com.acme.shop.OrderService
              Changed code:
                -        long total = service.getTotal(1L);
                +        long total = service.calcTotal(1L);
            - Round 3 · duplicate code (same as round 2)
              Changed code:
                -        long total = service.getTotal(1L);
                +        long total = service.calcTotal(1L);
            - Round 4 · invalid output: no Java class found
            """.ReplaceLineEndings("\n"), PromptBuilder.FormatAttempts(attempts));
        Assert.Equal("(none)", PromptBuilder.FormatAttempts([]));

        // Zero, one and five attempts; the order is by round.
        Assert.Equal("- Round 1 · s\n  First error: e", PromptBuilder.FormatAttempts([new(1, "s", "e", null)]));
        var five = PromptBuilder.FormatAttempts(Enumerable.Range(1, 5).Reverse().Select(r => new FailedAttempt(r, "sig" + r, "err" + r, null)).ToList());
        Assert.Equal(["- Round 1 · sig1", "- Round 2 · sig2"], five.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Take(2));
        Assert.DoesNotContain("err1", five, StringComparison.Ordinal);
        Assert.DoesNotContain("err2", five, StringComparison.Ordinal);
        Assert.Contains("err3", five, StringComparison.Ordinal);
        Assert.Contains("err5", five, StringComparison.Ordinal);
    }

    // Parity: FailureSummaryComesFromDataFile
    [Fact]
    public void FailureSummaryComesFromTheDataFile()
    {
        var summaries = Store.Get("repair/failure_summaries.md");
        Assert.Equal("Its test methods run, but some assertions fail.", PromptBuilder.FailureSummary(summaries, VerificationState.Assertion));
        Assert.StartsWith("Its test methods pass when run alone", PromptBuilder.FailureSummary(summaries, VerificationState.Regression), StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.B2TemplateInvalid, Assert.Throws<ArrowException>(() => PromptBuilder.FailureSummary(summaries, VerificationState.Valid)).Code);
    }

    [Fact]
    public void RepairStatesChangeTheSummarySentence()
    {
        Assert.Contains("It compiles, but the test runner did not discover", PromptBuilder.Repair(Store, Input(), Request(1, state: VerificationState.Discovery)).Messages[1].Content, StringComparison.Ordinal);
        Assert.Equal("repair/user/level2.md", PromptBuilder.RepairUserPath(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PromptBuilder.RepairUserPath(4));
        Assert.Equal("OrderService_Arrow2Test", PromptBuilder.TestClassName("com.acme.shop.OrderService_Arrow2Test"));
        Assert.Equal("A_ArrowTest", PromptBuilder.TestClassName("A_ArrowTest"));
    }
}
