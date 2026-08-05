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
            userJson: """{"subagents":{"maxDepth":2,"maxConcurrent":20}}""",
            projectJson: """{"subagents":{"maxDepth":4}}""");

        Assert.Equal(4, settings.Subagents.MaxDepth);

        // The field the project did not set still comes from the user file.
        Assert.Equal(20, settings.Subagents.MaxConcurrent);
    }

    // -----------------------------------------------------------------------
    // Prompt replacement is user-only: a project file is attacker-controlled the
    // moment someone clones a hostile repo
    // -----------------------------------------------------------------------

    [Fact]
    public void A_project_file_cannot_enable_system_prompt_replacement()
    {
        var settings = this.Load(projectJson: """{"subagents":{"allowSystemPromptReplacement":true}}""");

        Assert.False(settings.Subagents.AllowSystemPromptReplacement);
    }

    [Fact]
    public void A_project_file_cannot_enable_replacement_over_a_user_file_that_left_it_off()
    {
        var settings = this.Load(
            userJson: """{"subagents":{"allowSystemPromptReplacement":false}}""",
            projectJson: """{"subagents":{"allowSystemPromptReplacement":true}}""");

        Assert.False(settings.Subagents.AllowSystemPromptReplacement);
    }

    [Fact]
    public void The_user_file_is_what_enables_system_prompt_replacement()
    {
        var settings = this.Load(userJson: """{"subagents":{"allowSystemPromptReplacement":true}}""");

        Assert.True(settings.Subagents.AllowSystemPromptReplacement);
    }

    [Fact]
    public void A_project_file_cannot_disable_replacement_the_user_enabled()
    {
        // Symmetry matters as much as the block: the project file is simply not consulted for this
        // field, so a hostile repo cannot flip it in either direction.
        var settings = this.Load(
            userJson: """{"subagents":{"allowSystemPromptReplacement":true}}""",
            projectJson: """{"subagents":{"allowSystemPromptReplacement":false}}""");

        Assert.True(settings.Subagents.AllowSystemPromptReplacement);
    }

    [Fact]
    public void A_project_file_may_still_raise_the_clamped_resource_limits()
    {
        // Depth and fan-out stay project-settable: they are clamped bounds on resource use, not a
        // hand-off of the subagent's own instructions.
        var settings = this.Load(projectJson: """{"subagents":{"maxDepth":5,"maxConcurrent":16}}""");

        Assert.Equal(5, settings.Subagents.MaxDepth);
        Assert.Equal(16, settings.Subagents.MaxConcurrent);
    }

    // -----------------------------------------------------------------------
    // Default fan-out 20 (Task 6)
    // -----------------------------------------------------------------------

    [Fact]
    public void The_default_maxConcurrent_is_20()
    {
        Assert.Equal(20, SubagentSettings.Default.MaxConcurrent);
    }

    // -----------------------------------------------------------------------
    // Model / ModelByType — user-only fields (Tasks 1 and 2)
    // -----------------------------------------------------------------------

    [Fact]
    public void Default_model_is_null()
    {
        Assert.Null(SubagentSettings.Default.Model);
    }

    [Fact]
    public void Default_ModelByType_is_empty()
    {
        Assert.Empty(SubagentSettings.Default.ModelByType);
    }

    [Fact]
    public void Blank_or_whitespace_model_normalises_to_null()
    {
        Assert.Null(new SubagentSettings { Model = "" }.Model);
        Assert.Null(new SubagentSettings { Model = "   " }.Model);
    }

    [Fact]
    public void User_file_model_is_read()
    {
        var settings = this.Load(userJson: """{"subagents":{"model":"claude-sonnet-4-6"}}""");
        Assert.Equal("claude-sonnet-4-6", settings.Subagents.Model);
    }

    [Fact]
    public void User_file_modelByType_is_read()
    {
        var settings = this.Load(userJson: """{"subagents":{"modelByType":{"explore":"fast-model"}}}""");
        Assert.Equal("fast-model", settings.Subagents.ModelByType["explore"]);
    }

    [Fact]
    public void ModelByType_lookup_is_ordinal_ignore_case()
    {
        var settings = this.Load(userJson: """{"subagents":{"modelByType":{"Explore":"fast-model"}}}""");
        Assert.Equal("fast-model", settings.Subagents.ModelByType["EXPLORE"]);
    }

    [Fact]
    public void A_project_file_cannot_set_model()
    {
        // Model is a cost lever; a hostile project must not be able to force the session to an
        // expensive model.
        var settings = this.Load(
            projectJson: """{"subagents":{"model":"gpt-expensive"}}""");

        Assert.Null(settings.Subagents.Model);
    }

    [Fact]
    public void A_project_file_cannot_override_the_user_model()
    {
        var settings = this.Load(
            userJson: """{"subagents":{"model":"user-model"}}""",
            projectJson: """{"subagents":{"model":"project-model"}}""");

        Assert.Equal("user-model", settings.Subagents.Model);
    }

    [Fact]
    public void A_project_file_cannot_set_modelByType()
    {
        var settings = this.Load(
            projectJson: """{"subagents":{"modelByType":{"general-purpose":"gpt-expensive"}}}""");

        Assert.Empty(settings.Subagents.ModelByType);
    }
}
