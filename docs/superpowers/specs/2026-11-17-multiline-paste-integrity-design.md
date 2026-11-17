# Multiline paste integrity — design

Date: 2026-11-17. Scope: sections 11–13.2 and 14 of the upstream request
"preserve timeline chronology and multiline input in Coda TUI" (paste half
only; the timeline half is a separate change).

## Problem

One multiline paste must become one editable draft. Pasting must submit
nothing; one explicit send must submit exactly one message (a new prompt
when idle, one steering message while a turn runs).

On Windows this did not hold for terminal-managed paste (Windows Terminal
`Ctrl+V`, right-click, `Ctrl+Shift+V`):

* `rust/crates/coda-tui/src/terminal.rs` wrote `ESC[?2004h` (bracketed paste)
  and read input with `crossterm::event::{poll, read}`.
* crossterm 0.29.0 (still the newest release on crates.io, checked
  2026-11-17 with `cargo search crossterm`) reads native console records on
  Windows. `src/event/source/windows.rs` maps `KEY_EVENT` records to
  `Event::Key` and has no bracketed-paste parser; that parser exists only in
  `src/event/sys/unix/parse.rs`. Upgrading crossterm does not fix this.
* The console host only reports the paste brackets to a client that set
  `ENABLE_VIRTUAL_TERMINAL_INPUT` (confirmed by Microsoft in
  microsoft/terminal#18094). Without it the paste arrives as plain key
  records, and every `\r` is an Enter key, which the keymap maps to Submit.
  One paste became one submission per line.

The upper layer was already right: `TerminalEvent::Paste(text)` goes to
`App::on_paste`, which inserts the whole string without submitting. The
Windows transport never produced that event.

### Evidence gathered (ConPTY probe)

A throwaway probe (not committed) created a pseudo console with
`CreatePseudoConsole` on Windows 11 build 26300, ran a child that read raw
`INPUT_RECORD`s, and wrote the byte sequences Windows Terminal sends into the
ConPTY input pipe. Findings:

1. The client's `ESC[?2004h` is forwarded to the terminal on the output pipe,
   so the terminal knows to bracket pastes.
2. With `ENABLE_VIRTUAL_TERMINAL_INPUT` set, `ESC[200~ … ESC[201~` reaches the
   client as key-down records with `wVirtualKeyCode == 0` and the characters
   in `uChar`. Without it, the brackets are removed.
3. In VT input mode plain key records lose modifiers (Shift+Enter arrives as a
   bare `\r`). If the client also writes `ESC[?9001h` (win32-input-mode), the
   console host encodes every key as
   `ESC [ Vk ; Sc ; Uc ; Kd ; Cs ; Rc _`, with the full virtual-key code,
   scan code, character, key-down flag, control-key state and repeat count.
   Pasted characters are encoded the same way. The paste brackets and other
   pass-through sequences arrive as wrapped records with `Vk == 0`.
4. Pass-through sequences (SGR mouse `ESC[<…M`, focus `ESC[I`) reach a VT
   input client as characters, not as `MOUSE_EVENT`/`FOCUS_EVENT` records.
5. Characters outside the BMP arrive as an Alt key-up record carrying a
   surrogate, then another for the low surrogate (the Alt+numpad convention
   crossterm already handles).
6. Inside a paste, `\n` is synthesised as Ctrl key-down, `\n` record, Ctrl
   key-up; `\r` as a plain Enter record.

## Decisions

| Option | Verdict |
| --- | --- |
| Upgrade crossterm | Rejected: 0.29.0 is the newest release and has no Windows paste parser. |
| Patch or fork crossterm | Rejected: a long-lived fork of a core dependency for one input path. |
| Timing/typing-speed heuristics, dedupe | Rejected by the request; cannot establish paste boundaries. |
| VT input only | Rejected: loses Shift+Enter / Ctrl+Enter newline and other modifier keys. |
| **VT input + win32-input-mode, Coda-owned decoder** | **Chosen.** Full key fidelity plus real paste framing; verified with the probe. |

### Chosen design

**1. One input owner.** A new `input` module owns terminal input for the
whole process. `terminal::read_input()` (replacing `read_event`) returns
`InputAction`s. On Windows the active backend is `WindowsVt`; elsewhere it is
crossterm (which already decodes bracketed paste on Unix). There is never a
second reader: crossterm's event reader is not called while `WindowsVt` is
active.

**2. Console mode ownership (Windows).** `TerminalGuard` activates the
backend after raw mode and the alternate screen and deactivates it before
anything else on restore. Activation saves the `CONIN$` mode, sets
`(saved & !QUICK_EDIT) | EXTENDED_FLAGS | WINDOW_INPUT |
VIRTUAL_TERMINAL_INPUT` plus `MOUSE_INPUT` when mouse capture is on, and
writes `ESC[?9001h`. Deactivation writes `ESC[?9001l` and restores the
saved mode. On Windows the backend sets mouse input itself instead of
crossterm's `EnableMouseCapture` (which overwrites the whole console mode and
would clear VT input). Normal exit, error exit, panic hook and
`TerminalGuard::suspended` (external programs) all go through the same
restore; `suspended` reactivates afterwards and drops any half-received
transaction (reported as cancelled).

If `SetConsoleMode` rejects VT input (legacy console mode), the backend falls
back to crossterm's native reader and reports `paste framing: unavailable`.
The TUI shows a one-time warning naming `Alt+V` as the reliable route.

**3. Decoder (platform-independent, pure).** `input::decoder::Decoder` is fed
raw console records (`Record::Key {vk, scan, unit, down, state, repeat}`,
`Mouse`, `Resize`, `Focus`) and a clock, and yields `InputAction`s. It is pure
so it is unit- and property-tested on every platform.

* *Unit stage:* key-down records with a character, and Alt key-up records with
  a character, become UTF-16 units; other key-ups and character-less
  modifier records are ignored.
* *Outer stage (win32-input-mode):* recognises `ESC [ params _` wrappers and
  turns them into key records. Wrapped records with `Vk == 0` are characters
  and continue to the inner stage; records with a key code are keys.
  Anything that is not a wrapper is forwarded unchanged.
* *Inner stage (VT):* recognises `ESC[200~` (paste begin), `ESC[201~`, focus
  `ESC[I`/`ESC[O`, SGR mouse, CSI/SS3 cursor and function keys with xterm
  modifiers, CSI-u, and maps C0 controls and plain characters to keys exactly
  like crossterm's Unix parser. A lone `ESC` is an Esc key once 30 ms pass
  without further input (only relevant when win32-input-mode is not honoured).
* *Key mapping:* virtual-key records are mapped with a port of crossterm
  0.29's Windows `parse_key_event_record` (MIT), so ordinary keys behave as
  they did before.

**4. Paste transaction.** `ESC[200~` opens transaction *N*. While open:

* Every character is literal text (no key bindings, no wrappers interpreted as
  keys); only `ESC[201~` ends it, matched across arbitrary read boundaries.
* `\r\n`, `\r` and `\n` become `\n`, including a CR and LF that arrive in
  different reads. Surrogate pairs split across reads are rejoined; a lone
  surrogate becomes U+FFFD.
* Content is capped at 1 MiB of UTF-8. Past the cap the decoder keeps
  consuming until the end marker but stores nothing, and then reports
  `PasteAborted { reason: TooLarge }`.
* If the end marker does not arrive within 2 s of the last input, the
  transaction is aborted (`Incomplete`); buffered text is discarded, never
  replayed as keys.
* Deactivation (exit, suspension) aborts an open transaction (`Cancelled`).

The decoder emits `PasteStarted { txn }` at the begin marker and
`PasteText { text, source: Terminal, txn }` (or `PasteAborted`) at the end.

**5. Applying a paste (App).** `App` captures the paste target when the
transaction starts: the composer (no surface open) or the identity of the top
surface. At `PasteText`:

* Target unchanged → one editor operation: `Composer::insert` (or the
  surface's `handle_paste`). Completion suggestions refresh; nothing is
  accepted or run.
* Target changed (a permission prompt or question opened mid-transaction) →
  nothing is inserted anywhere and a warning says the paste was discarded
  because focus changed. Text never reaches the hidden composer.
* A surface that does not accept text (permission prompts, menus) declines;
  the user is told the paste was ignored. A paste can never approve anything:
  it never becomes key events.
* `PasteAborted` → a warning with the reason and the `Alt+V` route; no
  insertion, no submission.

Application-owned clipboard reads (`Alt+V`, `Ctrl+V` when it reaches the
app, `Shift+Insert`, right-click) go through the same text-insertion path
with `source: Clipboard`; image paste is unchanged. One gesture has one
owner: a terminal-managed paste never produces a key the app binds, and
while a transaction is open, keys are content, so a clipboard shortcut cannot
fire inside it. Nothing deduplicates by text or timing.

Paste and submit are separate APIs: no paste path calls `submit`, `steer`,
command dispatch, completion acceptance or approval. `submit` remains the only
caller of `session/prompt` and `session/steer`.

**6. Diagnostics (metadata only).** New `coda-diagnostics` events:

* `input_backend { backend, paste_framing, key_encoding }` at activation.
* `paste_transaction { phase, source, txn, bytes, lines, outcome }` for
  begin / end / cancel.
* `paste_draft_submitted { pastes, kind }` when a draft that received pastes
  is submitted (`kind` = prompt / steer / command / shell); one record per
  submission, so the submission count per paste is visible.

No pasted text, clipboard contents or prompt text is recorded.

## Supported hosts

| Host | Terminal paste | Notes |
| --- | --- | --- |
| Windows Terminal (ConPTY), Windows 10 1809+ / 11 | Supported | Paste framing and key fidelity via VT input + win32-input-mode. Exercised through a ConPTY harness on build 26300. |
| VS Code / other ConPTY-based terminals that implement bracketed paste | Expected to work | Same console-host path; not exercised interactively. |
| Classic console window (conhost), VT input available | Keys work; terminal paste framing depends on the conhost build | Not exercised interactively. `Alt+V` is the reliable route. |
| Legacy console mode (VT input rejected) | Unsupported | Detected; warning shown; crossterm native reader used; `Alt+V` works. |
| Unix terminals with bracketed paste | Supported (unchanged) | crossterm's Unix parser. |

## Testing

* Decoder unit tests: framing, CRLF/CR/LF/mixed, trailing newline, empty lines,
  indentation, Unicode/surrogates, slash-command and control bytes inside a
  paste, stray end marker, overflow, incomplete, cancellation, win32 wrappers
  for Shift+Enter / Ctrl+C / Esc / Alt-code characters, SGR mouse, focus,
  lone Esc.
* Property test: random splits of encoded pastes (raw VT and win32-wrapped)
  into arbitrary record batches yield the same `InputAction`s as one batch.
* App tests on the existing fake engine (`app_with`): paste then one Enter
  while idle → exactly one `session/prompt` whose `text` equals the whole
  draft; while busy → exactly one `session/steer`; paste alone → zero
  requests; paste with `/command` text → no command; paste while a
  permission prompt is open → no approval frame; focus change mid-paste →
  nothing inserted; abort → nothing inserted, no request; ordinary typing +
  Enter still sends one prompt.
* `terminal.rs` restore ordering test extended with the input step.

Live interactive verification in a real Windows Terminal window was not
possible in the automated environment; the ConPTY probe exercised the console
host layer only.

## Deferred / limitations

* Hosts that do not report paste framing cannot be fixed by the client; they
  get an explicit warning and the `Alt+V` route, never a heuristic.
* A terminal that sends a paste without brackets (bracketed paste disabled in
  its settings) is indistinguishable from typing.
