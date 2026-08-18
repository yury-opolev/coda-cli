using System.Collections.Immutable;
using System.Text;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Terminal.Gui.Drivers;

namespace Coda.Tui.Ui.Shells;

internal sealed class PromptOverlay : View, ISelectableOverlay
{
    /// <summary>Row the body starts on; rows 0-1 belong to the title (and its optional message).</summary>
    private const int BodyTop = 2;

    /// <summary>
    /// Narrowest the dialog is allowed to get. Below this an option label wraps into unreadable
    /// stubs, which is worse than a box that is wider than its content.
    /// </summary>
    private const int MinDialogWidth = 40;

    /// <summary>
    /// Widest the dialog is allowed to get regardless of terminal size. A prompt stretched across a
    /// 200-column terminal is harder to read than one the eye can take in without travelling.
    /// </summary>
    private const int MaxDialogWidth = 100;

    /// <summary>Share of the host surface the dialog may occupy before it goes full-screen.</summary>
    private const int MaxHostPercent = 80;

    /// <summary>Rows the rounded border takes off the content area (top + bottom).</summary>
    private const int Chrome = 2;

    /// <summary>
    /// Columns the border and its inside padding take off the content area: 2 for the border and
    /// 2 for the one-column gutter <see cref="Padding"/> puts on each side.
    /// </summary>
    private const int HorizontalChrome = 4;

    private readonly IUiEventPublisher publisher;
    private TuiTheme theme;
    private readonly Label titleLabel;
    private readonly SelectableTextView bodyLabel;
    private readonly HashSet<int> checkedIndices = [];
    private readonly StringBuilder textBuffer = new();

    private UiPromptRequest? request;
    private int selectedIndex;
    private bool completed;
    private bool freeTextMode;

    public PromptOverlay(IUiEventPublisher publisher, TuiTheme? theme = null, IApplication? app = null, Action<string, Action>? onCopyRequested = null)
    {
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.theme = theme ?? CodaThemes.Current.Tui;
        this.CanFocus = true;
        this.Visible = false;
        this.BorderStyle = LineStyle.Rounded;

        // One blank column inside each side of the border. Without it the longest option label
        // butts straight up against the box edge, which reads as clipped text.
        this.Padding.Thickness = new Thickness(1, 0, 1, 0);

        this.titleLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(), CanFocus = false };
        this.bodyLabel = new SelectableTextView(app) { X = 0, Y = BodyTop, Width = Dim.Fill(), Height = Dim.Fill() };
        if (onCopyRequested is not null)
        {
            this.bodyLabel.CopyRequested += text => onCopyRequested(text, this.bodyLabel.ClearSelection);
        }

        // The overlay sizes itself to its content and centres in its host. Dim.Func re-measures on
        // every layout pass, so a terminal resize re-clamps and re-centres the dialog without the
        // hosting shell having to know anything about prompt geometry.
        this.X = Pos.Center();
        this.Y = Pos.Center();
        this.Width = Dim.Func(_ => this.Measure().Width, this);
        this.Height = Dim.Func(_ => this.Measure().Height, this);

        this.Add(this.titleLabel);
        this.Add(this.bodyLabel);
    }

    /// <summary>The dialog box the overlay should occupy, in host coordinates.</summary>
    /// <remarks>
    /// Recomputed rather than cached because <see cref="Dim.Func"/> calls it during layout, which is
    /// also the only moment the host's own size is reliably known.
    /// </remarks>
    private (int Width, int Height) Measure()
    {
        var host = this.HostSize();
        if (host.Width <= 0 || host.Height <= 0 || this.request is null)
        {
            return (Math.Max(1, host.Width), Math.Max(1, host.Height));
        }

        var titleWidth = LongestLine(this.titleLabel.Text);
        var bodyLines = SplitLines(this.bodyLabel.AllText);
        var bodyWidth = bodyLines.Max(static line => line.Length);

        // The body is pinned to BodyTop, so the title always reserves those rows whether or not a
        // message pushed it onto a second line. The trailing row keeps the last option off the border.
        var contentHeight = BodyTop + bodyLines.Count + 1;

        var maxWidth = Math.Min(host.Width * MaxHostPercent / 100, MaxDialogWidth);
        var maxHeight = host.Height * MaxHostPercent / 100;

        var width = Math.Max(MinDialogWidth, Math.Max(titleWidth, bodyWidth) + HorizontalChrome);
        var height = contentHeight + Chrome;

        // Anything that will not fit inside the clamped box takes the whole screen instead of being
        // silently cropped: an option the user cannot see is an option the user cannot choose.
        return width > maxWidth || height > maxHeight || maxWidth < MinDialogWidth
            ? (host.Width, host.Height)
            : (width, height);
    }

    /// <summary>The surface the dialog is centred in — the hosting shell, or the screen.</summary>
    private (int Width, int Height) HostSize()
    {
        if (this.SuperView is { } superView && superView.Viewport is { Width: > 0, Height: > 0 } viewport)
        {
            return (viewport.Width, viewport.Height);
        }

        var screen = this.App?.Screen ?? default;
        return (screen.Width, screen.Height);
    }

    private static List<string> SplitLines(string? text) =>
        string.IsNullOrEmpty(text) ? [string.Empty] : [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    private static int LongestLine(string? text) => SplitLines(text).Max(static line => line.Length);

    internal void ApplyTheme(TuiTheme theme, IDriver? driver)
    {
        this.theme = theme ?? throw new ArgumentNullException(nameof(theme));
        this.bodyLabel.ApplyTheme(this.theme, driver, this.theme.PromptText, this.theme.Background);
        this.SetScheme(this.theme.PromptScheme(driver));
        this.SetNeedsDraw();
    }

    public UiPromptRequest? Request => this.request;
    internal string BodyText => this.bodyLabel.AllText;

    public void Update(UiPromptRequest? next)
    {
        if (next is null)
        {
            this.request = null;
            this.completed = false;

            // Release any grab BEFORE hiding. Once the overlay is invisible its body stops receiving mouse
            // events (Terminal.Gui gates delivery on CanBeVisible, which walks the SuperViews), so a grab
            // taken mid-drag could never see the release that would free it — and a held grab swallows every
            // mouse event in the application for the rest of the session.
            this.bodyLabel.ClearSelection();
            this.Visible = false;
            return;
        }

        if (this.request is not null && this.request.Id == next.Id)
        {
            this.request = next;
            this.Visible = true;
            this.Render();
            return;
        }

        this.request = next;
        this.completed = false;
        this.freeTextMode = false;
        this.selectedIndex = InitialSelection(next);
        this.checkedIndices.Clear();
        this.textBuffer.Clear();
        if (next.Kind is UiPromptKind.Text or UiPromptKind.Secret && next.DefaultValue is { Length: > 0 } seed)
        {
            this.textBuffer.Append(seed);
        }

        this.Visible = true;
        this.Render();
        this.InvokeHighlight();
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key is null)
        {
            return false;
        }

        // The overlay holds focus while visible and consumes every key, so the shell's own Ctrl+C handler
        // is unreachable from here — copy an active body selection before anything else claims the key.
        if (key == Key.C.WithCtrl && this.bodyLabel.TryCopySelection())
        {
            return true;
        }

        if (this.request is null || this.completed)
        {
            return base.OnKeyDown(key);
        }

        if (key == Key.Esc)
        {
            if (this.freeTextMode)
            {
                // Esc in text-entry: return to the list without cancelling.
                this.freeTextMode = false;
                this.textBuffer.Clear();
                this.Render();
                return true;
            }

            this.Complete(new UiPromptResponse(true, [], null));
            return true;
        }

        if (this.freeTextMode)
        {
            return this.HandleTextKey(key);
        }

        return this.request.Kind switch
        {
            UiPromptKind.Text or UiPromptKind.Secret => this.HandleTextKey(key),
            UiPromptKind.SelectMany => this.HandleChoiceKey(this.request, key, multiSelect: true),
            _ => this.HandleChoiceKey(this.request, key, multiSelect: false),
        };
    }
    SelectableTextView ISelectableOverlay.Body => this.bodyLabel;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.bodyLabel.CancelMouseInteraction();
            this.request = null;
        }

        base.Dispose(disposing);
    }

    private bool HandleChoiceKey(UiPromptRequest req, Key key, bool multiSelect)
    {
        var count = req.Options.Length + (req.AllowFreeText ? 1 : 0);

        if (key == Key.CursorDown || key == Key.Tab || key == Key.CursorRight)
        {
            this.MoveSelection(1, count);
            return true;
        }

        if (key == Key.CursorUp || key == Key.Tab.WithShift || key == Key.CursorLeft)
        {
            this.MoveSelection(-1, count);
            return true;
        }

        if (multiSelect && key == Key.Space)
        {
            // Space does not toggle the synthetic free-text row.
            if (!req.AllowFreeText || this.selectedIndex < req.Options.Length)
            {
                this.Toggle(this.selectedIndex);
                this.Render();
            }

            return true;
        }

        if (key == Key.Enter || (!multiSelect && key == Key.Space))
        {
            if (req.AllowFreeText && this.selectedIndex == req.Options.Length)
            {
                // The user chose the synthetic "✎ Type your own answer…" row.
                this.freeTextMode = true;
                this.textBuffer.Clear();
                this.Render();
                return true;
            }

            this.Complete(this.BuildChoiceResponse(req, multiSelect));
            return true;
        }

        return true;
    }

    private bool HandleTextKey(Key key)
    {
        if (key == Key.Enter)
        {
            this.Complete(new UiPromptResponse(false, [], this.textBuffer.ToString()));
            return true;
        }

        if (key == Key.Backspace)
        {
            if (this.textBuffer.Length > 0)
            {
                this.textBuffer.Remove(this.textBuffer.Length - 1, 1);
                this.Render();
            }

            return true;
        }

        if (TryGetPrintable(key, out var text))
        {
            this.textBuffer.Append(text);
            this.Render();
            return true;
        }

        return true;
    }

    private UiPromptResponse BuildChoiceResponse(UiPromptRequest req, bool multiSelect)
    {
        if (multiSelect)
        {
            var builder = ImmutableArray.CreateBuilder<string>();
            for (var i = 0; i < req.Options.Length; i++)
            {
                if (this.checkedIndices.Contains(i))
                {
                    builder.Add(req.Options[i].Id);
                }
            }

            return new UiPromptResponse(false, builder.ToImmutable(), null);
        }

        if (req.Options.Length == 0)
        {
            return new UiPromptResponse(false, [], null);
        }

        var index = Math.Clamp(this.selectedIndex, 0, req.Options.Length - 1);
        return new UiPromptResponse(false, [req.Options[index].Id], null);
    }

    private void Complete(UiPromptResponse response)
    {
        if (this.completed || this.request is null)
        {
            return;
        }

        this.completed = true;
        this.publisher.Publish(new UiPromptResponseSubmittedEvent(this.request.Id, response));
    }

    private void MoveSelection(int delta, int count)
    {
        if (count <= 0)
        {
            return;
        }

        this.selectedIndex = ((this.selectedIndex + delta) % count + count) % count;
        this.InvokeHighlight();
        this.Render();
    }

    private void Toggle(int index)
    {
        if (!this.checkedIndices.Add(index))
        {
            this.checkedIndices.Remove(index);
        }
    }

    private void InvokeHighlight()
    {
        if (this.request is { OnHighlight: { } cb, Options.Length: > 0 } req
            && req.Kind is not (UiPromptKind.Text or UiPromptKind.Secret)
            && this.selectedIndex < req.Options.Length)
        {
            var idx = Math.Clamp(this.selectedIndex, 0, req.Options.Length - 1);
            cb(req.Options[idx].Id);
        }
    }

    private void Render()
    {
        if (this.request is not { } req)
        {
            this.titleLabel.Text = string.Empty;
            this.bodyLabel.SetText(string.Empty);
            return;
        }

        this.titleLabel.Text = req.Message is { Length: > 0 } message ? $"{req.Title}\n{message}" : req.Title;
        this.bodyLabel.SetText(this.RenderBody(req));
    }

    private string RenderBody(UiPromptRequest req)
    {
        if (this.freeTextMode)
        {
            return this.textBuffer.ToString();
        }

        switch (req.Kind)
        {
            case UiPromptKind.Text:
                return this.textBuffer.ToString();

            case UiPromptKind.Secret:
                return new string('*', this.textBuffer.Length);

            default:
                var builder = new StringBuilder();
                for (var i = 0; i < req.Options.Length; i++)
                {
                    var option = req.Options[i];
                    var cursor = i == this.selectedIndex ? "\u276f" : " ";
                    var mark = req.Kind == UiPromptKind.SelectMany
                        ? (this.checkedIndices.Contains(i) ? "[x] " : "[ ] ")
                        : string.Empty;
                    builder.Append(cursor).Append(' ').Append(mark).Append(UiPromptOptionFormatter.Format(option));
                    builder.Append('\n');
                }

                if (req.AllowFreeText)
                {
                    var freeTextCursor = this.selectedIndex == req.Options.Length ? "\u276f" : " ";
                    builder.Append(freeTextCursor).Append(" \u270e Type your own answer\u2026");
                }
                else if (builder.Length > 0)
                {
                    // Remove the trailing newline added by the last option.
                    builder.Remove(builder.Length - 1, 1);
                }

                return builder.ToString();
        }
    }

    private static int InitialSelection(UiPromptRequest request)
    {
        if (request.Kind is not (UiPromptKind.Confirm or UiPromptKind.SelectOne))
        {
            return 0;
        }

        if (request.DefaultValue is { Length: > 0 } defaultId)
        {
            for (var i = 0; i < request.Options.Length; i++)
            {
                if (string.Equals(request.Options[i].Id, defaultId, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        return 0;
    }

    private static bool TryGetPrintable(Key key, out string text)
    {
        text = string.Empty;
        if (key is null || key.IsCtrl || key.IsAlt)
        {
            return false;
        }

        var rune = key.AsRune;
        if (rune.Value == 0 || System.Text.Rune.IsControl(rune))
        {
            return false;
        }

        text = rune.ToString();
        return true;
    }
}
