using System.Drawing;
using Coda.Tui.Ui.Rendering;
using Terminal.Gui.Drivers;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace Coda.Tui.Ui.Shells;

internal readonly record struct JumpHintHitTarget(int Left, int Width)
{
    public bool Contains(Point point) =>
        point.Y == 0 && point.X >= this.Left && point.X < this.Left + this.Width;
}

/// <summary>A centered, clickable one-row prompt for returning to the newest transcript content.</summary>
internal sealed class JumpToBottomHint : View
{
    private readonly TgAttribute attribute;
    private int unseenBlocks;
    private JumpHintHitTarget? renderedHitTarget;

    public JumpToBottomHint(TuiTheme theme, IDriver? driver)
    {
        this.attribute = theme.JumpHintAttribute(driver);
        this.CanFocus = false;
        this.Height = 1;
        this.Visible = false;
    }

    public event Action? Jump;

    internal JumpHintHitTarget? RenderedHitTargetForTest => this.renderedHitTarget;

    public static string HintText(int unseenBlocks) => unseenBlocks <= 0
        ? "Jump to bottom (Ctrl+End) v"
        : $"{unseenBlocks} new message{(unseenBlocks == 1 ? string.Empty : "s")} (Ctrl+End) v";

    public void Update(bool autoFollow, int unseenBlockCount)
    {
        this.unseenBlocks = unseenBlockCount;
        this.Visible = !autoFollow;
        this.UpdateRenderedHitTarget();
        this.SetNeedsDraw();
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        this.UpdateRenderedHitTarget();
        if (this.Visible
            && mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked)
            && this.renderedHitTarget is { } hitTarget
            && mouse.Position is { } position
            && hitTarget.Contains(position))
        {
            this.Jump?.Invoke();
            return true;
        }

        return false;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Math.Max(0, this.Viewport.Width);
        var (display, left) = this.UpdateRenderedHitTarget();
        this.SetAttribute(this.attribute);
        this.Move(0, 0);
        this.AddStr(new string(' ', width));
        this.Move(left, 0);
        this.AddStr(display);
        return true;
    }

    private (string Display, int Left) UpdateRenderedHitTarget()
    {
        if (!this.Visible)
        {
            this.renderedHitTarget = null;
            return (string.Empty, 0);
        }

        var width = Math.Max(0, this.Viewport.Width);
        var display = TerminalCellText.SliceByCells(HintText(this.unseenBlocks), 0, width);
        var displayWidth = TerminalCellText.Width(display);
        var left = Math.Max(0, (width - displayWidth) / 2);
        this.renderedHitTarget = displayWidth > 0 && displayWidth <= width
            ? new JumpHintHitTarget(left, displayWidth)
            : null;
        return (display, left);
    }
}
