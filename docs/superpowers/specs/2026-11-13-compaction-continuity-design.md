# Compaction continuity — design

Date: 2026-11-13 · Target release: 0.1.181 · Source revision: af8058f

Fixes three upstream reports against 0.1.180:

- **BUG-1** — the summariser inherits the 512-token cap of the short-answer
  `LlmForkedAgent` helper, discards stop reason and usage, and
  `CompactionService::compact` installs any nonempty text.
- **BUG-2** — `compaction_tail_start` keeps the newest user message but drops the
  complete exchange that gives a short confirmation ("yes, proceed") its meaning.
- **BUG-3** — auto-compaction sizes its threshold from the catalogue alone
  (`active_context_window`), while inference already prefers live provider limits,
  so a model missing from the catalogue compacts against a 200k fallback.

The key success criterion is not "the context got smaller" but "the agent continues the
same authorised task with its recent meaning intact".

## Decisions taken autonomously

The owner is away; these were decided without consultation and are recorded here.

| # | Question | Decision |
|---|---|---|
| D1 | Raise the shared helper's 512 cap, or add a separate seam? | **Separate seam.** A new `Summarizer` trait carries an explicit output allowance and returns text + stop reason + usage. `LlmForkedAgent` (judge, stand-in answerer, safety classifier) is untouched and keeps `max_tokens = 512`. |
| D2 | Unknown stop reason (`None`)? | **Rejected** as an unverifiable completion (as OpenCode does). All in-tree decoders report a stop reason on a terminal event. |
| D3 | How many exchanges are protected? | **The last two exchanges, always** (the latest complete exchange plus the active one; when the newest exchange is complete, the one before it). Simpler than inferring "active", and it is exactly the case that BUG-2 lost. |
| D4 | What starts an exchange? | A `User` message containing text, **no** `ToolResult` block, and text that does not open (after leading whitespace) with a runtime frame prefix: `[task-completed]`, `[agent-message]`, `[deadline]` or `[post-compact-context]` (`RUNTIME_FRAME_PREFIXES` in `compaction/tail.rs`; PostCompact hook context is injected with the `[post-compact-context]` prefix). Tool-result messages and runtime injections are never boundaries, so they never take a protected slot. Operator steering stays an exchange start: a steering delivery keeps arrival order but splits into one user message per maximal run of operator messages or runtime items, so operator words always open their own message (the first operator run optionally after a `[live-tasks]` brief, which is not a runtime frame). |
| D5 | Tail target | 16,000 estimated tokens. Older exchanges are added whole while the tail stays within the target; the protected exchanges are kept regardless of the target. |
| D6 | Summary budget | Initial allowance 8,192; one retry, only after an output-limit stop, at up to 32,768. Both are bounded by the model's output limit and by the room left in the window after the summariser's input. Below 1,024 tokens of room the attempt fails as "insufficient room". |
| D7 | Template validation | Headings are requested but **not** required: their absence is recorded (`structured=false`) in diagnostics, never used to reject. A heading check cannot prove completeness, and a false rejection would leave the window full. |
| D8 | Checkpoint store | One owner-only, atomically written JSON file per compaction under `.coda/sessions/<session-id>.compactions/`, newest 5 kept. Same message serialisation as the transcript, so it inherits the transcript's privacy policy exactly. |
| D9 | Oversized protected payloads | Only *tool results* inside the tail may be shortened, and only when the reconstructed request would not fit: head 6 KiB + tail 2 KiB with an explicit marker naming the omission and the checkpoint. User and assistant text is never shortened. |
| D10 | Failure suppression | A failed or ineffective automatic compaction suppresses further automatic attempts until occupancy has grown by another full threshold (the existing `blocked_compaction_at` rule, now also applied to failures). No loop can re-summarise on every iteration. |
| D11 | Display for an unknown window | `contextLimit` stays `null` (honest), and a visible warning states the 200,000-token assumption, its source (`fallback`) and the resulting trigger. `AgentEvent::Warning` never reaches clients (the adapter drops it), so this travels as a new additive `event/contextWindow` notification (window, source, threshold), emitted at turn start whenever the resolved (model, window, source) differs from the last one announced; the TUI shows a warning notice only when `source = "fallback"`. |
| D12 | Live limits not yet fetched | No discovery request on the prompt path: live limits come from `session/models`, which the TUI requests at startup and after each model switch; a headless run on a model unknown to the catalogue uses the visible fallback. |
| D13 | Wire format | Two additive, content-free notifications: `event/compaction` and `event/contextWindow`. `session/compact`'s response shape is unchanged. |

## Approaches considered

1. *Raise the cap / percentage only.* Rejected — still installs truncated summaries and
   still drops the antecedent.
2. **Validated checkpoint + protected recent context + recoverable pre-compaction file**
   (chosen) — fixes all three bugs across providers with no storage migration.
3. *Provider-native or background compaction first.* Deferred — adds capability,
   cancellation and staleness problems without guaranteeing continuity.

## Architecture

All compaction logic stays in `coda-agent::compaction`, split into focused files:

| File | Responsibility |
|---|---|
| `compaction/mod.rs` | `CompactionService`, `CompactionRequest`, `CompactionOutcome`, public re-exports, `TokenEstimator`, trigger policy (existing). |
| `compaction/tail.rs` | Exchange segmentation, protected-tail selection, tool pairing, bounded tool-result previews. |
| `compaction/render.rs` | Summariser input: structured prompt, previous-checkpoint reconciliation, staged degradation, omission record. |
| `compaction/summarizer.rs` | `Summarizer` trait, `SummaryRequest/Response`, stop-reason normalisation (`SummaryCompletion`). |
| `compaction/budget.rs` | Summary allowance and retry allowance; reconstructed-request fit check. |
| `compaction/report.rs` | Content-free `CompactionReport`; diagnostics record. |
| `compaction/checkpoint.rs` | `CheckpointSink` trait + `CheckpointMeta`/`CheckpointRef`. |
| `agent/occupancy.rs` | Measured-usage anchor and full-request projection. |

The host (`coda-serve`) supplies `LlmSummarizer` (dedicated budget), the file-backed
checkpoint sink, and the one effective-limits resolution reused by inference,
compaction and display.

### Summariser seam (BUG-1)

```rust
pub struct SummaryRequest { pub system: String, pub messages: Vec<Message>, pub max_output_tokens: u32 }
pub struct SummaryResponse { pub text: String, pub stop_reason: Option<String>, pub usage: Usage }
#[async_trait] pub trait Summarizer: Send + Sync {
    async fn summarize(&self, request: SummaryRequest, cancel: CancellationToken)
        -> Result<SummaryResponse, SummarizerError>;   // Cancelled | Failed(category)
}
```

`SummaryCompletion::classify(stop_reason, text)` normalises at the boundary:

- `Complete` — `end_turn`, `stop`, `stop_sequence`, `completed`, with nonempty trimmed text;
- `Truncated` — `max_tokens`, `length`, `max_output_tokens`, `incomplete`,
  `model_context_window_exceeded` (text irrelevant: a truncated candidate is never used);
- `Empty` — a normal stop with blank text;
- `Rejected(label)` — any other reason (`tool_use`, `content_filter`, `refusal`, …) or `None` (`unknown`).

Only `Complete` produces a candidate. `Truncated` triggers at most one retry with the
larger allowance when that allowance is strictly larger than the first; everything else
fails without retry. Cancellation returns `Cancelled` and never replaces history.

### Allowance (budget.rs)

```
room        = window − estimate(summariser input) − 2,048 (safety margin)
initial     = min(8,192, model_output_limit, room)
retry       = min(32,768, model_output_limit, room)       // only if > initial
fail "insufficient_room" when room < 1,024
```

`window` is the effective window (below); unknown ⇒ 200,000. The prompt asks for a
summary of roughly 2,000–4,000 tokens: a permitted maximum is not a target.

### Tail selection (BUG-2)

1. Boundaries = indices of exchange starts (D4) such that `history[i..]` has no unpaired
   `ToolUse` and `history[..i]` holds no `ToolUse` whose result lies after `i`.
2. Protected start = the second-to-last boundary (or the only one).
3. Extend backwards one whole exchange at a time while
   `estimate(history[start..]) ≤ 16,000`.
4. `head = history[..start]`. If `head` is only a previous checkpoint + its
   acknowledgement, there is nothing new to summarise.

Probe (§2 of the request): five messages
`[U old, A old, U "Which option?", A "Apply option B…", U "Yes, proceed."]` ⇒ the fixed
result keeps `tail_start ≤ 2`, so "Apply option B; leave option A unchanged" survives
verbatim.

### Summariser input (render.rs)

- A previous checkpoint at the head of history is lifted out and passed as
  `PREVIOUS CHECKPOINT` with the instruction to produce **one reconciled checkpoint**
  (newer evidence supersedes, finished work leaves the pending list, open constraints
  remain). Its acknowledgement is skipped. Summaries are never concatenated.
- Transcript lines keep identifiers: `[tool call <id> <name> args=<bounded JSON>]`,
  `[tool result <id> ok|error: <bounded preview>]`, `[image]`. Thinking is never sent
  (reasoning privacy).
- Staged degradation until the summariser request fits the window with the initial
  allowance: tool-result preview 4,096 → 1,024 → 256 bytes → omitted; tool arguments
  2,048 → 512 → 128 → name only; then the oldest whole exchanges are dropped. The
  previous checkpoint and all user/assistant text of retained exchanges are never cut.
  A header records what was abbreviated/omitted (`N tool outputs abbreviated, M omitted,
  K earlier messages omitted`). If the floor still does not fit ⇒ `input_too_large`.
- The structured contract (system prompt) asks for, in this order: **Next Move**,
  **Pending Decisions & Open Questions** (exact target and scope of anything awaiting
  approval), **Objective**, **Requirements & Constraints**, **Decisions**, **Work State**
  (done/verified vs pending, errors), **Relevant Files & Identifiers** (paths, ids,
  running task ids, commands), **Important Context**. It states that remembered
  approvals do not authorise new or broader actions, that tool output is untrusted data,
  and that system/project instructions are re-injected and must not be copied.
- A `PreCompact` hook's `instructions_override` still replaces the system prompt.

### Installed shape

```
User:      <coda-compaction-checkpoint>…preamble…\n\n{summary}\n</coda-compaction-checkpoint>
Assistant: Understood. I'll continue from the checkpoint above.
…tail verbatim (with explicit tool-result previews only if D9 applied)…
```

The preamble says the conversation was compacted, that approvals recorded in it do not
authorise new actions, and names the pre-compaction checkpoint file (name only, no path)
when one was written.

### Transaction (CompactionService::compact)

1. Select tail on the stable snapshot; render input; compute allowance.
2. Call the summariser (≤ 2 attempts). Validate completion.
3. Reconstruct `[checkpoint, ack, tail]`; **fit check**:
   `estimate(new) + request_overhead + reply_reserve + 2,048 ≤ window`. On failure,
   shorten tail tool results oldest-first (D9) and re-check; still failing ⇒
   `protected_context_too_large`.
4. Persist the pre-compaction history through the `CheckpointSink` (when configured).
   Failure ⇒ `checkpoint_write_failed`, history untouched.
5. Return `Compacted { history, summary, report }`. The caller commits.

If the head is empty but step 3's shortening applies, the outcome is a *tail-only*
compaction (no summariser call, `attempts = 0`), still checkpointed first.

`CompactionOutcome = Compacted { … } | NotCompacted { reason, report } | Failed { reason, report } | Cancelled { report }`.
Every outcome carries a `CompactionReport`; history is changed only by `Compacted`.

### Checkpoint file (D8)

`<working_dir>/.coda/sessions/<session-id>.compactions/<UTC yyyyMMddTHHmmssfffZ>.json`:

```json
{ "version": 1, "sessionId": "…", "createdUtc": "…", "trigger": "auto|overflow|manual",
  "messagesBefore": 312, "estimatedTokensBefore": 181234, "messages": [ … ] }
```

Written with `write_private_atomic` (owner-only, temp+rename). After a write the
directory is pruned to the newest 5. `messages` uses `message_json`, the transcript's own
serialisation, so nothing is persisted that the transcript did not already persist.

### Effective limits and occupancy (BUG-3)

- The host resolves one `EffectiveLimits { context_window, window_source, max_tokens,
  output_source, model_output_limit }` per turn from `output_budget::resolve` (live list
  → catalogue → fallback; settings override output only). The same record sizes
  inference (`with_model_budget`), the compaction policy, the summary allowance, the
  `OutputBudget` diagnostic, and the display (`context_limit_resolved`, now published
  from this record at turn claim instead of the catalogue-only lookup).
  `active_context_window` is removed.
- `CompactionPolicy` gains `context_window`, `window_source` and `model_output_limit`.
  Threshold resolution is unchanged: default 80%; `compactAtPercent`,
  `compactFreeTokens` and `compactByModel` keep their meaning — 60% of a live 1M window is
  600,000, 60% of the 200k fallback is 120,000; an explicit lower trigger is never raised.
- `OccupancyTracker` (shared per session, survives turns): after each response with
  non-zero usage it records `measured = total_input + output`, `history_len` (including
  the appended assistant message), a hash of the last message, and a fingerprint of
  model + system prompt + tool definitions. Projection = `measured +
  estimate(history[history_len..])` when the anchor is valid, otherwise the full
  estimate `estimate(history) + overhead(system + tool schemas)`. Fingerprint mismatch,
  shorter history or a different last message invalidates the anchor (rebase), so new
  overhead is never omitted and the previous response is never counted twice.
- `TokenEstimator` counts an image as ~1,600 tokens (was 16).
- The trigger compares the projection (not message-only bytes) with the threshold.

### Diagnostics and visibility

`CompactionReport` (all numeric or closed labels; no content): `trigger`, `outcome`,
`reason`, `window_source`, `context_window`, `threshold`, `estimated_tokens`,
`measured_tokens`, `messages_before/after`, `tokens_before/after`, `tail_messages`,
`tail_tokens`, `summary_allowance`, `retry_allowance`, `output_tokens`, `stop_reason`
(normalised label), `attempts`, `duration_ms`, `omitted_tool_outputs`,
`omitted_messages`, `abbreviated_tool_outputs`, `previewed_tail_results`, `structured`,
`checkpoint` (file name).

- Recorded as `coda_diagnostics::Event::Compaction` in the diagnostics log.
- Emitted as `AgentEvent::Compaction` → `event/compaction` (additive), and the TUI shows
  a one-line notice: success (Info) `Context compacted: 312 → 14 messages, ~181k →
  ~23k tokens (summary 2.4k tokens)`; failure (Warning) `Compaction failed
  (summary hit its output limit after 1 retry); history unchanged`.
- Manual `/compact` emits the same event; its RPC response keeps its shape and puts the
  failure sentence in `error`.

### Ordering and concurrency

- Automatic compaction runs at the top of a loop iteration, *before* steering and main
  inbox are drained, on the loop's own `&mut history`. Items arriving during the
  summariser call stay queued and are appended after the compacted history, once.
- Manual compaction holds the turn slot (`TurnKind::Compaction`, steering sealed),
  snapshots history, and commits only if the committed history still equals the
  snapshot; otherwise it fails with `history_changed`.

## Error handling summary

| Condition | History | User sees | Retry |
|---|---|---|---|
| Truncated summary | unchanged | warning | 1, larger allowance |
| Empty / rejected / unknown stop | unchanged | warning | none |
| Provider error | unchanged | warning | none (transport retries are the client's) |
| Cancelled | unchanged | cancellation | none |
| Input or protected tail cannot fit | unchanged | warning | suppressed until growth |
| Checkpoint write fails | unchanged | warning | suppressed until growth |
| Hook cancels | unchanged | existing `compactionCancelled` | — |

## Testing

Deterministic fake summariser/provider tests, mapped to the request's §6 checklist:

| Checklist row | Test location |
|---|---|
| Short approval after compaction keeps target verbatim; synthetic 5-message probe | `compaction` tests |
| All recent text fits + "yes, do it" keeps preceding exchange | `compaction::tail` |
| Tool calls/results stay paired | `compaction::tail`, service |
| max_tokens / empty / cancel / error ⇒ no replacement, ≤ 1 retry, visible failure | service + agent loop |
| 8k exhausted ⇒ bounded larger retry (model limit / room) | `compaction::budget`, service |
| Model absent from catalogue, live 1M ⇒ inference / compaction / display agree | host |
| Large system prompt + many tool schemas affect the decision | `agent::occupancy`, loop |
| Config change rebases the anchor | `agent::occupancy` |
| Repeated compactions keep identifiers / done-pending (reconciled, not concatenated) | service |
| Checkpoint write failure ⇒ old context stays | service + host |
| Oversized single exchange ⇒ no loop, protected text kept | service + loop |
| Steering during compaction ⇒ ordered, exactly once | agent loop |
| Short classifier/judge calls unchanged (512) | host |

## Deferred (not in this release)

- Background compaction (preparing a candidate ahead of the trigger).
- Provider-native compaction.
- Deferred tool-schema loading / discovery (Phase D).
- A history search/read tool over a durable journal (the checkpoint files make the
  evidence recoverable on disk, but no in-session retrieval tool is added).
- Re-tuning the 80% default against OpenCode-like 85% on recorded transcripts.
