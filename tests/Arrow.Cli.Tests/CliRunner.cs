using Microsoft.Extensions.DependencyInjection;

namespace Arrow.Cli.Tests;

/// <summary>Output of one in-process run of the CLI.</summary>
public sealed record CliRun(int ExitCode, string Out, string Error)
{
    public string All => Out + Error;
}

/// <summary>Runs <see cref="CliApp"/> in-process with a temporary <c>ARROW_HOME</c> and fake streams.</summary>
public sealed class CliRunner : IDisposable
{
    public CliRunner()
    {
        Home = Path.Combine(Path.GetTempPath(), "arrow-cli-tests", Guid.NewGuid().ToString("N")[..12]);
        Project = Path.Combine(Home, "project");
        Directory.CreateDirectory(Project);
    }

    /// <summary>The <c>ARROW_HOME</c> of this test.</summary>
    public string Home { get; }

    /// <summary>Replaces services after the real ones are registered, e.g. use cases with fakes; null keeps the real ones.</summary>
    public Action<IServiceCollection>? Services { get; set; }

    /// <summary>The current folder of the CLI, standing for a user project.</summary>
    public string Project { get; }

    /// <summary>Environment variables seen by the CLI. Never the real environment, so a real key cannot leak in.</summary>
    public Dictionary<string, string?> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<CliRun> RunAsync(params string[] args) => RunWithInputAsync("", args);

    /// <summary>Same as <see cref="RunAsync"/> with <paramref name="stdin"/> as the redirected input.</summary>
    public async Task<CliRun> RunWithInputAsync(string stdin, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var console = new CliConsole(new StringReader(stdin), output, error, IsInputRedirected: true, IsOutputRedirected: true, Environment, Project);
        var exit = await new CliApp(console, (c, g) => CompositionRoot.Build(c, g, Services)).RunAsync([.. args, "--home", Home], TestContext.Current.CancellationToken);
        return new CliRun(exit, output.ToString(), error.ToString());
    }

    /// <summary>Every file under <c>ARROW_HOME</c>, with its text.</summary>
    public IEnumerable<(string Path, string Text)> Files() =>
        Directory.GetFiles(Home, "*", SearchOption.AllDirectories).Select(f => (f, File.ReadAllText(f)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Home, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
