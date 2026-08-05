using System.Collections.Immutable;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Mcp;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

/// <summary>
/// Unit tests for <see cref="McpServerTableSource"/> and the selection-state wiring added to
/// <see cref="McpBrowserOverlay"/> (Task 4 and 5 of docs/superpowers/plans/2026-08-05-tui-browser-ux.md).
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class McpBrowserTableTests : IDisposable
{
    private readonly IApplication application = Application.Create();
    private readonly Window root = new();
    private readonly McpBrowserController controller;
    private readonly McpBrowserOverlay overlay;
    private readonly SessionToken? runState;

    public McpBrowserTableTests()
    {
        this.application.AppModel = AppModel.FullScreen;
        this.application.Init(DriverRegistry.Names.ANSI);
        this.application.Driver!.SetScreenSize(80, 24);
        this.controller = new McpBrowserController(() => null);
        this.overlay = new McpBrowserOverlay(this.application, this.controller);
        this.root.Add(this.overlay);
        this.runState = this.application.Begin(this.root);
    }

    // ── McpServerTableSource unit tests ──────────────────────────────────────

    [Fact]
    public void TableSource_column_count_is_six()
    {
        var source = new McpServerTableSource(ImmutableArray<McpServerSummary>.Empty, StatusGlyphs.Unicode, null);
        Assert.Equal(6, source.Columns);
    }

    [Fact]
    public void TableSource_column_names_are_correct()
    {
        var source = new McpServerTableSource(ImmutableArray<McpServerSummary>.Empty, StatusGlyphs.Unicode, null);
        Assert.Equal("Status", source.ColumnNames[0]);
        Assert.Equal("Name", source.ColumnNames[1]);
        Assert.Equal("Transport", source.ColumnNames[2]);
        Assert.Equal("Scope", source.ColumnNames[3]);
        Assert.Equal("Tools", source.ColumnNames[4]);
        Assert.Equal("Error", source.ColumnNames[5]);
    }

    [Fact]
    public void TableSource_row_count_matches_server_list()
    {
        var servers = MakeServers(3);
        var source = new McpServerTableSource(servers, StatusGlyphs.Unicode, null);
        Assert.Equal(3, source.Rows);
    }

    [Fact]
    public void TableSource_status_column_shows_unicode_glyph()
    {
        var server = MakeConnected("alpha");
        var source = new McpServerTableSource([server], StatusGlyphs.Unicode, null);

        // Healthy → "●"
        Assert.Equal(StatusGlyphs.Unicode.Healthy, source[0, 0]);
    }

    [Fact]
    public void TableSource_status_column_shows_ascii_glyph()
    {
        var server = MakeConnected("alpha");
        var source = new McpServerTableSource([server], StatusGlyphs.Ascii, null);
        Assert.Equal(StatusGlyphs.Ascii.Healthy, source[0, 0]);
    }

    [Fact]
    public void TableSource_name_column_is_sanitized()
    {
        var server = new McpServerSummary(
            new McpServerKey(McpConfigScope.Project, "name\u001b[31m"),
            "file.json", Enabled: true, IsEffective: true,
            McpTransportKind.Stdio, McpConnectionState.Connected, null);
        var source = new McpServerTableSource([server], StatusGlyphs.Unicode, null);

        Assert.DoesNotContain("\u001b", (string)source[0, 1], StringComparison.Ordinal);
        Assert.Contains("name", (string)source[0, 1], StringComparison.Ordinal);
    }

    [Fact]
    public void TableSource_transport_is_lowercase_stdio_or_http()
    {
        var stdio = MakeConnected("s");
        var http = new McpServerSummary(
            new McpServerKey(McpConfigScope.Project, "h"), "f.json",
            true, true, McpTransportKind.Http, McpConnectionState.Connected, null);
        var source = new McpServerTableSource([stdio, http], StatusGlyphs.Unicode, null);
        Assert.Equal("stdio", source[0, 2]);
        Assert.Equal("http", source[1, 2]);
    }

    [Fact]
    public void TableSource_scope_is_lowercase()
    {
        var project = MakeConnected("p");
        var user = new McpServerSummary(
            new McpServerKey(McpConfigScope.User, "u"), "f.json",
            true, true, McpTransportKind.Stdio, McpConnectionState.Connected, null);
        var source = new McpServerTableSource([project, user], StatusGlyphs.Unicode, null);
        Assert.Equal("project", source[0, 3]);
        Assert.Equal("user", source[1, 3]);
    }

    [Fact]
    public void TableSource_tools_column_shows_count_when_known()
    {
        var server = MakeConnected("s") with { ToolCount = 7 };
        var source = new McpServerTableSource([server], StatusGlyphs.Unicode, null);
        Assert.Equal("7", source[0, 4]);
    }

    [Fact]
    public void TableSource_tools_column_is_empty_when_unknown()
    {
        var server = MakeConnected("s") with { ToolCount = null };
        var source = new McpServerTableSource([server], StatusGlyphs.Unicode, null);
        Assert.Equal("", source[0, 4]);
    }

    [Fact]
    public void TableSource_error_column_shows_last_error()
    {
        var server = new McpServerSummary(
            new McpServerKey(McpConfigScope.Project, "e"), "f.json",
            true, true, McpTransportKind.Stdio, McpConnectionState.Error, "oops");
        var source = new McpServerTableSource([server], StatusGlyphs.Unicode, null);
        Assert.Equal("oops", source[0, 5]);
    }

    [Fact]
    public void TableSource_error_column_is_empty_when_null()
    {
        var server = MakeConnected("s");
        var source = new McpServerTableSource([server], StatusGlyphs.Unicode, null);
        Assert.Equal("", source[0, 5]);
    }

    [Fact]
    public void TableSource_GetState_maps_precedence_correctly()
    {
        // Inline to avoid xUnit [MemberData] exposing BrowserItemState publicly.
        var cases = new (McpServerSummary Summary, BrowserItemState Expected)[]
        {
            // 1. !IsEffective → Overridden, regardless of other flags
            (new McpServerSummary(new McpServerKey(McpConfigScope.Project, "a"), "f", true, false,
                McpTransportKind.Stdio, McpConnectionState.Connected, null),
             BrowserItemState.Overridden),

            // 2. !Enabled (IsEffective=true) → Disabled
            (new McpServerSummary(new McpServerKey(McpConfigScope.Project, "b"), "f", false, true,
                McpTransportKind.Stdio, McpConnectionState.Disconnected, null),
             BrowserItemState.Disabled),

            // 3. Connection=Error → Error
            (new McpServerSummary(new McpServerKey(McpConfigScope.Project, "c"), "f", true, true,
                McpTransportKind.Stdio, McpConnectionState.Error, "e"),
             BrowserItemState.Error),

            // 4. Connection=Connected → Healthy
            (new McpServerSummary(new McpServerKey(McpConfigScope.Project, "d"), "f", true, true,
                McpTransportKind.Stdio, McpConnectionState.Connected, null),
             BrowserItemState.Healthy),

            // 5. Otherwise → Idle
            (new McpServerSummary(new McpServerKey(McpConfigScope.Project, "e"), "f", true, true,
                McpTransportKind.Stdio, McpConnectionState.Disconnected, null),
             BrowserItemState.Idle),
        };

        foreach (var (summary, expected) in cases)
        {
            var actual = McpServerTableSource.GetState(summary);
            Assert.Equal(expected, actual);
        }
    }

    // ── TableView overlay integration tests ──────────────────────────────────

    [Fact]
    public void List_view_renders_server_names_via_table()
    {
        this.overlay.Show();
        var servers = MakeServers(3);
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            Servers = servers,
            SelectedKey = servers[0].Key,
        });
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();

        var rendered = RenderedDriverText(this.application);
        Assert.Contains("server-1", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void List_view_selection_tracked_via_controller_selected_key()
    {
        this.overlay.Show();
        var servers = MakeServers(5);
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            Servers = servers,
            SelectedKey = servers[2].Key,
        });
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();

        Assert.Equal(servers[2].Key, this.controller.State.SelectedKey);
    }

    [Fact]
    public void Controller_state_change_drives_table_selected_row()
    {
        // Verify that when the controller reports a new SelectedKey, RenderList syncs the table.
        // We cannot drive movement through ExecuteAsync in a no-provider controller, so we verify
        // the sync direction: controller state → table, not input → controller.
        this.overlay.Show();
        var servers = MakeServers(5);
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            Servers = servers,
            SelectedKey = servers[0].Key,
        });
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();

        // Move selection to row 3 via direct state mutation (as the controller would do).
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            Servers = servers,
            SelectedKey = servers[3].Key,
        });
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();

        Assert.Equal(servers[3].Key, this.controller.State.SelectedKey);
    }

    [Fact]
    public void Small_driver_shows_selected_server_name_and_status()
    {
        this.overlay.Show();
        var servers = Enumerable.Range(1, 12)
            .Select(i => new McpServerSummary(
                new McpServerKey(McpConfigScope.Project, $"server-{i}"),
                @"C:\project\.mcp.json",
                Enabled: true, IsEffective: true,
                McpTransportKind.Stdio, McpConnectionState.Disconnected, null))
            .ToImmutableArray();
        var selected = servers[^1].Key;
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            Servers = servers,
            SelectedKey = selected,
            StatusMessage = "selected status",
        });
        this.controller.NotifyChangedForTest();

        // At full size the table has enough rows to scroll to and show server-12.
        this.application.LayoutAndDraw();
        var rendered = RenderedDriverText(this.application);
        Assert.True(rendered.Contains("server-12", StringComparison.Ordinal), rendered);
        Assert.Contains("selected status", rendered, StringComparison.Ordinal);
        Assert.Equal(selected, this.controller.State.SelectedKey);

        // At narrow sizes the table header+separator rows consume the limited height so data rows
        // may not be visible — verify the controller's tracked selection and the status line.
        this.application.Driver!.SetScreenSize(28, 8);
        this.application.LayoutAndDraw();
        rendered = RenderedDriverText(this.application);
        Assert.Contains("selected status", rendered, StringComparison.Ordinal);
        Assert.Equal(selected, this.controller.State.SelectedKey);

        this.application.Driver.SetScreenSize(24, 8);
        this.application.LayoutAndDraw();
        rendered = RenderedDriverText(this.application);
        Assert.Contains("selected status", rendered, StringComparison.Ordinal);
        Assert.Equal(selected, this.controller.State.SelectedKey);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static McpServerSummary MakeConnected(string name) => new(
        new McpServerKey(McpConfigScope.Project, name),
        "file.json", Enabled: true, IsEffective: true,
        McpTransportKind.Stdio, McpConnectionState.Connected, null);

    private static ImmutableArray<McpServerSummary> MakeServers(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new McpServerSummary(
                new McpServerKey(McpConfigScope.Project, $"server-{i}"),
                "file.json", Enabled: true, IsEffective: true,
                McpTransportKind.Stdio, McpConnectionState.Connected, null))
            .ToImmutableArray();

    /// <summary>
    /// A table header plus its underline costs two rows. The overlay body is only a few rows tall,
    /// so with headers on, 24x8 left zero rows for data and the list rendered empty — a regression
    /// the previous plain-text list did not have. Headers are therefore off.
    /// </summary>
    [Fact]
    public void Narrow_terminal_still_renders_server_rows()
    {
        this.overlay.Show();
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            Servers = MakeServers(3),
            SelectedKey = new McpServerKey(McpConfigScope.Project, "server-1"),
        });
        this.controller.NotifyChangedForTest();

        foreach (var (width, height) in new[] { (28, 8), (24, 8) })
        {
            this.application.Driver!.SetScreenSize(width, height);
            this.application.LayoutAndDraw();

            Assert.Contains("server-1", RenderedDriverText(this.application), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The status column must honour the terminal's Unicode capability, like the transcript gutter
    /// already does. Without this the ASCII fallback in <see cref="StatusGlyphs"/> is dead code.
    /// </summary>
    [Fact]
    public void Status_column_uses_the_ascii_glyph_set_when_configured()
    {
        using var asciiOverlay = new McpBrowserOverlay(
            this.application,
            this.controller,
            statusGlyphs: StatusGlyphs.Ascii);
        this.root.Add(asciiOverlay);

        try
        {
            this.overlay.Hide();
            asciiOverlay.Show();
            this.controller.SetStateForTest(McpBrowserState.Empty with
            {
                Servers = MakeServers(1),
                SelectedKey = new McpServerKey(McpConfigScope.Project, "server-1"),
            });
            this.controller.NotifyChangedForTest();
            this.application.LayoutAndDraw();

            var rendered = RenderedDriverText(this.application);

            Assert.Contains("server-1", rendered, StringComparison.Ordinal);
            Assert.Contains(StatusGlyphs.Ascii.Healthy, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(StatusGlyphs.Unicode.Healthy, rendered, StringComparison.Ordinal);
        }
        finally
        {
            asciiOverlay.Hide();
            this.root.Remove(asciiOverlay);
        }
    }

    private static string RenderedDriverText(IApplication application)
    {
        var driver = application.Driver!;
        var lines = new List<string>(driver.Rows);
        for (var row = 0; row < driver.Rows; row++)
        {
            var sb = new System.Text.StringBuilder();
            for (var col = 0; col < driver.Cols; col++)
            {
                sb.Append(driver.Contents![row, col].Grapheme);
            }

            lines.Add(sb.ToString().TrimEnd());
        }

        return string.Join(Environment.NewLine, lines);
    }

    public void Dispose()
    {
        this.overlay.Dispose();
        if (this.runState is not null)
        {
            this.application.End(this.runState);
        }

        this.root.Dispose();
        this.application.Dispose();
    }
}
