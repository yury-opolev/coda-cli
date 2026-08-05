using System.Collections.Immutable;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Mcp;

/// <summary>Coordinates the headless state and management actions behind the interactive MCP browser.</summary>
internal sealed record McpBrowserProvider(
    IMcpManagementService Management,
    IUiPromptService Prompts,
    IExclusiveIdleGate IdleGate);

internal sealed class McpBrowserController
{
    private static readonly McpEditorField[] editorFields =
    [
        McpEditorField.Scope,
        McpEditorField.Name,
        McpEditorField.Transport,
        McpEditorField.Command,
        McpEditorField.Arguments,
        McpEditorField.Url,
        McpEditorField.Environment,
        McpEditorField.Headers,
        McpEditorField.AuthMode,
        McpEditorField.ClientId,
        McpEditorField.Scopes,
        McpEditorField.BearerToken,
        McpEditorField.Save,
        McpEditorField.Cancel,
    ];

    private readonly Func<McpBrowserProvider?> provider;
    private readonly object sync = new();
    private readonly SemaphoreSlim actions = new(1, 1);

    private McpBrowserProvider? bound;
    private CancellationTokenSource? workCts;
    private CancellationTokenSource? refreshCts;
    private long epoch;
    private long refreshGeneration;
    private bool open;
    private bool projectScopeAvailable;
    private McpBrowserState state = McpBrowserState.Empty;

    internal McpBrowserController(Func<McpBrowserProvider?> provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    internal event Action? Changed;

    internal int ChangedSubscriberCount => this.Changed?.GetInvocationList().Length ?? 0;

    internal McpBrowserState State
    {
        get { lock (this.sync) { return this.state; } }
    }

    internal static bool IsOpenRequest(string? text) =>
        string.Equals(text?.Trim(), "/mcp", StringComparison.Ordinal);

    internal void Open()
    {
        this.Close();
        var next = this.provider();
        var cts = new CancellationTokenSource();

        lock (this.sync)
        {
            this.bound = next;
            this.workCts = cts;
            this.open = true;
            this.epoch++;
            this.projectScopeAvailable = false;
            this.state = McpBrowserState.Empty.WithTurnBusy(next?.IdleGate.IsBusy ?? false);
            if (next is not null)
            {
                next.Management.Changed += this.OnManagementChanged;
                next.IdleGate.Changed += this.OnIdleGateChanged;
            }
        }

        this.RaiseChanged();
        _ = this.RefreshAsync(CancellationToken.None);
    }

    internal void Close()
    {
        McpBrowserProvider? previous;
        CancellationTokenSource? previousWork;
        CancellationTokenSource? previousRefresh;
        var notify = false;

        lock (this.sync)
        {
            if (!this.open &&
                this.bound is null &&
                this.workCts is null &&
                this.refreshCts is null &&
                this.state == McpBrowserState.Empty)
            {
                return;
            }

            previous = this.bound;
            previousWork = this.workCts;
            previousRefresh = this.refreshCts;
            notify = this.open || this.state != McpBrowserState.Empty;

            this.bound = null;
            this.workCts = null;
            this.refreshCts = null;
            this.open = false;
            this.projectScopeAvailable = false;
            this.epoch++;
            this.refreshGeneration++;
            this.state = McpBrowserState.Empty;
        }

        if (previous is not null)
        {
            previous.Management.Changed -= this.OnManagementChanged;
            previous.IdleGate.Changed -= this.OnIdleGateChanged;
        }

        previousRefresh?.Cancel();
        previousRefresh?.Dispose();
        previousWork?.Cancel();
        previousWork?.Dispose();

        if (notify)
        {
            this.RaiseChanged();
        }
    }

    internal async Task RefreshAsync(CancellationToken ct)
    {
        McpBrowserProvider? current;
        CancellationTokenSource? superseded;
        CancellationTokenSource linked;
        long refresh;
        long openEpoch;

        lock (this.sync)
        {
            current = this.bound;
            openEpoch = this.epoch;
            superseded = this.refreshCts;
            linked = this.workCts is null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : CancellationTokenSource.CreateLinkedTokenSource(ct, this.workCts.Token);
            this.refreshCts = linked;
            refresh = ++this.refreshGeneration;
        }

        superseded?.Cancel();
        superseded?.Dispose();

        if (current is null)
        {
            lock (this.sync)
            {
                if (this.refreshGeneration == refresh && ReferenceEquals(this.refreshCts, linked))
                {
                    this.refreshCts = null;
                }
            }

            linked.Dispose();
            return;
        }

        try
        {
            var snapshot = await current.Management.RefreshAsync(linked.Token).ConfigureAwait(false);
            this.ApplyRefresh(current, openEpoch, refresh, snapshot);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Close and a newer refresh intentionally supersede this read.
        }
        catch
        {
            this.ApplyStatus(current, openEpoch, "Unable to refresh MCP servers.");
        }
        finally
        {
            lock (this.sync)
            {
                if (this.refreshGeneration == refresh && ReferenceEquals(this.refreshCts, linked))
                {
                    this.refreshCts = null;
                }
            }

            linked.Dispose();
        }
    }

    internal async Task ExecuteAsync(McpBrowserCommand command, Key? key, CancellationToken ct)
    {
        if (command == McpBrowserCommand.Close)
        {
            this.Close();
            return;
        }

        McpBrowserProvider? current = null;
        CancellationTokenSource? actionCts = null;
        long openEpoch = 0;
        var acquired = false;
        try
        {
            lock (this.sync)
            {
                current = this.bound;
                if (current is null || !this.open || this.workCts is null)
                {
                    return;
                }

                openEpoch = this.epoch;
                actionCts = CancellationTokenSource.CreateLinkedTokenSource(ct, this.workCts.Token);
            }

            await this.actions.WaitAsync(actionCts.Token).ConfigureAwait(false);
            acquired = true;

            lock (this.sync)
            {
                if (!this.IsCurrent(current, openEpoch))
                {
                    return;
                }

                this.state = this.state.WithActionBusy(true);
            }

            this.RaiseChanged();
            await this.ExecuteCoreAsync(current, openEpoch, command, key, actionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (actionCts?.IsCancellationRequested == true)
        {
            // The close/new-open generation won before this action completed.
        }
        catch
        {
            if (current is not null)
            {
                this.ApplyStatus(current, openEpoch, "Unable to complete the MCP operation.");
            }
        }
        finally
        {
            actionCts?.Dispose();
            if (acquired && current is not null)
            {
                var changed = false;
                lock (this.sync)
                {
                    if (this.IsCurrent(current, openEpoch) && this.state.ActionBusy)
                    {
                        this.state = this.state.WithActionBusy(false);
                        changed = true;
                    }
                }

                if (changed)
                {
                    this.RaiseChanged();
                }
            }

            if (acquired)
            {
                this.actions.Release();
            }
        }
    }

    internal void SetStateForTest(McpBrowserState state)
    {
        lock (this.sync)
        {
            this.state = state;
        }
    }

    internal void NotifyChangedForTest() => this.RaiseChanged();

    /// <summary>
    /// Applies a draft mutation originating from a widget value-change event.
    /// Does not require epoch guards because it comes from the UI thread, not an async action.
    /// </summary>
    internal void UpdateEditorDraft(Func<McpServerDraft, McpServerDraft> update)
    {
        lock (this.sync)
        {
            if (this.state.Editor is not { } editor) return;
            this.state = this.state with { Editor = editor with { Draft = update(editor.Draft) } };
        }

        this.RaiseChanged();
    }

    /// <summary>
    /// Updates the focused-field cursor in the editor state when a widget gains focus.
    /// Keeps <see cref="McpEditorState.FocusedField"/> in sync so that
    /// <c>ApplyEditorAsync</c> acts on the correct field when Enter is pressed.
    /// </summary>
    internal void UpdateEditorFocus(McpEditorField field)
    {
        lock (this.sync)
        {
            if (this.state.Editor is not { } editor) return;
            if (editor.FocusedField == field) return;
            this.state = this.state with { Editor = editor with { FocusedField = field } };
        }

        this.RaiseChanged();
    }

    /// <summary>
    /// Updates the focused field AND the selected item cursor when a per-item widget (an argument,
    /// scope, environment or header row) gains focus. Keeps
    /// <see cref="McpEditorState.SelectedItem"/> and <see cref="McpEditorState.SelectedItemPart"/>
    /// aligned with the widget the user is actually editing so that add/remove/reorder and the
    /// Enter-driven secret prompt all act on that row.
    /// </summary>
    internal void UpdateEditorFocusItem(McpEditorField field, int itemIndex, McpEditorItemPart part)
    {
        lock (this.sync)
        {
            if (this.state.Editor is not { } editor) return;
            if (editor.FocusedField == field
                && editor.SelectedItem == itemIndex
                && editor.SelectedItemPart == part)
            {
                return;
            }

            this.state = this.state with
            {
                Editor = editor with
                {
                    FocusedField = field,
                    SelectedItem = itemIndex,
                    SelectedItemPart = part,
                },
            };
        }

        this.RaiseChanged();
    }

    private async Task ExecuteCoreAsync(
        McpBrowserProvider current,
        long openEpoch,
        McpBrowserCommand command,
        Key? key,
        CancellationToken ct)
    {
        switch (command)
        {
            case McpBrowserCommand.None:
                return;
            case McpBrowserCommand.MoveUp:
                this.Mutate(current, openEpoch, state => state.MoveSelection(-1));
                return;
            case McpBrowserCommand.MoveDown:
                this.Mutate(current, openEpoch, state => state.MoveSelection(1));
                return;
            case McpBrowserCommand.PageUp:
                this.Mutate(current, openEpoch, state => state.MoveSelection(-10));
                return;
            case McpBrowserCommand.PageDown:
                this.Mutate(current, openEpoch, state => state.MoveSelection(10));
                return;
            case McpBrowserCommand.MoveToStart:
                this.Mutate(current, openEpoch, state => state.MoveToStart());
                return;
            case McpBrowserCommand.MoveToEnd:
                this.Mutate(current, openEpoch, state => state.MoveToEnd());
                return;
            case McpBrowserCommand.ReturnToList:
                this.Mutate(current, openEpoch, state => state.ReturnToList());
                return;
            case McpBrowserCommand.OpenDetail:
                await this.OpenDetailAsync(current, openEpoch, ct).ConfigureAwait(false);
                return;
            case McpBrowserCommand.BeginAdd:
                this.BeginAdd(current, openEpoch);
                return;
            case McpBrowserCommand.BeginEdit:
                await this.BeginEditAsync(current, openEpoch, ct).ConfigureAwait(false);
                return;
            case McpBrowserCommand.ToggleEnabled:
                await this.MutateWithLeaseAsync(current, openEpoch, async token =>
                {
                    var selected = this.State.Selected;
                    if (selected is null)
                    {
                        this.ApplyStatus(current, openEpoch, "Select an MCP server first.");
                        return;
                    }

                    var result = await current.Management.SetEnabledAsync(
                        selected.Key, !selected.Enabled, token).ConfigureAwait(false);
                    this.ApplyMutation(current, openEpoch, result);
                }, ct).ConfigureAwait(false);
                return;
            case McpBrowserCommand.DeleteServer:
                await this.DeleteAsync(current, openEpoch, ct).ConfigureAwait(false);
                return;
            case McpBrowserCommand.Reauthenticate:
                await this.ReauthenticateAsync(current, openEpoch, ct).ConfigureAwait(false);
                return;
            case McpBrowserCommand.EditorCancel:
                this.Mutate(current, openEpoch, state => state.CancelEditor());
                return;
            case McpBrowserCommand.EditorApply:
                await this.ApplyEditorAsync(current, openEpoch, ct).ConfigureAwait(false);
                return;
            case McpBrowserCommand.EditorAddItem:
                this.AddEditorItem(current, openEpoch);
                return;
            case McpBrowserCommand.EditorRemoveItem:
                this.RemoveEditorItem(current, openEpoch);
                return;
            case McpBrowserCommand.EditorReorderUp:
                this.ReorderEditorItem(current, openEpoch, -1);
                return;
            case McpBrowserCommand.EditorReorderDown:
                this.ReorderEditorItem(current, openEpoch, 1);
                return;
            case McpBrowserCommand.Reload:
                // Re-emit the current state so the overlay re-renders with fresh data.
                this.Mutate(current, openEpoch, state => state);
                return;
            case McpBrowserCommand.Filter:
                // Filter mode is managed by the overlay itself; the controller is never invoked for it.
                return;
        }
    }

    private async Task OpenDetailAsync(McpBrowserProvider current, long openEpoch, CancellationToken ct)
    {
        var selected = this.State.Selected;
        if (selected is null)
        {
            this.ApplyStatus(current, openEpoch, "Select an MCP server first.");
            return;
        }

        var detail = await current.Management.GetDetailAsync(selected.Key, ct).ConfigureAwait(false);
        if (detail is null)
        {
            this.ApplyStatus(current, openEpoch, "The selected MCP server is no longer available.");
            return;
        }

        this.Mutate(current, openEpoch, state => state.OpenDetail(detail));
    }

    private void BeginAdd(McpBrowserProvider current, long openEpoch)
    {
        McpManagementSnapshot snapshot;
        lock (this.sync)
        {
            snapshot = new McpManagementSnapshot(
                this.projectScopeAvailable,
                this.state.Servers);
        }

        this.Mutate(current, openEpoch, state => state.BeginAdd(snapshot));
    }

    private async Task BeginEditAsync(McpBrowserProvider current, long openEpoch, CancellationToken ct)
    {
        var selected = this.State.Selected;
        if (selected is null)
        {
            this.ApplyStatus(current, openEpoch, "Select an MCP server first.");
            return;
        }

        var draft = await current.Management.CreateEditDraftAsync(selected.Key, ct).ConfigureAwait(false);
        if (draft is null)
        {
            this.ApplyStatus(current, openEpoch, "The selected MCP server is no longer available.");
            return;
        }

        this.Mutate(current, openEpoch, state => state.BeginEdit(draft));
    }

    private async Task DeleteAsync(McpBrowserProvider current, long openEpoch, CancellationToken ct) =>
        await this.MutateWithLeaseAsync(current, openEpoch, async token =>
        {
            var selected = this.State.Selected;
            if (selected is null)
            {
                this.ApplyStatus(current, openEpoch, "Select an MCP server first.");
                return;
            }

            var preview = await current.Management.PrepareDeleteAsync(selected.Key, token).ConfigureAwait(false);
            var confirmation = await current.Prompts.RequestAsync(
                UiPromptRequest.Confirm(SafeText(preview.Confirmation), defaultValue: false), token).ConfigureAwait(false);
            if (confirmation.Cancelled || !confirmation.SelectedIds.Contains("yes", StringComparer.Ordinal))
            {
                this.ApplyStatus(current, openEpoch, "Cancelled.");
                return;
            }

            this.ApplyMutation(
                current,
                openEpoch,
                await current.Management.CommitDeleteAsync(preview, token).ConfigureAwait(false));
        }, ct).ConfigureAwait(false);

    private async Task ReauthenticateAsync(McpBrowserProvider current, long openEpoch, CancellationToken ct) =>
        await this.MutateWithLeaseAsync(current, openEpoch, async token =>
        {
            var selected = this.State.Selected;
            if (selected is null)
            {
                this.ApplyStatus(current, openEpoch, "Select an MCP server first.");
                return;
            }

            var plan = await current.Management.PrepareReauthenticationAsync(selected.Key, token).ConfigureAwait(false);
            var confirmation = await current.Prompts.RequestAsync(
                UiPromptRequest.Confirm(SafeText(plan.Confirmation), defaultValue: false), token).ConfigureAwait(false);
            if (confirmation.Cancelled || !confirmation.SelectedIds.Contains("yes", StringComparer.Ordinal))
            {
                this.ApplyStatus(current, openEpoch, "Cancelled.");
                return;
            }

            var replacements = new Dictionary<string, McpSecretReplacement>(StringComparer.Ordinal);
            try
            {
                foreach (var field in plan.ManagedFields)
                {
                    var response = await current.Prompts.RequestAsync(
                        UiPromptRequest.Text($"Replace {SafeText(field)}", required: true, secret: true),
                        token).ConfigureAwait(false);
                    if (response.Cancelled || string.IsNullOrEmpty(response.Text))
                    {
                        this.ApplyStatus(current, openEpoch, "Cancelled.");
                        return;
                    }

                    replacements[field] = new McpSecretReplacement(response.Text);
                }

                this.ApplyMutation(
                    current,
                    openEpoch,
                    await current.Management.ReauthenticateAsync(plan, replacements, token).ConfigureAwait(false));
            }
            finally
            {
                replacements.Clear();
            }
        }, ct).ConfigureAwait(false);

    private async Task ApplyEditorAsync(McpBrowserProvider current, long openEpoch, CancellationToken ct)
    {
        var editor = this.State.Editor;
        if (editor is null)
        {
            return;
        }

        switch (editor.FocusedField)
        {
            case McpEditorField.Cancel:
                this.Mutate(current, openEpoch, state => state.CancelEditor());
                return;
            case McpEditorField.Save:
                await this.SaveEditorAsync(current, openEpoch, editor, ct).ConfigureAwait(false);
                return;
            case McpEditorField.Scope:
                this.ChangeScope(current, openEpoch);
                return;
            case McpEditorField.Transport:
                this.ChangeTransport(current, openEpoch);
                return;
            case McpEditorField.AuthMode:
                this.ChangeAuthMode(current, openEpoch);
                return;
            case McpEditorField.BearerToken:
                await this.PromptBearerReplacementAsync(current, openEpoch, ct).ConfigureAwait(false);
                return;
            case McpEditorField.Environment:
                if (editor.SelectedItemPart == McpEditorItemPart.Value)
                {
                    await this.PromptNamedReplacementAsync(current, openEpoch, "env", ct).ConfigureAwait(false);
                }
                else
                {
                    this.MoveEditorItemPart(current, openEpoch, 1);
                }

                return;
            case McpEditorField.Headers:
                if (editor.SelectedItemPart == McpEditorItemPart.Value)
                {
                    await this.PromptNamedReplacementAsync(current, openEpoch, "header", ct).ConfigureAwait(false);
                }
                else
                {
                    this.MoveEditorItemPart(current, openEpoch, 1);
                }

                return;
            default:
                return;
        }
    }

    /// <summary>
    /// Saves the editor draft in three phases so the idle-gate lease is NEVER held while the user
    /// is answering a warning confirmation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 1 acquires the lease, prepares the mutation (which validates the draft and computes any
    /// warnings against the current on-disk revision), then RELEASES the lease. Holding the lease
    /// across an interactive prompt would block a queued turn — or any other MCP action — for as long
    /// as the confirmation stays open.
    /// </para>
    /// <para>
    /// Phase 2, if the preview carries warnings, asks the user to confirm with no lease held.
    /// </para>
    /// <para>
    /// Phase 3 re-acquires the lease and commits. The commit re-validates: the service checks the
    /// preview's captured revision under its own mutation gate and returns a <c>Rejected</c> result
    /// if the configuration changed between prepare and commit, so a concurrent edit fails cleanly
    /// instead of clobbering the newer file.
    /// </para>
    /// </remarks>
    private async Task SaveEditorAsync(
        McpBrowserProvider current,
        long openEpoch,
        McpEditorState editor,
        CancellationToken ct)
    {
        var preview = await this.PrepareSaveAsync(current, openEpoch, editor, ct).ConfigureAwait(false);
        if (preview is null)
        {
            return;
        }

        if (!preview.Warnings.IsDefaultOrEmpty && !await this.ConfirmWarningsAsync(current, openEpoch, preview, ct).ConfigureAwait(false))
        {
            return;
        }

        var lease = this.TryAcquireLease(current, openEpoch);
        if (lease is null)
        {
            return;
        }

        try
        {
            var result = editor.Mode == McpEditorMode.Add
                ? await current.Management.CommitAddAsync(preview, ct).ConfigureAwait(false)
                : await current.Management.CommitEditAsync(preview, ct).ConfigureAwait(false);
            this.ApplyMutation(current, openEpoch, result);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Phase 1 of the save flow: prepares the mutation under a lease and releases the lease before
    /// returning. Returns <c>null</c> when the lease is unavailable (status already applied).
    /// </summary>
    private async Task<McpEditPreview?> PrepareSaveAsync(
        McpBrowserProvider current,
        long openEpoch,
        McpEditorState editor,
        CancellationToken ct)
    {
        var lease = this.TryAcquireLease(current, openEpoch);
        if (lease is null)
        {
            return null;
        }

        try
        {
            return editor.Mode == McpEditorMode.Add
                ? await current.Management.PrepareAddAsync(editor.Draft, ct).ConfigureAwait(false)
                : await current.Management.PrepareEditAsync(
                    this.State.SelectedKey ?? new McpServerKey(editor.Draft.Scope, editor.Draft.Name),
                    editor.Draft,
                    ct).ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Phase 2 of the save flow: confirms any warnings with NO lease held. Returns <c>true</c> when
    /// the user accepts and the commit should proceed.
    /// </summary>
    private async Task<bool> ConfirmWarningsAsync(
        McpBrowserProvider current,
        long openEpoch,
        McpEditPreview preview,
        CancellationToken ct)
    {
        var message = string.Join(
            " ",
            preview.Warnings.Select(SafeText).Append("Continue?"));
        var confirmation = await current.Prompts.RequestAsync(
            UiPromptRequest.Confirm(SafeText(message), defaultValue: false), ct).ConfigureAwait(false);
        if (confirmation.Cancelled || !confirmation.SelectedIds.Contains("yes", StringComparer.Ordinal))
        {
            this.ApplyStatus(current, openEpoch, "Cancelled.");
            return false;
        }

        return true;
    }

    private async Task PromptBearerReplacementAsync(McpBrowserProvider current, long openEpoch, CancellationToken ct)
    {
        var response = await current.Prompts.RequestAsync(
            UiPromptRequest.Text("Replace auth/token", required: true, secret: true), ct).ConfigureAwait(false);
        if (response.Cancelled || string.IsNullOrEmpty(response.Text))
        {
            this.ApplyStatus(current, openEpoch, "Cancelled.");
            return;
        }

        var replacement = new McpSecretReplacement(response.Text);
        this.Mutate(current, openEpoch, state => state.Editor is { } editor
            ? state with
            {
                Editor = editor with
                {
                    Draft = editor.Draft with
                    {
                        BearerToken = new McpSecretChange(
                            editor.Draft.BearerToken.Field,
                            McpSecretChangeKind.Replace,
                            replacement),
                    },
                    FocusedField = NextField(editor.FocusedField, 1),
                },
            }
            : state);
    }

    private void AddEditorItem(McpBrowserProvider current, long openEpoch) =>
        this.Mutate(current, openEpoch, state =>
        {
            if (state.Editor is not { } editor)
            {
                return state;
            }

            var draft = editor.Draft;
            var itemCount = 0;
            draft = editor.FocusedField switch
            {
                McpEditorField.Arguments => draft with
                {
                    Args = draft.Args.Add(string.Empty),
                    ArgumentItems = AppendItem(draft.ArgumentItems, draft.Args),
                },
                McpEditorField.Scopes => draft with
                {
                    Scopes = draft.Scopes.Add(string.Empty),
                    ScopeItems = AppendItem(draft.ScopeItems, draft.Scopes),
                },
                McpEditorField.Environment => draft with
                {
                    Environment = draft.Environment.Add(NewNamedSecret("env")),
                },
                McpEditorField.Headers => draft with
                {
                    Headers = draft.Headers.Add(NewNamedSecret("header")),
                },
                _ => draft,
            };

            itemCount = editor.FocusedField switch
            {
                McpEditorField.Arguments => draft.Args.Length,
                McpEditorField.Scopes => draft.Scopes.Length,
                McpEditorField.Environment => draft.Environment.Length,
                McpEditorField.Headers => draft.Headers.Length,
                _ => 0,
            };
            return itemCount == 0
                ? state
                : state with
                {
                    Editor = editor with
                    {
                        Draft = draft,
                        SelectedItem = itemCount - 1,
                        SelectedItemPart = editor.FocusedField is McpEditorField.Environment or McpEditorField.Headers
                            ? McpEditorItemPart.Name
                            : McpEditorItemPart.Value,
                    },
                };
        });

    private void RemoveEditorItem(McpBrowserProvider current, long openEpoch) =>
        this.Mutate(current, openEpoch, state =>
        {
            if (state.Editor is not { } editor)
            {
                return state;
            }

            var index = editor.SelectedItem;
            var draft = editor.Draft;
            var itemCount = 0;
            switch (editor.FocusedField)
            {
                case McpEditorField.Arguments when index >= 0 && index < draft.Args.Length:
                    draft = draft with
                    {
                        Args = draft.Args.RemoveAt(index),
                        ArgumentItems = RemoveItem(draft.ArgumentItems, index),
                    };
                    itemCount = draft.Args.Length;
                    break;
                case McpEditorField.Scopes when index >= 0 && index < draft.Scopes.Length:
                    draft = draft with
                    {
                        Scopes = draft.Scopes.RemoveAt(index),
                        ScopeItems = RemoveItem(draft.ScopeItems, index),
                    };
                    itemCount = draft.Scopes.Length;
                    break;
                case McpEditorField.Environment when index >= 0 && index < draft.Environment.Length:
                    draft = draft with { Environment = RemoveNamedSecret(draft.Environment, index) };
                    itemCount = draft.Environment.Length;
                    break;
                case McpEditorField.Headers when index >= 0 && index < draft.Headers.Length:
                    draft = draft with { Headers = RemoveNamedSecret(draft.Headers, index) };
                    itemCount = draft.Headers.Length;
                    break;
                case McpEditorField.BearerToken:
                    draft = draft with
                    {
                        BearerToken = new McpSecretChange(
                            draft.BearerToken.Field,
                            McpSecretChangeKind.Remove),
                    };
                    break;
                default:
                    return state;
            }

            return state with
            {
                Editor = editor with
                {
                    Draft = draft,
                    SelectedItem = itemCount == 0 ? 0 : Math.Min(index, itemCount - 1),
                },
            };
        });

    private void MoveEditorItemPart(McpBrowserProvider current, long openEpoch, int direction) =>
        this.Mutate(current, openEpoch, state => state.Editor is { } editor &&
                editor.FocusedField is McpEditorField.Environment or McpEditorField.Headers
            ? state with
            {
                Editor = editor with
                {
                    SelectedItemPart = direction < 0
                        ? McpEditorItemPart.Name
                        : McpEditorItemPart.Value,
                },
            }
            : state);

    /// <summary>
    /// Reorders the focused list item by one position in <paramref name="direction"/>.
    /// </summary>
    /// <remarks>
    /// The parallel <c>Args</c>/<c>ArgumentItems</c> and <c>Scopes</c>/<c>ScopeItems</c> arrays are
    /// swapped in lock-step so that each <see cref="McpDraftListItem"/> (and its stable
    /// <see cref="McpDraftListItem.Id"/>) travels WITH its display value. This is a hard correctness
    /// requirement: the commit path in <c>McpManagementService.MergeIdentifiedDraftListValues</c>
    /// recovers the true (possibly redacted) raw value of each item by its Guid. Swapping only the
    /// display strings while leaving the identity items in place would silently write a redaction
    /// sentinel (e.g. <c>[redacted URL]</c>) back into the config in place of the real secret.
    /// </remarks>
    private void ReorderEditorItem(McpBrowserProvider current, long openEpoch, int direction) =>
        this.Mutate(current, openEpoch, state =>
        {
            if (state.Editor is not { } editor)
            {
                return state;
            }

            var index = editor.SelectedItem;
            var target = index + direction;
            var draft = editor.Draft;
            switch (editor.FocusedField)
            {
                case McpEditorField.Arguments
                    when InRange(index, draft.Args.Length) && InRange(target, draft.Args.Length):
                    draft = draft with
                    {
                        Args = Swap(draft.Args, index, target),
                        ArgumentItems = SwapItems(draft.ArgumentItems, draft.Args.Length, index, target),
                    };
                    break;
                case McpEditorField.Scopes
                    when InRange(index, draft.Scopes.Length) && InRange(target, draft.Scopes.Length):
                    draft = draft with
                    {
                        Scopes = Swap(draft.Scopes, index, target),
                        ScopeItems = SwapItems(draft.ScopeItems, draft.Scopes.Length, index, target),
                    };
                    break;
                case McpEditorField.Environment
                    when InRange(index, draft.Environment.Length) && InRange(target, draft.Environment.Length):
                    draft = draft with { Environment = Swap(draft.Environment, index, target) };
                    break;
                case McpEditorField.Headers
                    when InRange(index, draft.Headers.Length) && InRange(target, draft.Headers.Length):
                    draft = draft with { Headers = Swap(draft.Headers, index, target) };
                    break;
                default:
                    return state;
            }

            return state with { Editor = editor with { Draft = draft, SelectedItem = target } };
        });

    private static bool InRange(int index, int length) => index >= 0 && index < length;

    private static ImmutableArray<T> Swap<T>(ImmutableArray<T> values, int first, int second)
    {
        var builder = values.ToBuilder();
        (builder[first], builder[second]) = (builder[second], builder[first]);
        return builder.ToImmutable();
    }

    /// <summary>
    /// Swaps two identity items in lock-step with their display array. Throws
    /// <see cref="InvalidOperationException"/> when the identity array is materialized but has a
    /// different length from the display array — that mismatch is a real corruption bug and must
    /// not be silently swallowed. When the identity array is default/empty, the display swap
    /// proceeds without identity tracking and the commit path falls back to display-based matching.
    /// </summary>
    private static ImmutableArray<McpDraftListItem> SwapItems(
        ImmutableArray<McpDraftListItem> items,
        int expectedLength,
        int first,
        int second)
    {
        if (items.IsDefault || items.Length == 0)
        {
            return items;
        }

        if (items.Length != expectedLength)
        {
            throw new InvalidOperationException(
                $"Identity array length {items.Length} does not match display array length {expectedLength}; " +
                "the draft is corrupted and the swap was aborted.");
        }

        return Swap(items, first, second);
    }

    private async Task MutateWithLeaseAsync(
        McpBrowserProvider current,
        long openEpoch,
        Func<CancellationToken, Task> mutation,
        CancellationToken ct)
    {
        var lease = this.TryAcquireLease(current, openEpoch);
        if (lease is null)
        {
            return;
        }

        try
        {
            await mutation(ct).ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Tries to acquire the exclusive idle-gate lease that guards MCP mutations. Returns the lease
    /// on success, or <c>null</c> after applying the standard "unavailable while a turn is running"
    /// status. Callers own disposing the returned lease. Split out from
    /// <see cref="MutateWithLeaseAsync"/> so the save flow can acquire and release the lease across
    /// several phases (prepare, confirm, commit) rather than holding it for the whole operation.
    /// </summary>
    private IDisposable? TryAcquireLease(McpBrowserProvider current, long openEpoch)
    {
        IDisposable? lease;
        try
        {
            lease = current.IdleGate.TryAcquire();
        }
        catch
        {
            this.ApplyStatus(current, openEpoch, "MCP changes are unavailable while a turn is running.");
            return null;
        }

        if (lease is null)
        {
            this.ApplyStatus(current, openEpoch, "MCP changes are unavailable while a turn is running.");
        }

        return lease;
    }

    private void ChangeScope(McpBrowserProvider current, long openEpoch) =>
        this.Mutate(current, openEpoch, state => state.Editor is { } editor
            ? editor.Mode == McpEditorMode.Edit
                ? state
                : state with
                {
                    Editor = editor with
                    {
                        Draft = editor.Draft with
                        {
                            Scope = editor.Draft.Scope == McpConfigScope.Project
                                ? McpConfigScope.User
                                : McpConfigScope.Project,
                        },
                    },
                }
            : state);

    private void ChangeTransport(McpBrowserProvider current, long openEpoch) =>
        this.Mutate(current, openEpoch, state => state.Editor is { } editor
            ? state with
            {
                Editor = editor with
                {
                    Draft = editor.Draft with
                    {
                        Transport = editor.Draft.Transport == McpTransportKind.Http
                            ? McpTransportKind.Stdio
                            : McpTransportKind.Http,
                    },
                },
            }
            : state);

    private void ChangeAuthMode(McpBrowserProvider current, long openEpoch) =>
        this.Mutate(current, openEpoch, state => state.Editor is { } editor
            ? state with
            {
                Editor = editor with
                {
                    Draft = editor.Draft with
                    {
                        AuthMode = editor.Draft.AuthMode switch
                        {
                            McpAuthMode.None => McpAuthMode.Bearer,
                            McpAuthMode.Bearer => McpAuthMode.OAuth,
                            _ => McpAuthMode.None,
                        },
                    },
                },
            }
            : state);

    private async Task PromptNamedReplacementAsync(
        McpBrowserProvider current,
        long openEpoch,
        string fieldPrefix,
        CancellationToken ct)
    {
        var editor = this.State.Editor;
        var values = editor?.FocusedField == McpEditorField.Environment
            ? editor.Draft.Environment
            : editor?.FocusedField == McpEditorField.Headers
                ? editor.Draft.Headers
                : [];
        if (editor is null || editor.SelectedItem < 0 || editor.SelectedItem >= values.Length)
        {
            this.ApplyStatus(current, openEpoch, "Add a named value first.");
            return;
        }

        var named = values[editor.SelectedItem];
        if (string.IsNullOrWhiteSpace(named.Name))
        {
            this.ApplyStatus(current, openEpoch, "Enter a name before replacing its value.");
            return;
        }

        var response = await current.Prompts.RequestAsync(
            UiPromptRequest.Text($"Replace {SafeText(fieldPrefix)}/{SafeText(named.Name)}", required: true, secret: true),
            ct).ConfigureAwait(false);
        if (response.Cancelled || string.IsNullOrEmpty(response.Text))
        {
            this.ApplyStatus(current, openEpoch, "Cancelled.");
            return;
        }

        var replacement = new McpSecretReplacement(response.Text);
        this.Mutate(current, openEpoch, state => state.Editor is { } active
            ? state with
            {
                Editor = active with
                {
                    Draft = fieldPrefix == "env"
                        ? active.Draft with
                        {
                            Environment = active.Draft.Environment.SetItem(
                                active.SelectedItem,
                                named with
                                {
                                    Change = new McpSecretChange(
                                        $"env/{named.Name}",
                                        McpSecretChangeKind.Replace,
                                        replacement),
                                }),
                        }
                        : active.Draft with
                        {
                            Headers = active.Draft.Headers.SetItem(
                                active.SelectedItem,
                                named with
                                {
                                    Change = new McpSecretChange(
                                        $"header/{named.Name}",
                                        McpSecretChangeKind.Replace,
                                        replacement),
                                }),
                        },
                },
            }
            : state);
    }

    private void ApplyRefresh(
        McpBrowserProvider current,
        long openEpoch,
        long refresh,
        McpManagementSnapshot snapshot)
    {
        var changed = false;
        lock (this.sync)
        {
            if (!this.IsCurrent(current, openEpoch) || refresh != this.refreshGeneration)
            {
                return;
            }

            this.projectScopeAvailable = snapshot.ProjectScopeAvailable;
            this.state = this.state.WithServers(snapshot.Servers)
                .WithStatus(snapshot.ReadError is null ? this.state.StatusMessage : SafeText(snapshot.ReadError));
            changed = true;
        }

        if (changed)
        {
            this.RaiseChanged();
        }
    }

    private void ApplyMutation(McpBrowserProvider current, long openEpoch, McpMutationResult result)
    {
        var changed = false;
        lock (this.sync)
        {
            if (!this.IsCurrent(current, openEpoch))
            {
                return;
            }

            this.state = this.state.Editor is not null &&
                result.Status is not McpMutationStatus.Succeeded and not McpMutationStatus.SavedWithRuntimeError
                    ? this.state.WithStatus(SafeText(result.Message))
                    : this.state
                        .WithServers(result.Snapshot.Servers, result.SelectedKey)
                        .ReturnToList()
                        .WithStatus(SafeText(result.Message));
            changed = true;
        }

        if (changed)
        {
            this.RaiseChanged();
        }
    }

    private void ApplyStatus(McpBrowserProvider current, long openEpoch, string status) =>
        this.Mutate(current, openEpoch, state => state.WithStatus(status));

    private void Mutate(
        McpBrowserProvider current,
        long openEpoch,
        Func<McpBrowserState, McpBrowserState> change)
    {
        var changed = false;
        lock (this.sync)
        {
            if (!this.IsCurrent(current, openEpoch))
            {
                return;
            }

            this.state = change(this.state);
            changed = true;
        }

        if (changed)
        {
            this.RaiseChanged();
        }
    }

    private bool IsCurrent(McpBrowserProvider current, long openEpoch) =>
        this.open && this.epoch == openEpoch && ReferenceEquals(this.bound, current);

    private void OnManagementChanged()
    {
        lock (this.sync)
        {
            if (!this.open || this.bound is null)
            {
                return;
            }
        }

        _ = this.RefreshAsync(CancellationToken.None);
    }

    private void OnIdleGateChanged()
    {
        McpBrowserProvider? current;
        long openEpoch;
        lock (this.sync)
        {
            current = this.bound;
            openEpoch = this.epoch;
        }

        if (current is not null)
        {
            this.Mutate(current, openEpoch, state => state.WithTurnBusy(current.IdleGate.IsBusy));
        }
    }

    private void RaiseChanged()
    {
        var handlers = this.Changed?.GetInvocationList().Cast<Action>().ToArray() ?? [];
        foreach (var handler in handlers)
        {
            try
            {
                handler();
            }
            catch
            {
                // A UI subscriber must not break lifecycle cleanup or another subscriber.
            }
        }
    }

    private static McpEditorField NextField(McpEditorField field, int direction)
    {
        var index = Array.IndexOf(editorFields, field);
        if (index < 0)
        {
            return editorFields[0];
        }

        return editorFields[(index + direction + editorFields.Length) % editorFields.Length];
    }

    private static ImmutableArray<string> ChangeItem(
        ImmutableArray<string> values,
        int index,
        Func<string, string> change)
    {
        if (values.IsDefaultOrEmpty)
        {
            return [change(string.Empty)];
        }

        index = Math.Clamp(index, 0, values.Length - 1);
        return values.SetItem(index, change(values[index]));
    }

    private static ImmutableArray<McpDraftListItem> ChangeItem(
        ImmutableArray<McpDraftListItem> values,
        int index,
        Func<string, string> change)
    {
        if (values.IsDefault)
        {
            return values;
        }

        if (values.IsEmpty)
        {
            return [McpDraftListItem.New(change(string.Empty))];
        }

        index = Math.Clamp(index, 0, values.Length - 1);
        var item = values[index];
        return values.SetItem(index, item with { Value = change(item.Value) });
    }

    private static ImmutableArray<McpDraftListItem> AppendItem(
        ImmutableArray<McpDraftListItem> items,
        ImmutableArray<string> previousValues) =>
        items.IsDefault
            ? previousValues.Select(McpDraftListItem.New).Append(McpDraftListItem.New(string.Empty)).ToImmutableArray()
            : items.Add(McpDraftListItem.New(string.Empty));

    private static ImmutableArray<McpDraftListItem> RemoveItem(
        ImmutableArray<McpDraftListItem> items,
        int index) =>
        items.IsDefault || index >= items.Length
            ? items
            : items.RemoveAt(index);

    private static McpNamedSecretDraft NewNamedSecret(string fieldPrefix) =>
        new(
            string.Empty,
            McpSecretSource.None,
            new McpSecretChange($"{fieldPrefix}/", McpSecretChangeKind.Unchanged));

    private static ImmutableArray<McpNamedSecretDraft> RemoveNamedSecret(
        ImmutableArray<McpNamedSecretDraft> values,
        int index)
    {
        var named = values[index];
        return named.ExistingSource == McpSecretSource.None
            ? values.RemoveAt(index)
            : values.SetItem(
                index,
                named with
                {
                    Change = new McpSecretChange(
                        named.Change.Field,
                        McpSecretChangeKind.Remove),
                });
    }

    private static string SafeText(string? value) =>
        TerminalTextSanitizer.SanitizeSingleLine(value) is { Length: > 0 } sanitized
            ? sanitized
            : "MCP operation could not be completed.";
}
