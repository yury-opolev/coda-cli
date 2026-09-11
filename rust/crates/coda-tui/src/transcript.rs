//! The transcript: the ordered list of blocks that make up a conversation.
//!
//! A block is a logical unit (one user message, one assistant reply, one batch
//! of tool calls). Blocks are rendered to rows on demand, because the row count
//! depends on the viewport width and must be recomputed on resize.

use coda_proto::Correlation;
use coda_render::text;
use coda_render::theme::Role;
use coda_render::tool::{CallStatus, ToolActivity, ToolDisplayMode, ToolSummary};
use coda_render::{markdown, Gutter, RenderLine, MARKER_CELLS};

use crate::render::glyphs;

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
///
/// A `call_id` alone is **not** an identity. Provider tool-call ids are only
/// unique within one request, and since the transcript can now be rehydrated
/// from stored history the same id genuinely appears twice in one buffer: once
/// on a replayed call from an earlier turn, once on a live one. So when both
/// sides name a turn or a batch, those have to agree too — otherwise a live
/// result would rewrite a historical call that merely shares an id, silently
/// replacing the wrong output. Either side omitting them (a legacy engine, an
/// old transcript) falls back to the previous behaviour rather than refusing
/// to match at all.
pub fn same_call(a: &Correlation, b: &Correlation) -> bool {
    if a.call_id.is_none() || a.call_id != b.call_id || a.source_id != b.source_id {
        return false;
    }
    let agrees = |left: &Option<String>, right: &Option<String>| match (left, right) {
        (Some(left), Some(right)) => left == right,
        _ => true,
    };
    agrees(&a.root_turn_id, &b.root_turn_id) && agrees(&a.activity_id, &b.activity_id)
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

/// How the flattened row list groups a conversation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum TranscriptStyle {
    /// One block after another, separated by a blank row. The original,
    /// unchanged behaviour — kept so existing plain-mode tests and helpers
    /// still describe exactly what they always described.
    #[default]
    Plain,
    /// Groups one delivered user message and everything that follows — the
    /// reply, its tools, its reasoning, any notices — until the next
    /// delivered user message, into a card separated by blank spacing.
    ///
    /// Presentation only: the underlying `Vec<Block>` and provider history
    /// are unchanged: cards are chrome rows inserted around the same blocks
    /// `Plain` would have drawn, computed in the same single pass.
    Cards,
}

/// Blank spacing marking a card boundary.
///
/// Wholly decorative (`is_chrome`): it must never enter a copy, and a click
/// on it must never resolve to a fold, no matter what the block-start
/// arithmetic around it happens to look like.
fn card_separator() -> RenderLine {
    RenderLine::separator().as_chrome()
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
    ///
    /// Foldable: collapsed live blocks preview the last line of reasoning;
    /// completed blocks show only their header. Expansion is explicit unless
    /// Full display mode is selected.
    Thinking {
        text: String,
        elapsed_ms: i64,
        tokens: Option<i32>,
        complete: bool,
        expanded: bool,
        /// Local `HH:mm` the reasoning finished, frozen once set.
        ///
        /// `None` until `ThinkingComplete` arrives, and never recomputed
        /// after: a duplicate or late completion event must not change what
        /// is already shown as done.
        done_at: Option<String>,
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
    /// A git diff requested via `/diff`.
    Diff { raw: String },
    /// A marker separating resumed sessions.
    SessionBoundary { id: String },
    /// The startup banner: wordmark plus session details.
    ///
    /// Rendered in the transcript rather than written to the raw console, so it
    /// scrolls, reflows and can be selected like any other content.
    Banner {
        /// The wordmark rows, carried separately so they keep the brand colour.
        wordmark: Vec<String>,
        /// Version, cwd, provider and model.
        details: Vec<String>,
    },
}

impl Block {
    /// Whether this block belongs to this client rather than to the engine's
    /// conversation.
    ///
    /// The distinction is load-bearing: a `session/getHistory` read is
    /// authoritative over everything it *can* describe, and it cannot
    /// describe the startup banner, a local notice, the output of a slash
    /// command or a `/diff`. Those are the terminal's own, so a rebuild
    /// replaces the conversation around them rather than through them.
    ///
    /// Permission outcomes and answered questions are here for a different
    /// reason: they are records of what the operator did *at this terminal*.
    /// The engine's rich history describes the conversation the model saw and
    /// carries neither, so dropping them on a rebuild would not "reload" them
    /// from anywhere — it would erase the only record that they happened.
    pub fn is_client_owned(&self) -> bool {
        matches!(
            self,
            Block::Banner { .. }
                | Block::Notice { .. }
                | Block::CommandOutput { .. }
                | Block::Diff { .. }
                | Block::SessionBoundary { .. }
                | Block::Permission { .. }
                | Block::Question { .. }
        )
    }

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
                expanded,
                done_at,
            } => render_thinking(
                text,
                *elapsed_ms,
                *tokens,
                *complete,
                *expanded,
                done_at.as_deref(),
                width,
                mode,
            ),
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
                    Some(answer) => format!("{question} {} {answer}", glyphs::ARROW_RIGHT),
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
            Block::Banner { wordmark, details } => {
                // The wordmark is never wrapped: a broken figlet is worse than
                // one clipped by a narrow terminal.
                let mut lines: Vec<RenderLine> = wordmark
                    .iter()
                    .map(|row| {
                        RenderLine::new(text::truncate(&text::sanitize(row), width), Role::Heading)
                    })
                    .collect();
                lines.extend(details.iter().flat_map(|line| {
                    text::wrap_preformatted(&text::sanitize(line), width)
                        .into_iter()
                        .map(|chunk| RenderLine::new(chunk, Role::Notification))
                }));
                lines
            }
            Block::Diff { raw } => {
                let diff = coda_render::diff::parse(raw);
                if diff.is_empty() {
                    if raw.trim().is_empty() {
                        return text::wrap("No changes.", width)
                            .into_iter()
                            .map(|chunk| RenderLine::new(chunk, Role::Notification))
                            .collect();
                    }
                    // Unparseable but non-empty: render the raw text flat so the
                    // user can still read it (matches the C# legacy fallback).
                    return raw
                        .lines()
                        .flat_map(|line| {
                            text::wrap_preformatted(&text::sanitize(line), width)
                        })
                        .map(|chunk| RenderLine::new(chunk, Role::Code))
                        .collect();
                }
                coda_render::diff::render(&diff, width, false)
            }
            Block::SessionBoundary { id } => {
                let label = format!("{0}{0} session {id} {0}{0}", glyphs::RULE);
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

/// Formats a whole number of seconds as `Ns` under a minute, else `M:SS`.
pub(crate) fn format_duration(seconds: i64) -> String {
    if seconds < 60 {
        format!("{seconds}s")
    } else {
        format!("{}:{:02}", seconds / 60, seconds % 60)
    }
}

fn render_thinking(
    body: &str,
    elapsed_ms: i64,
    tokens: Option<i32>,
    complete: bool,
    expanded: bool,
    done_at: Option<&str>,
    width: usize,
    mode: ToolDisplayMode,
) -> Vec<RenderLine> {
    // The body of a live block hangs under the header's inset — unless the
    // terminal is narrower than the inset itself, where an indent would push
    // every row past the right edge.
    let (body_gutter, content) = if width > MARKER_CELLS {
        (Gutter::Continuation, width - MARKER_CELLS)
    } else {
        (Gutter::None, width.max(1))
    };
    // Round half away from zero: `{:.0}` would round 4.5 to 4, which reads as
    // a stopwatch running backwards when the elapsed time ticks past .5.
    let seconds = (elapsed_ms as f64 / 1000.0).round() as i64;

    if mode == ToolDisplayMode::Hidden {
        return Vec::new();
    }

    // Nothing to open when the reasoning never arrived as text — a provider
    // that encrypts it sends only a signature. Offering a fold that expands to
    // an empty block would be a lie about what is behind it.
    let has_body = body.lines().any(|line| !line.trim().is_empty());

    // Full is the "show me everything" mode, so it opens every block without
    // needing a click; otherwise the block's own state decides.
    let open = has_body && (expanded || mode == ToolDisplayMode::Full);
    let marker = if !has_body {
        // Same width as a fold marker, so headers stay aligned in a column of
        // them even when this one has nothing to open.
        " "
    } else if open {
        glyphs::FOLD_EXPANDED
    } else {
        glyphs::FOLD_COLLAPSED
    };

    let headline = if complete {
        // A duration of zero means the clock never started, not that no time
        // passed: it only runs from the first delta, and encrypted reasoning
        // produces none. Claiming "0s" would be inventing a measurement.
        //
        // The local "done" time is appended only when known — never invented
        // — and is frozen at whatever `ThinkingComplete` reported, so a late
        // or duplicate completion event cannot make it jump.
        let suffix = done_at
            .map(|at| format!(" · done {at}"))
            .unwrap_or_default();
        match seconds {
            0 => format!("Thought{suffix}"),
            seconds => format!("Thought for {}{suffix}", format_duration(seconds)),
        }
    } else {
        let seconds = elapsed_ms.max(0) / 1000;
        let duration = format_duration(seconds);
        match tokens {
            Some(tokens) => format!("Thinking... {duration} · {tokens} tok"),
            None => format!("Thinking... {duration}"),
        }
    };

    // The same disclosure header a tool summary draws: one inset marker, no
    // second icon, and the marker column excluded from a copy.
    let role = if complete {
        Role::ThinkingHeader
    } else {
        Role::Notification
    };
    let mut out = coda_render::line::disclosure_header(&headline, marker, width, role);

    if complete {
        if open {
            let gutter = if width > Gutter::ThinkingBody.cells() {
                Gutter::ThinkingBody
            } else {
                Gutter::None
            };
            let body_width = width.saturating_sub(gutter.cells()).max(1);
            for line in markdown::render(body, body_width) {
                // Keep Markdown layout, but reasoning stays visually subordinate
                // instead of inheriting answer/code/link colors.
                out.push(RenderLine::new(line.text, Role::ThinkingBody).with_gutter(gutter));
            }
        }
        return out;
    }

    if open {
        for line in markdown::render(body, content) {
            out.push(line.with_gutter(body_gutter));
        }
        return out;
    }

    // Collapsed: the last line of reasoning. It is the part that says where
    // the model actually got to, and one line costs nothing when a turn
    // produces a dozen of these.
    if let Some(last) = body.lines().map(str::trim).filter(|l| !l.is_empty()).next_back() {
        for chunk in text::wrap(last, content) {
            out.push(RenderLine::new(chunk, Role::Notification).with_gutter(body_gutter));
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
        PermissionDecision::Allowed => (
            format!(" {} allowed", glyphs::ARROW_RIGHT),
            Role::PermissionApproved,
        ),
        PermissionDecision::Denied => (
            format!(" {} denied", glyphs::ARROW_RIGHT),
            Role::Permission,
        ),
        PermissionDecision::Pending => (String::new(), Role::Question),
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
    expanded_tool_groups: std::collections::BTreeSet<usize>,
    tool_group_boundaries: std::collections::BTreeSet<usize>,
}

fn completed_tools(block: &Block) -> bool {
    matches!(block, Block::Tools { activity, .. }
        if activity.complete && !activity.calls.is_empty()
            && activity.calls.iter().all(|call| call.status.is_terminal()))
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

/// Marks one block as no longer able to receive streamed content.
fn close_block(block: &mut Block) {
    match block {
        Block::Assistant { complete, .. } => *complete = true,
        Block::Thinking { complete, .. } => *complete = true,
        Block::Tools { activity, .. } => finalize_activity(activity),
        _ => {}
    }
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
        self.expanded_tool_groups.clear();
        self.tool_group_boundaries.clear();
    }

    /// Replaces the conversation with the engine's authoritative version,
    /// keeping the blocks that are this client's own.
    ///
    /// The engine owns what the conversation *is*; it does not own the
    /// startup banner, the launch notices, `/help` output or a `/diff`, none
    /// of which any history read could ever return. Clearing everything threw
    /// those away on every resume, gap and compaction — and an empty
    /// authoritative history did it while adding nothing back.
    ///
    /// Position is preserved as far as it can honestly be: client blocks that
    /// preceded the whole conversation still precede it, and the rest keep
    /// their order after it, because where they sat *within* a conversation
    /// that has just been rebuilt is no longer knowable.
    ///
    /// Fold and group state is dropped with the old blocks, exactly as
    /// [`Self::clear`] does: both are recorded by block index, and carrying
    /// them over would expand or fold whichever blocks happen to land on
    /// those indices next.
    pub fn replace_conversation(&mut self, conversation: Vec<Block>) {
        let mut conversation = conversation;
        // Closed here rather than by the caller: once the client's own
        // trailing blocks are appended, the conversation's last block is no
        // longer the transcript's last block, and a caller reaching for
        // `close_open` would finalise a banner instead of a tool batch.
        if let Some(last) = conversation.last_mut() {
            // A reasoning burst the engine is still streaming is the one
            // thing a rebuild may hand over open: the read describes it as
            // in-flight, and finalising it here would freeze a live row and
            // stop its clock the moment the conversation was re-read.
            if !matches!(last, Block::Thinking { complete: false, .. }) {
                close_block(last);
            }
        }
        let previous = std::mem::take(&mut self.blocks);
        let first_conversation = previous.iter().position(|block| !block.is_client_owned());
        let split = first_conversation.unwrap_or(previous.len());
        let mut rebuilt: Vec<Block> = Vec::with_capacity(previous.len() + conversation.len());
        let mut trailing: Vec<Block> = Vec::new();
        for (index, block) in previous.into_iter().enumerate() {
            if index < split {
                rebuilt.push(block);
            } else if block.is_client_owned() {
                trailing.push(block);
            }
        }
        rebuilt.extend(conversation);
        rebuilt.extend(trailing);

        self.blocks = rebuilt;
        self.expanded_tool_groups.clear();
        self.tool_group_boundaries.clear();
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
            close_block(block);
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
        let mut old_index = 0;
        let mut new_index = 0;
        let mut expanded = std::collections::BTreeSet::new();
        let mut boundaries = std::collections::BTreeSet::new();
        self.blocks.retain(|block| {
            if self.tool_group_boundaries.contains(&old_index) {
                boundaries.insert(new_index);
            }
            let keep = !matches!(block, Block::User { pending: true, .. });
            if keep {
                if self.expanded_tool_groups.contains(&old_index) {
                    expanded.insert(new_index);
                }
                new_index += 1;
            }
            old_index += 1;
            keep
        });
        if self.tool_group_boundaries.contains(&old_index) {
            boundaries.insert(new_index);
        }
        self.expanded_tool_groups = expanded;
        self.tool_group_boundaries = boundaries;
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

    /// Toggles the fold on the block at `index`, reporting whether it moved.
    ///
    /// Only reasoning blocks fold today. Returning `false` for everything else
    /// lets the caller treat a click on an ordinary row as "not mine" and pass
    /// it on to selection, rather than swallowing it.
    pub fn toggle_fold(&mut self, index: usize) -> bool {
        match self.blocks.get_mut(index) {
            Some(Block::Thinking { expanded, .. }) => {
                *expanded = !*expanded;
                true
            }
            _ => false,
        }
    }

    /// Whether the block at `index` can be folded at all.
    ///
    /// Asked before a click is claimed, so a click on an ordinary row still
    /// starts a selection instead of being swallowed by a fold that never
    /// happens. A reasoning block with no text is not foldable: it draws no
    /// fold marker, and toggling a state nothing renders would consume the
    /// click for nothing.
    pub fn is_foldable(&self, index: usize) -> bool {
        matches!(
            self.blocks.get(index),
            Some(Block::Thinking { text, .. }) if text.lines().any(|l| !l.trim().is_empty())
        )
    }

    pub fn is_tool_group_foldable(&self, index: usize) -> bool {
        self.tool_group_range(index).is_some()
    }

    /// Batch/root/source IDs describe engine activity, not UI turn ownership.
    /// Explicit boundaries prevent a later turn's adjacent tools joining this one.
    pub fn end_tool_group(&mut self) {
        self.tool_group_boundaries.insert(self.blocks.len());
    }

    pub fn toggle_tool_group(&mut self, index: usize) -> bool {
        let Some(range) = self.tool_group_range(index) else { return false };
        if !self.expanded_tool_groups.remove(&range.start) {
            self.expanded_tool_groups.insert(range.start);
        }
        true
    }

    fn tool_group_range(&self, index: usize) -> Option<std::ops::Range<usize>> {
        if !completed_tools(self.blocks.get(index)?) { return None; }
        let mut start = index;
        while start > 0 && !self.tool_group_boundaries.contains(&start)
            && completed_tools(&self.blocks[start - 1])
        {
            start -= 1;
        }
        let mut end = index + 1;
        while end < self.blocks.len() && !self.tool_group_boundaries.contains(&end)
            && completed_tools(&self.blocks[end])
        {
            end += 1;
        }
        Some(start..end)
    }

    /// Renders every block to rows, inserting a blank separator between them.
    pub fn render(&self, width: usize, mode: ToolDisplayMode) -> Vec<RenderLine> {
        self.render_pass(width, mode, TranscriptStyle::Plain).0
    }

    /// Renders all blocks and returns both the flat row list and a per-block
    /// start-row table.
    ///
    /// `block_starts[i]` is the index of the first row of block `i` in the
    /// returned `Vec<RenderLine>`.  Blocks that render to zero rows have a
    /// start equal to the next non-empty block's start.  A sentinel entry
    /// equal to `rows.len()` is appended so callers can use adjacent pairs for
    /// a range without bounds-checking.
    ///
    /// This is the Rust equivalent of `TranscriptLayoutIndex`'s prefix-sum
    /// array, computed in a single O(n) pass to avoid rendering blocks twice.
    pub fn render_with_block_starts(
        &self,
        width: usize,
        mode: ToolDisplayMode,
    ) -> (Vec<RenderLine>, Vec<usize>) {
        self.render_pass(width, mode, TranscriptStyle::Plain)
    }

    /// Same as [`Transcript::render_with_block_starts`], but with an explicit
    /// presentation [`TranscriptStyle`].
    ///
    /// One shared pass backs every style: `Cards` differs only in the chrome
    /// rows it inserts around a card's boundary, never in how a block itself
    /// is laid out or where its content genuinely starts — so a click, a
    /// fold, or a copy behaves exactly the same inside a card as outside one.
    pub fn render_with_block_starts_styled(
        &self,
        width: usize,
        mode: ToolDisplayMode,
        style: TranscriptStyle,
    ) -> (Vec<RenderLine>, Vec<usize>) {
        self.render_pass(width, mode, style)
    }

    fn render_pass(
        &self,
        width: usize,
        mode: ToolDisplayMode,
        style: TranscriptStyle,
    ) -> (Vec<RenderLine>, Vec<usize>) {
        let mut rows: Vec<RenderLine> = Vec::new();
        let mut starts: Vec<usize> = Vec::with_capacity(self.blocks.len() + 1);
        // Whether a card is currently open, so the transcript's very last
        // card can be closed with spacing after the loop rather than only
        // between two cards.
        let mut card_open = false;

        let mut index = 0;
        while index < self.blocks.len() {
            let block = &self.blocks[index];
            // A card starts at each delivered user message (every `User`
            // block is delivered: a pending one is never pushed here at all,
            // see `UiState`). One blank boundary row serves both cards,
            // preserving grouping without drawing a horizontal divider.
            // Banners, session boundaries and anything before the first user
            // message are drawn plainly, never inside a card.
            if style == TranscriptStyle::Cards && matches!(block, Block::User { .. }) {
                rows.push(card_separator());
                card_open = true;
            }

            // Recorded *after* any chrome for this block, so it always
            // points at the block's true first content row — never at a
            // blank boundary pushed in front of it.
            starts.push(rows.len());
            if mode == ToolDisplayMode::Summary {
                if let Some(group) = self.tool_group_range(index) {
                    let header_row = rows.len();
                    starts.extend(std::iter::repeat(header_row).take(group.end - index - 1));
                    let calls = self.blocks[group.clone()].iter().filter_map(|block| {
                        if let Block::Tools { activity, .. } = block {
                            Some(activity.calls.iter())
                        } else {
                            None
                        }
                    }).flatten();
                    let expanded = self.expanded_tool_groups.contains(&group.start);
                    let fold = if expanded { glyphs::FOLD_EXPANDED } else { glyphs::FOLD_COLLAPSED };
                    rows.extend(ToolSummary::from_calls(calls).render(width, Some(fold)));
                    if expanded {
                        for block in &self.blocks[group.clone()] {
                            rows.extend(block.render(width, ToolDisplayMode::Full));
                        }
                    }
                    rows.push(RenderLine::separator());
                    index = group.end;
                    continue;
                }
            }
            let block_rows = block.render(width, mode);
            if !block_rows.is_empty() {
                rows.extend(block_rows);
                rows.push(RenderLine::separator());
            }
            index += 1;
        }

        if style == TranscriptStyle::Cards && card_open {
            rows.push(card_separator());
        }

        starts.push(rows.len()); // sentinel
        (rows, starts)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_render::tool::{CallStatus, ToolCall};

    fn completed_tool(name: &str, status: CallStatus, root: &str, source: Option<&str>) -> Block {
        let mut call = ToolCall::new(name, "{}");
        call.status = status;
        call.result = Some(format!("{name} result"));
        call.is_error = status == CallStatus::Failed;
        Block::Tools {
            activity: ToolActivity { calls: vec![call], complete: true },
            key: ActivityKey { root_turn_id: Some(root.into()), activity_id: Some(name.into()) },
            calls: vec![Correlation {
                root_turn_id: Some(root.into()), source_id: source.map(str::to_owned),
                ..Default::default()
            }],
        }
    }

    #[test]
    fn consecutive_completed_tool_runs_share_one_summary_without_mutating_blocks() {
        let mut transcript = Transcript::new();
        for name in ["read_file", "grep", "glob"] {
            transcript.push(completed_tool(name, CallStatus::Succeeded, "turn", None));
        }
        let (rows, starts) = transcript.render_with_block_starts(80, ToolDisplayMode::Summary);
        assert_eq!(rows.iter().filter(|row| row.text.contains("Ran 3 tools")).count(), 1);
        assert!(!rows.iter().any(|row| row.text.contains("Ran 1 tool")));
        assert_eq!(transcript.blocks().len(), 3);
        assert_eq!(starts.len(), 4);
        assert!(starts.windows(2).all(|pair| pair[0] <= pair[1]));
        assert_eq!(starts[3], rows.len());
    }

    #[test]
    fn consecutive_completed_tool_runs_preserve_failed_cancelled_and_skipped_status() {
        let mut transcript = Transcript::new();
        for (name, status) in [
            ("read_file", CallStatus::Succeeded), ("grep", CallStatus::Failed),
            ("glob", CallStatus::Cancelled), ("edit", CallStatus::Skipped),
        ] {
            transcript.push(completed_tool(name, status, "turn", None));
        }
        let rows = transcript.render(120, ToolDisplayMode::Summary);
        let header = &rows[0].text;
        assert!(header.contains("Ran 4 tools"), "{header}");
        assert!(header.contains("1 failed") && header.contains("cancelled") && header.contains("1 skipped"), "{header}");
    }

    #[test]
    fn completed_tool_group_expands_results_and_collapses_from_any_member() {
        let mut transcript = Transcript::new();
        transcript.push(completed_tool("first", CallStatus::Succeeded, "turn", None));
        transcript.push(completed_tool("second", CallStatus::Failed, "turn", None));
        let collapsed = transcript.render(100, ToolDisplayMode::Summary);
        assert_eq!(collapsed.len(), 2);
        assert!(collapsed[0].text.contains(glyphs::FOLD_COLLAPSED));
        assert!(transcript.toggle_tool_group(1));
        let expanded = transcript.render(100, ToolDisplayMode::Summary);
        assert!(expanded[0].text.contains(glyphs::FOLD_EXPANDED));
        assert!(expanded.iter().any(|row| row.text.contains("first result")));
        assert!(expanded.iter().any(|row| row.text.contains("second result")));
        assert!(expanded.iter().any(|row| row.text.contains("[error]")));
        let copied = crate::selection::copy_visible_text(&expanded, 0..expanded.len());
        assert!(!copied.contains(glyphs::FOLD_EXPANDED));
        assert!(copied.contains("Ran 2 tools - 1 failed"));
        assert!(transcript.toggle_tool_group(0));
        assert_eq!(transcript.render(100, ToolDisplayMode::Summary), collapsed);
        assert_eq!(transcript.blocks().len(), 2);
    }

    #[test]
    fn running_tool_batch_stays_separate_until_it_completes() {
        let mut transcript = Transcript::new();
        transcript.push(completed_tool("first", CallStatus::Succeeded, "turn", None));
        let mut running = completed_tool("second", CallStatus::Running, "turn", None);
        if let Block::Tools { activity, .. } = &mut running { activity.complete = false; }
        transcript.push(running);
        let rows = transcript.render(100, ToolDisplayMode::Summary);
        assert!(rows.iter().any(|row| row.text.contains("Ran 1 tool")));
        assert!(rows.iter().any(|row| row.text.contains("Running 1 tool")));
        assert!(!transcript.is_tool_group_foldable(1));
        if let Block::Tools { activity, .. } = &mut transcript.blocks_mut()[1] {
            activity.complete = true;
            activity.calls[0].status = CallStatus::Succeeded;
        }
        let rows = transcript.render(100, ToolDisplayMode::Summary);
        assert_eq!(rows.len(), 2);
        assert!(rows[0].text.contains("Ran 2 tools"));
    }

    #[test]
    fn tool_detail_modes_still_show_each_original_call() {
        let mut transcript = Transcript::new();
        transcript.push(completed_tool("first", CallStatus::Succeeded, "turn", None));
        transcript.push(completed_tool("second", CallStatus::Failed, "turn", None));
        for mode in [ToolDisplayMode::Compact, ToolDisplayMode::Full] {
            let rows = transcript.render(100, mode);
            assert!(rows.iter().any(|row| row.text.contains("first")));
            assert!(rows.iter().any(|row| row.text.contains("second")));
            assert!(!rows.iter().any(|row| row.text.contains("Ran 2")));
        }
        assert!(transcript.render(100, ToolDisplayMode::Hidden).is_empty());
    }

    #[test]
    fn completed_tool_runs_do_not_cross_turn_or_message_boundaries() {
        let boundaries = [
            user("new user"),
            Block::Assistant { text: "explanation".into(), complete: true },
            Block::Notice { text: "warning".into(), level: NoticeLevel::Warning },
            Block::Permission { tool: "edit".into(), preview: "approval".into(), decision: PermissionDecision::Pending },
            Block::Thinking { text: "reasoning".into(), elapsed_ms: 1000, tokens: None, complete: true, expanded: false, done_at: None },
        ];
        for boundary in boundaries {
            let mut transcript = Transcript::new();
            transcript.push(completed_tool("first", CallStatus::Succeeded, "turn", None));
            transcript.push(boundary);
            transcript.push(completed_tool("second", CallStatus::Succeeded, "turn", None));
            assert_eq!(transcript.render(120, ToolDisplayMode::Summary).iter()
                .filter(|row| row.text.contains("Ran 1 tool")).count(), 2);
        }
        let mut transcript = Transcript::new();
        transcript.push(completed_tool("first", CallStatus::Succeeded, "turn", None));
        transcript.end_tool_group();
        transcript.push(completed_tool("second", CallStatus::Succeeded, "other-turn", None));
        assert_eq!(transcript.render(120, ToolDisplayMode::Summary).iter()
            .filter(|row| row.text.contains("Ran 1 tool")).count(), 2);
    }

    #[test]
    fn replacing_the_conversation_keeps_this_clients_own_blocks() {
        // The engine owns the conversation; the banner, the launch notices,
        // `/help` output and a `/diff` are this client's own and are not a
        // projection of any history the engine could return. Throwing them
        // away on every resume, gap or compaction lost them for good.
        let mut transcript = Transcript::new();
        transcript.push(Block::Banner { wordmark: vec!["coda".into()], details: vec![] });
        transcript.push(Block::Notice { text: "Forked from abc".into(), level: NoticeLevel::Info });
        transcript.push(user_block("old question"));
        transcript.push(assistant_block("old answer"));
        transcript.push(Block::CommandOutput { text: "/help".into() });
        transcript.push(Block::Diff { raw: "diff --git a b".into() });

        transcript.replace_conversation(vec![user_block("new question"), assistant_block("new answer")]);

        let shape: Vec<&str> = transcript
            .blocks()
            .iter()
            .map(|block| match block {
                Block::Banner { .. } => "banner",
                Block::Notice { .. } => "notice",
                Block::User { text, .. } => text.as_str(),
                Block::Assistant { text, .. } => text.as_str(),
                Block::CommandOutput { .. } => "output",
                Block::Diff { .. } => "diff",
                _ => "other",
            })
            .collect();
        assert_eq!(
            shape,
            [
                "banner",
                "notice",
                "new question",
                "new answer",
                "output",
                "diff",
            ],
            "client-owned blocks must survive, with the leading ones still leading"
        );
    }

    #[test]
    fn an_empty_authoritative_conversation_still_clears_the_old_one() {
        // A rewind to the very start says the conversation is empty. Keeping
        // the previous messages because there was nothing to replace them
        // with would show a conversation the engine no longer has.
        let mut transcript = Transcript::new();
        transcript.push(Block::Banner { wordmark: vec!["coda".into()], details: vec![] });
        transcript.push(user_block("old question"));
        transcript.push(assistant_block("old answer"));

        transcript.replace_conversation(Vec::new());

        assert_eq!(transcript.len(), 1, "{:?}", transcript.blocks());
        assert!(matches!(transcript.blocks()[0], Block::Banner { .. }));
    }

    #[test]
    fn replacing_the_conversation_drops_fold_state_that_indexed_the_old_one() {
        // Group expansion and group boundaries are recorded by block index.
        // Carrying them across a replacement would fold and expand whichever
        // blocks happen to land on those indices next.
        let mut transcript = Transcript::new();
        transcript.push(completed_tool("read_file", CallStatus::Succeeded, "turn", None));
        transcript.push(completed_tool("grep", CallStatus::Succeeded, "turn", None));
        assert!(transcript.toggle_tool_group(0), "the group is foldable to begin with");
        transcript.end_tool_group();

        transcript.replace_conversation(vec![
            completed_tool("edit", CallStatus::Succeeded, "next", None),
            completed_tool("bash", CallStatus::Succeeded, "next", None),
        ]);

        let rows = transcript.render(80, ToolDisplayMode::Summary);
        assert_eq!(
            rows.iter().filter(|row| row.text.contains("Ran 2 tools")).count(),
            1,
            "a stale expansion or boundary survived the replacement: {rows:?}"
        );
    }

    #[test]
    fn replacing_the_conversation_keeps_decisions_the_engine_cannot_reconstruct() {
        // A permission outcome and a question's answer are records of what the
        // operator did *at this terminal*. `session/getHistory` describes the
        // conversation the model saw and carries neither, so a rebuild that
        // dropped them erased the only record that they ever happened — and
        // erased it precisely when a resume or a compaction made the record
        // most valuable.
        let mut transcript = Transcript::new();
        transcript.push(user_block("old question"));
        transcript.push(Block::Permission {
            tool: "run_command".into(),
            preview: "rm -rf build".into(),
            decision: PermissionDecision::Denied,
        });
        transcript.push(Block::Question {
            question: "Which branch?".into(),
            answer: Some("main".into()),
        });

        transcript.replace_conversation(vec![user_block("new question")]);

        assert!(
            transcript.blocks().iter().any(|b| matches!(
                b,
                Block::Permission { decision: PermissionDecision::Denied, .. }
            )),
            "the permission decision was erased: {:?}",
            transcript.blocks()
        );
        assert!(
            transcript
                .blocks()
                .iter()
                .any(|b| matches!(b, Block::Question { answer: Some(a), .. } if a == "main")),
            "the answer given here was erased: {:?}",
            transcript.blocks()
        );
        assert!(
            !transcript
                .blocks()
                .iter()
                .any(|b| matches!(b, Block::User { text, .. } if text == "old question")),
            "the conversation itself must still be replaced: {:?}",
            transcript.blocks()
        );
    }

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
    fn a_collapsed_thinking_block_previews_its_last_line() {
        let rows = texts(
            &Block::Thinking {
                text: "first thought\nsecond thought\nwhere it got to".to_string(),
                elapsed_ms: 3000,
                tokens: Some(120),
                complete: false,
                expanded: false,
                done_at: None,
            }
            .render(80, ToolDisplayMode::Summary),
        );

        // The header, then one line of preview -- the last, because that is
        // where the model actually got to.
        assert_eq!(rows.len(), 2, "expected a header and one preview: {rows:?}");
        assert!(rows[0].contains("Thinking... 3s · 120 tok"));
        assert!(rows[1].contains("where it got to"));
        assert!(!rows.iter().any(|r| r.contains("first thought")));
    }

    #[test]
    fn live_thinking_formats_seconds_then_minutes_and_seconds() {
        for (elapsed_ms, expected) in [
            (0, "0s"),
            (999, "0s"),
            (1000, "1s"),
            (59_999, "59s"),
            (60_000, "1:00"),
            (65_000, "1:05"),
            (3_661_000, "61:01"),
        ] {
            for expanded in [false, true] {
                let rows = Block::Thinking {
                    text: "reasoning".into(),
                    elapsed_ms,
                    tokens: None,
                    complete: false,
                    expanded,
                    done_at: None,
                }
                .render(80, ToolDisplayMode::Summary);
                assert!(rows[0].text.ends_with(&format!("Thinking... {expected}")), "{rows:?}");
            }
        }
    }

    #[test]
    fn a_collapsed_thinking_block_offers_to_open_and_an_open_one_to_close() {
        let block = |expanded| Block::Thinking {
            text: "reasoning".to_string(),
            elapsed_ms: 1000,
            tokens: None,
            complete: true,
            expanded,
            done_at: None,
        };

        let collapsed = texts(&block(false).render(80, ToolDisplayMode::Summary));
        assert!(
            collapsed[0].contains(glyphs::FOLD_COLLAPSED),
            "a foldable block that does not say so is a feature nobody finds: {collapsed:?}"
        );

        let open = texts(&block(true).render(80, ToolDisplayMode::Summary));
        assert!(open[0].contains(glyphs::FOLD_EXPANDED), "{open:?}");
    }

    #[test]
    fn an_expanded_thinking_block_shows_everything() {
        let body = (1..=10)
            .map(|i| format!("line {i}"))
            .collect::<Vec<_>>()
            .join("\n\n");
        let rows = texts(
            &Block::Thinking {
                text: body,
                elapsed_ms: 1000,
                tokens: None,
                complete: true,
                expanded: true,
                done_at: None,
            }
            .render(80, ToolDisplayMode::Summary),
        );

        assert!(rows.iter().any(|r| r.contains("line 1")));
        assert!(rows.iter().any(|r| r.contains("line 10")));
    }

    #[test]
    fn a_hidden_thinking_block_renders_nothing_at_all() {
        // Hidden means hidden: the fold does not override an explicit request
        // to see none of this.
        let rows = Block::Thinking {
            text: "reasoning".to_string(),
            elapsed_ms: 1000,
            tokens: None,
            complete: true,
            expanded: true,
            done_at: None,
        }
        .render(80, ToolDisplayMode::Hidden);
        assert!(rows.is_empty(), "{rows:?}");
    }

    #[test]
    fn finished_thinking_reports_its_duration() {
        let rows = Block::Thinking {
            text: String::new(),
            elapsed_ms: 4500,
            tokens: None,
            complete: true,
            expanded: false,
            done_at: None,
        }
        .render(80, ToolDisplayMode::Summary);
        assert!(rows[0].text.contains("Thought for 5s"));
    }

    #[test]
    fn finished_thinking_appends_the_frozen_done_time_when_known() {
        let rows = Block::Thinking {
            text: String::new(),
            elapsed_ms: 4500,
            tokens: None,
            complete: true,
            expanded: false,
            done_at: Some("09:41".to_string()),
        }
        .render(80, ToolDisplayMode::Summary);
        assert!(
            rows[0].text.contains("Thought for 5s · done 09:41"),
            "{:?}",
            rows[0].text
        );
    }

    #[test]
    fn finished_thinking_omits_the_done_time_when_it_is_unknown() {
        let rows = Block::Thinking {
            text: String::new(),
            elapsed_ms: 4500,
            tokens: None,
            complete: true,
            expanded: false,
            done_at: None,
        }
        .render(80, ToolDisplayMode::Summary);
        assert!(!rows[0].text.contains("done"), "{:?}", rows[0].text);
    }

    #[test]
    fn a_zero_duration_thought_can_still_show_a_known_done_time() {
        // Zero elapsed means the clock never started (encrypted reasoning),
        // not that no time passed — but the local done time is independent
        // of that measurement and can still be known.
        let rows = Block::Thinking {
            text: String::new(),
            elapsed_ms: 0,
            tokens: None,
            complete: true,
            expanded: false,
            done_at: Some("09:41".to_string()),
        }
        .render(80, ToolDisplayMode::Summary);
        assert!(rows[0].text.contains("Thought · done 09:41"), "{:?}", rows[0].text);
    }

    #[test]
    fn thinking_shows_its_body_in_full_mode() {
        let rows = texts(
            &Block::Thinking {
                text: "the reasoning body".to_string(),
                elapsed_ms: 1000,
                tokens: None,
                complete: true,
                expanded: false,
                done_at: None,
            }
            .render(80, ToolDisplayMode::Full),
        );
        assert!(rows.iter().any(|r| r.contains("the reasoning body")));
    }

    #[test]
    fn completed_collapsed_thinking_shows_only_its_header() {
        let body = (1..=10).map(|i| format!("line {i}")).collect::<Vec<_>>().join("\n");
        for mode in [ToolDisplayMode::Compact, ToolDisplayMode::Summary] {
            let rows = texts(
                &Block::Thinking {
                    text: body.clone(),
                    elapsed_ms: 1000,
                    tokens: None,
                    complete: true,
                    expanded: false,
                    done_at: None,
                }
                .render(80, mode),
            );

            assert_eq!(
                rows,
                vec![format!(" {} Thought for 1s", glyphs::FOLD_COLLAPSED)]
            );
        }
    }

    #[test]
    fn completed_expanded_thinking_has_a_continuous_rule() {
        let rows = Block::Thinking {
            text: "first paragraph with enough words to wrap\n\nsecond paragraph".into(),
            elapsed_ms: 1000,
            tokens: None,
            complete: true,
            expanded: true,
            done_at: None,
        }
        .render(24, ToolDisplayMode::Summary);

        assert_eq!(rows[0].text, format!(" {} Thought for 1s", glyphs::FOLD_EXPANDED));
        assert!(rows.len() > 4, "expected wrapping and a paragraph break: {rows:?}");
        assert!(rows[1..].iter().all(|row| row.text.starts_with("\u{2502} ")));
        assert!(rows.iter().all(|row| text::width(&row.text) <= 24));
        assert!(rows.iter().any(|row| row.text.trim_end() == "\u{2502}"));
    }

    #[test]
    fn thinking_headers_are_one_inset_marker_that_never_enters_a_copy() {
        // The same disclosure header a tool summary draws: exactly one left
        // space, one marker, no second icon, and the marker column excluded
        // from what a copy takes.
        for complete in [false, true] {
            for (body, expected) in [("", " "), ("reasoning", glyphs::FOLD_COLLAPSED)] {
                let rows = Block::Thinking {
                    text: body.into(),
                    elapsed_ms: 1000,
                    tokens: None,
                    complete,
                    expanded: false,
                    done_at: None,
                }
                .render(60, ToolDisplayMode::Summary);
                assert_eq!(
                    rows[0].text.chars().take(3).collect::<String>(),
                    format!(" {expected} "),
                    "{:?}",
                    rows[0].text
                );
                assert_eq!(rows[0].content_start(), MARKER_CELLS);
                assert!(
                    !rows[0].text.contains(Gutter::AgentActive.prefix().trim()),
                    "the header must not carry a second icon: {:?}",
                    rows[0].text
                );
            }
        }
    }

    #[test]
    fn copying_a_reasoning_block_takes_the_words_and_not_the_chevron() {
        // The header's marker column is decoration: it draws with the row and
        // pasting it into an editor is never what was meant.
        for complete in [false, true] {
            let rows = Block::Thinking {
                text: "the reasoning body".into(),
                elapsed_ms: 1000,
                tokens: None,
                complete,
                expanded: true,
                done_at: None,
            }
            .render(60, ToolDisplayMode::Summary);
            let copied = crate::selection::copy_visible_text(&rows, 0..rows.len());
            assert!(
                !copied.contains(glyphs::FOLD_EXPANDED) && !copied.contains(glyphs::FOLD_COLLAPSED),
                "the fold marker was pasted: {copied:?}"
            );
            assert!(copied.starts_with("Th"), "the headline was clipped: {copied:?}");
            assert!(copied.contains("the reasoning body"), "{copied:?}");
        }
    }

    #[test]
    fn a_narrow_terminal_still_draws_a_thinking_header_it_can_fit() {        for width in [1, 2, 3, 4, 10] {
            let rows = Block::Thinking {
                text: "reasoning".into(),
                elapsed_ms: 1000,
                tokens: None,
                complete: false,
                expanded: false,
                done_at: None,
            }
            .render(width, ToolDisplayMode::Summary);
            assert!(rows.iter().all(|row| text::width(&row.text) <= width), "{rows:?}");
        }
    }

    #[test]
    fn completed_thinking_omits_the_rule_when_no_text_would_fit() {
        for width in [1, 2, 3] {
            let rows = Block::Thinking {
                text: "reasoning".into(),
                elapsed_ms: 1000,
                tokens: None,
                complete: true,
                expanded: true,
                done_at: None,
            }
            .render(width, ToolDisplayMode::Summary);

            assert!(rows.iter().all(|row| text::width(&row.text) <= width));
            let body: String = rows.iter()
                .filter(|row| row.role == Role::ThinkingBody)
                .map(|row| row.text.trim_start_matches("\u{2502} "))
                .collect();
            assert_eq!(body, "reasoning");
        }
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
            expanded: false,
            done_at: None,
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

    #[test]
    fn a_diff_block_renders_file_path_and_change_lines() {
        let raw = "diff --git a/foo.rs b/foo.rs\n\
            --- a/foo.rs\n\
            +++ b/foo.rs\n\
            @@ -1,1 +1,1 @@\n\
            -old line\n\
            +new line\n";
        let rows = Block::Diff { raw: raw.to_string() }.render(80, ToolDisplayMode::Summary);
        assert!(
            rows.iter().any(|r| r.text.contains("foo.rs")),
            "expected filename in rows: {:?}",
            rows.iter().map(|r| &r.text).collect::<Vec<_>>()
        );
        assert!(rows.iter().any(|r| r.text.contains("old line")));
        assert!(rows.iter().any(|r| r.text.contains("new line")));
    }

    #[test]
    fn an_empty_diff_block_says_no_changes() {
        let rows = Block::Diff { raw: String::new() }.render(80, ToolDisplayMode::Summary);
        assert_eq!(rows.len(), 1);
        assert!(rows[0].text.contains("No changes."));
    }

    #[test]
    fn a_diff_block_with_unparseable_non_empty_content_renders_flat_lines() {
        // Input that has no recognisable diff structure (no @@ hunks) must not
        // show "No changes." — the content should still be visible.
        let raw = "-old line\n+new line\n";
        let rows = Block::Diff { raw: raw.to_string() }.render(80, ToolDisplayMode::Summary);
        assert!(!rows.is_empty(), "expected at least one row");
        assert!(
            rows.iter().any(|r| r.text.contains("old line") || r.text.contains("new line")),
            "raw content should be visible; rows: {:?}",
            rows.iter().map(|r| &r.text).collect::<Vec<_>>()
        );
        // Must not claim "no changes" when there IS content.
        assert!(!rows.iter().any(|r| r.text.contains("No changes.")));
    }

    #[test]
    fn a_diff_block_is_never_open() {
        assert!(!Block::Diff { raw: String::new() }.is_open());
    }

    #[test]
    fn render_with_block_starts_matches_render_rows() {
        let mut transcript = Transcript::new();
        transcript.push(user("hello"));
        transcript.push(Block::Assistant {
            text: "world".to_string(),
            complete: true,
        });
        let width = 80;
        let mode = ToolDisplayMode::Summary;

        let expected_rows = transcript.render(width, mode);
        let (rows, starts) = transcript.render_with_block_starts(width, mode);

        assert_eq!(rows.len(), expected_rows.len(), "row counts must match");
        // starts has one sentinel past the end
        assert_eq!(starts.len(), transcript.len() + 1);
    }

    #[test]
    fn block_starts_sentinel_equals_total_row_count() {
        let mut transcript = Transcript::new();
        transcript.push(user("a"));
        transcript.push(Block::Assistant { text: "b".to_string(), complete: true });
        let (rows, starts) = transcript.render_with_block_starts(80, ToolDisplayMode::Summary);
        assert_eq!(*starts.last().unwrap(), rows.len());
    }

    #[test]
    fn block_starts_are_strictly_increasing_for_non_empty_blocks() {
        let mut transcript = Transcript::new();
        for i in 0..5 {
            transcript.push(Block::Notice {
                text: format!("notice {i}"),
                level: NoticeLevel::Info,
            });
        }
        let (_, starts) = transcript.render_with_block_starts(80, ToolDisplayMode::Summary);
        let content_starts: Vec<usize> = starts[..5].to_vec();
        for window in content_starts.windows(2) {
            assert!(
                window[0] < window[1],
                "block starts must be strictly increasing: {content_starts:?}"
            );
        }
    }

    #[test]
    fn reasoning_with_no_body_offers_no_fold_and_claims_no_duration() {
        // Encrypted reasoning arrives as a signature with empty text, and the
        // clock only runs from the first delta -- so there are none and the
        // elapsed time is zero. Showing "Thought for 0s" would invent a
        // measurement, and a fold marker would promise a body that is not
        // there.
        let rows = texts(
            &Block::Thinking {
                text: String::new(),
                elapsed_ms: 0,
                tokens: None,
                complete: true,
                expanded: false,
                done_at: None,
            }
            .render(80, ToolDisplayMode::Summary),
        );

        assert_eq!(rows.len(), 1, "nothing to preview: {rows:?}");
        assert!(rows[0].contains("Thought"), "{rows:?}");
        assert!(!rows[0].contains("0s"), "invented a duration: {rows:?}");
        assert!(
            !rows[0].contains(glyphs::FOLD_COLLAPSED)
                && !rows[0].contains(glyphs::FOLD_EXPANDED),
            "offered a fold with nothing behind it: {rows:?}"
        );
    }

    #[test]
    fn a_bodyless_reasoning_block_cannot_be_folded() {
        // The click path must agree with what is drawn, or a click is
        // swallowed toggling a state nothing renders.
        let mut transcript = Transcript::new();
        transcript.push(Block::Thinking {
            text: "   \n\n  ".to_string(),
            elapsed_ms: 0,
            tokens: None,
            complete: true,
            expanded: false,
            done_at: None,
        });
        assert!(!transcript.is_foldable(0));

        transcript.push(Block::Thinking {
            text: "actual reasoning".to_string(),
            elapsed_ms: 1000,
            tokens: None,
            complete: true,
            expanded: false,
            done_at: None,
        });
        assert!(transcript.is_foldable(1));
    }

    // -- Cards presentation (C) ----------------------------------------------

    fn user_block(text: &str) -> Block {
        Block::User {
            text: text.to_string(),
            timestamp: String::new(),
            pending: false,
            queue_id: None,
        }
    }

    fn assistant_block(text: &str) -> Block {
        Block::Assistant { text: text.to_string(), complete: true }
    }

    #[test]
    fn plain_style_is_unchanged_by_the_shared_pass() {
        // Regression guard: the refactor into one shared pass must not
        // change a single row Plain already produced.
        let mut transcript = Transcript::new();
        transcript.push(user_block("hello"));
        transcript.push(assistant_block("hi there"));

        let via_render = transcript.render(80, ToolDisplayMode::Summary);
        let (via_starts, starts) = transcript.render_with_block_starts(80, ToolDisplayMode::Summary);
        let (via_styled, starts_styled) = transcript.render_with_block_starts_styled(
            80,
            ToolDisplayMode::Summary,
            TranscriptStyle::Plain,
        );

        assert_eq!(via_render, via_starts);
        assert_eq!(via_starts, via_styled);
        assert_eq!(starts, starts_styled);
        assert!(via_render.iter().all(|r| !r.is_chrome), "Plain must insert no chrome at all");
    }

    #[test]
    fn cards_style_opens_a_border_at_each_delivered_user_message() {
        let mut transcript = Transcript::new();
        transcript.push(user_block("first"));
        transcript.push(assistant_block("reply one"));
        transcript.push(user_block("second"));
        transcript.push(assistant_block("reply two"));

        let (rows, _) =
            transcript.render_with_block_starts_styled(80, ToolDisplayMode::Summary, TranscriptStyle::Cards);

        let chrome_count = rows.iter().filter(|r| r.is_chrome).count();
        // One opening border per card (2 user messages) plus one closing
        // border after the whole transcript's last card.
        assert_eq!(chrome_count, 3, "{rows:?}");
    }

    #[test]
    fn cards_style_keeps_banners_and_pre_first_user_content_outside_any_card() {
        let mut transcript = Transcript::new();
        transcript.push(Block::Banner { wordmark: vec!["CODA".into()], details: vec!["v0".into()] });
        transcript.push(Block::Notice { text: "connected".into(), level: NoticeLevel::Info });
        transcript.push(user_block("hello"));

        let (rows, starts) =
            transcript.render_with_block_starts_styled(80, ToolDisplayMode::Summary, TranscriptStyle::Cards);

        // No chrome before the single border that opens the first card.
        let user_start = starts[2];
        assert!(
            rows[..user_start - 1].iter().all(|r| !r.is_chrome),
            "a border must not appear before the first delivered user message: {rows:?}"
        );
        assert!(
            rows[user_start - 1].is_chrome,
            "expected exactly one border directly before the user message: {rows:?}"
        );
        // Exactly one border: opening the one card, plus its closing one.
        assert_eq!(rows.iter().filter(|r| r.is_chrome).count(), 2);
    }

    #[test]
    fn cards_style_block_starts_point_at_true_content_not_at_a_border() {
        let mut transcript = Transcript::new();
        transcript.push(user_block("hello"));
        transcript.push(assistant_block("hi"));

        let (rows, starts) =
            transcript.render_with_block_starts_styled(80, ToolDisplayMode::Summary, TranscriptStyle::Cards);

        for (i, &start) in starts.iter().enumerate().take(transcript.len()) {
            if let Some(row) = rows.get(start) {
                assert!(
                    !row.is_chrome,
                    "block {i}'s recorded start ({start}) points at a chrome row: {rows:?}"
                );
            }
        }
        // The very first row is the card's border, not the user block's own
        // content — its start must be the row right after it.
        assert!(rows[0].is_chrome);
        assert_eq!(starts[0], 1);
    }

    #[test]
    fn cards_style_reasoning_folds_still_work_exactly_as_in_plain_style() {
        // No whole-card collapse is required, and existing per-block folds
        // must be untouched: only chrome is added around blocks, nothing
        // about a block's own rows or fold state changes.
        let mut transcript = Transcript::new();
        transcript.push(user_block("hello"));
        transcript.push(Block::Thinking {
            text: "some reasoning here".into(),
            elapsed_ms: 1000,
            tokens: None,
            complete: true,
            expanded: false,
            done_at: None,
        });
        assert!(transcript.is_foldable(1));
        transcript.toggle_fold(1);
        assert!(matches!(transcript.blocks()[1], Block::Thinking { expanded: true, .. }));

        let (rows, _) =
            transcript.render_with_block_starts_styled(80, ToolDisplayMode::Summary, TranscriptStyle::Cards);
        assert!(rows.iter().any(|r| r.text.contains("some reasoning here")), "{rows:?}");
    }

    #[test]
    fn cards_style_chrome_rows_are_excluded_from_a_full_copy_including_cjk_content() {
        use crate::selection::copy_visible_text;

        let mut transcript = Transcript::new();
        transcript.push(user_block("こんにちは世界"));
        transcript.push(assistant_block("plain reply"));

        let (rows, _) =
            transcript.render_with_block_starts_styled(80, ToolDisplayMode::Summary, TranscriptStyle::Cards);
        let copied = copy_visible_text(&rows, 0..rows.len());

        assert!(!copied.contains(glyphs::RULE), "a card border leaked into the copy: {copied:?}");
        assert!(copied.contains("こんにちは世界"), "{copied:?}");
        assert!(copied.contains("plain reply"), "{copied:?}");
    }

    #[test]
    fn a_card_boundary_is_blank_at_every_width() {
        for width in [1usize, 10, 40, 120] {
            let mut transcript = Transcript::new();
            transcript.push(user_block("hi"));
            let (rows, _) = transcript.render_with_block_starts_styled(
                width,
                ToolDisplayMode::Summary,
                TranscriptStyle::Cards,
            );
            let boundary = rows.iter().find(|r| r.is_chrome).expect("a card boundary");
            assert!(boundary.text.is_empty());
        }
    }
}
