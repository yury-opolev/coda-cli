using Coda.Agent.Settings;

namespace Engine.Tests.Settings;

/// <summary>
/// Subagent limits are settings rather than constants because the right depth and fan-out depend on
/// the work: a shallow refactor wants neither, an architectural survey wants both. These pin the
/// parsing, the clamping, and the user/project merge.
/// </summary>
public sealed class SubagentSettingsTests : IDisposable
{
    private readonly string dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "coda-subagent-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { }
    }

    private CodaSettings Load(string? userJson = null, string? projectJson = null)
    {
        var userDir = Path.Combine(this.dir, "user");
        var projectDir = Path.Combine(this.dir, "project");
        Directory.CreateDirectory(Path.Combine(userDir, ".coda"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".coda"));

        if (userJson is not null)
        {
            File.WriteAllText(Path.Combine(userDir, ".coda", "settings.json"), userJson);
        }

        if (projectJson is not null)
        {
            File.WriteAllText(Path.Combine(projectDir, ".coda", "settings.json"), projectJson);
        }

        return SettingsLoader.Load(projectDir, userDir);
    }

    // -----------------------------------------------------------------------
    // Defaults
    // -----------------------------------------------------------------------

    [Fact]
    public void With_no_settings_the_defaults_preserve_todays_behaviour()
    {
        var settings = SubagentSettings.Default;

        Assert.Equal(2, settings.MaxDepth);
        Assert.False(settings.AllowSystemPromptReplacement);
    }

    [Fact]
    public void An_absent_subagents_block_yields_the_defaults()
    {
        var settings = this.Load(userJson: """{"defaultProvider":"anthropic"}""");

        Assert.Equal(SubagentSettings.Default.MaxDepth, settings.Subagents.MaxDepth);
        Assert.Equal(SubagentSettings.Default.MaxConcurrent, settings.Subagents.MaxConcurrent);
    }

    // -----------------------------------------------------------------------
    // Parsing
    // -----------------------------------------------------------------------

    [Fact]
    public void Each_field_is_read_from_the_subagents_block()
    {
        var settings = this.Load(userJson: """
            {"subagents":{"maxDepth":4,"maxConcurrent":3,"allowSystemPromptReplacement":true}}
            """);

        Assert.Equal(4, settings.Subagents.MaxDepth);
        Assert.Equal(3, settings.Subagents.MaxConcurrent);
        Assert.True(settings.Subagents.AllowSystemPromptReplacement);
    }

    [Fact]
    public void A_partial_block_keeps_the_defaults_for_the_rest()
    {
        var settings = this.Load(userJson: """{"subagents":{"maxDepth":5}}""");

        Assert.Equal(5, settings.Subagents.MaxDepth);
        Assert.Equal(SubagentSettings.Default.MaxConcurrent, settings.Subagents.MaxConcurrent);
        Assert.False(settings.Subagents.AllowSystemPromptReplacement);
    }

    // -----------------------------------------------------------------------
    // Clamping — a settings file must never be able to disable the limit
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_depth_below_one_is_clamped_up(int configured)
    {
        Assert.Equal(1, new SubagentSettings { MaxDepth = configured }.MaxDepth);
    }

    [Fact]
    public void An_absurd_depth_is_clamped_down()
    {
        Assert.Equal(SubagentSettings.MaxAllowedDepth, new SubagentSettings { MaxDepth = 9999 }.MaxDepth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_concurrency_below_one_is_clamped_up(int configured)
    {
        Assert.Equal(1, new SubagentSettings { MaxConcurrent = configured }.MaxConcurrent);
    }

    [Fact]
    public void An_absurd_concurrency_is_clamped_down()
    {
        Assert.Equal(SubagentSettings.MaxAllowedConcurrent, new SubagentSettings { MaxConcurrent = 9999 }.MaxConcurrent);
    }

    // -----------------------------------------------------------------------
    // Merge: project overrides user, per field
    // -----------------------------------------------------------------------

    [Fact]
    public void A_project_setting_overrides_the_user_setting()
    {
        var settings = this.Load(
            userJson: """{"subagents":{"maxDepth":2,"maxConcurrent":8}}""",
            projectJson: """{"subagents":{"maxDepth":4}}""");

        Assert.Equal(4, settings.Subagents.MaxDepth);

        // The field the project did not set still comes from the user file.
        Assert.Equal(8, settings.Subagents.MaxConcurrent);
    }
}
