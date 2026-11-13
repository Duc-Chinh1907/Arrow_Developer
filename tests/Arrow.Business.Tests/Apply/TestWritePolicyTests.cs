using System.Security.Cryptography;
using System.Text;
using Arrow.Business.Analysis;
using Arrow.Business.Apply;
using Arrow.Business.Repair;
using Arrow.Business.Tests.Repair;
using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Generation;
using Arrow.Common.Verification;
using Arrow.DataAccess.Apply;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Tests.Apply;

/// <summary>The write step of P06 with a real temporary project, a fake verifier and a scripted user (P06 §9).</summary>
public sealed class TestWritePolicyTests : IDisposable
{
    private const string CutSource = "package com.acme;\r\n\r\npublic class Order {\r\n}\r\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-apply", Guid.NewGuid().ToString("N")[..8]);
    private readonly string _project;
    private readonly RunStore _runs;
    private readonly RunHandle _run;
    private readonly List<ArrowEvent> _events = [];

    public TestWritePolicyTests()
    {
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(_project, "src", "main", "java", "com", "acme"));
        Directory.CreateDirectory(Path.Combine(_project, "src", "test", "java", "com", "acme"));
        File.WriteAllText(Path.Combine(_project, "src", "main", "java", "com", "acme", "Order.java"), CutSource);
        var home = new ArrowHome(Path.Combine(_root, "home"));
        home.EnsureCreated();
        _runs = new RunStore(home);
        _run = _runs.Create(new RunInfo("r1", "test", DateTimeOffset.UtcNow, null, RunStatus.Running, ["gen"],
            new RunProject(ProjectSourceKind.LocalDirectory, "p", null, null, null, null, null, null), null,
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

    private RunContext Run => new("r1", Path.Combine(_root, "home"), Path.Combine(_root, "home", "work", "r1"), _run, new ListProgress(_events));

    private TargetRun Target(string cut = "Order", VerificationState state = VerificationState.Valid, string? sha = null, string testPath = "src/test/java/com/acme/Order_ArrowTest.java")
    {
        var fqn = "com.acme." + cut;
        var m = new RepositoryMetadata(
            new SourceMetadata(ProjectSourceKind.LocalDirectory, "p", null, null, null),
            new BuildToolMetadata(BuildToolKind.Maven, "3.9.6", false, false, ".", BuildMode.Standalone, "pom.xml", null, "detected:standalone", [], []),
            null, null, null,
            new ProjectStructureMetadata(false, [], ".", ".", ["com.acme"]),
            new PathsMetadata("src/main/java", "src/test/java", $"src/main/java/com/acme/{cut}.java", testPath, fqn + "_ArrowTest", 1, true),
            new CutMetadata(fqn, cut, "com.acme", CutKind.Class, sha ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CutSource)))),
            new Dictionary<string, string>());
        var code = $"package com.acme;\n\npublic class {cut}_ArrowTest {{\n    @org.junit.Test public void a() {{}}\n}}\n";
        var candidate = new CandidateTest(true, null, null, code, fqn + "_ArrowTest", testPath, "sha256:x", 1, 1, [], [], "stop");
        var verify = new VerifyResult { State = state, GeneratedTestRanInModule = true };
        var final = new FinalTestSuite { CutFqn = fqn, C0State = state, FinalState = state, Candidate = candidate, Verify = verify };
        var task = new TaskContext(Run, fqn, m, new BaselineInfo(1, new BaselineCommands([], []), ".", [], [], [], new BaselineDurations(0, 0, [])),
            new WorkspaceInfo("b", "s", null, 8, "/jdk", BuildToolKind.Maven, "mvn", false, ".", BuildMode.Standalone, "x", [], null, [], []), CutSource, "targets/" + fqn, []);
        var result = new TargetResult(fqn, state.ToOutcome(), state, 0, 0, null, candidate.TestFqn, testPath, "block3/final.java",
            new TargetMetrics(0.78, 0.5, 1, 0.61, 0.7, 0.5), null, null, TokenUsage.Zero, null, 1, new BlockSeconds(1, 1, 1, 1, 4), null, [], null);
        return new TargetRun(result, final, m, [], task);
    }

    private ApplyContext Context(bool yes = false, bool dryRun = false, bool remote = false, bool confirmSetting = true) =>
        new(Run, remote ? null : _project, yes, dryRun, confirmSetting, TimeSpan.FromMinutes(1), 1, TimeSpan.Zero);

    private sealed class FakeVerifier(VerificationState state) : IVerifier
    {
        public List<SuiteFiles> Suites { get; } = [];

        public Task<VerifyResult> VerifyAsync(SuiteFiles suite, TaskContext task, VerifyOptions options, CancellationToken ct)
        {
            Suites.Add(suite);
            return Task.FromResult(new VerifyResult { State = state });
        }
    }

    /// <summary>A user that answers from a script and records the questions.</summary>
    private sealed class ScriptedUser(bool canAsk = true, bool confirm = true, int? choice = 0, IReadOnlyList<int>? many = null) : IConfirmation
    {
        public List<string> Questions { get; } = [];

        public IReadOnlyList<bool>? Preselected { get; private set; }

        public bool CanAsk => canAsk;

        public Task<bool> ConfirmAsync(string question, bool defaultAnswer, CancellationToken ct)
        {
            Questions.Add(question);
            return Task.FromResult(confirm);
        }

        public Task<int?> ChooseOneAsync(string question, IReadOnlyList<string> options, CancellationToken ct)
        {
            Questions.Add(question + " | " + string.Join(" | ", options));
            return Task.FromResult(choice);
        }

        public Task<IReadOnlyList<int>?> ChooseManyAsync(string question, IReadOnlyList<string> options, IReadOnlyList<bool> preselected, CancellationToken ct)
        {
            Questions.Add(question + " | " + string.Join(" | ", options));
            Preselected = preselected;
            return Task.FromResult(many);
        }
    }

    private TestWritePolicy Policy(FakeVerifier? verifier = null) =>
        new(new ProjectFileWriter(), verifier ?? new FakeVerifier(VerificationState.Valid), new FakeHelperProvider(new FakeHelper()), _runs, ErrorCatalog.For("vi"));

    private async Task<IReadOnlyDictionary<string, ApplySummary>> Apply(IConfirmation user, ApplyContext context, FakeVerifier? verifier = null, params TargetRun[] targets) =>
        await ApplyStep.RunAsync(Policy(verifier), targets, context, user, [], TestContext.Current.CancellationToken);

    private string TestFile(string name) => Path.Combine(_project, "src", "test", "java", "com", "acme", name);

    private ApplyRecord Record(string cut = "Order") => _runs.ReadJson<ApplyRecord>(_run, $"targets/com.acme.{cut}/apply.json")!;

    [Fact]
    public async Task OneClassIsAskedAndWrittenWithTheLineEndingsOfTheCut()
    {
        var user = new ScriptedUser();
        var result = await Apply(user, Context(), null, Target());
        Assert.Equal(ApplyDecision.Written, result["com.acme.Order"].Decision);
        Assert.Equal("Ghi src/test/java/com/acme/Order_ArrowTest.java (1 test · line 78% · mutation 61%)?", Assert.Single(user.Questions));
        Assert.Contains("\r\n", File.ReadAllText(TestFile("Order_ArrowTest.java")), StringComparison.Ordinal);
        Assert.Equal(("crlf", false), (Record().LineEndings, Record().RenamedAfterRun));
    }

    // P06 §9: the matrix of --yes, --dry-run, keyboard and apply.confirm.
    [Theory]
    [InlineData(true, false, true, true, ApplyDecision.Written)]
    [InlineData(false, false, true, false, ApplyDecision.Written)]
    [InlineData(false, true, true, true, ApplyDecision.DryRun)]
    [InlineData(false, false, false, true, ApplyDecision.SkippedNonInteractive)]
    public async Task DecisionsFollowTheOptions(bool yes, bool dryRun, bool keyboard, bool confirmSetting, ApplyDecision expected)
    {
        var user = new ScriptedUser(canAsk: keyboard);
        var result = await Apply(user, Context(yes, dryRun, confirmSetting: confirmSetting), null, Target());
        Assert.Equal(expected, result["com.acme.Order"].Decision);
        Assert.Empty(user.Questions);
        Assert.Equal(expected == ApplyDecision.Written, File.Exists(TestFile("Order_ArrowTest.java")));
    }

    [Fact]
    public async Task NotValidAndRemoteAreNeverAsked()
    {
        var user = new ScriptedUser();
        Assert.Equal(ApplyDecision.NotValid, (await Apply(user, Context(), null, Target(state: VerificationState.Assertion)))["com.acme.Order"].Decision);
        Assert.Equal(ApplyDecision.RemoteProject, (await Apply(user, Context(remote: true), null, Target()))["com.acme.Order"].Decision);
        Assert.Empty(user.Questions);
    }

    [Fact]
    public async Task AUserWhoSaysNoDeclines()
    {
        var result = await Apply(new ScriptedUser(confirm: false), Context(), null, Target());
        Assert.Equal(ApplyDecision.Declined, result["com.acme.Order"].Decision);
        Assert.False(File.Exists(TestFile("Order_ArrowTest.java")));
    }

    // P06 §3 step 5: a changed CUT is asked with "no" as default, and never written with --yes.
    [Fact]
    public async Task AChangedCutIsNotWrittenWithYes()
    {
        var withYes = await Apply(new ScriptedUser(), Context(yes: true), null, Target(sha: "other"));
        Assert.Equal(ApplyDecision.Blocked, withYes["com.acme.Order"].Decision);
        Assert.Equal(ErrorCodes.WProjectChanged, Record().Error!.Code);

        var user = new ScriptedUser(confirm: false);
        var asked = await Apply(user, Context(), null, Target(sha: "other"));
        Assert.Equal(ApplyDecision.Declined, asked["com.acme.Order"].Decision);
        Assert.StartsWith("Order đã thay đổi sau khi ARROW đọc nó", Assert.Single(user.Questions), StringComparison.Ordinal);
    }

    // P06 §4.2 (b): the name appeared during the run, so the class is renamed, verified again and written as _Arrow2Test.
    [Fact]
    public async Task ANameThatAppearedIsRenamedAndVerifiedAgain()
    {
        File.WriteAllText(TestFile("Order_ArrowTest.java"), "the user's file");
        var verifier = new FakeVerifier(VerificationState.Valid);
        var user = new ScriptedUser(choice: 0);
        var result = await Apply(user, Context(), verifier, Target());

        Assert.Equal((ApplyDecision.Written, "src/test/java/com/acme/Order_Arrow2Test.java"), (result["com.acme.Order"].Decision, result["com.acme.Order"].Path));
        Assert.Equal("the user's file", File.ReadAllText(TestFile("Order_ArrowTest.java")));
        Assert.Contains("public class Order_Arrow2Test", File.ReadAllText(TestFile("Order_Arrow2Test.java")), StringComparison.Ordinal);
        var suite = Assert.Single(verifier.Suites);
        Assert.Equal("com.acme.Order_Arrow2Test", suite.TestFqn);
        Assert.Equal("Đã có Order_ArrowTest.java trong project. Ghi bản mới thành: | 1. Order_Arrow2Test.java (giữ nguyên file cũ) | 2. Không ghi, chỉ lưu trong runs/", Assert.Single(user.Questions));
        Assert.Equal((true, true, 2), (Record().RenamedAfterRun, Record().Reverified, Record().NameIndex));
        Assert.Contains(_events, e => e.Type == ArrowEventTypes.ApplyRenamed);
        Assert.Contains(_events, e => e.Type == ArrowEventTypes.ApplyReverified);
    }

    [Fact]
    public async Task ARenamedSuiteThatIsNoLongerValidIsNotWritten()
    {
        File.WriteAllText(TestFile("Order_ArrowTest.java"), "the user's file");
        var result = await Apply(new ScriptedUser(), Context(yes: true), new FakeVerifier(VerificationState.Compile), Target());
        Assert.Equal(ApplyDecision.Blocked, result["com.acme.Order"].Decision);
        Assert.Equal(ErrorCodes.WReverifyFailed, Record().Error!.Code);
        Assert.False(File.Exists(TestFile("Order_Arrow2Test.java")));
        Assert.NotNull(_runs.ReadText(_run, "targets/com.acme.Order/apply/Order_Arrow2Test.java"));
    }

    [Fact]
    public async Task APathOutsideTheTestRootIsBlocked()
    {
        var result = await Apply(new ScriptedUser(), Context(yes: true), null, Target(testPath: "src/main/java/com/acme/Order_ArrowTest.java"));
        Assert.Equal(ApplyDecision.Blocked, result["com.acme.Order"].Decision);
        Assert.Equal(ErrorCodes.WTargetOutsideProject, Record().Error!.Code);
    }

    // P06 §4.3: several classes, one multi-select list; a changed CUT is not ticked.
    [Fact]
    public async Task SeveralClassesAreAskedOnce()
    {
        File.WriteAllText(Path.Combine(_project, "src", "main", "java", "com", "acme", "Cart.java"), CutSource);
        var user = new ScriptedUser(many: [1]);
        var result = await Apply(user, Context(), null,
            Target(), Target("Cart", testPath: "src/test/java/com/acme/Cart_ArrowTest.java", sha: "other"));

        var question = Assert.Single(user.Questions);
        Assert.StartsWith("Chọn file test để ghi vào project | Order_ArrowTest.java", question, StringComparison.Ordinal);
        Assert.Contains("[!] CUT đã thay đổi", question, StringComparison.Ordinal);
        Assert.Equal([true, false], user.Preselected);
        Assert.Equal((ApplyDecision.Declined, ApplyDecision.Written), (result["com.acme.Order"].Decision, result["com.acme.Cart"].Decision));
    }

    private sealed class ListProgress(List<ArrowEvent> events) : IProgress<ArrowEvent>
    {
        public void Report(ArrowEvent value) => events.Add(value);
    }
}
