using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Tests for <see cref="ReasoningCapabilityResolver"/> covering Anthropic static rules,
/// Copilot dynamic-metadata rules, and the applied-level resolution with max→high clamp.
/// </summary>
public sealed class ReasoningCapabilityTests
{
    // ── ResolveStoredLevel: indeterminate vs unsupported ─────────────────────

    [Fact]
    public void ResolveStoredLevel_keeps_copilot_level_when_advertised_levels_are_unknown()
    {
        // The whole point of the method: at startup no model list exists, so a Copilot model's
        // levels are INDETERMINATE. Reporting it unsupported (which plain Resolve does) silently
        // discarded a level the user had explicitly configured and /effort had already validated.
        var applied = ReasoningCapabilityResolver.ResolveStoredLevel(
            "github-copilot", "gpt-5.6-sol", "xhigh");

        Assert.Equal("xhigh", applied);
    }

    [Fact]
    public void ResolveStoredLevel_applies_normal_rules_once_copilot_levels_are_known()
    {
        var applied = ReasoningCapabilityResolver.ResolveStoredLevel(
            "github-copilot", "gpt-5.6-sol", "xhigh", ["low", "medium", "high", "xhigh"]);

        Assert.Equal("xhigh", applied);
    }

    [Fact]
    public void ResolveStoredLevel_drops_a_level_the_model_no_longer_advertises()
    {
        // Known levels are authoritative, so a stale stored level is still dropped.
        var applied = ReasoningCapabilityResolver.ResolveStoredLevel(
            "github-copilot", "gpt-5.6-sol", "xhigh", ["low", "medium", "high"]);

        Assert.Null(applied);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void ResolveStoredLevel_treats_auto_and_blank_as_no_explicit_level(string? stored)
    {
        Assert.Null(ReasoningCapabilityResolver.ResolveStoredLevel("github-copilot", "gpt-5.6-sol", stored));
    }

    [Fact]
    public void ResolveStoredLevel_still_clamps_anthropic_without_advertised_levels()
    {
        // Anthropic rules are static, so they stay authoritative even with no levels supplied —
        // the indeterminate escape hatch must not weaken them.
        var applied = ReasoningCapabilityResolver.ResolveStoredLevel(
            "claude-ai", "claude-sonnet-4-6", "max");

        Assert.Equal("high", applied);
    }

    [Fact]
    public void ResolveStoredLevel_still_drops_effort_for_an_unsupported_anthropic_model()
    {
        Assert.Null(ReasoningCapabilityResolver.ResolveStoredLevel("claude-ai", "claude-haiku-4-5", "high"));
    }

    // ── Anthropic capability resolution ──────────────────────────────────────

    [Theory]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-opus-4.8", true)]
    [InlineData("claude-opus-4-6", true)]
    [InlineData("claude-opus-4.6", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-sonnet-4.6", true)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-haiku-4.5", false)]
    [InlineData("gpt-5.6-sol", false)] // non-Anthropic but not Copilot provider
    public void Resolve_Anthropic_supported_matches_model_rules(string model, bool expectedSupported)
    {
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", model);
        Assert.Equal(expectedSupported, capability.Supported);
    }

    [Theory]
    [InlineData("claude-opus-4-8")]
    [InlineData("claude-opus-4.8")]
    [InlineData("claude-opus-4-6")]
    [InlineData("claude-opus-4.6")]
    public void Resolve_Anthropic_opus_has_max_level(string model)
    {
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", model);
        Assert.True(capability.Supported);
        Assert.Contains("max", capability.Levels, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ReasoningDelivery.AnthropicEffort, capability.Delivery);
    }

    [Theory]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("claude-sonnet-4.6")]
    public void Resolve_Anthropic_sonnet_lacks_max_level(string model)
    {
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", model);
        Assert.True(capability.Supported);
        Assert.DoesNotContain("max", capability.Levels, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("high", capability.Levels, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ReasoningDelivery.AnthropicEffort, capability.Delivery);
    }

    [Fact]
    public void Resolve_Anthropic_haiku_is_unsupported()
    {
        var capability = ReasoningCapabilityResolver.Resolve("anthropic-api-key", "claude-haiku-4-5");
        Assert.False(capability.Supported);
        Assert.Empty(capability.Levels);
        Assert.Equal(ReasoningDelivery.None, capability.Delivery);
    }

    [Fact]
    public void Resolve_unknown_model_is_unsupported()
    {
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", "some-future-unknown-model");
        Assert.False(capability.Supported);
    }

    // ── Copilot / OpenAI capability resolution ────────────────────────────────

    [Fact]
    public void Resolve_Copilot_with_reasoning_levels_is_supported()
    {
        IReadOnlyList<string> levels = ["low", "medium", "high"];
        var capability = ReasoningCapabilityResolver.Resolve("github-copilot", "gpt-5.6-sol", levels);
        Assert.True(capability.Supported);
        Assert.Equal(["low", "medium", "high"], capability.Levels);
        Assert.True(capability.SupportsAuto);
        Assert.Equal(ReasoningDelivery.OpenAiResponses, capability.Delivery);
    }

    [Fact]
    public void Resolve_Copilot_without_reasoning_levels_is_unsupported()
    {
        var capability = ReasoningCapabilityResolver.Resolve("github-copilot", "gpt-4o", null);
        Assert.False(capability.Supported);
        Assert.Equal(ReasoningDelivery.None, capability.Delivery);
    }

    [Fact]
    public void Resolve_Copilot_with_empty_reasoning_levels_is_unsupported()
    {
        var capability = ReasoningCapabilityResolver.Resolve("github-copilot", "gpt-4o", []);
        Assert.False(capability.Supported);
    }

    [Fact]
    public void Resolve_Copilot_is_case_insensitive_on_provider_id()
    {
        IReadOnlyList<string> levels = ["low", "high"];
        var capability = ReasoningCapabilityResolver.Resolve("GitHub-Copilot", "gpt-5.6-sol", levels);
        Assert.True(capability.Supported);
        Assert.Equal(ReasoningDelivery.OpenAiResponses, capability.Delivery);
    }

    // ── ResolveAnthropic (provider-agnostic) ─────────────────────────────────

    [Fact]
    public void ResolveAnthropic_returns_opus_levels_for_opus_model()
    {
        var capability = ReasoningCapabilityResolver.ResolveAnthropic("claude-opus-4-8");
        Assert.True(capability.Supported);
        Assert.Contains("max", capability.Levels, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ReasoningDelivery.AnthropicEffort, capability.Delivery);
    }

    // ── Applied level resolution ──────────────────────────────────────────────

    [Fact]
    public void ResolveAppliedLevel_returns_null_for_unsupported_model()
    {
        var capability = ReasoningCapability.Unsupported;
        Assert.Null(ReasoningCapabilityResolver.ResolveAppliedLevel(capability, "high"));
    }

    [Fact]
    public void ResolveAppliedLevel_returns_null_for_auto()
    {
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", "claude-sonnet-4-6");
        Assert.Null(ReasoningCapabilityResolver.ResolveAppliedLevel(capability, "auto"));
    }

    [Fact]
    public void ResolveAppliedLevel_returns_null_for_null_or_empty()
    {
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", "claude-sonnet-4-6");
        Assert.Null(ReasoningCapabilityResolver.ResolveAppliedLevel(capability, null));
        Assert.Null(ReasoningCapabilityResolver.ResolveAppliedLevel(capability, string.Empty));
    }

    [Fact]
    public void ResolveAppliedLevel_clamps_max_to_high_on_sonnet()
    {
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", "claude-sonnet-4-6");
        Assert.Equal("high", ReasoningCapabilityResolver.ResolveAppliedLevel(capability, "max"));
    }

    [Fact]
    public void ResolveAppliedLevel_allows_max_on_opus()
    {
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", "claude-opus-4-8");
        Assert.Equal("max", ReasoningCapabilityResolver.ResolveAppliedLevel(capability, "max"));
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("HIGH", "high")]
    [InlineData("Low", "low")]
    public void ResolveAppliedLevel_returns_lowercased_valid_level(string input, string expected)
    {
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", "claude-sonnet-4-6");
        Assert.Equal(expected, ReasoningCapabilityResolver.ResolveAppliedLevel(capability, input));
    }

    [Fact]
    public void ResolveAppliedLevel_returns_null_for_invalid_level()
    {
        var capability = ReasoningCapabilityResolver.Resolve("claude-ai", "claude-sonnet-4-6");
        Assert.Null(ReasoningCapabilityResolver.ResolveAppliedLevel(capability, "turbo"));
    }

    [Fact]
    public void ResolveAppliedLevel_Copilot_passes_level_through()
    {
        IReadOnlyList<string> levels = ["low", "medium", "high"];
        var capability = ReasoningCapabilityResolver.Resolve("github-copilot", "gpt-5.6-sol", levels);
        Assert.Equal("medium", ReasoningCapabilityResolver.ResolveAppliedLevel(capability, "medium"));
    }

    // ── Unsupported singleton ─────────────────────────────────────────────────

    [Fact]
    public void Unsupported_is_a_stable_singleton_with_correct_fields()
    {
        var u = ReasoningCapability.Unsupported;
        Assert.False(u.Supported);
        Assert.Empty(u.Levels);
        Assert.False(u.SupportsAuto);
        Assert.Equal(ReasoningDelivery.None, u.Delivery);
    }
}
