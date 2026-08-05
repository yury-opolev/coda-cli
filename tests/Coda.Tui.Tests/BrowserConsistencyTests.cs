using Coda.Tui.Ui.Mcp;
using Coda.Tui.Ui.Plugins;
using Coda.Tui.Ui.Schedule;
using Coda.Tui.Ui.Skills;
using Coda.Tui.Ui.Tasks;
using Xunit;

namespace Coda.Tui.Tests;

/// <summary>
/// Table-driven consistency tests asserting that all five browsers share the mandatory key-binding
/// contract defined in Task 11:
/// <list type="bullet">
///   <item>List view: Esc and q close; Up/Down and k/j navigate; PgUp/PgDn and Home/End jump; r reloads; / enters filter.</item>
///   <item>Detail view: Esc and q return to list; Up/Down and k/j also bound.</item>
/// </list>
/// The per-browser command enums are internal, so all assertions live inside a single [Fact] loop
/// rather than in a public [Theory] signature — this satisfies the "do not expose internal types in
/// public test method signatures" constraint.
/// </summary>
public sealed class BrowserConsistencyTests
{
    // ── MCP ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Mcp_List_HasMandatoryBindings()
    {
        Assert.Equal(McpBrowserCommand.Close, McpBrowserKeyMap.Map(Key.Esc, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.Close, McpBrowserKeyMap.Map(new Key('q'), McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.MoveUp, McpBrowserKeyMap.Map(Key.CursorUp, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.MoveUp, McpBrowserKeyMap.Map(new Key('k'), McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.MoveDown, McpBrowserKeyMap.Map(Key.CursorDown, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.MoveDown, McpBrowserKeyMap.Map(new Key('j'), McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.PageUp, McpBrowserKeyMap.Map(Key.PageUp, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.PageDown, McpBrowserKeyMap.Map(Key.PageDown, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.MoveToStart, McpBrowserKeyMap.Map(Key.Home, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.MoveToEnd, McpBrowserKeyMap.Map(Key.End, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.Reload, McpBrowserKeyMap.Map(new Key('r'), McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.Filter, McpBrowserKeyMap.Map(new Key('/'), McpBrowserView.List));
    }

    [Fact]
    public void Mcp_Detail_HasMandatoryBindings()
    {
        Assert.Equal(McpBrowserCommand.ReturnToList, McpBrowserKeyMap.Map(Key.Esc, McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.ReturnToList, McpBrowserKeyMap.Map(new Key('q'), McpBrowserView.Detail));
        // Up/Down and k/j scroll the detail pane content (handled by TryScrollDetail in the overlay,
        // not by the controller command dispatch), so the keymap correctly returns None for them.
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorUp, McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(new Key('k'), McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorDown, McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(new Key('j'), McpBrowserView.Detail));
    }

    [Fact]
    public void Mcp_Editor_PrintableRunes_AreNotActions()
    {
        // In the editor, ordinary letters must NOT map to actions — they are text input.
        foreach (var ch in "qrjkdnaexl/")
        {
            var cmd = McpBrowserKeyMap.Map(new Key(ch), McpBrowserView.Editor);
            Assert.True(cmd == McpBrowserCommand.None, $"'{ch}' must be None in editor view but was {cmd}");
        }
    }

    /// <summary>
    /// The editor footer advertises Tab/↑/↓ field, Enter save, Ctrl+N add, Ctrl+R remove,
    /// Alt+↑/↓ reorder, Esc cancel. Each advertised key must actually map to the documented action
    /// so the footer can never drift from the keymap again.
    /// </summary>
    [Fact]
    public void Mcp_Editor_FooterKeysMustMatchKeymap()
    {
        // Esc → cancel
        Assert.Equal(McpBrowserCommand.EditorCancel, McpBrowserKeyMap.Map(Key.Esc, McpBrowserView.Editor));
        // Enter → apply (focused field decides save vs. prompt vs. no-op)
        Assert.Equal(McpBrowserCommand.EditorApply, McpBrowserKeyMap.Map(Key.Enter, McpBrowserView.Editor));
        // Ctrl+N → add item
        Assert.Equal(McpBrowserCommand.EditorAddItem, McpBrowserKeyMap.Map(Key.N.WithCtrl, McpBrowserView.Editor));
        // Ctrl+R → remove item
        Assert.Equal(McpBrowserCommand.EditorRemoveItem, McpBrowserKeyMap.Map(Key.R.WithCtrl, McpBrowserView.Editor));
        // Alt+↑/↓ → reorder
        Assert.Equal(McpBrowserCommand.EditorReorderUp, McpBrowserKeyMap.Map(Key.CursorUp.WithAlt, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.EditorReorderDown, McpBrowserKeyMap.Map(Key.CursorDown.WithAlt, McpBrowserView.Editor));
        // Tab / Shift+Tab and plain Up/Down handled by McpEditorForm.AdvanceFocus → None from keymap
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.Tab, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.Tab.WithShift, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorUp, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorDown, McpBrowserView.Editor));
        // Ctrl+↑/↓/←/→ are retired and must NOT be bound (footer no longer advertises them)
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorUp.WithCtrl, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorDown.WithCtrl, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorLeft.WithCtrl, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorRight.WithCtrl, McpBrowserView.Editor));
    }

    // ── Skills ───────────────────────────────────────────────────────────────

    [Fact]
    public void Skills_List_HasMandatoryBindings()
    {
        Assert.Equal(SkillBrowserCommand.Close, SkillBrowserKeyMap.Map(Key.Esc, SkillBrowserView.List));
        Assert.Equal(SkillBrowserCommand.Close, SkillBrowserKeyMap.Map(new Key('q'), SkillBrowserView.List));
        Assert.Equal(SkillBrowserCommand.MoveUp, SkillBrowserKeyMap.Map(Key.CursorUp, SkillBrowserView.List));
        Assert.Equal(SkillBrowserCommand.MoveUp, SkillBrowserKeyMap.Map(new Key('k'), SkillBrowserView.List));
        Assert.Equal(SkillBrowserCommand.MoveDown, SkillBrowserKeyMap.Map(Key.CursorDown, SkillBrowserView.List));
        Assert.Equal(SkillBrowserCommand.MoveDown, SkillBrowserKeyMap.Map(new Key('j'), SkillBrowserView.List));
        Assert.Equal(SkillBrowserCommand.PageUp, SkillBrowserKeyMap.Map(Key.PageUp, SkillBrowserView.List));
        Assert.Equal(SkillBrowserCommand.PageDown, SkillBrowserKeyMap.Map(Key.PageDown, SkillBrowserView.List));
        Assert.Equal(SkillBrowserCommand.MoveToStart, SkillBrowserKeyMap.Map(Key.Home, SkillBrowserView.List));
        Assert.Equal(SkillBrowserCommand.MoveToEnd, SkillBrowserKeyMap.Map(Key.End, SkillBrowserView.List));
        Assert.Equal(SkillBrowserCommand.Reload, SkillBrowserKeyMap.Map(new Key('r'), SkillBrowserView.List));
        Assert.Equal(SkillBrowserCommand.Filter, SkillBrowserKeyMap.Map(new Key('/'), SkillBrowserView.List));
    }

    [Fact]
    public void Skills_Detail_HasMandatoryBindings()
    {
        Assert.Equal(SkillBrowserCommand.ReturnToList, SkillBrowserKeyMap.Map(Key.Esc, SkillBrowserView.Detail));
        Assert.Equal(SkillBrowserCommand.ReturnToList, SkillBrowserKeyMap.Map(new Key('q'), SkillBrowserView.Detail));
        Assert.Equal(SkillBrowserCommand.MoveUp, SkillBrowserKeyMap.Map(Key.CursorUp, SkillBrowserView.Detail));
        Assert.Equal(SkillBrowserCommand.MoveUp, SkillBrowserKeyMap.Map(new Key('k'), SkillBrowserView.Detail));
        Assert.Equal(SkillBrowserCommand.MoveDown, SkillBrowserKeyMap.Map(Key.CursorDown, SkillBrowserView.Detail));
        Assert.Equal(SkillBrowserCommand.MoveDown, SkillBrowserKeyMap.Map(new Key('j'), SkillBrowserView.Detail));
        Assert.Equal(SkillBrowserCommand.PageUp, SkillBrowserKeyMap.Map(Key.PageUp, SkillBrowserView.Detail));
        Assert.Equal(SkillBrowserCommand.PageDown, SkillBrowserKeyMap.Map(Key.PageDown, SkillBrowserView.Detail));
        Assert.Equal(SkillBrowserCommand.Reload, SkillBrowserKeyMap.Map(new Key('r'), SkillBrowserView.Detail));
    }

    // ── Plugins ──────────────────────────────────────────────────────────────

    [Fact]
    public void Plugins_List_HasMandatoryBindings()
    {
        Assert.Equal(PluginBrowserCommand.Close, PluginBrowserKeyMap.Map(Key.Esc, PluginBrowserView.List));
        Assert.Equal(PluginBrowserCommand.Close, PluginBrowserKeyMap.Map(new Key('q'), PluginBrowserView.List));
        Assert.Equal(PluginBrowserCommand.MoveUp, PluginBrowserKeyMap.Map(Key.CursorUp, PluginBrowserView.List));
        Assert.Equal(PluginBrowserCommand.MoveUp, PluginBrowserKeyMap.Map(new Key('k'), PluginBrowserView.List));
        Assert.Equal(PluginBrowserCommand.MoveDown, PluginBrowserKeyMap.Map(Key.CursorDown, PluginBrowserView.List));
        Assert.Equal(PluginBrowserCommand.MoveDown, PluginBrowserKeyMap.Map(new Key('j'), PluginBrowserView.List));
        Assert.Equal(PluginBrowserCommand.PageUp, PluginBrowserKeyMap.Map(Key.PageUp, PluginBrowserView.List));
        Assert.Equal(PluginBrowserCommand.PageDown, PluginBrowserKeyMap.Map(Key.PageDown, PluginBrowserView.List));
        Assert.Equal(PluginBrowserCommand.MoveToStart, PluginBrowserKeyMap.Map(Key.Home, PluginBrowserView.List));
        Assert.Equal(PluginBrowserCommand.MoveToEnd, PluginBrowserKeyMap.Map(Key.End, PluginBrowserView.List));
        Assert.Equal(PluginBrowserCommand.Reload, PluginBrowserKeyMap.Map(new Key('r'), PluginBrowserView.List));
        Assert.Equal(PluginBrowserCommand.Filter, PluginBrowserKeyMap.Map(new Key('/'), PluginBrowserView.List));
    }

    [Fact]
    public void Plugins_Detail_HasMandatoryBindings()
    {
        Assert.Equal(PluginBrowserCommand.ReturnToList, PluginBrowserKeyMap.Map(Key.Esc, PluginBrowserView.Detail));
        Assert.Equal(PluginBrowserCommand.ReturnToList, PluginBrowserKeyMap.Map(new Key('q'), PluginBrowserView.Detail));
        Assert.Equal(PluginBrowserCommand.MoveUp, PluginBrowserKeyMap.Map(Key.CursorUp, PluginBrowserView.Detail));
        Assert.Equal(PluginBrowserCommand.MoveUp, PluginBrowserKeyMap.Map(new Key('k'), PluginBrowserView.Detail));
        Assert.Equal(PluginBrowserCommand.MoveDown, PluginBrowserKeyMap.Map(Key.CursorDown, PluginBrowserView.Detail));
        Assert.Equal(PluginBrowserCommand.MoveDown, PluginBrowserKeyMap.Map(new Key('j'), PluginBrowserView.Detail));
        Assert.Equal(PluginBrowserCommand.PageUp, PluginBrowserKeyMap.Map(Key.PageUp, PluginBrowserView.Detail));
        Assert.Equal(PluginBrowserCommand.PageDown, PluginBrowserKeyMap.Map(Key.PageDown, PluginBrowserView.Detail));
        Assert.Equal(PluginBrowserCommand.Reload, PluginBrowserKeyMap.Map(new Key('r'), PluginBrowserView.Detail));
    }

    // ── Schedule ─────────────────────────────────────────────────────────────

    [Fact]
    public void Schedule_List_HasMandatoryBindings()
    {
        // Schedule has no detail pane; all bindings live at list level.
        Assert.Equal(ScheduleBrowserCommand.Close, ScheduleBrowserKeyMap.Map(Key.Esc));
        Assert.Equal(ScheduleBrowserCommand.Close, ScheduleBrowserKeyMap.Map(new Key('q')));
        Assert.Equal(ScheduleBrowserCommand.MoveUp, ScheduleBrowserKeyMap.Map(Key.CursorUp));
        Assert.Equal(ScheduleBrowserCommand.MoveUp, ScheduleBrowserKeyMap.Map(new Key('k')));
        Assert.Equal(ScheduleBrowserCommand.MoveDown, ScheduleBrowserKeyMap.Map(Key.CursorDown));
        Assert.Equal(ScheduleBrowserCommand.MoveDown, ScheduleBrowserKeyMap.Map(new Key('j')));
        Assert.Equal(ScheduleBrowserCommand.PageUp, ScheduleBrowserKeyMap.Map(Key.PageUp));
        Assert.Equal(ScheduleBrowserCommand.PageDown, ScheduleBrowserKeyMap.Map(Key.PageDown));
        Assert.Equal(ScheduleBrowserCommand.MoveToStart, ScheduleBrowserKeyMap.Map(Key.Home));
        Assert.Equal(ScheduleBrowserCommand.MoveToEnd, ScheduleBrowserKeyMap.Map(Key.End));
        Assert.Equal(ScheduleBrowserCommand.Reload, ScheduleBrowserKeyMap.Map(new Key('r')));
        Assert.Equal(ScheduleBrowserCommand.Filter, ScheduleBrowserKeyMap.Map(new Key('/')));
    }

    // ── Tasks ────────────────────────────────────────────────────────────────

    [Fact]
    public void Tasks_List_HasMandatoryBindings()
    {
        Assert.Equal(TaskBrowserCommand.Close, TaskBrowserKeyMap.Map(Key.Esc, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.Close, TaskBrowserKeyMap.Map(new Key('q'), TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.MoveUp, TaskBrowserKeyMap.Map(Key.CursorUp, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.MoveUp, TaskBrowserKeyMap.Map(new Key('k'), TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.MoveDown, TaskBrowserKeyMap.Map(Key.CursorDown, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.MoveDown, TaskBrowserKeyMap.Map(new Key('j'), TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.PageUp, TaskBrowserKeyMap.Map(Key.PageUp, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.PageDown, TaskBrowserKeyMap.Map(Key.PageDown, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.MoveToStart, TaskBrowserKeyMap.Map(Key.Home, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.MoveToEnd, TaskBrowserKeyMap.Map(Key.End, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.Reload, TaskBrowserKeyMap.Map(new Key('r'), TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.Filter, TaskBrowserKeyMap.Map(new Key('/'), TaskBrowserView.List));
        // D8: dismiss is 'd' not 'r'.
        Assert.Equal(TaskBrowserCommand.Dismiss, TaskBrowserKeyMap.Map(new Key('d'), TaskBrowserView.List));
    }

    [Fact]
    public void Tasks_Detail_HasMandatoryBindings()
    {
        Assert.Equal(TaskBrowserCommand.ReturnToList, TaskBrowserKeyMap.Map(Key.Esc, TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ReturnToList, TaskBrowserKeyMap.Map(new Key('q'), TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ScrollUp, TaskBrowserKeyMap.Map(Key.CursorUp, TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ScrollUp, TaskBrowserKeyMap.Map(new Key('k'), TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ScrollDown, TaskBrowserKeyMap.Map(Key.CursorDown, TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ScrollDown, TaskBrowserKeyMap.Map(new Key('j'), TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ScrollUp, TaskBrowserKeyMap.Map(Key.PageUp, TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ScrollDown, TaskBrowserKeyMap.Map(Key.PageDown, TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.Reload, TaskBrowserKeyMap.Map(new Key('r'), TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.Dismiss, TaskBrowserKeyMap.Map(new Key('d'), TaskBrowserView.Detail));
    }

    [Fact]
    public void Tasks_Steering_PrintableRunes_AreNotActions()
    {
        // Printable runes in steering modal are text; only the named chords are actions.
        foreach (var ch in "qrjkdaelxs/")
        {
            var cmd = TaskBrowserKeyMap.Map(new Key(ch), TaskBrowserView.Steering);
            Assert.True(cmd == TaskBrowserCommand.None, $"'{ch}' must be None in steering view but was {cmd}");
        }
    }
}
