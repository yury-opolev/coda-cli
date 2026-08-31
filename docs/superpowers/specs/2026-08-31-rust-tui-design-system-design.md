# Rust TUI design system

**Date:** 2026-08-31
**Status:** Approved
**Scope:** `rust/crates/coda-tui`

## Problem

The Rust TUI works, but it has no foundation for new surfaces.

`app.rs` holds 3,388 lines of production code across 112 methods: the event
loop, 19 slash-command handlers, engine RPC, clipboard, pointer gestures,
render orchestration, markdown export and image handling. It has 287 lines of
tests, because almost nothing in it can be exercised without a live engine.

There are three unrelated modal mechanisms, each with its own key routing,
outcome type and draw path:

| Mechanism | Lives in | Outcome type |
|---|---|---|
| `PendingPrompt` | `state.rs`, drawn by `draw_prompt` | none — handled inline |
| `Browser` | `overlay.rs` + `browsers.rs` | `Intent` |
| `Form` | `widgets.rs` | `FormOutcome` |

`draw()` takes nine arguments, and the settings form had to be rendered as a
second pass because there was no room for a tenth.

The visual inconsistencies reported during use — border weight, padding, prompt
inset, banner placement, focus visibility — are symptoms. There is nowhere for a
design language to live, so each surface reinvents one. Forty-four glyph
literals (20 distinct) are spread across eight files, and although about sixty
theme `Role`s exist, nothing requires a surface to use them.

The C# build solved this. It repeats one structure across six browsers —
`Controller`, `KeyMap`, `Models`, `Overlay`, `TableSource` — and has a real form
type (`McpEditorForm`) with typed fields and save/cancel events. C# gets its
widgets from Terminal.Gui. Ratatui is immediate-mode and provides none, which is
why the Rust port hand-rolls each surface.

## Goals

- Adding a surface is a local change: one file plus one `SurfaceAction` variant.
- Every surface is testable without an engine or a terminal.
- The visual language is written down and enforced by tests, not by discipline.
- All C# surfaces are expressible, including multi-step wizards and split panes.
- Migration is incremental: every phase ends green and shippable.

## Non-goals

- Rewriting the reducer (`state.rs`) or the transcript renderer.
- Changing the engine wire protocol.
- Theme redesign. New roles are added; existing palettes keep their colours.

## Architecture

### The `Surface` contract

```rust
pub trait Surface {
    fn title(&self) -> String;
    fn hints(&self) -> String;
    fn placement(&self) -> Placement;
    fn modality(&self) -> Modality;

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome;
    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>>;
    fn cursor(&self, area: Rect) -> Option<(u16, u16)>;
}
```

**A surface cannot reach the engine.** No `App`, no async, no RPC, no I/O. This
is the load-bearing constraint: it is what makes every surface a pure state
machine that can be tested with a key event and an assertion, and it is the
direct fix for the 287-tests-per-3,388-lines ratio.

Surfaces render to `Line`s rather than into a `Frame`, matching the existing
`widgets` module. The caret, which lines cannot express, is returned separately
by `cursor`.

**A surface returns at most `area.height` lines**, having scrolled internally so
that whatever currently has focus is visible. The stack does not scroll on a
surface's behalf: only the surface knows which of its rows matters. Scrolling
must key off the focused element's row range, not off the caret, because
controls such as a switch or a radio group have no caret and would otherwise be
scrolled out of view exactly when focused.

### Outcomes

```rust
pub enum SurfaceOutcome {
    Handled,                    // consumed; stay open
    Ignored,                    // fall through to the global keymap
    Close,                      // pop this surface
    Emit(SurfaceAction),        // ask the app to act
    Push(Box<dyn Surface>),     // detail view, wizard step
    Replace(Box<dyn Surface>),  // wizard advance
}
```

`SurfaceAction` is the only channel from a surface to the application —
`SaveSettings`, `ResumeSession(String)`, `SetModel { .. }`, `RunCommand(String)`
and so on. `app/actions.rs` interprets it and performs the asynchronous work.

`Ignored` matters: it is what keeps global keys such as `Ctrl+C` working while a
surface is open.

### Placement

```rust
pub enum Placement {
    Modal { width_pct: u16, height_pct: u16 },
    Full,
    Split { side: Side, width_pct: u16 },
    Inline { max_rows: u16 },
}
```

The surface declares its own placement; the caller does not choose. This is what
makes a split-pane diff review and a full-screen wizard fit without
special-casing at each call site.

**Degradation.** When the terminal is too small for the requested placement, the
surface degrades — `Split` to `Modal`, `Modal` to `Full` — rather than clipping.
A surface never draws outside its area.

### Modality

```rust
pub enum Modality { Normal, Exclusive }
```

An `Exclusive` surface refuses anything pushed above it and cannot be popped by
`Esc` alone. This preserves today's rule — *a prompt from the engine outranks a
browser: it blocks the turn* — as an explicit property rather than as the order
of `if` statements in `on_key`.

### The stack

`SurfaceStack` owns `Vec<Box<dyn Surface>>`.

- **Keys** go to the top surface first. `Ignored` falls through to the global keymap.
- **Rendering** draws the shell, then surfaces bottom-up, so a detail view sits over its list.
- **Caret** comes from the top surface only.
- **`Esc`** pops one level unless the surface consumed it or is `Exclusive`.

### What this collapses

| Today | Becomes |
|---|---|
| `PendingPrompt` | `PromptSurface`, `Exclusive` |
| `Browser` | `ListSurface`, built on a new `Table` control |
| `Form` | `FormSurface` |
| Command completions | `CompletionSurface`, `Inline` |

## Visual language

### Chrome

- **Modals** keep a conventional box border with a title, in `Role::PromptAccent`.
  A modal floats and needs left and right edges to read as bounded.
- **The composer** keeps its half-block edges (`▄` top, `▀` bottom) painted
  against the shell background, so the panel appears to bleed half a row outward.
- **Inline** surfaces have no border, only the panel background.
- **Full** surfaces have no border; they own the screen.

### Spacing

| Rule | Value |
|---|---|
| Modal padding | 1 column horizontal, 0 vertical |
| Control separation | 1 blank row |
| Focus gutter | 2 cells (`❯ ` or `  `) |
| Composer prompt inset | 1 space before the glyph |
| Transcript blocks | 1 blank row after each |

Vertical modal padding is deliberately zero: modals are already capped to a
fraction of the screen and cannot spare the rows.

### Focus

Three layered signals, so none is load-bearing alone:

1. **Background band** across every row of the focused control — the primary
   signal, findable in peripheral vision, and it scales to multi-row controls so
   a six-option radio group still reads as one focused unit.
2. **Accent label**, confirming which control.
3. **`❯` gutter marker**, the fallback that survives a monochrome terminal.

This adds two theme roles, `FocusBackground` and `FocusText`. A theme with no
distinct band colour falls back to the accent label and marker.

**Inversion is reserved for the selected row inside a list.** Focus and
selection must be distinguishable at the same time: which control has focus, and
which row within it is chosen.

### State must never depend on colour alone

| State | Non-colour signal |
|---|---|
| Focused control | `❯ ` gutter |
| Selected row | reversed background |
| Radio chosen | `(●)` against `( )` |
| Switch on/off | `[● ]` against `[ ●]` |
| Select open/closed | `▲` against `▼` |

### Two enforced rules

- **One glyph table.** All twenty glyphs move to `render/glyphs.rs` as named
  constants. A test asserts no `\u{..}` literal exists elsewhere in the crate.
- **Roles only.** Surfaces style through `Role`, never a literal `Color`. A test
  asserts no `Color::` appears outside `theme.rs`.

Both are greppable, so both are pinned by a test. This matters more than the
rules themselves: an unenforced convention decays.

## Module layout

```
coda-tui/src/
  app/
    mod.rs          App, event loop, frame scheduling
    engine.rs       RPC calls and inbound event handling
    actions.rs      SurfaceAction interpreter — the only surface-to-engine bridge
    commands/       19 slash handlers: session.rs, config.rs, plugins.rs
    clipboard.rs    copy, paste, pointer gestures
  surface/
    mod.rs          Surface, SurfaceOutcome, Placement, Modality
    stack.rs        SurfaceStack
    prompt.rs       Exclusive: permission, question, plan approval
    list.rs         generic list surface
    form.rs         generic form surface
    completion.rs   Inline
    settings.rs models.rs skills.rs tasks.rs mcp.rs
    sessions.rs plugins.rs schedule.rs hooks.rs wizard.rs diff.rs
  widgets/
    mod.rs, static_text.rs, text_input.rs, text_area.rs,
    select.rs, radio.rs, switch.rs, table.rs
  render/
    draw.rs         shell layout; composer, status and modal chrome drawing
    glyphs.rs       every glyph, single source
  state.rs, transcript.rs, composer.rs, viewport.rs, selection.rs,
  keymap.rs, config.rs, branding.rs, ...
```

`transcript.rs` and `composer.rs` stay at the top level. They are models that
happen to render, not renderers; filing them under `render/` would misname them
and force a split that buys nothing.

## Data flow

```
key ──► SurfaceStack.top ──► SurfaceOutcome
                              ├─ Handled | Ignored  (Ignored → global keymap)
                              ├─ Close | Push | Replace → stack mutates
                              └─ Emit(action) → app/actions.rs → engine RPC (async)

engine event ──► UiState reducer ──► transcript
                └─► app may push a surface (permission prompt)

render ──► shell ──► surfaces bottom-up ──► caret from top surface only
```

Surfaces emit intent synchronously; only `app/actions.rs` awaits. No surface
holds a connection handle.

## Error handling

- Surfaces perform no I/O and therefore cannot fail. Failures occur in
  `actions.rs` and are reported as transcript notices, the existing pattern.
- Placement degrades rather than clipping when the terminal is too small.
- The panic hook stays: restore the terminal before printing, so a panic never
  leaves a wedged console.
- A `SurfaceAction` that names a surface or control that no longer exists is
  ignored with a warning notice rather than silently substituting a default,
  because substituting would write the wrong setting.

## Testing

Development is test-first throughout: a failing test for each behaviour, then
the change that makes it pass.

- **Per surface.** Key to `SurfaceOutcome`, and render to lines, at several
  widths. No engine, no terminal.
- **Enforcement.** No `\u{..}` outside `glyphs.rs`; no `Color::` outside
  `theme.rs`; every surface returns a non-empty `hints()`; exactly one focus
  marker per rendered form.
- **Overflow.** Every surface renders at 40×10 without drawing outside its area.
- **Integration path.** Drive `SurfaceStack` with real key events and assert the
  emitted `SurfaceAction`. This is the guard against the recurring failure mode
  in this codebase: modules that each pass their own tests while nothing
  connects them. Unit tests cannot see it.
- **Mutation checks** on the load-bearing assertions — focus placement, key
  priority, modality, masking — confirming each fails when the behaviour breaks.

Run `cargo clean -p coda-tui` after any mutation experiment; stale artifacts
produce false results.

## Sequencing

Each phase ends green and shippable. Work may stop after any phase.

| Phase | Work | Risk |
|---|---|---|
| 0 | `glyphs.rs`, `render/` split, focus band, new roles, enforcement tests | None — no behaviour change |
| 1 | `Surface` and `SurfaceStack`; migrate the settings form as first adopter | Low — newest, already isolated |
| 2 | `PromptSurface` as `Exclusive` | **Highest** — blocking semantics |
| 3 | `Table` control and `ListSurface`; migrate six browsers, one per commit | Medium, repetitive |
| 4 | Completions become an `Inline` surface | Low |
| 5 | Split `app.rs` into `commands/`, `engine.rs`, `clipboard.rs` | Low — much smaller by then |
| 6 | New surfaces: wizard, diff review, model picker, theme editor | The payoff |

Phase 2 is early by choice. Modality is the one place where a mistake breaks a
blocking permission gate, and that is better found while the design is fresh
than at phase 5.

Phase 0 alone resolves the reported visual inconsistencies. Phases 0 to 3 give
the foundation. Phase 6 is where new surfaces become cheap.

**The first implementation plan covers phases 0 to 3** — through the six
migrated browsers, at which point every existing surface runs on the new
foundation and the three modal mechanisms have become one. Phases 4 to 6 are
listed here so the design is judged against where it leads, but they get their
own plans. Planning all seven at once would produce a document too large to
follow and would commit to details of phase 6 before phase 3 has taught us
anything.

## Review

Each phase gets a code review before the next begins; critical and important
findings are fixed before moving on. After phase 6: a full review and an OWASP
security review, with all findings and any deferred minor items resolved.
