# Fork carries the source audit sidecar — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make `--fork` / `/fork` copy the source session's audit sidecar (and eagerly persist the transcript) into the new id, so a forked session is a complete, resumable, fully-auditable session — the pre-fork turns keep their system prompt + tool-call audit, not just their transcript.

**Architecture:** A new `SessionForking.ForkAsync` mints the fresh id, writes the seeded transcript, and copies `<source>.audit.jsonl` → `<new>.audit.jsonl`. The three fork surfaces (headless, TUI startup, REPL `/fork`) call it instead of leaving the id null / minting inline. `CodaSession` already reseeds the audit turn index from the sidecar line count on id change (`CodaSession.cs:679-683`), so appended turns continue monotonically after the copied ones.

**Tech Stack:** C# / .NET 10, xUnit (Engine.Tests + Coda.Tui.Tests).

## Global Constraints

- Repo C# style: `this.` on instance members, braces always, file-scoped namespaces, one type per file, `ConfigureAwait(false)` in library code.
- The source session is NEVER modified by a fork.
- Audit copy is best-effort (the audit sidecar is a swallowed seam — never throw out of it).
- Session id shape unchanged (`SessionIds.NewId()`). No on-disk format changes.
- Fork still forks the ENTIRE source session (no slicing) — copy the whole sidecar.

---

### Task 1: `SessionAuditStore.CopyAsync` + `SessionForking.ForkAsync`

**Files:**
- Modify: `src/Coda.Sdk/SessionAuditStore.cs`
- Create: `src/Coda.Sdk/SessionForking.cs`
- Test: `tests/Engine.Tests/SessionForkingTests.cs` (create)

**Interfaces:**
- Produces: `public async Task SessionAuditStore.CopyAsync(string sourceId, string targetId, CancellationToken ct = default)` — copies the source sidecar file to the target; no-op if either id is invalid or the source file is missing; never throws.
- Produces: `public static Task<string> SessionForking.ForkAsync(string workingDirectory, string? sourceId, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)` — mints a new id, saves `messages` as its transcript, copies `sourceId`'s audit sidecar (when `sourceId` is non-null), returns the new id.

- [ ] **Step 1: Write failing tests** in `tests/Engine.Tests/SessionForkingTests.cs`. Use a temp dir (IDisposable pattern like `SessionAuditStoreTests`). Seed a source session: `new SessionTranscriptStore(dir).SaveAsync("aaaaaaaaaaaa", msgs)` and append audit turns via `new SessionAuditStore(dir).AppendTurnAsync("aaaaaaaaaaaa", turn)` (see `SessionAuditStoreTests` for how to build a `SessionAuditTurn`).

```csharp
[Fact]
public async Task CopyAsync_duplicates_the_source_sidecar_to_the_target()
{
    var store = new SessionAuditStore(this.dir);
    await store.AppendTurnAsync("aaaaaaaaaaaa", MakeTurn(0));
    await store.AppendTurnAsync("aaaaaaaaaaaa", MakeTurn(1));

    await store.CopyAsync("aaaaaaaaaaaa", "bbbbbbbbbbbb");

    var src = await store.LoadAsync("aaaaaaaaaaaa");
    var dst = await store.LoadAsync("bbbbbbbbbbbb");
    Assert.Equal(2, dst.Count);
    Assert.Equal(src.Count, dst.Count);
    Assert.Equal(src[0].SystemPrompt, dst[0].SystemPrompt);
}

[Fact]
public async Task CopyAsync_is_a_noop_when_source_has_no_sidecar()
{
    var store = new SessionAuditStore(this.dir);
    await store.CopyAsync("aaaaaaaaaaaa", "bbbbbbbbbbbb"); // no source file
    Assert.Empty(await store.LoadAsync("bbbbbbbbbbbb"));
}

[Fact]
public async Task ForkAsync_creates_a_new_session_with_copied_transcript_and_audit()
{
    await new SessionTranscriptStore(this.dir).SaveAsync("aaaaaaaaaaaa",
        [new ChatMessage(ChatRole.User, [new TextBlock("hi")])]);
    await new SessionAuditStore(this.dir).AppendTurnAsync("aaaaaaaaaaaa", MakeTurn(0));

    var newId = await SessionForking.ForkAsync(this.dir, "aaaaaaaaaaaa",
        [new ChatMessage(ChatRole.User, [new TextBlock("hi")])]);

    Assert.NotEqual("aaaaaaaaaaaa", newId);
    Assert.Matches("^[0-9a-f]{12}$", newId);
    // new transcript exists with the seeded messages
    var t = await new SessionTranscriptStore(this.dir).LoadAsync(newId);
    Assert.NotNull(t);
    Assert.Single(t!);
    // new audit carries the source's turns
    Assert.Single(await new SessionAuditStore(this.dir).LoadAsync(newId));
    // source is untouched
    Assert.Single(await new SessionAuditStore(this.dir).LoadAsync("aaaaaaaaaaaa"));
}

[Fact]
public async Task ForkAsync_with_null_source_still_persists_the_transcript()
{
    var newId = await SessionForking.ForkAsync(this.dir, null,
        [new ChatMessage(ChatRole.User, [new TextBlock("hi")])]);
    Assert.NotNull(await new SessionTranscriptStore(this.dir).LoadAsync(newId));
    Assert.Empty(await new SessionAuditStore(this.dir).LoadAsync(newId));
}
```

`MakeTurn(int i)` helper: build a `SessionAuditTurn` with `TurnIndex = i`, a `TsUtc`, `Provider="p"`, `Model="m"`, `InputTokens/OutputTokens`, `SystemPrompt = "sys-" + i`, empty `ToolCalls`/`ToolDefs` — mirror how `SessionAuditStoreTests` constructs turns (read that file for the exact record shape).

- [ ] **Step 2: Run tests, verify they fail** (`CopyAsync`/`SessionForking` missing). Run: `dotnet test tests/Engine.Tests --filter "FullyQualifiedName~SessionForkingTests"`. Expected: FAIL (compile).
- [ ] **Step 3: Implement `SessionAuditStore.CopyAsync`.** Add a public method: guard `SessionIds.IsValid(sourceId) && SessionIds.IsValid(targetId)` (else return); resolve source path via the existing `FilePath(sourceId)` and target via `FilePath(targetId)`; if source file missing, return; `Directory.CreateDirectory(this.SessionsDir)` then `File.Copy(sourcePath, targetPath, overwrite: true)`; wrap in try/catch that swallows (never throw — matches the store's contract). Do NOT touch the change-only `emittedStateBySession` cache. Create `src/Coda.Sdk/SessionForking.cs`:

```csharp
using LlmClient;

namespace Coda.Sdk;

/// <summary>Creates a forked session: a brand-new id seeded from an existing session's transcript
/// and audit sidecar, leaving the source untouched.</summary>
public static class SessionForking
{
    /// <summary>
    /// Forks <paramref name="sourceId"/> into a fresh session id under <paramref name="workingDirectory"/>:
    /// persists <paramref name="messages"/> as the new session's transcript and copies the source's audit
    /// sidecar so the fork is a complete, resumable, fully-auditable session. The source is never modified.
    /// Returns the new id.
    /// </summary>
    public static async Task<string> ForkAsync(
        string workingDirectory, string? sourceId, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var newId = SessionIds.NewId();
        await new SessionTranscriptStore(workingDirectory).SaveAsync(newId, messages, ct).ConfigureAwait(false);
        if (sourceId is not null)
        {
            await new SessionAuditStore(workingDirectory).CopyAsync(sourceId, newId, ct).ConfigureAwait(false);
        }

        return newId;
    }
}
```

- [ ] **Step 4: Run tests, verify pass.** Expected: PASS (4/4).
- [ ] **Step 5: Commit** `feat(sdk): SessionForking.ForkAsync + SessionAuditStore.CopyAsync (fork carries audit)`.

---

### Task 2: Wire the three fork surfaces to carry audit

**Files:**
- Modify: `src/Coda.Tui/HeadlessRunner.cs`
- Modify: `src/Coda.Tui/Program.cs`
- Modify: `src/Coda.Tui/Commands/ForkCommand.cs`
- Test: `tests/Coda.Tui.Tests/ForkCommandTests.cs` (extend)

**Interfaces:**
- Consumes: `SessionForking.ForkAsync` (Task 1).

- [ ] **Step 1: Write a failing test** appended to `ForkCommandTests.cs` asserting the on-disk carry. Seed a source session in the context's working dir (the test's `tempDir`) with a transcript id + one audit turn, set `context.Session.SessionId` to that id and `History` to its messages, then run `/fork` and assert: `SessionId` changed to a fresh valid id; the NEW id's transcript exists; the NEW id's audit sidecar has the source's turn count; the SOURCE audit is unchanged. (Use `SessionTranscriptStore`/`SessionAuditStore` over `this.tempDir` to arrange + assert; the `BuildContext` helper already uses `tempDir` as the `SessionState` working dir.)

```csharp
[Fact]
public async Task Fork_carries_the_source_audit_into_the_new_session()
{
    var (_, context) = this.BuildContext();
    var dir = context.Session.WorkingDirectory;
    await new SessionTranscriptStore(dir).SaveAsync("source-aaaa",
        [new ChatMessage(ChatRole.User, [new TextBlock("q")])]);
    await new SessionAuditStore(dir).AppendTurnAsync("source-aaaa", MakeTurn());
    context.Session.SessionId = "source-aaaa";
    context.Session.History.Add(new ChatMessage(ChatRole.User, [new TextBlock("q")]));

    await new ForkCommand().ExecuteAsync(context, Array.Empty<string>());

    var newId = context.Session.SessionId!;
    Assert.NotEqual("source-aaaa", newId);
    Assert.NotNull(await new SessionTranscriptStore(dir).LoadAsync(newId));
    Assert.Single(await new SessionAuditStore(dir).LoadAsync(newId));      // audit carried
    Assert.Single(await new SessionAuditStore(dir).LoadAsync("source-aaaa")); // source untouched
}
```

(Add a small `MakeTurn()` helper in the test file, same shape as Task 1's.)

- [ ] **Step 2: Run tests, verify the new one fails** (current `/fork` only mints an id in memory; no files written). Run: `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~ForkCommandTests"`. Expected: the new test FAILS.
- [ ] **Step 3: Implement.**
  - `ForkCommand.ExecuteAsync`: replace `context.Session.SessionId = Coda.Sdk.SessionIds.NewId();` with
    `context.Session.SessionId = await Coda.Sdk.SessionForking.ForkAsync(context.Session.WorkingDirectory, context.Session.SessionId, context.Session.History, cancellationToken).ConfigureAwait(false);`
    (History is unchanged in memory; the source id is the current `SessionId`, which may be null — `ForkAsync` handles null). Keep the "Forked into a new session {id} (original frozen)." message using the returned id.
  - `HeadlessRunner.RunAsync`: in the seed block, change `seedSessionId = options.Fork ? null : target.Id;` to
    `seedSessionId = options.Fork ? await SessionForking.ForkAsync(workingDirectory, target.Id, target.Messages, cancellationToken).ConfigureAwait(false) : target.Id;`
    Update the `[fork]` stderr note to print the new id, e.g. `Console.Error.WriteLine($"[fork] from {target.Id} -> {seedSessionId} ({target.Messages.Count} messages)");` (compute after the assignment).
  - `Program.cs` (TUI startup): in the `startupIntent.Fork` branch, instead of leaving `session.SessionId` null, set
    `session.SessionId = await SessionForking.ForkAsync(session.WorkingDirectory, target.Id, target.Messages, cts.Token);`
    (still `session.History.AddRange(target.Messages)`). Keep the "Forked from {target.Id} into a new session ({n} messages)." message.

- [ ] **Step 4: Run the affected suites, verify pass.** Run:
  - `dotnet test tests/Coda.Tui.Tests --filter "FullyQualifiedName~ForkCommandTests|FullyQualifiedName~SessionCliTests"`
  - `dotnet build src/Coda.Tui/Coda.Tui.csproj`
  Expected: PASS, 0 warnings.
- [ ] **Step 5: Commit** `feat(tui): --fork / /fork / startup-fork carry the source audit into the new session`.

## Self-Review

- Coverage: audit copy primitive + fork helper (T1), all three surfaces wired (T2, REPL end-to-end tested on disk; headless/startup are thin wiring over the T1-tested helper).
- Type consistency: `SessionForking.ForkAsync(workingDirectory, sourceId?, messages, ct)` used identically in all three surfaces; `SessionAuditStore.CopyAsync(sourceId, targetId, ct)`.
- Behavior change to note in review: fork now eagerly persists the new session (transcript + audit) at fork time instead of lazily on the first turn — the fork appears in `/resume` immediately.
