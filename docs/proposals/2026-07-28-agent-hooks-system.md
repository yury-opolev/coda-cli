# Proposal: Agent hooks system

- **Date:** 2026-07-28
- **Status:** Completed. All 8 phases (35 items) implemented; see [`docs/hooks.md`](../hooks.md).
- **Author:** Yury Opolev (design exploration, Coda)
- **Scope:** `Coda.Agent` (hook bus, runner, payloads, `AgentLoop` seams, `AgentOptions`,
  `SubagentHost`, settings schema), `Coda.Sdk` (`CodaSession` turn entry, `TurnPipelineBuilder`),
  `Coda.Tui` (transcript surfacing of hook effects, `serve` parity).

## 1. Summary

Coda can be extended today only by editing its source or by writing an MCP server. There is no
supported way to run your own code at the moments that matter — when a question arrives, when a tool
is about to run, when the agent answers, when a session opens or closes.

This proposal turns hooks into a **first-class extension surface**: a set of lifecycle events, each
with a declared input payload and a declared set of outputs, where an output may **observe**,
**block**, or **mutate** the data flowing through that point. The motivating case is a policy hook
that inspects an incoming question — is it legitimate, does it carry private data? — and responds by
rewriting the prompt, tightening the system prompt, and restricting the toolset for that turn.

Two capabilities here go beyond anything shipping in a comparable harness, and are adopted
deliberately rather than by accident: **system-prompt mutation** and **assistant-response mutation**.
Both are called out in §9 with their hazards.

## 2. Current state (verified on `fcbd8c9`)

| Concern | Today | File |
|---|---|---|
| User-configurable hooks | Three events only: `PreToolUse`, `PostToolUse`, `Stop` | `src/Coda.Agent/Hooks/UserHook.cs` |
| Hook transport | Shell subprocess (`cmd.exe /c` or `/bin/sh -c`), JSON on stdin, 10 s timeout, 4096-char stdout/stderr cap | `src/Coda.Agent/Hooks/UserHookRunner.cs:18,140-210` |
| `PreToolUse` power | **Block only** — non-zero exit blocks, stdout becomes the reason | `UserHookRunner.cs:42-75` |
| `PostToolUse` power | **None** — exit code and errors ignored | `UserHookRunner.cs:80-104` |
| `Stop` power | **None** — payload is the literal `{}`, exit code ignored | `UserHookRunner.cs:109-128` |
| Mutation, anywhere | **None** — no hook can alter any value | — |
| Matcher | Exact tool-name string, case-insensitive; no regex | `UserHookRunner.cs:228-230` |
| Payload metadata | `tool`, `input`, `result` only — no event name, session id, cwd, or depth | `UserHookRunner.cs:232-303` |
| Failure policy | Every exception and timeout is swallowed and treated as **Allow** | `UserHookRunner.cs:68-71,202-203` |
| In-process hooks | `IPostSamplingHook` (fire-and-forget, result discarded) and `IStopHook` (**can** block the stop and inject a reason) | `src/Coda.Agent/AgentHooks.cs` |
| Tier asymmetry | `IStopHook` can force continuation; the user-facing `Stop` hook cannot | `AgentHooks.cs:63-89` vs `UserHookRunner.cs:109` |
| Config | `CodaSettings.Hooks`, wired only when non-empty | `src/Coda.Agent/Settings/CodaSettings.cs:13`, `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs:117` |
| Request assembly | `ChatRequest` rebuilt every iteration from `this.options` + `this.tools`; toolset already recomputed per call when tool search is active | `src/Coda.Agent/AgentLoop.cs:337-352` |
| Turn entry | User message appended directly to history | `src/Coda.Sdk/CodaSession.cs:469` |
| System prompt | Fixed at construction, read per request | `src/Coda.Agent/AgentOptions.cs:10`, `AgentLoop.cs:348` |

The two seams that matter: `ChatRequest` is **already** assembled fresh per iteration, and the
toolset is **already** recomputed per call. Per-turn overrides therefore need no restructuring of the
loop — only a per-turn shape object consulted at `AgentLoop.cs:344`.

## 3. Prior art (researched 2026-07-28)

Claude Code (docs, minified binary), GitHub Copilot CLI (docs + `changelog.md`, closed source),
Gemini CLI (source-verified at `bef6119`).

| Can a hook mutate… | Claude Code | Copilot CLI | Gemini CLI |
|---|---|---|---|
| The user's message, model-facing | ❌ documented as impossible | ✅ `userPromptTransformed` | ✅ `BeforeModel` |
| Injected context (additive) | ✅ 10 k cap, system-reminder wrapper | ✅ 10 KB cap | ✅ |
| **The system prompt** | ❌ | ❌ | ❌ |
| Tool input | ✅ `updatedInput` | ✅ `modifiedArgs` | ✅ `tool_input` merge |
| Tool result | ✅ `updatedToolOutput` | ✅ `modifiedResult` | ✅ deny+reason |
| Available toolset per turn | ❌ | ❌ | ✅ `BeforeToolSelection` |
| Model / params per call | ❌ | ❌ | ✅ `BeforeModel` |
| **Assistant's outgoing message** | ❌ display-only (`MessageDisplay`) | ❌ | ✅ `AfterModel` |
| Force continuation | ✅ 8-block cap | ✅ 8-block cap | ✅ |
| Persist permission rules | ✅ `updatedPermissions` | ❌ | ❌ |

Points worth carrying over:

- **Claude Code** is explicit that `UserPromptSubmit` *cannot* replace the prompt — you may only
  block it and re-inject via `additionalContext`. Copilot solves the same need with a separate
  `userPromptTransformed` event that rewrites the model-facing prompt while the UI keeps the
  original. **Copilot's shape is the better one** and is what this proposal adopts.
- **No harness exposes the system prompt.** All three restrict customization to out-of-band
  mechanisms (`--append-system-prompt`, `CLAUDE.md`, output styles).
- **Claude Code's `MessageDisplay` is deliberately display-only**: *"the transcript and what Claude
  sees keep the original text."* Gemini's `AfterModel` is the only true response mutation in the
  field.
- Both Claude Code and Copilot independently converged on an **8-consecutive-block cap** for
  continuation hooks.
- **Claude Code offers five handler types** — `command`, `http`, `mcp_tool`, `prompt` (LLM-evaluated)
  and `agent` (subagent-evaluated). The `prompt` type is what makes "is this question legitimate?"
  expressible as a rule instead of a regex.
- **Fail-open vs fail-closed** differs sharply: Claude fails *closed* for `UserPromptSubmit` and
  `PreToolUse`; Copilot always fails open, even for policy hooks. For a redaction hook, fail-open is
  a data leak.

## 4. Hook catalogue

Markers: ⚡ mutates data flowing through · ⛔ control/blocking · ➕ additive only · — observe only.

**Common envelope.** Every hook receives `event`, `sessionId`, `cwd`, `timestamp`, `depth`
(0 = main agent, 1–2 = subagent), `taskId?`. Every hook may return `continue:false` (⛔ abort the
run), `stopReason`, `systemMessage` (surfaced to the user), `suppressOutput`.

| # | Hook | When it runs | Input | Output |
|---|---|---|---|---|
| 1 | `SessionStart` | Session created or resumed, before the first prompt is accepted | `source` (new/resume/scheduled), `model`, `permissionMode`, `transcriptPath`, `resumedFrom?` | ➕ `additionalContext` · ⚡ `appendSystemPrompt` (session-wide) · ⛔ `initialUserMessage` (creates a turn) |
| 2 | `UserPromptSubmit` | After slash-command parsing, before the message is appended to history and before the first model call | `prompt`, `attachments[]`, `historyLength`, `model`, `permissionMode` | ⛔ `decision` allow/block/ask + `reason` · ⚡ `modifiedPrompt` · ➕ `additionalContext` · ⚡ `systemPrompt` (replace, opt-in) · ⚡ `appendSystemPrompt` · ⚡ `allowedTools` / `deniedTools` / `toolChoice` · ⚡ `model` · ⚡ `effort` |
| 3 | `PreToolUse` | Model requested a tool, **before** the permission check | `tool`, `input`, `toolUseId`, `iteration` | ⛔ `decision` allow/deny/ask + `reason` · ⚡ `modifiedInput` · ➕ `additionalContext` |
| 4 | `PermissionRequest` | A tool needs approval, before the interactive prompt renders | `tool`, `input`, `permissionMode`, `matchedRule?` | ⛔ `decision` allow/deny/prompt · ⚡ `modifiedInput` · ⚡ `updatedPermissions` (rules/mode; scope session/project/user) |
| 5 | `PostToolUse` | Tool finished, success **or** failure, before the result returns to the model | `tool`, `input`, `result`, `error?`, `durationMs`, `toolUseId` | ⛔ `decision` block (reason replaces the result) · ⚡ `modifiedResult` · ➕ `additionalContext` |
| 6 | `Stop` | The loop is about to end the turn — no further tool calls | `stopReason`, `iterations`, `continuationCount`, `stopHookActive` | ⛔ `decision` block → forces continuation, `reason` becomes the next instruction · ➕ `additionalContext`. **Cap: 8 consecutive blocks** |
| 7 | `AgentResponse` | After `Stop` agreed to stop; final text settled, before display and persistence | `response`, `stopReason`, `usage {input,output,cost}`, `durationMs` | ⚡ `displayContent` (UI only — history keeps the original) · ⚡ `modifiedResponse` (UI **and** history) |
| 8 | `SubagentStart` | The `task` tool spawns a nested agent, before its first model call | `parentTaskId`, `taskId`, `depth`, `prompt`, `toolset[]` | ⛔ `decision` block · ⚡ `modifiedPrompt` · ➕ `additionalContext` · ⚡ `appendSystemPrompt` · ⚡ `allowedTools` / `deniedTools` |
| 9 | `SubagentStop` | Nested agent finished, before its result returns to the parent | `taskId`, `depth`, `result`, `usage` | ⛔ `decision` block → subagent continues · ⚡ `modifiedResult` |
| 10 | `PreCompact` | Before history compaction (auto threshold or `/compact`) | `trigger` auto/manual, `tokensBefore`, `messageCount`, `instructions?` | ⛔ `decision` block → cancel compaction · ⚡ `instructions` (summarization prompt) |
| 11 | `PostCompact` | After compaction, before the next model call | `tokensBefore`, `tokensAfter`, `messageCount`, `summary` | ➕ `additionalContext` — re-inject what the summary dropped |
| 12 | `Notification` | Agent needs attention: idle awaiting input, approval pending, background task done | `kind`, `message`, `taskId?` | — side effects only |
| 13 | `SessionEnd` | Session closing: `/exit`, double `Ctrl+C`, shutdown | `reason` exit/interrupt/error/shutdown, `durationMs`, `turnCount`, `usage`, `transcriptPath` | — side effects only, ~2 s budget |

## 5. Output protocol

Today a hook communicates only through its exit code, and only `PreToolUse` is listened to. That is
replaced by a **single JSON document on stdout**:

```jsonc
{
  "continue": true,          // false ⇒ abort the whole run
  "stopReason": "…",         // surfaced when continue:false
  "systemMessage": "…",      // warning shown to the user
  "suppressOutput": false,   // keep hook stdout out of the transcript
  "decision": "block",       // event-specific; see catalogue
  "reason": "…",
  "hookSpecificOutput": { "hookEventName": "UserPromptSubmit", /* per-event fields */ }
}
```

Exit-code semantics, chosen to stay compatible with the hooks people have already written:

| Exit | Meaning |
|---|---|
| `0` | Success. Parse stdout as the schema above; empty stdout is a valid no-op |
| `2` | Block. `stderr` becomes the reason. Equivalent to `{"decision":"block"}` |
| other | Warn and apply the hook's `failOpen` policy (§8) |

All output strings are capped (proposed: 10 000 characters, matching both Claude Code and Copilot),
with overflow spilled to a file under `~/.coda/hook-output/` and a preview inlined.

## 6. Composition

Multiple hooks may match one event. They run **in configuration order**, and their outputs merge by
rules chosen so that a permissive hook can never widen a restrictive one:

| Output | Merge rule |
|---|---|
| `deniedTools` | **Union** |
| `allowedTools` | **Intersection** |
| `decision` | Strictest wins (`block` > `deny` > `ask` > `allow`) |
| `additionalContext` | Concatenate in order, `\n\n` separated |
| `appendSystemPrompt` | Concatenate in order |
| `modifiedPrompt`, `systemPrompt`, `modifiedResult`, `modifiedResponse`, `model`, `effort` | **Last writer wins, and the override is logged** so a silent overwrite is traceable |

Gemini CLI union-merges both tool lists, which lets a permissive hook widen a restrictive one; that
is the mistake this table avoids.

## 7. Handler types

`command` alone forces a policy author to express "does this contain private data?" as a regex. Four
types are proposed, following Claude Code:

| Type | Shape | Use |
|---|---|---|
| `command` | Shell subprocess, JSON on stdin (today's behaviour) | Scripts, linters, formatters, notifications |
| `http` | POST the payload to a URL, response is the schema | Central policy service shared across a team |
| `prompt` | Natural-language rule evaluated by a cheap model, returns `{ok, reason}` | **"Is this question legitimate? Does it carry secrets?"** |
| `agent` | A Coda subagent evaluates the payload | Judgements needing tools — e.g. grep the repo before deciding |

`prompt` and `agent` reuse machinery Coda already has: the safety classifier and `SubagentHost`.

## 8. Execution model and safety

| Concern | Decision |
|---|---|
| Timeouts | Per-event defaults, per-hook override. Fast gates (`UserPromptSubmit`, `PreToolUse`) short; `prompt`/`agent` handlers longer; `SessionEnd` ~2 s. Today's flat 10 s is both too short and too long |
| Failure policy | Per-hook `failOpen` (default `true`), but **`false` for policy hooks**. Today every exception and timeout silently means Allow — unacceptable for a redaction hook |
| Continuation cap | 8 consecutive blocks on `Stop`/`SubagentStop`, then the turn ends regardless. `stopHookActive` is passed so the hook can see it is in a continuation |
| Matchers | Regex, **anchored** (`^(?:…)$`). Copilot anchors, Claude does not, and unanchored matchers surprise people |
| Subagent coverage | Every gate also runs for subagent turns. Otherwise a policy hook is bypassed by delegating to a subagent — Coda nests to depth 2 |
| Auditability | The original prompt and every applied mutation are recorded in the session log and surfaced in the transcript |
| Trust | Hooks execute arbitrary code from settings files. Project-scoped hooks need an explicit trust decision before first run |
| Secrets | Payloads pass through the existing `SecretRedactor` before reaching an `http` handler |
| Unattended `ask` | `decision:"ask"` needs an answerer. Where none exists, the hook's `unattendedDecision` applies — see §8.2 |

### 8.1 Streaming and display mutation

`AgentResponse` fires once the turn has ended, but the TUI streams assistant text as it arrives. A
redaction hook would therefore run long after the secret was on screen.

The decision to withhold streaming has to be **static**, taken at session start from the registered
hook set — by the time a hook could report that it wants to change something, the text has already
been rendered. So a hook declares what it may return:

```jsonc
{ "event": "AgentResponse", "mutates": ["displayContent"], "handler": { /* … */ } }
```

If any registered `AgentResponse` hook declares `displayContent` or `modifiedResponse`, the shell
**buffers assistant text for the turn** and shows an animated placeholder in its place; the final
text is rendered once, after the hook has run. If none does, streaming is untouched and sessions
without display-mutating hooks pay nothing.

- The placeholder must stay legible during a long turn. Coda's operational status row already
  carries elapsed time and token counts, which is enough to show the turn is alive.
- **Only assistant text is buffered.** Tool activity continues to stream, because tool results have
  their own gate (`PostToolUse.modifiedResult`) that already runs before anything is displayed.
- Rejected alternative: a **per-chunk filter**, which is what Gemini's `AfterModel` does. A
  subprocess per chunk is untenable, and a secret split across a chunk boundary is invisible to
  both chunks. A sliding-window delay has the same hole. Buffering is the only variant that closes
  it.

### 8.2 Unattended contexts and `decision:"ask"`

`ask` means *"I am not confident — put this to a human."* Silently treating it as `allow` is the
worst available resolution, so it is not a no-op.

This is not specific to scheduled tasks. The same gap exists in `coda serve` when the client has not
implemented the permission callback, and in any headless run. The rule is therefore general:

> `ask` requires an answerer. Where none exists, the hook's declared `unattendedDecision`
> (`"allow"` | `"deny"`, **default `"deny"`**) is applied instead.

It belongs on the hook definition rather than in global configuration, because the policy author is
the one who knows whether "unsure" should stop the run or let it through. The outcome must be
visible rather than silent: the task log records `blocked by <hook> (unattended)`, and a scheduled
task that repeatedly trips a policy is diagnosable from `/tasks` and the schedule browser.

A third resolution — park the turn and let a human answer it later — is what Claude Code's `defer`
plus its `deferred_tool_use` resume round-trip provides. That is out of scope here, but
`unattendedDecision` does not preclude adding it later.

## 9. Deliberate departures from prior art

Three capabilities here exist in no shipping harness, or in only one. Each is intentional; each has a
hazard worth restating at implementation time.

1. **`systemPrompt` replacement** (#2). No harness exposes this. The system prompt is where the
   tool-use contract lives, so wholesale replacement while tools remain enabled tends to produce a
   model that ignores its tools. Mitigation: `appendSystemPrompt` is the documented default; full
   replacement requires an explicit opt-in flag on the hook definition.
2. **`modifiedResponse` writing back to history** (#7). Only Gemini does this; Claude Code
   deliberately restricted its equivalent to display. Rewriting the stored text means the model's
   next turn sees words it did not produce. Mitigation: ship `displayContent` alongside it, document
   the desync, and prefer display-only for redaction that does not need to persist.
3. **Tool shaping on `SubagentStart`** (#8). No harness offers it. Without it, tool restrictions
   applied at `UserPromptSubmit` are trivially escaped by delegating to a subagent.

## 10. Backlog

Phases are ordered so each is independently shippable and useful on its own.

### Phase 0 — Protocol foundation

- [ ] Replace the exit-code-only contract with the JSON schema in §5; keep exit `2` = block
- [ ] Add the common envelope (`event`, `sessionId`, `cwd`, `timestamp`, `depth`, `taskId`) to every payload
- [ ] Anchored regex matchers, replacing exact-string matching
- [ ] Per-event timeouts with per-hook override; per-hook `failOpen`
- [ ] Per-hook `unattendedDecision` with `deny` default, applied wherever no answerer exists (§8.2)
- [ ] Output caps with spillover to `~/.coda/hook-output/`
- [ ] Hook bus: ordered execution, merge rules from §6, override logging
- [ ] Settings schema + migration for existing three-event configs (must keep working untouched)

### Phase 1 — The request-shaping gate (the motivating case)

- [ ] Per-turn shape object consumed at `AgentLoop.cs:344` (system prompt, toolset, model, effort)
- [ ] `UserPromptSubmit` with `decision`, `modifiedPrompt`, `additionalContext`
- [ ] `appendSystemPrompt`, then opt-in `systemPrompt` replacement
- [ ] `allowedTools` / `deniedTools` / `toolChoice`, with intersect/union merge
- [ ] `model` / `effort` override
- [ ] Transcript surfacing: show that a prompt was rewritten, with the original available

### Phase 2 — Session lifecycle

- [ ] `SessionStart` (`additionalContext`, `appendSystemPrompt`, `initialUserMessage`)
- [ ] `SessionEnd` with a bounded budget, on all exit paths including interrupt
- [ ] `Notification`

### Phase 3 — Response side

- [ ] Hook `mutates` declaration in the settings schema, read at session start
- [ ] Static display-buffering decision + animated placeholder with elapsed/token progress (§8.1)
- [ ] `AgentResponse` with `displayContent`
- [ ] `modifiedResponse` with history write-back, documented desync
- [ ] Unify `Stop`: give the user-facing hook the blocking power `IStopHook` already has, with the 8-block cap

### Phase 4 — Tool and permission mutation

- [ ] `PreToolUse.modifiedInput`
- [ ] `PostToolUse.modifiedResult`, `decision:block`, and failure payloads
- [ ] `PermissionRequest` with `modifiedInput` and `updatedPermissions`

### Phase 5 — Subagents and compaction

- [ ] `SubagentStart` with prompt and tool shaping
- [ ] `SubagentStop` with `modifiedResult` and continuation
- [ ] `PreCompact` / `PostCompact`

### Phase 6 — Handler types

- [ ] `http` handler with URL allowlist and secret redaction
- [ ] `prompt` handler over the existing classifier
- [ ] `agent` handler over `SubagentHost`

### Phase 7 — Surfacing and trust

- [ ] `/hooks` command: list configured hooks, show last run, enable/disable, dry-run a payload
- [ ] Trust prompt for project-scoped hooks before first execution
- [ ] `serve` parity — hook effects observable over JSON-RPC
- [ ] Documentation: reference page plus worked examples for the PII-gate case

## 11. Open questions

Resolved since first draft: streaming vs display mutation (→ §8.1) and `ask` in unattended contexts
(→ §8.2).

1. **Do in-process hooks stay a separate tier?** `IPostSamplingHook` and `IStopHook` exist for
   embedders. Either every event gets a matching interface, or the shell bus becomes the only
   contract and the interfaces are re-expressed on top of it. The second is less code and one
   semantics; the first is friendlier to `Coda.Sdk` consumers.
2. **Ordering of `Stop` and `AgentResponse`.** Proposed: `Stop` runs first and may loop the agent
   back; `AgentResponse` runs only once the turn has truly ended. With §8.1 in place the streaming
   hazard is handled, but the interaction with a *continuation* is still open — whether the
   placeholder persists across blocked stops, or each continuation reveals its own text.
3. **Does `modifiedPrompt` alter what is stored in session history**, or only what is sent to the
   model? Copilot keeps the UI original. Storing the original preserves auditability; storing the
   rewrite makes resumed sessions self-consistent. Probably: store both.
4. **Cost of `prompt`/`agent` handlers on every turn.** A per-turn classifier call is real latency
   and real spend. Caching by prompt hash, or restricting these handlers to specific events, may be
   necessary.
