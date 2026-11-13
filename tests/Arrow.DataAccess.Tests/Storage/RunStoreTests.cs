using System.Text.RegularExpressions;
using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.DataAccess.Storage;

namespace Arrow.DataAccess.Tests.Storage;

/// <summary>Run folders, <c>run.json</c>, events and history (P09 §5).</summary>
public class RunStoreTests
{
    /// <summary>Parity: <c>ARROW_Framework/tests/Arrow.DataAccess.Tests/RunRepositoryTests.EventsAreRedactedBeforeWriting</c>.</summary>
    [Fact]
    public void EventsAreRedactedBeforeWriting()
    {
        using var temp = new TempDir();
        var (store, run) = NewRun(temp);
        store.AppendEvent(run, new ArrowEvent(DateTimeOffset.UnixEpoch, ArrowEventTypes.LlmRetry, null, null, null, EventLevel.Warning,
            "Authorization: Bearer abcdefghijklmnopqrstuvwxyz api_key: sk-zzzzzzzzzzzzzzzz9999", new Dictionary<string, object?>()));
        var text = File.ReadAllText(Path.Combine(run.Root, RunStore.EventsFile));
        Assert.DoesNotContain("abcdefghijklmnop", text, StringComparison.Ordinal);
        Assert.DoesNotContain("zzzzzzzzzzzz", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Parity: <c>ARROW_Framework/tests/Arrow.DataAccess.Tests/RunRepositoryTests.TaskResultsSaveLoadDeleteAndRejectEscapingPaths</c>.
    /// The task/model/technique folders of the research layout become <c>targets/&lt;cut_fqn&gt;/…</c> (P09 §5.2).
    /// </summary>
    [Fact]
    public void ResultsSaveLoadDeleteAndRejectEscapingPaths()
    {
        using var temp = new TempDir();
        var (store, run) = NewRun(temp);
        const string target = "targets/com.acme.OrderService";
        store.WriteJson(run, target + "/block1/outcome.json", new ErrorInfo("B1_JDK_MISSING", "m", "h", null));
        store.WriteText(run, target + "/block3/round_2/candidate.java", "class A {}");

        Assert.Equal("B1_JDK_MISSING", store.ReadJson<ErrorInfo>(run, target + "/block1/outcome.json")!.Code);
        Assert.Equal("class A {}", store.ReadText(run, target + "/block3/round_2/candidate.java"));
        Assert.True(store.Exists(run, target + "/block3"));

        store.Delete(run, target + "/block3");
        Assert.False(store.Exists(run, target + "/block3"));
        Assert.Null(store.ReadJson<ErrorInfo>(run, target + "/block1/none.json"));

        Assert.Throws<ArgumentException>(() => store.WriteText(run, "../../escape.txt", "x"));
        Assert.Throws<ArgumentException>(() => store.WriteText(run, Path.GetFullPath(Path.Combine(temp.Root, "abs.txt")), "x"));
        Assert.Throws<ArgumentException>(() => store.WriteText(run, "targets/../../other/x.txt", "x"));
    }

    [Fact]
    public void RunIdsFollowP09AndStayUnique()
    {
        using var temp = new TempDir();
        var store = new RunStore(new ArrowHome(temp.Root));
        var at = new DateTime(2026, 11, 10, 14, 25, 30, DateTimeKind.Local);
        var ids = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
        {
            var id = store.NewUniqueRunId(at);
            Assert.Matches(new Regex("^20261110-142530-[a-z0-9]{4}$"), id);
            Assert.True(ids.Add(id));
            store.Create(Info(id));
        }
    }

    [Fact]
    public void RunJsonIsWrittenOnCreateAndUpdate()
    {
        using var temp = new TempDir();
        var (store, run) = NewRun(temp);
        Assert.Contains("\"status\": \"RUNNING\"", File.ReadAllText(Path.Combine(run.Root, RunStore.RunFile)), StringComparison.Ordinal);

        store.Update(run, Info(run.RunId) with { Status = RunStatus.Completed });
        Assert.Equal(RunStatus.Completed, store.ReadJson<RunInfo>(run, RunStore.RunFile)!.Status);
        Assert.Throws<InvalidOperationException>(() => store.Create(Info(run.RunId)));
    }

    [Fact]
    public void OpensRunsByIdOrUniquePrefix()
    {
        using var temp = new TempDir();
        var store = new RunStore(new ArrowHome(temp.Root));
        store.Create(Info("20261110-142530-aaaa"));
        store.Create(Info("20261110-142530-aabb"));
        store.Create(Info("20261111-090000-cccc"));

        Assert.Equal("20261111-090000-cccc", store.Open("20261111").RunId);
        Assert.Equal("20261110-142530-aaaa", store.Open("20261110-142530-aaaa").RunId);
        Assert.Equal(ErrorCodes.CliArgs, Assert.Throws<ArrowException>(() => store.Open("20261110-1425")).Code);
        var missing = Assert.Throws<ArrowException>(() => store.Open("2025"));
        Assert.Equal(ErrorCodes.GenResumeNotFound, missing.Code);
        Assert.Equal("2025", missing.Args["run_id"]);
        Assert.Equal(ErrorCodes.GenResumeNotFound, Assert.Throws<ArrowException>(() => store.Open("../runs")).Code);
    }

    [Fact]
    public void HistoryIsNewestFirstAndSkipsDamagedLines()
    {
        using var temp = new TempDir();
        var home = new ArrowHome(temp.Root);
        var store = new RunStore(home);
        store.AppendHistory(History("r1"));
        File.AppendAllText(home.HistoryFile, "{broken\n");
        store.AppendHistory(History("r2"));
        store.AppendHistory(History("r3"));

        var history = store.History(2);
        Assert.Equal(["r3", "r2"], history.Entries.Select(e => e.RunId));
        Assert.Equal(1, history.SkippedLines);
    }

    private static (RunStore Store, RunHandle Run) NewRun(TempDir temp)
    {
        var store = new RunStore(new ArrowHome(temp.Root));
        var run = store.Create(Info(store.NewUniqueRunId(DateTime.Now)));
        return (store, run);
    }

    private static RunInfo Info(string runId) => new(
        runId, "0.1.0", DateTimeOffset.UnixEpoch, null, RunStatus.Running, ["gen", "OrderService"],
        new RunProject(ProjectSourceKind.LocalDirectory, "MyShop", "D:/src/MyShop", null, null, null, null, null),
        new RunConfig("gpt-x", "api.teamorouter.com", 5, 3, true, true),
        new Dictionary<string, string>(), new Dictionary<string, string>(), [], RunTotals.Zero);

    private static HistoryEntry History(string runId) =>
        new(runId, DateTimeOffset.UnixEpoch, "MyShop", ["com.acme.A"], 1, 0, RunStatus.Completed);
}
