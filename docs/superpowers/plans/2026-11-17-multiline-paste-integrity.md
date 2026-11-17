# Multiline Paste Integrity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A multiline paste on Windows Terminal becomes one draft. Pasting submits nothing, and one explicit send submits exactly one message.

**Architecture:** A new `coda-tui::input` module owns terminal input. On Windows it enables VT input and win32-input-mode, reads raw console records, and decodes them with a pure, platform-independent `Decoder`. The decoder recognises bracketed-paste transactions. On Unix, crossterm keeps decoding input. Both backends produce `InputAction`s. `App` applies a paste as one editor operation for the target captured when the paste began. Spec: `docs/superpowers/specs/2026-11-17-multiline-paste-integrity-design.md`.

**Tech Stack:** Rust, crossterm 0.29 (key/mouse types, Unix reader), windows-sys 0.61 (`Win32_System_Console`), tokio, the existing `app_with` fake-engine harness, proptest-free property tests (seeded split loops).

---

## File structure

| File | Responsibility |
| --- | --- |
| `rust/crates/coda-tui/src/input/mod.rs` (new) | `InputAction`, `PasteSource`, `PasteAbort`, `BackendStatus`; re-exports. |
| `rust/crates/coda-tui/src/input/keys.rs` (new) | Pure key mapping: port of crossterm 0.29's Windows `parse_key_event_record` (minus `ToUnicodeEx`, replaced by a `vk`→ASCII fallback). Also maps VT characters and CSI/SS3 sequences to `KeyEvent`. |
| `rust/crates/coda-tui/src/input/decoder.rs` (new) | Pure `Decoder`: records → units → win32 wrappers → VT parser → actions, including paste transactions, timeouts and limits. |
| `rust/crates/coda-tui/src/input/windows.rs` (new, `cfg(windows)`) | Owns `CONIN$`: saves and sets modes, writes `ESC[?9001h/l`, reads `INPUT_RECORD`s, and feeds the decoder. |
| `rust/crates/coda-tui/src/terminal.rs` | Activates and deactivates the backend in `enter`/`suspended`/`restore`/the panic hook. Adds `read_input()`; `read_event()` becomes an adapter over it. |
| `rust/crates/coda-tui/src/app/mod.rs` | `on_terminal(TerminalEvent)` → `on_input(InputAction)`. Adds the paste-state fields and the backend warning. |
| `rust/crates/coda-tui/src/app/clipboard.rs` | `begin_paste`, `apply_paste_text`, `abort_paste`; the clipboard text route goes through `apply_paste_text`. |
| `rust/crates/coda-tui/src/surface/stack.rs` | Per-surface ids (`top_id`). |
| `rust/crates/coda-diagnostics/src/event.rs` | `InputBackend`, `PasteTransaction`, `PasteDraftSubmitted` events. |
| `rust/crates/coda-tui/Cargo.toml` | `windows-sys` (cfg(windows)) dependency. |
| `README.md`, `docs/…` | Supported hosts table and the `Alt+V` fallback. |

---

### Task 1: Input types and key mapping (`input/mod.rs`, `input/keys.rs`)

**Files:** Create `src/input/mod.rs`, `src/input/keys.rs`; modify `src/lib.rs` (add `pub mod input;`).

- [ ] **Step 1: Types**

```rust
// input/mod.rs
pub mod decoder;
pub mod keys;
#[cfg(windows)]
pub(crate) mod windows;

use crossterm::event::{KeyEvent, MouseEvent};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PasteSource { Terminal, Clipboard }

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PasteAbort { TooLarge, Incomplete, Cancelled }

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum InputAction {
    Key(KeyEvent),
    Mouse(MouseEvent),
    Resize(u16, u16),
    FocusGained,
    FocusLost,
    PasteStarted { txn: u64 },
    PasteText { text: String, source: PasteSource, txn: u64 },
    PasteAborted { txn: u64, reason: PasteAbort, bytes: usize },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BackendStatus {
    pub backend: &'static str,       // "windows-vt" | "crossterm-native" | "crossterm"
    pub paste_framing: bool,
    pub key_encoding: &'static str,  // "win32-input-mode" | "native-records" | "vt"
}
```

- [ ] **Step 2: Failing tests in `keys.rs`**: `vk_record(VK_RETURN, SHIFT)` → `Enter+SHIFT`; `vk 'C'` with `LEFT_CTRL`, unit 3 → `Char('c')+CONTROL`; `VK_TAB+SHIFT` → `BackTab`; `VK_MENU` key-up with unit `0xE9` → `Char('é')`; numpad digit with Alt only → `None`; `vt_char('\r')` → `Enter`; `'\n'` → `Char('j')+CONTROL`; `0x7f` → `Backspace`; `0x01` → `Char('a')+CONTROL`; `'A'` → `Char('A')+SHIFT`; `csi("1;5", 'A')` → `Up+CONTROL`; `csi("3", '~')` → `Delete`; `csi("", 'Z')` → `BackTab`; `csi("97;5", 'u')` → `Char('a')+CONTROL`; `ss3('P')` → `F(1)`.
- [ ] **Step 3: Implement** `pub fn map_key_record(r: &KeyRecord) -> Option<MappedKey>` (where `MappedKey` is `Key(KeyEvent)` or `Surrogate(u16, KeyModifiers, KeyEventKind)`), `pub fn modifiers_from_state(u32)`, `pub fn vt_char(c: char, alt: bool) -> KeyEvent`, `pub fn csi_key(params: &str, final: char) -> Option<KeyEvent>` and `pub fn ss3_key(c: char) -> Option<KeyEvent>`. Control-key-state constants are local `const`s, so the module compiles on every OS. Ctrl+letter records whose unit is a C0 control map through `vk` (0x41..=0x5A → lowercase ASCII, 0x30..=0x39 → digit) instead of `ToUnicodeEx`.
- [ ] **Step 4:** `cargo test -p coda-tui --lib input::keys` passes.
- [ ] **Step 5: Commit** `feat(tui): input action types and pure key mapping`.

### Task 2: Decoder (`input/decoder.rs`)

**API:**

```rust
pub const MAX_PASTE_BYTES: usize = 1 << 20;
pub const ESC_TIMEOUT: Duration = Duration::from_millis(30);
pub const PASTE_STALL_TIMEOUT: Duration = Duration::from_secs(2);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct KeyRecord { pub vk: u16, pub scan: u16, pub unit: u16, pub down: bool, pub state: u32, pub repeat: u16 }
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MouseRecord { pub column: u16, pub row: u16, pub buttons: u32, pub flags: u32, pub state: u32 }
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Record { Key(KeyRecord), Mouse(MouseRecord), Resize { cols: u16, rows: u16 }, Focus(bool) }

pub struct Decoder { /* private */ }
impl Decoder {
    pub fn new() -> Self;
    pub fn feed(&mut self, record: Record, now: Instant);
    pub fn tick(&mut self, now: Instant);          // applies ESC and paste-stall timeouts
    pub fn cancel(&mut self);                      // aborts an open paste (Cancelled), drops partial escapes
    pub fn next_action(&mut self) -> Option<InputAction>;
    pub fn in_paste(&self) -> bool;
    pub fn has_pending(&self) -> bool;             // a partial escape awaiting more input
}
```

**Pipeline** (spec §3–4): record → UTF-16 unit (key-down with a unit; an Alt key-up with a unit) or direct key/mouse/resize/focus. Units → chars (surrogates joined; a lone surrogate becomes U+FFFD in paste text and is dropped elsewhere). Chars → outer stage (win32 wrapper `ESC [ p1;…;p6 _`, at most 6 numeric params, at most 32 chars; otherwise the buffered chars are forwarded verbatim). Wrapped records with `vk == 0` and `down` feed their unit back into the char path (the inner stage). Other wrapped records go to the key path, unless a paste is open, in which case a wrapped down-record's unit is paste text. Inner stage = VT parser (Ground / Esc / Csi / Ss3 / Paste) as in the spec.

- [ ] **Step 1: Failing tests** (all in `decoder.rs` `mod tests`; helpers `raw(&str) -> Vec<Record>` encodes every UTF-16 unit as a key-down record with `vk = 0`, and `w32(&str) -> Vec<Record>` wraps every unit as `ESC[0;0;<u>;1;0;1_` *and then* encodes the wrapper as raw records, mirroring the probe):
  1. `raw("\x1b[200~first\r\nsecond\r\n\r\nfourth\r\n\x1b[201~")` → `[PasteStarted{1}, PasteText{"first\nsecond\n\nfourth\n", Terminal, 1}]`.
  2. CR-only, LF-only and mixed endings normalise to LF; leading indentation, tabs and `é😀` are preserved.
  3. Slash-command text, `\x03`, `\x1b[A` and `\x1b[200~` inside the paste are literal text, with no `Key` actions.
  4. A stray `\x1b[201~` outside a paste yields nothing.
  5. Over `MAX_PASTE_BYTES` → `PasteAborted{TooLarge, bytes}`, no `PasteText`; the decoder accepts keys afterwards.
  6. Missing end marker + `tick(now + 2s)` → `PasteAborted{Incomplete}`; later `"x"` → `Key('x')` (buffered text is not replayed).
  7. `cancel()` during a paste → `PasteAborted{Cancelled}`.
  8. win32 wrapper `\x1b[13;28;13;1;16;1_` → `Key(Enter, SHIFT)`; `\x1b[67;46;3;1;8;1_` → `Ctrl+C`; key-up wrappers yield nothing; the Alt key-up surrogate pair `\x1b[18;56;55357;0;0;1_\x1b[18;56;56832;0;0;1_` → `Char('😀')`.
  9. A paste delivered through win32 wrappers (probe shape: markers as `vk=0` wrapped chars, text as wrapped down/up pairs with real vks, `\n` as a Ctrl-down / `\n` / Ctrl-up triple) → the same `PasteText` as test 1.
  10. SGR mouse `\x1b[<0;5;5M` → `Mouse(Down(Left), column 4, row 4)`; `\x1b[<64;1;1M` → `ScrollUp`; `\x1b[I` / `\x1b[O` → focus.
  11. A lone `\x1b` then `tick(+30ms)` → `Esc`; `\x1bx` → `Alt+x`.
  12. Ordinary typing `raw("ab\r")` → `a`, `b`, `Enter`.
  13. **Property:** for 200 seeded random split points (a tiny LCG, so no new dependency), feeding test 1's and test 9's records in arbitrary batches, with `tick` calls between batches that are shorter than the timeouts, yields actions identical to feeding all records at once.
- [ ] **Step 2:** Run `cargo test -p coda-tui --lib input::decoder` and expect failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4:** Tests pass.
- [ ] **Step 5: Commit** `feat(tui): bracketed-paste aware console input decoder`.

### Task 3: Diagnostics events

- [ ] Add to `coda-diagnostics/src/event.rs`:

```rust
/// The terminal input backend chosen at activation. Metadata only.
InputBackend { backend: &'static str, paste_framing: bool, key_encoding: &'static str },
/// One paste transaction. Never carries the pasted text.
PasteTransaction { phase: &'static str, source: &'static str, txn: u64, bytes: usize, lines: usize, outcome: &'static str },
/// A draft that received pastes was submitted once.
PasteDraftSubmitted { pastes: u32, kind: &'static str },
```

  Add a test asserting that the serialized `paste_transaction` record has only those keys (no `text`). Commit `feat(diagnostics): metadata-only input and paste events`.

### Task 4: Windows console backend (`input/windows.rs`) and terminal ownership

- [ ] Add `windows-sys = { version = "0.61", features = ["Win32_Foundation", "Win32_System_Console", "Win32_Storage_FileSystem", "Win32_Security"] }` under `[target.'cfg(windows)'.dependencies]` in `coda-tui/Cargo.toml`.
- [ ] `WindowsVtInput::activate(mouse: bool) -> io::Result<WindowsVtInput>`: open `CONIN$`, `GetConsoleMode` → saved, set the new mode (spec §2) and verify it with `GetConsoleMode`, then write `ESC[?9001h` to stdout. If VT input is rejected, restore the saved mode and return `Err(Unsupported)`.
- [ ] `poll(&mut self, now) -> io::Result<Option<InputAction>>`: return a queued action first. Otherwise read the available records with `GetNumberOfConsoleInputEvents` + `ReadConsoleInputW` (up to 128), convert them to `Record` (the mouse row is made viewport-relative with `GetConsoleScreenBufferInfo`; a resize uses the window size from the same call), feed the decoder, then `tick(now)`. Also check the window size every 250 ms and emit `Resize` when it changed (ConPTY does not always post `WINDOW_BUFFER_SIZE_EVENT`).
- [ ] `deactivate(&mut self)`: `decoder.cancel()` (keep the abort action queued), write `ESC[?9001l`, restore the saved mode. Idempotent.
- [ ] In `terminal.rs`: `static INPUT: Mutex<Option<Backend>>` where `enum Backend { #[cfg(windows)] Vt(WindowsVtInput), Crossterm { status } }`.
  - `enter`: after the screen setup, activate. On Windows, mouse capture is passed to the backend and crossterm's `EnableMouseCapture` is skipped. If activation fails, fall back to crossterm and record `paste_framing: false`.
  - `restore_with` gains `RestoreStep::Input` first (backend deactivation); the unit test asserts the order `[Input, Mouse, …]`.
  - `suspended`: deactivate before `restore`, reactivate after re-entering.
  - `pub fn input_status() -> Option<BackendStatus>`, and `pub(crate) async fn read_input() -> io::Result<InputAction>` (10 ms poll loop, no lock held across `.await`).
  - `read_event()` maps over `read_input`: `PasteText` → `Event::Paste`, `PasteStarted`/`PasteAborted` are skipped. It is used only by preflight and the shutdown screen.
- [ ] Record `Event::InputBackend` at activation. Commit `feat(tui): Windows VT input backend owns console input`.

### Task 5: App integration

- [ ] `surface/stack.rs`: `ids: Vec<u64>`, `next_id`; push/replace_top assign new ids; `pub fn top_id(&self) -> Option<u64>`. Unit test: replacing the top changes `top_id`.
- [ ] `app/mod.rs`: `on_terminal` → `on_input(InputAction, guard)`. Fields: `paste_target: Option<(u64, PasteTarget)>` (`enum PasteTarget { Composer, Surface(u64) }`), `pastes_since_submit: u32`, `input_warning_shown: bool`.
- [ ] `app/clipboard.rs`:
  - `begin_paste(txn)` captures the target and records the begin phase.
  - `apply_paste_text(text, source, txn)` (the only text-paste entry point; terminal and clipboard both use it):
    - A clipboard paste captures its target on the spot.
    - A terminal paste with a missing or changed target inserts nothing and shows the "focus changed" warning.
    - Composer target: `composer.insert(text)` + `refresh_completions()`.
    - Surface target: `surfaces.handle_paste`, or a "this dialog does not accept pasted text" hint when it declines.
    - In every case it records the end phase with its outcome and never calls `submit`.
  - `abort_paste(txn, reason, bytes)` shows a warning with the reason and the `Alt+V` route.
  - `paste_image_from_clipboard`'s `Text` arm calls `apply_paste_text(text, Clipboard, 0)`.
- [ ] `submit` records `PasteDraftSubmitted` when `pastes_since_submit > 0`, with `kind` set to command/shell/steer/prompt, then resets the counter.
- [ ] Startup: when `input_status()` reports `paste_framing == false` on Windows, show a one-time warning notice.
- [ ] **Tests** in `app/clipboard.rs` (`#[tokio::test]` with `super::serve::tests::app_with`):
  - The paste-alone action stream sends zero requests.
  - Paste, then an `Enter` key while idle, sends exactly one `session/prompt` whose `params.text` is the whole draft.
  - With `state` busy (apply `UiEvent::Submitted` + an assistant delta), it sends exactly one `session/steer`.
  - Paste of `"/clear\n"` → the draft equals `"/clear\n"` and no command runs.
  - A permission prompt surface open before the paste starts → no frames are written and the prompt is still open.
  - A prompt that opens between `PasteStarted` and `PasteText` → the composer is unchanged and a warning notice appears.
  - `PasteAborted` → nothing inserted and zero requests.
  - Typing `h`,`i`,`Enter` → exactly one `session/prompt` with text `"hi"`.
  - Each test drives real decoder output: `Decoder` fed `raw(...)` records, so the transport-to-app chain is exercised.
- [ ] Commit `fix(tui): apply terminal paste as one draft edit, never a submission`.

### Task 6: Docs, version, verification

- [ ] README: replace the paste paragraph with the supported-hosts summary. `docs/` gets a "Terminal input on Windows" section in README (there is no separate input doc).
- [ ] `version.json` build → 182.
- [ ] `cargo test --workspace`, `cargo test -p coda-proto --features schema --lib`, the engine contract with `CODA_ENGINE`, `cargo clippy -p coda-tui --all-targets` (no new warnings in touched files).
- [ ] Commit, push, open the PR, merge.
