using System.Collections.Immutable;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui;
using Coda.Tui.Ui.Mcp;
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Tests;

public sealed class McpBrowserControllerTests
{
    [Theory]
    [InlineData("/mcp", true)]
    [InlineData(" /mcp ", true)]
    [InlineData("/MCP", false)]
    [InlineData("/mcp list", false)]
    [InlineData("/mcp x", false)]
    public void Open_request_is_exact_and_case_sensitive(string text, bool expected) =>
        Assert.Equal(expected, McpBrowserController.IsOpenRequest(text));

    [Fact]
    public async Task Open_binds_once_per_epoch_subscribes_once_and_refreshes()
    {
        var management = new TestManagementService();
        var gate = new TestIdleGate();
        var providerCalls = 0;
        var controller = new McpBrowserController(() =>
        {
            providerCalls++;
            return new McpBrowserProvider(management, new RecordingPromptService(), gate);
        });

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, providerCalls);
        Assert.Equal(1, management.ChangedSubscriberCount);
        Assert.Equal(4, management.RefreshCalls);
        controller.Close();
        Assert.Equal(0, management.ChangedSubscriberCount);
    }

    [Fact]
    public async Task Close_cancels_refresh_and_stale_completion_cannot_overwrite_new_epoch()
    {
        var firstRefresh = new TaskCompletionSource<McpManagementSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var management = new TestManagementService
        {
            Refresh = call => call == 1
                ? firstRefresh.Task
                : Task.FromResult(TestManagementService.Snapshot("fresh")),
        };
        var controller = new McpBrowserController(() =>
            new McpBrowserProvider(management, new RecordingPromptService(), new TestIdleGate()));

        controller.Open();
        var stale = controller.RefreshAsync(CancellationToken.None);
        await management.RefreshStarted.Task;
        controller.Close();
        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        firstRefresh.SetResult(TestManagementService.Snapshot("stale"));
        await stale;

        Assert.Equal("fresh", controller.State.SelectedKey?.Name);
        controller.Close();
    }

    [Fact]
    public async Task Busy_browser_allows_refresh_but_rejects_mutation_without_a_lease()
    {
        var management = new TestManagementService();
        var gate = new TestIdleGate { Busy = true };
        var controller = NewController(management, gate);

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.ToggleEnabled, null, CancellationToken.None);

        Assert.NotEmpty(controller.State.Servers);
        Assert.Contains("turn", controller.State.StatusMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, management.SetEnabledCalls);
        controller.Close();
    }

    [Fact]
    public async Task Detail_remains_available_while_turn_is_busy()
    {
        var management = new TestManagementService();
        var controller = NewController(management, new TestIdleGate { Busy = true });

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.OpenDetail, null, CancellationToken.None);

        Assert.Equal(McpBrowserView.Detail, controller.State.View);
        Assert.Equal(1, management.GetDetailCalls);
        controller.Close();
    }

    [Fact]
    public async Task Delete_confirms_with_default_false_before_committing()
    {
        var management = new TestManagementService();
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["no"], null));
        var controller = NewController(management, new TestIdleGate(), prompts);

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.DeleteServer, null, CancellationToken.None);

        Assert.Equal(1, management.PrepareDeleteCalls);
        Assert.Equal(0, management.CommitDeleteCalls);
        Assert.Equal(UiPromptKind.Confirm, Assert.Single(prompts.Requests).Kind);
        Assert.Equal("no", prompts.Requests[0].DefaultValue);
        Assert.Contains("Cancelled", controller.State.StatusMessage!, StringComparison.Ordinal);
        controller.Close();
    }

    [Fact]
    public async Task Reauthentication_confirms_before_replacing_oauth_state()
    {
        var management = new TestManagementService();
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["no"], null));
        var controller = NewController(management, new TestIdleGate(), prompts);

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.Reauthenticate, null, CancellationToken.None);

        Assert.Equal(1, management.PrepareReauthenticationCalls);
        Assert.Equal(0, management.ReauthenticateCalls);
        Assert.Equal("no", Assert.Single(prompts.Requests).DefaultValue);
        Assert.Contains("Cancelled", controller.State.StatusMessage!, StringComparison.Ordinal);
        controller.Close();
    }

    [Fact]
    public async Task Reauthentication_prompts_for_managed_replacement_as_required_secret()
    {
        var management = new TestManagementService
        {
            ReauthenticationPlan = TestManagementService.Plan(managedFields: ["headers/authorization"]),
        };
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ["yes"], null),
            new UiPromptResponse(false, [], "replacement-value"));
        var controller = NewController(management, new TestIdleGate(), prompts);

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.Reauthenticate, null, CancellationToken.None);

        Assert.Equal(1, management.ReauthenticateCalls);
        var replacementPrompt = prompts.Requests[1];
        Assert.Equal(UiPromptKind.Secret, replacementPrompt.Kind);
        Assert.True(replacementPrompt.Required);
        Assert.DoesNotContain("replacement-value", controller.State.ToString(), StringComparison.Ordinal);
        controller.Close();
    }

    [Fact]
    public async Task Mutation_holds_idle_lease_through_service_completion()
    {
        var gate = new TestIdleGate();
        var management = new TestManagementService
        {
            OnSetEnabled = () => Assert.True(gate.IsBusy),
        };
        var controller = NewController(management, gate);

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.ToggleEnabled, null, CancellationToken.None);

        Assert.Equal(1, management.SetEnabledCalls);
        Assert.False(gate.IsBusy);
        controller.Close();
    }

    [Fact]
    public async Task Queued_old_epoch_destructive_action_cannot_run_after_close_and_reopen()
    {
        var mutationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var management = new TestManagementService
        {
            Refresh = call => Task.FromResult(TestManagementService.Snapshot(
                call >= 3 ? "reopened-selection" : "server")),
            SetEnabledAsyncHandler = async (_, _) =>
            {
                mutationStarted.TrySetResult();
                await completeMutation.Task;
                return TestManagementService.Result(TestManagementService.Key("server"), "Saved.", "server");
            },
        };
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["yes"], null));
        var controller = NewController(management, new TestIdleGate(), prompts);

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        var blocked = controller.ExecuteAsync(McpBrowserCommand.ToggleEnabled, null, CancellationToken.None);
        await mutationStarted.Task;
        var queuedDelete = controller.ExecuteAsync(McpBrowserCommand.DeleteServer, null, CancellationToken.None);

        controller.Close();
        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        Assert.Equal("reopened-selection", controller.State.SelectedKey?.Name);
        completeMutation.TrySetResult();
        await Task.WhenAll(blocked, queuedDelete);

        Assert.Equal(0, management.PrepareDeleteCalls);
        Assert.Equal(0, management.CommitDeleteCalls);
        Assert.Empty(prompts.Requests);
        controller.Close();
    }

    [Fact]
    public async Task Successful_rename_selects_returned_key()
    {
        var oldKey = TestManagementService.Key("server");
        var newKey = TestManagementService.Key("renamed");
        var management = new TestManagementService
        {
            EditDraft = TestManagementService.Draft("server"),
            CommitEditResult = TestManagementService.Result(newKey, "Saved.", "renamed"),
        };
        var controller = NewController(management, new TestIdleGate());

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.BeginEdit, null, CancellationToken.None);
        controller.SetStateForTest(controller.State with
        {
            Editor = controller.State.Editor! with { FocusedField = McpEditorField.Save },
        });
        await controller.ExecuteAsync(McpBrowserCommand.EditorApply, null, CancellationToken.None);

        Assert.Equal(newKey, controller.State.SelectedKey);
        controller.Close();
    }

    [Fact]
    public async Task Enter_on_an_ordinary_editor_field_does_not_commit()
    {
        var management = new TestManagementService();
        var controller = NewController(management, new TestIdleGate());

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.BeginAdd, null, CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.EditorApply, null, CancellationToken.None);

        Assert.Equal(McpBrowserView.Editor, controller.State.View);
        Assert.Equal(0, management.CommitAddCalls);
        controller.Close();
    }

    [Fact]
    public async Task Editing_a_list_field_preserves_the_authoritative_item_identity()
    {
        var item = new McpDraftListItem(Guid.NewGuid(), "arg");
        var draft = TestManagementService.Draft("server") with
        {
            Args = ["arg"],
            ArgumentItems = [item],
        };
        var controller = NewController(new TestManagementService(), new TestIdleGate());
        controller.Open();
        controller.SetStateForTest(McpBrowserState.Empty with
        {
            View = McpBrowserView.Editor,
            Editor = new McpEditorState(
                McpEditorMode.Edit,
                McpBrowserView.List,
                draft,
                McpEditorField.Arguments),
        });

        await controller.ExecuteAsync(
            McpBrowserCommand.EditorInsert,
            new Key('x'),
            CancellationToken.None);

        var changed = controller.State.Editor!.Draft;
        Assert.Equal("argx", Assert.Single(changed.Args));
        Assert.Equal(item.Id, Assert.Single(changed.ArgumentItems).Id);
        Assert.Equal("argx", changed.ArgumentItems[0].Value);
        controller.Close();
    }

    [Fact]
    public async Task Empty_add_draft_can_add_edit_and_remove_collection_rows()
    {
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, [], "environment-secret"),
            new UiPromptResponse(false, [], "header-secret"));
        var controller = NewController(new TestManagementService(), new TestIdleGate(), prompts);

        controller.Open();
        await controller.ExecuteAsync(McpBrowserCommand.BeginAdd, null, CancellationToken.None);

        await SetEditorFieldAsync(controller, McpEditorField.Arguments);
        await controller.ExecuteAsync(McpBrowserCommand.EditorAddItem, null, CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.EditorInsert, new Key('a'), CancellationToken.None);
        Assert.Equal("a", Assert.Single(controller.State.Editor!.Draft.Args));
        await controller.ExecuteAsync(McpBrowserCommand.EditorRemoveItem, null, CancellationToken.None);
        Assert.Empty(controller.State.Editor!.Draft.Args);

        await SetEditorFieldAsync(controller, McpEditorField.Scopes);
        await controller.ExecuteAsync(McpBrowserCommand.EditorAddItem, null, CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.EditorInsert, new Key('s'), CancellationToken.None);
        Assert.Equal("s", Assert.Single(controller.State.Editor!.Draft.Scopes));
        await controller.ExecuteAsync(McpBrowserCommand.EditorRemoveItem, null, CancellationToken.None);
        Assert.Empty(controller.State.Editor!.Draft.Scopes);

        await SetEditorFieldAsync(controller, McpEditorField.Environment, McpEditorItemPart.Name);
        await controller.ExecuteAsync(McpBrowserCommand.EditorAddItem, null, CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.EditorInsert, new Key('E'), CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.EditorNextItemPart, null, CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.EditorApply, null, CancellationToken.None);
        var environment = Assert.Single(controller.State.Editor!.Draft.Environment);
        Assert.Equal("E", environment.Name);
        Assert.Equal(McpSecretChangeKind.Replace, environment.Change.Kind);
        await controller.ExecuteAsync(McpBrowserCommand.EditorRemoveItem, null, CancellationToken.None);
        Assert.Empty(controller.State.Editor!.Draft.Environment);

        await SetEditorFieldAsync(controller, McpEditorField.Headers, McpEditorItemPart.Name);
        await controller.ExecuteAsync(McpBrowserCommand.EditorAddItem, null, CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.EditorInsert, new Key('H'), CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.EditorNextItemPart, null, CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.EditorApply, null, CancellationToken.None);
        var header = Assert.Single(controller.State.Editor!.Draft.Headers);
        Assert.Equal("H", header.Name);
        Assert.Equal(McpSecretChangeKind.Replace, header.Change.Kind);
        await controller.ExecuteAsync(McpBrowserCommand.EditorRemoveItem, null, CancellationToken.None);
        Assert.Empty(controller.State.Editor!.Draft.Headers);
        Assert.DoesNotContain("environment-secret", controller.State.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", controller.State.ToString(), StringComparison.Ordinal);
        controller.Close();
    }

    [Fact]
    public async Task Existing_named_secrets_keep_identity_when_replaced_or_marked_removed()
    {
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, [], "replacement"),
            new UiPromptResponse(false, [], "replacement"));
        var draft = TestManagementService.Draft("server") with
        {
            Environment =
            [
                new McpNamedSecretDraft(
                    "TOKEN",
                    McpSecretSource.Managed,
                    new McpSecretChange("env/TOKEN", McpSecretChangeKind.Unchanged)),
            ],
            Headers =
            [
                new McpNamedSecretDraft(
                    "Authorization",
                    McpSecretSource.Environment,
                    new McpSecretChange("header/Authorization", McpSecretChangeKind.Unchanged)),
            ],
        };
        var controller = NewController(new TestManagementService(), new TestIdleGate(), prompts);
        controller.Open();
        controller.SetStateForTest(EditorState(draft, McpEditorField.Environment, McpEditorItemPart.Value));

        await controller.ExecuteAsync(McpBrowserCommand.EditorApply, null, CancellationToken.None);
        var environment = Assert.Single(controller.State.Editor!.Draft.Environment);
        Assert.Equal("TOKEN", environment.Name);
        Assert.Equal(McpSecretSource.Managed, environment.ExistingSource);
        Assert.Equal(McpSecretChangeKind.Replace, environment.Change.Kind);
        await controller.ExecuteAsync(McpBrowserCommand.EditorRemoveItem, null, CancellationToken.None);
        Assert.Equal(McpSecretChangeKind.Remove, Assert.Single(controller.State.Editor!.Draft.Environment).Change.Kind);

        controller.SetStateForTest(EditorState(controller.State.Editor!.Draft, McpEditorField.Headers, McpEditorItemPart.Value));
        await controller.ExecuteAsync(McpBrowserCommand.EditorApply, null, CancellationToken.None);
        var header = Assert.Single(controller.State.Editor!.Draft.Headers);
        Assert.Equal("Authorization", header.Name);
        Assert.Equal(McpSecretSource.Environment, header.ExistingSource);
        Assert.Equal(McpSecretChangeKind.Replace, header.Change.Kind);
        await controller.ExecuteAsync(McpBrowserCommand.EditorRemoveItem, null, CancellationToken.None);
        Assert.Equal(McpSecretChangeKind.Remove, Assert.Single(controller.State.Editor!.Draft.Headers).Change.Kind);

        controller.SetStateForTest(EditorState(controller.State.Editor!.Draft, McpEditorField.BearerToken));
        await controller.ExecuteAsync(McpBrowserCommand.EditorRemoveItem, null, CancellationToken.None);
        Assert.Equal(McpSecretChangeKind.Remove, controller.State.Editor!.Draft.BearerToken.Kind);
        controller.Close();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_save_keeps_editor_draft_for_retry(bool isEdit)
    {
        var rejected = new McpMutationResult(
            McpMutationStatus.Rejected,
            null,
            "Stale draft.\r\nRetry.",
            TestManagementService.Snapshot("other"));
        var management = new TestManagementService
        {
            CommitAddResult = rejected,
            CommitEditResult = rejected,
        };
        var controller = NewController(management, new TestIdleGate());
        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        if (!isEdit)
        {
            await controller.ExecuteAsync(McpBrowserCommand.BeginAdd, null, CancellationToken.None);
        }
        else
        {
            await controller.ExecuteAsync(McpBrowserCommand.BeginEdit, null, CancellationToken.None);
        }

        var draft = controller.State.Editor!.Draft with { Name = "retry-me" };
        controller.SetStateForTest(EditorState(
            draft,
            McpEditorField.Save,
            mode: isEdit ? McpEditorMode.Edit : McpEditorMode.Add,
            origin: controller.State.Editor!.Origin));
        await controller.ExecuteAsync(McpBrowserCommand.EditorApply, null, CancellationToken.None);

        Assert.Equal(McpBrowserView.Editor, controller.State.View);
        Assert.Equal(draft, controller.State.Editor!.Draft);
        Assert.Equal("Stale draft. Retry.", controller.State.StatusMessage);
        controller.Close();
    }

    [Fact]
    public async Task Saved_with_runtime_error_keeps_the_saved_result_status()
    {
        var key = TestManagementService.Key("server");
        var management = new TestManagementService
        {
            SetEnabled = _ => new McpMutationResult(
                McpMutationStatus.SavedWithRuntimeError,
                key,
                "Saved, but runtime reconnect failed.",
                TestManagementService.Snapshot("server")),
        };
        var controller = NewController(management, new TestIdleGate());

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.ToggleEnabled, null, CancellationToken.None);

        Assert.Equal("Saved, but runtime reconnect failed.", controller.State.StatusMessage);
        controller.Close();
    }

    [Fact]
    public async Task Service_exception_is_converted_to_sanitized_status_without_secret()
    {
        const string secret = "replacement-value";
        var management = new TestManagementService
        {
            SetEnabled = _ => throw new InvalidOperationException($"operation failed: {secret}\r\n"),
        };
        var controller = NewController(management, new TestIdleGate());

        controller.Open();
        await controller.RefreshAsync(CancellationToken.None);
        await controller.ExecuteAsync(McpBrowserCommand.ToggleEnabled, null, CancellationToken.None);

        Assert.DoesNotContain(secret, controller.State.StatusMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", controller.State.StatusMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", controller.State.StatusMessage!, StringComparison.Ordinal);
        controller.Close();
    }

    [Fact]
    public void Closing_editor_removes_replacement_secrets_from_state()
    {
        var state = McpBrowserState.Empty with
        {
            View = McpBrowserView.Editor,
            Editor = new McpEditorState(
                McpEditorMode.Edit,
                McpBrowserView.List,
                TestManagementService.Draft("server") with
                {
                    BearerToken = new McpSecretChange(
                        "auth/token",
                        McpSecretChangeKind.Replace,
                        new McpSecretReplacement("replacement-value")),
                },
                McpEditorField.BearerToken),
        };
        var controller = NewController(new TestManagementService(), new TestIdleGate());
        controller.SetStateForTest(state);

        controller.Close();

        Assert.Null(controller.State.Editor);
        Assert.DoesNotContain("replacement-value", controller.State.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_editor_removes_named_replacement_secrets_from_state()
    {
        var draft = TestManagementService.Draft("server") with
        {
            Environment =
            [
                new McpNamedSecretDraft(
                    "TOKEN",
                    McpSecretSource.Managed,
                    new McpSecretChange(
                        "env/TOKEN",
                        McpSecretChangeKind.Replace,
                        new McpSecretReplacement("replacement-value"))),
            ],
        };
        var controller = NewController(new TestManagementService(), new TestIdleGate());
        controller.Open();
        controller.SetStateForTest(EditorState(draft, McpEditorField.Environment));

        await controller.ExecuteAsync(McpBrowserCommand.EditorCancel, null, CancellationToken.None);

        Assert.Null(controller.State.Editor);
        Assert.DoesNotContain("replacement-value", controller.State.ToString(), StringComparison.Ordinal);
        controller.Close();
    }

    [Fact]
    public void Changed_is_raised_outside_controller_lock()
    {
        var controller = NewController(new TestManagementService(), new TestIdleGate());
        var calls = 0;
        controller.Changed += () =>
        {
            _ = controller.State;
            calls++;
        };

        controller.NotifyChangedForTest();

        Assert.Equal(1, calls);
    }

    private static McpBrowserController NewController(
        TestManagementService management,
        TestIdleGate gate,
        RecordingPromptService? prompts = null) =>
        new(() => new McpBrowserProvider(
            management,
            prompts ?? new RecordingPromptService(),
            gate));

    private static async Task SetEditorFieldAsync(
        McpBrowserController controller,
        McpEditorField field,
        McpEditorItemPart part = McpEditorItemPart.Value)
    {
        controller.SetStateForTest(EditorState(controller.State.Editor!.Draft, field, part));
        await Task.CompletedTask;
    }

    private static McpBrowserState EditorState(
        McpServerDraft draft,
        McpEditorField field,
        McpEditorItemPart part = McpEditorItemPart.Value,
        McpEditorMode mode = McpEditorMode.Add,
        McpBrowserView origin = McpBrowserView.List) =>
        McpBrowserState.Empty with
        {
            View = McpBrowserView.Editor,
            Editor = new McpEditorState(mode, origin, draft, field)
            {
                SelectedItem = 0,
                SelectedItemPart = part,
            },
        };

    private sealed class TestIdleGate : IExclusiveIdleGate
    {
        private bool leased;
        public bool Busy { get; set; }
        public bool IsBusy => this.Busy || this.leased;
        public event Action? Changed;

        public IDisposable? TryAcquire()
        {
            if (this.IsBusy)
            {
                return null;
            }

            this.leased = true;
            this.Changed?.Invoke();
            return new Lease(this);
        }

        private sealed class Lease(TestIdleGate owner) : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
                owner.leased = false;
                owner.Changed?.Invoke();
            }
        }
    }

    private sealed class TestManagementService : IMcpManagementService
    {
        private int refreshCalls;
        public event Action? Changed;
        public int ChangedSubscriberCount => this.Changed?.GetInvocationList().Length ?? 0;
        public int RefreshCalls => this.refreshCalls;
        public int GetDetailCalls { get; private set; }
        public int SetEnabledCalls { get; private set; }
        public int PrepareDeleteCalls { get; private set; }
        public int CommitDeleteCalls { get; private set; }
        public int PrepareReauthenticationCalls { get; private set; }
        public int ReauthenticateCalls { get; private set; }
        public int CommitAddCalls { get; private set; }
        public Func<int, Task<McpManagementSnapshot>>? Refresh { get; init; }
        public Func<bool, McpMutationResult>? SetEnabled { get; init; }
        public Action? OnSetEnabled { get; init; }
        public McpServerDraft? EditDraft { get; init; }
        public McpMutationResult? CommitEditResult { get; init; }
        public McpMutationResult? CommitAddResult { get; init; }
        public Func<bool, CancellationToken, Task<McpMutationResult>>? SetEnabledAsyncHandler { get; init; }
        public McpReauthenticationPlan ReauthenticationPlan { get; init; } = Plan([]);
        public TaskCompletionSource RefreshStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<McpManagementSnapshot> RefreshAsync(CancellationToken ct)
        {
            var call = Interlocked.Increment(ref this.refreshCalls);
            this.RefreshStarted.TrySetResult();
            return this.Refresh?.Invoke(call) ?? Task.FromResult(Snapshot("server"));
        }

        public Task<McpServerDetail?> GetDetailAsync(McpServerKey key, CancellationToken ct)
        {
            this.GetDetailCalls++;
            var summary = Snapshot(key.Name).Servers.Single();
            return Task.FromResult<McpServerDetail?>(new McpServerDetail(
                summary, null, [], "https://example.test/mcp", [], [],
                McpAuthMode.None, null, [], null, [], [], []));
        }

        public Task<McpServerDraft?> CreateEditDraftAsync(McpServerKey key, CancellationToken ct) =>
            Task.FromResult<McpServerDraft?>(this.EditDraft ?? Draft(key.Name));

        public Task<McpEditPreview> PrepareAddAsync(McpServerDraft draft, CancellationToken ct) =>
            Task.FromResult(new McpEditPreview(Guid.NewGuid(), null, draft, Revision(), []));

        public Task<McpEditPreview> PrepareEditAsync(McpServerKey original, McpServerDraft draft, CancellationToken ct) =>
            Task.FromResult(new McpEditPreview(Guid.NewGuid(), original, draft, Revision(), []));

        public Task<McpMutationResult> CommitAddAsync(McpEditPreview preview, CancellationToken ct)
        {
            this.CommitAddCalls++;
            return Task.FromResult(this.CommitAddResult ?? Result(Key(preview.Draft.Name), "Saved.", preview.Draft.Name));
        }

        public Task<McpMutationResult> CommitEditAsync(McpEditPreview preview, CancellationToken ct) =>
            Task.FromResult(this.CommitEditResult ?? Result(Key(preview.Draft.Name), "Saved.", preview.Draft.Name));

        public async Task<McpMutationResult> SetEnabledAsync(McpServerKey key, bool enabled, CancellationToken ct)
        {
            this.SetEnabledCalls++;
            this.OnSetEnabled?.Invoke();
            return this.SetEnabledAsyncHandler is { } handler
                ? await handler(enabled, ct)
                : this.SetEnabled?.Invoke(enabled) ?? Result(key, "Saved.", key.Name);
        }

        public Task<McpDeletePreview> PrepareDeleteAsync(McpServerKey key, CancellationToken ct)
        {
            this.PrepareDeleteCalls++;
            return Task.FromResult(new McpDeletePreview(Guid.NewGuid(), key, Revision(), "Delete?", false));
        }

        public Task<McpMutationResult> CommitDeleteAsync(McpDeletePreview confirmedPreview, CancellationToken ct)
        {
            this.CommitDeleteCalls++;
            return Task.FromResult(Result(null, "Deleted.", null));
        }

        public Task<McpReauthenticationPlan> PrepareReauthenticationAsync(McpServerKey key, CancellationToken ct)
        {
            this.PrepareReauthenticationCalls++;
            return Task.FromResult(this.ReauthenticationPlan);
        }

        public Task<McpMutationResult> ReauthenticateAsync(
            McpReauthenticationPlan plan,
            IReadOnlyDictionary<string, McpSecretReplacement> replacements,
            CancellationToken ct)
        {
            this.ReauthenticateCalls++;
            return Task.FromResult(Result(plan.Key, "Saved.", plan.Key.Name));
        }

        public Task<McpMutationResult> StartAsync(string name, CancellationToken ct) =>
            Task.FromResult(Result(Key(name), "Started.", name));

        public Task<McpMutationResult> StopAsync(string name, CancellationToken ct) =>
            Task.FromResult(Result(Key(name), "Stopped.", name));

        public Task<McpMutationResult> RestartAsync(string? name, CancellationToken ct) =>
            Task.FromResult(Result(name is null ? null : Key(name), "Restarted.", name));

        public static McpServerKey Key(string name) => new(McpConfigScope.Project, name);

        public static McpManagementSnapshot Snapshot(string name) =>
            new(true, [new McpServerSummary(
                Key(name), @"C:\project\.mcp.json", true, true,
                McpTransportKind.Http, McpConnectionState.Disconnected, null)]);

        public static McpMutationResult Result(McpServerKey? key, string message, string? snapshotName) =>
            new(McpMutationStatus.Succeeded, key, message,
                snapshotName is null ? new McpManagementSnapshot(true, []) : Snapshot(snapshotName));

        public static McpReauthenticationPlan Plan(ImmutableArray<string> managedFields) =>
            new(Guid.NewGuid(), Key("server"), Revision(), McpReauthenticationKind.StoredSecret,
                "Reauthenticate?", managedFields, null);

        public static McpServerDraft Draft(string name) => new(
            name, McpConfigScope.Project, true, McpTransportKind.Http, null, [],
            "https://example.test/mcp", [], [], McpAuthMode.None, null, [],
            new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged));

        private static McpConfigRevision Revision() => new("user", "project");
    }
}
