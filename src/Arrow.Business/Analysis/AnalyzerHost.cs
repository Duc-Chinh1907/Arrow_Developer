using Arrow.Common.Errors;
using Arrow.Common.Logging;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Analysis;

/// <summary>What a run needs to start block 1.</summary>
/// <param name="JavaExecutable">The <c>java</c> that runs java-helper (JDK 17 or newer).</param>
/// <param name="Git">Absolute path of git; null when there is none.</param>
/// <param name="Options">Block 1 settings.</param>
/// <param name="Catalog">Error texts in the UI language.</param>
/// <param name="Jobs">Number of java-helper processes (<c>--jobs</c>).</param>
public sealed record AnalyzerSetup(string JavaExecutable, string? Git, AnalysisOptions Options, ErrorCatalog Catalog, int Jobs = 1);

/// <summary>A started block 1 with its java-helper processes; disposing stops them.</summary>
public interface IAnalyzerHost : IAsyncDisposable
{
    /// <summary>Block 1.</summary>
    IProjectAnalyzer Analyzer { get; }
}

/// <summary>Creates <see cref="IAnalyzerHost"/>; use cases take this so that tests can use a fake block 1.</summary>
public interface IAnalyzerHostFactory
{
    /// <summary>
    /// Starts java-helper and checks that it answers (<c>ping</c>, P07 §2 step 6). Raises <c>JH_START_FAILED</c> or
    /// <c>JH_VERSION_MISMATCH</c> when it does not.
    /// </summary>
    Task<IAnalyzerHost> StartAsync(AnalyzerSetup setup, CancellationToken ct);
}

/// <summary><see cref="IAnalyzerHostFactory"/> with the real git, build tools and java-helper.</summary>
public sealed class AnalyzerHostFactory(ArrowHome home, IProcessRunner runner, IRunStore runs, Func<string, string?> getEnvironment, string userProfile, IAppLog? log = null) : IAnalyzerHostFactory
{
    public async Task<IAnalyzerHost> StartAsync(AnalyzerSetup setup, CancellationToken ct)
    {
        var pool = new JavaHelperPool(new JavaHelperOptions(setup.JavaExecutable, JavaHelperJar.Ensure(home)), Math.Max(1, setup.Jobs), log);
        try
        {
            using (var lease = await pool.RentAsync(ct).ConfigureAwait(false))
            {
                await lease.Helper.PingAsync(ct).ConfigureAwait(false);
            }

            var workspaces = new WorkspaceManager();
            var builds = new BuildExecutor(runner, getEnvironment, BuildFailureClassifier.UserCaches(userProfile));
            var analyzer = new ProjectAnalyzer(
                new ProjectSource(home.MirrorsDir, setup.Git is null ? null : new GitClient(runner, setup.Git), workspaces),
                token => pool.RentAsync(token),
                new ModuleAnalyzer(builds, workspaces, setup.Options, setup.Catalog),
                workspaces,
                runs,
                setup.Catalog);
            return new Host(analyzer, pool);
        }
        catch
        {
            await pool.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Host(IProjectAnalyzer analyzer, JavaHelperPool pool) : IAnalyzerHost
    {
        public IProjectAnalyzer Analyzer { get; } = analyzer;

        public ValueTask DisposeAsync() => pool.DisposeAsync();
    }
}
