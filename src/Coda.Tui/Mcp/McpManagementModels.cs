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
    McpConfigRevision? BaseRevision = null);

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
