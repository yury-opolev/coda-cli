using System.Collections.Immutable;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Covers mouse text selection and copy inside the modal overlays. Every browser overlay and the
/// prompt overlay (which is what <c>/model</c> renders its list into) hosts a
/// <see cref="SelectableTextView"/> body, so their text can be left-dragged and copied with a
/// right-click or Ctrl+C — routed through the shell's single clipboard path.
/// </summary>
public sealed class OverlaySelectionTests : IDisposable
{
    private readonly IApplication app;

    public OverlaySelectionTests()
    {
        this.app = Application.Create();
        this.app.AppModel = AppModel.FullScreen;
        this.app.Init(DriverRegistry.Names.ANSI);
        this.app.Driver!.SetScreenSize(80, 24);
    }

    public void Dispose() => this.app.Dispose();

    // ── Prompt overlay (the /model list) ──────────────────────────────────────

    [Fact]
    public void Prompt_overlay_body_selection_copies_the_dragged_text()
    {
        string? copied = null;
        var overlay = this.NewPromptOverlay((text, clear) =>
        {
            copied = text;
            clear();
        });

        overlay.Update(ModelPrompt());

        var body = BodyOf(overlay);
        Drag(body, 0, 0, 5, 0);
        Assert.True(body.HasSelection);

        body.ProcessMouse(RightClick(2, 0));

        Assert.NotNull(copied);
        Assert.Equal(6, copied!.Length);
        Assert.False(body.HasSelection);
        overlay.Dispose();
    }

    [Fact]
    public void Prompt_overlay_selection_survives_an_identical_re_render()
    {
        var overlay = this.NewPromptOverlay();
        var request = ModelPrompt();
        overlay.Update(request);

        var body = BodyOf(overlay);
        Drag(body, 0, 0, 5, 0);
        Assert.True(body.HasSelection);

        // Overlays re-render on every controller change; identical content must not drop the selection.
        overlay.Update(request);

        Assert.True(body.HasSelection);
        overlay.Dispose();
    }

    [Fact]
    public void Prompt_overlay_arrow_keys_still_navigate_while_a_selection_is_active()
    {
        var overlay = this.NewPromptOverlay();
        overlay.Update(ModelPrompt());

        var body = BodyOf(overlay);
        Drag(body, 0, 0, 5, 0);
        Assert.True(body.HasSelection);

        var before = overlay.BodyText;
        overlay.NewKeyDownEvent(Key.CursorDown);

        // The selection is presentational only: it must never intercept the overlay's own navigation.
        Assert.NotEqual(before, overlay.BodyText);
        overlay.Dispose();
    }

    // ── Skill browser overlay ─────────────────────────────────────────────────

    [Fact]
    public void Skill_overlay_body_selection_copies_the_dragged_text()
    {
        string? copied = null;
        using var temp = new TempSkillDirectory();
        temp.WriteSkill("alpha", "first skill");

        var overlay = new Coda.Tui.Ui.Skills.SkillBrowserOverlay(
            this.app,
            new Coda.Tui.Ui.Skills.SkillBrowserController(
                () => new Coda.Tui.Ui.Skills.SkillBrowserProvider(temp.Path, StateStore: null)),
            TuiTheme.WarmEmber,
            onCopyRequested: (text, clear) =>
            {
                copied = text;
                clear();
            });

        var host = new Window();
        host.Add(overlay);
        var token = this.app.Begin(host)!;
        try
        {
            overlay.Show();
            this.app.LayoutAndDraw();

            // The list view now uses a TableView (Task 10). Navigate to the detail pane (Enter),
            // where the SelectableTextView is visible and supports drag-select / copy.
            overlay.NewKeyDownEvent(Key.Enter);
            this.app.LayoutAndDraw();

            var body = BodyOf(overlay);
            Assert.True(body.Visible, "detail body must be visible after Enter");
            Drag(body, 0, 0, 4, 0);
            Assert.True(body.HasSelection);

            body.ProcessMouse(RightClick(2, 0));

            Assert.NotNull(copied);
            Assert.Equal(5, copied!.Length);
            Assert.False(body.HasSelection);
        }
        finally
        {
            this.app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Skill_overlay_hide_releases_the_mouse_grab()
    {
        using var temp = new TempSkillDirectory();
        temp.WriteSkill("alpha", "first skill");

        var overlay = new Coda.Tui.Ui.Skills.SkillBrowserOverlay(
            this.app,
            new Coda.Tui.Ui.Skills.SkillBrowserController(
                () => new Coda.Tui.Ui.Skills.SkillBrowserProvider(temp.Path, StateStore: null)),
            TuiTheme.WarmEmber);

        var host = new Window();
        host.Add(overlay);
        var token = this.app.Begin(host)!;
        try
        {
            overlay.Show();
            this.app.LayoutAndDraw();

            var body = BodyOf(overlay);
            body.ProcessMouse(LeftPress(0, 0));
            Assert.True(this.app.Mouse!.IsGrabbed(body));

            overlay.Hide();

            // A hidden overlay must never leave the application grabbing its body.
            Assert.False(this.app.Mouse!.IsGrabbed(body));
        }
        finally
        {
            this.app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    // ── Ctrl+C reaches the copy path even though overlays swallow every key ───

    [Fact]
    public void Prompt_overlay_ctrl_c_copies_an_active_body_selection()
    {
        string? copied = null;
        var overlay = this.NewPromptOverlay((text, clear) =>
        {
            copied = text;
            clear();
        });
        overlay.Update(ModelPrompt());

        var body = BodyOf(overlay);
        Drag(body, 0, 0, 5, 0);
        Assert.True(body.HasSelection);

        // The overlay holds focus and consumes every key, so the shell's Ctrl+C handler never sees it —
        // the overlay itself must service the copy.
        var handled = overlay.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.True(handled);
        Assert.NotNull(copied);
        Assert.False(body.HasSelection);
        overlay.Dispose();
    }

    [Fact]
    public void Prompt_overlay_ctrl_c_without_a_selection_does_not_copy()
    {
        var copies = 0;
        var overlay = this.NewPromptOverlay((_, _) => copies++);
        overlay.Update(ModelPrompt());

        overlay.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.Equal(0, copies);
        overlay.Dispose();
    }

    [Fact]
    public void Prompt_overlay_dismissal_releases_the_mouse_grab()
    {
        var overlay = this.NewPromptOverlay();
        var host = new Window();
        host.Add(overlay);
        var token = this.app.Begin(host)!;
        try
        {
            overlay.Update(ModelPrompt());
            this.app.LayoutAndDraw();

            var body = BodyOf(overlay);
            body.ProcessMouse(LeftPress(0, 0));
            Assert.True(this.app.Mouse!.IsGrabbed(body));

            // Dismissing mid-drag must free the grab. Once the overlay is invisible its body stops
            // receiving mouse events, so a grab left behind would swallow every event for the session.
            overlay.Update(null);

            Assert.False(this.app.Mouse!.IsGrabbed(body));
        }
        finally
        {
            this.app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    // ── Body content fidelity ─────────────────────────────────────────────────

    [Fact]
    public void Overlay_body_preserves_leading_indentation()
    {
        var overlay = this.NewPromptOverlay();
        var body = BodyOf(overlay);

        body.SetText("parent\n    child\n        grandchild");

        // Overlays show hierarchy with leading spaces; the single-line sanitizer would collapse them.
        Assert.Equal(["parent", "    child", "        grandchild"], body.Lines);
        overlay.Dispose();
    }

    [Fact]
    public void Overlay_body_strips_escape_sequences_without_touching_indentation()
    {
        var overlay = this.NewPromptOverlay();
        var body = BodyOf(overlay);

        body.SetText("  \u001b[31mred\u001b[0m text");

        Assert.Equal(["  red text"], body.Lines);
        overlay.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PromptOverlay NewPromptOverlay(Action<string, Action>? onCopyRequested = null) =>
        new(new NullPublisher(), TuiTheme.WarmEmber, this.app, onCopyRequested);

    private static UiPromptRequest ModelPrompt() =>
        UiPromptRequest.Select(
            "Choose a model",
            [
                new UiPromptOption("claude-sonnet-4-6", "claude-sonnet-4-6"),
                new UiPromptOption("claude-opus-4-8", "claude-opus-4-8"),
                new UiPromptOption("gpt-5-codex", "gpt-5-codex"),
            ]);

    /// <summary>The overlay's selectable body, found by walking its subviews.</summary>
    private static SelectableTextView BodyOf(View overlay) =>
        overlay.SubViews.OfType<SelectableTextView>().Single();

    private static void Drag(SelectableTextView body, int fromX, int fromY, int toX, int toY)
    {
        body.ProcessMouse(LeftPress(fromX, fromY));
        body.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new System.Drawing.Point(toX, toY),
        });
        body.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new System.Drawing.Point(toX, toY),
        });
    }

    private static Mouse LeftPress(int x, int y) => new()
    {
        Flags = MouseFlags.LeftButtonPressed,
        Position = new System.Drawing.Point(x, y),
    };

    private static Mouse RightClick(int x, int y) => new()
    {
        Flags = MouseFlags.RightButtonClicked,
        Position = new System.Drawing.Point(x, y),
    };

    private sealed class NullPublisher : IUiEventPublisher
    {
        public void Publish(UiEvent uiEvent)
        {
        }
    }

    private sealed class TempSkillDirectory : IDisposable
    {
        public TempSkillDirectory()
        {
            this.Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"coda-overlaysel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(this.Path);
        }

        public string Path { get; }

        public void WriteSkill(string name, string description)
        {
            var dir = System.IO.Path.Combine(this.Path, ".coda", "skills", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                System.IO.Path.Combine(dir, "SKILL.md"),
                $"---\nname: {name}\ndescription: {description}\n---\nbody\n");
        }

        public void Dispose()
        {
            if (Directory.Exists(this.Path))
            {
                Directory.Delete(this.Path, recursive: true);
            }
        }
    }
}
