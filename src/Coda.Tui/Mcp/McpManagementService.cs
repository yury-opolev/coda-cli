using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Coda.Common;
using Coda.Mcp;
using Coda.Mcp.Auth;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Rendering;
using LlmAuth;

namespace Coda.Tui.Mcp;

internal interface IMcpConfigMutator
{
    void Upsert(
        McpConfigScope scope,
        string name,
        McpServerConfig config,
        bool disabled,
        string workingDirectory,
        string? userMcpDir);

    void ReplaceEntry(
        McpConfigScope scope,
        string currentName,
        string newName,
        McpServerConfig config,
        bool disabled,
        string workingDirectory,
        string? userMcpDir);

    bool Remove(
        McpConfigScope scope,
        string name,
        string workingDirectory,
        string? userMcpDir);

    bool SetDisabled(
        McpConfigScope scope,
        string name,
        bool disabled,
        string workingDirectory,
        string? userMcpDir);
}

/// <summary>
/// Optional revision-aware mutation contract. Legacy test and extension mutators remain supported,
/// while the production adapter uses this contract to make its destination write compare-and-swap.
/// </summary>
internal interface IRevisionedMcpConfigMutator : IMcpConfigMutator
{
    void Upsert(
        McpConfigScope scope,
        string name,
        McpServerConfig config,
        bool disabled,
        string workingDirectory,
        string? userMcpDir,
        string expectedRevision);

    void ReplaceEntry(
        McpConfigScope scope,
        string currentName,
        string newName,
        McpServerConfig config,
        bool disabled,
        string workingDirectory,
        string? userMcpDir,
        string expectedRevision);

    bool Remove(
        McpConfigScope scope,
        string name,
        string workingDirectory,
        string? userMcpDir,
        string expectedRevision);

    bool SetDisabled(
        McpConfigScope scope,
        string name,
        bool disabled,
        string workingDirectory,
        string? userMcpDir,
        string expectedRevision);
}

internal interface IMcpManagementService
{
    event Action? Changed;

    Task<McpManagementSnapshot> RefreshAsync(CancellationToken ct);

    Task<McpServerDetail?> GetDetailAsync(McpServerKey key, CancellationToken ct);

    Task<McpServerDraft?> CreateEditDraftAsync(McpServerKey key, CancellationToken ct);

    Task<McpEditPreview> PrepareAddAsync(McpServerDraft draft, CancellationToken ct);

    Task<McpEditPreview> PrepareEditAsync(
        McpServerKey original,
        McpServerDraft draft,
        CancellationToken ct);

    Task<McpMutationResult> CommitAddAsync(McpEditPreview preview, CancellationToken ct);

    Task<McpMutationResult> CommitEditAsync(McpEditPreview preview, CancellationToken ct);

    Task<McpMutationResult> SetEnabledAsync(
        McpServerKey key,
        bool enabled,
        CancellationToken ct);

    Task<McpDeletePreview> PrepareDeleteAsync(McpServerKey key, CancellationToken ct);

    Task<McpMutationResult> CommitDeleteAsync(
        McpDeletePreview confirmedPreview,
        CancellationToken ct);

    Task<McpReauthenticationPlan> PrepareReauthenticationAsync(
        McpServerKey key,
        CancellationToken ct);

    Task<McpMutationResult> ReauthenticateAsync(
        McpReauthenticationPlan plan,
        IReadOnlyDictionary<string, McpSecretReplacement> replacements,
        CancellationToken ct);

    Task<McpMutationResult> StartAsync(string name, CancellationToken ct);

    Task<McpMutationResult> StopAsync(string name, CancellationToken ct);

    Task<McpMutationResult> RestartAsync(string? name, CancellationToken ct);
}

internal sealed partial class McpManagementService : IMcpManagementService
{
    internal const string MissingFileRevision = "missing";

    private static readonly TimeSpan capabilityTimeout = TimeSpan.FromSeconds(5);
    private const long maximumLcsCells = 1_000_000;
    private readonly string workingDirectory;
    private readonly string? userMcpDir;
    private readonly McpClientManager? runtime;
    private readonly ITokenStore credentials;
    private readonly IMcpOAuthReauthenticator oauth;
    private readonly IUiEventPublisher events;
    private readonly IMcpConfigMutator configMutator;
    private readonly Action? afterPreparationEntriesRead;
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly HashSet<Guid> completedOperations = [];

    internal McpManagementService(
        string workingDirectory,
        string? userMcpDir,
        McpClientManager? runtime,
        ITokenStore credentials,
        IMcpOAuthReauthenticator oauth,
        IUiEventPublisher events,
        IMcpConfigMutator? configMutator = null,
        Action? afterPreparationEntriesRead = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        this.workingDirectory = workingDirectory;
        this.userMcpDir = userMcpDir;
        this.runtime = runtime;
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        this.events = events ?? throw new ArgumentNullException(nameof(events));
        this.configMutator = configMutator ?? McpConfigWriterMutator.Instance;
        this.afterPreparationEntriesRead = afterPreparationEntriesRead;
    }

    public event Action? Changed;

    public Task<McpManagementSnapshot> RefreshAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var entries = this.LoadPhysicalEntries(ct);
            var servers = entries
                .Select(entry => this.CreateSummary(entry))
                .ToImmutableArray();
            return Task.FromResult(new McpManagementSnapshot(
                Directory.Exists(this.workingDirectory),
                servers));
        }
        catch (McpException exception)
        {
            return Task.FromResult(this.CreateReadFailure(exception));
        }
        catch (IOException exception)
        {
            return Task.FromResult(this.CreateReadFailure(exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Task.FromResult(this.CreateReadFailure(exception));
        }
    }

    public async Task<McpServerDetail?> GetDetailAsync(McpServerKey key, CancellationToken ct)
    {
        var entry = this.FindPhysicalEntry(key, ct);
        if (entry is null)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        var detail = this.CreateDetail(entry);
        if (!entry.IsEffective || this.runtime?.IsServerConnected(entry.Key.Name) != true)
        {
            return detail;
        }

        var capabilities = await this.ReadCapabilitiesAsync(entry.Key.Name, ct).ConfigureAwait(false);
        return detail with
        {
            Summary = this.CreateSummary(entry, capabilities.LastError),
            Tools = capabilities.Tools,
            Prompts = capabilities.Prompts,
            Resources = capabilities.Resources,
        };
    }

    public Task<McpServerDraft?> CreateEditDraftAsync(McpServerKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var beforeRead = CaptureRevision(this.workingDirectory, this.userMcpDir, ct);
        var entry = this.FindPhysicalEntry(key, ct);
        if (entry is null)
        {
            return Task.FromResult<McpServerDraft?>(null);
        }

        var afterRead = CaptureRevision(this.workingDirectory, this.userMcpDir, ct);
        if (beforeRead != afterRead)
        {
            throw StaleEditDraft();
        }

        return Task.FromResult<McpServerDraft?>(this.CreateDraft(entry, afterRead));
    }

    public Task<McpEditPreview> PrepareAddAsync(McpServerDraft draft, CancellationToken ct) =>
        Task.FromResult(this.PrepareAdd(draft, ct));

    public Task<McpEditPreview> PrepareEditAsync(
        McpServerKey original,
        McpServerDraft draft,
        CancellationToken ct) =>
        Task.FromResult(this.PrepareEdit(original, draft, ct));

    public Task<McpMutationResult> CommitAddAsync(McpEditPreview preview, CancellationToken ct) =>
        this.CommitAsync(preview, isAdd: true, ct);

    public Task<McpMutationResult> CommitEditAsync(McpEditPreview preview, CancellationToken ct) =>
        this.CommitAsync(preview, isAdd: false, ct);

    public Task<McpMutationResult> SetEnabledAsync(
        McpServerKey key,
        bool enabled,
        CancellationToken ct) =>
        this.SetEnabledCoreAsync(key, enabled, ct);

    public Task<McpDeletePreview> PrepareDeleteAsync(McpServerKey key, CancellationToken ct) =>
        Task.FromResult(this.PrepareDelete(key, ct));

    public Task<McpMutationResult> CommitDeleteAsync(
        McpDeletePreview confirmedPreview,
        CancellationToken ct) =>
        this.CommitDeleteCoreAsync(confirmedPreview, ct);

    public Task<McpReauthenticationPlan> PrepareReauthenticationAsync(
        McpServerKey key,
        CancellationToken ct) =>
        Task.FromResult(this.PrepareReauthentication(key, ct));

    public Task<McpMutationResult> ReauthenticateAsync(
        McpReauthenticationPlan plan,
        IReadOnlyDictionary<string, McpSecretReplacement> replacements,
        CancellationToken ct) =>
        this.ReauthenticateCoreAsync(plan, replacements, ct);

    public Task<McpMutationResult> StartAsync(string name, CancellationToken ct) =>
        this.StartCoreAsync(name, ct);

    public Task<McpMutationResult> StopAsync(string name, CancellationToken ct) =>
        this.StopCoreAsync(name, ct);

    public Task<McpMutationResult> RestartAsync(string? name, CancellationToken ct) =>
        this.RestartCoreAsync(name, ct);

    private async Task<McpMutationResult> StartCoreAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ct.ThrowIfCancellationRequested();
        if (this.runtime is null)
        {
            return await this.CreateRejectedResultAsync("MCP runtime is not available in this session.").ConfigureAwait(false);
        }

        if (this.runtime.IsServerConnected(name))
        {
            return await this.CreateLifecycleResultAsync(
                McpMutationStatus.NoOp,
                new McpServerKey(McpConfigScope.Project, name),
                $"'{SanitizeIdentifier(name)}' is already running.").ConfigureAwait(false);
        }

        var entries = this.LoadPhysicalEntries(ct);
        var entry = entries.FirstOrDefault(candidate => candidate.IsEffective
            && string.Equals(candidate.Key.Name, name, StringComparison.Ordinal));
        if (entry is null)
        {
            return await this.CreateRejectedResultAsync(
                $"'{SanitizeIdentifier(name)}' is not configured.").ConfigureAwait(false);
        }

        try
        {
            var config = await McpSecretResolver.ResolveAsync(entry.Config, this.credentials, ct).ConfigureAwait(false);
            var result = await this.runtime.ConnectServerAsync(name, config, ct).ConfigureAwait(false);
            return await this.CreateLifecycleResultAsync(
                result.Connected ? McpMutationStatus.Succeeded : McpMutationStatus.SavedWithRuntimeError,
                entry.Key,
                result.Connected
                    ? $"Started '{SanitizeIdentifier(name)}'."
                    : $"Failed to start '{SanitizeIdentifier(name)}': {SanitizeError(result.Error)}").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await this.CreateLifecycleResultAsync(
                McpMutationStatus.SavedWithRuntimeError,
                entry.Key,
                $"Failed to start '{SanitizeIdentifier(name)}': {SanitizeError(exception.Message)}").ConfigureAwait(false);
        }
    }

    private async Task<McpMutationResult> StopCoreAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ct.ThrowIfCancellationRequested();
        if (this.runtime is null)
        {
            return await this.CreateRejectedResultAsync("MCP runtime is not available in this session.").ConfigureAwait(false);
        }

        var stopped = await this.runtime.DisconnectServerAsync(name).ConfigureAwait(false);
        return await this.CreateLifecycleResultAsync(
            stopped ? McpMutationStatus.Succeeded : McpMutationStatus.NoOp,
            new McpServerKey(McpConfigScope.Project, name),
            stopped
                ? $"Stopped '{SanitizeIdentifier(name)}'."
                : $"'{SanitizeIdentifier(name)}' is not running.").ConfigureAwait(false);
    }

    private async Task<McpMutationResult> RestartCoreAsync(string? name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (this.runtime is null)
        {
            return await this.CreateRejectedResultAsync("MCP runtime is not available in this session.").ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var entries = this.LoadPhysicalEntries(ct);
            var entry = entries.FirstOrDefault(candidate => candidate.IsEffective
                && string.Equals(candidate.Key.Name, name, StringComparison.Ordinal));
            if (entry is null)
            {
                return await this.CreateRejectedResultAsync(
                    $"'{SanitizeIdentifier(name)}' is not configured.").ConfigureAwait(false);
            }

            try
            {
                var config = await McpSecretResolver.ResolveAsync(entry.Config, this.credentials, ct).ConfigureAwait(false);
                await this.runtime.DisconnectServerAsync(name).ConfigureAwait(false);
                var result = await this.runtime.ConnectServerAsync(name, config, ct).ConfigureAwait(false);
                return await this.CreateLifecycleResultAsync(
                    result.Connected ? McpMutationStatus.Succeeded : McpMutationStatus.SavedWithRuntimeError,
                    entry.Key,
                    result.Connected
                        ? $"Restarted '{SanitizeIdentifier(name)}'."
                        : $"Failed to restart '{SanitizeIdentifier(name)}': {SanitizeError(result.Error)}").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return await this.CreateLifecycleResultAsync(
                    McpMutationStatus.SavedWithRuntimeError,
                    entry.Key,
                    $"Failed to restart '{SanitizeIdentifier(name)}': {SanitizeError(exception.Message)}").ConfigureAwait(false);
            }
        }

        var servers = McpConfig.Load(this.workingDirectory, this.userMcpDir);
        var resolvedServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var (serverName, rawConfig) in servers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                resolvedServers.Add(
                    serverName,
                    await McpSecretResolver.ResolveAsync(rawConfig, this.credentials, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add($"{SanitizeIdentifier(serverName)}: {SanitizeError(exception.Message)}");
            }
        }

        if (errors.Count == 0)
        {
            foreach (var serverName in this.runtime.Clients.Select(client => client.ServerName).ToList())
            {
                await this.runtime.DisconnectServerAsync(serverName).ConfigureAwait(false);
            }

            foreach (var (serverName, config) in resolvedServers)
            {
                try
                {
                    var result = await this.runtime.ConnectServerAsync(serverName, config, ct).ConfigureAwait(false);
                    if (!result.Connected)
                    {
                        errors.Add($"{SanitizeIdentifier(serverName)}: {SanitizeError(result.Error)}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    errors.Add($"{SanitizeIdentifier(serverName)}: {SanitizeError(exception.Message)}");
                }
            }
        }

        var message = errors.Count == 0
            ? $"Reconnected MCP servers ({this.runtime.Clients.Count} connected)."
            : $"MCP servers reconnected with errors: {errors[0]}";
        return await this.CreateLifecycleResultAsync(
            errors.Count == 0 ? McpMutationStatus.Succeeded : McpMutationStatus.SavedWithRuntimeError,
            null,
            message).ConfigureAwait(false);
    }

    private async Task<McpMutationResult> CreateLifecycleResultAsync(
        McpMutationStatus status,
        McpServerKey? key,
        string message)
    {
        if (this.runtime is not null)
        {
            this.events.Publish(new McpRuntimeChangedEvent(this.runtime.GetSnapshot()));
        }

        this.RaiseChanged();
        return new McpMutationResult(
            status,
            key,
            SanitizeError(message),
            await this.RefreshAsync(CancellationToken.None).ConfigureAwait(false));
    }

    internal static McpConfigRevision CaptureRevision(
        string workingDirectory,
        string? userMcpDir,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ct.ThrowIfCancellationRequested();
        var user = FileHash(McpConfig.FilePath(McpConfigScope.User, workingDirectory, userMcpDir), ct);
        var project = FileHash(McpConfig.FilePath(McpConfigScope.Project, workingDirectory, userMcpDir), ct);
        return new McpConfigRevision(user, project);
    }

    internal McpEditPreview PrepareAdd(McpServerDraft draft, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = ValidateAndNormalizeDraft(draft, isAdd: true);
        this.ValidateScopeAvailable(normalized.Scope);
        var prepared = this.LoadStablePreparationEntries(ct);
        var entries = prepared.Entries;
        var target = new McpServerKey(normalized.Scope, normalized.Name);
        if (entries.Any(entry => entry.Key == target))
        {
            throw new McpException("An MCP server with this name already exists in the selected scope.");
        }

        return new McpEditPreview(
            Guid.NewGuid(),
            null,
            normalized,
            prepared.Revision,
            CreateScopeWarnings(entries, null, normalized));
    }

    internal McpEditPreview PrepareEdit(
        McpServerKey original,
        McpServerDraft draft,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(draft);
        var prepared = this.LoadStablePreparationEntries(ct);
        if (draft.BaseRevision is { } baseRevision && baseRevision != prepared.Revision)
        {
            throw StaleEditDraft();
        }

        var entries = prepared.Entries;
        var current = entries.FirstOrDefault(entry => entry.Key == original);
        if (current is null)
        {
            throw new McpException("The selected MCP server no longer exists in the selected scope.");
        }

        var baseline = this.CreateDraft(current, prepared.Revision, draft.DraftId);
        if (draft.BaseRevision is null && !IsSafeExternalEditBaseline(baseline, current.Config))
        {
            throw new McpException(
                "An MCP edit draft without a base revision is not safe for this configuration. Reopen the edit and try again.");
        }

        var normalized = ValidateAndNormalizeDraft(draft, isAdd: false);
        ValidateBearerTokenAvailability(normalized, current.Config);
        this.ValidateScopeAvailable(normalized.Scope);
        if (original.Scope != normalized.Scope)
        {
            throw new McpException("An MCP edit cannot change configuration scope.");
        }

        var target = new McpServerKey(normalized.Scope, normalized.Name);
        if (target != original && entries.Any(entry => entry.Key == target))
        {
            throw new McpException("An MCP server with this name already exists in the selected scope.");
        }

        normalized = normalized with { BaseRevision = prepared.Revision };
        return new McpEditPreview(
            Guid.NewGuid(),
            original,
            normalized,
            prepared.Revision,
            CreateScopeWarnings(entries, original, normalized));
    }

    internal McpDeletePreview PrepareDelete(McpServerKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        this.ValidateScopeAvailable(key.Scope);
        var prepared = this.LoadStablePreparationEntries(ct);
        var entry = prepared.Entries.FirstOrDefault(candidate => candidate.Key == key)
            ?? throw new McpException("The selected MCP server no longer exists in the selected scope.");
        var revealsLowerScope = key.Scope == McpConfigScope.Project
            && prepared.Entries.Any(candidate =>
                candidate.Key == new McpServerKey(McpConfigScope.User, key.Name));
        var scope = key.Scope == McpConfigScope.Project ? "project" : "user";
        var confirmation = revealsLowerScope
            ? $"Delete {scope}-scope MCP server '{SanitizeIdentifier(key.Name)}'? The user-scope server with the same name will be revealed."
            : $"Delete {scope}-scope MCP server '{SanitizeIdentifier(key.Name)}'?";
        return new McpDeletePreview(
            Guid.NewGuid(),
            entry.Key,
            prepared.Revision,
            confirmation,
            revealsLowerScope);
    }

    internal McpReauthenticationPlan PrepareReauthentication(McpServerKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        this.ValidateScopeAvailable(key.Scope);
        var prepared = this.LoadStablePreparationEntries(ct);
        var entry = prepared.Entries.FirstOrDefault(candidate => candidate.Key == key)
            ?? throw new McpException("The selected MCP server no longer exists in the selected scope.");
        var safeName = SanitizeIdentifier(key.Name);

        if (entry.Config is not McpHttpServerConfig http)
        {
            return new McpReauthenticationPlan(
                Guid.NewGuid(),
                key,
                prepared.Revision,
                McpReauthenticationKind.Unavailable,
                $"Reauthenticate MCP server '{safeName}'?",
                [],
                "Stdio MCP servers use command and environment credentials. Use Edit to change them.");
        }

        var managedFields = OwnedSecretReferences(key.Name, http)
            .Select(static binding => binding.Field)
            .OrderBy(static field => field, StringComparer.Ordinal)
            .ToImmutableArray();
        if (HasEnvironmentCredentials(http))
        {
            return new McpReauthenticationPlan(
                Guid.NewGuid(),
                key,
                prepared.Revision,
                McpReauthenticationKind.EnvironmentOwned,
                $"Reauthenticate MCP server '{safeName}'?",
                [],
                "This MCP server uses environment-owned credentials. Update the referenced environment variable instead.");
        }

        if (!managedFields.IsDefaultOrEmpty)
        {
            return new McpReauthenticationPlan(
                Guid.NewGuid(),
                key,
                prepared.Revision,
                McpReauthenticationKind.StoredSecret,
                $"Replace the managed credentials for MCP server '{safeName}'?",
                managedFields,
                null);
        }

        if (HasLiteralCredentials(http))
        {
            return new McpReauthenticationPlan(
                Guid.NewGuid(),
                key,
                prepared.Revision,
                McpReauthenticationKind.Unavailable,
                $"Reauthenticate MCP server '{safeName}'?",
                [],
                "This MCP server uses literal credentials. Use Edit to replace them safely.");
        }

        if (http.Auth.Mode == McpAuthMode.OAuth)
        {
            return new McpReauthenticationPlan(
                Guid.NewGuid(),
                key,
                prepared.Revision,
                McpReauthenticationKind.OAuth,
                $"Reauthenticate OAuth for MCP server '{safeName}'? The shared token for this server URL will be replaced; its dynamic client registration will be retained.",
                [],
                null,
                CanonicalResourceUri.From(http.Url));
        }

        return new McpReauthenticationPlan(
            Guid.NewGuid(),
            key,
            prepared.Revision,
            McpReauthenticationKind.Unavailable,
            $"Reauthenticate MCP server '{safeName}'?",
            [],
            "This MCP server does not have reauthenticatable credentials.");
    }

    private void ValidateScopeAvailable(McpConfigScope scope)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new McpException("The selected MCP configuration scope is not supported.");
        }

        if (scope == McpConfigScope.Project && !Directory.Exists(this.workingDirectory))
        {
            throw new McpException("The project MCP configuration scope is unavailable.");
        }
    }

    private static McpException StaleEditDraft() =>
        new("MCP configuration changed after this edit was opened. Review the current configuration and try again.");

    private static McpServerDraft ValidateAndNormalizeDraft(McpServerDraft draft, bool isAdd)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var nameError = McpServerNameValidator.Validate(draft.Name);
        if (nameError is not null)
        {
            throw new McpException(nameError);
        }

        if (!Enum.IsDefined(draft.Scope))
        {
            throw new McpException("The selected MCP configuration scope is not supported.");
        }

        if (!Enum.IsDefined(draft.Transport))
        {
            throw new McpException("The selected MCP transport is not supported.");
        }

        return draft.Transport switch
        {
            McpTransportKind.Stdio => NormalizeStdioDraft(draft, isAdd),
            McpTransportKind.Http => NormalizeHttpDraft(draft, isAdd),
            _ => throw new McpException("The selected MCP transport is not supported."),
        };
    }

    private static McpServerDraft NormalizeStdioDraft(McpServerDraft draft, bool isAdd)
    {
        if (string.IsNullOrWhiteSpace(draft.Command))
        {
            throw new McpException("Stdio MCP servers require a command.");
        }

        ValidateSafeText(draft.Command, "The MCP command contains unsafe characters.");
        var args = NormalizeArgs(draft.Args);
        var argumentItems = NormalizeArgumentItems(draft.ArgumentItems);
        if (isAdd && !argumentItems.IsDefault)
        {
            args = argumentItems.Select(static item => item.Value).ToImmutableArray();
        }

        var environment = NormalizeNamedSecrets(draft.Environment, "env", "Environment variable", isAdd);
        return draft with
        {
            Enabled = isAdd || draft.Enabled,
            Command = draft.Command,
            Args = args,
            Url = null,
            ArgumentItems = argumentItems,
            Environment = environment,
            Headers = ImmutableArray<McpNamedSecretDraft>.Empty,
            AuthMode = McpAuthMode.None,
            ClientId = null,
            Scopes = ImmutableArray<string>.Empty,
            ScopeItems = default,
            UrlChanged = false,
            BearerToken = UnchangedBearerToken(),
        };
    }

    private static void ValidateBearerTokenAvailability(
        McpServerDraft draft,
        McpServerConfig? original)
    {
        if (draft.Transport != McpTransportKind.Http
            || draft.AuthMode != McpAuthMode.Bearer
            || draft.BearerToken.Kind != McpSecretChangeKind.Unchanged)
        {
            return;
        }

        if (original is not McpHttpServerConfig
            {
                Auth.BearerToken: { Length: > 0 },
            })
        {
            throw new McpException("Bearer authentication requires a replacement token.");
        }
    }

    private static McpServerDraft NormalizeHttpDraft(McpServerDraft draft, bool isAdd)
    {
        var url = NormalizeHttpUrl(draft.Url);
        var headers = NormalizeNamedSecrets(draft.Headers, "header", "HTTP header", isAdd);
        if (!Enum.IsDefined(draft.AuthMode))
        {
            throw new McpException("The selected MCP authentication mode is not supported.");
        }

        var clientId = NormalizeOptionalText(draft.ClientId, "The OAuth client ID contains unsafe characters.");
        var scopes = NormalizeScopes(draft.Scopes);
        var scopeItems = NormalizeScopeItems(draft.ScopeItems);
        if (isAdd && !scopeItems.IsDefault)
        {
            scopes = scopeItems.Select(static item => item.Value).ToImmutableArray();
        }

        if (draft.AuthMode is McpAuthMode.None or McpAuthMode.Bearer
            && (clientId is not null || scopes.Length > 0))
        {
            throw new McpException("Client ID and scopes are only supported with OAuth authentication.");
        }

        var bearer = NormalizeSecretChange(draft.BearerToken, "auth/token", isAdd, McpSecretSource.None);
        if (draft.AuthMode == McpAuthMode.None && bearer.Kind == McpSecretChangeKind.Replace)
        {
            throw new McpException("Authentication mode None cannot contain a bearer token.");
        }

        if (draft.AuthMode == McpAuthMode.Bearer
            && (bearer.Kind == McpSecretChangeKind.Remove
                || (isAdd && bearer.Kind == McpSecretChangeKind.Unchanged)))
        {
            throw new McpException("Bearer authentication requires a replacement token.");
        }

        return draft with
        {
            Enabled = isAdd || draft.Enabled,
            Command = null,
            Args = ImmutableArray<string>.Empty,
            ArgumentItems = default,
            Url = url,
            Environment = ImmutableArray<McpNamedSecretDraft>.Empty,
            Headers = headers,
            AuthMode = draft.AuthMode,
            ClientId = clientId,
            Scopes = scopes,
            ScopeItems = scopeItems,
            BearerToken = bearer,
        };
    }

    private static ImmutableArray<string> NormalizeArgs(ImmutableArray<string> values)
    {
        if (values.IsDefaultOrEmpty)
        {
            return ImmutableArray<string>.Empty;
        }

        var result = ImmutableArray.CreateBuilder<string>(values.Length);
        foreach (var value in values)
        {
            if (value is null)
            {
                throw new McpException("MCP arguments cannot be null.");
            }

            ValidateSafeText(value, "An MCP argument contains unsafe characters.");
            result.Add(value);
        }

        return result.MoveToImmutable();
    }

    private static ImmutableArray<string> NormalizeScopes(ImmutableArray<string> values)
    {
        if (values.IsDefaultOrEmpty)
        {
            return ImmutableArray<string>.Empty;
        }

        var result = ImmutableArray.CreateBuilder<string>(values.Length);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new McpException("OAuth scopes cannot be blank.");
            }

            ValidateSafeText(value, "An OAuth scope contains unsafe characters.");
            result.Add(value);
        }

        return result.MoveToImmutable();
    }

    private static ImmutableArray<McpDraftListItem> NormalizeArgumentItems(
        ImmutableArray<McpDraftListItem> values)
    {
        if (values.IsDefault)
        {
            return default;
        }

        var seen = new HashSet<Guid>();
        var result = ImmutableArray.CreateBuilder<McpDraftListItem>(values.Length);
        foreach (var item in values)
        {
            if (item is null || item.Id == Guid.Empty || !seen.Add(item.Id) || item.Value is null)
            {
                throw new McpException("MCP argument item identities must be unique and valid.");
            }

            ValidateSafeText(item.Value, "An MCP argument contains unsafe characters.");
            result.Add(item);
        }

        return result.MoveToImmutable();
    }

    private static ImmutableArray<McpDraftListItem> NormalizeScopeItems(
        ImmutableArray<McpDraftListItem> values)
    {
        if (values.IsDefault)
        {
            return default;
        }

        var seen = new HashSet<Guid>();
        var result = ImmutableArray.CreateBuilder<McpDraftListItem>(values.Length);
        foreach (var item in values)
        {
            if (item is null
                || item.Id == Guid.Empty
                || !seen.Add(item.Id)
                || string.IsNullOrWhiteSpace(item.Value))
            {
                throw new McpException("OAuth scope item identities must be unique, valid, and nonblank.");
            }

            ValidateSafeText(item.Value, "An OAuth scope contains unsafe characters.");
            result.Add(item);
        }

        return result.MoveToImmutable();
    }

    private static bool HasAuthoritativeItems(
        Guid draftId,
        ImmutableArray<McpDraftListItem> items) =>
        draftId != Guid.Empty && !items.IsDefault;

    private static ImmutableArray<McpNamedSecretDraft> NormalizeNamedSecrets(
        ImmutableArray<McpNamedSecretDraft> values,
        string fieldPrefix,
        string displayName,
        bool isAdd)
    {
        if (values.IsDefaultOrEmpty)
        {
            return ImmutableArray<McpNamedSecretDraft>.Empty;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<McpNamedSecretDraft>(values.Length);
        foreach (var named in values)
        {
            if (named is null || string.IsNullOrWhiteSpace(named.Name))
            {
                throw new McpException($"{displayName} names cannot be blank.");
            }

            ValidateSafeText(named.Name, $"{displayName} names contain unsafe characters.");
            if (!seen.Add(named.Name))
            {
                throw new McpException($"{displayName} names must be unique.");
            }

            if (!Enum.IsDefined(named.ExistingSource))
            {
                throw new McpException("An MCP secret source is not supported.");
            }

            var change = NormalizeSecretChange(
                named.Change,
                $"{fieldPrefix}/{named.Name}",
                isAdd,
                named.ExistingSource);
            normalized.Add(named with { Change = change });
        }

        return normalized.ToImmutableArray();
    }

    private static McpSecretChange NormalizeSecretChange(
        McpSecretChange? change,
        string expectedField,
        bool isAdd,
        McpSecretSource existingSource)
    {
        if (change is null || !string.Equals(change.Field, expectedField, StringComparison.Ordinal))
        {
            throw new McpException("An MCP secret field is invalid.");
        }

        if (!Enum.IsDefined(change.Kind))
        {
            throw new McpException("An MCP secret change is not supported.");
        }

        switch (change.Kind)
        {
            case McpSecretChangeKind.Replace when change.Replacement is null:
                throw new McpException("Replacing an MCP secret requires a replacement value.");
            case McpSecretChangeKind.Unchanged when change.Replacement is not null:
                throw new McpException("An unchanged MCP secret cannot contain a replacement value.");
            case McpSecretChangeKind.Remove when change.Replacement is not null:
                throw new McpException("Removing an MCP secret cannot contain a replacement value.");
            case McpSecretChangeKind.Unchanged
                when isAdd && existingSource != McpSecretSource.None:
                throw new McpException("New MCP secret values must be replaced rather than left unchanged.");
        }

        return change;
    }

    private static string NormalizeHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new McpException("HTTP MCP servers require an absolute http or https URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new McpException("HTTP MCP URLs cannot include user information.");
        }

        return uri.OriginalString;
    }

    private static string? NormalizeOptionalText(string? value, string unsafeMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        ValidateSafeText(value, unsafeMessage);
        return value;
    }

    private static void ValidateSafeText(string value, string unsafeMessage)
    {
        if (!IsWellFormedUtf16(value))
        {
            throw new McpException(unsafeMessage);
        }

        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                throw new McpException(unsafeMessage);
            }
        }
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }

    private static ImmutableArray<string> CreateScopeWarnings(
        IReadOnlyList<McpPhysicalServerEntry> entries,
        McpServerKey? original,
        McpServerDraft draft)
    {
        var warnings = ImmutableArray.CreateBuilder<string>();
        var oppositeScope = draft.Scope == McpConfigScope.Project
            ? McpConfigScope.User
            : McpConfigScope.Project;
        var targetIsNew = original is null
            || !string.Equals(original.Value.Name, draft.Name, StringComparison.Ordinal);
        if (targetIsNew
            && entries.Any(entry => entry.Key.Scope == oppositeScope
            && string.Equals(entry.Key.Name, draft.Name, StringComparison.Ordinal)))
        {
            var safeName = SanitizeIdentifier(draft.Name);
            warnings.Add(draft.Scope == McpConfigScope.Project
                ? $"The project MCP server '{safeName}' will override the user-scope server with the same name."
                : $"The user-scope MCP server '{safeName}' will be overridden by the project-scope server with the same name.");
        }

        if (original is { Scope: McpConfigScope.Project } oldKey
            && !string.Equals(oldKey.Name, draft.Name, StringComparison.Ordinal)
            && entries.Any(entry => entry.Key.Scope == McpConfigScope.User
                && string.Equals(entry.Key.Name, oldKey.Name, StringComparison.Ordinal)))
        {
            warnings.Add(
                $"Renaming '{SanitizeIdentifier(oldKey.Name)}' will reveal the user-scope server with that name.");
        }

        return warnings.ToImmutable();
    }

    private async Task<McpMutationResult> CommitAsync(
        McpEditPreview preview,
        bool isAdd,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(preview);
        await this.mutationGate.WaitAsync(ct).ConfigureAwait(false);
        McpMutationResult result;
        var notify = false;
        try
        {
            result = await this.CommitUnderGateAsync(preview, isAdd, ct).ConfigureAwait(false);
            if (result.Status is McpMutationStatus.Succeeded or McpMutationStatus.SavedWithRuntimeError)
            {
                this.completedOperations.Add(preview.OperationId);
                notify = true;
            }
        }
        finally
        {
            this.mutationGate.Release();
        }

        if (notify)
        {
            this.RaiseChanged();
        }

        return result;
    }

    private async Task<McpMutationResult> CommitUnderGateAsync(
        McpEditPreview preview,
        bool isAdd,
        CancellationToken ct)
    {
        var stagedKeys = new List<string>();
        var writeSucceeded = false;
        McpConfigMutationLease? lease = null;
        try
        {
            lease = await McpConfigMutationLease
                .AcquireAsync(this.workingDirectory, this.userMcpDir, ct)
                .ConfigureAwait(false);
            var currentRevision = CaptureRevision(this.workingDirectory, this.userMcpDir, ct);
            if (currentRevision != preview.Revision)
            {
                return await this.CreateRejectedResultAsync(
                    "MCP configuration changed after this edit was prepared. Review the current configuration and try again.")
                    .ConfigureAwait(false);
            }

            if (preview.OperationId == Guid.Empty || this.completedOperations.Contains(preview.OperationId))
            {
                return await this.CreateRejectedResultAsync(
                    "This MCP edit preview has already been committed or is invalid.")
                    .ConfigureAwait(false);
            }

            var entries = this.LoadPhysicalEntriesForMutation(ct);
            if (CaptureRevision(this.workingDirectory, this.userMcpDir, ct) != preview.Revision)
            {
                return await this.CreateRejectedResultAsync(
                    "MCP configuration changed after this edit was prepared. Review the current configuration and try again.")
                    .ConfigureAwait(false);
            }

            var draft = ValidateAndNormalizeDraft(preview.Draft, isAdd);
            this.ValidateScopeAvailable(draft.Scope);
            var original = ValidateCommitEntries(entries, preview, draft, isAdd);
            var before = isAdd
                ? null
                : this.LoadEffectiveConfigsForMutation();
            ValidateBearerTokenAvailability(draft, original?.Config);
            if (!isAdd && draft.BaseRevision != preview.Revision)
            {
                return await this.CreateRejectedResultAsync(
                    "This MCP edit preview is invalid or stale. Reopen the edit and try again.")
                    .ConfigureAwait(false);
            }

            var finalDraft = original is null
                ? draft
                : PreserveUnchangedNonSecretValues(
                    draft,
                    this.CreateDraft(original, currentRevision, draft.DraftId),
                    original.Config);
            var renamed = original is not null
                && !string.Equals(original.Key.Name, finalDraft.Name, StringComparison.Ordinal);
            var config = await this.BuildFinalConfigAsync(
                finalDraft,
                original?.Key.Name,
                original?.Config,
                renamed,
                stagedKeys,
                ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (CaptureRevision(this.workingDirectory, this.userMcpDir, ct) != preview.Revision)
            {
                return await this.CreateRejectedAfterStagedCleanupAsync(
                    "MCP configuration changed while this edit was being saved. Review the current configuration and try again.",
                    stagedKeys).ConfigureAwait(false);
            }

            if (isAdd)
            {
                this.UpsertWithExpectedRevision(
                    finalDraft.Scope,
                    finalDraft.Name,
                    config,
                    disabled: false,
                    this.workingDirectory,
                    this.userMcpDir,
                    RevisionForScope(preview.Revision, finalDraft.Scope));
            }
            else
            {
                this.ReplaceWithExpectedRevision(
                    finalDraft.Scope,
                    original!.Key.Name,
                    finalDraft.Name,
                    config,
                    disabled: !finalDraft.Enabled,
                    this.workingDirectory,
                    this.userMcpDir,
                    RevisionForScope(preview.Revision, finalDraft.Scope));
            }

            writeSucceeded = true;
            var cleanupWarning = await this.DeleteUnreferencedOldSecretsAsync(
                original?.Key.Name,
                original?.Config).ConfigureAwait(false);
            var runtimeReconcile = EmptyRuntimeReconcileResult();
            IReadOnlyDictionary<string, McpServerConfig>? after = null;
            var finalDefinitionIsNewlyEffective = false;
            if (!isAdd)
            {
                try
                {
                    after = this.LoadEffectiveConfigsForMutation();
                    finalDefinitionIsNewlyEffective = !original!.IsEffective
                        && finalDraft.Enabled
                        && this.LoadPhysicalEntriesForMutation(CancellationToken.None).Any(
                            entry => entry.Key == new McpServerKey(finalDraft.Scope, finalDraft.Name)
                                && entry.IsEffective);
                }
                catch when (cleanupWarning is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    lease = null;
                    var failedSnapshot = await this.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                    return new McpMutationResult(
                        McpMutationStatus.Succeeded,
                        new McpServerKey(finalDraft.Scope, finalDraft.Name),
                        CreateSavedMessage(runtimeReconcile.Errors, cleanupWarning),
                        failedSnapshot);
                }
            }


            await lease.DisposeAsync().ConfigureAwait(false);
            lease = null;
            if (!isAdd)
            {
                runtimeReconcile = await this.ReconcileAsync(
                    before!,
                    after!,
                    CreateTouchedNames(original!.Key.Name, finalDraft.Name),
                    original.IsEffective
                        ? CreateTouchedNames(original.Key.Name, finalDraft.Name)
                        : new HashSet<string>(StringComparer.Ordinal),
                    original.Config.Disabled && finalDraft.Enabled
                        ? CreateTouchedNames(original.Key.Name, finalDraft.Name)
                        : finalDefinitionIsNewlyEffective
                            ? CreateTouchedNames(original.Key.Name, finalDraft.Name)
                            : new HashSet<string>(StringComparer.Ordinal),
                    ct).ConfigureAwait(false);
            }

            var snapshot = await this.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            return new McpMutationResult(
                runtimeReconcile.Errors.IsDefaultOrEmpty
                    ? McpMutationStatus.Succeeded
                    : McpMutationStatus.SavedWithRuntimeError,
                new McpServerKey(finalDraft.Scope, finalDraft.Name),
                CreateSavedMessage(runtimeReconcile.Errors, cleanupWarning),
                snapshot);
        }
        catch (OperationCanceledException)
        {
            if (!writeSucceeded)
            {
                if (await this.CleanupStagedKeysAsync(stagedKeys).ConfigureAwait(false))
                {
                    throw new OperationCanceledException("MCP operation was cancelled; cleanup incomplete.", ct);
                }

                throw;
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                lease = null;
            }

            return await this.CreateSavedWithRuntimeErrorAsync(
                new McpServerKey(preview.Draft.Scope, preview.Draft.Name),
                "The saved MCP configuration could not be reconciled with the runtime.").ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!writeSucceeded)
            {
                return await this.CreateRejectedAfterStagedCleanupAsync(
                    "MCP server could not be saved. Review the configuration and try again.",
                    stagedKeys)
                    .ConfigureAwait(false);
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                lease = null;
            }

            return await this.CreateSavedWithRuntimeErrorAsync(
                new McpServerKey(preview.Draft.Scope, preview.Draft.Name),
                "The saved MCP configuration could not be reconciled with the runtime.").ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<McpMutationResult> CommitDeleteCoreAsync(
        McpDeletePreview preview,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(preview);
        await this.mutationGate.WaitAsync(ct).ConfigureAwait(false);
        McpMutationResult result;
        var notify = false;
        try
        {
            result = await this.CommitDeleteUnderGateAsync(preview, ct).ConfigureAwait(false);
            if (result.Status is McpMutationStatus.Succeeded or McpMutationStatus.SavedWithRuntimeError)
            {
                this.completedOperations.Add(preview.OperationId);
                notify = true;
            }
        }
        finally
        {
            this.mutationGate.Release();
        }

        if (notify)
        {
            this.RaiseChanged();
        }

        return result;
    }

    private async Task<McpMutationResult> CommitDeleteUnderGateAsync(
        McpDeletePreview preview,
        CancellationToken ct)
    {
        McpConfigMutationLease? lease = null;
        var writeSucceeded = false;
        try
        {
            this.ValidateScopeAvailable(preview.Key.Scope);
            lease = await McpConfigMutationLease
                .AcquireAsync(this.workingDirectory, this.userMcpDir, ct)
                .ConfigureAwait(false);
            if (preview.OperationId == Guid.Empty || this.completedOperations.Contains(preview.OperationId))
            {
                return await this.CreateRejectedResultAsync(
                    "This MCP delete confirmation has already been committed or is invalid.").ConfigureAwait(false);
            }

            if (CaptureRevision(this.workingDirectory, this.userMcpDir, ct) != preview.Revision)
            {
                return await this.CreateRejectedResultAsync(
                    "MCP configuration changed after this delete was prepared. Review the current configuration and try again.")
                    .ConfigureAwait(false);
            }

            var entries = this.LoadPhysicalEntriesForMutation(ct);
            var entry = entries.FirstOrDefault(candidate => candidate.Key == preview.Key);
            if (entry is null)
            {
                return await this.CreateRejectedResultAsync(
                    "The selected MCP server no longer exists in the selected scope.").ConfigureAwait(false);
            }

            var before = this.LoadEffectiveConfigsForMutation();
            if (CaptureRevision(this.workingDirectory, this.userMcpDir, ct) != preview.Revision)
            {
                return await this.CreateRejectedResultAsync(
                    "MCP configuration changed while this delete was being saved. Review the current configuration and try again.")
                    .ConfigureAwait(false);
            }

            if (!this.RemoveWithExpectedRevision(
                    preview.Key.Scope,
                    preview.Key.Name,
                    this.workingDirectory,
                    this.userMcpDir,
                    RevisionForScope(preview.Revision, preview.Key.Scope)))
            {
                return await this.CreateRejectedResultAsync(
                    "The selected MCP server no longer exists in the selected scope.").ConfigureAwait(false);
            }

            writeSucceeded = true;
            var cleanupWarning = await this.DeleteUnreferencedOldSecretsAsync(
                preview.Key.Name,
                entry.Config).ConfigureAwait(false);
            IReadOnlyDictionary<string, McpServerConfig>? after;
            IReadOnlyList<McpPhysicalServerEntry>? afterEntries;
            try
            {
                after = this.LoadEffectiveConfigsForMutation();
                afterEntries = this.LoadPhysicalEntriesForMutation(CancellationToken.None);
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                lease = null;
                var snapshot = await this.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                return new McpMutationResult(
                    McpMutationStatus.Succeeded,
                    null,
                    CreateSavedMessage(EmptyRuntimeReconcileResult().Errors, cleanupWarning
                        ?? "Prior MCP credentials were retained because post-save cleanup could not be verified."),
                    snapshot);
            }

            var selected = SelectNearestKey(entries, afterEntries, preview.Key);
            await lease.DisposeAsync().ConfigureAwait(false);
            lease = null;
            var forceStart = after.ContainsKey(preview.Key.Name)
                ? new HashSet<string>(StringComparer.Ordinal) { preview.Key.Name }
                : new HashSet<string>(StringComparer.Ordinal);
            var reconcile = await this.ReconcileAsync(
                before,
                after,
                new HashSet<string>(StringComparer.Ordinal) { preview.Key.Name },
                new HashSet<string>(StringComparer.Ordinal),
                forceStart,
                ct).ConfigureAwait(false);
            var refreshed = await this.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            return new McpMutationResult(
                reconcile.Errors.IsDefaultOrEmpty
                    ? McpMutationStatus.Succeeded
                    : McpMutationStatus.SavedWithRuntimeError,
                selected,
                CreateSavedMessage(reconcile.Errors, cleanupWarning),
                refreshed);
        }
        catch (OperationCanceledException)
        {
            if (!writeSucceeded)
            {
                throw;
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                lease = null;
            }

            return await this.CreateSavedWithRuntimeErrorAsync(
                preview.Key,
                "The saved MCP configuration could not be reconciled with the runtime.").ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!writeSucceeded)
            {
                return await this.CreateRejectedResultAsync(
                    "MCP server could not be deleted. Review the configuration and try again.").ConfigureAwait(false);
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                lease = null;
            }

            return await this.CreateSavedWithRuntimeErrorAsync(
                preview.Key,
                "The saved MCP configuration could not be reconciled with the runtime.").ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<McpMutationResult> ReauthenticateCoreAsync(
        McpReauthenticationPlan plan,
        IReadOnlyDictionary<string, McpSecretReplacement> replacements,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(replacements);
        await this.mutationGate.WaitAsync(ct).ConfigureAwait(false);
        McpMutationResult result;
        var notify = false;
        try
        {
            result = await this.ReauthenticateUnderGateAsync(plan, replacements, ct).ConfigureAwait(false);
            if (result.Status is McpMutationStatus.Succeeded or McpMutationStatus.SavedWithRuntimeError)
            {
                this.completedOperations.Add(plan.OperationId);
                notify = true;
            }
        }
        finally
        {
            this.mutationGate.Release();
        }

        if (notify)
        {
            this.RaiseChanged();
        }

        return result;
    }

    private async Task<McpMutationResult> ReauthenticateUnderGateAsync(
        McpReauthenticationPlan plan,
        IReadOnlyDictionary<string, McpSecretReplacement> replacements,
        CancellationToken ct)
    {
        McpConfigMutationLease? lease = null;
        var stagedKeys = new List<string>();
        var writeSucceeded = false;
        try
        {
            this.ValidateScopeAvailable(plan.Key.Scope);
            lease = await McpConfigMutationLease
                .AcquireAsync(this.workingDirectory, this.userMcpDir, ct)
                .ConfigureAwait(false);
            if (plan.OperationId == Guid.Empty || this.completedOperations.Contains(plan.OperationId))
            {
                return await this.CreateRejectedResultAsync(
                    "This MCP reauthentication confirmation has already been committed or is invalid.").ConfigureAwait(false);
            }

            if (CaptureRevision(this.workingDirectory, this.userMcpDir, ct) != plan.Revision)
            {
                return await this.CreateRejectedResultAsync(
                    "MCP configuration changed after reauthentication was prepared. Review the current configuration and try again.")
                    .ConfigureAwait(false);
            }

            var entries = this.LoadPhysicalEntriesForMutation(ct);
            var entry = entries.FirstOrDefault(candidate => candidate.Key == plan.Key);
            if (entry?.Config is not McpHttpServerConfig http)
            {
                return await this.CreateRejectedResultAsync(
                    "The selected MCP server no longer supports HTTP reauthentication.").ConfigureAwait(false);
            }

            if (plan.Kind == McpReauthenticationKind.EnvironmentOwned)
            {
                return await this.CreateRejectedResultAsync(
                    "This MCP server uses environment-owned credentials. Update the referenced environment variable instead.")
                    .ConfigureAwait(false);
            }

            if (plan.Kind == McpReauthenticationKind.Unavailable)
            {
                return await this.CreateRejectedResultAsync(
                    plan.DisabledReason ?? "This MCP server cannot be reauthenticated from this screen.")
                    .ConfigureAwait(false);
            }

            var before = this.LoadEffectiveConfigsForMutation();
            if (plan.Kind == McpReauthenticationKind.OAuth)
            {
                if (http.Auth.Mode != McpAuthMode.OAuth)
                {
                    return await this.CreateRejectedResultAsync(
                        "MCP authentication changed after reauthentication was prepared. Review the current configuration and try again.")
                        .ConfigureAwait(false);
                }

                await lease.DisposeAsync().ConfigureAwait(false);
                lease = null;
                var authResult = await this.oauth.ReauthenticateAsync(http, ct).ConfigureAwait(false);
                if (!authResult.Succeeded)
                {
                    return await this.CreateRejectedResultAsync(
                        authResult.Error ?? "MCP OAuth reauthentication did not complete.").ConfigureAwait(false);
                }

                lease = await McpConfigMutationLease
                    .AcquireAsync(this.workingDirectory, this.userMcpDir, ct)
                    .ConfigureAwait(false);
                var revalidationRevision = CaptureRevision(this.workingDirectory, this.userMcpDir, ct);
                var revalidationEntries = this.LoadPhysicalEntriesForMutation(ct);
                var revalidated = revalidationEntries.FirstOrDefault(candidate => candidate.Key == plan.Key);
                var after = this.LoadEffectiveConfigsForMutation();
                if (revalidationRevision
                    != CaptureRevision(this.workingDirectory, this.userMcpDir, ct)
                    || revalidated?.Config is not McpHttpServerConfig revalidatedHttp
                    || revalidatedHttp.Auth.Mode != McpAuthMode.OAuth
                    || plan.OAuthCanonicalResource is null
                    || !string.Equals(
                        plan.OAuthCanonicalResource,
                        CanonicalResourceUri.From(revalidatedHttp.Url),
                        StringComparison.Ordinal)
                    || !EffectiveConfigsEqual(entry.Config, revalidated.Config))
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    lease = null;
                    return await this.CreateOAuthTokenSavedConfigurationChangedResultAsync(plan.Key)
                        .ConfigureAwait(false);
                }

                await lease.DisposeAsync().ConfigureAwait(false);
                lease = null;
                return await this.ReconcileReauthenticatedServerAsync(
                    plan.Key,
                    revalidated.IsEffective && !revalidatedHttp.Disabled,
                    before,
                    ct,
                    after: after).ConfigureAwait(false);
            }

            if (plan.Kind != McpReauthenticationKind.StoredSecret
                || !ReplacementSetMatches(plan.ManagedFields, replacements))
            {
                return await this.CreateRejectedResultAsync(
                    "Provide a masked replacement value for every managed MCP credential.").ConfigureAwait(false);
            }

            var currentFields = OwnedSecretReferences(plan.Key.Name, http)
                .Select(static binding => binding.Field)
                .OrderBy(static field => field, StringComparer.Ordinal)
                .ToImmutableArray();
            if (!currentFields.SequenceEqual(plan.ManagedFields, StringComparer.Ordinal)
                || HasEnvironmentCredentials(http))
            {
                return await this.CreateRejectedResultAsync(
                    "MCP credentials changed after reauthentication was prepared. Review the current configuration and try again.")
                    .ConfigureAwait(false);
            }

            var references = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in plan.ManagedFields)
            {
                references.Add(
                    field,
                    await this.StageReplacementAsync(
                        replacements[field],
                        plan.Key.Name,
                        field,
                        stagedKeys,
                        ct).ConfigureAwait(false));
            }

            if (CaptureRevision(this.workingDirectory, this.userMcpDir, ct) != plan.Revision)
            {
                return await this.CreateRejectedAfterStagedCleanupAsync(
                    "MCP configuration changed while reauthentication was being saved. Review the current configuration and try again.",
                    stagedKeys).ConfigureAwait(false);
            }

            var updated = ReplaceManagedReferences(http, references);
            this.ReplaceWithExpectedRevision(
                plan.Key.Scope,
                plan.Key.Name,
                plan.Key.Name,
                updated,
                disabled: updated.Disabled,
                this.workingDirectory,
                this.userMcpDir,
                RevisionForScope(plan.Revision, plan.Key.Scope));
            writeSucceeded = true;
            var cleanupWarning = await this.DeleteUnreferencedOldSecretsAsync(
                plan.Key.Name,
                http).ConfigureAwait(false);

            await lease.DisposeAsync().ConfigureAwait(false);
            lease = null;
            return await this.ReconcileReauthenticatedServerAsync(
                plan.Key,
                entry.IsEffective,
                before,
                ct,
                cleanupWarning).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!writeSucceeded)
            {
                if (await this.CleanupStagedKeysAsync(stagedKeys).ConfigureAwait(false))
                {
                    throw new OperationCanceledException("MCP operation was cancelled; cleanup incomplete.", ct);
                }

                throw;
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                lease = null;
            }

            return await this.CreateSavedWithRuntimeErrorAsync(
                plan.Key,
                "The saved MCP configuration could not be reconciled with the runtime.").ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!writeSucceeded)
            {
                return await this.CreateRejectedAfterStagedCleanupAsync(
                    "MCP credentials could not be saved. Review the configuration and try again.",
                    stagedKeys).ConfigureAwait(false);
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                lease = null;
            }

            return await this.CreateSavedWithRuntimeErrorAsync(
                plan.Key,
                "The saved MCP configuration could not be reconciled with the runtime.").ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }


    private static McpPhysicalServerEntry? ValidateCommitEntries(
        IReadOnlyList<McpPhysicalServerEntry> entries,
        McpEditPreview preview,
        McpServerDraft draft,
        bool isAdd)
    {
        var target = new McpServerKey(draft.Scope, draft.Name);
        if (isAdd)
        {
            if (preview.OriginalKey is not null)
            {
                throw new McpException("An add preview cannot contain an original MCP server.");
            }

            if (entries.Any(entry => entry.Key == target))
            {
                throw new McpException("An MCP server with this name already exists in the selected scope.");
            }

            return null;
        }

        if (preview.OriginalKey is not { } originalKey)
        {
            throw new McpException("An edit preview must identify the original MCP server.");
        }

        if (originalKey.Scope != draft.Scope)
        {
            throw new McpException("An MCP edit cannot change configuration scope.");
        }

        var original = entries.FirstOrDefault(entry => entry.Key == originalKey);
        if (original is null)
        {
            throw new McpException("The selected MCP server no longer exists in the selected scope.");
        }

        if (target != originalKey && entries.Any(entry => entry.Key == target))
        {
            throw new McpException("An MCP server with this name already exists in the selected scope.");
        }

        return original;
    }

    private async Task<McpMutationResult> ReconcileReauthenticatedServerAsync(
        McpServerKey key,
        bool wasEffective,
        IReadOnlyDictionary<string, McpServerConfig> before,
        CancellationToken ct,
        string? cleanupWarning = null,
        IReadOnlyDictionary<string, McpServerConfig>? after = null)
    {
        after ??= this.LoadEffectiveConfigsForMutation();
        var shouldReconnect = wasEffective && after.ContainsKey(key.Name);
        var names = new HashSet<string>(StringComparer.Ordinal) { key.Name };
        var reconcile = await this.ReconcileAsync(
            before,
            after,
            names,
            shouldReconnect ? names : new HashSet<string>(StringComparer.Ordinal),
            shouldReconnect ? names : new HashSet<string>(StringComparer.Ordinal),
            ct).ConfigureAwait(false);
        var snapshot = await this.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        return new McpMutationResult(
            reconcile.Errors.IsDefaultOrEmpty
                ? McpMutationStatus.Succeeded
                : McpMutationStatus.SavedWithRuntimeError,
            key,
            CreateSavedMessage(reconcile.Errors, cleanupWarning),
            snapshot);
    }

    private static bool ReplacementSetMatches(
        ImmutableArray<string> fields,
        IReadOnlyDictionary<string, McpSecretReplacement> replacements)
    {
        if (fields.Length != replacements.Count)
        {
            return false;
        }

        return fields.All(field =>
            replacements.TryGetValue(field, out var replacement) && replacement is not null);
    }

    private static bool HasEnvironmentCredentials(McpHttpServerConfig config) =>
        config.Headers.Values.Any(value => ClassifySecret(value) == McpSecretSource.Environment)
        || ClassifySecret(config.Auth.BearerToken) == McpSecretSource.Environment;

    private static bool HasLiteralCredentials(McpHttpServerConfig config) =>
        config.Headers.Values.Any(value => ClassifySecret(value) == McpSecretSource.Literal)
        || ClassifySecret(config.Auth.BearerToken) == McpSecretSource.Literal;

    private static McpHttpServerConfig ReplaceManagedReferences(
        McpHttpServerConfig config,
        IReadOnlyDictionary<string, string> references)
    {
        var headers = new Dictionary<string, string>(config.Headers, StringComparer.Ordinal);
        foreach (var (field, reference) in references)
        {
            if (field.StartsWith("header/", StringComparison.Ordinal))
            {
                headers[field["header/".Length..]] = reference;
            }
        }

        var bearer = references.TryGetValue("auth/token", out var bearerReference)
            ? bearerReference
            : config.Auth.BearerToken;
        return new McpHttpServerConfig(
            config.Url,
            headers,
            new McpAuthConfig(
                config.Auth.Mode,
                config.Auth.ClientId,
                config.Auth.Scopes,
                bearer))
        {
            Disabled = config.Disabled,
        };
    }

    private static McpServerKey? SelectNearestKey(
        IReadOnlyList<McpPhysicalServerEntry> before,
        IReadOnlyList<McpPhysicalServerEntry> after,
        McpServerKey deleted)
    {
        if (after.Count == 0)
        {
            return null;
        }

        var deletedIndex = before
            .Select(static entry => entry.Key)
            .ToList()
            .IndexOf(deleted);
        return after[Math.Clamp(deletedIndex, 0, after.Count - 1)].Key;
    }

    private void UpsertWithExpectedRevision(
        McpConfigScope scope,
        string name,
        McpServerConfig config,
        bool disabled,
        string workingDirectory,
        string? userDirectory,
        string expectedRevision)
    {
        if (this.configMutator is IRevisionedMcpConfigMutator revisioned)
        {
            revisioned.Upsert(
                scope,
                name,
                config,
                disabled,
                workingDirectory,
                userDirectory,
                expectedRevision);
            return;
        }

        this.configMutator.Upsert(scope, name, config, disabled, workingDirectory, userDirectory);
    }

    private void ReplaceWithExpectedRevision(
        McpConfigScope scope,
        string currentName,
        string newName,
        McpServerConfig config,
        bool disabled,
        string workingDirectory,
        string? userDirectory,
        string expectedRevision)
    {
        if (this.configMutator is IRevisionedMcpConfigMutator revisioned)
        {
            revisioned.ReplaceEntry(
                scope,
                currentName,
                newName,
                config,
                disabled,
                workingDirectory,
                userDirectory,
                expectedRevision);
            return;
        }

        this.configMutator.ReplaceEntry(
            scope,
            currentName,
            newName,
            config,
            disabled,
            workingDirectory,
            userDirectory);
    }

    private async Task<McpMutationResult> SetEnabledCoreAsync(
        McpServerKey key,
        bool enabled,
        CancellationToken ct)
    {
        await this.mutationGate.WaitAsync(ct).ConfigureAwait(false);
        McpMutationResult result;
        var notify = false;
        try
        {
            result = await this.SetEnabledUnderGateAsync(key, enabled, ct).ConfigureAwait(false);
            notify = result.Status is McpMutationStatus.Succeeded or McpMutationStatus.SavedWithRuntimeError;
        }
        finally
        {
            this.mutationGate.Release();
        }

        if (notify)
        {
            this.RaiseChanged();
        }

        return result;
    }

    private async Task<McpMutationResult> SetEnabledUnderGateAsync(
        McpServerKey key,
        bool enabled,
        CancellationToken ct)
    {
        McpConfigMutationLease? lease = null;
        var writeSucceeded = false;
        try
        {
            this.ValidateScopeAvailable(key.Scope);
            lease = await McpConfigMutationLease
                .AcquireAsync(this.workingDirectory, this.userMcpDir, ct)
                .ConfigureAwait(false);
            var revision = CaptureRevision(this.workingDirectory, this.userMcpDir, ct);
            var entries = this.LoadPhysicalEntriesForMutation(ct);
            if (CaptureRevision(this.workingDirectory, this.userMcpDir, ct) != revision)
            {
                return await this.CreateRejectedResultAsync(
                    "MCP configuration changed while this update was being prepared. Review the current configuration and try again.")
                    .ConfigureAwait(false);
            }

            var entry = entries.FirstOrDefault(candidate => candidate.Key == key);
            if (entry is null)
            {
                return await this.CreateRejectedResultAsync(
                    "The selected MCP server no longer exists in the selected scope.")
                    .ConfigureAwait(false);
            }

            if (entry.Config.Disabled == !enabled)
            {
                var unchanged = await this.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                return new McpMutationResult(
                    McpMutationStatus.NoOp,
                    key,
                    "MCP server is already in the requested state.",
                    unchanged);
            }

            var before = this.LoadEffectiveConfigsForMutation();
            if (CaptureRevision(this.workingDirectory, this.userMcpDir, ct) != revision)
            {
                return await this.CreateRejectedResultAsync(
                    "MCP configuration changed while this update was being saved. Review the current configuration and try again.")
                    .ConfigureAwait(false);
            }

            this.SetDisabledWithExpectedRevision(
                key.Scope,
                key.Name,
                disabled: !enabled,
                this.workingDirectory,
                this.userMcpDir,
                RevisionForScope(revision, key.Scope));
            writeSucceeded = true;
            var after = this.LoadEffectiveConfigsForMutation();
            await lease.DisposeAsync().ConfigureAwait(false);
            lease = null;
            var reconcile = await this.ReconcileAsync(
                before,
                after,
                new HashSet<string>(StringComparer.Ordinal) { key.Name },
                new HashSet<string>(StringComparer.Ordinal),
                enabled
                    ? new HashSet<string>(StringComparer.Ordinal) { key.Name }
                    : new HashSet<string>(StringComparer.Ordinal),
                ct).ConfigureAwait(false);
            var snapshot = await this.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            return new McpMutationResult(
                reconcile.Errors.IsDefaultOrEmpty
                    ? McpMutationStatus.Succeeded
                    : McpMutationStatus.SavedWithRuntimeError,
                key,
                CreateSavedMessage(reconcile.Errors, cleanupWarning: null),
                snapshot);
        }
        catch (OperationCanceledException)
        {
            if (!writeSucceeded)
            {
                throw;
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                lease = null;
            }

            return await this.CreateSavedWithRuntimeErrorAsync(
                key,
                "The saved MCP configuration could not be reconciled with the runtime.").ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (writeSucceeded)
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    lease = null;
                }

                return await this.CreateSavedWithRuntimeErrorAsync(
                    key,
                    "The saved MCP configuration could not be reconciled with the runtime.").ConfigureAwait(false);
            }

            return await this.CreateRejectedResultAsync(
                "MCP server could not be saved. Review the configuration and try again.")
                .ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void SetDisabledWithExpectedRevision(
        McpConfigScope scope,
        string name,
        bool disabled,
        string workingDirectory,
        string? userDirectory,
        string expectedRevision)
    {
        if (this.configMutator is IRevisionedMcpConfigMutator revisioned)
        {
            _ = revisioned.SetDisabled(
                scope,
                name,
                disabled,
                workingDirectory,
                userDirectory,
                expectedRevision);
            return;
        }

        _ = this.configMutator.SetDisabled(
            scope,
            name,
            disabled,
            workingDirectory,
            userDirectory);
    }

    private IReadOnlyDictionary<string, McpServerConfig> LoadEffectiveConfigsForMutation()
    {
        this.ValidatePhysicalConfiguration(CancellationToken.None);
        return McpConfig.Load(this.workingDirectory, this.userMcpDir);
    }

    private bool RemoveWithExpectedRevision(
        McpConfigScope scope,
        string name,
        string workingDirectory,
        string? userDirectory,
        string expectedRevision)
    {
        if (this.configMutator is IRevisionedMcpConfigMutator revisioned)
        {
        return revisioned.Remove(
            scope,
            name,
            workingDirectory,
            userDirectory,
            expectedRevision);
        }

        return this.configMutator.Remove(scope, name, workingDirectory, userDirectory);
    }

    private async Task<McpRuntimeReconcileResult> ReconcileAsync(
        IReadOnlyDictionary<string, McpServerConfig> before,
        IReadOnlyDictionary<string, McpServerConfig> after,
        IReadOnlySet<string> touchedNames,
        IReadOnlySet<string> forceRestartNames,
        IReadOnlySet<string> forceStartNames,
        CancellationToken ct)
    {
        if (this.runtime is null)
        {
            return EmptyRuntimeReconcileResult();
        }

        var stopped = ImmutableArray.CreateBuilder<string>();
        var started = ImmutableArray.CreateBuilder<string>();
        var errors = ImmutableArray.CreateBuilder<string>();
        var names = touchedNames.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        var runtimeHasIntent = names.ToDictionary(
            static name => name,
            name => this.runtime.IsServerConnected(name)
                || RuntimeErrorFor(this.runtime, name) is not null,
            StringComparer.Ordinal);
        var forcedRuntimeHasIntent = forceRestartNames.Any(
            name => runtimeHasIntent.GetValueOrDefault(name));

        try
        {
            foreach (var name in names)
            {
                before.TryGetValue(name, out var oldConfig);
                after.TryGetValue(name, out var newConfig);
                if (oldConfig is not null
                    && (newConfig is null
                        || !EffectiveConfigsEqual(oldConfig, newConfig)
                        || forceRestartNames.Contains(name)))
                {
                    if (await this.runtime.DisconnectServerAsync(name).ConfigureAwait(false))
                    {
                        stopped.Add(name);
                    }
                }
            }

            foreach (var name in names)
            {
                before.TryGetValue(name, out var oldConfig);
                after.TryGetValue(name, out var newConfig);
                if (newConfig is null
                    || (oldConfig is not null
                        && EffectiveConfigsEqual(oldConfig, newConfig)
                        && !forceRestartNames.Contains(name)))
                {
                    continue;
                }

                var shouldStart = forceStartNames.Contains(name)
                    || runtimeHasIntent.GetValueOrDefault(name)
                    || (forceRestartNames.Contains(name) && forcedRuntimeHasIntent);
                if (!shouldStart)
                {
                    continue;
                }

                try
                {
                    var resolved = await McpSecretResolver
                        .ResolveAsync(newConfig, this.credentials, ct)
                        .ConfigureAwait(false);
                    var result = await this.runtime
                        .ConnectServerAsync(name, resolved, ct)
                        .ConfigureAwait(false);
                    if (result.Connected)
                    {
                        started.Add(name);
                    }
                    else
                    {
                        errors.Add(SanitizeError(result.Error));
                        if (ct.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    errors.Add("The runtime update was cancelled.");
                    break;
                }
                catch (Exception exception)
                {
                    errors.Add(SanitizeError(exception.Message));
                }
            }
        }
        finally
        {
            this.events.Publish(new McpRuntimeChangedEvent(this.runtime.GetSnapshot()));
        }

        return new McpRuntimeReconcileResult(
            stopped.ToImmutable(),
            started.ToImmutable(),
            errors.ToImmutable());
    }

    private static HashSet<string> CreateTouchedNames(string originalName, string finalName) =>
        new(StringComparer.Ordinal) { originalName, finalName };

    private static bool EffectiveConfigsEqual(McpServerConfig left, McpServerConfig right) =>
        (left, right) switch
        {
            (McpStdioServerConfig leftStdio, McpStdioServerConfig rightStdio) =>
                leftStdio.Disabled == rightStdio.Disabled
                && string.Equals(leftStdio.Command, rightStdio.Command, StringComparison.Ordinal)
                && leftStdio.Args.SequenceEqual(rightStdio.Args, StringComparer.Ordinal)
                && StringMapsEqual(leftStdio.Env, rightStdio.Env),
            (McpHttpServerConfig leftHttp, McpHttpServerConfig rightHttp) =>
                leftHttp.Disabled == rightHttp.Disabled
                && string.Equals(leftHttp.Url.OriginalString, rightHttp.Url.OriginalString, StringComparison.Ordinal)
                && StringMapsEqual(leftHttp.Headers, rightHttp.Headers)
                && leftHttp.Auth.Mode == rightHttp.Auth.Mode
                && string.Equals(leftHttp.Auth.ClientId, rightHttp.Auth.ClientId, StringComparison.Ordinal)
                && string.Equals(leftHttp.Auth.BearerToken, rightHttp.Auth.BearerToken, StringComparison.Ordinal)
                && (leftHttp.Auth.Scopes ?? []).SequenceEqual(rightHttp.Auth.Scopes ?? [], StringComparer.Ordinal),
            _ => false,
        };

    private static bool StringMapsEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(pair =>
            right.TryGetValue(pair.Key, out var rightValue)
            && string.Equals(pair.Value, rightValue, StringComparison.Ordinal));

    private static McpRuntimeReconcileResult EmptyRuntimeReconcileResult() =>
        new([], [], []);

    private static string CreateSavedMessage(
        ImmutableArray<string> runtimeErrors,
        string? cleanupWarning)
    {
        var message = runtimeErrors.IsDefaultOrEmpty
            ? "MCP server saved."
            : $"MCP server saved, but the runtime could not be updated: {runtimeErrors[0]}";
        return cleanupWarning is null ? message : $"{message} Warning: {cleanupWarning}";
    }

    private static string RevisionForScope(McpConfigRevision revision, McpConfigScope scope) =>
        scope switch
        {
            McpConfigScope.User => revision.UserSha256,
            McpConfigScope.Project => revision.ProjectSha256,
            _ => throw new McpException("The selected MCP configuration scope is not supported."),
        };

    private async Task<McpServerConfig> BuildFinalConfigAsync(
        McpServerDraft draft,
        string? originalServerName,
        McpServerConfig? original,
        bool renamed,
        ICollection<string> stagedKeys,
        CancellationToken ct)
    {
        return draft.Transport switch
        {
            McpTransportKind.Stdio => new McpStdioServerConfig(
                draft.Command!,
                draft.Args,
                await this.BuildNamedSecretValuesAsync(
                    draft.Environment,
                    (original as McpStdioServerConfig)?.Env,
                    "env",
                    originalServerName ?? draft.Name,
                    draft.Name,
                    renamed,
                    stagedKeys,
                    ct).ConfigureAwait(false))
            {
                Disabled = !draft.Enabled,
            },
            McpTransportKind.Http => await this.BuildHttpConfigAsync(
                draft,
                originalServerName ?? draft.Name,
                original as McpHttpServerConfig,
                renamed,
                stagedKeys,
                ct).ConfigureAwait(false),
            _ => throw new McpException("The selected MCP transport is not supported."),
        };
    }

    private async Task<McpHttpServerConfig> BuildHttpConfigAsync(
        McpServerDraft draft,
        string originalServerName,
        McpHttpServerConfig? original,
        bool renamed,
        ICollection<string> stagedKeys,
        CancellationToken ct)
    {
        var headers = await this.BuildNamedSecretValuesAsync(
            draft.Headers,
            original?.Headers,
            "header",
            originalServerName,
            draft.Name,
            renamed,
            stagedKeys,
            ct).ConfigureAwait(false);
        var bearer = await this.BuildBearerTokenAsync(
            draft,
            original?.Auth.BearerToken,
            originalServerName,
            renamed,
            stagedKeys,
            ct).ConfigureAwait(false);
        var auth = new McpAuthConfig(
            draft.AuthMode,
            draft.ClientId,
            draft.Scopes.IsDefaultOrEmpty ? null : draft.Scopes,
            bearer);
        return new McpHttpServerConfig(new Uri(draft.Url!, UriKind.Absolute), headers, auth)
        {
            Disabled = !draft.Enabled,
        };
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildNamedSecretValuesAsync(
        ImmutableArray<McpNamedSecretDraft> drafts,
        IReadOnlyDictionary<string, string>? original,
        string fieldPrefix,
        string originalServerName,
        string finalServerName,
        bool renamed,
        ICollection<string> stagedKeys,
        CancellationToken ct)
    {
        var originalValues = original ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var draftByName = drafts.ToDictionary(draft => draft.Name, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var draft in drafts)
        {
            var name = draft.Name;
            var hasOriginal = originalValues.TryGetValue(name, out var originalValue);
            var value = await this.ResolveNamedSecretValueAsync(
                draft,
                hasOriginal,
                originalValue,
                $"{fieldPrefix}/{name}",
                originalServerName,
                finalServerName,
                renamed,
                stagedKeys,
                ct).ConfigureAwait(false);
            if (value is not null)
            {
                result.Add(name, value);
            }
        }

        foreach (var (name, originalValue) in originalValues)
        {
            if (draftByName.ContainsKey(name))
            {
                continue;
            }

            var value = await this.ResolveNamedSecretValueAsync(
                null,
                hasOriginal: true,
                originalValue,
                $"{fieldPrefix}/{name}",
                originalServerName,
                finalServerName,
                renamed,
                stagedKeys,
                ct).ConfigureAwait(false);
            if (value is not null)
            {
                result.Add(name, value);
            }
        }

        return result;
    }

    private async Task<string?> ResolveNamedSecretValueAsync(
        McpNamedSecretDraft? draft,
        bool hasOriginal,
        string? originalValue,
        string field,
        string originalServerName,
        string finalServerName,
        bool renamed,
        ICollection<string> stagedKeys,
        CancellationToken ct)
    {
        if (draft is null)
        {
            return hasOriginal
                ? await this.PreserveRawSecretValueAsync(
                    originalValue!,
                    field,
                    originalServerName,
                    finalServerName,
                    renamed,
                    stagedKeys,
                    ct).ConfigureAwait(false)
                : null;
        }

        return draft.Change.Kind switch
        {
            McpSecretChangeKind.Remove => null,
            McpSecretChangeKind.Replace => await this.StageReplacementAsync(
                draft.Change.Replacement!,
                finalServerName,
                field,
                stagedKeys,
                ct).ConfigureAwait(false),
            McpSecretChangeKind.Unchanged when !hasOriginal && draft.ExistingSource == McpSecretSource.None => null,
            McpSecretChangeKind.Unchanged when !hasOriginal => throw new McpException(
                "An unchanged MCP secret no longer exists in the current configuration."),
            McpSecretChangeKind.Unchanged when ClassifySecret(originalValue!) != draft.ExistingSource => throw new McpException(
                "An MCP secret changed outside this edit. Review the current configuration and try again."),
            McpSecretChangeKind.Unchanged => await this.PreserveRawSecretValueAsync(
                originalValue!,
                field,
                originalServerName,
                finalServerName,
                renamed,
                stagedKeys,
                ct).ConfigureAwait(false),
            _ => throw new McpException("An MCP secret change is not supported."),
        };
    }

    private async Task<string?> BuildBearerTokenAsync(
        McpServerDraft draft,
        string? originalValue,
        string originalServerName,
        bool renamed,
        ICollection<string> stagedKeys,
        CancellationToken ct)
    {
        if (draft.AuthMode == McpAuthMode.None)
        {
            return null;
        }

        string? value = draft.BearerToken.Kind switch
        {
            McpSecretChangeKind.Remove => null,
            McpSecretChangeKind.Replace => await this.StageReplacementAsync(
                draft.BearerToken.Replacement!,
                draft.Name,
                "auth/token",
                stagedKeys,
                ct).ConfigureAwait(false),
            McpSecretChangeKind.Unchanged when originalValue is null => null,
            McpSecretChangeKind.Unchanged => await this.PreserveRawSecretValueAsync(
                originalValue!,
                "auth/token",
                originalServerName,
                draft.Name,
                renamed,
                stagedKeys,
                ct).ConfigureAwait(false),
            _ => throw new McpException("An MCP secret change is not supported."),
        };

        if (draft.AuthMode == McpAuthMode.Bearer && value is null)
        {
            throw new McpException("Bearer authentication requires a token.");
        }

        return value;
    }

    private async Task<string> PreserveRawSecretValueAsync(
        string rawValue,
        string field,
        string originalServerName,
        string finalServerName,
        bool renamed,
        ICollection<string> stagedKeys,
        CancellationToken ct)
    {
        if (!renamed
            || !McpSecretStore.TryGetStoreKey(rawValue, out var oldStoreKey)
            || !McpSecretStore.IsOwnedKey(originalServerName, field, oldStoreKey))
        {
            return rawValue;
        }

        string? secret = await this.credentials.GetAsync(oldStoreKey, ct).ConfigureAwait(false);
        if (secret is null)
        {
            throw new McpException(
                "A managed MCP secret is missing from the credential store. The configuration was not changed.");
        }

        try
        {
            var staged = await McpSecretStore.StageAsync(
                this.credentials,
                finalServerName,
                field,
                secret,
                ct,
                staged => stagedKeys.Add(staged.StoreKey)).ConfigureAwait(false);
            return staged.Reference;
        }
        finally
        {
            secret = null;
        }
    }

    private async Task<string> StageReplacementAsync(
        McpSecretReplacement replacement,
        string finalServerName,
        string field,
        ICollection<string> stagedKeys,
        CancellationToken ct)
    {
        string? value = replacement.RevealForCommit();
        try
        {
            if (value.StartsWith(McpSecretResolver.SecretRefPrefix, StringComparison.Ordinal)
                || (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}')))
            {
                return value;
            }

            if (!replacement.StoreInCredentialStore)
            {
                return value;
            }

            var staged = await McpSecretStore.StageAsync(
                this.credentials,
                finalServerName,
                field,
                value,
                ct,
                staged => stagedKeys.Add(staged.StoreKey)).ConfigureAwait(false);
            return staged.Reference;
        }
        finally
        {
            value = null;
        }
    }

    private async Task<string?> DeleteUnreferencedOldSecretsAsync(
        string? originalServerName,
        McpServerConfig? original)
    {
        if (original is null || originalServerName is null)
        {
            return null;
        }

        var oldKeys = OwnedSecretReferences(originalServerName, original)
            .Select(binding => binding.StoreKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (oldKeys.Length == 0)
        {
            return null;
        }

        try
        {
            var liveKeys = this.LoadPhysicalEntries(CancellationToken.None)
                .SelectMany(entry => McpSecretStore.References(entry.Config))
                .Select(binding => binding.StoreKey)
                .ToHashSet(StringComparer.Ordinal);
            var obsolete = oldKeys.Where(key => !liveKeys.Contains(key)).ToArray();
            await McpSecretStore.DeleteKeysAsync(
                this.credentials,
                obsolete,
                CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception)
        {
            return "Prior MCP credentials were retained because post-save cleanup could not be verified.";
        }
    }

    private async Task<McpMutationResult> CreateRejectedAfterStagedCleanupAsync(
        string message,
        IEnumerable<string> stagedKeys)
    {
        if (await this.CleanupStagedKeysAsync(stagedKeys).ConfigureAwait(false))
        {
            message += " Cleanup incomplete; newly staged MCP credentials may remain.";
        }

        return await this.CreateRejectedResultAsync(message).ConfigureAwait(false);
    }

    private async Task<bool> CleanupStagedKeysAsync(IEnumerable<string> stagedKeys)
    {
        var incomplete = false;
        foreach (var key in stagedKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal))
        {
            try
            {
                await McpSecretStore.DeleteKeysAsync(
                    this.credentials,
                    [key],
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                incomplete = true;
            }
        }

        return incomplete;
    }

    private static IEnumerable<McpSecretBinding> OwnedSecretReferences(
        string serverName,
        McpServerConfig config) =>
        McpSecretStore.References(config).Where(binding =>
            McpSecretStore.IsOwnedKey(serverName, binding.Field, binding.StoreKey));

    private async Task<McpMutationResult> CreateRejectedResultAsync(string message)
    {
        var snapshot = await this.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        return new McpMutationResult(
            McpMutationStatus.Rejected,
            null,
            SanitizeError(message),
            snapshot);
    }

    private async Task<McpMutationResult> CreateSavedWithRuntimeErrorAsync(
        McpServerKey key,
        string runtimeError)
    {
        if (this.runtime is not null)
        {
            this.events.Publish(new McpRuntimeChangedEvent(this.runtime.GetSnapshot()));
        }

        var snapshot = await this.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        return new McpMutationResult(
            McpMutationStatus.SavedWithRuntimeError,
            key,
            CreateSavedMessage([SanitizeError(runtimeError)], cleanupWarning: null),
            snapshot);
    }

    private async Task<McpMutationResult> CreateOAuthTokenSavedConfigurationChangedResultAsync(
        McpServerKey key)
    {
        var snapshot = await this.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        McpServerKey? selected = snapshot.Servers.Any(server => server.Key == key) ? key : null;
        return new McpMutationResult(
            McpMutationStatus.SavedWithRuntimeError,
            selected,
            "The OAuth token was replaced, but the MCP server definition changed before it could be reconnected. Review the current configuration and reconnect it if appropriate.",
            snapshot);
    }

    private static Task<T> MutationUnavailable<T>(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromException<T>(new NotSupportedException(
            "MCP management mutations are not available in this service version."));
    }

    private void RaiseChanged() => this.Changed?.Invoke();

    private static string FileHash(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return MissingFileRevision;
        }

        var bytes = File.ReadAllBytes(path);
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private IReadOnlyList<McpPhysicalServerEntry> LoadPhysicalEntries(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            this.ValidatePhysicalConfiguration(ct);
            var entries = McpConfig.LoadPhysicalEntries(this.workingDirectory, this.userMcpDir);
            ct.ThrowIfCancellationRequested();
            return entries;
        }
        catch (InvalidOperationException exception)
        {
            throw new McpException("MCP config contains an invalid value type.", exception);
        }
    }

    private IReadOnlyList<McpPhysicalServerEntry> LoadPhysicalEntriesForMutation(CancellationToken ct)
    {
        try
        {
            return this.LoadPhysicalEntries(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new McpException("MCP configuration could not be read safely. Fix the configuration before editing.");
        }
    }

    private StablePreparationRead LoadStablePreparationEntries(CancellationToken ct)
    {
        const int maximumAttempts = 3;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var beforeRead = CaptureRevision(this.workingDirectory, this.userMcpDir, ct);
            var entries = this.LoadPhysicalEntriesForMutation(ct);
            this.afterPreparationEntriesRead?.Invoke();
            ct.ThrowIfCancellationRequested();
            var afterRead = CaptureRevision(this.workingDirectory, this.userMcpDir, ct);
            if (beforeRead == afterRead)
            {
                return new StablePreparationRead(entries, afterRead);
            }
        }

        throw new McpException("MCP configuration changed while preparing; retry.");
    }

    private sealed record StablePreparationRead(
        IReadOnlyList<McpPhysicalServerEntry> Entries,
        McpConfigRevision Revision);

    private void ValidatePhysicalConfiguration(CancellationToken ct)
    {
        ValidateConfigFile(
            McpConfig.FilePath(McpConfigScope.User, this.workingDirectory, this.userMcpDir),
            ct);
        ValidateConfigFile(
            McpConfig.FilePath(McpConfigScope.Project, this.workingDirectory, this.userMcpDir),
            ct);
    }

    private static void ValidateConfigFile(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new McpException("MCP config must contain valid JSON.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidConfigShape();
            }

            if (!document.RootElement.TryGetProperty("mcpServers", out var servers))
            {
                return;
            }

            if (servers.ValueKind != JsonValueKind.Object)
            {
                throw InvalidConfigShape();
            }

            foreach (var server in servers.EnumerateObject())
            {
                ct.ThrowIfCancellationRequested();
                if (server.Value.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidConfigShape();
                }

                ValidateServerShape(server.Value);
            }
        }
    }

    private static void ValidateServerShape(JsonElement server)
    {
        ValidateOptionalBoolean(server, "disabled");
        var type = ReadOptionalString(server, "type");
        switch (type)
        {
            case "http":
            case "streamable-http":
                ValidateOptionalString(server, "url");
                ValidateStringMap(server, "headers");
                ValidateAuthShape(server);
                break;
            case null:
            case "stdio":
                ValidateOptionalString(server, "command");
                ValidateStringArray(server, "args");
                ValidateStringMap(server, "env");
                break;
        }
    }

    private static void ValidateAuthShape(JsonElement server)
    {
        if (!server.TryGetProperty("auth", out var auth))
        {
            return;
        }

        if (auth.ValueKind != JsonValueKind.Object)
        {
            throw InvalidConfigShape();
        }

        ValidateOptionalString(auth, "mode");
        ValidateOptionalString(auth, "clientId");
        ValidateOptionalString(auth, "token");
        ValidateStringArray(auth, "scopes");
    }

    private static void ValidateOptionalBoolean(JsonElement obj, string property)
    {
        if (obj.TryGetProperty(property, out var value)
            && value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw InvalidConfigShape();
        }
    }

    private static void ValidateOptionalString(JsonElement obj, string property)
    {
        _ = ReadOptionalString(obj, property);
    }

    private static string? ReadOptionalString(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidConfigShape();
        }

        return value.GetString();
    }

    private static void ValidateStringArray(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var values))
        {
            return;
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            throw InvalidConfigShape();
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw InvalidConfigShape();
            }
        }
    }

    private static void ValidateStringMap(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var values))
        {
            return;
        }

        if (values.ValueKind != JsonValueKind.Object)
        {
            throw InvalidConfigShape();
        }

        foreach (var value in values.EnumerateObject())
        {
            if (value.Value.ValueKind != JsonValueKind.String)
            {
                throw InvalidConfigShape();
            }
        }
    }

    private static McpException InvalidConfigShape() =>
        new("MCP config contains an invalid value shape.");

    private McpPhysicalServerEntry? FindPhysicalEntry(McpServerKey key, CancellationToken ct)
    {
        try
        {
            return this.LoadPhysicalEntries(ct).FirstOrDefault(entry => entry.Key == key);
        }
        catch (McpException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private McpManagementSnapshot CreateReadFailure(Exception exception) =>
        new(
            Directory.Exists(this.workingDirectory),
            ImmutableArray<McpServerSummary>.Empty,
            SanitizeError(exception.Message));

    private McpServerSummary CreateSummary(
        McpPhysicalServerEntry entry,
        string? capabilityError = null)
    {
        string? runtimeError = null;
        var connected = false;
        if (entry.IsEffective && this.runtime is not null)
        {
            connected = this.runtime.IsServerConnected(entry.Key.Name);
            runtimeError = this.runtime.LastConnectionErrorFor(entry.Key.Name);
        }

        var lastError = capabilityError ?? runtimeError;
        var connection = !entry.IsEffective
            ? McpConnectionState.Overridden
            : connected
                ? McpConnectionState.Connected
                : lastError is not null
                    ? McpConnectionState.Error
                    : McpConnectionState.Disconnected;

        return new McpServerSummary(
            entry.Key,
            SanitizeIdentifier(entry.SourceFile),
            !entry.Config.Disabled,
            entry.IsEffective,
            TransportFor(entry.Config),
            connection,
            lastError is null ? null : SanitizeError(lastError));
    }

    private McpServerDetail CreateDetail(McpPhysicalServerEntry entry)
    {
        var summary = this.CreateSummary(entry);
        return entry.Config switch
        {
            McpStdioServerConfig stdio => new McpServerDetail(
                summary,
                SanitizeCredentialBearingText(stdio.Command),
                stdio.Args.Select(SanitizeCredentialBearingText).ToImmutableArray(),
                null,
                CreateSecretDescriptors(stdio.Env, "env"),
                ImmutableArray<McpSecretDescriptor>.Empty,
                McpAuthMode.None,
                null,
                ImmutableArray<string>.Empty,
                null,
                ImmutableArray<McpCapabilitySummary>.Empty,
                ImmutableArray<McpCapabilitySummary>.Empty,
                ImmutableArray<McpCapabilitySummary>.Empty),
            McpHttpServerConfig http => new McpServerDetail(
                summary,
                null,
                ImmutableArray<string>.Empty,
                DisplayUrl(http.Url),
                ImmutableArray<McpSecretDescriptor>.Empty,
                CreateSecretDescriptors(http.Headers, "header"),
                http.Auth.Mode,
                SanitizeOptionalIdentifier(http.Auth.ClientId),
                (http.Auth.Scopes ?? []).Select(SanitizeScopeLabel).ToImmutableArray(),
                CreateBearerDescriptor(http.Auth.BearerToken),
                ImmutableArray<McpCapabilitySummary>.Empty,
                ImmutableArray<McpCapabilitySummary>.Empty,
                ImmutableArray<McpCapabilitySummary>.Empty),
            _ => throw new McpException("MCP configuration has an unsupported transport."),
        };
    }

    private McpServerDraft CreateDraft(
        McpPhysicalServerEntry entry,
        McpConfigRevision? baseRevision = null,
        Guid draftId = default)
    {
        draftId = draftId == Guid.Empty ? Guid.NewGuid() : draftId;
        return entry.Config switch
        {
            McpStdioServerConfig stdio => new McpServerDraft(
                SanitizeIdentifier(entry.Key.Name),
                entry.Key.Scope,
                !stdio.Disabled,
                McpTransportKind.Stdio,
                SanitizeCredentialBearingText(stdio.Command),
                stdio.Args.Select(SanitizeCredentialBearingText).ToImmutableArray(),
                null,
                CreateSecretDrafts(stdio.Env, "env"),
                ImmutableArray<McpNamedSecretDraft>.Empty,
                McpAuthMode.None,
                null,
                ImmutableArray<string>.Empty,
                UnchangedBearerToken(),
                baseRevision)
            {
                DraftId = draftId,
                ArgumentItems = CreateDraftListItems(draftId, "argument", stdio.Args),
                ScopeItems = ImmutableArray<McpDraftListItem>.Empty,
                UrlChanged = false,
            },
            McpHttpServerConfig http => new McpServerDraft(
                SanitizeIdentifier(entry.Key.Name),
                entry.Key.Scope,
                !http.Disabled,
                McpTransportKind.Http,
                null,
                ImmutableArray<string>.Empty,
                DisplayUrl(http.Url),
                ImmutableArray<McpNamedSecretDraft>.Empty,
                CreateSecretDrafts(http.Headers, "header"),
                http.Auth.Mode,
                SanitizeOptionalIdentifier(http.Auth.ClientId),
                (http.Auth.Scopes ?? []).Select(SanitizeScopeLabel).ToImmutableArray(),
                UnchangedBearerToken(),
                baseRevision)
            {
                DraftId = draftId,
                ArgumentItems = ImmutableArray<McpDraftListItem>.Empty,
                ScopeItems = CreateDraftListItems(draftId, "scope", http.Auth.Scopes ?? []),
                UrlChanged = false,
            },
            _ => throw new McpException("MCP configuration has an unsupported transport."),
        };
    }

    private static ImmutableArray<McpDraftListItem> CreateDraftListItems(
        Guid draftId,
        string kind,
        IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return ImmutableArray<McpDraftListItem>.Empty;
        }

        var items = ImmutableArray.CreateBuilder<McpDraftListItem>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            items.Add(new McpDraftListItem(
                StableDraftItemId(draftId, kind, index),
                kind switch
                {
                    "argument" => SanitizeCredentialBearingText(values[index]),
                    "scope" => SanitizeScopeLabel(values[index]),
                    _ => SanitizeIdentifier(values[index]),
                }));
        }

        return items.MoveToImmutable();
    }

    private static Guid StableDraftItemId(Guid draftId, string kind, int originalIndex)
    {
        var identity = Encoding.UTF8.GetBytes(
            string.Concat(draftId.ToString("N"), ":", kind, ":", originalIndex.ToString(CultureInfo.InvariantCulture)));
        var hash = SHA256.HashData(identity);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static bool IsSafeExternalEditBaseline(
        McpServerDraft baseline,
        McpServerConfig config)
    {
        return config switch
        {
            McpStdioServerConfig stdio =>
                string.Equals(stdio.Command, baseline.Command, StringComparison.Ordinal)
                && stdio.Args.SequenceEqual(baseline.Args, StringComparer.Ordinal),
            McpHttpServerConfig http =>
                string.Equals(http.Url.OriginalString, baseline.Url, StringComparison.Ordinal)
                && string.Equals(http.Auth.ClientId, baseline.ClientId, StringComparison.Ordinal)
                && (http.Auth.Scopes ?? []).SequenceEqual(baseline.Scopes, StringComparer.Ordinal),
            _ => false,
        };
    }

    private static McpServerDraft PreserveUnchangedNonSecretValues(
        McpServerDraft draft,
        McpServerDraft baseline,
        McpServerConfig original)
    {
        var originalStdio = original as McpStdioServerConfig;
        var originalHttp = original as McpHttpServerConfig;
        return draft.Transport switch
        {
            McpTransportKind.Stdio => draft with
            {
                Command = originalStdio is not null
                    && baseline.Transport == McpTransportKind.Stdio
                    && string.Equals(draft.Command, baseline.Command, StringComparison.Ordinal)
                    ? originalStdio.Command
                    : draft.Command,
                Args = MergeDraftListValues(
                    originalStdio?.Args ?? [],
                    originalStdio is not null && baseline.Transport == McpTransportKind.Stdio
                        ? baseline.Args
                        : [],
                    draft.Args,
                    draft.DraftId,
                    originalStdio is not null && baseline.Transport == McpTransportKind.Stdio
                        ? baseline.ArgumentItems
                        : ImmutableArray<McpDraftListItem>.Empty,
                    draft.ArgumentItems),
            },
            McpTransportKind.Http => draft with
            {
                Url = originalHttp is not null
                    && baseline.Transport == McpTransportKind.Http
                    && !draft.UrlChanged
                    && string.Equals(draft.Url, baseline.Url, StringComparison.Ordinal)
                    ? originalHttp.Url.OriginalString
                    : draft.Url,
                ClientId = originalHttp is not null
                    && baseline.Transport == McpTransportKind.Http
                    && string.Equals(draft.ClientId, baseline.ClientId, StringComparison.Ordinal)
                    ? originalHttp.Auth.ClientId
                    : draft.ClientId,
                Scopes = MergeDraftListValues(
                    originalHttp?.Auth.Scopes ?? [],
                    originalHttp is not null && baseline.Transport == McpTransportKind.Http
                        ? baseline.Scopes
                        : [],
                    draft.Scopes,
                    draft.DraftId,
                    originalHttp is not null && baseline.Transport == McpTransportKind.Http
                        ? baseline.ScopeItems
                        : ImmutableArray<McpDraftListItem>.Empty,
                    draft.ScopeItems),
            },
            _ => throw new McpException("MCP configuration has an unsupported transport."),
        };
    }

    private static ImmutableArray<string> MergeDraftListValues(
        IReadOnlyList<string> originalValues,
        ImmutableArray<string> baselineDisplayedValues,
        ImmutableArray<string> editedValues,
        Guid draftId,
        ImmutableArray<McpDraftListItem> baselineItems,
        ImmutableArray<McpDraftListItem> editedItems)
    {
        if (HasAuthoritativeItems(draftId, baselineItems)
            && HasAuthoritativeItems(draftId, editedItems)
            && !baselineItems.SequenceEqual(editedItems))
        {
            return MergeIdentifiedDraftListValues(originalValues, baselineItems, editedItems);
        }

        return MergeUnchangedDisplayedValues(originalValues, baselineDisplayedValues, editedValues);
    }

    private static ImmutableArray<string> MergeIdentifiedDraftListValues(
        IReadOnlyList<string> originalValues,
        ImmutableArray<McpDraftListItem> baselineItems,
        ImmutableArray<McpDraftListItem> editedItems)
    {
        if (originalValues.Count != baselineItems.Length)
        {
            return editedItems.Select(static item => item.Value).ToImmutableArray();
        }

        var originalsById = new Dictionary<Guid, (string DisplayValue, string RawValue)>();
        for (var index = 0; index < baselineItems.Length; index++)
        {
            var item = baselineItems[index];
            originalsById[item.Id] = (item.Value, originalValues[index]);
        }

        var merged = ImmutableArray.CreateBuilder<string>(editedItems.Length);
        foreach (var item in editedItems)
        {
            merged.Add(
                originalsById.TryGetValue(item.Id, out var original)
                && string.Equals(item.Value, original.DisplayValue, StringComparison.Ordinal)
                    ? original.RawValue
                    : item.Value);
        }

        return merged.MoveToImmutable();
    }

    private static ImmutableArray<string> MergeUnchangedDisplayedValues(
        IReadOnlyList<string> originalValues,
        ImmutableArray<string> baselineDisplayedValues,
        ImmutableArray<string> editedValues)
    {
        if (originalValues.Count != baselineDisplayedValues.Length
            || originalValues.Count == 0
            || editedValues.IsDefaultOrEmpty)
        {
            return editedValues;
        }

        var rawForEditedIndex = new string?[editedValues.Length];
        var matchedOriginal = new bool[originalValues.Count];
        var matchedEdited = new bool[editedValues.Length];

        if (((long)baselineDisplayedValues.Length + 1) * ((long)editedValues.Length + 1) <= maximumLcsCells)
        {
            foreach (var (originalIndex, editedIndex) in LongestCommonSubsequenceMatches(
                         baselineDisplayedValues,
                         editedValues))
            {
                rawForEditedIndex[editedIndex] = originalValues[originalIndex];
                matchedOriginal[originalIndex] = true;
                matchedEdited[editedIndex] = true;
            }
        }

        // The ordinal queues retain values moved outside the LCS and keep identical
        // safe display values deterministic without quadratic allocation.
        var remainingOriginalByDisplay = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var originalIndex = 0; originalIndex < baselineDisplayedValues.Length; originalIndex++)
        {
            if (matchedOriginal[originalIndex])
            {
                continue;
            }

            var display = baselineDisplayedValues[originalIndex];
            if (!remainingOriginalByDisplay.TryGetValue(display, out var remainingIndices))
            {
                remainingIndices = new Queue<int>();
                remainingOriginalByDisplay.Add(display, remainingIndices);
            }

            remainingIndices.Enqueue(originalIndex);
        }

        for (var editedIndex = 0; editedIndex < editedValues.Length; editedIndex++)
        {
            if (matchedEdited[editedIndex]
                || !remainingOriginalByDisplay.TryGetValue(editedValues[editedIndex], out var remainingIndices)
                || remainingIndices.Count == 0)
            {
                continue;
            }

            var originalIndex = remainingIndices.Dequeue();
            rawForEditedIndex[editedIndex] = originalValues[originalIndex];
        }

        var merged = ImmutableArray.CreateBuilder<string>(editedValues.Length);
        for (var editedIndex = 0; editedIndex < editedValues.Length; editedIndex++)
        {
            merged.Add(rawForEditedIndex[editedIndex] ?? editedValues[editedIndex]);
        }

        return merged.MoveToImmutable();
    }

    private static IEnumerable<(int OriginalIndex, int EditedIndex)> LongestCommonSubsequenceMatches(
        ImmutableArray<string> baselineDisplayedValues,
        ImmutableArray<string> editedValues)
    {
        var lengths = new int[baselineDisplayedValues.Length + 1, editedValues.Length + 1];
        for (var originalIndex = baselineDisplayedValues.Length - 1; originalIndex >= 0; originalIndex--)
        {
            for (var editedIndex = editedValues.Length - 1; editedIndex >= 0; editedIndex--)
            {
                lengths[originalIndex, editedIndex] =
                    string.Equals(
                        baselineDisplayedValues[originalIndex],
                        editedValues[editedIndex],
                        StringComparison.Ordinal)
                        ? lengths[originalIndex + 1, editedIndex + 1] + 1
                        : Math.Max(
                            lengths[originalIndex + 1, editedIndex],
                            lengths[originalIndex, editedIndex + 1]);
            }
        }

        var original = 0;
        var edited = 0;
        while (original < baselineDisplayedValues.Length && edited < editedValues.Length)
        {
            if (string.Equals(
                    baselineDisplayedValues[original],
                    editedValues[edited],
                    StringComparison.Ordinal)
                && lengths[original, edited] == lengths[original + 1, edited + 1] + 1)
            {
                yield return (original, edited);
                original++;
                edited++;
            }
            else if (lengths[original + 1, edited] >= lengths[original, edited + 1])
            {
                original++;
            }
            else
            {
                edited++;
            }
        }
    }

    private async Task<CapabilityRead> ReadCapabilitiesAsync(string serverName, CancellationToken ct)
    {
        var manager = this.runtime!;
        var tools = manager.ServerTools(serverName)
            .Select(tool => new McpCapabilitySummary(
                SanitizeIdentifier(DisplayToolName(tool)),
                SanitizeOptionalCredentialBearingText(tool.Description)))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(capabilityTimeout);
        var prompts = ImmutableArray<McpCapabilitySummary>.Empty;
        var resources = ImmutableArray<McpCapabilitySummary>.Empty;
        string? lastError = null;

        try
        {
            var promptEntries = await manager.ServerPromptsAsync(serverName, timeout.Token).ConfigureAwait(false);
            prompts = promptEntries
                .Where(prompt => string.Equals(prompt.ServerName, serverName, StringComparison.Ordinal))
                .Select(prompt => new McpCapabilitySummary(
                    SanitizeIdentifier(prompt.Name),
                    SanitizeOptionalCredentialBearingText(prompt.Description)))
                .OrderBy(prompt => prompt.Name, StringComparer.Ordinal)
                .ToImmutableArray();
            lastError = RuntimeErrorFor(manager, serverName);

            var resourceEntries = await manager.ServerResourcesAsync(serverName, timeout.Token).ConfigureAwait(false);
            resources = resourceEntries
                .Where(resource => string.Equals(resource.ServerName, serverName, StringComparison.Ordinal))
                .Select(resource => new McpCapabilitySummary(
                    SanitizeIdentifier(resource.Name),
                    DisplayUri(resource.Uri)))
                .OrderBy(resource => resource.Name, StringComparer.Ordinal)
                .ToImmutableArray();
            lastError ??= RuntimeErrorFor(manager, serverName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            lastError = "MCP capability request timed out.";
        }

        return new CapabilityRead(tools, prompts, resources, lastError);
    }

    private static string? RuntimeErrorFor(McpClientManager manager, string serverName)
    {
        var error = manager.LastConnectionErrorFor(serverName);
        return error is null ? null : SanitizeError(error);
    }

    private static McpTransportKind TransportFor(McpServerConfig config) => config switch
    {
        McpStdioServerConfig => McpTransportKind.Stdio,
        McpHttpServerConfig => McpTransportKind.Http,
        _ => throw new McpException("MCP configuration has an unsupported transport."),
    };

    private static ImmutableArray<McpSecretDescriptor> CreateSecretDescriptors(
        IReadOnlyDictionary<string, string> values,
        string fieldPrefix)
    {
        return values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var source = ClassifySecret(pair.Value);
                return new McpSecretDescriptor(
                    $"{fieldPrefix}/{pair.Key}",
                    SanitizeIdentifier(pair.Key),
                    source,
                    DisplayValueFor(source));
            })
            .ToImmutableArray();
    }

    private static ImmutableArray<McpNamedSecretDraft> CreateSecretDrafts(
        IReadOnlyDictionary<string, string> values,
        string fieldPrefix)
    {
        return values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new McpNamedSecretDraft(
                SanitizeIdentifier(pair.Key),
                ClassifySecret(pair.Value),
                new McpSecretChange(
                    $"{fieldPrefix}/{pair.Key}",
                    McpSecretChangeKind.Unchanged)))
            .ToImmutableArray();
    }

    private static McpSecretDescriptor? CreateBearerDescriptor(string? value)
    {
        var source = ClassifySecret(value);
        return new McpSecretDescriptor(
            "auth/token",
            "token",
            source,
            DisplayValueFor(source));
    }

    private static string DisplayToolName(McpTool tool)
    {
        var prefix = $"mcp__{SanitizeToolSegment(tool.ServerName)}__";
        return tool.Name.StartsWith(prefix, StringComparison.Ordinal)
            ? tool.Name[prefix.Length..]
            : tool.Name;
    }

    private static string SanitizeToolSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_' or '-'
                ? character
                : '_');
        }

        return builder.ToString();
    }

    private static McpSecretChange UnchangedBearerToken() =>
        new("auth/token", McpSecretChangeKind.Unchanged);

    private static McpSecretSource ClassifySecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return McpSecretSource.None;
        }

        if (McpSecretStore.TryGetStoreKey(value, out _))
        {
            return McpSecretSource.Managed;
        }

        return value.Contains("${", StringComparison.Ordinal)
            ? McpSecretSource.Environment
            : McpSecretSource.Literal;
    }

    private static string DisplayValueFor(McpSecretSource source) => source switch
    {
        McpSecretSource.Managed => "***** (encrypted)",
        McpSecretSource.Environment => "***** (environment)",
        McpSecretSource.Literal => "*****",
        _ => string.Empty,
    };

    private static string? SanitizeOptionalIdentifier(string? value) =>
        value is null ? null : SanitizeIdentifier(value);

    private static string? SanitizeOptionalCredentialBearingText(string? value) =>
        value is null ? null : SanitizeCredentialBearingText(value);

    private static string DisplayUrl(Uri url)
    {
        if (!url.IsAbsoluteUri || string.IsNullOrEmpty(url.Host))
        {
            return $"{url.Scheme}:[redacted]";
        }

        var host = url.HostNameType == UriHostNameType.IPv6
            ? $"[{url.DnsSafeHost.Trim('[', ']')}]"
            : url.Host;
        var port = url.IsDefaultPort ? string.Empty : $":{url.Port}";
        var path = url.AbsolutePath;
        return SanitizeFreeText($"{url.Scheme}://{host}{port}{path}");
    }

    private static string DisplayUri(string value)
    {
        var withoutQueryOrFragment = StripQueryAndFragment(value);
        if (withoutQueryOrFragment.StartsWith("//", StringComparison.Ordinal))
        {
            return DisplaySchemeRelativeUri(withoutQueryOrFragment);
        }

        if (Uri.TryCreate(withoutQueryOrFragment, UriKind.Absolute, out var uri))
        {
            return DisplayUrl(uri);
        }

        return SanitizeCredentialBearingText(withoutQueryOrFragment);
    }

    private static string SanitizeIdentifier(string? value) => SanitizeSingleLine(value);

    private static string SanitizeFreeText(string? value)
    {
        var redacted = RedactSecrets(value);
        return RedactSecrets(SanitizeSingleLine(redacted));
    }

    private static string SanitizeCredentialBearingText(string? value) =>
        NetworkUriPattern().Replace(SanitizeFreeText(value), "[redacted URL]");

    private static string SanitizeScopeLabel(string? value) =>
        NetworkUriPattern().Replace(SanitizeIdentifier(value), "[redacted URL]");

    private static string SanitizeError(string? value)
    {
        try
        {
            var safe = SanitizeCredentialBearingText(value);
            return string.IsNullOrEmpty(safe) ? "MCP operation failed." : safe;
        }
        catch (RegexMatchTimeoutException)
        {
            return "MCP operation failed.";
        }
    }

    private static string StripQueryAndFragment(string value)
    {
        var queryOrFragment = value.IndexOfAny(['?', '#']);
        return queryOrFragment < 0 ? value : value[..queryOrFragment];
    }

    private static string DisplaySchemeRelativeUri(string value)
    {
        var pathStart = value.IndexOf('/', 2);
        var authority = pathStart < 0 ? value[2..] : value[2..pathStart];
        var userInfo = authority.LastIndexOf('@');
        if (userInfo >= 0)
        {
            authority = authority[(userInfo + 1)..];
        }

        var path = pathStart < 0 ? string.Empty : value[pathStart..];
        return SanitizeFreeText($"//{authority}{path}");
    }

    private static string RedactSecrets(string? value) =>
        SecretAssignmentPattern().Replace(SecretRedactor.Redact(value), RedactSecretAssignment);

    private static string SanitizeSingleLine(string? value)
    {
        var stripped = TerminalTextSanitizer.StripAnsiEscapes(value);
        if (stripped.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(stripped.Length);
        var pendingSpace = false;
        foreach (var rune in stripped.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static string RedactSecretAssignment(Match match) =>
        $"{match.Groups[1].Value}{match.Groups[2].Value}{SecretRedactor.Placeholder}";

    [GeneratedRegex(@"(?x)
        \b(authorization|proxy-authorization|x-api-key|cookie|set-cookie|
        token|secret|password|api[_-]?key|apikey|
        [a-z_][a-z0-9_-]*(?:token|secret|password|api[_-]?key)[a-z0-9_-]*)
        (\s*(?:=|:)\s*)(?:Bearer\s+)?(?:""[^""]*""|'[^']*'|[^\s;,]+)",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        1000)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(
        @"(?:(?:[a-z][a-z0-9+.-]*:)?//|\b[a-z][a-z0-9+.-]+:)[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        1000)]
    private static partial Regex NetworkUriPattern();

    private sealed record CapabilityRead(
        ImmutableArray<McpCapabilitySummary> Tools,
        ImmutableArray<McpCapabilitySummary> Prompts,
        ImmutableArray<McpCapabilitySummary> Resources,
        string? LastError);

    private sealed class McpConfigWriterMutator : IRevisionedMcpConfigMutator
    {
        public static McpConfigWriterMutator Instance { get; } = new();

        public void Upsert(
            McpConfigScope scope,
            string name,
            McpServerConfig config,
            bool disabled,
            string workingDirectory,
            string? userMcpDir) =>
            McpConfigWriter.Upsert(scope, name, config, disabled, workingDirectory, userMcpDir);

        public void Upsert(
            McpConfigScope scope,
            string name,
            McpServerConfig config,
            bool disabled,
            string workingDirectory,
            string? userMcpDir,
            string expectedRevision) =>
            McpConfigWriter.Upsert(
                scope,
                name,
                config,
                disabled,
                workingDirectory,
                userMcpDir,
                expectedRevision);

        public void ReplaceEntry(
            McpConfigScope scope,
            string currentName,
            string newName,
            McpServerConfig config,
            bool disabled,
            string workingDirectory,
            string? userMcpDir) =>
            McpConfigWriter.ReplaceEntry(
                scope,
                currentName,
                newName,
                config,
                disabled,
                workingDirectory,
                userMcpDir);

        public bool Remove(
            McpConfigScope scope,
            string name,
            string workingDirectory,
            string? userMcpDir) =>
            McpConfigWriter.Remove(scope, name, workingDirectory, userMcpDir);

        public bool SetDisabled(
            McpConfigScope scope,
            string name,
            bool disabled,
            string workingDirectory,
            string? userMcpDir) =>
            McpConfigWriter.SetDisabled(scope, name, disabled, workingDirectory, userMcpDir);

        public void ReplaceEntry(
            McpConfigScope scope,
            string currentName,
            string newName,
            McpServerConfig config,
            bool disabled,
            string workingDirectory,
            string? userMcpDir,
            string expectedRevision) =>
            McpConfigWriter.ReplaceEntry(
                scope,
                currentName,
                newName,
                config,
                disabled,
                workingDirectory,
                userMcpDir,
                expectedRevision);

        public bool Remove(
            McpConfigScope scope,
            string name,
            string workingDirectory,
            string? userMcpDir,
            string expectedRevision) =>
            McpConfigWriter.Remove(scope, name, workingDirectory, userMcpDir, expectedRevision);

        public bool SetDisabled(
            McpConfigScope scope,
            string name,
            bool disabled,
            string workingDirectory,
            string? userMcpDir,
            string expectedRevision) =>
            McpConfigWriter.SetDisabled(
                scope,
                name,
                disabled,
                workingDirectory,
                userMcpDir,
                expectedRevision);
    }
}
