# Warm Ember TUI polish — design

**Date:** 2026-07-18
**Status:** Approved

## Goal

Polish Coda's retained Terminal.Gui experience so it is easier to read and faster to operate:

1. Use a warm, low-fatigue palette with clear semantic distinctions.
2. Move changing operational state into a dedicated row above the composer.
3. Grow the composer with wrapped text, then scroll internally at a bounded height.
4. Make the composer the default typing target whenever no modal prompt owns input.
5. Use the full terminal width for transcript content.

## Approved visual direction

The selected palette is **Warm Ember**:

- warm ivory/peach for assistant responses;
- soft amber for user turns and headings;
- brighter muted gold for tool calls and results;
- muted coral for approvals;
- warm yellow for questions and warnings;
- restrained red for errors;
- gray for idle/waiting states.

Colors must remain readable on a near-black background and degrade safely to named 16-color terminal
values. Avoid saturated neon blue, cyan, or magenta as primary transcript colors.

## Layout

The retained layout, from top to bottom:

1. session header;
2. full-width virtualized transcript;
3. optional command-completion overlay;
4. dedicated one-row operational status;
5. dynamically sized borderless composer;
6. one-row session metadata.

Prompt/approval overlays remain topmost.

### Transcript

- Remove the 120-column transcript cap.
- Use all available terminal width.
- Reflow on resize through the existing virtualized layout index.
- Preserve viewport-bounded rendering and incremental block updates.

### Composer visual

- Dark background across the entire composer region.
- No box border and no colored vertical accent bar.
- A warm `>` glyph marks the input start.
- Composer text begins after a small fixed gutter.
- During startup the composer and cursor are hidden; the dark region remains, but the chrome does not
  repeat `Initializing…` because that text lives in the dedicated operational row.
- The command-completion overlay appears immediately above the operational status row and never moves
  the composer or metadata.

## Composer sizing and scrolling

The composer starts at three rows and grows according to explicit newlines and visual line wrapping.

Maximum height:

```text
min(8 rows, floor(available screen height * 0.35))
```

The effective maximum is never lower than the three-row minimum.

Once the cap is reached:

- the TextView scrolls internally;
- the caret remains visible;
- no draft text is discarded;
- the transcript keeps the remaining screen area.

Height is recalculated after:

- typing/deletion;
- paste;
- completion;
- history navigation;
- terminal resize;
- mode switch/draft restore.

## Composer navigation

- Completion open: Up/Down changes the selected completion.
- Multiline draft: Up/Down moves the caret vertically within the draft.
- Up/Down moves by **visual wrapped line**, preserving the preferred display column.
- At the first/last visual line, Up/Down may transition to previous/next prompt history.
- Ctrl+Up/Ctrl+Down explicitly navigates prompt history.
- Tab completes the selected slash command.
- Escape dismisses completion.
- PageUp/PageDown and transcript-specific shortcuts continue to scroll transcript history.

## Focus and typing

The composer is the default typing target whenever no modal prompt owns input.

- Focus automatically when startup becomes ready.
- Restore focus after a prompt/approval closes.
- Preserve focus/draft after inline/full-screen mode switching.
- If transcript currently has focus, an unhandled printable character focuses the composer and inserts
  that character there.
- `/` typed from transcript focus opens slash completion in the composer.
- Transcript navigation, selection, and scrolling keys remain with the transcript.
- Modal prompts are the only intentional exception: their keys answer the prompt, never edit the composer.

### Exit and interrupt chords

- A first Esc dismisses completion, transcript selection, or other transient shell state.
- During active work, when no transient state consumed it, the first Esc arms interruption and the
  operational row shows `Press Esc again to interrupt`.
- A second Esc within 800 ms interrupts the active turn/tool/background operation.
- Ctrl+C first checks for transcript selection:
  - when selected text exists, copy it, clear selection, and do not arm exit;
  - otherwise arm exit and show `Press Ctrl+C again to exit`.
- A second Ctrl+C within 1.5 seconds requests graceful application exit.
- `/exit` remains an explicit command.
- Ctrl+D is not an exit binding.
- Expired chord windows restore the normally projected operational status.

## Transcript text selection and clipboard

When mouse support is enabled:

- left-button drag selects text in the retained transcript;
- selected cells use a readable Warm Ember highlight;
- selection may span wrapped rows and multiple transcript blocks;
- releasing the button keeps the selection but does not copy automatically;
- Ctrl+C copies the current transcript selection through Terminal.Gui's clipboard abstraction;
- when transcript has no selection, Ctrl+C retains its existing interrupt behavior;
- Escape clears transcript selection;
- mouse-wheel transcript scrolling remains available.

A press/release with no cell movement remains a normal click and keeps the existing tool/diff
expand/collapse behavior. Selection begins only after movement of at least one cell.

When clipboard access is unavailable, selection remains visible and the operational status briefly reports
`Clipboard unavailable`.

`--no-mouse` disables in-app selection. Shift+drag remains the documented native-terminal selection escape
hatch where the terminal supports bypassing mouse reporting.

## Operational status row

Changing operational state moves out of the bottom metadata line into a dedicated row above the composer.
The row is always visible.

States:

| State | Example | Tone |
|---|---|---|
| Ready | `· Ready` | gray |
| Initializing | `◌ Initializing…` | muted ochre |
| Working | `⠋ Working · running tests` | amber/orange |
| Intensive thinking | `⠋ Thinking deeply` | muted crimson/coral |
| Waiting | `◌ Waiting` | gray |
| Background work | `◌ Waiting for 2 background tasks` | gray |
| Approval | `! Waiting for approval` | coral |
| Error | concise error label | restrained red |

### Status derivation

`OperationalStatusProjector` derives `{text, tone, animated}` from the immutable snapshot:

1. pending prompt/approval;
2. startup active operation;
3. incomplete tool block;
4. active turn, with high/max effort classified as intensive thinking;
5. running background-task count;
6. idle/ready.

The bottom metadata line retains model, effort, context, token usage, cost, MCP/LSP, git, and cwd, but no
longer includes `ActiveOperation.Label`.

Chord hints (`Press Esc again…`, `Press Ctrl+C again…`) are shell-local operational overrides with higher
display priority than the projected semantic status.

### Animation

- Use a low-frequency Unicode spinner for animated states.
- Only the operational status row changes per animation tick.
- No whole-screen color waves.
- Start the timer only while the current status is animated.
- Dispose it on state change, shell stop, mode switch, failure, and exit.

## Palette architecture

Add a centralized `TuiTheme` with semantic roles instead of hard-coded colors in each view:

- transcript user;
- transcript assistant;
- heading;
- code;
- tool;
- diff;
- permission/approval;
- question;
- warning;
- notification;
- error;
- composer background/text/prompt;
- operational ready/initializing/working/thinking/waiting/approval/error;
- completion selected/normal;
- prompt overlay.

`VirtualizedTranscriptView`, `ComposerChromeView`, `CommandCompletionView`, `PromptOverlay`, and the new
operational status view consume this theme.

## Components

### `TuiTheme`

Owns Warm Ember RGB values and named 16-color fallbacks. No session or rendering state.

### `OperationalStatusProjector`

Pure projection from `UiSessionSnapshot` to an immutable display model:

```csharp
OperationalStatus(string Text, OperationalTone Tone, bool Animated)
```

### `OperationalStatusView`

Draws one status row with the projected tone and optional spinner frame.

### Dynamic composer layout

The shell computes wrapped line count using terminal cell width and grapheme boundaries. It updates the
composer/chrome height and re-anchors transcript/status/completion without changing semantic state.

### Focus router

Shell-level key handling redirects only unhandled printable input to the composer. It never intercepts
active prompt input or transcript navigation commands.

### Transcript selection model

`TranscriptSelection` stores an anchor and active endpoint as `{global row, cell column}` values. The
virtualized transcript draws selected cell ranges without moving selection into semantic session state.
Copy reconstructs text from the layout index and preserves line breaks. `TranscriptLayoutIndex` gains an
arbitrary global-row-range accessor; copy is not limited to the current viewport.

### Chord state

`ShellCommandChordState` owns the first-key timestamp and armed action for Esc/Ctrl+C. It is shell-local,
uses a monotonic clock, exposes the temporary operational hint, and resets on timeout, completion, mode
switch, prompt activation, or disposal.

## Data flow

```text
UiSessionSnapshot
  ├─ transcript roles ──────────────> TuiTheme -> VirtualizedTranscriptView
  ├─ operational projection ───────> OperationalStatusView
  ├─ stable metadata projection ───> bottom Status label
  └─ pending prompt ───────────────> PromptOverlay

Composer text/caret
  └─ wrapped-line measurement ─────> shell-local composer height

Transcript mouse/key input
  └─ shell-local selection ────────> clipboard on Ctrl+C
```

No presentation state enters `UiSessionSnapshot`.

## Error handling and lifecycle

- Timer callbacks check shell/application lifetime before drawing.
- Disposed shells unsubscribe composer, transcript, prompt, and timer handlers.
- Failed layout measurement retains the previous valid composer height.
- Terminal.Gui cleanup remains owned by `TerminalGuiModeRunner`.
- Plain and Spectre fallbacks do not create theme timers or retained composer layout state.

## Testing

### Theme and transcript

- Every transcript role maps to the approved semantic color.
- Tool and approval colors are brighter/easier to read than the previous blue/magenta.
- Named low-color fallbacks are present.
- Wide layouts use the full available transcript width.

### Composer

- Visual wrapping increases height.
- Height stops at `max(3, min(8, floor(screen height * 0.35)))`.
- Text beyond the cap remains present and the caret stays visible through internal scrolling.
- Resize recalculates height.
- Mode switch restores draft, caret, scroll, and height.
- Completion/paste/history updates height.
- Up/Down navigation follows the approved completion/editor/history precedence.

### Operational status

- Every state projects the correct text, tone, and animation flag.
- Metadata line excludes active-operation text.
- Spinner updates only the status view and stops after state change/disposal.

### Focus

- Composer focuses after startup.
- Prompt close restores composer focus.
- Printable keys from transcript focus route to composer.
- Transcript navigation stays in transcript.
- Modal prompt input is never redirected.

### Selection and clipboard

- Left-drag selects visible text across rows.
- Selection survives redraw and scrolling.
- Ctrl+C copies selected text and consumes the key.
- First Ctrl+C without selection shows the exit hint; second Ctrl+C exits.
- Escape clears selection.
- First Esc during active work shows the interrupt hint; second Esc interrupts.
- Clipboard-unavailable behavior is non-destructive.
- `--no-mouse` bypasses in-app selection.

### Regression

- Full-screen and inline retained layouts remain bottom-anchored.
- Completion overlay and prompts preserve z-order.
- Plain/redirection output remains unchanged.
- Existing actor/mailbox/virtualization tests continue passing.

## Acceptance criteria

1. Assistant, tool, approval, question, warning, and error roles map to distinct approved Warm Ember
   attributes, including brighter tool and approval colors than the previous blue/magenta.
2. Transcript uses all available terminal width.
3. Operational status is always visible above the composer with correct color/state.
4. Composer grows with wrapping to the approved cap, then scrolls without losing text.
5. Typing works immediately after readiness without requiring a mouse click.
6. Printable input redirects from transcript to composer when no modal is active.
7. Startup, prompt, mode-switch, resize, and shutdown paths leave focus/timers/layout consistent.
8. Transcript text can be selected with left-drag and copied with Ctrl+C without breaking interrupt behavior.
9. Esc/Ctrl+C chord hints appear in the operational row and expire/reset deterministically.

## Out of scope

- A user-configurable theme editor.
- Arbitrary custom RGB configuration.
- Rich syntax highlighting inside assistant Markdown/code blocks.
- Changing engine/tool execution semantics.
