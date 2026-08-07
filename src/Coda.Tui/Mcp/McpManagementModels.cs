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
    McpSecretChange Change,
    /// <summary>
    /// The display value shown in the editor — the raw value exactly as it appears in .mcp.json,
    /// sanitized by <c>SanitizeIdentifier</c>.  <c>coda-secret:</c> and <c>${VAR}</c> references
    /// are stored here verbatim; they are never resolved to plaintext.  A modal-entered secret
    /// replacement keeps <see cref="McpSecretChangeKind.Replace"/> on the <see cref="Change"/>
    /// and is never placed in this field.
    /// </summary>
    string Value = "")
{
    /// <summary>
    /// Keeps <see cref="Value"/> out of the generated <c>ToString()</c>. Nothing logs a draft today,
    /// but the value is the raw <c>.mcp.json</c> content — a literal one IS the secret — and a
    /// record's default formatting would put it into any future log or telemetry line for free.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Name = ").Append(this.Name)
            .Append(", ExistingSource = ").Append(this.ExistingSource)
            .Append(", Change = ").Append(this.Change)
            .Append(", Value = *****");
        return true;
    }
}

public sealed record McpSecretDescriptor(
    string Field,
    string Name,
    McpSecretSource Source,
    string DisplayValue);

public sealed record McpCapabilitySummary(
    string Name,
    string? Description,
    bool SchemaCoerced = false);

public sealed record McpServerSummary(
    McpServerKey Key,
    string SourceFile,
    bool Enabled,
    bool IsEffective,
    McpTransportKind Transport,
    McpConnectionState Connection,
    string? LastError)
{
    /// <summary>Number of tools exposed by this server, or null when the runtime has not yet connected.</summary>
    public int? ToolCount { get; init; }
}

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
    ImmutableArray<McpCapabilitySummary> Resources,
    string? SchemaNote = null);

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
