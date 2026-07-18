# Terminal.Gui inline + full-screen TUI — design

**Date:** 2026-07-17
**Status:** Approved

## Goal

Replace Coda's line-oriented interactive console with a cross-platform TUI architecture that:

1. Uses a composer-first inline mode by default while preserving native terminal scrollback.
2. Offers an optional transcript-focused full-screen mode using the alternate screen.
3. Shows live model, effort, context, usage, permission, and service status without disrupting input.
4. Supports keyboard-complete operation, with mouse interaction as an optional enhancement.
5. Runs on modern Windows, macOS, and Linux terminals, with safe fallback for redirected output, SSH, multiplexers, and limited terminals.
6. Prevents flicker, output/input corruption, and terminal-state leaks during streaming, cancellation, resize, failure, or exit.

## Scope

This specification covers the interactive TUI architecture, rendering modes, input, status presentation, terminal lifecycle, migration, and testing.

Remote MCP OAuth is a separate security subsystem and will receive its own specification and implementation plan. The TUI will expose generic status, authentication, picker, confirmation, and diagnostic surfaces that MCP can use without embedding OAuth behavior in the renderer.

## Current state and constraints

- `Coda.Tui` targets .NET 10 and references Spectre.Console 0.55.2.
- `TuiApp` is a line-oriented REPL. Commands and the agent sink write directly to `IAnsiConsole`.
- `InteractiveLineEditor` mixes Spectre input with direct `System.Console` cursor coordinates and cursor movement.
- `SessionState` already carries provider, model, effort, permission mode, history, usage, pending images, goal state, and working directory.
- `/status`, `/context`, `/cost`, and `/effort` already compute most of the data needed for a persistent status surface.
- `TuiAgentSink` renders streaming agent and tool events directly. Concurrent direct writes cannot coexist safely with a persistent composer.
- Non-interactive `run`, `serve`, redirected input, and redirected output must remain plain and script-friendly.

`System.Console` is adequate for line input and ordinary output, but it does not provide a complete cross-platform abstraction for bracketed paste, mouse reporting, focus events, raw terminal input, synchronized updates, or a retained full-screen layout. Spectre's live display can repaint content, but its documentation states that it is not thread-safe and cannot be combined safely with other interactive components.

## Decision

Use **Terminal.Gui v2** as the target interactive engine for both inline and full-screen modes.

Migration is phased:

- Keep the current Spectre REPL as a temporary compatibility fallback.
- Prove Terminal.Gui inline and full-screen behavior with a focused cross-platform spike.
- Make Terminal.Gui inline the default only after the compatibility acceptance criteria pass.
- Do not maintain Spectre and Terminal.Gui as two permanent interactive architectures.

Terminal.Gui v2 was selected because it provides:

- Inline and full-screen operation.
- Double-buffered rendering.
- Responsive layout.
- Keyboard and mouse input.
- TrueColor and Unicode support.
- Cross-platform Windows, macOS, and Linux drivers.
- A testable instance-based application model.

Consolonia is not selected because its Avalonia/XAML stack is heavier than Coda needs. A custom VT renderer is not selected because it would require Coda to own raw-mode handling, escape-sequence parsing, mouse protocols, cell buffering, damage rendering, Unicode width, and cross-platform terminal restoration.

## Architecture

### `TuiHost`

`TuiHost` owns the interactive terminal lifecycle.

Responsibilities:

- Select `plain`, `inline`, or `fullscreen`.
- Honor explicit CLI/config overrides before capability detection.
- Initialize and dispose the Terminal.Gui application.
- Enter and restore raw input, cursor, mouse, paste, and alternate-screen modes.
- Observe resize, cancellation, suspend, and process-exit signals.
- Restore terminal state after normal exit, cancellation, renderer failure, and startup failure.
- Fall back from full-screen to inline, or from inline to plain, with a visible diagnostic.

No command, sink, prompt, or logger may mutate the terminal outside the active host.

### `UiEventBus`

All interactive output flows through one bounded, serialized event channel.

Producers include:

- Agent streaming.
- Tool lifecycle events.
- Command results.
- Permission and user-question prompts.
- Model/provider/session changes.
- MCP/LSP/task status.
- Logs and diagnostics.
- Input, resize, and mode-switch events.

Streaming text deltas may be coalesced over a short interval. Completion, error, permission, cancellation, and state-transition events bypass coalescing.

The queue is bounded. When rendering falls behind, merge compatible streaming updates rather than growing memory without limit or blocking the agent engine indefinitely.

### `UiSessionSnapshot`

The reducer projects typed events into an immutable semantic snapshot.

The snapshot includes:

- Provider and model.
- Requested and effective effort.
- Context tokens used, maximum context, percentage, and exact/estimated state.
- Session input/output token totals and estimated cost.
- Working directory and git summary.
- Permission mode and pending permission count.
- MCP and LSP connection/health summaries.
- Active task/tool/goal state.
- Current session identity and connection state.
- Transcript render blocks.
- Current error or notification state.

The snapshot contains no Terminal.Gui controls, coordinates, focus state, wrapping state, or viewport state.

### Shell-local state

Each shell owns presentation details:

- Focus and selection.
- Composer cursor and visual wrapping.
- Transcript viewport and scroll position.
- Open dialogs.
- Mouse hover.
- Expanded/collapsed tool cards.
- Responsive layout decisions.

This state is not shared between shells unless it has semantic meaning.

### Renderers

1. **`InlineTuiShell`**
   - Default interactive mode.
   - Native terminal scrollback remains authoritative.
   - Completed transcript blocks are appended once.
   - A bounded bottom region contains the composer and compact status.
   - Previously emitted transcript output is not virtualized or repainted.

2. **`FullscreenTuiShell`**
   - Optional alternate-screen mode.
   - Transcript-focused layout with maximum reading width.
   - Virtualizes only visible transcript blocks.
   - Uses inline cards and modal overlays instead of a persistent sidebar.
   - Supports auto-follow and a visible "new updates" indicator when scrolled away from the bottom.

3. **`PlainOutputRenderer`**
   - Used for redirected output, CI, unsupported terminals, explicit `--plain`, and accessibility fallback.
   - Emits stable text without cursor control, alternate screen, color dependence, or interactive prompts.

## Input and actions

### Named actions

Physical keys map to named actions rather than invoking behavior directly.

Initial actions include:

- Submit prompt.
- Insert newline.
- Interrupt active operation.
- Exit application.
- Move cursor by character, word, and line.
- Navigate prompt history.
- Open command palette.
- Open model picker.
- Open session picker.
- Open MCP status.
- Scroll transcript.
- Jump to newest output.
- Toggle inline/full-screen.
- Force redraw.

Keybindings are context-sensitive and configurable. Keyboard interaction must cover every mouse-accessible feature.

### Composer

The composer supports:

- Multiline editing.
- Unicode-aware cursor movement and deletion.
- Prompt history.
- Draft preservation.
- Bracketed paste without accidental submission.
- Slash-command completion.
- Command picker integration.
- Future `@` file completion.

Switching render modes preserves the draft, cursor position, and prompt history.

### Mouse

Mouse support is optional and never required.

Initial mouse scope:

- Wheel transcript scrolling.
- Click to focus/place the composer cursor where supported reliably.
- Click selectable picker rows.
- Click expand/collapse on tool or diff cards.

Text selection and copy must retain a keyboard and terminal-native fallback. Users can disable mouse reporting explicitly.

## Transcript model

Transcript output is represented as typed render blocks:

- User prompt.
- Assistant markdown.
- Tool start/progress/result.
- Diff or patch.
- Permission request/result.
- User question/result.
- Warning/error.
- Notification.
- Session boundary.

Inline and full-screen modes both render a retained, virtualized transcript. Inline uses the primary
terminal buffer; full-screen uses the alternate screen.

Streaming assistant text remains a mutable active block until completion. Tool progress updates replace the active tool block rather than appending repeated status lines.

## Approved layouts

### Inline: composer first

From top to bottom:

1. Native terminal scrollback/transcript.
2. Large bordered composer.
3. Compact one-line status.

The composer receives visual priority. The status line does not split the composer or consume a second persistent row unless the terminal is wide enough and a future design explicitly enables it.

### Full-screen: transcript focus

From top to bottom:

1. Compact session header.
2. Full-width virtualized transcript.
3. Large bordered composer.
4. Compact status.

Diffs, tool details, context, model selection, commands, permissions, and help appear as inline cards or modal overlays. There is no permanently visible sidebar.

## Status projection

Status content responds to terminal width.

Priority order:

1. Model.
2. Effective effort.
3. Context percentage and token ratio.
4. Permission mode or pending permission state.
5. Active operation.
6. Session token usage and cost.
7. MCP/LSP health.
8. Git branch/dirty state.
9. Working directory.

Example narrow status:

```text
gpt-5.6-sol | high | ctx 42% | default
```

Example wide status:

```text
gpt-5.6-sol | high | ctx 84k/200k | default | 18.2k in / 2.4k out | $0.184 | MCP 3 | LSP 2 | main*
```

The projector truncates or removes lower-priority fields before wrapping. Status never changes composer height.

## Rendering and flicker control

- One actor exclusively owns terminal output.
- Terminal.Gui performs layout and double-buffered rendering.
- Streaming updates are coalesced and frame-rate-limited.
- Render only on state changes, input changes, resize, or animation ticks.
- Full-screen transcript rendering is virtualized.
- Both interactive modes repaint only their owned retained layout.
- Hide the cursor during frame application and restore it after.
- Use synchronized-output mode when supported by the active driver/terminal; fall back safely when unavailable.
- Avoid terminal capability queries in the render hot path.

## Error handling and cleanup

Terminal modes are acquired resources and disposed in reverse order.

The host must restore:

- Alternate screen.
- Raw/cooked input.
- Cursor visibility and shape.
- Mouse reporting.
- Bracketed paste.
- Focus reporting.
- Synchronized update mode.
- Scroll regions.

Ctrl-C interrupts the active operation first. Exit is a separate action and remains available after interruption.

If the renderer fails:

1. Stop accepting interactive updates.
2. Restore terminal state.
3. Write a concise diagnostic to stderr.
4. Restart in the next safer mode when possible.

During migration, the runtime fallback ladder is:

```text
Terminal.Gui full-screen -> Terminal.Gui inline -> Spectre REPL -> plain
```

After the Spectre fallback is retired, the ladder becomes full-screen -> inline -> plain.

Do not swallow terminal initialization, rendering, or cleanup failures silently. Cleanup should attempt all independent restoration steps while retaining the primary failure for diagnostics.

## Mode selection and compatibility

Supported controls:

```text
--tui=auto
--tui=inline
--tui=fullscreen
--plain
```

`auto` chooses:

1. Plain when input/output is redirected or the terminal is non-interactive.
2. Inline when interactive capabilities are sufficient.
3. Current Spectre fallback during migration if Terminal.Gui initialization fails.

Full-screen is opt-in until explicitly promoted by a future decision.

Compatibility targets:

- Windows Terminal.
- VS Code/Cursor integrated terminal.
- iTerm2.
- Apple Terminal.
- Common Linux terminals.
- tmux and screen.
- Local and SSH sessions.

Capability detection is advisory. Explicit user overrides and safe fallbacks remain available because environment variables and terminal queries are imperfect.

The minimum interactive layout is **60 columns by 12 rows**. Below either limit, `auto` selects plain mode and an explicitly requested interactive mode reports that the terminal is too small. At the minimum size, the composer retains one visible content row, the status remains one line, and submitted output is still readable without horizontal corruption.

## Migration

### Phase 1: compatibility spike

Build a small Terminal.Gui harness covering:

- Inline and full-screen startup.
- Streaming while typing.
- Resize.
- Unicode and wide characters.
- Multiline paste.
- Cancellation and exit.
- Mouse enabled/disabled.
- Terminal restoration after exceptions.

Run it across the compatibility targets before changing the production default.

### Phase 2: semantic UI boundary

- Introduce typed UI events and the bounded UI actor.
- Replace direct rendering in the agent sink with event publication.
- Add the semantic reducer and status projector.
- Add the plain renderer.
- Route existing Spectre behavior through the new boundary where practical.

### Phase 3: Terminal.Gui inline

- Implement composer-first inline mode.
- Add named actions, multiline editing, history, and command completion.
- Add compact responsive status.
- Ship behind `--tui=inline`.
- Promote to `auto` default after acceptance criteria pass.

### Phase 4: Terminal.Gui full-screen

- Add alternate-screen lifecycle.
- Add virtualized transcript.
- Add auto-follow and new-update indication.
- Add modal command/model/session/diff/help surfaces.
- Ship behind `--tui=fullscreen`.

### Phase 5: enhancements

- Mouse affordances.
- Richer diff and tool cards.
- `@` file completion.
- Synchronized-output optimization if not already provided by the driver.
- Configurable keybindings and theme extensions.

## Testing

### Unit

- Event reducer transitions.
- Status priority/truncation at narrow, medium, and wide widths.
- Named-action keymaps by input context.
- Composer editing, Unicode, history, paste, and draft preservation.
- Transcript block construction and streaming coalescing.
- Queue bounds and backpressure behavior.
- Mode policy and explicit overrides.

### Render/layout

- Snapshot tests for approved inline and full-screen layouts.
- Width/height boundary cases.
- Modal layering and focus restoration.
- Status never wraps or changes composer height.
- Transcript-focus mode never introduces a permanent sidebar.

### Terminal lifecycle

- All acquired modes are restored after normal exit.
- Cleanup still runs after initialization, render, resize, and cancellation failures.
- Ctrl-C interrupts before exit.
- Mode switching preserves session and composer state.
- Full-screen fallback restores the primary screen before inline/plain output.

### PTY integration

- Streaming while the user types.
- Resize during streaming and while a modal is open.
- Bracketed multiline paste.
- Redirected input/output.
- SSH/tmux behavior.
- Windows/macOS/Linux process cancellation and terminal restoration.

### Manual accessibility and compatibility

- IME input.
- Screen reader/plain mode.
- Native text selection and copy.
- Mouse disabled.
- Low-color terminals.
- Narrow terminals.

## Acceptance criteria

The interactive TUI can become the default when:

1. Under a synthetic load of 100 coalescible streaming events per second, keystroke-to-paint latency remains below 100 ms at the 95th percentile with no lost or reordered input actions.
2. Output never overwrites or corrupts the composer/status region.
3. Ctrl-C interrupts cleanly without an unhandled exception.
4. Terminal state is restored after normal exit and injected renderer failures.
5. Multiline paste is inserted literally and does not submit unexpectedly.
6. The approved composer-first layout remains usable at the minimum supported width.
7. Redirected output remains stable plain text.
8. The compatibility spike passes on Windows Terminal, VS Code/Cursor terminal, iTerm2 or Apple Terminal, a common Linux terminal, tmux, screen, and an SSH session.

Full-screen mode can be considered stable when:

1. Transcript memory and render cost remain bounded as conversations grow.
2. Scrolling pauses auto-follow and exposes a clear jump-to-newest action.
3. Mode switching preserves the session and composer draft.
4. All overlays are keyboard accessible.
5. Exiting or crashing always restores the primary screen.

## Out of scope

- Replacing the agent/session engine.
- Reimplementing Terminal.Gui's cell renderer or terminal drivers.
- Making mouse interaction mandatory.
- A permanent information-dense sidebar.
- Desktop GUI or web UI.
- MCP OAuth protocol design and credential handling.

## References

- Terminal.Gui v2 documentation: <https://tui-cs.github.io/Terminal.Gui/>
- Spectre.Console Live Display limitations: <https://spectreconsole.net/console/live/live-display/>
- Microsoft Console Virtual Terminal Sequences: <https://learn.microsoft.com/windows/console/console-virtual-terminal-sequences>
- Microsoft classic Console APIs versus VT: <https://learn.microsoft.com/windows/console/classic-vs-vt>
- xterm control sequences: <https://invisible-island.net/xterm/ctlseqs/ctlseqs.html>
- Synchronized output extension: <https://github.com/contour-terminal/vt-extensions/blob/master/synchronized-output.md>
