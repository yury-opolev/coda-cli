# Proposal: Subagent orchestration — per-subagent models, wide fan-out, orchestrator-only main agent

- **Date:** 2026-08-04
- **Status:** Draft — reviewed 2026-08-04; awaiting decisions on §7 (D1–D5).
- **Author:** Yury Opolev (design exploration, Coda)
- **Scope:** `Coda.Agent` (`SubagentHost`, `SubagentDefinition`, `SubagentRequest`, `TaskTool`,
  `BackgroundTaskStartTool`, `TaskManager`, `AgentLoop` injection seam, `SubagentSettings`,
  `CodaSettings`/`SettingsLoader`), `Coda.Sdk` (`TurnPipelineBuilder`, serve events),
  `Coda.Tui` (task browser columns).
- **Verified against:** `main` @ `44cc369`.

## 1. Summary

Coda can delegate work to subagents, but it cannot *orchestrate* them. Four gaps block the
"main agent as a pure orchestrator" workflow:

1. **Every subagent runs the session's model.** There is no way to run an Opus main agent that
   dispatches Haiku workers, even though the wire plumbing to do so already exists.
2. **Fan-out defaults to 8.** Raising it to 20 is nearly free, but the interaction with sequential
   tool dispatch has to be understood: `task_start` scales, the foreground `task` tool does not.
3. **The main agent's toolset cannot be restricted.** There is no supported way to say "you may
   only delegate; you may not read, write, or run commands yourself."
4. **Background completions are poll-only** — and failures are worse than poll-only: a background
   subagent that *fails* or is *stopped* fires nothing at all today.

This proposal addresses all four. (1) and (3) are small, contained changes on existing seams;
(2) is a default bump plus a documented limitation; (4) needs a new injection seam alongside the
three that already exist in `AgentLoop`.

## 2. Current state (verified on `44cc369`)

| Concern | Today | File |
|---|---|---|
| Subagent model | Always the session model — `SubagentHost` holds one injected `ILlmClient` and reuses `baseOptions` verbatim | `src/Coda.Agent/SubagentHost.cs:14,20,264` |
| Model on a definition | Not present — `SubagentDefinition` is `(Type, Description, SystemPromptBody, ReadOnlyToolsOnly)` | `src/Coda.Agent/Subagents/SubagentDefinition.cs:7-11` |
| Model on a launch | Not present — neither `task` nor `task_start` accepts a model | `Tools/TaskTool.cs:24-26`, `Tools/BackgroundTaskStartTool.cs:20-22` |
| Per-turn model override | **Exists**: `TurnShape.Model` → `TurnShapeResolver` → `ChatRequest.Model` | `TurnShape.cs:80`, `TurnShapeResolver.cs:222`, `AgentLoop.cs:457` |
| Session model plumbing | `AgentOptions.Model` is the resolver's `sessionModel` input | `AgentOptions.cs:8`, `TurnShapeResolver.cs:222` |
| Fan-out limit | `SubagentSettings.MaxConcurrent` default **8**, clamp `[1, 64]`; enforced by a session-wide `SemaphoreSlim` | `Settings/SubagentSettings.cs:19,26,42`, `Tasks/TaskManager.cs:86,151` |
| Depth limit | `MaxDepth` default 2, clamp `[1, 10]` | `Settings/SubagentSettings.cs:16,25` |
| Settings surface | `subagents: { maxDepth, maxConcurrent, allowSystemPromptReplacement }`; project may raise limits, `allowSystemPromptReplacement` is user-file-only | `Settings/CodaSettings.cs:107-140`, `SettingsLoader.cs:624-656` |
| **Tool-call execution** | **Strictly sequential** — `for (var i = 0; i < toolUses.Count; i++)` with `await` inside; no `Task.WhenAll` over tool calls | `AgentLoop.cs:986-1090` |
| Foreground `task` | Blocks the parent turn until the subagent returns its report | `Tools/TaskTool.cs:70-91` |
| Background `task_start` | Returns a task id immediately; slot handed to the run | `Tools/BackgroundTaskStartTool.cs:66-84` |
| Main-agent tool restriction | None from settings. `TurnShape.AllowedTools`/`DeniedTools` exist but are hook/skill-driven | `TurnShape.cs:52,57`, `TurnShapeResolver.cs:232-274` |
| Restriction propagation | `ToToolRestrictionShape()` forwards the parent's resolved allowlist to **children** — and, when only a deny list was active, forwards a **deny-only** shape via `DeniedOnlyInput` | `TurnShapeResolver.cs:85-101,270-275`, `AgentLoop.cs:976` |
| Tool registries | Four, each built independently from `BuiltInTools.All()` + `options.ExtraTools` | main `BuildParentTools` `TurnPipelineBuilder.cs:567`; scheduled root `BuildScheduledTools` `:382`; subagent host `BuildSubagentHost` `:501`; scheduled-root's subagent host `:335` |
| Completion → model | **None.** Terminal background tasks fire `Notification("task-complete")` *user hooks* only (external processes) — and **only from `Complete`**; `Fail` and `Stop` fire nothing | `Tasks/TaskManager.cs:132,554-586` |
| Per-tool wall-clock ceiling | 30 minutes for **any single tool call** (`CODA_TOOL_MAX_SECONDS` overrides) | `AgentLoop.cs:78-90` |
| Plugin agent definitions | Frontmatter keys outside the four `SubagentDefinition` fields land in `UnknownFields` and are **never read** — a stated security guarantee | `Coda.Tui/Plugins/PluginAgentLoader.cs:65-70` |
| Completion → agent | Pull only: `task_wait` (blocks ≤30 min), `task_output`, `task_get`, `task_peek`, `task_list` | `Tools/TaskWaitTool.cs:17,21` |
| Parent → subagent messages | `task_send` — one-way steering to a *running* task; reply only via output stream / final report; subagent reads with `task_recall` | `Tools/TaskSendTool.cs:16,49-56`, `Tools/TaskRecallTool.cs:10` |
| `AgentLoop` injection seams | Three, all at the iteration boundary: LSP diagnostics, steering inbox, deferred-tools reminder | `AgentLoop.cs:396-441` |
| Serve task surface | **None** — `ServeMethods` has no task/subagent method or event | `src/Coda.Sdk/Serve/ServeMethods.cs` |

Three facts drive the whole design:

- **`ChatRequest.Model` is already per-turn**, so same-provider model selection needs no new client
  plumbing — only a resolved value in `AgentOptions.Model` for the child loop.
- **Tool calls are sequential** — but `task_start` returns immediately, so 20 sequential
  `task_start` calls still yield 20 *concurrently running* subagents. Only the **foreground** `task`
  tool is serialised by this. See §4.
- **The four tool registries are built separately**, so a main-agent-only restriction can be
  enforced by construction rather than by a propagating `TurnShape` — which would otherwise strip
  every tool from every subagent.

## 3. Feature 1 — per-subagent model (same provider)

**Decision (agreed):** same-provider only. A subagent may name any model the session's provider
serves. Cross-provider selection is explicitly out of scope; §8 keeps the seam open.

### 3.1 Surface

`task` and `task_start` gain an optional `model` property:

```json
{"model": {"type": "string",
  "description": "Model id for this subagent, from the session's provider. Omit to inherit the session model."}}
```

`SubagentDefinition` gains `Model` (nullable; `null` = inherit), so a plugin-contributed agent type
can declare its own default — e.g. a `explore` definition pinned to a fast model.

Settings gain two operator controls under the existing `subagents` block, **read from the user
settings file only** (see §3.3):

```jsonc
{
  "subagents": {
    "model": "claude-haiku-4-5",              // default for every subagent
    "modelByType": { "explore": "claude-haiku-4-5", "reviewer": "claude-opus-4-8" }
  }
}
```

### 3.2 Resolution order

Resolved once in `SubagentHost.RunSubagentAsync`, first non-empty wins:

1. `request.Model` — explicit `model` argument on `task`/`task_start`
2. `settings.Subagents.ModelByType[subagentType]` — operator, per type
3. `settings.Subagents.Model` — operator, global subagent default
4. `definition.Model` — plugin-declared default for that type, **user-scoped plugins only**
5. `baseOptions.Model` — the session model (today's behaviour)

The operator's settings outrank a plugin's declared default at *every* level: a plugin may only
supply a model when the operator has expressed no opinion at all. The explicit tool argument still
wins so the main agent can decide per task.

### 3.3 Plugin- and project-supplied models are a cost-escalation vector

`PluginAgentLoader.cs:65-70` states as a security guarantee that `SubagentDefinition` carries only
`Type`, `Description`, `SystemPromptBody`, `ReadOnlyToolsOnly`, and that every other frontmatter key
"lands in `UnknownFields` and is never read". Adding `Model` **retires that guarantee**, and
`model` is not currently in the loader's `ForbiddenStripped` set (`PluginAgentLoader.cs:18-22`).

The threat is not a typo — it is a hostile default. A cloned repo shipping a plugin agent named
`explore` pinned to the most expensive model in the provider's catalogue would silently spend the
operator's money on every research subagent. The same argument the codebase already makes for
`AllowSystemPromptReplacement` (`SubagentSettings.cs:52-59`: "a project settings file is
attacker-controlled the moment someone clones a hostile repo") applies to model choice, because
cost *is* operator-facing privilege.

Therefore:

- `definition.Model` is honoured **only for user-scoped plugins** (`~/.coda`). A project-scoped
  plugin declaring `model:` has it ignored, with a warning naming the plugin and file.
- `subagents.model` and `subagents.modelByType` are **user-settings-file only**, exactly like
  `AllowSystemPromptReplacement`. A project settings file declaring them is ignored with a warning.
- The guarantee comment at `PluginAgentLoader.cs:65-70` is rewritten to state the new, narrower
  invariant rather than left to rot.

This costs nothing in the intended workflow — an operator configuring cheap workers edits their own
settings — and closes the escalation path outright.

### 3.4 Implementation

- `SubagentRequest` gains `string? Model { get; init; }` (`Subagents/SubagentRequest.cs`).
- `SubagentHost` builds the child options with the resolved value:
  ```csharp
  var options = this.baseOptions with
  {
      SystemPrompt = systemPrompt,
      Model = resolvedModel,                       // NEW
      MaxIterations = Math.Min(this.baseOptions.MaxIterations, 500),
  };
  ```
  (`SubagentHost.cs:264-269`). Everything downstream — `TurnShapeResolver`'s `sessionModel`,
  `ChatRequest.Model`, the effort clamp in the wire client — already keys off this value.
- `TaskTool`/`BackgroundTaskStartTool` read `model` via `ToolInput.GetString` and pass it through
  `RunSubagentForegroundAsync`/`StartSubagentBackground` into `SubagentRequest`.
- `SubagentSettings` gains `Model` and `ModelByType`, read from the user file only (§3.3);
  `SubagentOverrides` merges them per field.
- `ManagedTask` records the resolved model so `task_list`, `task_get` and the TUI task browser can
  show which model each subagent is running.

### 3.5 Validation

**None, deliberately.** Coda has no handcrafted model list to validate against (the models.dev
catalog lives in `Coda.Sdk`, which `Coda.Agent` does not reference), and a hardcoded list is exactly
what previous work removed. An unknown model id surfaces as the provider's own error, returned to
the parent as a normal `IsError` tool result the model can correct. Values are trimmed; empty or
whitespace is treated as absent (inherit), and terminal control characters are stripped before the
id reaches a log or the TUI.

Two failure shapes to expect and document:

- *Typo.* Burns a subagent slot, the `SubagentStart` hook, and one provider round trip before
  failing. Accepted; §8 lists an optional validator seam as a follow-up.
- *Effort incompatibility.* `includeAnthropicSystemPrefix` and the session effort are captured at
  host construction, not re-derived per model (`SubagentHost.cs:264`). A resolved model whose family
  rejects the session's effort level fails at the provider, and the error reads "explore subagent
  failed" rather than naming the real cause. The message must be surfaced verbatim so the cause is
  at least visible.

## 4. Feature 2 — fan-out of 20

### 4.1 The setting

`SubagentSettings.maxConcurrent` default **8 → 20**. `MaxAllowedConcurrent` stays 64, so an operator
can still raise it. One-line change plus tests and docs.

### 4.2 What the setting does and does not buy

`AgentLoop` executes the tool calls of one assistant message **one at a time** (`AgentLoop.cs:986`,
`await` inside the loop). That matters differently for the two launch tools:

- **`task_start` is unaffected.** It registers the run and returns immediately
  (`BackgroundTaskStartTool.cs:66-84`), so 20 sequential `task_start` calls in a single assistant
  message produce **20 concurrently running subagents**. The user goal — "spawn 20 subagents" — is
  met by the setting bump alone.
- **The foreground `task` tool is fully serialised.** It blocks until its subagent finishes
  (`TaskTool.cs:70-91`), so 20 `task` calls run 20 subagents *one after another*, never using more
  than one slot.

So the setting is sufficient for the stated goal, provided the orchestrator uses `task_start`. What
it does not fix is *ergonomics*: fire-and-forget launches must be reconciled later, which is exactly
what Feature 4's completion push exists to make reliable.

### 4.3 Should there also be a `task_many`?

A `task_many` tool — one call carrying an array of launches, run concurrently via `Task.WhenAll`
inside the tool, returning all reports together — is the obvious ergonomic upgrade. On inspection it
carries two hazards serious enough that it should **not** ship in this proposal:

- **The 30-minute per-tool ceiling applies to the whole batch.** `AgentLoop.cs:78-90` caps *any*
  single tool call at 30 minutes. Because `task_many` is one call awaiting the slowest leaf, a batch
  of 20 in which one worker stalls is cancelled *in its entirety* — every other worker's output lost
  mid-flight. The "spawn 20 and collect" model silently assumes short workers, which is false for
  `general-purpose` subagents that write files and run commands.
- **It breaks the sink and permission-prompt concurrency contract.** Today, foreground subagents run
  strictly sequentially and background subagents route their events to `NullAgentSink.Instance`
  (`TaskManager.Subagents.cs:126`), so the parent sink has never had concurrent writers.
  `task_many` would hand 20 foreground subagents the same parent sink at once. `TuiAgentSink` keeps
  unguarded instance-level burst state (`currentBurstStartTick`, `currentBurstStartedAt`), which
  concurrent `OnThinking`/`OnThinkingComplete` calls would corrupt. `IPermissionPrompt` is likewise
  shared and realistically modal.

Both are fixable — partial-result batching for the first, a serialising sink adapter or per-task
rings for the second — but neither is small, and neither is needed to reach the stated goal.
**Recommendation: ship the setting bump and rely on `task_start` + completion push;** revisit
`task_many` once the push channel is proven. See decision **D1**.

### 4.4 Risks of 20-wide fan-out

- **Provider rate limits.** 20 concurrent streams on one credential will hit 429s on most plans.
  The existing retry policy absorbs transient failures, but a burst of 20 is a new operating point.
- **Cost.** 20 workers on a large model is expensive; Feature 1's `subagents.model` is the natural
  control (cheap workers, expensive orchestrator).
- **Permission prompts.** 20 subagents can queue 20 interactive prompts. Already possible today at
  8; worth verifying the dialog queue behaves under 20.
- **Slot budget is session-wide, not per depth** (`TaskManager.cs:86`). If the leader dispatches 20
  workers it holds *every* slot, and each worker's own `TryAcquireSubagentSlot()` returns false
  (`TaskTool.cs:60`) — so a wide top-level fan-out silently reduces the effective `MaxDepth` to 1.
  This bites precisely the "orchestrator → planner → implementers" shape the feature is for. Treat
  `MaxConcurrent` as a *fleet* budget and document it; a per-depth reservation is listed in §8.
- **Log/output volume.** Each task keeps a bounded output ring (`outputRingBytes`), so memory is
  bounded, but the TUI task list at 20 rows needs a check.

## 5. Feature 3 — restricting the main agent's tools

### 5.1 Surface

A new top-level settings block, scoped to the **main agent only**. Name-based allow/deny lists,
no presets — the operator states exactly which tools the main agent may use:

```jsonc
{
  "agent": {
    "tools": {
      "allow": ["task", "task_start", "task_output", "task_wait", "task_send",
                "task_list", "task_stop", "todo_write", "ask_user_question"],
      "deny":  ["run_command"]
    }
  }
}
```

- `allow` — when present, the main agent gets **only** these tools. Absent (or null) = no allowlist,
  which is today's behaviour. An empty list is honoured literally and will trip the inert-agent
  guard (§5.4).
- `deny` — removed after the allowlist is applied. **Deny wins**, so a name in both is denied.
- Names match tools by their registered `ITool.Name`, so built-in, MCP and plugin tools are all
  addressable by the same rule.
- Merge across user and project files is **monotonically tightening**, mirroring the hook merge
  rules (`HookBus.cs:1648-1663`): `allow` is intersected, `deny` is unioned. A project file can
  restrict the agent further but can never widen what the user file permitted.

The "orchestrator-only" configuration above is therefore just an *example* the documentation ships,
not a built-in mode. That keeps one mechanism instead of two, and means a curated list can never
drift out of sync with the tool registry as tools are added or renamed.

### 5.2 Implementation — and the trap to avoid

The obvious implementation — express it as a `TurnShape.AllowedTools` — **is wrong**.
`TurnShapeResolver.ToToolRestrictionShape()` forwards the parent's resolved allowlist to every child
(`TurnShapeResolver.cs:85-101`, consumed at `AgentLoop.cs:976`). An orchestrator allowlist of
`task*` would intersect with each subagent's registry and leave every subagent with **zero tools** —
the exact opposite of the intent.

Instead, filter the **main agent's registry at composition time**:

- `TurnPipelineBuilder.BuildParentTools(...)` (`:567`, returns at `:581`) applies the resolved
  allow/deny filter.
- The subagent registry (`BuildSubagentHost`, `:501`), the scheduled-root registry
  (`BuildScheduledTools`, `:382`) and the scheduled-root's own subagent host (`:335`) are built from
  `BuiltInTools.All()` independently and are **left untouched**.

This gives non-propagation *by construction* rather than by a rule someone must remember. It also
composes correctly with the existing hook/skill `TurnShape` machinery, which continues to operate on
whatever registry the main agent was given.

### 5.3 What still propagates, and what is not covered

Composition-time filtering removes the *operator's allow/deny lists* from the propagation path, but
two existing
mechanisms are unchanged and must be documented rather than assumed away:

- **Skill-driven `DeniedTools` still propagate.** `SkillTurnShapeComposer.cs:53` layers a skill's
  `DisallowedTools` onto the turn; `TurnShapeResolver.cs:270-275` then records `DeniedOnlyInput`, and
  `ToToolRestrictionShape()` (`:94-97`) forwards a deny-only shape to every child. An orchestrator
  that invokes a planning skill which denies `run_command` will silently narrow its subagents too.
- **Hook-driven `allowedTools` still propagate** (`HookBus.cs:1648-1667` → `AllowedNames` →
  `ToToolRestrictionShape()`). Independent of the operator's lists, but surprising to an operator who believes
  §5.2 removed propagation entirely.

Two registries are deliberately **not** filtered, and the documentation must say so plainly or an
operator will be caught out:

- **Scheduled roots** (`BuildScheduledTools`, `:382`) keep full tools. `agent.tools` is a
  *main-agent* policy; a schedule that fires still reads and writes files. An operator expecting
  a session-wide policy will be surprised.
- **The hook-free subagent host** used by `AgentHookHandler` (`BuildHookHandlers`, `:531-565`) keeps
  full tools, so hook-spawned agents are unaffected.

### 5.4 Guards

- **Inert-agent guard.** If the resolved main-agent toolset contains no subagent-launching tool
  (`task`, `task_start`), the agent can neither act nor delegate. Refuse the
  configuration at load with a clear error naming the offending file, rather than starting a session
  that silently cannot do anything.
- **MCP and plugin tools.** `options.ExtraTools` are filtered by the same name-based rules, so an
  allowlist excludes MCP tools by omission. An operator wanting a specific MCP tool adds
  it to `tools.allow`.
- **The restriction is not a security boundary.** It shapes what the *main* agent may do; subagents
  keep full tools by design (that is the point). It is a workflow control, not a sandbox — the
  documentation must say so plainly, or an operator will mistake it for one.
- **Permission modes are unaffected.** Filtering removes tools from the registry; it does not
  pre-approve anything.

### 5.5 Interaction with Feature 1

An orchestrator-only main agent is exactly the case where per-subagent models pay off: an expensive
model that only plans and delegates, cheap models doing the mechanical work. The two features are
independent but designed to be used together.

### 5.6 The cost of a reports-only workflow

Removing file tools from the orchestrator is not free, and the proposal should be honest about what
the operator is buying:

- **Everything reaches the orchestrator as text.** Its entire picture of the repository is whatever
  subagents chose to put in their final reports. Anything a worker omitted is invisible.
- **Truncation round trips.** The push channel caps each report (§6). Larger outputs force a
  `task_output` call, spending tokens re-reading text the worker already produced. At 20-wide
  fan-out this is a real cost.
- **No independent verification.** Without `read_file`, the orchestrator cannot check that an
  implementer wrote what was asked; the only answer is to dispatch a reviewer subagent, roughly
  doubling tokens and latency for the verification step.
- **Iterative refinement is quote-based.** To say "change line X to Y" the orchestrator must quote
  content from an earlier report, and the receiving worker must `read_file` to reconcile. Workable,
  but there is no stable "file as of the last edit" reference to point at.

This is the trade being made: a strictly delegating orchestrator in exchange for losing direct
observation. An operator who finds it too tight simply adds `read_file`/`grep` to `agent.tools.allow`
— that is now a configuration change, not a code change.

## 6. Feature 4 — completion push

### 6.1 Today

A background subagent that finishes tells the model **nothing**. `TaskManager` fires
`Notification("task-complete")` *user hooks* — external processes, not the LLM
(`TaskManager.cs:561-565`). The main agent discovers completion only by calling `task_wait`,
`task_output`, `task_get` or `task_list`.

With one or two background tasks that is tolerable. With 20 — and a main agent whose only tools are
task tools — it becomes the dominant failure mode: the orchestrator forgets to poll, or polls in a
tight loop burning tokens.

### 6.2 Design

Add a fourth injection seam to `AgentLoop`, alongside the three that already exist at the iteration
boundary (`AgentLoop.cs:396-441`):

- `TaskManager` keeps a bounded **completion outbox** keyed by owner (`ParentId`, or the main agent
  when null).
- The enqueue point is the **terminal transition itself**, covering `Completed`, `Failed` *and*
  `Stopped`. It must **not** be co-located with the existing notification callback: that callback
  fires only from `Complete` (`TaskManager.cs:554-567`), while `Fail` (`:569-577`) and `Stop`
  (`:579-586`) fire nothing. Wiring the outbox there would make every failed or cancelled worker
  invisible to the orchestrator — strictly worse than the polling it replaces, and precisely the
  case an orchestrator most needs to hear about.
- `AgentLoop` drains the outbox for `this.currentTaskId` before each model call and injects a
  synthetic user message:

  ```
  <task-completed id="tsk_…" status="failed" description="Audit auth module">
  …final report or error, truncated at N chars…
  (truncated — use task_output for the full log)
  </task-completed>
  ```

- **Drain semantics**: delivered exactly once, mirroring `SteeringInbox.TakeAllForDelivery()`.
- **Ordering**: the steering seam drains **first**, then completions. Operator intent outranks
  worker results in the same iteration. Both are plain user messages appended after the previous
  tool-result message and before the next model call, so the Anthropic
  thinking/`tool_use`/`tool_result` ordering constraints (`AgentLoop.cs:620-645`) are untouched.
- **Bounded**: report truncated (proposed 4 000 chars per task) with a cap on tasks per injection;
  overflow is reported as a count plus instructions to use `task_output`, so 20 simultaneous
  completions cannot blow the context window.
- **Ownership**: an agent only ever receives completions for tasks it owns — the same authorization
  rule `task_send`/`task_wait` already enforce (`TaskManager.Subagents.cs:158-176`), so no
  cross-subtree leak.

### 6.3 Orphaned entries

A depth-1 subagent A may `task_start` a depth-2 worker B and then terminate while B is still
running. B's completion would land in an outbox whose owner no longer has a loop to drain it —
a slow leak, and a lost result, in exactly the long-running fan-out this feature targets.

**Rule:** when an owner reaches a terminal state, its pending completions roll up to the nearest
*live* strict ancestor, with the main agent as the ultimate root. The ancestor walk that
`IsAuthorizedCaller` already performs (`TaskManager.Subagents.cs:158-176`) provides the traversal,
so nothing is dropped and nothing accumulates against a dead owner.

### 6.4 Relationship to `task_wait`

If a `task_wait` on task X is in flight when X terminates, the agent would otherwise learn of it
twice: once from the wait's tool result, once from the push. To keep one event to one signal,
a `task_wait` that returns terminal for X **consumes** X's outbox entry for that owner and carries
the report in its own result. The push then covers exactly the tasks nobody was waiting on.

### 6.5 The idle case

The seam fires at *iteration boundaries within a turn*. If the main agent has already ended its turn
and the session is idle, a completion arriving later is delivered at the start of the next turn.

- **(i) Deliver on next turn.** Simple, no autonomy surprises. An orchestrator that wants to stay
  live uses `task_wait`, and in practice will be inside one when the completion lands.
- **(ii) Auto-wake the idle agent** and start a turn carrying the completions. True autonomy, but it
  makes the session start turns nobody asked for — with cost, permission-prompt and TUI-focus
  implications, and a possible wake loop when a woken turn spawns more work.

**Recommendation: (i)**, with (ii) as an explicit opt-in setting later if the workflow demands it.
See decision **D3**.

### 6.6 Serve parity

Per the standing parity rule, add `event/taskCompleted` to `ServeMethods` carrying the same payload.
Serve currently exposes **no** task surface at all — full task RPC parity (list/get/start/stop) is a
larger gap noted in §8, not resolved here.

## 7. Decisions

All five resolved 2026-08-04. D2 and D5 by explicit user choice; D1, D3 and D4 adopt the
recommendation.

- **D1 — parallel fan-out. DECIDED: setting bump only; no `task_many`.** `task_start` × 20 already
  gives true 20-way concurrency (§4.2), and `task_many` collides with the 30-minute per-tool ceiling
  and the sink/permission concurrency contract (§4.3). Revisit once the push channel is proven.
- **D2 — how the main agent is restricted. DECIDED: name-based `allow`/`deny` lists only, no
  presets, main agent only.** Coda ships one mechanism — `agent.tools.allow` and `agent.tools.deny`
  matched against `ITool.Name` — and the "orchestrator-only" setup becomes an example in the docs
  rather than a built-in mode. A curated preset would be a second mechanism to maintain and would
  drift as tools are added or renamed. The restriction never applies to subagents, scheduled roots
  or hook-spawned agents (§5.2, §5.3). §5.6 records what a delegation-only allowlist trades away, so
  an operator choosing one knows the cost up front.
- **D3 — idle wake-up. DECIDED: (i) deliver on the next turn.** No auto-waking of an idle session.
  An orchestrator that wants to stay live uses `task_wait`, and in practice will be inside one when
  a completion lands. An opt-in auto-wake setting remains possible later.
- **D4 — settings namespace. DECIDED: new top-level `agent` block.** It restricts the main agent,
  not subagents; folding it into `subagents` would mislead.
- **D5 — ask a running subagent a question. DECIDED: deferred.** `task_send` stays one-way for this
  iteration; the orchestrator spawns a fresh worker rather than interrogating a running one. A
  request/response round trip is a separate design (§8) — deferred deliberately, not by default,
  and expected to become the sharpest edge of the orchestrator workflow.

## 8. Out of scope / follow-ups

- **Cross-provider subagent models** — would need `ILlmClientFactory`, credentials and an
  `HttpClient` threaded into `SubagentHost` with per-provider client caching, plus a per-provider
  `includeAnthropicSystemPrefix` decision and effort-clamp revalidation. The resolution seam in §3.2
  is deliberately a single `resolvedModel` string so a `(providerId, model)` pair can replace it
  without touching the callers.
- **`task_many`** — viable once (a) it returns partial results plus a residual handle so the outer
  call finishes well inside the 30-minute ceiling, and (b) the parent sink is either serialised by
  an adapter or bypassed in favour of per-task rings (§4.3).
- **Per-depth slot reservation** — so a wide leader fan-out cannot starve deeper levels (§4.4).
- **Model-id validation seam** — an optional injected predicate on `SubagentHost`, wired from the
  models.dev catalog in `Coda.Sdk`, to fail a bad model id before the slot and hooks are spent.
- **Request/response channel to a running subagent** — see D5.
- **Parallel tool execution in `AgentLoop`** — the general fix for foreground `task`; it disturbs
  steering interleave (`AgentLoop.cs:990-1005`), hook-abort skip semantics (`:1064`),
  permission-prompt ordering and sink event ordering, all of which currently rely on strict
  sequence. Too risky to bundle here.
- **Full serve task RPC parity** — `session/taskList`, `session/taskGet`, `session/taskStop`, etc.
  Serve has no task surface today; only `event/taskCompleted` is added here.
- **TUI orchestration view** — a live "20 workers" dashboard with per-worker model and status is a
  natural companion, and belongs with the separate TUI browser-UX proposal.

## 9. Test strategy

Red/green per behaviour, focused subsets only (`tests/Engine.Tests`, `tests/Coda.Tui.Tests`).

| Area | Cases |
|---|---|
| Model resolution | Each of the 5 precedence levels; blank/whitespace → inherit; control chars stripped; unknown id surfaces provider error as `IsError`; recorded on `ManagedTask` |
| Plugin model trust | User-scoped plugin `model:` honoured; **project-scoped ignored with a warning**; operator `model`/`modelByType` outrank a plugin definition |
| Settings | `model`/`modelByType` parse; **user-file-only — a project file setting them is ignored with a warning**; absent = null |
| Fan-out | Default is 20; clamp `[1,64]` still holds; 21st concurrent launch refused with the existing message; 20 × `task_start` in one message yields 20 running tasks |
| Slot budget | A leader holding all slots causes a worker's `task` launch to be refused (documents the depth interaction) |
| Tool restriction | `allow` filters main registry; **subagent registry unaffected**; deny wins over allow; user∩project intersection; empty allow list honoured literally then caught by the inert-agent guard; unknown tool names in either list are inert; `ExtraTools`/MCP filtered by name |
| Non-propagation | Orchestrator-restricted main agent yields a subagent with a full toolset; scheduled root (`:382`) and hook-free host (`:531`) also unfiltered |
| Known propagation | A skill denying tool X **does** still narrow subagents (documents existing behaviour so a future change is deliberate) |
| Completion push | Enqueued on **Completed, Failed and Stopped** background transitions (not foreground); delivered once; owner-scoped; orphaned entries roll up to the nearest live ancestor; truncation and overflow message; steering drains before completions; `task_wait` consumes the entry it reports |
| Serve | `event/taskCompleted` payload shape |
