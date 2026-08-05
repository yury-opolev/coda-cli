using Coda.Agent;
using Coda.Agent.Settings;
using Coda.Agent.Subagents;
using Xunit;

namespace Engine.Tests.Subagents;

/// <summary>
/// Pins the model-resolution precedence documented in SubagentHost.ResolveModel:
/// request → settings.ModelByType → settings.Model → definition.Model → sessionModel.
/// Operator settings outrank a plugin-declared model at every level so a hostile project plugin
/// cannot force an expensive model.
/// </summary>
public sealed class SubagentModelResolutionTests
{
    private static string Resolve(
        string? requestModel = null,
        string? settingsModel = null,
        string? settingsModelByType = null,
        string? definitionModel = null,
        string sessionModel = "session-default",
        string subagentType = "general-purpose")
    {
        var settings = new SubagentSettings
        {
            Model = settingsModel,
            ModelByType = settingsModelByType is not null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [subagentType] = settingsModelByType }
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
        return SubagentHost.ResolveModel(requestModel, settings, subagentType, definitionModel, sessionModel);
    }

    [Fact]
    public void Session_model_is_used_when_nothing_else_is_configured()
    {
        Assert.Equal("session-default", Resolve());
    }

    [Fact]
    public void Definition_model_takes_precedence_over_session_model()
    {
        Assert.Equal("definition-model", Resolve(definitionModel: "definition-model"));
    }

    [Fact]
    public void Settings_global_model_takes_precedence_over_definition_model()
    {
        Assert.Equal("settings-global",
            Resolve(settingsModel: "settings-global", definitionModel: "definition-model"));
    }

    [Fact]
    public void Settings_per_type_model_takes_precedence_over_settings_global()
    {
        Assert.Equal("per-type",
            Resolve(settingsModel: "settings-global", settingsModelByType: "per-type", definitionModel: "definition-model"));
    }

    [Fact]
    public void Request_model_takes_precedence_over_everything()
    {
        Assert.Equal("request-model",
            Resolve(
                requestModel: "request-model",
                settingsModelByType: "per-type",
                settingsModel: "settings-global",
                definitionModel: "definition-model"));
    }

    [Fact]
    public void Whitespace_only_candidates_are_skipped()
    {
        // A whitespace-only request must not mask a valid definition model.
        Assert.Equal("definition-model",
            Resolve(requestModel: "   ", settingsModel: "  ", definitionModel: "definition-model"));
    }

    [Fact]
    public void Terminal_control_characters_are_stripped()
    {
        // ESC and carriage return are stripped before the id is returned.
        var result = Resolve(requestModel: "claude\x1b[31m-bad");
        Assert.Equal("claude[31m-bad", result);
    }

    [Fact]
    public void ModelByType_lookup_is_case_insensitive()
    {
        var settings = new SubagentSettings
        {
            ModelByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["EXPLORE"] = "explore-model",
            },
        };
        var result = SubagentHost.ResolveModel(null, settings, "explore", null, "session");
        Assert.Equal("explore-model", result);
    }

    [Fact]
    public void ModelByType_for_different_type_does_not_match()
    {
        // A per-type entry for "explore" must not affect "general-purpose".
        var settings = new SubagentSettings
        {
            ModelByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["explore"] = "explore-only-model",
            },
        };
        var result = SubagentHost.ResolveModel(null, settings, "general-purpose", null, "session-default");
        Assert.Equal("session-default", result);
    }
}
