# Splitting app.rs and Making Surfaces Local — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Break `app.rs` into focused modules, and make adding a surface a genuinely local change.

**Architecture:** `app.rs` keeps the event loop and the `App` struct. Everything with a distinct responsibility moves into `app/`: slash commands, engine RPC, clipboard and pointer, browser orchestration. Browser row actions move onto the surfaces that raise them, so a new browser stops touching `app.rs` in five places.

**Tech Stack:** Rust 1.86, ratatui 0.30, crossterm 0.29. No rustup/clippy/rustfmt; the feedback loop is `cargo test`.

**Spec:** `docs/superpowers/specs/2026-08-31-rust-tui-design-system-design.md`, phases 5 and part of 6.

---

## Why this plan exists

The Surface work delivered testability and one modal mechanism, but it did not
deliver its headline goal. Measured honestly at the end of that PR:

- `app.rs` **grew** from 3,395 to 3,761 lines. It is now 3,672 production lines
  across 120 methods.
- Adding a ninth browser touches `app.rs` in five places, all keyed on
  `BrowserKind`: `build_browser`, `activate_browser_row`, `toggle_browser_row`,
  `delete_browser_row`, and `browser_key_action`.

Where the lines are today:

| Responsibility | Lines |
|---|---|
| Slash commands | 1,321 |
| The `App` struct, fields and misc helpers | 1,037 |
| Event loop and rendering | 511 |
| Engine RPC | 322 |
| Browser orchestration | 316 |
| Clipboard and pointer | 165 |

## Conventions for every task

- Run tests from `rust/`: `cargo test -p coda-tui`
- Count failures with `Select-String "test result: FAILED"`, never `"FAILED"`.
- Verify warnings explicitly: `cargo test -p coda-tui 2>&1 | Select-String "^warning"`.
- `pub` items in a lib crate produce **no** dead-code warning. Grep for callers.
- After any mutation experiment, `cargo clean -p coda-tui`.
- PowerShell `.Replace()` on multi-line strings silently no-ops when line
  endings differ. Prefer the edit tool; verify every replacement took.

## File structure

| File | Responsibility |
|---|---|
| `app/mod.rs` (from `app.rs`) | `App`, its fields, the event loop, rendering. |
| `app/commands/mod.rs` | Dispatch plus the shared `Invocation` helpers. |
| `app/commands/session.rs` | resume, fork, rewind, compact, export, diff, image. |
| `app/commands/config.rs` | model, provider, permissions, yolo, headers, theme, output-style, log. |
| `app/commands/plugins.rs` | skill, plugin, marketplace, mcp, hooks, init, memory. |
| `app/engine.rs` | `fetch`, request plumbing, inbound events, prompt answering. |
| `app/clipboard.rs` | copy, paste, pointer gestures, caret placement. |
| `app/browsers.rs` | opening, reloading and row actions for every browser. |
| `surface/browser.rs` (modify) | Gains the per-row action set, so a browser owns its own keys. |

---

# Phase 5 — Splitting app.rs

Each task is a pure move: no behaviour change, tests green throughout.

---

### Task 1: Turn `app.rs` into a module directory

**Files:**
- Move: `rust/crates/coda-tui/src/app.rs` → `rust/crates/coda-tui/src/app/mod.rs`

- [ ] **Step 1: Move the file**

```powershell
cd C:\Users\yurio\Documents\github\coda-cli\rust\crates\coda-tui\src
New-Item -ItemType Directory -Force -Path app | Out-Null
git mv app.rs app/mod.rs
```

- [ ] **Step 2: Run tests to verify nothing changed**

Run: `cargo test -p coda-tui`
Expected: identical pass count to before the move, zero warnings.

- [ ] **Step 3: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Make app a module directory"
```

---

### Task 2: Extract the engine seam

**Files:**
- Create: `rust/crates/coda-tui/src/app/engine.rs`
- Modify: `rust/crates/coda-tui/src/app/mod.rs`

- [ ] **Step 1: Write the failing test**

Add to `rust/crates/coda-tui/tests/conventions.rs`:

```rust
/// `app/mod.rs` must stay the event loop, not the whole application.
///
/// A ceiling rather than a target. It is deliberately generous: the point is
/// to notice when a responsibility drifts back in, not to police every line.
#[test]
fn the_application_module_stays_a_shell() {
    const CEILING: usize = 1_600;

    let source = std::fs::read_to_string("src/app/mod.rs").expect("read app/mod.rs");
    let production = without_test_modules(&source).lines().count();

    assert!(
        production <= CEILING,
        "app/mod.rs is {production} production lines, over the {CEILING}-line ceiling. \
         Something with its own responsibility has drifted back in; move it to a \
         sibling under app/ rather than raising this number."
    );
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-tui --test conventions the_application_module -- --nocapture`
Expected: FAIL reporting roughly 3,672 lines.

- [ ] **Step 3: Create the engine module**

Create `rust/crates/coda-tui/src/app/engine.rs`:

```rust
//! The engine seam: requests out, notifications in.
//!
//! Split from the loop because it is the only part of the application that
//! knows the wire protocol exists. Everything else deals in `UiEvent` and
//! `SurfaceAction`.

use anyhow::Result;
use coda_client::ClientError;
use coda_proto::messages;
use serde_json::Value;

use super::App;
use crate::state::{NoticeLevel, PendingPrompt, UiEvent};

impl App {
    /// Issues a request and deserialises its result.
    pub(super) async fn fetch<T: serde::de::DeserializeOwned>(
        &self,
        rpc_method: &str,
        params: Option<Value>,
    ) -> Result<T, ClientError> {
        let value = self.connection.request(rpc_method, params).await?;
        serde_json::from_value(value).map_err(ClientError::Serde)
    }
}
```

Then move these methods out of `app/mod.rs` into it, changing `fn` to
`pub(super) fn` where the loop still calls them:

`fetch`, `answer_prompt`, `apply_permission_mode`, `on_server_request`,
`on_inbound`, `retire_prompt_surface`.

- [ ] **Step 4: Declare the module**

In `app/mod.rs`, below the `use` block:

```rust
mod engine;
```

- [ ] **Step 5: Run tests**

Run: `cargo test -p coda-tui`
Expected: same pass count; the ceiling test still fails (the file is still too
big), every other test passes.

- [ ] **Step 6: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Move the engine seam out of the loop"
```

---

### Task 3: Extract clipboard and pointer handling

**Files:**
- Create: `rust/crates/coda-tui/src/app/clipboard.rs`
- Modify: `rust/crates/coda-tui/src/app/mod.rs`

- [ ] **Step 1: Create the module**

```rust
//! Clipboard and pointer gestures.
//!
//! Grouped because they are the same subject from the user's side: selecting,
//! copying, pasting and placing the caret are one continuous interaction.

use super::{App, PointerAction};
use crate::state::NoticeLevel;
use crossterm::event::{MouseButton, MouseEvent, MouseEventKind};

impl App {
    // moved methods go here
}
```

Move out of `app/mod.rs`: `copy_selection_via_pointer`, `paste_from_pointer`,
`copy_to_clipboard`, `move_caret_to_click`, `mouse_to_selection`,
`decide_pointer_action`, and the free function `pointer_action`.

Keep `PointerAction` in `app/mod.rs` and re-export it to the child with
`pub(super)`.

- [ ] **Step 2: Declare the module and run tests**

Add `mod clipboard;` to `app/mod.rs`.

Run: `cargo test -p coda-tui`
Expected: same pass count.

- [ ] **Step 3: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Move clipboard and pointer handling out of the loop"
```

---

### Task 4: Extract browser orchestration

**Files:**
- Create: `rust/crates/coda-tui/src/app/browsers.rs`
- Modify: `rust/crates/coda-tui/src/app/mod.rs`

- [ ] **Step 1: Create the module**

Move out of `app/mod.rs`: `browser`, `browser_kind`, `retire_browser_surface`,
`close_browser`, `build_browser`, `open_browser`, `browser_failed`,
`reload_browser`, `activate_browser_row`, `toggle_browser_row`,
`delete_browser_row`, `browser_key_action`, `edit_mcp_server`,
`delete_mcp_server`, `update_plugin`.

The module header should say why this is one unit:

```rust
//! Opening browsers and performing the actions their rows raise.
//!
//! One module because every one of these is the same shape: fetch from the
//! engine or the filesystem, build a `Browser`, and act on a row by id. The
//! per-kind `match`es here are what Task 6 removes.
```

- [ ] **Step 2: Declare the module and run tests**

Run: `cargo test -p coda-tui`
Expected: same pass count.

- [ ] **Step 3: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Move browser orchestration out of the loop"
```

---

### Task 5: Extract the slash commands

**Files:**
- Create: `rust/crates/coda-tui/src/app/commands/mod.rs`
- Create: `rust/crates/coda-tui/src/app/commands/session.rs`
- Create: `rust/crates/coda-tui/src/app/commands/config.rs`
- Create: `rust/crates/coda-tui/src/app/commands/plugins.rs`
- Modify: `rust/crates/coda-tui/src/app/mod.rs`

This is the largest move — 1,321 lines — so it is split by subject rather than
done in one file.

- [ ] **Step 1: Create `app/commands/mod.rs`**

```rust
//! Slash commands.
//!
//! Dispatch lives here; the handlers live in siblings grouped by subject, so a
//! change to session handling does not scroll past every configuration
//! command on its way.

mod config;
mod plugins;
mod session;

use super::App;
use crate::commands::{self, Invocation, Scope};
use crate::state::NoticeLevel;

impl App {
    /// Routes a parsed slash command to its handler.
    pub(super) async fn run_command(&mut self, invocation: Invocation) {
        // moved from app/mod.rs unchanged
    }
}
```

- [ ] **Step 2: Move the handlers**

- `session.rs`: `cmd_resume`, `cmd_fork`, `cmd_rewind`, `cmd_compact`,
  `cmd_export`, `cmd_diff`, `cmd_image`, `resume_to_session`,
  `resolve_resume_target`, `parse_rewind_n`, `build_markdown_export`, and the
  image helpers.
- `config.rs`: `cmd_model`, `cmd_provider`, `cmd_permissions`, `cmd_yolo`,
  `cmd_headers`, `cmd_theme`, `cmd_output_style`, `cmd_log`,
  `parse_permission_mode`, `format_result`.
- `plugins.rs`: `cmd_skill`, `cmd_plugin`, `cmd_marketplace`, `cmd_mcp`,
  `cmd_hooks`, `cmd_init`, `cmd_memory`, `bind_skill_args` and its helpers.

Move each handler's tests with it. Tests that exercise a pure helper
(`parse_rewind_n`, `bind_skill_args`, `build_markdown_export`) move to the file
that now owns the helper.

- [ ] **Step 3: Declare the module and run tests**

Add `mod commands;` to `app/mod.rs`.

Run: `cargo test -p coda-tui`
Expected: same pass count, and `the_application_module_stays_a_shell` now
**passes**.

- [ ] **Step 4: Verify no warnings**

Run: `cargo test -p coda-tui 2>&1 | Select-String "^warning"`
Expected: no output.

- [ ] **Step 5: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Move the slash commands out of the loop"
```

---

# Phase 6a — Making a browser local

The honest limitation from the last PR: adding a browser touches `app.rs` in
five `BrowserKind` matches. This removes them.

---

### Task 6: A browser carries its own row actions

**Files:**
- Modify: `rust/crates/coda-tui/src/surface/browser.rs`
- Modify: `rust/crates/coda-tui/src/app/browsers.rs`

- [ ] **Step 1: Write the failing test**

Add to `rust/crates/coda-tui/tests/conventions.rs`:

```rust
/// Adding a browser must not mean editing a chain of `BrowserKind` matches.
///
/// Each `match` on the kind is a place a ninth browser has to be remembered,
/// and forgetting one gives a browser whose keys silently do nothing.
#[test]
fn browser_behaviour_is_not_dispatched_by_kind() {
    let source = std::fs::read_to_string("src/app/browsers.rs").expect("read browsers.rs");
    let code = without_comments(&without_test_modules(&source));
    let matches = code.matches("BrowserKind::").count();

    // One for building each browser is expected and fine — that is the
    // constructor. Anything beyond it is behaviour keyed on kind.
    const ALLOWED: usize = 10;
    assert!(
        matches <= ALLOWED,
        "browsers.rs mentions BrowserKind {matches} times, over the {ALLOWED} allowed. \
         Row behaviour should live on the surface that raises it, not in a match here."
    );
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p coda-tui --test conventions browser_behaviour -- --nocapture`
Expected: FAIL, reporting well over the allowance.

- [ ] **Step 3: Give `BrowserSurface` an action set**

In `surface/browser.rs`:

```rust
/// What a row's keys do, supplied when the browser is built.
///
/// Carried by the surface rather than looked up by kind in the host: a browser
/// that knows its own actions can be added in one place, and cannot be half
/// added by forgetting one of several matches.
#[derive(Default)]
pub struct RowActions {
    /// Enter on a row.
    pub activate: Option<Box<dyn Fn(&str) -> SurfaceAction>>,
    /// Space on a row.
    pub toggle: Option<Box<dyn Fn(&str) -> SurfaceAction>>,
    /// A per-browser key, such as `d` to delete.
    pub keys: Vec<(char, Box<dyn Fn(&str) -> SurfaceAction>)>,
}
```

`BrowserSurface::handle_key` consults these before falling back to
`SurfaceAction::Browser`, so a browser with no action set behaves exactly as it
does today.

- [ ] **Step 4: Move each browser's actions to its constructor**

In `app/browsers.rs`, `build_browser` returns the `RowActions` alongside the
`Browser`, replacing the arms of `activate_browser_row`, `toggle_browser_row`,
`delete_browser_row` and `browser_key_action`.

- [ ] **Step 5: Run tests**

Run: `cargo test -p coda-tui`
Expected: PASS, including the new convention test.

- [ ] **Step 6: Prove a browser is now local**

Add to `rust/crates/coda-tui/tests/surface_integration.rs` a test that builds a
`BrowserSurface` with a `RowActions` set and asserts Enter, Space and a custom
key each emit the configured action — without any reference to `App`.

- [ ] **Step 7: Commit**

```powershell
git add rust/crates/coda-tui
git commit -m "Give each browser its own row actions"
```

---

## Definition of done

- [x] `cargo test --workspace` passes with zero warnings. *(2,267 passing.)*
- [x] `app/mod.rs` is under 1,600 production lines, enforced by a test.
      *(1,351, down from 3,959; `the_application_module_stays_a_shell`.)*
- [x] ~~`browsers.rs` mentions `BrowserKind` no more than ten times~~ — replaced,
      see the note below. `row_behaviour_is_not_looked_up_by_browser_kind` caps
      `browser_kind()` at two calls instead.
- [x] Adding a browser means: one constructor arm, one `RowActions`. Nothing else.
      *(Both matches are exhaustive over `BrowserKind`, so a new browser is a
      compile error until it says what its rows do.)*
- [x] No behaviour change: every existing test passes unmodified except where a
      test moved file with the code it covers.
- [ ] `.\build.ps1 -Deploy` installs the native `coda` at the bumped version via the `Coda.Cli` .NET tool.

### Deviation: how "not dispatched by kind" is measured

Task 6 planned to count `BrowserKind::` in `browsers.rs` and hold it under ten.
That measures the wrong thing. It counts a browser *declaring itself* — its
constructor arm and its `RowActions` arm — the same as it counts behaviour being
looked up by kind, and the first is the very thing this task set out to produce.
Written as planned the test would have failed at seventeen mentions with the
refactor complete and correct, and the only way to pass it would have been to
make the declarations less explicit.

The property actually wanted is that *behaviour* is not chosen by asking which
browser is open. That is `browser_kind()`, and after this task there are two
calls left, both genuine "which browser is open" questions: rebuilding on
reload, and noticing that saving an MCP server affects the open list. The test
caps those at two, and it does fail if kind-dispatch is reintroduced — verified
by putting one back.

### Two bugs Task 6 surfaced

Both are the shape this plan exists to prevent — an action that exists and never
fires — and neither was visible to a unit test:

- A `RowActions` key had to be registered on the `Browser` separately, or the
  browser swallowed it before the surface ever saw it. Found by driving the real
  stack in `surface_integration.rs`. `with_actions` now registers its own keys.
- `reload_browser` rebuilt the surface without attaching actions, so pressing `r`
  left a browser that looked identical and had gone inert. Both construction
  sites now go through one helper, held by
  `a_browser_surface_is_built_in_one_place`.
