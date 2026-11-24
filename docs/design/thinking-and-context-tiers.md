# Thinking modes and context tiers

Status: thinking-mode selection shipped in 0.1.185; context tiers in 0.1.186.

## Problem

Coda's effort controls (`/effort`, `--effort`, the model browser, saved
`effortByModel`, `session/setEffort`) accepted a level and the Anthropic
Messages serializer always sent it as a manual reasoning budget:

```json
{ "thinking": { "type": "enabled", "budget_tokens": 16384 } }
```

Adaptive-only Claude models (Claude Opus 5.5, Sonnet 5.5, Haiku 5.5 and every
other Claude 5 family; Opus 4.7/4.8) reject that with HTTP 400
(`"thinking.type.enabled" is not supported for this model`). The Copilot
Messages route delegates to the same serializer, so a correct route and a
validated level still produced an invalid request. Reproduced against Coda
0.1.184 with `coda run --model claude-opus-5.5 --effort high -p hello`.

## Capability provenance

Copilot's `GET /models` (observed live 2026-11-24) advertises thinking under
`capabilities.supports`:

| Model | `adaptive_thinking` | `min/max_thinking_budget` | `reasoning_effort` |
|---|---|---|---|
| claude-opus-5.5, sonnet-5.5, haiku-5.5 | `true` | absent | low…max |
| claude-sonnet-5 | `true` | 1024 / 32000 | low…max |
| claude-haiku-4.5 | absent | 1024 / 32000 | absent |

Anthropic's own table
(<https://platform.claude.com/docs/en/build-with-claude/thinking#configuring-thinking>)
is authoritative where metadata is silent: Opus/Sonnet 4.6 accept both forms
(manual is deprecated); Opus 4.7+, Sonnet 5+, Haiku 5.5 and Fable/Mythos reject
manual; Claude 4.5 and earlier reject adaptive. Sonnet 5 advertises budgets yet
rejects `enabled`, so budgets alone never imply manual support.

## Thinking-mode selection

`coda_llm::thinking::resolve(model, metadata)` returns a `ThinkingPolicy`
(`mode`, `source`, optional advertised `max_budget`). Precedence:

1. **Model metadata** — an explicit `adaptive_thinking` flag decides
   (`source = model_metadata`).
2. **Compatibility table** — family/version parsed from the model id
   (`claude-opus-4-8` and `claude-opus-4.8` agree; a date suffix is not a minor
   version) (`compatibility_table`).
3. **Metadata budgets** — an unknown model advertising budgets is manual
   (`model_metadata`).
4. **Legacy default** — anything else stays manual, exactly as before
   (`legacy_default`). Unknown never becomes adaptive, because older models
   reject adaptive.

The policy is carried on `ChatRequest::thinking` and resolved at the provider
client boundary before serialization, so every caller — foreground turns,
subagents, compaction summaries, side questions — shares one answer. The
Copilot client reads the cached `/models` row (no extra round trip); the direct
Anthropic client uses the table. A caller may pre-set a policy.

Serialization (`anthropic::request::build_with_thinking`):

| Effort | Adaptive | Manual |
|---|---|---|
| level | `thinking: {type: adaptive}`, `output_config: {effort: <level>}` | `thinking: {type: enabled, budget_tokens: N}` |
| `auto` | nothing | nothing |

Manual budgets keep the historical mapping (2048…32768), stay strictly below
`max_tokens`, and are clamped into the advertised `min/max_thinking_budget`
range (with a floor of 1024); if `max_tokens` cannot fit the minimum, thinking
is omitted rather than sent invalid. `max_tokens` is unchanged in both modes (for
adaptive it is the hard cap on thinking plus answer). OpenAI Responses
(`reasoning.effort`) and Chat Completions (no effort field) are unchanged.

`auto` means **provider default**, not "thinking disabled": on Claude 5 models
that is adaptive thinking at the model's default effort; on Opus 4.8 it is
thinking off. This is the pre-existing behavior and is now documented.

Supported levels: Copilot models use their advertised `reasoning_effort` list.
The direct-Anthropic static table now follows Anthropic's effort table
(<https://platform.claude.com/docs/en/build-with-claude/effort#effort-levels>):
Opus 4.6 / Sonnet 4.6 offer `max` but not `xhigh` (previously Sonnet was given
`xhigh` and denied `max`), and Opus 4.7+, Sonnet 5+, Haiku 5.5 and Fable/Mythos
offer all five. Unsupported explicit levels are refused, or reported as the
effective value, by the existing resolver; nothing is sent that the model does
not advertise.

Deliberate behavior changes for the **direct Anthropic** provider (Copilot
models follow their live metadata and are unaffected):

- Opus 4.6 / Sonnet 4.6 move from manual budgets to adaptive thinking, because
  Anthropic deprecated manual thinking there.
- A saved `xhigh` for Opus/Sonnet 4.6 no longer applies (the model rejects it);
  `model/reasoningCapability` reports `current: null` (automatic) and an
  explicit `--effort xhigh` is refused at startup with a reason. It is not
  silently upgraded to `max` or downgraded to `high` — consistent with the
  existing rule that `xhigh` is never faked as `high`.
- Model ids with a gateway prefix (`anthropic/claude-…`,
  `us.anthropic.claude-…`) resolve like bare ids.

Unchanged: signed thinking blocks replay verbatim (including display-omitted
blocks with empty text), unsigned ones are still dropped, a reply with no
thinking block is accepted, and prompt-cache markers, tools, streaming and
cancellation are untouched. A 400 is not retried or silently downgraded.

Trade-off: Claude 5 models default to `display: "omitted"`, so adaptive
thinking text is not shown in the transcript. Requesting `display: "summarized"`
was left out deliberately: it is not needed for correctness and its acceptance
through the Copilot proxy was not established.

## Diagnostics

`request_shape` now records, from the final wire body, `thinking_type`
(`adaptive`/`enabled`/`disabled`/`other`), `effort_value` (allowlisted level or
`other`) and `thinking_source` (`model_metadata`/`compatibility_table`/
`legacy_default`) alongside the existing `thinking_budget_value` and
`max_tokens_value`. Requested vs effective effort remains visible through
`model/reasoningCapability` (`current`) and the setter results. No raw bodies,
prompts, tool payloads or thinking text are logged.

## Context tiers

### Provider contract (established 2026-11-24)

- Copilot `GET /models` exposes, per model, **one** window in
  `capabilities.limits` (`max_context_window_tokens`, `max_prompt_tokens`) and,
  separately, pricing tiers in `billing.token_prices`: `default.context_max`,
  optionally `long_context.context_max`, and an optional `preferred_tier`.
  Observed: Claude Sonnet 5.5 default 200,000 / long 936,000; Haiku 5.5
  100,000 / 936,000 (preferred `long_context`); Opus 5.5 200,000 / 1,000,000;
  GPT-5.3-Codex default only (272,000); MAI Code 2 no tier data.
- GitHub's pricing docs define the long-context tier by an **input-token
  threshold** ("≤ 200K" default, "> 200K" long context) and say the user can
  "choose between the default context size or the extended context". Copilot
  CLI documents `--context default|long_context` / `contextTier` "for
  tiered-pricing models", defaulting to the catalog's preferred tier.
- **No documented request header or body property negotiates a tier.** None
  was invented. A tier is therefore implemented as what the contract supports:
  the effective window Coda budgets against. Selecting `default` keeps the
  conversation within the default tier's size (compacting earlier, so requests
  stay at default pricing); `long_context` lets it grow to the long tier's size.
  A host test proves the inference request is the same under either tier
  (model, `max_tokens`, effort, thinking, tools, system); only budgeting moves.

### Model

| Value | Where | Meaning |
|---|---|---|
| Requested tier | session override, resumed session, `contextTierByModel` | What the user chose; `None` = unset |
| Effective tier | `ContextResolution.effective` | The tier the effective window belongs to, when known |
| Effective window | `ModelBudget.context_window` | Used for fit checks, occupancy, compaction threshold, summary budget, status |
| Compaction trigger | `compactAtPercent`/`compactByModel` | Unchanged; resolved against the effective window |
| Output limit | `maxOutputTokens*` / model limit | Independent of the tier |

Capability is `unknown` (no live row: never fetched, or the fetch failed),
`single` (a row with no tier data, or a default-only tier) or `tiered`. Unknown
is never treated as single, and neither is treated as supporting a tier.
Windows from metadata are bounded (1,024–10,000,000) and a "long" tier that is
not larger than the default is dropped rather than offered.

### Resolution and precedence

`output_budget::resolve_with_tier` is the single resolver (turns, manual and
automatic compaction, subagents, display). Requested tier:

1. `--context` launch flag (applied via `session/setContextTier` after
   `initialize`, so it overrides everything below);
2. the resumed session's recorded tier (`<sessions>/<id>.context.json`,
   written when a turn's requested tier changes, restored on resume as a
   session request);
3. the saved `contextTierByModel["provider/model"]`;
4. unset — the provider-reported window exactly as before (backward
   compatible; the reported effective tier is the one that window falls in).

Session requests are keyed by model, so a model or provider switch resolves the
new model's own request; a metadata refresh can only change the effective
window at the next resolution. Each turn resolves once at its start, so a
mid-turn change applies to the next turn, never to the request in flight. A
change republishes the effective window (`usage.contextLimit`,
`event/contextWindow`), which rebases the occupancy display.

Migration: sessions saved before 0.1.186 have no tier record and resolve from
the saved preference, i.e. unset unless the user saved one — the same window as
before. A refused or invalid saved/session tier leaves the window unchanged and
is reported with a `tierNote`.

### Surfaces

CLI `--context default|long_context|auto` (interactive and `coda run`); TUI
`/context tier [tier] [--session]`, a `tier` line in `/context`, and `t` in the
model browser; serve `model/contextCapability` and `session/setContextTier`
(older engines answer `-32601`, which clients report as "tiers unsupported");
`event/contextWindow` gains `requestedTier`, `effectiveTier`, `tierNote`.
Discovery uses the cached model list and fetches it at most once for an
unknown model; nothing is fetched per prompt.

### Trade-offs

- A client-side tier cannot *force* the provider to price a request as
  default; it keeps requests below the threshold by budgeting. That is exactly
  what the documented threshold pricing makes meaningful.
- `preferred_tier` is reported but not applied to unset sessions, to keep
  existing sessions' windows unchanged.
- Subagents resolve their own model's request (session override, then saved);
  there is no separate subagent tier setting.

## Threat / abuse review

- **Untrusted metadata.** `adaptive_thinking` is only read as a boolean;
  advertised budgets are discarded when zero, above 1,000,000, or inverted.
  A hostile or broken row can at worst choose a mode the provider then rejects
  with an explicit 400 — it cannot inject fields.
- **Bounded values.** Manual budgets are bounded by `max_tokens`, the 1024
  minimum and the advertised maximum; adaptive sends only an allowlisted level.
- **No success-shaped failure.** A rejected request surfaces the provider error;
  nothing retries in a loop or silently drops the user's effort.
- **Secret-free logs.** New diagnostic fields are fixed enums; unknown values are
  recorded as `other`, never copied.
- **Cost.** Higher effort spends more output tokens; that is unchanged.
- **Tier metadata is untrusted.** Context windows are bounded and validated
  before use; an inverted or absurd long tier is dropped. Unknown capability
  refuses a tier instead of guessing a window.
- **Tier failures are explicit.** Unsupported, unknown or malformed tier
  requests return `ok: false` with a reason and change nothing; a saved or
  resumed tier the model no longer offers is reported via `tierNote`.
- **Tier cost/availability disclosure.** `model/contextCapability` and
  `/context tier` state that input above the default tier's size is billed at
  the long-context rate.
- **Bounded refresh.** Tier discovery reuses the cached model list and fetches
  at most once per unknown model; the session sidecar holds only a model id
  and a tier enum.
