using Arrow.Business.UseCases;
using Arrow.Common.Logging;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.Logging;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Security;
using Arrow.DataAccess.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Arrow.Cli;

/// <summary>Options shared by every command (P08 §2.1).</summary>
public sealed record GlobalOptions(bool Plain, bool Json, bool Verbose, bool Yes, string? Home);

/// <summary>
/// Registers the DataAccess and Business implementations for dependency injection (P08 §9).
/// It is the only Cli type allowed to use <c>Arrow.DataAccess</c>; <c>ArchitectureTests</c> checks this.
/// </summary>
internal static class CompositionRoot
{
    /// <summary>Builds the services of one command run. <c>--home</c> overrides <c>ARROW_HOME</c>.</summary>
    public static ServiceProvider Build(CliConsole console, GlobalOptions globals)
    {
        var env = console.Environment;
        var home = globals.Home is { Length: > 0 } dir
            ? new ArrowHome(Path.GetFullPath(Path.Combine(console.CurrentDirectory, dir)))
            : ArrowHome.Resolve(name => env.GetValueOrDefault(name));

        var services = new ServiceCollection();
        services.AddSingleton(home);
        services.AddSingleton<IAppLog>(_ => new FileAppLog(home, globals.Verbose, console.Error));
        services.AddSingleton<IConfigStore>(_ => new UserConfigStore(home, () => env));
        services.AddSingleton<ISecretStore>(_ => new SecretStore(home.SecretsDir, name => env.GetValueOrDefault(name), () => env.Keys));
        services.AddSingleton<IRunStore>(_ => new RunStore(home));
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ConfigUseCase>();
        services.AddSingleton<SecretsUseCase>();
        return services.BuildServiceProvider();
    }
}
