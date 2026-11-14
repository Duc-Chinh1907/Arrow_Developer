using Arrow.Business;
using Arrow.Business.Analysis;
using Arrow.Business.Environment;
using Arrow.Business.Llm;
using Arrow.Business.UseCases;
using Arrow.Cli.Prompts;
using Arrow.Common.Logging;
using Arrow.DataAccess.Apply;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.Llm;
using Arrow.DataAccess.Logging;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Security;
using Arrow.DataAccess.Storage;
using Arrow.DataAccess.Tools;
using Arrow.Common.Errors;
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
    /// <summary>
    /// Builds the services of one command run. <c>--home</c> overrides <c>ARROW_HOME</c>. <paramref name="configure"/> runs last, so
    /// tests can replace use cases with fakes (P11: output of the commands is tested with fake use cases).
    /// </summary>
    public static ServiceProvider Build(CliConsole console, GlobalOptions globals, Action<IServiceCollection>? configure = null)
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
        var userProfile = env.GetValueOrDefault("USERPROFILE") ?? env.GetValueOrDefault("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Func<string, string?> getEnvironment = name => env.GetValueOrDefault(name);
        services.AddSingleton(sp => new ToolDiscovery(sp.GetRequiredService<IProcessRunner>(), getEnvironment, userProfile));
        services.AddSingleton<IAnalyzerHostFactory>(sp => new AnalyzerHostFactory(
            home, sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IRunStore>(), getEnvironment, userProfile, sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IConfirmation>(_ => globals.Yes || globals.Json || !console.HasKeyboard ? new NoConfirmation(globals.Yes) : new SpectrePrompter());
        services.AddSingleton<IInspectUseCase>(sp => new InspectUseCase(
            sp.GetRequiredService<IConfigStore>(), home, sp.GetRequiredService<ToolDiscovery>(), sp.GetRequiredService<IRunStore>(),
            sp.GetRequiredService<IAnalyzerHostFactory>(), CliApp.Version));
        services.AddSingleton(_ => new OllamaClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }));
        services.AddSingleton<ILlmClientFactory>(sp => new LlmClientFactory(
            sp.GetRequiredService<ISecretStore>(), sp.GetRequiredService<IRunStore>(), "arrow-cli/" + CliApp.Version, sp.GetRequiredService<IAppLog>(),
            fakeRulesFile: CliApp.FakeLlmRules(env)));
        services.AddSingleton<IGenerateTestsUseCase>(sp => new GenerateTestsUseCase(
            sp.GetRequiredService<IConfigStore>(), home, sp.GetRequiredService<ToolDiscovery>(), sp.GetRequiredService<IRunStore>(),
            sp.GetRequiredService<IAnalyzerHostFactory>(), sp.GetRequiredService<ILlmClientFactory>(), new ProjectFileWriter(),
            sp.GetRequiredService<IToolStore>(), sp.GetRequiredService<IProcessRunner>(), CliApp.Version));
        services.AddSingleton<ISessionUseCase>(sp => new SessionUseCase(sp.GetRequiredService<IConfigStore>(), sp.GetRequiredService<ToolDiscovery>(), home));
        services.AddSingleton<IShowRunUseCase>(sp => new ShowRunUseCase(sp.GetRequiredService<IRunStore>()));
        services.AddSingleton<IToolStore>(_ => new ToolStore(home, new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, getEnvironment));
        services.AddSingleton<IToolsUseCase>(sp => new ToolsUseCase(sp.GetRequiredService<IToolStore>(), ErrorCatalog.For("vi")));
        services.AddSingleton(sp => new ModelsUseCase(
            sp.GetRequiredService<IConfigStore>(), sp.GetRequiredService<ISecretStore>(), sp.GetRequiredService<ILlmClientFactory>(), sp.GetRequiredService<OllamaClient>()));
        services.AddSingleton<IModelsUseCase>(sp => sp.GetRequiredService<ModelsUseCase>());
        services.AddSingleton<IModelDiagnostics>(sp => sp.GetRequiredService<ModelsUseCase>());
        services.AddSingleton<IWizardPrompter, SpectreWizardPrompter>();
        services.AddSingleton<IDoctorUseCase>(sp => new DoctorUseCase(
            sp.GetRequiredService<IConfigStore>(), home, sp.GetRequiredService<ToolDiscovery>(), sp.GetRequiredService<IProcessRunner>(), new JavaHelperProbe(home),
            models: sp.GetRequiredService<IModelDiagnostics>()));
        services.AddSingleton<ConfigUseCase>();
        services.AddSingleton<SecretsUseCase>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
