using System.Collections.Immutable;
using Coda.Mcp;

namespace Coda.Tui.Mcp;

public enum McpTransportKind
{
    Stdio,
    Http,
}

public enum McpConnectionState
{
    Overridden,
    Disconnected,
    Connected,
    Error,
}

public enum McpSecretSource
{
    None,
    Managed,
    Environment,
    Literal,
}

public enum McpSecretChangeKind
{
    Unchanged,
    Replace,
    Remove,
}

public enum McpMutationStatus
{
    Succeeded,
    Rejected,
    SavedWithRuntimeError,
    NoOp,
}

public enum McpReauthenticationKind
{
    OAuth,
    StoredSecret,
    EnvironmentOwned,
    Unavailable,
}

public sealed class McpSecretReplacement
{
    private readonly string value;
    private readonly bool storeInCredentialStore;

    public McpSecretReplacement(string value, bool storeInCredentialStore = true)
    {
        this.value = value ?? throw new ArgumentNullException(nameof(value));
        this.storeInCredentialStore = storeInCredentialStore;
    }

    internal string RevealForCommit() => this.value;

    internal bool StoreInCredentialStore => this.storeInCredentialStore;

    internal static McpSecretReplacement Literal(string value) => new(value, storeInCredentialStore: false);

    public override string ToString() => "*****";
}

public sealed record McpSecretChange(
    string Field,
    McpSecretChangeKind Kind,
    McpSecretReplacement? Replacement = null);

public sealed record McpNamedSecretDraft(
    string Name,
    McpSecretSource ExistingSource,
    McpSecretChange Change);

public sealed record McpSecretDescriptor(
    string Field,
    string Name,
    McpSecretSource Source,
    string DisplayValue);

public sealed record McpCapabilitySummary(
    string Name,
    string? Description);

public sealed record McpServerSummary(
    McpServerKey Key,
    string SourceFile,
    bool Enabled,
    bool IsEffective,
    McpTransportKind Transport,
    McpConnectionState Connection,
    string? LastError);

public sealed record McpManagementSnapshot(
    bool ProjectScopeAvailable,
    ImmutableArray<McpServerSummary> Servers,
    string? ReadError = null);

public sealed record McpServerDetail(
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
public sealed record McpDraftListItem(Guid Id, string Value)
{
    /// <summary>Create a new user-entered item that cannot be mistaken for an original item.</summary>
    public static McpDraftListItem New(string value) => new(Guid.NewGuid(), value);
}

public sealed record McpServerDraft(
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

public sealed record McpConfigRevision(
    string UserSha256,
    string ProjectSha256);

public sealed record McpEditPreview(
    Guid OperationId,
    McpServerKey? OriginalKey,
    McpServerDraft Draft,
    McpConfigRevision Revision,
    ImmutableArray<string> Warnings);

public sealed record McpDeletePreview(
    Guid OperationId,
    McpServerKey Key,
    McpConfigRevision Revision,
    string Confirmation,
    bool RevealsLowerScope);

public sealed record McpReauthenticationPlan(
    Guid OperationId,
    McpServerKey Key,
    McpConfigRevision Revision,
    McpReauthenticationKind Kind,
    string Confirmation,
    ImmutableArray<string> ManagedFields,
    string? DisabledReason,
    string? OAuthCanonicalResource = null);

public sealed record McpMutationResult(
    McpMutationStatus Status,
    McpServerKey? SelectedKey,
    string Message,
    McpManagementSnapshot Snapshot);

public sealed record McpRuntimeReconcileResult(
    ImmutableArray<string> Stopped,
    ImmutableArray<string> Started,
    ImmutableArray<string> Errors);
