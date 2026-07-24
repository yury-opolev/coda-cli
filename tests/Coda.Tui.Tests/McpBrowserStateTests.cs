using System.Collections.Immutable;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Mcp;

namespace Coda.Tui.Tests;

public sealed class McpBrowserStateTests
{
    [Fact]
    public void Selection_identity_is_scope_plus_name_and_rename_selects_the_new_key()
    {
        var user = Summary(McpConfigScope.User, "shared");
        var project = Summary(McpConfigScope.Project, "shared");
        var state = McpBrowserState.Empty
            .WithServers([user, project])
            .Select(project.Key);

        var renamed = project with
        {
            Key = new McpServerKey(McpConfigScope.Project, "renamed"),
        };

        state = state.WithServers([user, renamed], preferredKey: renamed.Key);

        Assert.Equal(renamed.Key, state.SelectedKey);
    }

    [Fact]
    public void Same_name_in_each_scope_remains_distinct()
    {
        var user = Summary(McpConfigScope.User, "shared");
        var project = Summary(McpConfigScope.Project, "shared");

        var state = McpBrowserState.Empty.WithServers([user, project]).Select(project.Key);

        Assert.Equal(project, state.Selected);
        Assert.NotEqual(user.Key, project.Key);
    }

    [Fact]
    public void Removing_selected_row_chooses_the_nearest_previous_index()
    {
        var first = Summary(McpConfigScope.User, "a");
        var second = Summary(McpConfigScope.User, "b");
        var third = Summary(McpConfigScope.User, "c");
        var state = McpBrowserState.Empty
            .WithServers([first, second, third])
            .Select(second.Key);

        state = state.WithServers([first, third]);

        Assert.Equal(first.Key, state.SelectedKey);
    }

    [Fact]
    public void Selection_defaults_to_first_row_and_survives_refresh()
    {
        var first = Summary(McpConfigScope.User, "a");
        var second = Summary(McpConfigScope.User, "b");
        var state = McpBrowserState.Empty.WithServers([first, second]);

        Assert.Equal(first.Key, state.SelectedKey);

        var refreshed = Summary(McpConfigScope.User, "b");
        state = state.WithServers([refreshed]);

        Assert.Equal(refreshed.Key, state.SelectedKey);
    }

    [Fact]
    public void Detail_is_cleared_when_refresh_changes_selected_server()
    {
        var first = Summary(McpConfigScope.User, "a");
        var second = Summary(McpConfigScope.User, "b");
        var detail = new McpServerDetail(
            first,
            "command",
            [],
            null,
            [],
            [],
            McpAuthMode.None,
            null,
            [],
            null,
            [],
            [],
            []);
        var state = McpBrowserState.Empty
            .WithServers([first, second])
            .OpenDetail(detail)
            .WithServers([second]);

        Assert.Equal(McpBrowserView.Detail, state.View);
        Assert.Null(state.Detail);
        Assert.Equal(second.Key, state.SelectedKey);
    }

    [Fact]
    public void Begin_add_defaults_to_project_scope_when_available_and_keeps_scope_editable()
    {
        var state = McpBrowserState.Empty.BeginAdd(new McpManagementSnapshot(true, []));

        Assert.Equal(McpBrowserView.Editor, state.View);
        Assert.NotNull(state.Editor);
        Assert.Equal(McpEditorMode.Add, state.Editor!.Mode);
        Assert.Equal(McpBrowserView.List, state.Editor.Origin);
        Assert.Equal(McpConfigScope.Project, state.Editor.Draft.Scope);
        Assert.Equal(McpEditorField.Scope, state.Editor.FocusedField);
    }

    [Fact]
    public void Begin_add_defaults_to_user_scope_when_project_scope_is_unavailable()
    {
        var state = McpBrowserState.Empty.BeginAdd(new McpManagementSnapshot(false, []));

        Assert.Equal(McpConfigScope.User, state.Editor!.Draft.Scope);
    }

    [Fact]
    public void Begin_edit_preserves_scope_and_origin_and_starts_on_name()
    {
        var draft = new McpServerDraft(
            "server",
            McpConfigScope.Project,
            true,
            McpTransportKind.Http,
            null,
            [],
            "https://example.test",
            [],
            [],
            McpAuthMode.OAuth,
            null,
            [],
            new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged));

        var state = McpBrowserState.Empty
            .OpenDetail(new McpServerDetail(
                Summary(McpConfigScope.Project, "server"),
                null,
                [],
                "https://example.test",
                [],
                [],
                McpAuthMode.OAuth,
                null,
                [],
                null,
                [],
                [],
                []))
            .BeginEdit(draft);

        Assert.Equal(McpEditorMode.Edit, state.Editor!.Mode);
        Assert.Equal(McpBrowserView.Detail, state.Editor.Origin);
        Assert.Equal(McpConfigScope.Project, state.Editor.Draft.Scope);
        Assert.Equal(McpEditorField.Name, state.Editor.FocusedField);
    }

    [Fact]
    public void Cancel_editor_returns_to_origin_without_mutating_servers()
    {
        var server = Summary(McpConfigScope.User, "server");
        var state = McpBrowserState.Empty
            .WithServers([server])
            .Select(server.Key)
            .BeginAdd(new McpManagementSnapshot(true, []));

        var cancelled = state.CancelEditor();

        Assert.Equal(McpBrowserView.List, cancelled.View);
        Assert.Null(cancelled.Editor);
        Assert.Single(cancelled.Servers);
        Assert.Equal(server, cancelled.Servers[0]);
        Assert.Equal("Cancelled.", cancelled.StatusMessage);
    }

    [Fact]
    public void Busy_flags_are_independent()
    {
        var state = McpBrowserState.Empty
            .WithTurnBusy(true)
            .WithActionBusy(false);

        state = state.WithActionBusy(true);
        Assert.True(state.TurnBusy);
        Assert.True(state.ActionBusy);

        state = state.WithTurnBusy(false);
        Assert.False(state.TurnBusy);
        Assert.True(state.ActionBusy);
    }

    [Fact]
    public void State_transitions_do_not_mutate_the_input_array()
    {
        var input = ImmutableArray.Create(Summary(McpConfigScope.User, "server"));
        var state = McpBrowserState.Empty.WithServers(input);

        Assert.Equal(input, state.Servers);
        _ = state.MoveSelection(1);
        Assert.Equal(input, state.Servers);
    }

    private static McpServerSummary Summary(McpConfigScope scope, string name) =>
        new(
            new McpServerKey(scope, name),
            scope == McpConfigScope.User ? @"C:\user\.mcp.json" : @"C:\project\.mcp.json",
            Enabled: true,
            IsEffective: true,
            McpTransportKind.Stdio,
            McpConnectionState.Disconnected,
            LastError: null);
}
