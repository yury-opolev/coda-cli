# Per-model auto-compaction threshold Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let `~/.coda/settings.json` set the auto-compaction threshold per model via `compactByModel`.

**Architecture:** `coda-serve::settings::compaction_trigger_from` gains the active provider/model and resolves `compactByModel["provider/model"]` → `compactByModel["model"]` → global keys into a `CompactionTrigger`. `coda-agent`'s `CompactionPolicy::resolve_threshold_with` clamps a pinned `tokens` value with the existing floor/cap. Spec: `docs/superpowers/specs/2026-10-26-compact-by-model-design.md`.

**Tech Stack:** Rust, serde_json. Run all cargo commands from `rust/`.

---

### Task 1: Clamp a pinned token threshold (coda-agent)

**Files:**
- Modify: `rust/crates/coda-agent/src/compaction/mod.rs` (`CompactionTrigger.tokens` doc, `resolve_threshold` doc, `resolve_threshold_with`)
- Test: `rust/crates/coda-agent/src/agent/mod.rs` (test `the_threshold_honours_a_configured_ratio_or_free_token_budget`)

- [ ] **Step 1: Write the failing test.** At the end of `the_threshold_honours_a_configured_ratio_or_free_token_budget`, after the "An absolute pin outranks both" assertion, add:

```rust
        // A pin is still held to the floor and the reply cap: one above the
        // window would never fire, and one of a few hundred tokens would
        // summarise after every exchange.
        let pin = |tokens| CompactionTrigger { tokens, ..CompactionTrigger::default() };
        assert_eq!(resolve(pin(120_000), 200_000, Some(4_096)), 120_000);
        assert_eq!(resolve(pin(500_000), 200_000, Some(4_096)), 195_904);
        assert_eq!(resolve(pin(1_000), 200_000, None), 8_000);
```

- [ ] **Step 2: Run, expect FAIL** (`500_000 != 195_904`):
`cargo test -p coda-agent --lib the_threshold_honours`

- [ ] **Step 3: Implement.** In `resolve_threshold_with` remove the early `if trigger.tokens > 0 { return trigger.tokens; }` and make the derivation:

```rust
        let derived = if trigger.tokens > 0 {
            trigger.tokens
        } else {
            match trigger.free_tokens {
                // (existing free-tokens comment unchanged)
                Some(free) if window.saturating_sub(free) >= MIN_AUTO_COMPACT_THRESHOLD => {
                    window - free
                }
                // (existing `as usize` comment unchanged)
                _ => (window as f64 * trigger.effective_ratio()) as usize,
            }
        };
```

Update docs: `CompactionTrigger.tokens` → "An absolute token count. Any positive value outranks `ratio` and `free_tokens`; like them it is held to the floor and the reply cap, since a pin above the window would never fire." `resolve_threshold`'s paragraph "`configured` is an explicit override: any positive value wins…" → "…any positive value wins over the window fraction, which is how a user pins a threshold regardless of the model (still within the floor and the reply cap)." Update the struct doc "Three ways…" only if it contradicts.

- [ ] **Step 4: Run, expect PASS:** `cargo test -p coda-agent --lib compaction` and `cargo test -p coda-agent --lib the_threshold`

- [ ] **Step 5: Commit** `fix: hold a pinned compaction threshold to the floor and reply cap` with the `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>` trailer.

### Task 2: `compactByModel` (coda-serve)

**Files:**
- Modify: `rust/crates/coda-serve/src/settings.rs` (`compaction_trigger_from` + tests)
- Modify: `rust/crates/coda-serve/src/host.rs` (~L4942-4950, the per-turn agent build)

- [ ] **Step 1: Write failing tests** in `settings.rs` `mod tests`. Change existing calls `compaction_trigger_from(&x)` to `compaction_trigger_from(&x, "p", "m")`. Add:

```rust
    #[test]
    fn a_model_entry_is_found_by_provider_and_model_then_by_bare_id() {
        let settings = json!({
            "compactAtPercent": 60,
            "compactByModel": {
                "github-copilot/claude-opus-5.5": { "tokens": 120_000 },
                "claude-opus-5.5": { "percent": 70 },
                "gpt-5": { "freeTokens": 40_000 }
            }
        });
        assert_eq!(
            compaction_trigger_from(&settings, "github-copilot", "claude-opus-5.5"),
            CompactionTrigger { tokens: 120_000, ratio: None, free_tokens: None },
        );
        assert_eq!(
            compaction_trigger_from(&settings, "claude-ai", "claude-opus-5.5"),
            CompactionTrigger { tokens: 0, ratio: Some(0.7), free_tokens: None },
        );
        assert_eq!(
            compaction_trigger_from(&settings, "openai", "gpt-5"),
            CompactionTrigger { tokens: 0, ratio: None, free_tokens: Some(40_000) },
        );
        // Keys match exactly, like `effortByModel`.
        assert_eq!(
            compaction_trigger_from(&settings, "openai", "GPT-5"),
            CompactionTrigger::ratio(0.6),
        );
    }

    #[test]
    fn a_model_entry_replaces_the_global_settings() {
        let settings = json!({
            "compactFreeTokens": 10_000,
            "compactByModel": { "p/m": { "percent": 50 } }
        });
        assert_eq!(compaction_trigger_from(&settings, "p", "m"), CompactionTrigger::ratio(0.5));
        assert_eq!(
            compaction_trigger_from(&settings, "p", "other"),
            CompactionTrigger::free_tokens(10_000),
        );
    }

    #[test]
    fn an_entry_keeps_all_its_valid_fields_and_drops_invalid_ones() {
        let settings = json!({ "compactByModel": { "p/m": {
            "tokens": 90_000, "freeTokens": 30_000, "percent": 70, "unknown": true
        } } });
        assert_eq!(
            compaction_trigger_from(&settings, "p", "m"),
            CompactionTrigger { tokens: 90_000, ratio: Some(0.7), free_tokens: Some(30_000) },
        );
        let settings = json!({ "compactByModel": { "p/m": { "tokens": "lots", "percent": 70 } } });
        assert_eq!(compaction_trigger_from(&settings, "p", "m"), CompactionTrigger::ratio(0.7));
    }

    #[test]
    fn an_entry_with_nothing_usable_falls_through() {
        for bad in [
            json!({}),
            json!(120_000),
            json!("big"),
            json!({ "tokens": 0 }),
            json!({ "tokens": -5 }),
            json!({ "percent": 140 }),
            json!({ "freeTokens": "lots" }),
        ] {
            let settings = json!({
                "compactAtPercent": 60,
                "compactByModel": { "p/m": bad.clone(), "m": { "tokens": 50_000 } }
            });
            assert_eq!(
                compaction_trigger_from(&settings, "p", "m").tokens,
                50_000,
                "{bad} must fall through to the bare id",
            );
            let settings = json!({ "compactAtPercent": 60, "compactByModel": { "p/m": bad.clone() } });
            assert_eq!(
                compaction_trigger_from(&settings, "p", "m"),
                CompactionTrigger::ratio(0.6),
                "{bad} must fall through to the global setting",
            );
        }
        // A map that is not an object is ignored too.
        let settings = json!({ "compactAtPercent": 60, "compactByModel": [1, 2] });
        assert_eq!(compaction_trigger_from(&settings, "p", "m"), CompactionTrigger::ratio(0.6));
    }
```

- [ ] **Step 2: Run, expect compile FAIL** (arity): `cargo test -p coda-serve --lib settings::tests`

- [ ] **Step 3: Implement** in `settings.rs`, replacing `compaction_trigger_from`:

```rust
/// Reads the auto-compaction trigger for the active model from a settings
/// document.
///
/// `compactByModel` holds per-model overrides keyed `"<provider>/<model>"` (as
/// `effortByModel`) or by the bare model id, tried in that order. An entry
/// is `{ "tokens", "freeTokens", "percent" }`; the first entry with at least
/// one valid field replaces the global settings outright.
///
/// Otherwise the global keys apply. Two spellings of one decision, because the
/// question is asked both ways: `compactAtPercent` is how full the context
/// window may get, and `compactFreeTokens` is how much of it to keep free. The
/// token form wins when both are present, being the more specific of the two.
///
/// A wrongly typed or out-of-range value is ignored rather than rejected. The
/// engine must start whatever the settings file says, and the fallback here is
/// the default trigger, which is always safe — unlike the startup model, a bad
/// value cannot leave compaction in a state that fails every prompt.
pub fn compaction_trigger_from(value: &Value, provider: &str, model: &str) -> CompactionTrigger {
    let by_model = value.get("compactByModel");
    [format!("{provider}/{model}"), model.to_owned()]
        .iter()
        .filter_map(|key| by_model?.get(key))
        .find_map(model_compaction_trigger)
        .unwrap_or(CompactionTrigger {
            tokens: 0,
            ratio: compaction_ratio(value.get("compactAtPercent")),
            free_tokens: token_count(value.get("compactFreeTokens")),
        })
}

/// One `compactByModel` entry, or `None` when none of its fields is usable so
/// the lookup falls through to the next key.
fn model_compaction_trigger(entry: &Value) -> Option<CompactionTrigger> {
    let trigger = CompactionTrigger {
        tokens: token_count(entry.get("tokens")).unwrap_or(0),
        ratio: compaction_ratio(entry.get("percent")),
        free_tokens: token_count(entry.get("freeTokens")),
    };
    (trigger != CompactionTrigger::default()).then_some(trigger)
}

/// A percentage in `0 < p <= 100`, as a fraction.
fn compaction_ratio(percent: Option<&Value>) -> Option<f64> {
    percent
        .and_then(Value::as_f64)
        .filter(|percent| percent.is_finite() && *percent > 0.0 && *percent <= 100.0)
        .map(|percent| percent / 100.0)
}

/// A non-negative integer token count.
fn token_count(tokens: Option<&Value>) -> Option<usize> {
    tokens.and_then(Value::as_u64).and_then(|tokens| usize::try_from(tokens).ok())
}
```

Note: `Value::get` on a non-object returns `None`, which handles non-object entries and maps. `{"tokens": 0}` yields the default trigger and is skipped.

In `host.rs` per-turn build replace

```rust
        // The threshold is a fraction of the model's window (80% by default),
        // overridable per user via `compactAtPercent` / `compactFreeTokens`.
```
and the call with

```rust
        // The threshold is a fraction of the model's window (80% by default),
        // overridable per user via `compactAtPercent` / `compactFreeTokens`,
        // or per model via `compactByModel`. The agent is rebuilt every turn,
        // so a model switch picks up the new model's entry.
        ...
            crate::settings::compaction_trigger_from(
                &load_settings_value(),
                client.provider_id(),
                &captured.config.model,
            ),
```

- [ ] **Step 4: Run, expect PASS:** `cargo test -p coda-serve --lib settings::tests`

- [ ] **Step 5: Commit** `feat: per-model auto-compaction threshold via compactByModel` with the trailer.

### Task 3: README

**Files:** Modify `README.md` "Auto-compaction" section (~L1126-1141).

- [ ] Replace the example with the spec's example; add a `compactByModel` table row: "Per-model overrides keyed `"<provider>/<model>"` (as `effortByModel`) or bare model id, tried in that order; exact, case-sensitive match. Each entry takes `tokens` (absolute threshold), `freeTokens` and/or `percent`; within an entry `tokens` > `freeTokens` > `percent`. The first entry with a valid field replaces the global settings; an entry with none is ignored." Change "Both are floored at 8,000 tokens…" to "Every form — including `tokens` — is floored at 8,000 tokens and capped…".
- [ ] Commit `docs: document compactByModel` with the trailer.

### Task 4: Verification

- [ ] From `rust/`: `cargo test -p coda-agent --lib compaction`, `cargo test -p coda-agent --lib the_threshold`, `cargo test -p coda-serve --lib settings`, `cargo clippy -p coda-agent -p coda-serve --all-targets -- -D warnings`, `cargo fmt -p coda-agent -p coda-serve --check` (only fail on touched code if fmt was already dirty on main).
