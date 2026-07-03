# MCP in the coda serve path — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `coda serve` connect the same merged MCP config (`~/.coda/.mcp.json` + `<cwd>/.mcp.json`) as the TUI and `coda run`, exposing the servers' tools to the serve-hosted session, default-on with a `--no-mcp` opt-out.

**Architecture:** Mirror the `HeadlessRunner` MCP pattern inside `ServeRunner`, but factor the logic into small, pure, individually-testable static seams (`ResolveMcpEnabled`, `BuildMcpExtraTools`, `LoadMcpToolsAsync`, and an `extraTools` parameter on `BuildSessionOptions`). `RunAsync` becomes a thin composition root that wires those seams together and owns disposal. All MCP diagnostics go to **stderr** (stdout is the JSON-RPC protocol channel). No new OAuth code — reuse `DefaultMcpHttpClientFactory`, `McpClientManager`, and `McpConfig`.

**Tech Stack:** C# / .NET 10, xUnit, `Coda.Mcp` (`McpConfig`, `McpClientManager`, `DefaultMcpHttpClientFactory`, the four MCP resource/prompt helper tools), `Coda.Sdk` (`SessionOptions.ExtraTools`).

**Spec:** `docs/proposals/2026-07-03-mcp-in-serve-path.md` (Option C — default-on + `--no-mcp` + `CODA_SERVE_DISABLE_MCP` + `CODA_USER_MCP_DIR` curation).

## Global Constraints

- **Platform:** Windows-first; `serve` already fails fast on non-Windows (`ServeRunner.RunAsync` top). MCP wiring runs only after that check.
- **stdout purity:** stdout is the JSON-RPC channel. Every MCP connect/skip/diagnostic message MUST go to `Console.Error` (stderr). Never `Console.Out`.
- **Non-interactive auth:** the HTTP MCP factory is built with `interactive: false` — a serve process must never open a browser. HTTP servers needing a fresh sign-in are skipped + logged, never block.
- **Default-on:** MCP loads by default. `--no-mcp` (flag) or `CODA_SERVE_DISABLE_MCP` in (`"1"`, `"true"`) disables it. `--mcp` is an explicit no-op affirmative for self-documenting spawn args.
- **Forward-compatible parsing:** unknown serve flags are silently ignored (existing `ServeOptions.Parse` contract). New boolean flags follow the `--telemetry` / `--session-memory` style.
- **Build:** `TreatWarningsAsErrors` is on. Code must build warning-clean under `-warnaserror`.
- **Style:** `this.` on instance members (n/a here — all new members are static), curly braces on every block, one type per file, file-scoped namespaces, collection expressions (`[...]`), `ConfigureAwait(false)` on awaits in library/runner code, `Async` suffix on async methods.
- **Testing:** strict red/green TDD. Every new static seam gets full-branch unit coverage in `tests/Coda.Tui.Tests/ServeRunnerTests.cs`. Tests must be hermetic — never read the machine's real `~/.coda/.mcp.json` (pass an explicit empty `userMcpDir`).

---

### Task 1: `ServeOptions.EnableMcp` + `--no-mcp` / `--mcp` parsing

**Files:**
- Modify: `src/Coda.Tui/ServeOptions.cs` (add `EnableMcp` property; add parse cases + local)
- Test: `tests/Coda.Tui.Tests/ServeRunnerTests.cs` (add parsing tests)

**Interfaces:**
- Produces: `ServeOptions.EnableMcp { get; init; } = true;` — consumed by Task 2's `ResolveMcpEnabled` and by `RunAsync`.

- [ ] **Step 1: Write the failing tests**

Add to `ServeRunnerTests.cs` (in the `ServeOptions parsing` region):

```csharp
[Fact]
public void Parse_enable_mcp_defaults_to_true()
{
    var options = ServeOptions.Parse([]);
    Assert.True(options.EnableMcp);
}

[Fact]
public void Parse_no_mcp_flag_disables_mcp()
{
    var options = ServeOptions.Parse(["--no-mcp"]);
    Assert.False(options.EnableMcp);
}

[Fact]
public void Parse_mcp_flag_keeps_mcp_enabled()
{
    var options = ServeOptions.Parse(["--mcp"]);
    Assert.True(options.EnableMcp);
}

[Fact]
public void Parse_no_mcp_wins_regardless_of_position()
{
    var options = ServeOptions.Parse(["--mcp", "--no-mcp"]);
    Assert.False(options.EnableMcp);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~Parse_no_mcp_flag_disables_mcp"`
Expected: FAIL — `ServeOptions` has no `EnableMcp` (compile error).

- [ ] **Step 3: Add the property**

In `src/Coda.Tui/ServeOptions.cs`, add after the `ForceTelemetry` / `TelemetryLevel` properties (before the `Parse` summary):

```csharp
/// <summary>When false (<c>--no-mcp</c> or <c>CODA_SERVE_DISABLE_MCP</c>), skip connecting MCP
/// servers. Defaults to true for parity with the TUI and <c>coda run</c>.</summary>
public bool EnableMcp { get; init; } = true;
```

- [ ] **Step 4: Add the parse local + cases + wire into the returned record**

In `ServeOptions.Parse`, add a local next to the other flag locals (near `var forceTelemetry = false;`):

```csharp
var enableMcp = true;
```

Add these `case` labels inside the `switch (arg)` (next to `--session-memory`):

```csharp
case "--no-mcp":
    enableMcp = false;
    break;

case "--mcp":
    enableMcp = true;
    break;
```

Add to the returned `new ServeOptions { ... }` initializer (next to `TelemetryLevel = telemetryLevel,`):

```csharp
EnableMcp = enableMcp,
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~ServeRunnerTests&FullyQualifiedName~mcp"`
Expected: PASS (4 new tests green).

- [ ] **Step 6: Commit**

```bash
git add src/Coda.Tui/ServeOptions.cs tests/Coda.Tui.Tests/ServeRunnerTests.cs
git commit -m "feat(serve): add ServeOptions.EnableMcp + --no-mcp/--mcp parsing (M1)"
```

---

### Task 2: `ServeRunner.ResolveMcpEnabled` — env override

**Files:**
- Modify: `src/Coda.Tui/ServeRunner.cs` (add `public static bool ResolveMcpEnabled(...)`)
- Test: `tests/Coda.Tui.Tests/ServeRunnerTests.cs`

**Interfaces:**
- Consumes: `ServeOptions.EnableMcp` (Task 1).
- Produces: `public static bool ServeRunner.ResolveMcpEnabled(bool parsedEnableMcp, string? disableEnvValue)` — consumed by `RunAsync` (Task 6).

- [ ] **Step 1: Write the failing tests**

Add to `ServeRunnerTests.cs`:

```csharp
// ── ResolveMcpEnabled (CODA_SERVE_DISABLE_MCP override) ────────────────

[Theory]
[InlineData(true, null, true)]
[InlineData(true, "", true)]
[InlineData(true, "0", true)]
[InlineData(true, "1", false)]
[InlineData(true, "true", false)]
[InlineData(false, null, false)]   // --no-mcp already off; env absent keeps it off
[InlineData(false, "1", false)]
public void ResolveMcpEnabled_applies_env_override(bool parsed, string? env, bool expected)
{
    Assert.Equal(expected, ServeRunner.ResolveMcpEnabled(parsed, env));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~ResolveMcpEnabled_applies_env_override"`
Expected: FAIL — `ResolveMcpEnabled` not defined (compile error).

- [ ] **Step 3: Implement**

In `src/Coda.Tui/ServeRunner.cs`, add near the other public static seams (e.g. after `ValidateApiMode`):

```csharp
/// <summary>
/// Resolves whether MCP should be connected for this serve run: the parsed flag default
/// (<c>--no-mcp</c> / <c>--mcp</c>), overridden off by <c>CODA_SERVE_DISABLE_MCP</c> in
/// (<c>"1"</c>, <c>"true"</c>). Split out so the env precedence is unit-testable.
/// </summary>
public static bool ResolveMcpEnabled(bool parsedEnableMcp, string? disableEnvValue)
    => disableEnvValue is "1" or "true" ? false : parsedEnableMcp;
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~ResolveMcpEnabled_applies_env_override"`
Expected: PASS (7 theory cases).

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/ServeRunner.cs tests/Coda.Tui.Tests/ServeRunnerTests.cs
git commit -m "feat(serve): ResolveMcpEnabled env override for CODA_SERVE_DISABLE_MCP (M1)"
```

---

### Task 3: `BuildSessionOptions` threads `ExtraTools`

**Files:**
- Modify: `src/Coda.Tui/ServeRunner.cs` (add optional `extraTools` param to `BuildSessionOptions`)
- Test: `tests/Coda.Tui.Tests/ServeRunnerTests.cs`

**Interfaces:**
- Produces: `public static SessionOptions BuildSessionOptions(ServeOptions options, TelemetrySettings? baseTelemetry = null, IReadOnlyList<ITool>? extraTools = null)` — the third param defaults to null (→ empty), so all existing callers/tests are unaffected.

- [ ] **Step 1: Write the failing tests**

Add to `ServeRunnerTests.cs` (needs `using Coda.Mcp;` at the top of the file — add it if absent):

```csharp
[Fact]
public void BuildSessionOptions_defaults_extra_tools_to_empty()
{
    var options = ServeRunner.Parse(["--cwd", "C:\\x"]);

    var so = ServeRunner.BuildSessionOptions(options);

    Assert.Empty(so.ExtraTools);
}

[Fact]
public void BuildSessionOptions_threads_extra_tools_through()
{
    var options = ServeRunner.Parse(["--cwd", "C:\\x"]);
    // A real MCP helper tool instance is a convenient ITool sample (no hand-written double).
    ITool sample = new ListMcpPromptsTool(new McpClientManager());

    var so = ServeRunner.BuildSessionOptions(options, baseTelemetry: null, extraTools: [sample]);

    Assert.Single(so.ExtraTools);
    Assert.Same(sample, so.ExtraTools[0]);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~BuildSessionOptions_threads_extra_tools_through"`
Expected: FAIL — `BuildSessionOptions` has no third parameter (compile error).

- [ ] **Step 3: Implement**

In `src/Coda.Tui/ServeRunner.cs`, change the `BuildSessionOptions` signature and add the `ExtraTools` initializer line:

```csharp
public static SessionOptions BuildSessionOptions(
    ServeOptions options,
    TelemetrySettings? baseTelemetry = null,
    IReadOnlyList<ITool>? extraTools = null) =>
    new()
    {
        ProviderId = options.ProviderId!,
        Model = options.Model!,
        WorkingDirectory = options.WorkingDirectory!,
        PermissionMode = options.PermissionMode,
        EnableBypassClassifier = options.EnableClassifier,
        InteractivePrompt = null,
        Goal = options.Goal,
        EnableSessionMemory = options.EnableSessionMemory,
        MaxStopContinuations = options.MaxStopContinuations,
        GoalMaxDuration = options.GoalMaxDuration,
        GoalMaxContinuations = options.GoalMaxContinuations,
        ExtraTools = extraTools ?? [],
        TelemetryOverride = TelemetryResolver.ResolveServeOverride(
            options.ForceTelemetry, options.TelemetryLevel, baseTelemetry),
    };
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~BuildSessionOptions"`
Expected: PASS (new tests + all existing `BuildSessionOptions_*` tests still green).

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/ServeRunner.cs tests/Coda.Tui.Tests/ServeRunnerTests.cs
git commit -m "feat(serve): BuildSessionOptions threads ExtraTools (M2)"
```

---

### Task 4: `ServeRunner.BuildMcpExtraTools` — tool composition

**Files:**
- Modify: `src/Coda.Tui/ServeRunner.cs` (add `public static IReadOnlyList<ITool> BuildMcpExtraTools(McpClientManager)`; add `using Coda.Mcp;`)
- Test: `tests/Coda.Tui.Tests/ServeRunnerTests.cs`

**Interfaces:**
- Consumes: `Coda.Mcp.McpClientManager` (public ctor `McpClientManager(IMcpHttpClientFactory? = null)`; `manager.Tools`), and the four public helper tools `ListMcpResourcesTool`, `ReadMcpResourceTool`, `ListMcpPromptsTool`, `GetMcpPromptTool` (each ctor takes an `McpClientManager`).
- Produces: `public static IReadOnlyList<ITool> ServeRunner.BuildMcpExtraTools(McpClientManager manager)` — server tools followed by the four resource/prompt helper tools, matching the interactive TUI (`Program.cs`).

- [ ] **Step 1: Write the failing test**

Add to `ServeRunnerTests.cs`:

```csharp
// ── BuildMcpExtraTools composition ────────────────────────────────────

[Fact]
public void BuildMcpExtraTools_appends_the_four_resource_prompt_helpers()
{
    // An empty manager (no connected servers) → manager.Tools is empty, so the result is
    // exactly the four helper tools the TUI also adds.
    var manager = new McpClientManager();

    var tools = ServeRunner.BuildMcpExtraTools(manager);

    Assert.Equal(4, tools.Count);
    Assert.Single(tools.OfType<ListMcpResourcesTool>());
    Assert.Single(tools.OfType<ReadMcpResourceTool>());
    Assert.Single(tools.OfType<ListMcpPromptsTool>());
    Assert.Single(tools.OfType<GetMcpPromptTool>());
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~BuildMcpExtraTools_appends_the_four_resource_prompt_helpers"`
Expected: FAIL — `BuildMcpExtraTools` not defined (compile error).

- [ ] **Step 3: Implement**

Ensure `using Coda.Mcp;` is present at the top of `src/Coda.Tui/ServeRunner.cs`. Then add:

```csharp
/// <summary>
/// Composes the agent's MCP tool list: the servers' own tools followed by the four
/// resource/prompt helper tools (matching the interactive TUI). Split out so the
/// composition is unit-testable with an empty <see cref="McpClientManager"/>.
/// </summary>
public static IReadOnlyList<ITool> BuildMcpExtraTools(McpClientManager manager)
{
    ArgumentNullException.ThrowIfNull(manager);
    return
    [
        .. manager.Tools,
        new ListMcpResourcesTool(manager),
        new ReadMcpResourceTool(manager),
        new ListMcpPromptsTool(manager),
        new GetMcpPromptTool(manager),
    ];
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~BuildMcpExtraTools_appends_the_four_resource_prompt_helpers"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/ServeRunner.cs tests/Coda.Tui.Tests/ServeRunnerTests.cs
git commit -m "feat(serve): BuildMcpExtraTools composes server + helper tools (M2)"
```

---

### Task 5: `ServeRunner.LoadMcpToolsAsync` — connect + compose loader

**Files:**
- Modify: `src/Coda.Tui/ServeRunner.cs` (add `public static async Task<...> LoadMcpToolsAsync(...)`)
- Test: `tests/Coda.Tui.Tests/ServeRunnerTests.cs`

**Interfaces:**
- Consumes: `ResolveMcpEnabled` result (bool), `Coda.Mcp.IMcpHttpClientFactory`, `Coda.Mcp.McpConfig.Load(workingDirectory, userMcpDir)`, `McpClientManager.ConnectAllAsync`, `BuildMcpExtraTools` (Task 4).
- Produces: `public static Task<(IReadOnlyList<ITool> Tools, McpClientManager? Manager)> ServeRunner.LoadMcpToolsAsync(bool enableMcp, string workingDirectory, IMcpHttpClientFactory httpFactory, Action<string> log, CancellationToken cancellationToken, string? userMcpDir = null)`. Returns a non-null `Manager` (which the caller MUST dispose) only when at least one server is configured; otherwise `(empty, null)`.

- [ ] **Step 1: Write the failing tests**

Add to `ServeRunnerTests.cs` (add the throwing factory stub as a nested private class alongside `TempSettingsHome`):

```csharp
// ── LoadMcpToolsAsync ─────────────────────────────────────────────────

[Fact]
public async Task LoadMcpToolsAsync_disabled_returns_empty_and_no_manager()
{
    var (tools, manager) = await ServeRunner.LoadMcpToolsAsync(
        enableMcp: false,
        workingDirectory: Directory.GetCurrentDirectory(),
        httpFactory: new ThrowingHttpFactory(),
        log: _ => { },
        cancellationToken: default);

    Assert.Empty(tools);
    Assert.Null(manager);
}

[Fact]
public async Task LoadMcpToolsAsync_enabled_but_no_servers_returns_empty_and_no_manager()
{
    // Hermetic: both the working dir and the user MCP dir are empty temp dirs, so
    // McpConfig.Load finds zero servers regardless of the machine's real ~/.coda.
    using var work = new TempDir();
    using var user = new TempDir();

    var (tools, manager) = await ServeRunner.LoadMcpToolsAsync(
        enableMcp: true,
        workingDirectory: work.Path,
        httpFactory: new ThrowingHttpFactory(),
        log: _ => { },
        cancellationToken: default,
        userMcpDir: user.Path);

    Assert.Empty(tools);
    Assert.Null(manager);
}
```

Add these nested private helpers to the test class (next to `TempSettingsHome`):

```csharp
private sealed class ThrowingHttpFactory : Coda.Mcp.IMcpHttpClientFactory
{
    // No HTTP server is configured in these tests, so Create must never be called.
    public Coda.Mcp.IMcpClient Create(string serverName, Coda.Mcp.McpHttpServerConfig config)
        => throw new InvalidOperationException("HTTP factory must not be used when no HTTP server is configured.");
}

private sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coda-mcp-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(this.Path);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.Path))
            {
                Directory.Delete(this.Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~LoadMcpToolsAsync"`
Expected: FAIL — `LoadMcpToolsAsync` not defined (compile error).

- [ ] **Step 3: Implement**

In `src/Coda.Tui/ServeRunner.cs`, add:

```csharp
/// <summary>
/// Loads and connects the merged MCP config for a serve session, returning the agent's MCP
/// tool list and the owning <see cref="McpClientManager"/> (which the caller MUST dispose).
/// Returns <c>(empty, null)</c> when MCP is disabled or no servers are configured — in that
/// case nothing is left to dispose. HTTP servers are connected non-interactively via
/// <paramref name="httpFactory"/>; all diagnostics go through <paramref name="log"/> (stderr).
/// </summary>
/// <param name="userMcpDir">Test/override seam for the user-level <c>.mcp.json</c> directory;
/// null uses the default resolution (<c>CODA_USER_MCP_DIR</c> or <c>~/.coda</c>).</param>
public static async Task<(IReadOnlyList<ITool> Tools, McpClientManager? Manager)> LoadMcpToolsAsync(
    bool enableMcp,
    string workingDirectory,
    IMcpHttpClientFactory httpFactory,
    Action<string> log,
    CancellationToken cancellationToken,
    string? userMcpDir = null)
{
    if (!enableMcp)
    {
        return ([], null);
    }

    var manager = new McpClientManager(httpFactory);
    var servers = McpConfig.Load(workingDirectory, userMcpDir);
    if (servers.Count == 0)
    {
        await manager.DisposeAsync().ConfigureAwait(false);
        return ([], null);
    }

    await manager.ConnectAllAsync(servers, log, cancellationToken).ConfigureAwait(false);
    return (BuildMcpExtraTools(manager), manager);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~LoadMcpToolsAsync"`
Expected: PASS (both tests).

- [ ] **Step 5: Commit**

```bash
git add src/Coda.Tui/ServeRunner.cs tests/Coda.Tui.Tests/ServeRunnerTests.cs
git commit -m "feat(serve): LoadMcpToolsAsync connect+compose loader with hermetic seam (M2)"
```

---

### Task 6: Wire MCP into `ServeRunner.RunAsync` (composition root)

**Files:**
- Modify: `src/Coda.Tui/ServeRunner.cs` (`RunAsync`: resolve enable, build factory, load tools, thread into `BuildSessionOptions`, dispose)
- Test: no new unit test — `RunAsync` is the process/transport composition root (only the pre-existing `BuildHost` seam is unit-tested). This task is verified by (a) the whole suite staying green, (b) a warning-clean build, and (c) a manual smoke check below. All logic it calls is already unit-tested in Tasks 1–5.

**Interfaces:**
- Consumes: `ResolveMcpEnabled`, `LoadMcpToolsAsync`, `BuildMcpExtraTools` (via the loader), `BuildSessionOptions(options, telemetry, extraTools)`, `Coda.Mcp.DefaultMcpHttpClientFactory`, `CredentialStoreFactory.Create()`.

- [ ] **Step 1: Remove the early `BuildSessionOptions` call**

In `RunAsync`, delete this line (currently around line 155, before the API-key resolution):

```csharp
var sessionOptions = BuildSessionOptions(options, settings.Telemetry);
```

(`sessionOptions` is only consumed at `BuildHost`, far below; it will be rebuilt with MCP tools in Step 2.)

- [ ] **Step 2: Load MCP and build session options inside the transport block**

Inside the `await using (transport)` block, AFTER `cts` is created and the models-refresh `Task.Run(...)` is kicked off, and BEFORE `var streams = await transport.AcceptAsync(cts.Token)...`, insert:

```csharp
// Connect MCP servers (parity with the TUI / `coda run`): default-on unless --no-mcp
// or CODA_SERVE_DISABLE_MCP. Non-interactive (never opens a browser); all MCP
// diagnostics go to stderr because stdout is the JSON-RPC protocol channel.
var enableMcp = ResolveMcpEnabled(
    options.EnableMcp, Environment.GetEnvironmentVariable("CODA_SERVE_DISABLE_MCP"));
using var mcpHttp = new HttpClient();
var mcpHttpFactory = new Coda.Mcp.DefaultMcpHttpClientFactory(
    mcpHttp, CredentialStoreFactory.Create(), interactive: false,
    msg => Console.Error.WriteLine(msg));
var (mcpTools, mcpManager) = await LoadMcpToolsAsync(
    enableMcp, options.WorkingDirectory!, mcpHttpFactory,
    msg => Console.Error.WriteLine(msg), cts.Token).ConfigureAwait(false);
await using var mcpScope = mcpManager; // no-op when null; disposes the manager after the host stops
var sessionOptions = BuildSessionOptions(options, settings.Telemetry, mcpTools);
```

Notes for the implementer:
- `HttpClient` needs `using System.Net.Http;` — add it to the top of `ServeRunner.cs` if not already present.
- `DefaultMcpHttpClientFactory` and `CredentialStoreFactory` are already reachable (`Coda.Mcp` via `using Coda.Mcp;` added in Task 4; `CredentialStoreFactory` is in the `Coda.Tui` namespace).
- `mcpHttp` (a `using` local) disposes when the transport block exits — after `host.RunAsync` returns — which is correct: the HTTP MCP clients must stay usable for the whole session.
- `await using var mcpScope = mcpManager;` disposes the stdio server processes / HTTP clients at the same point. `await using` on a null is a no-op, so the disabled / no-servers case is safe.

- [ ] **Step 3: Confirm `BuildHost` still consumes the rebuilt `sessionOptions`**

The existing lines remain unchanged:

```csharp
var streams = await transport.AcceptAsync(cts.Token).ConfigureAwait(false);
await using var host = BuildHost(streams.Input, streams.Output, credentials, sessionOptions, options.ApiKey);
await host.RunAsync(cts.Token).ConfigureAwait(false);
return 0;
```

- [ ] **Step 4: Build warning-clean**

Run: `dotnet build src/Coda.Tui/Coda.Tui.csproj -warnaserror`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 5: Run the full Coda.Tui test suite (no regressions)**

Run: `dotnet test tests/Coda.Tui.Tests`
Expected: PASS — all tests green, including the pre-existing `BuildHost_*` and `BuildSessionOptions_*` tests.

- [ ] **Step 6: Manual smoke check (stdout purity + opt-out)**

With no `.mcp.json` configured, a serve process must start clean and never print MCP noise to stdout. Drive a minimal handshake and confirm the readiness/JSON-RPC frames on stdout carry no MCP diagnostics (those, if any, go to stderr):

```bash
# From repo root; --no-mcp must produce identical stdout to the default-on no-servers case.
echo '' | dotnet run --project src/Coda.Tui -- serve --no-mcp --provider anthropic-api-key --model claude-sonnet-5 2>/dev/null | head -5
```

Expected: only protocol JSON on stdout (or nothing if it waits on input); no `MCP server ...` lines on stdout.

- [ ] **Step 7: Commit**

```bash
git add src/Coda.Tui/ServeRunner.cs
git commit -m "feat(serve): wire MCP into RunAsync — default-on, stderr-only, disposed with host (M2)"
```

---

### Task 7: Docs — README, serve protocol, serve help text

**Files:**
- Modify: `README.md` (MCP section notes `serve` now participates)
- Modify: `docs/serve-protocol.md` (auth-skip + default-on behavior)
- Modify: `src/Coda.Tui/ImmediateCli.cs` (serve usage/help lists `--no-mcp`)
- Test: `tests/Coda.Tui.Tests/ServeRunnerTests.cs` already asserts `--help` documents `serve`; add an assertion that the serve help mentions `--no-mcp`.

**Interfaces:** none (documentation + help string).

- [ ] **Step 1: Write the failing help-text test**

Add to `ServeRunnerTests.cs` (Help text region):

```csharp
[Fact]
public void Help_output_documents_no_mcp_flag()
{
    var writer = new StringWriter();
    ImmediateCli.TryHandle(["--help"], writer);
    var output = writer.ToString();

    Assert.Contains("--no-mcp", output);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~Help_output_documents_no_mcp_flag"`
Expected: FAIL — help text does not yet mention `--no-mcp`.

- [ ] **Step 3: Add `--no-mcp` to the serve help text**

In `src/Coda.Tui/ImmediateCli.cs`, locate the serve usage/flag block (the same text that lists `--yolo`, `--session-memory`, `--telemetry`) and add a line describing the flag, matching the surrounding format, e.g.:

```
  --no-mcp                 Do not connect MCP servers for this serve session (default: connect)
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~Help_output_documents_no_mcp_flag"`
Expected: PASS.

- [ ] **Step 5: Update `README.md`**

In the MCP section, replace any statement that only the TUI / `coda run` load MCP with a note that **all three entry points** now do, and document the serve controls. Add (adjust to the section's prose style):

```markdown
`coda serve` connects the same merged MCP config (`~/.coda/.mcp.json` + `<cwd>/.mcp.json`)
as the interactive TUI and `coda run`, by default. Disable it per session with `--no-mcp`
(or `CODA_SERVE_DISABLE_MCP=1`). Point serve at an orchestrator-curated config with
`CODA_USER_MCP_DIR`. HTTP MCP servers are connected non-interactively: a valid stored token
is reused; a server needing a fresh sign-in is skipped and logged to stderr (never opens a
browser). Pre-authorize such servers once via the TUI or `coda run`.
```

- [ ] **Step 6: Update `docs/serve-protocol.md`**

Add a short subsection documenting: MCP is default-on under serve; `--no-mcp` / `CODA_SERVE_DISABLE_MCP` disable it; `CODA_USER_MCP_DIR` selects a curated set; HTTP servers needing fresh auth are skipped + logged to stderr; and the invariant that **no MCP diagnostic is ever written to stdout** (stdout is the protocol channel).

- [ ] **Step 7: Build + full suite + commit**

Run: `dotnet build src/Coda.Tui/Coda.Tui.csproj -warnaserror` → 0 warnings.
Run: `dotnet test tests/Coda.Tui.Tests` → all green.

```bash
git add README.md docs/serve-protocol.md src/Coda.Tui/ImmediateCli.cs tests/Coda.Tui.Tests/ServeRunnerTests.cs
git commit -m "docs(serve): document MCP-in-serve (README, serve-protocol, --no-mcp help) (M3)"
```

---

## Self-Review

**Spec coverage** (against `docs/proposals/2026-07-03-mcp-in-serve-path.md`):
- §4 wire MCP into `ServeRunner` → Tasks 4–6. `SessionOptions.ExtraTools` threading → Task 3. Disposal/lifetime → Task 6 Step 2 (`using` HttpClient + `await using` manager spanning `host.RunAsync`). stderr discipline → Task 6 (`Console.Error.WriteLine` for both factory and connect logs) + Task 6 Step 6 regression smoke.
- §5 default-on + `--no-mcp` + `--mcp` → Task 1; `CODA_SERVE_DISABLE_MCP` → Task 2; `CODA_USER_MCP_DIR` → honored automatically via `McpConfig.Load` (Task 5 loader) — no code needed, documented in Task 7.
- §6 non-interactive auth (`interactive: false`) → Task 6 factory construction. stdio/HTTP-token/HTTP-skip behavior is `McpClientManager`/`DefaultMcpHttpClientFactory` existing behavior — reused, not reimplemented.
- §10 milestones: M1 → Tasks 1–2; M2 → Tasks 3–6; M3 → Task 7. **M4 (Bridge repo) is out of scope** for this coda-side plan (separate repo/PR).
- §11 testing: `ServeOptions.Parse` → Task 1; `ResolveMcpEnabled` env precedence → Task 2; `ExtraTools` threading → Task 3; tool composition → Task 4; loader branches (disabled / no-servers) → Task 5; stdout-purity regression → Task 6 Step 6.

**Coverage boundary (honest note):** the one line not covered by an automated unit test is `manager.ConnectAllAsync(...)` inside `LoadMcpToolsAsync` (the servers-configured branch), because exercising it requires spawning a real stdio MCP server process. That branch's *output composition* is covered by Task 4 (`BuildMcpExtraTools`), and `ConnectAllAsync` itself is covered by `Engine.Tests` in `Coda.Mcp`. `RunAsync` remains at its pre-existing coverage (only the `BuildHost` seam is unit-tested); every new decision it makes is delegated to a fully-tested seam. If true end-to-end coverage of the connect path is required, add an integration test with a fake stdio MCP server as a follow-up (spec §9 / future work).

**Placeholder scan:** no TBD/TODO; every code step shows complete code; the only prose-only step is the README/serve-protocol wording (Task 7 Steps 5–6), which is documentation, not code.

**Type consistency:** `ServeOptions.EnableMcp` (bool) used identically in Tasks 1/2/6. `BuildSessionOptions(ServeOptions, TelemetrySettings?, IReadOnlyList<ITool>?)` signature is consistent across Tasks 3 and 6. `LoadMcpToolsAsync` return tuple `(IReadOnlyList<ITool> Tools, McpClientManager? Manager)` is consistent across Tasks 5 and 6. `BuildMcpExtraTools(McpClientManager) : IReadOnlyList<ITool>` consistent across Tasks 4 and 5.
