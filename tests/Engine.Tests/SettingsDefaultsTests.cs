using Coda.Agent.Settings;

namespace Engine.Tests;

public sealed class SettingsDefaultsTests : IDisposable
{
    private readonly string root;

    public SettingsDefaultsTests()
    {
        this.root = Path.Combine(Path.GetTempPath(), "coda_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.root);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private string WriteUserSettings(string json)
    {
        var dir = Path.Combine(this.root, ".coda");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
        return this.root;
    }

    [Fact]
    public void Load_reads_default_provider_and_model()
    {
        var home = WriteUserSettings("""{ "defaultProvider": "github-copilot", "defaultModel": "claude-sonnet-4" }""");

        var settings = SettingsLoader.Load(Path.Combine(this.root, "project"), home);

        Assert.Equal("github-copilot", settings.DefaultProvider);
        Assert.Equal("claude-sonnet-4", settings.DefaultModel);
    }

    [Fact]
    public void Project_default_overrides_user_default()
    {
        var home = WriteUserSettings("""{ "defaultProvider": "claude-ai" }""");
        var projectDir = Path.Combine(this.root, "project");
        Directory.CreateDirectory(Path.Combine(projectDir, ".coda"));
        File.WriteAllText(Path.Combine(projectDir, ".coda", "settings.json"), """{ "defaultProvider": "github-copilot" }""");

        var settings = SettingsLoader.Load(projectDir, home);

        Assert.Equal("github-copilot", settings.DefaultProvider);
    }

    [Fact]
    public void Writer_sets_default_preserving_other_keys()
    {
        var home = WriteUserSettings("""{ "permissions": { "allow": ["read_file"] } }""");

        SettingsWriter.SetUserDefaults(defaultProvider: "github-copilot", userSettingsDir: home);

        var settings = SettingsLoader.Load(Path.Combine(this.root, "project"), home);
        Assert.Equal("github-copilot", settings.DefaultProvider);
        Assert.Contains("read_file", settings.Allow); // existing key preserved
    }

    [Fact]
    public void Writer_empty_value_clears_the_key()
    {
        var home = WriteUserSettings("""{ "defaultModel": "claude-opus-4-8" }""");

        SettingsWriter.SetUserDefaults(defaultModel: string.Empty, userSettingsDir: home);

        var settings = SettingsLoader.Load(Path.Combine(this.root, "project"), home);
        Assert.Null(settings.DefaultModel);
    }

    [Fact]
    public void Writer_creates_file_when_missing()
    {
        SettingsWriter.SetUserDefaults(defaultProvider: "anthropic-api-key", defaultModel: "claude-opus-4-8", userSettingsDir: this.root);

        var settings = SettingsLoader.Load(Path.Combine(this.root, "project"), this.root);
        Assert.Equal("anthropic-api-key", settings.DefaultProvider);
        Assert.Equal("claude-opus-4-8", settings.DefaultModel);
    }

    [Fact]
    public void Goal_block_round_trips_from_user_settings()
    {
        var home = WriteUserSettings(
            """
            {
              "goal": {
                "maxDuration": "1.00:00:00",
                "maxContinuations": 500,
                "autoCompact": false,
                "extensionFraction": 0.5
              }
            }
            """);

        var settings = SettingsLoader.Load(Path.Combine(this.root, "project"), home);

        Assert.NotNull(settings.Goal);
        Assert.Equal(TimeSpan.FromDays(1), settings.Goal.MaxDuration);
        Assert.Equal(500, settings.Goal.MaxContinuations);
        Assert.False(settings.Goal.AutoCompact);
        Assert.Equal(0.5, settings.Goal.ExtensionFraction);
    }

    [Fact]
    public void Goal_block_maxDuration_accepts_human_friendly_suffix()
    {
        // Same suffix forms as the CLI/serve (e.g. "24h"), not only "dd.hh:mm:ss".
        var home = WriteUserSettings("""{ "goal": { "maxDuration": "24h" } }""");

        var settings = SettingsLoader.Load(Path.Combine(this.root, "project"), home);

        Assert.NotNull(settings.Goal);
        Assert.Equal(TimeSpan.FromHours(24), settings.Goal.MaxDuration);
    }

    [Fact]
    public void Goal_block_project_overrides_user_per_field()
    {
        var home = WriteUserSettings(
            """
            {
              "goal": {
                "maxDuration": "02:00:00",
                "maxContinuations": 100
              }
            }
            """);

        var projectDir = Path.Combine(this.root, "project");
        Directory.CreateDirectory(Path.Combine(projectDir, ".coda"));
        File.WriteAllText(
            Path.Combine(projectDir, ".coda", "settings.json"),
            """{ "goal": { "maxContinuations": 999 } }""");

        var settings = SettingsLoader.Load(projectDir, home);

        Assert.NotNull(settings.Goal);
        // Project overrides MaxContinuations.
        Assert.Equal(999, settings.Goal.MaxContinuations);
        // User MaxDuration survives (project did not set it).
        Assert.Equal(TimeSpan.FromHours(2), settings.Goal.MaxDuration);
    }
}
