using Coda.Agent.Settings;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using LlmClient;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests that persisted reasoning effort is loaded into <see cref="SessionState"/> at TUI startup
/// and restored on model switch, always resolved through
/// <see cref="ReasoningCapabilityResolver.ResolveAppliedLevel"/> so stale or unsupported stored
/// levels are clamped/dropped rather than sent verbatim.
/// </summary>
public sealed class SessionStateEffortTests
{
    // ── ApplyStartupEffort (TUI startup seeding) ──────────────────────────────

    [Fact]
    public void ApplyStartupEffort_seeds_EffortByModel_from_settings()
    {
        var session = MakeSession("claude-ai", model: "claude-opus-4-8");
        var settings = new CodaSettings([], [], [])
        {
            EffortByModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-ai/claude-opus-4-8"] = "high",
                ["claude-ai/claude-sonnet-4-6"] = "low",
            },
        };

        DefaultInteractiveSessionRunner.ApplyStartupEffort(session, "claude-ai", settings);

        Assert.Equal("high", session.EffortByModel["claude-ai/claude-opus-4-8"]);
        Assert.Equal("low", session.EffortByModel["claude-ai/claude-sonnet-4-6"]);
    }

    [Fact]
    public void ApplyStartupEffort_sets_Effort_for_starting_model_from_settings()
    {
        var session = MakeSession("claude-ai", model: "claude-opus-4-8");
        var settings = new CodaSettings([], [], [])
        {
            EffortByModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-ai/claude-opus-4-8"] = "high",
            },
        };

        DefaultInteractiveSessionRunner.ApplyStartupEffort(session, "claude-ai", settings);

        Assert.Equal("high", session.Effort);
    }

    [Fact]
    public void ApplyStartupEffort_clamps_stale_max_level_to_high_for_sonnet()
    {
        // "max" is stored but sonnet 4.6 does not support it → must be clamped to "high".
        var session = MakeSession("claude-ai", model: "claude-sonnet-4-6");
        var settings = new CodaSettings([], [], [])
        {
            EffortByModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-ai/claude-sonnet-4-6"] = "max",
            },
        };

        DefaultInteractiveSessionRunner.ApplyStartupEffort(session, "claude-ai", settings);

        Assert.Equal("high", session.Effort); // clamped, not "max"
    }

    [Fact]
    public void ApplyStartupEffort_drops_effort_for_unsupported_model()
    {
        // haiku does not support reasoning effort; any stored level must result in null.
        var session = MakeSession("claude-ai", model: "claude-haiku-4-5");
        var settings = new CodaSettings([], [], [])
        {
            EffortByModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-ai/claude-haiku-4-5"] = "high",
            },
        };

        DefaultInteractiveSessionRunner.ApplyStartupEffort(session, "claude-ai", settings);

        Assert.Null(session.Effort); // dropped, not "high"
    }

    [Fact]
    public void ApplyStartupEffort_leaves_Effort_null_when_no_persisted_entry_for_starting_model()
    {
        var session = MakeSession("claude-ai", model: "claude-opus-4-8");

        DefaultInteractiveSessionRunner.ApplyStartupEffort(session, "claude-ai", CodaSettings.Empty);

        Assert.Null(session.Effort);
        Assert.Empty(session.EffortByModel);
    }

    [Fact]
    public void ApplyStartupEffort_preserves_stored_level_for_copilot_when_levels_are_not_yet_known()
    {
        // Copilot models advertise their reasoning levels at runtime, so at startup — before any
        // model list has been fetched — the capability is UNKNOWN, not unsupported. Treating it as
        // unsupported silently resolved the user's configured level to null, so the status bar
        // showed "auto" while the model browser still showed the saved level. The stored value was
        // validated by /effort when it was set, so it must survive an unknown capability.
        var session = MakeSession("github-copilot", model: "gpt-5.6-sol");
        var settings = new CodaSettings([], [], [])
        {
            EffortByModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["github-copilot/gpt-5.6-sol"] = "xhigh",
            },
        };

        DefaultInteractiveSessionRunner.ApplyStartupEffort(session, "github-copilot", settings);

        Assert.Equal("xhigh", session.Effort);
    }

    [Fact]
    public void ApplyStartupEffort_leaves_Effort_null_for_copilot_when_nothing_is_stored()
    {
        var session = MakeSession("github-copilot", model: "gpt-5.6-sol");

        DefaultInteractiveSessionRunner.ApplyStartupEffort(session, "github-copilot", CodaSettings.Empty);

        Assert.Null(session.Effort);
    }

    // ── ModelCommand model-switch restoration ─────────────────────────────────

    [Fact]
    public async Task Model_switch_restores_stored_effort_from_EffortByModel()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.EffortByModel["claude-ai/claude-opus-4-8"] = "high";

        var command = new ModelCommand((_, _) => "note");
        await command.ExecuteAsync(context, ["claude-opus-4-8"], CancellationToken.None);

        Assert.Equal("high", context.Session.Effort);
    }

    [Fact]
    public async Task Model_switch_clamps_stale_stored_effort_via_resolver_not_verbatim()
    {
        // "max" stored for sonnet, which only supports low/medium/high → must be clamped to "high".
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.Model = "claude-opus-4-8"; // ensure we start on a different model
        context.Session.EffortByModel["claude-ai/claude-sonnet-4-6"] = "max";

        var command = new ModelCommand((_, _) => "note");
        await command.ExecuteAsync(context, ["claude-sonnet-4-6"], CancellationToken.None);

        Assert.Equal("high", context.Session.Effort); // clamped, NOT "max"
    }

    [Fact]
    public async Task Model_switch_drops_stored_effort_for_unsupported_model()
    {
        // haiku does not support reasoning effort → stored "high" must be dropped.
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.EffortByModel["claude-ai/claude-haiku-4-5"] = "high";

        var command = new ModelCommand((_, _) => "note");
        await command.ExecuteAsync(context, ["claude-haiku-4-5"], CancellationToken.None);

        Assert.Null(context.Session.Effort); // dropped, not "high"
    }

    [Fact]
    public async Task Model_switch_away_and_back_restores_stored_effort()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.Model = "claude-opus-4-8";
        context.Session.Effort = "high";
        context.Session.EffortByModel["claude-ai/claude-opus-4-8"] = "high";
        context.Session.EffortByModel["claude-ai/claude-sonnet-4-6"] = "low";

        var command = new ModelCommand((_, _) => "note");

        await command.ExecuteAsync(context, ["claude-sonnet-4-6"], CancellationToken.None);
        Assert.Equal("low", context.Session.Effort);

        await command.ExecuteAsync(context, ["claude-opus-4-8"], CancellationToken.None);
        Assert.Equal("high", context.Session.Effort); // restored for opus
    }

    [Fact]
    public async Task Model_switch_clears_effort_when_no_entry_in_EffortByModel()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.Effort = "high"; // previously set

        var command = new ModelCommand((_, _) => "note");
        await command.ExecuteAsync(context, ["claude-opus-4-8"], CancellationToken.None);

        Assert.Null(context.Session.Effort); // no stored entry → null (auto)
    }

    // ── ServeRunner.BuildSessionOptions effort seeding ────────────────────────

    [Fact]
    public void BuildSessionOptions_seeds_Effort_from_effortByModel_for_starting_model()
    {
        var options = new ServeOptions
        {
            ProviderId = "claude-ai",
            Model = "claude-opus-4-8",
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
        var effortByModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-ai/claude-opus-4-8"] = "high",
        };

        var sessionOptions = ServeRunner.BuildSessionOptions(options, effortByModel: effortByModel);

        Assert.Equal("high", sessionOptions.Effort);
    }

    [Fact]
    public void BuildSessionOptions_clamps_stale_effort_via_resolver()
    {
        // "max" stored for sonnet → clamped to "high" by the resolver.
        var options = new ServeOptions
        {
            ProviderId = "claude-ai",
            Model = "claude-sonnet-4-6",
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
        var effortByModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-ai/claude-sonnet-4-6"] = "max",
        };

        var sessionOptions = ServeRunner.BuildSessionOptions(options, effortByModel: effortByModel);

        Assert.Equal("high", sessionOptions.Effort); // clamped, not "max"
    }

    [Fact]
    public void BuildSessionOptions_drops_effort_for_unsupported_model()
    {
        var options = new ServeOptions
        {
            ProviderId = "claude-ai",
            Model = "claude-haiku-4-5",
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
        var effortByModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-ai/claude-haiku-4-5"] = "high",
        };

        var sessionOptions = ServeRunner.BuildSessionOptions(options, effortByModel: effortByModel);

        Assert.Null(sessionOptions.Effort); // dropped
    }

    [Fact]
    public void BuildSessionOptions_leaves_Effort_null_when_effortByModel_is_null()
    {
        var options = new ServeOptions
        {
            ProviderId = "claude-ai",
            Model = "claude-opus-4-8",
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        var sessionOptions = ServeRunner.BuildSessionOptions(options);

        Assert.Null(sessionOptions.Effort);
    }

    [Fact]
    public void BuildSessionOptions_leaves_Effort_null_when_starting_model_has_no_entry()
    {
        var options = new ServeOptions
        {
            ProviderId = "claude-ai",
            Model = "claude-opus-4-8",
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
        var effortByModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-ai/claude-sonnet-4-6"] = "high", // different model
        };

        var sessionOptions = ServeRunner.BuildSessionOptions(options, effortByModel: effortByModel);

        Assert.Null(sessionOptions.Effort);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static SessionState MakeSession(string providerId, string model)
    {
        var session = new SessionState(providerId);
        session.Model = model;
        return session;
    }
}
