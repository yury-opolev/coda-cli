# Single-Provider + Windows Encryption + Banner — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make coda single-provider (one connected provider, no separate `defaultProvider` pointer), harden Windows credential-at-rest, and show the connected provider + model in the startup banner.

**Architecture:** The credential store becomes the single source of truth for "which provider": login enforces exactly one credential; provider resolution derives from that credential (with `--provider` flag override); `coda serve` tolerates a stale/unusable `--provider` by falling back to the single connection. On Windows the AES key is DPAPI-wrapped and credential files ACL-locked.

**Tech Stack:** .NET 10, C#, xUnit, Spectre.Console TUI, `System.Security.Cryptography` (AES-GCM + DPAPI `ProtectedData`).

## Global Constraints

- Repo: `lib/coda-cli` (github.com/yury-opolev/coda-cli), branch `feat/single-provider-encryption-ui`.
- Build: no `.sln` — use `dotnet build` at the repo root (or `./build.ps1`); tests `dotnet test` at repo root. Test projects: `tests/LlmAuth.Tests`, `tests/Engine.Tests`, `tests/Coda.Tui.Tests`.
- Filter syntax on this runner: use `--filter "FullyQualifiedName~<Class>"` (NOT `ClassName=`).
- C# style: `this.` on instance members, braces always, file-scoped namespaces, `sealed`, source-generated logging where the class already uses it, `ConfigureAwait(false)` in library code (`LlmAuth`, `Coda.Sdk`, `Coda.Agent`), async methods suffixed `Async`.
- Single-provider invariant: at most ONE `*.cred` file exists in `~/.coda/credentials/` at any time.
- `settings.json` `defaultProvider` is retired as a provider *selector* (no longer read to choose the provider, no longer written by login). `defaultModel` is unchanged.
- Windows credential encryption: AES-256-GCM key (`key.bin`) is DPAPI-wrapped (`DataProtectionScope.CurrentUser`); credentials dir + files ACL-restricted to the current user. Non-Windows behaviour unchanged (raw key, `0600`/`0700`).
- Cross-platform guards: all Windows-only calls (`ProtectedData`, `FileSecurity`) MUST be behind `OperatingSystem.IsWindows()` so the library still compiles/runs on Linux/macOS.
- Deferred (NOT in this plan): cortex Bridge provider-resolution removal / `--provider` drop.

**Test:** `dotnet test --filter "FullyQualifiedName~<Class>"`
**Build:** `dotnet build` (repo root)

---

### Task 1: Windows-protected AES key in FileTokenStore

**Files:**
- Modify: `src/LlmAuth/FileTokenStore.cs` (`LoadOrCreateKey`, `SetOwnerOnly`, ctor dir creation)
- Create: `src/LlmAuth/WindowsCredentialProtection.cs`
- Test: `tests/LlmAuth.Tests/FileTokenStoreTests.cs` (add cases; create file if absent — check `tests/` layout first)

**Interfaces:**
- Produces: `internal static class WindowsCredentialProtection` with
  `static byte[] ProtectKey(byte[] key)` and `static byte[] UnprotectKey(byte[] blob)` (Windows DPAPI, CurrentUser);
  `static void RestrictToCurrentUser(string path)` (Windows ACL, no-op elsewhere).
- Consumes: nothing new.

- [ ] **Step 1: Write the failing test**

Add to `FileTokenStoreTests.cs` (mirror existing store tests; use a temp dir):

```csharp
[Fact]
public async Task SetThenGet_RoundTripsAcrossInstances()
{
    var dir = Path.Combine(Path.GetTempPath(), "ftok-" + Guid.NewGuid().ToString("N"));
    try
    {
        await new FileTokenStore(dir).SetAsync("llmauth:copilot", "secret-value");
        var got = await new FileTokenStore(dir).GetAsync("llmauth:copilot");
        Assert.Equal("secret-value", got);
    }
    finally { Directory.Delete(dir, true); }
}

[Fact]
public void KeyFile_OnWindows_IsDpapiWrapped_NotRawKey()
{
    if (!OperatingSystem.IsWindows()) { return; } // Windows-only behaviour
    var dir = Path.Combine(Path.GetTempPath(), "ftok-" + Guid.NewGuid().ToString("N"));
    try
    {
        _ = new FileTokenStore(dir); // creates key.bin
        var raw = File.ReadAllBytes(Path.Combine(dir, "key.bin"));
        // A DPAPI blob is NOT 32 bytes (a raw AES-256 key would be exactly 32).
        Assert.NotEqual(32, raw.Length);
        // And it round-trips back to a 32-byte key.
        Assert.Equal(32, WindowsCredentialProtection.UnprotectKey(raw).Length);
    }
    finally { Directory.Delete(dir, true); }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~FileTokenStoreTests"`
Expected: FAIL — `WindowsCredentialProtection` undefined; on Windows the raw key is 32 bytes.

- [ ] **Step 3: Implement**

`src/LlmAuth/WindowsCredentialProtection.cs`:

```csharp
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace LlmAuth;

/// <summary>
/// Windows-only at-rest hardening for the credential store: DPAPI-wraps the AES key so a
/// stolen key.bin is useless to another user, and ACL-restricts credential files to the
/// current user. All methods are safe no-ops (or identity) on non-Windows.
/// </summary>
internal static class WindowsCredentialProtection
{
    // Ties the wrapped key to this store so a blob copied elsewhere still needs the user.
    private static readonly byte[] Entropy = "coda.credential-store.v1"u8.ToArray();

    public static byte[] ProtectKey(byte[] key)
    {
        if (!OperatingSystem.IsWindows())
        {
            return key;
        }

        return ProtectWindows(key);
    }

    public static byte[] UnprotectKey(byte[] blob)
    {
        if (!OperatingSystem.IsWindows())
        {
            return blob;
        }

        return UnprotectWindows(blob);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] key) =>
        ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] blob) =>
        ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);

    public static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RestrictWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null)
        {
            return;
        }

        var isDir = Directory.Exists(path);
        var inherit = isDir
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;

        if (isDir)
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(sec);
        }
        else
        {
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(sec);
        }
    }
}
```

In `FileTokenStore.cs`: (a) in the ctor, after `Directory.CreateDirectory(this.directory)`, add
`WindowsCredentialProtection.RestrictToCurrentUser(this.directory);`. (b) Rewrite `LoadOrCreateKey`:

```csharp
private byte[] LoadOrCreateKey()
{
    var keyPath = Path.Combine(this.directory, "key.bin");
    if (File.Exists(keyPath))
    {
        try
        {
            var unwrapped = WindowsCredentialProtection.UnprotectKey(File.ReadAllBytes(keyPath));
            if (unwrapped.Length == 32)
            {
                return unwrapped;
            }
        }
        catch (CryptographicException)
        {
            // Unreadable/foreign key (e.g. different user, corrupt) — regenerate below.
        }
    }

    var newKey = RandomNumberGenerator.GetBytes(32);
    File.WriteAllBytes(keyPath, WindowsCredentialProtection.ProtectKey(newKey));
    SetOwnerOnly(keyPath);
    return newKey;
}
```

(c) Extend `SetOwnerOnly` to also restrict on Windows:

```csharp
private static void SetOwnerOnly(string path)
{
    if (OperatingSystem.IsWindows())
    {
        WindowsCredentialProtection.RestrictToCurrentUser(path);
        return;
    }

    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~FileTokenStoreTests"`
Expected: PASS (round-trip + Windows key-wrap).

- [ ] **Step 5: Commit**

```bash
git add src/LlmAuth/WindowsCredentialProtection.cs src/LlmAuth/FileTokenStore.cs tests/LlmAuth.Tests/FileTokenStoreTests.cs
git commit -m "feat(llmauth): DPAPI-wrap AES key + ACL-lock credentials on Windows"
```

---

### Task 2: Single-credential enforcement in CredentialManager

**Files:**
- Modify: `src/LlmAuth/CredentialManager.cs`
- Test: `tests/LlmAuth.Tests/CredentialManagerTests.cs` (create if absent)

**Interfaces:**
- Produces on `CredentialManager`:
  - `Task<string?> GetConnectedProviderIdAsync(CancellationToken = default)` — the single provider id that has a stored credential, or null. (Iterates `ProviderIds`, returns the first with a non-null `GetStoredCredentialAsync`.)
  - Login/store now enforce single: after persisting provider X, all other providers' credentials are deleted.
- Consumes: existing `ProviderIds`, `GetStoredCredentialAsync`, `LogoutAsync`, `PersistAsync`, `StoreKey`.

- [ ] **Step 1: Write the failing test**

Use a fake `ITokenStore` (in-memory dict) + two fake `ICredentialProvider`s (ids "a", "b"). If the test project already has fakes, reuse them; otherwise add a minimal in-memory `ITokenStore`.

```csharp
[Fact]
public async Task Store_SecondProvider_RemovesFirst_SingleCredentialInvariant()
{
    var store = new InMemoryTokenStore();
    var mgr = new CredentialManager(store, [FakeProvider("a"), FakeProvider("b")]);

    await mgr.StoreAsync("a", NewCredential());
    await mgr.StoreAsync("b", NewCredential());

    Assert.Null(await mgr.GetStoredCredentialAsync("a"));
    Assert.NotNull(await mgr.GetStoredCredentialAsync("b"));
    Assert.Equal("b", await mgr.GetConnectedProviderIdAsync());
}

[Fact]
public async Task GetConnectedProviderId_NoCredential_ReturnsNull()
{
    var mgr = new CredentialManager(new InMemoryTokenStore(), [FakeProvider("a")]);
    Assert.Null(await mgr.GetConnectedProviderIdAsync());
}
```

(Provide `InMemoryTokenStore` implementing `ITokenStore` with a `Dictionary<string,string>`; `FakeProvider(id)` returning an `ICredentialProvider` whose `ProviderId==id`, `NeedsRefresh`→false; `NewCredential()` a minimal `Credential`. Model these on existing test helpers in the repo — search `tests/LlmAuth.Tests` first.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~CredentialManagerTests"`
Expected: FAIL — `GetConnectedProviderIdAsync` undefined; second store keeps first credential.

- [ ] **Step 3: Implement**

Add a private helper and call it from the three persist paths:

```csharp
/// <summary>Delete every stored credential except the given provider's (single-credential invariant).</summary>
private async Task RemoveOtherCredentialsAsync(string keepProviderId, CancellationToken cancellationToken)
{
    foreach (var id in this.providers.Keys)
    {
        if (!string.Equals(id, keepProviderId, StringComparison.Ordinal))
        {
            await this.store.DeleteAsync(StoreKey(id), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The single provider id that currently has a stored credential, or null.</summary>
public async Task<string?> GetConnectedProviderIdAsync(CancellationToken cancellationToken = default)
{
    foreach (var id in this.providers.Keys)
    {
        var raw = await this.store.GetAsync(StoreKey(id), cancellationToken).ConfigureAwait(false);
        if (raw is not null)
        {
            return id;
        }
    }

    return null;
}
```

In `LoginAsync` (after `await this.PersistAsync(...)`, before `return credential;`), `LoginWithDeviceCodeAsync` (same spot), and `StoreAsync`, add:
`await this.RemoveOtherCredentialsAsync(providerId, cancellationToken).ConfigureAwait(false);`
(For `StoreAsync`, change its body to `await` both persist then removal — make it `async`.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~CredentialManagerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LlmAuth/CredentialManager.cs tests/LlmAuth.Tests/CredentialManagerTests.cs
git commit -m "feat(llmauth): enforce single stored credential + GetConnectedProviderId"
```

---

### Task 3: Resolve provider from the connected credential

**Files:**
- Modify: `src/Coda.Sdk/Providers/ProviderModelResolver.cs`
- Test: `tests/Engine.Tests/Providers/ProviderModelResolverTests.cs` (EXISTS — add cases)

**Interfaces:**
- Produces: overload
  `static (string? ProviderId, string? Model) Resolve(string? providerFlag, string? modelFlag, CodaSettings settings, string? connectedProviderId)`.
  Precedence for provider: `providerFlag` → `connectedProviderId` (NOT `settings.DefaultProvider`). Model unchanged: `modelFlag` → `settings.DefaultModel`.
- Consumes: `ProviderAliases.Resolve`, `CodaSettings`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Resolve_NoFlag_UsesConnectedProvider_NotSettingsDefault()
{
    var settings = new CodaSettings { DefaultProvider = "anthropic-api-key", DefaultModel = "m1" };
    var (provider, model) = ProviderModelResolver.Resolve(
        providerFlag: null, modelFlag: null, settings, connectedProviderId: "github-copilot");
    Assert.Equal(ProviderAliases.Resolve("github-copilot"), provider);
    Assert.Equal("m1", model);
}

[Fact]
public void Resolve_Flag_OverridesConnected()
{
    var settings = new CodaSettings();
    var (provider, _) = ProviderModelResolver.Resolve("claude", null, settings, connectedProviderId: "github-copilot");
    Assert.Equal(ProviderAliases.Resolve("claude"), provider);
}

[Fact]
public void Resolve_NoFlagNoConnected_ProviderIsNull()
{
    var (provider, _) = ProviderModelResolver.Resolve(null, null, new CodaSettings(), connectedProviderId: null);
    Assert.Null(provider);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~ProviderModelResolverTests"`
Expected: FAIL — 4-arg overload undefined.

- [ ] **Step 3: Implement**

Add the overload (keep the old 3-arg one delegating with `connectedProviderId: null` so existing callers compile until Task 4 updates them):

```csharp
public static (string? ProviderId, string? Model) Resolve(
    string? providerFlag, string? modelFlag, CodaSettings settings, string? connectedProviderId)
{
    ArgumentNullException.ThrowIfNull(settings);
    var providerToken = Blank(providerFlag) ?? Blank(connectedProviderId);
    var providerId = providerToken is null ? null : ProviderAliases.Resolve(providerToken);
    var model = Blank(modelFlag) ?? Blank(settings.DefaultModel);
    return (providerId, model);
}

// Back-compat: no connected provider supplied.
public static (string? ProviderId, string? Model) Resolve(string? providerFlag, string? modelFlag, CodaSettings settings)
    => Resolve(providerFlag, modelFlag, settings, connectedProviderId: null);
```

Update the `Require` messages to drop the `defaultProvider` reference: change the provider message to
`"Not signed in. Run \"coda auth login\" (or pass --provider <id>)."`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~ProviderModelResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Sdk/Providers/ProviderModelResolver.cs tests/Coda.Sdk.Tests/ProviderModelResolverTests.cs
git commit -m "feat(sdk): resolve provider from connected credential, not settings default"
```

---

### Task 4: Serve/headless/models pass the connected provider + serve fallback

**Files:**
- Modify: `src/Coda.Tui/ServeRunner.cs` (`ApplyDefaults` + its caller in `RunAsync`)
- Modify: `src/Coda.Tui/HeadlessRunner.cs:33`, `src/Coda.Tui/ModelsRunner.cs:31`
- Test: `tests/Coda.Tui.Tests/ServeProviderFallbackTests.cs` (create) or extend existing serve tests

**Interfaces:**
- Consumes: `CredentialManager.GetConnectedProviderIdAsync`, `ProviderModelResolver.Resolve(...connectedProviderId)`.
- Produces: a pure, testable fallback helper
  `internal static string? ResolveServeProvider(string? flagProvider, bool flagHasCredential, string? connectedProviderId)`
  returning: `flagProvider` when it has a credential; else `connectedProviderId`; else `flagProvider` (so `Require` throws the clean not-signed-in error).

- [ ] **Step 1: Write the failing test**

```csharp
[Theory]
// flag has credential -> use flag
[InlineData("claude", true, "github-copilot", "claude")]
// flag has NO credential, one connected -> fall back to connected
[InlineData("anthropic-api-key", false, "github-copilot", "github-copilot")]
// flag has no credential, none connected -> keep flag (Require throws downstream)
[InlineData("anthropic-api-key", false, null, "anthropic-api-key")]
// no flag, connected -> connected
[InlineData(null, false, "github-copilot", "github-copilot")]
public void ResolveServeProvider_Cases(string? flag, bool flagHasCred, string? connected, string? expected)
{
    Assert.Equal(expected, ServeRunner.ResolveServeProvider(flag, flagHasCred, connected));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~ServeProviderFallbackTests"`
Expected: FAIL — `ResolveServeProvider` undefined.

- [ ] **Step 3: Implement**

Add to `ServeRunner`:

```csharp
/// <summary>
/// Serve provider resolution with credential-aware fallback: use the requested provider when
/// it is authenticated; otherwise fall back to the single connected provider; otherwise keep
/// the requested one so Require throws the clean not-signed-in error.
/// </summary>
internal static string? ResolveServeProvider(string? flagProvider, bool flagHasCredential, string? connectedProviderId)
{
    if (!string.IsNullOrWhiteSpace(flagProvider) && flagHasCredential)
    {
        return flagProvider;
    }

    return connectedProviderId ?? flagProvider;
}
```

Wire it in `RunAsync` where the `CredentialManager` exists (search for where serve builds it): before spawning the session, compute
`var connected = await credentialManager.GetConnectedProviderIdAsync(ct).ConfigureAwait(false);`
and, if the parsed `options.ProviderId` differs from `connected` and has no credential, resolve via
`ResolveServeProvider(options.ProviderId, flagHasCred, connected)`, log a warning when it changed
(`"requested provider '{requested}' has no credential; using connected provider '{connected}'"` via the serve logger), and set `options` to the resolved provider before `Require`. For `HeadlessRunner` and `ModelsRunner`, change their `ProviderModelResolver.Resolve(...)` call to the 4-arg overload passing `await credentialManager.GetConnectedProviderIdAsync(...)` (locate the `CredentialManager` each already builds; if none is in scope, construct/resolve it the same way `RunAsync` does).

> Keep `ApplyDefaults` pure (settings only) for its existing unit tests; do the credential-aware step in `RunAsync` after `ApplyDefaults`, not inside it.

- [ ] **Step 4: Run to verify pass + build**

Run: `dotnet test --filter "FullyQualifiedName~ServeProviderFallbackTests"` → PASS
Run: `dotnet build` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/ServeRunner.cs src/Coda.Tui/HeadlessRunner.cs src/Coda.Tui/ModelsRunner.cs tests/Coda.Tui.Tests/ServeProviderFallbackTests.cs
git commit -m "feat(serve): credential-aware provider fallback + pass connected provider to resolver"
```

---

### Task 5: Login/provider commands — single-cred, non-silent, /provider = connect

**Files:**
- Modify: `src/Coda.Tui/Commands/LoginCommand.cs`
- Modify: `src/Coda.Tui/Commands/ProviderCommand.cs`
- Test: `tests/Coda.Tui.Tests/ProviderCommandTests.cs` (extend if present; else assert via the persist helper)

**Interfaces:**
- Consumes: `CredentialManager` (via `context.Credentials`), `SettingsWriter.SetUserDefaults`.
- Produces: login no longer writes `defaultProvider` (retired selector); persist failures are surfaced;
  `/provider <id>` triggers a login/connect (single-cred) rather than only flipping a settings pointer.

- [ ] **Step 1: Write the failing test / characterization**

Because these commands are interactive, add a focused test on the one pure change that matters: `LoginCommand` and `ProviderCommand` MUST NOT call `SettingsWriter.SetUserDefaults` with a `defaultProvider` value (provider is now derived from the credential). If the codebase has no seam to assert this, extract the persist decision into a pure helper and test it:

```csharp
// New pure helper in LoginCommand:  internal static bool ShouldPersistDefaultProvider => false;
[Fact]
public void Login_DoesNotPersistDefaultProvider()
{
    Assert.False(LoginCommand.ShouldPersistDefaultProvider);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~LoginCommand"`
Expected: FAIL — helper undefined.

- [ ] **Step 3: Implement**

- In `LoginCommand`: delete the `PersistDefaultProvider(provider)` calls (lines 48 and 64) — provider is now the connected credential, so no settings pointer is written. Keep `context.SetActiveProvider(provider)` (in-session). Add `internal const bool ShouldPersistDefaultProvider = false;` (documents the decision + anchors the test). The device/loopback login already persists the credential via `CredentialManager`, which now enforces single-cred (Task 2).
- In `ProviderCommand`: change the switch (`/provider <id>`) so that instead of `ModelCommand.TryPersistDefaults(defaultProvider: resolved.Id, ...)`, it **runs the login/connect flow** for `resolved` (delegate to the same path `LoginCommand` uses, or instruct the user to `/login <id>` if a shared connect helper isn't readily callable — prefer extracting a `ConnectAsync(context, provider, ct)` used by both). It must NOT write `defaultProvider`. Update the command help text to say "connect to a provider (replaces the current connection)".
- Make any remaining `SetUserDefaults` persist non-silent: where a settings write is still needed (model only), surface failures with a visible warning (they are currently swallowed).

> If extracting a shared `ConnectAsync` is too large for this task, the minimal correct version is: `/provider <id>` prints "run `/login <id>` to connect" and only `/provider` (no args) shows the connected provider. Note whichever you choose in the report.

- [ ] **Step 4: Run to verify pass + build**

Run: `dotnet test --filter "FullyQualifiedName~LoginCommand"` → PASS
Run: `dotnet build` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Commands/LoginCommand.cs src/Coda.Tui/Commands/ProviderCommand.cs tests/Coda.Tui.Tests/ProviderCommandTests.cs
git commit -m "feat(tui): login/provider stop writing defaultProvider; single connection model"
```

---

### Task 6: Banner shows connected provider + model

**Files:**
- Modify: `src/Coda.Tui/Rendering/Banner.cs` (`Render` signature + body)
- Modify: `src/Coda.Tui/TuiApp.cs:26` and `src/Coda.Tui/Commands/ClearCommand.cs:26` (callers)
- Test: `tests/Coda.Tui.Tests/BannerTests.cs` (EXISTS — add cases; render to a `TestConsole` and assert substrings)

**Interfaces:**
- Produces: `Banner.Render(IAnsiConsole console, SessionState session, string? connectedProvider, string? model)` — when `connectedProvider` is null, the body shows a "not signed in" line; otherwise `provider: <id>   model: <model>`.
- Consumes: callers compute `connectedProvider` from `context.Credentials.GetConnectedProviderIdAsync()` and `model` from `session.Model`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Render_SignedIn_ShowsProviderAndModel()
{
    var console = new TestConsole();
    var session = new SessionState { WorkingDirectory = "/tmp", Model = "claude-opus-4.8" };
    Banner.Render(console, session, connectedProvider: "github-copilot", model: "claude-opus-4.8");
    Assert.Contains("github-copilot", console.Output, StringComparison.Ordinal);
    Assert.Contains("claude-opus-4.8", console.Output, StringComparison.Ordinal);
}

[Fact]
public void Render_NotSignedIn_ShowsLoginHint()
{
    var console = new TestConsole();
    var session = new SessionState { WorkingDirectory = "/tmp" };
    Banner.Render(console, session, connectedProvider: null, model: null);
    Assert.Contains("not signed in", console.Output, StringComparison.OrdinalIgnoreCase);
}
```

(`TestConsole` is Spectre.Console.Testing — confirm the package is referenced by the TUI test project; add if needed. `SessionState` construction: match its real shape — check `src/Coda.Tui/Repl/SessionState.cs`.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~BannerTests"`
Expected: FAIL — 4-arg `Render` undefined.

- [ ] **Step 3: Implement**

Change `Banner.Render` to accept `string? connectedProvider, string? model` and add to `body`:

```csharp
var providerLine = connectedProvider is null
    ? Theme.DimMarkup("not signed in — run ") + Theme.AccentMarkup("/login")
    : $"{Theme.DimMarkup("provider:")} {Markup.Escape(connectedProvider)}   {Theme.DimMarkup("model:")} {Markup.Escape(model ?? "—")}";

var body = new Markup(
    $"{Theme.DimMarkup("cwd:")} {Markup.Escape(session.WorkingDirectory)}\n" +
    providerLine + "\n" +
    $"{Theme.DimMarkup("Type")} {Theme.AccentMarkup("/help")} {Theme.DimMarkup("for commands...")}");
```

Update the two callers. In `TuiApp.cs` (async context available at startup) compute:
`var connected = await this.context.Credentials.GetConnectedProviderIdAsync().ConfigureAwait(false);`
then `Banner.Render(this.context.Console, this.context.Session, connected, this.context.Session.Model);`.
For `ClearCommand.cs` (has `context`), do the same (its `ExecuteAsync` is async).

- [ ] **Step 4: Run to verify pass + build**

Run: `dotnet test --filter "FullyQualifiedName~BannerTests"` → PASS
Run: `dotnet build` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/Rendering/Banner.cs src/Coda.Tui/TuiApp.cs src/Coda.Tui/Commands/ClearCommand.cs tests/Coda.Tui.Tests/BannerTests.cs
git commit -m "feat(tui): show connected provider + model in startup banner"
```

---

### Task 7: Docs + full-suite verification

**Files:**
- Modify: `docs/architecture-overview.md` (or the auth/providers doc) — add a "Single-provider model" note + Windows credential-at-rest note.

- [ ] **Step 1: Full build + test**

Run: `dotnet build` → 0 errors.
Run: `dotnet test` → all green (report totals). If a pre-existing test asserted multi-provider or `defaultProvider` selection, update it to the single-provider contract and note it in the report.

- [ ] **Step 2: Docs**

Add a concise subsection documenting: one connected provider at a time (login replaces), provider derived from the credential (`--provider` overrides), `defaultProvider` retired as selector, `coda serve` fallback, and Windows DPAPI-wrapped key + ACLs.

- [ ] **Step 3: Commit**

```bash
git add docs/
git commit -m "docs: single-provider model + Windows credential encryption"
```

---

## Rollout (after all tasks green + merged to coda main)

Operational, run by the controller (not a TDD task):

1. Merge `feat/single-provider-encryption-ui` → coda `main`; push to origin (github.com/yury-opolev/coda-cli).
2. Rebuild + reinstall the host coda tool: `dotnet pack` the TUI, then `dotnet tool update --global` (or the repo's install script) so host `coda` v0.1.53 → the new build. Verify `coda --version`.
3. One-time re-login on the host: `coda auth login` → GitHub Copilot (the legacy DPAPI `.cred` won't decrypt under the new AES-GCM store). Verify the banner shows `github-copilot` + model.
4. In cortex: `git -C lib/coda-cli checkout main && git pull`, then `git add lib/coda-cli && git commit -m "chore(submodule): bump coda-cli to <sha> (single-provider + Windows encryption)"`.
5. Rebuild + redeploy cortex (`scripts/Build-All.ps1`, then `Add-AppxPackage -ForceUpdateFromAnyVersion -ForceApplicationShutdown`, relaunch).
6. End-to-end verify: start a cortex coding session in an allowed folder → runs on Copilot, no credential error; `~/.coda/credentials/` holds exactly one `.cred`; on Windows `key.bin` is a DPAPI blob (not 32 raw bytes).

## Self-Review Notes

- Spec coverage: encryption (Task 1), single-credential (Task 2), provider-from-credential (Task 3), serve fallback (Task 4), login/provider UX + retire defaultProvider (Task 5), banner (Task 6), docs/verify (Task 7), rollout (section). All spec sections mapped.
- Type consistency: `GetConnectedProviderIdAsync`, `ResolveServeProvider`, the 4-arg `Resolve`, and `Banner.Render(console, session, connectedProvider, model)` are referenced identically across tasks.
- Cross-platform: every Windows API (`ProtectedData`, `FileSecurity`) is behind `OperatingSystem.IsWindows()`; tests that assert Windows behaviour early-return on non-Windows.
