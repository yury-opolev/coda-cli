# Coda Session Continue/Resume + Export/Import (Full Audit) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to
> implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring coda to Claude-parity for session handling — exit-and-continue/resume from the
shell (TUI **and** headless `coda run`, true append via id-adoption), plus export/import of a
self-contained **full-audit** session bundle (system prompt + tool defs + per-turn usage).

**Architecture:** Almost all logic lands in the SDK (`Coda.Sdk`) so the three front-ends benefit
uniformly; front-ends stay thin (parse flags → set the SDK up → run). The true-continue primitive
(`CodaSession.Resume`) already exists — the front-ends just never call it. Two genuinely new SDK
components: an append-only per-turn **audit sidecar** (`SessionAuditStore`), and a
**session-bundle** export/import service (`SessionBundleService`). Design:
`docs/superpowers/specs/2026-07-13-coda-session-continue-export-design.md`.

**Tech Stack:** C# / .NET 10, `System.Text.Json` (`JsonNode`), xUnit (Engine.Tests, Coda.Tui.Tests).

## Global Constraints

- **Style:** file-scoped namespaces; `this.` on all instance-member access; braces on every
  `if`/`for`/`foreach`/`using` (no single-line bodies); `sealed` classes; `readonly` fields;
  `var` when the RHS type is obvious; collection expressions (`[...]`); pattern matching.
- **`TreatWarningsAsErrors` is ON globally** — zero warnings, or the build fails.
- **Async:** suffix async methods with `Async`; **`ConfigureAwait(false)` on every await in
  library code** (`Coda.Sdk`, `Coda.Agent`, `LlmClient`). TUI (`Coda.Tui`) code does not require it.
- **Persistence must never break a turn:** every sidecar/transcript write is wrapped so a failure is
  logged (source-generated `[LoggerMessage]` on a `partial` class) and swallowed — mirror
  `CodaSession.PersistTranscriptAsync` (`src/Coda.Sdk/CodaSession.cs:636-650`).
- **Storage layout:** sessions live at `<workingDirectory>/.coda/sessions/`. Lean transcript is
  `<id>.json`; the new audit sidecar is **`<id>.audit.jsonl`** (append-only, one JSON object/line).
- **Bundle schema string is exactly `"coda.session/1"`.** Import rejects an unknown schema *major*.
- **Session id validity:** reuse `SessionTranscriptStore`'s rule (non-empty, no invalid filename
  chars, no path separators) for any id used as a file name. New ids are
  `Guid.NewGuid().ToString("N")[..12]` (the existing convention, `CodaSession.cs:80`).
- **Tests:** one temp dir per test via `Directory.CreateTempSubdirectory("coda_<x>_").FullName`,
  class implements `IDisposable` and best-effort-deletes it (see
  `tests/Engine.Tests/SessionTranscriptTests.cs`). `CapturingLogger` lives in
  `Engine.Tests.TestSupport`. Global `using Xunit;` is on (no per-file using needed).
- **No new NuGet dependencies.**

## File Structure

New (SDK — `src/Coda.Sdk/`):
- `SessionAuditTurn.cs` — the per-turn audit record (DTO).
- `SessionAuditStore.cs` — append-only sidecar writer/reader with change-only prompt/tools emission.
- `SessionBundle.cs` — the portable bundle DTO (+ `SessionBundleTurn`).
- `SessionBundleService.cs` — export (merge transcript+audit) / write / import.

New (TUI — `src/Coda.Tui/`):
- `SessionCli.cs` — shared resolver: `--continue`/`--resume [id]`/`export`/`import` for both
  `Program.cs` and headless.
- `Commands/ImportCommand.cs` — REPL `/import`.

Modified:
- `src/Coda.Sdk/CodaSession.cs` — add `AdoptSessionId`; write the audit sidecar from the per-turn seam.
- `src/Coda.Sdk/HeadlessOptions.cs` — parse `--continue` / `--resume <id>`.
- `src/Coda.Tui/HeadlessRunner.cs` — resolve + `Resume` before running.
- `src/Coda.Tui/Repl/SessionState.cs` — add `SessionId`.
- `src/Coda.Tui/Agent/AgentRunner.cs` — pass/sync the session id.
- `src/Coda.Tui/Program.cs` — dispatch `continue`/`resume`/`export`/`import` + `-c`/`-r` flags.
- `src/Coda.Tui/Commands/ResumeCommand.cs` — adopt id + numbered pick.
- `src/Coda.Tui/Commands/ExportCommand.cs` — add `--json` (bundle) alongside markdown default.
- `src/Coda.Tui/Repl/SlashCommandCatalog.cs` — register `ImportCommand`.
- `src/Coda.Tui/ImmediateCli.cs` + README — usage/help text.

---

## Task 1: `SessionAuditStore` — append-only per-turn audit sidecar

**Files:**
- Create: `src/Coda.Sdk/SessionAuditTurn.cs`
- Create: `src/Coda.Sdk/SessionAuditStore.cs`
- Test: `tests/Engine.Tests/SessionAuditStoreTests.cs`

**Interfaces:**
- Consumes: `LlmClient.TokenUsage` (`sealed record TokenUsage(int InputTokens, int OutputTokens)`),
  `LlmClient.ToolDefinition` (`sealed record ToolDefinition(string Name, string Description, string InputSchemaJson)`),
  `Coda.Sdk.ToolCallRecord` (`sealed record ToolCallRecord(string Name, string Input, string? Result, bool IsError)`).
- Produces (later tasks rely on these exact signatures):
  ```csharp
  public sealed record SessionAuditTurn
  {
      public required int TurnIndex { get; init; }
      public required DateTime TsUtc { get; init; }
      public required string Provider { get; init; }
      public required string Model { get; init; }
      public required int InputTokens { get; init; }
      public required int OutputTokens { get; init; }
      public string? StopReason { get; init; }
      public IReadOnlyList<ToolCallRecord> ToolCalls { get; init; } = [];
      // On disk, SystemPrompt/ToolDefs are written only when changed. On Load they are
      // always populated (carried forward from the most recent turn that emitted them).
      public string? SystemPrompt { get; init; }
      public IReadOnlyList<ToolDefinition> ToolDefs { get; init; } = [];
  }

  public sealed class SessionAuditStore(string workingDirectory)
  {
      public bool Exists(string sessionId);
      public Task AppendTurnAsync(string sessionId, SessionAuditTurn turn, CancellationToken ct = default);
      public Task<IReadOnlyList<SessionAuditTurn>> LoadAsync(string sessionId, CancellationToken ct = default);
  }
  ```

**Behavior contract:**
- File: `<workingDirectory>/.coda/sessions/<sessionId>.audit.jsonl`, one JSON object per line.
- **Change-only emission:** `SystemPrompt` and `ToolDefs` are written on a line **only when they
  differ (ordinal string compare on prompt; serialized-JSON compare on tool defs) from the last
  line that emitted them.** The first appended turn always emits both. The store keeps the last
  emitted values in memory; on the first append in a **fresh process** where the file already
  exists (the resume case), it recovers them by scanning the existing file for the most recent
  non-null values.
- **`LoadAsync`** reconstructs the effective `SystemPrompt`/`ToolDefs` for every turn by carrying
  forward the most recent emitted value; tolerates a torn final line (skip-if-unparseable); returns
  `[]` for a missing/absent file; never throws on a corrupt file.
- Invalid `sessionId` (per the transcript-store rule) → `AppendTurnAsync` is a no-op,
  `LoadAsync` returns `[]`, `Exists` returns false.
- JSON is written with `System.Text.Json` `JsonObject` (match `SessionTranscriptStore`), compact,
  one line + `\n`. Serialize `ToolDefs` as an array of `{name,description,inputSchema}`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Engine.Tests/SessionAuditStoreTests.cs`:

```csharp
using Coda.Sdk;
using LlmClient;

namespace Engine.Tests;

public sealed class SessionAuditStoreTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_audit_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    private static SessionAuditTurn Turn(int index, string system, string[] toolNames, string stop = "end_turn") => new()
    {
        TurnIndex = index,
        TsUtc = new DateTime(2026, 7, 13, 9, 0, index, DateTimeKind.Utc),
        Provider = "github-copilot",
        Model = "claude-opus-4.8",
        InputTokens = 100 + index,
        OutputTokens = 10 + index,
        StopReason = stop,
        ToolCalls = [new ToolCallRecord("read_file", "{\"path\":\"a\"}", "ok", false)],
        SystemPrompt = system,
        ToolDefs = [.. toolNames.Select(n => new ToolDefinition(n, $"{n} desc", "{}"))],
    };

    [Fact]
    public async Task AppendThenLoad_round_trips_a_single_turn()
    {
        var store = new SessionAuditStore(this.tempDir);
        await store.AppendTurnAsync("s1", Turn(0, "SYS-A", ["read_file"]));

        var loaded = await store.LoadAsync("s1");

        var t = Assert.Single(loaded);
        Assert.Equal(0, t.TurnIndex);
        Assert.Equal("github-copilot", t.Provider);
        Assert.Equal("claude-opus-4.8", t.Model);
        Assert.Equal(101, t.InputTokens);
        Assert.Equal("end_turn", t.StopReason);
        Assert.Equal("SYS-A", t.SystemPrompt);
        Assert.Equal("read_file", Assert.Single(t.ToolDefs).Name);
        Assert.Equal("read_file", Assert.Single(t.ToolCalls).Name);
    }

    [Fact]
    public async Task SystemPrompt_and_ToolDefs_emitted_only_on_change_but_carried_forward_on_load()
    {
        var store = new SessionAuditStore(this.tempDir);
        await store.AppendTurnAsync("s1", Turn(0, "SYS-A", ["read_file"]));
        await store.AppendTurnAsync("s1", Turn(1, "SYS-A", ["read_file"]));      // unchanged
        await store.AppendTurnAsync("s1", Turn(2, "SYS-B", ["read_file", "grep"])); // changed

        // Raw file: the second line must NOT contain the (repeated) system prompt.
        var path = Path.Combine(this.tempDir, ".coda", "sessions", "s1.audit.jsonl");
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(3, lines.Length);
        Assert.Contains("SYS-A", lines[0]);
        Assert.DoesNotContain("SYS-A", lines[1]);   // change-only: omitted when unchanged
        Assert.DoesNotContain("SYS-B", lines[1]);
        Assert.Contains("SYS-B", lines[2]);

        // Load: every turn has the effective value carried forward.
        var loaded = await store.LoadAsync("s1");
        Assert.Equal("SYS-A", loaded[0].SystemPrompt);
        Assert.Equal("SYS-A", loaded[1].SystemPrompt);   // carried forward
        Assert.Equal("SYS-B", loaded[2].SystemPrompt);
        Assert.Single(loaded[1].ToolDefs);               // carried forward (1 tool)
        Assert.Equal(2, loaded[2].ToolDefs.Count);       // changed to 2 tools
    }

    [Fact]
    public async Task Append_in_fresh_process_recovers_last_emitted_and_still_omits_unchanged()
    {
        // First "process".
        var first = new SessionAuditStore(this.tempDir);
        await first.AppendTurnAsync("s1", Turn(0, "SYS-A", ["read_file"]));

        // Second "process" (fresh store instance) appends an UNCHANGED prompt/tools turn.
        var second = new SessionAuditStore(this.tempDir);
        await second.AppendTurnAsync("s1", Turn(1, "SYS-A", ["read_file"]));

        var path = Path.Combine(this.tempDir, ".coda", "sessions", "s1.audit.jsonl");
        var lines = await File.ReadAllLinesAsync(path);
        Assert.DoesNotContain("SYS-A", lines[1]); // recovered baseline → still omitted
    }

    [Fact]
    public async Task LoadAsync_tolerates_a_torn_final_line()
    {
        var store = new SessionAuditStore(this.tempDir);
        await store.AppendTurnAsync("s1", Turn(0, "SYS-A", ["read_file"]));
        var path = Path.Combine(this.tempDir, ".coda", "sessions", "s1.audit.jsonl");
        await File.AppendAllTextAsync(path, "{ this is a torn half-written line");

        var loaded = await store.LoadAsync("s1"); // must not throw
        Assert.Single(loaded);                    // the torn line is skipped
    }

    [Fact]
    public async Task LoadAsync_returns_empty_for_missing_file()
    {
        var store = new SessionAuditStore(this.tempDir);
        Assert.Empty(await store.LoadAsync("nope"));
        Assert.False(store.Exists("nope"));
    }

    [Fact]
    public async Task AppendAsync_is_noop_for_invalid_id()
    {
        var store = new SessionAuditStore(this.tempDir);
        await store.AppendTurnAsync("../escape", Turn(0, "SYS-A", ["read_file"]));
        Assert.False(Directory.Exists(Path.Combine(this.tempDir, ".coda", "sessions")));
    }
}
```

- [ ] **Step 2: Run the tests — verify they fail (types don't exist yet)**

Run: `dotnet test tests/Engine.Tests --filter "FullyQualifiedName~SessionAuditStoreTests"`
Expected: compile error / FAIL — `SessionAuditStore` / `SessionAuditTurn` not defined.

- [ ] **Step 3: Implement `SessionAuditTurn`**

Create `src/Coda.Sdk/SessionAuditTurn.cs` with the record shown in **Interfaces → Produces** above
(file-scoped `namespace Coda.Sdk;`, `using LlmClient;`).

- [ ] **Step 4: Implement `SessionAuditStore`**

Create `src/Coda.Sdk/SessionAuditStore.cs`. Key points:
- Mirror `SessionTranscriptStore` for `SessionsDir`, `FilePath` (but `.audit.jsonl`), and `IsValidId`.
- Fields: `private string? lastSystemPrompt; private string? lastToolDefsJson; private bool baselineLoaded;`
  keyed per store instance (single-session-per-process in practice; if multiple ids are used, key by
  id in a dictionary — keep it simple with a `ConcurrentDictionary<string,(string? sys,string? tools)>`).
- `AppendTurnAsync`:
  1. `if (!IsValidId(sessionId)) return;`
  2. `Directory.CreateDirectory(SessionsDir);`
  3. Ensure baseline: if not yet loaded for this id and the file exists, scan it for the most recent
     non-null `system`/`toolDefs` and seed `last*`.
  4. Serialize the turn to a `JsonObject`: always `turnIndex, tsUtc (O format), provider, model,
     usage:{in,out}, stopReason, toolCalls:[{name,input,result,isError}]`. Add `systemPrompt` only
     if `turn.SystemPrompt != last.sys`; add `toolDefs` only if `SerializeToolDefs(turn.ToolDefs) !=
     last.tools`. Update `last.*` when emitted.
  5. Append `obj.ToJsonString(compact) + "\n"` with `File.AppendAllTextAsync`.
- `LoadAsync`: read all lines; for each parseable line build a `SessionAuditTurn`, carrying forward
  the last seen `systemPrompt`/`toolDefs` when the line omitted them; skip unparseable lines.
- `SerializeToolDefs`: a `JsonArray` of `{name,description,inputSchema}`; use its `ToJsonString()`
  for the change-compare and for writing.
- Do **not** add logging that throws; this type is only ever called from a swallowed seam, but keep
  it internally exception-safe (a bad line is skipped, not thrown).

- [ ] **Step 5: Run the tests — verify they pass**

Run: `dotnet test tests/Engine.Tests --filter "FullyQualifiedName~SessionAuditStoreTests"`
Expected: PASS (6/6).

- [ ] **Step 6: Commit**

```bash
git add src/Coda.Sdk/SessionAuditTurn.cs src/Coda.Sdk/SessionAuditStore.cs tests/Engine.Tests/SessionAuditStoreTests.cs
git commit -m "feat(sdk): append-only per-turn session audit sidecar (SessionAuditStore)"
```

---

## Task 2: `SessionBundle` + `SessionBundleService` — export/import

**Files:**
- Create: `src/Coda.Sdk/SessionBundle.cs`
- Create: `src/Coda.Sdk/SessionBundleService.cs`
- Test: `tests/Engine.Tests/SessionBundleServiceTests.cs`

**Interfaces:**
- Consumes: `SessionTranscriptStore` (`SaveAsync`, `LoadAsync`), `SessionAuditStore` (Task 1),
  `ChatMessage`/`ContentBlock` subtypes, `ToolDefinition`.
- Produces:
  ```csharp
  public sealed record SessionBundleTurn
  {
      public required string Role { get; init; }                 // "user" | "assistant"
      public DateTime? TsUtc { get; init; }
      public int? InputTokens { get; init; }
      public int? OutputTokens { get; init; }
      public string? StopReason { get; init; }
      public required IReadOnlyList<ContentBlock> Blocks { get; init; }
  }

  public sealed record SessionBundle
  {
      public string Schema { get; init; } = "coda.session/1";
      public required string CodaVersion { get; init; }
      public required DateTime ExportedUtc { get; init; }
      public required string Id { get; init; }
      public DateTime CreatedUtc { get; init; }
      public string? Provider { get; init; }
      public string? Model { get; init; }
      public bool AuditAvailable { get; init; }
      public string? SystemPrompt { get; init; }
      public IReadOnlyList<ToolDefinition> ToolDefs { get; init; } = [];
      public required IReadOnlyList<SessionBundleTurn> Turns { get; init; }
  }

  public sealed class SessionBundleService(string workingDirectory, string codaVersion)
  {
      public Task<SessionBundle?> ExportAsync(string sessionId, DateTime exportedUtc, CancellationToken ct = default);
      public Task<string> WriteAsync(SessionBundle bundle, string outPath, bool pretty, CancellationToken ct = default);
      public Task<string> ImportAsync(string bundlePath, CancellationToken ct = default); // returns the (possibly new) local id
  }
  ```

**Behavior contract:**
- **Export** (`ExportAsync`): returns `null` if the session's transcript is absent. Otherwise builds
  a bundle: `Turns` from the lean transcript (role + blocks); when an audit sidecar exists,
  `AuditAvailable=true` and each assistant turn is enriched with usage/stopReason/ts, and top-level
  `SystemPrompt`/`ToolDefs`/`Provider`/`Model` come from the **last** audit turn (the effective final
  values). No sidecar → `AuditAvailable=false`, block-only turns, null system prompt/tooldefs.
  `CreatedUtc` comes from the transcript file's stored `createdUtc` (read via the store's list or
  file); if unavailable use `exportedUtc`.
- **Write** (`WriteAsync`): serialize to `outPath` (compact unless `pretty`); return the path.
- **Import** (`ImportAsync`): parse+validate (`Schema` must start with `"coda.session/"` and major
  `1`; else throw `InvalidOperationException` with a clear message). Target id = bundle `Id`; if a
  transcript already exists locally for that id, mint a new `Guid…[..12]`. Write the lean transcript
  via `SessionTranscriptStore.SaveAsync(targetId, messages)` and, when `AuditAvailable`, reconstruct
  `<targetId>.audit.jsonl` by replaying the turns through a `SessionAuditStore`. Return `targetId`.
- Alignment of audit turns to assistant messages: the transcript alternates user/assistant; map the
  k-th assistant message to audit turn k (both are per-user-turn). If counts disagree (older/edited
  session), enrich what aligns and leave the rest block-only — never throw.

- [ ] **Step 1: Write the failing tests**

Create `tests/Engine.Tests/SessionBundleServiceTests.cs`:

```csharp
using Coda.Sdk;
using LlmClient;

namespace Engine.Tests;

public sealed class SessionBundleServiceTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_bundle_").FullName;
    private static readonly DateTime FixedExport = new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    private async Task SeedSessionAsync(string id)
    {
        var transcript = new SessionTranscriptStore(this.tempDir);
        await transcript.SaveAsync(id,
        [
            new(ChatRole.User, [new TextBlock("hello")]),
            new(ChatRole.Assistant, [new TextBlock("hi there")]),
        ]);
        var audit = new SessionAuditStore(this.tempDir);
        await audit.AppendTurnAsync(id, new SessionAuditTurn
        {
            TurnIndex = 0,
            TsUtc = new DateTime(2026, 7, 13, 9, 0, 0, DateTimeKind.Utc),
            Provider = "github-copilot",
            Model = "claude-opus-4.8",
            InputTokens = 200,
            OutputTokens = 20,
            StopReason = "end_turn",
            SystemPrompt = "SYSTEM-PROMPT-TEXT",
            ToolDefs = [new ToolDefinition("read_file", "reads", "{}")],
        });
    }

    [Fact]
    public async Task ExportAsync_includes_system_prompt_usage_and_turns()
    {
        await this.SeedSessionAsync("s1");
        var svc = new SessionBundleService(this.tempDir, "0.1.63");

        var bundle = await svc.ExportAsync("s1", FixedExport);

        Assert.NotNull(bundle);
        Assert.Equal("coda.session/1", bundle.Schema);
        Assert.True(bundle.AuditAvailable);
        Assert.Equal("SYSTEM-PROMPT-TEXT", bundle.SystemPrompt);
        Assert.Equal("github-copilot", bundle.Provider);
        Assert.Equal(2, bundle.Turns.Count);
        var assistant = bundle.Turns[1];
        Assert.Equal("assistant", assistant.Role);
        Assert.Equal(200, assistant.InputTokens);
        Assert.Equal("end_turn", assistant.StopReason);
    }

    [Fact]
    public async Task ExportAsync_returns_null_for_missing_session()
    {
        var svc = new SessionBundleService(this.tempDir, "0.1.63");
        Assert.Null(await svc.ExportAsync("nope", FixedExport));
    }

    [Fact]
    public async Task ExportAsync_without_sidecar_sets_auditAvailable_false()
    {
        var transcript = new SessionTranscriptStore(this.tempDir);
        await transcript.SaveAsync("s2", [new(ChatRole.User, [new TextBlock("q")])]);
        var svc = new SessionBundleService(this.tempDir, "0.1.63");

        var bundle = await svc.ExportAsync("s2", FixedExport);

        Assert.NotNull(bundle);
        Assert.False(bundle.AuditAvailable);
        Assert.Null(bundle.SystemPrompt);
    }

    [Fact]
    public async Task Export_Write_Import_round_trips_and_preserves_history()
    {
        await this.SeedSessionAsync("s1");
        var svc = new SessionBundleService(this.tempDir, "0.1.63");
        var bundle = await svc.ExportAsync("s1", FixedExport);
        var outPath = Path.Combine(this.tempDir, "s1.coda-session.json");
        await svc.WriteAsync(bundle!, outPath, pretty: false);

        // Import into a DIFFERENT working dir (fresh machine simulation).
        var otherDir = Directory.CreateTempSubdirectory("coda_bundle_dst_").FullName;
        try
        {
            var svc2 = new SessionBundleService(otherDir, "0.1.63");
            var importedId = await svc2.ImportAsync(outPath);

            Assert.Equal("s1", importedId); // no collision in the fresh dir → id preserved
            var transcript = new SessionTranscriptStore(otherDir);
            var loaded = await transcript.LoadAsync("s1");
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.Count);
            var audit = await new SessionAuditStore(otherDir).LoadAsync("s1");
            Assert.Equal("SYSTEM-PROMPT-TEXT", Assert.Single(audit).SystemPrompt);
        }
        finally
        {
            try { Directory.Delete(otherDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task ImportAsync_mints_new_id_on_collision()
    {
        await this.SeedSessionAsync("s1");
        var svc = new SessionBundleService(this.tempDir, "0.1.63");
        var bundle = await svc.ExportAsync("s1", FixedExport);
        var outPath = Path.Combine(this.tempDir, "s1.coda-session.json");
        await svc.WriteAsync(bundle!, outPath, pretty: false);

        // Import back into the SAME dir → id "s1" already exists → new id.
        var importedId = await svc.ImportAsync(outPath);

        Assert.NotEqual("s1", importedId);
        Assert.NotNull(await new SessionTranscriptStore(this.tempDir).LoadAsync(importedId));
    }

    [Fact]
    public async Task ImportAsync_rejects_unknown_schema_major()
    {
        var bad = Path.Combine(this.tempDir, "bad.json");
        await File.WriteAllTextAsync(bad, """{"schema":"coda.session/9","id":"x","turns":[]}""");
        var svc = new SessionBundleService(this.tempDir, "0.1.63");

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ImportAsync(bad));
    }
}
```

- [ ] **Step 2: Run — verify FAIL** (`--filter "FullyQualifiedName~SessionBundleServiceTests"`).
- [ ] **Step 3: Implement `SessionBundle.cs`** (the two records above).
- [ ] **Step 4: Implement `SessionBundleService.cs`** per the behavior contract. Serialize/parse with
  `JsonNode`/`JsonObject`, reusing block (de)serialization identical to `SessionTranscriptStore`
  (text/tool_use/tool_result) — extract a shared helper if convenient, else replicate the shapes so
  the two stay wire-compatible. Read the transcript's `createdUtc` from
  `SessionTranscriptStore.ListAsync` (find the matching summary) or from the file directly.
- [ ] **Step 5: Run — verify PASS** (6/6).
- [ ] **Step 6: Commit**

```bash
git add src/Coda.Sdk/SessionBundle.cs src/Coda.Sdk/SessionBundleService.cs tests/Engine.Tests/SessionBundleServiceTests.cs
git commit -m "feat(sdk): full-audit session bundle export/import (SessionBundleService)"
```

---

## Task 3: `CodaSession.AdoptSessionId` (id adoption for the shared-history path)

**Files:**
- Modify: `src/Coda.Sdk/CodaSession.cs`
- Test: `tests/Engine.Tests/CodaSessionAdoptIdTests.cs`

**Interfaces:**
- Produces: `public void AdoptSessionId(string sessionId)` on `CodaSession` — sets `SessionId` so
  subsequent `PersistTranscriptAsync` targets the new file, **without** touching history (unlike
  `Resume`, which swaps history; the TUI shares its history list by reference, so it must not be
  cleared). Throws `ArgumentException` on null/empty.

**Context:** `CodaSession.SessionId` is `{ get; private set; }` (`CodaSession.cs:237`).
`PersistTranscriptAsync` saves `SaveAsync(this.SessionId, this.history)` (`:643`). The TUI's
`AgentRunner` shares `SessionState.History` with the session (`AgentRunner.cs:36`), so id-only
adoption is required there.

- [ ] **Step 1: Write the failing test**

Create `tests/Engine.Tests/CodaSessionAdoptIdTests.cs`. Use the existing DI seams
(`ILlmClientFactory` / `IAgentLoopFactory`) the SDK tests already use for offline runs — model this
on `tests/Engine.Tests/Sdk/CodaSessionRunAsyncTests.cs` (read it for the fake-loop/fake-client
setup). The test: construct a session with a fake loop that appends one assistant message, run once,
call `AdoptSessionId("newid")`, run again, and assert `<tempDir>/.coda/sessions/newid.json` exists
and the pre-adopt id file was not written on the second turn.

```csharp
[Fact]
public void AdoptSessionId_rejects_empty()
{
    var session = /* build via the fake-factory helper */;
    Assert.Throws<ArgumentException>(() => session.AdoptSessionId(""));
}

[Fact]
public async Task AdoptSessionId_redirects_persistence_to_the_new_id_file()
{
    // build session with fake loop + fake client over `tempDir`
    session.AdoptSessionId("adopted99");
    await session.RunAsync("hi");
    Assert.True(File.Exists(Path.Combine(tempDir, ".coda", "sessions", "adopted99.json")));
}
```

- [ ] **Step 2: Run — verify FAIL** (method missing).
- [ ] **Step 3: Implement** `AdoptSessionId`:

```csharp
/// <summary>
/// Adopt an existing session id so subsequent transcript/audit saves target its files,
/// WITHOUT replacing history. Used by the TUI, whose history list is shared by reference
/// (so <see cref="Resume"/>, which swaps history, is not appropriate there).
/// </summary>
public void AdoptSessionId(string sessionId)
{
    ArgumentException.ThrowIfNullOrEmpty(sessionId);
    this.SessionId = sessionId;
}
```

- [ ] **Step 4: Run — verify PASS.**
- [ ] **Step 5: Commit** — `feat(sdk): CodaSession.AdoptSessionId for shared-history id adoption`.

---

## Task 4: Write the audit sidecar from `CodaSession`'s per-turn seam

**Files:**
- Modify: `src/Coda.Sdk/CodaSession.cs`
- Test: `tests/Engine.Tests/CodaSessionAuditIntegrationTests.cs`

**Interfaces:**
- Consumes: `SessionAuditStore` (Task 1), `RecordingSink` (already gives `Usage`, `ToolCalls`,
  `StopReason`), `AgentSystemPrompt.Build`, `ToolRegistry(...).Definitions`.
- Produces: after each **successful** `RunAsync`, one `<id>.audit.jsonl` line is appended.

**Context / seam:** in `RunAsync` (`CodaSession.cs:354-363`), on success the code calls
`PersistTranscriptAsync` then returns a `RunResult`. Add the audit write immediately after the
transcript persist, inside the same `try`, wrapped so it can never throw out of the turn. Build the
system prompt + tool defs exactly as `AnalyzeContextAsync` does (`CodaSession.cs:429-440`):

```csharp
var includeAnthropicSystemPrefix = options.ProviderId != GitHubCopilotProvider.Id;
var systemPrompt = AgentSystemPrompt.Build(
    options.WorkingDirectory, includeAnthropicSystemPrefix,
    ProjectContext.Load(options.WorkingDirectory),
    BuiltInOutputStyles.Resolve(options.OutputStyle).SystemPromptSuffix);
var toolDefs = new ToolRegistry([.. BuiltInTools.All(), .. options.ExtraTools]).Definitions;
```

Track a per-session turn counter (`private int auditTurnIndex;`) and a lazily-created
`SessionAuditStore` (like `transcriptStore ??= …` at `:640`). Wrap in try/catch + a
source-generated `[LoggerMessage]` warning (`LogAuditPersistFailed`), mirroring
`PersistTranscriptAsync`.

- [ ] **Step 1: Write the failing integration test**

`tests/Engine.Tests/CodaSessionAuditIntegrationTests.cs` — using the same fake-factory helper as
Task 3, run one turn and assert both files exist and the audit turn carries the model + a system
prompt:

```csharp
[Fact]
public async Task RunAsync_writes_transcript_and_audit_sidecar()
{
    // session over tempDir, sessionId "abc123", fake loop appends an assistant reply + usage
    await session.RunAsync("hello");

    Assert.True(File.Exists(Path.Combine(tempDir, ".coda", "sessions", "abc123.json")));
    var audit = await new SessionAuditStore(tempDir).LoadAsync("abc123");
    var t = Assert.Single(audit);
    Assert.Equal(session.Options.Model, t.Model);
    Assert.False(string.IsNullOrEmpty(t.SystemPrompt));
}
```

> The fake `IAgentLoopFactory` must surface usage into the `RecordingSink` (call
> `sink.OnUsage(...)`) and append an assistant message so the turn is "successful". Read
> `tests/Engine.Tests/Sdk/CodaSessionRunAsyncTests.cs` for the exact fake shape and reuse it.

- [ ] **Step 2: Run — verify FAIL** (no sidecar written yet).
- [ ] **Step 3: Implement** the audit write in `RunAsync` (success branch only) + the counter +
  `LogAuditPersistFailed`. Do **not** write audit on the cancel/exception branches (no completed turn).
- [ ] **Step 4: Run — verify PASS**, and run the whole Engine.Tests project to confirm no regression:
  `dotnet test tests/Engine.Tests`.
- [ ] **Step 5: Commit** — `feat(sdk): record per-turn audit sidecar from the CodaSession turn seam`.

---

## Task 5: `SessionCli` resolver + headless resume (`HeadlessOptions` + `HeadlessRunner`)

**Files:**
- Create: `src/Coda.Tui/SessionCli.cs` (shared resolver — first consumer is headless; the TUI reuses it in Task 6)
- Modify: `src/Coda.Sdk/HeadlessOptions.cs`
- Modify: `src/Coda.Tui/HeadlessRunner.cs`
- Test: `tests/Coda.Tui.Tests/SessionCliTests.cs`
- Test: `tests/Engine.Tests/HeadlessOptionsResumeTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class SessionCli
  {
      // Resolved id + its loaded history, or null (caller prints the reason).
      public sealed record ResumeTarget(string Id, IReadOnlyList<ChatMessage> Messages);
      public static async Task<ResumeTarget?> ResolveAsync(
          string workingDirectory, bool continueLatest, string? resumeId, CancellationToken ct = default);
  }
  ```
  `continueLatest` → newest transcript in the dir (`SessionTranscriptStore.ListAsync().FirstOrDefault()`);
  `resumeId` → that id via `LoadAsync`; both null, or a missing/absent target → null.
- Produces on `HeadlessOptions`: `public bool Continue { get; init; }` and
  `public string? ResumeSessionId { get; init; }`.

**Behavior:**
- `--continue` sets `Continue=true`; `--resume <id>` sets `ResumeSessionId=<id>` (requires a value).
  `--continue` and `--resume` are mutually exclusive → parse error if both given. `--resume` with no
  following value → error (`"Missing value for --resume."`).
- In `HeadlessRunner.RunAsync`, after resolving `providerId`/`model` and before constructing the
  session: if `Continue` → resolve the newest id in `workingDirectory` via
  `new SessionTranscriptStore(workingDirectory).ListAsync()` (`.FirstOrDefault()?.Id`); if
  `ResumeSessionId` is set → use it. If a target id resolved, `LoadAsync` it; when messages are
  found, construct `new CodaSession(credentials, sessionOptions, history: [.. messages], sessionId: targetId)`
  (the ctor already accepts both — `CodaSession.cs:64-80`). If `--continue` finds no sessions, or
  `--resume <id>` finds no transcript, write a clear stderr message and return exit code 1
  **before** spending a model call.
- Update the usage string to include `[--continue] [--resume <id>]`.

- [ ] **Step 1: Write failing `SessionCliTests`**

`tests/Coda.Tui.Tests/SessionCliTests.cs`:

```csharp
using Coda.Sdk;
using Coda.Tui;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class SessionCliTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_sescli_").FullName;
    public void Dispose() { try { Directory.Delete(this.tempDir, true); } catch { /* ignore */ } }

    private async Task Seed(string id, string text)
    {
        await new SessionTranscriptStore(this.tempDir)
            .SaveAsync(id, [new(ChatRole.User, [new TextBlock(text)])]);
    }

    [Fact]
    public async Task Continue_resolves_the_newest_session()
    {
        await this.Seed("older", "a");
        await Task.Delay(50);
        await this.Seed("newer", "b");

        var target = await SessionCli.ResolveAsync(this.tempDir, continueLatest: true, resumeId: null);

        Assert.NotNull(target);
        Assert.Equal("newer", target.Id);
        Assert.Single(target.Messages);
    }

    [Fact]
    public async Task Resume_by_id_loads_that_session()
    {
        await this.Seed("pick-me", "x");
        var target = await SessionCli.ResolveAsync(this.tempDir, continueLatest: false, resumeId: "pick-me");
        Assert.NotNull(target);
        Assert.Equal("pick-me", target.Id);
    }

    [Fact]
    public async Task Resume_missing_id_returns_null()
    {
        Assert.Null(await SessionCli.ResolveAsync(this.tempDir, false, "ghost"));
    }

    [Fact]
    public async Task Continue_with_no_sessions_returns_null()
    {
        Assert.Null(await SessionCli.ResolveAsync(this.tempDir, true, null));
    }
}
```

- [ ] **Step 2: Run — verify FAIL** (`--filter "FullyQualifiedName~SessionCliTests"`; type missing).
- [ ] **Step 3: Implement `SessionCli.ResolveAsync`** over `SessionTranscriptStore` (`ListAsync` for
  newest, `LoadAsync` for the id; return null on miss).
- [ ] **Step 4: Run `SessionCliTests` — verify PASS.**
- [ ] **Step 5: Write failing `HeadlessOptions` tests**

`tests/Engine.Tests/HeadlessOptionsResumeTests.cs`:

```csharp
using Coda.Sdk;

namespace Engine.Tests;

public sealed class HeadlessOptionsResumeTests
{
    [Fact]
    public void Parses_continue_flag()
    {
        Assert.True(HeadlessOptions.TryParse(["-p", "go", "--continue"], out var o, out _));
        Assert.True(o.Continue);
        Assert.Null(o.ResumeSessionId);
    }

    [Fact]
    public void Parses_resume_with_id()
    {
        Assert.True(HeadlessOptions.TryParse(["-p", "go", "--resume", "abc123"], out var o, out _));
        Assert.Equal("abc123", o.ResumeSessionId);
        Assert.False(o.Continue);
    }

    [Fact]
    public void Resume_without_value_is_an_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "go", "--resume"], out _, out var err));
        Assert.Contains("--resume", err);
    }

    [Fact]
    public void Continue_and_resume_together_is_an_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "go", "--continue", "--resume", "x"], out _, out var err));
        Assert.NotNull(err);
    }
}
```

- [ ] **Step 6: Run — verify FAIL.**
- [ ] **Step 7: Implement** the two `case` arms in `HeadlessOptions.TryParse` + the mutual-exclusion
  check + the new init properties + updated usage string.
- [ ] **Step 8: Implement the `HeadlessRunner` resume wiring.** After resolving provider/model and
  before `new CodaSession(...)`: if `options.Continue || options.ResumeSessionId is not null`, call
  `SessionCli.ResolveAsync(workingDirectory, options.Continue, options.ResumeSessionId)`. On a hit,
  construct `new CodaSession(credentials, sessionOptions, history: [.. target.Messages], sessionId: target.Id)`.
  On a miss, write a clear stderr line (`"No session to continue."` / `"Session '<id>' not found."`)
  and `return 1` **before** any model call. (No dedicated automated test for the live `RunAsync` path;
  it is covered by `SessionCliTests` for resolution + the Step-5 parse tests + the final manual smoke.)
- [ ] **Step 9: Run — verify PASS**; `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~SessionCliTests"`
  and `dotnet test tests/Engine.Tests --filter "FullyQualifiedName~HeadlessOptionsResumeTests"`.
- [ ] **Step 10: Commit** — `feat(run): SessionCli resolver + coda run --continue/--resume`.

---

## Task 6: TUI startup continue/resume — `SessionState.SessionId` + `AgentRunner` id-sync + `Program.cs`

**Files:**
- Modify: `src/Coda.Tui/Repl/SessionState.cs`
- Modify: `src/Coda.Tui/Agent/AgentRunner.cs`
- Modify: `src/Coda.Tui/Program.cs`
- Test: `tests/Coda.Tui.Tests/AgentRunnerSessionIdTests.cs`

**Interfaces:**
- Consumes: `SessionCli.ResolveAsync` / `SessionCli.ResumeTarget` (created in Task 5),
  `CodaSession.AdoptSessionId` (Task 3).
- Produces on `SessionState`: `public string? SessionId { get; set; }`.

**AgentRunner wiring:** at the top of `RunAsync`, after ensuring `this.session` exists:
- On first creation, pass `sessionId: context.Session.SessionId` to the ctor; if
  `context.Session.SessionId` is null, capture the generated one back:
  `context.Session.SessionId = this.session.SessionId;`.
- On every call, if `this.session is not null && context.Session.SessionId is { } id &&
  this.session.SessionId != id`, call `this.session.AdoptSessionId(id);` — so a mid-REPL `/resume`
  (Task 8b) takes effect on the next turn.

**Program.cs:** add `continue` / `resume` to the `args[0]` dispatch and `-c`/`--continue`/`-r`/
`--resume [id]` handling before the TUI composition. When a resume is requested, call
`SessionCli.ResolveAsync`; on success set `session.SessionId = target.Id` and
`session.History.AddRange(target.Messages)` **before** `TuiApp` runs; on failure print a message and
continue with a fresh session (do not abort the TUI). Keep `export`/`import` (Task 8) in the same
dispatch block.

- [ ] **Step 1: Add `SessionState.SessionId`** — `public string? SessionId { get; set; }` (default null,
  meaning "not yet resolved; AgentRunner will capture the generated id on first turn").
- [ ] **Step 2: Wire `AgentRunner`** — on first session creation pass `sessionId: context.Session.SessionId`
  to the `CodaSession` ctor; immediately after, if `context.Session.SessionId is null` capture it back
  (`context.Session.SessionId = this.session.SessionId;`). On every `RunAsync`, before running, if
  `this.session is not null && context.Session.SessionId is { } id && this.session.SessionId != id`,
  call `this.session.AdoptSessionId(id);` (so a mid-REPL `/resume` — Task 7 — takes effect next turn).
- [ ] **Step 3: Write a failing `AgentRunnerSessionIdTests`** asserting that when
  `SessionState.SessionId` is preset before the first turn, persistence lands under that id. Build the
  runner the way existing `tests/Coda.Tui.Tests` build a `CommandContext`/`SessionState`; drive one turn
  through a fake session seam (mirror any existing AgentRunner test; if none exists, assert the smaller
  invariant — after a run the file `<cwd>/.coda/sessions/<presetId>.json` exists — using an offline
  provider path). Keep it focused; no live LLM.
- [ ] **Step 4: Run — verify FAIL, then PASS after Step 2 is in.** (Write the test first if not already;
  order Steps 2/3 red-green as the TDD flow requires.)
- [ ] **Step 5: Wire `Program.cs`** — add `continue`/`resume` to the `args[0]` dispatch and
  `-c`/`--continue`/`-r`/`--resume [id]` flag handling before the TUI composition root. Call
  `SessionCli.ResolveAsync(session.WorkingDirectory, continueLatest, resumeId)`; on a hit set
  `session.SessionId = target.Id` and `session.History.AddRange(target.Messages)` **before** `TuiApp`
  runs, and print `Resumed <id> (<n> messages).`; on a miss print a dim "No session to continue." and
  proceed with a fresh session (do not abort the TUI).
- [ ] **Step 6: Build + run TUI tests** — `dotnet test tests/Coda.Tui.Tests`.
- [ ] **Step 7: Commit** — `feat(tui): coda continue/resume startup + SessionState.SessionId + AgentRunner id-sync`.

---

## Task 7: Fix REPL `/resume` — adopt id + numbered pick

**Files:**
- Modify: `src/Coda.Tui/Commands/ResumeCommand.cs`
- Test: `tests/Coda.Tui.Tests/ResumeCommandTests.cs` (extend existing
  `ResumeRewindCommandTests.cs` if that is where resume is covered — read it first).

**Behavior:**
- `/resume <id>` continues to load history into `context.Session.History` (clear+add) **and now sets
  `context.Session.SessionId = <id>`** so the next turn appends to the original transcript (via the
  AgentRunner sync from Task 6). It also accepts a **1-based index** from the listed sessions
  (`/resume 2` → the 2nd newest) — resolve the index against `ListAsync` before treating the arg as a
  literal id.
- `/resume` with no args is unchanged (lists sessions).

- [ ] **Step 1: Write failing tests** — `/resume <id>` sets `context.Session.SessionId`; `/resume 1`
  resolves to the newest session's id. Use a seeded `SessionTranscriptStore` in a temp
  `WorkingDirectory` and a `CommandContext` built the way the existing command tests build it (read
  `tests/Coda.Tui.Tests/ResumeRewindCommandTests.cs`).
- [ ] **Step 2: Run — verify FAIL.**
- [ ] **Step 3: Implement** id-adoption + index resolution in `ResumeCommand.ResumeSessionAsync`
  (and a small "is this arg an index into the list?" branch).
- [ ] **Step 4: Run — verify PASS.**
- [ ] **Step 5: Commit** — `fix(tui): /resume adopts the session id (true continue) + numbered pick`.

---

## Task 8: Export/import — shell subcommands, `/export --json`, `/import`

**Files:**
- Modify: `src/Coda.Tui/Commands/ExportCommand.cs` (add `--json` bundle mode)
- Create: `src/Coda.Tui/Commands/ImportCommand.cs`
- Modify: `src/Coda.Tui/Repl/SlashCommandCatalog.cs` (register `ImportCommand`)
- Modify: `src/Coda.Tui/Program.cs` (add `export` / `import` shell subcommands)
- Modify: `src/Coda.Tui/ImmediateCli.cs` + README (usage)
- Test: `tests/Coda.Tui.Tests/SessionExportImportCommandTests.cs`

**Behavior:**
- **REPL `/export`** keeps its current default (Markdown of the live conversation). Add `--json`
  (and/or `--format json|md`): `/export --json [<path>]` writes a **bundle** for the *current*
  session id (`context.Session.SessionId`, falling back to a freshly generated id if somehow unset)
  via `SessionBundleService.ExportAsync(id, DateTime.UtcNow)` + `WriteAsync`. Default path
  `./<id>.coda-session.json` (or the given path). `--pretty` → indented.
- **REPL `/import <file>`** — `SessionBundleService.ImportAsync(file)` into
  `context.Session.WorkingDirectory`; print `Imported as <id>. Use /resume <id> to continue.`
- **Shell `coda export <id> [--out <path>] [--pretty]`** — full-audit bundle for `<id>` in the cwd;
  credential-free (no session/LLM), prints the written path; exit 1 with a message if `<id>` not found.
- **Shell `coda import <file>`** — imports into the cwd; prints the id; exit 1 on invalid/missing file.
- Coda version for the bundle: the entry assembly's informational version (same value `coda --version`
  prints — reuse whatever `ImmediateCli`/`Branding` already uses).

- [ ] **Step 1: Write failing tests** — `tests/Coda.Tui.Tests/SessionExportImportCommandTests.cs`:
  seed a session (transcript + audit) in a temp dir, run `/export --json`, assert a
  `*.coda-session.json` is written and parses with `schema == "coda.session/1"`; run `/import` on it
  into another temp dir and assert the transcript is importable + `/resume`-able. Build `CommandContext`
  as the existing command tests do.
- [ ] **Step 2: Run — verify FAIL.**
- [ ] **Step 3: Implement** `ExportCommand` `--json` branch (delegating to `SessionBundleService`),
  `ImportCommand`, catalog registration, and the `Program.cs` `export`/`import` shell subcommands
  (immediate, credential-free, like `help`/`models`).
- [ ] **Step 4: Run — verify PASS**; then `dotnet test tests/Coda.Tui.Tests`.
- [ ] **Step 5: Update usage** in `ImmediateCli` + README (`continue`/`resume`/`export`/`import`,
  `run --continue/--resume`).
- [ ] **Step 6: Commit** — `feat(tui): session bundle export/import (shell + /export --json + /import)`.

---

## Final verification (after all tasks)

- [ ] `./build.ps1 -Test` (bumps build, Release build, full suite) is green.
- [ ] Manual smoke (documented, not automated): `coda run -p "say hi"` then
  `coda run --continue -p "what did you just say"` shows one grown transcript + a `.audit.jsonl`;
  `coda export <id> --out s.json` then `coda import s.json` in a fresh dir round-trips.
- [ ] Whole-branch code review (subagent-driven-development final step).

## Notes for the executor

- **Fake-factory test seam:** Tasks 3/4 need offline `CodaSession` runs. `CodaSession`'s ctor accepts
  `ILlmClientFactory` and `IAgentLoopFactory` (`CodaSession.cs:64-72`); reuse the fakes already used
  by `tests/Engine.Tests/Sdk/CodaSessionRunAsyncTests.cs` — read that file first and mirror its setup.
- **Do not** register a second `ExportCommand` (one already exists — extend it). There is no existing
  `ImportCommand`.
- **`--fork` is out of scope** (deferred per the spec). Do not add it.
