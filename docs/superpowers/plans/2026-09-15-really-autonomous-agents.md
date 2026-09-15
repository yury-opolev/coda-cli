# Really autonomous agents — implementation plan

**Date:** 2026-09-15
**Spec:** `docs/superpowers/specs/2026-09-15-really-autonomous-agents-design.md`
**Branch:** `feat/in-memory-automation`

Seven phases, executed serially. Each phase is independently testable, ends
green, and is committed and pushed before the next begins. A review pass runs
after every phase; critical and important findings are fixed before moving on,
minor and low are deferred to the final phase.

| # | Phase | Depends on | Ends when |
|---|---|---|---|
| 1 | Module move + `AutonomySupervisor` facade | — | Existing goal tests pass unchanged under new names |
| 2 | `AssumptionLedger` | 1 | Entries, exhaustion rule and redaction tested |
| 3 | `StuckDetector` | 1 | Each heuristic fires at its threshold, not before |
| 4 | `ProxyAnswerer` | 2 | `ask_user_question` never blocks under a goal |
| 5 | `PermissionResolver` + `RecoveryGuard` | 2 | Each mode parks the expected classes |
| 6 | Terminal-state proof in `decide_stop` | 3, 4, 5 | `GenuinelyBlocked` / `Stalled` provable |
| 7 | End-of-run report + wiring | 6 | States serialise; docs match implementation |

## Phase detail

**1 — Module move + facade.** `coda-agent/src/goal/` becomes `autonomy/`.
`GoalSupervisor` becomes `AutonomySupervisor`, `Arc`-shared, with `Mutex` around
seam-facing state and plain fields for loop-owned budget and outcome. Pure
refactor: no behaviour change, no new tests, every existing goal test passes
under the new names.

**2 — `AssumptionLedger`.** `autonomy/ledger.rs`. `LedgerEntry` and
`BlockerKind` as specified. Enforces the exhaustion rule — parking with an empty
`tried` list is rejected. All entries pass through
`streaming_secret_redactor` before being recorded. Lives in session state.

**3 — `StuckDetector`.** `autonomy/stuck.rs`. Five heuristics with thresholds
4 / >3 / 3 / 6, semantic equality over tool name, arguments and thought, ignoring
call ids, response ids and timestamps. One-shot corrective nudge on first trip.

**4 — `ProxyAnswerer`.** `autonomy/answerer.rs`. Decorates `UserQuestion` only
when a goal is active. Always returns a choice; low confidence selects the most
reversible option and flags the ledger entry. Answerer failure parks the
question — it never fabricates a choice, preserving the existing
"a fault is not an answer" invariant.

**5 — `PermissionResolver` + `RecoveryGuard`.** `autonomy/permission.rs`
resolves from the active `PermissionMode`, reusing the existing
`ToolActionClassifierPrompt`; its `ASK` verdict becomes deny-and-park rather
than prompt-a-human. `autonomy/recovery.rs` manufactures an undo before the
unrecoverable set and proceeds even when the undo fails.

**6 — Terminal-state proof.** `decide_stop` gains the `GenuinelyBlocked` and
`Stalled` rungs, progress-signal tracking, and the N=3 no-progress window. The
`todo_write` list is the work-item registry.

**7 — Report + wiring.** Ledger summary in `goalStatus` and end-of-run output;
new terminal states plumbed through `coda-proto`, `coda-serve` and `coda-tui`;
`docs/serve-protocol.md` updated.

## Final phase

After phase 7: full workspace test suite, full review plus security review, fix
everything found together with the deferred minor and low findings, commit and
push, open a pull request and complete it, then pull `main`, build all, and
install the latest `coda` locally.
