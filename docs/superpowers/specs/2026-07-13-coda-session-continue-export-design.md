# Session Continue/Resume + Export/Import (Full Audit) — Design

**Date:** 2026-07-13
**Repo:** coda-cli (github.com/yury-opolev/coda-cli)
**Branch:** `feature/session-continue-export-audit`
**Status:** Approved (brainstorming complete)

## Problem

Coda persists a lean conversation transcript per turn, but a user cannot exit and reliably
pick a conversation back up from the shell, nor move a conversation between machines, nor
produce a complete replay/eval artifact of a run. Concretely:

1. **No shell-level continue/resume.** Launching `coda` (TUI) or `coda run` (headless) *always*
   mints a brand-new session id (`CodaSession` ctor: `sessionId ?? Guid.NewGuid().ToString("N")[..12]`,
   `src/Coda.Sdk/CodaSession.cs:80`). There is no `coda --continue` / `coda --resume`. The only
   resume path is the in-REPL `/resume <id>` command.
2. **In-REPL `/resume` forks instead of continuing.** `ResumeCommand` loads a transcript into
   `context.Session.History` but does **not** adopt the resumed id, so subsequent turns persist
   under the *current* session's (new) id — the old transcript is copied forward into a new file
   rather than appended to. (The SDK already has the correct primitive — see below — it is just
   not wired to the front-ends.)
3. **No export/import.** There is no command to emit a portable session file or ingest one. The
   `<workdir>/.coda/sessions/<id>.json` file is copyable by hand, but it is keyed to a working
   directory and there is no first-class, self-describing round-trip.
4. **No full audit record.** The persisted transcript is deliberately lean — messages with
   `text` / `tool_use` / `tool_result` blocks only. It omits the rendered **system prompt**, the
   **tool definitions**, and per-turn **usage / timestamps / stop reason**. That data is needed
   for debugging and for automated quality assessment / replay.

Goal: bring coda to **at least Claude Code parity** for session handling —
exit-and-continue/resume from the shell (true append, all three front-ends), plus
export/import of a **full audit record**.

## What already exists (grounded in source)

- **Auto-persist per turn.** `CodaSession.RunAsync` calls `PersistTranscriptAsync`
  (`CodaSession.cs:357`, also wired as the `PersistTurnAsync` delegate at `:339`) →
  `SessionTranscriptStore.SaveAsync(SessionId, history)` → atomic temp+rename write of
  `<workdir>/.coda/sessions/<id>.json` (`src/Coda.Sdk/SessionTranscriptStore.cs`). Survives a hard
  kill. Confirmed live: a 54-message transcript survived at `<repo>/.coda/sessions/<id>.json`.
- **True-continue primitive already in the SDK.** `CodaSession.Resume(sessionId, messages)`
  (`CodaSession.cs:274-286`) adopts the id and replaces history — docstring: *"Used to resume a
  session in a fresh process."* The ctor also accepts `history` + `sessionId` (`:64-80`). **No new
  resume mechanic is required; the front-ends simply never call it.**
- **Per-turn metadata already captured.** `RecordingSink` (`src/Coda.Sdk/RecordingSink.cs`)
  records final text, tool calls/results, stop reason, and token usage; surfaced as `RunResult`
  (`CodaSession.cs:359-380`, `Usage = recording.Usage`).
- **System prompt has a deterministic builder** — `AgentSystemPrompt.Build(...)`
  (`CodaSession.cs:431`); tool defs are available as `IReadOnlyList<ToolDefinition>`.
- **Session listing** — `SessionTranscriptStore.ListAsync` returns summaries newest-first
  (id, createdUtc, message count, preview).
- **Front-end entry points** — `src/Coda.Tui/Program.cs` dispatches `run` / `serve` / `models` /
  `help` on `args[0]`, then falls through to the interactive TUI; `--version` / `--help` handled by
  `ImmediateCli`. Headless is `src/Coda.Tui/HeadlessRunner.cs` (+ `HeadlessOptions`).

## Approved decisions

1. **CLI surface: flags AND subcommands** (aliases of each other).
   - `coda -c` / `coda --continue` / `coda continue` — resume the most-recent session *in this
     working directory*, adopt its id, append.
   - `coda -r [id]` / `coda --resume [id]` / `coda resume [id]` — resume by id, or an interactive
     numbered picker when no id is given (over `ListAsync`).
   - `coda export <id> [--out <file>] [--pretty]`, `coda import <file>` — plus REPL `/export`,
     `/import`.
2. **Export = full audit record**, sourced from **continuous capture via a sidecar**.
3. **Scoping: per-working-directory** (matches coda's existing `<workdir>/.coda/sessions/`
   storage and Claude Code's per-project model).
4. **Resume semantics: adopt-id-and-append (true continue)** by default.
5. **Headless `coda run` continue/resume is in v1** (alongside the TUI). `coda serve` already has
   resume and is left as-is; it inherits the audit sidecar for free because the sidecar lives in
   the SDK.
6. **`--fork` (resume into a fresh id) is deferred.** The mechanic is a one-line difference (load
   history but do *not* adopt the id), but the CLI surface + tests across three front-ends are not
   worth v1. Documented as an easy follow-on.

## Design

Most logic lands in the **SDK (`Coda.Sdk`)** so all three front-ends benefit; the front-ends stay
thin (parse flags → set up the SDK → run).

### 1. Resume wiring (SDK primitive already exists)

No new SDK mechanic. Each front-end, when continue/resume is requested:

1. Resolve the target id: `--continue` → the newest summary from
   `SessionTranscriptStore.ListAsync` for the working dir (or a clear "no sessions" error);
   `--resume <id>` → the given id; `--resume` with no id → interactive numbered picker (TUI) or a
   required id (headless, non-interactive).
2. `var messages = await store.LoadAsync(id)` (clear error if not found).
3. Construct the `CodaSession` and call `session.Resume(id, messages)` (or pass `history` +
   `sessionId` to the ctor) **before** the first `RunAsync`. Subsequent `PersistTranscriptAsync`
   then targets the same `<id>.json` — true append, fixing problem #2.

`ResumeCommand` (REPL) is updated to adopt the resumed id as well, so in-REPL `/resume <id>`
matches shell semantics (true continue, not fork), and to support numbered selection from the list.

### 2. Audit sidecar — `SessionAuditStore` (new, SDK)

Append-only `<workdir>/.coda/sessions/<id>.audit.jsonl`, one JSON object per turn, written from the
**same per-turn seam** as transcript persistence (the `PersistTurnAsync` path / immediately after a
turn completes in `RunAsync`), pulling per-turn data from the turn's `RecordingSink`/`RunResult`.

Per-turn line shape:

```json
{
  "ts": "2026-07-13T09:00:00.0000000Z",
  "turnIndex": 3,
  "provider": "github-copilot",
  "model": "claude-opus-4.8",
  "usage": { "inputTokens": 1234, "outputTokens": 567, "cacheReadTokens": 0, "cacheWriteTokens": 0 },
  "stopReason": "end_turn",
  "toolCalls": [ { "name": "read_file", "input": "…", "result": "…", "isError": false } ],
  "systemPrompt": "…",   // emitted ONLY when it changes from the previous turn
  "toolDefs":   [ … ]    // emitted ONLY when it changes from the previous turn
}
```

- **Change-only emission** of `systemPrompt` and `toolDefs`: the store keeps the last-emitted hash
  (in memory; recomputed from the file on first append in a fresh process) and writes the full
  value only when the hash changes (first turn always emits). Readers reconstruct the effective
  value for any turn by carrying forward the most recent emitted value. This keeps the file small
  while remaining complete.
- **Never breaks a turn.** Sidecar writes are wrapped like `PersistTranscriptAsync` already is
  (`CodaSession.cs:645-649`): a failure is logged and swallowed.
- **Atomic append.** Append a single line with a flush; a torn final line is tolerated by the
  reader (skip-last-if-unparseable), mirroring the transcript store's corruption tolerance.
- **Resume ignores the sidecar** — it is purely the audit trail; resume reads only the lean
  transcript.
- **Off/on:** the sidecar is written whenever transcript persistence is active (i.e. a valid
  session id and a working dir). No new setting for v1 (YAGNI); a future opt-out can hang off
  `SessionOptions` if needed.

### 3. Session bundle — `SessionBundle` + `SessionBundleService` (new, SDK)

Self-contained, portable `*.coda-session.json`:

```json
{
  "schema": "coda.session/1",
  "codaVersion": "0.1.63",
  "exportedUtc": "2026-07-13T09:05:00Z",
  "id": "8f2a1c9b4d0e",
  "createdUtc": "2026-07-12T18:00:00Z",
  "provider": "github-copilot",
  "model": "claude-opus-4.8",
  "auditAvailable": true,
  "systemPrompt": "…",        // effective final system prompt (+ per-turn overrides in turns[])
  "toolDefs": [ … ],
  "turns": [
    { "role": "user",      "ts": "…", "blocks": [ … ] },
    { "role": "assistant", "ts": "…", "usage": { … }, "stopReason": "end_turn", "blocks": [ … ] }
  ]
}
```

- **Export** = read lean transcript (`LoadAsync`) + audit sidecar, merge, write bundle.
  - `--out <file>` chooses the path (default `./<id>.coda-session.json`); `--pretty` writes indented
    JSON (default compact).
  - If the sidecar is missing (a session created before this feature), export still succeeds with
    `auditAvailable: false` and turns carrying blocks only (no usage/system prompt); a note is
    printed to stderr.
- **Import** = parse a bundle → write `<id>.json` (lean transcript reconstructed from `turns`) and
  `<id>.audit.jsonl` (reconstructed from the per-turn audit fields) into the current working dir's
  `.coda/sessions/`. Then the user runs `coda continue` / `coda resume <id>`.
  - **Id preserved**; on collision with an existing local session, mint a new 12-char id and print
    it (`imported as <newid>`). Validation: reject a bundle whose `schema` major version is unknown.

### 4. Front-end wiring

- **TUI (`Program.cs`)** — add `continue` / `resume` / `export` / `import` to the `args[0]`
  dispatch (next to `run`/`serve`/`models`/`help`), and accept `-c` / `--continue` / `-r` /
  `--resume [id]` as flag forms (handled in the same pre-TUI resolve). When resuming, load the
  transcript and thread the id + history into the TUI's `CodaSession` (via `Resume`) so persistence
  appends. `export`/`import` run as immediate, credential-free subcommands (no session/LLM needed) —
  they operate purely on files — and print a short result to stdout.
- **Headless (`HeadlessRunner` / `HeadlessOptions`)** — add `--continue` and `--resume <id>` to
  the parser. Before the first `RunAsync`, resolve the id (most-recent-in-cwd / given), `LoadAsync`
  the transcript, and `session.Resume(id, messages)`. `coda run --continue -p "<next>"` then appends
  the new prompt to the same transcript + audit trail (enables multi-invocation headless
  conversations and eval replay). Update the usage string.
- **`serve`** — unchanged (already resumes); inherits the audit sidecar via the SDK.
- **Slash commands** — register `ExportCommand` and `ImportCommand` in
  `src/Coda.Tui/Repl/SlashCommandCatalog.cs`; update `ResumeCommand` for id-adoption + numbered
  pick.
- **Help/usage** — update `ImmediateCli` usage text, `coda help` metadata, and README.

## Testing

**SDK unit (Engine.Tests):**
- `SessionAuditStore`: appends one line per turn; emits `systemPrompt`/`toolDefs` on the first turn
  and again only when they change; carries the effective value forward on read; tolerates a torn
  final line; a write failure never throws out of the turn seam.
- `SessionBundleService`: export→import→export is idempotent (byte-stable bundle for the same
  input); export of a session with no sidecar yields `auditAvailable:false` and block-only turns;
  import preserves id, and on collision mints + reports a new id; unknown `schema` major is rejected.
- Resume/adopt-id: after `Resume(id, msgs)`, a subsequent turn persists back to the **same**
  `<id>.json` (not a new file) and appends to `<id>.audit.jsonl`.

**Front-end (Coda.Tui.Tests):**
- `HeadlessOptions` parses `--continue` / `--resume <id>` (and rejects `--resume` with no id in the
  non-interactive path).
- `coda run --continue` across two invocations produces **one** transcript that grew, not two files.
- TUI arg dispatch routes `continue`/`resume`/`export`/`import`; `ResumeCommand` adopts the id.
- Export/import command happy-path + not-found/invalid-file errors.

**Integration:**
- One real turn writes both `<id>.json` and `<id>.audit.jsonl`; the exported bundle contains the
  system prompt and per-turn usage.

## Files (anticipated)

**New (SDK):** `src/Coda.Sdk/SessionAuditStore.cs`, `src/Coda.Sdk/SessionBundle.cs`,
`src/Coda.Sdk/SessionBundleService.cs`.
**New (TUI):** `src/Coda.Tui/Commands/ExportCommand.cs`, `src/Coda.Tui/Commands/ImportCommand.cs`,
and a small `src/Coda.Tui/SessionCli.cs` (shared continue/resume/export/import arg resolution used
by both `Program.cs` and headless).
**Modify:** `src/Coda.Sdk/CodaSession.cs` (wire the audit sidecar into the per-turn seam),
`src/Coda.Tui/Program.cs` (dispatch + flags), `src/Coda.Tui/HeadlessRunner.cs` +
`HeadlessOptions.cs` (continue/resume), `src/Coda.Tui/Commands/ResumeCommand.cs` (adopt id + pick),
`src/Coda.Tui/Repl/SlashCommandCatalog.cs` (register export/import), `src/Coda.Tui/ImmediateCli.cs`
(usage), README.

## Scope boundaries (v1)

- **In:** shell + subcommand continue/resume for **TUI and headless `run`**; true append (id
  adoption); continuous audit sidecar; export/import of a full-audit bundle; REPL `/resume`
  (fixed) + `/export` + `/import`.
- **Deferred:** `--fork` (resume into a fresh id); a picker for headless (`--resume` requires an id
  when non-interactive); any audit-sidecar opt-out setting; changes to `coda serve`'s existing
  resume.

## Delivery

Implement on this branch, `./build.ps1 -Test` green, PR to coda-cli `main` (its established
workflow — PRs #13–15). Rebuilding/reinstalling the host `coda` tool and bumping cortex's
`lib/coda-cli` submodule pin are a separate, later step (not part of this spec).
