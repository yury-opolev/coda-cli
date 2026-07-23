using System.Collections.Immutable;
using Coda.Mcp;

namespace Coda.Tui.Mcp;

internal enum McpTransportKind
{
    Stdio,
    Http,
}

internal enum McpConnectionState
{
    Overridden,
    Disconnected,
    Connected,
    Error,
}

internal enum McpSecretSource
{
    None,
    Managed,
    Environment,
    Literal,
}

internal enum McpSecretChangeKind
{
    Unchanged,
    Replace,
    Remove,
}

internal enum McpMutationStatus
{
    Succeeded,
    Rejected,
    SavedWithRuntimeError,
    NoOp,
}

internal enum McpReauthenticationKind
{
    OAuth,
    StoredSecret,
    EnvironmentOwned,
    Unavailable,
}

internal sealed class McpSecretReplacement
{
    private readonly string value;

    public McpSecretReplacement(string value) =>
        this.value = value ?? throw new ArgumentNullException(nameof(value));

    internal string RevealForCommit() => this.value;

    public override string ToString() => "*****";
}

internal sealed record McpSecretChange(
    string Field,
    McpSecretChangeKind Kind,
    McpSecretReplacement? Replacement = null);

internal sealed record McpNamedSecretDraft(
    string Name,
    McpSecretSource ExistingSource,
    McpSecretChange Change);

internal sealed record McpSecretDescriptor(
    string Field,
    string Name,
    McpSecretSource Source,
    string DisplayValue);

internal sealed record McpCapabilitySummary(
    string Name,
    string? Description);

internal sealed record McpServerSummary(
    McpServerKey Key,
    string SourceFile,
    bool Enabled,
    bool IsEffective,
    McpTransportKind Transport,
    McpConnectionState Connection,
    string? LastError);

internal sealed record McpManagementSnapshot(
    bool ProjectScopeAvailable,
    ImmutableArray<McpServerSummary> Servers,
    string? ReadError = null);

internal sealed record McpServerDetail(
    McpServerSummary Summary,
    string? Command,
    ImmutableArray<string> Args,
    string? Url,
    ImmutableArray<McpSecretDescriptor> Environment,
    ImmutableArray<McpSecretDescriptor> Headers,
    McpAuthMode AuthMode,
    string? ClientId,
    ImmutableArray<string> Scopes,
    McpSecretDescriptor? BearerToken,
    ImmutableArray<McpCapabilitySummary> Tools,
    ImmutableArray<McpCapabilitySummary> Prompts,
    ImmutableArray<McpCapabilitySummary> Resources);

/// <summary>
/// A safe, display-only list item in an MCP edit draft. Service-created item IDs identify an
/// original list position without retaining that position's raw configuration value.
/// </summary>
internal sealed record McpDraftListItem(Guid Id, string Value)
{
    /// <summary>Create a new user-entered item that cannot be mistaken for an original item.</summary>
    public static McpDraftListItem New(string value) => new(Guid.NewGuid(), value);
}

internal sealed record McpServerDraft(
    string Name,
    McpConfigScope Scope,
    bool Enabled,
    McpTransportKind Transport,
    string? Command,
    ImmutableArray<string> Args,
    string? Url,
    ImmutableArray<McpNamedSecretDraft> Environment,
    ImmutableArray<McpNamedSecretDraft> Headers,
    McpAuthMode AuthMode,
    string? ClientId,
    ImmutableArray<string> Scopes,
    McpSecretChange BearerToken,
    McpConfigRevision? BaseRevision = null)
{
    /// <summary>
    /// Stable, non-secret identity for service-created edit drafts. A zero value indicates a legacy
    /// draft that only uses the positional fields above.
    /// </summary>
    public Guid DraftId { get; init; }

    /// <summary>
    /// Authoritative service-created stdio argument items. Values are safe display values only; a
    /// default array means callers are using the compatible positional <see cref="Args"/> field.
    /// </summary>
    public ImmutableArray<McpDraftListItem> ArgumentItems { get; init; }

    /// <summary>
    /// Authoritative service-created OAuth scope items. Values are safe display values only; a
    /// default array means callers are using the compatible positional <see cref="Scopes"/> field.
    /// </summary>
    public ImmutableArray<McpDraftListItem> ScopeItems { get; init; }

    /// <summary>
    /// Indicates that an editor intentionally changed the URL, even when its value equals the safe
    /// display form of a redacted original URL.
    /// </summary>
    public bool UrlChanged { get; init; }
}

internal sealed record McpConfigRevision(
    string UserSha256,
    string ProjectSha256);

internal sealed record McpEditPreview(
    Guid OperationId,
    McpServerKey? OriginalKey,
    McpServerDraft Draft,
    McpConfigRevision Revision,
    ImmutableArray<string> Warnings);

internal sealed record McpDeletePreview(
    Guid OperationId,
    McpServerKey Key,
    McpConfigRevision Revision,
    string Confirmation,
    bool RevealsLowerScope);

internal sealed record McpReauthenticationPlan(
    Guid OperationId,
    McpServerKey Key,
    McpConfigRevision Revision,
    McpReauthenticationKind Kind,
    string Confirmation,
    ImmutableArray<string> ManagedFields,
    string? DisabledReason);

internal sealed record McpMutationResult(
    McpMutationStatus Status,
    McpServerKey? SelectedKey,
    string Message,
    McpManagementSnapshot Snapshot);

internal sealed record McpRuntimeReconcileResult(
    ImmutableArray<string> Stopped,
    ImmutableArray<string> Started,
    ImmutableArray<string> Errors);
