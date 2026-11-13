using Arrow.DataAccess.Build;

namespace Arrow.Business.Analysis;

/// <summary>Baseline test ids grouped as in <c>baseline.json</c> (P01 §6.2 step 3).</summary>
public sealed record BaselineGroups(IReadOnlyList<string> Passing, IReadOnlyList<string> Flaky, IReadOnlyList<string> Failing);

/// <summary>Merges the results of the baseline runs (P01 §6.2).</summary>
public static class BaselineEvaluator
{
    /// <summary>
    /// Passed in every run → passing; failed or errored in every run → failing; anything else (passed in some runs, or missing from a
    /// run) → flaky. A test skipped in every run where it appears belongs to no group. A test reported twice in one run counts with its
    /// worst status. Ids are sorted.
    /// </summary>
    public static BaselineGroups Classify(IReadOnlyList<IReadOnlyList<TestCaseResult>> runs)
    {
        var perRun = runs.Select(run => run.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => Worst(g.Select(t => t.Status)))).ToList();
        List<string> passing = [], flaky = [], failing = [];
        foreach (var id in perRun.SelectMany(r => r.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var statuses = perRun.Select(r => r.TryGetValue(id, out var s) ? s : (TestCaseStatus?)null).ToList();
            if (statuses.All(s => s is TestCaseStatus.Skipped or null))
            {
                continue;
            }

            if (statuses.All(s => s == TestCaseStatus.Passed))
            {
                passing.Add(id);
            }
            else if (statuses.All(s => s is TestCaseStatus.Failed or TestCaseStatus.Error))
            {
                failing.Add(id);
            }
            else
            {
                flaky.Add(id);
            }
        }

        return new BaselineGroups(passing, flaky, failing);
    }

    private static TestCaseStatus Worst(IEnumerable<TestCaseStatus> statuses)
    {
        var list = statuses.ToList();
        return list.Contains(TestCaseStatus.Error) ? TestCaseStatus.Error
            : list.Contains(TestCaseStatus.Failed) ? TestCaseStatus.Failed
            : list.Contains(TestCaseStatus.Passed) ? TestCaseStatus.Passed
            : TestCaseStatus.Skipped;
    }
}
