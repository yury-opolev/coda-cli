# Per-model auto-compaction threshold

**Date:** 2026-10-26
**Status:** Approved
**Scope:** `rust/crates/coda-serve`, `rust/crates/coda-agent`, `README.md`
**Request:** "REQUEST CODA feature: per-model auto-compaction threshold"

## Problem

`~/.coda/settings.json` only configures auto-compaction globally
(`compactAtPercent`, `compactFreeTokens`). `settings::compaction_trigger_from`
always builds `CompactionTrigger { tokens: 0, .. }`, so the absolute-token field
`CompactionPolicy::resolve_threshold_with` already honours is unreachable. The
only workaround — a custom catalogue via `CODA_MODELS_PATH` with a smaller
`context_limit` — also distorts the context display.

## Settings

```json
{
  "compactAtPercent": 60,
  "compactByModel": {
    "github-copilot/claude-opus-5.5": { "tokens": 120000 },
    "claude-ai/claude-sonnet-4-6": { "percent": 70 },
    "gpt-5": { "freeTokens": 40000 }
  }
}
```

An entry is an object with any of:

| Field | Meaning | Valid when |
|---|---|---|
| `tokens` | Absolute threshold. | positive integer |
| `freeTokens` | Headroom, as `compactFreeTokens`. | non-negative integer |
| `percent` | Fraction of the window, as `compactAtPercent`. | number, `0 < p <= 100` |

## Resolution

1. `compactByModel["<provider>/<model>"]`, then `compactByModel["<model>"]`.
   Keys match exactly and case-sensitively, like `effortByModel` and
   `maxOutputTokensByModel`. The model id is the active one as configured.
2. An entry is *usable* when at least one of its fields is valid. Invalid fields
   are dropped silently; an entry with no valid field (or that is not an object)
   is skipped, so lookup falls through to the bare id and then to the global
   settings.
3. A usable entry replaces the global settings entirely — the global
   `compactAtPercent` / `compactFreeTokens` are not mixed into it.
4. No usable entry: the global settings as today; none set: 80%.

Within an entry the precedence is `tokens > freeTokens > percent`, which is the
precedence `CompactionTrigger` already implements, so an entry maps one-to-one
onto a trigger (`tokens` → `CompactionTrigger.tokens`).

## Floor and cap apply to `tokens`

Today a positive `tokens` "wins outright" and bypasses the 8,000-token floor and
the output-budget cap. Nothing user-facing could set it, so the bypass was never
observable. Now that users can, the request says the floor and cap still apply,
and that is also the safe behaviour: a pin above the window never fires (it
disables compaction just as a zero would), and a pin at 1,000 would summarise
after every exchange. `resolve_threshold_with` therefore clamps a pinned value
with the same `[min(floor, cap), cap]` range as a derived one. A pin within the
range — the only sensible kind — is unchanged.

## Wiring

`compaction_trigger_from(settings, provider, model)` takes the active provider
and model. Its single caller, the per-turn agent build in `host.rs`, passes
`client.provider_id()` and `captured.config.model` — the same pair it already
uses for the output budget. The agent is rebuilt every turn from the captured
config, so a mid-session model switch picks up the new model's entry on the next
turn. No other site constructs a compaction policy from settings: subagents do
not auto-compact, `/compact` ignores the threshold, and the TUI does not display
it.

## Testing

`settings.rs` unit tests: provider/model key, bare-id fallback, provider/model
beats bare id, invalid-only entry falls through, entry replaces global, global
still works without an entry, `tokens` maps to the trigger. `coda-agent` test:
a pinned token count is clamped to the cap and the floor.

## Docs

README "Auto-compaction" section: document `compactByModel`, the resolution
order and that the floor/cap apply to every form.
