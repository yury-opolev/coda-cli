using Coda.Agent.Settings;

namespace Engine.Tests.Settings;

/// <summary>
/// Tests that <see cref="SettingsLoader"/> is the single source of the resolved
/// <c>defaultProvider</c> and the per-provider <c>modelByProvider</c> map: merging user + project
/// files (project overrides user, per field / per provider), honoring <c>CODA_SETTINGS_DIR</c>, and
/// normalizing blank values away. These are the values both the TUI startup and <c>coda serve</c>
/// consume. There is intentionally no provider-agnostic default model.
/// </summary>
[Collection("env")]
public sealed class SettingsDefaultsTests : IDisposable
{
    private readonly string userHome;
    private readonly string projectDir;

    public SettingsDefaultsTests()
    {
        this.userHome = Path.Combine(Path.GetTempPath(), "coda-userdefaults-" + Guid.NewGuid().ToString("N"));
        this.projectDir = Path.Combine(Path.GetTempPath(), "coda-projdefaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(this.userHome, ".coda"));
        Directory.CreateDirectory(Path.Combine(this.projectDir, ".coda"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODA_SETTINGS_DIR", null);
        TryDelete(this.userHome);
        TryDelete(this.projectDir);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private void WriteUser(string json) =>
        File.WriteAllText(Path.Combine(this.userHome, ".coda", "settings.json"), json);

    private void WriteProject(string json) =>
        File.WriteAllText(Path.Combine(this.projectDir, ".coda", "settings.json"), json);

    [Fact]
    public void Exposes_user_defaults_when_only_user_file_present()
    {
        this.WriteUser("""
        {
            "defaultProvider": "github-copilot",
            "modelByProvider": { "github-copilot": "claude-opus-4" }
        }
        """);

        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Equal("github-copilot", settings.DefaultProvider);
        Assert.Equal("claude-opus-4", settings.ModelByProvider["github-copilot"]);
    }

    [Fact]
    public void Project_models_override_user_models_per_provider()
    {
        this.WriteUser("""
        {
            "defaultProvider": "github-copilot",
            "modelByProvider": { "github-copilot": "user-model", "anthropic-api-key": "user-anthropic" }
        }
        """);
        this.WriteProject("""
        {
            "modelByProvider": { "github-copilot": "project-model" }
        }
        """);

        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Equal("github-copilot", settings.DefaultProvider); // provider falls through to user
        Assert.Equal("project-model", settings.ModelByProvider["github-copilot"]); // project overrides
        Assert.Equal("user-anthropic", settings.ModelByProvider["anthropic-api-key"]); // user entry survives
    }

    [Fact]
    public void Honors_CODA_SETTINGS_DIR_for_user_defaults()
    {
        this.WriteUser("""
        {
            "defaultProvider": "github-copilot",
            "modelByProvider": { "github-copilot": "env-model" }
        }
        """);

        Environment.SetEnvironmentVariable("CODA_SETTINGS_DIR", this.userHome);

        // No explicit userSettingsDir → falls back to CODA_SETTINGS_DIR.
        var settings = SettingsLoader.Load(this.projectDir);

        Assert.Equal("github-copilot", settings.DefaultProvider);
        Assert.Equal("env-model", settings.ModelByProvider["github-copilot"]);
    }

    [Fact]
    public void Blank_values_normalize_away()
    {
        this.WriteUser("""
        {
            "defaultProvider": "   ",
            "modelByProvider": { "github-copilot": "  " }
        }
        """);

        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Null(settings.DefaultProvider);
        Assert.Empty(settings.ModelByProvider); // blank per-provider model values are dropped
    }

    [Fact]
    public void No_files_yields_empty_defaults()
    {
        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Null(settings.DefaultProvider);
        Assert.Empty(settings.ModelByProvider);
    }

    [Fact]
    public void Tool_display_mode_preserves_raw_user_value()
    {
        this.WriteUser("""{ "toolDisplayMode": "  CoMpAcT  " }""");

        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Equal("  CoMpAcT  ", settings.ToolDisplayMode);
    }

    [Fact]
    public void Project_tool_display_mode_cannot_override_user_value()
    {
        this.WriteUser("""{ "toolDisplayMode": "verbose" }""");
        this.WriteProject("""{ "toolDisplayMode": "tiny" }""");

        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Equal("verbose", settings.ToolDisplayMode);
    }

    [Fact]
    public void Missing_tool_display_mode_keeps_empty_settings_fast_path()
    {
        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Same(CodaSettings.Empty, settings);
        Assert.Null(settings.ToolDisplayMode);
    }
}
