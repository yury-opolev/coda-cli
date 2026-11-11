//! The body of an engine question: wrapped text, options, a free-text field
//! and image attachments, in one scrollable column.
//!
//! Owned by [`PromptSurface`](super::prompt::PromptSurface), which keeps the
//! `Exclusive` modality and turns a [`QuestionOutcome`] into the reply. Like
//! every surface this never reaches the engine or the clipboard: a paste chord
//! asks for a clipboard read, and the application feeds the result back
//! through [`QuestionView::paste_text`] or [`QuestionView::attach_image`].

use std::cell::Cell;

use crate::render::glyphs;
use crate::widgets::{Control, TextArea};
use coda_proto::messages::WireImage;
use coda_render::text;
use coda_render::theme::{Role, Theme};
use crossterm::event::{KeyCode, KeyEvent, KeyModifiers, MouseButton, MouseEvent, MouseEventKind};
use ratatui::layout::Rect;
use ratatui::style::Style;
use ratatui::text::{Line, Span};

/// The most images one answer may carry.
pub const MAX_QUESTION_IMAGES: usize = 5;

const FIELD_LABEL: &str = "Your answer / explanation (optional when an option is chosen)";
const SYNTHETIC_OTHER: &str = "Other — type your own answer below";
const UNSUPPORTED_IMAGES: &str = "Images can't be sent with this answer: the current model or engine \
     does not accept image input. Describe it in text instead.";
const TOO_MANY_IMAGES: &str = "At most 5 images can accompany an answer.";
const EMPTY_OTHER: &str = "Type your answer in the field below (or choose another option).";
const NOTHING_CHOSEN: &str = "Choose an option or type an answer.";
const IMAGES_ALONE: &str = "Add a short description or choose an option to go with the image(s).";
const NO_FIELD: &str = "This question takes one of the listed options; typed text cannot be added.";
const DETAILS_HEADING: &str = "Additional details from the user:";

/// Rows the wheel moves per notch.
const WHEEL_ROWS: usize = 3;
/// Rows the answer field shows, before and after it grows with its content.
const FIELD_MIN_ROWS: usize = 3;
const FIELD_MAX_ROWS: usize = 8;

/// What a key, click or paste did to the question.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum QuestionOutcome {
    Handled,
    Submit { answer: String },
    Cancel,
    /// Read the clipboard: an image is attached, text is pasted.
    RequestPaste,
}

/// The row that stands for "my own answer".
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum OtherRow {
    /// Added after the model's options.
    Synthetic,
    /// One of the model's own options, reused rather than duplicated.
    Supplied(usize),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Zone {
    Options,
    Field,
    Attachments,
}

/// Whether an option the model supplied already means "something else".
///
/// Reused as the Other row so the list does not offer the same escape hatch
/// twice, and so choosing it opens the field instead of sending its label.
pub fn looks_like_other(label: &str) -> bool {
    let label = label.trim().to_lowercase();
    if label == "other" {
        return true;
    }
    if let Some(rest) = label.strip_prefix("other") {
        if rest.chars().next().is_some_and(|c| !c.is_ascii_alphabetic()) {
            return true;
        }
    }
    ["type your own", "your own answer", "something else", "custom answer"]
        .iter()
        .any(|phrase| label.contains(phrase))
}

/// Every row laid out at one width, with the ranges that scrolling and
/// hit-testing need. Recomputed per frame, so a resize loses nothing.
struct Layout {
    lines: Vec<Line<'static>>,
    /// Inclusive line range scrolling keeps visible.
    focus_rows: (usize, usize),
    option_rows: Vec<(usize, usize)>,
    field_rows: Option<(usize, usize)>,
    /// The caret as (column, line), when the field has focus.
    caret: Option<(u16, usize)>,
}

pub struct QuestionView {
    question: String,
    options: Vec<String>,
    other: Option<OtherRow>,
    multi_select: bool,
    allow_free_text: bool,
    accepts_images: bool,
    highlighted: usize,
    selected: Option<usize>,
    ticked: Vec<bool>,
    field: TextArea,
    focus: Zone,
    attachments: Vec<(String, WireImage)>,
    attachment_cursor: usize,
    next_image_number: usize,
    message: Option<(String, bool)>,
    manual_scroll: Option<usize>,
    last_scroll: Cell<usize>,
    last_height: Cell<u16>,
    overflowing: Cell<bool>,
}

impl QuestionView {
    pub fn new(
        question: String,
        options: Vec<String>,
        multi_select: bool,
        allow_free_text: bool,
        accepts_images: bool,
    ) -> Self {
        // Without a field there is nowhere to type, so an "other" option is
        // just an option and sends its label.
        let other = if allow_free_text && !options.is_empty() {
            Some(
                options
                    .iter()
                    .position(|o| looks_like_other(o))
                    .map(OtherRow::Supplied)
                    .unwrap_or(OtherRow::Synthetic),
            )
        } else {
            None
        };
        let rows = options.len() + usize::from(other == Some(OtherRow::Synthetic));
        let focus = if rows == 0 && allow_free_text {
            Zone::Field
        } else {
            Zone::Options
        };
        Self {
            question,
            options,
            other,
            multi_select,
            allow_free_text,
            accepts_images,
            highlighted: 0,
            selected: None,
            ticked: vec![false; rows],
            field: TextArea::new(FIELD_LABEL).with_soft_wrap(),
            focus,
            attachments: Vec::new(),
            attachment_cursor: 0,
            next_image_number: 1,
            message: None,
            manual_scroll: None,
            last_scroll: Cell::new(0),
            last_height: Cell::new(0),
            overflowing: Cell::new(false),
        }
    }

    fn row_count(&self) -> usize {
        self.ticked.len()
    }

    fn row_label(&self, row: usize) -> &str {
        self.options.get(row).map(String::as_str).unwrap_or(SYNTHETIC_OTHER)
    }

    fn is_other(&self, row: usize) -> bool {
        match self.other {
            Some(OtherRow::Supplied(index)) => row == index,
            Some(OtherRow::Synthetic) => row == self.options.len(),
            None => false,
        }
    }

    fn is_chosen(&self, row: usize) -> bool {
        if self.multi_select {
            self.ticked.get(row).copied().unwrap_or(false)
        } else {
            self.selected == Some(row)
        }
    }

    /// The zones Tab cycles through, in order.
    fn zones(&self) -> Vec<Zone> {
        let mut zones = Vec::new();
        if self.row_count() > 0 {
            zones.push(Zone::Options);
        }
        if self.allow_free_text {
            zones.push(Zone::Field);
        }
        if !self.attachments.is_empty() {
            zones.push(Zone::Attachments);
        }
        if zones.is_empty() {
            zones.push(Zone::Options);
        }
        zones
    }

    fn cycle_focus(&mut self, forward: bool) {
        let zones = self.zones();
        let here = zones.iter().position(|z| *z == self.focus).unwrap_or(0);
        let next = if forward {
            (here + 1) % zones.len()
        } else {
            (here + zones.len() - 1) % zones.len()
        };
        self.focus = zones[next];
    }

    fn set_message(&mut self, message: impl Into<String>) {
        self.message = Some((message.into(), true));
    }

    /// Shows a message under the question, e.g. a clipboard error.
    pub fn show_message(&mut self, message: impl Into<String>) {
        self.set_message(message);
    }

    pub fn handle_key(&mut self, key: KeyEvent) -> QuestionOutcome {
        if key.code == KeyCode::Esc {
            return QuestionOutcome::Cancel;
        }
        let ctrl_or_alt = key.modifiers.intersects(KeyModifiers::CONTROL | KeyModifiers::ALT);
        let paste = matches!(key.code, KeyCode::Char('v') | KeyCode::Char('V')) && ctrl_or_alt
            || key.code == KeyCode::Insert && key.modifiers.contains(KeyModifiers::SHIFT);
        if paste {
            return QuestionOutcome::RequestPaste;
        }
        match key.code {
            KeyCode::PageUp | KeyCode::PageDown => {
                let page = usize::from(self.last_height.get()).max(1);
                let from = self.last_scroll.get();
                self.manual_scroll = Some(if key.code == KeyCode::PageUp {
                    from.saturating_sub(page)
                } else {
                    from + page
                });
                return QuestionOutcome::Handled;
            }
            KeyCode::Tab => {
                self.cycle_focus(true);
                self.manual_scroll = None;
                return QuestionOutcome::Handled;
            }
            KeyCode::BackTab => {
                self.cycle_focus(false);
                self.manual_scroll = None;
                return QuestionOutcome::Handled;
            }
            _ => {}
        }
        self.manual_scroll = None;

        // A chord never answers. The exceptions are the composer's newline
        // chords inside the field, which only edit.
        if key
            .modifiers
            .intersects(KeyModifiers::CONTROL | KeyModifiers::ALT | KeyModifiers::SUPER)
        {
            let newline = key.code == KeyCode::Enter
                || key.code == KeyCode::Char('j') && key.modifiers.contains(KeyModifiers::CONTROL);
            if self.focus == Zone::Field && self.allow_free_text && newline {
                self.field.insert_text("\n");
                self.message = None;
            }
            return QuestionOutcome::Handled;
        }

        match self.focus {
            Zone::Options => self.options_key(key),
            Zone::Field => self.field_key(key),
            Zone::Attachments => self.attachments_key(key),
        }
    }

    fn options_key(&mut self, key: KeyEvent) -> QuestionOutcome {
        let rows = self.row_count();
        if rows == 0 {
            return match key.code {
                KeyCode::Enter => self.try_submit(),
                _ => QuestionOutcome::Handled,
            };
        }
        match key.code {
            KeyCode::Up => self.highlighted = self.highlighted.saturating_sub(1),
            KeyCode::Down => self.highlighted = (self.highlighted + 1).min(rows - 1),
            KeyCode::Home => self.highlighted = 0,
            KeyCode::End => self.highlighted = rows - 1,
            KeyCode::Char(' ') => self.choose(self.highlighted),
            KeyCode::Char(c) if c.is_ascii_digit() => {
                // A digit past the end does nothing rather than answering
                // something the user cannot see.
                let Some(row) = c
                    .to_digit(10)
                    .and_then(|d| (d as usize).checked_sub(1))
                    .filter(|row| *row < rows)
                else {
                    return QuestionOutcome::Handled;
                };
                self.highlighted = row;
                self.choose(row);
                if !self.multi_select && !self.is_other(row) {
                    return self.try_submit();
                }
            }
            KeyCode::Enter => {
                if self.multi_select {
                    return self.try_submit();
                }
                let row = self.highlighted;
                self.choose(row);
                if !self.is_other(row) {
                    return self.try_submit();
                }
            }
            _ => {}
        }
        QuestionOutcome::Handled
    }

    fn field_key(&mut self, key: KeyEvent) -> QuestionOutcome {
        if !self.allow_free_text {
            return QuestionOutcome::Handled;
        }
        if key.code == KeyCode::Enter {
            if key.modifiers.contains(KeyModifiers::SHIFT) {
                self.field.insert_text("\n");
                self.message = None;
                return QuestionOutcome::Handled;
            }
            return self.try_submit();
        }
        self.field.handle_key(key);
        self.message = None;
        QuestionOutcome::Handled
    }

    fn attachments_key(&mut self, key: KeyEvent) -> QuestionOutcome {
        match key.code {
            KeyCode::Left => self.attachment_cursor = self.attachment_cursor.saturating_sub(1),
            KeyCode::Right => {
                self.attachment_cursor =
                    (self.attachment_cursor + 1).min(self.attachments.len().saturating_sub(1));
            }
            KeyCode::Delete | KeyCode::Backspace => {
                if self.attachment_cursor < self.attachments.len() {
                    self.attachments.remove(self.attachment_cursor);
                }
                self.attachment_cursor =
                    self.attachment_cursor.min(self.attachments.len().saturating_sub(1));
                if self.attachments.is_empty() {
                    self.focus = if self.allow_free_text {
                        Zone::Field
                    } else {
                        Zone::Options
                    };
                }
            }
            _ => {}
        }
        QuestionOutcome::Handled
    }

    /// Selects (single) or toggles (multi) a row. The Other row hands the
    /// keyboard to the field instead of standing for its own label.
    fn choose(&mut self, row: usize) {
        if row >= self.row_count() {
            return;
        }
        let now_chosen = if self.multi_select {
            self.ticked[row] = !self.ticked[row];
            self.ticked[row]
        } else {
            self.selected = Some(row);
            true
        };
        if now_chosen && self.is_other(row) {
            self.focus = Zone::Field;
            self.message = None;
        }
    }

    fn try_submit(&mut self) -> QuestionOutcome {
        let rows = self.row_count();
        let other_chosen = (0..rows).any(|row| self.is_other(row) && self.is_chosen(row));
        let labels: Vec<&str> = (0..rows)
            .filter(|row| !self.is_other(*row) && self.is_chosen(*row))
            .map(|row| self.row_label(row))
            .collect();
        let value = self.field.value();
        let typed = value.trim();

        if other_chosen && typed.is_empty() {
            self.set_message(EMPTY_OTHER);
            if self.allow_free_text {
                self.focus = Zone::Field;
            }
            return QuestionOutcome::Handled;
        }
        let answer = match (labels.is_empty(), typed.is_empty()) {
            (true, true) => {
                let message = if self.attachments.is_empty() {
                    NOTHING_CHOSEN
                } else {
                    IMAGES_ALONE
                };
                self.set_message(message);
                return QuestionOutcome::Handled;
            }
            (true, false) => typed.to_string(),
            (false, true) => labels.join(", "),
            (false, false) => format!("{}\n\n{DETAILS_HEADING}\n{typed}", labels.join(", ")),
        };
        QuestionOutcome::Submit { answer }
    }

    pub fn handle_mouse(&mut self, event: MouseEvent, content: Rect) -> QuestionOutcome {
        match event.kind {
            MouseEventKind::ScrollUp => {
                self.manual_scroll = Some(self.last_scroll.get().saturating_sub(WHEEL_ROWS));
            }
            MouseEventKind::ScrollDown => {
                self.manual_scroll = Some(self.last_scroll.get() + WHEEL_ROWS);
            }
            MouseEventKind::Down(MouseButton::Left) => {
                let inside = event.row >= content.y
                    && event.row < content.y.saturating_add(content.height)
                    && event.column >= content.x
                    && event.column < content.x.saturating_add(content.width);
                if !inside {
                    return QuestionOutcome::Handled;
                }
                let line = usize::from(event.row - content.y) + self.last_scroll.get();
                let layout = self.layout(content.width.max(1) as usize, &Theme::default());
                let within = |(start, end): (usize, usize)| line >= start && line <= end;
                if let Some(row) = layout.option_rows.iter().position(|r| within(*r)) {
                    self.highlighted = row;
                    self.focus = Zone::Options;
                    self.manual_scroll = None;
                    // A click chooses but never submits: a stray click must
                    // not send an answer.
                    self.choose(row);
                } else if layout.field_rows.is_some_and(within) {
                    self.focus = Zone::Field;
                    self.manual_scroll = None;
                }
            }
            _ => {}
        }
        QuestionOutcome::Handled
    }

    /// Inserts pasted text into the answer field and focuses it.
    pub fn paste_text(&mut self, pasted: &str) {
        if !self.allow_free_text {
            self.set_message(NO_FIELD);
            return;
        }
        self.field.insert_text(pasted);
        self.focus = Zone::Field;
        self.message = None;
        self.manual_scroll = None;
    }

    /// Attaches an image to the answer. The error is also shown in the modal.
    pub fn attach_image(&mut self, media_type: &str, base64: String) -> Result<(), String> {
        let refusal = if !self.accepts_images {
            Some(UNSUPPORTED_IMAGES)
        } else if self.attachments.len() >= MAX_QUESTION_IMAGES {
            Some(TOO_MANY_IMAGES)
        } else {
            None
        };
        if let Some(refusal) = refusal {
            self.set_message(refusal);
            return Err(refusal.to_string());
        }
        let extension = match media_type {
            "image/jpeg" | "image/jpg" => "jpg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            _ => "png",
        };
        let label = format!("image-{}.{extension}", self.next_image_number);
        self.next_image_number += 1;
        self.attachments.push((
            label,
            WireImage {
                media_type: media_type.to_string(),
                base64,
            },
        ));
        self.message = None;
        Ok(())
    }

    pub fn images(&self) -> Vec<WireImage> {
        self.attachments.iter().map(|(_, image)| image.clone()).collect()
    }

    pub fn attachment_count(&self) -> usize {
        self.attachments.len()
    }

    /// Key hints, the focused zone's first so they survive truncation.
    pub fn hints(&self) -> String {
        let mut hints: Vec<String> = Vec::new();
        match self.focus {
            Zone::Options if self.row_count() > 0 => {
                hints.push(format!("{} move", glyphs::ARROWS_VERTICAL));
                hints.push(if self.multi_select { "Space tick" } else { "Space select" }.into());
                hints.push("1-9 pick".into());
                hints.push("Enter submit".into());
                if self.allow_free_text {
                    hints.push("Tab answer field".into());
                } else if !self.attachments.is_empty() {
                    hints.push("Tab attachments".into());
                }
            }
            Zone::Options => hints.push("Enter submit".into()),
            Zone::Field => {
                hints.push("Enter submit".into());
                hints.push("Shift+Enter newline".into());
                if self.row_count() > 0 {
                    hints.push("Tab options".into());
                } else if !self.attachments.is_empty() {
                    hints.push("Tab attachments".into());
                }
            }
            Zone::Attachments => {
                hints.push(format!("{}{} choose", glyphs::ARROW_LEFT, glyphs::ARROW_RIGHT));
                hints.push("Del remove".into());
            }
        }
        if self.accepts_images {
            hints.push("Ctrl+V/Alt+V paste image".into());
        } else if self.allow_free_text {
            hints.push("Ctrl+V/Alt+V paste".into());
        }
        if self.overflowing.get() {
            hints.push("PgUp/PgDn scroll".into());
        }
        hints.push("Esc cancel".into());
        hints.join("  ")
    }

    /// The number of rows the whole question needs at `width`.
    pub fn content_height(&self, width: u16, theme: &Theme) -> usize {
        self.layout(width.max(1) as usize, theme).lines.len()
    }

    fn layout(&self, width: usize, theme: &Theme) -> Layout {
        let mut lines: Vec<Line<'static>> = Vec::new();

        let prose = theme.style(Role::PromptText);
        for paragraph in self.question.split('\n') {
            for row in text::wrap(&text::sanitize(paragraph), width) {
                lines.push(Line::from(Span::styled(row, prose)));
            }
        }

        let mut option_rows = Vec::new();
        if self.row_count() > 0 {
            lines.push(Line::default());
            let heading = if self.multi_select {
                "Choose any that apply:"
            } else {
                "Choose one:"
            };
            lines.push(Line::from(Span::styled(heading, theme.style(Role::Notification))));
            for row in 0..self.row_count() {
                let start = lines.len();
                lines.extend(self.option_lines(row, width, theme));
                option_rows.push((start, lines.len() - 1));
            }
        }

        let mut field_rows = None;
        let mut caret = None;
        if self.allow_free_text {
            lines.push(Line::default());
            let start = lines.len();
            let focused = self.focus == Zone::Field;
            let mut field = self.field.clone();
            field.set_visible_rows(
                field
                    .visual_rows(width as u16)
                    .clamp(FIELD_MIN_ROWS, FIELD_MAX_ROWS),
            );
            let mut rendered = field.render(width as u16, focused, theme);
            let label_rows = relabel(&mut rendered, width);
            if focused {
                caret = field.cursor(width as u16).map(|(column, row)| {
                    (column, start + label_rows + usize::from(row).saturating_sub(1))
                });
            }
            lines.extend(rendered);
            field_rows = Some((start, lines.len() - 1));
        }

        let mut attachment_rows = None;
        if !self.attachments.is_empty() {
            let start = lines.len();
            lines.extend(self.attachment_lines(width, theme));
            attachment_rows = Some((start, lines.len() - 1));
        }

        if let Some((message, warning)) = &self.message {
            let (role, prefix) = if *warning {
                (Role::Warning, "⚠ ")
            } else {
                (Role::Notification, "")
            };
            let message = format!("{prefix}{}", text::sanitize(message));
            for row in text::wrap(&message, width) {
                lines.push(Line::from(Span::styled(row, theme.style(role))));
            }
        }

        let focus_rows = match self.focus {
            Zone::Options => option_rows.get(self.highlighted).copied(),
            // Only down to the caret: in a tall field, the caret is the row
            // that matters.
            Zone::Field => field_rows.map(|(start, end)| {
                (start, caret.map(|(_, line)| line).unwrap_or(end).min(end))
            }),
            Zone::Attachments => attachment_rows,
        }
        .unwrap_or((0, 0));

        let lines = lines.into_iter().map(|line| clip(line, width)).collect();
        Layout {
            lines,
            focus_rows,
            option_rows,
            field_rows,
            caret,
        }
    }

    fn option_lines(&self, row: usize, width: usize, theme: &Theme) -> Vec<Line<'static>> {
        let highlighted = self.focus == Zone::Options && row == self.highlighted;
        let marker = if highlighted {
            glyphs::OPTION_MARKER
        } else {
            glyphs::OPTION_BLANK
        };
        let chosen = self.is_chosen(row);
        let tick = match (self.multi_select, chosen) {
            (true, true) => glyphs::CHECK_ON,
            (true, false) => glyphs::CHECK_OFF,
            (false, true) => glyphs::RADIO_ON,
            (false, false) => glyphs::RADIO_OFF,
        };
        let number = if row < 9 {
            format!("{}. ", row + 1)
        } else {
            "   ".to_string()
        };
        let prefix = format!("{marker}{tick} {number}");
        let indent = text::width(&prefix);
        let style = if highlighted {
            theme
                .style(Role::CompletionSelectedText)
                .bg(theme.fg(Role::CompletionSelectedBackground))
        } else {
            theme.style(Role::CompletionNormal)
        };
        let label = text::sanitize(self.row_label(row));

        // The label is wrapped on its own and the prefix laid beside it, so
        // the indent is exact: wrapping the prefix with the label would drop
        // the blank marker's leading spaces and misalign unhighlighted rows.
        if width <= indent + 1 {
            return text::wrap(&format!("{prefix}{label}"), width)
                .into_iter()
                .map(|row| Line::from(Span::styled(row, style)))
                .collect();
        }
        let pad = " ".repeat(indent);
        text::wrap(&label, width - indent)
            .into_iter()
            .enumerate()
            .map(|(index, row)| {
                let lead = if index == 0 { prefix.clone() } else { pad.clone() };
                Line::from(Span::styled(format!("{lead}{row}"), style))
            })
            .collect()
    }

    fn attachment_lines(&self, width: usize, theme: &Theme) -> Vec<Line<'static>> {
        let heading = "Attachments: ";
        let mut lines = Vec::new();
        let mut current: Vec<Span<'static>> =
            vec![Span::styled(heading, theme.style(Role::Notification))];
        let mut used = text::width(heading);
        for (index, (label, _)) in self.attachments.iter().enumerate() {
            let chip = format!("[{} {label}]", glyphs::CAMERA);
            let chip_width = text::width(&chip);
            let gap = if used > text::width(heading) { 2 } else { 0 };
            if used + gap + chip_width > width && used > 0 {
                lines.push(Line::from(std::mem::take(&mut current)));
                used = 0;
            } else if gap > 0 {
                current.push(Span::raw("  "));
                used += gap;
            }
            let style = if self.focus == Zone::Attachments && index == self.attachment_cursor {
                theme
                    .style(Role::CompletionSelectedText)
                    .bg(theme.fg(Role::CompletionSelectedBackground))
            } else {
                theme.style(Role::PromptText)
            };
            current.push(Span::styled(chip, style));
            used += chip_width;
        }
        if !current.is_empty() {
            lines.push(Line::from(current));
        }
        lines
    }

    /// The first visible line: the manual offset while the user scrolls,
    /// otherwise the last offset moved just enough to show the focus.
    fn scroll_for(&self, layout: &Layout, height: usize) -> usize {
        let max = layout.lines.len().saturating_sub(height);
        let scroll = match self.manual_scroll {
            Some(manual) => manual,
            None => {
                let (start, end) = layout.focus_rows;
                let mut scroll = self.last_scroll.get();
                if end + 1 - start > height {
                    scroll = if self.focus == Zone::Field {
                        end + 1 - height
                    } else {
                        start
                    };
                } else if start < scroll {
                    scroll = start;
                } else if end >= scroll + height {
                    scroll = end + 1 - height;
                }
                scroll
            }
        };
        scroll.min(max)
    }

    pub fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        let height = area.height as usize;
        let layout = self.layout(area.width.max(1) as usize, theme);
        self.last_height.set(area.height);
        self.overflowing.set(layout.lines.len() > height);
        if height == 0 {
            return Vec::new();
        }
        let scroll = self.scroll_for(&layout, height);
        self.last_scroll.set(scroll);
        layout.lines.into_iter().skip(scroll).take(height).collect()
    }

    pub fn cursor(&self, area: Rect, theme: &Theme) -> Option<(u16, u16)> {
        let height = area.height as usize;
        if height == 0 || area.width == 0 {
            return None;
        }
        let layout = self.layout(area.width as usize, theme);
        let (column, line) = layout.caret?;
        let scroll = self.scroll_for(&layout, height);
        let row = line.checked_sub(scroll).filter(|row| *row < height)?;
        Some((column.min(area.width - 1), row as u16))
    }
}

/// Replaces the field's single label row with the label wrapped to `width`,
/// keeping its gutter and styles. Returns how many rows the label now takes.
fn relabel(rendered: &mut Vec<Line<'static>>, width: usize) -> usize {
    if rendered.is_empty() {
        return 0;
    }
    let first = rendered.remove(0);
    let mut spans = first.spans.into_iter();
    let gutter = spans.next().unwrap_or_default();
    let label = spans.next().unwrap_or_default();
    let gutter_width = text::width(&gutter.content);
    let budget = width.saturating_sub(gutter_width).max(1);
    let rows: Vec<Line<'static>> = text::wrap(&label.content, budget)
        .into_iter()
        .enumerate()
        .map(|(index, row)| {
            let lead = if index == 0 {
                gutter.clone()
            } else {
                Span::styled(" ".repeat(gutter_width), gutter.style)
            };
            Line::from(vec![lead, Span::styled(row, label.style)])
        })
        .collect();
    let count = rows.len();
    rendered.splice(0..0, rows);
    count
}

/// Cuts a line to `width` cells. Wrapping already fits every row at any
/// usable width; this only bites in a terminal too narrow to hold a gutter.
fn clip(line: Line<'static>, width: usize) -> Line<'static> {
    let total: usize = line.spans.iter().map(|s| text::width(&s.content)).sum();
    if total <= width {
        return line;
    }
    let mut left = width;
    let mut spans = Vec::new();
    for span in line.spans {
        if left == 0 {
            break;
        }
        let cut = text::truncate(&span.content, left);
        left = left.saturating_sub(text::width(&cut));
        let style: Style = span.style;
        spans.push(Span::styled(cut, style));
    }
    Line::from(spans)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn chord(code: KeyCode, modifiers: KeyModifiers) -> KeyEvent {
        KeyEvent::new(code, modifiers)
    }

    fn text(lines: &[Line<'static>]) -> String {
        lines
            .iter()
            .map(|l| l.spans.iter().map(|s| s.content.to_string()).collect::<String>())
            .collect::<Vec<_>>()
            .join("\n")
    }

    fn widths(lines: &[Line<'static>]) -> Vec<usize> {
        lines
            .iter()
            .map(|l| l.spans.iter().map(|s| text::width(&s.content)).sum())
            .collect()
    }

    fn q(options: &[&str], multi: bool) -> QuestionView {
        QuestionView::new(
            "Which approach?".into(),
            options.iter().map(|o| o.to_string()).collect(),
            multi,
            true,
            true,
        )
    }

    fn typed(view: &mut QuestionView, s: &str) {
        for c in s.chars() {
            view.handle_key(key(KeyCode::Char(c)));
        }
    }

    fn shown(view: &QuestionView, w: u16, h: u16) -> String {
        text(&view.render(Rect::new(0, 0, w, h), &Theme::default()))
    }

    fn submitted(answer: &str) -> QuestionOutcome {
        QuestionOutcome::Submit {
            answer: answer.into(),
        }
    }

    fn words(prefix: &str, chars: usize) -> String {
        let mut out = String::new();
        let mut i = 0;
        while out.len() < chars {
            out.push_str(&format!("{prefix}{i} "));
            i += 1;
        }
        out.trim_end().to_string()
    }

    #[test]
    fn a_long_question_and_long_options_render_in_full_without_truncation() {
        let question = words("q", 300);
        let options: Vec<String> = ["a", "b", "c"].iter().map(|p| words(p, 150)).collect();
        let view = QuestionView::new(question.clone(), options.clone(), false, true, true);
        let lines = view.render(Rect::new(0, 0, 60, 200), &Theme::default());
        let joined = lines
            .iter()
            .map(|l| l.spans.iter().map(|s| s.content.to_string()).collect::<String>())
            .collect::<Vec<_>>()
            .join(" ");
        let found: std::collections::HashSet<&str> = joined.split_whitespace().collect();
        for word in question.split(' ').chain(options.iter().flat_map(|o| o.split(' '))) {
            assert!(found.contains(word), "{word} is missing from {joined}");
        }
        assert!(widths(&lines).iter().all(|w| *w <= 60), "{:?}", widths(&lines));
    }

    #[test]
    fn wide_glyph_options_never_overflow_the_row() {
        let wide = "\u{754C}".repeat(80);
        let view = q(&[&wide, "b"], false);
        let lines = view.render(Rect::new(0, 0, 30, 200), &Theme::default());
        assert!(widths(&lines).iter().all(|w| *w <= 30), "{:?}", widths(&lines));
        let total: usize = text(&lines).matches('\u{754C}').count();
        assert_eq!(total, 80, "a wide glyph was dropped");
    }

    #[test]
    fn every_row_is_reachable_by_scrolling_on_a_small_terminal() {
        let options: Vec<String> = (0..40).map(|i| format!("{} last{i}", words("w", 40))).collect();
        let refs: Vec<&str> = options.iter().map(String::as_str).collect();
        let mut view = q(&refs, false);
        let mut seen = String::new();
        for _ in 0..200 {
            seen.push_str(&shown(&view, 30, 10));
            view.handle_key(key(KeyCode::PageDown));
        }
        assert!(seen.contains("last39"), "the last option was never shown");

        let mut view = q(&refs, false);
        shown(&view, 30, 10);
        view.handle_key(key(KeyCode::End));
        assert!(shown(&view, 30, 10).contains("last39"));
    }

    #[test]
    fn single_choice_enter_submits_the_highlighted_option() {
        let mut view = q(&["first", "second", "third"], false);
        view.handle_key(key(KeyCode::Down));
        assert_eq!(view.handle_key(key(KeyCode::Enter)), submitted("second"));
    }

    #[test]
    fn single_choice_space_selects_without_submitting_and_text_adds_details() {
        let mut view = q(&["first", "second"], false);
        assert_eq!(view.handle_key(key(KeyCode::Char(' '))), QuestionOutcome::Handled);
        view.handle_key(key(KeyCode::Tab));
        typed(&mut view, "because");
        assert_eq!(
            view.handle_key(key(KeyCode::Enter)),
            submitted("first\n\nAdditional details from the user:\nbecause")
        );
    }

    #[test]
    fn multi_select_submits_ticks_and_explanation_together() {
        let mut view = q(&["one", "two", "three"], true);
        assert_eq!(view.handle_key(key(KeyCode::Char('3'))), QuestionOutcome::Handled);
        view.handle_key(key(KeyCode::Char('1')));
        view.handle_key(key(KeyCode::Tab));
        typed(&mut view, "both");
        assert_eq!(
            view.handle_key(key(KeyCode::Enter)),
            submitted("one, three\n\nAdditional details from the user:\nboth")
        );
    }

    #[test]
    fn multi_with_nothing_chosen_refuses_with_a_hint() {
        let mut view = q(&["one", "two"], true);
        assert_eq!(view.handle_key(key(KeyCode::Enter)), QuestionOutcome::Handled);
        assert!(shown(&view, 80, 40).contains("Choose an option or type an answer"));
    }

    #[test]
    fn choosing_other_focuses_the_field_instead_of_submitting() {
        let mut view = q(&["first", "second"], false);
        assert!(shown(&view, 80, 40).contains("3. Other"));
        assert_eq!(view.handle_key(key(KeyCode::Char('3'))), QuestionOutcome::Handled);
        assert!(view.hints().starts_with("Enter submit"), "{}", view.hints());
        assert_eq!(view.handle_key(key(KeyCode::Enter)), QuestionOutcome::Handled);
        assert!(shown(&view, 80, 40).contains("Type your answer"));
        typed(&mut view, "mine");
        assert_eq!(view.handle_key(key(KeyCode::Enter)), submitted("mine"));
    }

    #[test]
    fn a_model_supplied_other_option_is_reused_not_duplicated() {
        let mut view = q(&["A", "Other (please specify)"], false);
        let rendered = shown(&view, 80, 40);
        assert_eq!(
            rendered.lines().filter(|l| l.contains("Other")).count(),
            1,
            "{rendered}"
        );
        view.handle_key(key(KeyCode::Down));
        assert_eq!(view.handle_key(key(KeyCode::Enter)), QuestionOutcome::Handled);
        assert!(view.hints().starts_with("Enter submit"));
        typed(&mut view, "mine");
        assert_eq!(view.handle_key(key(KeyCode::Enter)), submitted("mine"));
    }

    #[test]
    fn looks_like_other_cases() {
        for yes in ["Other", "other: explain", "Type your own answer", "Something else"] {
            assert!(looks_like_other(yes), "{yes}");
        }
        for no in ["Otherwise keep it", "Mother"] {
            assert!(!looks_like_other(no), "{no}");
        }
    }

    #[test]
    fn pasted_text_goes_into_the_field_and_focuses_it() {
        let mut view = q(&["first"], false);
        view.paste_text("line1\r\nline2");
        assert!(view.hints().starts_with("Enter submit"));
        assert_eq!(view.handle_key(key(KeyCode::Enter)), submitted("line1\nline2"));
    }

    #[test]
    fn images_attach_show_and_can_be_removed() {
        let mut view = q(&["first"], false);
        assert!(view.attach_image("image/png", "AAAA".into()).is_ok());
        assert!(view.attach_image("image/png", "BBBB".into()).is_ok());
        let rendered = shown(&view, 80, 40);
        assert!(rendered.contains("image-1.png") && rendered.contains("image-2.png"), "{rendered}");
        for _ in 0..3 {
            if view.hints().starts_with(&format!("{}{}", glyphs::ARROW_LEFT, glyphs::ARROW_RIGHT)) {
                break;
            }
            view.handle_key(key(KeyCode::Tab));
        }
        assert!(view.hints().starts_with(glyphs::ARROW_LEFT));
        view.handle_key(key(KeyCode::Delete));
        assert_eq!(view.attachment_count(), 1);
        assert_eq!(view.images().len(), 1);
        assert_eq!(view.images()[0].base64, "BBBB");
    }

    #[test]
    fn at_most_five_images_are_attached() {
        let mut view = q(&["first"], false);
        for _ in 0..MAX_QUESTION_IMAGES {
            assert!(view.attach_image("image/png", "AAAA".into()).is_ok());
        }
        assert!(view.attach_image("image/png", "AAAA".into()).is_err());
        assert_eq!(view.attachment_count(), MAX_QUESTION_IMAGES);
        assert!(shown(&view, 80, 40).contains("At most 5 images"));
    }

    #[test]
    fn images_are_refused_with_a_clear_message_when_unsupported() {
        let mut view = QuestionView::new("Q".into(), vec!["a".into()], false, true, false);
        assert!(view.attach_image("image/png", "AAAA".into()).is_err());
        assert!(shown(&view, 200, 40).contains("does not accept image input"));
        assert!(view.images().is_empty());
    }

    #[test]
    fn images_alone_are_not_an_answer() {
        let mut view = q(&["one", "two"], true);
        view.attach_image("image/png", "AAAA".into()).unwrap();
        assert_eq!(view.handle_key(key(KeyCode::Enter)), QuestionOutcome::Handled);
        assert!(shown(&view, 200, 40).contains("image"));
        assert!(shown(&view, 200, 40).contains("Add a short description"));
    }

    #[test]
    fn state_survives_a_resize() {
        let mut view = q(&["one", "two", "three"], true);
        view.handle_key(key(KeyCode::Char('2')));
        view.handle_key(key(KeyCode::Tab));
        typed(&mut view, "kept");
        view.attach_image("image/png", "AAAA".into()).unwrap();
        let wide = shown(&view, 100, 40);
        shown(&view, 30, 10);
        assert_eq!(shown(&view, 100, 40), wide);
        assert_eq!(view.images().len(), 1);
        assert_eq!(
            view.handle_key(key(KeyCode::Enter)),
            submitted("two\n\nAdditional details from the user:\nkept")
        );
    }

    #[test]
    fn escape_cancels_from_every_zone() {
        let mut view = q(&["one"], false);
        assert_eq!(view.handle_key(key(KeyCode::Esc)), QuestionOutcome::Cancel);
        view.handle_key(key(KeyCode::Tab));
        assert!(view.hints().starts_with("Enter submit"));
        assert_eq!(view.handle_key(key(KeyCode::Esc)), QuestionOutcome::Cancel);
        view.attach_image("image/png", "AAAA".into()).unwrap();
        view.handle_key(key(KeyCode::Tab));
        assert!(view.hints().starts_with(glyphs::ARROW_LEFT));
        assert_eq!(view.handle_key(key(KeyCode::Esc)), QuestionOutcome::Cancel);
    }

    #[test]
    fn no_chord_answers() {
        for modifier in [KeyModifiers::CONTROL, KeyModifiers::ALT, KeyModifiers::SUPER] {
            for code in [KeyCode::Enter, KeyCode::Char('1'), KeyCode::Char('y')] {
                let mut view = q(&["one", "two"], false);
                assert_eq!(
                    view.handle_key(chord(code, modifier)),
                    QuestionOutcome::Handled,
                    "{code:?} with {modifier:?} answered"
                );
            }
        }
    }

    #[test]
    fn paste_chords_request_a_clipboard_read() {
        let mut view = q(&["one"], false);
        for (code, modifier) in [
            (KeyCode::Char('v'), KeyModifiers::CONTROL),
            (KeyCode::Char('v'), KeyModifiers::ALT),
            (KeyCode::Insert, KeyModifiers::SHIFT),
        ] {
            assert_eq!(view.handle_key(chord(code, modifier)), QuestionOutcome::RequestPaste);
        }
    }

    #[test]
    fn field_newline_chords_insert_newlines() {
        for (code, modifier) in [
            (KeyCode::Enter, KeyModifiers::SHIFT),
            (KeyCode::Enter, KeyModifiers::ALT),
            (KeyCode::Enter, KeyModifiers::CONTROL),
            (KeyCode::Char('j'), KeyModifiers::CONTROL),
        ] {
            let mut view = q(&["one"], false);
            view.handle_key(key(KeyCode::Tab));
            typed(&mut view, "a");
            assert_eq!(view.handle_key(chord(code, modifier)), QuestionOutcome::Handled);
            typed(&mut view, "b");
            assert_eq!(view.handle_key(key(KeyCode::Enter)), submitted("a\nb"), "{code:?}+{modifier:?}");
        }
    }

    #[test]
    fn mouse_wheel_scrolls_and_click_focuses_an_option() {
        let options: Vec<String> = (0..30).map(|i| format!("option{i}")).collect();
        let refs: Vec<&str> = options.iter().map(String::as_str).collect();
        let mut view = q(&refs, false);
        let area = Rect::new(5, 2, 40, 10);
        let theme = Theme::default();
        let before = text(&view.render(area, &theme));
        let wheel = |kind| MouseEvent {
            kind,
            column: 10,
            row: 5,
            modifiers: KeyModifiers::NONE,
        };
        view.handle_mouse(wheel(MouseEventKind::ScrollDown), area);
        view.handle_mouse(wheel(MouseEventKind::ScrollDown), area);
        let after = view.render(area, &theme);
        assert_ne!(text(&after), before, "the wheel did not scroll");

        let index = after
            .iter()
            .position(|l| text(std::slice::from_ref(l)).contains("option7"))
            .expect("option7 is visible after scrolling");
        let click = MouseEvent {
            kind: MouseEventKind::Down(MouseButton::Left),
            column: 10,
            row: area.y + index as u16,
            modifiers: KeyModifiers::NONE,
        };
        assert_eq!(view.handle_mouse(click, area), QuestionOutcome::Handled);
        assert_eq!(view.handle_key(key(KeyCode::Enter)), submitted("option7"));
    }

    #[test]
    fn a_tall_field_keeps_its_caret_visible_and_inside_the_area() {
        let mut view = q(&["one", "two"], false);
        view.paste_text(&"line\n".repeat(20));
        let area = Rect::new(0, 0, 30, 8);
        let lines = view.render(area, &Theme::default());
        let (column, row) = view.cursor(area, &Theme::default()).expect("a caret in the field");
        assert!(column < 30 && (row as usize) < lines.len());
    }
}
