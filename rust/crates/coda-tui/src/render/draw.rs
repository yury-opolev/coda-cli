//! Drawing: turning state into terminal output.
//!
//! The screen is three stacked regions — transcript, composer, status — with an
//! optional scrollbar down the right edge and overlays drawn on top.

use std::time::Instant;

use coda_render::text;
use coda_render::theme::{Role, Theme};
use coda_render::RenderLine;
use ratatui::layout::{Alignment, Constraint, Direction, Layout, Rect};
use ratatui::style::Style;
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Borders, Clear, Padding, Paragraph, Wrap};
use ratatui::Frame;

use crate::composer::Composer;
use crate::state::UiState;
use crate::render::glyphs;
use crate::viewport::Viewport;

/// Width of the scrollbar column.
const SCROLLBAR_WIDTH: u16 = 1;

/// Most rows the completion popup may take.
///
/// Eight is enough to see the shape of the list without burying the
/// transcript it floats over.
const COMPLETION_MAX_ROWS: usize = 8;
/// Composer height bounds, in rows.
const COMPOSER_MIN_ROWS: u16 = 1;
const COMPOSER_MAX_ROWS: u16 = 10;

/// The regions the screen is divided into.
#[derive(Debug, Clone, Copy)]
pub struct Regions {
    /// Session identity, when there is room for it.
    pub header: Option<Rect>,
    pub transcript: Rect,
    pub scrollbar: Option<Rect>,
    /// One line for transient status, above the composer.
    pub hint: Option<Rect>,
    /// Bounded previews of pending/recoverable input, never transcript rows.
    pub pending: Option<Rect>,
    /// One line, directly above the composer, showing what a running turn
    /// is doing right now.
    ///
    /// Present only while a turn is busy. Outranks the decorative `header`
    /// and `hint` rows for the space it needs: a tiny terminal drops those
    /// first, because live progress is content and they are chrome.
    pub activity: Option<Rect>,
    pub composer: Rect,
    pub status: Rect,
}

/// Below this many rows the header and hint are dropped.
///
/// They are context; the transcript is the content. On a short terminal the
/// content wins — chrome degrades rather than squeezing the conversation into
/// nothing.
const MIN_ROWS_FOR_CHROME: u16 = 12;

/// Splits the frame into its regions.
///
/// The composer grows with its content up to a cap, after which it scrolls
/// internally rather than crowding out the transcript. `busy` reserves the
/// pinned activity row directly above the composer; it is independent of
/// `MIN_ROWS_FOR_CHROME` so live progress still shows on a terminal too short
/// for the decorative header and hint rows.
pub fn layout(area: Rect, composer_lines: usize, scrollable: bool, busy: bool) -> Regions {
    layout_with_pending(area, composer_lines, scrollable, busy, 0)
}

pub fn layout_with_pending(
    area: Rect, composer_lines: usize, scrollable: bool, busy: bool, pending_count: usize,
) -> Regions {
    let chrome = area.height >= MIN_ROWS_FOR_CHROME;
    // The header and hint rows, when present.
    let chrome_rows = if chrome { 2 } else { 0 } + if busy { 1 } else { 0 };
    let pending_rows = if pending_count == 0 {
        0
    } else {
        (pending_count.min(3) + usize::from(pending_count > 3)) as u16
    }.min(area.height.saturating_sub(COMPOSER_MIN_ROWS + 5 + chrome_rows));
    let extra = chrome_rows + pending_rows;

    let composer_rows = (composer_lines as u16)
        .clamp(COMPOSER_MIN_ROWS, COMPOSER_MAX_ROWS)
        // Leave at least three transcript rows however tall the composer is.
        // The composer costs its rows plus both half-block edges, the status
        // bar one more, and the chrome two (plus one live-activity row when
        // busy) when they are shown.
        .min(
            area.height
                .saturating_sub(6 + extra)
                .max(COMPOSER_MIN_ROWS),
        );

    let mut constraints: Vec<Constraint> = Vec::new();
    if chrome {
        constraints.push(Constraint::Length(1));
    }
    constraints.push(Constraint::Min(1));
    if pending_rows > 0 {
        constraints.push(Constraint::Length(pending_rows));
    }
    if busy {
        constraints.push(Constraint::Length(1));
    }
    if chrome {
        constraints.push(Constraint::Length(1));
    }
    // + the panel's top and bottom half-block edges
    constraints.push(Constraint::Length(composer_rows + 2));
    constraints.push(Constraint::Length(1));

    let chunks = Layout::default()
        .direction(Direction::Vertical)
        .constraints(constraints)
        .split(area);

    let mut next = 0usize;
    let mut take = || {
        let rect = chunks[next];
        next += 1;
        rect
    };
    let header = chrome.then(&mut take);
    let transcript_area = take();
    let pending = (pending_rows > 0).then(&mut take);
    let activity = busy.then(&mut take);
    let hint = chrome.then(&mut take);
    let composer = take();
    let status = take();

    let (transcript, scrollbar) = if scrollable && transcript_area.width > SCROLLBAR_WIDTH {
        let split = Layout::default()
            .direction(Direction::Horizontal)
            .constraints([Constraint::Min(1), Constraint::Length(SCROLLBAR_WIDTH)])
            .split(transcript_area);
        (split[0], Some(split[1]))
    } else {
        (transcript_area, None)
    };

    Regions {
        header,
        transcript,
        scrollbar,
        hint,
        pending,
        activity,
        composer,
        status,
    }
}

/// Converts one rendered row into a styled ratatui line.
///
/// Cell-coordinate spans from the renderer are mapped onto grapheme clusters so
/// a wide character is never split across two styles.
pub fn to_line(row: &RenderLine, theme: &Theme, width: usize) -> Line<'static> {
    let base = row_style(row, theme);
    let mut spans: Vec<Span<'static>> = Vec::new();

    if row.spans.is_empty() && row.prefix_role.is_none() {
        spans.push(Span::styled(row.text.clone(), base));
    } else {
        spans.extend(styled_runs(row, theme, base));
    }

    // Pad to the full width so a filled background reaches the right edge, and
    // reserve space for a right-aligned annotation when one fits.
    //
    // The transcript already narrows the first row to make room for a
    // timestamp, so if it does not fit the annotation is dropped rather than
    // truncating what the user actually wrote.
    let content_width: usize = spans.iter().map(|s| text::width(&s.content)).sum();
    let right = row.right_text.as_deref();
    let right_width = right.map_or(0, text::width);
    let show_right = right.is_some() && width >= content_width + right_width + 1;

    if row.fill_width || show_right {
        let target = width.saturating_sub(if show_right { right_width } else { 0 });
        if content_width < target {
            spans.push(Span::styled(" ".repeat(target - content_width), base));
        }
    }

    if show_right {
        spans.push(Span::styled(
            right.expect("checked above").to_string(),
            theme.style(Role::UserTime),
        ));
    }

    Line::from(spans)
}

/// Splits a row into runs of uniform style, walking cell by cell.
fn styled_runs(row: &RenderLine, theme: &Theme, base: Style) -> Vec<Span<'static>> {
    use unicode_segmentation::UnicodeSegmentation;

    let mut spans = Vec::new();
    let mut current = String::new();
    let mut current_style: Option<Style> = None;
    let mut cell = 0usize;

    for grapheme in row.text.graphemes(true) {
        let role = row.role_at(cell);
        let style = style_for(role, row, theme, base);

        match current_style {
            Some(previous) if previous == style => current.push_str(grapheme),
            Some(previous) => {
                spans.push(Span::styled(std::mem::take(&mut current), previous));
                current.push_str(grapheme);
                current_style = Some(style);
            }
            None => {
                current.push_str(grapheme);
                current_style = Some(style);
            }
        }
        cell += text::grapheme_width(grapheme);
    }

    if let Some(style) = current_style {
        spans.push(Span::styled(current, style));
    }
    spans
}

fn style_for(role: Role, row: &RenderLine, theme: &Theme, base: Style) -> Style {
    let mut style = theme.style(role);
    if let Some(background) = row.background {
        style = style.bg(theme.fg(background));
    } else if let Some(bg) = base.bg {
        style = style.bg(bg);
    }
    style
}

fn row_style(row: &RenderLine, theme: &Theme) -> Style {
    let mut style = theme.style(row.role);
    if let Some(background) = row.background {
        style = style.bg(theme.fg(background));
    }
    style
}

/// Draws the whole screen.
pub fn draw(
    frame: &mut Frame,
    state: &UiState,
    composer: &Composer,
    viewport: &Viewport,
    rows: &[RenderLine],
    theme: &Theme,
    now: Instant,
) {
    draw_with_pin(frame, state, composer, viewport, rows, theme, None, None, false, now);
}

/// Draws the whole screen, optionally showing a pin row at the top of the
/// transcript when the active user prompt has scrolled out of view.
///
/// The `pin_text` is pre-composed by `App` so that draw logic stays pure.
/// Draws the whole screen and reports where the transcript content ended up.
///
/// The returned `(top_row, height)` is what turns a mouse position into a
/// transcript row. Returning it from the draw keeps the two in step instead of
/// duplicating the layout arithmetic in the event handler, where it would
/// silently drift the first time the layout changed.
pub fn draw_with_pin(
    frame: &mut Frame,
    state: &UiState,
    composer: &Composer,
    viewport: &Viewport,
    rows: &[RenderLine],
    theme: &Theme,
    pin_text: Option<&str>,
    selection: Option<&crate::selection::TranscriptSelection>,
    header_id_selected: bool,
    now: Instant,
) -> (u16, u16) {
    let area = frame.area();
    frame.render_widget(
        Block::default().style(theme.surface()),
        area,
    );

    let regions = layout_with_pending(
        area, composer.line_count(), viewport.is_scrollable(), state.is_busy(),
        state.queued.len() + state.unsent.len(),
    );

    let content = draw_transcript_with_pin(
        frame,
        regions.transcript,
        viewport,
        rows,
        theme,
        pin_text,
        selection,
    );
    if let Some(header) = regions.header {
        draw_header(frame, header, state, theme, header_id_selected);
    }
    if let Some(scrollbar) = regions.scrollbar {
        draw_scrollbar(frame, scrollbar, viewport, theme);
    }
    if let Some(pending) = regions.pending {
        draw_pending(frame, pending, state, theme);
    }
    if let Some(activity) = regions.activity {
        draw_activity(frame, activity, state, theme, now);
    }
    if let Some(hint) = regions.hint {
        draw_hint(frame, hint, state, viewport, theme, now);
    }
    draw_composer(frame, regions.composer, composer, theme);
    // After the composer, so it floats above it rather than under it.
    draw_completions(frame, regions.composer, composer, theme);
    draw_status(frame, regions.status, state, viewport, theme);


    // Prompts are surfaces now, drawn by the stack after this returns. Their
    // Exclusive modality is what puts them above a browser, rather than the
    // order of these calls.

    content
}

fn draw_pending(frame: &mut Frame, area: Rect, state: &UiState, theme: &Theme) {
    let entries: Vec<_> = state.queued.iter().map(|message| ("pending", &message.text))
        .chain(state.unsent.iter().map(|message| ("not sent", &message.text)))
        .collect();
    let preview_count = if entries.len() > area.height as usize && area.height > 1 {
        area.height as usize - 1
    } else {
        area.height as usize
    };
    let mut lines: Vec<_> = entries.iter().take(preview_count).map(|(status, message)| {
        let preview = text::sanitize(message).split_whitespace().collect::<Vec<_>>().join(" ");
        Line::from(Span::styled(
            text::truncate_with_ellipsis(&format!("[{status}] {preview}"), area.width as usize),
            theme.style(Role::PendingUser),
        ))
    }).collect();
    let remaining = entries.len().saturating_sub(preview_count);
    if remaining > 0 && lines.len() < area.height as usize {
        lines.push(Line::from(Span::styled(
            text::truncate_with_ellipsis(&format!("+{remaining} more"), area.width as usize),
            theme.style(Role::Notification),
        )));
    }
    frame.render_widget(Paragraph::new(lines), area);
}

/// Abbreviates a token count so the status bar stays one line.
///
/// Thousands are what these numbers are read in; the exact digit is noise
/// beside knowing whether it is 8k or 800k.
fn compact_count(tokens: i64) -> String {
    match tokens {
        n if n >= 1_000_000 => format!("{:.1}M", n as f64 / 1_000_000.0),
        n if n >= 10_000 => format!("{}k", n / 1_000),
        n if n >= 1_000 => format!("{:.1}k", n as f64 / 1_000.0),
        n => n.to_string(),
    }
}

/// Draws the identity line: what this is, and which session.
fn draw_header(frame: &mut Frame, area: Rect, state: &UiState, theme: &Theme, id_selected: bool) {
    let mut text = format!(" coda {}", crate::branding::version());
    if let Some(id) = &state.session_id {
        text.push_str(&format!("  {}  session {id}", glyphs::RULE_VERTICAL));
    }
    let line = Line::from(Span::styled(text, theme.style(Role::Notification)));
    let line = if id_selected {
        match header_id_rect(area, state) {
            Some(rect) => {
                let start = (rect.x - area.x) as usize;
                let end = start + rect.width as usize;
                highlight_span(line, start, end, theme)
            }
            None => line,
        }
    } else {
        line
    };
    frame.render_widget(Paragraph::new(line), area);
}

/// The screen rectangle the session id occupies within the header, or `None`
/// when there is no session id or no room to show it.
///
/// Shared by drawing (to highlight a selection) and by pointer hit-testing (to
/// know whether a click landed on the id), so the two positions can never
/// silently drift apart. Pure and terminal-free: `App` calls it with the
/// header rect from `layout()` alone, without needing an actual frame.
pub fn header_id_rect(area: Rect, state: &UiState) -> Option<Rect> {
    if area.width == 0 || area.height == 0 {
        return None;
    }
    let id = state.session_id.as_deref()?;
    let prefix = format!(
        " coda {}  {}  session ",
        crate::branding::version(),
        glyphs::RULE_VERTICAL
    );
    let prefix_width = text::width(&prefix);
    if prefix_width >= area.width as usize {
        // The header itself is too narrow to have reached the id at all.
        return None;
    }
    let available = area.width as usize - prefix_width;
    let id_width = text::width(id).min(available);
    if id_width == 0 {
        return None;
    }
    Some(Rect::new(
        area.x + prefix_width as u16,
        area.y,
        id_width as u16,
        1,
    ))
}

/// Draws the pinned activity row: what a running turn is doing, and for how
/// long, directly above the composer.
///
/// Composed fresh every frame from `state` and `now` rather than cached, so
/// its elapsed time can advance on the existing spinner/timer wakeup without
/// forcing a full transcript relayout — the one thing a plain per-second tick
/// must never cost.
fn draw_activity(frame: &mut Frame, area: Rect, state: &UiState, theme: &Theme, now: Instant) {
    let Some(text) = compose_activity(state, now) else {
        return;
    };
    frame.render_widget(
        Paragraph::new(Line::from(Span::styled(text, theme.style(state.activity.role())))),
        area,
    );
}

/// Builds the pinned activity row's text, or `None` when nothing is running.
///
/// The one spinner: the status bar no longer animates, so this is the only
/// place a running turn's motion is shown. Reasoning time and last-response
/// tokens are appended only once actually known — an unstarted reasoning
/// segment or an unreported response never gets a guessed number.
pub fn compose_activity(state: &UiState, now: Instant) -> Option<String> {
    if !state.is_busy() {
        return None;
    }
    let progress = state.turn_progress.as_ref()?;
    let spinner = if state.activity.is_animated() {
        format!("{} ", glyphs::SPINNER[state.spinner % glyphs::SPINNER.len()])
    } else {
        String::new()
    };
    let elapsed = crate::transcript::format_duration(progress.elapsed_ms(now) / 1000);
    let mut text = format!("{spinner}{} {elapsed}", progress.phase().label());

    if let Some(reasoning_ms) = progress.reasoning_ms(now) {
        if reasoning_ms > 0 {
            text.push_str(&format!(
                " {} reasoned {}",
                glyphs::RULE_VERTICAL,
                crate::transcript::format_duration(reasoning_ms / 1000)
            ));
        }
    }

    if let Some((_, output_tokens)) = progress.last_response_tokens() {
        text.push_str(&format!(
            " {} last response {output_tokens} tok out",
            glyphs::RULE_VERTICAL
        ));
    }

    if !state.queued.is_empty() {
        let n = state.queued.len();
        text.push_str(&format!(" {} {n} queued", glyphs::RULE_VERTICAL));
    }

    Some(text)
}

/// Draws the transient status line above the composer.
///
/// Always reserved, even when empty: a line that appears and disappears
/// reflows the transcript underneath it, so the conversation would jump every
/// time something was copied.
///
/// Centred, because it belongs to the whole screen rather than to the column
/// of text beneath it, and a short message pinned left reads as debris.
///
/// Being scrolled away outranks a passing notice, and shows the way back for
/// as long as it applies — not only while something new is arriving. A reader
/// who has stopped following has no other indication that the view is frozen,
/// and a stale "copied" message must not hide it. Armed cancellation/exit
/// chords take precedence over both so their confirmation is always visible.
fn draw_hint(frame: &mut Frame, area: Rect, state: &UiState, viewport: &Viewport, theme: &Theme, now: Instant) {
    let (text, role) = if let Some(chord) = state.hints.current_chord(now) {
        (chord.to_string(), Role::Notification)
    } else if !viewport.is_following() {
        let catch_up = "Ctrl+End to catch up";
        // Lines, not messages. The count is rows of rendered transcript, and
        // calling five rows "5 new" reads as five messages — which is how a
        // single command's output came to look like a conversation the reader
        // had missed.
        let text = match viewport.unread() {
            0 => catch_up.to_string(),
            1 => format!("1 new line below {} {catch_up}", glyphs::RULE_VERTICAL),
            unread => format!("{unread} new lines below {} {catch_up}", glyphs::RULE_VERTICAL),
        };
        (text, Role::PendingUser)
    } else {
        match state.hints.current(now) {
            Some(hint) => (hint.to_string(), Role::Notification),
            None => (String::new(), Role::Notification),
        }
    };
    frame.render_widget(
        Paragraph::new(Line::from(Span::styled(text, theme.style(role))))
            .alignment(Alignment::Center),
        area,
    );
}

/// Draws the completion popup just above the composer.
///
/// Floated over the transcript rather than given its own layout row, so the
/// transcript does not reflow on every keystroke as candidates appear and
/// disappear.
///
/// This is drawn by the shell rather than as a `Surface` because completions
/// are not modal: the composer keeps the keyboard while they are shown, and a
/// surface owns it. The popup is a hint over the shell, not a layer above it.
fn draw_completions(frame: &mut Frame, below: Rect, composer: &Composer, theme: &Theme) {
    let completion = composer.completion();
    if !completion.is_active() || below.y == 0 {
        return;
    }

    // At most eight rows, and never more than the space above the composer.
    let rows = completion
        .candidates
        .len()
        .min(COMPLETION_MAX_ROWS)
        .min(below.y as usize) as u16;
    if rows == 0 {
        return;
    }

    let width = below.width;
    let area = Rect::new(below.x, below.y - rows, width, rows);
    frame.render_widget(Clear, area);

    // Scrolled so the selection stays visible once the list is longer than
    // the popup: otherwise pressing Down past the eighth entry moves a
    // highlight nobody can see.
    let first = completion
        .selected
        .saturating_sub(rows.saturating_sub(1) as usize);

    let lines: Vec<Line> = completion
        .candidates
        .iter()
        .enumerate()
        .skip(first)
        .take(rows as usize)
        .map(|(index, candidate)| {
            let selected = index == completion.selected;
            let style = if selected {
                theme
                    .style(Role::CompletionSelectedText)
                    .bg(theme.fg(Role::CompletionSelectedBackground))
            } else {
                theme.style(Role::CompletionNormal)
            };
            let marker = if selected {
                glyphs::OPTION_MARKER
            } else {
                glyphs::OPTION_BLANK
            };
            let label = match &candidate.description {
                Some(detail) => format!("{marker}{:<18} {detail}", candidate.label),
                None => format!("{marker}{}", candidate.label),
            };
            Line::from(Span::styled(
                text::truncate(&text::sanitize(&label), width as usize),
                style,
            ))
        })
        .collect();

    frame.render_widget(
        Paragraph::new(lines).style(Style::default().bg(theme.fg(Role::ComposerPanelBackground))),
        area,
    );
}

fn draw_transcript_with_pin(
    frame: &mut Frame,
    area: Rect,
    viewport: &Viewport,
    rows: &[RenderLine],
    theme: &Theme,
    pin_text: Option<&str>,
    selection: Option<&crate::selection::TranscriptSelection>,
) -> (u16, u16) {
    let width = area.width as usize;

    // When a pin is active, it occupies the top row of the transcript area and
    // the remaining rows show one fewer scroll line.
    let (pin_area, content_area) = if pin_text.is_some() && area.height > 1 {
        let top = Rect::new(area.x, area.y, area.width, 1);
        let rest = Rect::new(area.x, area.y + 1, area.width, area.height - 1);
        (Some(top), rest)
    } else {
        (None, area)
    };

    // Draw the pin row.
    if let (Some(pin_area), Some(text)) = (pin_area, pin_text) {
        let style = theme.style(Role::User);
        let line = Line::from(Span::styled(text.to_string(), style));
        frame.render_widget(Paragraph::new(vec![line]), pin_area);
    }

    // Draw the transcript rows.
    let content_height = content_area.height as usize;
    let visible = viewport.visible_range();
    // Clamp to what the content area can show.
    let take = visible.len().min(content_height);
    let lines: Vec<Line> = rows
        .get(visible.clone())
        .unwrap_or(&[])
        .iter()
        .take(take)
        .enumerate()
        .map(|(offset, row)| {
            let line = to_line(row, theme, width);
            // Highlight the selected span of this row, if any. Done here
            // rather than in `to_line` so an unselected transcript costs
            // nothing extra.
            match selection.and_then(|s| s.range_for_row(visible.start + offset, width)) {
                Some((start, end)) if end > start => highlight_span(line, start, end, theme),
                _ => line,
            }
        })
        .collect();

    frame.render_widget(Paragraph::new(lines).style(theme.surface()), content_area);

    (content_area.y, content_area.height)
}

/// Re-styles the cells in `[start, end)` to read as selected.
///
/// Reverses the existing style rather than imposing a fixed colour, so the
/// highlight works on both the light and dark themes without either needing to
/// know about it.
fn highlight_span(line: Line<'static>, start: usize, end: usize, _theme: &Theme) -> Line<'static> {
    use ratatui::style::Modifier;

    let mut out: Vec<Span<'static>> = Vec::new();
    let mut cell = 0usize;

    for span in line.spans {
        let text = span.content.to_string();
        let width = coda_render::text::width(&text);
        let span_start = cell;
        let span_end = cell + width;
        cell = span_end;

        // Entirely outside the selection.
        if span_end <= start || span_start >= end {
            out.push(Span::styled(text, span.style));
            continue;
        }

        // Split the span at the selection boundaries, measured in cells so a
        // wide character is never cut in half.
        let before = crate::selection::slice_by_cells(&text, 0, start.saturating_sub(span_start));
        let mid = crate::selection::slice_by_cells(
            &text,
            start.saturating_sub(span_start),
            end.saturating_sub(span_start),
        );
        let after = crate::selection::slice_by_cells(&text, end.saturating_sub(span_start), width);

        if !before.is_empty() {
            out.push(Span::styled(before, span.style));
        }
        if !mid.is_empty() {
            out.push(Span::styled(mid, span.style.add_modifier(Modifier::REVERSED)));
        }
        if !after.is_empty() {
            out.push(Span::styled(after, span.style));
        }
    }

    Line::from(out)
}

/// `draw_transcript` kept for tests that call it directly.
pub fn draw_transcript(
    frame: &mut Frame,
    area: Rect,
    viewport: &Viewport,
    rows: &[RenderLine],
    theme: &Theme,
) {
    draw_transcript_with_pin(frame, area, viewport, rows, theme, None, None);
}

fn draw_scrollbar(frame: &mut Frame, area: Rect, viewport: &Viewport, theme: &Theme) {
    let track = area.height as usize;
    let Some((position, size)) = viewport.thumb(track) else {
        return;
    };

    let track_style = theme.style(Role::ScrollbarTrack);
    let thumb_style = theme.style(Role::ScrollbarThumb);

    let lines: Vec<Line> = (0..track)
        .map(|row| {
            let inside = row >= position && row < position + size;
            let (glyph, style) = if inside {
                (glyphs::BLOCK, thumb_style) // █
            } else {
                (glyphs::RULE_VERTICAL, track_style) // │
            };
            Line::from(Span::styled(glyph, style))
        })
        .collect();

    frame.render_widget(Paragraph::new(lines), area);
}

/// The composer's top edge is a *lower* half block: the cell's upper half keeps
/// the shell background and its lower half carries the panel colour, so the
/// panel appears to begin half a row above its first content row rather than
/// starting abruptly on a cell boundary.
const TOP_EDGE_GLYPH: &str = glyphs::COMPOSER_TOP;

/// Mirrors [`TOP_EDGE_GLYPH`] with an *upper* half block, so the panel appears
/// to end half a row below its last content row.
const BOTTOM_EDGE_GLYPH: &str = glyphs::COMPOSER_BOTTOM;

/// Column where composer text starts, past the padded prompt marker.
///
/// The cursor is placed from this, so it must track the marker's width or the
/// caret drifts away from the text it is meant to sit in.
pub const COMPOSER_TEXT_COLUMN: u16 = 3;

/// Breathing room between a modal's border and its contents.
///
/// Horizontal only: vertical padding would cost rows that modals — which are
/// already capped to a fraction of the screen — cannot spare.
pub const MODAL_PADDING: Padding = Padding::horizontal(1);

fn draw_composer(
    frame: &mut Frame,
    area: Rect,
    composer: &Composer,
    theme: &Theme,
) {
    if area.height == 0 || area.width == 0 {
        return;
    }

    // The panel body. The edges are drawn over the first and last rows below.
    frame.render_widget(
        Block::default().style(Style::default().bg(theme.fg(Role::ComposerPanelBackground))),
        area,
    );

    // The edge rows are painted against the SHELL background, not the panel
    // background: the half block then reads as the panel bleeding half a row
    // outward, rather than as a lighter rim floating inside the panel.
    let edge_style = Style::default()
        .fg(theme.fg(Role::ComposerPanelEdge))
        .bg(theme.fg(Role::Background));
    let width = area.width as usize;
    frame.render_widget(
        Paragraph::new(Line::from(Span::styled(
            TOP_EDGE_GLYPH.repeat(width),
            edge_style,
        ))),
        Rect::new(area.x, area.y, area.width, 1),
    );
    if area.height > 1 {
        frame.render_widget(
            Paragraph::new(Line::from(Span::styled(
                BOTTOM_EDGE_GLYPH.repeat(width),
                edge_style,
            ))),
            Rect::new(area.x, area.bottom() - 1, area.width, 1),
        );
    }

    let inner = Rect::new(
        area.x,
        area.y + 1,
        area.width,
        area.height.saturating_sub(2),
    );
    if inner.height == 0 {
        return;
    }

    let prompt_style = theme.style(Role::ComposerPrompt);
    let text_style = theme.style(Role::ComposerText);

    let lines: Vec<Line> = composer
        .lines()
        .enumerate()
        .map(|(index, line)| {
            // A leading space keeps the glyph off the terminal edge; the
            // continuation indent matches its width so wrapped lines align
            // under the first one's text.
            //
            // The marker does not change while a turn runs. Swapping it for an
            // ellipsis marked the one region inviting input as though it were
            // disabled, when typing there queues the next message. Progress is
            // the status bar's job.
            let marker = if index == 0 {
                glyphs::PROMPT_PADDED
            } else {
                glyphs::PROMPT_CONTINUATION
            };
            Line::from(vec![
                Span::styled(marker, prompt_style),
                Span::styled(line.to_string(), text_style),
            ])
        })
        .collect();

    frame.render_widget(Paragraph::new(lines), inner);

    // Place the hardware cursor so the terminal draws it for us.
    let (line, column) = composer.cursor_position();
    let x = inner.x + COMPOSER_TEXT_COLUMN + column as u16;
    let y = inner.y + line as u16;
    if x < inner.right() && y < inner.bottom() {
        frame.set_cursor_position((x, y));
    }
}

fn draw_status(
    frame: &mut Frame,
    area: Rect,
    state: &UiState,
    viewport: &Viewport,
    theme: &Theme,
) {
    frame.render_widget(
        Paragraph::new(Line::from(status_spans(state, viewport, theme))).style(theme.surface()),
        area,
    );
}

/// Builds the status line's spans.
///
/// Pure and terminal-free so what the status bar *claims* — connected or not,
/// and about which model — can be asserted without a frame.
pub fn status_spans(
    state: &UiState,
    viewport: &Viewport,
    theme: &Theme,
) -> Vec<Span<'static>> {
    // Static only: the pinned activity row above the composer owns the one
    // animated spinner now, so this never claims motion a second time in a
    // second place.
    let mut spans = vec![Span::styled(
        format!(" {} ", state.activity.label()),
        theme.style(state.activity.role()),
    )];

    if let Some(model) = &state.model {
        // Labelled, not dropped: which model the session was on is worth
        // keeping after a disconnection, but printing it bare next to a status
        // reads as the model of a live connection.
        let text = if state.activity.is_connected() {
            format!("{} {model} ", glyphs::RULE_VERTICAL)
        } else {
            format!("{} last model {model} ", glyphs::RULE_VERTICAL)
        };
        spans.push(Span::styled(text, theme.style(Role::Notification)));
    }

    if let Some(effort) = &state.effort {
        spans.push(Span::styled(
            format!("{} effort {effort} ", glyphs::RULE_VERTICAL),
            theme.style(Role::Notification),
        ));
    }

    if let Some(percent) = state.usage.percent_used() {
        spans.push(Span::styled(
            format!("{} context {percent}% ", glyphs::RULE_VERTICAL),
            theme.style(Role::Notification),
        ));
    }

    // Tokens in and out. The direction matters: a long context re-sent every
    // turn reads very differently from a long reply, and a single total hides
    // which one is growing.
    if state.usage.input_tokens > 0 || state.usage.output_tokens > 0 {
        spans.push(Span::styled(
            format!(
                "{} {} {} in {} {} out ",
                glyphs::RULE_VERTICAL,
                glyphs::ARROW_DOWN,
                compact_count(state.usage.input_tokens),
                glyphs::ARROW_UP,
                compact_count(state.usage.output_tokens),
            ),
            theme.style(Role::Notification),
        ));
        // Only when the catalogue prices this model. Showing "$0.00" for an
        // unpriced one would read as free.
        if let Some(cost) = state.usage.estimated_cost() {
            spans.push(Span::styled(
                format!("{} ${cost:.2} ", glyphs::RULE_VERTICAL),
                theme.style(Role::Notification),
            ));
        }
    }

    if state.interrupting {
        spans.push(Span::styled(
            format!("{} interrupting… ", glyphs::RULE_VERTICAL),
            theme.style(Role::Warning),
        ));
    }

    if !state.queued.is_empty() {
        spans.push(Span::styled(
            format!("{} {} queued ", glyphs::RULE_VERTICAL, state.queued.len()),
            theme.style(Role::PendingUser),
        ));
    }

    // The jump-to-bottom hint only matters when content arrived unseen.
    if viewport.unread() > 0 {
        spans.push(Span::styled(
            format!("{} {} new {} Ctrl+End ", glyphs::RULE_VERTICAL, viewport.unread(), glyphs::ARROW_DOWN),
            theme.style(Role::PromptAccent),
        ));
    }

    spans
}

/// Draws one surface: chrome, its pre-rendered lines, its hints and its caret.
///
/// The surface has already scrolled and clipped its own content, so this is
/// pure placement. Keeping the two apart is what lets a surface be tested
/// without a terminal.
pub fn draw_surface(
    frame: &mut Frame,
    rendered: &crate::surface::stack::RenderedSurface,
    theme: &Theme,
) {
    let region = rendered.region;
    if region.width == 0 || region.height == 0 {
        return;
    }
    frame.render_widget(Clear, region);

    if crate::surface::chrome::is_bordered(rendered.placement) {
        let block = Block::default()
            .title(format!(" {} ", rendered.title))
            .borders(Borders::ALL)
            .border_style(theme.style(Role::PromptAccent))
            .padding(MODAL_PADDING)
            .style(theme.surface());
        frame.render_widget(block, region);
    }

    // Geometry comes from the same helper the stack used, so the surface is
    // drawn into exactly the area it scrolled itself against.
    frame.render_widget(Paragraph::new(rendered.lines.clone()), rendered.content);

    let footer =
        crate::surface::chrome::footer(region, &rendered.hints, rendered.placement);
    if footer.height > 0 {
        // Wrapped, not truncated. A hint line that runs past the border loses
        // whatever is on the right — which is where "Esc: cancel" sits, the
        // one hint a stuck user most needs.
        frame.render_widget(
            Paragraph::new(rendered.hints.clone())
                .style(theme.style(Role::Notification))
                .wrap(Wrap { trim: true }),
            footer,
        );
    }

    if let Some((x, y)) = rendered.cursor {
        if x < rendered.content.right() && y < rendered.content.bottom() {
            frame.set_cursor_position((x, y));
        }
    }
}






#[cfg(test)]
mod tests {
    use super::*;
    use coda_render::theme::ColorDepth;
    use coda_render::{Gutter, Span as RenderSpan};
    use crate::state::UiEvent;
    use ratatui::style::Modifier;

    /// The status line as plain text, which is what the user actually reads.
    fn status_text(state: &UiState) -> String {
        status_spans(state, &Viewport::new(), &Theme::default())
            .into_iter()
            .map(|span| span.content.into_owned())
            .collect()
    }

    #[test]
    fn a_disconnected_session_says_so_and_labels_the_model_it_used_to_have() {
        // "ready" in green next to a model name is a claim about an engine.
        // After a sign-out — or a replacement that did not start — there is no
        // engine, and the conversation is kept precisely so the user can see
        // what it *was*. Saying "ready" there invited a message that could not
        // be sent, and the model shown belonged to a session that no longer
        // existed.
        let mut state = UiState::new();
        state.apply(UiEvent::Connected { session_id: "s1".into() });
        state.apply(UiEvent::ModelChanged {
            id: "claude-sonnet-4-5".into(),
            context_limit: None,
        });
        let connected = status_text(&state);
        assert!(connected.contains("ready"), "{connected}");
        assert!(connected.contains("claude-sonnet-4-5"), "{connected}");
        assert!(!connected.contains("last model"), "{connected}");

        state.apply(UiEvent::EngineDisconnected);
        let disconnected = status_text(&state);
        assert!(disconnected.contains("disconnected"), "{disconnected}");
        assert!(!disconnected.contains("ready"), "a signed-out session claimed to be ready");
        assert!(
            disconnected.contains("last model claude-sonnet-4-5"),
            "the previous model was shown as though it were live: {disconnected}"
        );
        // And nothing may claim to be running.
        assert!(!state.is_busy(), "a disconnected session reported a turn in flight");
        assert!(!state.activity.is_animated(), "a disconnected session spun a spinner");
        assert_eq!(compose_activity(&state, Instant::now()), None);

        // Adopting a replacement is what puts it back.
        state.apply(UiEvent::EngineAdopted);
        let again = status_text(&state);
        assert!(again.contains("ready"), "{again}");
        assert!(!again.contains("last model"), "{again}");
    }

    #[test]
    fn a_disconnection_keeps_the_conversation_and_the_session_it_belonged_to() {
        // The status is the only thing that changes: nothing about a
        // disconnection is a reason to lose what was said or which session it
        // was said in.
        let mut state = UiState::new();
        state.apply(UiEvent::Connected { session_id: "s1".into() });
        state.apply(UiEvent::Submitted { text: "keep me".into() });
        let before = state.transcript.blocks().len();

        state.apply(UiEvent::EngineDisconnected);

        assert_eq!(state.transcript.blocks().len(), before, "the conversation was lost");
        assert_eq!(state.session_id.as_deref(), Some("s1"));
        assert_eq!(state.model, None);
    }


    fn theme() -> Theme {
        Theme::warm_ember().with_depth(ColorDepth::TrueColor)
    }

    fn area(width: u16, height: u16) -> Rect {
        Rect::new(0, 0, width, height)
    }

    fn plain_text(line: &Line) -> String {
        line.spans.iter().map(|s| s.content.as_ref()).collect()
    }

    #[test]
    fn layout_reserves_rows_for_the_chrome_composer_and_status() {
        let regions = layout(area(80, 24), 1, false, false);
        assert_eq!(regions.header.expect("header").height, 1);
        assert_eq!(regions.hint.expect("hint").height, 1);
        assert_eq!(regions.status.height, 1);
        assert_eq!(regions.composer.height, 3); // one row plus both edges
        assert_eq!(regions.transcript.height, 18);
    }

    #[test]
    fn the_composer_grows_with_its_content() {
        let regions = layout(area(80, 24), 5, false, false);
        assert_eq!(regions.composer.height, 7);
        assert_eq!(regions.transcript.height, 14);
    }

    #[test]
    fn a_short_terminal_drops_the_chrome_rather_than_the_conversation() {
        // Header and hint are context; the transcript is the content. On a
        // short terminal they go, rather than squeezing the conversation into
        // nothing to keep decoration.
        let regions = layout(area(80, MIN_ROWS_FOR_CHROME - 1), 1, false, false);
        assert!(regions.header.is_none());
        assert!(regions.hint.is_none());
        assert!(regions.transcript.height >= 3, "the transcript was starved");
        assert_eq!(regions.status.height, 1, "the status bar must survive");
    }

    #[test]
    fn the_hint_line_is_reserved_even_with_nothing_to_say() {
        // A line that comes and goes reflows the transcript under it, so the
        // conversation would jump every time something was copied.
        let quiet = layout(area(80, 24), 1, false, false);
        let busy = layout(area(80, 24), 1, false, false);
        assert_eq!(
            quiet.transcript.height, busy.transcript.height,
            "the transcript height depends on what the hint line says"
        );
        assert_eq!(quiet.hint.expect("hint").height, 1);
    }

    #[test]
    fn chord_hint_overrides_scroll_guidance_until_expiry() {
        let now = Instant::now();
        let ttl = std::time::Duration::from_millis(1500);
        let mut state = UiState::new();
        state.hints.push_transient("Copied text", now);
        state.hints.push_chord("Press Ctrl+C again to exit.", ttl, now);
        let mut viewport = Viewport::new();
        viewport.update(100, 10);
        viewport.scroll_up(5);
        let mut terminal = ratatui::Terminal::new(
            ratatui::backend::TestBackend::new(80, 1),
        ).unwrap();
        for (at, expected) in [
            (now, "Press Ctrl+C again to exit."),
            (now + ttl, "Ctrl+End to catch up"),
        ] {
            terminal.draw(|frame| {
                draw_hint(frame, frame.area(), &state, &viewport, &theme(), at);
            }).unwrap();
            let rendered: String = terminal.backend().buffer().content()
                .iter().map(|cell| cell.symbol()).collect();
            assert!(rendered.contains(expected), "{rendered}");
            assert!(!rendered.contains("Copied text"));
        }
    }

    #[test]
    fn the_composer_stops_growing_at_its_cap() {
        let regions = layout(area(80, 40), 50, false, false);
        assert_eq!(regions.composer.height, COMPOSER_MAX_ROWS + 2);
    }

    #[test]
    fn the_composer_never_starves_the_transcript() {
        let regions = layout(area(80, 8), 50, false, false);
        assert!(regions.transcript.height >= 3, "transcript was squeezed out");
    }

    #[test]
    fn a_scrollbar_column_is_reserved_only_when_scrollable() {
        assert!(layout(area(80, 24), 1, false, false).scrollbar.is_none());

        let regions = layout(area(80, 24), 1, true, false);
        let scrollbar = regions.scrollbar.expect("a scrollbar");
        assert_eq!(scrollbar.width, SCROLLBAR_WIDTH);
        assert_eq!(regions.transcript.width, 79);
    }

    #[test]
    fn a_very_narrow_frame_drops_the_scrollbar() {
        let regions = layout(area(1, 24), 1, true, false);
        assert!(regions.scrollbar.is_none());
    }

    #[test]
    fn a_plain_row_becomes_a_single_styled_span() {
        let row = RenderLine::new("hello", Role::Assistant);
        let line = to_line(&row, &theme(), 20);

        assert_eq!(line.spans.len(), 1);
        assert_eq!(plain_text(&line), "hello");
        assert_eq!(line.spans[0].style.fg, Some(theme().fg(Role::Assistant)));
    }

    #[test]
    fn spans_split_the_row_into_styled_runs() {
        let row = RenderLine::new("let x", Role::Code)
            .with_spans(vec![RenderSpan::new(0, 3, Role::SyntaxKeyword)]);
        let line = to_line(&row, &theme(), 20);

        assert_eq!(plain_text(&line), "let x");
        assert_eq!(line.spans[0].content, "let");
        assert_eq!(line.spans[0].style.fg, Some(theme().fg(Role::SyntaxKeyword)));
        assert_eq!(line.spans[1].content, " x");
    }

    #[test]
    fn a_prefix_is_styled_ahead_of_spans() {
        let row = RenderLine::new("  1 + added", Role::DiffAdded)
            .with_prefix(4, Role::DiffContext)
            .with_spans(vec![RenderSpan::new(0, 11, Role::SyntaxString)]);
        let line = to_line(&row, &theme(), 20);

        assert_eq!(line.spans[0].style.fg, Some(theme().fg(Role::DiffContext)));
        assert_eq!(line.spans[0].content, "  1 ");
    }

    #[test]
    fn a_filled_row_is_padded_to_the_full_width() {
        let row = RenderLine::new("hi", Role::User).with_fill(Role::UserBackground);
        let line = to_line(&row, &theme(), 10);

        assert_eq!(text::width(&plain_text(&line)), 10);
        assert_eq!(
            line.spans.last().unwrap().style.bg,
            Some(theme().fg(Role::UserBackground))
        );
    }

    #[test]
    fn an_unfilled_row_is_not_padded() {
        let row = RenderLine::new("hi", Role::Assistant);
        let line = to_line(&row, &theme(), 10);
        assert_eq!(plain_text(&line), "hi");
    }

    #[test]
    fn a_timestamp_is_placed_at_the_right_edge() {
        let row = RenderLine::new("hello", Role::User)
            .with_fill(Role::UserBackground)
            .with_right_text("09:41");
        let line = to_line(&row, &theme(), 20);

        let rendered = plain_text(&line);
        assert!(rendered.ends_with("09:41"), "got {rendered:?}");
        assert!(rendered.starts_with("hello"));
        assert_eq!(text::width(&rendered), 20);
    }

    #[test]
    fn a_timestamp_is_dropped_when_there_is_no_room() {
        let row = RenderLine::new("a fairly long message", Role::User)
            .with_fill(Role::UserBackground)
            .with_right_text("09:41");
        let line = to_line(&row, &theme(), 10);
        assert!(!plain_text(&line).contains("09:41"));
    }

    #[test]
    fn wide_characters_are_not_split_across_styles() {
        // A span boundary mid-way through a two-cell character must not slice it.
        let row = RenderLine::new("日本語", Role::Code)
            .with_spans(vec![RenderSpan::new(0, 3, Role::SyntaxKeyword)]);
        let line = to_line(&row, &theme(), 20);

        assert_eq!(plain_text(&line), "日本語");
        for span in &line.spans {
            assert!(span.content.chars().count() > 0);
        }
    }

    #[test]
    fn completed_thinking_styles_survive_terminal_conversion() {
        let theme = Theme::default();
        let rows = crate::transcript::Block::Thinking {
            text: "Some **reasoning** with `code`.".into(),
            elapsed_ms: 1000,
            tokens: None,
            complete: true,
            expanded: true,
            done_at: None,
        }
        .render(80, coda_render::tool::ToolDisplayMode::Summary);

        for (index, row) in rows.iter().enumerate() {
            let line = to_line(row, &theme, 80);
            assert!(line.spans.iter().all(|span| {
                span.style.add_modifier.contains(Modifier::ITALIC)
                    && span.style.fg == Some(theme.fg(Role::Notification))
            }));
            assert!(line.spans.iter().all(|span| {
                span.style.add_modifier.contains(Modifier::DIM) == (index > 0)
            }));
        }
    }

    #[test]
    fn a_gutter_prefix_survives_conversion() {
        let row = RenderLine::new("hello", Role::Assistant).with_gutter(Gutter::AgentComplete);
        let line = to_line(&row, &theme(), 20);
        assert!(plain_text(&line).starts_with(" \u{25CF} "));
    }


    #[test]
    fn columns_keep_their_widths_when_they_all_fit() {
        let browser = crate::overlay::Browser::new(
            "t",
            vec![
                crate::overlay::Column::new("a", 5),
                crate::overlay::Column::new("b", 10),
            ],
        );
        assert_eq!(browser.fit_columns(80), vec![5, 10]);
    }

    #[test]
    fn columns_shrink_to_fit_a_narrow_viewport() {
        let browser = crate::overlay::Browser::new(
            "t",
            vec![
                crate::overlay::Column::new("a", 1),
                crate::overlay::Column::new("b", 40),
                crate::overlay::Column::new("c", 30),
            ],
        );
        let widths = browser.fit_columns(40);
        let total: usize = widths.iter().sum::<usize>() + widths.len() - 1;

        assert!(total <= 40, "columns {widths:?} still overflow");
        assert_eq!(widths[0], 1, "the narrow status column should be preserved");
    }

    #[test]
    fn shrinking_takes_from_the_widest_column_first() {
        let browser = crate::overlay::Browser::new(
            "t",
            vec![
                crate::overlay::Column::new("a", 4),
                crate::overlay::Column::new("b", 40),
            ],
        );
        let widths = browser.fit_columns(30);
        assert_eq!(widths[0], 4, "the narrow column was raided first");
        assert!(widths[1] < 40);
    }

    #[test]
    fn columns_never_shrink_below_one_cell() {
        let browser = crate::overlay::Browser::new(
            "t",
            vec![
                crate::overlay::Column::new("a", 10),
                crate::overlay::Column::new("b", 10),
                crate::overlay::Column::new("c", 10),
            ],
        );
        for available in [0usize, 1, 2, 5] {
            for width in browser.fit_columns(available) {
                assert!(width >= 1, "a column collapsed to nothing");
            }
        }
    }

    #[test]
    fn a_formatted_row_fits_the_computed_widths() {
        let browser = crate::overlay::Browser::new(
            "t",
            vec![
                crate::overlay::Column::new("a", 3),
                crate::overlay::Column::new("b", 20),
            ],
        );
        let item = crate::overlay::Item::new(
            "x",
            vec!["ab".into(), "a rather long value here".into()],
        );
        let widths = browser.fit_columns(40);
        let row = browser.format_columns(&item, &widths);

        assert!(text::width(&row) <= 40, "row {row:?} overflows");
        assert!(row.starts_with("ab "), "cells should be padded: {row:?}");
    }

    #[test]
    fn layout_is_valid_at_every_reasonable_terminal_size() {        for width in [10u16, 40, 80, 200] {
            for height in [5u16, 10, 24, 60] {
                for lines in [1usize, 3, 20] {
                    for busy in [false, true] {
                        let regions = layout(area(width, height), lines, true, busy);
                        let total = regions.transcript.height
                            + regions.composer.height
                            + regions.status.height;
                        assert!(
                            total <= height,
                            "regions overflow at {width}x{height} with {lines} composer lines, busy={busy}"
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn layout_never_panics_at_extreme_terminal_sizes() {
        // 1x1 and a short-but-usable 40x8 must degrade gracefully rather than
        // panic or overlap; a normal 80x24 must keep every region distinct.
        for (width, height) in [(1u16, 1u16), (40, 8), (80, 24)] {
            for busy in [false, true] {
                let regions = layout(area(width, height), 1, true, busy);
                assert!(regions.transcript.height <= height);
                assert!(regions.composer.height <= height);
                assert!(regions.status.height <= height);
            }
        }
    }

    #[test]
    fn a_busy_turn_reserves_the_activity_row_even_when_chrome_is_dropped() {
        // 40x8 is below MIN_ROWS_FOR_CHROME, so header/hint disappear, but
        // live activity is content, not decoration, and must still show.
        let regions = layout(area(40, 8), 1, false, true);
        assert!(regions.header.is_none(), "chrome should be dropped at this height");
        assert!(regions.hint.is_none());
        assert!(regions.activity.is_some(), "the pinned activity row must survive a short terminal");
    }

    #[test]
    fn the_activity_row_is_absent_when_nothing_is_busy() {
        let regions = layout(area(80, 24), 1, false, false);
        assert!(regions.activity.is_none());
    }

    #[test]
    fn header_id_rect_is_none_without_a_session_id() {
        let state = UiState::new();
        assert!(header_id_rect(area(80, 1), &state).is_none());
    }

    #[test]
    fn header_id_rect_is_none_when_the_header_is_too_narrow_to_reach_it() {
        let mut state = UiState::new();
        state.apply(UiEvent::Connected { session_id: "abcdef-0123456789".into() });
        assert!(header_id_rect(area(5, 1), &state).is_none(), "no room, no hit region");
    }

    #[test]
    fn header_id_rect_covers_exactly_the_id_text() {
        let mut state = UiState::new();
        state.apply(UiEvent::Connected { session_id: "sid-123".into() });
        let rect = header_id_rect(area(80, 1), &state).expect("room for the id");
        assert_eq!(rect.width as usize, text::width("sid-123"));
        assert_eq!(rect.y, 0);
    }

    #[test]
    fn header_id_rect_clips_to_the_available_width_but_stays_present() {
        // The rect narrows to what is visible; the *copy* payload (tested in
        // app::clipboard) still uses the full stored id regardless.
        let mut state = UiState::new();
        state.apply(UiEvent::Connected { session_id: "a-very-long-session-identifier-indeed".into() });
        let narrow = header_id_rect(area(30, 1), &state).expect("still some room");
        let wide = header_id_rect(area(200, 1), &state).expect("plenty of room");
        assert!(narrow.width < wide.width, "a narrower header must clip the hit rect");
    }

    #[test]
    fn compose_activity_is_none_when_idle() {
        let state = UiState::new();
        assert_eq!(compose_activity(&state, Instant::now()), None);
    }

    #[test]
    fn compose_activity_shows_zero_seconds_the_instant_a_turn_is_submitted() {
        // Before any engine event at all — the whole point of starting the
        // clock locally rather than waiting for the first response.
        let mut state = UiState::new();
        state.apply(crate::state::UiEvent::Submitted { text: "go".into() });
        let text = compose_activity(&state, Instant::now()).expect("a busy turn composes a row");
        assert!(text.contains("Working"), "{text:?}");
        assert!(text.contains('0'), "expected a zero-second reading: {text:?}");
    }

    #[test]
    fn compose_activity_never_shows_a_fake_token_count() {
        let mut state = UiState::new();
        state.apply(crate::state::UiEvent::Submitted { text: "go".into() });
        let text = compose_activity(&state, Instant::now()).expect("busy");
        assert!(!text.contains("tok"), "no Usage event arrived yet: {text:?}");
    }

    #[test]
    fn compose_activity_mentions_the_queue_once_something_is_queued() {
        let mut state = UiState::new();
        state.apply(crate::state::UiEvent::Submitted { text: "go".into() });
        state.apply(crate::state::UiEvent::Queued { text: "later".into(), id: Some("s1".into()) });
        let text = compose_activity(&state, Instant::now()).expect("busy");
        assert!(text.contains("1 queued"), "{text:?}");
    }
}
