using Coda.Tui.Ui.Rendering;
using Terminal.Gui.Drivers;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace Coda.Tui.Ui.Shells;

/// <summary>A centered, clickable one-row prompt for returning to the newest transcript content.</summary>
internal sealed class JumpToBottomHint : View
{
    private readonly TgAttribute attribute;
    private int unseenBlocks;

    public JumpToBottomHint(TuiTheme theme, IDriver? driver)
    {
        this.attribute = theme.JumpHintAttribute(driver);
        this.CanFocus = false;
        this.Height = 1;
        this.Visible = false;
    }

    public event Action? Jump;

    public static string HintText(int unseenBlocks) => unseenBlocks <= 0
        ? "Jump to bottom (Ctrl+End) ↓"
        : $"{unseenBlocks} new message{(unseenBlocks == 1 ? string.Empty : "s")} (Ctrl+End) ↓";

    public void Update(bool autoFollow, int unseenBlockCount)
    {
        this.unseenBlocks = unseenBlockCount;
        this.Visible = !autoFollow;
        this.SetNeedsDraw();
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
        {
            this.Jump?.Invoke();
            return true;
        }

        return false;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Math.Max(0, this.Viewport.Width);
        var text = HintText(this.unseenBlocks);
        var display = text.Length > width ? text[..width] : text;
        this.SetAttribute(this.attribute);
        this.Move(0, 0);
        this.AddStr(new string(' ', width));
        this.Move(Math.Max(0, (width - display.Length) / 2), 0);
        this.AddStr(display);
        return true;
    }
}
