using Coda.Agent.Settings;

namespace Engine.Tests.Settings;

/// <summary>
/// Tests that <see cref="SettingsLoader"/> is the single source of resolved
/// <c>defaultProvider</c>/<c>defaultModel</c>: merging user + project files
/// (project overrides user, per field), honoring <c>CODA_SETTINGS_DIR</c>, and
/// normalizing blank values to <see langword="null"/>. These are the values
/// both the TUI startup and <c>coda serve</c> now consume.
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
            "defaultModel": "claude-opus-4"
        }
        """);

        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Equal("github-copilot", settings.DefaultProvider);
        Assert.Equal("claude-opus-4", settings.DefaultModel);
    }

    [Fact]
    public void Project_defaults_override_user_defaults_per_field()
    {
        this.WriteUser("""
        {
            "defaultProvider": "github-copilot",
            "defaultModel": "user-model"
        }
        """);
        this.WriteProject("""
        {
            "defaultModel": "project-model"
        }
        """);

        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        // Provider falls through to user (not set in project); model is overridden by project.
        Assert.Equal("github-copilot", settings.DefaultProvider);
        Assert.Equal("project-model", settings.DefaultModel);
    }

    [Fact]
    public void Honors_CODA_SETTINGS_DIR_for_user_defaults()
    {
        this.WriteUser("""
        {
            "defaultProvider": "github-copilot",
            "defaultModel": "env-model"
        }
        """);

        Environment.SetEnvironmentVariable("CODA_SETTINGS_DIR", this.userHome);

        // No explicit userSettingsDir → falls back to CODA_SETTINGS_DIR.
        var settings = SettingsLoader.Load(this.projectDir);

        Assert.Equal("github-copilot", settings.DefaultProvider);
        Assert.Equal("env-model", settings.DefaultModel);
    }

    [Fact]
    public void Blank_defaults_normalize_to_null()
    {
        this.WriteUser("""
        {
            "defaultProvider": "   ",
            "defaultModel": ""
        }
        """);

        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Null(settings.DefaultProvider);
        Assert.Null(settings.DefaultModel);
    }

    [Fact]
    public void No_files_yields_null_defaults()
    {
        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Null(settings.DefaultProvider);
        Assert.Null(settings.DefaultModel);
    }
}
