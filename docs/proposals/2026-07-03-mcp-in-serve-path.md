# Proposal: MCP servers in the coda serve path (host MCP for orchestrator-driven coda)

- **Date:** 2026-07-03
- **Status:** Draft / design exploration
- **Author:** Yury Opolev (design exploration, Coda)
- **Scope:** `Coda.Tui` (`ServeRunner`, `ServeOptions`), `Coda.Mcp` wiring; downstream note for
  Cortex Bridge (`CodaServeArgsBuilder`)

## 1. Summary

`coda serve` (the JSON-RPC-over-stdio backend used by orchestrators such as **Cortex**) is the
**only** coda entry point that does **not** connect MCP servers. The interactive TUI and
`coda run` both load `~/.coda/.mcp.json` + `<cwd>/.mcp.json` and expose the servers' tools to the
agent; `serve` silently exposes none.

This proposal wires MCP into the serve path so a serve-hosted session sees the same MCP tools as
the other two entry points, and resolves the one design question the wiring forces us to answer
explicitly:

> **Should serve load host MCP config by default, or only behind a switch?**

**Recommendation:** load MCP **by default** (parity with the TUI and `coda run`), with an explicit
`--no-mcp` opt-out, a `CODA_SERVE_DISABLE_MCP` env fallback, and reliance on the existing
`CODA_USER_MCP_DIR` override so an orchestrator can inject a **curated** MCP set instead of the
human's personal `~/.coda/.mcp.json`.

## 2. Current state (verified on `7bab63e`)

| Entry point | Loads `.mcp.json`? | Wiring |
|---|---|---|
| Interactive TUI | ✅ | `src/Coda.Tui/Program.cs:100` → `McpConfig.Load`, `context.ExtraTools = agentTools` |
| `coda run` (headless) | ✅ | `src/Coda.Tui/HeadlessRunner.cs:87` → `McpConfig.Load`; `:99` `ExtraTools = mcp.Tools` |
| **`coda serve`** | ❌ | `src/Coda.Tui/ServeRunner.cs` never calls `McpConfig.Load`; `BuildSessionOptions` never sets `ExtraTools` |

`grep` confirms `McpConfig.Load` appears only in `Program.cs` and `HeadlessRunner.cs`.
`ServeRunner.BuildSessionOptions` (`ServeRunner.cs:253`) constructs `SessionOptions` with no
`ExtraTools`, so the serve-hosted `CodaSession` has no MCP tools at all. `SessionOptions.ExtraTools`
already exists and defaults to empty (`src/Coda.Sdk/SessionOptions.cs:19`); the tool registry folds
it in at every construction site (`TurnPipelineBuilder`, `CodaSession`, `InProcessTeammateAgent`).

### How Cortex drives coda today

The Cortex Bridge spawns coda via `serve`
(`src/Cortex.Contained.Bridge/Coding/CodaServeArgsBuilder.cs`):

```
coda serve --cwd <sessionFolder> --session-id <id> --permission-mode <m> --telemetry \
  [--provider <p>] [--goal <g>] [--session-memory]
```

The binary is the **bundled host `coda.exe`** (`CodaOptions.ResolveDefaultBinaryPath` →
`<BaseDir>\coda\coda.exe`, else PATH), running on the **Windows host** as the Bridge's user. So
`~/.coda/.mcp.json` **is** reachable — the only gap is that serve never reads it.

Because `--cwd` points at a Cortex-managed per-session working folder (which normally has no
`.mcp.json`), in practice the **user-level `~/.coda/.mcp.json` is the layer that would apply** —
i.e. "use the host's MCP settings" is exactly what default-on delivers.

## 3. Goals & non-goals

**Goals**

- A `coda serve` session connects the same merged MCP config (`~/.coda/.mcp.json` +
  `<cwd>/.mcp.json`) as the TUI / `coda run`, and exposes the servers' tools as
  `mcp__<server>__<tool>` (plus the resource/prompt helper tools the TUI adds).
- Non-interactive auth semantics identical to `coda run`: stored tokens are reused; a server that
  would need a fresh browser sign-in is **logged to stderr and skipped**, never blocking.
- An explicit, documented way for an orchestrator to **disable** MCP or **substitute** a curated
  config, without editing the human's personal `~/.coda`.
- No new OAuth code — reuse `DefaultMcpHttpClientFactory` and `McpClientManager` exactly as
  `HeadlessRunner` does.

**Non-goals**

- Interactive OAuth inside serve (stdout is the protocol channel; there is no TTY). HTTP servers
  needing fresh sign-in are pre-authorized out-of-band (via the TUI or `coda run`) and serve reuses
  the stored token.
- A `/mcp` slash command (that is the interactive TUI's concern and is **tracked separately** — see
  the companion `/mcp` interactive-management design; it does **not** affect the serve path).
- Exposing MCP server management over the serve JSON-RPC protocol (future work; see §9).
- Elicitation / sampling / server-initiated requests (already out of scope in Coda).

## 4. Core design — wire MCP into `ServeRunner`

Mirror `HeadlessRunner` (`src/Coda.Tui/HeadlessRunner.cs:83–99`) inside `ServeRunner.RunAsync`, and
thread the resulting tools into `SessionOptions.ExtraTools`.

```csharp
// ServeRunner.RunAsync, after credentials are built and before BuildHost(...):

IReadOnlyList<Coda.Agent.ITool> extraTools = [];
McpClientManager? mcp = null;
HttpClient? mcpHttp = null;
if (options.EnableMcp) // see §5 for how EnableMcp is resolved
{
    mcpHttp = new HttpClient();                            // disposed on shutdown
    var mcpHttpFactory = new DefaultMcpHttpClientFactory(
        mcpHttp, CredentialStoreFactory.Create(),
        interactive: false,                               // serve is headless — never opens a browser
        msg => Console.Error.WriteLine(msg));             // diagnostics to stderr, NEVER stdout
    mcp = new McpClientManager(mcpHttpFactory);

    var mcpServers = McpConfig.Load(options.WorkingDirectory!);
    if (mcpServers.Count > 0)
    {
        await mcp.ConnectAllAsync(mcpServers, msg => Console.Error.WriteLine(msg), cts.Token);
        extraTools =
        [
            .. mcp.Tools,
            new ListMcpResourcesTool(mcp), new ReadMcpResourceTool(mcp),
            new ListMcpPromptsTool(mcp),   new GetMcpPromptTool(mcp),
        ];
    }
}

var sessionOptions = BuildSessionOptions(options, settings.Telemetry) with { ExtraTools = extraTools };
```

Required changes:

1. **`ServeRunner.BuildSessionOptions`** — currently omits `ExtraTools`. Either set it here or apply
   it via `with { ExtraTools = ... }` at the call site (above). Keep `BuildSessionOptions`
   unit-testable by passing the tool list in (add an optional `extraTools` parameter defaulting to
   empty, so the existing telemetry-focused tests are unaffected).
2. **Lifetime** — `McpClientManager` (`IAsyncDisposable`) and its `HttpClient` (`IDisposable`) must
   live for the whole serve session; dispose them alongside the `ServeHost` (both are created around
   `host.RunAsync` and disposed after it returns / on the exception path).
3. **stdout discipline** — all MCP connect/skip diagnostics go to **stderr**. This is the existing
   serve invariant (stdout is the JSON-RPC channel); `HeadlessRunner` already logs MCP to
   `Console.Error`, so this is consistent.

## 5. The design question: default-on vs. switch

Three options; the wiring in §4 is identical apart from how `EnableMcp` is resolved.

### Option A — default-on, no switch

Always load MCP (pure parity with TUI / `coda run`).

- ➕ Simplest; principle of least surprise — all three entry points behave identically.
- ➖ An orchestrator can't turn it off without editing `~/.coda/.mcp.json`; the coding sub-agent's
  tool surface (and token cost / security exposure) is dictated by host config.

### Option B — opt-in switch (`--mcp` / `--enable-mcp`)

Off unless the caller asks.

- ➕ Orchestrator explicitly controls the tool surface; safest default for programmatic use.
- ➖ Breaks parity — a human who runs `coda serve` by hand gets different behavior than the TUI;
  easy to forget and wonder why tools are missing. Diverges from `coda run`.

### Option C — default-on with opt-out (`--no-mcp`) ✅ recommended

Load by default; allow the caller to suppress it.

- ➕ Parity with TUI / `coda run` **and** orchestrator control.
- ➕ Matches the forward-compatible, additive style of `ServeOptions.Parse` (unknown flags are
  ignored; boolean flags like `--telemetry`, `--session-memory` set a bool).
- ➖ One more flag to document.

**Recommendation: Option C**, plus two escape hatches that need no new code paths:

- **`CODA_SERVE_DISABLE_MCP=1`** env fallback (mirrors how `CODA_DISABLE_MODELS_FETCH` /
  `CODA_SERVE_API_KEY` are honored) — lets an operator disable MCP for all spawned serve processes
  without changing the spawn args.
- **`CODA_USER_MCP_DIR`** (already supported by `McpConfig.Load`) — point coda at a **curated**
  `.mcp.json` the orchestrator owns, instead of the human's personal `~/.coda`. This is the cleanest
  way for Cortex to give the coding engine a **deliberate** MCP set (e.g. only a filesystem + a docs
  server) decoupled from whatever the user configured for their interactive coda.

### `ServeOptions` change

Add to `src/Coda.Tui/ServeOptions.cs`:

```csharp
/// <summary>When false (--no-mcp or CODA_SERVE_DISABLE_MCP), skip connecting MCP servers.</summary>
public bool EnableMcp { get; init; } = true;
```

Parse (additive, matches existing switch style):

```csharp
case "--no-mcp":
    enableMcp = false;
    break;

// optional explicit form for symmetry / self-documenting spawn args:
case "--mcp":
    enableMcp = true;
    break;
```

Resolve the env fallback where other env-driven options are resolved in `RunAsync` (next to the
`CODA_SERVE_API_KEY` resolution at `ServeRunner.cs:158`):

```csharp
if (Environment.GetEnvironmentVariable("CODA_SERVE_DISABLE_MCP") is "1" or "true")
{
    options = options with { EnableMcp = false };
}
```

## 6. Non-interactive auth behavior

Identical to `coda run` (`interactive: false`):

| MCP server kind | Behavior under serve |
|---|---|
| stdio | Launched normally — **works** (subject to the sandbox / permission mode). |
| HTTP with a valid stored token | Reused — **works**. |
| HTTP needing a fresh OAuth sign-in | **Skipped**, logged to stderr; never opens a browser, never blocks the handshake. |

Operational guidance: pre-authorize HTTP MCP servers once via the interactive TUI (or `coda run`) on
the host; the encrypted token under `~/.coda/credentials` (keyed by canonical resource URI) is then
reused by every serve process automatically, including refresh.

## 7. Downstream: Cortex Bridge integration

With Option C, Cortex chooses per policy in
`src/Cortex.Contained.Bridge/Coding/CodaServeArgsBuilder.cs`:

- **Default (do nothing):** the spawned coda gets the host's `~/.coda/.mcp.json` servers — the "use
  MCP settings on host by default" behavior the request asks about.
- **Curated set:** set `CODA_USER_MCP_DIR` on the spawned process's environment to a Cortex-managed
  directory containing a vetted `.mcp.json`. Recommended for multi-tenant / least privilege — the
  coding engine only sees MCP servers Cortex intends, not the operator's personal ones.
- **Off:** append `--no-mcp` (or set `CODA_SERVE_DISABLE_MCP=1`) when a session should have no
  external MCP tools.

A follow-up could surface this as a `CodaOptions` / YAML setting (e.g. `Coding:Mcp = host|curated|off`)
so it is configurable without a rebuild. Out of scope for the coda-side change but noted for the
Bridge.

**Interaction note:** Cortex's **own** host-side MCP plugin system (Bridge↔agent SignalR hub) is a
separate mechanism and does **not** flow into coda. This proposal only concerns coda's own
`.mcp.json`-based MCP client.

## 8. Security considerations

- **Tool-surface expansion.** Turning MCP on gives the (possibly `--yolo`) coding agent whatever
  tools the configured servers expose. Pair default-on with the `CODA_USER_MCP_DIR` curation option
  for untrusted / multi-tenant contexts; document that `--yolo` + broad MCP = broad blast radius.
- **Credential reuse.** serve reads the same encrypted token store as the TUI. No new secret is
  introduced, but a serve process can now **use** those tokens. Disabling MCP (`--no-mcp`) fully
  removes that exposure for a given session.
- **Encrypted stdio secrets (shared with the `/mcp` TUI work).** The companion interactive
  `/mcp` feature persists secret-looking `env` / header values in coda's encrypted credential store
  and writes only a `coda-secret:<key>` (or `${ENV_VAR}`) reference into `.mcp.json` — never
  plaintext. Because resolution happens inside `McpConfig.Load`, serve resolves those references
  transparently and non-interactively (it already constructs `CredentialStoreFactory.Create()`), so
  no plaintext token need ever live in a `.mcp.json` that a serve process reads. This is a shared
  dependency, not serve-specific: when the loader gains secret-ref resolution, all three entry
  points (TUI, `coda run`, serve) must pass the store through to it.
- **No browser from serve.** `interactive: false` guarantees a serve process can never trigger an
  interactive OAuth popup on a headless / service host.
- **stdout purity.** MCP diagnostics must never reach stdout (protocol channel). Enforced by routing
  all MCP logging to stderr (§4.3); worth a regression test.

## 9. Future work

- **Serve-protocol MCP introspection** — a JSON-RPC method to list connected MCP servers / tools and
  per-server connect status, so the orchestrator can display or gate them.
- **Per-request MCP scoping** — allow the orchestrator to select a subset of servers per session via
  a protocol param rather than only via `CODA_USER_MCP_DIR`.
- **Step-up auth signaling** — when an HTTP server is skipped for lack of a token, emit a structured
  stderr / event the orchestrator can turn into a "sign in to server X" prompt.

## 10. Milestones

- **M1** — `EnableMcp` on `ServeOptions` + `--no-mcp` / `--mcp` parsing + `CODA_SERVE_DISABLE_MCP`
  fallback. Unit tests in `tests/Coda.Tui.Tests` for parsing + env precedence.
- **M2** — Wire `McpConfig.Load` + `McpClientManager` into `ServeRunner.RunAsync`; set
  `SessionOptions.ExtraTools`; correct disposal; stderr-only logging. Test: serve session with a
  stub stdio MCP server exposes `mcp__<server>__<tool>`; with `--no-mcp` exposes none.
- **M3** — Docs: update `README.md` (MCP section notes serve now participates) and the serve protocol
  doc (`docs/serve-protocol.md`) with the auth-skip behavior.
- **M4 (Bridge, separate repo/PR)** — optional `Coding:Mcp` policy in `CodaOptions` +
  `CodaServeArgsBuilder`; default = host config.

## 11. Testing

- `ServeOptions.Parse` — `--no-mcp` sets `EnableMcp=false`; absent → `true`; env override wins as
  specified.
- `ServeRunner` (via `BuildSessionOptions` seam) — tool list is threaded into `ExtraTools`; empty
  when disabled or when no servers configured.
- Integration — a fake stdio MCP server (echo tool) is reachable through a serve session's tool list;
  a fake HTTP server with no token is skipped without blocking the initialize handshake.
- Regression — assert no MCP diagnostic ever lands on stdout during a serve run.

## 12. Open questions

1. Default-on (Option C) vs opt-in (Option B) for the **coda** default — recommendation is C for
   parity, but if the primary consumer is orchestrators, an argument exists for B. Cortex can pin
   either explicitly regardless.
2. Should serve prefer `CODA_USER_MCP_DIR`-curated config as the **documented** Cortex path from day
   one (and should the Bridge set it by default to isolate the coding engine from the human's
   personal MCP servers)?
3. Do we want a protocol-level toggle in addition to the CLI flag, so a long-lived serve host could
   enable/disable MCP per session without respawning? (Deferred to §9.)
