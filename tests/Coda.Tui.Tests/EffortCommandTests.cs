using Coda.Sdk;
using Coda.Tui.Commands;
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Tests;

public sealed class EffortCommandTests
{
    [Fact]
    public async Task Effort_with_no_args_reports_auto_by_default()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var command = new EffortCommand();
        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("auto", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Null(context.Session.Effort);
    }

    [Fact]
    public async Task Effort_sets_level_on_session()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var command = new EffortCommand();
        await command.ExecuteAsync(context, ["high"], CancellationToken.None);

        Assert.Equal("high", context.Session.Effort);
        Assert.Contains("high", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Effort_auto_clears_the_level()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.Effort = "high";

        var command = new EffortCommand();
        await command.ExecuteAsync(context, ["auto"], CancellationToken.None);

        Assert.Null(context.Session.Effort);
    }

    [Fact]
    public async Task Effort_rejects_invalid_level()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var command = new EffortCommand();
        await command.ExecuteAsync(context, ["turbo"], CancellationToken.None);

        Assert.Null(context.Session.Effort);
        Assert.Contains("Invalid", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Effort_resolves_Copilot_reasoning_model_levels_without_prior_model_command()
    {
        // Parity with serve: /effort should lazily fetch model list when cache is empty,
        // so a Copilot reasoning model is correctly recognized even before /model is opened.
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        // Switch to github-copilot with a reasoning model; NO ModelListCache entry yet.
        context.Session.ActiveProviderId = "github-copilot";
        context.Session.Model = "o4-mini";
        Assert.Empty(context.Session.ModelListCache);

        // Inject a fake model-list resolver (mirrors serve's ListModelsAsync path).
        var fakeList = new ModelListResult(
            "github-copilot",
            ModelSource.Live,
            [new ModelListEntry("o4-mini", ReasoningLevels: ["low", "medium", "high"])]);

        var command = new EffortCommand(
            EffortCommand.TryPersistEffortForModel,
            (_, _) => Task.FromResult<ModelListResult?>(fakeList));

        await command.ExecuteAsync(context, ["high"], CancellationToken.None);

        // Effort was accepted and applied.
        Assert.Equal("high", context.Session.Effort);
        Assert.Contains("high", console.Output, StringComparison.OrdinalIgnoreCase);

        // Cache was populated so subsequent calls skip the network.
        Assert.True(context.Session.ModelListCache.ContainsKey("github-copilot"));
    }

    [Fact]
    public async Task Effort_treats_Copilot_model_as_unsupported_when_lazy_fetch_returns_null()
    {
        // If the lazy fetch fails (returns null), the model is treated as non-reasoning.
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.ActiveProviderId = "github-copilot";
        context.Session.Model = "gpt-4o";

        var command = new EffortCommand(
            EffortCommand.TryPersistEffortForModel,
            (_, _) => Task.FromResult<ModelListResult?>(null));

        await command.ExecuteAsync(context, ["high"], CancellationToken.None);

        // gpt-4o with no metadata → unsupported → rejected
        Assert.Null(context.Session.Effort);
        Assert.Contains("not supported", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ── per-model behaviour ─────────────────────────────────────────────────

    /// <summary>
    /// "current" asks a question. Routing it into the picker meant an interactive user could not
    /// inspect the level without being made to choose one.
    /// </summary>
    [Fact]
    public async Task Effort_current_shows_the_level_and_never_opens_a_picker()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["low"], null));
        var (_, context, console, _) = TestAppBuilder.BuildApp(prompts: prompts);
        context.Session.Model = "claude-opus-4-8";
        context.Session.Effort = "high";

        await new EffortCommand().ExecuteAsync(context, ["current"], CancellationToken.None);

        Assert.Empty(prompts.Requests);
        Assert.Equal("high", context.Session.Effort);
        Assert.Contains("high", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The report is about the CURRENT model, and names the levels that model supports.</summary>
    [Fact]
    public async Task Effort_current_names_the_model_and_its_own_levels()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.Model = "claude-sonnet-4-6";

        await new EffortCommand().ExecuteAsync(context, ["current"], CancellationToken.None);

        Assert.Contains("claude-sonnet-4-6", console.Output, StringComparison.Ordinal);

        // Assert against the levels line specifically, so unrelated wording elsewhere in the output
        // cannot make this pass or fail by accident.
        var levelsLine = console.Output
            .Split('\n')
            .Single(l => l.Contains("Supported by", StringComparison.Ordinal));
        var offered = levelsLine[(levelsLine.IndexOf(':', StringComparison.Ordinal) + 1)..]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["low", "medium", "high", "auto"], offered);
    }

    [Fact]
    public async Task Effort_current_says_so_when_the_model_has_no_effort_control()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.Model = "claude-haiku-4-5";

        await new EffortCommand().ExecuteAsync(context, ["current"], CancellationToken.None);

        Assert.Contains("not supported", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude-haiku-4-5", console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The picker must offer exactly what the model advertises, plus auto — never a fixed list.
    /// </summary>
    [Fact]
    public async Task The_picker_offers_only_the_current_models_levels()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["auto"], null));
        var (_, context, _, _) = TestAppBuilder.BuildApp(prompts: prompts);
        context.Session.Model = "claude-sonnet-4-6";

        await new EffortCommand().ExecuteAsync(context, [], CancellationToken.None);

        var request = Assert.Single(prompts.Requests);
        Assert.Equal(["auto", "low", "medium", "high"], request.Options.Select(o => o.Id));
        Assert.Contains("claude-sonnet-4-6", request.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// Copilot models advertise levels the Anthropic wording does not cover. A level we have no
    /// gloss for must carry NO description rather than a confidently wrong one.
    /// </summary>
    [Fact]
    public async Task The_picker_leaves_an_unfamiliar_level_undescribed()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["auto"], null));
        var (_, context, _, _) = TestAppBuilder.BuildApp(prompts: prompts);
        context.Session.ActiveProviderId = "github-copilot";
        context.Session.Model = "gpt-5.6-sol";
        context.Session.ModelListCache["github-copilot"] = new ModelListResult(
            "github-copilot",
            ModelSource.Live,
            [new ModelListEntry("gpt-5.6-sol", "Sol", 400_000, ["none", "low", "high", "turbo"])]);

        await new EffortCommand().ExecuteAsync(context, [], CancellationToken.None);

        var request = Assert.Single(prompts.Requests);
        Assert.Equal(["auto", "none", "low", "high", "turbo"], request.Options.Select(o => o.Id));

        // "turbo" has no gloss, so it must be offered with no description at all...
        Assert.Null(Assert.Single(request.Options, o => o.Id == "turbo").Detail);

        // ...and medium's wording must not leak onto a level that is not even offered.
        Assert.DoesNotContain(
            request.Options,
            o => o.Detail is not null && o.Detail.Contains("Balanced", StringComparison.Ordinal));
    }

    /// <summary>
    /// Clearing is the one path that used to run before the support check, so it announced,
    /// persisted and published a change for a model with no effort control at all.
    /// </summary>
    [Fact]
    public async Task Effort_auto_on_an_unsupported_model_reports_instead_of_persisting()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.Model = "claude-haiku-4-5";
        var persisted = 0;
        var command = new EffortCommand((_, _, _) =>
        {
            persisted++;
            return "saved";
        });

        await command.ExecuteAsync(context, ["auto"], CancellationToken.None);

        Assert.Equal(0, persisted);
        Assert.Empty(context.Session.EffortByModel);
        Assert.Contains("not supported", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Effort_current_is_case_insensitive()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["low"], null));
        var (_, context, console, _) = TestAppBuilder.BuildApp(prompts: prompts);
        context.Session.Model = "claude-opus-4-8";

        await new EffortCommand().ExecuteAsync(context, ["Current"], CancellationToken.None);

        Assert.Empty(prompts.Requests);
        Assert.DoesNotContain("Invalid effort level", console.Output, StringComparison.Ordinal);
    }
}
