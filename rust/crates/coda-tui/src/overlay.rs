//! The list/detail browser shared by every overlay.
//!
//! The model, task, MCP, skill, plugin and schedule browsers differ only in
//! their data and a few extra keys, so the navigation, filtering, paging and
//! detail behaviour live here once. Overlays supply columns and rows; this
//! module owns selection and translates keys into intents.
//!
//! Like the keymap, this is deliberately free of terminal and engine
//! dependencies so all of its behaviour is testable without a screen.

use coda_render::text;

/// Rows moved by a page key.
pub const PAGE_ROWS: usize = 10;

/// A column in the list view.
#[derive(Debug, Clone)]
pub struct Column {
    pub header: &'static str,
    /// Cells wider than this are truncated with an ellipsis.
    pub max_width: usize,
}

impl Column {
    pub const fn new(header: &'static str, max_width: usize) -> Self {
        Self { header, max_width }
    }
}

/// One row, plus the detail shown when it is opened.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Item {
    /// Stable identifier returned with any action taken on this row.
    pub id: String,
    /// Cells, parallel to the browser's columns.
    pub cells: Vec<String>,
    /// Lines shown in the detail view.
    pub detail: Vec<String>,
}

impl Item {
    pub fn new(id: impl Into<String>, cells: Vec<String>) -> Self {
        Self {
            id: id.into(),
            cells,
            detail: Vec::new(),
        }
    }

    pub fn with_detail(mut self, detail: Vec<String>) -> Self {
        self.detail = detail;
        self
    }

    /// Text matched against the filter: every cell plus the id.
    fn haystack(&self) -> String {
        let mut text = self.cells.join(" ");
        text.push(' ');
        text.push_str(&self.id);
        text.to_lowercase()
    }
}

/// Which pane the browser is showing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum View {
    List,
    Detail,
}

/// What a key press asked the host to do.
///
/// The browser handles navigation itself; anything requiring data access or an
/// engine call is returned for the host to perform.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Intent {
    /// Handled internally; just redraw.
    Redraw,
    /// The key was not bound here.
    Ignored,
    /// Close the overlay.
    Close,
    /// Enter pressed on a row in list view with no detail to show.
    Activate(String),
    /// Space pressed on a row.
    Toggle(String),
    /// Delete pressed on a row.
    Delete(String),
    /// Reload the underlying data.
    Reload,
    /// An overlay-specific key, with the selected row's id.
    Key(char, Option<String>),
}

/// A list/detail browser over a set of rows.
#[derive(Debug, Clone)]
pub struct Browser {
    title: String,
    columns: Vec<Column>,
    items: Vec<Item>,
    /// Indices into `items` that pass the current filter.
    visible: Vec<usize>,
    /// Index into `visible`.
    selected: usize,
    view: View,
    /// `Some` while filter entry is active.
    filter: Option<String>,
    detail_scroll: usize,
    status: String,
    footer: String,
    /// Keys this overlay handles beyond the shared set.
    extra_keys: Vec<char>,
    /// Whether Enter opens a detail pane. Schedule has no detail view.
    has_detail: bool,
}

impl Browser {
    pub fn new(title: impl Into<String>, columns: Vec<Column>) -> Self {
        Self {
            title: title.into(),
            columns,
            items: Vec::new(),
            visible: Vec::new(),
            selected: 0,
            view: View::List,
            filter: None,
            detail_scroll: 0,
            status: String::new(),
            footer: String::new(),
            extra_keys: Vec::new(),
            has_detail: true,
        }
    }

    pub fn with_footer(mut self, footer: impl Into<String>) -> Self {
        self.footer = footer.into();
        self
    }

    pub fn with_extra_keys(mut self, keys: &[char]) -> Self {
        self.extra_keys = keys.to_vec();
        self
    }

    /// Adds keys the browser should report rather than swallow.
    ///
    /// Additive, unlike [`with_extra_keys`], so a caller attaching actions can
    /// register their keys without discarding any the browser already
    /// declared.
    pub fn add_extra_keys(&mut self, keys: &[char]) {
        for key in keys {
            if !self.extra_keys.contains(key) {
                self.extra_keys.push(*key);
            }
        }
    }

    /// Marks this browser as list-only, so Enter activates instead of opening
    /// a detail pane.
    pub fn without_detail(mut self) -> Self {
        self.has_detail = false;
        self
    }

    pub fn title(&self) -> &str {
        &self.title
    }

    pub fn columns(&self) -> &[Column] {
        &self.columns
    }

    pub fn view(&self) -> View {
        self.view
    }

    pub fn status(&self) -> &str {
        &self.status
    }

    pub fn footer(&self) -> &str {
        &self.footer
    }

    pub fn set_status(&mut self, status: impl Into<String>) {
        self.status = status.into();
    }

    /// Whether filter entry is active.
    pub fn is_filtering(&self) -> bool {
        self.filter.is_some()
    }

    pub fn filter_text(&self) -> Option<&str> {
        self.filter.as_deref()
    }

    /// Replaces the rows, preserving the selected row by id where possible.
    ///
    /// A reload must not silently move the user's selection to a different
    /// item, which is what a naive index-based restore would do.
    pub fn set_items(&mut self, items: Vec<Item>) {
        let previous = self.selected_id().map(str::to_string);
        self.items = items;
        self.reindex();

        if let Some(previous) = previous {
            if let Some(position) = self
                .visible
                .iter()
                .position(|&i| self.items[i].id == previous)
            {
                self.selected = position;
            }
        }
        self.clamp();
    }

    pub fn items(&self) -> &[Item] {
        &self.items
    }

    /// Rows passing the current filter, in display order.
    pub fn visible_items(&self) -> Vec<&Item> {
        self.visible.iter().map(|&i| &self.items[i]).collect()
    }

    pub fn len(&self) -> usize {
        self.visible.len()
    }

    pub fn is_empty(&self) -> bool {
        self.visible.is_empty()
    }

    pub fn selected_index(&self) -> usize {
        self.selected
    }

    pub fn selected(&self) -> Option<&Item> {
        self.visible.get(self.selected).map(|&i| &self.items[i])
    }

    pub fn selected_id(&self) -> Option<&str> {
        self.selected().map(|item| item.id.as_str())
    }

    pub fn detail_scroll(&self) -> usize {
        self.detail_scroll
    }

    /// Detail lines of the selected row.
    pub fn detail_lines(&self) -> &[String] {
        self.selected().map_or(&[], |item| item.detail.as_slice())
    }

    /// Formats a row's cells, truncated to each column's width.
    pub fn format_row(&self, item: &Item) -> Vec<String> {
        self.columns
            .iter()
            .enumerate()
            .map(|(i, column)| {
                let cell = item.cells.get(i).map(String::as_str).unwrap_or("");
                text::truncate_with_ellipsis(cell, column.max_width)
            })
            .collect()
    }

    /// Recomputes which rows pass the filter.
    fn reindex(&mut self) {
        let needle = self
            .filter
            .as_deref()
            .map(str::trim)
            .filter(|f| !f.is_empty())
            .map(str::to_lowercase);

        self.visible = self
            .items
            .iter()
            .enumerate()
            .filter(|(_, item)| match &needle {
                Some(needle) => item.haystack().contains(needle),
                None => true,
            })
            .map(|(i, _)| i)
            .collect();
    }

    fn clamp(&mut self) {
        if self.visible.is_empty() {
            self.selected = 0;
        } else if self.selected >= self.visible.len() {
            self.selected = self.visible.len() - 1;
        }
    }

    fn move_by(&mut self, delta: isize) {
        if self.visible.is_empty() {
            return;
        }
        let last = self.visible.len() as isize - 1;
        let next = (self.selected as isize + delta).clamp(0, last);
        self.selected = next as usize;
        self.detail_scroll = 0;
    }

    /// Handles a key, returning what the host should do.
    /// Chooses a display width for each column so the row fits the viewport.
///
    /// Columns declare a maximum, but a narrow terminal cannot honour all of them.
    /// Rather than letting the rightmost columns fall off the edge, the surplus is
    /// taken from the widest columns first, which preserves short status and
    /// version columns that carry most of the signal per cell.
    pub fn fit_columns(&self, available: usize) -> Vec<usize> {
    let mut widths: Vec<usize> = self.columns().iter().map(|c| c.max_width).collect();
    if widths.is_empty() {
        return widths;
    }

    let separators = widths.len().saturating_sub(1);
    let budget = available.saturating_sub(separators);

    let mut total: usize = widths.iter().sum();
    while total > budget {
        // Shrink the widest column by one cell, never below one.
        let Some((index, _)) = widths
            .iter()
            .enumerate()
            .filter(|(_, &w)| w > 1)
            .max_by_key(|(_, &w)| w)
        else {
            break;
        };
        widths[index] -= 1;
        total -= 1;
    }
    widths
}

    /// Renders one row's cells into a padded, separated line.
    pub fn format_columns(&self, item: &Item, widths: &[usize]) -> String {
    self
        .columns()
        .iter()
        .enumerate()
        .map(|(i, _)| {
            let width = widths.get(i).copied().unwrap_or(0);
            let cell = item.cells.get(i).map(String::as_str).unwrap_or("");
            let cell = text::truncate_with_ellipsis(cell, width);
            let padding = width.saturating_sub(text::width(&cell));
            format!("{cell}{}", " ".repeat(padding))
        })
        .collect::<Vec<_>>()
        .join(" ")
}

    /// Moves the selection to the row with `id`, if it is visible.
    ///
    /// Used when a browser is rebuilt from fresh data: the new instance starts
    /// at the top, so without this a reload silently throws away the user's
    /// place in the list.
    pub fn select_by_id(&mut self, id: &str) -> bool {
        match self
            .visible
            .iter()
            .position(|&i| self.items[i].id == id)
        {
            Some(position) => {
                self.selected = position;
                true
            }
            None => false,
        }
    }

    pub fn handle(&mut self, key: crossterm::event::KeyEvent) -> Intent {
        use crossterm::event::{KeyCode, KeyEventKind, KeyModifiers};

        if key.kind == KeyEventKind::Release {
            return Intent::Ignored;
        }

        // Filter entry swallows printable keys, so it is handled first.
        if self.filter.is_some() {
            return self.handle_filter(key.code);
        }

        let ctrl = key.modifiers.contains(KeyModifiers::CONTROL);

        match (self.view, key.code) {
            // Closing and going back.
            (View::Detail, KeyCode::Esc | KeyCode::Char('q'))
            | (View::Detail, KeyCode::Char('b')) if !ctrl || key.code == KeyCode::Char('b') => {
                self.view = View::List;
                self.detail_scroll = 0;
                Intent::Redraw
            }
            (View::List, KeyCode::Esc | KeyCode::Char('q')) => Intent::Close,

            // Movement.
            (_, KeyCode::Up | KeyCode::Char('k')) => {
                if self.view == View::Detail {
                    self.detail_scroll = self.detail_scroll.saturating_sub(1);
                } else {
                    self.move_by(-1);
                }
                Intent::Redraw
            }
            (_, KeyCode::Down | KeyCode::Char('j')) => {
                if self.view == View::Detail {
                    self.scroll_detail(1);
                } else {
                    self.move_by(1);
                }
                Intent::Redraw
            }
            (_, KeyCode::PageUp) => {
                if self.view == View::Detail {
                    self.detail_scroll = self.detail_scroll.saturating_sub(PAGE_ROWS);
                } else {
                    self.move_by(-(PAGE_ROWS as isize));
                }
                Intent::Redraw
            }
            (_, KeyCode::PageDown) => {
                if self.view == View::Detail {
                    self.scroll_detail(PAGE_ROWS);
                } else {
                    self.move_by(PAGE_ROWS as isize);
                }
                Intent::Redraw
            }
            (View::List, KeyCode::Home) => {
                self.selected = 0;
                self.detail_scroll = 0;
                Intent::Redraw
            }
            (View::List, KeyCode::End) => {
                self.selected = self.visible.len().saturating_sub(1);
                self.detail_scroll = 0;
                Intent::Redraw
            }
            (View::Detail, KeyCode::Home) => {
                self.detail_scroll = 0;
                Intent::Redraw
            }
            (View::Detail, KeyCode::End) => {
                self.detail_scroll = self.detail_lines().len().saturating_sub(1);
                Intent::Redraw
            }

            // Opening.
            (View::List, KeyCode::Enter) => match self.selected_id().map(str::to_string) {
                Some(_) if self.has_detail && !self.detail_lines().is_empty() => {
                    self.view = View::Detail;
                    self.detail_scroll = 0;
                    Intent::Redraw
                }
                Some(id) => Intent::Activate(id),
                None => Intent::Redraw,
            },

            // Shared actions.
            (_, KeyCode::Char(' ')) => match self.selected_id() {
                Some(id) => Intent::Toggle(id.to_string()),
                None => Intent::Redraw,
            },
            (_, KeyCode::Delete) => match self.selected_id() {
                Some(id) => Intent::Delete(id.to_string()),
                None => Intent::Redraw,
            },
            (_, KeyCode::Char('r')) => Intent::Reload,
            (View::List, KeyCode::Char('/')) => {
                self.filter = Some(String::new());
                Intent::Redraw
            }

            // Overlay-specific keys.
            (_, KeyCode::Char(c)) if self.extra_keys.contains(&c) => {
                Intent::Key(c, self.selected_id().map(str::to_string))
            }

            _ => Intent::Ignored,
        }
    }

    fn scroll_detail(&mut self, by: usize) {
        let last = self.detail_lines().len().saturating_sub(1);
        self.detail_scroll = (self.detail_scroll + by).min(last);
    }

    fn handle_filter(&mut self, code: crossterm::event::KeyCode) -> Intent {
        use crossterm::event::KeyCode;

        match code {
            // Leaving filter entry keeps the filter applied, matching the C#
            // browsers: Esc exits the mode, it does not undo the narrowing.
            KeyCode::Esc => {
                if self.filter.as_deref().is_some_and(str::is_empty) {
                    self.filter = None;
                } else {
                    self.filter = None;
                    self.reindex();
                    self.clamp();
                }
                Intent::Redraw
            }
            KeyCode::Enter => {
                self.filter = None;
                Intent::Redraw
            }
            KeyCode::Backspace => {
                if let Some(filter) = self.filter.as_mut() {
                    filter.pop();
                }
                self.reindex();
                self.clamp();
                Intent::Redraw
            }
            KeyCode::Char(c) => {
                if let Some(filter) = self.filter.as_mut() {
                    filter.push(c);
                }
                self.reindex();
                self.selected = 0;
                Intent::Redraw
            }
            _ => Intent::Ignored,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn browser() -> Browser {
        let mut browser = Browser::new(
            "Models",
            vec![Column::new("id", 20), Column::new("name", 20)],
        );
        browser.set_items(vec![
            Item::new("alpha", vec!["alpha".into(), "Alpha Model".into()])
                .with_detail(vec!["detail a1".into(), "detail a2".into()]),
            Item::new("beta", vec!["beta".into(), "Beta Model".into()])
                .with_detail(vec!["detail b".into()]),
            Item::new("gamma", vec!["gamma".into(), "Gamma Model".into()]),
        ]);
        browser
    }

    #[test]
    fn starts_on_the_first_row_in_list_view() {
        let browser = browser();
        assert_eq!(browser.view(), View::List);
        assert_eq!(browser.selected_id(), Some("alpha"));
        assert_eq!(browser.len(), 3);
    }

    #[test]
    fn moves_with_arrows_and_vim_keys() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Down));
        assert_eq!(browser.selected_id(), Some("beta"));
        browser.handle(key(KeyCode::Char('j')));
        assert_eq!(browser.selected_id(), Some("gamma"));
        browser.handle(key(KeyCode::Char('k')));
        assert_eq!(browser.selected_id(), Some("beta"));
        browser.handle(key(KeyCode::Up));
        assert_eq!(browser.selected_id(), Some("alpha"));
    }

    #[test]
    fn selection_stops_at_both_ends() {
        let mut browser = browser();
        for _ in 0..10 {
            browser.handle(key(KeyCode::Up));
        }
        assert_eq!(browser.selected_id(), Some("alpha"));
        for _ in 0..10 {
            browser.handle(key(KeyCode::Down));
        }
        assert_eq!(browser.selected_id(), Some("gamma"));
    }

    #[test]
    fn home_and_end_jump_to_the_ends() {
        let mut browser = browser();
        browser.handle(key(KeyCode::End));
        assert_eq!(browser.selected_id(), Some("gamma"));
        browser.handle(key(KeyCode::Home));
        assert_eq!(browser.selected_id(), Some("alpha"));
    }

    #[test]
    fn paging_moves_by_a_page_and_clamps() {
        let mut browser = browser();
        browser.handle(key(KeyCode::PageDown));
        assert_eq!(browser.selected_id(), Some("gamma"), "should clamp to the end");
        browser.handle(key(KeyCode::PageUp));
        assert_eq!(browser.selected_id(), Some("alpha"));
    }

    #[test]
    fn escape_closes_from_the_list() {
        let mut browser = browser();
        assert_eq!(browser.handle(key(KeyCode::Esc)), Intent::Close);
        assert_eq!(browser.handle(key(KeyCode::Char('q'))), Intent::Close);
    }

    #[test]
    fn enter_opens_the_detail_pane() {
        let mut browser = browser();
        assert_eq!(browser.handle(key(KeyCode::Enter)), Intent::Redraw);
        assert_eq!(browser.view(), View::Detail);
        assert_eq!(browser.detail_lines().len(), 2);
    }

    #[test]
    fn escape_returns_from_detail_to_the_list_without_closing() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Enter));
        assert_eq!(browser.handle(key(KeyCode::Esc)), Intent::Redraw);
        assert_eq!(browser.view(), View::List);
    }

    #[test]
    fn enter_activates_a_row_that_has_no_detail() {
        let mut browser = browser();
        browser.handle(key(KeyCode::End));
        assert_eq!(
            browser.handle(key(KeyCode::Enter)),
            Intent::Activate("gamma".into())
        );
        assert_eq!(browser.view(), View::List);
    }

    #[test]
    fn a_list_only_browser_activates_instead_of_opening_detail() {
        let mut browser = browser().without_detail();
        assert_eq!(
            browser.handle(key(KeyCode::Enter)),
            Intent::Activate("alpha".into())
        );
    }

    #[test]
    fn arrows_scroll_the_detail_pane_instead_of_moving_the_selection() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Enter));
        browser.handle(key(KeyCode::Down));

        assert_eq!(browser.detail_scroll(), 1);
        assert_eq!(browser.selected_id(), Some("alpha"), "selection must not move");
    }

    #[test]
    fn detail_scroll_stops_at_the_last_line() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Enter));
        for _ in 0..20 {
            browser.handle(key(KeyCode::Down));
        }
        assert_eq!(browser.detail_scroll(), 1, "two detail lines means max index 1");
    }

    #[test]
    fn space_toggles_the_selected_row() {
        let mut browser = browser();
        assert_eq!(
            browser.handle(key(KeyCode::Char(' '))),
            Intent::Toggle("alpha".into())
        );
    }

    #[test]
    fn delete_targets_the_selected_row() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Down));
        assert_eq!(
            browser.handle(key(KeyCode::Delete)),
            Intent::Delete("beta".into())
        );
    }

    #[test]
    fn r_requests_a_reload() {
        let mut browser = browser();
        assert_eq!(browser.handle(key(KeyCode::Char('r'))), Intent::Reload);
    }

    #[test]
    fn unregistered_keys_are_ignored() {
        let mut browser = browser();
        assert_eq!(browser.handle(key(KeyCode::Char('z'))), Intent::Ignored);
    }

    #[test]
    fn registered_extra_keys_are_reported_with_the_selection() {
        let mut browser = browser().with_extra_keys(&['a', 'e', 'u']);
        assert_eq!(
            browser.handle(key(KeyCode::Char('e'))),
            Intent::Key('e', Some("alpha".into()))
        );
        assert_eq!(browser.handle(key(KeyCode::Char('z'))), Intent::Ignored);
    }

    #[test]
    fn slash_enters_filter_mode() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Char('/')));
        assert!(browser.is_filtering());
        assert_eq!(browser.filter_text(), Some(""));
    }

    #[test]
    fn typing_in_filter_mode_narrows_the_list() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Char('/')));
        for c in "bet".chars() {
            browser.handle(key(KeyCode::Char(c)));
        }

        assert_eq!(browser.len(), 1);
        assert_eq!(browser.selected_id(), Some("beta"));
    }

    #[test]
    fn filter_matching_is_case_insensitive_and_spans_all_cells() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Char('/')));
        // Upper case, and matching text that spans two different cells.
        for c in "GAMMA MODEL".chars() {
            browser.handle(key(KeyCode::Char(c)));
        }

        assert_eq!(browser.len(), 1);
        assert_eq!(browser.selected_id(), Some("gamma"));
    }

    #[test]
    fn a_filter_matching_nothing_leaves_an_empty_list() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Char('/')));
        for c in "nothing-matches-this".chars() {
            browser.handle(key(KeyCode::Char(c)));
        }

        assert!(browser.is_empty());
        assert_eq!(browser.selected_id(), None);
    }

    #[test]
    fn backspace_widens_the_filter_again() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Char('/')));
        for c in "beta".chars() {
            browser.handle(key(KeyCode::Char(c)));
        }
        assert_eq!(browser.len(), 1);

        browser.handle(key(KeyCode::Backspace));
        browser.handle(key(KeyCode::Backspace));
        browser.handle(key(KeyCode::Backspace));
        browser.handle(key(KeyCode::Backspace));
        assert_eq!(browser.len(), 3);
    }

    #[test]
    fn enter_leaves_filter_mode_keeping_the_filter_applied() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Char('/')));
        for c in "beta".chars() {
            browser.handle(key(KeyCode::Char(c)));
        }
        browser.handle(key(KeyCode::Enter));

        assert!(!browser.is_filtering());
        assert_eq!(browser.len(), 1, "the narrowing should survive");
    }

    #[test]
    fn escape_from_filter_mode_clears_the_filter() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Char('/')));
        for c in "beta".chars() {
            browser.handle(key(KeyCode::Char(c)));
        }
        browser.handle(key(KeyCode::Esc));

        assert!(!browser.is_filtering());
        assert_eq!(browser.len(), 3);
    }

    #[test]
    fn escape_from_an_empty_filter_just_exits_the_mode() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Char('/')));
        browser.handle(key(KeyCode::Esc));

        assert!(!browser.is_filtering());
        assert_eq!(browser.len(), 3);
    }

    #[test]
    fn filter_mode_does_not_close_the_overlay_or_move_the_selection() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Char('/')));
        // `q` and `j` are navigation keys outside filter mode.
        assert_eq!(browser.handle(key(KeyCode::Char('q'))), Intent::Redraw);
        assert_eq!(browser.handle(key(KeyCode::Char('j'))), Intent::Redraw);
        assert_eq!(browser.filter_text(), Some("qj"));
    }

    #[test]
    fn a_reload_preserves_the_selected_row_by_id() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Down));
        assert_eq!(browser.selected_id(), Some("beta"));

        // A row is inserted ahead of the selection.
        browser.set_items(vec![
            Item::new("new", vec!["new".into(), "New".into()]),
            Item::new("alpha", vec!["alpha".into(), "Alpha Model".into()]),
            Item::new("beta", vec!["beta".into(), "Beta Model".into()]),
        ]);

        assert_eq!(
            browser.selected_id(),
            Some("beta"),
            "the selection followed the index instead of the row"
        );
    }

    #[test]
    fn a_reload_that_removes_the_selection_clamps_safely() {
        let mut browser = browser();
        browser.handle(key(KeyCode::End));
        browser.set_items(vec![Item::new("only", vec!["only".into(), "Only".into()])]);

        assert_eq!(browser.selected_id(), Some("only"));
    }

    #[test]
    fn an_empty_browser_reports_no_selection_and_never_panics() {
        let mut browser = Browser::new("Empty", vec![Column::new("id", 10)]);
        assert!(browser.is_empty());
        assert_eq!(browser.selected_id(), None);

        for code in [
            KeyCode::Up,
            KeyCode::Down,
            KeyCode::Home,
            KeyCode::End,
            KeyCode::PageUp,
            KeyCode::PageDown,
            KeyCode::Enter,
            KeyCode::Char(' '),
            KeyCode::Delete,
        ] {
            let _ = browser.handle(key(code));
        }
        assert_eq!(browser.selected_id(), None);
    }

    #[test]
    fn cells_are_truncated_to_their_column_width() {
        let browser = Browser::new("t", vec![Column::new("id", 6)]);
        let row = browser.format_row(&Item::new("x", vec!["a-very-long-value".into()]));
        assert_eq!(row[0], "a-ver…");
        assert_eq!(text::width(&row[0]), 6);
    }

    #[test]
    fn a_missing_cell_renders_as_empty() {
        let browser = Browser::new("t", vec![Column::new("a", 5), Column::new("b", 5)]);
        let row = browser.format_row(&Item::new("x", vec!["only".into()]));
        assert_eq!(row, vec!["only", ""]);
    }

    #[test]
    fn key_releases_are_ignored() {
        use crossterm::event::KeyEventKind;
        let mut browser = browser();
        let mut event = key(KeyCode::Down);
        event.kind = KeyEventKind::Release;
        assert_eq!(browser.handle(event), Intent::Ignored);
        assert_eq!(browser.selected_id(), Some("alpha"));
    }

    #[test]
    fn moving_the_selection_resets_the_detail_scroll() {
        let mut browser = browser();
        browser.handle(key(KeyCode::Enter));
        browser.handle(key(KeyCode::Down));
        assert_eq!(browser.detail_scroll(), 1);

        browser.handle(key(KeyCode::Esc));
        browser.handle(key(KeyCode::Down));
        assert_eq!(browser.detail_scroll(), 0);
    }
}
