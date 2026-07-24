using System.Collections.Immutable;
using System.Drawing;
using System.Text;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Mcp;

namespace Coda.Tui.Tests;

public sealed class McpBrowserOverlayTests : IDisposable
{
    private readonly IApplication application = Application.Create();
    private readonly Window root = new();
    private readonly McpBrowserController controller;
    private readonly McpBrowserOverlay overlay;
    private readonly SessionToken? runState;

    public McpBrowserOverlayTests()
    {
        this.application.AppModel = AppModel.FullScreen;
        this.application.Init(DriverRegistry.Names.ANSI);
        this.application.Driver!.SetScreenSize(80, 24);
        this.controller = new McpBrowserController(() => null);
        this.overlay = new McpBrowserOverlay(this.application, this.controller);
        this.root.Add(this.overlay);
        this.runState = this.application.Begin(this.root);
    }

    [Fact]
    public void Hidden_by_default_and_show_hide_dispose_are_idempotent()
    {
        Assert.False(this.overlay.Visible);
        Assert.Equal(0, this.controller.ChangedSubscriberCount);

        this.overlay.Show();
        this.overlay.Show();
        Assert.True(this.overlay.Visible);
        Assert.True(this.overlay.HasFocus);
        Assert.Equal(1, this.controller.ChangedSubscriberCount);

        this.overlay.Hide();
        this.overlay.Hide();
        Assert.False(this.overlay.Visible);
        Assert.Equal(0, this.controller.ChangedSubscriberCount);

        this.overlay.Dispose();
        this.overlay.Dispose();
        Assert.Equal(0, this.controller.ChangedSubscriberCount);
    }

    [Fact]
    public void Visible_overlay_swallows_every_key_and_mouse_input()
    {
        this.overlay.Show();

        Assert.True(this.overlay.NewKeyDownEvent(new Key('z')));
        Assert.True(this.overlay.NewKeyDownEvent(Key.F12));
        Assert.True(this.overlay.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonClicked,
            Position = new Point(2, 2),
        }));
    }

    [Fact]
    public void Hidden_overlay_does_not_receive_controller_updates()
    {
        this.overlay.Show();
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            StatusMessage = "before hide",
        });
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();
        var beforeHide = this.overlay.VisibleTextForTest;

        this.overlay.Hide();
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            StatusMessage = "after hide",
        });
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();

        Assert.Equal(beforeHide, this.overlay.VisibleTextForTest);
    }

    [Fact]
    public void Editor_masks_replacement_secret_and_sanitizes_visible_text()
    {
        const string secret = "super-secret";
        this.overlay.Show();
        this.controller.SetStateForTest(EditorStateWithSecret(secret));
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();

        Assert.DoesNotContain(secret, this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.Contains("*****", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
    }

    [Fact]
    public void List_and_detail_render_all_runtime_fields_without_ansi_or_markup_interpretation()
    {
        this.overlay.Show();
        var key = new McpServerKey(McpConfigScope.Project, "name[red]\u001b[31m");
        var summary = new McpServerSummary(
            key,
            "C:\\config\\[unsafe].json\u001b[0m",
            Enabled: true,
            IsEffective: false,
            McpTransportKind.Http,
            McpConnectionState.Error,
            "connection failed\u001b[2J");
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            Servers = [summary],
            SelectedKey = key,
            StatusMessage = "status [bold] unsafe",
        });
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();

        Assert.Contains("overridden", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.Contains("connection=Error", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.Contains("[red]", this.overlay.VisibleTextForTest, StringComparison.Ordinal);

        var detail = new McpServerDetail(
            summary,
            Command: null,
            Args: [],
            Url: "https://example.test/[url]\u001b[31m",
            Environment:
            [
                new McpSecretDescriptor("env/TOKEN", "TOKEN", McpSecretSource.Managed, "raw-secret"),
            ],
            Headers:
            [
                new McpSecretDescriptor("header/Auth", "Auth", McpSecretSource.Literal, "raw-header"),
            ],
            AuthMode: McpAuthMode.Bearer,
            ClientId: "client[bold]",
            Scopes: ["scope\u001b[0m"],
            BearerToken: new McpSecretDescriptor("auth/token", "token", McpSecretSource.Managed, "raw-bearer"),
            Tools: [new McpCapabilitySummary("tool[italic]", "description\u001b[2J")],
            Prompts: [],
            Resources: []);
        this.controller.SetStateForTest(McpBrowserState.Empty.OpenDetail(detail));
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();

        Assert.Contains("Source:", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-header", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-bearer", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", this.overlay.VisibleTextForTest, StringComparison.Ordinal);

        Assert.True(this.overlay.NewKeyDownEvent(Key.End));
        this.application.LayoutAndDraw();
        Assert.Contains("Capabilities", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
    }

    [Fact]
    public void Small_driver_reaches_selected_rows_and_status_after_resize()
    {
        this.overlay.Show();
        var servers = Enumerable.Range(1, 12)
            .Select(index => new McpServerSummary(
                new McpServerKey(McpConfigScope.Project, $"server-{index}"),
                @"C:\project\.mcp.json",
                Enabled: true,
                IsEffective: true,
                McpTransportKind.Stdio,
                McpConnectionState.Disconnected,
                LastError: null))
            .ToImmutableArray();
        var selected = servers[^1].Key;
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            Servers = servers,
            SelectedKey = selected,
            StatusMessage = "selected status",
        });
        this.controller.NotifyChangedForTest();
        this.application.Driver!.SetScreenSize(28, 8);
        this.application.LayoutAndDraw();

        var rendered = RenderedDriverText(this.application);
        Assert.True(rendered.Contains("> server-12", StringComparison.Ordinal), rendered);
        Assert.Contains("selected status", rendered, StringComparison.Ordinal);

        this.application.Driver.SetScreenSize(24, 8);
        this.application.LayoutAndDraw();
        rendered = RenderedDriverText(this.application);
        Assert.True(rendered.Contains("> server-12", StringComparison.Ordinal), rendered);
        Assert.Contains("selected status", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Small_driver_reaches_focused_editor_actions_and_status()
    {
        this.overlay.Show();
        var draft = new McpServerDraft(
            Name: "server",
            Scope: McpConfigScope.Project,
            Enabled: true,
            Transport: McpTransportKind.Stdio,
            Command: "node",
            Args: ["server.js"],
            Url: null,
            Environment: [],
            Headers: [],
            AuthMode: McpAuthMode.None,
            ClientId: null,
            Scopes: [],
            BearerToken: new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged));
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            View = McpBrowserView.Editor,
            StatusMessage = "editor status",
            Editor = new McpEditorState(
                McpEditorMode.Edit,
                McpBrowserView.List,
                draft,
                McpEditorField.Save),
        });
        this.controller.NotifyChangedForTest();
        this.application.Driver!.SetScreenSize(28, 8);
        this.application.LayoutAndDraw();

        var rendered = RenderedDriverText(this.application);
        Assert.Contains("Save", rendered, StringComparison.Ordinal);
        Assert.Contains("editor status", rendered, StringComparison.Ordinal);

        this.controller.SetStateForTest(this.controller.State with
        {
            Editor = this.controller.State.Editor! with { FocusedField = McpEditorField.Cancel },
        });
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();
        rendered = RenderedDriverText(this.application);
        Assert.Contains("> Cancel", rendered, StringComparison.Ordinal);
        Assert.Contains("editor status", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Stdio_detail_renders_masked_environment_names_without_raw_secrets()
    {
        this.overlay.Show();
        var summary = new McpServerSummary(
            new McpServerKey(McpConfigScope.Project, "stdio"),
            @"C:\project\.mcp.json",
            Enabled: true,
            IsEffective: true,
            McpTransportKind.Stdio,
            McpConnectionState.Connected,
            LastError: null);
        var detail = new McpServerDetail(
            summary,
            Command: "node",
            Args: ["server.js"],
            Url: null,
            Environment:
            [
                new McpSecretDescriptor("env/TOKEN", "TOKEN", McpSecretSource.Managed, "raw-stdio-secret"),
                new McpSecretDescriptor("env/PLAIN", "PLAIN", McpSecretSource.Literal, "raw-stdio-value"),
            ],
            Headers: [],
            AuthMode: McpAuthMode.None,
            ClientId: null,
            Scopes: [],
            BearerToken: null,
            Tools: [],
            Prompts: [],
            Resources: []);
        this.controller.SetStateForTest(McpBrowserState.Empty.OpenDetail(detail) with
        {
            StatusMessage = "stdio status",
        });
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();

        Assert.Contains("TOKEN", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.Contains("PLAIN", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.Contains("*****", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-stdio-secret", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-stdio-value", this.overlay.VisibleTextForTest, StringComparison.Ordinal);
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

    internal static McpBrowserState EditorStateWithSecret(string secret)
    {
        var draft = new McpServerDraft(
            Name: "server",
            Scope: McpConfigScope.Project,
            Enabled: true,
            Transport: McpTransportKind.Http,
            Command: null,
            Args: [],
            Url: "https://example.test/mcp",
            Environment: [],
            Headers: [],
            AuthMode: McpAuthMode.Bearer,
            ClientId: null,
            Scopes: [],
            BearerToken: new McpSecretChange(
                "auth/token",
                McpSecretChangeKind.Replace,
                new McpSecretReplacement(secret)));
        return McpBrowserState.Empty with
        {
            View = McpBrowserView.Editor,
            Editor = new McpEditorState(
                McpEditorMode.Edit,
                McpBrowserView.List,
                draft,
                McpEditorField.BearerToken),
        };
    }

    private static string RenderedDriverText(IApplication application)
    {
        var driver = application.Driver!;
        var lines = new List<string>(driver.Rows);
        for (var row = 0; row < driver.Rows; row++)
        {
            var line = new StringBuilder();
            for (var col = 0; col < driver.Cols; col++)
            {
                line.Append(driver.Contents![row, col].Grapheme);
            }

            lines.Add(line.ToString().TrimEnd());
        }

        return string.Join(Environment.NewLine, lines);
    }

}
