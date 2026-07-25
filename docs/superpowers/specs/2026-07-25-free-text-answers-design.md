# Free-Text Answers for Agent Questions — Design

*Date: 2026-07-25*
*Status: proposed*

## Goal

Guarantee that the interactive TUI **always** offers a free-text answer (plus a dismiss/clarify escape)
for the agent's `ask_user_question`, so the user is never forced to pick only from the agent's fixed
options. The guarantee is enforced by the framework at the one point every agent question is rendered —
not left to the agent to remember. Parity: serve already permits free text; make the intent explicit on
the wire.

## Problem / current state

- **Tool:** `ask_user_question` (`src/Coda.Agent/Tools/AskUserQuestionTool.cs:37,54-82`) *requires* an
  `options` array, calls `IUserQuestionPrompt.AskAsync(question, options, multiSelect) → Task<string>`,
  and reports `"User selected: {answer}"`. There is **no free-text path**.
- **TUI impl:** `TuiUserQuestionPrompt` (`src/Coda.Tui/Agent/TuiUserQuestionPrompt.cs`) builds
  `UiPromptRequest.Select`/`SelectMany` — a **selection picker** — and returns the chosen option label(s);
  cancellation yields an empty answer. No free text.
- **Serve impl:** `WireUserQuestionPrompt` (`src/Coda.Sdk/Serve/WireUserQuestionPrompt.cs:36-37`) returns
  `resp.Answer` — an **unconstrained string** — so the orchestrator can *already* answer freely. **The gap
  is purely the interactive TUI.**
- **The model already supports free text:** `UiPromptKind.Text` and `UiPromptResponse.Text` exist
  (`src/Coda.Tui/Ui/Prompts/UiPromptModels.cs:17,69`), and the prompt overlay already renders a text-entry
  mode for `Text` prompts (used by `/mcp add`, marketplace, etc.). The capability exists; it is simply never
  offered for agent questions.

## Design decisions (from brainstorm)

- **Enforcement (the guarantee):** implemented at the single renderer every agent question passes through
  (`TuiUserQuestionPrompt` → prompt overlay). Every `ask_user_question` gets it by construction; a test
  asserts the overlay always includes the free-text affordance. The agent cannot opt out.
- **UX:** append a **`✎ Type your own answer…`** row at the bottom of the options; selecting it switches
  the overlay into its existing text-entry mode; Enter submits the free text. `Esc` in the list cancels the
  whole prompt (dismiss → type a normal message); `Esc` in text-entry returns to the list.
- **Scope:** agent questions only. Closed-set internal pickers (`/model`, `/resume`, login, `/mcp`,
  `/plugin`, marketplace, setup, command palette) keep free text **off** — their option sets are the only
  valid answers.
- **Multi-select:** choosing "type your own" is a distinct path — free text *replaces* any selection.
- **Serve parity:** serve already returns an unconstrained answer; add `allowFreeText: true` to the wire
  `QuestionRequest` so the orchestrator's UI also knows to offer a free-text input. Backward-compatible.

## Non-goals (YAGNI)

- Free text on closed-set internal pickers (one-line opt-in later via the flag).
- Changing the agent tool's JSON schema (`options` stays; free text is a UI guarantee, not a tool param).
- Combining free text *with* selections in a single answer.

## Components

### 1. `UiPromptRequest.AllowFreeText` — `Coda.Tui.Ui.Prompts`

- Add `bool AllowFreeText` (default `false`) to `UiPromptRequest`. The `Select`/`SelectMany` factories gain
  an optional `allowFreeText = false` parameter. `Confirm`/`Text`/`Secret` are unaffected.

### 2. Prompt overlay — `Coda.Tui.Ui.Shells` (`PromptOverlay`)

- When `AllowFreeText` is set, render a trailing synthetic row `✎ Type your own answer…` **after** the
  options (always last, visually distinct). Selecting it transitions the overlay into the **existing**
  text-entry rendering (reused from `UiPromptKind.Text`); Enter submits → `UiPromptResponse.Text` set,
  `SelectedIds` empty. `Esc` in text-entry returns to the list.
- The row is appended **unconditionally** whenever `AllowFreeText` — there is no code path for an agent
  question that lacks it. A guarantee test locks this.

### 3. `TuiUserQuestionPrompt` — `Coda.Tui.Agent`

- Build the request with `allowFreeText: true`. On the response: if `response.Text` is non-empty use it as
  the answer (free text); otherwise map `SelectedIds` → labels as today. Continue publishing
  `UserQuestionRequestedEvent`/`UserQuestionResolvedEvent`.

### 4. `AskUserQuestionTool` result — `Coda.Agent`

- Report `"User answered: {answer}"` uniformly. The answer's content already conveys whether it was a preset
  option or free-form, and this avoids fragile "was it one of the options?" comparisons (especially for
  multi-select). `IUserQuestionPrompt.AskAsync` keeps its `Task<string>` signature — no interface change.

### 5. Serve — `Coda.Sdk.Serve`

- Extend the wire `QuestionRequest` message with `allowFreeText` (`true` for agent questions).
  `WireUserQuestionPrompt` still returns `resp.Answer` unchanged (already unconstrained). The flag signals
  the orchestrator's UI to offer a free-text input; older orchestrators ignore it and can still answer
  freely. Backward-compatible.

## Data flow

```
ask_user_question(options)
  → IUserQuestionPrompt.AskAsync(question, options, multiSelect)
      TUI:  overlay = [options…] + "✎ Type your own answer…"
              → pick option → label
              → or type      → UiPromptResponse.Text
            → answer string → tool result "User answered: {answer}"
      Serve: QuestionRequest(question, options, multiSelect, allowFreeText:true)
            → orchestrator returns any Answer string → tool result "User answered: {answer}"
```

## Parity (TUI ↔ serve)

Both modes guarantee/permit free text: the TUI via the always-present `✎` row; serve via the unconstrained
answer plus the explicit `allowFreeText` flag. Identical capability in both.

## Error handling / edge cases

- Empty free-text submit ⇒ no answer (equivalent to cancel) ⇒ empty string ⇒ the agent proceeds with best
  judgment or re-asks.
- Headless (`context.UserQuestion is null`) ⇒ unchanged: "No interactive user is available; proceed using
  your best judgment."
- Long options list ⇒ the `✎` row is always last; the overlay's existing scrolling applies.
- `Esc` from the list ⇒ `Cancelled` (dismiss). `Esc` from text-entry ⇒ back to the list.

## Testing

- Overlay: `AllowFreeText` renders the `✎ Type your own answer…` row; selecting it enters text mode; submit
  → `UiPromptResponse.Text`; the row is **always** present for `AllowFreeText` requests (guarantee test).
- Internal pickers (`AllowFreeText = false`) ⇒ no free-text row.
- `TuiUserQuestionPrompt`: free text ⇒ returns the typed string; option ⇒ returns label(s).
- Tool: result is `"User answered: {answer}"`.
- Serve: `QuestionRequest` carries `allowFreeText: true`; `WireUserQuestionPrompt` returns the orchestrator's
  `Answer` verbatim.
- Follow existing `UiPromptServiceTests` / `AskUserQuestionTests` / `WireHostTests` patterns.

## Rollout

Additive: agent questions gain free text; closed-set internal pickers are unchanged; the serve wire flag is
backward-compatible.
