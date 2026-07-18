using System.Collections.Immutable;
using System.Text;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Terminal.Gui.Drivers;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// A self-contained, keyboard-only prompt surface embedded in a Terminal.Gui shell. It renders one
/// <see cref="UiPromptRequest"/> at a time as a bordered child view — never a nested Spectre widget
/// or a second <c>Application.Run</c> — and answers it by publishing exactly one
/// <see cref="UiPromptResponseSubmittedEvent"/>.
/// </summary>
/// <remarks>
/// The overlay owns its own selection model (highlighted row, checked rows for multi-select, and a
/// text/secret buffer) rather than deriving the answer from focused child controls, which keeps key
/// handling deterministic and unit-testable without a running application loop. A single
/// <see cref="completed"/> latch prevents a duplicate Enter (or a repeated activation) from
/// publishing a second response for the same request.
/// </remarks>
internal sealed class PromptOverlay : View
{
    private readonly IUiEventPublisher publisher;
    private readonly TuiTheme theme;
    private readonly Label titleLabel;
    private readonly Label bodyLabel;
    private readonly HashSet<int> checkedIndices = [];
    private readonly StringBuilder textBuffer = new();

    private UiPromptRequest? request;
    private int selectedIndex;
    private bool completed;

    public PromptOverlay(IUiEventPublisher publisher, TuiTheme? theme = null)
    {
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.theme = theme ?? TuiTheme.WarmEmber;
        this.CanFocus = true;
        this.Visible = false;
        this.BorderStyle = LineStyle.Rounded;

        this.titleLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(), CanFocus = false };
        this.bodyLabel = new Label { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = false };
        this.Add(this.titleLabel);
        this.Add(this.bodyLabel);
    }

    /// <summary>Applies the Warm Ember prompt scheme, resolved for the given driver's color depth.</summary>
    internal void ApplyTheme(IDriver? driver) =>
        this.SetScheme(this.theme.PromptScheme(driver));

    /// <summary>The request currently displayed, or <see langword="null"/> when the overlay is hidden.</summary>
    public UiPromptRequest? Request => this.request;

    /// <summary>The rendered body text, exposed so tests can assert masking and option state.</summary>
    internal string BodyText => this.bodyLabel.Text ?? string.Empty;

    /// <summary>
    /// Shows <paramref name="next"/> (resetting interaction state for a new request id) or, when it
    /// is <see langword="null"/>, hides the overlay. Re-applying the same request id refreshes the
    /// rendered content without discarding the user's in-progress selection or text.
    /// </summary>
    public void Update(UiPromptRequest? next)
    {
        if (next is null)
        {
            this.request = null;
            this.completed = false;
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
        this.selectedIndex = InitialSelection(next);
        this.checkedIndices.Clear();
        this.textBuffer.Clear();
        if (next.Kind is UiPromptKind.Text or UiPromptKind.Secret && next.DefaultValue is { Length: > 0 } seed)
        {
            this.textBuffer.Append(seed);
        }

        this.Visible = true;
        this.Render();
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key is null)
        {
            return false;
        }

        if (this.request is null || this.completed)
        {
            return base.OnKeyDown(key);
        }

        if (key == Key.Esc)
        {
            this.Complete(new UiPromptResponse(true, [], null));
            return true;
        }

        return this.request.Kind switch
        {
            UiPromptKind.Text or UiPromptKind.Secret => this.HandleTextKey(key),
            UiPromptKind.SelectMany => this.HandleChoiceKey(this.request, key, multiSelect: true),
            _ => this.HandleChoiceKey(this.request, key, multiSelect: false),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.request = null;
        }

        base.Dispose(disposing);
    }

    private bool HandleChoiceKey(UiPromptRequest req, Key key, bool multiSelect)
    {
        var count = req.Options.Length;

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
            this.Toggle(this.selectedIndex);
            this.Render();
            return true;
        }

        if (key == Key.Enter || (!multiSelect && key == Key.Space))
        {
            this.Complete(this.BuildChoiceResponse(req, multiSelect));
            return true;
        }

        // Swallow every other key so navigation never escapes the modal overlay.
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
        this.Render();
    }

    private void Toggle(int index)
    {
        if (!this.checkedIndices.Add(index))
        {
            this.checkedIndices.Remove(index);
        }
    }

    private void Render()
    {
        if (this.request is not { } req)
        {
            this.titleLabel.Text = string.Empty;
            this.bodyLabel.Text = string.Empty;
            return;
        }

        this.titleLabel.Text = req.Message is { Length: > 0 } message ? $"{req.Title}\n{message}" : req.Title;
        this.bodyLabel.Text = this.RenderBody(req);
    }

    private string RenderBody(UiPromptRequest req)
    {
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
                    builder.Append(cursor).Append(' ').Append(mark).Append(option.Label);
                    if (option.Detail is { Length: > 0 } detail)
                    {
                        builder.Append(" — ").Append(detail);
                    }

                    if (i < req.Options.Length - 1)
                    {
                        builder.Append('\n');
                    }
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
