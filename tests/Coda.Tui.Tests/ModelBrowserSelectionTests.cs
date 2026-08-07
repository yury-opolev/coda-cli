using Coda.Sdk;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Models;
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Tests;

/// <summary>
/// Covers the <c>/model</c> BROWSER path — seeding the picker from the session's saved efforts and
/// persisting the effort chosen there.
/// </summary>
/// <remarks>
/// This path had no coverage, and it silently deleted saved effort levels: the browser looks rows up
/// by the bare <c>ModelListEntry.Id</c> while the session keys efforts as <c>"{provider}/{model}"</c>,
/// so a seed built straight from the session never matched, every row reported "auto", and an
/// ordinary Enter then wrote that "auto" back over a real saved level.
/// </remarks>
public sealed class ModelBrowserSelectionTests
{
    private const string Provider = "claude-ai";

    private static ModelListResult Models() => new(
        Provider,
        ModelSource.Catalog,
        [
            new ModelListEntry("claude-opus-4-8", "Opus", 200_000, ["low", "medium", "high", "max"]),
            new ModelListEntry("claude-sonnet-4-6", "Sonnet", 200_000, ["low", "medium", "high"]),
        ]);

    /// <summary>Returns the picker's chosen selection and records the seed it was given.</summary>
    private sealed class StubBrowser(ModelSelection? selection) : IModelBrowserService
    {
        public IReadOnlyDictionary<string, string>? Seed { get; private set; }

        public Task<ModelSelection?> SelectModelAsync(
            ModelListResult result,
            string? currentModelId,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? initialEffortByModel = null)
        {
            this.Seed = initialEffortByModel;
            return Task.FromResult(selection);
        }
    }

    private static (CommandContext Context, StubBrowser Browser, List<string?> Persisted, ModelCommand Command)
        Build(ModelSelection? selection)
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, [], null));
        var (_, context, _, _) = TestAppBuilder.BuildApp(prompts: prompts);
        var browser = new StubBrowser(selection);
        context.ModelBrowserService = browser;
        context.Session.ModelListCache[Provider] = Models();

        var persisted = new List<string?>();
        var command = new ModelCommand(
            (_, _) => "note",
            (_, _, effort) =>
            {
                persisted.Add(effort);
                return "saved";
            });

        return (context, browser, persisted, command);
    }

    // ── seeding ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_picker_is_seeded_with_saved_efforts_keyed_by_bare_model_id()
    {
        var (context, browser, _, command) = Build(null);
        context.Session.EffortByModel[$"{Provider}/claude-opus-4-8"] = "high";

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.NotNull(browser.Seed);
        Assert.Equal("high", browser.Seed["claude-opus-4-8"]);
    }

    [Fact]
    public async Task The_seed_excludes_other_providers_and_cleared_entries()
    {
        var (context, browser, _, command) = Build(null);
        context.Session.EffortByModel[$"{Provider}/claude-opus-4-8"] = "high";
        context.Session.EffortByModel["github-copilot/gpt-5.5"] = "low";
        context.Session.EffortByModel[$"{Provider}/claude-sonnet-4-6"] = null;

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.NotNull(browser.Seed);
        Assert.Equal(["claude-opus-4-8"], browser.Seed.Keys);
    }

    // ── the data-loss regression ────────────────────────────────────────────

    /// <summary>
    /// Selecting a model without touching the effort control must leave its saved level alone. The
    /// seed is what makes this true: the picker reports back the level it was seeded with, so the
    /// "did it change?" guard sees no change.
    /// </summary>
    [Fact]
    public async Task Selecting_a_model_without_changing_effort_does_not_clear_the_saved_level()
    {
        var (context, _, persisted, command) = Build(
            new ModelSelection("claude-opus-4-8", "high") { EffortChosen = true });
        context.Session.Model = "claude-sonnet-4-6";
        context.Session.EffortByModel[$"{Provider}/claude-opus-4-8"] = "high";

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Empty(persisted);
        Assert.Equal("high", context.Session.EffortByModel[$"{Provider}/claude-opus-4-8"]);
    }

    /// <summary>
    /// The generic prompt fallback has no effort control, so it must never be read as "the user
    /// chose auto" — that would wipe the saved level on every fallback model switch.
    /// </summary>
    [Fact]
    public async Task A_picker_without_an_effort_control_never_touches_the_saved_level()
    {
        var (context, _, persisted, command) = Build(new ModelSelection("claude-opus-4-8", null));
        context.Session.Model = "claude-sonnet-4-6";
        context.Session.EffortByModel[$"{Provider}/claude-opus-4-8"] = "high";

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Empty(persisted);
        Assert.Equal("high", context.Session.EffortByModel[$"{Provider}/claude-opus-4-8"]);
    }

    // ── applying a real change ──────────────────────────────────────────────

    [Fact]
    public async Task Choosing_a_new_effort_persists_it_for_that_model()
    {
        var (context, _, persisted, command) = Build(
            new ModelSelection("claude-opus-4-8", "max") { EffortChosen = true });
        context.Session.Model = "claude-sonnet-4-6";

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(["max"], persisted);
        Assert.Equal("max", context.Session.EffortByModel[$"{Provider}/claude-opus-4-8"]);
    }

    /// <summary>
    /// EffortByModel keeps the RAW choice; Session.Effort keeps the RESOLVED one — "max" on Sonnet
    /// clamps to "high", exactly as /effort behaves.
    /// </summary>
    [Fact]
    public async Task The_session_effort_is_the_resolved_level_while_the_stored_one_stays_raw()
    {
        var (context, _, _, command) = Build(
            new ModelSelection("claude-sonnet-4-6", "max") { EffortChosen = true });
        context.Session.Model = "claude-opus-4-8";

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal("max", context.Session.EffortByModel[$"{Provider}/claude-sonnet-4-6"]);
        Assert.Equal("high", context.Session.Effort);
    }

    [Fact]
    public async Task Choosing_auto_clears_the_saved_level()
    {
        var (context, _, persisted, command) = Build(
            new ModelSelection("claude-opus-4-8", null) { EffortChosen = true });
        context.Session.Model = "claude-sonnet-4-6";
        context.Session.EffortByModel[$"{Provider}/claude-opus-4-8"] = "high";

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal([null], persisted);
        Assert.Null(context.Session.EffortByModel[$"{Provider}/claude-opus-4-8"]);
        Assert.Null(context.Session.Effort);
    }

    /// <summary>
    /// Changing only the effort of the model already in use still has to reach the status line —
    /// ApplyModel returns early for an unchanged model and publishes nothing.
    /// </summary>
    [Fact]
    public async Task Changing_only_the_effort_publishes_session_metadata()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, [], null));
        var events = new RecordingUiEvents();
        var (_, context, _, _) = TestAppBuilder.BuildApp(prompts: prompts, events: events);
        context.ModelBrowserService = new StubBrowser(
            new ModelSelection("claude-opus-4-8", "low") { EffortChosen = true });
        context.Session.ModelListCache[Provider] = Models();
        context.Session.Model = "claude-opus-4-8";
        var command = new ModelCommand((_, _) => "note", (_, _, _) => "saved");

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains(events.Events, e => e is SessionMetadataChangedEvent);
    }

    /// <summary>
    /// Copilot/OpenAI models advertise their levels at runtime rather than through static rules, so
    /// the capability must be resolved WITH those levels. Resolving without them reports every such
    /// model as unsupported, which silently resolves the picked level to null — the session then
    /// reasons at the model default while the status bar agrees with the wrong value.
    /// </summary>
    [Fact]
    public async Task A_level_picked_for_a_copilot_model_reaches_the_session()
    {
        const string copilot = "github-copilot";
        var prompts = new RecordingPromptService(new UiPromptResponse(false, [], null));
        var (_, context, _, _) = TestAppBuilder.BuildApp(prompts: prompts);
        context.Session.ActiveProviderId = copilot;
        context.Session.Model = "gpt-5.5";
        context.ModelBrowserService = new StubBrowser(
            new ModelSelection("gpt-5.6-sol", "xhigh") { EffortChosen = true });
        context.Session.ModelListCache[copilot] = new ModelListResult(
            copilot,
            ModelSource.Live,
            [
                new ModelListEntry("gpt-5.5", "GPT-5.5", 400_000, ["low", "medium", "high"]),
                new ModelListEntry("gpt-5.6-sol", "Sol", 400_000, ["low", "medium", "high", "xhigh"]),
            ]);
        var command = new ModelCommand((_, _) => "note", (_, _, _) => "saved");

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal("gpt-5.6-sol", context.Session.Model);
        Assert.Equal("xhigh", context.Session.EffortByModel[$"{copilot}/gpt-5.6-sol"]);
        Assert.Equal("xhigh", context.Session.Effort);
    }

    /// <summary>Switching TO a Copilot model must likewise restore its stored level, not drop it.</summary>
    [Fact]
    public async Task Switching_to_a_copilot_model_restores_its_stored_level()
    {
        const string copilot = "github-copilot";
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.ActiveProviderId = copilot;
        context.Session.Model = "gpt-5.5";
        context.Session.EffortByModel[$"{copilot}/gpt-5.6-sol"] = "xhigh";
        context.Session.ModelListCache[copilot] = new ModelListResult(
            copilot,
            ModelSource.Live,
            [
                new ModelListEntry("gpt-5.5", "GPT-5.5", 400_000, ["low", "medium", "high"]),
                new ModelListEntry("gpt-5.6-sol", "Sol", 400_000, ["low", "medium", "high", "xhigh"]),
            ]);

        var command = new ModelCommand((_, _) => "note", (_, _, _) => "saved");
        await command.ExecuteAsync(context, ["gpt-5.6-sol"], CancellationToken.None);

        Assert.Equal("xhigh", context.Session.Effort);
    }
}
