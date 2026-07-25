# TUI Feature Suite — Implementation Overview

**Date:** 2026-07-25
**Status:** In progress
**Branch:** `features/tui-feature-suite`

This suite makes the Coda TUI more responsive, expressive, and pleasant to use. It collects nine designed
features, each with its own approved design spec under `docs/superpowers/specs/`. Every feature honors the
**TUI ↔ serve parity** rule: user-facing behavior works in both the interactive Terminal.Gui shell and serve
JSON-RPC mode, or its plane split (rendering-only vs data) is called out explicitly in the spec.

## Specs

| # | Feature | Spec |
|---|---------|------|
| 1 | Reasoning-effort control | [`2026-07-25-reasoning-effort-control-design.md`](specs/2026-07-25-reasoning-effort-control-design.md) |
| 2 | Thinking display | [`2026-07-25-thinking-display-design.md`](specs/2026-07-25-thinking-display-design.md) |
| 3 | Theme system | [`2026-07-25-theme-system-design.md`](specs/2026-07-25-theme-system-design.md) |
| 4 | Callouts | [`2026-07-25-callouts-design.md`](specs/2026-07-25-callouts-design.md) |
| 5 | Clickable links | [`2026-07-25-clickable-links-design.md`](specs/2026-07-25-clickable-links-design.md) |
| 6 | Pending indicator | [`2026-07-25-tui-pending-indicator-design.md`](specs/2026-07-25-tui-pending-indicator-design.md) |
| 7 | Free-text answers | [`2026-07-25-free-text-answers-design.md`](specs/2026-07-25-free-text-answers-design.md) |
| 8 | Scheduled-tasks control surface | [`2026-07-25-scheduled-tasks-control-surface-design.md`](specs/2026-07-25-scheduled-tasks-control-surface-design.md) |
| 9 | Image paste | [`2026-07-25-tui-clipboard-image-paste-design.md`](specs/2026-07-25-tui-clipboard-image-paste-design.md) |

## Dependencies and build order

- **Reasoning-effort control (1)** is the foundation for **Thinking display (2)** — thinking display only
  renders what effort causes the model to emit.
- **Theme system (3)** is the foundation for **Callouts (4)**, **Clickable links (5)**, and the styling of
  the **Pending indicator (6)** — each adds theme roles.
- **Scheduled-tasks (8)**, **Free-text answers (7)**, and **Image paste (9)** are independent.

Implementation proceeds serially in this order:

1. Reasoning-effort control
2. Thinking display
3. Theme system
4. Callouts
5. Clickable links
6. Pending indicator
7. Free-text answers
8. Scheduled-tasks control surface
9. Image paste

## Working method

- **Test-driven development** — red/green tests for each unit of behavior, per spec.
- **Loosely coupled, testable components** — new logic goes behind injectable seams (openers, resolvers,
  clocks, sinks, control services) with pure, separately-tested cores; clean, stylistically consistent code
  matching existing conventions.
- **Per-spec validation** — run only the test subset related to the spec under implementation. After each
  spec: a code review; critical/important findings fixed before proceeding, minor/low deferred to the end.
- **Commit + push after each spec** on this branch.
- **Final phase** — after all specs: full test suite, full review + security review, fix everything
  (including deferred minor/low findings), then open and complete a pull request, pull latest main, build,
  and install the latest `coda` locally.
