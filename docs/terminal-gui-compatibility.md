# Terminal.Gui v2 compatibility checklist

This document is a **reproducible checklist**, not a claim that every terminal below has already
passed. Each cell starts as `☐ untested`. Fill a cell in **only** with real evidence from a run of the
compatibility spike (`samples/Coda.TerminalGuiSpike`) driven by
[`scripts/terminal-gui-pty-smoke.ps1`](../scripts/terminal-gui-pty-smoke.ps1). Record the operator
verdict (Pass/Fail/Skip) and, where relevant, the measured numbers (for example the reported p95
key-to-paint latency). Do not mark a row Pass from expectation alone.

## How to run

```powershell
# One scenario, one terminal, both host models where supported:
./scripts/terminal-gui-pty-smoke.ps1 -TerminalName "Windows Terminal" -Mode inline     -Scenario stream
./scripts/terminal-gui-pty-smoke.ps1 -TerminalName "Windows Terminal" -Mode fullscreen -Scenario stream
```

The script launches the harness in a real terminal, captures **only** the exit code (never terminal
contents), prints the exact checklist item, asks for Pass/Fail/Skip plus an optional non-sensitive
note, and appends a row (timestamp, terminal, mode, scenario, exit code, result, note) to a CSV. No
credentials or network access are involved in any scenario.

The harness itself accepts `--mode inline|fullscreen` and
`--scenario stream|unicode|paste|resize|cancel|mouse-off|managed-crash` (plus `--help`). Streaming runs
for **ten seconds** by default; a test-only `--duration-ms` override exists purely to let CI run a short
automated smoke against an isolated ANSI screen buffer.

## Acceptance thresholds

- **Streaming latency:** p95 key-to-paint **< 100 ms** at **100 coalescible events/second** with
  **zero lost and zero reordered** actions.
- **No corruption:** the streaming transcript never overwrites the composer or status line.
- **Full-screen virtualization:** with **10,000** preloaded transcript blocks, the visible-row
  formatting work per frame stays **bounded by the viewport height** (plus a small overscan), never by
  the total block count.
- **Restoration:** startup and every exit path (clean exit, a double-`Esc` interrupt followed by an
  explicit exit, and a managed renderer crash) restore the alternate screen, cursor, mouse mode,
  bracketed paste, and scroll region.
- **Minimum sizes:** **60×12** is the minimum usable interactive layout. **59×12** (too narrow) and
  **60×11** (too short) are below that minimum and instead verify the graceful fallback (auto) and
  usage-error (explicit `--tui`) paths — they are not usable interactive layouts.
- **Warm Ember layout:** the transcript fills the full width, an **operational status row** sits
  **directly above** the composer, the **dynamic composer** starts at **three rows** and grows to a
  **capped height**, and a **stable metadata row** occupies the **final row**; focus stays on the
  composer while typing.
- **Composer cursor:** `Up`/`Down` move the composer cursor between the lines of a multi-line prompt,
  while `Ctrl+Up`/`Ctrl+Down` step through prompt history — the plain arrows never leave the composer.
- **Selection/copy:** **left-drag** selects transcript text and `Ctrl+C` copies it; **`Shift`-drag**
  (and `--no-mouse`) hands native selection and copy to the terminal where supported. There is no
  in-app Shift+arrow/`Home`/`End` selection and no composer-text copy binding.
- **Chords:** a double `Esc` interrupts the active turn (a single `Esc` only dismisses the menu or
  clears a selection); `Ctrl+C` copies a selection and, with nothing selected, exits on the second
  press; `/exit` or `/quit` exits — there is **no `Ctrl+D`** binding.

## Checklist items (columns)

Each terminal row is evaluated against every item below, in **both** inline and full-screen modes where
the terminal supports full-screen:

1. **Restore** — startup/exit restoration (alternate screen, cursor, mouse, bracketed paste, scroll region).
2. **Stream+type** — streaming while typing; p95 key-to-paint < 100 ms at 100 events/s, zero lost/reordered.
3. **No overwrite** — transcript never corrupts the composer or status.
4. **Resize (stream)** — resize while streaming reflows cleanly.
5. **Resize (prompt)** — resize while a prompt overlay is open reflows cleanly.
6. **Unicode** — wide CJK / emoji / combining marks render with correct alignment.
7. **IME** — IME composition works (where the platform provides one).
8. **Paste** — multiline bracketed paste inserts verbatim, without submitting.
9. **Selection/copy** — **left-drag** selects transcript text and `Ctrl+C` copies it; **`Shift`-drag** and `--no-mouse` hand native selection/copy to the terminal (native selection/copy in inline mode).
10. **Picker** — keyboard-only picker/completion works.
11. **Mouse off** — with the mouse disabled, the keyboard remains fully usable.
12. **Plain fallback** — low-color / `TERM=dumb` falls back to plain output.
13. **Exit/interrupt chords** — a double `Esc` interrupts the active turn; `/exit` or a second `Ctrl+C` (with no selection) then leaves cleanly, while `Ctrl+C` over a selection copies instead of exiting.
14. **Crash restore** — a managed renderer crash restores the terminal and exits non-zero.
15. **Bounded 10k** — full-screen visible-row formatting stays bounded with 10,000 blocks.
16. **Redirected** — redirected input and redirected output both use plain behavior.
17. **Min size** — **60×12** is the minimum usable interactive layout; **59×12** and **60×11** verify the fallback/error path (not usable interactive layouts).

Legend: `☐` untested · `✅` pass · `❌` fail · `➖` not applicable / unsupported.

## Matrix

Both host models are listed per terminal (`i` = inline, `f` = full-screen). Full-screen is marked `➖`
for line-oriented tools that do not host an alternate-screen application.

| Terminal | Mode | 1 Restore | 2 Stream+type | 3 No overwrite | 4 Resize (stream) | 5 Resize (prompt) | 6 Unicode | 7 IME | 8 Paste | 9 Selection/copy | 10 Picker | 11 Mouse off | 12 Plain fallback | 13 Exit/interrupt | 14 Crash restore | 15 Bounded 10k | 16 Redirected | 17 Min size |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Windows Terminal | i | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ |
| Windows Terminal | f | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| VS Code integrated terminal | i | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ |
| VS Code integrated terminal | f | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Cursor integrated terminal | i | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ |
| Cursor integrated terminal | f | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| iTerm2 | i | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ |
| iTerm2 | f | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Apple Terminal | i | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ |
| Apple Terminal | f | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| GNOME Terminal (Linux) | i | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ |
| GNOME Terminal (Linux) | f | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| tmux | i | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ |
| tmux | f | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| screen | i | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ |
| screen | f | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| SSH (local client/server) | i | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ |
| SSH (local client/server) | f | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ➖ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |

## Scenario-to-item mapping

The spike scenarios exercise the checklist items as follows. Some items (IME, native copy, plain/`dumb`
fallback, redirected I/O) are evaluated by the operator around the spike rather than by a single
scenario, because they depend on the host terminal or the surrounding pipeline rather than on harness
logic alone.

| Scenario | Primarily verifies |
|---|---|
| `stream` | 2 Stream+type, 3 No overwrite; full-screen also 15 Bounded 10k |
| `unicode` | 6 Unicode (and 7 IME by operator observation) |
| `paste` | 8 Paste |
| `resize` | 4 Resize (stream), 5 Resize (prompt), 17 Min size |
| `cancel` | 13 Exit/interrupt chords, 1 Restore |
| `mouse-off` | 11 Mouse off |
| `managed-crash` | 14 Crash restore, 1 Restore |
| (operator, around any run) | 9 Selection/copy, 10 Picker, 12 Plain fallback, 16 Redirected |

## Evidence log

Append dated notes here as rows are filled in (terminal, mode, scenario, measured p95 where relevant,
verdict). Keep the machine-readable results in the CSV produced by the smoke script; this section is
for human-readable context and links to CI artifacts.

_No verified evidence recorded yet._
