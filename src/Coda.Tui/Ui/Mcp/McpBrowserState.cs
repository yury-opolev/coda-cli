using System.Collections.Immutable;
using Coda.Mcp;
using Coda.Tui.Mcp;

namespace Coda.Tui.Ui.Mcp;

internal sealed record McpBrowserState
{
    public static McpBrowserState Empty { get; } = new();

    public McpBrowserView View { get; init; } = McpBrowserView.List;

    public ImmutableArray<McpServerSummary> Servers { get; init; } = [];

    public McpServerKey? SelectedKey { get; init; }

    public McpServerDetail? Detail { get; init; }

    public McpEditorState? Editor { get; init; }

    public bool TurnBusy { get; init; }

    public bool ActionBusy { get; init; }

    public string? StatusMessage { get; init; }

    public McpServerSummary? Selected =>
        this.SelectedKey is { } key
            ? this.Servers.FirstOrDefault(server => server.Key == key)
            : null;

    public McpBrowserState Select(McpServerKey key) =>
        this.Servers.Any(server => server.Key == key)
            ? this with { SelectedKey = key }
            : this;

    public McpBrowserState WithServers(
        ImmutableArray<McpServerSummary> servers,
        McpServerKey? preferredKey = null)
    {
        var oldIndex = this.IndexOf(this.SelectedKey);
        McpServerKey? selectedKey = preferredKey is { } preferred &&
            servers.Any(server => server.Key == preferred)
                ? preferred
                : this.SelectedKey is { } retained &&
                    servers.Any(server => server.Key == retained)
                    ? retained
                    : servers.Length == 0
                        ? null
                        : servers[Math.Clamp(
                            oldIndex <= 0 ? 0 : oldIndex - 1,
                            0,
                            servers.Length - 1)].Key;

        return this with
        {
            Servers = servers,
            SelectedKey = selectedKey,
            Detail = this.View == McpBrowserView.Detail &&
                selectedKey != this.SelectedKey
                    ? null
                    : this.Detail,
        };
    }

    public McpBrowserState CancelEditor() =>
        this.Editor is { } editor
            ? this with
            {
                View = editor.Origin,
                Editor = null,
                StatusMessage = "Cancelled.",
            }
            : this;

    public McpBrowserState MoveSelection(int delta)
    {
        if (this.Servers.Length == 0)
        {
            return this;
        }

        var current = Math.Max(0, this.IndexOf(this.SelectedKey));
        var next = Math.Clamp(current + delta, 0, this.Servers.Length - 1);
        return this with { SelectedKey = this.Servers[next].Key };
    }

    public McpBrowserState MoveToStart() =>
        this.Servers.Length == 0
            ? this
            : this with { SelectedKey = this.Servers[0].Key };

    public McpBrowserState MoveToEnd() =>
        this.Servers.Length == 0
            ? this
            : this with { SelectedKey = this.Servers[^1].Key };

    public McpBrowserState OpenDetail(McpServerDetail detail) =>
        this with
        {
            View = McpBrowserView.Detail,
            SelectedKey = detail.Summary.Key,
            Detail = detail,
            Editor = null,
        };

    public McpBrowserState ReturnToList() =>
        this with
        {
            View = McpBrowserView.List,
            Detail = null,
            Editor = null,
        };

    public McpBrowserState BeginAdd(McpManagementSnapshot snapshot)
    {
        var scope = snapshot.ProjectScopeAvailable
            ? McpConfigScope.Project
            : McpConfigScope.User;
        var draft = new McpServerDraft(
            Name: string.Empty,
            Scope: scope,
            Enabled: true,
            Transport: McpTransportKind.Stdio,
            Command: null,
            Args: [],
            Url: null,
            Environment: [],
            Headers: [],
            AuthMode: McpAuthMode.OAuth,
            ClientId: null,
            Scopes: [],
            BearerToken: new McpSecretChange(
                "auth/token",
                McpSecretChangeKind.Unchanged));

        return this with
        {
            View = McpBrowserView.Editor,
            Editor = new McpEditorState(
                McpEditorMode.Add,
                this.View,
                draft,
                McpEditorField.Scope),
        };
    }

    public McpBrowserState BeginEdit(McpServerDraft draft) =>
        this with
        {
            View = McpBrowserView.Editor,
            Editor = new McpEditorState(
                McpEditorMode.Edit,
                this.View,
                draft,
                McpEditorField.Name),
        };

    public McpBrowserState WithTurnBusy(bool busy) =>
        this with { TurnBusy = busy };

    public McpBrowserState WithActionBusy(bool busy) =>
        this with { ActionBusy = busy };

    public McpBrowserState WithStatus(string? message) =>
        this with { StatusMessage = message };

    private int IndexOf(McpServerKey? key)
    {
        if (key is null)
        {
            return -1;
        }

        for (var index = 0; index < this.Servers.Length; index++)
        {
            if (this.Servers[index].Key == key.Value)
            {
                return index;
            }
        }

        return -1;
    }
}
