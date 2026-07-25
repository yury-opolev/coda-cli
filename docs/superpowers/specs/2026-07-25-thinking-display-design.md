# Thinking Display Design

**Date:** 2026-07-25
**Status:** Approved

## Goal

Surface model reasoning ("thinking") in Coda's UI, provider-agnostically. Every provider that emits
reasoning (Anthropic extended thinking, OpenAI/Copilot Responses reasoning summaries) is normalized into
one internal stream and rendered identically in the interactive TUI, while serve mode forwards the same
reasoning events to downstream clients. The user must be able to control how much reasoning is shown, and
see a live time/token status while a model is thinking.

This feature is strictly **downstream of the reasoning-effort control spec**
(`2026-07-25-reasoning-effort-control-design.md`): reasoning *effort* decides whether and how much a model
reasons; this spec only renders whatever reasoning the model actually emits. If no reasoning is emitted, no
thinking block appears.

## Scope and architecture

The design has two planes with a clean boundary:

- **Data plane (applies to both TUI and serve).** Per-provider SSE readers parse reasoning content and
  normalize it into a single pair of sink events. A new content-model block type carries reasoning across
  turns so provider replay requirements (Anthropic signed blocks, OpenAI encrypted reasoning) are satisfied.
  Serve emits reasoning notifications so any downstream client receives the stream.
- **Display plane (TUI-only).** The `ToolDisplayMode` verbosity knob — renamed to a clean ordered scale —
  is shared by tools and thinking. A `ThinkingTranscriptBlock` renders per reasoning burst with a live
  time/token status.

Provider differences (parsing, signatures, encrypted content, usage timing) live entirely below the sink.
The UI never branches on provider.

Bounded components:

- per-provider SSE readers own reasoning parsing and normalization;
- the content model owns the `ThinkingBlock` type and its provider serialization/replay;
- `IAgentSink` owns the normalized `OnThinking` / `OnThinkingComplete` contract;
- the TUI reducer owns burst/block lifecycle, elapsed-time, and token state;
- `TranscriptBlockFormatter` owns per-mode projection of a `ThinkingTranscriptBlock`;
- user settings own the shared display-mode policy;
- serve owns `event/thinking` / `event/thinkingComplete` emission.

## Display-mode rename (shared tools + thinking knob)

The existing `ToolDisplayMode` enum values do not form an obvious verbosity scale and `Tiny` (which fully
hides tool output) is misleading. Because thinking now shares this one knob, rename to an ordered scale.

| Old value | New value | Meaning (verbosity high → low) |
|-----------|-----------|--------------------------------|
| `Verbose` | `Full`    | Most detail                    |
| `Compact` | `Compact` | Header + preview / tail        |
| `Summary` | `Summary` | Rollup / one-liner (default)   |
| `Tiny`    | `Hidden`  | Not rendered at all            |

- Enum members are declared in verbosity order: `Full, Compact, Summary, Hidden`.
- Default remains `Summary`.
- **Clean rename, no aliases.** Persisted `settings.json` values are migrated in place
  (`verbose` → `full`, `tiny` → `hidden`); the resolver accepts only the new strings. This is safe because
  the setting is only used internally today.

### Thinking rendering per mode

Consistent with tools (where `Hidden` shows nothing and the next visible level is a one-liner):

| Mode | Thinking rendering |
|------|--------------------|
| **Full** | Stream the entire reasoning live, stays expanded; on completion show "💭 Thought for Xs" + full text. |
| **Compact** | Collapsed live tail (~3–5 lines) while streaming; on completion "💭 Thought for Xs" + tail. |
| **Summary** (default) | One-liner: "💭 Thinking…" → "💭 Thought for Xs". |
| **Hidden** | Not rendered. |

Verbosity is static/mode-driven, exactly like tools. Per-block interactive expand/collapse is **out of
scope for v1** (possible follow-up).

## Data model

Add a `ThinkingBlock` content type to the message content model
(`ContentBlock` / `ChatMessage`):

- `Text` — accumulated reasoning text.
- `Signature?` — provider signature/opaque token required for replay (Anthropic signed thinking blocks;
  OpenAI encrypted reasoning content stored in the same field). `null` when the provider requires no replay
  token.

### Provider normalization

- **Anthropic** (`AnthropicSseReader`): parse `content_block_start`/`delta` for `thinking` blocks —
  `thinking_delta` (text) and `signature_delta` (signature). The client **preserves** thinking blocks with
  their signatures in assistant message history and **re-serializes** them on subsequent tool-use requests.
  This is a hard Anthropic API requirement: a turn that combines extended thinking with `tool_use` is
  rejected if the thinking blocks are not returned intact on the next request.
- **OpenAI / Copilot Responses** (`OpenAiResponsesSseReader`): parse
  `response.reasoning_summary_text.delta` for text; store any encrypted reasoning content as the opaque
  `Signature` for stateless replay.

Both paths emit the **same** normalized sink events. The content-model change is provider-neutral; only the
serialization/deserialization in each client is provider-specific.

**Implementation-time verification:** confirm whether Coda's current `output_config.effort` request path
already causes Anthropic to emit thinking content blocks in the stream. If it does, parse and preserve them.
If it does not, enable the native `thinking` parameter — coordinated with the reasoning-effort spec so the
two reasoning controls do not conflict.

## Sink and transcript

Extend `IAgentSink`, mirroring `OnAssistantText` / `OnAssistantComplete`:

- `OnThinking(string delta)` — a chunk of reasoning text for the current burst. The first delta implicitly
  starts a burst.
- `OnThinkingComplete()` — closes the current burst.

A **burst** is one contiguous reasoning phase. Multi-step turns interleave bursts with tool calls
(thinking → tool → thinking → tool → answer), producing multiple `ThinkingTranscriptBlock`s in order.

`ThinkingTranscriptBlock`:

- `Id` — burst identifier.
- `Text` — accumulated reasoning.
- `Complete` — whether the burst finished.
- `StartedAt` — timestamp of the first delta.
- `ElapsedMs` — frozen on completion; computed live from `StartedAt` while streaming.
- `ThinkingTokens?` — normalized token count (see Live status).

The TUI reducer creates a block on the first `OnThinking`, appends deltas, and finalizes on
`OnThinkingComplete`. Streaming tail rendering reuses the incremental markdown formatter (WS3) so large
reasoning streams stay cheap.

## Live status (when block is visible)

Rendered at every visible verbosity level, including the Summary one-liner:

- **Elapsed time** — ticks at ~1 Hz while the burst is active ("💭 Thinking… 4s"), freezing to
  "💭 Thought for Xs" on completion. The 1 Hz refresh is driven by the existing render/tick loop, decoupled
  from delta arrival, so it does not regress streaming performance work.
- **Tokens** — one normalized `ThinkingTokens` field on the block, fed from the provider usage stream:
  updates live for Anthropic (running `output_tokens` in `message_delta`; during the thinking phase
  output ≈ thinking tokens) and is populated at completion for OpenAI/Copilot (usage reported only at
  `response.completed`). Rendered only when present ("💭 Thinking… 4s · 320 tok"). No local estimation or
  guessing.

## Serve parity

Serve always emits reasoning notifications; there is no verbosity on the wire (the client decides how to
render):

- `event/thinking` — `{ sessionId, delta }`.
- `event/thinkingComplete` — `{ sessionId, elapsedMs, thinkingTokens? }`.

These flow through `WireAgentSink` alongside the existing assistant-text deltas. Downstream clients (Cortex,
editors, other UIs) receive the full reasoning stream and filter/render as they wish.

## Error handling

- Missing/partial reasoning (provider emits no thinking) → no block; normal behavior.
- A stream that ends without an explicit completion event → the reducer finalizes the open burst on turn
  completion (freeze elapsed time, mark `Complete`).
- Anthropic replay failure (missing/invalid signature) is prevented by preserving blocks verbatim;
  round-trip tests guard this. If a signature is absent, the block is still displayed but flagged
  non-replayable so the client can drop it from history rather than send an invalid request.
- Unknown/renamed display-mode string → resolver falls back to `Summary` (existing behavior, new default
  name).

## Testing

- **SSE readers:** per-provider unit tests for thinking-delta and signature/encrypted-content parsing, and
  for correct normalization to sink events.
- **Content model:** round-trip and preservation tests — an Anthropic assistant message with thinking +
  tool_use serializes and replays with signatures intact across a follow-up request.
- **Reducer/formatter:** burst lifecycle (create/append/finalize), multiple interleaved bursts, elapsed-time
  computation, per-mode rendering (`Full`/`Compact`/`Summary`/`Hidden`), and token rendering when present.
- **Display-mode rename:** resolver accepts new strings, migrates persisted values, rejects old strings.
- **Serve:** `event/thinking` / `event/thinkingComplete` emission and payload shape.

## Out of scope (v1)

- Per-block interactive expand/collapse (verbosity is mode-driven).
- Local token estimation for providers that don't stream usage.
- Any change to reasoning *effort* control (owned by the reasoning-effort spec).
