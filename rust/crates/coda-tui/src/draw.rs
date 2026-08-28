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
use ratatui::widgets::{Block, Borders, Clear, Paragraph, Wrap};
use ratatui::Frame;

use crate::composer::Composer;
use crate::state::{PendingPrompt, UiState};
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
        // The composer costs its rows plus a border, and the status bar one
        // more, so the budget is `height - 3 - 1 - 1`.
        .min(area.height.saturating_sub(5).max(COMPOSER_MIN_ROWS));

    let chunks = Layout::default()
        .direction(Direction::Vertical)
        .constraints([
            Constraint::Min(1),
            Constraint::Length(composer_rows + 1), // + the panel's top border
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
    let area = frame.area();
    frame.render_widget(
        Block::default().style(theme.surface()),
        area,
    );

    let regions = layout(area, composer.line_count(), viewport.is_scrollable());

    draw_transcript(frame, regions.transcript, viewport, rows, theme);
    if let Some(scrollbar) = regions.scrollbar {
        draw_scrollbar(frame, scrollbar, viewport, theme);
    }
    draw_composer(frame, regions.composer, composer, state, theme);
    draw_status(frame, regions.status, state, viewport, theme);

    if let Some(prompt) = &state.prompt {
        draw_prompt(frame, area, prompt, theme);
    }
}

fn draw_transcript(
    frame: &mut Frame,
    area: Rect,
    viewport: &Viewport,
    rows: &[RenderLine],
    theme: &Theme,
) {
    let width = area.width as usize;
    let lines: Vec<Line> = rows
        .get(viewport.visible_range())
        .unwrap_or(&[])
        .iter()
        .map(|row| to_line(row, theme, width))
        .collect();

    frame.render_widget(Paragraph::new(lines).style(theme.surface()), area);
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
                ("\u{2588}", thumb_style) // █
            } else {
                ("\u{2502}", track_style) // │
            };
            Line::from(Span::styled(glyph, style))
        })
        .collect();

    frame.render_widget(Paragraph::new(lines), area);
}

fn draw_composer(
    frame: &mut Frame,
    area: Rect,
    composer: &Composer,
    state: &UiState,
    theme: &Theme,
) {
    let panel = Block::default()
        .borders(Borders::TOP)
        .border_style(theme.style(Role::ComposerPanelEdge))
        .style(Style::default().bg(theme.fg(Role::ComposerPanelBackground)));
    let inner = panel.inner(area);
    frame.render_widget(panel, area);

    let prompt_style = theme.style(Role::ComposerPrompt);
    let text_style = theme.style(Role::ComposerText);

    let lines: Vec<Line> = composer
        .lines()
        .enumerate()
        .map(|(index, line)| {
            let marker = if index == 0 {
                if state.is_busy() { "\u{22EF} " } else { "\u{276F} " }
            } else {
                "  "
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
    let x = inner.x + 2 + column as u16;
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
            format!("\u{2502} {model} "),
            theme.style(Role::Notification),
        ));
    }

    if let Some(percent) = state.usage.percent_used() {
        spans.push(Span::styled(
            format!("\u{2502} context {percent}% "),
            theme.style(Role::Notification),
        ));
    }

    if state.interrupting {
        spans.push(Span::styled(
            "\u{2502} interrupting… ",
            theme.style(Role::Warning),
        ));
    }

    if !state.queued.is_empty() {
        spans.push(Span::styled(
            format!("\u{2502} {} queued ", state.queued.len()),
            theme.style(Role::PendingUser),
        ));
    }

    // The jump-to-bottom hint only matters when content arrived unseen.
    if viewport.unread() > 0 {
        spans.push(Span::styled(
            format!("\u{2502} {} new \u{2193} Ctrl+End ", viewport.unread()),
            theme.style(Role::PromptAccent),
        ));
    }

    frame.render_widget(
        Paragraph::new(Line::from(spans)).style(theme.surface()),
        area,
    );
}

fn draw_prompt(frame: &mut Frame, area: Rect, prompt: &PendingPrompt, theme: &Theme) {
    let (title, body, hint) = match prompt {
        PendingPrompt::Permission { tool, preview } => (
            "Permission required",
            format!("{tool}\n\n{preview}"),
            "y: allow    n: deny    Esc: deny",
        ),
        PendingPrompt::Question { question, options, .. } => {
            let body = if options.is_empty() {
                question.clone()
            } else {
                let list = options
                    .iter()
                    .enumerate()
                    .map(|(i, option)| format!("  {}. {option}", i + 1))
                    .collect::<Vec<_>>()
                    .join("\n");
                format!("{question}\n\n{list}")
            };
            ("Question", body, "\u{2191}\u{2193}: choose    Enter: answer")
        }
        PendingPrompt::PlanApproval { plan } => (
            "Approve plan?",
            plan.clone(),
            "y: approve    n: reject    Esc: reject",
        ),
    };

    let region = centered(area, 70, 60);
    frame.render_widget(Clear, region);

    let block = Block::default()
        .title(format!(" {title} "))
        .borders(Borders::ALL)
        .border_style(theme.style(Role::PromptAccent))
        .style(theme.surface());
    let inner = block.inner(region);
    frame.render_widget(block, region);

    let chunks = Layout::default()
        .direction(Direction::Vertical)
        .constraints([Constraint::Min(1), Constraint::Length(1)])
        .split(inner);

    frame.render_widget(
        Paragraph::new(body)
            .style(theme.style(Role::PromptText))
            .wrap(Wrap { trim: false }),
        chunks[0],
    );
    frame.render_widget(
        Paragraph::new(hint).style(theme.style(Role::Notification)),
        chunks[1],
    );
}

/// A rectangle centred within `area`, sized as a percentage of it.
fn centered(area: Rect, percent_x: u16, percent_y: u16) -> Rect {
    let vertical = Layout::default()
        .direction(Direction::Vertical)
        .constraints([
            Constraint::Percentage((100 - percent_y) / 2),
            Constraint::Percentage(percent_y),
            Constraint::Percentage((100 - percent_y) / 2),
        ])
        .split(area);

    Layout::default()
        .direction(Direction::Horizontal)
        .constraints([
            Constraint::Percentage((100 - percent_x) / 2),
            Constraint::Percentage(percent_x),
            Constraint::Percentage((100 - percent_x) / 2),
        ])
        .split(vertical[1])[1]
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
        assert_eq!(regions.composer.height, 2); // one row plus the border
        assert_eq!(regions.transcript.height, 21);
    }

    #[test]
    fn the_composer_grows_with_its_content() {
        let regions = layout(area(80, 24), 5, false);
        assert_eq!(regions.composer.height, 6);
        assert_eq!(regions.transcript.height, 17);
    }

    #[test]
    fn the_composer_stops_growing_at_its_cap() {
        let regions = layout(area(80, 40), 50, false);
        assert_eq!(regions.composer.height, COMPOSER_MAX_ROWS + 1);
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
    fn the_centred_region_stays_inside_its_area() {
        for (width, height) in [(80u16, 24u16), (20, 10), (200, 60), (4, 4)] {
            let outer = area(width, height);
            let inner = centered(outer, 70, 60);
            assert!(inner.right() <= outer.right());
            assert!(inner.bottom() <= outer.bottom());
            assert!(inner.x >= outer.x);
            assert!(inner.y >= outer.y);
        }
    }

    #[test]
    fn layout_is_valid_at_every_reasonable_terminal_size() {
        for width in [10u16, 40, 80, 200] {
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

