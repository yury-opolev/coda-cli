# Question & Answer UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `ask_user_question` answerable in full in the TUI: a large wrapped, scrollable modal with a visible
multi-line "Other / explanation" field, paste support, and image attachments that actually reach the model.

**Architecture:** Additive protocol fields (`acceptsImages` on `request/question`, `images` on the reply) carry
images from the TUI through `coda-serve` into `coda_tool::QuestionReply`, then into `ToolResult.images`. The agent
tool batch appends those images as `Content::Image` blocks after the tool results. In the TUI, a new
`surface/question.rs::QuestionView` (owned by `PromptSurface`) handles layout, wrapping, scrolling, focus zones,
paste and attachments.

**Tech Stack:** Rust workspace (`rust/`), ratatui, crossterm, serde, tokio, arboard (clipboard), png.

Spec: `docs/superpowers/specs/2026-11-11-question-ux-design.md`. Read it first. Every task below implements a section of it.

## Conventions (all tasks)

* Work in the worktree `C:\Users\yurio\Documents\github\coda-question-ux` (branch `feat/question-ux`). Run cargo from `rust\`.
* TDD: write the failing test, watch it fail, implement, watch it pass.
* No new compiler warnings. No new clippy findings in code you touch.
* Commit at the end of each task with the trailer
  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
* Comments only where something needs a short explanation. Match the existing comment style (explain *why*).
* ratatui drops graphemes that contain control characters, so pass every user or model string through
  `coda_render::text::sanitize` per line before it reaches a `Span`.
* `coda-tui` has a conventions test (`cargo test -p coda-tui --test conventions`, or a similar name under
  `crates/coda-tui/tests`). Glyphs must come from `crate::render::glyphs`, not inline literals. Check that test
  when adding glyphs.

## File map

| File | Responsibility |
|---|---|
| `crates/coda-tool/src/context.rs` | `ImageAttachment`, `QuestionReply`, `UserQuestion::ask_with_attachments` |
| `crates/coda-tool/src/lib.rs` | `ToolResult.images`, `with_images`; re-exports |
| `crates/coda-mcp/src/tool.rs` | literal `ToolResult {..}` gains `images: Vec::new()` |
| `crates/coda-llm/src/client.rs` | `LlmClient::accepts_image_input` (default true) |
| `crates/coda-llm/src/copilot/client.rs` | Copilot override (false on chat-completions) |
| `crates/coda-agent/src/agent/tools.rs` | append answer images after the tool results |
| `crates/coda-agent/src/tools/ask_user_question.rs` | use `ask_with_attachments`; mention image count |
| `crates/coda-agent/src/agent/stop.rs` | goal stop compares the first line only |
| `crates/coda-proto/src/messages.rs` | `QuestionRequest.accepts_images`, `QuestionResponse.images`, `WireImage: PartialEq, Eq` |
| `docs/protocol/schemas/*.json` | regenerated |
| `crates/coda-serve/src/state/requests.rs` | `RequestOutcome::Question(AnswerOutcome, Vec<ImageAttachment>)` |
| `crates/coda-serve/src/prompts.rs` | parse/validate `images`; `acceptsImages`; `ImageAwareQuestion` |
| `crates/coda-serve/src/host.rs` | use `ImageAwareQuestion` for the goal-less tool seam |
| `docs/serve-protocol.md`, `docs/protocol/catalog.md` | wire docs |
| `crates/coda-tui/src/widgets.rs` | `TextArea::insert_text`, soft wrap, `set_visible_rows`, `focus_end` helpers |
| `crates/coda-tui/src/surface/question.rs` (new) | `QuestionView`: state, layout, keys, mouse, paste, attachments |
| `crates/coda-tui/src/surface/prompt.rs` | delegate questions to `QuestionView`; placement |
| `crates/coda-tui/src/surface/mod.rs` | `Surface::handle_paste` default; `SurfaceAction::PasteIntoPrompt` |
| `crates/coda-tui/src/surface/stack.rs` | `handle_paste` routing |
| `crates/coda-tui/src/state.rs` | `PendingPrompt::Question.accepts_images` |
| `crates/coda-tui/src/app/engine.rs` | parse `acceptsImages`; responses carry images |
| `crates/coda-tui/src/api/requests.rs` | discovery path reads `acceptsImages` |
| `crates/coda-tui/src/app/clipboard.rs` | shared `read_clipboard_for_paste`; question paste |
| `crates/coda-tui/src/app/mod.rs` | bracketed paste to surfaces; `PasteIntoPrompt`; answer with images |
| `crates/coda-tui/src/app/serve.rs` | `deliver_answer` with images |
| `README.md` | user docs for the question modal |
| `version.json` | build 180 |

---

### Task 1: Tool and LLM foundation (coda-tool, coda-mcp, coda-llm)

**Files:** `crates/coda-tool/src/context.rs`, `crates/coda-tool/src/lib.rs`, `crates/coda-mcp/src/tool.rs`,
`crates/coda-llm/src/client.rs`, `crates/coda-llm/src/copilot/client.rs`

- [ ] **Step 1: Failing tests in `coda-tool/src/context.rs`** (add a `#[cfg(test)] mod` if none exists; use `#[tokio::test]`):

```rust
struct Fixed(AnswerOutcome);
#[async_trait]
impl UserQuestion for Fixed {
    async fn ask(&self, _: &str, _: &[String], _: bool, _: CancellationToken) -> AnswerOutcome { self.0.clone() }
}

#[tokio::test]
async fn the_default_rich_ask_delegates_and_attaches_no_images() {
    let reply = Fixed(AnswerOutcome::Answered("A".into()))
        .ask_with_attachments("q", &["A".into()], false, CancellationToken::new()).await;
    assert_eq!(reply.outcome, AnswerOutcome::Answered("A".into()));
    assert!(reply.images.is_empty());
}
```

In `lib.rs` tests:

```rust
#[test]
fn a_result_carries_images_only_when_asked() {
    assert!(ToolResult::ok("x").images.is_empty());
    let image = ImageAttachment { media_type: "image/png".into(), base64: "AAAA".into() };
    let result = ToolResult::ok("x").with_images(vec![image.clone()]);
    assert_eq!(result.images, vec![image]);
}
```

- [ ] **Step 2:** `cargo test -p coda-tool`. Expect a compile failure (missing items).
- [ ] **Step 3: Implement.** In `context.rs`:

```rust
/// An image the operator attached, carried as base64 so it can be replayed to a provider verbatim.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ImageAttachment {
    /// `image/png`, `image/jpeg`, `image/gif` or `image/webp`.
    pub media_type: String,
    pub base64: String,
}

/// A question's outcome plus any images the operator attached to an answer.
///
/// Images are meaningful only with [`AnswerOutcome::Answered`]; every other outcome carries none.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuestionReply {
    pub outcome: AnswerOutcome,
    pub images: Vec<ImageAttachment>,
}

impl From<AnswerOutcome> for QuestionReply {
    fn from(outcome: AnswerOutcome) -> Self { Self { outcome, images: Vec::new() } }
}
```

Add a default method to `UserQuestion`:

```rust
    /// Like [`ask`](Self::ask), but lets the operator attach images to the answer.
    ///
    /// The default attaches none, so every existing implementation keeps its behaviour.
    async fn ask_with_attachments(
        &self,
        question: &str,
        options: &[String],
        multi_select: bool,
        cancel: CancellationToken,
    ) -> QuestionReply {
        self.ask(question, options, multi_select, cancel).await.into()
    }
```

In `lib.rs`: re-export `ImageAttachment` and `QuestionReply`. Add `pub images: Vec<ImageAttachment>` to
`ToolResult` (doc: "Images to show the model after this result; empty for every ordinary tool."). Initialise it to
`Vec::new()` in every constructor (`ok`, `error`, `abort`, `attach_task`, `running_task`). Add:

```rust
    pub fn with_images(mut self, images: Vec<ImageAttachment>) -> Self { self.images = images; self }
```

Fix the struct literal in `crates/coda-mcp/src/tool.rs:140` (add `images: Vec::new()`). Run
`cargo build --workspace --tests` and fix any other literal it reports.

- [ ] **Step 4: LLM capability, failing test first.** In `coda-llm/src/copilot/client.rs` tests (the file already
seeds the model cache in tests around line 579; follow that pattern with a unique base URL per test):

```rust
#[test]
fn image_input_is_refused_only_where_the_encoder_drops_images() {
    let base = "https://images-capability.example.com";
    models::cache_set(base, vec![
        ModelInfo { supported_endpoints: vec!["/chat/completions".into()], ..ModelInfo::new("chat-only") },
        ModelInfo { supported_endpoints: vec!["/responses".into()], ..ModelInfo::new("responses-model") },
        ModelInfo { supported_endpoints: vec!["/v1/messages".into()], ..ModelInfo::new("claude-x") },
    ]);
    let client = CopilotClient::new(CopilotConfig::with_token("t").with_base_url(base)).unwrap();
    assert!(!client.accepts_image_input("chat-only"));
    assert!(client.accepts_image_input("responses-model"));
    assert!(client.accepts_image_input("claude-x"));
}
```

(Adapt the constructor calls to the real `CopilotConfig` API in the file.) Implement in `client.rs`
(`LlmClient` trait):

```rust
    /// Whether a request to `model` delivers image blocks to the model.
    ///
    /// `true` unless the provider is known to drop or rewrite images, so a caller can say so up front instead of the
    /// model silently receiving placeholder text.
    fn accepts_image_input(&self, _model: &str) -> bool { true }
```

In `CopilotClient`'s `impl LlmClient`:

```rust
    fn accepts_image_input(&self, model: &str) -> bool {
        // The chat-completions encoder replaces images with placeholder text.
        self.endpoint_for(model) != CopilotEndpoint::ChatCompletions
    }
```

(Derive or check `PartialEq` on `CopilotEndpoint`.)

- [ ] **Step 5:** `cargo test -p coda-tool -p coda-llm -p coda-mcp`. All pass.
- [ ] **Step 6:** Commit: `feat(tool): image attachments on question replies and tool results`.

### Task 2: Agent — deliver answer images to the model (coda-agent)

**Files:** `crates/coda-agent/src/agent/tools.rs`, `crates/coda-agent/src/tools/ask_user_question.rs`,
`crates/coda-agent/src/agent/stop.rs`

- [ ] **Step 1: Failing test, `ask_user_question.rs`:**

```rust
struct AnswersWithImage;
#[async_trait]
impl UserQuestion for AnswersWithImage {
    async fn ask(&self, _: &str, _: &[String], _: bool, _: CancellationToken) -> AnswerOutcome {
        AnswerOutcome::Answered("plain".into())
    }
    async fn ask_with_attachments(&self, _: &str, _: &[String], _: bool, _: CancellationToken) -> coda_tool::QuestionReply {
        coda_tool::QuestionReply {
            outcome: AnswerOutcome::Answered("alpha".into()),
            images: vec![coda_tool::ImageAttachment { media_type: "image/png".into(), base64: "AAAA".into() }],
        }
    }
}

#[tokio::test]
async fn an_answer_with_images_returns_them_and_says_how_many() {
    let ctx = ToolContext::new("/").with_user_question(Arc::new(AnswersWithImage));
    let result = AskUserQuestionTool
        .execute(&serde_json::json!({"question": "Pick", "options": ["alpha"]}), &ctx, CancellationToken::new())
        .await;
    assert!(!result.is_error);
    assert!(result.content.contains("User answered: alpha"), "{}", result.content);
    assert!(result.content.contains("1 image"), "{}", result.content);
    assert_eq!(result.images.len(), 1);
}
```

Also assert that `AlwaysChooseFirst` (no override) produces `result.images.is_empty()` and content without
"image".

- [ ] **Step 2: Implement** in `ask_user_question.rs`: call `uq.ask_with_attachments(..)` and match on
`reply.outcome`. For `Answered(answer)`:

```rust
let images = reply.images;
if images.is_empty() {
    ToolResult::ok(format!("User answered: {answer}"))
} else {
    let n = images.len();
    let noun = if n == 1 { "image" } else { "images" };
    ToolResult::ok(format!(
        "User answered: {answer}\n\n(The user attached {n} {noun} to this answer; they follow this tool result.)"
    ))
    .with_images(images)
}
```

Keep the `Parked`/`NoAnswer` arms byte-identical. Update the tool `description()`: mention that the user can pick
options, type their own answer and attach screenshots.

- [ ] **Step 3: Failing test, `agent/tools.rs`.** Find the existing tests that run a batch through
`run_tools`/`run_tools_body` with a fake tool (search for a test that builds a `BatchContext` and asserts on
`result_blocks`). Add a fake tool that returns `ToolResult::ok("done").with_images(vec![img])` and a batch with
two tool uses, `[image_tool, plain_tool]`. Assert:
  * `result_blocks` is `[ToolResult(id1), ToolResult(id2), Image]`, with the image strictly after every `ToolResult`;
  * the `Image` has the same media type and base64.
- [ ] **Step 4: Implement** in `run_tools_body`: `let mut trailing_images: Vec<Content> = Vec::new();`. After the
main-path `results.push(make_result_block(id, &tool_result.content, ..))`, add
`trailing_images.extend(tool_result.images.iter().map(|i| Content::Image { media_type: i.media_type.clone(), base64: i.base64.clone() }));`.
Before `Ok(ToolBatchResult { .. })`, add `results.extend(trailing_images);`, with a comment: providers require every
`tool_result` block before any other content in the message.
- [ ] **Step 5: Failing test, `stop.rs`** (follow the existing `FixedPrompt` tests near lines 440-460): an answer of
`format!("{GOAL_STOP_OPTION}\n\nAdditional details from the user:\nno more budget")` must be treated as stop. Use
the same assertion style as the existing mixed-case stop test.
- [ ] **Step 6: Implement:** in the guard at `stop.rs:104-105`, compare
`a.lines().next().unwrap_or("").trim()` with `eq_ignore_ascii_case(GOAL_STOP_OPTION)`. Keep the empty check on the
whole answer.
- [ ] **Step 7:** `cargo test -p coda-agent`. All pass.
- [ ] **Step 8:** Commit: `feat(agent): answer images reach the model after tool results`.

### Task 3: Wire protocol and engine (coda-proto, coda-serve, docs)

**Files:** `crates/coda-proto/src/messages.rs`, `crates/coda-serve/src/state/requests.rs`,
`crates/coda-serve/src/prompts.rs`, `crates/coda-serve/src/host.rs`, `crates/coda-serve/src/transport.rs`
(test patterns), `docs/protocol/schemas/`, `docs/serve-protocol.md`, `docs/protocol/catalog.md`

- [ ] **Step 1: Proto.** `QuestionRequest`: add
  `/// Whether the reply may carry images (additive; absent means no). #[serde(default)] pub accepts_images: bool,`.
  `QuestionResponse`: add `#[serde(default, skip_serializing_if = "Vec::is_empty")] pub images: Vec<WireImage>`
  (also derive `Deserialize` if useful for tests). Add `PartialEq, Eq` to `WireImage` derives. Fix every
  `QuestionResponse { answer }` construction (`cargo build --workspace --tests`).
- [ ] **Step 2: Regenerate schemas.** Run `cargo test -p coda-proto --features schema --lib`. It fails on drift.
  Find the emitter (`grep -rn emit_schemas crates/coda-proto`; it is a bin or example with the `schema` feature) and
  run it, e.g. `cargo run -p coda-proto --features schema --bin emit_schemas`. Re-run the test until it passes.
  Inspect `git diff docs/protocol/schemas`: only the question schemas (and the shared `WireImage` definition) may change.
- [ ] **Step 3: `RequestOutcome`.** Change the variant to `Question(AnswerOutcome, Vec<ImageAttachment>)`.
  `kind()` → `Question(..)`. `label()` patterns → `Question(AnswerOutcome::Answered(_), _)` etc. `fail_closed` →
  `Question(AnswerOutcome::NoAnswer(reason), Vec::new())`. Fix every pattern and constructor in coda-serve
  (prompts.rs, host.rs, transport.rs, state/requests.rs tests). The compiler lists them. Images are never part of
  `label()` or of any event (the label stays a secret-free token).
- [ ] **Step 4: Failing tests, `prompts.rs`** (in the existing test module):

```rust
const PNG_B64: &str = "iVBORw0KGgo="; // valid base64; content is not decoded as PNG

#[test]
fn a_raw_answer_carries_its_images() {
    let reply = Ok(json!({ "answer": "Keep", "images": [{ "mediaType": "image/png", "base64": PNG_B64 }] }));
    match parse_wire_response(PendingRequestKind::Question, &reply) {
        RequestOutcome::Question(AnswerOutcome::Answered(a), images) => {
            assert_eq!(a, "Keep");
            assert_eq!(images, vec![ImageAttachment { media_type: "image/png".into(), base64: PNG_B64.into() }]);
        }
        other => panic!("{other:?}"),
    }
}

#[test]
fn a_raw_answer_with_an_unusable_image_is_malformed_not_answered() {
    for images in [
        json!([{ "mediaType": "image/bmp", "base64": PNG_B64 }]),
        json!([{ "mediaType": "image/png", "base64": "not base64!!" }]),
        json!([{ "mediaType": "image/png" }]),
        json!((0..9).map(|_| json!({ "mediaType": "image/png", "base64": PNG_B64 })).collect::<Vec<_>>()),
    ] {
        let reply = Ok(json!({ "answer": "Keep", "images": images }));
        assert_eq!(
            parse_wire_response(PendingRequestKind::Question, &reply),
            RequestOutcome::Question(AnswerOutcome::NoAnswer(NoAnswerReason::Malformed), Vec::new())
        );
    }
}

#[test]
fn an_out_of_band_answer_with_a_bad_image_is_rejected_for_correction() {
    let err = outcome_from_rpc(PendingRequestKind::Question,
        &json!({ "answer": "Keep", "images": [{ "mediaType": "image/bmp", "base64": PNG_B64 }] })).unwrap_err();
    assert!(matches!(err, ResolveError::MalformedOutcome { .. }));
    let ok = outcome_from_rpc(PendingRequestKind::Question,
        &json!({ "answer": "Keep", "images": [{ "mediaType": "image/jpeg", "base64": PNG_B64 }] })).unwrap();
    assert!(matches!(ok, RequestOutcome::Question(AnswerOutcome::Answered(_), ref i) if i.len() == 1));
}
```

Also add a test that `WireUserQuestion` sends `"acceptsImages": false` from plain `ask` and `true` from
`ask_impl(.., accepts_images = true, ..)`, in both the frame params and the pending display. Follow the existing
test that inspects the outgoing `request/question` frame and `registry().snapshot()` / list (search prompts.rs tests
for `"allowFreeText"` or `request/question`).

- [ ] **Step 5: Implement parsing** in `prompts.rs`:

```rust
/// Most images one answer may carry.
pub(crate) const MAX_ANSWER_IMAGES: usize = 8;
/// Largest base64 payload per image (5 MiB raw).
const MAX_ANSWER_IMAGE_BASE64: usize = 7_000_000;

/// Reads the optional `images` of a question reply. `Err` names the first problem.
fn answer_images(value: &Value) -> Result<Vec<ImageAttachment>, String> {
    let Some(raw) = value.get("images") else { return Ok(Vec::new()) };
    let Some(items) = raw.as_array() else { return Err("`images` must be an array".into()) };
    if items.len() > MAX_ANSWER_IMAGES {
        return Err(format!("at most {MAX_ANSWER_IMAGES} images may accompany an answer"));
    }
    items.iter().map(|item| {
        let media_type = item.get("mediaType").and_then(Value::as_str).ok_or("an image needs `mediaType`")?;
        if !matches!(media_type, "image/png" | "image/jpeg" | "image/gif" | "image/webp") {
            return Err(format!("unsupported image media type: {media_type}"));
        }
        let base64 = item.get("base64").and_then(Value::as_str).ok_or("an image needs `base64`")?;
        if base64.len() > MAX_ANSWER_IMAGE_BASE64 { return Err("an image exceeds 5 MiB".into()); }
        validate_base64(base64)?;
        Ok(ImageAttachment { media_type: media_type.into(), base64: base64.into() })
    }).collect()
}
```

Move `validate_base64` from `host.rs:7332` to a shared place both files can use (for example
`pub(crate) fn validate_base64` in a small new `crates/coda-serve/src/images.rs` module, re-used by `host.rs`).
Keep its tests with it. `parse_wire_response`: a usable answer plus `Ok(images)` → `Question(Answered, images)`;
`Err(_)` images → `Question(NoAnswer(Malformed), vec![])`. `outcome_from_rpc`: `Err(detail)` →
`ResolveError::MalformedOutcome { detail }`.

- [ ] **Step 6: Implement `acceptsImages` and the wrapper.** `WireUserQuestion::ask_impl(&self, question, options,
  multi_select, accepts_images: bool, cancel) -> QuestionReply` puts `"acceptsImages": accepts_images` in both
  `params` and `display` and returns `QuestionReply { outcome, images }`. When `accepts_images` is false and images
  arrive anyway, drop them and `tracing::warn!` (or the crate's logging macro) without the payload. Plain
  `UserQuestion::ask` and `UserQuestionPrompt::ask` call `ask_impl(.., false, ..)` and return `.outcome`.
  Add:

```rust
/// The tool-path question seam for one turn: offers image attachments only when the
/// active provider/model actually delivers images to the model.
pub struct ImageAwareQuestion {
    pub inner: Arc<WireUserQuestion>,
    pub client: Arc<dyn coda_llm::LlmClient>,
    pub model: String,
}

#[async_trait]
impl coda_tool::UserQuestion for ImageAwareQuestion {
    async fn ask(&self, q: &str, o: &[String], m: bool, c: CancellationToken) -> AnswerOutcome {
        self.inner.ask_impl(q, o, m, false, c).await.outcome
    }
    async fn ask_with_attachments(&self, q: &str, o: &[String], m: bool, c: CancellationToken) -> QuestionReply {
        // Asked at question time: the provider's model metadata is loaded by then.
        let accepts = self.client.accepts_image_input(&self.model);
        self.inner.ask_impl(q, o, m, accepts, c).await
    }
}
```

  In `host.rs` (the `uq_tool` match around line 6078), replace the `None =>` arm with
  `Arc::new(ImageAwareQuestion { inner: Arc::clone(&self.user_question), client: Arc::clone(&client), model: self.current_model() })`.
  Add a test for `ImageAwareQuestion` using a fake `LlmClient` whose `accepts_image_input` returns false: the issued
  params carry `acceptsImages: false`.
- [ ] **Step 7: Docs.** `docs/serve-protocol.md` line ~1196 table:
  `{ question, options[], multiSelect, allowFreeText, acceptsImages?, requestId? }` → `{ answer: string, images?: WireImage[] }`.
  Add a short paragraph under it covering the semantics: additive; at most 8 images; allowed types; a 5 MiB limit;
  an invalid raw reply is `noAnswer.malformed`; `resolveRequest` rejects it for correction; the engine drops images
  for `acceptsImages:false`; images follow the tool result in history. Update `docs/protocol/catalog.md` near
  line 160 the same way, and mention `session/resolveRequest`'s question outcome `{ answer, images? }` wherever the
  outcome shape is documented (grep both files for `"answer"` / `outcome`).
- [ ] **Step 8:** `cargo test -p coda-serve -p coda-proto` and `cargo test -p coda-proto --features schema --lib`
  and `cargo test -p coda-engine` (conformance tests use these shapes). All pass.
- [ ] **Step 9:** Commit: `feat(serve): question replies carry images; acceptsImages capability`.

### Task 4: TextArea paste and soft wrap (coda-tui widgets)

**Files:** `crates/coda-tui/src/widgets.rs`

- [ ] **Step 1: Failing tests** (in the widgets test module):

```rust
#[test]
fn text_area_inserts_pasted_text_with_normalised_newlines() {
    let mut area = TextArea::new("Answer");
    area.insert_text("one\r\ntwo\rthree\u{1b}[2J\tx");
    assert_eq!(area.value(), "one\ntwo\nthree[2J    x");
}

#[test]
fn soft_wrap_shows_every_character_of_a_long_line() {
    let area = TextArea::new("Answer").with_soft_wrap().with_value("abcdefghijklmnopqrstuvwxyz".repeat(3));
    let shown: String = plain(&area.render(20, false, &theme())).join("");
    for chunk in ["abcdefghijklmnopqrstuvwxyz"] { assert!(shown.contains(&chunk[..10])); }
    assert!(shown.matches('z').count() == 3, "{shown}");
}

#[test]
fn soft_wrap_places_the_caret_on_the_wrapped_row() {
    let area = TextArea::new("A").with_soft_wrap().with_value("x".repeat(25));
    // content width at 20 = 20 - gutter; the caret sits after the last char on the 2nd visual row
    let (col, row) = area.cursor(20).unwrap();
    assert_eq!(row, 2, "label row + second visual row");
    assert!(col as usize <= 20);
}

#[test]
fn soft_wrap_measures_wide_glyphs_in_cells() {
    let area = TextArea::new("A").with_soft_wrap().with_value("界".repeat(20));
    for line in area.render(20, false, &theme()) {
        let w: usize = line.spans.iter().map(|s| coda_render::text::width(&s.content)).sum();
        assert!(w <= 20, "a row overflowed: {w}");
    }
}
```

Adjust `plain`/`theme` to the module's existing helpers. Compute the expected rows from `content_width(20)`.

- [ ] **Step 2:** Run `cargo test -p coda-tui --lib widgets`. Expect failures.
- [ ] **Step 3: Implement.**
  * `pub fn insert_text(&mut self, text: &str)`: replace `\r\n` and `\r` with `\n`, tab with four spaces, drop
    other `char::is_control` characters, and drop ESC. A bare ESC must disappear, so `"\u{1b}[2J"` becomes `"[2J"`.
    Insert at the caret, splitting lines on `\n` (reuse the Enter logic).
  * `soft_wrap: bool` field, `pub fn with_soft_wrap(mut self) -> Self`, `pub fn set_visible_rows(&mut self, rows: usize)`,
    `pub fn line_count(&self) -> usize`, `pub fn visual_rows(&self, width: u16) -> usize`.
  * Visual layout helper `fn visual_lines(&self, inner: usize) -> Vec<(usize /*logical row*/, usize /*start char*/, usize /*end char*/)>`:
    break each logical line where the accumulated `coda_render::text::width(&c.to_string())` would exceed `inner`.
    Always break at least one char, and treat an empty line as one visual row.
  * The caret's visual row is the segment containing `column` (a caret at the end of a full segment sits on that
    segment, not on the next empty one, unless the line ends exactly at the boundary; then use the last segment).
  * Scrolling in soft-wrap mode keeps the caret's visual row visible within `visible_rows`. Render uses visual
    segments; `cursor()` returns `(gutter + width of segment text before caret, 1 + visual_row - scroll)`.
  * Without soft wrap, behaviour is unchanged (the existing tests must still pass).
- [ ] **Step 4:** `cargo test -p coda-tui --lib widgets`. All pass.
- [ ] **Step 5:** Commit: `feat(tui): TextArea paste insertion and soft wrap`.

### Task 5: QuestionView surface (coda-tui)

**Files:** create `crates/coda-tui/src/surface/question.rs`; modify `surface/mod.rs` (add `pub mod question;`,
`Surface::handle_paste`, `SurfaceAction::PasteIntoPrompt`), `surface/stack.rs` (`handle_paste`),
`surface/prompt.rs` (delegate), `state.rs` (`PendingPrompt::Question.accepts_images: bool`), `render/glyphs.rs`
(only if new glyphs are needed).

Changing `PendingPrompt::Question` adds a field. Update every constructor in the crate (tests, `app/engine.rs`,
`api/requests.rs`, `app/serve.rs`, `tests/*.rs`), with `accepts_images: false` where nothing better is known.
`app/engine.rs` and `api/requests.rs` get real parsing in Task 6. Fill those two with `false` for now.

**Public API of `QuestionView`** (used by `PromptSurface` and by Task 6):

```rust
pub struct QuestionView { /* see state below */ }
pub enum QuestionOutcome { Handled, Submit { answer: String }, Cancel, RequestPaste }
impl QuestionView {
    pub fn new(question: String, options: Vec<String>, multi_select: bool, allow_free_text: bool, accepts_images: bool) -> Self;
    pub fn handle_key(&mut self, key: KeyEvent) -> QuestionOutcome;
    pub fn handle_mouse(&mut self, event: MouseEvent, content: Rect) -> QuestionOutcome;
    pub fn paste_text(&mut self, text: &str);                  // inserts into the field, focuses it
    pub fn attach_image(&mut self, media_type: &str, base64: String) -> Result<(), String>; // Err = message shown
    pub fn show_message(&mut self, message: impl Into<String>); // e.g. clipboard error
    pub fn images(&self) -> Vec<coda_proto::messages::WireImage>;
    pub fn attachment_count(&self) -> usize;
    pub fn hints(&self) -> String;
    pub fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>>;
    pub fn cursor(&self, area: Rect, theme: &Theme) -> Option<(u16, u16)>;
}
pub const MAX_QUESTION_IMAGES: usize = 5;
```

**State:** `question`, `options: Vec<String>`, `other: Option<OtherRow>` (`Synthetic` or `Supplied(index)`),
`multi_select`, `accepts_images`, `highlighted: usize` (index into the visible rows; the synthetic Other row is the
last), `selected: Option<usize>` (single), `ticked: Vec<bool>` (multi, one per visible row), `field: TextArea`
(label "Your answer / explanation (optional when an option is chosen)", soft wrap), `focus: Zone { Options, Field, Attachments }`,
`attachments: Vec<(String /*label image-N.ext*/, WireImage)>` plus `attachment_cursor`, `next_image_number`,
`message: Option<(String, bool /*warning*/)>`, `manual_scroll: Option<usize>`, `last_scroll: Cell<usize>`.

**Other detection** (spec §3): `fn looks_like_other(label: &str) -> bool`. Lowercase and trim. True if it equals
`other` or starts with `other` followed by a char that is not ASCII-alphabetic, or contains any of `type your own`,
`your own answer`, `something else`, `custom answer`. Only the first matching supplied option becomes the Other
row. If none matches and `allow_free_text`, append the synthetic row labelled `Other — type your own answer below`
(use an em dash; if the conventions test forbids it inline, add a glyph constant).

**Layout** (`fn layout(&self, width: usize, theme) -> Layout { lines, focus_rows: (start,end), option_rows: Vec<(start,end)>, field_rows: (start,end) }`):
1. The question: each `\n` paragraph sanitised and `text::wrap`ped to `width`, style `Role::PromptText`.
2. A blank line, then `Choose one:` or `Choose any that apply:` (style `Role::Notification`).
3. Each option row: prefix `marker + box + "N. "`, where marker is `glyphs::OPTION_MARKER`/`OPTION_BLANK` (focused
   and highlighted vs not) and box is single `(•)`/`( )` and multi `RADIO_ON`/`RADIO_OFF`. Use existing glyphs and
   add `CHECK_ON/CHECK_OFF` glyph constants if needed for multi tick boxes. Digits only for rows 1-9; later rows
   are indented to the same width. Wrap the label with `text::wrap_with_hanging_indent(&prefix_plus_label, width, prefix_width)`.
   The highlighted row while `focus == Options` uses the `CompletionSelectedText` style on a
   `CompletionSelectedBackground` background, as today. Other rows use `CompletionNormal`.
4. If there is free text: a blank line, then `field.render(width, focus == Field, theme)`. Field visible rows =
   `clamp(field.visual_rows(width), 3, 8)`, set before rendering via a `RefCell`, or computed in a `&mut` pre-pass
   on each key/paste and on render via `set_visible_rows` on a clone. Choose the clone approach to keep `render(&self)`.
5. If there are attachments: `Attachments: ` followed by `[📷 image-1.png]` chips (`glyphs::CAMERA`). The chosen chip
   is highlighted when `focus == Attachments`. Wrap the chips across rows when needed.
6. If there is a message: a sanitised, wrapped row in `Role::Warning` (or the closest existing warning role).

**Scrolling** in `render`: `height = area.height`. If `manual_scroll` is `Some(s)`, use `s`. Otherwise start from
`last_scroll` and adjust minimally so `focus_rows` is visible (if the focus range is taller than the height, show
its last row for Field, its first row otherwise). Clamp to `lines.len().saturating_sub(height)`, store the result
in `last_scroll`, then return the visible slice. Do not draw an indicator row. Instead, the hints show
`PgUp/PgDn scroll` only when the content overflows (store a `Cell<bool> overflowing`, set during render).

**Keys** (`handle_key`), in this order:
1. `Esc` → `Cancel` (any zone).
2. Paste chords: `Ctrl+V`, `Alt+V` (`Char('v')` with CONTROL or ALT), `Shift+Insert` → `RequestPaste`.
3. `PgUp`/`PgDn` → `manual_scroll = Some(last_scroll ∓ max(1, page))`, with page = the last rendered height (store
   `Cell<u16>`). Return `Handled`.
4. `Tab` → next zone (Options → Field (if free text) → Attachments (if any) → Options). `BackTab` → previous.
   Clear `manual_scroll`.
5. Any other key with CONTROL/ALT/SUPER: in the Field zone `Enter`+mods and `Ctrl+J` insert a newline
   (`field.insert_text("\n")`). Otherwise → `Handled` (a chord never answers).
6. By zone:
   * Options: `Up/Down/Home/End` move the highlight. `Space` → `choose(highlighted)`. A digit `d` (1..=9, within
     range) → set highlighted, then `choose`, and in single-select, if the row is not Other → `try_submit()`.
     `Enter` → single: `choose(highlighted)`; if the row is not Other → `try_submit()`. Multi: `try_submit()`.
   * Field: `Enter` → `try_submit()`. Anything else → `field.handle_key(key)`. Clear `message` on edits.
   * Attachments: `Left/Right` move the chip cursor. `Delete/Backspace` remove that attachment; when none remain,
     focus returns to Field (or Options).
   * `choose(row)`: single → `selected = Some(row)`; multi → toggle `ticked[row]`. If the row is the Other row and is
     now selected/ticked → `focus = Field`, `message = None`.
   Every focus/highlight change clears `manual_scroll`.

**`try_submit`** composes per spec §3 "Answer composition". Labels are the chosen supplied options in original
order, excluding the Other row. `other_chosen` comes from the Other row's selected/ticked state. `text = field.value().trim()`.
Rules, in order:
* `other_chosen && text.is_empty()` → message "Type your answer in the field below (or choose another option)."; focus Field; `Handled`.
* labels empty and text empty: if `!attachments.is_empty()` → "Add a short description or choose an option to go with the image(s)."; else "Choose an option or type an answer."; `Handled`.
* labels empty → `Submit { answer: text }`.
* text empty → `Submit { answer: labels.join(", ") }`.
* else → `Submit { answer: format!("{}\n\nAdditional details from the user:\n{}", labels.join(", "), text) }`.

**Mouse:** `ScrollUp/ScrollDown` → `manual_scroll` ∓3. `Down(Left)` at a row: map `event.row - content.y + last_scroll`
to an option (`highlighted = i`, focus Options, then `choose(i)`; never submit on a click) or to the field (focus
Field). Anything outside → `Handled`.

**`attach_image`:** if `!accepts_images` → `Err("Images can't be sent with this answer: the current model or engine does not accept image input. Describe it in text instead.")`,
and also set it as the message. If `attachments.len() >= MAX_QUESTION_IMAGES` → `Err("At most 5 images can accompany an answer.")`.
Otherwise push the attachment labelled `image-{n}.{ext}` (png/jpg/gif/webp), clear the message and return `Ok`.

**`hints()`**, focused zone first, joined by `"  "`:
* Options: `↑↓ move` (use `glyphs::ARROWS_VERTICAL`), `Space select` (multi: `Space tick`), `1-9 pick`, `Enter submit`,
  `Tab answer field` (if free text).
* Field: `Enter submit`, `Shift+Enter newline`, `Tab options`.
* Attachments: `←→ choose`, `Del remove`.
* Then always: `Ctrl+V/Alt+V paste` (+` image` if `accepts_images`), `PgUp/PgDn scroll` (only if overflowing),
  `Esc cancel`.

**`PromptSurface` changes:** add a field `question: Option<QuestionView>`, built in `new` for
`PendingPrompt::Question`. Drop the old `highlighted/chosen/free_text/editing` fields and the question branches,
and route to the view. `QuestionOutcome::Submit{answer}` → `Emit(AnswerPrompt{allowed:true, answer:Some(answer)})`;
`Cancel` → deny; `RequestPaste` → `Emit(SurfaceAction::PasteIntoPrompt)`; `Handled` → `Handled`. The chord guard at
the top of `handle_key` stays for permission and plan prompts only; the question view applies its own (step 5
above). `placement()`: questions → `FitContent { preferred_width: 110 }`; others unchanged. `hints()`, `render()`,
`cursor()`, `handle_mouse()` and the new `handle_paste()` delegate for questions. Add pub accessors
`question_view_mut(&mut self) -> Option<&mut QuestionView>` and `answer_images(&self) -> Vec<WireImage>`.

**`Surface` trait:** `fn handle_paste(&mut self, _text: &str) -> bool { false }` (returns whether the paste was
consumed). `SurfaceStack::handle_paste(&mut self, text) -> bool` routes it to the top surface. Add
`SurfaceAction::PasteIntoPrompt` (doc: "Read the clipboard for the open question: an image is attached, text is
pasted into its answer field.").

- [ ] **Step 1: Failing tests** in `question.rs` (`#[cfg(test)] mod tests`). Helper `fn key(code)`,
  `fn text(lines) -> String`, `fn q(options, multi) -> QuestionView` (`allow_free_text = true`, `accepts_images = true`):
  1. `a_long_question_and_long_options_render_in_full_without_truncation`: a 300-character question and 3 options
     of 150 chars, rendered at `Rect(0,0,60,200)`. Every word of each appears (join the rendered text with spaces,
     then check each word). No row is wider than 60 cells.
  2. `wide_glyph_options_never_overflow_the_row`: an option of `"界".repeat(80)` at width 30. Every line is ≤ 30 cells.
  3. `every_row_is_reachable_by_scrolling_on_a_small_terminal`: 40 long options at `Rect(0,0,30,10)`. Press
     `PgDn` repeatedly and collect the rendered rows; the last option's text eventually appears. Also `End` in Options
     makes the last option visible.
  4. `single_choice_enter_submits_the_highlighted_option`: Down, Enter → `Submit{"second"}`.
  5. `single_choice_space_selects_without_submitting_and_text_adds_details`: Space on row 0 → `Handled`; Tab; type
     "because"; Enter → `Submit{"first\n\nAdditional details from the user:\nbecause"}`.
  6. `multi_select_submits_ticks_and_explanation_together`: tick 1 and 3 with digits, Tab, type "both", Enter →
     `"one, three\n\nAdditional details from the user:\nboth"`.
  7. `multi_with_nothing_chosen_refuses_with_a_hint`: Enter → `Handled`; rendered text contains "Choose an option or type an answer".
  8. `choosing_other_focuses_the_field_instead_of_submitting`: single; press the digit of the synthetic Other row
     → `Handled`; `hints()` starts with the Field hints; Enter with an empty field → `Handled`, and the message
     contains "Type your answer"; type "mine", Enter → `Submit{"mine"}` (never the literal label).
  9. `a_model_supplied_other_option_is_reused_not_duplicated`: options `["A","Other (please specify)"]`. Exactly
     one row contains "Other"; choosing it focuses the field.
  10. `looks_like_other_cases`: true for "Other", "other: explain", "Type your own answer", "Something else";
      false for "Otherwise keep it", "Mother".
  11. `pasted_text_goes_into_the_field_and_focuses_it`: `paste_text("line1\r\nline2")`; focus Field; Enter →
      `Submit{"line1\nline2"}`.
  12. `images_attach_show_and_can_be_removed`: `attach_image("image/png", "AAAA".into())` twice → rendered text
      contains "image-1.png" and "image-2.png"; Tab to Attachments (cycle until the hints start with "←→"); Delete →
      one left; `images()` length 1.
  13. `images_are_refused_with_a_clear_message_when_unsupported`: `accepts_images = false` → `attach_image` is
      `Err`; rendered text contains "does not accept image input"; `images()` is empty.
  14. `images_alone_are_not_an_answer`: attach an image, Enter with nothing chosen → `Handled`, message mentions "image".
  15. `state_survives_a_resize`: tick, type, attach; render at 100×40, then 30×10, then 100×40. `images()`, the
      ticked state (via submit) and the field text are unchanged, and the rendered text at 100×40 equals the first.
  16. `escape_cancels_from_every_zone`: Options/Field/Attachments → `Cancel`.
  17. `no_chord_answers`: CONTROL/ALT/SUPER with `Enter`, `Char('1')`, `Char('y')` in Options → `Handled`.
  18. `paste_chords_request_a_clipboard_read`: Ctrl+V, Alt+V, Shift+Insert → `RequestPaste`.
  19. `field_newline_chords_insert_newlines`: Tab, type "a", Shift+Enter, type "b", Enter → `Submit{"a\nb"}`.
  20. `mouse_wheel_scrolls_and_click_focuses_an_option`.
  Update the old question tests in `prompt.rs` to the new semantics (Space on single-select now selects;
  `a_typed_answer_wins_over_the_selection` → typed text with no option chosen gives `Submit{text}`;
  `tab_only_offers_typing_where_the_question_allows_it` still holds). Keep the permission/plan tests unchanged.
- [ ] **Step 2:** `cargo test -p coda-tui --lib surface` → fails.
- [ ] **Step 3:** Implement as specified above.
- [ ] **Step 4:** `cargo test -p coda-tui` (lib + integration tests such as `surface_integration.rs` and `render.rs`) → pass.
  `every_surface_stays_inside_its_area_at_any_terminal_size` must still pass for questions.
- [ ] **Step 5:** Commit: `feat(tui): large wrapped, scrollable question modal with Other field and attachments`.

### Task 6: App wiring — capability, paste routing, answer delivery (coda-tui)

**Files:** `crates/coda-tui/src/app/engine.rs`, `api/requests.rs`, `app/clipboard.rs`, `app/mod.rs`,
`app/serve.rs`, `state.rs`

- [ ] **Step 1: Failing tests.**
  * `app/engine.rs` `prompt_response_tests`:
    `prompt_response(&question(), true, Some("Keep"), &[img])` == `{"answer":"Keep","images":[{"mediaType":"image/png","base64":"AAAA"}]}`;
    with no images the shape stays exactly `{"answer":"Keep"}`;
    `rpc_outcome(&question(), true, Some("Keep"), &[img])` includes `images`; declines ignore images.
  * `api/requests.rs`: `prompt_from_dto` reads `acceptsImages: true` → `accepts_images: true`; absent → `false`.
  * Server-request parsing: a `request/question` params with `"acceptsImages": true` produces `accepts_images: true`.
    Find the existing test that drives `on_server_request` (search `app/serve.rs` tests for `server_method::QUESTION`
    or `PendingPrompt::Question {` around line 6332) and extend it.
  * App-level (`app/serve.rs` tests, using the existing harness that answers a question over the raw responder;
    search for a test that asserts the `{"answer": ...}` response frame): open a question with
    `acceptsImages: true`, call the view's `attach_image`, answer, and assert the frame carries `images`. A second
    test: bracketed paste (`TerminalEvent::Paste("hello")`) while a question is open lands in the question field,
    not in `app.composer` (assert `app.composer` is still empty and that submitting yields `"hello"`).
- [ ] **Step 2: Implement.**
  * `engine.rs`: `prompt_response(prompt, allowed, answer, images: &[WireImage])` and
    `rpc_outcome(.., images)` add `"images"` only when non-empty. `on_server_request` maps
    `request.accepts_images`. `requests.rs::prompt_from_dto` reads `display["acceptsImages"]` (bool, default false).
  * `answer_prompt(&mut self, allowed, answer, images: Vec<WireImage>)` and `deliver_answer(.., images: &[WireImage])`
    pass the images through. The `UiEvent::PromptAnswered` answer for the transcript gets
    `format!("{answer}\n[{} {n} image(s) attached]", glyphs::CAMERA)` appended when images exist (display only; the
    wire gets the plain answer). Update every caller (`close_out` paths and tests) with `Vec::new()`.
  * `app/mod.rs` `SurfaceAction::AnswerPrompt` arm: before `self.surfaces.pop()`, read
    `let images = self.surfaces.top().and_then(|s| s.as_any().downcast_ref::<PromptSurface>()).map(|p| p.answer_images()).unwrap_or_default();`
    For a decline (`allowed == false`), pass `Vec::new()`.
  * `TerminalEvent::Paste(text)`: if surfaces are open → `if !self.surfaces.handle_paste(&text) && text.is_empty() { /* nothing */ }`.
    A paste must never reach the composer while a surface is open. For an open question, an empty paste triggers
    `self.paste_into_question()` (some terminals send an empty bracketed paste for an image clipboard). With no
    surface open, the existing composer insert is unchanged.
  * `SurfaceAction::PasteIntoPrompt` → `self.paste_into_question()`.
  * `clipboard.rs`: factor the clipboard read out of `paste_image_from_clipboard` into
    `fn read_clipboard_for_paste() -> ClipboardPaste` with
    `enum ClipboardPaste { Image(Vec<u8> /*png*/), Text(String), Empty, Problem(String) }`. The image branch does
    the PNG encoding and the 5 MiB check; `Problem` carries the same user-facing wording as today. Rewrite
    `paste_image_from_clipboard` on top of it with identical behaviour. Add
    `pub(super) fn paste_into_question(&mut self)`: get the top `PromptSurface` mutably (add
    `SurfaceStack::top_mut()` if needed) and its `question_view_mut()`; `Image(png)` →
    `view.attach_image("image/png", base64_encode(&png))` (an `Err` is already shown in the view); `Text(t)` →
    `view.paste_text(&t)`; `Problem(m)` → `view.show_message(m)`; `Empty` → nothing. Set `self.dirty = true`.
- [ ] **Step 3:** `cargo test -p coda-tui` → all pass.
- [ ] **Step 4:** Commit: `feat(tui): route paste and image attachments into question answers`.

### Task 7: Docs, version, full verification

- [ ] **Step 1:** `README.md`: after the Mouse paragraph, add a **Questions** paragraph. Cover: the large wrapped
  scrollable modal; single vs multiple choice keys; the "Other" row focusing the answer field; options plus an
  explanation sent together; `Shift+Enter` newline; text paste; `Ctrl+V`/`Alt+V`/`Shift+Insert` image attach
  (max 5, 5 MiB each) with `Tab` to the attachments row and `Delete` to remove; the message when the model cannot
  take images (Copilot chat-completions models); `PgUp/PgDn` and the wheel scroll; `Esc` cancels without answering.
- [ ] **Step 2:** `version.json` → `"build": 180` (keep its formatting).
- [ ] **Step 3:** From `rust\`: `cargo build --workspace`; `cargo test --workspace` (no new warnings);
  `cargo test -p coda-proto --features schema --lib`; `cargo build --release -p coda` (or the engine binary used by
  the contract test), then `$env:CODA_ENGINE="<abs path>\rust\target\release\coda.exe"; cargo test -p coda-tui --test engine_contract`;
  `cargo clippy -p coda-tui -p coda-tool -p coda-agent -p coda-llm --all-targets` (no new findings in touched code).
- [ ] **Step 4:** Commit: `docs: question modal usage; bump version to 0.1.180`.
