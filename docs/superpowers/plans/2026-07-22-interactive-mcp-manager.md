# Interactive MCP Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make exact bare `/mcp` open a task-style Terminal.Gui list/detail/editor manager that shows both physical scopes and safely performs rename-capable edits, enable/disable, reauthentication, deletion, and immediate runtime reconciliation.

**Architecture:** `Coda.Mcp` gains strict physical read/write, staged-secret, OAuth, and runtime-detail primitives while preserving the existing merged startup load. A host-neutral `McpManagementService` owns validation, revision checks, transaction ordering, secret cleanup, and runtime reconciliation; textual commands and an immutable Terminal.Gui browser controller call the same service, and mutations require an atomic whole-turn idle lease.

**Tech Stack:** C# 14, .NET 10, Terminal.Gui 2.x, `System.Text.Json.Nodes`, SHA-256 revisions, DPAPI-backed `ITokenStore`, existing MCP clients/configuration, xUnit.

---

## File responsibility map

### Coda.Mcp primitives

- Create: `src/Coda.Mcp/McpServerKey.cs` — physical identity `(scope, name)`.
- Create: `src/Coda.Mcp/McpPhysicalServerEntry.cs` — raw scoped config, source file, and effective marker.
- Modify: `src/Coda.Mcp/McpConfig.cs` — strict physical two-scope read model; existing merged `Load`/`LoadEntries` behavior remains.
- Modify: `src/Coda.Mcp/McpConfigWriter.cs` — one-write incremental edit/rename with unique same-directory temp files.
- Modify: `src/Coda.Mcp/McpSecretStore.cs` — enumerate references, stage versioned keys, and delete explicit keys.
- Create: `src/Coda.Mcp/Auth/IMcpOAuthReauthenticator.cs` and `DefaultMcpOAuthReauthenticator.cs` — proactive OAuth reauthentication abstraction.
- Modify: `src/Coda.Mcp/Auth/McpOAuthProvider.cs` — force token replacement while preserving dynamic client registration.
- Modify: `src/Coda.Mcp/McpClientManager.cs` — sanitized per-server connection error plus per-server prompts/resources.

### Shared management layer

- Create: `src/Coda.Tui/Mcp/McpManagementModels.cs` — typed snapshots, details, drafts, previews, mutation results, and secret ownership/change models.
- Create: `src/Coda.Tui/Mcp/McpServerNameValidator.cs` — exact server-name validation.
- Create: `src/Coda.Tui/Mcp/McpManagementService.cs` — read, validate, prepare, commit, clean up, reconcile, and publish.
- Modify: `src/Coda.Tui/Repl/CommandContext.cs` — cache/expose the shared service.
- Modify: `src/Coda.Tui/Commands/McpCommand.cs`, `McpFlagParser.cs`, `McpView.cs` — thin textual parse/prompt/render clients of the service.

### Browser and shell

- Create: `src/Coda.Tui/Ui/IExclusiveIdleGate.cs` — atomic whole-turn mutation lease.
- Modify: `src/Coda.Agent/Tasks/TaskManager.cs` — block new managed task registration while a runtime-mutation lease is held.
- Modify: `src/Coda.Tui/Ui/TuiController.cs` — implement the idle gate under the dispatch lock.
- Create: `src/Coda.Tui/Ui/Mcp/McpBrowserModels.cs`, `McpBrowserState.cs`, `McpBrowserKeyMap.cs`, `McpBrowserController.cs`, `McpBrowserOverlay.cs`.
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`, `FullscreenTuiShell.cs`, `InlineTuiShell.cs`, and `src/Coda.Tui/InteractiveProgram.cs` — exact interception, composition, z-order, focus restoration, both-mode parity, and disposal.

### Tests

- Engine: extend `McpReadModelTests.cs`, `McpConfigDisabledTests.cs`, `McpConfigWriterTests.cs`, `McpSecretStoreTests.cs`, `McpAuthTests.cs`, `McpLifecycleTests.cs`.
- TUI management: create `McpManagementTestHarness.cs`, `McpManagementReadTests.cs`, `McpManagementEditTests.cs`, `McpManagementRuntimeTests.cs`, `McpManagementDeleteTests.cs`, `McpManagementAuthenticationTests.cs`.
- TUI browser: create `McpBrowserStateTests.cs`, `McpBrowserKeyMapTests.cs`, `McpBrowserControllerTests.cs`, `McpBrowserOverlayTests.cs`, `McpInterceptTests.cs`.
- Engine concurrency: create `tests/Engine.Tests/TaskManagerIdleLeaseTests.cs`.
- Existing textual command/view/parser and `TuiControllerTests.cs` remain regression coverage.

## Shared typed management contracts

Define these together in `McpManagementModels.cs`; later tasks use these exact names and signatures.

```csharp
internal enum McpTransportKind { Stdio, Http }
internal enum McpConnectionState { Overridden, Disconnected, Connected, Error }
internal enum McpSecretSource { None, Managed, Environment, Literal }
internal enum McpSecretChangeKind { Unchanged, Replace, Remove }
internal enum McpMutationStatus { Succeeded, Rejected, SavedWithRuntimeError, NoOp }
internal enum McpReauthenticationKind { OAuth, StoredSecret, EnvironmentOwned, Unavailable }

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
    McpSecretChange BearerToken);

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
```

No record placed in browser state, UI events, logs, or exception text may contain an existing or replacement secret value. `McpSecretReplacement.ToString()` must stay masked.

### Task 1: Expose a strict physical two-scope read model

**Files:**
- Create: `src/Coda.Mcp/McpServerKey.cs`
- Create: `src/Coda.Mcp/McpPhysicalServerEntry.cs`
- Modify: `src/Coda.Mcp/McpConfig.cs`
- Modify: `tests/Engine.Tests/McpReadModelTests.cs`
- Modify: `tests/Engine.Tests/McpConfigDisabledTests.cs`

- [ ] **Step 1: Write failing physical-scope tests**

```csharp
[Fact]
public void LoadPhysicalEntries_returns_both_scopes_for_the_same_name()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    var user = Path.Combine(root, "user");
    var project = Path.Combine(root, "project");
    Directory.CreateDirectory(user);
    Directory.CreateDirectory(project);
    try
    {
        File.WriteAllText(
            Path.Combine(user, ".mcp.json"),
            """{"mcpServers":{"shared":{"command":"user"}}}""");
        File.WriteAllText(
            Path.Combine(project, ".mcp.json"),
            """{"mcpServers":{"shared":{"command":"project","disabled":true}}}""");

        var entries = McpConfig.LoadPhysicalEntries(project, user);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry =>
            entry.Key == new McpServerKey(McpConfigScope.User, "shared") &&
            !entry.IsEffective);
        Assert.Contains(entries, entry =>
            entry.Key == new McpServerKey(McpConfigScope.Project, "shared") &&
            entry.IsEffective &&
            entry.Config.Disabled);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

[Fact]
public void Strict_physical_read_surfaces_corrupt_existing_json()
{
    var project = Directory.CreateTempSubdirectory().FullName;
    try
    {
        File.WriteAllText(Path.Combine(project, ".mcp.json"), "{");

        var error = Assert.Throws<McpException>(
            () => McpConfig.LoadPhysicalEntries(project));

        Assert.Contains("valid JSON", error.Message, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
        Directory.Delete(project, recursive: true);
    }
}
```

- [ ] **Step 2: Run physical read-model tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~McpReadModelTests|FullyQualifiedName~McpConfigDisabledTests"`

Expected: FAIL because physical entries and strict reads do not exist.

- [ ] **Step 3: Implement physical identity and merge-then-mark semantics**

```csharp
namespace Coda.Mcp;

public readonly record struct McpServerKey(
    McpConfigScope Scope,
    string Name);

public sealed record McpPhysicalServerEntry(
    McpServerKey Key,
    McpServerConfig Config,
    string SourceFile,
    bool IsEffective);
```

Add:

```csharp
public static IReadOnlyList<McpPhysicalServerEntry> LoadPhysicalEntries(
    string workingDirectory,
    string? userMcpDir = null,
    bool includeProject = true);
```

Read each existing physical file strictly. A project definition is effective whenever present, even when disabled; a same-name user definition is then overridden. When no project definition exists, the user definition is effective. Preserve current `Load` and `LoadEntries` APIs and their merged startup behavior.

- [ ] **Step 4: Run physical read-model tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~McpReadModelTests|FullyQualifiedName~McpConfigDisabledTests"`

Expected: PASS, including existing merge-then-filter disabled precedence.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Mcp\McpServerKey.cs src\Coda.Mcp\McpPhysicalServerEntry.cs src\Coda.Mcp\McpConfig.cs tests\Engine.Tests\McpReadModelTests.cs tests\Engine.Tests\McpConfigDisabledTests.cs
git commit -m "feat(mcp): expose physical scoped server definitions" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 2: Add one-write incremental edit and rename

**Files:**
- Modify: `src/Coda.Mcp/McpConfigWriter.cs`
- Modify: `tests/Engine.Tests/McpConfigWriterTests.cs`

- [ ] **Step 1: Write failing atomic rename and preservation tests**

```csharp
[Fact]
public void ReplaceEntry_renames_in_one_document_and_preserves_unknown_fields()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    try
    {
        var path = Path.Combine(root, ".mcp.json");
        File.WriteAllText(
            path,
            """{"other":7,"mcpServers":{"old":{"command":"before","disabled":true,"vendor":{"x":1}}}}""");

        McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project,
            "old",
            "new",
            new McpHttpServerConfig(
                new Uri("https://example.test/mcp"),
                new Dictionary<string, string>(),
                McpAuthConfig.Default),
            disabled: true,
            root);

        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var servers = json["mcpServers"]!.AsObject();
        Assert.False(servers.ContainsKey("old"));
        Assert.True(servers.ContainsKey("new"));
        Assert.Equal(7, json["other"]!.GetValue<int>());
        Assert.Equal(1, servers["new"]!["vendor"]!["x"]!.GetValue<int>());
        Assert.Null(servers["new"]!["command"]);
        Assert.True(servers["new"]!["disabled"]!.GetValue<bool>());
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

[Fact]
public void ReplaceEntry_collision_leaves_original_bytes_unchanged()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    try
    {
        var path = Path.Combine(root, ".mcp.json");
        const string original =
            """{"mcpServers":{"old":{"command":"a"},"taken":{"command":"b"}}}""";
        File.WriteAllText(path, original);

        Assert.Throws<McpException>(() => McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project,
            "old",
            "taken",
            new McpStdioServerConfig("c", [], new Dictionary<string, string>()),
            disabled: false,
            root));

        Assert.Equal(original, File.ReadAllText(path));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 2: Run config-writer tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~McpConfigWriterTests"`

Expected: FAIL because `ReplaceEntry` does not exist.

- [ ] **Step 3: Implement incremental replacement and unique atomic temp files**

```csharp
public static void ReplaceEntry(
    McpConfigScope scope,
    string currentName,
    string newName,
    McpServerConfig config,
    bool disabled,
    string workingDirectory,
    string? userMcpDir = null);
```

Read once, reject an absent source or a different existing same-scope target, clone the old `JsonObject`, remove known transport keys (`type`, `command`, `args`, `env`, `url`, `headers`, `auth`, `disabled`), overlay the new known shape, then remove old/add new in the in-memory document and write once.

Replace the fixed `.tmp` path:

```csharp
var temp = Path.Combine(
    directory!,
    $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
try
{
    File.WriteAllText(temp, root.ToJsonString(writeOptions));
    File.Move(temp, path, overwrite: true);
}
finally
{
    if (File.Exists(temp))
    {
        File.Delete(temp);
    }
}
```

- [ ] **Step 4: Run config-writer tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~McpConfigWriterTests"`

Expected: PASS, including corrupt-file byte preservation and transport stale-field removal.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Mcp\McpConfigWriter.cs tests\Engine.Tests\McpConfigWriterTests.cs
git commit -m "feat(mcp): add atomic incremental edit and rename" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 3: Add staged secrets and proactive OAuth reauthentication

**Files:**
- Modify: `src/Coda.Mcp/McpSecretStore.cs`
- Modify: `src/Coda.Mcp/Auth/McpOAuthProvider.cs`
- Create: `src/Coda.Mcp/Auth/IMcpOAuthReauthenticator.cs`
- Create: `src/Coda.Mcp/Auth/DefaultMcpOAuthReauthenticator.cs`
- Modify: `tests/Engine.Tests/McpSecretStoreTests.cs`
- Modify: `tests/Engine.Tests/McpAuthTests.cs`

- [ ] **Step 1: Write failing staging/reference/OAuth tests**

```csharp
[Fact]
public async Task StageAsync_uses_a_versioned_key_without_overwriting_the_old_value()
{
    var store = new InMemoryTokenStore();
    await store.SetAsync("mcp:server/header/Auth", "old");

    var staged = await McpSecretStore.StageAsync(
        store,
        "server",
        "header/Auth",
        "new");

    Assert.StartsWith("mcp:server/header/Auth/", staged.StoreKey, StringComparison.Ordinal);
    Assert.Equal("new", await store.GetAsync(staged.StoreKey));
    Assert.Equal("old", await store.GetAsync("mcp:server/header/Auth"));
    Assert.Equal($"coda-secret:{staged.StoreKey}", staged.Reference);
}

[Fact]
public void References_returns_env_header_and_bearer_bindings()
{
    var config = new McpHttpServerConfig(
        new Uri("https://example.test/mcp"),
        new Dictionary<string, string>
        {
            ["Authorization"] = "coda-secret:mcp:s/header/Authorization",
        },
        new McpAuthConfig(
            McpAuthMode.Bearer,
            BearerToken: "coda-secret:mcp:s/auth/token"));

    var fields = McpSecretStore.References(config)
        .Select(binding => binding.Field)
        .OrderBy(field => field, StringComparer.Ordinal)
        .ToArray();

    Assert.Equal(["auth/token", "header/Authorization"], fields);
}
```

Add an OAuth test that seeds the canonical token and DCR client registration, forces reauthorization through a scripted browser/listener flow, and asserts only the token is replaced while the client registration remains.

- [ ] **Step 2: Run secret/auth tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~McpSecretStoreTests|FullyQualifiedName~McpOAuthTokenLifecycleTests|FullyQualifiedName~McpClientIdResolutionTests|FullyQualifiedName~McpUnauthorizedResolutionFlowTests"`

Expected: FAIL because staged keys, reference enumeration, and proactive OAuth are absent.

- [ ] **Step 3: Implement transaction-safe secret and OAuth primitives**

```csharp
public sealed record McpSecretBinding(
    string Field,
    string StoreKey);

public sealed record McpStagedSecret(
    string Field,
    string StoreKey,
    string Reference);
```

Add:

```csharp
public static IReadOnlyList<McpSecretBinding> References(McpServerConfig config);

public static async Task<McpStagedSecret> StageAsync(
    ITokenStore store,
    string server,
    string field,
    string value,
    CancellationToken ct = default)
{
    var key = $"{KeyFor(server, field)}/{Guid.NewGuid():N}";
    await store.SetAsync(key, value, ct).ConfigureAwait(false);
    return new McpStagedSecret(
        field,
        key,
        McpSecretResolver.SecretRefPrefix + key);
}

public static async Task DeleteKeysAsync(
    ITokenStore store,
    IEnumerable<string> keys,
    CancellationToken ct = default)
{
    foreach (var key in keys.Distinct(StringComparer.Ordinal))
    {
        await store.DeleteAsync(key, ct).ConfigureAwait(false);
    }
}
```

Create:

```csharp
public sealed record McpAuthResult(
    bool Succeeded,
    string? Error);

public interface IMcpOAuthReauthenticator
{
    Task<McpAuthResult> ReauthenticateAsync(
        McpHttpServerConfig config,
        CancellationToken cancellationToken = default);
}
```

`McpOAuthProvider.ForceReauthorizeAsync` deletes only `mcp-token:<canonicalResource>`, calls the existing discovery/PKCE acquisition path proactively, preserves `mcp-client:<issuer>`, and returns an actionable `McpAuthResult` for cancellation/discovery/exchange failures.

- [ ] **Step 4: Run secret/auth tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~McpSecretStoreTests|FullyQualifiedName~McpOAuthTokenLifecycleTests|FullyQualifiedName~McpClientIdResolutionTests|FullyQualifiedName~McpUnauthorizedResolutionFlowTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Mcp\McpSecretStore.cs src\Coda.Mcp\Auth\McpOAuthProvider.cs src\Coda.Mcp\Auth\IMcpOAuthReauthenticator.cs src\Coda.Mcp\Auth\DefaultMcpOAuthReauthenticator.cs tests\Engine.Tests\McpSecretStoreTests.cs tests\Engine.Tests\McpAuthTests.cs
git commit -m "feat(mcp): add staged secrets and explicit oauth reauthentication" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 4: Expose runtime errors and per-server capabilities

**Files:**
- Modify: `src/Coda.Mcp/McpClientManager.cs`
- Modify: `tests/Engine.Tests/McpLifecycleTests.cs`
- Modify: `tests/Engine.Tests/McpReadModelTests.cs`

- [ ] **Step 1: Write failing runtime-detail tests**

```csharp
[Fact]
public async Task Failed_connect_records_error_and_success_or_stop_clears_it()
{
    var manager = new McpClientManager(
        new SequencedHttpFactory(
            new FakeMcpClient("server")
            {
                ThrowOnInit = "authentication failed",
            },
            new FakeMcpClient("server")));
    var config = new McpHttpServerConfig(
        new Uri("https://example.test/mcp"),
        new Dictionary<string, string>(),
        McpAuthConfig.Default);

    var failed = await manager.ConnectServerAsync("server", config);
    Assert.False(failed.Connected);
    Assert.Equal("authentication failed", manager.LastConnectionErrorFor("server"));

    var connected = await manager.ConnectServerAsync("server", config);
    Assert.True(connected.Connected);
    Assert.Null(manager.LastConnectionErrorFor("server"));

    await manager.DisconnectServerAsync("server");
    Assert.Null(manager.LastConnectionErrorFor("server"));
}

[Fact]
public async Task Per_server_prompt_and_resource_queries_do_not_fan_out()
{
    var first = new FakeMcpClient("first")
    {
        Prompts = [new McpPromptInfo("first", "p1", "one")],
        Resources = [new McpResourceInfo("first", "file:///one", "r1", null)],
    };
    var second = new FakeMcpClient("second")
    {
        Prompts = [new McpPromptInfo("second", "p2", "two")],
        Resources = [new McpResourceInfo("second", "file:///two", "r2", null)],
    };
    var manager = new McpClientManager([first, second]);

    Assert.Equal("p1", Assert.Single(await manager.ServerPromptsAsync("first")).Name);
    Assert.Equal("r1", Assert.Single(await manager.ServerResourcesAsync("first")).Name);
    Assert.Equal(1, first.PromptCalls);
    Assert.Equal(0, second.PromptCalls);
}

private sealed class SequencedHttpFactory(
    params IMcpClient[] clients) : IMcpHttpClientFactory
{
    private readonly Queue<IMcpClient> remaining = new(clients);

    public IMcpClient Create(
        string serverName,
        McpHttpServerConfig config) =>
        this.remaining.Dequeue();
}
```

Extend the existing local `FakeMcpClient` with:

```csharp
public string? ThrowOnInit { get; init; }
public IReadOnlyList<McpPromptInfo> Prompts { get; init; } = [];
public IReadOnlyList<McpResourceInfo> Resources { get; init; } = [];
public int PromptCalls { get; private set; }
public int ResourceCalls { get; private set; }

public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(
    CancellationToken cancellationToken = default) =>
    this.ThrowOnInit is null
        ? Task.FromResult<IReadOnlyList<McpToolInfo>>([])
        : Task.FromException<IReadOnlyList<McpToolInfo>>(
            new InvalidOperationException(this.ThrowOnInit));

public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(
    CancellationToken cancellationToken = default)
{
    this.PromptCalls++;
    return Task.FromResult(this.Prompts);
}

public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(
    CancellationToken cancellationToken = default)
{
    this.ResourceCalls++;
    return Task.FromResult(this.Resources);
}
```

- [ ] **Step 2: Run lifecycle/read tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpReadModelTests"`

Expected: FAIL because runtime failures are discarded and capabilities only have fan-out APIs.

- [ ] **Step 3: Add sanitized error state and targeted capability methods**

Maintain `Dictionary<string, string> lastConnectionErrors`. A failed connection stores its sanitized real error; successful connect and clean intentional disconnect remove it. Failed adoption must leave no client/tools.

During disconnect, remove the client/tools first, then dispose inside `try/catch`. A disposal failure records its sanitized error, still increments `Version`, and returns `true` because the server is no longer effective in the manager; a later successful connect clears that error. This keeps failed disposal from re-exposing stale tools or blocking recovery.

```csharp
public string? LastConnectionErrorFor(string serverName) =>
    this.lastConnectionErrors.GetValueOrDefault(serverName);

public Task<IReadOnlyList<McpPromptInfo>> ServerPromptsAsync(
    string serverName,
    CancellationToken cancellationToken = default);

public Task<IReadOnlyList<McpResourceInfo>> ServerResourcesAsync(
    string serverName,
    CancellationToken cancellationToken = default);
```

Target exactly one connected client and return an empty list when absent. Propagate caller cancellation; surface other failures through `LastConnectionErrorFor` and return an empty detail list.

- [ ] **Step 4: Run lifecycle/read tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpReadModelTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Mcp\McpClientManager.cs tests\Engine.Tests\McpLifecycleTests.cs tests\Engine.Tests\McpReadModelTests.cs
git commit -m "feat(mcp): expose runtime failure and server detail state" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 5: Build the redacted read-only management service

**Files:**
- Create: `src/Coda.Tui/Mcp/McpManagementModels.cs`
- Create: `src/Coda.Tui/Mcp/McpServerNameValidator.cs`
- Create: `src/Coda.Tui/Mcp/McpManagementService.cs`
- Modify: `src/Coda.Tui/Repl/CommandContext.cs`
- Create: `tests/Coda.Tui.Tests/McpManagementTestHarness.cs`
- Create: `tests/Coda.Tui.Tests/McpManagementReadTests.cs`

- [ ] **Step 1: Write failing physical-list/detail/redaction tests**

```csharp
[Fact]
public async Task Refresh_returns_both_physical_rows_and_attaches_runtime_only_to_effective_row()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteUser(
        """{"mcpServers":{"shared":{"command":"user"}}}""");
    harness.WriteProject(
        """{"mcpServers":{"shared":{"command":"project"}}}""");
    await harness.ConnectEffectiveAsync("shared");

    var snapshot = await harness.Service.RefreshAsync(CancellationToken.None);

    Assert.Equal(2, snapshot.Servers.Length);
    var user = snapshot.Servers.Single(
        server => server.Key.Scope == McpConfigScope.User);
    var project = snapshot.Servers.Single(
        server => server.Key.Scope == McpConfigScope.Project);
    Assert.False(user.IsEffective);
    Assert.Equal(McpConnectionState.Overridden, user.Connection);
    Assert.True(project.IsEffective);
    Assert.Equal(McpConnectionState.Connected, project.Connection);
}

[Fact]
public async Task Detail_and_edit_draft_never_contain_secret_values()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    await harness.Store.SetAsync("mcp:server/header/Auth", "super-secret");
    harness.WriteProject(
        """{"mcpServers":{"server":{"type":"http","url":"https://example.test/mcp","headers":{"Auth":"coda-secret:mcp:server/header/Auth"},"auth":{"mode":"bearer","token":"${TOKEN}"}}}}""");

    var key = new McpServerKey(McpConfigScope.Project, "server");
    var detail = await harness.Service.GetDetailAsync(key, CancellationToken.None);
    var draft = await harness.Service.CreateEditDraftAsync(key, CancellationToken.None);

    Assert.NotNull(detail);
    Assert.NotNull(draft);
    Assert.DoesNotContain("super-secret", detail.ToString(), StringComparison.Ordinal);
    Assert.DoesNotContain("super-secret", draft.ToString(), StringComparison.Ordinal);
    Assert.Equal(McpSecretSource.Managed, detail.Headers.Single().Source);
    Assert.Equal(McpSecretSource.Environment, detail.BearerToken!.Source);
}

[Fact]
public async Task Detail_contains_only_the_selected_servers_tools_prompts_and_resources()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.RuntimeFactory.Tools =
        [new McpToolInfo("echo", "Echo", "{}", true)];
    harness.RuntimeFactory.Prompts =
        [new McpPromptInfo("server", "review", "Review code")];
    harness.RuntimeFactory.Resources =
        [new McpResourceInfo("server", "file:///readme", "README", "text/plain")];
    harness.WriteProject(
        """{"mcpServers":{"server":{"type":"http","url":"https://example.test/mcp"}}}""");
    await harness.ConnectEffectiveAsync("server");

    var detail = await harness.Service.GetDetailAsync(
        new McpServerKey(McpConfigScope.Project, "server"),
        CancellationToken.None);

    Assert.Equal("echo", Assert.Single(detail!.Tools).Name);
    Assert.Equal("review", Assert.Single(detail.Prompts).Name);
    Assert.Equal("README", Assert.Single(detail.Resources).Name);
}

[Fact]
public async Task Corrupt_configuration_is_a_read_error_not_an_empty_snapshot()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteProject("{");

    var snapshot = await harness.Service.RefreshAsync(CancellationToken.None);

    Assert.NotNull(snapshot.ReadError);
    Assert.Contains("valid JSON", snapshot.ReadError, StringComparison.OrdinalIgnoreCase);
}
```

Create this reusable test harness:

```csharp
internal sealed class McpManagementTestHarness : IAsyncDisposable
{
    private readonly string root;

    private McpManagementTestHarness(
        string root,
        string user,
        string project,
        TestTokenStore store,
        CountingHttpFactory runtimeFactory,
        McpClientManager runtime,
        SuccessfulOAuthReauthenticator oauth,
        IMcpManagementService service)
    {
        this.root = root;
        this.User = user;
        this.Project = project;
        this.Store = store;
        this.RuntimeFactory = runtimeFactory;
        this.Runtime = runtime;
        this.OAuth = oauth;
        this.Service = service;
    }

    public string User { get; }
    public string Project { get; }
    public TestTokenStore Store { get; }
    public CountingHttpFactory RuntimeFactory { get; }
    public McpClientManager Runtime { get; }
    public SuccessfulOAuthReauthenticator OAuth { get; }
    public IMcpManagementService Service { get; }

    public static Task<McpManagementTestHarness> CreateAsync(
        IMcpConfigMutator? mutator = null)
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var user = Path.Combine(root, "user");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(project);
        var store = new TestTokenStore();
        var runtimeFactory = new CountingHttpFactory();
        var runtime = new McpClientManager(runtimeFactory);
        var oauth = new SuccessfulOAuthReauthenticator();
        IMcpManagementService service = new McpManagementService(
            project,
            user,
            runtime,
            store,
            oauth,
            new RecordingUiEvents(),
            mutator);
        return Task.FromResult(new McpManagementTestHarness(
            root,
            user,
            project,
            store,
            runtimeFactory,
            runtime,
            oauth,
            service));
    }

    public void WriteUser(string json) =>
        File.WriteAllText(Path.Combine(this.User, ".mcp.json"), json);

    public void WriteProject(string json) =>
        File.WriteAllText(Path.Combine(this.Project, ".mcp.json"), json);

    public async Task ConnectEffectiveAsync(string name)
    {
        var config = McpConfig.Load(this.Project, this.User)[name];
        var result = await this.Runtime.ConnectServerAsync(name, config);
        Assert.True(result.Connected, result.Error);
    }

    public async ValueTask DisposeAsync()
    {
        await this.Runtime.DisposeAsync();
        Directory.Delete(this.root, recursive: true);
    }
}

internal sealed class TestTokenStore : ITokenStore
{
    private readonly Dictionary<string, string> values =
        new(StringComparer.Ordinal);

    public Task<string?> GetAsync(
        string key,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(this.values.GetValueOrDefault(key));

    public Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        this.values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        this.values.Remove(key);
        return Task.CompletedTask;
    }
}

internal sealed class CountingHttpFactory : IMcpHttpClientFactory
{
    private readonly Queue<string?> failures = new();
    public int ConnectCalls { get; private set; }
    public IReadOnlyList<McpToolInfo> Tools { get; set; } = [];
    public IReadOnlyList<McpPromptInfo> Prompts { get; set; } = [];
    public IReadOnlyList<McpResourceInfo> Resources { get; set; } = [];

    public void FailNext(string message) =>
        this.failures.Enqueue(message);

    public IMcpClient Create(
        string serverName,
        McpHttpServerConfig config) =>
        new TestMcpClient(
            serverName,
            () => this.ConnectCalls++,
            this.failures.Count > 0 ? this.failures.Dequeue() : null,
            this.Tools,
            this.Prompts,
            this.Resources);
}

internal sealed class TestMcpClient(
    string serverName,
    Action onInitialize,
    string? initializeFailure,
    IReadOnlyList<McpToolInfo> tools,
    IReadOnlyList<McpPromptInfo> prompts,
    IReadOnlyList<McpResourceInfo> resources) : IMcpClient
{
    public string ServerName => serverName;

    public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        onInitialize();
        if (initializeFailure is not null)
        {
            throw new InvalidOperationException(initializeFailure);
        }

        return Task.FromResult(tools);
    }

    public Task<(string Text, bool IsError)> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(("ok", false));

    public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(resources);

    public Task<string> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(prompts);

    public Task<string> GetPromptAsync(
        string name,
        JsonNode? arguments,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SuccessfulOAuthReauthenticator
    : IMcpOAuthReauthenticator
{
    public int Calls { get; private set; }

    public Task<McpAuthResult> ReauthenticateAsync(
        McpHttpServerConfig config,
        CancellationToken cancellationToken = default)
    {
        this.Calls++;
        return Task.FromResult(new McpAuthResult(true, null));
    }
}
```

- [ ] **Step 2: Run management read tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpManagementReadTests"`

Expected: FAIL because management models/service do not exist.

- [ ] **Step 3: Implement read, detail, draft, revision, and redaction**

Create the contracts listed at the start of this plan. Add:

```csharp
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
```

`McpManagementService : IMcpManagementService` uses this contract and constructor:

```csharp
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

internal McpManagementService(
    string workingDirectory,
    string? userMcpDir,
    McpClientManager? runtime,
    ITokenStore credentials,
    IMcpOAuthReauthenticator oauth,
    IUiEventPublisher events,
    IMcpConfigMutator? configMutator = null);
```

The service owns a private `SemaphoreSlim mutationGate = new(1, 1)` and wraps every commit/toggle/delete/reauth/lifecycle mutation in it. Read-only refresh/detail/draft calls remain concurrent and revision-checked commits reject stale data.

Expose:

```csharp
internal Task<McpManagementSnapshot> RefreshAsync(CancellationToken ct);
internal Task<McpServerDetail?> GetDetailAsync(McpServerKey key, CancellationToken ct);
internal Task<McpServerDraft?> CreateEditDraftAsync(McpServerKey key, CancellationToken ct);
```

`RefreshAsync` calls strict physical load and reports parse/read failures in `ReadError`. `ProjectScopeAvailable` is true when the configured working directory exists and project MCP loading is enabled. Runtime connected/error state is attached only to the effective row. Detail capability calls use a linked five-second timeout and sanitize all names/descriptions/errors at rendering boundaries.

Raise `IMcpManagementService.Changed` after each successful persistence/runtime mutation and after an explicit lifecycle start/stop/restart; never raise it while holding a config/runtime lock.

Secret classification:

- whole `coda-secret:` reference → Managed, display `***** (encrypted)`;
- any embedded `${VAR}` → Environment, display `***** (environment)`;
- non-empty literal → Literal, display `*****`;
- absent → None.

Edit drafts preserve names/source classifications but set every secret change to `Unchanged` with no replacement value.

Cache one service on `CommandContext` so textual commands and the overlay share it:

```csharp
internal IMcpManagementService? McpManagement { get; set; }
```

- [ ] **Step 4: Run management read tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpManagementReadTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Mcp\McpManagementModels.cs src\Coda.Tui\Mcp\McpServerNameValidator.cs src\Coda.Tui\Mcp\McpManagementService.cs src\Coda.Tui\Repl\CommandContext.cs tests\Coda.Tui.Tests\McpManagementTestHarness.cs tests\Coda.Tui.Tests\McpManagementReadTests.cs
git commit -m "feat(tui): add redacted mcp management read service" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 6: Implement validated add, incremental edit, and atomic rename

**Files:**
- Modify: `src/Coda.Tui/Mcp/McpManagementModels.cs`
- Modify: `src/Coda.Tui/Mcp/McpServerNameValidator.cs`
- Modify: `src/Coda.Tui/Mcp/McpManagementService.cs`
- Modify: `tests/Coda.Tui.Tests/McpManagementTestHarness.cs`
- Create: `tests/Coda.Tui.Tests/McpManagementEditTests.cs`

- [ ] **Step 1: Write failing validation, rename, secret migration, and rollback tests**

```csharp
[Theory]
[InlineData("")]
[InlineData("bad/name")]
[InlineData("bad\\name")]
[InlineData("bad\nname")]
public void Invalid_server_names_are_rejected(string name)
{
    Assert.NotNull(McpServerNameValidator.Validate(name));
}

[Fact]
public async Task Rename_with_unchanged_managed_secret_migrates_reference_and_preserves_disabled()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    await harness.Store.SetAsync("mcp:old/header/Auth", "secret");
    harness.WriteProject(
        """{"mcpServers":{"old":{"type":"http","url":"https://example.test/mcp","disabled":true,"headers":{"Auth":"coda-secret:mcp:old/header/Auth"}}}}""");
    var original = new McpServerKey(McpConfigScope.Project, "old");
    var draft = (await harness.Service.CreateEditDraftAsync(original, CancellationToken.None))!
        with { Name = "new" };

    var preview = await harness.Service.PrepareEditAsync(
        original,
        draft,
        CancellationToken.None);
    var result = await harness.Service.CommitEditAsync(
        preview,
        CancellationToken.None);

    Assert.Equal(McpMutationStatus.Succeeded, result.Status);
    var physical = McpConfig.LoadPhysicalEntries(harness.Project, harness.User);
    var renamed = Assert.Single(physical);
    Assert.Equal("new", renamed.Key.Name);
    Assert.True(renamed.Config.Disabled);
    var binding = Assert.Single(McpSecretStore.References(renamed.Config));
    Assert.StartsWith("mcp:new/header/Auth/", binding.StoreKey, StringComparison.Ordinal);
    Assert.Equal("secret", await harness.Store.GetAsync(binding.StoreKey));
    Assert.Null(await harness.Store.GetAsync("mcp:old/header/Auth"));
}

[Fact]
public async Task Write_failure_preserves_old_config_secret_and_runtime()
{
    await using var harness = await McpManagementTestHarness.CreateAsync(
        mutator: new ThrowingConfigMutator("write failed"));
    await harness.Store.SetAsync("mcp:old/auth/token", "secret");
    harness.WriteProject(
        """{"mcpServers":{"old":{"type":"http","url":"https://example.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:old/auth/token"}}}}""");
    var before = File.ReadAllText(Path.Combine(harness.Project, ".mcp.json"));
    var key = new McpServerKey(McpConfigScope.Project, "old");
    var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!
        with { Name = "new" };
    var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

    var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

    Assert.Equal(McpMutationStatus.Rejected, result.Status);
    Assert.Equal(before, File.ReadAllText(Path.Combine(harness.Project, ".mcp.json")));
    Assert.Equal("secret", await harness.Store.GetAsync("mcp:old/auth/token"));
}

internal sealed class ThrowingConfigMutator(string message)
    : IMcpConfigMutator
{
    private Exception Failure() => new McpException(message);

    public void Upsert(
        McpConfigScope scope,
        string name,
        McpServerConfig config,
        bool disabled,
        string workingDirectory,
        string? userMcpDir) =>
        throw this.Failure();

    public void ReplaceEntry(
        McpConfigScope scope,
        string currentName,
        string newName,
        McpServerConfig config,
        bool disabled,
        string workingDirectory,
        string? userMcpDir) =>
        throw this.Failure();

    public bool Remove(
        McpConfigScope scope,
        string name,
        string workingDirectory,
        string? userMcpDir) =>
        throw this.Failure();

    public bool SetDisabled(
        McpConfigScope scope,
        string name,
        bool disabled,
        string workingDirectory,
        string? userMcpDir) =>
        throw this.Failure();
}
```

- [ ] **Step 2: Run management edit tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpManagementEditTests"`

Expected: FAIL because prepare/commit edit, revision checks, and transactional migration are absent.

- [ ] **Step 3: Implement validation, prepared revisions, and staged commit ordering**

Name validation returns a concrete error for blank/whitespace, control characters, `/`, `\`, and same-scope collisions.

```csharp
internal static class McpServerNameValidator
{
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Server name cannot be blank.";
        }

        if (name.Any(char.IsControl))
        {
            return "Server name cannot contain control characters.";
        }

        if (name.Contains('/') || name.Contains('\\'))
        {
            return "Server name cannot contain path separators.";
        }

        return null;
    }
}
```

Expose:

```csharp
internal Task<McpEditPreview> PrepareAddAsync(
    McpServerDraft draft,
    CancellationToken ct);

internal Task<McpEditPreview> PrepareEditAsync(
    McpServerKey original,
    McpServerDraft draft,
    CancellationToken ct);

internal Task<McpMutationResult> CommitAddAsync(
    McpEditPreview preview,
    CancellationToken ct);

internal Task<McpMutationResult> CommitEditAsync(
    McpEditPreview preview,
    CancellationToken ct);
```

Preparation validates transport fields and stores SHA-256 hashes of both config files. Rename warnings explicitly say whether the new name will override an opposite-scope definition or whether removing the old name will reveal one.

Commit order:

1. reload and reject stale revisions;
2. derive the final config from the original plus incremental draft;
3. stage replacement secrets under versioned keys;
4. on rename, read and restage unchanged managed secrets under the new name;
5. perform one config write;
6. on write failure, delete staged keys and leave old bytes/secrets/runtime untouched;
7. after success, scan every physical post-write definition and delete only old keys no longer referenced;
8. reload snapshot and return the new `(scope, name)` selection.

If any remaining physical file cannot be parsed during post-write reference scanning, keep the old keys and return a warning status; conservative credential retention is safer than deleting a possibly shared secret.

Add starts enabled and persists only; it does not implicitly connect. Immediate runtime behavior for edit/toggle/delete is implemented in Task 7, matching the approved action semantics.

- [ ] **Step 4: Run management edit tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpManagementEditTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Mcp\McpManagementModels.cs src\Coda.Tui\Mcp\McpServerNameValidator.cs src\Coda.Tui\Mcp\McpManagementService.cs tests\Coda.Tui.Tests\McpManagementTestHarness.cs tests\Coda.Tui.Tests\McpManagementEditTests.cs
git commit -m "feat(tui): centralize validated mcp add edit and rename" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 7: Reconcile live runtime after managed mutations

**Files:**
- Modify: `src/Coda.Tui/Mcp/McpManagementService.cs`
- Create: `tests/Coda.Tui.Tests/McpManagementRuntimeTests.cs`

- [ ] **Step 1: Write failing effective/overridden/restart tests**

```csharp
[Fact]
public async Task Disabling_project_override_stops_runtime_without_revealing_user()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteUser(
        """{"mcpServers":{"shared":{"type":"http","url":"https://user.test/mcp"}}}""");
    harness.WriteProject(
        """{"mcpServers":{"shared":{"type":"http","url":"https://project.test/mcp"}}}""");
    await harness.ConnectEffectiveAsync("shared");

    var result = await harness.Service.SetEnabledAsync(
        new McpServerKey(McpConfigScope.Project, "shared"),
        enabled: false,
        CancellationToken.None);

    Assert.Equal(McpMutationStatus.Succeeded, result.Status);
    Assert.False(harness.Runtime.IsServerConnected("shared"));
    var snapshot = result.Snapshot;
    Assert.Equal(
        McpConnectionState.Overridden,
        snapshot.Servers.Single(server => server.Key.Scope == McpConfigScope.User).Connection);
    Assert.False(
        snapshot.Servers.Single(server => server.Key.Scope == McpConfigScope.Project).Enabled);
}

[Fact]
public async Task Editing_an_overridden_row_changes_persistence_without_restarting_runtime()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteUser(
        """{"mcpServers":{"shared":{"type":"http","url":"https://user.test/mcp"}}}""");
    harness.WriteProject(
        """{"mcpServers":{"shared":{"type":"http","url":"https://project.test/mcp"}}}""");
    await harness.ConnectEffectiveAsync("shared");
    var restartsBefore = harness.RuntimeFactory.ConnectCalls;
    var key = new McpServerKey(McpConfigScope.User, "shared");
    var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!
        with { Url = "https://changed.test/mcp" };
    var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

    await harness.Service.CommitEditAsync(preview, CancellationToken.None);

    Assert.Equal(restartsBefore, harness.RuntimeFactory.ConnectCalls);
    Assert.True(harness.Runtime.IsServerConnected("shared"));
}

[Fact]
public async Task Enabling_an_effective_server_starts_it_immediately()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteProject(
        """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","disabled":true}}}""");

    var result = await harness.Service.SetEnabledAsync(
        new McpServerKey(McpConfigScope.Project, "server"),
        enabled: true,
        CancellationToken.None);

    Assert.Equal(McpMutationStatus.Succeeded, result.Status);
    Assert.True(harness.Runtime.IsServerConnected("server"));
}

[Fact]
public async Task Failed_effective_edit_restart_keeps_saved_config_and_real_error()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteProject(
        """{"mcpServers":{"server":{"type":"http","url":"https://old.test/mcp"}}}""");
    await harness.ConnectEffectiveAsync("server");
    harness.RuntimeFactory.FailNext("restart failed");
    var key = new McpServerKey(McpConfigScope.Project, "server");
    var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!
        with { Url = "https://new.test/mcp" };
    var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

    var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

    Assert.Equal(McpMutationStatus.SavedWithRuntimeError, result.Status);
    Assert.Equal(
        new Uri("https://new.test/mcp"),
        Assert.IsType<McpHttpServerConfig>(
            McpConfig.LoadPhysicalEntries(harness.Project, harness.User).Single().Config).Url);
    Assert.Equal("restart failed", harness.Runtime.LastConnectionErrorFor("server"));
    Assert.False(harness.Runtime.IsServerConnected("server"));
}

[Fact]
public async Task Effective_rename_stops_old_name_and_starts_new_name()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteProject(
        """{"mcpServers":{"old":{"type":"http","url":"https://x.test/mcp"}}}""");
    await harness.ConnectEffectiveAsync("old");
    var key = new McpServerKey(McpConfigScope.Project, "old");
    var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!
        with { Name = "new" };
    var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

    var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

    Assert.Equal(McpMutationStatus.Succeeded, result.Status);
    Assert.False(harness.Runtime.IsServerConnected("old"));
    Assert.True(harness.Runtime.IsServerConnected("new"));
}
```

- [ ] **Step 2: Run runtime reconciliation tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpManagementRuntimeTests"`

Expected: FAIL because managed mutations do not reconcile runtime.

- [ ] **Step 3: Implement before/after effective-map reconciliation**

```csharp
private async Task<McpRuntimeReconcileResult> ReconcileAsync(
    IReadOnlyDictionary<string, McpServerConfig> before,
    IReadOnlyDictionary<string, McpServerConfig> after,
    IReadOnlySet<string> touchedNames,
    IReadOnlySet<string> forceRestartNames,
    CancellationToken ct);
```

For each touched name:

- disconnect when the old effective connectable definition disappears, changes, or is force-restarted;
- connect when the new effective connectable definition appears, changes, or is force-restarted;
- resolve managed/environment references immediately before connect;
- do nothing when only an overridden physical row changed;
- preserve disabled higher-precedence shadowing because maps come from merge-then-filter `McpConfig.Load`;
- publish `McpRuntimeChangedEvent` after reconciliation.

If persistence succeeds but reconnect fails, return `SavedWithRuntimeError`, keep saved configuration, and expose the manager’s real sanitized error.

- [ ] **Step 4: Run runtime reconciliation tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpManagementRuntimeTests|FullyQualifiedName~McpManagementEditTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Mcp\McpManagementService.cs tests\Coda.Tui.Tests\McpManagementRuntimeTests.cs
git commit -m "feat(tui): reconcile mcp runtime after managed mutations" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 8: Add confirmed delete and ownership-aware reauthentication

**Files:**
- Modify: `src/Coda.Tui/Mcp/McpManagementModels.cs`
- Modify: `src/Coda.Tui/Mcp/McpManagementService.cs`
- Create: `tests/Coda.Tui.Tests/McpManagementDeleteTests.cs`
- Create: `tests/Coda.Tui.Tests/McpManagementAuthenticationTests.cs`

- [ ] **Step 1: Write failing delete/reveal/shared-secret/reauth tests**

```csharp
[Fact]
public async Task Delete_project_override_reveals_and_starts_enabled_user_definition()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteUser(
        """{"mcpServers":{"shared":{"type":"http","url":"https://user.test/mcp"}}}""");
    harness.WriteProject(
        """{"mcpServers":{"shared":{"type":"http","url":"https://project.test/mcp","disabled":true}}}""");
    var key = new McpServerKey(McpConfigScope.Project, "shared");

    var preview = await harness.Service.PrepareDeleteAsync(key, CancellationToken.None);
    Assert.True(preview.RevealsLowerScope);
    Assert.Contains("project", preview.Confirmation, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("user", preview.Confirmation, StringComparison.OrdinalIgnoreCase);

    var result = await harness.Service.CommitDeleteAsync(preview, CancellationToken.None);

    Assert.Equal(McpMutationStatus.Succeeded, result.Status);
    Assert.True(harness.Runtime.IsServerConnected("shared"));
    Assert.Single(McpConfig.LoadPhysicalEntries(harness.Project, harness.User));
}

[Fact]
public async Task Delete_keeps_a_managed_key_still_referenced_elsewhere()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    await harness.Store.SetAsync("shared-key", "secret");
    harness.WriteUser(
        """{"mcpServers":{"one":{"command":"a","env":{"TOKEN":"coda-secret:shared-key"}},"two":{"command":"b","env":{"TOKEN":"coda-secret:shared-key"}}}}""");
    var preview = await harness.Service.PrepareDeleteAsync(
        new McpServerKey(McpConfigScope.User, "one"),
        CancellationToken.None);

    await harness.Service.CommitDeleteAsync(preview, CancellationToken.None);

    Assert.Equal("secret", await harness.Store.GetAsync("shared-key"));
}

[Fact]
public async Task Delete_write_failure_preserves_config_secret_and_runtime()
{
    await using var harness = await McpManagementTestHarness.CreateAsync(
        new ThrowingConfigMutator("write failed"));
    await harness.Store.SetAsync("mcp:server/auth/token", "secret");
    const string json =
        """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:server/auth/token"}}}}""";
    harness.WriteProject(json);
    await harness.ConnectEffectiveAsync("server");
    var preview = await harness.Service.PrepareDeleteAsync(
        new McpServerKey(McpConfigScope.Project, "server"),
        CancellationToken.None);

    var result = await harness.Service.CommitDeleteAsync(
        preview,
        CancellationToken.None);

    Assert.Equal(McpMutationStatus.Rejected, result.Status);
    Assert.Equal(
        json,
        File.ReadAllText(Path.Combine(harness.Project, ".mcp.json")));
    Assert.Equal(
        "secret",
        await harness.Store.GetAsync("mcp:server/auth/token"));
    Assert.True(harness.Runtime.IsServerConnected("server"));
}

[Fact]
public async Task Delete_rejects_a_stale_prepared_revision()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteProject(
        """{"mcpServers":{"server":{"command":"x"}}}""");
    var preview = await harness.Service.PrepareDeleteAsync(
        new McpServerKey(McpConfigScope.Project, "server"),
        CancellationToken.None);
    harness.WriteUser(
        """{"mcpServers":{"external":{"command":"changed"}}}""");

    var result = await harness.Service.CommitDeleteAsync(
        preview,
        CancellationToken.None);

    Assert.Equal(McpMutationStatus.Rejected, result.Status);
    Assert.Single(McpConfig.LoadPhysicalEntries(
        harness.Project,
        harness.User).Where(entry => entry.Key.Name == "server"));
}

[Theory]
[InlineData("""{"type":"http","url":"https://x.test/mcp"}""", McpReauthenticationKind.OAuth)]
[InlineData("""{"type":"http","url":"https://x.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:x/auth/token"}}""", McpReauthenticationKind.StoredSecret)]
[InlineData("""{"type":"http","url":"https://x.test/mcp","headers":{"Auth":"Bearer ${TOKEN}"}}""", McpReauthenticationKind.EnvironmentOwned)]
[InlineData("""{"command":"server"}""", McpReauthenticationKind.Unavailable)]
public async Task Reauthentication_classifies_credential_ownership(
    string serverJson,
    McpReauthenticationKind expected)
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteProject($$"""{"mcpServers":{"x":{{serverJson}}}}""");

    var plan = await harness.Service.PrepareReauthenticationAsync(
        new McpServerKey(McpConfigScope.Project, "x"),
        CancellationToken.None);

    Assert.Equal(expected, plan.Kind);
}

[Fact]
public async Task Stored_secret_reauthentication_replaces_value_after_confirmation_plan()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    await harness.Store.SetAsync("mcp:server/auth/token", "old");
    harness.WriteProject(
        """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:server/auth/token"}}}}""");
    var key = new McpServerKey(McpConfigScope.Project, "server");
    var plan = await harness.Service.PrepareReauthenticationAsync(
        key,
        CancellationToken.None);

    var result = await harness.Service.ReauthenticateAsync(
        plan,
        new Dictionary<string, McpSecretReplacement>(StringComparer.Ordinal)
        {
            ["auth/token"] = new McpSecretReplacement("new"),
        },
        CancellationToken.None);

    Assert.Equal(McpMutationStatus.Succeeded, result.Status);
    var config = Assert.IsType<McpHttpServerConfig>(
        McpConfig.LoadPhysicalEntries(harness.Project, harness.User).Single().Config);
    var binding = Assert.Single(McpSecretStore.References(config));
    Assert.Equal("new", await harness.Store.GetAsync(binding.StoreKey));
    Assert.Null(await harness.Store.GetAsync("mcp:server/auth/token"));
}

[Fact]
public async Task Environment_owned_reauthentication_writes_nothing()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    const string json =
        """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","headers":{"Auth":"Bearer ${TOKEN}"}}}}""";
    harness.WriteProject(json);
    var key = new McpServerKey(McpConfigScope.Project, "server");
    var plan = await harness.Service.PrepareReauthenticationAsync(
        key,
        CancellationToken.None);

    var result = await harness.Service.ReauthenticateAsync(
        plan,
        new Dictionary<string, McpSecretReplacement>(),
        CancellationToken.None);

    Assert.Equal(McpMutationStatus.Rejected, result.Status);
    Assert.Equal(
        json,
        File.ReadAllText(Path.Combine(harness.Project, ".mcp.json")));
}

[Fact]
public async Task Unavailable_reauthentication_has_a_disabled_reason()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteProject(
        """{"mcpServers":{"server":{"command":"server"}}}""");

    var plan = await harness.Service.PrepareReauthenticationAsync(
        new McpServerKey(McpConfigScope.Project, "server"),
        CancellationToken.None);

    Assert.Equal(McpReauthenticationKind.Unavailable, plan.Kind);
    Assert.False(string.IsNullOrWhiteSpace(plan.DisabledReason));
}
```

- [ ] **Step 2: Run delete/authentication tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpManagementDeleteTests|FullyQualifiedName~McpManagementAuthenticationTests"`

Expected: FAIL because prepared confirmation/revision operations do not exist.

- [ ] **Step 3: Implement prepared confirmation operations and safe ordering**

Expose:

```csharp
internal Task<McpDeletePreview> PrepareDeleteAsync(
    McpServerKey key,
    CancellationToken ct);

internal Task<McpMutationResult> CommitDeleteAsync(
    McpDeletePreview confirmedPreview,
    CancellationToken ct);

internal Task<McpReauthenticationPlan> PrepareReauthenticationAsync(
    McpServerKey key,
    CancellationToken ct);

internal Task<McpMutationResult> ReauthenticateAsync(
    McpReauthenticationPlan plan,
    IReadOnlyDictionary<string, McpSecretReplacement> replacements,
    CancellationToken ct);
```

Delete confirmation names server, scope, and override/reveal effect. Commit rejects stale hashes, writes first, reconciles runtime, scans all remaining physical definitions, deletes only globally unreferenced keys, then selects the nearest row. A post-write parse failure skips secret deletion and reports a warning rather than guessing that a key is unreferenced.

Reauthentication:

- OAuth: confirmation first, state that the canonical URL token may be shared by other same-URL definitions, force token replacement without deleting DCR registration, and reconnect only when effective and enabled.
- Managed bearer/header fields: confirmation first, require a masked replacement per listed field, stage references, atomically update config, clean old unreferenced keys, reconnect when effective/enabled.
- Environment-owned `${VAR}` bearer/header credentials: return `Rejected` with a clear externally managed
  explanation; no write.
- No auth, non-managed literal credentials, and stdio-only definitions: `Unavailable` with a concrete reason; use
  Edit for literal or stdio values.

A failed write leaves runtime and secrets unchanged. A failed reconnect keeps saved credentials/config and reports `SavedWithRuntimeError`.

- [ ] **Step 4: Run delete/authentication tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpManagementDeleteTests|FullyQualifiedName~McpManagementAuthenticationTests|FullyQualifiedName~McpManagementRuntimeTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Mcp\McpManagementModels.cs src\Coda.Tui\Mcp\McpManagementService.cs tests\Coda.Tui.Tests\McpManagementDeleteTests.cs tests\Coda.Tui.Tests\McpManagementAuthenticationTests.cs
git commit -m "feat(tui): add confirmed mcp delete and reauthentication" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 9: Route textual MCP commands through the shared service

**Files:**
- Modify: `src/Coda.Tui/Commands/McpCommand.cs`
- Modify: `src/Coda.Tui/Commands/McpFlagParser.cs`
- Modify: `src/Coda.Tui/Commands/McpView.cs`
- Modify: `tests/Coda.Tui.Tests/McpCommandTests.cs`
- Modify: `tests/Coda.Tui.Tests/McpFlagParserTests.cs`
- Modify: `tests/Coda.Tui.Tests/McpViewTests.cs`

- [ ] **Step 1: Write failing textual compatibility and new-operation tests**

```csharp
[Fact]
public async Task Bare_textual_mcp_lists_both_physical_scopes()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteUser(
        """{"mcpServers":{"shared":{"command":"user"}}}""");
    harness.WriteProject(
        """{"mcpServers":{"shared":{"command":"project"}}}""");
    var built = TestAppBuilder.BuildApp(
        store: harness.Store,
        workingDirectory: harness.Project);
    var context = built.Context;
    var console = built.Console;
    context.Mcp = harness.Runtime;
    context.CredentialStore = harness.Store;
    context.McpManagement = harness.Service;

    await new McpCommand().ExecuteAsync(context, [], CancellationToken.None);

    Assert.Equal(2, console.Output.Split("shared", StringSplitOptions.None).Length - 1);
    Assert.Contains("[user]", console.Output, StringComparison.Ordinal);
    Assert.Contains("[project]", console.Output, StringComparison.Ordinal);
}

[Fact]
public async Task Noninteractive_remove_refuses_without_confirmation()
{
    await using var harness = await McpManagementTestHarness.CreateAsync();
    harness.WriteProject(
        """{"mcpServers":{"server":{"command":"x"}}}""");
    var built = TestAppBuilder.BuildApp(
        store: harness.Store,
        workingDirectory: harness.Project);
    var context = built.Context;
    var console = built.Console;
    context.Mcp = harness.Runtime;
    context.CredentialStore = harness.Store;
    context.McpManagement = harness.Service;
    context.Prompts = PlainUiPromptService.Instance;

    await new McpCommand().ExecuteAsync(
        context,
        ["remove", "server"],
        CancellationToken.None);

    Assert.Contains("Cancelled", console.Output, StringComparison.OrdinalIgnoreCase);
    Assert.True(McpConfig.Parse(
        File.ReadAllText(Path.Combine(harness.Project, ".mcp.json")))
        .ContainsKey("server"));
}

[Fact]
public void Edit_flags_accept_a_new_name()
{
    var current = new McpServerDraft(
        Name: "old",
        Scope: McpConfigScope.Project,
        Enabled: false,
        Transport: McpTransportKind.Http,
        Command: null,
        Args: [],
        Url: "https://example.test/mcp",
        Environment: [],
        Headers: [],
        AuthMode: McpAuthMode.OAuth,
        ClientId: null,
        Scopes: [],
        BearerToken: new McpSecretChange(
            "auth/token",
            McpSecretChangeKind.Unchanged));
    var parsed = McpFlagParser.ParseEdit(
        current,
        ["--name", "renamed"]);

    Assert.True(parsed.Ok);
    Assert.Equal("renamed", parsed.Draft!.Name);
    Assert.False(parsed.Draft.Enabled);
    Assert.Equal("https://example.test/mcp", parsed.Draft.Url);
}

[Fact]
public void Text_view_sanitizes_control_and_ansi_sequences()
{
    var summary = new McpServerSummary(
        new McpServerKey(
            McpConfigScope.Project,
            "safe\u001b[31m\nspoof"),
        @"C:\project\.mcp.json",
        Enabled: true,
        IsEffective: true,
        Transport: McpTransportKind.Stdio,
        Connection: McpConnectionState.Disconnected,
        LastError: null);

    var text = McpView.FormatList(
        new McpManagementSnapshot(
            ProjectScopeAvailable: true,
            Servers: [summary]));

    Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
    Assert.Equal(2, text.Split(Environment.NewLine).Length);
}
```

- [ ] **Step 2: Run textual command tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpCommandTests|FullyQualifiedName~McpFlagParserTests|FullyQualifiedName~McpViewTests"`

Expected: FAIL because handlers still manipulate config/secrets/runtime directly and cannot rename/reauthenticate.

- [ ] **Step 3: Make command handlers parse/prompt/render only**

Keep the existing add parse result and add a typed incremental edit result:

```csharp
public sealed record McpEditFlagParseResult(
    bool Ok,
    string? Error,
    McpServerDraft? Draft);
```

Add:

```csharp
public static McpEditFlagParseResult ParseEdit(
    McpServerDraft current,
    IReadOnlyList<string> flags);
```

It starts from `current`, applies only explicitly supplied fields (including `--name`), preserves enabled state and every unspecified setting/secret change, and returns a validated incremental draft. The existing `Parse` remains the complete add parser.

Add `/mcp reauth <name>` and edit `--name <new-name>`. Every add/edit/toggle/remove/reauth handler calls prepare, collects confirmation/replacements through `IUiPromptService`, then commits through `IMcpManagementService`.

Change the pure view surface to:

```csharp
public static string FormatList(McpManagementSnapshot snapshot);
public static string FormatInfo(McpServerDetail detail);
```

Both methods call `TerminalTextSanitizer.SanitizeSingleLine` on names, paths, errors, capability names/descriptions, and transport values before formatting; the command then applies `Markup.Escape`.

Keep textual `start`, `stop`, and `restart` and route them through service lifecycle methods so runtime state/error publication has one implementation.

List renders every physical row and its effective/overridden state. All confirmation defaults are false. Plain/Spectre bare `/mcp` remains textual list. Sanitize terminal/control text before Spectre markup escaping.

- [ ] **Step 4: Run textual command tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpCommandTests|FullyQualifiedName~McpFlagParserTests|FullyQualifiedName~McpViewTests"`

Expected: PASS, including existing lifecycle command behavior.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Commands\McpCommand.cs src\Coda.Tui\Commands\McpFlagParser.cs src\Coda.Tui\Commands\McpView.cs tests\Coda.Tui.Tests\McpCommandTests.cs tests\Coda.Tui.Tests\McpFlagParserTests.cs tests\Coda.Tui.Tests\McpViewTests.cs
git commit -m "refactor(tui): route textual mcp commands through management service" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 10: Add immutable browser state and pure key mapping

**Files:**
- Create: `src/Coda.Tui/Ui/Mcp/McpBrowserModels.cs`
- Create: `src/Coda.Tui/Ui/Mcp/McpBrowserState.cs`
- Create: `src/Coda.Tui/Ui/Mcp/McpBrowserKeyMap.cs`
- Create: `tests/Coda.Tui.Tests/McpBrowserStateTests.cs`
- Create: `tests/Coda.Tui.Tests/McpBrowserKeyMapTests.cs`

- [ ] **Step 1: Write failing stable-selection and modal-editor tests**

```csharp
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

[Theory]
[InlineData('a')]
[InlineData('e')]
[InlineData('u')]
public void Printable_action_letters_are_text_in_the_editor(char value)
{
    Assert.Equal(
        McpBrowserCommand.EditorInsert,
        McpBrowserKeyMap.Map(new Key(value), McpBrowserView.Editor));
}

[Fact]
public void Delete_key_edits_the_editor_field_instead_of_deleting_a_server()
{
    Assert.Equal(
        McpBrowserCommand.EditorDelete,
        McpBrowserKeyMap.Map(Key.Delete, McpBrowserView.Editor));
}

private static McpServerSummary Summary(
    McpConfigScope scope,
    string name) =>
    new(
        new McpServerKey(scope, name),
        scope == McpConfigScope.User
            ? @"C:\user\.mcp.json"
            : @"C:\project\.mcp.json",
        Enabled: true,
        IsEffective: true,
        Transport: McpTransportKind.Stdio,
        Connection: McpConnectionState.Disconnected,
        LastError: null);
```

- [ ] **Step 2: Run browser state/key tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpBrowserStateTests|FullyQualifiedName~McpBrowserKeyMapTests"`

Expected: FAIL because browser state/key types do not exist.

- [ ] **Step 3: Implement list/detail/editor state and commands**

```csharp
internal enum McpBrowserView { List, Detail, Editor }
internal enum McpEditorMode { Add, Edit }
internal enum McpEditorField
{
    Scope,
    Name,
    Transport,
    Command,
    Arguments,
    Url,
    Environment,
    Headers,
    AuthMode,
    ClientId,
    Scopes,
    BearerToken,
    Save,
    Cancel,
}

internal enum McpBrowserCommand
{
    None,
    Close,
    MoveUp,
    MoveDown,
    PageUp,
    PageDown,
    MoveToStart,
    MoveToEnd,
    OpenDetail,
    BeginAdd,
    BeginEdit,
    ToggleEnabled,
    Reauthenticate,
    DeleteServer,
    ReturnToList,
    EditorNext,
    EditorPrevious,
    EditorApply,
    EditorCancel,
    EditorBackspace,
    EditorDelete,
    EditorInsert,
}

internal sealed record McpEditorState(
    McpEditorMode Mode,
    McpBrowserView Origin,
    McpServerDraft Draft,
    McpEditorField FocusedField);

internal sealed record McpBrowserState
{
    public static McpBrowserState Empty { get; } = new();
    public McpBrowserView View { get; init; } = McpBrowserView.List;
    public ImmutableArray<McpServerSummary> Servers { get; init; } = [];
    public McpServerKey? SelectedKey { get; init; }
    public McpServerDetail? Detail { get; init; }
    public McpEditorState? Editor { get; init; }
    public bool TurnBusy { get; init; }
    public bool ActionBusy { get; init; }
    public string? StatusMessage { get; init; }

    public McpServerSummary? Selected =>
        this.SelectedKey is { } key
            ? this.Servers.FirstOrDefault(server => server.Key == key)
            : null;

    public McpBrowserState Select(McpServerKey key) =>
        this.Servers.Any(server => server.Key == key)
            ? this with { SelectedKey = key }
            : this;

    public McpBrowserState WithServers(
        ImmutableArray<McpServerSummary> servers,
        McpServerKey? preferredKey = null)
    {
        var oldIndex = this.IndexOf(this.SelectedKey);
        var selectedKey = preferredKey is { } preferred &&
            servers.Any(server => server.Key == preferred)
                ? preferred
                : this.SelectedKey is { } retained &&
                    servers.Any(server => server.Key == retained)
                    ? retained
                    : servers.Length == 0
                        ? null
                        : servers[Math.Clamp(
                            oldIndex <= 0 ? 0 : oldIndex - 1,
                            0,
                            servers.Length - 1)].Key;
        return this with
        {
            Servers = servers,
            SelectedKey = selectedKey,
            Detail = this.View == McpBrowserView.Detail &&
                selectedKey != this.SelectedKey
                    ? null
                    : this.Detail,
        };
    }

    public McpBrowserState CancelEditor() =>
        this.Editor is { } editor
            ? this with
            {
                View = editor.Origin,
                Editor = null,
                StatusMessage = "Cancelled.",
            }
            : this;

    public McpBrowserState MoveSelection(int delta)
    {
        if (this.Servers.Length == 0)
        {
            return this;
        }

        var current = Math.Max(0, this.IndexOf(this.SelectedKey));
        var next = Math.Clamp(current + delta, 0, this.Servers.Length - 1);
        return this with { SelectedKey = this.Servers[next].Key };
    }

    public McpBrowserState MoveToStart() =>
        this.Servers.Length == 0
            ? this
            : this with { SelectedKey = this.Servers[0].Key };

    public McpBrowserState MoveToEnd() =>
        this.Servers.Length == 0
            ? this
            : this with { SelectedKey = this.Servers[^1].Key };

    public McpBrowserState OpenDetail(McpServerDetail detail) =>
        this with
        {
            View = McpBrowserView.Detail,
            SelectedKey = detail.Summary.Key,
            Detail = detail,
            Editor = null,
        };

    public McpBrowserState ReturnToList() =>
        this with
        {
            View = McpBrowserView.List,
            Detail = null,
            Editor = null,
        };

    public McpBrowserState BeginAdd(McpManagementSnapshot snapshot)
    {
        var scope = snapshot.ProjectScopeAvailable
            ? McpConfigScope.Project
            : McpConfigScope.User;
        var draft = new McpServerDraft(
            Name: string.Empty,
            Scope: scope,
            Enabled: true,
            Transport: McpTransportKind.Stdio,
            Command: null,
            Args: [],
            Url: null,
            Environment: [],
            Headers: [],
            AuthMode: McpAuthMode.OAuth,
            ClientId: null,
            Scopes: [],
            BearerToken: new McpSecretChange(
                "auth/token",
                McpSecretChangeKind.Unchanged));
        return this with
        {
            View = McpBrowserView.Editor,
            Editor = new McpEditorState(
                McpEditorMode.Add,
                this.View,
                draft,
                McpEditorField.Scope),
        };
    }

    public McpBrowserState BeginEdit(McpServerDraft draft) =>
        this with
        {
            View = McpBrowserView.Editor,
            Editor = new McpEditorState(
                McpEditorMode.Edit,
                this.View,
                draft,
                McpEditorField.Name),
        };

    public McpBrowserState WithTurnBusy(bool busy) =>
        this with { TurnBusy = busy };

    public McpBrowserState WithActionBusy(bool busy) =>
        this with { ActionBusy = busy };

    public McpBrowserState WithStatus(string? message) =>
        this with { StatusMessage = message };

    private int IndexOf(McpServerKey? key)
    {
        if (key is null)
        {
            return -1;
        }

        for (var index = 0; index < this.Servers.Length; index++)
        {
            if (this.Servers[index].Key == key.Value)
            {
                return index;
            }
        }

        return -1;
    }
}

internal static class McpBrowserKeyMap
{
    public static McpBrowserCommand Map(
        Key key,
        McpBrowserView view) =>
        view switch
        {
            McpBrowserView.Editor => MapEditor(key),
            McpBrowserView.Detail => MapDetail(key),
            _ => MapList(key),
        };

    private static McpBrowserCommand MapList(Key key)
    {
        if (key == Key.Esc) return McpBrowserCommand.Close;
        if (key == Key.CursorUp) return McpBrowserCommand.MoveUp;
        if (key == Key.CursorDown) return McpBrowserCommand.MoveDown;
        if (key == Key.PageUp) return McpBrowserCommand.PageUp;
        if (key == Key.PageDown) return McpBrowserCommand.PageDown;
        if (key == Key.Home) return McpBrowserCommand.MoveToStart;
        if (key == Key.End) return McpBrowserCommand.MoveToEnd;
        if (key == Key.Enter) return McpBrowserCommand.OpenDetail;
        if (key == new Key('a')) return McpBrowserCommand.BeginAdd;
        if (key == new Key('e')) return McpBrowserCommand.BeginEdit;
        if (key == Key.Space) return McpBrowserCommand.ToggleEnabled;
        if (key == new Key('u')) return McpBrowserCommand.Reauthenticate;
        if (key == Key.Delete) return McpBrowserCommand.DeleteServer;
        return McpBrowserCommand.None;
    }

    private static McpBrowserCommand MapDetail(Key key)
    {
        if (key == Key.Esc) return McpBrowserCommand.ReturnToList;
        if (key == new Key('e')) return McpBrowserCommand.BeginEdit;
        if (key == Key.Space) return McpBrowserCommand.ToggleEnabled;
        if (key == new Key('u')) return McpBrowserCommand.Reauthenticate;
        if (key == Key.Delete) return McpBrowserCommand.DeleteServer;
        return McpBrowserCommand.None;
    }

    private static McpBrowserCommand MapEditor(Key key)
    {
        if (key == Key.Esc) return McpBrowserCommand.EditorCancel;
        if (key == Key.Tab) return McpBrowserCommand.EditorNext;
        if (key == Key.Tab.WithShift) return McpBrowserCommand.EditorPrevious;
        if (key == Key.Enter) return McpBrowserCommand.EditorApply;
        if (key == Key.Backspace) return McpBrowserCommand.EditorBackspace;
        if (key == Key.Delete) return McpBrowserCommand.EditorDelete;
        var rune = key.AsRune;
        return !key.IsCtrl && !key.IsAlt &&
            rune.Value != 0 &&
            !System.Text.Rune.IsControl(rune)
                ? McpBrowserCommand.EditorInsert
                : McpBrowserCommand.None;
    }
}
```

`BeginAdd` keeps scope as an explicit editable field until save. `BeginEdit` renders scope read-only and preserves the original scope. Editor printable input never maps to list/detail actions.

The controller interprets `EditorApply` by focused field: Save validates/prepares/commits, Cancel returns without mutation, Scope/Transport/AuthMode changes the selected option, and text/list fields advance focus without saving. Enter on an ordinary text field therefore cannot commit an incomplete draft.

- [ ] **Step 4: Run browser state/key tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpBrowserStateTests|FullyQualifiedName~McpBrowserKeyMapTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Mcp\McpBrowserModels.cs src\Coda.Tui\Ui\Mcp\McpBrowserState.cs src\Coda.Tui\Ui\Mcp\McpBrowserKeyMap.cs tests\Coda.Tui.Tests\McpBrowserStateTests.cs tests\Coda.Tui.Tests\McpBrowserKeyMapTests.cs
git commit -m "feat(tui): add immutable mcp browser state and key map" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 11: Add an atomic whole-turn idle lease

**Files:**
- Create: `src/Coda.Tui/Ui/IExclusiveIdleGate.cs`
- Modify: `src/Coda.Agent/Tasks/TaskManager.cs`
- Modify: `src/Coda.Tui/Ui/TuiController.cs`
- Create: `tests/Engine.Tests/TaskManagerIdleLeaseTests.cs`
- Modify: `tests/Coda.Tui.Tests/TuiControllerTests.cs`

- [ ] **Step 1: Write failing dispatch/lease race tests**

```csharp
[Fact]
public async Task Submission_cannot_start_while_an_idle_lease_is_held()
{
    var started = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var controller = NewController(async (text, ct) =>
    {
        started.TrySetResult();
        await Task.CompletedTask;
    });
    var gate = Assert.IsAssignableFrom<IExclusiveIdleGate>(controller);
    using var lease = Assert.IsAssignableFrom<IDisposable>(gate.TryAcquire());

    controller.OnSubmitted("queued");
    await Task.Delay(50);
    Assert.False(started.Task.IsCompleted);

    lease.Dispose();
    await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
}

[Fact]
public async Task Lease_cannot_overlap_an_in_flight_dispatch()
{
    var release = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var controller = NewController(
        (text, ct) => release.Task);
    controller.OnSubmitted("running");
    await WaitUntilAsync(() => controller.HasActiveWork);

    var gate = Assert.IsAssignableFrom<IExclusiveIdleGate>(controller);
    Assert.Null(gate.TryAcquire());

    release.TrySetResult();
    await controller.WaitForDispatchAsync();
}

[Fact]
public async Task Task_manager_lease_blocks_new_scheduled_registration_and_rejects_while_running()
{
    var manager = new TaskManager(sessionId: "idle-gate", logRoot: null);
    using var lease = Assert.IsAssignableFrom<IDisposable>(
        manager.TryAcquireIdleLease());
    var start = Task.Run(() => manager.StartScheduledBackground(
        new ImmediateScheduledHost(),
        "prompt",
        "scheduled",
        _ => { }));
    await Task.Delay(50);
    Assert.False(start.IsCompleted);

    lease.Dispose();
    var id = await start.WaitAsync(TimeSpan.FromSeconds(1));
    await WaitUntilAsync(
        () => manager.Get(id)?.Status != TaskRunStatus.Running);

    var blockingHost = new BlockingScheduledHost();
    var runningId = manager.StartScheduledBackground(
        blockingHost,
        "prompt",
        "running",
        _ => { });
    await blockingHost.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Assert.Null(manager.TryAcquireIdleLease());
    blockingHost.Release.TrySetResult();
    await WaitUntilAsync(
        () => manager.Get(runningId)?.Status != TaskRunStatus.Running);
}

private sealed class ImmediateScheduledHost : IScheduledAgentHost
{
    public Task<string> RunScheduledAsync(
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken) =>
        Task.FromResult("done");
}

private sealed class BlockingScheduledHost : IScheduledAgentHost
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<string> RunScheduledAsync(
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken)
    {
        this.Started.TrySetResult();
        await this.Release.Task.WaitAsync(cancellationToken);
        return "done";
    }
}

private static async Task WaitUntilAsync(Func<bool> predicate)
{
    var deadline = DateTime.UtcNow.AddSeconds(2);
    while (!predicate())
    {
        if (DateTime.UtcNow >= deadline)
        {
            throw new TimeoutException("Condition was not reached.");
        }

        await Task.Delay(10);
    }
}
```

Place the first two tests in `TuiControllerTests` and use its existing `NewController`/dispatch helpers. Place the task-manager test and its three helpers in the new `TaskManagerIdleLeaseTests`.

- [ ] **Step 2: Run controller tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskManagerIdleLeaseTests"`

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiControllerTests"`

Expected: FAIL because there is no atomic idle lease.

- [ ] **Step 3: Implement the gate under the existing dispatch lock**

```csharp
internal interface IExclusiveIdleGate
{
    bool IsBusy { get; }
    event Action? Changed;
    IDisposable? TryAcquire();
}
```

`TuiController` implements it with a single lease count/boolean guarded by `gate`. Acquisition fails during startup, dispatch, queued continuation, shutdown drain, exit, or active turn. `OnSubmitted` queues ordinary submissions while leased rather than starting them. Disposing the lease under the same lock starts the oldest queued submission when safe, then raises `Changed` outside the lock.

`TaskManager.TryAcquireIdleLease()` returns null while any managed task is Running. While its lease is held, `Register` waits under the existing task-manager condition lock; release clears the flag and pulses blocked scheduled/subagent/shell registrations. `TuiController.TryAcquire` first marks a local pending lease under `gate` (blocking new dispatch), then acquires the task-manager lease, and rolls back the local mark if task acquisition fails. The returned composite lease releases task registration before allowing queued interactive dispatch.

The busy-change event reflects the real dispatch/task boundary and never fires while either lock is held.

- [ ] **Step 4: Run controller tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskManagerIdleLeaseTests"`

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiControllerTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\IExclusiveIdleGate.cs src\Coda.Agent\Tasks\TaskManager.cs src\Coda.Tui\Ui\TuiController.cs tests\Engine.Tests\TaskManagerIdleLeaseTests.cs tests\Coda.Tui.Tests\TuiControllerTests.cs
git commit -m "feat(tui): expose atomic idle lease for runtime mutations" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 12: Add the headless MCP browser controller

**Files:**
- Create: `src/Coda.Tui/Ui/Mcp/McpBrowserController.cs`
- Create: `tests/Coda.Tui.Tests/McpBrowserControllerTests.cs`

- [ ] **Step 1: Write failing open/busy/confirmation/secret-clearing tests**

```csharp
[Theory]
[InlineData("/mcp", true)]
[InlineData(" /mcp ", true)]
[InlineData("/MCP", false)]
[InlineData("/mcp list", false)]
[InlineData("/mcp x", false)]
public void Open_request_is_exact_and_case_sensitive(string text, bool expected) =>
    Assert.Equal(expected, McpBrowserController.IsOpenRequest(text));

[Fact]
public async Task Busy_browser_allows_refresh_but_rejects_mutation_without_a_lease()
{
    await using var fixture = await McpBrowserControllerFixture.CreateAsync();
    fixture.IdleGate.SetBusy(true);
    fixture.Controller.Open();
    await fixture.Controller.RefreshAsync(CancellationToken.None);

    await fixture.Controller.ExecuteAsync(
        McpBrowserCommand.ToggleEnabled,
        key: null,
        CancellationToken.None);

    Assert.NotEmpty(fixture.Controller.State.Servers);
    Assert.Contains(
        "turn",
        fixture.Controller.State.StatusMessage!,
        StringComparison.OrdinalIgnoreCase);
    Assert.False(fixture.Harness.Runtime.IsServerConnected("server"));
    Assert.False(McpConfig.LoadPhysicalEntries(
        fixture.Harness.Project,
        fixture.Harness.User).Single().Config.Disabled);
}

[Fact]
public async Task Delete_confirms_before_committing()
{
    await using var fixture = await McpBrowserControllerFixture.CreateAsync();
    fixture.Controller.Open();
    await fixture.Controller.RefreshAsync(CancellationToken.None);

    await fixture.Controller.ExecuteAsync(
        McpBrowserCommand.DeleteServer,
        key: null,
        CancellationToken.None);

    Assert.Contains("Cancelled", fixture.Controller.State.StatusMessage!, StringComparison.Ordinal);
    Assert.Single(McpConfig.LoadPhysicalEntries(
        fixture.Harness.Project,
        fixture.Harness.User));
}

[Fact]
public async Task Reauthentication_confirms_before_replacing_oauth_state()
{
    await using var fixture = await McpBrowserControllerFixture.CreateAsync();
    fixture.Controller.Open();
    await fixture.Controller.RefreshAsync(CancellationToken.None);

    await fixture.Controller.ExecuteAsync(
        McpBrowserCommand.Reauthenticate,
        key: null,
        CancellationToken.None);

    Assert.Equal(0, fixture.Harness.OAuth.Calls);
    Assert.Contains("Cancelled", fixture.Controller.State.StatusMessage!, StringComparison.Ordinal);
}

[Fact]
public void Closing_editor_removes_replacement_secrets_from_state()
{
    var state = BrowserStateWithReplacementSecret();

    var closed = state.CancelEditor();

    Assert.Null(closed.Editor);
    Assert.DoesNotContain("replacement-value", closed.ToString(), StringComparison.Ordinal);
}

private static McpBrowserState BrowserStateWithReplacementSecret()
{
    var draft = new McpServerDraft(
        Name: "server",
        Scope: McpConfigScope.Project,
        Enabled: true,
        Transport: McpTransportKind.Http,
        Command: null,
        Args: [],
        Url: "https://example.test/mcp",
        Environment: [],
        Headers: [],
        AuthMode: McpAuthMode.Bearer,
        ClientId: null,
        Scopes: [],
        BearerToken: new McpSecretChange(
            "auth/token",
            McpSecretChangeKind.Replace,
            new McpSecretReplacement("replacement-value")));
    return McpBrowserState.Empty with
    {
        View = McpBrowserView.Editor,
        Editor = new McpEditorState(
            McpEditorMode.Edit,
            McpBrowserView.List,
            draft,
            McpEditorField.BearerToken),
    };
}

private sealed class McpBrowserControllerFixture : IAsyncDisposable
{
    private McpBrowserControllerFixture(
        McpManagementTestHarness harness,
        TestIdleGate idleGate,
        McpBrowserController controller)
    {
        this.Harness = harness;
        this.IdleGate = idleGate;
        this.Controller = controller;
    }

    public McpManagementTestHarness Harness { get; }
    public TestIdleGate IdleGate { get; }
    public McpBrowserController Controller { get; }

    public static async Task<McpBrowserControllerFixture> CreateAsync()
    {
        var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://example.test/mcp"}}}""");
        var idleGate = new TestIdleGate();
        var prompts = new RecordingPromptService(
            new UiPromptResponse(
                Cancelled: false,
                SelectedIds: ["no"],
                Text: null));
        var controller = new McpBrowserController(
            () => new McpBrowserProvider(
                harness.Service,
                prompts,
                idleGate));
        return new McpBrowserControllerFixture(
            harness,
            idleGate,
            controller);
    }

    public async ValueTask DisposeAsync()
    {
        this.Controller.Close();
        await this.Harness.DisposeAsync();
    }
}

private sealed class TestIdleGate : IExclusiveIdleGate
{
    private bool busy;
    private bool leased;
    public bool IsBusy => this.busy || this.leased;
    public event Action? Changed;

    public void SetBusy(bool value)
    {
        this.busy = value;
        this.Changed?.Invoke();
    }

    public IDisposable? TryAcquire()
    {
        if (this.IsBusy)
        {
            return null;
        }

        this.leased = true;
        this.Changed?.Invoke();
        return new ReleaseLease(this);
    }

    private sealed class ReleaseLease(TestIdleGate owner) : IDisposable
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
```

- [ ] **Step 2: Run controller tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpBrowserControllerTests"`

Expected: FAIL because the controller does not exist.

- [ ] **Step 3: Implement lifecycle, serialized actions, prompts, and generation safety**

```csharp
internal sealed record McpBrowserProvider(
    IMcpManagementService Management,
    IUiPromptService Prompts,
    IExclusiveIdleGate IdleGate);
```

Controller responsibilities:

- `IsOpenRequest` uses `string.Equals(text?.Trim(), "/mcp", StringComparison.Ordinal)`;
- `Open` binds once, subscribes to idle changes, and begins a generation-protected refresh;
- `Close` cancels work, unsubscribes, clears editor replacement secrets, and is idempotent;
- a `SemaphoreSlim` serializes actions;
- refresh applies only when its generation/open epoch is current;
- read actions remain available while busy;
- every mutation obtains `IdleGate.TryAcquire()` and disposes it after service completion;
- delete and reauth always call prepare, ask confirmation with default false, then commit;
- managed replacements use `UiPromptRequest.Text($"Replace {field}", required: true, secret: true)`;
- successful rename selects the returned new key; delete uses nearest selection;
- service errors become sanitized state status/error, never raw exceptions containing drafts.

Use this constructor:

```csharp
private readonly Func<McpBrowserProvider?> provider;
private McpBrowserState state = McpBrowserState.Empty;

internal McpBrowserController(
    Func<McpBrowserProvider?> provider)
{
    this.provider = provider
        ?? throw new ArgumentNullException(nameof(provider));
}

internal event Action? Changed;

internal McpBrowserState State => this.state;

internal void Open();
internal Task RefreshAsync(CancellationToken ct);
internal Task ExecuteAsync(
    McpBrowserCommand command,
    Key? key,
    CancellationToken ct);
internal void Close();
```

Expose these internal test seams without changing production behavior:

```csharp
internal int ChangedSubscriberCount =>
    this.Changed?.GetInvocationList().Length ?? 0;

internal void SetStateForTest(McpBrowserState state) =>
    this.state = state;

internal void NotifyChangedForTest() =>
    this.Changed?.Invoke();
```

- [ ] **Step 4: Run controller tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpBrowserControllerTests|FullyQualifiedName~McpBrowserStateTests|FullyQualifiedName~McpBrowserKeyMapTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Mcp\McpBrowserController.cs tests\Coda.Tui.Tests\McpBrowserControllerTests.cs
git commit -m "feat(tui): add headless mcp browser controller" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 13: Render the Terminal.Gui MCP overlay

**Files:**
- Create: `src/Coda.Tui/Ui/Mcp/McpBrowserOverlay.cs`
- Create: `tests/Coda.Tui.Tests/McpBrowserOverlayTests.cs`

- [ ] **Step 1: Write failing lifecycle, input-swallow, and masked-editor tests**

```csharp
[Fact]
public void Show_is_idempotent_and_dispose_unsubscribes()
{
    using var fixture = McpBrowserOverlayFixture.Create();

    fixture.Overlay.Show();
    fixture.Overlay.Show();
    Assert.Equal(1, fixture.Controller.ChangedSubscriberCount);

    fixture.Overlay.Dispose();
    Assert.Equal(0, fixture.Controller.ChangedSubscriberCount);
}

[Fact]
public void Visible_overlay_swallows_unmapped_input()
{
    using var fixture = McpBrowserOverlayFixture.Create();
    fixture.Overlay.Show();

    Assert.True(fixture.Overlay.NewKeyDownEvent(new Key('z')));
}

[Fact]
public void Editor_never_renders_secret_replacement_text()
{
    using var fixture = McpBrowserOverlayFixture.Create();
    fixture.Overlay.Show();
    fixture.Controller.SetStateForTest(
        McpBrowserOverlayFixture.EditorStateWithSecret("super-secret"));
    fixture.Controller.NotifyChangedForTest();
    fixture.Application.LayoutAndDraw();

    Assert.DoesNotContain(
        "super-secret",
        fixture.Overlay.VisibleTextForTest,
        StringComparison.Ordinal);
    Assert.Contains("*****", fixture.Overlay.VisibleTextForTest, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run overlay tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpBrowserOverlayTests"`

Expected: FAIL because the overlay does not exist.

- [ ] **Step 3: Implement task-browser-style overlay lifecycle and rendering**

`McpBrowserOverlay` follows `TaskBrowserOverlay`:

- hidden by default;
- one `Changed` subscription while shown;
- marshal changes through `IApplication.Invoke`;
- full-overlay list, detail, and editor views;
- list rows show name, scope, enabled, connected/error, effective/overridden;
- detail shows source/transport/redacted config/capabilities/last error;
- editor owns focus and field rendering, masks replacement values, and routes Tab/Shift+Tab/Enter/Esc/Backspace/Delete/printable input through key map/controller;
- sanitize all body text;
- visible overlay returns `true` for every key/mouse input;
- `Show`, `Hide`, and `Dispose` are idempotent and cancel outstanding work.

`McpBrowserOverlayFixture.Create` constructs an ANSI `IApplication`, a root `Toplevel`, a real `McpManagementTestHarness`, `TestIdleGate`, `RecordingPromptService`, `McpBrowserController`, and `McpBrowserOverlay`; it exposes `Application`, `Controller`, and `Overlay`, adds the overlay to the root, begins/layouts the application, and disposes the run state, overlay, application, and harness in reverse order.

Its secret-state helper is:

```csharp
internal static McpBrowserState EditorStateWithSecret(string secret)
{
    var draft = new McpServerDraft(
        Name: "server",
        Scope: McpConfigScope.Project,
        Enabled: true,
        Transport: McpTransportKind.Http,
        Command: null,
        Args: [],
        Url: "https://example.test/mcp",
        Environment: [],
        Headers: [],
        AuthMode: McpAuthMode.Bearer,
        ClientId: null,
        Scopes: [],
        BearerToken: new McpSecretChange(
            "auth/token",
            McpSecretChangeKind.Replace,
            new McpSecretReplacement(secret)));
    return McpBrowserState.Empty with
    {
        View = McpBrowserView.Editor,
        Editor = new McpEditorState(
            McpEditorMode.Edit,
            McpBrowserView.List,
            draft,
            McpEditorField.BearerToken),
    };
}
```

Expose this render-only test seam:

```csharp
internal string VisibleTextForTest { get; private set; } = string.Empty;
```

Update it from the same sanitized strings assigned to the visible labels/list rows, never from raw drafts.

Use this surface:

```csharp
internal sealed class McpBrowserOverlay : View
{
    private readonly IApplication app;
    private readonly McpBrowserController controller;
    private bool subscribed;

    internal McpBrowserOverlay(
        IApplication app,
        McpBrowserController controller)
    {
        this.app = app;
        this.controller = controller;
        this.Visible = false;
        this.CanFocus = true;
    }

    internal void Show()
    {
        if (this.Visible)
        {
            return;
        }

        if (!this.subscribed)
        {
            this.controller.Changed += this.OnControllerChanged;
            this.subscribed = true;
        }

        this.controller.Open();
        this.Visible = true;
        this.SetFocus();
    }

    internal void Hide()
    {
        if (!this.Visible && !this.subscribed)
        {
            return;
        }

        this.Visible = false;
        this.controller.Close();
        if (this.subscribed)
        {
            this.controller.Changed -= this.OnControllerChanged;
            this.subscribed = false;
        }
    }

    private void OnControllerChanged() =>
        this.app.Invoke(this.ApplyState);

    private void ApplyState()
    {
        this.VisibleTextForTest = FormatState(this.controller.State);
        this.SetNeedsDraw();
    }

    private static string FormatState(McpBrowserState state)
    {
        var text = state.View switch
        {
            McpBrowserView.List => string.Join(
                Environment.NewLine,
                state.Servers.Select(server =>
                    $"{server.Key.Name} [{server.Key.Scope}] {server.Connection}")),
            McpBrowserView.Detail when state.Detail is { } detail =>
                $"{detail.Summary.Key.Name} [{detail.Summary.Key.Scope}]" +
                Environment.NewLine +
                $"{detail.Summary.SourceFile}" +
                Environment.NewLine +
                $"{detail.Summary.Connection}",
            McpBrowserView.Editor when state.Editor is { } editor =>
                $"{editor.Mode}: {editor.Draft.Name}" +
                Environment.NewLine +
                $"{editor.Draft.BearerToken.Replacement}",
            _ => string.Empty,
        };
        return TerminalTextSanitizer.Sanitize(text);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.Hide();
        }

        base.Dispose(disposing);
    }
}
```

Core key path:

```csharp
protected override bool OnKeyDown(Key key)
{
    if (!this.Visible)
    {
        return false;
    }

    var command = McpBrowserKeyMap.Map(key, this.controller.State.View);
    _ = this.controller.ExecuteAsync(command, key, this.lifetime.Token);
    return true;
}
```

- [ ] **Step 4: Run overlay tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpBrowserOverlayTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Mcp\McpBrowserOverlay.cs tests\Coda.Tui.Tests\McpBrowserOverlayTests.cs
git commit -m "feat(tui): render interactive mcp manager overlay" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 14: Intercept exact bare `/mcp` and compose both shell modes

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs`
- Modify: `src/Coda.Tui/Ui/Shells/InlineTuiShell.cs`
- Modify: `src/Coda.Tui/InteractiveProgram.cs`
- Create: `tests/Coda.Tui.Tests/McpInterceptTests.cs`
- Modify: `tests/Coda.Tui.Tests/TasksInterceptTests.cs`
- Modify: `tests/Coda.Tui.Tests/RetainedShellFixture.cs`

- [ ] **Step 1: Write failing interception, parity, focus, and disposal tests**

```csharp
[Fact]
public void Exact_bare_mcp_opens_without_dispatch_even_while_busy()
{
    using var fixture = RetainedShellFixture.CreateWithMcpBrowser(activeWork: true);
    var dispatched = 0;
    fixture.Shell.PromptSubmitted += (_, _) => dispatched++;
    fixture.Shell.Composer.SetDraft("/mcp", 4);

    fixture.Shell.Composer.NewKeyDownEvent(Key.Enter);

    Assert.True(fixture.Shell.McpOverlay!.Visible);
    Assert.Equal(0, dispatched);
}

[Theory]
[InlineData("/mcp list")]
[InlineData("/MCP")]
[InlineData("/mcp x")]
public void Non_exact_forms_dispatch_normally(string text)
{
    using var fixture = RetainedShellFixture.CreateWithMcpBrowser(activeWork: false);
    string? dispatched = null;
    fixture.Shell.PromptSubmitted += (_, value) => dispatched = value;
    fixture.Shell.Composer.SetDraft(text, text.Length);

    fixture.Shell.Composer.NewKeyDownEvent(Key.Enter);

    Assert.Equal(text, dispatched);
    Assert.False(fixture.Shell.McpOverlay!.Visible);
}

[Theory]
[InlineData(TuiRunMode.Fullscreen)]
[InlineData(TuiRunMode.Inline)]
public void Both_terminal_gui_modes_host_the_overlay(TuiRunMode mode)
{
    using var fixture = RetainedShellFixture.CreateWithMcpBrowser(
        activeWork: false,
        mode: mode);
    Assert.NotNull(fixture.Shell.McpOverlay);
}

[Fact]
public async Task Prompt_focus_returns_to_mcp_then_escape_returns_to_composer()
{
    using var fixture = RetainedShellFixture.CreateWithMcpBrowser(activeWork: false);
    fixture.Shell.McpOverlay!.Show();
    await fixture.Shell.ApplyAsync(
        fixture.Shell.Snapshot with
        {
            PendingPrompt = UiPromptRequest.Confirm("Delete?", defaultValue: false),
        },
        CancellationToken.None);
    Assert.True(fixture.Shell.PromptOverlay.HasFocus);

    await fixture.Shell.ApplyAsync(
        fixture.Shell.Snapshot with { PendingPrompt = null },
        CancellationToken.None);
    Assert.True(fixture.Shell.McpOverlay.HasFocus);

    fixture.Shell.McpOverlay.NewKeyDownEvent(Key.Esc);
    Assert.True(fixture.Shell.Composer.HasFocus);
}
```

- [ ] **Step 2: Run interception tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpInterceptTests|FullyQualifiedName~TasksInterceptTests"`

Expected: FAIL because no shell hosts/intercepts the MCP overlay.

- [ ] **Step 3: Compose provider, z-order, interception, and focus restoration**

Add optional `Func<McpBrowserProvider?>? mcpBrowserProvider` to shared/derived shell constructors. `TerminalGuiShellBase` owns controller/overlay creation and exposes `McpOverlay`.

In `OnComposerSubmitted`, before normal dispatch:

```csharp
if (this.McpOverlay is not null &&
    McpBrowserController.IsOpenRequest(text))
{
    this.McpOverlay.Show();
    return;
}
```

No provider means normal textual fallback. Add MCP overlay after normal/completion and task overlay views but before `PromptOverlay`, so prompts always win. Generalize focus checks to “visible browser overlay”: prompt closes to the previously visible browser; browser closes to composer.

`InteractiveProgram` constructs one `McpManagementService`, one provider using the shared `TuiController` as `IExclusiveIdleGate`, and passes it to both fullscreen and inline shells. Correct the existing task-provider inline parity while touching constructor composition.

Add this test-fixture constructor, using the same real provider composition:

```csharp
internal static RetainedShellFixture CreateWithMcpBrowser(
    bool activeWork,
    TuiRunMode mode = TuiRunMode.Fullscreen);
```

Mode switch/disposal hides/disposes overlay, cancels controller work, unsubscribes, and releases prompt/mouse focus.

- [ ] **Step 4: Run interception and MCP subsystem tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpInterceptTests|FullyQualifiedName~TasksInterceptTests|FullyQualifiedName~McpBrowserControllerTests|FullyQualifiedName~McpBrowserOverlayTests|FullyQualifiedName~McpCommandTests|FullyQualifiedName~McpManagementRuntimeTests|FullyQualifiedName~McpManagementDeleteTests|FullyQualifiedName~McpManagementAuthenticationTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs src\Coda.Tui\Ui\Shells\FullscreenTuiShell.cs src\Coda.Tui\Ui\Shells\InlineTuiShell.cs src\Coda.Tui\InteractiveProgram.cs tests\Coda.Tui.Tests\McpInterceptTests.cs tests\Coda.Tui.Tests\TasksInterceptTests.cs tests\Coda.Tui.Tests\RetainedShellFixture.cs
git commit -m "feat(tui): intercept bare mcp and host manager in both shells" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Subsystem completion checks

Run from `C:\Users\yurio\Documents\github\coda-cli`:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~McpReadModelTests|FullyQualifiedName~McpConfigDisabledTests|FullyQualifiedName~McpConfigWriterTests|FullyQualifiedName~McpSecretStoreTests|FullyQualifiedName~McpOAuthTokenLifecycleTests|FullyQualifiedName~McpClientIdResolutionTests|FullyQualifiedName~McpUnauthorizedResolutionFlowTests|FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~TaskManagerIdleLeaseTests"
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~McpManagementReadTests|FullyQualifiedName~McpManagementEditTests|FullyQualifiedName~McpManagementRuntimeTests|FullyQualifiedName~McpManagementDeleteTests|FullyQualifiedName~McpManagementAuthenticationTests|FullyQualifiedName~McpCommandTests|FullyQualifiedName~McpFlagParserTests|FullyQualifiedName~McpViewTests|FullyQualifiedName~McpBrowserStateTests|FullyQualifiedName~McpBrowserKeyMapTests|FullyQualifiedName~TuiControllerTests|FullyQualifiedName~McpBrowserControllerTests|FullyQualifiedName~McpBrowserOverlayTests|FullyQualifiedName~McpInterceptTests|FullyQualifiedName~TasksInterceptTests"
```

Expected: both projects pass with both physical scopes visible, stable scoped selection, rename-capable edit, managed secret migration, confirmations before destructive/auth changes, disabled override shadowing, and immediate effective runtime reconciliation.

## Explicit implementation risks to verify during execution

- `ITokenStore` cannot transact with config files; staged versioned keys plus compensation may leave harmless orphan keys only if compensation itself fails.
- OAuth tokens are URL-scoped and may be shared across names/scopes; reauthentication must not delete shared dynamic client registration.
- Runtime is name-scoped; overridden physical rows never inherit effective connected/error details.
- Whole-turn safety depends on the composite `TuiController` plus `TaskManager` lease from Task 11; do not replace it with a non-atomic `HasActiveWork` check.
- External editors are outside the process lock; both config-file revision hashes must be rechecked at commit.
- Post-write cancellation returns saved-with-runtime-error/cancelled state; refresh/restart must make recovery explicit.
- Arguments remain separate `ImmutableArray<string>` editor rows; do not reintroduce lossy space splitting.
