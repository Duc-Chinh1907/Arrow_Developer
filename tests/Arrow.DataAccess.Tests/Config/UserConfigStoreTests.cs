using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.Storage;

namespace Arrow.DataAccess.Tests.Config;

/// <summary>Five configuration layers (P09 §3).</summary>
public class UserConfigStoreTests
{
    private static readonly Dictionary<string, string?> NoOverrides = [];

    [Fact]
    public void DefaultsAloneGiveTheValuesOfP09()
    {
        using var temp = new TempDir();
        var effective = Store(temp).Load(null, NoOverrides);
        var config = effective.Config;
        Assert.Equal((5, 3), (config.Repair.Rounds, config.Repair.EarlyStopP));
        Assert.Equal(["jacoco", "pit", "tsdetect"], config.Quality.Tools);
        Assert.Equal((ColorMode.Auto, "vi"), (config.Ui.Color, config.Ui.Language));
        Assert.Null(config.Llm.DefaultModel);
        Assert.All(effective.Entries, e => Assert.Equal(ConfigOrigin.Default, e.Origin));
        Assert.Null(effective.ProjectConfigPath);
    }

    [Fact]
    public void LayersOverrideKeyByKeyAndRememberTheOrigin()
    {
        using var temp = new TempDir();
        var home = temp.PathOf("home");
        var project = temp.PathOf("project");
        temp.Write("home/config.json", """
            { "schema_version": 1,
              "repair": { "rounds": 7 },
              "run": { "jobs": 2 },
              "ui": { "language": "en" },
              "llm": { "default_model": "gpt-x",
                       "models": { "gpt-x": { "provider": "teamo", "model_id": "gpt-4.1-mini", "params": { "temperature": 0 } } },
                       "providers": { "teamo": { "kind": "openai-compatible", "base_url": "https://api.teamorouter.com/v1", "secret_ref": "teamo" } } } }
            """);
        temp.Write("project/.arrow.json", """{ "repair": { "rounds": 4, "early_stop_p": null }, "run": { "jobs": 3 } }""");
        var env = new Dictionary<string, string?> { ["ARROW_RUN__JOBS"] = "4", ["ARROW_QUALITY__TOOLS"] = "jacoco,pit", ["ARROW_HOME"] = "ignored" };

        var effective = Store(temp, env, home).Load(project, new Dictionary<string, string?> { ["repair.rounds"] = "2" });
        var config = effective.Config;

        Assert.Equal(2, config.Repair.Rounds);
        Assert.Null(config.Repair.EarlyStopP);
        Assert.Equal(4, config.Run.Jobs);
        Assert.Equal(["jacoco", "pit"], config.Quality.Tools);
        Assert.Equal("en", config.Ui.Language);
        Assert.Equal(ProviderKind.OpenAiCompatible, config.Llm.Providers["teamo"].Kind);
        Assert.Equal(0, config.Llm.Models["gpt-x"].Params!["temperature"].GetInt32());

        Assert.Equal(ConfigOrigin.CommandLine, effective.OriginOf("repair.rounds"));
        Assert.Equal(ConfigOrigin.Project, effective.OriginOf("repair.early_stop_p"));
        Assert.Equal(ConfigOrigin.Environment, effective.OriginOf("run.jobs"));
        Assert.Equal(ConfigOrigin.User, effective.OriginOf("ui.language"));
        Assert.Equal(ConfigOrigin.User, effective.OriginOf("llm.models.gpt-x.model_id"));
        Assert.Equal(ConfigOrigin.Default, effective.OriginOf("verification.baseline_runs"));
        Assert.Equal("2", effective.Entries.Single(e => e.Key == "repair.rounds").ValueJson);
        Assert.Equal(Path.Combine(project, ".arrow.json"), effective.ProjectConfigPath);
    }

    [Fact]
    public void RelativePathsResolveFromTheFileThatSetsThem()
    {
        using var temp = new TempDir();
        var home = temp.PathOf("home");
        var project = temp.PathOf("project");
        temp.Write("home/config.json", """{ "java": { "maven_path": "tools/mvn/bin/mvn", "jdk_homes": { "17": "jdk-17" } } }""");
        temp.Write("project/.arrow.json", """{ "prompts": { "override_dir": "my-prompts" } }""");

        var config = Store(temp, home: home).Load(project, NoOverrides).Config;

        Assert.Equal(Path.GetFullPath(Path.Combine(home, "tools/mvn/bin/mvn")), config.Java.MavenPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, "jdk-17")), config.Java.JdkHomes["17"]);
        Assert.Equal(Path.GetFullPath(Path.Combine(project, "my-prompts")), config.Prompts.OverrideDir);
    }

    [Fact]
    public void ProjectFileMustNotHoldAnApiKey()
    {
        using var temp = new TempDir();
        temp.Write("project/.arrow.json", """{ "llm": { "providers": { "teamo": { "kind": "openai-compatible", "base_url": "https://x/v1", "api_key": "sk-test-SECRET-123" } } } }""");
        var error = Assert.Throws<ArrowException>(() => Store(temp).Load(temp.PathOf("project"), NoOverrides));
        Assert.Equal(ErrorCodes.CfgSecretInProject, error.Code);
        Assert.DoesNotContain("sk-test-SECRET-123", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewerSchemaIsRefused()
    {
        using var temp = new TempDir();
        var file = temp.Write("home/config.json", """{ "schema_version": 2 }""");
        var error = Assert.Throws<ArrowException>(() => Store(temp, home: temp.PathOf("home")).Load(null, NoOverrides));
        Assert.Equal(ErrorCodes.CfgNewerSchema, error.Code);
        Assert.Equal(Path.GetFullPath(file), error.Args["file"]);
    }

    [Fact]
    public void BrokenJsonReportsLineAndColumnAndKeepsTheFile()
    {
        using var temp = new TempDir();
        var file = temp.Write("home/config.json", "{\n  \"repair\": { \"rounds\": 5 \n}");
        var error = Assert.Throws<ArrowException>(() => Store(temp, home: temp.PathOf("home")).Load(null, NoOverrides));
        Assert.Equal(ErrorCodes.CfgParse, error.Code);
        Assert.Equal("3", error.Args["line"]);
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData("repair.rounds", "abc")]
    [InlineData("repair.enabled", "yes")]
    [InlineData("repair.round", "5")]
    [InlineData("ui.color", "pink")]
    public void WrongValuesAndUnknownKeysAreCfgInvalid(string key, string value)
    {
        using var temp = new TempDir();
        var error = Assert.Throws<ArrowException>(() => Store(temp).Load(null, new Dictionary<string, string?> { [key] = value }));
        Assert.Equal(ErrorCodes.CfgInvalid, error.Code);
        Assert.Equal(key, error.Args["key"]);
        Assert.Equal("cli", error.Args["origin"]);
    }

    [Fact]
    public void SetKeepsOtherKeysAndTheirOrder()
    {
        using var temp = new TempDir();
        var home = temp.PathOf("home");
        var file = temp.Write("home/config.json", "{\n  \"schema_version\": 1,\n  \"ui\": { \"language\": \"en\" },\n  \"repair\": { \"enabled\": false }\n}\n");
        var store = Store(temp, home: home);

        store.Set(ConfigScope.User, "repair.rounds", "3", null);
        store.Set(ConfigScope.User, "run.max_tokens", "200000", null);
        store.Set(ConfigScope.User, "java.jdk_homes.17", "D:/jdk-17", null);

        var text = File.ReadAllText(file);
        Assert.True(text.IndexOf("\"ui\"", StringComparison.Ordinal) < text.IndexOf("\"repair\"", StringComparison.Ordinal));
        var config = store.Load(null, NoOverrides).Config;
        Assert.Equal(("en", false, 3), (config.Ui.Language, config.Repair.Enabled, config.Repair.Rounds));
        Assert.Equal(200000, config.Run.MaxTokens);
        Assert.Equal("D:/jdk-17", config.Java.JdkHomes["17"]);

        Assert.True(store.Unset(ConfigScope.User, "repair.rounds", null));
        Assert.False(store.Unset(ConfigScope.User, "repair.rounds", null));
        Assert.Equal(5, store.Load(null, NoOverrides).Config.Repair.Rounds);
    }

    [Fact]
    public void SetRefusesBadValuesWithoutTouchingTheFile()
    {
        using var temp = new TempDir();
        var home = temp.PathOf("home");
        var store = Store(temp, home: home);
        Assert.Equal(ErrorCodes.CfgInvalid, Assert.Throws<ArrowException>(() => store.Set(ConfigScope.User, "run.jobs", "many", null)).Code);
        Assert.False(File.Exists(Path.Combine(home, "config.json")));

        store.Set(ConfigScope.Project, "repair.rounds", "4", temp.PathOf("project"));
        Assert.True(File.Exists(temp.PathOf("project", ".arrow.json")));
        Assert.Equal(ErrorCodes.CfgSecretInProject, Assert.Throws<ArrowException>(
            () => store.Set(ConfigScope.Project, "llm.providers.teamo.api_key", "sk-test-SECRET-123", temp.PathOf("project"))).Code);
    }

    private static UserConfigStore Store(TempDir temp, Dictionary<string, string?>? env = null, string? home = null) =>
        new(new ArrowHome(home ?? temp.PathOf("home")), () => env ?? []);
}
