# Picker and Composer Clipboard UX Design

## Scope

This project improves two related interaction surfaces:

1. The `/model` picker visibly identifies and initially selects the active model.
2. The composer gains predictable native-style mouse selection, copying, pasting, and context-menu behavior.

The interactive MCP manager, exit summary, and logo replacement are separate projects.

## Goals

- Make the active model unambiguous before the user changes it.
- Preserve Terminal.Gui `TextView` as the authority for wrapped-line mouse positioning and selection.
- Give composer clipboard actions precedence over Coda's global Ctrl+C exit chord.
- Report clipboard outcomes briefly in the operational status row.
- Keep keyboard paste, transcript selection, command completion, and exit chords working as they do now.

## Model Picker

Prompt options gain semantic current-state metadata rather than encoding markers into model IDs or labels. The current model option:

- renders with a warm `●` marker and `Current` detail;
- is initially highlighted when the picker opens;
- remains a valid selection;
- does not cause an unnecessary settings rewrite when selected unchanged.

Terminal.Gui and Spectre prompt renderers consume the same semantic option state so fallback behavior remains consistent.

## Composer Mouse and Clipboard Behavior

### Selection

Terminal.Gui `TextView` continues to own left-button drag selection. Coda does not introduce a second selection model or convert wrapped display coordinates itself.

### Copy

When the composer has a non-empty selection:

- Ctrl+C copies the selected text instead of entering the exit chord.
- A left click copies the selection and clears it.
- A right click copies the selection and clears it.
- Successful copy displays `<N> symbols copied to clipboard` briefly.

The count uses Unicode text-element boundaries so user-visible symbols, not UTF-16 code units, are reported.

### Paste

When the composer has no selection, a right click:

1. lets `TextView` position the caret from the mouse event;
2. reads the clipboard;
3. inserts clipboard text through the native incremental paste path at that caret;
4. displays a brief pasted-symbol count.

Ctrl+V remains the direct keyboard paste binding. Empty or unavailable clipboard content does not mutate the draft and produces a short operational status message.

### Context Menu

A middle click opens the existing `TextView` context menu at the pointer. Right click is reserved for the copy-or-paste behavior above.

Mouse clipboard actions are disabled while a modal prompt owns input or the composer is otherwise unavailable.

## Component Responsibilities

### `UiPromptOption` and prompt requests

Carry current-option state and the initially selected option ID without coupling command code to a renderer.

### `ModelCommand`

Identifies the active model, marks the corresponding option as current, and supplies it as the initial picker selection.

### `PromptOverlay` and Spectre prompt adapter

Render the current marker consistently and focus the supplied initial option.

### `ComposerView`

Owns mouse-event arbitration around native `TextView` behavior. It exposes explicit copy, paste, and context-menu intents but does not own application status presentation.

### `TerminalGuiShellBase`

Owns clipboard access, Ctrl+C precedence, and transient operational status. Clipboard reader and writer seams remain injectable for deterministic tests.

## Event Flow

For drag selection, events pass directly to `TextView`.

For click actions, `ComposerView` determines whether a selection exists and emits the corresponding intent. The shell performs clipboard I/O and returns an explicit success or failure result. ComposerView clears selection only after a successful copy and mutates text only after a successful clipboard read.

For right-click paste, native mouse positioning occurs before paste intent dispatch. This preserves wrapped-line caret correctness and avoids duplicating Terminal.Gui's coordinate mapping.

For Ctrl+C, the shell checks composer selection first, transcript selection second, and only then evaluates the exit chord.

## Error Handling

- Clipboard access failures are non-fatal and appear in the operational status row.
- Failed copy leaves the selection intact.
- Failed or empty paste leaves the draft and cursor unchanged.
- Missing context-menu support is reported without affecting editing.
- Prompt options without current-state metadata retain their existing rendering and selection behavior.

## Testing

Focused tests cover:

- active-model marker and initial selection in Terminal.Gui and Spectre paths;
- selecting the already-active model without a redundant save;
- wrapped multiline mouse selection;
- Ctrl+C precedence for composer and transcript selections;
- left-click and right-click copy-and-clear;
- failed copy retaining selection;
- right-click paste at a clicked wrapped-line caret;
- empty and unavailable clipboard behavior;
- middle-click context-menu placement;
- Unicode symbol counts;
- Ctrl+V, command completion, mouse-caret synchronization, and double-Ctrl+C regressions.

The implementation must pass the existing TUI and engine suites and a warning-free solution build.
