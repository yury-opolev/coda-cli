# Exit Session Summary and Shift+Enter Design

## Scope

This release adds:

1. A visible post-terminal exit card for every clean interactive exit.
2. `Shift+Enter` as another composer newline shortcut.

## Exit Card Lifecycle

The card renders after Terminal.Gui has restored the console and after pending UI output has drained. It appears for `/exit`, `/quit`, double Ctrl+C, and clean EOF/terminal shutdown. It does not replace crash diagnostics or render during mode switches.

`ExitCommand` stops printing the old standalone `Goodbye.` line; all clean exit paths use the centralized card.

## Snapshot

Create an immutable `SessionExitSnapshot` containing:

- elapsed session duration;
- session ID, when persisted;
- working directory;
- provider, model, and reasoning effort;
- conversation message count;
- input, output, and total token usage;
- estimated USD cost using the existing pricing/catalog logic;
- latest cached context usage: used tokens, maximum tokens, percentage, and exact/estimated status.

The runner records session start time through an injectable `TimeProvider`. Shutdown must not trigger a new provider request or context analysis. If no cached context report exists, the card says `Context: not measured`.

## Rendering

Create a focused `ExitSummaryRenderer` that writes to the restored real console:

- the embedded Coda logo from `Branding.BannerLines`;
- a compact session summary;
- exact continuation instructions when a session ID exists:
  - `coda --cwd "<working-directory>" --resume <session-id>`
  - `coda --cwd "<working-directory>" --continue`

Paths and IDs are escaped for display. If the session has not been persisted, the card says so and omits resume commands.

Rendering is best-effort: an output failure must not convert a successful interactive exit into a non-zero process result.

## Shift+Enter

`Key.Enter.WithShift` maps to the existing `UiAction.InsertNewline`, alongside `Ctrl+Enter` and `Ctrl+J`, before plain Enter handling. Plain Enter continues to submit or complete-and-submit. Help and README list all three newline shortcuts and explain that modified Enter depends on terminal support.

## Testing and Release

- Unit-test snapshot projection, duration, pricing, missing context, and continuation commands.
- Integration-test that the summary renders after a clean host exit and is absent during mode switches.
- Test `/exit`, double Ctrl+C/host exit seams, and no-session behavior through existing lifecycle seams.
- Add action-map and real composer tests for Shift+Enter while preserving Ctrl+Enter, Ctrl+J, plain Enter, completion, and paste behavior.
- Release through a pull request, rebuild all projects, and update the locally installed Coda tool.
