//! The transcript: the ordered list of blocks that make up a conversation.
//!
//! A block is a logical unit (one user message, one assistant reply, one batch
//! of tool calls). Blocks are rendered to rows on demand, because the row count
//! depends on the viewport width and must be recomputed on resize.

use coda_proto::Correlation;
use coda_render::text;
use coda_render::theme::Role;
use coda_render::tool::{CallStatus, ToolActivity, ToolDisplayMode};
use coda_render::{markdown, Gutter, RenderLine, MARKER_CELLS};

/// Identifies a batch of tool calls within a turn.
///
/// Both components are optional because the engine may omit them; two batches
/// with no ids at all are treated as the same batch, which matches the
/// single-threaded case where that is the only sensible reading.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ActivityKey {
    pub root_turn_id: Option<String>,
    pub activity_id: Option<String>,
}

impl ActivityKey {
    pub fn from_correlation(correlation: &Correlation) -> Self {
        Self {
            root_turn_id: correlation.root_turn_id.clone(),
            activity_id: correlation.activity_id.clone(),
        }
    }
}

/// Whether two correlations name the same individual call.
///
/// Requires a `call_id`: without one there is nothing to distinguish two calls
/// to the same tool, so callers must fall back to a positional match.
pub fn same_call(a: &Correlation, b: &Correlation) -> bool {
    a.call_id.is_some() && a.call_id == b.call_id && a.source_id == b.source_id
}

/// Severity of a notice block.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NoticeLevel {
    Info,
    Warning,
    Error,
}

impl NoticeLevel {
    fn role(self) -> Role {
        match self {
            NoticeLevel::Info => Role::Notification,
            NoticeLevel::Warning => Role::Warning,
            NoticeLevel::Error => Role::Error,
        }
    }
}

/// What the user decided about a permission request.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PermissionDecision {
    Pending,
    Allowed,
    Denied,
}

/// One logical unit of the transcript.
#[derive(Debug, Clone)]
pub enum Block {
    /// A message the user sent, or has queued while the agent is busy.
    User {
        text: String,
        /// `HH:mm` shown right-aligned on the first row.
        timestamp: String,
        /// Queued but not yet delivered to the engine.
        pending: bool,
        /// Steering queue id, used to match a delivery notification exactly.
        queue_id: Option<String>,
    },
    /// Assistant prose, rendered as markdown.
    Assistant { text: String, complete: bool },
    /// Model reasoning.
    Thinking {
        text: String,
        elapsed_ms: i64,
        tokens: Option<i32>,
        complete: bool,
    },
    /// A batch of tool calls made in one agent step.
    ///
    /// A turn can produce several batches: once assistant text or another block
    /// follows, later calls open a new batch rather than reopening this one.
    /// The correlation ids identify which batch owns a given result.
    Tools {
        activity: ToolActivity,
        /// Identifies the batch, from the engine's correlation ids.
        key: ActivityKey,
        /// Correlation of each call, parallel to `activity.calls`.
        calls: Vec<Correlation>,
    },
    /// A status or error line.
    Notice { text: String, level: NoticeLevel },
    /// A permission request and its outcome.
    Permission {
        tool: String,
        preview: String,
        decision: PermissionDecision,
    },
    /// A question the agent asked and the answer given.
    Question {
        question: String,
        answer: Option<String>,
    },
    /// Output from a slash command.
    CommandOutput { text: String },
    /// A marker separating resumed sessions.
    SessionBoundary { id: String },
}

impl Block {
    /// Whether this block can still receive streamed content.
    pub fn is_open(&self) -> bool {
        match self {
            Block::Assistant { complete, .. } => !complete,
            Block::Thinking { complete, .. } => !complete,
            Block::Tools { activity, .. } => !activity.complete,
            _ => false,
        }
    }

    /// Renders the block to rows for a viewport of `width` cells.
    pub fn render(&self, width: usize, mode: ToolDisplayMode) -> Vec<RenderLine> {
        let width = width.max(1);
        match self {
            Block::User {
                text,
                timestamp,
                pending,
                ..
            } => render_user(text, timestamp, *pending, width),
            Block::Assistant { text, complete } => render_assistant(text, *complete, width),
            Block::Thinking {
                text,
                elapsed_ms,
                tokens,
                complete,
            } => render_thinking(text, *elapsed_ms, *tokens, *complete, width, mode),
            Block::Tools { activity, .. } => activity.render(mode, width),
            Block::Notice { text, level } => text::wrap(text, width)
                .into_iter()
                .map(|chunk| RenderLine::new(chunk, level.role()))
                .collect(),
            Block::Permission {
                tool,
                preview,
                decision,
            } => render_permission(tool, preview, *decision, width),
            Block::Question { question, answer } => {
                let text = match answer {
                    Some(answer) => format!("{question} → {answer}"),
                    None => question.clone(),
                };
                text::wrap(&text, width)
                    .into_iter()
                    .map(|chunk| RenderLine::new(chunk, Role::Question))
                    .collect()
            }
            Block::CommandOutput { text } => text
                .lines()
                .flat_map(|line| text::wrap_preformatted(&text::sanitize(line), width))
                .map(|chunk| RenderLine::new(chunk, Role::Code))
                .collect(),
            Block::SessionBoundary { id } => {
                let label = format!("\u{2500}\u{2500} session {id} \u{2500}\u{2500}");
                text::wrap(&label, width)
                    .into_iter()
                    .map(|chunk| RenderLine::new(chunk, Role::Notification))
                    .collect()
            }
        }
    }
}

fn render_user(text: &str, timestamp: &str, pending: bool, width: usize) -> Vec<RenderLine> {
    let role = if pending { Role::PendingUser } else { Role::User };
    let content = width.saturating_sub(MARKER_CELLS).max(1);

    // The timestamp is reserved out of the first row only, with a one-cell gap.
    let stamp_width = if timestamp.is_empty() {
        0
    } else {
        text::width(timestamp) + 1
    };
    let first_budget = content.saturating_sub(stamp_width).max(1);

    let body = if pending {
        format!("[pending] {text}")
    } else {
        text.to_string()
    };

    // Wrap the first line narrower, then the remainder at full width.
    let mut rows: Vec<String> = Vec::new();
    let wrapped = text::wrap(&body, first_budget);
    match wrapped.split_first() {
        Some((head, _)) if wrapped.len() > 1 => {
            rows.push(head.clone());
            let consumed = head.chars().count();
            let rest: String = body.chars().skip(consumed).collect();
            rows.extend(text::wrap(rest.trim_start(), content));
        }
        _ => rows.extend(wrapped),
    }

    rows.into_iter()
        .enumerate()
        .map(|(i, chunk)| {
            let mut line = RenderLine::new(chunk, role)
                .with_gutter(if i == 0 {
                    Gutter::UserMarker
                } else {
                    Gutter::Continuation
                })
                .with_fill(Role::UserBackground);
            if i == 0 && !timestamp.is_empty() {
                line = line.with_right_text(timestamp);
            }
            line
        })
        .collect()
}

fn render_assistant(text: &str, complete: bool, width: usize) -> Vec<RenderLine> {
    let content = width.saturating_sub(MARKER_CELLS).max(1);
    let rows = markdown::render(text, content);

    rows.into_iter()
        .enumerate()
        .map(|(i, line)| {
            let gutter = if i == 0 {
                if complete {
                    Gutter::AgentComplete
                } else {
                    Gutter::AgentActive
                }
            } else {
                Gutter::Continuation
            };
            line.with_gutter(gutter)
        })
        .collect()
}

fn render_thinking(
    body: &str,
    elapsed_ms: i64,
    tokens: Option<i32>,
    complete: bool,
    width: usize,
    mode: ToolDisplayMode,
) -> Vec<RenderLine> {
    let content = width.saturating_sub(MARKER_CELLS).max(1);
    // Round half away from zero: `{:.0}` would round 4.5 to 4, which reads as
    // a stopwatch running backwards when the elapsed time ticks past .5.
    let seconds = (elapsed_ms as f64 / 1000.0).round() as i64;

    let status = if complete {
        format!("\u{1F4AD} Thought for {seconds}s")
    } else {
        match tokens {
            Some(tokens) => format!("\u{1F4AD} Thinking… {seconds}s · {tokens} tok"),
            None => format!("\u{1F4AD} Thinking… {seconds}s"),
        }
    };

    let mut out: Vec<RenderLine> = text::wrap(&status, content)
        .into_iter()
        .enumerate()
        .map(|(i, chunk)| {
            RenderLine::new(chunk, Role::Notification).with_gutter(if i == 0 {
                if complete {
                    Gutter::AgentComplete
                } else {
                    Gutter::AgentActive
                }
            } else {
                Gutter::Continuation
            })
        })
        .collect();

    match mode {
        // The status line alone; the reasoning text stays hidden.
        ToolDisplayMode::Summary | ToolDisplayMode::Hidden => {}
        ToolDisplayMode::Compact => {
            // The tail is the most relevant part of a long reasoning trace.
            let tail: Vec<&str> = body
                .lines()
                .filter(|l| !l.trim().is_empty())
                .rev()
                .take(5)
                .collect();
            for line in tail.into_iter().rev() {
                for chunk in text::wrap(line, content) {
                    out.push(
                        RenderLine::new(chunk, Role::Notification)
                            .with_gutter(Gutter::Continuation),
                    );
                }
            }
        }
        ToolDisplayMode::Full => {
            for line in markdown::render(body, content) {
                out.push(line.with_gutter(Gutter::Continuation));
            }
        }
    }

    out
}

fn render_permission(
    tool: &str,
    preview: &str,
    decision: PermissionDecision,
    width: usize,
) -> Vec<RenderLine> {
    let (suffix, role) = match decision {
        PermissionDecision::Allowed => (" → allowed", Role::PermissionApproved),
        PermissionDecision::Denied => (" → denied", Role::Permission),
        PermissionDecision::Pending => ("", Role::Question),
    };
    let text = format!("{tool} {preview}{suffix}");
    text::wrap(&text, width)
        .into_iter()
        .map(|chunk| RenderLine::new(chunk, role))
        .collect()
}

/// The ordered blocks of a conversation, plus a cache of their row counts.
#[derive(Debug, Default)]
pub struct Transcript {
    blocks: Vec<Block>,
}

/// Resolves a batch's unfinished calls when it ends.
fn finalize_activity(activity: &mut ToolActivity) {
    for call in &mut activity.calls {
        call.status = match call.status {
            CallStatus::Pending => CallStatus::Skipped,
            CallStatus::Running | CallStatus::AwaitingApproval => CallStatus::Cancelled,
            settled => settled,
        };
    }
    activity.complete = true;
}

impl Transcript {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn blocks(&self) -> &[Block] {
        &self.blocks
    }

    pub fn len(&self) -> usize {
        self.blocks.len()
    }

    pub fn is_empty(&self) -> bool {
        self.blocks.is_empty()
    }

    pub fn push(&mut self, block: Block) {
        self.blocks.push(block);
    }

    pub fn last_mut(&mut self) -> Option<&mut Block> {
        self.blocks.last_mut()
    }

    /// Mutable access to every block, used when a delivery notification
    /// retroactively changes earlier rows.
    pub fn blocks_mut(&mut self) -> &mut [Block] {
        &mut self.blocks
    }

    pub fn clear(&mut self) {
        self.blocks.clear();
    }

    /// The trailing block if it is still open, so streamed content can be
    /// appended to it rather than starting a new block per delta.
    pub fn open_tail(&mut self) -> Option<&mut Block> {
        self.blocks.last_mut().filter(|b| b.is_open())
    }

    /// Closes any open block, used when a turn ends or is interrupted.
    ///
    /// Finalising a tool batch also resolves calls that never reported a
    /// result: a queued call becomes skipped and a running one cancelled, so
    /// an interrupted turn never leaves tools apparently still running.
    pub fn close_open(&mut self) {
        if let Some(block) = self.blocks.last_mut() {
            match block {
                Block::Assistant { complete, .. } => *complete = true,
                Block::Thinking { complete, .. } => *complete = true,
                Block::Tools { activity, .. } => finalize_activity(activity),
                _ => {}
            }
        }
    }

    /// Finalises every batch belonging to a turn, not just the trailing one.
    ///
    /// A turn can leave several batches open when assistant text interleaves
    /// with tool calls, and all of them end together.
    pub fn finalize_activities(&mut self, root_turn_id: Option<&str>) {
        for block in &mut self.blocks {
            if let Block::Tools { activity, key, .. } = block {
                let ours = root_turn_id.is_none()
                    || key.root_turn_id.is_none()
                    || key.root_turn_id.as_deref() == root_turn_id;
                if ours && !activity.complete {
                    finalize_activity(activity);
                }
            }
        }
    }

    /// Drops queued messages that were never delivered.
    ///
    /// Anything still pending when a turn ends did not reach the model, so
    /// leaving it in the transcript would misrepresent what was sent.
    pub fn remove_pending_user(&mut self) {
        self.blocks
            .retain(|block| !matches!(block, Block::User { pending: true, .. }));
    }

    /// Marks queued messages as delivered by their steering queue id.
    ///
    /// Returns how many blocks were promoted.
    pub fn mark_delivered(&mut self, ids: &[String]) -> usize {
        let mut promoted = 0;
        for block in &mut self.blocks {
            if let Block::User {
                pending, queue_id, ..
            } = block
            {
                let matched = queue_id
                    .as_ref()
                    .is_some_and(|id| ids.iter().any(|candidate| candidate == id));
                if *pending && matched {
                    *pending = false;
                    promoted += 1;
                }
            }
        }
        promoted
    }

    /// Renders every block to rows, inserting a blank separator between them.
    pub fn render(&self, width: usize, mode: ToolDisplayMode) -> Vec<RenderLine> {
        let mut out = Vec::new();
        for block in &self.blocks {
            let rows = block.render(width, mode);
            if rows.is_empty() {
                continue;
            }
            out.extend(rows);
            out.push(RenderLine::separator());
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_render::tool::{CallStatus, ToolCall};

    fn texts(lines: &[RenderLine]) -> Vec<String> {
        lines.iter().map(|l| l.text.clone()).collect()
    }

    fn user(text: &str) -> Block {
        Block::User {
            text: text.to_string(),
            timestamp: "09:41".to_string(),
            pending: false,
            queue_id: None,
        }
    }

    #[test]
    fn renders_a_user_message_with_its_marker_and_timestamp() {
        let rows = user("hello").render(80, ToolDisplayMode::Summary);
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].text, " \u{276F} hello");
        assert_eq!(rows[0].right_text.as_deref(), Some("09:41"));
        assert_eq!(rows[0].role, Role::User);
    }

    #[test]
    fn user_rows_fill_the_width_with_their_background() {
        let rows = user("hello").render(80, ToolDisplayMode::Summary);
        assert!(rows[0].fill_width);
        assert_eq!(rows[0].background, Some(Role::UserBackground));
    }

    #[test]
    fn only_the_first_user_row_carries_the_timestamp() {
        let long = "word ".repeat(40);
        let rows = Block::User {
            text: long,
            timestamp: "09:41".to_string(),
            pending: false,
            queue_id: None,
        }
        .render(30, ToolDisplayMode::Summary);

        assert!(rows.len() > 1);
        assert!(rows[0].right_text.is_some());
        assert!(rows[1..].iter().all(|r| r.right_text.is_none()));
    }

    #[test]
    fn a_pending_user_message_is_prefixed_and_dimmed() {
        let rows = Block::User {
            text: "later".to_string(),
            timestamp: String::new(),
            pending: true,
            queue_id: None,
        }
        .render(80, ToolDisplayMode::Summary);

        assert!(rows[0].text.contains("[pending] later"));
        assert_eq!(rows[0].role, Role::PendingUser);
    }

    #[test]
    fn renders_an_in_progress_assistant_block_with_the_active_marker() {
        let rows = Block::Assistant {
            text: "hi".to_string(),
            complete: false,
        }
        .render(80, ToolDisplayMode::Summary);
        assert_eq!(rows[0].gutter, Gutter::AgentActive);
    }

    #[test]
    fn renders_a_finished_assistant_block_with_the_complete_marker() {
        let rows = Block::Assistant {
            text: "hi".to_string(),
            complete: true,
        }
        .render(80, ToolDisplayMode::Summary);
        assert_eq!(rows[0].gutter, Gutter::AgentComplete);
    }

    #[test]
    fn assistant_text_is_rendered_as_markdown() {
        let rows = Block::Assistant {
            text: "# Title\n\n- item".to_string(),
            complete: true,
        }
        .render(80, ToolDisplayMode::Summary);

        assert!(rows.iter().any(|r| r.role == Role::Heading));
        assert!(rows.iter().any(|r| r.text.contains('\u{2022}')));
    }

    #[test]
    fn wrapped_assistant_rows_use_the_continuation_gutter() {
        let rows = Block::Assistant {
            text: "alpha beta gamma delta epsilon zeta".to_string(),
            complete: true,
        }
        .render(20, ToolDisplayMode::Summary);

        assert_eq!(rows[0].gutter, Gutter::AgentComplete);
        assert!(rows[1..].iter().all(|r| r.gutter == Gutter::Continuation));
    }

    #[test]
    fn thinking_shows_only_its_status_in_summary_mode() {
        let rows = Block::Thinking {
            text: "deep thoughts".to_string(),
            elapsed_ms: 3000,
            tokens: Some(120),
            complete: false,
        }
        .render(80, ToolDisplayMode::Summary);

        assert_eq!(rows.len(), 1);
        assert!(rows[0].text.contains("Thinking… 3s · 120 tok"));
    }

    #[test]
    fn finished_thinking_reports_its_duration() {
        let rows = Block::Thinking {
            text: String::new(),
            elapsed_ms: 4500,
            tokens: None,
            complete: true,
        }
        .render(80, ToolDisplayMode::Summary);
        assert!(rows[0].text.contains("Thought for 5s"));
    }

    #[test]
    fn thinking_shows_its_body_in_full_mode() {
        let rows = texts(
            &Block::Thinking {
                text: "the reasoning body".to_string(),
                elapsed_ms: 1000,
                tokens: None,
                complete: true,
            }
            .render(80, ToolDisplayMode::Full),
        );
        assert!(rows.iter().any(|r| r.contains("the reasoning body")));
    }

    #[test]
    fn thinking_shows_only_the_tail_in_compact_mode() {
        let body = (1..=10).map(|i| format!("line {i}")).collect::<Vec<_>>().join("\n");
        let rows = texts(
            &Block::Thinking {
                text: body,
                elapsed_ms: 1000,
                tokens: None,
                complete: true,
            }
            .render(80, ToolDisplayMode::Compact),
        );

        assert!(!rows.iter().any(|r| r.contains("line 1\u{0}")));
        assert!(rows.iter().any(|r| r.contains("line 10")));
        assert!(rows.iter().any(|r| r.contains("line 6")));
        assert!(!rows.iter().any(|r| r.contains("line 5")));
    }

    #[test]
    fn renders_a_permission_decision() {
        for (decision, expected, role) in [
            (PermissionDecision::Allowed, "→ allowed", Role::PermissionApproved),
            (PermissionDecision::Denied, "→ denied", Role::Permission),
        ] {
            let rows = Block::Permission {
                tool: "run_command".to_string(),
                preview: "rm -rf /".to_string(),
                decision,
            }
            .render(80, ToolDisplayMode::Summary);
            assert!(rows[0].text.contains(expected));
            assert_eq!(rows[0].role, role);
        }
    }

    #[test]
    fn a_pending_permission_has_no_outcome_suffix() {
        let rows = Block::Permission {
            tool: "edit".to_string(),
            preview: "a.rs".to_string(),
            decision: PermissionDecision::Pending,
        }
        .render(80, ToolDisplayMode::Summary);
        assert_eq!(rows[0].text, "edit a.rs");
        assert_eq!(rows[0].role, Role::Question);
    }

    #[test]
    fn renders_a_question_with_its_answer() {
        let rows = Block::Question {
            question: "Which one?".to_string(),
            answer: Some("the first".to_string()),
        }
        .render(80, ToolDisplayMode::Summary);
        assert_eq!(rows[0].text, "Which one? → the first");
    }

    #[test]
    fn renders_notices_at_their_severity() {
        for (level, role) in [
            (NoticeLevel::Info, Role::Notification),
            (NoticeLevel::Warning, Role::Warning),
            (NoticeLevel::Error, Role::Error),
        ] {
            let rows = Block::Notice {
                text: "something".to_string(),
                level,
            }
            .render(80, ToolDisplayMode::Summary);
            assert_eq!(rows[0].role, role);
        }
    }

    #[test]
    fn command_output_is_sanitized_and_not_word_wrapped() {
        let rows = texts(
            &Block::CommandOutput {
                text: "\u{1b}[31mred\u{1b}[0m\n  indented".to_string(),
            }
            .render(80, ToolDisplayMode::Summary),
        );
        assert_eq!(rows, vec!["red", "  indented"]);
    }

    #[test]
    fn open_blocks_are_recognised() {
        assert!(Block::Assistant {
            text: String::new(),
            complete: false
        }
        .is_open());
        assert!(!Block::Assistant {
            text: String::new(),
            complete: true
        }
        .is_open());
        assert!(!user("x").is_open());
    }

    #[test]
    fn open_tail_only_returns_a_streaming_block() {
        let mut transcript = Transcript::new();
        transcript.push(user("hello"));
        assert!(transcript.open_tail().is_none());

        transcript.push(Block::Assistant {
            text: "hi".to_string(),
            complete: false,
        });
        assert!(transcript.open_tail().is_some());
    }

    #[test]
    fn closing_marks_the_trailing_block_complete() {
        let mut transcript = Transcript::new();
        transcript.push(Block::Assistant {
            text: "hi".to_string(),
            complete: false,
        });
        transcript.close_open();
        assert!(transcript.open_tail().is_none());
    }

    #[test]
    fn closing_completes_an_open_tool_batch() {
        let mut transcript = Transcript::new();
        transcript.push(Block::Tools {
            activity: ToolActivity {
                calls: vec![ToolCall::new("read_file", "{}")],
                complete: false,
            },
            key: ActivityKey::default(),
            calls: Vec::new(),
        });
        transcript.close_open();

        let Some(Block::Tools { activity, .. }) = transcript.blocks().last() else {
            panic!("expected a tool block");
        };
        assert!(activity.complete);
    }

    #[test]
    fn a_separator_follows_every_rendered_block() {
        let mut transcript = Transcript::new();
        transcript.push(user("one"));
        transcript.push(Block::Assistant {
            text: "two".to_string(),
            complete: true,
        });

        let rows = transcript.render(80, ToolDisplayMode::Summary);
        assert!(rows[1].is_separator);
        assert!(rows.last().unwrap().is_separator);
    }

    #[test]
    fn a_block_that_renders_nothing_gets_no_separator() {
        let mut transcript = Transcript::new();
        transcript.push(Block::Tools {
            activity: ToolActivity {
                calls: vec![ToolCall::new("read_file", "{}")],
                complete: true,
            },
            key: ActivityKey::default(),
            calls: Vec::new(),
        });
        assert!(transcript
            .render(80, ToolDisplayMode::Hidden)
            .is_empty());
    }

    #[test]
    fn no_rendered_row_exceeds_the_viewport() {
        let mut transcript = Transcript::new();
        transcript.push(user("a reasonably long user message that will need wrapping"));
        transcript.push(Block::Assistant {
            text: "# Heading\n\nSome **body** text with `code` in it.".to_string(),
            complete: true,
        });
        transcript.push(Block::Thinking {
            text: "reasoning".to_string(),
            elapsed_ms: 1200,
            tokens: Some(40),
            complete: true,
        });
        transcript.push(Block::Tools {
            activity: ToolActivity {
                calls: vec![ToolCall {
                    status: CallStatus::Succeeded,
                    ..ToolCall::new("run_command", r#"{"command":"cargo test --all"}"#)
                }],
                complete: true,
            },
            key: ActivityKey::default(),
            calls: Vec::new(),
        });
        transcript.push(Block::Notice {
            text: "a notice that is long enough to wrap across lines".to_string(),
            level: NoticeLevel::Warning,
        });

        for width in [10usize, 20, 40, 80, 120] {
            for mode in [
                ToolDisplayMode::Full,
                ToolDisplayMode::Compact,
                ToolDisplayMode::Summary,
            ] {
                for row in transcript.render(width, mode) {
                    assert!(
                        text::width(&row.text) <= width,
                        "row {:?} exceeds width {width} in {mode:?}",
                        row.text
                    );
                }
            }
        }
    }
}

