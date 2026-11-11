# Question & answer UX in the TUI — design

Date: 2026-11-11 · Status: approved (autonomous; decisions recorded below) · Target version: 0.1.180

## Problem

`ask_user_question` questions open in a small `FitContent { preferred_width: 60 }` modal
(`rust/crates/coda-tui/src/surface/prompt.rs`). Option labels are truncated to one row, the
"Or type an answer" field is a single-line `TextInput` reachable only through an unadvertised `Tab`,
bracketed paste goes to the hidden composer, and there is no way to attach a screenshot. The request
(email "REQUEST CODA TUI", 2026-11-11) lists these expected behaviours, all mandatory:

1. Every question and option can be read in full.
2. Single-choice and multiple-choice questions stay easy to answer.
3. Custom answers are discoverable and actually reach the assistant.
4. Several selections plus an explanation can be submitted together without losing either.
5. Images attached to an answer reach the assistant; unsupported attachments produce a clear message.
6. Resizing the terminal preserves typed text, selections and attachments.
7. Cancelling never submits an unintended answer.

## Decision: Option A — improve the question modal

Option B (answering in the main chat) was rejected: it needs a second "pending question" mode for
the composer, and every chat message would have to be checked for whether it answers a question.
The modal keeps the existing contract (an exclusive surface; `Esc` declines) and reuses the widget
toolkit (`widgets.rs`).

## Current path (investigated)

`ask_user_question` (`coda-agent/src/tools/ask_user_question.rs`) → `ToolContext.user_question`
(`coda_tool::UserQuestion::ask -> AnswerOutcome`) → `WireUserQuestion` (`coda-serve/src/prompts.rs`)
issues the reverse request `request/question { question, options, multiSelect, allowFreeText, requestId }`
and registers it with `PendingRegistry` (display copy for `session/getPendingRequests`). The TUI
(`app/engine.rs::on_server_request`) turns it into `PendingPrompt::Question`, opens `PromptSurface`, and
answers with `{ answer }` on the raw round trip or through `session/resolveRequest { outcome: { answer } }`.
A JSON-RPC error or `session/cancelRequest` means declined. The tool returns
`ToolResult::ok("User answered: …")`. Tool results are text-only (`coda_tool::ToolResult.content: String`).

Images already flow on the prompt path: `session/prompt { images: [WireImage] }` →
`Content::Image` in the user message. Anthropic and the Copilot Responses/Messages endpoints send
real image blocks. The Copilot chat-completions encoder replaces images with
`[image attached: <type>]` text. The composer stages clipboard images through
`app/clipboard.rs::paste_image_from_clipboard` and `app/image.rs` (PNG encoding, 5 MiB cap, base64).

## Design

### 1. Protocol (additive, backward compatible)

* `request/question` params gain `acceptsImages: bool` (default `false` when absent). The engine sets
  it to `true` only when the tool path can deliver images **and** the active provider/model accepts
  image input. The same field is published in the pending-request `display`.
* The question response (raw result **and** the `session/resolveRequest` outcome) gains optional
  `images: [{ mediaType, base64 }]` (the existing `WireImage` shape). `answer` stays required and
  non-empty. An older engine ignores `images`; an older client never sends it.
* Engine validation of `images`: at most 8 entries; `mediaType` must be `image/png`, `image/jpeg`,
  `image/gif` or `image/webp`; `base64` must be valid; each entry ≤ 7,000,000 base64 characters
  (5 MiB raw). A raw reply with invalid images is `noAnswer.malformed`, the same rule as any other
  unusable reply. A `resolveRequest` with invalid images is rejected as a malformed outcome, so the
  caller can correct it and retry.
* Images attached to an `acceptsImages: false` question are dropped by the engine with a warning log.
  The TUI never sends them.
* Schemas (`QuestionRequest.json`, `QuestionResponse.json`, …) are regenerated. Update
  `docs/serve-protocol.md` and `docs/protocol/catalog.md`.

### 2. Engine

* `coda_tool`: new `ImageAttachment { media_type, base64 }`; `QuestionReply { outcome: AnswerOutcome,
  images: Vec<ImageAttachment> }`; new default method
  `UserQuestion::ask_with_attachments(question, options, multi, cancel) -> QuestionReply` that delegates
  to `ask` and attaches no images. `ToolResult` gains `images: Vec<ImageAttachment>` (default empty)
  and `with_images(..)`.
* `coda_llm::LlmClient::accepts_image_input(&self, model: &str) -> bool` defaults to `true`.
  `CopilotClient` returns `false` when the model routes to `/chat/completions`, because that encoder
  turns images into placeholder text.
* `coda-serve`: `RequestOutcome::Question` carries the images next to the `AnswerOutcome`.
  `WireUserQuestion::ask_impl` takes `accepts_images`. Plain `ask` (and the goal-escalation seam) sends
  `false`. A per-turn `ImageAwareQuestion { inner, client, model }` wrapper implements
  `ask_with_attachments` with `accepts_images = client.accepts_image_input(model)`, checked when the
  question is asked (the Copilot model cache is filled by then). Goal runs keep `ProxyAnswerer`, which
  never returns images.
* `ask_user_question` calls `ask_with_attachments`. On an answer that carries images it returns
  `"User answered: …\n\n(The user attached N image(s) to this answer; they follow this tool result.)"`
  with `.with_images(..)`.
* Agent tool batch (`agent/tools.rs`): after all `tool_result` blocks, the batch appends one
  `Content::Image` per attached image to the same user message. Providers require tool results first,
  then other content. The images are persisted with history, like prompt images.
* Goal-escalation safety (`agent/stop.rs`): the stop check compares only the **first line** of the
  answer to `GOAL_STOP_OPTION`, so "Stop — goal not met" followed by an explanation still stops.

### 3. TUI question surface

`PromptSurface` keeps permission and plan approval. For `PendingPrompt::Question` it delegates to a new
`surface/question.rs::QuestionView` (state, layout, keys, mouse, paste). `PromptSurface` keeps owning
the type, so `retire_prompt_surface` and the existing tests keep working.

**Placement and size.** `FitContent { preferred_width: 110 }`: the content width is
`min(110, terminal width − 4)`, and the height fits the content up to the terminal height. A terminal
smaller than 24×8 falls back to full screen (existing rule). On a wide terminal the modal is about
twice as wide as before.

**Layout (one scrollable column):**

```
<question, every paragraph word-wrapped (display-cell aware)>

Choose one:            | Choose any that apply:
▸ (•) 1. option text wrapped with a hanging indent so long descriptions stay readable
  ( ) 2. …
  ( ) 3. Other — type your own answer below        (synthetic row; see "Other")

Your answer / explanation (optional when an option is chosen):
▸ multi-line field, soft-wrapped, 3–8 visible rows, grows with content
Attachments: [📷 image-1.png]  [📷 image-2.png]              (only when there are attachments)
⚠ validation or capability message                            (only when there is one)
```

Single choice uses radio markers `(•)/( )`. Multiple choice uses tick boxes. The options and the
field both show the focus marker and band.

**Wrapping.** The question uses `text::wrap` per paragraph. Each option uses
`text::wrap_with_hanging_indent`. All widths are display cells (grapheme aware). Text passes through
`text::sanitize` per line, so control characters cannot corrupt the screen.

**Focus zones.** `Options` → `Answer field` → `Attachments` (only when non-empty). `Tab` and
`Shift+Tab` cycle the zones. A mouse click on an option or on the field moves focus there.

**Keys.**

| Zone | Key | Effect |
|---|---|---|
| options | `↑/↓`, `Home/End` | move highlight |
| options | `Space` | single: select (radio, no submit); multi: tick/untick |
| options | `1–9` | single: select and submit; multi: tick/untick |
| options | `Enter` | single: select highlighted and submit; multi: submit ticked |
| field | typing, `←→↑↓`, `Home/End`, `Backspace/Delete` | edit |
| field | `Shift+Enter`, `Alt+Enter`, `Ctrl+Enter`, `Ctrl+J` | newline (same chords as the composer) |
| field | `Enter` | submit |
| attachments | `←/→` | choose attachment |
| attachments | `Delete`/`Backspace` | remove the chosen attachment |
| any | `Ctrl+V`, `Alt+V`, `Shift+Insert` | paste: an image is attached; text goes into the field |
| any | bracketed paste | text goes into the field and focuses it; an empty paste tries the clipboard image |
| any | `PgUp/PgDn`, mouse wheel | scroll the modal |
| any | `Esc` | cancel (decline), exactly as today |

Except for the edit and newline chords listed above, a Ctrl/Alt/Super chord never answers. The
security test "no chord can answer a question" keeps passing.

**"Other".** If `allowFreeText`, the options list gets one "Other — type your own answer" row. If the
model already supplied an option that looks like "other" (case-insensitive: it equals or starts with
`other` followed by a non-letter, or it contains `type your own`, `your own answer`, `something else`
or `custom answer`), that option becomes the Other row and no synthetic row is added. Choosing or
ticking the Other row (Enter, a digit, Space or a click) **focuses the field** and never submits the
literal label. Submitting with Other chosen and an empty field is refused with
"Type your answer in the field below (or choose another option)."

**Answer composition** (sent as `answer`):

* chosen options only → labels joined by `", "` (unchanged; the C# contract);
* text only → the text;
* options and text → `"<labels>\n\nAdditional details from the user:\n<text>"`;
* Other with text plus other ticked options → the ticked labels, then the details section with the text;
* nothing chosen and no text → refused: "Choose an option or type an answer.";
* only images, with no choice and no text → refused: "Add a short description or choose an option to go with the image(s)."

**Images.** `Ctrl+V`/`Alt+V`/`Shift+Insert` emit `SurfaceAction::PasteIntoPrompt`, and the app reads
the clipboard. The clipboard logic is factored out of `paste_image_from_clipboard` and shared with the
composer. An image is checked, encoded and capped (5 MiB, PNG) the same way as in the composer, then
attached to the question (at most 5). Text goes into the field. If the question does not accept images,
the modal shows "Images can't be sent with this answer: the current model or engine does not accept
image input. Describe it in text instead." and nothing is attached. Each attachment shows as
`[📷 image-N.png]` in the attachments row. `Delete` removes the focused attachment. On submit, the
images go to the engine in the reply's `images` field, and the transcript shows
"[📷 N image(s) attached]" under the answer.

**Hints footer** (at most two rows, kept up to date by focus):
`↑↓ move · Space select · 1-9 pick · Tab field · Enter submit · Shift+Enter newline · Ctrl+V/Alt+V paste image · PgUp/PgDn scroll · Esc cancel`
(the hints for the focused zone come first, so the most relevant ones survive truncation).

**Scrolling.** Rendering lays out every row, records the row range of the focused element, and scrolls
so that range is visible. `PgUp/PgDn` and the mouse wheel move a manual offset, which stays until focus
moves. The offset is clamped at render time, and the last effective offset is stored in a `Cell`, so a
resize never loses the position or shows an empty screen.

**Resize.** All state (highlight, selections, field text and caret, attachments, focus zone, scroll)
lives in the surface object, which survives a resize. Only the layout is recomputed.

**Cancel.** `Esc` in any zone emits the existing decline. Images and text are discarded. Nothing is
sent as an answer.

### 4. Widget work

`TextArea` gains `insert_text(&str)` (normalises `\r\n`/`\r` to `\n`, strips other control characters
except tab, which becomes four spaces) and an opt-in soft wrap (`with_soft_wrap()`). Soft wrap breaks
lines by display width and maps the caret to the wrapped row and column. The settings form keeps the
current behaviour.

## Error handling

* Clipboard errors and too-large images show in the modal's message line (and as notices in the
  composer path, as today).
* An image the user attached to a model that cannot accept images never reaches the engine (blocked
  in the TUI). If a client sends one anyway, the engine drops it and logs a warning.
* Copilot chat-completions models are reported as not accepting images, so the user sees the
  message up front instead of the model receiving placeholder text.

## Testing

* `coda-tool` / `coda-agent`: the default `ask_with_attachments` has no images; `ask_user_question` attaches
  images and states the count; the tool batch appends `Content::Image` after every `tool_result`;
  the goal stop check uses the first line.
* `coda-llm`: Copilot `accepts_image_input` is false for chat-completions routing and true for
  Responses/Messages.
* `coda-serve`: raw and RPC parsing of `images` (valid, invalid type, invalid base64, too many);
  `acceptsImages` present in params and display; plain `ask` sends `false`.
* `coda-proto`: schema regeneration test.
* `coda-tui`: `QuestionView` unit tests for wrapping (no truncation, wide glyphs), scroll reachability
  on a 30×10 area, single/multi submission, options plus text composition, Other focusing and empty
  Other refusal, synthetic vs model-supplied Other, paste into the field, attachment add/remove and the
  unsupported message, resize state preservation, Esc decline, chord safety; `TextArea` soft wrap and
  paste; app tests: bracketed paste reaches an open question, the answer carries images on both the raw
  and the RPC path, an old engine without `acceptsImages` blocks images.

## Out of scope / deferred

* Attaching image files by path from inside the modal (the composer's `/image` remains).
* Per-option descriptions as a separate schema field (labels are wrapped in full instead).
* Image thumbnails (terminal graphics protocols).
