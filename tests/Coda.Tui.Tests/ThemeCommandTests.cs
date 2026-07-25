using Coda.Tui.Commands;
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Tests;

[Collection("ThemeState")]
public sealed class ThemeCommandTests : IDisposable
{
    private readonly Coda.Tui.Ui.Rendering.CodaTheme originalTheme = Coda.Tui.Ui.Rendering.CodaThemes.Current;

    public ThemeCommandTests()
    {
        Coda.Tui.Ui.Rendering.CodaThemes.Set(Coda.Tui.Ui.Rendering.CodaThemes.Default);
    }

    public void Dispose()
    {
        Coda.Tui.Ui.Rendering.CodaThemes.Set(this.originalTheme);
    }

    [Fact]
    public async Task Theme_without_args_in_plain_mode_lists_available_themes_and_marks_current()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp(prompts: PlainUiPromptService.Instance);

        var command = new ThemeCommand();
        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("Current theme", console.Output);
        Assert.Contains("default", console.Output);
        Assert.Contains("warm-ember", console.Output);
        Assert.Contains("cool-dark", console.Output);
        Assert.Contains("(active)", console.Output);
    }

    [Fact]
    public async Task Theme_with_name_sets_the_current_theme_and_persists_it()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp(prompts: PlainUiPromptService.Instance);
        var persisted = new List<string>();
        var command = new ThemeCommand(themeName =>
        {
            persisted.Add(themeName);
            return "— saved.";
        });

        await command.ExecuteAsync(context, ["warm-ember"], CancellationToken.None);

        Assert.Equal("warm-ember", Coda.Tui.Ui.Rendering.CodaThemes.Current.Name);
        Assert.Equal(["warm-ember"], persisted);
        Assert.Contains("warm-ember", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Theme_with_unknown_name_warns_and_does_not_change_the_current_theme()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp(prompts: PlainUiPromptService.Instance);
        var persisted = 0;
        var command = new ThemeCommand(_ =>
        {
            persisted++;
            return "— saved.";
        });

        await command.ExecuteAsync(context, ["mystery"], CancellationToken.None);

        Assert.Equal("default", Coda.Tui.Ui.Rendering.CodaThemes.Current.Name);
        Assert.Equal(0, persisted);
        Assert.Contains("Unknown theme", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default, warm-ember, cool-dark", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Interactive_theme_picker_cancel_keeps_the_original_theme_and_does_not_persist()
    {
        Coda.Tui.Ui.Rendering.CodaThemes.Set(Coda.Tui.Ui.Rendering.CodaThemes.WarmEmber);
        var prompts = new RecordingPromptService(new UiPromptResponse(true, [], null));
        var (_, context, _, _) = TestAppBuilder.BuildApp(prompts: prompts);
        var persisted = 0;
        var command = new ThemeCommand(_ =>
        {
            persisted++;
            return "— saved.";
        });

        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal("warm-ember", Coda.Tui.Ui.Rendering.CodaThemes.Current.Name);
        Assert.Equal(0, persisted);
        var request = Assert.Single(prompts.Requests);
        Assert.Equal("Choose a theme", request.Title);
        Assert.Equal("warm-ember", request.DefaultValue);
    }

    [Fact]
    public async Task Interactive_picker_selection_sets_the_theme_and_persists_it()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["cool-dark"], null));
        var (_, context, console, _) = TestAppBuilder.BuildApp(prompts: prompts);
        var persisted = new List<string>();
        var command = new ThemeCommand(themeName =>
        {
            persisted.Add(themeName);
            return "— saved.";
        });

        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal("cool-dark", Coda.Tui.Ui.Rendering.CodaThemes.Current.Name);
        Assert.Equal(["cool-dark"], persisted);
        Assert.Contains("cool-dark", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Interactive_picker_supplies_on_highlight_callback()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["warm-ember"], null));
        var (_, context, _, _) = TestAppBuilder.BuildApp(prompts: prompts);
        var command = new ThemeCommand(_ => "— saved.");

        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        var request = Assert.Single(prompts.Requests);
        Assert.NotNull(request.OnHighlight);
    }

    [Fact]
    public async Task Interactive_picker_live_preview_changes_theme_for_each_highlighted_option()
    {
        Coda.Tui.Ui.Rendering.CodaThemes.Set(Coda.Tui.Ui.Rendering.CodaThemes.Default);
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["cool-dark"], null));
        prompts.PendingHighlights.Enqueue(["warm-ember", "cool-dark"]);
        var (_, context, _, _) = TestAppBuilder.BuildApp(prompts: prompts);
        var themesDuringPicker = new List<string>();
        var command = new ThemeCommand(_ => "— saved.");

        // Intercept theme changes by subscribing before execution
        void OnChanged() => themesDuringPicker.Add(Coda.Tui.Ui.Rendering.CodaThemes.Current.Name);
        Coda.Tui.Ui.Rendering.CodaThemes.Changed += OnChanged;
        try
        {
            await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);
        }
        finally
        {
            Coda.Tui.Ui.Rendering.CodaThemes.Changed -= OnChanged;
        }

        // Highlights fired warm-ember then cool-dark; final commit also sets cool-dark (idempotent).
        Assert.Contains("warm-ember", themesDuringPicker);
        Assert.Contains("cool-dark", themesDuringPicker);
        Assert.Equal("cool-dark", Coda.Tui.Ui.Rendering.CodaThemes.Current.Name);
    }

    [Fact]
    public async Task Interactive_picker_cancel_after_live_preview_reverts_to_original_and_does_not_persist()
    {
        Coda.Tui.Ui.Rendering.CodaThemes.Set(Coda.Tui.Ui.Rendering.CodaThemes.Default);
        var prompts = new RecordingPromptService(new UiPromptResponse(true, [], null)); // cancelled
        prompts.PendingHighlights.Enqueue(["warm-ember"]);  // preview warm-ember, then user hits Esc
        var (_, context, _, _) = TestAppBuilder.BuildApp(prompts: prompts);
        var persisted = 0;
        var command = new ThemeCommand(_ => { persisted++; return "— saved."; });

        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal("default", Coda.Tui.Ui.Rendering.CodaThemes.Current.Name);
        Assert.Equal(0, persisted);
    }
}
