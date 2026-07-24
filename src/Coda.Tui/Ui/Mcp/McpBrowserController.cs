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
            case McpBrowserCommand.EditorNext:
                this.MoveEditor(current, openEpoch, 1);
                return;
            case McpBrowserCommand.EditorPrevious:
                this.MoveEditor(current, openEpoch, -1);
                return;
            case McpBrowserCommand.EditorCancel:
                this.Mutate(current, openEpoch, state => state.CancelEditor());
                return;
            case McpBrowserCommand.EditorApply:
                await this.ApplyEditorAsync(current, openEpoch, ct).ConfigureAwait(false);
                return;
            case McpBrowserCommand.EditorInsert:
                this.InsertEditorCharacter(current, openEpoch, key);
                return;
            case McpBrowserCommand.EditorAddItem:
                this.AddEditorItem(current, openEpoch);
                return;
            case McpBrowserCommand.EditorRemoveItem:
                this.RemoveEditorItem(current, openEpoch);
                return;
            case McpBrowserCommand.EditorPreviousItem:
                this.MoveEditorItem(current, openEpoch, -1);
                return;
            case McpBrowserCommand.EditorNextItem:
                this.MoveEditorItem(current, openEpoch, 1);
                return;
            case McpBrowserCommand.EditorPreviousItemPart:
                this.MoveEditorItemPart(current, openEpoch, -1);
                return;
            case McpBrowserCommand.EditorNextItemPart:
                this.MoveEditorItemPart(current, openEpoch, 1);
                return;
            case McpBrowserCommand.EditorBackspace:
                this.EditEditorText(current, openEpoch, value =>
                    value.Length == 0 ? value : value[..^1]);
                return;
            case McpBrowserCommand.EditorDelete:
                this.EditEditorText(current, openEpoch, _ => string.Empty);
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
                this.MoveEditor(current, openEpoch, 1);
                return;
        }
    }

    private async Task SaveEditorAsync(
        McpBrowserProvider current,
        long openEpoch,
        McpEditorState editor,
        CancellationToken ct) =>
        await this.MutateWithLeaseAsync(current, openEpoch, async token =>
        {
            var result = editor.Mode == McpEditorMode.Add
                ? await current.Management.CommitAddAsync(
                    await current.Management.PrepareAddAsync(editor.Draft, token).ConfigureAwait(false),
                    token).ConfigureAwait(false)
                : await current.Management.CommitEditAsync(
                    await current.Management.PrepareEditAsync(
                        this.State.SelectedKey ?? new McpServerKey(editor.Draft.Scope, editor.Draft.Name),
                        editor.Draft,
                        token).ConfigureAwait(false),
                    token).ConfigureAwait(false);
            this.ApplyMutation(current, openEpoch, result);
        }, ct).ConfigureAwait(false);

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

    private void MoveEditorItem(McpBrowserProvider current, long openEpoch, int direction) =>
        this.Mutate(current, openEpoch, state =>
        {
            if (state.Editor is not { } editor)
            {
                return state;
            }

            var count = editor.FocusedField switch
            {
                McpEditorField.Arguments => editor.Draft.Args.Length,
                McpEditorField.Scopes => editor.Draft.Scopes.Length,
                McpEditorField.Environment => editor.Draft.Environment.Length,
                McpEditorField.Headers => editor.Draft.Headers.Length,
                _ => 0,
            };
            return count == 0
                ? state
                : state with
                {
                    Editor = editor with
                    {
                        SelectedItem = Math.Clamp(editor.SelectedItem + direction, 0, count - 1),
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

    private async Task MutateWithLeaseAsync(
        McpBrowserProvider current,
        long openEpoch,
        Func<CancellationToken, Task> mutation,
        CancellationToken ct)
    {
        IDisposable? lease;
        try
        {
            lease = current.IdleGate.TryAcquire();
        }
        catch
        {
            this.ApplyStatus(current, openEpoch, "MCP changes are unavailable while a turn is running.");
            return;
        }

        if (lease is null)
        {
            this.ApplyStatus(current, openEpoch, "MCP changes are unavailable while a turn is running.");
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

    private void MoveEditor(McpBrowserProvider current, long openEpoch, int direction) =>
        this.Mutate(current, openEpoch, state => state.Editor is { } editor
            ? state with { Editor = editor with { FocusedField = NextField(editor.FocusedField, direction) } }
            : state);

    private void InsertEditorCharacter(McpBrowserProvider current, long openEpoch, Key? key)
    {
        var rune = key?.AsRune;
        if (rune is null || rune.Value.Value == 0 || System.Text.Rune.IsControl(rune.Value))
        {
            return;
        }

        this.EditEditorText(current, openEpoch, value => value + rune.Value.ToString());
    }

    private void EditEditorText(
        McpBrowserProvider current,
        long openEpoch,
        Func<string, string> change)
    {
        this.Mutate(current, openEpoch, state =>
        {
            if (state.Editor is not { } editor)
            {
                return state;
            }

            var draft = editor.Draft;
            draft = editor.FocusedField switch
            {
                McpEditorField.Name => draft with { Name = change(draft.Name) },
                McpEditorField.Command => draft with { Command = change(draft.Command ?? string.Empty) },
                McpEditorField.Url => draft with { Url = change(draft.Url ?? string.Empty), UrlChanged = true },
                McpEditorField.ClientId => draft with { ClientId = change(draft.ClientId ?? string.Empty) },
                McpEditorField.Scopes => draft with
                {
                    Scopes = ChangeItem(draft.Scopes, editor.SelectedItem, change),
                    ScopeItems = ChangeItem(draft.ScopeItems, editor.SelectedItem, change),
                },
                McpEditorField.Arguments => draft with
                {
                    Args = ChangeItem(draft.Args, editor.SelectedItem, change),
                    ArgumentItems = ChangeItem(draft.ArgumentItems, editor.SelectedItem, change),
                },
                McpEditorField.Environment when editor.SelectedItemPart == McpEditorItemPart.Name =>
                    draft with { Environment = ChangeNamedName(draft.Environment, editor.SelectedItem, "env", change) },
                McpEditorField.Headers when editor.SelectedItemPart == McpEditorItemPart.Name =>
                    draft with { Headers = ChangeNamedName(draft.Headers, editor.SelectedItem, "header", change) },
                _ => draft,
            };

            return state with { Editor = editor with { Draft = draft } };
        });
    }

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

    private static ImmutableArray<McpNamedSecretDraft> ChangeNamedName(
        ImmutableArray<McpNamedSecretDraft> values,
        int index,
        string fieldPrefix,
        Func<string, string> change)
    {
        if (index < 0 || index >= values.Length)
        {
            return values;
        }

        var named = values[index];
        var name = change(named.Name);
        return values.SetItem(
            index,
            named with
            {
                Name = name,
                Change = named.Change with { Field = $"{fieldPrefix}/{name}" },
            });
    }

    private static string SafeText(string? value) =>
        TerminalTextSanitizer.SanitizeSingleLine(value) is { Length: > 0 } sanitized
            ? sanitized
            : "MCP operation could not be completed.";
}
