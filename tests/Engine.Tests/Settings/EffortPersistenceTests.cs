using Coda.Agent.Settings;
using LlmClient;

namespace Engine.Tests.Settings;

/// <summary>Tests for <c>effortByModel</c> persistence in <c>settings.json</c>.</summary>
public sealed class EffortPersistenceTests
{
    private readonly string settingsDir = Directory.CreateTempSubdirectory("coda_effort_").FullName;

    [Fact]
    public void Load_returns_empty_effortByModel_when_no_settings_file()
    {
        var settings = SettingsLoader.Load(this.settingsDir, this.settingsDir);
        Assert.Empty(settings.EffortByModel);
    }

    [Fact]
    public void SetUserEffortForModel_persists_effort_and_Load_reads_it_back()
    {
        SettingsWriter.SetUserEffortForModel("github-copilot", "gpt-5.6-sol", "high", this.settingsDir);

        var settings = SettingsLoader.Load(this.settingsDir, this.settingsDir);

        Assert.True(settings.EffortByModel.TryGetValue("github-copilot/gpt-5.6-sol", out var effort));
        Assert.Equal("high", effort);
    }

    [Fact]
    public void SetUserEffortForModel_multiple_models_are_independent()
    {
        SettingsWriter.SetUserEffortForModel("github-copilot", "gpt-5.6-sol", "high", this.settingsDir);
        SettingsWriter.SetUserEffortForModel("claude-ai", "claude-opus-4-8", "max", this.settingsDir);

        var settings = SettingsLoader.Load(this.settingsDir, this.settingsDir);

        Assert.Equal("high", settings.EffortByModel["github-copilot/gpt-5.6-sol"]);
        Assert.Equal("max", settings.EffortByModel["claude-ai/claude-opus-4-8"]);
    }

    [Fact]
    public void SetUserEffortForModel_overwrites_existing_entry()
    {
        SettingsWriter.SetUserEffortForModel("github-copilot", "gpt-5.6-sol", "low", this.settingsDir);
        SettingsWriter.SetUserEffortForModel("github-copilot", "gpt-5.6-sol", "high", this.settingsDir);

        var settings = SettingsLoader.Load(this.settingsDir, this.settingsDir);

        Assert.Equal("high", settings.EffortByModel["github-copilot/gpt-5.6-sol"]);
    }

    [Fact]
    public void SetUserEffortForModel_with_null_effort_removes_the_key()
    {
        SettingsWriter.SetUserEffortForModel("github-copilot", "gpt-5.6-sol", "high", this.settingsDir);
        SettingsWriter.SetUserEffortForModel("github-copilot", "gpt-5.6-sol", null, this.settingsDir);

        var settings = SettingsLoader.Load(this.settingsDir, this.settingsDir);

        Assert.False(settings.EffortByModel.ContainsKey("github-copilot/gpt-5.6-sol"));
    }

    [Fact]
    public void SetUserEffortForModel_preserves_other_settings()
    {
        SettingsWriter.SetUserModelForProvider("claude-ai", "claude-sonnet-4-6", this.settingsDir);
        SettingsWriter.SetUserEffortForModel("claude-ai", "claude-sonnet-4-6", "high", this.settingsDir);

        var settings = SettingsLoader.Load(this.settingsDir, this.settingsDir);

        Assert.Equal("claude-sonnet-4-6", settings.ModelByProvider["claude-ai"]);
        Assert.Equal("high", settings.EffortByModel["claude-ai/claude-sonnet-4-6"]);
    }

    [Fact]
    public void Load_is_case_insensitive_for_effortByModel_keys()
    {
        SettingsWriter.SetUserEffortForModel("GitHub-Copilot", "GPT-5.6-Sol", "medium", this.settingsDir);

        var settings = SettingsLoader.Load(this.settingsDir, this.settingsDir);

        // Should be readable with any casing
        Assert.True(settings.EffortByModel.ContainsKey("github-copilot/gpt-5.6-sol"));
    }

    // ── Resolver clamping / dropping of persisted values ─────────────────────

    [Fact]
    public void Stale_persisted_max_effort_is_clamped_to_high_for_sonnet_via_resolver()
    {
        // "max" is valid for opus but NOT for sonnet; the resolver must clamp, not pass through verbatim.
        SettingsWriter.SetUserEffortForModel("claude-ai", "claude-sonnet-4-6", "max", this.settingsDir);
        var settings = SettingsLoader.Load(this.settingsDir, this.settingsDir);

        var stored = settings.EffortByModel["claude-ai/claude-sonnet-4-6"];
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", "claude-sonnet-4-6");
        var resolved = ReasoningCapabilityResolver.ResolveAppliedLevel(capability, stored);

        Assert.Equal("high", resolved); // clamped to high, not "max"
    }

    [Fact]
    public void Persisted_effort_for_unsupported_model_is_dropped_via_resolver()
    {
        // haiku does not support reasoning effort; any stored level must be dropped (null).
        SettingsWriter.SetUserEffortForModel("claude-ai", "claude-haiku-4-5", "high", this.settingsDir);
        var settings = SettingsLoader.Load(this.settingsDir, this.settingsDir);

        var stored = settings.EffortByModel["claude-ai/claude-haiku-4-5"];
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", "claude-haiku-4-5");
        var resolved = ReasoningCapabilityResolver.ResolveAppliedLevel(capability, stored);

        Assert.Null(resolved); // dropped, not "high"
    }
}
