# Live Permission Mode Design

## Goal

Make `/yolo` and `/permissions` affect the next permission decision of the main agent and every already-running foreground/background subagent.

## Architecture

- Add a thread-safe session-scoped `PermissionModeState` in `Coda.Agent`.
- `SessionState.PermissionMode` delegates to that shared state.
- Pass the same state through `SessionOptions` into each per-turn pipeline.
- `ModePermissionPrompt` reads the current mode for every request instead of capturing one enum at construction.
- Parent and subagent loops share the same prompt/state reference, including loops owned by `BackgroundTaskRunner`.
- Headless/serve sessions use a fixed state unless a live state is explicitly supplied.

An already-visible approval prompt is not dismissed retroactively. The next permission decision observes the new mode.

## Testing

- Start a permission prompt in Default mode, change the shared state to Bypass, and prove the next mutating tool is allowed without invoking the inner prompt.
- Switch back to Default and prove the following request invokes the inner prompt.
- Prove a subagent/background loop receives the same state reference.
- Keep fixed-mode, plan, accept-edits, rules, headless, and serve permission tests green.

## Release

Release through a pull request, run all tests and a warning-free build, then update the locally installed Coda tool.
