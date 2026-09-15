# Really autonomous agents

**Date:** 2026-09-15
**Status:** Approved
**Scope:** `rust/crates/coda-agent`, `rust/crates/coda-serve`, `rust/crates/coda-tool`

## Problem

A goal run stops to ask the operator a question, and if nobody is at the
keyboard the run dies. The operator returns hours later to a session that made
no progress after the first ambiguity.

Three places in the Rust engine park the agent loop on an `await` with no
timeout:

| Site | Mechanism | Outcome when nobody answers |
|---|---|---|
| `tools/ask_user_question.rs` | `ctx.user_question.ask(...)` | `ToolControl::AbortRun{"question.noAnswer.*"}` — run dies |
| `permission/` prompt stack | `request/permission` RPC | waits indefinitely |
| `agent/stop.rs` `GoalVerdict::Escalate` | `UserQuestionPrompt::ask` | headless → `NoController` → `mark_stopped_unmet()` |

An active goal does not currently suppress any of them. `/goal` promises "keep
working until a judge says this is done", and each of these three sites breaks
that promise in a way the operator cannot see until they come back.

The engine already has most of the raw material: `GoalSupervisor` with a
completion judge and budget, `ask_main` as a proven non-blocking question queue,
`notify_user` as a one-way outbox, a live permission-mode state, a safety
classifier, and a `todo_write` work-item list. What is missing is an autonomy
contract that binds them together, and any notion of "stuck".

## Definitions

These are load-bearing and are used precisely throughout.

**Hard blocker.** A state in which the agent genuinely cannot advance the goal
by any available means, *after having tried*. This is a statement about
**capability**, not about permission-to-ask or about risk. A risky action is not
a blocker. An ambiguous requirement is not a blocker. An action the agent is not
permitted to take **is** a blocker, because the agent is genuinely unable to
perform it.

**Recoverable vs unrecoverable.** The axis that governs dangerous actions. Not
"destructive vs safe".

| | Examples | Recovery |
|---|---|---|
| Recoverable | commit, push, deploy, migration, `reset --hard`, rebase, dependency install, CI change | `git revert`, redeploy, rollback, reflog |
| Unrecoverable | force-push over others' history, `git clean -xfd` of uncommitted work, dropping a data store with no backup, publishing a registry version, sending customer email, revoking a shared credential | none |

Commits and deployments are recoverable and receive no special handling.

**Progress signal.** Any of: a todo reaching `done`; a file changing; a new
**distinct** ledger entry.

Deliberately *not* the completion judge's "what remains" prose. That text is
regenerated from the agent's own varying output, so it differs almost every
turn, which reset the quiet counter continually and left both proofs unable to
fire in any realistic run. Nor the *total* ledger count: an agent retrying the
same denied action appends an identical entry every turn, and counting those
would make hitting the same wall repeatedly read as forward motion. A progress
signal that a flailing agent can produce by flailing is not a progress signal.

**No-progress window (`N`).** Three consecutive continuations without a
progress signal. Chosen to match the StuckDetector's own error/monologue
thresholds so the two mechanisms trip on the same timescale.

## Decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | **A goal is the autonomy contract.** Setting a goal enables autonomous behaviour; no goal leaves today's interactive behaviour untouched. | One concept, not two. A goal already promises "keep working until done". |
| 2 | **A blocker blocks a branch, not the run.** Park it, notify, keep working other branches. Terminate only when every open item is parked. | Makes the terminal state provable rather than a judgement call. |
| 3 | **Permission mode is the autonomy envelope.** A goal does *not* imply `bypassPermissions`. | Lets the operator dial autonomy down and still get an honest report instead of a stall. |
| 4 | **An autonomous run never waits for a permission answer.** It resolves the prompt from mode policy: allowed → proceed; not allowed → deny, park, continue. | Removes the second blocking site without widening blast radius. |
| 5 | **Unrecoverable actions are made recoverable, not blocked.** Manufacture an undo, then proceed. | Honours "autonomous means autonomous" without losing work. |
| 6 | **If the undo cannot be created, proceed anyway.** | Safety machinery must never become a blocker. |
| 7 | **Ledger lives in session state.** | Queryable; surfaced in the end-of-run summary. |
| 8 | **Single `AutonomySupervisor` facade**, `Arc`-shared with interior mutability. | One concept to learn. `Arc` + `Mutex` is forced: `UserQuestion` and `PermissionPrompt` are `&self` `Arc` seams while the loop holds `&mut`. |

## Architecture

`coda-agent/src/goal/` becomes `coda-agent/src/autonomy/`. The existing
`GoalSupervisor` is absorbed into `AutonomySupervisor`, which is the single
public facade.

```
autonomy/
  mod.rs         AutonomySupervisor  (facade; Arc-shared)
  completion.rs  "is the goal met?"          <- today's judge.rs, unchanged
  answerer.rs    ProxyAnswerer
  permission.rs  PermissionResolver
  stuck.rs       StuckDetector
  ledger.rs      AssumptionLedger
  recovery.rs    RecoveryGuard
  budget.rs  retry.rs  verdict.rs            <- moved unchanged
```

State is split by access pattern, which is what makes the shared `Arc` honest:

| State | Access | Representation |
|---|---|---|
| budget, completion outcome, escalation flag | `&mut`, loop-only | plain fields |
| ledger, stuck detector | `&self`, from tool threads | `Mutex` |
| goal text, envelope config | read-only | immutable |

The supervisor is consulted at four points:

1. **Every loop iteration** — `stuck.observe(action, observation)`.
2. **`UserQuestion::ask`** — `ProxyAnswerer` decides and logs. Never blocks.
3. **`PermissionPrompt`** — `PermissionResolver` allows or denies from policy.
   `RecoveryGuard` runs first for unrecoverable actions. Never blocks.
4. **`decide_stop`** — completion judge, then the terminal-state proof.

The decorators at seams 2 and 3 are installed **only when a goal is active**, so
interactive behaviour is unchanged by construction rather than by conditional.

## Components

### CompletionJudge

Today's `goal/judge.rs`, behaviour unchanged: replies `DONE` or
`CONTINUE: <remaining>`; only a whole first line of `DONE` completes; failure
fails open to `Continue`.

### ProxyAnswerer

Replaces the block in `ask_user_question`.

- **Input:** question, options, goal text, and the tail of the conversation so
  far. Bounded on both message count and characters, so one question cannot
  turn into an enormous request.
- **Not** project convention files. The Rust engine has no `CODA.md`/`AGENTS.md`
  mechanism, so claiming to feed them would be fiction. If one is added later,
  `ProxyAnswerer::with_conventions` is the seam already waiting for it.
- **Output:** `{ chosen, rationale, confidence }`. It always chooses; there is
  no "I don't know" return value.
- **Matching is exact.** The chosen text is resolved against the offered
  options by exact then case-insensitive comparison, and nothing else. There is
  deliberately no substring or fuzzy fallback: substring matching reads
  `"Do not delete"` as `Delete`, inverting the stand-in's meaning and returning
  it as a confident answer. It also resolves plain hallucinations to whichever
  option shares a few letters — usually the first, the one outcome the question
  seam must never produce by accident. A reply that cannot be matched exactly
  has not chosen, and is parked.
- **Tie-break:** when confidence is low, choose the option that preserves the
  most future choice — that is, the most reversible option. This is the
  mitigation for the documented mis-calibration of simulated users: a
  mis-calibrated proxy does least damage when biased toward reversibility.
- Low confidence never blocks. It flags the ledger entry so the end-of-run
  summary leads with it.
- The agent receives an ordinary successful tool result:
  `"Proceeding on the assumption: <chosen>. Reason: <rationale>."`

### PermissionResolver

Resolves a permission request from the active `PermissionMode`. Never waits.

| Mode | Resolution |
|---|---|
| `bypassPermissions` | allow (then `RecoveryGuard` for unrecoverable) |
| `acceptEdits` | allow reads and edits; deny risky commands |
| `default` | allow safe, reversible, in-project operations only |
| `plan` | allow read-only operations only |

"Risky" is not redefined here. The resolver classifies an action with the
**existing** `ToolActionClassifierPrompt` safety classifier
(`permission/classifier.rs`), whose `ASK` verdict becomes "deny and park" rather
than "prompt a human". Reusing it means the autonomy envelope and the
interactive `yolo-safe` mode agree on what risky means, by construction.

A denial returns a tool **error the agent can route around** — "denied under
mode `default`; this branch is parked" — so the agent tries another path. A
blind retry is caught by the StuckDetector.

### StuckDetector

Heuristics and thresholds as shipped by OpenHands' `StuckDetector`:

| Pattern | Threshold |
|---|---|
| same action → same observation | 4 |
| same action → same error | streak exceeds 3 |
| agent monologue, no tool progress | 3 |
| repeating cycle of 2–4 distinct events | 3 full repetitions |

Equality is **semantic** — tool name, arguments and thought — ignoring call ids,
response ids and timestamps.

The cycle heuristic generalises OpenHands' `[A,B,A,B,A,B]` rule in two ways,
both closing hangs that the literal rule misses and that nothing else would
catch once the budget can be `none`:

- **Period 2 to 4**, not just 2. An agent rotating through three or four tools
  forever trips the original rule not at all.
- **Over events, not actions.** A monologue breaks every action streak, so
  `[A, think, A, think, …]` evades all three action heuristics. Counting
  thinking turns as cycle steps catches it. A period-2 cycle still trips at six
  events exactly as before.

On the first trip of a streak it injects a corrective nudge once
("repeating the exact same call will not work — review the error and either
correct the arguments or try a different approach") and only escalates the
branch to stuck if the streak continues.

A nudge is remembered against the **specific streak** that earned it — the
pattern plus a fingerprint of the offending action — and all grace is restored
once the agent breaks out of every loop. Remembering only the pattern would
mean the second error loop of a long run, on a different tool for a different
reason, got no warning at all and was declared stuck immediately.

### AssumptionLedger

Session-state record, and the structure that makes termination provable.

```rust
enum LedgerEntry {
    Assumption    { question, options, chosen, rationale, confidence },
    ParkedBlocker { kind: BlockerKind, tried: Vec<String>, needs: String },
    Recovery      { action, undo_ref: Option<String>, error: Option<String> },
    Denial        { tool, mode, needed_mode },
}

enum BlockerKind {
    MissingCredential,
    MissingAccess,
    InsufficientPermission,
    ExternalUnavailable,
    Infeasible,
}
```

**Exhaustion rule.** Nothing may become a `ParkedBlocker` with an empty `tried`
list. At least one genuine resolution attempt must be recorded first. This is
what stops "blocked" from becoming the new "asked".

All entries pass through the existing `streaming_secret_redactor` before being
recorded, because the ledger is summarised back to the model and an unredacted
entry would be a leak path.

### RecoveryGuard

For the unrecoverable set only: manufacture an undo (backup ref, stash tag,
snapshot), then run the action. If the undo cannot be created, run the action
anyway and record `Recovery{ undo_ref: None, error: Some(..) }`.

## Data flow

**Question interception:**

```
tool calls ask_user_question
  -> ProxyAnswerer.answer(q, opts, goal, transcript, conventions)
       -> Ledger += Assumption{chosen, rationale, confidence}
       -> ToolResult::ok("Proceeding on the assumption: <chosen>. Reason: <why>.")
```

**Permission interception:**

```
PermissionPrompt.request(tool, input)
  |- mode allows? -- yes -> unrecoverable? -> RecoveryGuard.prepare() -> Allow
  |                -- no  -> Ledger += Denial + ParkedBlocker{InsufficientPermission,
  |                                              tried:[..], needs:"acceptEdits"} -> Deny
```

**Stop decision** — `decide_stop` gains one rung between completion and budget:

```
1. CompletionJudge -- DONE ----------------------> Stop{ Met }
2. progress signal since last stop? -- yes ------> Continue{ nudge }
3. terminal-state proof, once quiet for N turns:
     any blocker parked?
       yes, and every open item parked ---------> Stop{ GenuinelyBlocked, report }
       yes, some open item unparked, looping ---> Stop{ Stalled, report }
       yes, some open item unparked, not looping > Continue{ nudge }
       no, and looping -------------------------> Stop{ Stalled, report }
       no, not looping -------------------------> Continue{ nudge }
4. budget exhausted -----------------------------> Stop{ Unmet }
```

A parked blocker must never disable termination for the rest of the run. An
earlier form of this ladder returned "keep going" whenever *something* was
parked but *something else* was not, on the grounds that a run with real
blockers should report them rather than be dismissed as looping. That confused
the label with the decision: it declined to call the state `Stalled` and then
chose no terminal state at all, so one parked branch made the whole run
immortal — the precise hang this design exists to remove. Which proof fires is
a presentation question; terminating is not optional. The report lists every
parked blocker either way.

`todo_write`'s list is the work-item registry, so "every open item is parked" is
a real query rather than an estimate. When no todos exist, the proof rests on
the progress signal and the StuckDetector alone.

### Terminal states

| State | Meaning |
|---|---|
| `Met` | Completion judge says done. |
| `GenuinelyBlocked` | Provable: every open item parked, no progress. Report lists each blocker, what was tried, and what is needed. |
| `Stalled` | Stuck detector tripped with no parked blockers and no progress. |
| `Unmet` | Budget exhausted (240h / 60000 by default, or `none`). |

None of these is "waiting for a human".

## Error handling

Governing rule: **no failure may reintroduce a block, and no failure may be read
as consent.**

| Failure | Behaviour |
|---|---|
| CompletionJudge unreachable | fail open → `Continue` (today's behaviour, kept) |
| ProxyAnswerer unreachable | park the question as a blocker; return a tool error inviting another branch. **Never fabricate a choice.** |
| RecoveryGuard cannot create an undo | proceed anyway; log the error |
| Ledger mutex poisoned | fatal, consistent with existing `.expect("poisoned")` convention |
| Cancellation | propagates through every seam; the answerer call is cancellable |

The second row preserves the existing security invariant in
`tools/ask_user_question.rs` verbatim:

```rust
// SECURITY: a fault is not an answer. Never the first option,
// never an empty success, never a retry.
```

A broken answerer must not silently become "chose option 1".

**Termination does not depend on the budget.** Default budgets are 240h / 60000
and may be `none`, so they cannot serve as the safety net. The StuckDetector
guarantees termination: no progress, no parked blockers, stuck streak →
`Stalled`.

## Testing

All deterministic and offline, reusing the existing `ForkedAgent` trait and
`ScriptedJudge` fake.

1. **Never blocks.** With every seam faulting — answerer down, permissions
   denying, judge erroring — a goal run still reaches a terminal state.
   Exercised across all `NoAnswerReason` variants.
2. **A fault is never an answer.** No answerer failure path yields
   `chosen == options[0]`.
3. **Terminates without a budget.** Unlimited budget plus a model that loops
   forever still terminates, via the StuckDetector.
4. **Exhaustion rule.** Parking with an empty `tried` list is rejected.
5. **Terminal proof is sound.** `GenuinelyBlocked` is reported only when every
   open todo has a `ParkedBlocker`.
6. **Envelope.** Each permission mode parks exactly the expected action classes.
7. **Regression guard.** With no goal set, every seam behaves identically to
   today; the decorators are not installed.
8. **No secrets in the ledger.**

## Out of scope

- Speculative branching across git worktrees (try both interpretations).
- Changes to the `/goal` slash command so it can set budgets; budgets remain
  settable via CLI flags and `session/setGoal`.
- Any change to interactive, goal-less behaviour.

## Prior art

- **Claude Code `auto` mode** — "a second model, the classifier" approves
  actions instead of the user. The ProxyAnswerer is the question-shaped analogue.
- **OpenHands `StuckDetector`** — the five heuristics and thresholds adopted here.
- **opencode `doom_loop`** — same tool call three times with identical input.
- **Cursor / Codex system prompts** — "State assumptions and continue; don't
  stop for approval unless you're blocked"; "attempt to resolve blockers
  yourself."
- **Devin** — report environment issues and continue via CI rather than stalling.
- **GitHub Copilot coding agent** — writes uncertainty into the PR body instead
  of blocking.
- **"Lost in Simulation" (ACL 2026)** — simulated users are mis-calibrated
  proxies; motivates the reversibility tie-break and the ledger.
