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
    private readonly string workingDirectory;
    private readonly string? userMcpDir;
    private readonly McpClientManager? runtime;
    private readonly ITokenStore credentials;
    private readonly IMcpOAuthReauthenticator oauth;
    private readonly IUiEventPublisher events;
    private readonly IMcpConfigMutator configMutator;
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    internal McpManagementService(
        string workingDirectory,
        string? userMcpDir,
        McpClientManager? runtime,
        ITokenStore credentials,
        IMcpOAuthReauthenticator oauth,
        IUiEventPublisher events,
        IMcpConfigMutator? configMutator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        this.workingDirectory = workingDirectory;
        this.userMcpDir = userMcpDir;
        this.runtime = runtime;
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        this.events = events ?? throw new ArgumentNullException(nameof(events));
        this.configMutator = configMutator ?? McpConfigWriterMutator.Instance;
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
        var entry = this.FindPhysicalEntry(key, ct);
        if (entry is null)
        {
            return Task.FromResult<McpServerDraft?>(null);
        }

        ct.ThrowIfCancellationRequested();
        return Task.FromResult<McpServerDraft?>(this.CreateDraft(entry));
    }

    public Task<McpEditPreview> PrepareAddAsync(McpServerDraft draft, CancellationToken ct) =>
        MutationUnavailable<McpEditPreview>(ct);

    public Task<McpEditPreview> PrepareEditAsync(
        McpServerKey original,
        McpServerDraft draft,
        CancellationToken ct) =>
        MutationUnavailable<McpEditPreview>(ct);

    public Task<McpMutationResult> CommitAddAsync(McpEditPreview preview, CancellationToken ct) =>
        MutationUnavailable<McpMutationResult>(ct);

    public Task<McpMutationResult> CommitEditAsync(McpEditPreview preview, CancellationToken ct) =>
        MutationUnavailable<McpMutationResult>(ct);

    public Task<McpMutationResult> SetEnabledAsync(
        McpServerKey key,
        bool enabled,
        CancellationToken ct) =>
        MutationUnavailable<McpMutationResult>(ct);

    public Task<McpDeletePreview> PrepareDeleteAsync(McpServerKey key, CancellationToken ct) =>
        MutationUnavailable<McpDeletePreview>(ct);

    public Task<McpMutationResult> CommitDeleteAsync(
        McpDeletePreview confirmedPreview,
        CancellationToken ct) =>
        MutationUnavailable<McpMutationResult>(ct);

    public Task<McpReauthenticationPlan> PrepareReauthenticationAsync(
        McpServerKey key,
        CancellationToken ct) =>
        MutationUnavailable<McpReauthenticationPlan>(ct);

    public Task<McpMutationResult> ReauthenticateAsync(
        McpReauthenticationPlan plan,
        IReadOnlyDictionary<string, McpSecretReplacement> replacements,
        CancellationToken ct) =>
        MutationUnavailable<McpMutationResult>(ct);

    public Task<McpMutationResult> StartAsync(string name, CancellationToken ct) =>
        MutationUnavailable<McpMutationResult>(ct);

    public Task<McpMutationResult> StopAsync(string name, CancellationToken ct) =>
        MutationUnavailable<McpMutationResult>(ct);

    public Task<McpMutationResult> RestartAsync(string? name, CancellationToken ct) =>
        MutationUnavailable<McpMutationResult>(ct);

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
            SanitizeVisibleText(entry.SourceFile),
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
                SanitizeVisibleText(stdio.Command),
                stdio.Args.Select(SanitizeVisibleText).ToImmutableArray(),
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
                SanitizeOptionalVisibleText(http.Auth.ClientId),
                (http.Auth.Scopes ?? []).Select(SanitizeVisibleText).ToImmutableArray(),
                CreateBearerDescriptor(http.Auth.BearerToken),
                ImmutableArray<McpCapabilitySummary>.Empty,
                ImmutableArray<McpCapabilitySummary>.Empty,
                ImmutableArray<McpCapabilitySummary>.Empty),
            _ => throw new McpException("MCP configuration has an unsupported transport."),
        };
    }

    private McpServerDraft CreateDraft(McpPhysicalServerEntry entry)
    {
        return entry.Config switch
        {
            McpStdioServerConfig stdio => new McpServerDraft(
                entry.Key.Name,
                entry.Key.Scope,
                !stdio.Disabled,
                McpTransportKind.Stdio,
                SanitizeVisibleText(stdio.Command),
                stdio.Args.Select(SanitizeVisibleText).ToImmutableArray(),
                null,
                CreateSecretDrafts(stdio.Env, "env"),
                ImmutableArray<McpNamedSecretDraft>.Empty,
                McpAuthMode.None,
                null,
                ImmutableArray<string>.Empty,
                UnchangedBearerToken()),
            McpHttpServerConfig http => new McpServerDraft(
                entry.Key.Name,
                entry.Key.Scope,
                !http.Disabled,
                McpTransportKind.Http,
                null,
                ImmutableArray<string>.Empty,
                DisplayUrl(http.Url),
                ImmutableArray<McpNamedSecretDraft>.Empty,
                CreateSecretDrafts(http.Headers, "header"),
                http.Auth.Mode,
                SanitizeOptionalVisibleText(http.Auth.ClientId),
                (http.Auth.Scopes ?? []).Select(SanitizeVisibleText).ToImmutableArray(),
                UnchangedBearerToken()),
            _ => throw new McpException("MCP configuration has an unsupported transport."),
        };
    }

    private async Task<CapabilityRead> ReadCapabilitiesAsync(string serverName, CancellationToken ct)
    {
        var manager = this.runtime!;
        var tools = manager.ServerTools(serverName)
            .Select(tool => new McpCapabilitySummary(
                SanitizeVisibleText(DisplayToolName(tool)),
                SanitizeOptionalVisibleText(tool.Description)))
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
                    SanitizeVisibleText(prompt.Name),
                    SanitizeOptionalVisibleText(prompt.Description)))
                .OrderBy(prompt => prompt.Name, StringComparer.Ordinal)
                .ToImmutableArray();
            lastError = RuntimeErrorFor(manager, serverName);

            var resourceEntries = await manager.ServerResourcesAsync(serverName, timeout.Token).ConfigureAwait(false);
            resources = resourceEntries
                .Where(resource => string.Equals(resource.ServerName, serverName, StringComparison.Ordinal))
                .Select(resource => new McpCapabilitySummary(
                    SanitizeVisibleText(resource.Name),
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
                    SanitizeVisibleText(pair.Key),
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
                SanitizeVisibleText(pair.Key),
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

        if (IsWholeManagedReference(value))
        {
            return McpSecretSource.Managed;
        }

        return value.Contains("${", StringComparison.Ordinal)
            ? McpSecretSource.Environment
            : McpSecretSource.Literal;
    }

    private static bool IsWholeManagedReference(string value)
    {
        if (!value.StartsWith(McpSecretResolver.SecretRefPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var key = value[McpSecretResolver.SecretRefPrefix.Length..];
        return key.Length > 0 && !key.Any(char.IsWhiteSpace);
    }

    private static string DisplayValueFor(McpSecretSource source) => source switch
    {
        McpSecretSource.Managed => "***** (encrypted)",
        McpSecretSource.Environment => "***** (environment)",
        McpSecretSource.Literal => "*****",
        _ => string.Empty,
    };

    private static string? SanitizeOptionalVisibleText(string? value) =>
        value is null ? null : SanitizeVisibleText(value);

    private static string DisplayUrl(Uri url)
    {
        if (!url.IsAbsoluteUri || string.IsNullOrEmpty(url.Host))
        {
            return $"{url.Scheme}:[redacted]";
        }

        var host = url.Host.Contains(':')
            ? $"[{url.Host}]"
            : url.Host;
        var port = url.IsDefaultPort ? string.Empty : $":{url.Port}";
        var path = url.AbsolutePath;
        return SanitizePlainText($"{url.Scheme}://{host}{port}{path}");
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

        return SanitizeVisibleText(withoutQueryOrFragment);
    }

    private static string SanitizeVisibleText(string? value)
    {
        var safe = SanitizePlainText(value);
        return NetworkUriPattern().Replace(safe, "[redacted URL]");
    }

    private static string SanitizePlainText(string? value)
    {
        var safe = SanitizeSingleLine(value);
        safe = SecretRedactor.Redact(safe);
        return SecretAssignmentPattern().Replace(safe, RedactSecretAssignment);
    }

    private static string SanitizeError(string? value)
    {
        try
        {
            var safe = SanitizeVisibleText(value);
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
        return SanitizePlainText($"//{authority}{path}");
    }

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

    private sealed class McpConfigWriterMutator : IMcpConfigMutator
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
    }
}
