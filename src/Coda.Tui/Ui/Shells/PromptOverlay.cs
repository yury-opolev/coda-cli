using System.Collections.Immutable;
using System.Text;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Terminal.Gui.Drivers;

namespace Coda.Tui.Ui.Shells;

internal sealed class PromptOverlay : View, ISelectableOverlay
{
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

        this.titleLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(), CanFocus = false };
        this.bodyLabel = new SelectableTextView(app) { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill() };
        if (onCopyRequested is not null)
        {
            this.bodyLabel.CopyRequested += text => onCopyRequested(text, this.bodyLabel.ClearSelection);
        }

        this.Add(this.titleLabel);
        this.Add(this.bodyLabel);
    }

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
                    var cursor = i == this.selectedIndex ? ">" : " ";
                    var mark = req.Kind == UiPromptKind.SelectMany
                        ? (this.checkedIndices.Contains(i) ? "[x] " : "[ ] ")
                        : string.Empty;
                    builder.Append(cursor).Append(' ').Append(mark).Append(UiPromptOptionFormatter.Format(option));
                    builder.Append('\n');
                }

                if (req.AllowFreeText)
                {
                    var freeTextCursor = this.selectedIndex == req.Options.Length ? ">" : " ";
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
