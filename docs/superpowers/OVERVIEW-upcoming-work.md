# Upcoming Work — Overview

**Date:** 2026-07-28
**Status:** Current. No proposal open.

This is the forward-looking view: designed-but-unbuilt work, plus items explicitly deferred from
proposals that have otherwise shipped. **Completed work is not listed here** — see
[Recently shipped](#recently-shipped) for where it lives instead.

## Proposals

None open. The four that were open — TUI transcript and interaction polish, hooks,
skills/plugins/marketplaces and prompt caching — have all shipped; see
[Recently shipped](#recently-shipped).

## Deferred from shipped work

Items explicitly parked when their parent proposal shipped. Small, and none blocks anything.

| Item | Source |
|---|---|
| MCP HTTP transport **M6** — Client ID Metadata Documents, full step-up escalation, `~/.claude.json` import | [`2026-06-28-mcp-http-transport-and-auth.md`](../proposals/2026-06-28-mcp-http-transport-and-auth.md) |
| Redacted display of secret env/header values in `/mcp info` | [`2026-07-03-mcp-tui-management.md`](../proposals/2026-07-03-mcp-tui-management.md) |
| Deleting stored secrets on `/mcp remove` — blocked: `ITokenStore` has no enumerate API | same |

## Known documentation drift

Statuses that no longer match reality, found while assembling this overview:

- [x] `docs/proposals/2026-07-03-mcp-in-serve-path.md` was marked **"Draft / design exploration"** but
      serve-side MCP had shipped — `ServeOptions.EnableMcp` defaults to true and `--no-mcp` /
      `--no-project-mcp` both exist. Corrected to completed.
- [x] `README.md` listed GitHub Copilot as a *planned* credential provider. Corrected — Copilot is
      implemented; only OpenAI remains planned.
- [x] `OVERVIEW-tui-feature-suite.md` was marked *In progress*. Corrected to completed.

## Recently shipped

Not upcoming — recorded here only so this overview is not mistaken for the whole picture.

| Work | Where |
|---|---|
| TUI transcript and interaction polish — gutters and tree connectors, a pinned prompt, outcome colour through a named palette, right-click copy, a selectable session id, and selectable modal overlays | [`2026-07-28-tui-transcript-polish.md`](../proposals/2026-07-28-tui-transcript-polish.md) |
| Agent hooks system — 13 lifecycle events, four handler types, trust gating | [`2026-07-28-agent-hooks-system.md`](../proposals/2026-07-28-agent-hooks-system.md), [`docs/hooks.md`](../hooks.md) |
| Skills, plugins and marketplaces — model-invocable skills, plugin components, marketplace integrity | [`2026-07-28-skills-plugins-marketplaces.md`](../proposals/2026-07-28-skills-plugins-marketplaces.md), [`docs/skills-and-plugins.md`](../skills-and-plugins.md) |
| Prompt caching — four breakpoints, provider-agnostic accounting, `/cost` savings | [`2026-07-28-prompt-caching.md`](../proposals/2026-07-28-prompt-caching.md) |
| TUI feature suite — nine features, released in v0.1.91 | [`OVERVIEW-tui-feature-suite.md`](OVERVIEW-tui-feature-suite.md) |
| Interactive `/mcp` management (P1–P4) | [`2026-07-03-mcp-tui-management.md`](../proposals/2026-07-03-mcp-tui-management.md) |
| MCP HTTP transport and auth (M1–M5) | [`2026-06-28-mcp-http-transport-and-auth.md`](../proposals/2026-06-28-mcp-http-transport-and-auth.md) |
| Serve-side MCP | [`2026-07-03-mcp-in-serve-path.md`](../proposals/2026-07-03-mcp-in-serve-path.md) |

## Maintaining this file

An item leaves this overview when it ships — it does not get a strikethrough or a "done" marker. Move
it to [Recently shipped](#recently-shipped) with a link to where the detail now lives, and update the
source proposal's own **Status** line in the same change. Documentation drift is what this file
exists to prevent.
