# Thinking modes and context tiers

Status: thinking-mode selection shipped in 0.1.185. Context tiers follow in a
separate change and are documented in their own section when they land.

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
`max_tokens`, respect the 1024 minimum, and are additionally clamped to the
advertised `max_thinking_budget`. `max_tokens` is unchanged in both modes (for
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
