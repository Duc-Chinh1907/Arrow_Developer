using System.Text.Json.Nodes;
using Arrow.Business;
using Arrow.Business.Analysis;
using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Arrow.Cli.Tests;

/// <summary>Output and exit codes of <c>arrow inspect</c> and <c>arrow doctor</c>, with fake use cases (P08 §2.3, §2.6, §8; P11).</summary>
public class InspectDoctorCommandTests
{
    private sealed class FakeInspect(Func<ProjectTarget, InspectResult> result) : IInspectUseCase
    {
        public ProjectTarget? Target { get; private set; }

        public IReadOnlyList<string>? Command { get; private set; }

        public string? ProjectRoot { get; private set; }

        public Task<InspectResult> RunAsync(ProjectTarget target, IReadOnlyList<string> command, string? projectRoot, IProgress<ArrowEvent>? progress, IConfirmation confirm, CancellationToken ct)
        {
            (Target, Command, ProjectRoot) = (target, command, projectRoot);
            progress?.Report(new ArrowEvent(DateTimeOffset.UtcNow, ArrowEventTypes.B1Compile, "x", null, null, EventLevel.Info, "Biên dịch với JDK 11", new Dictionary<string, object?>()));
            progress?.Report(new ArrowEvent(DateTimeOffset.UtcNow, ArrowEventTypes.RunStarted, null, null, null, EventLevel.Info, "run-level", new Dictionary<string, object?>()));
            return Task.FromResult(result(target));
        }
    }

    private sealed class FakeDoctor(DoctorReport report) : IDoctorUseCase
    {
        public Task<DoctorReport> RunAsync(string? projectRoot, Action<DoctorItem>? onItem, CancellationToken ct)
        {
            foreach (var item in report.Items)
            {
                onItem?.Invoke(item);
            }

            return Task.FromResult(report);
        }
    }

    private static RepositoryMetadata Metadata() => new(
        new SourceMetadata(ProjectSourceKind.LocalDirectory, "MyShop", null, "ab12", true),
        new BuildToolMetadata(BuildToolKind.Maven, "3.9.9", true, false, ".", BuildMode.Reactor, "pom.xml", "shop-core", "detected:reactor", [], []),
        new JavaMetadata("11", "maven.compiler.release", 11, 11, "declared"),
        new DependencyMetadata([new DependencyRef("a", "b", "1", "compile")], [new DependencyRef("junit", "junit", "4.13.2", "test")], 9),
        new TestFrameworkMetadata("junit5", "5.10.2", false, ["org.mockito:mockito-core:5.11.0"], [], "dependency"),
        new ProjectStructureMetadata(true, ["shop-api", "shop-core"], "shop-core", "shop-core", ["com.shop"]),
        new PathsMetadata("shop-core/src/main/java", "shop-core/src/test/java", "shop-core/src/main/java/com/shop/Order.java",
            "shop-core/src/test/java/com/shop/Order_ArrowTest.java", "com.shop.Order_ArrowTest", 1, true),
        new CutMetadata("com.shop.Order", "Order", "com.shop", CutKind.Class, "sha"),
        new Dictionary<string, string>());

    private static InspectResult Ok() => new(
        "20261113-101010-abcd",
        "/home/runs/20261113-101010-abcd",
        BlockOutcome.Ok(61.2),
        new ProjectAnalysis(
            BlockOutcome.Ok(61.2),
            Metadata(),
            new BaselineInfo(2, new BaselineCommands(["mvnw"], ["mvnw"]), ".", ["a.T#x", "a.T#y", "a.T#z"], [], ["a.B#f"], new BaselineDurations(1, 2, [3, 4])),
            new WorkspaceInfo("work/r/base", "work/r/snapshot-1", "sha256:1", 11, "/jdk11", BuildToolKind.Maven, "mvnw", true, ".", BuildMode.Reactor, "detected:reactor", ["-pl", "shop-core", "-am"], null, ["-B"], []),
            new CutInfo("com.shop.Order", "Order", "com.shop", CutKind.Class, "shop-core/src/main/java/com/shop/Order.java", "sha", "shop-core/src/test/java/com/shop/Order_ArrowTest.java", "com.shop.Order_ArrowTest", 1),
            ["Có 1 test fail sẵn"],
            new StepTimings(0.1, 0.2, 2.1, 3.4, [3.7, 3.5], 12.7),
            null),
        [],
        ["Có 1 test fail sẵn"]);

    private static InspectResult Blocked() => new(
        "20261113-101010-abcd",
        "/home/runs/20261113-101010-abcd",
        new BlockOutcome(BlockOutcomeKind.Blocked, ErrorCodes.B1CutMissing, "Không tìm thấy class Ordr.", "Dùng FQN hoặc đường dẫn file .java.", null, null, 0),
        null,
        ["com.shop.Order"],
        []);

    private static CliRunner Cli(IInspectUseCase? inspect = null, IDoctorUseCase? doctor = null)
    {
        var cli = new CliRunner();
        cli.Services = services =>
        {
            if (inspect is not null)
            {
                services.AddSingleton(inspect);
            }

            if (doctor is not null)
            {
                services.AddSingleton(doctor);
            }
        };
        return cli;
    }

    [Fact]
    public async Task InspectPrintsTheSummaryOfP08()
    {
        var fake = new FakeInspect(_ => Ok());
        using var cli = Cli(fake);
        var run = await cli.RunAsync("inspect", "Order", "--project", cli.Project);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(("Order", ProjectSourceKind.LocalDirectory), (fake.Target!.CutQuery, fake.Target.Kind));
        Assert.Equal(Path.GetFullPath(cli.Project), fake.Target.Location);
        Assert.Equal("inspect", fake.Command![0]);
        Assert.Contains("[>] Biên dịch với JDK 11", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("run-level", run.Out, StringComparison.Ordinal);
        foreach (var line in new[]
        {
            "[OK] com.shop.Order: khối 1 xong trong 12.7 s",
            "Build tool    Maven 3.9.9 (wrapper)",
            "Build root    . · reactor · -pl shop-core -am",
            "Java          khai báo 11 (maven.compiler.release) · dùng JDK 11 (declared)",
            "Test          JUnit 5 5.10.2 · mock: org.mockito:mockito-core:5.11.0 · assertion: không",
            "Dependency    9 đã phân giải (khai báo: 1 chính, 1 test)",
            "Source root   shop-core/src/main/java",
            "Test root     shop-core/src/test/java",
            "Test mới      shop-core/src/test/java/com/shop/Order_ArrowTest.java",
            "Baseline      3 pass · 0 flaky · 1 fail (2 lần chạy)",
            "Thời gian     lấy mã 0.1 s · liệt kê class 0.2 s · tìm cấu hình 2.1 s · biên dịch 3.4 s · baseline 3.7 s + 3.5 s",
            "[!] Có 1 test fail sẵn",
        })
        {
            Assert.Contains(line, run.Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InspectJsonHasOnlyTheResultOnStdout()
    {
        using var cli = Cli(new FakeInspect(_ => Ok()));
        var run = await cli.RunAsync("inspect", "Order", "--project", cli.Project, "--json");

        Assert.Equal(0, run.ExitCode);
        var json = JsonNode.Parse(run.Out)!;
        Assert.Equal("20261113-101010-abcd", (string?)json["run_id"]);
        Assert.Equal("OK", (string?)json["outcome"]!["outcome"]);
        Assert.Equal("shop-core", (string?)json["analysis"]!["metadata"]!["build_tool"]!["reactor_module"]);
        Assert.Equal(3, json["analysis"]!["baseline"]!["passing_in_all_runs"]!.AsArray().Count);
        Assert.Equal("com.shop.Order_ArrowTest", (string?)json["analysis"]!["cut"]!["generated_test_fqn"]);
        // Progress lines go to stderr in JSON mode.
        Assert.Contains("[>] Biên dịch với JDK 11", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlockedClassExitsWithCode2AndShowsSuggestions()
    {
        using var cli = Cli(new FakeInspect(_ => Blocked()));
        var run = await cli.RunAsync("inspect", "Ordr", "--project", cli.Project);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("Gần giống: com.shop.Order", run.Out, StringComparison.Ordinal);
        Assert.Contains("[X] B1_CUT_MISSING · Không tìm thấy class Ordr.", run.Out, StringComparison.Ordinal);
        Assert.Contains("→ Cách sửa: Dùng FQN", run.Out, StringComparison.Ordinal);

        var json = await cli.RunAsync("inspect", "Ordr", "--project", cli.Project, "--json");
        Assert.Equal(ExitCodes.Usage, json.ExitCode);
        Assert.Equal("BLOCKED", (string?)JsonNode.Parse(json.Out)!["outcome"]!["outcome"]);
        Assert.Equal("com.shop.Order", (string?)JsonNode.Parse(json.Out)!["suggestions"]![0]);
    }

    [Fact]
    public async Task RepoAndRefBecomeAGitTarget()
    {
        var fake = new FakeInspect(_ => Ok());
        using var cli = Cli(fake);
        var run = await cli.RunAsync("inspect", "EdgeTypeStore", "--repo", "https://github.com/gephi/graphstore", "--ref", "v0.7.1");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal((ProjectSourceKind.GitRepository, "https://github.com/gephi/graphstore", "v0.7.1"), (fake.Target!.Kind, fake.Target.Location, fake.Target.GitRef));
    }

    [Theory]
    [InlineData("inspect", "X", "--repo", "https://github.com/a/b", "--project", ".")]
    [InlineData("inspect", "X", "--ref", "main")]
    [InlineData("inspect")]
    public async Task WrongArgumentsFailBeforeAnythingRuns(params string[] args)
    {
        var fake = new FakeInspect(_ => throw new InvalidOperationException("must not run"));
        using var cli = Cli(fake);
        var run = await cli.RunAsync(args);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("CLI_ARGS", run.Out, StringComparison.Ordinal);
        Assert.Contains("arrow inspect --help", run.Out, StringComparison.Ordinal);
        Assert.Null(fake.Target);
        Assert.DoesNotContain(cli.Files(), f => f.Path.Contains("runs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnvironmentErrorsExitWithCode2()
    {
        var fake = new FakeInspect(_ => throw new ArrowException(ErrorCodes.B1JdkMissing, args: new Dictionary<string, string> { ["jdk"] = "17" }));
        using var cli = Cli(fake);
        var run = await cli.RunAsync("inspect", "X", "--project", cli.Project);
        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("B1_JDK_MISSING", run.Out, StringComparison.Ordinal);
        Assert.Contains("Cài JDK 17", run.Out, StringComparison.Ordinal);
    }

    private static DoctorReport Report(params DoctorItem[] items) => new(items);

    [Fact]
    public async Task DoctorPrintsEachLineAndTheVerdict()
    {
        var report = Report(
            new DoctorItem(DoctorLevel.Ok, "git", "2.46.0", null),
            new DoctorItem(DoctorLevel.Warning, "JDK 21", "không tìm thấy (chỉ cần cho project Java 21)", null));
        using var cli = Cli(doctor: new FakeDoctor(report));
        var run = await cli.RunAsync("doctor");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Kiểm tra môi trường", run.Out, StringComparison.Ordinal);
        Assert.Contains("[OK] " + "git".PadRight(22) + " 2.46.0", run.Out, StringComparison.Ordinal);
        Assert.Contains("[!] " + "JDK 21".PadRight(22) + " không tìm thấy", run.Out, StringComparison.Ordinal);
        Assert.Contains("Kết quả: SẴN SÀNG (1 cảnh báo)", run.Out, StringComparison.Ordinal);

        using var clean = Cli(doctor: new FakeDoctor(Report(new DoctorItem(DoctorLevel.Ok, "git", "2.46.0", null))));
        Assert.Contains("Kết quả: SẴN SÀNG\n", (await clean.RunAsync("doctor")).Out.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorErrorsGiveExitCode2WithTheFix()
    {
        var report = Report(new DoctorItem(DoctorLevel.Error, "JDK", "không tìm thấy JDK 8, 11, 17 hay 21", "Cài JDK 17, rồi chạy \"arrow setup\"."));
        using var cli = Cli(doctor: new FakeDoctor(report));
        var run = await cli.RunAsync("doctor");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("[X] JDK", run.Out, StringComparison.Ordinal);
        Assert.Contains("→ Cách sửa: Cài JDK 17", run.Out, StringComparison.Ordinal);
        Assert.Contains("Kết quả: CHƯA SẴN SÀNG (1 lỗi)", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorJson()
    {
        var report = Report(new DoctorItem(DoctorLevel.Ok, "git", "2.46.0", null), new DoctorItem(DoctorLevel.Error, "JDK", "x", "fix"));
        using var cli = Cli(doctor: new FakeDoctor(report));
        var run = await cli.RunAsync("doctor", "--json");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        var json = JsonNode.Parse(run.Out)!;
        Assert.False((bool)json["ready"]!);
        Assert.Equal((0, 1), ((int)json["warnings"]!, (int)json["errors"]!));
        Assert.Equal("ERROR", (string?)json["items"]![1]!["level"]);
        Assert.Equal("fix", (string?)json["items"]![1]!["hint"]);
    }
}
