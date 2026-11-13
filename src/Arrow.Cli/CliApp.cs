using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using Arrow.Business.UseCases;
using Arrow.Cli.Commands;
using Arrow.Cli.Rendering;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Arrow.Cli;

/// <summary>What a command needs while it runs.</summary>
public sealed record CommandContext(
    GlobalOptions Globals,
    CliConsole Console,
    IRenderer Renderer,
    ErrorCatalog Catalog,
    IAppLog Log,
    IServiceProvider Services)
{
    /// <summary>The project of the current folder, used by the project layer of the configuration.</summary>
    public string ProjectRoot => Console.CurrentDirectory;

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
}

/// <summary>
/// The <c>arrow</c> command line (P08): global options, commands, rendering mode, error output and exit codes.
/// </summary>
public sealed class CliApp
{
    /// <summary>Name shown in help and error hints.</summary>
    public const string CommandName = "arrow";

    private readonly CliConsole _console;
    private readonly Func<CliConsole, GlobalOptions, IServiceProvider> _compose;

    private readonly Option<bool> _plain = new("--plain") { Description = "Không màu, ký hiệu ASCII", Recursive = true };
    private readonly Option<bool> _json = new("--json") { Description = "In kết quả dạng JSON ra stdout", Recursive = true };
    private readonly Option<bool> _verbose = new("--verbose", "-v") { Description = "In thêm log chi tiết ra stderr", Recursive = true };
    private readonly Option<bool> _yes = new("--yes", "-y") { Description = "Đồng ý mọi câu hỏi", Recursive = true };
    private readonly Option<string?> _home = new("--home") { Description = "Thư mục dữ liệu, thay cho ARROW_HOME", Recursive = true };

    /// <param name="console">Streams and environment; tests pass their own.</param>
    /// <param name="compose">Builds the services; defaults to <see cref="CompositionRoot"/>.</param>
    public CliApp(CliConsole console, Func<CliConsole, GlobalOptions, IServiceProvider>? compose = null)
    {
        _console = console;
        _compose = compose ?? CompositionRoot.Build;
    }

    /// <summary>Parses <paramref name="args"/>, runs the command and returns the exit code of P08 §8.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var root = BuildCommands();
        var parse = root.Parse(args);
        var configuration = new InvocationConfiguration { Output = _console.Out, Error = _console.Error, EnableDefaultExceptionHandler = false };

        if (parse.Action is ParseErrorAction)
        {
            var renderer = new Renderer(RenderMode.Plain, _console.Out, _console.Error);
            var catalog = ErrorCatalog.For(null);
            var help = string.Join(' ', [CommandName, .. CommandPath(parse.CommandResult), "--help"]);
            var detail = string.Join(" ", parse.Errors.Select(e => e.Message).Distinct(StringComparer.Ordinal));
            renderer.Error(catalog.Describe(ErrorCodes.CliArgs, detail, new Dictionary<string, string> { ["help_command"] = help }));
            return ExitCodes.Usage;
        }

        return await parse.InvokeAsync(configuration, ct).ConfigureAwait(false);
    }

    private Command BuildCommands()
    {
        // A plain Command named "arrow" rather than RootCommand, whose name would be the assembly name (Arrow.Cli).
        // The final command name and PackageId are still open (CLAUDE.md); change CommandName when they are decided.
        var root = new Command(CommandName, "ARROW: sinh unit test Java bằng LLM, xác minh trên build thật và sửa tự động.");
        root.Options.Add(new HelpOption());
        root.Options.Add(new VersionOption());
        foreach (var option in new Option[] { _plain, _json, _verbose, _yes, _home })
        {
            root.Options.Add(option);
        }

        root.Subcommands.Add(ConfigCommand.Build(this));
        root.Subcommands.Add(SecretsCommand.Build(this));

        // GĐ6 opens the interactive session here (P08 §5). Until then, "arrow" alone shows the help.
        root.SetAction((parse, ct) => root.Parse(["--help"]).InvokeAsync(
            new InvocationConfiguration { Output = _console.Out, Error = _console.Error }, ct));
        return root;
    }

    /// <summary>
    /// Runs one command body with its context and turns failures into output and exit codes:
    /// coded errors print code, message and hint (exit 2); Ctrl+C exits 3; anything else is logged with its stack trace (exit 4).
    /// </summary>
    internal async Task<int> Execute(ParseResult parse, CancellationToken ct, Func<CommandContext, CancellationToken, Task<int>> body)
    {
        var globals = new GlobalOptions(parse.GetValue(_plain), parse.GetValue(_json), parse.GetValue(_verbose), parse.GetValue(_yes), parse.GetValue(_home));
        var services = _compose(_console, globals);
        try
        {
            var log = services.GetRequiredService<IAppLog>();
            var (color, language) = UiSettings(services);
            var renderer = new Renderer(
                RenderModes.Select(globals.Plain, globals.Json, _console.IsOutputRedirected, _console.Environment.GetValueOrDefault("NO_COLOR"), color),
                _console.Out,
                _console.Error);
            var context = new CommandContext(globals, _console, renderer, ErrorCatalog.For(language), log, services);
            log.Info("arrow " + string.Join(' ', parse.Tokens.Select(t => t.Value)));
            try
            {
                return await body(context, ct).ConfigureAwait(false);
            }
            catch (ArrowException ex)
            {
                log.Warning($"{ex.Code}: {ex.Message}");
                renderer.Error(context.Catalog.Describe(ex));
                return ExitCodes.For(ex);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                renderer.Warn("Đã huỷ.");
                return ExitCodes.Cancelled;
            }
            catch (Exception ex)
            {
                log.Error("Unexpected exception", ex);
                renderer.Error(context.Catalog.Describe(ErrorCodes.CliInternal, args: new Dictionary<string, string> { ["log_path"] = log.FilePath }));
                return ExitCodes.Internal;
            }
        }
        finally
        {
            (services as IDisposable)?.Dispose();
        }
    }

    /// <summary>Colors and language from the configuration; a broken configuration falls back to the defaults here.</summary>
    private (ColorMode Color, string Language) UiSettings(IServiceProvider services)
    {
        try
        {
            var ui = services.GetRequiredService<ConfigUseCase>().List(_console.CurrentDirectory).Effective.Config.Ui;
            return (ui.Color, ui.Language);
        }
        catch (ArrowException)
        {
            return (ColorMode.Auto, ErrorCatalog.DefaultLanguage);
        }
    }

    private static IEnumerable<string> CommandPath(System.CommandLine.Parsing.CommandResult? result)
    {
        var names = new Stack<string>();
        for (var current = result; current?.Parent is not null; current = current.Parent as System.CommandLine.Parsing.CommandResult)
        {
            names.Push(current.Command.Name);
        }

        return names;
    }
}
