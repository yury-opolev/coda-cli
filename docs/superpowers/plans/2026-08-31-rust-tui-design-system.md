# Rust TUI Design System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace three ad-hoc modal mechanisms in `coda-tui` with one `Surface` abstraction, and put the visual language in one enforced place.

**Architecture:** A `Surface` trait that cannot reach the engine — surfaces are pure state machines returning `SurfaceOutcome`, and only `app/actions.rs` performs async work. A `SurfaceStack` routes keys to the top surface and renders bottom-up. Placement and modality are declared by the surface rather than chosen by the caller.

**Tech Stack:** Rust 1.86, ratatui 0.30, crossterm 0.29. No rustup/clippy/rustfmt available; the feedback loop is `cargo test`.

**Spec:** `docs/superpowers/specs/2026-08-31-rust-tui-design-system-design.md`

**Scope:** Phases 0–3. Phases 4–6 get their own plan.

---

## Conventions for every task

- Run tests from `rust/`: `cargo test -p coda-tui <filter>`
- After any mutation experiment run `cargo clean -p coda-tui`; stale artifacts give false results.
- Count failures with `Select-String "test result: FAILED"`, never `"FAILED"` — test *names* contain that word.
- Verify warnings explicitly: `cargo test -p coda-tui 2>&1 | Select-String "^warning"`.
- PowerShell has no heredocs. Multi-line commit messages go through a file and `git commit -F`.

---

## File Structure

| File | Responsibility |
|---|---|
| `render/glyphs.rs` (create) | Every UI glyph as a named constant. Single source. |
| `render/mod.rs` (create) | Re-exports `draw` and `glyphs`. |
| `render/draw.rs` (move from `draw.rs`) | Shell layout; composer, status and modal chrome. |
| `surface/mod.rs` (create) | `Surface`, `SurfaceOutcome`, `SurfaceAction`, `Placement`, `Modality`, `Side`. |
| `surface/stack.rs` (create) | `SurfaceStack`: key routing, render order, modality enforcement. |
| `surface/form.rs` (create) | `FormSurface`: adapts a `widgets::Form` to `Surface`. |
| `surface/settings.rs` (create) | The `/settings` surface. Replaces `settings_form.rs`. |
| `surface/prompt.rs` (create) | `PromptSurface`, `Exclusive`. Replaces `draw_prompt`. |
| `surface/list.rs` (create) | `ListSurface`: adapts `widgets::Table` to `Surface`. |
| `widgets/table.rs` (create) | `Table` control: columns, rows, selection, filtering, paging. |
| `app/actions.rs` (create) | Interprets `SurfaceAction`. The only surface-to-engine bridge. |
| `theme.rs` (modify, coda-render) | Adds `FocusBackground`, `FocusText` roles. |
| `widgets.rs` (modify) | Focus band rendering; `Control::render` gains the band. |

---

# Phase 0 — Foundations

No behaviour change beyond the focus band. Every task here is independently shippable.

---

### Task 1: Glyph table

**Files:**
- Create: `rust/crates/coda-tui/src/render/glyphs.rs`
- Create: `rust/crates/coda-tui/src/render/mod.rs`
- Modify: `rust/crates/coda-tui/src/lib.rs`

- [ ] **Step 1: Write the failing test**

Create `rust/crates/coda-tui/tests/conventions.rs`:

```rust
//! Conventions enforced by test rather than by discipline.
//!
//! An unenforced convention decays. These two are greppable, so they are
//! cheap to enforce and expensive to violate by accident.

use std::path::Path;

/// Walks every `.rs` file under `src/`, returning (path, source).
fn sources() -> Vec<(String, String)> {
    fn walk(dir: &Path, out: &mut Vec<(String, String)>) {
        for entry in std::fs::read_dir(dir).expect("read src dir") {
            let path = entry.expect("dir entry").path();
            if path.is_dir() {
                walk(&path, out);
            } else if path.extension().is_some_and(|e| e == "rs") {
                let text = std::fs::read_to_string(&path).expect("read source");
                out.push((path.display().to_string(), text));
            }
        }
    }
    let mut out = Vec::new();
    walk(Path::new("src"), &mut out);
    out
}

/// Strips `#[cfg(test)]` modules so test fixtures are not judged as UI code.
///
/// Tests legitimately contain glyph literals — CJK strings for width
/// assertions, for example — and holding them to the UI rule would force
/// pointless indirection in test data.
fn without_test_modules(source: &str) -> String {
    match source.find("#[cfg(test)]") {
        Some(at) => source[..at].to_string(),
        None => source.to_string(),
    }
}

#[test]
fn glyph_literals_live_only_in_the_glyph_table() {
    let offenders: Vec<String> = sources()
        .into_iter()
        .filter(|(path, _)| !path.replace('\\', "/").ends_with("render/glyphs.rs"))
        .filter_map(|(path, source)| {
            let code = without_test_modules(&source);
            code.contains("\\u{").then(|| path)
        })
        .collect();

    assert!(
        offenders.is_empty(),
        "glyph literals must live in render/glyphs.rs, found them in: {offenders:#?}"
    );
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-tui --test conventions glyph_literals -- --nocapture`
Expected: FAIL listing `draw.rs`, `widgets.rs`, `transcript.rs`, `browsers.rs`, `pin.rs`, `app.rs`, `state.rs`.

- [ ] **Step 3: Create the glyph table**

Create `rust/crates/coda-tui/src/render/glyphs.rs`:

```rust
//! Every glyph the interface draws.
//!
//! Kept in one place so a change to the visual language is one edit rather
//! than a search across eight files, and so a reviewer can see the whole
//! vocabulary at once. Pinned by `tests/conventions.rs`.

// --- Prompts and focus ---------------------------------------------------

/// Composer prompt, and the focus marker on the active control.
pub const PROMPT: &str = "\u{276F}"; // ❯
/// Composer prompt while the agent is busy.
pub const BUSY: &str = "\u{22EF}"; // ⋯
/// Marks the highlighted row of an open dropdown.
pub const OPTION_SELECTED: &str = "\u{203A}"; // ›

// --- Composer chrome -----------------------------------------------------

/// Composer top edge: a lower half block, so the panel appears to begin half
/// a row above its first content row.
pub const COMPOSER_TOP: &str = "\u{2584}"; // ▄
/// Composer bottom edge, mirroring [`COMPOSER_TOP`].
pub const COMPOSER_BOTTOM: &str = "\u{2580}"; // ▀

// --- Controls ------------------------------------------------------------

/// Filled dot: chosen radio option, switch knob, bullet.
pub const DOT: &str = "\u{25CF}"; // ●
/// Hollow dot: an unset state in a list.
pub const DOT_HOLLOW: &str = "\u{25CB}"; // ○
/// Bullet in prose, and the masking character for secrets.
pub const BULLET: &str = "\u{2022}"; // •
/// Dropdown closed.
pub const CHEVRON_DOWN: &str = "\u{25BC}"; // ▼
/// Dropdown open.
pub const CHEVRON_UP: &str = "\u{25B2}"; // ▲
/// Filled square, for enabled entries in a browser.
pub const SQUARE: &str = "\u{25A0}"; // ■

// --- Status --------------------------------------------------------------

pub const CHECK: &str = "\u{2713}"; // ✓
pub const CROSS: &str = "\u{2717}"; // ✗
pub const ARROW_UP: &str = "\u{2191}"; // ↑
pub const ARROW_DOWN: &str = "\u{2193}"; // ↓
/// Em dash, shown where a value is absent.
pub const EM_DASH: &str = "\u{2014}"; // —

// --- Rules and borders ---------------------------------------------------

/// Horizontal rule inside transcript content.
pub const RULE: &str = "\u{2500}"; // ─
/// Vertical rule; also the modal border's side.
pub const RULE_VERTICAL: &str = "\u{2502}"; // │
/// Full block, used by the scrollbar thumb.
pub const BLOCK: &str = "\u{2588}"; // █
```

Create `rust/crates/coda-tui/src/render/mod.rs`:

```rust
//! Rendering: turning models into terminal output.

pub mod draw;
pub mod glyphs;
```

- [ ] **Step 4: Move `draw.rs` into `render/` and replace every literal**

```powershell
cd C:\Users\yurio\Documents\github\coda-cli\rust\crates\coda-tui\src
git mv draw.rs render/draw.rs
```

In `lib.rs`, replace `pub mod draw;` with `pub mod render;` and add
`pub use render::draw;` so existing `crate::draw::` paths keep working.

Then in each of `render/draw.rs`, `widgets.rs`, `transcript.rs`, `browsers.rs`,
`pin.rs`, `app.rs`, `state.rs`: replace every `"\u{XXXX}"` literal with the
matching `glyphs::` constant, adding `use crate::render::glyphs;` at the top.

Mapping (all 19 UI glyphs; `\u{6587}` in `selection.rs` is a test fixture and stays):

| Literal | Constant |
|---|---|
| `\u{276F}` | `glyphs::PROMPT` |
| `\u{22EF}` | `glyphs::BUSY` |
| `\u{203A}` | `glyphs::OPTION_SELECTED` |
| `\u{2584}` | `glyphs::COMPOSER_TOP` |
| `\u{2580}` | `glyphs::COMPOSER_BOTTOM` |
| `\u{25CF}` | `glyphs::DOT` |
| `\u{25CB}` | `glyphs::DOT_HOLLOW` |
| `\u{2022}` | `glyphs::BULLET` |
| `\u{25BC}` | `glyphs::CHEVRON_DOWN` |
| `\u{25B2}` | `glyphs::CHEVRON_UP` |
| `\u{25A0}` | `glyphs::SQUARE` |
| `\u{2713}` | `glyphs::CHECK` |
| `\u{2717}` | `glyphs::CROSS` |
| `\u{2191}` | `glyphs::ARROW_UP` |
| `\u{2193}` | `glyphs::ARROW_DOWN` |
| `\u{2014}` | `glyphs::EM_DASH` |
| `\u{2500}` | `glyphs::RULE` |
| `\u{2502}` | `glyphs::RULE_VERTICAL` |
| `\u{2588}` | `glyphs::BLOCK` |

Where a literal is used in a `const` (for example `TOP_EDGE_GLYPH`), replace the
const's value with the `glyphs::` constant rather than deleting the const.

- [ ] **Step 5: Run the full crate suite**

Run: `cargo test -p coda-tui`
Expected: all pass, including `glyph_literals_live_only_in_the_glyph_table`.

Then confirm no warnings:
Run: `cargo test -p coda-tui 2>&1 | Select-String "^warning"`
Expected: no output.

- [ ] **Step 6: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Move every glyph into one table, enforced by test"
```

---

### Task 2: Role-only styling, enforced

**Files:**
- Modify: `rust/crates/coda-tui/tests/conventions.rs`

- [ ] **Step 1: Write the failing test**

Append to `rust/crates/coda-tui/tests/conventions.rs`:

```rust
#[test]
fn colours_come_from_the_theme_not_from_literals() {
    let offenders: Vec<String> = sources()
        .into_iter()
        .filter_map(|(path, source)| {
            let code = without_test_modules(&source);
            // `Color::` naming a literal colour. Style methods that take a
            // Color obtained from the theme are fine, so only the enum path
            // counts as a violation.
            code.contains("Color::").then(|| path)
        })
        .collect();

    assert!(
        offenders.is_empty(),
        "colours must come from Role, not literals; found Color:: in: {offenders:#?}"
    );
}
```

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `cargo test -p coda-tui --test conventions colours_come_from -- --nocapture`
Expected: FAIL listing any file using `Color::`. If it passes immediately, the
convention already holds — keep the test as a ratchet and move to Step 4.

- [ ] **Step 3: Replace each literal colour with a `Role`**

For every offender, replace `Color::X` with `theme.fg(Role::Y)` choosing the
role whose meaning matches. If no role fits, add one to `Role` in
`rust/crates/coda-render/src/theme.rs` following Task 3's pattern rather than
reaching for a literal.

- [ ] **Step 4: Run tests**

Run: `cargo test -p coda-tui --test conventions`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Require theme roles instead of literal colours, enforced by test"
```

---

### Task 3: Focus roles

**Files:**
- Modify: `rust/crates/coda-render/src/theme.rs`

- [ ] **Step 1: Write the failing test**

Add to the `tests` module in `rust/crates/coda-render/src/theme.rs`:

```rust
#[test]
fn every_theme_defines_a_distinct_focus_band() {
    for theme in [Theme::warm_ember(), Theme::cool_dark()] {
        assert_ne!(
            theme.fg(Role::FocusBackground),
            theme.fg(Role::Background),
            "{}: the focus band must differ from the shell background, \
             or a focused control is invisible",
            theme.name
        );
        assert_ne!(
            theme.fg(Role::FocusBackground),
            theme.fg(Role::SelectionBackground),
            "{}: focus and selection must be distinguishable at the same time",
            theme.name
        );
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-render every_theme_defines -- --nocapture`
Expected: FAIL to compile — `Role::FocusBackground` does not exist.

- [ ] **Step 3: Add the roles**

In `rust/crates/coda-render/src/theme.rs`:

1. Add to `enum Role`, next to `SelectionText` / `SelectionBackground`:

```rust
    FocusText,
    FocusBackground,
```

2. Add to `struct ThemeColors`, next to `selection_text`:

```rust
    focus_text: ThemeColor,
    focus_background: ThemeColor,
```

3. In `warm_ember()`, next to its `selection_*` entries:

```rust
            focus_text: ThemeColor::new(240, 224, 208, Color::White),
            focus_background: ThemeColor::new(46, 38, 32, Color::DarkGrey),
```

4. In `cool_dark()`:

```rust
            focus_text: ThemeColor::new(226, 232, 240, Color::White),
            focus_background: ThemeColor::new(30, 38, 52, Color::DarkGrey),
```

5. In the `Role` match (near `Role::SelectionText => c.selection_text`):

```rust
            Role::FocusText => c.focus_text,
            Role::FocusBackground => c.focus_background,
```

- [ ] **Step 4: Run tests**

Run: `cargo test -p coda-render`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add rust/crates/coda-render
git commit -m "Add focus band roles to every theme"
```

---

### Task 4: Focus band on controls

**Files:**
- Modify: `rust/crates/coda-tui/src/widgets.rs`

- [ ] **Step 1: Write the failing test**

Add to the `tests` module in `rust/crates/coda-tui/src/widgets.rs`:

```rust
#[test]
fn a_focused_control_is_banded_on_every_row() {
    let radio = RadioGroup::new("Mode", options());
    let focused = radio.render(40, true, &theme());
    let unfocused = radio.render(40, false, &theme());

    let band = theme().fg(Role::FocusBackground);
    for (index, line) in focused.iter().enumerate() {
        assert!(
            line.spans.iter().all(|s| s.style.bg == Some(band)),
            "focused row {index} is missing the band"
        );
    }
    assert!(
        unfocused
            .iter()
            .flat_map(|l| l.spans.iter())
            .all(|s| s.style.bg != Some(band)),
        "an unfocused control must not be banded"
    );
}

#[test]
fn a_focused_switch_is_banded_even_though_it_has_no_caret() {
    // The switch is the control that proves the band matters: it has no
    // caret, so without the band its focus would rest on the marker alone.
    let switch = Switch::new("Telemetry");
    let band = theme().fg(Role::FocusBackground);
    assert!(
        switch.render(40, true, &theme())[0]
            .spans
            .iter()
            .all(|s| s.style.bg == Some(band))
    );
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-tui --lib a_focused_control_is_banded -- --nocapture`
Expected: FAIL — spans have no background.

- [ ] **Step 3: Implement the band**

Add to `widgets.rs`, near `gutter`:

```rust
/// Applies the focus band to every span of every row.
///
/// The band is the primary focus signal; the accent label and the gutter
/// marker are layered beneath it so none is load-bearing alone and the state
/// survives a monochrome terminal.
fn band(lines: Vec<Line<'static>>, focused: bool, theme: &Theme) -> Vec<Line<'static>> {
    if !focused {
        return lines;
    }
    let bg = theme.fg(Role::FocusBackground);
    lines
        .into_iter()
        .map(|line| {
            let spans = line
                .spans
                .into_iter()
                .map(|span| {
                    let style = span.style.bg(bg);
                    Span::styled(span.content, style)
                })
                .collect::<Vec<_>>();
            Line::from(spans)
        })
        .collect()
}
```

Then wrap each control's `render` return value. For example in `Switch::render`,
change the final expression from `vec![Line::from(vec![...])]` to
`band(vec![Line::from(vec![...])], focused, theme)`. Do the same for
`StaticText` (passing `false`, since it never takes focus), `TextInput`,
`TextArea`, `Select` and `RadioGroup`.

- [ ] **Step 4: Run tests**

Run: `cargo test -p coda-tui`
Expected: PASS. The existing `form_marks_exactly_one_control_as_focused` and the
render tests must still pass.

- [ ] **Step 5: Verify the band actually discriminates**

Temporarily change `band` to return `lines` unchanged, run
`cargo test -p coda-tui --lib a_focused_control_is_banded`, confirm FAIL, then
restore and run `cargo clean -p coda-tui` before re-testing.

- [ ] **Step 6: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Band the focused control so focus is visible without the caret"
```

---

# Phase 1 — The Surface abstraction

---

### Task 5: The `Surface` contract

**Files:**
- Create: `rust/crates/coda-tui/src/surface/mod.rs`
- Modify: `rust/crates/coda-tui/src/lib.rs`

- [ ] **Step 1: Write the failing test**

Create `rust/crates/coda-tui/src/surface/mod.rs` with only its `tests` module
populated, then add:

```rust
#[cfg(test)]
mod tests {
    use super::*;

    struct Stub;
    impl Surface for Stub {
        fn as_any(&self) -> &dyn std::any::Any { self }
        fn title(&self) -> String { "Stub".into() }
        fn hints(&self) -> String { "Esc: close".into() }
        fn handle_key(&mut self, _key: KeyEvent) -> SurfaceOutcome {
            SurfaceOutcome::Handled
        }
        fn render(&self, _area: Rect, _theme: &Theme) -> Vec<Line<'static>> {
            Vec::new()
        }
    }

    #[test]
    fn a_surface_defaults_to_a_normal_modal() {
        let stub = Stub;
        assert_eq!(stub.modality(), Modality::Normal);
        assert!(matches!(stub.placement(), Placement::Modal { .. }));
        assert_eq!(stub.cursor(Rect::new(0, 0, 10, 10)), None);
    }

    #[test]
    fn placement_degrades_rather_than_clipping() {
        // A split too narrow to be legible becomes a modal; a modal too small
        // becomes full screen. A surface must never be asked to draw into an
        // area it cannot use.
        let split = Placement::Split { side: Side::Right, width_pct: 50 };
        assert!(matches!(
            split.resolve(Rect::new(0, 0, 30, 20)),
            Placement::Modal { .. }
        ));
        assert!(matches!(
            Placement::Modal { width_pct: 70, height_pct: 70 }
                .resolve(Rect::new(0, 0, 20, 6)),
            Placement::Full
        ));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-tui --lib surface::`
Expected: FAIL to compile.

- [ ] **Step 3: Write the contract**

Prepend to `rust/crates/coda-tui/src/surface/mod.rs`:

```rust
//! The `Surface` abstraction: one contract for every interactive overlay.
//!
//! A surface is a state machine that turns keys into outcomes and renders to
//! lines. It **cannot reach the engine** — no `App`, no async, no RPC, no I/O.
//! That constraint is what makes every surface testable with a key event and
//! an assertion, and it is why `SurfaceAction` exists: a surface states its
//! intent and the application performs the work.

use coda_render::theme::Theme;
use crossterm::event::KeyEvent;
use ratatui::layout::Rect;
use ratatui::text::Line;

pub mod stack;

/// Minimum usable dimensions for a split pane, below which it degrades.
const MIN_SPLIT_WIDTH: u16 = 40;
/// Minimum usable dimensions for a modal, below which it degrades to full.
const MIN_MODAL_WIDTH: u16 = 24;
const MIN_MODAL_HEIGHT: u16 = 8;

/// Which side a split pane docks to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Side {
    Left,
    Right,
}

/// Where a surface wants to be drawn.
///
/// Declared by the surface rather than chosen by the caller, so a split-pane
/// diff and a full-screen wizard need no special-casing at each call site.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Placement {
    Modal { width_pct: u16, height_pct: u16 },
    Full,
    Split { side: Side, width_pct: u16 },
    Inline { max_rows: u16 },
}

impl Placement {
    /// Degrades this placement until it fits `area`.
    ///
    /// Degrading rather than clipping means a small terminal shows a usable
    /// surface instead of a truncated one.
    pub fn resolve(self, area: Rect) -> Placement {
        match self {
            Placement::Split { .. } if area.width < MIN_SPLIT_WIDTH => {
                Placement::Modal { width_pct: 90, height_pct: 80 }.resolve(area)
            }
            Placement::Modal { .. }
                if area.width < MIN_MODAL_WIDTH || area.height < MIN_MODAL_HEIGHT =>
            {
                Placement::Full
            }
            other => other,
        }
    }
}

/// Whether a surface can be superseded.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Modality {
    /// Another surface may open above this one.
    Normal,
    /// Nothing may open above, and `Esc` alone cannot dismiss it.
    ///
    /// Used by engine prompts, which block the turn until answered.
    Exclusive,
}

/// Work only the application can do, requested by a surface.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SurfaceAction {
    SaveSettings,
    ResumeSession(String),
    RunCommand(String),
    AnswerPrompt { index: usize },
    DenyPrompt,
}

/// What a key did.
pub enum SurfaceOutcome {
    /// Consumed; stay open.
    Handled,
    /// Not consumed; the global keymap may act, so `Ctrl+C` keeps working.
    Ignored,
    /// Pop this surface.
    Close,
    /// Ask the application to act.
    Emit(SurfaceAction),
    /// Open another surface above this one.
    Push(Box<dyn Surface>),
    /// Replace this surface, for a wizard advancing a step.
    Replace(Box<dyn Surface>),
}

/// One interactive overlay.
pub trait Surface {
    fn title(&self) -> String;
    fn hints(&self) -> String;

    fn placement(&self) -> Placement {
        Placement::Modal { width_pct: 70, height_pct: 70 }
    }

    fn modality(&self) -> Modality {
        Modality::Normal
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome;

    /// Renders at most `area.height` lines, scrolled so the focused element is
    /// visible. Scrolling keys off the focused element's row range, not the
    /// caret: a switch and a radio group have no caret and would otherwise be
    /// scrolled out of view exactly when focused.
    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>>;

    fn cursor(&self, _area: Rect) -> Option<(u16, u16)> {
        None
    }

    /// Recovers the concrete type, so the action interpreter can read typed
    /// values back out of the surface that emitted an action.
    ///
    /// Deliberately on the trait from the start: adding it later would mean
    /// revisiting every implementor.
    fn as_any(&self) -> &dyn std::any::Any;
}
```

Add `pub mod surface;` to `lib.rs`.

- [ ] **Step 4: Run tests**

Run: `cargo test -p coda-tui --lib surface::`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Add the Surface contract"
```

---

### Task 6: `SurfaceStack`

**Files:**
- Create: `rust/crates/coda-tui/src/surface/stack.rs`

- [ ] **Step 1: Write the failing test**

Create `rust/crates/coda-tui/src/surface/stack.rs` and add its tests:

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::{KeyCode, KeyModifiers};

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    /// A surface that reports which keys it saw and can be told what to return.
    struct Probe {
        name: &'static str,
        modality: Modality,
        outcome_for_enter: Option<SurfaceAction>,
    }

    impl Probe {
        fn normal(name: &'static str) -> Self {
            Self { name, modality: Modality::Normal, outcome_for_enter: None }
        }
        fn exclusive(name: &'static str) -> Self {
            Self { name, modality: Modality::Exclusive, outcome_for_enter: None }
        }
    }

    impl Surface for Probe {
        fn as_any(&self) -> &dyn std::any::Any { self }
        fn title(&self) -> String { self.name.into() }
        fn hints(&self) -> String { String::new() }
        fn modality(&self) -> Modality { self.modality }
        fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
            match key.code {
                KeyCode::Enter => match self.outcome_for_enter.clone() {
                    Some(action) => SurfaceOutcome::Emit(action),
                    None => SurfaceOutcome::Handled,
                },
                KeyCode::Char('x') => SurfaceOutcome::Close,
                // Anything else falls through, so the stack's own rules apply.
                _ => SurfaceOutcome::Ignored,
            }
        }
        fn render(&self, _area: Rect, _theme: &Theme) -> Vec<Line<'static>> {
            vec![Line::from(self.name)]
        }
    }

    #[test]
    fn keys_go_to_the_top_surface() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("under")));
        stack.push(Box::new(Probe::normal("over")));
        assert_eq!(stack.top_title().as_deref(), Some("over"));
        assert!(matches!(stack.handle_key(key(KeyCode::Enter)), StackOutcome::Handled));
    }

    #[test]
    fn an_unhandled_key_falls_through_so_global_keys_keep_working() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("only")));
        // 'q' is Ignored by the probe; the stack must not swallow it.
        assert!(matches!(stack.handle_key(key(KeyCode::Char('q'))), StackOutcome::Ignored));
    }

    #[test]
    fn escape_pops_one_level() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("under")));
        stack.push(Box::new(Probe::normal("over")));
        stack.handle_key(key(KeyCode::Esc));
        assert_eq!(stack.top_title().as_deref(), Some("under"));
    }

    #[test]
    fn escape_cannot_dismiss_an_exclusive_surface() {
        // An engine prompt blocks the turn; letting Esc close it would answer
        // a permission question the user never answered.
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::exclusive("prompt")));
        stack.handle_key(key(KeyCode::Esc));
        assert_eq!(stack.top_title().as_deref(), Some("prompt"));
    }

    #[test]
    fn nothing_can_be_pushed_above_an_exclusive_surface() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::exclusive("prompt")));
        let rejected = stack.push(Box::new(Probe::normal("browser")));
        assert!(!rejected, "a surface opened above an exclusive prompt");
        assert_eq!(stack.top_title().as_deref(), Some("prompt"));
        assert_eq!(stack.len(), 1);
    }

    #[test]
    fn close_pops_the_surface_that_asked() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("only")));
        stack.handle_key(key(KeyCode::Char('x')));
        assert!(stack.is_empty());
    }

    #[test]
    fn an_emitted_action_reaches_the_caller() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe {
            name: "settings",
            modality: Modality::Normal,
            outcome_for_enter: Some(SurfaceAction::SaveSettings),
        }));
        match stack.handle_key(key(KeyCode::Enter)) {
            StackOutcome::Action(SurfaceAction::SaveSettings) => {}
            _ => panic!("the action did not reach the caller"),
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-tui --lib surface::stack`
Expected: FAIL to compile.

- [ ] **Step 3: Implement the stack**

Prepend to `rust/crates/coda-tui/src/surface/stack.rs`:

```rust
//! The surface stack: key routing, render order and modality enforcement.

use super::{Modality, Surface, SurfaceAction, SurfaceOutcome};
use coda_render::theme::Theme;
use crossterm::event::{KeyCode, KeyEvent};
use ratatui::layout::Rect;
use ratatui::text::Line;

/// What handling a key did to the stack as a whole.
pub enum StackOutcome {
    /// A surface consumed the key.
    Handled,
    /// No surface consumed it; the caller's global keymap may act.
    Ignored,
    /// A surface asked the application to do something.
    Action(SurfaceAction),
}

/// An ordered set of open surfaces. The last is on top.
#[derive(Default)]
pub struct SurfaceStack {
    surfaces: Vec<Box<dyn Surface>>,
}

impl SurfaceStack {
    pub fn len(&self) -> usize {
        self.surfaces.len()
    }

    pub fn is_empty(&self) -> bool {
        self.surfaces.is_empty()
    }

    pub fn top_title(&self) -> Option<String> {
        self.surfaces.last().map(|s| s.title())
    }

    pub fn top(&self) -> Option<&dyn Surface> {
        self.surfaces.last().map(|s| s.as_ref())
    }

    /// Opens a surface. Returns `false` when an exclusive surface refused it.
    pub fn push(&mut self, surface: Box<dyn Surface>) -> bool {
        if self.top_is_exclusive() {
            return false;
        }
        self.surfaces.push(surface);
        true
    }

    pub fn pop(&mut self) -> Option<Box<dyn Surface>> {
        self.surfaces.pop()
    }

    pub fn clear(&mut self) {
        self.surfaces.clear();
    }

    fn top_is_exclusive(&self) -> bool {
        self.surfaces
            .last()
            .map(|s| s.modality() == Modality::Exclusive)
            .unwrap_or(false)
    }

    pub fn handle_key(&mut self, key: KeyEvent) -> StackOutcome {
        let Some(surface) = self.surfaces.last_mut() else {
            return StackOutcome::Ignored;
        };

        match surface.handle_key(key) {
            SurfaceOutcome::Handled => StackOutcome::Handled,
            SurfaceOutcome::Close => {
                self.surfaces.pop();
                StackOutcome::Handled
            }
            SurfaceOutcome::Emit(action) => StackOutcome::Action(action),
            SurfaceOutcome::Push(next) => {
                self.push(next);
                StackOutcome::Handled
            }
            SurfaceOutcome::Replace(next) => {
                self.surfaces.pop();
                self.surfaces.push(next);
                StackOutcome::Handled
            }
            // The surface passed. Esc is the stack's own key, but an exclusive
            // surface blocks the turn and must be answered, not dismissed.
            SurfaceOutcome::Ignored => {
                if key.code == KeyCode::Esc && !self.top_is_exclusive() {
                    self.surfaces.pop();
                    StackOutcome::Handled
                } else {
                    StackOutcome::Ignored
                }
            }
        }
    }

    /// Renders every surface bottom-up, so a detail view sits over its list.
    pub fn render(&self, area: Rect, theme: &Theme) -> Vec<(Rect, Vec<Line<'static>>)> {
        self.surfaces
            .iter()
            .map(|surface| {
                let placement = surface.placement().resolve(area);
                let region = super::region_for(placement, area);
                (region, surface.render(region, theme))
            })
            .collect()
    }
}
```

Add `region_for` to `surface/mod.rs`:

```rust
/// Turns a resolved placement into a concrete region of `area`.
pub fn region_for(placement: Placement, area: Rect) -> Rect {
    match placement {
        Placement::Full => area,
        Placement::Modal { width_pct, height_pct } => {
            let w = (area.width as u32 * width_pct as u32 / 100) as u16;
            let h = (area.height as u32 * height_pct as u32 / 100) as u16;
            Rect::new(
                area.x + (area.width.saturating_sub(w)) / 2,
                area.y + (area.height.saturating_sub(h)) / 2,
                w.max(1),
                h.max(1),
            )
        }
        Placement::Split { side, width_pct } => {
            let w = (area.width as u32 * width_pct as u32 / 100) as u16;
            let w = w.max(1).min(area.width);
            match side {
                Side::Right => Rect::new(area.right() - w, area.y, w, area.height),
                Side::Left => Rect::new(area.x, area.y, w, area.height),
            }
        }
        Placement::Inline { max_rows } => {
            let h = max_rows.min(area.height).max(1);
            Rect::new(area.x, area.bottom() - h, area.width, h)
        }
    }
}
```

- [ ] **Step 4: Run tests**

Run: `cargo test -p coda-tui --lib surface::`
Expected: PASS, all seven stack tests.

- [ ] **Step 5: Verify modality actually discriminates**

Temporarily make `top_is_exclusive` return `false` always. Run
`cargo test -p coda-tui --lib surface::stack`. Expected: FAIL on
`escape_cannot_dismiss_an_exclusive_surface` and
`nothing_can_be_pushed_above_an_exclusive_surface`. Restore, then
`cargo clean -p coda-tui`.

- [ ] **Step 6: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Add SurfaceStack with modality enforcement"
```

---

### Task 7: `FormSurface` and the settings surface

**Files:**
- Create: `rust/crates/coda-tui/src/surface/form.rs`
- Create: `rust/crates/coda-tui/src/surface/settings.rs`
- Delete: `rust/crates/coda-tui/src/settings_form.rs` (contents move to `surface/settings.rs`)

- [ ] **Step 1: Write the failing test**

Create `rust/crates/coda-tui/src/surface/settings.rs` with tests:

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::{KeyCode, KeyModifiers};

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn settings() -> Settings {
        Settings::empty_at(std::env::temp_dir().join("coda-settings-surface-test.json"))
    }

    #[test]
    fn enter_emits_a_save_action_rather_than_saving_directly() {
        // The surface must not touch the filesystem: that is the app's job.
        let mut surface = SettingsSurface::new(&settings());
        match surface.handle_key(key(KeyCode::Enter)) {
            SurfaceOutcome::Emit(SurfaceAction::SaveSettings) => {}
            _ => panic!("Enter did not emit SaveSettings"),
        }
    }

    #[test]
    fn escape_closes_the_surface() {
        let mut surface = SettingsSurface::new(&settings());
        assert!(matches!(
            surface.handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Close
        ));
    }

    #[test]
    fn the_surface_opens_on_configured_values() {
        let mut s = settings();
        s.set_theme("high-contrast");
        let surface = SettingsSurface::new(&s);
        assert_eq!(surface.theme_index(), 2);
    }

    #[test]
    fn it_renders_no_more_lines_than_its_area_allows() {
        let surface = SettingsSurface::new(&settings());
        let area = Rect::new(0, 0, 60, 8);
        assert!(surface.render(area, &Theme::default()).len() <= area.height as usize);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-tui --lib surface::settings`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `FormSurface`**

Create `rust/crates/coda-tui/src/surface/form.rs`:

```rust
//! Adapts a `widgets::Form` to the `Surface` contract.
//!
//! Everything about focus, key routing and scrolling already lives in `Form`.
//! This is the thin layer that gives it a title, hints and an outcome, so a
//! form-shaped surface needs no key handling of its own.

use super::{Surface, SurfaceOutcome};
use crate::widgets::{Form, FormOutcome};
use coda_render::theme::Theme;
use crossterm::event::KeyEvent;
use ratatui::layout::Rect;
use ratatui::text::Line;

pub struct FormSurface {
    pub form: Form,
    title: String,
    hints: String,
}

impl FormSurface {
    pub fn new(title: impl Into<String>, hints: impl Into<String>, form: Form) -> Self {
        Self { form, title: title.into(), hints: hints.into() }
    }

    /// Maps a form outcome onto a surface outcome, leaving the caller to
    /// decide what `Submit` means.
    pub fn dispatch(&mut self, key: KeyEvent) -> FormOutcome {
        self.form.handle_key(key)
    }
}

impl Surface for FormSurface {
    fn title(&self) -> String {
        self.title.clone()
    }

    fn hints(&self) -> String {
        self.hints.clone()
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        match self.form.handle_key(key) {
            FormOutcome::Consumed => SurfaceOutcome::Handled,
            FormOutcome::Ignored => SurfaceOutcome::Ignored,
            FormOutcome::Cancel => SurfaceOutcome::Close,
            // A bare FormSurface has no action of its own; wrappers override.
            FormOutcome::Submit => SurfaceOutcome::Close,
        }
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        let mut lines = self.form.render(area.width, theme);
        let (_, focus_end) = self.form.focused_rows(area.width, theme);
        let scroll = focus_end.saturating_sub(area.height) as usize;
        if scroll > 0 {
            lines.drain(..scroll.min(lines.len()));
        }
        lines.truncate(area.height as usize);
        lines
    }

    fn cursor(&self, area: Rect) -> Option<(u16, u16)> {
        let theme = Theme::default();
        let (column, row) = self.form.cursor(area.width, &theme)?;
        let (_, focus_end) = self.form.focused_rows(area.width, &theme);
        let scroll = focus_end.saturating_sub(area.height);
        Some((column, row.saturating_sub(scroll)))
    }
}
```

- [ ] **Step 4: Implement `SettingsSurface`**

Move the body of `settings_form.rs` into `surface/settings.rs`, wrapping it:

```rust
//! The `/settings` surface.

use super::{Surface, SurfaceAction, SurfaceOutcome};
use crate::config::{ConfigError, Paths, Settings};
use crate::widgets::{Form, FormOutcome, RadioGroup, Select, StaticText, Switch};
use coda_render::theme::{Role, Theme};
use crossterm::event::KeyEvent;
use ratatui::layout::Rect;
use ratatui::text::Line;

pub const PERMISSION_MODES: &[&str] = &["ask", "auto", "plan"];
pub const THEMES: &[&str] = &["warm-ember", "cool-slate", "high-contrast"];
pub const TOOL_DISPLAY_MODES: &[&str] = &["compact", "expanded"];

mod index {
    pub const PERMISSION: usize = 1;
    pub const THEME: usize = 2;
    pub const TOOL_DISPLAY: usize = 3;
    pub const TELEMETRY: usize = 4;
}

pub struct SettingsSurface {
    form: Form,
}

impl SettingsSurface {
    pub fn new(settings: &Settings) -> Self {
        Self { form: build(settings) }
    }

    pub fn open(paths: &Paths) -> Self {
        let settings =
            Settings::load(paths).unwrap_or_else(|_| Settings::empty_at(paths.settings()));
        Self::new(&settings)
    }

    pub fn theme_index(&self) -> usize {
        selected(&self.form, index::THEME).unwrap_or(0)
    }

    /// Applies the form's values onto `settings` and saves.
    pub fn apply(&self, settings: &mut Settings) -> Result<(), ConfigError> {
        apply(&self.form, settings)
    }
}

impl Surface for SettingsSurface {
    fn title(&self) -> String {
        "Settings".into()
    }

    fn hints(&self) -> String {
        format!(
            "Tab: next    {}{}: change    Enter: save    Esc: cancel",
            crate::render::glyphs::ARROW_UP,
            crate::render::glyphs::ARROW_DOWN
        )
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        match self.form.handle_key(key) {
            FormOutcome::Consumed => SurfaceOutcome::Handled,
            FormOutcome::Ignored => SurfaceOutcome::Ignored,
            FormOutcome::Cancel => SurfaceOutcome::Close,
            FormOutcome::Submit => SurfaceOutcome::Emit(SurfaceAction::SaveSettings),
        }
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        let mut lines = self.form.render(area.width, theme);
        let (_, focus_end) = self.form.focused_rows(area.width, theme);
        let scroll = focus_end.saturating_sub(area.height) as usize;
        if scroll > 0 {
            lines.drain(..scroll.min(lines.len()));
        }
        lines.truncate(area.height as usize);
        lines
    }

    fn cursor(&self, area: Rect) -> Option<(u16, u16)> {
        let theme = Theme::default();
        let (column, row) = self.form.cursor(area.width, &theme)?;
        let (_, focus_end) = self.form.focused_rows(area.width, &theme);
        Some((column, row.saturating_sub(focus_end.saturating_sub(area.height))))
    }
}
```

Keep `build`, `selected`, `apply` and `position_or_first` from the old
`settings_form.rs` as private functions in this module, along with their tests.

- [ ] **Step 5: Wire into `app.rs`**

Replace the `settings_form: Option<crate::widgets::Form>` field with
`surfaces: SurfaceStack`. Replace `open_settings_form` with:

```rust
    fn open_settings_form(&mut self) {
        self.surfaces
            .push(Box::new(crate::surface::settings::SettingsSurface::open(&self.paths)));
        self.dirty = true;
    }
```

Replace `on_settings_key` with a stack-routing branch in `on_key`, placed where
the settings-form branch was:

```rust
        if !self.surfaces.is_empty() {
            match self.surfaces.handle_key(key) {
                StackOutcome::Handled => {
                    self.dirty = true;
                    return;
                }
                StackOutcome::Action(action) => {
                    self.dirty = true;
                    self.apply_surface_action(action).await;
                    return;
                }
                // Fall through to the global keymap so Ctrl+C still exits.
                StackOutcome::Ignored => {}
            }
        }
```

- [ ] **Step 6: Add the action interpreter**

Create `rust/crates/coda-tui/src/app/actions.rs` — or, if `app.rs` has not yet
been split, add this method to `impl App` in `app.rs`:

```rust
    /// Performs the work a surface asked for.
    ///
    /// This is the only bridge from a surface to the engine and the
    /// filesystem; surfaces themselves do no I/O.
    async fn apply_surface_action(&mut self, action: SurfaceAction) {
        match action {
            SurfaceAction::SaveSettings => {
                // Take the surface first: the modal closes whether or not the
                // save succeeds, so a broken settings file cannot trap the
                // user in a form they can only leave by killing the process.
                let Some(surface) = self.surfaces.pop() else { return };
                let Some(settings_surface) = surface
                    .as_any()
                    .downcast_ref::<crate::surface::settings::SettingsSurface>()
                else {
                    return;
                };
                let mut settings = crate::config::Settings::load(&self.paths)
                    .unwrap_or_else(|_| {
                        crate::config::Settings::empty_at(self.paths.settings())
                    });
                match settings_surface.apply(&mut settings) {
                    Ok(()) => self.notice(
                        "Settings saved. Some changes apply on restart.",
                        NoticeLevel::Info,
                    ),
                    Err(err) => self.notice(
                        format!("Could not save settings: {err}"),
                        NoticeLevel::Error,
                    ),
                }
            }
            SurfaceAction::ResumeSession(id) => self.resume_to_session(&id).await,
            SurfaceAction::RunCommand(command) => self.run_command(&command).await,
            SurfaceAction::AnswerPrompt { index } => self.answer_prompt(index).await,
            SurfaceAction::DenyPrompt => self.deny_prompt().await,
        }
    }
```

This requires `Surface::as_any`, which the trait declares from Task 5. Every
implementor provides `fn as_any(&self) -> &dyn std::any::Any { self }`.

- [ ] **Step 7: Render the stack**

In `app.rs`'s draw closure, replace the settings-form second pass with:

```rust
            for (region, lines) in surfaces.render(frame.area(), theme) {
                draw::draw_surface_lines(frame, region, &title, &hints, lines, theme);
            }
```

Add `draw_surface_lines` to `render/draw.rs`, modelled on the existing
`draw_form` but taking pre-rendered lines:

```rust
/// Draws pre-rendered surface lines inside the standard modal chrome.
pub fn draw_surface_lines(
    frame: &mut Frame,
    region: Rect,
    title: &str,
    hint: &str,
    lines: Vec<Line<'static>>,
    theme: &Theme,
) {
    frame.render_widget(Clear, region);
    let block = Block::default()
        .title(format!(" {title} "))
        .borders(Borders::ALL)
        .border_style(theme.style(Role::PromptAccent))
        .padding(MODAL_PADDING)
        .style(theme.surface());
    let inner = block.inner(region);
    frame.render_widget(block, region);
    if inner.height == 0 {
        return;
    }
    let chunks = Layout::default()
        .direction(Direction::Vertical)
        .constraints([Constraint::Min(1), Constraint::Length(1)])
        .split(inner);
    frame.render_widget(Paragraph::new(lines), chunks[0]);
    frame.render_widget(
        Paragraph::new(hint.to_string()).style(theme.style(Role::Notification)),
        chunks[1],
    );
}
```

- [ ] **Step 8: Run the whole crate**

Run: `cargo test -p coda-tui`
Expected: PASS. Delete `settings_form.rs` and its `lib.rs` entry; update the two
render tests that referenced `coda_tui::settings_form::build` to use
`SettingsSurface::new(..)` instead.

- [ ] **Step 9: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Move the settings form onto the Surface abstraction"
```

---

# Phase 2 — The prompt surface

The highest-risk phase: modality is what keeps a blocking permission gate blocking.

---

### Task 8: `PromptSurface`

**Files:**
- Create: `rust/crates/coda-tui/src/surface/prompt.rs`
- Modify: `rust/crates/coda-tui/src/app.rs`

- [ ] **Step 1: Write the failing test**

Create `rust/crates/coda-tui/src/surface/prompt.rs` with tests:

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::{KeyCode, KeyModifiers};

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn permission() -> PromptSurface {
        PromptSurface::new(PendingPrompt::Permission {
            tool: "write_file".into(),
            preview: "src/main.rs".into(),
        })
    }

    #[test]
    fn a_prompt_is_exclusive() {
        // This single property is what stops a browser opening over a
        // permission gate and what stops Esc answering it.
        assert_eq!(permission().modality(), Modality::Exclusive);
    }

    #[test]
    fn y_allows_and_n_denies() {
        let mut prompt = permission();
        assert!(matches!(
            prompt.handle_key(key(KeyCode::Char('y'))),
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { index: 0 })
        ));

        let mut prompt = permission();
        assert!(matches!(
            prompt.handle_key(key(KeyCode::Char('n'))),
            SurfaceOutcome::Emit(SurfaceAction::DenyPrompt)
        ));
    }

    #[test]
    fn escape_denies_rather_than_closing() {
        // Closing without answering would leave the engine waiting forever.
        let mut prompt = permission();
        assert!(matches!(
            prompt.handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Emit(SurfaceAction::DenyPrompt)
        ));
    }

    #[test]
    fn an_unrelated_key_is_swallowed_not_ignored() {
        // Ignored would let Esc-handling in the stack pop the prompt.
        let mut prompt = permission();
        assert!(matches!(
            prompt.handle_key(key(KeyCode::Char('z'))),
            SurfaceOutcome::Handled
        ));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-tui --lib surface::prompt`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

```rust
//! The engine prompt surface: permission, question and plan approval.
//!
//! `Exclusive`, because the engine is blocked until this is answered. Nothing
//! may open above it, and `Esc` denies rather than dismissing — closing
//! without answering would leave the turn waiting forever.

use super::{Modality, Surface, SurfaceAction, SurfaceOutcome};
use crate::state::PendingPrompt;
use coda_render::theme::{Role, Theme};
use crossterm::event::{KeyCode, KeyEvent};
use ratatui::layout::Rect;
use ratatui::text::{Line, Span};

pub struct PromptSurface {
    prompt: PendingPrompt,
    highlighted: usize,
}

impl PromptSurface {
    pub fn new(prompt: PendingPrompt) -> Self {
        Self { prompt, highlighted: 0 }
    }

    fn option_count(&self) -> usize {
        match &self.prompt {
            PendingPrompt::Question { options, .. } => options.len(),
            _ => 0,
        }
    }
}

impl Surface for PromptSurface {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn title(&self) -> String {
        match &self.prompt {
            PendingPrompt::Permission { .. } => "Permission required".into(),
            PendingPrompt::Question { .. } => "Question".into(),
            PendingPrompt::PlanApproval { .. } => "Approve plan?".into(),
        }
    }

    fn hints(&self) -> String {
        match &self.prompt {
            PendingPrompt::Question { .. } => {
                format!(
                    "{}{}: choose    Enter: answer",
                    crate::render::glyphs::ARROW_UP,
                    crate::render::glyphs::ARROW_DOWN
                )
            }
            _ => "y: allow    n: deny    Esc: deny".into(),
        }
    }

    fn modality(&self) -> Modality {
        Modality::Exclusive
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        match key.code {
            KeyCode::Char('y') | KeyCode::Char('Y') => {
                SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { index: 0 })
            }
            KeyCode::Char('n') | KeyCode::Char('N') | KeyCode::Esc => {
                SurfaceOutcome::Emit(SurfaceAction::DenyPrompt)
            }
            KeyCode::Up if self.highlighted > 0 => {
                self.highlighted -= 1;
                SurfaceOutcome::Handled
            }
            KeyCode::Down if self.highlighted + 1 < self.option_count() => {
                self.highlighted += 1;
                SurfaceOutcome::Handled
            }
            KeyCode::Enter if self.option_count() > 0 => {
                SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { index: self.highlighted })
            }
            // Everything else is swallowed. Returning Ignored would let the
            // stack's Esc handling pop a prompt the engine is waiting on.
            _ => SurfaceOutcome::Handled,
        }
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        let body = match &self.prompt {
            PendingPrompt::Permission { tool, preview } => format!("{tool}\n\n{preview}"),
            PendingPrompt::PlanApproval { plan } => plan.clone(),
            PendingPrompt::Question { question, options, .. } => {
                if options.is_empty() {
                    question.clone()
                } else {
                    let list = options
                        .iter()
                        .enumerate()
                        .map(|(i, option)| {
                            let marker = if i == self.highlighted {
                                crate::render::glyphs::OPTION_SELECTED
                            } else {
                                " "
                            };
                            format!("{marker} {}. {option}", i + 1)
                        })
                        .collect::<Vec<_>>()
                        .join("\n");
                    format!("{question}\n\n{list}")
                }
            }
        };

        let style = theme.style(Role::PromptText);
        body.lines()
            .flat_map(|line| {
                coda_render::text::wrap(&coda_render::text::sanitize(line), area.width as usize)
            })
            .map(|row| Line::from(Span::styled(row, style)))
            .take(area.height as usize)
            .collect()
    }
}
```

- [ ] **Step 4: Wire into `app.rs`**

Where `UiEvent::PromptRequested` currently sets `state.prompt`, also push the
surface:

```rust
        self.surfaces
            .push(Box::new(crate::surface::prompt::PromptSurface::new(prompt.clone())));
```

Remove the `if self.state.prompt.is_some() { self.on_prompt_key(key); return; }`
branch from `on_key` — the stack now handles it, and `Exclusive` guarantees the
prompt is on top. Remove the `draw_prompt` call from `draw_with_pin`.

- [ ] **Step 5: Add the integration test**

Add to `rust/crates/coda-tui/tests/surface_integration.rs`:

```rust
//! Tests that drive the stack the way the application does.
//!
//! Unit tests cannot see a handler that is never reached. These can.

use coda_tui::state::PendingPrompt;
use coda_tui::surface::prompt::PromptSurface;
use coda_tui::surface::settings::SettingsSurface;
use coda_tui::surface::stack::{StackOutcome, SurfaceStack};
use coda_tui::surface::SurfaceAction;
use coda_tui::config::Settings;
use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

fn key(code: KeyCode) -> KeyEvent {
    KeyEvent::new(code, KeyModifiers::NONE)
}

#[test]
fn a_prompt_blocks_a_browser_from_opening_over_it() {
    let mut stack = SurfaceStack::default();
    stack.push(Box::new(PromptSurface::new(PendingPrompt::Permission {
        tool: "write_file".into(),
        preview: "src/main.rs".into(),
    })));

    let settings = Settings::empty_at(std::env::temp_dir().join("coda-int-test.json"));
    assert!(
        !stack.push(Box::new(SettingsSurface::new(&settings))),
        "a settings surface opened over a blocking permission prompt"
    );
    assert_eq!(stack.len(), 1);
}

#[test]
fn answering_a_prompt_reaches_the_application() {
    let mut stack = SurfaceStack::default();
    stack.push(Box::new(PromptSurface::new(PendingPrompt::Permission {
        tool: "write_file".into(),
        preview: "x".into(),
    })));
    match stack.handle_key(key(KeyCode::Char('y'))) {
        StackOutcome::Action(SurfaceAction::AnswerPrompt { index: 0 }) => {}
        _ => panic!("the answer never reached the application"),
    }
}
```

- [ ] **Step 6: Run tests**

Run: `cargo test -p coda-tui`
Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Move engine prompts onto an exclusive Surface"
```

---

# Phase 3 — Table control and list surfaces

---

### Task 9: The `Table` control

**Files:**
- Create: `rust/crates/coda-tui/src/widgets/table.rs` (or append to `widgets.rs` if not yet split)

- [ ] **Step 1: Write the failing test**

```rust
#[cfg(test)]
mod table_tests {
    use super::*;
    use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn table() -> Table {
        Table::new(
            vec!["Name".into(), "Status".into()],
            vec![
                Row::new("a", vec!["alpha".into(), "on".into()]),
                Row::new("b", vec!["beta".into(), "off".into()]),
                Row::new("c", vec!["gamma".into(), "on".into()]),
            ],
        )
    }

    #[test]
    fn arrows_move_the_selection_and_stop_at_the_ends() {
        let mut t = table();
        t.handle_key(key(KeyCode::Up));
        assert_eq!(t.selected_id(), Some("a"));
        for _ in 0..5 {
            t.handle_key(key(KeyCode::Down));
        }
        assert_eq!(t.selected_id(), Some("c"));
    }

    #[test]
    fn filtering_narrows_the_rows_and_keeps_a_valid_selection() {
        let mut t = table();
        t.set_filter("bet");
        assert_eq!(t.visible_len(), 1);
        assert_eq!(t.selected_id(), Some("b"));
    }

    #[test]
    fn a_filter_matching_nothing_leaves_no_selection() {
        let mut t = table();
        t.set_filter("zzz");
        assert_eq!(t.visible_len(), 0);
        assert_eq!(t.selected_id(), None);
    }

    #[test]
    fn it_pages_to_keep_the_selection_visible() {
        let mut t = table();
        t.handle_key(key(KeyCode::Down));
        t.handle_key(key(KeyCode::Down));
        // Two content rows plus a header.
        let lines = t.render(40, 3, &Theme::default());
        assert!(lines.len() <= 3);
        let text: String = lines
            .iter()
            .flat_map(|l| l.spans.iter().map(|s| s.content.to_string()))
            .collect();
        assert!(text.contains("gamma"), "the selected row scrolled out of view");
    }

    #[test]
    fn columns_are_padded_so_values_align() {
        let t = table();
        let lines = t.render(40, 5, &Theme::default());
        let rendered: Vec<String> = lines
            .iter()
            .map(|l| l.spans.iter().map(|s| s.content.to_string()).collect())
            .collect();
        let status_at: Vec<usize> = rendered[1..]
            .iter()
            .filter_map(|row| row.find("on").or_else(|| row.find("off")))
            .collect();
        assert!(
            status_at.windows(2).all(|w| w[0] == w[1]),
            "columns did not align: {rendered:#?}"
        );
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-tui --lib table_tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

```rust
/// One row of a [`Table`], carrying a stable id.
///
/// The id is what an action refers to, so it must survive filtering and
/// re-sorting; an index would not.
#[derive(Debug, Clone)]
pub struct Row {
    pub id: String,
    pub cells: Vec<String>,
}

impl Row {
    pub fn new(id: impl Into<String>, cells: Vec<String>) -> Self {
        Self { id: id.into(), cells }
    }
}

/// A scrollable, filterable table with a selected row.
///
/// The equivalent of the C# `TableSource` plus its browser state, which the
/// six browser surfaces all need and currently each reimplement.
#[derive(Debug, Clone)]
pub struct Table {
    headers: Vec<String>,
    rows: Vec<Row>,
    filter: String,
    selected: usize,
}

impl Table {
    pub fn new(headers: Vec<String>, rows: Vec<Row>) -> Self {
        Self { headers, rows, filter: String::new(), selected: 0 }
    }

    /// Rows passing the current filter.
    pub fn visible(&self) -> Vec<&Row> {
        if self.filter.is_empty() {
            return self.rows.iter().collect();
        }
        let needle = self.filter.to_lowercase();
        self.rows
            .iter()
            .filter(|row| {
                row.cells
                    .iter()
                    .any(|cell| cell.to_lowercase().contains(&needle))
            })
            .collect()
    }

    pub fn visible_len(&self) -> usize {
        self.visible().len()
    }

    /// The current filter text, so a surface can render and edit it.
    pub fn filter(&self) -> &str {
        &self.filter
    }

    pub fn selected_id(&self) -> Option<&str> {
        self.visible().get(self.selected).map(|row| row.id.as_str())
    }

    /// Sets the filter, clamping the selection so it stays on a visible row.
    pub fn set_filter(&mut self, filter: impl Into<String>) {
        self.filter = filter.into();
        let len = self.visible_len();
        self.selected = if len == 0 { 0 } else { self.selected.min(len - 1) };
    }

    pub fn handle_key(&mut self, key: KeyEvent) -> KeyOutcome {
        let len = self.visible_len();
        match key.code {
            KeyCode::Up if self.selected > 0 => {
                self.selected -= 1;
                KeyOutcome::Consumed
            }
            KeyCode::Down if self.selected + 1 < len => {
                self.selected += 1;
                KeyOutcome::Consumed
            }
            KeyCode::Home => {
                self.selected = 0;
                KeyOutcome::Consumed
            }
            KeyCode::End => {
                self.selected = len.saturating_sub(1);
                KeyOutcome::Consumed
            }
            KeyCode::Up | KeyCode::Down => KeyOutcome::Consumed,
            _ => KeyOutcome::Ignored,
        }
    }

    /// Widths that fit `width`, each column sized to its widest value.
    fn column_widths(&self, width: usize) -> Vec<usize> {
        let count = self.headers.len().max(1);
        let mut widths: Vec<usize> = self
            .headers
            .iter()
            .map(|header| text::width(header))
            .collect();
        for row in self.visible() {
            for (i, cell) in row.cells.iter().enumerate() {
                if i < widths.len() {
                    widths[i] = widths[i].max(text::width(cell));
                }
            }
        }
        // One space between columns; shrink the last if the total overflows.
        let total: usize = widths.iter().sum::<usize>() + count.saturating_sub(1);
        if total > width && !widths.is_empty() {
            let over = total - width;
            let last = widths.len() - 1;
            widths[last] = widths[last].saturating_sub(over).max(1);
        }
        widths
    }

    /// Renders the header plus as many rows as `height` allows, paged so the
    /// selection stays visible.
    pub fn render(&self, width: u16, height: u16, theme: &Theme) -> Vec<Line<'static>> {
        if height == 0 {
            return Vec::new();
        }
        let widths = self.column_widths(width as usize);
        let body_rows = (height as usize).saturating_sub(1);
        let visible = self.visible();

        let scroll = if body_rows == 0 {
            0
        } else {
            self.selected.saturating_sub(body_rows - 1)
        };

        let pad = |cells: &[String]| -> String {
            cells
                .iter()
                .enumerate()
                .map(|(i, cell)| {
                    let w = widths.get(i).copied().unwrap_or(0);
                    let trimmed = text::truncate(&text::sanitize(cell), w);
                    format!("{trimmed:<w$}")
                })
                .collect::<Vec<_>>()
                .join(" ")
        };

        let mut lines = vec![Line::from(Span::styled(
            pad(&self.headers),
            theme.style(Role::Heading),
        ))];

        for (offset, row) in visible.iter().skip(scroll).take(body_rows).enumerate() {
            let is_selected = scroll + offset == self.selected;
            let style = if is_selected {
                theme
                    .style(Role::SelectionText)
                    .bg(theme.fg(Role::SelectionBackground))
            } else {
                theme.style(Role::ComposerText)
            };
            lines.push(Line::from(Span::styled(pad(&row.cells), style)));
        }
        lines
    }
}
```

- [ ] **Step 4: Run tests**

Run: `cargo test -p coda-tui --lib table_tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Add a Table control for list surfaces"
```

---

### Task 10: `ListSurface` and browser migration

**Files:**
- Create: `rust/crates/coda-tui/src/surface/list.rs`
- Modify: `rust/crates/coda-tui/src/app.rs`, `browsers.rs`

- [ ] **Step 1: Write the failing test**

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn list() -> ListSurface {
        ListSurface::new(
            "Skills",
            Table::new(
                vec!["Name".into()],
                vec![Row::new("a", vec!["alpha".into()])],
            ),
        )
    }

    #[test]
    fn enter_emits_an_action_carrying_the_selected_id() {
        let mut surface = list().on_activate(|id| SurfaceAction::RunCommand(format!("skill {id}")));
        match surface.handle_key(key(KeyCode::Enter)) {
            SurfaceOutcome::Emit(SurfaceAction::RunCommand(cmd)) => {
                assert_eq!(cmd, "skill a");
            }
            _ => panic!("Enter did not emit the activation action"),
        }
    }

    #[test]
    fn slash_starts_a_filter_and_typing_narrows_it() {
        let mut surface = list();
        surface.handle_key(key(KeyCode::Char('/')));
        assert!(surface.is_filtering());
        surface.handle_key(key(KeyCode::Char('z')));
        assert_eq!(surface.table().visible_len(), 0);
    }

    #[test]
    fn escape_leaves_the_filter_before_it_closes_the_surface() {
        // One Esc, one effect: clearing a filter must not also close the list.
        let mut surface = list();
        surface.handle_key(key(KeyCode::Char('/')));
        assert!(matches!(
            surface.handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Handled
        ));
        assert!(!surface.is_filtering());
        assert!(matches!(
            surface.handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Close
        ));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-tui --lib surface::list`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

```rust
//! A list surface: a `Table` with a filter and an activation action.
//!
//! Replaces the six hand-rolled browsers, each of which reimplemented
//! selection, filtering and paging.

use super::{Surface, SurfaceAction, SurfaceOutcome};
use crate::widgets::{KeyOutcome, Row, Table};
use coda_render::theme::Theme;
use crossterm::event::{KeyCode, KeyEvent};
use ratatui::layout::Rect;
use ratatui::text::Line;

type Activate = Box<dyn Fn(&str) -> SurfaceAction>;

pub struct ListSurface {
    title: String,
    table: Table,
    filtering: bool,
    activate: Option<Activate>,
}

impl ListSurface {
    pub fn new(title: impl Into<String>, table: Table) -> Self {
        Self { title: title.into(), table, filtering: false, activate: None }
    }

    /// Sets what pressing Enter on a row asks the application to do.
    pub fn on_activate(mut self, f: impl Fn(&str) -> SurfaceAction + 'static) -> Self {
        self.activate = Some(Box::new(f));
        self
    }

    pub fn table(&self) -> &Table {
        &self.table
    }

    pub fn is_filtering(&self) -> bool {
        self.filtering
    }

    fn filter_text(&self) -> String {
        self.table.filter().to_string()
    }
}

impl Surface for ListSurface {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn title(&self) -> String {
        self.title.clone()
    }

    fn hints(&self) -> String {
        "/: filter    Enter: open    Esc: close".into()
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        if self.filtering {
            match key.code {
                KeyCode::Esc => {
                    // Leave the filter without closing the surface. One Esc,
                    // one effect.
                    self.filtering = false;
                    self.table.set_filter("");
                    return SurfaceOutcome::Handled;
                }
                KeyCode::Enter => {
                    self.filtering = false;
                    return SurfaceOutcome::Handled;
                }
                KeyCode::Backspace => {
                    let mut text = self.filter_text();
                    text.pop();
                    self.table.set_filter(text);
                    return SurfaceOutcome::Handled;
                }
                KeyCode::Char(c) => {
                    let text = format!("{}{c}", self.filter_text());
                    self.table.set_filter(text);
                    return SurfaceOutcome::Handled;
                }
                _ => {}
            }
        }

        match key.code {
            KeyCode::Char('/') => {
                self.filtering = true;
                SurfaceOutcome::Handled
            }
            KeyCode::Enter => match (&self.activate, self.table.selected_id()) {
                (Some(f), Some(id)) => SurfaceOutcome::Emit(f(id)),
                _ => SurfaceOutcome::Handled,
            },
            _ => match self.table.handle_key(key) {
                KeyOutcome::Consumed => SurfaceOutcome::Handled,
                KeyOutcome::Ignored => SurfaceOutcome::Ignored,
            },
        }
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        self.table.render(area.width, area.height, theme)
    }
}
```

Add `pub fn filter(&self) -> &str` to `Table` — already included in Task 9.

- [ ] **Step 4: Migrate the browsers one at a time**

Take `skills` first as the template. In `app.rs`, replace

```rust
            "skills" => self.open_browser(BrowserKind::Skills).await,
```

with

```rust
            "skills" => self.open_skills_surface().await,
```

and add:

```rust
    /// Opens the skills list.
    ///
    /// The row data still comes from `browsers::`; only presentation moves, so
    /// the engine call and its shape are unchanged.
    async fn open_skills_surface(&mut self) {
        let items = match self.request(method::SKILLS_LIST, serde_json::json!({})).await {
            Ok(value) => browsers::skills(&value),
            Err(err) => {
                self.notice(format!("Could not list skills: {err}"), NoticeLevel::Error);
                return;
            }
        };
        let rows = items
            .iter()
            .map(|item| {
                Row::new(item.id.clone(), vec![item.name.clone(), item.summary.clone()])
            })
            .collect();
        let table = Table::new(vec!["Name".into(), "Summary".into()], rows);
        self.surfaces.push(Box::new(
            ListSurface::new("Skills", table)
                .on_activate(|id| SurfaceAction::RunCommand(format!("skill {id}"))),
        ));
        self.dirty = true;
    }
```

Then repeat for `plugins`, `hooks`, `mcp`, `tasks` and `sessions`, changing only
the RPC method, the `browsers::` builder, the column headers and the activation
action. Commit each separately so a regression bisects to one browser.

Where the existing browser had a detail view, the activation action returns
`SurfaceOutcome::Push(Box::new(detail_surface))` instead of `Emit`, which is how
a detail comes to sit over its list.

- [ ] **Step 5: Run tests after each browser**

Run: `cargo test -p coda-tui`
Expected: PASS each time.

- [ ] **Step 6: Delete the old mechanism**

Once all six are migrated, delete `overlay.rs`, the `browser` and
`browser_kind` fields, `on_browser_key`, `draw_browser`, `draw_browser_list` and
`draw_browser_detail`.

Run: `cargo test -p coda-tui 2>&1 | Select-String "^warning"`
Expected: no output — no dead code left behind.

- [ ] **Step 7: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Move the six browsers onto ListSurface and delete the old overlay"
```

---

### Task 11: Overflow guard

**Files:**
- Modify: `rust/crates/coda-tui/tests/surface_integration.rs`

- [ ] **Step 1: Write the failing test**

```rust
/// Builds one of every surface, so the guard cannot silently miss a type.
fn every_surface() -> Vec<Box<dyn coda_tui::surface::Surface>> {
    use coda_tui::widgets::{Row, Table};
    let settings = Settings::empty_at(std::env::temp_dir().join("coda-overflow-test.json"));
    vec![
        Box::new(SettingsSurface::new(&settings)),
        Box::new(PromptSurface::new(PendingPrompt::Permission {
            tool: "write_file".into(),
            preview: "a rather long preview line that will need wrapping".into(),
        })),
        Box::new(coda_tui::surface::list::ListSurface::new(
            "Skills",
            Table::new(
                vec!["Name".into(), "Summary".into()],
                (0..40)
                    .map(|i| Row::new(format!("id{i}"), vec![format!("row {i}"), "x".repeat(60)]))
                    .collect(),
            ),
        )),
    ]
}

#[test]
fn no_surface_draws_more_than_its_area_allows() {
    // A cramped terminal must show a usable surface, not a clipped one.
    let theme = coda_render::theme::Theme::default();
    for surface in every_surface() {
        for (w, h) in [(40, 10), (20, 6), (200, 60), (12, 3)] {
            let area = coda_tui::surface::region_for(
                surface.placement().resolve(ratatui::layout::Rect::new(0, 0, w, h)),
                ratatui::layout::Rect::new(0, 0, w, h),
            );
            let lines = surface.render(area, &theme);
            assert!(
                lines.len() <= area.height as usize,
                "{} produced {} lines for a {}-row area at {w}x{h}",
                surface.title(),
                lines.len(),
                area.height
            );
            for line in &lines {
                let width: usize = line.spans.iter().map(|s| coda_render::text::width(&s.content)).sum();
                assert!(
                    width <= area.width as usize,
                    "{} produced a {width}-cell line for a {}-cell area at {w}x{h}",
                    surface.title(),
                    area.width
                );
            }
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-tui --test surface_integration no_surface_draws -- --nocapture`
Expected: FAIL on at least one surface — most likely the list, whose 60-cell
column exceeds a 20-cell area.

- [ ] **Step 3: Fix each overflow**

Clamp in the surface that overflowed. For `Table`, `column_widths` must never
return a total exceeding `width`; shrink columns from the right until it fits,
and give every column at least one cell so a column never vanishes entirely.

- [ ] **Step 4: Run tests**

Run: `cargo test -p coda-tui`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Guard every surface against drawing outside its area"
```

---

## Definition of done

- [ ] `cargo test --workspace` passes with zero warnings.
- [ ] `tests/conventions.rs` passes: no glyph literals outside `render/glyphs.rs`, no `Color::` outside `theme.rs`.
- [ ] `overlay.rs`, `settings_form.rs` and `draw_prompt` are gone.
- [ ] `app.rs` no longer routes keys for prompts, browsers or forms.
- [ ] Every surface renders at 40×10 without exceeding its area.
- [ ] `.\build.ps1 -Deploy` produces a working `coda-rs.exe` at the bumped version.
