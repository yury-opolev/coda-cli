# Proposal: Prompt caching

- **Date:** 2026-07-28
- **Status:** Completed. All 4 phases (19 items) implemented.
- **Author:** Yury Opolev (design exploration, Coda)
- **Scope:** `LlmClient` (`AnthropicMessagesClient.BuildBody`, `AnthropicSseReader`,
  `CopilotChatClient`), `Coda.Sdk` (`Pricing`, `CatalogModel`, `TokenUsage`), `Coda.Agent`
  (`AgentLoop` request assembly, tool-search interaction).
- **Companions:** [`2026-07-28-agent-hooks-system.md`](2026-07-28-agent-hooks-system.md),
  [`2026-07-28-skills-plugins-marketplaces.md`](2026-07-28-skills-plugins-marketplaces.md).
  Independent of both.

## 1. Summary

Coda caches its system prompt and nothing else. It uses **one of the four available cache
breakpoints**, and places it on the part of the request that was already the cheapest and most
stable. The conversation — the part that grows on every one of up to 500 loop iterations and is
re-sent in full each time — is not cached at all, and neither are the tool definitions.

Separately, the cost figure in the status bar is **wrong whenever caching works**. Cached reads bill
at 0.1× input, but `Pricing` flattens every input token to the full rate. Its own comment admits it.

These are independent defects. Adding breakpoints without fixing accounting means the status bar
never shows the saving; fixing accounting without adding breakpoints just makes today's small
system-prompt cache report honestly.

## 2. Current state (verified on `fcbd8c9`)

| Concern | Today | File |
|---|---|---|
| System prompt | ✅ Cached — one `cache_control: {"type":"ephemeral"}` block | `src/LlmClient/AnthropicMessagesClient.cs:230-243` |
| Tool definitions | ❌ Serialized with no `cache_control` | `AnthropicMessagesClient.cs:253-267` |
| Conversation messages | ❌ Serialized plainly | `AnthropicMessagesClient.cs:245-251` |
| Breakpoints used | **1 of 4** | — |
| Copilot provider | ❌ No cache markers on any of its three endpoints | `src/LlmClient/CopilotChatClient.cs:240-242` |
| Usage parsing | ✅ Reads `cache_creation_input_tokens` and `cache_read_input_tokens` … | `src/LlmClient/AnthropicSseReader.cs:76-84` |
| Usage modelling | ❌ … then flattens all three into one `inputTokens` field, destroying the breakdown | `AnthropicSseReader.cs:86` |
| Cost | ❌ All input tokens billed at the full input rate | `src/Coda.Sdk/Pricing.cs:47-61` |
| Catalog | ✅ `CacheReadPerMTok` exists and is parsed from models.dev — **never read** | `src/Coda.Sdk/CatalogModel.cs:15`, `ModelCatalog.cs:294` |

`Pricing.cs:44-45` states the gap outright:

> *"Cache-read/write rates are not applied here because `TokenUsage` does not break out cached
> tokens; all input tokens are billed at the input rate."*

**A misleading comment worth correcting.** `AnthropicSseReader.cs:69-70` claims `input_tokens` is
*"a cumulative total for the entire request (including cache tokens)"*. That is **false** — see §3.
The arithmetic on line 86 is nevertheless correct as a *total*, because it adds the three disjoint
fields. But a maintainer who trusts the comment would "fix" the line by deleting the addition and
silently start undercounting every cached turn.

## 3. Anthropic mechanics (verified 2026-07-28)

Source: `platform.claude.com/docs/en/build-with-claude/prompt-caching`.

**Placement.** `cache_control` accepts `{"type":"ephemeral"}` or `{"type":"ephemeral","ttl":"1h"}`.
It may sit on tool definitions, `system` blocks, and text / `tool_use` / `tool_result` / image blocks
in `messages`. It may **not** sit on thinking blocks — though those are cached implicitly as part of
prior assistant turns and do count as input tokens when read.

**Coverage and ordering** — quoted:

> *"Prompt caching references the entire prompt — `tools`, `system`, and `messages` (in that order)
> up to and including the block designated with `cache_control`."*

**Breakpoints: 4 maximum.** A 5th explicit breakpoint returns HTTP 400. Breakpoints themselves are
free; only actual writes and reads are billed.

**Minimum cacheable prefix is per-model, not per-tier.** This is the correction that matters most for
implementation:

| Minimum | Models |
|---|---|
| 512 | Opus 5, Fable 5, Mythos 5 |
| 1 024 | Opus 4.8, Sonnet 5, Sonnet 4.6, Sonnet 4.5, Opus 4.1, Opus 4, Sonnet 4 |
| 2 048 | Mythos Preview, Opus 4.7, Haiku 3.5 |
| 4 096 | Opus 4.6, Opus 4.5, Haiku 4.5 |

A below-minimum request is **silently processed uncached, with no error**. This must be a per-model
catalog value, not a tier constant — Haiku 4.5 requiring 4 096 while Opus 5 requires 512 defeats any
tiering heuristic. Detection is simple: both cache counters zero means nothing cached.

**Pricing multipliers.** 5-minute write **1.25×** base input; 1-hour write **2×**; read/refresh
**0.1×**.

**TTL.** Default 5 minutes, refreshed free on every hit. The 1-hour TTL is GA. When mixing, 1h
entries must appear before 5m entries.

**`input_tokens` excludes cached tokens** — the definitive answer, quoted:

> *"The `input_tokens` field represents only the tokens that come **after the last cache breakpoint**
> in your request — not all the input tokens you sent."*
>
> `total_input_tokens = cache_read_input_tokens + cache_creation_input_tokens + input_tokens`

**The 20-block lookback window.** Writes happen only *at* a breakpoint. Reads walk backward looking
for entries that prior requests actually wrote, and the walk is capped at **20 blocks**. The docs'
own example: turn 1 writes at block 10; turn 2 (15 blocks) walks back and hits it; turn 3 (35 blocks)
checks blocks 35→16, misses the block-15 entry by one position, and takes a **total cache miss**.

This is precisely the failure mode of an agent loop that appends many `tool_result` blocks in a
single turn, and it is the reason the two-rolling-breakpoint pattern exists rather than a single
trailing one.

**Invalidation.** Any change to tool definitions invalidates everything. `tool_choice`,
`disable_parallel_tool_use`, and the presence or absence of images anywhere invalidate `tools` and
`system`. Thinking parameters and `output_config.effort` invalidate model-specifically. Matching is
byte-exact, and caches are workspace-isolated.

## 4. Other providers

**GitHub Copilot — caching exists but is proprietary and undocumented.** There is no page on
docs.github.com describing it. What is verifiable from code: Copilot uses a field named
**`copilot_cache_control`**, not Anthropic's `cache_control`, carried in an OpenAI-shaped payload.
Evidence is in GitHub's own repository — `github/gh-aw:pkg/workflow/test_data/sample_copilot_log.txt`
is a captured Copilot CLI debug log against `api.githubcopilot.com` whose last tool definition
carries `"copilot_cache_control": {"type": "ephemeral"}`. Microsoft corroborates the field
(`microsoft/vscode-docs:release-notes/v1_104.md`; type definition in
`posit-dev/positron:extensions/copilot/src/platform/networking/common/openai.ts`). A community client
records a 4-breakpoint limit (`olimorris/codecompanion.nvim`).

**The decisive point for Coda: Copilot bills premium requests, not tokens.** Per
`docs.github.com/en/copilot/reference/copilot-billing/…/copilot-requests`, one premium request per
user prompt multiplied by the model's rate, and *"actions Copilot takes autonomously to complete your
task, such as tool calls, do not"* count. **Caching therefore does not reduce a Copilot user's cost
at all** — only latency, and GitHub's own backend cost. Any user-visible saving from this proposal
accrues to the Anthropic-direct providers.

Whether Copilot returns cache usage fields is **UNVERIFIED**; no evidence was found that it does.

**OpenAI-shaped endpoints** cache automatically with no markers, minimum prefix 1 024 tokens.
Critically, the convention is **inverted**: `prompt_tokens` is the total and
`prompt_tokens_details.cached_tokens` is a subset of it, where Anthropic's `input_tokens` excludes
its cache fields. The two accounting paths cannot share arithmetic.

## 5. Proposed breakpoint layout

Four slots, three static and one rolling — the pattern used by the Roo-Code lineage
(`zgsm-ai/costrict:src/api/providers/anthropic.ts`):

| Slot | Placement | Rationale |
|---|---|---|
| 1 | Last entry of `tools` | Rarely changes, and any tool edit invalidates everything downstream, so isolate it |
| 2 | Last stable `system` block | Requires splitting the system prompt into stable prefix and volatile tail |
| 3 | Last content block of the **second-to-last** user message | The anchor — what the *current* request reads |
| 4 | Last content block of the **last** user / tool-result message | The rolling write — becomes next turn's anchor |

Two message breakpoints rather than one, because writes occur only at breakpoints and reads only find
prior writes. The trailing breakpoint writes at turn N what the anchor reads at turn N+1, so the
20-block walk is never load-bearing. Breakpoints are free, so this costs nothing.

**Simpler alternative worth evaluating first:** a single top-level `cache_control` puts the API in
*automatic* mode, where it places and advances the rolling breakpoint itself. Combined with one
explicit system breakpoint this is a legitimate, much lower-complexity design for an agent loop whose
last block is a stable `tool_result`. It consumes one of the four slots and is not supported on
Amazon Bedrock.

## 6. Cost accounting

`TokenUsage` must carry the breakdown rather than a single input number:

```
cost = input_tokens                                * base
     + cache_read_input_tokens                     * base * 0.10
     + cache_creation.ephemeral_5m_input_tokens    * base * 1.25
     + cache_creation.ephemeral_1h_input_tokens    * base * 2.00
     + output_tokens                               * output_rate
```

`usage.cache_creation` splits writes by TTL and its members sum to `cache_creation_input_tokens`;
both are present in the streaming `message_start` event that `AnthropicSseReader` already parses.
`CatalogModel.CacheReadPerMTok` is already populated from models.dev and merely needs consuming.

Context accounting must keep using the **total** (all three summed), since that is what occupies the
context window. Only *cost* differentiates. Conflating the two is what produced the current bug.

## 7. Coda-specific hazards

| Hazard | Detail |
|---|---|
| **Tool search mutates the tool list mid-turn** | `AgentLoop.cs:337-342` recomputes wire definitions per call when tool search is active. Any change to `tools` invalidates the entire cache — tools, system and messages — so a breakpoint on `tools` is worthless while tool search is growing the set |
| **`/effort` changes `output_config.effort`** | Model-specific invalidation of `tools` and `system`. Changing effort mid-session silently discards the cache |
| **JSON key ordering** | The docs explicitly warn that languages randomizing key order break caching on `tool_use` blocks. `System.Text.Json` over a `Dictionary<string,object>` can reorder — tool input serialization must be deterministic |
| **Compaction** | Rewrites the whole prefix, so a full miss on the next call is expected and correct. Worth measuring rather than fixing |
| **Volatile content before a breakpoint** | The single most common mistake per the docs: a timestamp or cwd ahead of the breakpoint means paying a write every turn and never getting a read. Coda's system prompt embeds environment context, so §5 slot 2 depends on splitting it |
| **Below-minimum prefixes** | Silently uncached. Without instrumentation this looks identical to caching that works |

## 8. Backlog

### Phase 0 — Make the current cache observable

- [ ] Carry `cache_read`, `cache_creation` (5m and 1h) through `TokenUsage` instead of flattening
- [ ] Fix the false comment at `AnthropicSseReader.cs:69-70`
- [ ] Apply cache multipliers in `Pricing.EstimateUsd`, consuming the existing `CacheReadPerMTok`
- [ ] Keep context accounting on the summed total; only cost differentiates
- [ ] Surface hit rate and cache savings in `/cost` and `/context`
- [ ] Warn once per session when both cache counters stay zero — the silent below-minimum case

### Phase 1 — Breakpoints

- [ ] Per-model minimum cacheable prefix in the catalog, not a tier constant
- [ ] Breakpoint on the last tool definition, gated on tool search being inactive
- [ ] Split the system prompt into stable prefix and volatile tail; breakpoint the stable part
- [ ] Evaluate automatic mode (single top-level `cache_control`) against explicit rolling breakpoints
- [ ] Two rolling message breakpoints — anchor plus trailing write
- [ ] Deterministic key ordering for tool-input serialization

### Phase 2 — Hygiene and other providers

- [ ] Audit for volatile content ahead of any breakpoint
- [ ] Decide the `/effort` mid-session policy: warn, defer to next turn, or accept the invalidation
- [ ] `copilot_cache_control` on the Copilot path — latency only, no user-visible cost change
- [ ] OpenAI-shaped accounting with its inverted `cached_tokens` convention
- [ ] 1-hour TTL for human-in-the-loop pauses, ordered before 5m entries

### Phase 3 — Measurement

- [ ] Log hit rate, write and read tokens per turn at debug level
- [ ] A benchmark over a scripted multi-iteration tool loop, asserting hit rate does not regress

## 9. Open questions

1. **Automatic mode or explicit breakpoints?** Automatic is dramatically simpler and self-manages the
   rolling breakpoint. It is unsupported on Bedrock, which Coda does not target today. Worth
   measuring both before committing to the four-slot layout.
2. **Is splitting the system prompt worth it?** Slot 2 requires separating stable instructions from
   environment context. If the volatile tail is small, an alternative is to move it into the first
   user message instead, leaving the system prompt entirely stable.
3. **What to do about tool search.** It exists to reduce tokens by narrowing the tool set, but it
   invalidates the cache every time the set grows. These two optimizations are in direct conflict and
   the tradeoff has never been measured.
4. **Does Copilot report cache usage at all?** Unverified. If it does not, cache effectiveness is
   unobservable for the provider many users are on, and Phase 0's instrumentation is Anthropic-only.
5. **Pre-warming.** `max_tokens: 0` can write the tools+system prefix before the first user turn.
   Whether the latency win at session start justifies a guaranteed extra write is untested.
