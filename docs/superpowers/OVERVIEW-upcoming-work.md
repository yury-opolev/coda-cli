# Upcoming Work — Overview

**Date:** 2026-07-28
**Status:** Current. Nothing here is implemented.

This is the forward-looking view: designed-but-unbuilt work, plus items explicitly deferred from
proposals that have otherwise shipped. **Completed work is not listed here** — see
[Recently shipped](#recently-shipped) for where it lives instead.

## Proposals

Three proposals are open, each with its own phased backlog. They are independent — none blocks
another — though the first two meet at plugin-shipped hooks.

| # | Proposal | Backlog | Summary |
|---|---|---|---|
| 1 | [Agent hooks system](../proposals/2026-07-28-agent-hooks-system.md) | 8 phases, 35 items | 13 lifecycle events that can observe, block, or **mutate** what flows through them — prompts, system prompt, toolset, tool arguments, tool results, responses |
| 2 | [Skills, plugins and marketplaces](../proposals/2026-07-28-skills-plugins-marketplaces.md) | 9 phases, 41 items | Make skills **model-invocable**, widen what a plugin may carry, add lifecycle and trust, harden marketplaces |
| 3 | [Prompt caching](../proposals/2026-07-28-prompt-caching.md) | 4 phases, 19 items | Use all four cache breakpoints instead of one, and fix cost accounting that bills cached reads at 10× their real price |
| 4 | [TUI transcript and interaction polish](../proposals/2026-07-28-tui-transcript-polish.md) | 4 phases, 14 items | Message gutters and tree connectors, a pinned prompt during long turns, colour that distinguishes partial from total failure, right-click copy and a selectable session id |

### Why these three

- **Hooks** is the only one that adds a genuinely new extension surface. Today Coda can be extended
  by editing its source or writing an MCP server, and nothing else.
- **Skills/plugins** is mostly *exposure* rather than new subsystems — Coda already has subagents,
  MCP, output styles, themes and LSP; plugins simply cannot carry them. The exception is
  model-invocable skills, which is a real gap: a Coda skill fires only when a human types
  `/skill <name>`, so the agent cannot know its own skills exist.
- **Caching** is the only one with a live correctness bug attached: the cost shown in the status bar
  is overstated whenever caching works.

### Sequencing

There is no hard ordering, but there is a cheapest-first ordering:

1. **Caching Phase 0** — self-contained, fixes a wrong number users can see, and needs no new concepts.
2. **Hooks Phase 0** — the protocol foundation. Existing three-event configurations keep working untouched.
3. **Skills Phase 1** — the skill tool. The single highest-value item in proposal 2.
4. Everything else, by appetite.

Two cross-proposal dependencies are worth knowing:

- **Plugin-shipped hooks** (proposal 2, Phase 4) requires **hooks Phase 0**.
- **Skill-scoped hooks** — the `hooks` frontmatter field (proposal 2, §4.2) — likewise.

## Deferred from shipped work

Items explicitly parked when their parent proposal shipped. Small, and none blocks anything.

| Item | Source |
|---|---|
| MCP HTTP transport **M6** — Client ID Metadata Documents, full step-up escalation, `~/.claude.json` import | [`2026-06-28-mcp-http-transport-and-auth.md`](../proposals/2026-06-28-mcp-http-transport-and-auth.md) |
| Redacted display of secret env/header values in `/mcp info` | [`2026-07-03-mcp-tui-management.md`](../proposals/2026-07-03-mcp-tui-management.md) |
| Deleting stored secrets on `/mcp remove` — blocked: `ITokenStore` has no enumerate API | same |

## Known documentation drift

Statuses that no longer match reality, found while assembling this overview:

- [ ] `docs/proposals/2026-07-03-mcp-in-serve-path.md` is marked **"Draft / design exploration"** but
      serve-side MCP has shipped — `ServeOptions.EnableMcp` defaults to true and `--no-mcp` /
      `--no-project-mcp` both exist. Its own companion proposal describes it as *"already shipped"*.
- [x] `README.md` listed GitHub Copilot as a *planned* credential provider. Corrected — Copilot is
      implemented; only OpenAI remains planned.
- [x] `OVERVIEW-tui-feature-suite.md` was marked *In progress*. Corrected to completed.

## Recently shipped

Not upcoming — recorded here only so this overview is not mistaken for the whole picture.

| Work | Where |
|---|---|
| TUI feature suite — nine features, released in v0.1.91 | [`OVERVIEW-tui-feature-suite.md`](OVERVIEW-tui-feature-suite.md) |
| Interactive `/mcp` management (P1–P4) | [`2026-07-03-mcp-tui-management.md`](../proposals/2026-07-03-mcp-tui-management.md) |
| MCP HTTP transport and auth (M1–M5) | [`2026-06-28-mcp-http-transport-and-auth.md`](../proposals/2026-06-28-mcp-http-transport-and-auth.md) |
| Serve-side MCP | [`2026-07-03-mcp-in-serve-path.md`](../proposals/2026-07-03-mcp-in-serve-path.md) |

## Maintaining this file

An item leaves this overview when it ships — it does not get a strikethrough or a "done" marker. Move
it to [Recently shipped](#recently-shipped) with a link to where the detail now lives, and update the
source proposal's own **Status** line in the same change. Documentation drift is what this file
exists to prevent.
