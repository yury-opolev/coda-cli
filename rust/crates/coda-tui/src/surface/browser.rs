//! The browser surface: a filterable table with an optional detail view.
//!
//! Wraps the existing [`Browser`] rather than reimplementing it. `Browser`
//! already does columns, filtering, selection, paging, a detail view and extra
//! per-browser keys, and it is well covered by tests — it *is* the table
//! control this phase needed. Rewriting eight working browsers onto a second,
//! identical widget would have been churn with a real chance of regression and
//! nothing to show for it.
//!
//! What this adds is the part that was missing: browsers now live on the same
//! stack as prompts and forms, with one key path and one render path, so an
//! engine prompt outranks a browser because it is `Exclusive` rather than
//! because of the order of two `if` statements.

use super::{Surface, SurfaceAction, SurfaceOutcome};
use crate::overlay::{Browser, Intent, View};
use coda_render::text;
use coda_render::theme::{Role, Theme};
use crossterm::event::KeyEvent;
use ratatui::layout::Rect;
use ratatui::text::{Line, Span};

/// Which browser is open, so the host knows what an action refers to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BrowserKind {
    Models,
    Schedules,
    Skills,
    Plugins,
    Hooks,
    Mcp,
    Tasks,
    Sessions,
}

pub struct BrowserSurface {
    browser: Browser,
    kind: BrowserKind,
}

impl BrowserSurface {
    pub fn new(kind: BrowserKind, browser: Browser) -> Self {
        Self { browser, kind }
    }

    pub fn kind(&self) -> BrowserKind {
        self.kind
    }

    pub fn browser(&self) -> &Browser {
        &self.browser
    }

    pub fn browser_mut(&mut self) -> &mut Browser {
        &mut self.browser
    }
}

impl Surface for BrowserSurface {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn title(&self) -> String {
        self.browser.title().to_string()
    }

    fn hints(&self) -> String {
        // The status row and the key list share one line. A filter being typed
        // outranks both: it is what the user is doing right now.
        //
        // The old three-row layout had a dedicated status row; folding it in
        // here keeps `set_status` visible rather than writing to something
        // nothing renders, which is what happened when the row was dropped.
        if let Some(filter) = self.browser.filter_text() {
            return format!("/{filter}");
        }
        let status = self.browser.status();
        if status.is_empty() {
            self.browser.footer().to_string()
        } else {
            format!("{status}    {}", self.browser.footer())
        }
    }

    fn placement(&self) -> super::Placement {
        // Browsers are tables; they earn more of the screen than a short form.
        super::Placement::Modal {
            width_pct: 90,
            height_pct: 85,
        }
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        match self.browser.handle(key) {
            Intent::Redraw => SurfaceOutcome::Handled,
            Intent::Ignored => SurfaceOutcome::Ignored,
            Intent::Close => SurfaceOutcome::Close,
            // Everything else needs the engine or the filesystem, so it goes
            // to the host. The browser stays open; the host decides.
            intent => SurfaceOutcome::Emit(SurfaceAction::Browser {
                kind: self.kind,
                intent,
            }),
        }
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        let width = area.width.max(1) as usize;
        let height = area.height as usize;
        if height == 0 {
            return Vec::new();
        }

        let mut lines: Vec<Line<'static>> = Vec::new();

        match self.browser.view() {
            View::Detail => {
                let style = theme.style(Role::ComposerText);
                for row in self
                    .browser
                    .detail_lines()
                    .iter()
                    .skip(self.browser.detail_scroll())
                {
                    for chunk in text::wrap_preformatted(&text::sanitize(row), width) {
                        lines.push(Line::from(Span::styled(chunk, style)));
                    }
                }
            }
            View::List => {
                let items = self.browser.visible_items();
                // The column fitting lives on Browser and is already tested
                // there; duplicating it here would give two answers to the
                // same question and no way to notice when they diverged.
                let widths = self.browser.fit_columns(width);

                let headings: Vec<String> = self
                    .browser
                    .columns()
                    .iter()
                    .enumerate()
                    .map(|(i, c)| {
                        let w = widths.get(i).copied().unwrap_or(0);
                        let text = text::truncate(c.header, w);
                        let pad = w.saturating_sub(text::width(&text));
                        format!("{text}{}", " ".repeat(pad))
                    })
                    .collect();
                lines.push(Line::from(Span::styled(
                    headings.join(" "),
                    theme.style(Role::Heading),
                )));

                // Paged so the selection stays visible: the header costs a row.
                let body = height.saturating_sub(1);
                let selected = self.browser.selected_index();
                let scroll = selected.saturating_sub(body.saturating_sub(1));

                for (offset, item) in items.iter().skip(scroll).take(body).enumerate() {
                    let is_selected = scroll + offset == selected;
                    let style = if is_selected {
                        theme
                            .style(Role::SelectionText)
                            .bg(theme.fg(Role::SelectionBackground))
                    } else {
                        theme.style(Role::ComposerText)
                    };
                    lines.push(Line::from(Span::styled(
                        self.browser.format_columns(item, &widths),
                        style,
                    )));
                }

                if items.is_empty() {
                    lines.push(Line::from(Span::styled(
                        "Nothing to show.".to_string(),
                        theme.style(Role::Notification),
                    )));
                }
            }
        }

        lines.truncate(height);
        lines
    }
}



#[cfg(test)]
mod tests {
    use super::*;
    use crate::overlay::{Column, Item};
    use crossterm::event::{KeyCode, KeyModifiers};

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn surface() -> BrowserSurface {
        let mut browser = Browser::new(
            "Skills",
            vec![
                Column::new("Name", 24),
                Column::new("Summary", 40),
            ],
        )
        .with_footer("Enter select");
        browser.set_items(vec![
            Item::new("a", vec!["alpha".into(), "first".into()]),
            Item::new("b", vec!["beta".into(), "second".into()]),
            Item::new("c", vec!["gamma".into(), "third".into()]),
        ]);
        BrowserSurface::new(BrowserKind::Skills, browser)
    }

    #[test]
    fn navigation_is_handled_without_troubling_the_host() {
        let mut s = surface();
        assert!(matches!(
            s.handle_key(key(KeyCode::Down)),
            SurfaceOutcome::Handled
        ));
        assert_eq!(s.browser().selected_id(), Some("b"));
    }

    #[test]
    fn escape_closes() {
        assert!(matches!(
            surface().handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Close
        ));
    }

    #[test]
    fn an_action_carries_its_browser_kind_to_the_host() {
        // Without the kind the host cannot tell which browser asked, and would
        // have to keep a parallel field in step with the stack.
        let mut s = surface();
        match s.handle_key(key(KeyCode::Char(' '))) {
            SurfaceOutcome::Emit(SurfaceAction::Browser { kind, intent }) => {
                assert_eq!(kind, BrowserKind::Skills);
                assert_eq!(intent, Intent::Toggle("a".into()));
            }
            _ => panic!("Space did not reach the host as a toggle"),
        }
    }

    #[test]
    fn the_header_and_rows_line_up() {
        let lines = surface().render(Rect::new(0, 0, 40, 10), &Theme::default());
        let rendered: Vec<String> = lines
            .iter()
            .map(|l| l.spans.iter().map(|s| s.content.to_string()).collect())
            .collect();
        let starts: Vec<usize> = rendered
            .iter()
            .filter_map(|row| row.find("first").or_else(|| row.find("second")))
            .collect();
        assert!(
            starts.windows(2).all(|w| w[0] == w[1]),
            "columns did not align: {rendered:#?}"
        );
    }

    #[test]
    fn the_selected_row_is_marked_without_relying_on_position() {
        let mut s = surface();
        s.handle_key(key(KeyCode::Down));
        let theme = Theme::default();
        let selected_bg = theme.fg(Role::SelectionBackground);
        let marked = s
            .render(Rect::new(0, 0, 40, 10), &theme)
            .iter()
            .filter(|l| l.spans.iter().any(|sp| sp.style.bg == Some(selected_bg)))
            .count();
        assert_eq!(marked, 1, "expected exactly one highlighted row");
    }

    #[test]
    fn a_long_list_pages_to_keep_the_selection_visible() {
        let mut browser = Browser::new("Many", vec![Column::new("Name", 24)]);
        browser.set_items(
            (0..60)
                .map(|i| Item::new(format!("id{i}"), vec![format!("row{i}")]))
                .collect(),
        );
        let mut s = BrowserSurface::new(BrowserKind::Tasks, browser);
        for _ in 0..59 {
            s.handle_key(key(KeyCode::Down));
        }
        let text: String = s
            .render(Rect::new(0, 0, 40, 8), &Theme::default())
            .iter()
            .flat_map(|l| l.spans.iter().map(|sp| sp.content.to_string()))
            .collect();
        assert!(
            text.contains("row59"),
            "the selected row scrolled out of sight: {text:?}"
        );
    }

    #[test]
    fn it_never_renders_more_lines_than_the_area_allows() {
        let s = surface();
        for height in [0, 1, 2, 4, 40] {
            let area = Rect::new(0, 0, 40, height);
            assert!(s.render(area, &Theme::default()).len() <= height as usize);
        }
    }

    #[test]
    fn columns_never_exceed_the_width_they_are_given() {
        let mut browser = Browser::new(
            "Wide",
            vec![
                Column::new("One", 80),
                Column::new("Two", 80),
            ],
        );
        browser.set_items(vec![Item::new(
            "x",
            vec!["a".repeat(80), "b".repeat(80)],
        )]);
        let s = BrowserSurface::new(BrowserKind::Mcp, browser);
        for width in [10u16, 20, 40] {
            for line in s.render(Rect::new(0, 0, width, 6), &Theme::default()) {
                let rendered: usize = line
                    .spans
                    .iter()
                    .map(|sp| text::width(&sp.content))
                    .sum();
                assert!(
                    rendered <= width as usize,
                    "a {rendered}-cell row was produced for {width} cells"
                );
            }
        }
    }

    #[test]
    fn the_status_is_visible_alongside_the_key_hints() {
        // The old layout had a dedicated status row. When it was dropped,
        // set_status wrote to something nothing rendered, so a reload gave no
        // confirmation at all.
        let mut s = surface();
        s.browser.set_status("reloaded");
        let hints = s.hints();
        assert!(hints.contains("reloaded"), "status missing from {hints:?}");
        assert!(hints.contains("Enter select"), "hints lost: {hints:?}");
    }

    #[test]
    fn selecting_by_id_moves_to_that_row() {
        // A reload builds a fresh Browser, which starts at the top; without
        // this the user's place is silently thrown away.
        let mut s = surface();
        assert!(s.browser.select_by_id("c"));
        assert_eq!(s.browser.selected_id(), Some("c"));
        assert!(!s.browser.select_by_id("nope"));
        assert_eq!(s.browser.selected_id(), Some("c"), "a miss moved the selection");
    }

    #[test]
    fn the_filter_replaces_the_hints_while_it_is_being_typed() {
        let mut s = surface();
        s.handle_key(key(KeyCode::Char('/')));
        s.handle_key(key(KeyCode::Char('b')));
        assert_eq!(s.hints(), "/b");
    }
}
