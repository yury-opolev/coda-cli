# Hooks reference

Hooks let you attach shell commands, HTTP endpoints, prompt rules, or subagents to agent
lifecycle events.  They run at defined points in the agent loop — before a tool call, after a
response, when the session ends — and can observe, redirect, mutate, or block the action.

---

## Contents

- [Configuration](#configuration)
- [Handler types](#handler-types)
- [Per-hook policy fields](#per-hook-policy-fields)
- [Events](#events)
- [JSON output protocol](#json-output-protocol)
- [Exit-code semantics](#exit-code-semantics)
- [Composition rules](#composition-rules)
- [Trust model](#trust-model)
- [Managing hooks with `/hooks`](#managing-hooks-with-hooks)
- [Serve protocol](#serve-protocol)
- [Worked example](#worked-example)

---

## Configuration

Hooks live in `settings.json` under the `hooks` key.  The key is the event name; the value is an
array of hook entries.  User settings (`~/.coda/settings.json`) and project settings
(`<project>/.coda/settings.json`) are merged: user hooks run first, then project hooks, in
declaration order within each file.

```json
{
  "hooks": {
    "UserPromptSubmit": [
      {
        "command": "~/.coda/hooks/classify.sh",
        "timeoutSeconds": 20,
        "failOpen": false,
        "mutates": ["modifiedPrompt", "deniedTools"]
      }
    ],
    "PreToolUse": [
      {
        "command": "~/.coda/hooks/audit.sh",
        "matcher": "bash|write_file"
      }
    ]
  },
  "httpHookAllowlist": ["https://hooks.internal.example.com"],
  "hookDisabledHashes": []
}
```

`hookDisabledHashes` holds the content hashes of hooks that have been disabled via
`/hooks disable <n>`.  It is managed automatically; do not edit it by hand.

---

## Handler types

Set `type` on a hook entry to select the handler.  When `type` is omitted the loader infers
it from the fields present: a `command` field → `command`; a `url` field → `http`;
a `prompt` field without a `url` → `prompt`.

| Type | Dispatch | Required field |
|------|----------|----------------|
| `command` | Shell subprocess; payload on stdin, output on stdout | `command` |
| `http` | HTTP POST; payload as JSON body, response body parsed as output | `url`; must be in `httpHookAllowlist` |
| `prompt` | LLM evaluates the `prompt` rule against the payload | `prompt` |
| `agent` | Subagent evaluates the `prompt` task against the payload | `prompt` |

**`command`** — The command is passed to the system shell.  The full payload JSON is written to
stdin.  Stdout is captured and parsed; stderr is captured and logged.  The hook is killed after
the timeout and the fail-open/closed policy applies.

**`http`** — The payload is POST-ed as `application/json` to the URL.  The response body is
parsed identically to command stdout.  Destination URLs must be listed in `httpHookAllowlist`
(union of user and project lists); an unlisted URL is skipped and treated as a timeout for
fail-open/closed purposes.

**`prompt`** — The `prompt` string is a natural-language rule evaluated by the configured model
against the payload.  The LLM is asked to produce a JSON output in the standard format.  Use for
semantic classification tasks where a script is too brittle.

**`agent`** — A full subagent is launched with the `prompt` as its task.  Set `agentType` to the
agent type key (default `"general-purpose"`).  Use sparingly — a subagent has its own tool
budget.

---

## Per-hook policy fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `matcher` | `string` | `null` (all tools) | Regex anchored to the full tool name (`^(?:<matcher>)$`, case-insensitive). Applies to `PreToolUse`, `PostToolUse`, and `PermissionRequest`; ignored for other events. |
| `timeoutSeconds` | `int` | Event default | How long the hook may run before it is killed. |
| `failOpen` | `bool` | Event default | When `true`, a timeout or error is treated as allow. When `false`, it blocks. |
| `unattendedDecision` | `"allow"\|"deny"` | `"deny"` | Resolution when the hook returns `decision: "ask"` and no interactive answerer is available (headless, serve without a callback, scheduled). |
| `allowSystemPromptReplace` | `bool` | `false` | Must be `true` for a `UserPromptSubmit` hook's `systemPrompt` output to take effect.  Without this flag the field is silently ignored. |
| `mutates` | `string[]` | `null` | The output fields this hook may return that mutate user-visible data.  Declared statically at session start; used by the TUI to decide whether to buffer assistant text before the hook runs. |

---

## Events

All payloads include an **envelope** prepended by the bus:

```json
{
  "event": "EventName",
  "sessionId": "abc123",
  "cwd": "/home/user/project",
  "timestamp": "2026-07-28T12:00:00.000Z",
  "depth": 0,
  "taskId": null
}
```

`depth` is 0 for top-level sessions, 1+ for subagents.  `taskId` identifies the running task.

---

### `UserPromptSubmit`

Fires once per turn, after the user submits a message and before it is appended to history.
The most powerful gate: a block here means the model never sees the message.

**Default policy:** fail-closed, 30 s timeout.

**Payload:**

```json
{
  "event": "UserPromptSubmit",
  "prompt": "What is the capital of France?",
  "attachments": ["image", "file"],
  "historyLength": 4,
  "model": "claude-opus-4-5",
  "permissionMode": "default"
}
```

**Output fields** (`hookSpecificOutput`):

| Field | Type | Composition | Description |
|-------|------|-------------|-------------|
| `modifiedPrompt` | `string` | last-writer-wins | Replaces the user message text sent to the model. The original is surfaced via `OnPromptRewritten`; history stores the modified version. |
| `additionalContext` | `string` | concatenated | Appended as a separate synthetic user message after the (modified) prompt. |
| `appendSystemPrompt` | `string` | concatenated | Appended to the system prompt for this turn only. |
| `systemPrompt` | `string` | last-writer-wins | Full system prompt replacement. Requires `allowSystemPromptReplace: true` on the hook. |
| `deniedTools` | `string[]` | union | Tool names the model must not call this turn. |
| `allowedTools` | `string[]` | intersection | When set, restricts the model to only these tools. Multiple hooks: intersection. |
| `toolChoice` | `string` | last-writer-wins | Overrides tool choice mode for this turn. |
| `model` | `string` | last-writer-wins | Overrides the model for this turn. |
| `effort` | `string` | last-writer-wins | Overrides the reasoning effort for this turn. |

---

### `PreToolUse`

Fires before each tool call.  A block here prevents the tool from executing.

**Default policy:** fail-closed, 10 s timeout.  `matcher` applies.

**Payload:**

```json
{
  "event": "PreToolUse",
  "tool": "write_file",
  "input": { "path": "/etc/hosts", "content": "..." }
}
```

**Output fields:**

| Field | Type | Description |
|-------|------|-------------|
| `decision` | `"allow"\|"block"\|"ask"` | `block` prevents the call.  `ask` forwards to the permission prompt. |
| `reason` | `string` | Shown to the user when blocked. |

---

### `PermissionRequest`

Fires after `PreToolUse` passed, only when **no deny rule matches** and the tool would otherwise
trigger the interactive approve/deny prompt.  Lets a hook grant or deny programmatically without
interrupting the user.

> **Note:** a call blocked by a configured deny rule never reaches this hook — the deny rule is a
> floor the hook cannot lift.  This matches the documented "only when it would otherwise prompt"
> semantics: a denied call would never reach the prompt.

**Default policy:** fail-closed, 10 s timeout.  `matcher` applies.

**Payload:**

```json
{
  "event": "PermissionRequest",
  "tool": "bash",
  "input": { "command": "rm -rf /tmp/scratch" },
  "permissionMode": "default",
  "matchedRule": null
}
```

`matchedRule` is populated when an existing allow rule matched the call (e.g. `"allow:bash(safe:*)"`),
or `null` when no rule matched.  A deny rule match prevents the hook from firing entirely.

**Output fields:**

| Field | Type | Description |
|-------|------|-------------|
| `decision` | `"allow"\|"deny"\|"allowOnce"\|"denySession"\|"prompt"` | Strictest decision across hooks wins.  `prompt` (default) defers to the user. |
| `reason` | `string` | Shown when denied. |
| `hookSpecificOutput.updatedPermissions` | `object` | Optional — persist rule changes for this or future sessions (see below). |

**`updatedPermissions` output:**

| Field | Type | Description |
|-------|------|-------------|
| `addRules.allow` | `string[]` | Allow rules to add (e.g. `["bash(safe:*)"]`). Must include an argument pattern — bare tool names are rejected (see security notes below). |
| `addRules.deny` | `string[]` | Deny rules to add. |
| `setMode` | `string` | Set the permission mode for this session (e.g. `"acceptEdits"`). Cannot be `"bypassPermissions"`. |
| `scope` | `"session"\|"project"\|"user"` | Where to persist. `session` applies only to the current run. `project` writes `<project>/.coda/settings.json`. `user` writes `~/.coda/settings.json`. |

**Security restrictions on `updatedPermissions`:**

- **No bypass escalation** — `setMode: "bypassPermissions"` is always refused; a hook cannot
  disable all permission prompts.
- **No scope escalation** — a project-scoped hook requesting `scope: "user"` is silently clamped
  to `scope: "project"`.  Project hooks may only persist to project scope.
- **No over-broad allow rules** — a bare tool name with no argument pattern (e.g. `"bash"`) is
  rejected for disk persistence.  Such a rule would match every call to that tool, blanket-disabling
  future prompts.  Specify an argument pattern (e.g. `"bash(safe:*)"`) to persist to disk.

---

### `PostToolUse`

Fires after a tool call completes, whether it succeeded or failed.  Cannot undo the call;
can block the agent from using the result or modify the result text the model sees.

**Default policy:** fail-open, 10 s timeout.  `matcher` applies.

**Payload:**

```json
{
  "event": "PostToolUse",
  "tool": "bash",
  "input": { "command": "ls /etc" },
  "result": "...",
  "error": null
}
```

`error` is present and non-null when the tool call produced an error.

**Output fields** (`hookSpecificOutput`):

| Field | Type | Description |
|-------|------|-------------|
| `decision` | `"block"\|"allow"` | `block` surfaces the `reason` to the model as a tool-call error and prevents the result from being appended. |
| `reason` | `string` | Shown when blocked. |
| `modifiedResult` | `string` | Replaces the tool result text seen by the model (and stored in history). |

---

### `Stop`

Fires when the agent loop is about to stop — after the last model response and before returning
to the caller.  A hook returning `continue: false` or `decision: "block"` causes the loop to
continue (the hook is saying "do not stop yet").  Use to inject a follow-up instruction.

**Default policy:** fail-open, 10 s timeout.

**Payload (observation form, when no outcome is available):**

```json
{
  "event": "Stop"
}
```

**Payload (outcome form, when the agent loop can act on the result):**

```json
{
  "event": "Stop",
  "stopReason": "end_turn",
  "iterations": 3,
  "continuationCount": 0,
  "stopHookActive": false
}
```

**Output fields** (`hookSpecificOutput`):

| Field | Type | Description |
|-------|------|-------------|
| `continue` | `bool` | `false` → re-enter the agent loop. |
| `reason` | `string` | Message injected as a synthetic user turn when `continue: false`. |
| `stopReason` | `string` | Overrides the stop reason. |

---

### `SessionStart`

Fires once at session startup, before the first model call.

**Default policy:** fail-open, 10 s timeout.

**Payload:**

```json
{
  "event": "SessionStart",
  "source": "repl",
  "model": "claude-opus-4-5",
  "permissionMode": "default",
  "transcriptPath": "/home/user/project/.coda/sessions/abc.json",
  "resumedFrom": null
}
```

**Output fields** (`hookSpecificOutput`):

| Field | Type | Composition | Description |
|-------|------|-------------|-------------|
| `additionalContext` | `string` | last-writer-wins | Prepended to the session context. |
| `appendSystemPrompt` | `string` | concatenated | Appended to the system prompt for the whole session. |
| `initialUserMessage` | `string` | last-writer-wins | A synthetic first user turn injected before the user speaks. |

---

### `SessionEnd`

Fires once when the session terminates.  Outputs are discarded (observation-only).

**Default policy:** fail-open, 2 s timeout.

**Payload:**

```json
{
  "event": "SessionEnd",
  "reason": "exit",
  "durationMs": 30000,
  "turnCount": 5,
  "usage": {
    "inputTokens": 1200,
    "outputTokens": 800,
    "cacheReadTokens": 0,
    "cacheWriteTokens": 0
  },
  "transcriptPath": "/home/user/project/.coda/sessions/abc.json"
}
```

---

### `Notification`

Fires when the agent emits a structured notification (branch switch, context-window warning,
compaction, etc.).  Observation-only.

**Default policy:** fail-open, 10 s timeout.

**Payload:**

```json
{
  "event": "Notification",
  "kind": "branch_switch",
  "message": "Switched from main to feature/x",
  "taskId": null
}
```

---

### `AgentResponse`

Fires once per model turn, after the response is received and before the turn result is
surfaced to the caller.  Observation-only.

It fires on **every** turn, including a tool-only turn whose final assistant text is empty —
`response` is then `""`. This is an audit surface, and a turn that produced only tool calls is
exactly the kind of turn an auditor needs to see.

**Default policy:** fail-open, 10 s timeout.

**Payload:**

```json
{
  "event": "AgentResponse",
  "response": "The capital of France is Paris.",
  "stopReason": "end_turn",
  "usage": {
    "inputTokens": 150,
    "outputTokens": 12,
    "cacheReadTokens": 0,
    "cacheWrite5mTokens": 0,
    "cacheWrite1hTokens": 0
  },
  "durationMs": 340
}
```

#### Scope of the redaction guarantee

`AgentResponse` can rewrite what the user sees and what is persisted, but the guarantee is
narrower than "nothing sensitive escapes":

- **Covered:** the assistant's response text and its thinking. Both reach the hook in `response`
  and can be replaced.
- **Not covered:** model-authored *tool arguments*. A secret the model puts into a tool call never
  passes through `AgentResponse`. Use `PreToolUse` with `hookSpecificOutput.modifiedInput` to
  rewrite tool arguments before the tool runs — that is the only seam that sees them.
- **Not covered:** tool *results*. Use `PostToolUse` with `hookSpecificOutput.modifiedResult`.

The two rewrite fields differ in what they reach:

| Field | Display | History / transcript |
|-------|---------|----------------------|
| `displayContent` | replaced | **unchanged — the original text stays in history and in the persisted transcript** |
| `modifiedResponse` | replaced | replaced |

So `displayContent` alone hides text from the screen but does not scrub persistence. A hook that
must keep a secret out of the transcript has to set `modifiedResponse`. Setting both shows
`displayContent` to the user while storing `modifiedResponse`.

---

### `SubagentStart`

Fires before a subagent makes its first model call.  Can block the subagent or modify its
prompt.

**Default policy:** fail-closed, 10 s timeout.

**Payload:**

```json
{
  "event": "SubagentStart",
  "parentTaskId": "parent-task-id",
  "taskId": "child-task-id",
  "depth": 1,
  "prompt": "Summarise the diff in CHANGES.md",
  "toolset": ["read_file", "bash"]
}
```

**Output fields** (`hookSpecificOutput`):

| Field | Type | Description |
|-------|------|-------------|
| `decision` | `"allow"\|"block"` | `block` prevents the subagent from running. |
| `reason` | `string` | Shown when blocked. |
| `modifiedPrompt` | `string` | Replaces the subagent prompt. |
| `appendSystemPrompt` | `string` | Appended to the subagent's system prompt. |
| `deniedTools` | `string[]` | Tools the subagent must not call. |
| `allowedTools` | `string[]` | If set, restricts the subagent to these tools (intersection across hooks). |

---

### `SubagentStop`

Fires after a subagent finishes, before its result returns to the parent.  Can modify the
result.

**Default policy:** fail-open, 10 s timeout.

**Payload:**

```json
{
  "event": "SubagentStop",
  "taskId": "child-task-id",
  "depth": 1,
  "result": "The diff introduces two new exported functions...",
  "usage": {
    "inputTokens": 800,
    "outputTokens": 200,
    "cacheReadTokens": 0,
    "cacheWrite5mTokens": 0,
    "cacheWrite1hTokens": 0
  }
}
```

**Output fields** (`hookSpecificOutput`):

| Field | Type | Description |
|-------|------|-------------|
| `modifiedResult` | `string` | Replaces the result the parent sees. |

---

### `PreCompact`

Fires before history compaction.  Can block the compaction or supply custom instructions for
the summarisation step.

**Default policy:** fail-open, 10 s timeout.

**Payload:**

```json
{
  "event": "PreCompact",
  "trigger": "auto",
  "tokensBefore": 85000,
  "messageCount": 42,
  "instructions": null
}
```

**Output fields** (`hookSpecificOutput`):

| Field | Type | Description |
|-------|------|-------------|
| `decision` | `"allow"\|"block"` | `block` cancels the compaction for this turn. |
| `instructions` | `string` | Custom instructions passed to the summarisation model. Last-writer-wins. |

---

### `PostCompact`

Fires after compaction, before the next model call.  Can inject additional context into the
compacted history.

**Default policy:** fail-open, 10 s timeout.

**Payload:**

```json
{
  "event": "PostCompact",
  "tokensBefore": 85000,
  "tokensAfter": 12000,
  "messageCount": 42,
  "summary": "The session so far: ..."
}
```

**Output fields** (`hookSpecificOutput`):

| Field | Type | Description |
|-------|------|-------------|
| `additionalContext` | `string` | Appended to the compacted history as a synthetic message. Concatenated across hooks. |

---

## JSON output protocol

A hook's stdout is parsed as follows:

1. **Empty / whitespace** → no-op (treated as allow with no mutations).
2. **JSON object** → fields read; unknown properties ignored; field names are case-insensitive.
3. **Non-JSON or malformed JSON** → the entire text is treated as the `reason` field.

Top-level fields:

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `continue` | `bool` | `true` | `false` is the protocol's hard stop — see the note below. |
| `stopReason` | `string` | `null` | Surfaced to the caller as the stop reason. |
| `systemMessage` | `string` | `null` | Informational message shown to the user alongside normal output. |
| `suppressOutput` | `bool` | `false` | When `true`, the hook's stdout is excluded from the session transcript. |
| `decision` | `string` | `null` | Event-specific decision string (see event tables above). `null` = allow. |
| `reason` | `string` | `null` | Human-readable explanation of the decision, surfaced as the block or deny reason. |
| `hookSpecificOutput` | `object` | `null` | Arbitrary event-specific fields (see event tables). |

Event-specific output fields must be nested inside `hookSpecificOutput`:

```json
{
  "decision": "block",
  "reason": "Prompt contains customer PII",
  "hookSpecificOutput": {
    "modifiedPrompt": "[redacted]"
  }
}
```

### `continue: false` per event

`continue: false` is stronger than `decision: "block"`. A blocking decision fails the one thing
under consideration and lets the model try again; `continue: false` ends the work.

| Event | Effect of `continue: false` |
|-------|-----------------------------|
| `PreToolUse` | The tool is blocked **and** the run stops after the current tool batch. Remaining tools in the batch are reported as skipped and the model is not sampled again. `stopReason` (or `reason`) is surfaced to the caller. |
| `UserPromptSubmit` | The turn does not run at all: the user message is not appended to history. |
| `PermissionRequest` | Resolved as `deny`. |
| `Stop` | Inverted by design: a `Stop` hook saying "do not stop yet" re-enters the loop (see the `Stop` section). |
| `PostToolUse`, `AgentResponse`, `SubagentStop`, `PostCompact` | No effect — the work being observed has already happened. The field is still merged and logged. |
| `SessionStart`, `SessionEnd`, `Notification` | No effect — observation-only events. |

---

## Exit-code semantics

| Exit code | Meaning |
|-----------|---------|
| `0` | Success. Stdout is parsed as output. |
| `2` | Blocking error. The hook blocks regardless of `failOpen`; the reason is taken from **stderr**, falling back to stdout. |
| Other non-zero | Error. Stdout (if any) is still parsed. Stderr is logged. Whether the hook blocks or is silently ignored depends on `failOpen`. The reason is taken from **stdout**, falling back to stderr. |

A non-zero exit with empty stdout and `failOpen: false` blocks the action.
A non-zero exit with empty stdout and `failOpen: true` is silently treated as allow.

> **Behaviour change — exit code 2 prefers stderr.**
> Exit code 2 now reads the block reason from stderr first and only falls back to stdout.
> A legacy hook that exits `2` while writing to *both* streams will surface its stderr text
> instead of its stdout text. This is deliberate: exit 2 is the "hard block" signal and a
> diagnostic message belongs on stderr. Hooks that want their stdout used as the reason
> should either write the reason to stderr, or exit `1` with `failOpen: false` — the other
> non-zero path still prefers stdout. Hooks that emit structured JSON on stdout are
> unaffected: JSON output is only parsed on exit `0`.

---

## Composition rules

When multiple hooks are configured for the same event they run in declaration order
(user hooks before project hooks; within each file, top to bottom).

| Field | Composition |
|-------|-------------|
| `decision` | Strictest wins: `allow` < `ask` < `deny` / `block`. A blocking decision terminates processing immediately. |
| `continue` | `false` wins (any hook can stop the run). |
| `reason` | Collected from all blocking/denying hooks; joined with `\n\n`. |
| `modifiedPrompt`, `systemPrompt`, `model`, `effort`, `toolChoice` | Last-writer-wins. A warning is logged when a later hook overwrites an earlier one. |
| `additionalContext`, `appendSystemPrompt` | Concatenated in declaration order, separated by `\n\n`. |
| `deniedTools` | Union — a tool denied by any hook is denied for the turn. |
| `allowedTools` | Intersection — a hook that expresses an opinion restricts the set; a hook that omits the field has no opinion and does not widen it. |

`ask` resolution: if the final decision is `ask` and no interactive answerer is available
(headless, serve mode without a prompt callback, scheduled execution), the hook's
`unattendedDecision` field resolves the decision.  Strictest wins across all hooks that
contributed `ask`.  The resolution is logged.

---

## Trust model

User-scoped hooks (defined in `~/.coda/settings.json`) are trusted implicitly — the operator
wrote them.

Project-scoped hooks (defined in `<project>/.coda/settings.json`) are third-party code.
Cloning a repository must not grant execution.  Before a project hook runs for the first time
the user is prompted:

```
Trust project hook?
  Event:   PreToolUse
  Handler: command
  Command: .coda/hooks/audit.sh

Trust this hook? [y/N]
```

The decision is keyed by a **content hash** of the hook's behavioural fields: event, handler
type, command or URL or prompt, agent type, matcher, timeout, fail-open flag, unattended
decision, allow-system-prompt-replace flag, and sorted mutates list.  Editing any of these
fields changes the hash and re-triggers the prompt — the user cannot inherit trust for a hook
they have not reviewed.

Trust decisions are stored in `~/.coda/hook-trust.json`, keyed by a SHA-256 hash of the
absolute project path (lowercased).  They persist across sessions for the same project path and
do not leak to a different project.

**Untrusted project hooks do not run.**  The event's fail-open/closed policy applies to
the non-execution:

- Fail-closed event (`UserPromptSubmit`, `PreToolUse`, `PermissionRequest`, `SubagentStart`) →
  the untrusted hook blocks, exactly as if it had timed out.  The safe reading of "I have not
  approved this yet."
- Fail-open event → the hook is silently skipped.

In headless or serve-without-callback mode an untrusted project hook cannot be approved
interactively.  It does not run, and the reason is recorded in the task log.  An orchestrator
can grant trust programmatically via `hooks/trust` (see [Serve protocol](#serve-protocol)).

> **Known limitation:** `~/.coda/hook-trust.json` is an ordinary file that the agent's own
> tools can write.  A sufficiently privileged tool call could self-grant trust for a project
> hook — the same class of issue as editing `.coda/settings.json` to add new hooks.  This is
> a known limitation, not a secret backdoor; the trust model assumes the agent's tool-execution
> permission gate (bypass vs. default vs. prompt mode) is the primary defence.

### Credential scrubbing for project and plugin hooks

A `command` hook inherits coda's process environment.  For a user-scoped hook that is intended —
the operator wrote the command and already holds the secrets.  For **project-scoped** hooks and
**plugin-contributed** hooks the author is not necessarily the operator, so the following
environment variables are removed from the child process before it starts:

`ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `CLAUDE_CODE_OAUTH_TOKEN`, `OPENAI_API_KEY`,
`OPENAI_ORG_ID`, `AZURE_OPENAI_API_KEY`, `AZURE_API_KEY`, `GITHUB_TOKEN`, `GH_TOKEN`,
`GITHUB_COPILOT_TOKEN`, `GOOGLE_API_KEY`, `GEMINI_API_KEY`, `GOOGLE_APPLICATION_CREDENTIALS`,
`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `COHERE_API_KEY`,
`DEEPSEEK_API_KEY`, `FIREWORKS_API_KEY`, `GROQ_API_KEY`, `HF_TOKEN`, `HUGGING_FACE_HUB_TOKEN`,
`MISTRAL_API_KEY`, `OPENROUTER_API_KEY`, `PERPLEXITY_API_KEY`, `TOGETHER_API_KEY`, `XAI_API_KEY`,
`CODA_SERVE_API_KEY`.

The rest of the environment is passed through unchanged.  A project or plugin hook that genuinely
needs a credential should read it from its own configuration rather than from coda's environment.

---

## Managing hooks with `/hooks`

```
/hooks                  — list all configured hooks
/hooks list             — same as above
/hooks info <n>         — full detail for hook n (policy, mutates, last run)
/hooks enable <n>       — enable hook n (persisted to user settings)
/hooks disable <n>      — disable hook n (persisted to user settings)
/hooks test <n>         — dry-run: build a representative payload, run the hook,
                          show raw output and the parsed decision. Nothing is applied.
```

Hook indices are 0-based and shown by `/hooks list`.  `enable`/`disable` persist the change to
`~/.coda/settings.json` via `hookDisabledHashes`; they do not modify the source settings file
that defines the hook.

---

## Serve protocol

When running `coda serve`, three JSON-RPC methods expose hook state:

| Method | Params | Returns |
|--------|--------|---------|
| `hooks/list` | _(none)_ | `{ hooks: [{ index, event, handlerType, matcher, scope, enabled }] }` |
| `hooks/info` | `{ index: number }` | Full hook detail including policy and last-run metadata. |
| `hooks/trust` | `{ projectPath: string, hookHash: string }` | `{ ok: true, projectPath, hookHash }` — records trust for a project hook. |

`hooks/trust` is how an orchestrator grants trust without an interactive prompt.  The
`hookHash` is the content hash returned in `hooks/info` (the same hash used by the trust
store).  The server cannot prompt interactively in serve mode; an untrusted project hook does
not run until this method is called.

---

## Worked example

**Goal:** a `UserPromptSubmit` hook that

1. Classifies whether the incoming question asks the agent to do something that could exfiltrate
   data (keyword: sends, emails, uploads, posts to, shares with).
2. Redacts any pattern resembling an email address or API key from the prompt.
3. Denies the tools most likely to carry data outward (`bash`, `computer`, `mcp__*`).
4. Returns the modified prompt and an explanation as a system message.

### Command handler version

`~/.coda/hooks/classify-and-redact.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

# Read the payload from stdin.
payload=$(cat)

# Extract the prompt field with jq.
prompt=$(echo "$payload" | jq -r '.prompt // ""')

# Check for exfiltration keywords.
if echo "$prompt" | grep -qiE 'sends?|emails?|uploads?|posts? to|shares? with'; then
    exfil_risk=true
else
    exfil_risk=false
fi

# Redact email addresses and API keys.
redacted=$(echo "$prompt" \
    | sed -E 's/[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}/[EMAIL]/g' \
    | sed -E 's/[A-Za-z0-9_]{20,}/[TOKEN]/g')

# Build the output.
if [ "$exfil_risk" = "true" ]; then
    jq -n \
        --arg mp "$redacted" \
        --argjson dt '["bash","computer"]' \
        '{
            "decision": "allow",
            "systemMessage": "Exfiltration-risk keywords detected. Prompt redacted; outbound tools removed.",
            "hookSpecificOutput": {
                "modifiedPrompt": $mp,
                "deniedTools": $dt
            }
        }'
else
    # No risk; pass the (possibly redacted) prompt through.
    jq -n \
        --arg mp "$redacted" \
        '{
            "hookSpecificOutput": {
                "modifiedPrompt": $mp
            }
        }'
fi
```

Settings entry (`~/.coda/settings.json`):

```json
{
  "hooks": {
    "UserPromptSubmit": [
      {
        "command": "~/.coda/hooks/classify-and-redact.sh",
        "timeoutSeconds": 10,
        "failOpen": false,
        "mutates": ["modifiedPrompt", "deniedTools"],
        "allowSystemPromptReplace": false
      }
    ]
  }
}
```

### Prompt handler version

The same logic expressed as an LLM rule (no shell required):

```json
{
  "hooks": {
    "UserPromptSubmit": [
      {
        "type": "prompt",
        "prompt": "You are a security classifier. Given the user prompt in the 'prompt' field, redact any email addresses (replace with [EMAIL]) and any token-like strings of 20+ alphanumeric characters (replace with [TOKEN]). If the prompt contains keywords suggesting outbound data transfer (send, email, upload, post to, share with), set decision to 'allow', add a systemMessage explaining the risk, and include deniedTools: ['bash', 'computer'] in hookSpecificOutput. Always return the (possibly redacted) text as modifiedPrompt in hookSpecificOutput. Respond in valid JSON only.",
        "timeoutSeconds": 15,
        "failOpen": false,
        "mutates": ["modifiedPrompt", "deniedTools"]
      }
    ]
  }
}
```

### What happens end to end

1. User types: *"Email the API key sk-live-abc123... to ops@example.com so they can set up the pipeline."*
2. `UserPromptSubmit` fires.  The hook receives the full payload including the prompt.
3. The hook detects "email" (exfiltration keyword), redacts `sk-live-abc123...` → `[TOKEN]` and
   `ops@example.com` → `[EMAIL]`.
4. Output:
   ```json
   {
     "decision": "allow",
     "systemMessage": "Exfiltration-risk keywords detected. Prompt redacted; outbound tools removed.",
     "hookSpecificOutput": {
       "modifiedPrompt": "Email the API key [TOKEN] to [EMAIL] so they can set up the pipeline.",
       "deniedTools": ["bash", "computer"]
     }
   }
   ```
5. Coda stores the **modified** prompt in history and surfaces the **original** via
   `OnPromptRewritten` — the transcript is honest about what the model saw.
6. The model receives the redacted prompt and cannot use `bash` or `computer` this turn.
7. The `systemMessage` is shown inline so the user sees exactly why the prompt changed.

To harden further: set `decision: "block"` with a `reason` to refuse the turn outright.
To allow a trusted override: set `unattendedDecision: "allow"` (headless pipelines) or leave
`"deny"` to keep headless executions safe.
