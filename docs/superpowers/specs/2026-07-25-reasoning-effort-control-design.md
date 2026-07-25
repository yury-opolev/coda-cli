# Reasoning Effort Control (all providers + auto) — Design

*Date: 2026-07-25*
*Status: proposed*

## Goal

Let a user set the reasoning **effort** level for **any** provider/model — native Anthropic *and* GitHub
Copilot/OpenAI — including **`auto`** (model default), using each model's *correct* levels and delivery,
**remembered per model**, in both the interactive TUI and serve. This replaces the Anthropic-only effort
handling that today leaves Copilot models unsupported and the `/effort` display misleading.

## Problem / current state

- **`EffortSupport` is Anthropic-only** (`src/LlmClient/EffortSupport.cs`): levels `low/medium/high/max`,
  gated to `opus-4-8`/`sonnet-4-6`/`opus-4-6`, delivered as `output_config.effort` + the
  `anthropic-beta: effort-2025-11-24` header. `ResolveAppliedEffort` returns `null` for anything else, so
  effort is silently dropped and the UI shows "auto (model default)" for e.g. `gpt-5.6-sol`.
- **Copilot gets no effort on the default path.** `OpenAiRequest` (chat/completions) sends **no** effort
  (`src/LlmClient/OpenAiRequest.cs`); only `OpenAiResponsesRequest` sends `reasoning.effort`, and only on
  the Responses endpoint. `CopilotChatClient.ResolveEndpoint(model)` picks `Messages`/`Responses`/
  `ChatCompletions` **per model** (`:291-309`), from Copilot's per-model `capabilities` metadata (Coda
  already reads `capabilities.type`/`limits`).
- **Threading:** `Session.Effort` → `SessionOptions`/`AgentOptions.Effort` → `ChatMessage.Effort`; serve via
  `HeadlessOptions.Effort`/session options. `/effort` (`src/Coda.Tui/Commands/EffortCommand.cs`) validates
  and displays through the Anthropic-only `EffortSupport`.
- **Reference:** OpenCode's per-model `variants()` catalog — canonical levels filtered per provider, Copilot
  levels read from `capabilities.supports.reasoning_effort`, persisted per provider/model.

## Design decisions (from brainstorm)

- A per-*(provider, model)* **`ReasoningCapability`** (provider-native levels + `auto` + request mapping)
  **replaces** the Anthropic-only `EffortSupport`. **Hybrid** sourcing: static Anthropic rules + dynamic
  Copilot capabilities.
- **Per-model persistence** (`effortByModel` in `settings.json`), default `auto`; switching models restores
  that model's level.
- `/effort`: **picker (no arg) + `<level>` arg**, validated per the current model; correct per-provider
  display.
- Copilot reasoning delivered via the **Responses endpoint** (`reasoning.effort`), not by adding
  `reasoning_effort` to chat/completions.
- Serve gets a **capability-query + set** surface for parity.

## Components

### 1. `ReasoningCapability` + resolver — `LlmClient`

- `ReasoningCapability(bool Supported, IReadOnlyList<string> Levels, bool SupportsAuto, ReasoningDelivery Delivery)`
  — `Levels` are ordered provider-native names; `SupportsAuto` means "auto"/none (send nothing) is allowed;
  `Delivery` describes how a chosen level maps onto the request (which param/header/endpoint).
- Resolver `Resolve(provider, model, modelCapabilities?)`:
  - **Anthropic (native):** generalize `EffortSupport` — `[low,medium,high,max]` for Opus 4.x,
    `[low,medium,high]` for `sonnet-4-6` (no `max`), unsupported for haiku/older; delivery =
    `output_config.effort` + beta header; keep the `max`→`high` clamp on non-Opus.
  - **Copilot/OpenAI:** read supported levels from the model's `capabilities` metadata
    (`capabilities.supports.reasoning_effort`, exact field verified at implementation); delivery =
    `reasoning.effort` on the Responses endpoint; non-reasoning models ⇒ `Supported = false`.
- Existing `EffortSupport` constants may be reused internally, but the public entry point becomes the
  resolver.

### 2. Per-model persistence — `settings.json`

- Add `effortByModel: Record<"{provider}/{model}", level|"auto">` (additive; other settings unchanged).
- `Session.Effort` becomes the *current* in-memory value derived from persistence: on model switch, load the
  stored effort for the new `(provider, model)` (default `auto`); on an `/effort` change, write back to
  `effortByModel[currentKey]`.

### 3. `/effort` command — `Coda.Tui/Commands/EffortCommand`

- `/effort` (no arg) ⇒ a `Select` picker of the current model's supported `Levels` + `auto` (current
  pre-selected) ⇒ set + persist.
- `/effort <level>` ⇒ validate against the current model's capability; unsupported ⇒ error listing the valid
  levels (or apply the documented clamp); `/effort auto` ⇒ model default.
- Non-reasoning model ⇒ "reasoning effort is not supported for {model}".
- Replace **all** `EffortSupport.ResolveAppliedEffort` usages for *effective effort* display with the
  resolver: `EffortCommand`, `StatusProjector`, `SessionMetadataEvents`, `StatusCommand`,
  `UiSessionSnapshot.EffectiveEffort`, exit summary.

### 4. Delivery wiring — `LlmClient`

- `AnthropicMessagesClient`: consult the resolver (not `EffortSupport` directly) to decide the applied level
  and whether to add `output_config.effort` + the beta header.
- `CopilotChatClient`/`OpenAiResponsesRequest`: a reasoning-capable Copilot model routes to the Responses
  endpoint and sends `reasoning.effort` (driven by the capability's `Delivery`). No `reasoning_effort` is
  added to chat/completions.
- `ChatMessage.Effort` semantics: `auto`/empty ⇒ omit; otherwise the provider-native level.

### 5. Serve parity — `Coda.Sdk/Serve`

- **Query:** a model's reasoning capability (supported levels + `auto`) — a serve method
  (e.g. `model/reasoningCapability`) or included in the model list / `initialize` response — so the
  orchestrator can present the same picker.
- **Set:** effort per-model — a serve method (e.g. `session/setEffort`) and/or session options, persisted
  with the same `effortByModel` semantics. Effort keeps threading into the turn as today.

## Data flow

```
pick effort (/effort or serve)
  → persist effortByModel["{provider}/{model}"]
  → Session.Effort (current, provider-native or "auto")
  → ChatMessage.Effort (omitted for "auto")
  → request builder consults ReasoningCapability.Delivery:
        Anthropic  → output_config.effort + beta header
        Copilot/OpenAI → reasoning.effort on the Responses endpoint
        unsupported/auto → nothing
switch model → restore that model's stored effort
```

## Parity (TUI ↔ serve)

Both use the same `ReasoningCapability`, the same per-model persistence and `auto` default. TUI drives it via
the `/effort` picker; serve via the capability query + set. Identical behavior and identical wire effect.

## Error handling / edge cases

- Unsupported level for the current model ⇒ rejected with the valid list, or the documented clamp
  (Anthropic `max`→`high` on non-Opus).
- Model with no reasoning support ⇒ effort N/A; `/effort` informs; requests omit effort.
- Missing/failed Copilot capability metadata ⇒ treat as no-reasoning (safe), best-effort logged.
- `auto` ⇒ never sends any effort param/header.
- Provider/model switch mid-session ⇒ restore the stored effort for the new model.

## Testing

- Capability resolution: Anthropic (`opus-4-8` ⇒ full set; `sonnet-4-6` ⇒ no `max`; `haiku` ⇒ unsupported);
  Copilot reasoning ⇒ levels from metadata + Responses delivery; Copilot non-reasoning ⇒ unsupported;
  unknown ⇒ unsupported.
- Per-model persistence round-trip (set on model A, switch to B and back ⇒ restored).
- `/effort`: picker options = supported + `auto`; arg validation (unsupported rejected with list); `auto`
  clears.
- Request mapping: Anthropic body carries `output_config.effort` + beta header only when applied; Responses
  body carries `reasoning.effort`; chat/completions carries none; `auto` omits everywhere.
- Display: effective effort correct for Copilot models.
- Serve: capability query returns per-model levels; set effort persists + applies.
- Migrate the existing `EffortSupport` tests to capability tests; keep `AnthropicMessagesClient` /
  `OpenAiResponses` / `EffortCommand` coverage.

## Non-goals

- The thinking **display** (its own next spec).
- `max_tokens` behavior.
- Inventing levels a provider doesn't advertise; adding `reasoning_effort` to chat/completions.
