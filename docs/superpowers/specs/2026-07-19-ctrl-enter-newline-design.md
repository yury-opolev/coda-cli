# Ctrl+Enter Newline Design

## Behavior

- `Ctrl+Enter` inserts a newline in the composer when the terminal reports it as a distinct modified key.
- `Ctrl+J` remains an equivalent, broadly compatible terminal fallback.
- Plain `Enter` continues to submit, or complete and submit an active slash-command suggestion.
- During bracketed paste, Enter behavior remains literal and cannot submit.

## Implementation

- Map `Key.Enter.WithCtrl` to `UiAction.InsertNewline` before plain Enter handling in `UiActionMap`.
- Reuse the existing native incremental newline path; do not add composer-specific duplicate editing logic.
- Update README and CLI help to present `Ctrl+Enter` first and `Ctrl+J` as the fallback.

## Testing and Release

- Add action-map and real `ComposerView` tests proving Ctrl+Enter inserts without submitting.
- Keep existing Ctrl+J, plain Enter, completion submission, and paste tests green.
- Release as Coda 0.1.73 through a pull request and update the locally installed tool.
