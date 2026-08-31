//! Drawing: turning state into terminal output.
//!
//! The screen is three stacked regions — transcript, composer, status — with an
//! optional scrollbar down the right edge and overlays drawn on top.

use coda_render::text;
use coda_render::theme::{Role, Theme};
use coda_render::RenderLine;
use ratatui::layout::{Constraint, Direction, Layout, Rect};
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
/// Composer height bounds, in rows.
const COMPOSER_MIN_ROWS: u16 = 1;
const COMPOSER_MAX_ROWS: u16 = 10;

/// The regions the screen is divided into.
#[derive(Debug, Clone, Copy)]
pub struct Regions {
    pub transcript: Rect,
    pub scrollbar: Option<Rect>,
    pub composer: Rect,
    pub status: Rect,
}

/// Splits the frame into its regions.
///
/// The composer grows with its content up to a cap, after which it scrolls
/// internally rather than crowding out the transcript.
pub fn layout(area: Rect, composer_lines: usize, scrollable: bool) -> Regions {
    let composer_rows = (composer_lines as u16)
        .clamp(COMPOSER_MIN_ROWS, COMPOSER_MAX_ROWS)
        // Leave at least three transcript rows however tall the composer is.
        // The composer costs its rows plus both half-block edges, and the
        // status bar one more, so the budget is `height - 3 - 2 - 1`.
        .min(area.height.saturating_sub(6).max(COMPOSER_MIN_ROWS));

    let chunks = Layout::default()
        .direction(Direction::Vertical)
        .constraints([
            Constraint::Min(1),
            // + the panel's top and bottom half-block edges
            Constraint::Length(composer_rows + 2),
            Constraint::Length(1),
        ])
        .split(area);

    let (transcript, scrollbar) = if scrollable && chunks[0].width > SCROLLBAR_WIDTH {
        let split = Layout::default()
            .direction(Direction::Horizontal)
            .constraints([Constraint::Min(1), Constraint::Length(SCROLLBAR_WIDTH)])
            .split(chunks[0]);
        (split[0], Some(split[1]))
    } else {
        (chunks[0], None)
    };

    Regions {
        transcript,
        scrollbar,
        composer: chunks[1],
        status: chunks[2],
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
    let mut style = Style::default().fg(theme.fg(role));
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
) {
    draw_with_pin(frame, state, composer, viewport, rows, theme, None, None);
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
) -> (u16, u16) {
    let area = frame.area();
    frame.render_widget(
        Block::default().style(theme.surface()),
        area,
    );

    let regions = layout(area, composer.line_count(), viewport.is_scrollable());

    let content = draw_transcript_with_pin(
        frame,
        regions.transcript,
        viewport,
        rows,
        theme,
        pin_text,
        selection,
    );
    if let Some(scrollbar) = regions.scrollbar {
        draw_scrollbar(frame, scrollbar, viewport, theme);
    }
    draw_composer(frame, regions.composer, composer, state, theme);
    draw_status(frame, regions.status, state, viewport, theme);


    // Prompts are surfaces now, drawn by the stack after this returns. Their
    // Exclusive modality is what puts them above a browser, rather than the
    // order of these calls.

    content
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
const COMPOSER_TEXT_COLUMN: u16 = 3;

/// Breathing room between a modal's border and its contents.
///
/// Horizontal only: vertical padding would cost rows that modals — which are
/// already capped to a fraction of the screen — cannot spare.
pub const MODAL_PADDING: Padding = Padding::horizontal(1);

fn draw_composer(
    frame: &mut Frame,
    area: Rect,
    composer: &Composer,
    state: &UiState,
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
            let marker = if index == 0 {
                if state.is_busy() { glyphs::BUSY_PADDED } else { glyphs::PROMPT_PADDED }
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
    let mut spans = vec![Span::styled(
        format!(" {} ", state.activity.label()),
        theme.style(state.activity.role()),
    )];

    if let Some(model) = &state.model {
        spans.push(Span::styled(
            format!("{} {model} ", glyphs::RULE_VERTICAL),
            theme.style(Role::Notification),
        ));
    }

    if let Some(percent) = state.usage.percent_used() {
        spans.push(Span::styled(
            format!("{} context {percent}% ", glyphs::RULE_VERTICAL),
            theme.style(Role::Notification),
        ));
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

    frame.render_widget(
        Paragraph::new(Line::from(spans)).style(theme.surface()),
        area,
    );
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

    let block = Block::default()
        .title(format!(" {} ", rendered.title))
        .borders(Borders::ALL)
        .border_style(theme.style(Role::PromptAccent))
        .padding(MODAL_PADDING)
        .style(theme.surface());
    frame.render_widget(block, region);

    // Geometry comes from the same helper the stack used, so the surface is
    // drawn into exactly the area it scrolled itself against.
    frame.render_widget(Paragraph::new(rendered.lines.clone()), rendered.content);

    let footer = crate::surface::chrome::footer(region, &rendered.hints);
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
    fn layout_reserves_rows_for_the_composer_and_status() {
        let regions = layout(area(80, 24), 1, false);
        assert_eq!(regions.status.height, 1);
        assert_eq!(regions.composer.height, 3); // one row plus both edges
        assert_eq!(regions.transcript.height, 20);
    }

    #[test]
    fn the_composer_grows_with_its_content() {
        let regions = layout(area(80, 24), 5, false);
        assert_eq!(regions.composer.height, 7);
        assert_eq!(regions.transcript.height, 16);
    }

    #[test]
    fn the_composer_stops_growing_at_its_cap() {
        let regions = layout(area(80, 40), 50, false);
        assert_eq!(regions.composer.height, COMPOSER_MAX_ROWS + 2);
    }

    #[test]
    fn the_composer_never_starves_the_transcript() {
        let regions = layout(area(80, 8), 50, false);
        assert!(regions.transcript.height >= 3, "transcript was squeezed out");
    }

    #[test]
    fn a_scrollbar_column_is_reserved_only_when_scrollable() {
        assert!(layout(area(80, 24), 1, false).scrollbar.is_none());

        let regions = layout(area(80, 24), 1, true);
        let scrollbar = regions.scrollbar.expect("a scrollbar");
        assert_eq!(scrollbar.width, SCROLLBAR_WIDTH);
        assert_eq!(regions.transcript.width, 79);
    }

    #[test]
    fn a_very_narrow_frame_drops_the_scrollbar() {
        let regions = layout(area(1, 24), 1, true);
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
                    let regions = layout(area(width, height), lines, true);
                    let total = regions.transcript.height
                        + regions.composer.height
                        + regions.status.height;
                    assert!(
                        total <= height,
                        "regions overflow at {width}x{height} with {lines} composer lines"
                    );
                }
            }
        }
    }
}

