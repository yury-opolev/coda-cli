using Coda.Sdk;
using Coda.Tui.Commands;

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
}
