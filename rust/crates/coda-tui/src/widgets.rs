//! Reusable interactive controls for modal surfaces.
//!
//! Modals in this codebase grew one at a time, each hand-rolling its own key
//! handling and drawing. That is workable for a single list overlay and stops
//! being workable the moment two surfaces need a text field that behaves the
//! same way. This module is the shared vocabulary: a [`Control`] trait, a
//! [`Form`] that owns focus, and the controls themselves.
//!
//! # Navigation
//!
//! **Tab and Shift+Tab move between controls; arrow keys act inside the
//! focused control.** The split is absolute, and it has to be: a radio group
//! and a text area both want the arrow keys, so any scheme where arrows
//! sometimes escape to the form is ambiguous exactly when the user is least
//! able to predict it.
//!
//! # Rendering
//!
//! Controls render to [`Line`]s rather than drawing into a [`Frame`]. Keeping
//! them pure means a test can assert on what a control produced without
//! standing up a terminal, and it sidesteps the borrowed-frame lifetimes that
//! make trait objects awkward. The one thing lines cannot express — where the
//! caret sits — comes back through [`Control::cursor`] as an offset the form
//! translates into screen coordinates.
//!
//! A [`Select`] expands inline when open rather than floating a popup over the
//! surface. Inline expansion needs no z-order handling and no second render
//! pass, and in a terminal it reads just as well.

use coda_render::theme::{Role, Theme};
use coda_render::text;
use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
use ratatui::text::{Line, Span};

use crate::render::glyphs;

/// Gutter marker on the focused control, echoing the composer's prompt so the
/// two read as the same idea.
const FOCUS_MARKER: &str = glyphs::FOCUS_MARKER;
/// Same width as [`FOCUS_MARKER`], so unfocused rows stay aligned.
const FOCUS_BLANK: &str = glyphs::FOCUS_BLANK;

/// Whether a control used a key.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeyOutcome {
    /// The control handled the key; the form must not act on it.
    Consumed,
    /// The control did not handle the key; the form may.
    Ignored,
}

/// What a key did to a whole form.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FormOutcome {
    /// A control or the focus ring handled the key.
    Consumed,
    /// Nobody handled the key; the caller may.
    Ignored,
    /// The user asked to submit.
    Submit,
    /// The user asked to cancel.
    Cancel,
}

/// A focusable, interactive element of a [`Form`].
pub trait Control {
    /// Whether focus can land here. Static text returns `false`.
    fn focusable(&self) -> bool {
        true
    }

    /// Handles a key already determined to belong to this control.
    fn handle_key(&mut self, key: KeyEvent) -> KeyOutcome;

    /// Renders the control, including its focus marker.
    fn render(&self, width: u16, focused: bool, theme: &Theme) -> Vec<Line<'static>>;

    /// Caret position as `(column, row)` within this control's own rows.
    fn cursor(&self, _width: u16) -> Option<(u16, u16)> {
        None
    }

    /// Recovers the concrete type, so a caller can read a typed value back out
    /// of a form it built.
    ///
    /// Deliberately not a set of `as_switch`-style methods on this trait: those
    /// would have to name every control here, so adding one anywhere would mean
    /// editing the trait every control implements.
    fn as_any(&self) -> &dyn std::any::Any;
}

/// The column at which control content starts, past the focus gutter.
fn content_width(width: u16) -> usize {
    (width as usize).saturating_sub(FOCUS_MARKER.len())
}

/// Builds a control's first row: focus gutter, then content.
fn gutter(focused: bool, theme: &Theme) -> Span<'static> {
    if focused {
        Span::styled(FOCUS_MARKER, theme.style(Role::ComposerPrompt))
    } else {
        Span::raw(FOCUS_BLANK)
    }
}

/// The style a label takes, brightened while focused so the active control is
/// identifiable without relying on the caret alone — the caret is invisible on
/// controls that have none, such as a switch.
fn label_style(focused: bool, theme: &Theme) -> ratatui::style::Style {
    if focused {
        theme.style(Role::PromptAccent)
    } else {
        theme.style(Role::PromptText)
    }
}

/// Paints the focus band across every row of the focused control.
///
/// The band is the primary focus signal, because it is findable in peripheral
/// vision and it scales to multi-row controls: a six-option radio group still
/// reads as one focused unit. The accent label and the gutter marker are
/// layered beneath it so none of the three is load-bearing alone and the state
/// survives a terminal with no colour at all.
///
/// A span that already carries a background keeps it. The open dropdown's
/// highlighted option is the case that matters: it is drawn as dark text on a
/// bright highlight, so overwriting that background would leave the *selected*
/// row the hardest one to read. Focus and selection have to be legible at the
/// same time, which is the whole reason the two colours are required to
/// differ.
fn band(lines: Vec<Line<'static>>, focused: bool, theme: &Theme) -> Vec<Line<'static>> {
    if !focused {
        return lines;
    }
    let bg = theme.fg(Role::FocusBackground);
    lines
        .into_iter()
        .map(|line| {
            Line::from(
                line.spans
                    .into_iter()
                    .map(|span| {
                        let style = if span.style.bg.is_none() {
                            span.style.bg(bg)
                        } else {
                            span.style
                        };
                        Span::styled(span.content, style)
                    })
                    .collect::<Vec<_>>(),
            )
        })
        .collect()
}

// ---------------------------------------------------------------------------
// Static text
// ---------------------------------------------------------------------------

/// Non-interactive text, for headings and help within a form.
#[derive(Debug, Clone)]
pub struct StaticText {
    text: String,
    role: Role,
}

impl StaticText {
    pub fn new(text: impl Into<String>) -> Self {
        Self {
            text: text.into(),
            role: Role::Notification,
        }
    }

    /// Overrides the role, for headings that should stand out from help text.
    pub fn with_role(mut self, role: Role) -> Self {
        self.role = role;
        self
    }
}

impl Control for StaticText {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn focusable(&self) -> bool {
        false
    }

    fn handle_key(&mut self, _key: KeyEvent) -> KeyOutcome {
        KeyOutcome::Ignored
    }

    fn render(&self, width: u16, _focused: bool, theme: &Theme) -> Vec<Line<'static>> {
        let style = theme.style(self.role);
        text::wrap(&text::sanitize(&self.text), content_width(width))
            .into_iter()
            .map(|row| Line::from(vec![Span::raw(FOCUS_BLANK), Span::styled(row, style)]))
            .collect()
    }
}

// ---------------------------------------------------------------------------
// Text input
// ---------------------------------------------------------------------------

/// A single-line text field.
#[derive(Debug, Clone)]
pub struct TextInput {
    label: String,
    /// Held as chars so cursor arithmetic cannot land inside a code point.
    value: Vec<char>,
    cursor: usize,
    placeholder: String,
    masked: bool,
}

impl TextInput {
    pub fn new(label: impl Into<String>) -> Self {
        Self {
            label: label.into(),
            value: Vec::new(),
            cursor: 0,
            placeholder: String::new(),
            masked: false,
        }
    }

    /// Sets the greyed text shown while the field is empty.
    pub fn with_placeholder(mut self, placeholder: impl Into<String>) -> Self {
        self.placeholder = placeholder.into();
        self
    }

    /// Renders the value as bullets, for secrets such as API keys.
    pub fn masked(mut self) -> Self {
        self.masked = true;
        self
    }

    pub fn with_value(mut self, value: impl Into<String>) -> Self {
        self.value = value.into().chars().collect();
        self.cursor = self.value.len();
        self
    }

    pub fn value(&self) -> String {
        self.value.iter().collect()
    }

    pub fn is_empty(&self) -> bool {
        self.value.is_empty()
    }

    pub fn clear(&mut self) {
        self.value.clear();
        self.cursor = 0;
    }

    /// What the field displays: the value, masked if secret, or the
    /// placeholder when empty.
    fn display(&self) -> (String, bool) {
        if self.value.is_empty() {
            return (self.placeholder.clone(), true);
        }
        if self.masked {
            return (glyphs::BULLET.repeat(self.value.len()), false);
        }
        (self.value(), false)
    }
}

impl Control for TextInput {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn handle_key(&mut self, key: KeyEvent) -> KeyOutcome {
        // Alt and Control chords belong to the surrounding application, not to
        // a text field; swallowing them here would break its shortcuts.
        if key.modifiers.contains(KeyModifiers::CONTROL)
            || key.modifiers.contains(KeyModifiers::ALT)
        {
            return KeyOutcome::Ignored;
        }
        match key.code {
            KeyCode::Char(c) => {
                self.value.insert(self.cursor, c);
                self.cursor += 1;
                KeyOutcome::Consumed
            }
            KeyCode::Backspace if self.cursor > 0 => {
                self.cursor -= 1;
                self.value.remove(self.cursor);
                KeyOutcome::Consumed
            }
            KeyCode::Delete if self.cursor < self.value.len() => {
                self.value.remove(self.cursor);
                KeyOutcome::Consumed
            }
            KeyCode::Left if self.cursor > 0 => {
                self.cursor -= 1;
                KeyOutcome::Consumed
            }
            KeyCode::Right if self.cursor < self.value.len() => {
                self.cursor += 1;
                KeyOutcome::Consumed
            }
            KeyCode::Home => {
                self.cursor = 0;
                KeyOutcome::Consumed
            }
            KeyCode::End => {
                self.cursor = self.value.len();
                KeyOutcome::Consumed
            }
            // Backspace at the start and Delete at the end are still the
            // field's to swallow: letting them fall through would submit the
            // form on a keystroke that visibly did nothing.
            KeyCode::Backspace | KeyCode::Delete | KeyCode::Left | KeyCode::Right => {
                KeyOutcome::Consumed
            }
            _ => KeyOutcome::Ignored,
        }
    }

    fn render(&self, width: u16, focused: bool, theme: &Theme) -> Vec<Line<'static>> {
        let (shown, is_placeholder) = self.display();
        let style = if is_placeholder {
            theme.style(Role::Notification)
        } else {
            theme.style(Role::ComposerText)
        };
        band(
            vec![
                Line::from(vec![
                    gutter(focused, theme),
                    Span::styled(self.label.clone(), label_style(focused, theme)),
                ]),
                Line::from(vec![
                    Span::raw(FOCUS_BLANK),
                    Span::styled(
                        text::truncate(&text::sanitize(&shown), content_width(width)),
                        style,
                    ),
                ]),
            ],
            focused,
            theme,
        )
    }

    fn cursor(&self, width: u16) -> Option<(u16, u16)> {
        let before: String = self.value[..self.cursor].iter().collect();
        let column = if self.masked {
            self.cursor
        } else {
            text::width(&before)
        };
        // Clamped so a value longer than the field cannot park the caret past
        // the right edge, where the terminal would draw it over the border.
        let column = column.min(content_width(width));
        Some(((FOCUS_BLANK.len() + column) as u16, 1))
    }
}

// ---------------------------------------------------------------------------
// Text area
// ---------------------------------------------------------------------------

/// A multi-line text field.
#[derive(Debug, Clone)]
pub struct TextArea {
    label: String,
    lines: Vec<Vec<char>>,
    row: usize,
    column: usize,
    visible_rows: usize,
}

impl TextArea {
    pub fn new(label: impl Into<String>) -> Self {
        Self {
            label: label.into(),
            lines: vec![Vec::new()],
            row: 0,
            column: 0,
            visible_rows: 5,
        }
    }

    /// Sets how many rows the field shows before it scrolls.
    pub fn with_visible_rows(mut self, rows: usize) -> Self {
        self.visible_rows = rows.max(1);
        self
    }

    pub fn with_value(mut self, value: impl Into<String>) -> Self {
        self.lines = value
            .into()
            .split('\n')
            .map(|line| line.chars().collect())
            .collect();
        if self.lines.is_empty() {
            self.lines.push(Vec::new());
        }
        self.row = self.lines.len() - 1;
        self.column = self.lines[self.row].len();
        self
    }

    pub fn value(&self) -> String {
        self.lines
            .iter()
            .map(|line| line.iter().collect::<String>())
            .collect::<Vec<_>>()
            .join("\n")
    }

    pub fn is_empty(&self) -> bool {
        self.lines.iter().all(|line| line.is_empty())
    }

    /// First visible row, scrolled to keep the caret in view.
    fn scroll(&self) -> usize {
        self.row.saturating_sub(self.visible_rows.saturating_sub(1))
    }
}

impl Control for TextArea {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn handle_key(&mut self, key: KeyEvent) -> KeyOutcome {
        if key.modifiers.contains(KeyModifiers::CONTROL)
            || key.modifiers.contains(KeyModifiers::ALT)
        {
            return KeyOutcome::Ignored;
        }
        match key.code {
            KeyCode::Char(c) => {
                self.lines[self.row].insert(self.column, c);
                self.column += 1;
                KeyOutcome::Consumed
            }
            // Enter inserts a newline rather than submitting: in a multi-line
            // field the newline is the whole point, so the form's submit key
            // has to give way here.
            KeyCode::Enter => {
                let tail = self.lines[self.row].split_off(self.column);
                self.lines.insert(self.row + 1, tail);
                self.row += 1;
                self.column = 0;
                KeyOutcome::Consumed
            }
            KeyCode::Backspace => {
                if self.column > 0 {
                    self.column -= 1;
                    self.lines[self.row].remove(self.column);
                } else if self.row > 0 {
                    // Joining onto the previous line puts the caret exactly at
                    // the seam, which is where the user expects it.
                    let current = self.lines.remove(self.row);
                    self.row -= 1;
                    self.column = self.lines[self.row].len();
                    self.lines[self.row].extend(current);
                }
                KeyOutcome::Consumed
            }
            KeyCode::Delete => {
                if self.column < self.lines[self.row].len() {
                    self.lines[self.row].remove(self.column);
                } else if self.row + 1 < self.lines.len() {
                    let next = self.lines.remove(self.row + 1);
                    self.lines[self.row].extend(next);
                }
                KeyOutcome::Consumed
            }
            KeyCode::Left => {
                if self.column > 0 {
                    self.column -= 1;
                } else if self.row > 0 {
                    self.row -= 1;
                    self.column = self.lines[self.row].len();
                }
                KeyOutcome::Consumed
            }
            KeyCode::Right => {
                if self.column < self.lines[self.row].len() {
                    self.column += 1;
                } else if self.row + 1 < self.lines.len() {
                    self.row += 1;
                    self.column = 0;
                }
                KeyOutcome::Consumed
            }
            KeyCode::Up if self.row > 0 => {
                self.row -= 1;
                self.column = self.column.min(self.lines[self.row].len());
                KeyOutcome::Consumed
            }
            KeyCode::Down if self.row + 1 < self.lines.len() => {
                self.row += 1;
                self.column = self.column.min(self.lines[self.row].len());
                KeyOutcome::Consumed
            }
            KeyCode::Home => {
                self.column = 0;
                KeyOutcome::Consumed
            }
            KeyCode::End => {
                self.column = self.lines[self.row].len();
                KeyOutcome::Consumed
            }
            // Up on the first row and Down on the last stay here rather than
            // moving focus: Tab is the only key that leaves a control.
            KeyCode::Up | KeyCode::Down => KeyOutcome::Consumed,
            _ => KeyOutcome::Ignored,
        }
    }

    fn render(&self, width: u16, focused: bool, theme: &Theme) -> Vec<Line<'static>> {
        let style = theme.style(Role::ComposerText);
        let inner = content_width(width);
        let scroll = self.scroll();

        let mut rows = vec![Line::from(vec![
            gutter(focused, theme),
            Span::styled(self.label.clone(), label_style(focused, theme)),
        ])];
        for offset in 0..self.visible_rows {
            let content = self
                .lines
                .get(scroll + offset)
                .map(|line| text::truncate(&text::sanitize(&line.iter().collect::<String>()), inner))
                .unwrap_or_default();
            rows.push(Line::from(vec![
                Span::raw(FOCUS_BLANK),
                Span::styled(content, style),
            ]));
        }
        band(rows, focused, theme)
    }

    fn cursor(&self, width: u16) -> Option<(u16, u16)> {
        let before: String = self.lines[self.row][..self.column].iter().collect();
        let column = text::width(&before).min(content_width(width));
        Some((
            (FOCUS_BLANK.len() + column) as u16,
            // + 1 for the label row.
            (self.row - self.scroll() + 1) as u16,
        ))
    }
}

// ---------------------------------------------------------------------------
// Select (dropdown)
// ---------------------------------------------------------------------------

/// A single-choice field that expands inline when opened.
#[derive(Debug, Clone)]
pub struct Select {
    label: String,
    options: Vec<String>,
    selected: usize,
    /// Where the highlight sits while open, which is only committed to
    /// `selected` on Enter so Esc can back out cleanly.
    highlight: usize,
    open: bool,
}

impl Select {
    pub fn new(label: impl Into<String>, options: Vec<String>) -> Self {
        Self {
            label: label.into(),
            options,
            selected: 0,
            highlight: 0,
            open: false,
        }
    }

    pub fn with_selected(mut self, index: usize) -> Self {
        if index < self.options.len() {
            self.selected = index;
            self.highlight = index;
        }
        self
    }

    pub fn selected_index(&self) -> usize {
        self.selected
    }

    pub fn value(&self) -> Option<&str> {
        self.options.get(self.selected).map(String::as_str)
    }

    pub fn is_open(&self) -> bool {
        self.open
    }
}

impl Control for Select {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn handle_key(&mut self, key: KeyEvent) -> KeyOutcome {
        if key.modifiers.contains(KeyModifiers::CONTROL)
            || key.modifiers.contains(KeyModifiers::ALT)
        {
            return KeyOutcome::Ignored;
        }
        if self.options.is_empty() {
            return KeyOutcome::Ignored;
        }

        if !self.open {
            return match key.code {
                KeyCode::Enter | KeyCode::Char(' ') => {
                    self.open = true;
                    self.highlight = self.selected;
                    KeyOutcome::Consumed
                }
                // Closed, the arrows step through options in place. This is how
                // a native combo box behaves and saves opening the list to move
                // by one.
                KeyCode::Up if self.selected > 0 => {
                    self.selected -= 1;
                    KeyOutcome::Consumed
                }
                KeyCode::Down if self.selected + 1 < self.options.len() => {
                    self.selected += 1;
                    KeyOutcome::Consumed
                }
                KeyCode::Up | KeyCode::Down => KeyOutcome::Consumed,
                _ => KeyOutcome::Ignored,
            };
        }

        match key.code {
            KeyCode::Up if self.highlight > 0 => {
                self.highlight -= 1;
                KeyOutcome::Consumed
            }
            KeyCode::Down if self.highlight + 1 < self.options.len() => {
                self.highlight += 1;
                KeyOutcome::Consumed
            }
            KeyCode::Up | KeyCode::Down => KeyOutcome::Consumed,
            KeyCode::Enter | KeyCode::Char(' ') => {
                self.selected = self.highlight;
                self.open = false;
                KeyOutcome::Consumed
            }
            // Esc closes the list without committing, and is consumed so it
            // does not also cancel the surrounding form. One Esc, one effect.
            KeyCode::Esc => {
                self.open = false;
                self.highlight = self.selected;
                KeyOutcome::Consumed
            }
            _ => KeyOutcome::Ignored,
        }
    }

    fn render(&self, width: u16, focused: bool, theme: &Theme) -> Vec<Line<'static>> {
        let inner = content_width(width);
        let current = self.value().unwrap_or("—").to_string();
        let marker = if self.open { glyphs::CHEVRON_UP } else { glyphs::CHEVRON_DOWN };

        let mut rows = vec![
            Line::from(vec![
                gutter(focused, theme),
                Span::styled(self.label.clone(), label_style(focused, theme)),
            ]),
            Line::from(vec![
                Span::raw(FOCUS_BLANK),
                Span::styled(
                    text::truncate(&text::sanitize(&current), inner.saturating_sub(2)),
                    theme.style(Role::ComposerText),
                ),
                Span::raw(" "),
                Span::styled(marker.to_string(), theme.style(Role::Notification)),
            ]),
        ];

        if self.open {
            for (index, option) in self.options.iter().enumerate() {
                let selected = index == self.highlight;
                let style = if selected {
                    theme
                        .style(Role::CompletionSelectedText)
                        .bg(theme.fg(Role::CompletionSelectedBackground))
                } else {
                    theme.style(Role::CompletionNormal)
                };
                let prefix = if selected { glyphs::OPTION_MARKER } else { glyphs::OPTION_BLANK };
                rows.push(Line::from(vec![
                    Span::raw(FOCUS_BLANK),
                    Span::styled(
                        format!(
                            "{prefix}{}",
                            text::truncate(&text::sanitize(option), inner.saturating_sub(2))
                        ),
                        style,
                    ),
                ]));
            }
        }
        band(rows, focused, theme)
    }
}

// ---------------------------------------------------------------------------
// Radio group
// ---------------------------------------------------------------------------

/// A single choice shown as a permanently expanded list.
///
/// Prefer this over [`Select`] when the options matter enough to be worth the
/// vertical space; prefer `Select` when there are many, or when the choice is
/// secondary to the rest of the form.
#[derive(Debug, Clone)]
pub struct RadioGroup {
    label: String,
    options: Vec<String>,
    selected: usize,
}

impl RadioGroup {
    pub fn new(label: impl Into<String>, options: Vec<String>) -> Self {
        Self {
            label: label.into(),
            options,
            selected: 0,
        }
    }

    pub fn with_selected(mut self, index: usize) -> Self {
        if index < self.options.len() {
            self.selected = index;
        }
        self
    }

    pub fn selected_index(&self) -> usize {
        self.selected
    }

    pub fn value(&self) -> Option<&str> {
        self.options.get(self.selected).map(String::as_str)
    }
}

impl Control for RadioGroup {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn handle_key(&mut self, key: KeyEvent) -> KeyOutcome {
        if key.modifiers.contains(KeyModifiers::CONTROL)
            || key.modifiers.contains(KeyModifiers::ALT)
        {
            return KeyOutcome::Ignored;
        }
        match key.code {
            KeyCode::Up if self.selected > 0 => {
                self.selected -= 1;
                KeyOutcome::Consumed
            }
            KeyCode::Down if self.selected + 1 < self.options.len() => {
                self.selected += 1;
                KeyOutcome::Consumed
            }
            KeyCode::Up | KeyCode::Down => KeyOutcome::Consumed,
            _ => KeyOutcome::Ignored,
        }
    }

    fn render(&self, width: u16, focused: bool, theme: &Theme) -> Vec<Line<'static>> {
        let inner = content_width(width);
        let mut rows = vec![Line::from(vec![
            gutter(focused, theme),
            Span::styled(self.label.clone(), label_style(focused, theme)),
        ])];
        for (index, option) in self.options.iter().enumerate() {
            let chosen = index == self.selected;
            // A filled dot rather than colour alone, so the choice survives a
            // monochrome terminal.
            let glyph = if chosen { glyphs::RADIO_ON } else { glyphs::RADIO_OFF };
            let style = if chosen && focused {
                theme.style(Role::PromptAccent)
            } else if chosen {
                theme.style(Role::ComposerText)
            } else {
                theme.style(Role::Notification)
            };
            rows.push(Line::from(vec![
                Span::raw(FOCUS_BLANK),
                Span::styled(
                    format!(
                        "{glyph} {}",
                        text::truncate(&text::sanitize(option), inner.saturating_sub(4))
                    ),
                    style,
                ),
            ]));
        }
        band(rows, focused, theme)
    }
}

// ---------------------------------------------------------------------------
// Switch
// ---------------------------------------------------------------------------

/// An on/off toggle.
#[derive(Debug, Clone)]
pub struct Switch {
    label: String,
    on: bool,
}

impl Switch {
    pub fn new(label: impl Into<String>) -> Self {
        Self {
            label: label.into(),
            on: false,
        }
    }

    pub fn with_value(mut self, on: bool) -> Self {
        self.on = on;
        self
    }

    pub fn is_on(&self) -> bool {
        self.on
    }
}

impl Control for Switch {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn handle_key(&mut self, key: KeyEvent) -> KeyOutcome {
        if key.modifiers.contains(KeyModifiers::CONTROL)
            || key.modifiers.contains(KeyModifiers::ALT)
        {
            return KeyOutcome::Ignored;
        }
        match key.code {
            KeyCode::Char(' ') | KeyCode::Enter => {
                self.on = !self.on;
                KeyOutcome::Consumed
            }
            // Left means off and Right means on, rather than toggling: on a
            // control with two states, a directional key that flips rather than
            // sets is a coin toss.
            KeyCode::Left => {
                self.on = false;
                KeyOutcome::Consumed
            }
            KeyCode::Right => {
                self.on = true;
                KeyOutcome::Consumed
            }
            _ => KeyOutcome::Ignored,
        }
    }

    fn render(&self, width: u16, focused: bool, theme: &Theme) -> Vec<Line<'static>> {
        // The knob's position carries the state on its own, so the switch is
        // still readable where colour is not.
        let knob = if self.on { glyphs::SWITCH_ON } else { glyphs::SWITCH_OFF };
        let state = if self.on { "on" } else { "off" };
        let state_style = if self.on {
            theme.style(Role::ToolSuccess)
        } else {
            theme.style(Role::Notification)
        };
        band(
            vec![Line::from(vec![
                gutter(focused, theme),
                Span::styled(
                    text::truncate(
                        &text::sanitize(&self.label),
                        content_width(width).saturating_sub(10),
                    ),
                    label_style(focused, theme),
                ),
                Span::raw("  "),
                Span::styled(knob.to_string(), state_style),
                Span::raw(" "),
                Span::styled(state.to_string(), state_style),
            ])],
            focused,
            theme,
        )
    }
}

// ---------------------------------------------------------------------------
// Form
// ---------------------------------------------------------------------------

/// An ordered set of controls with a focus ring.
///
/// Tab and Shift+Tab move focus, Enter submits, Esc cancels — except where the
/// focused control claims those keys first, which is how a text area keeps
/// Enter and an open dropdown keeps Esc.
pub struct Form {
    controls: Vec<Box<dyn Control>>,
    focus: usize,
}

impl Form {
    pub fn new(controls: Vec<Box<dyn Control>>) -> Self {
        let mut form = Self { controls, focus: 0 };
        // Focus must start somewhere legal: a leading heading is static, and
        // focusing it would make the first Tab appear to do nothing.
        if !form.is_focusable(form.focus) {
            form.focus_next();
        }
        form
    }

    pub fn focused_index(&self) -> usize {
        self.focus
    }

    pub fn control(&self, index: usize) -> Option<&dyn Control> {
        self.controls.get(index).map(|c| c.as_ref())
    }

    pub fn len(&self) -> usize {
        self.controls.len()
    }

    pub fn is_empty(&self) -> bool {
        self.controls.is_empty()
    }

    fn is_focusable(&self, index: usize) -> bool {
        self.controls
            .get(index)
            .map(|c| c.focusable())
            .unwrap_or(false)
    }

    /// Whether any control can take focus.
    ///
    /// Guards the focus ring against spinning forever on a form of pure static
    /// text, which is a legitimate thing to render.
    fn has_focusable(&self) -> bool {
        self.controls.iter().any(|c| c.focusable())
    }

    pub fn focus_next(&mut self) {
        if !self.has_focusable() {
            return;
        }
        let count = self.controls.len();
        for step in 1..=count {
            let candidate = (self.focus + step) % count;
            if self.is_focusable(candidate) {
                self.focus = candidate;
                return;
            }
        }
    }

    pub fn focus_previous(&mut self) {
        if !self.has_focusable() {
            return;
        }
        let count = self.controls.len();
        for step in 1..=count {
            let candidate = (self.focus + count - (step % count)) % count;
            if self.is_focusable(candidate) {
                self.focus = candidate;
                return;
            }
        }
    }

    pub fn handle_key(&mut self, key: KeyEvent) -> FormOutcome {
        // The focused control gets first refusal on every key, so a control
        // that needs Enter, Esc or Tab can keep it.
        if let Some(control) = self.controls.get_mut(self.focus) {
            if control.handle_key(key) == KeyOutcome::Consumed {
                return FormOutcome::Consumed;
            }
        }

        match key.code {
            KeyCode::Tab | KeyCode::BackTab => {
                if key.code == KeyCode::BackTab || key.modifiers.contains(KeyModifiers::SHIFT) {
                    self.focus_previous();
                } else {
                    self.focus_next();
                }
                FormOutcome::Consumed
            }
            KeyCode::Enter => FormOutcome::Submit,
            KeyCode::Esc => FormOutcome::Cancel,
            _ => FormOutcome::Ignored,
        }
    }

    /// Renders every control in order, separated by a blank row.
    pub fn render(&self, width: u16, theme: &Theme) -> Vec<Line<'static>> {
        let mut rows = Vec::new();
        for (index, control) in self.controls.iter().enumerate() {
            if index > 0 {
                rows.push(Line::default());
            }
            rows.extend(control.render(width, index == self.focus, theme));
        }
        rows
    }

    /// Row at which the control at `index` starts, within the rendered block.
    fn control_offset(&self, index: usize, width: u16, theme: &Theme) -> usize {
        let mut offset = 0usize;
        for (i, control) in self.controls.iter().enumerate().take(index) {
            if i > 0 {
                offset += 1;
            }
            offset += control.render(width, false, theme).len();
        }
        // The separator that precedes the control itself.
        if index > 0 {
            offset += 1;
        }
        offset
    }

    /// The half-open row range occupied by the focused control.
    ///
    /// Scrolling keys off this rather than off the caret, because controls
    /// without a caret — a switch, a radio group — would otherwise be scrolled
    /// out of sight the moment they took focus, leaving the user editing
    /// something they cannot see.
    pub fn focused_rows(&self, width: u16, theme: &Theme) -> (u16, u16) {
        let start = self.control_offset(self.focus, width, theme);
        let height = self
            .controls
            .get(self.focus)
            .map(|c| c.render(width, true, theme).len())
            .unwrap_or(0);
        (start as u16, (start + height) as u16)
    }

    /// Caret position within the rendered block, or `None` when the focused
    /// control has no caret.
    pub fn cursor(&self, width: u16, theme: &Theme) -> Option<(u16, u16)> {
        let control = self.controls.get(self.focus)?;
        let (column, row) = control.cursor(width)?;
        let offset = self.control_offset(self.focus, width, theme);
        Some((column, row + offset as u16))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn theme() -> Theme {
        Theme::default()
    }

    fn plain(lines: &[Line<'static>]) -> Vec<String> {
        lines
            .iter()
            .map(|line| {
                line.spans
                    .iter()
                    .map(|span| span.content.as_ref())
                    .collect::<String>()
                    .trim_end()
                    .to_string()
            })
            .collect()
    }

    // --- TextInput ---------------------------------------------------------

    #[test]
    fn text_input_accepts_typing() {
        let mut input = TextInput::new("Name");
        for c in "abc".chars() {
            assert_eq!(input.handle_key(key(KeyCode::Char(c))), KeyOutcome::Consumed);
        }
        assert_eq!(input.value(), "abc");
    }

    #[test]
    fn text_input_backspace_deletes_before_the_cursor() {
        let mut input = TextInput::new("Name").with_value("abc");
        input.handle_key(key(KeyCode::Left));
        input.handle_key(key(KeyCode::Backspace));
        assert_eq!(input.value(), "ac");
    }

    #[test]
    fn text_input_delete_removes_under_the_cursor() {
        let mut input = TextInput::new("Name").with_value("abc");
        input.handle_key(key(KeyCode::Home));
        input.handle_key(key(KeyCode::Delete));
        assert_eq!(input.value(), "bc");
    }

    #[test]
    fn text_input_ignores_control_chords_so_shortcuts_still_work() {
        let mut input = TextInput::new("Name");
        let ctrl_c = KeyEvent::new(KeyCode::Char('c'), KeyModifiers::CONTROL);
        assert_eq!(input.handle_key(ctrl_c), KeyOutcome::Ignored);
        assert_eq!(input.value(), "");
    }

    #[test]
    fn text_input_does_not_leak_edit_keys_that_did_nothing() {
        // Backspace at position zero must not fall through and submit a form.
        let mut input = TextInput::new("Name");
        assert_eq!(
            input.handle_key(key(KeyCode::Backspace)),
            KeyOutcome::Consumed
        );
    }

    #[test]
    fn text_input_masks_a_secret_value() {
        let input = TextInput::new("Key").masked().with_value("s3cret");
        let rows = plain(&input.render(40, true, &theme()));
        assert_eq!(rows[1].trim(), "\u{2022}".repeat(6));
        assert!(!rows[1].contains("s3cret"), "the secret leaked into the render");
    }

    #[test]
    fn text_input_shows_a_placeholder_only_while_empty() {
        let input = TextInput::new("Name").with_placeholder("your name");
        assert!(plain(&input.render(40, false, &theme()))[1].contains("your name"));

        let typed = input.clone().with_value("x");
        assert!(!plain(&typed.render(40, false, &theme()))[1].contains("your name"));
    }

    #[test]
    fn text_input_keeps_the_caret_inside_the_field() {
        let input = TextInput::new("Name").with_value("x".repeat(200));
        let (column, _) = input.cursor(20).expect("a caret");
        assert!(column < 20, "caret at {column} escaped a 20-cell field");
    }

    // --- TextArea ----------------------------------------------------------

    #[test]
    fn text_area_enter_inserts_a_newline_rather_than_submitting() {
        let mut area = TextArea::new("Notes").with_value("ab");
        assert_eq!(area.handle_key(key(KeyCode::Enter)), KeyOutcome::Consumed);
        area.handle_key(key(KeyCode::Char('c')));
        assert_eq!(area.value(), "ab\nc");
    }

    #[test]
    fn text_area_backspace_joins_onto_the_previous_line() {
        let mut area = TextArea::new("Notes").with_value("ab\ncd");
        area.handle_key(key(KeyCode::Home));
        area.handle_key(key(KeyCode::Backspace));
        assert_eq!(area.value(), "abcd");
    }

    #[test]
    fn text_area_scrolls_to_keep_the_caret_visible() {
        let mut area = TextArea::new("Notes").with_visible_rows(2);
        for _ in 0..5 {
            area.handle_key(key(KeyCode::Enter));
        }
        let (_, row) = area.cursor(40).expect("a caret");
        // Label row plus at most `visible_rows`.
        assert!(row <= 2, "caret row {row} fell outside the visible window");
    }

    // --- Select ------------------------------------------------------------

    fn options() -> Vec<String> {
        vec!["one".into(), "two".into(), "three".into()]
    }

    #[test]
    fn select_opens_and_commits_on_enter() {
        let mut select = Select::new("Pick", options());
        select.handle_key(key(KeyCode::Enter));
        assert!(select.is_open());
        select.handle_key(key(KeyCode::Down));
        select.handle_key(key(KeyCode::Enter));
        assert!(!select.is_open());
        assert_eq!(select.value(), Some("two"));
    }

    #[test]
    fn select_escape_closes_without_committing() {
        let mut select = Select::new("Pick", options());
        select.handle_key(key(KeyCode::Enter));
        select.handle_key(key(KeyCode::Down));
        assert_eq!(select.handle_key(key(KeyCode::Esc)), KeyOutcome::Consumed);
        assert!(!select.is_open());
        assert_eq!(
            select.value(),
            Some("one"),
            "escape committed a value it should have discarded"
        );
    }

    #[test]
    fn select_lists_its_options_only_while_open() {
        let mut select = Select::new("Pick", options());
        let closed = plain(&select.render(40, true, &theme())).join("\n");
        assert!(!closed.contains("three"));

        select.handle_key(key(KeyCode::Enter));
        let open = plain(&select.render(40, true, &theme())).join("\n");
        assert!(open.contains("three"), "open list did not render its options");
    }

    #[test]
    fn select_arrows_step_through_options_while_closed() {
        let mut select = Select::new("Pick", options());
        select.handle_key(key(KeyCode::Down));
        assert_eq!(select.value(), Some("two"));
        assert!(!select.is_open());
    }

    #[test]
    fn select_with_no_options_ignores_keys() {
        let mut select = Select::new("Pick", Vec::new());
        assert_eq!(select.handle_key(key(KeyCode::Enter)), KeyOutcome::Ignored);
    }

    // --- RadioGroup --------------------------------------------------------

    #[test]
    fn radio_group_moves_with_the_arrows_and_stops_at_the_ends() {
        let mut radio = RadioGroup::new("Mode", options());
        radio.handle_key(key(KeyCode::Up));
        assert_eq!(radio.selected_index(), 0, "moved above the first option");

        radio.handle_key(key(KeyCode::Down));
        radio.handle_key(key(KeyCode::Down));
        radio.handle_key(key(KeyCode::Down));
        assert_eq!(radio.selected_index(), 2, "moved past the last option");
    }

    #[test]
    fn radio_group_marks_the_selection_without_relying_on_colour() {
        let radio = RadioGroup::new("Mode", options()).with_selected(1);
        let rows = plain(&radio.render(40, false, &theme()));
        assert!(rows[2].contains("(\u{25CF})"), "selected option unmarked");
        assert!(rows[1].contains("( )"), "unselected option looks selected");
    }

    // --- Switch ------------------------------------------------------------

    #[test]
    fn switch_toggles_on_space() {
        let mut switch = Switch::new("Telemetry");
        assert!(!switch.is_on());
        switch.handle_key(key(KeyCode::Char(' ')));
        assert!(switch.is_on());
        switch.handle_key(key(KeyCode::Char(' ')));
        assert!(!switch.is_on());
    }

    #[test]
    fn switch_arrows_set_rather_than_toggle() {
        let mut switch = Switch::new("Telemetry");
        switch.handle_key(key(KeyCode::Right));
        switch.handle_key(key(KeyCode::Right));
        assert!(switch.is_on(), "a second Right turned the switch back off");

        switch.handle_key(key(KeyCode::Left));
        switch.handle_key(key(KeyCode::Left));
        assert!(!switch.is_on());
    }

    #[test]
    fn switch_state_is_readable_without_colour() {
        let on = plain(&Switch::new("T").with_value(true).render(40, false, &theme()));
        let off = plain(&Switch::new("T").with_value(false).render(40, false, &theme()));
        assert_ne!(on[0], off[0], "on and off render identically");
    }

    // --- Form --------------------------------------------------------------

    fn sample_form() -> Form {
        Form::new(vec![
            Box::new(StaticText::new("Heading")),
            Box::new(TextInput::new("Name")),
            Box::new(Switch::new("Telemetry")),
        ])
    }

    #[test]
    fn a_focused_control_is_banded_on_every_row() {
        let radio = RadioGroup::new("Mode", options());
        let focused = radio.render(40, true, &theme());
        let unfocused = radio.render(40, false, &theme());
        let expected = theme().fg(Role::FocusBackground);

        for (index, line) in focused.iter().enumerate() {
            assert!(
                line.spans.iter().all(|s| s.style.bg == Some(expected)),
                "focused row {index} is missing the band"
            );
        }
        assert!(
            unfocused
                .iter()
                .flat_map(|l| l.spans.iter())
                .all(|s| s.style.bg != Some(expected)),
            "an unfocused control must not be banded"
        );
    }

    #[test]
    fn a_focused_switch_is_banded_even_though_it_has_no_caret() {
        // The switch is what proves the band earns its place: it has no caret,
        // so without the band its focus would rest on the gutter marker alone.
        let switch = Switch::new("Telemetry");
        let expected = theme().fg(Role::FocusBackground);
        assert!(switch.render(40, true, &theme())[0]
            .spans
            .iter()
            .all(|s| s.style.bg == Some(expected)));
    }

    #[test]
    fn a_form_bands_only_the_focused_control() {
        let form = sample_form();
        let expected = theme().fg(Role::FocusBackground);
        let banded = form
            .render(40, &theme())
            .iter()
            .filter(|line| {
                !line.spans.is_empty()
                    && line.spans.iter().all(|s| s.style.bg == Some(expected))
            })
            .count();
        // The focused text input contributes a label row and a value row.
        assert_eq!(banded, 2, "expected the focused control's two rows banded");
    }

    #[test]
    fn the_band_does_not_overwrite_the_open_dropdown_highlight() {
        // The highlighted option is dark text on a bright highlight. Painting
        // the band over it would leave the selected row the hardest one to
        // read — the exact inversion the focus/selection split exists to
        // prevent.
        let mut select = Select::new("Pick", options());
        select.handle_key(key(KeyCode::Enter)); // open the list

        let highlight = theme().fg(Role::CompletionSelectedBackground);
        let rendered = select.render(40, true, &theme());
        assert!(
            rendered
                .iter()
                .flat_map(|line| line.spans.iter())
                .any(|span| span.style.bg == Some(highlight)),
            "the band overwrote the highlighted option's background"
        );
    }

    #[test]
    fn form_skips_static_text_when_placing_initial_focus() {
        let form = sample_form();
        assert_eq!(form.focused_index(), 1, "focus landed on static text");
    }

    #[test]
    fn form_tab_moves_forward_and_wraps_past_static_text() {
        let mut form = sample_form();
        form.handle_key(key(KeyCode::Tab));
        assert_eq!(form.focused_index(), 2);
        form.handle_key(key(KeyCode::Tab));
        assert_eq!(form.focused_index(), 1, "wrap landed on static text");
    }

    #[test]
    fn form_back_tab_moves_backward() {
        let mut form = sample_form();
        form.handle_key(key(KeyCode::BackTab));
        assert_eq!(form.focused_index(), 2);
        form.handle_key(key(KeyCode::BackTab));
        assert_eq!(form.focused_index(), 1);
    }

    #[test]
    fn form_enter_submits_but_a_text_area_keeps_it() {
        let mut plain_form = Form::new(vec![Box::new(TextInput::new("Name"))]);
        assert_eq!(plain_form.handle_key(key(KeyCode::Enter)), FormOutcome::Submit);

        let mut area_form = Form::new(vec![Box::new(TextArea::new("Notes"))]);
        assert_eq!(
            area_form.handle_key(key(KeyCode::Enter)),
            FormOutcome::Consumed,
            "Enter submitted the form instead of adding a newline"
        );
    }

    #[test]
    fn form_escape_cancels_but_an_open_select_keeps_it() {
        let mut form = Form::new(vec![Box::new(Select::new("Pick", options()))]);
        form.handle_key(key(KeyCode::Enter)); // open the list
        assert_eq!(
            form.handle_key(key(KeyCode::Esc)),
            FormOutcome::Consumed,
            "Esc cancelled the form while the dropdown was open"
        );
        // Now that it is closed, Esc reaches the form.
        assert_eq!(form.handle_key(key(KeyCode::Esc)), FormOutcome::Cancel);
    }

    #[test]
    fn form_with_no_focusable_controls_does_not_hang() {
        let mut form = Form::new(vec![
            Box::new(StaticText::new("a")),
            Box::new(StaticText::new("b")),
        ]);
        // Would spin forever if the ring did not check for focusables first.
        form.focus_next();
        form.focus_previous();
    }

    #[test]
    fn form_marks_exactly_one_control_as_focused() {
        let form = sample_form();
        let rows = plain(&form.render(40, &theme()));
        let marked = rows
            .iter()
            .filter(|row| row.starts_with(FOCUS_MARKER.trim_end()))
            .count();
        assert_eq!(marked, 1, "expected one focus marker, found {marked}");
    }

    #[test]
    fn form_cursor_accounts_for_controls_rendered_above() {
        let mut form = sample_form();
        // Heading (1 row) + separator (1) + the input's label row (1) puts the
        // caret on row 3, the input's value row.
        let (_, row) = form.cursor(40, &theme()).expect("a caret");
        assert_eq!(row, 3, "caret did not account for the rows above it");

        let rendered = plain(&form.render(40, &theme()));
        assert!(
            rendered[row as usize - 1].contains("Name"),
            "the row above the caret should be the input's label"
        );

        form.handle_key(key(KeyCode::Tab));
        assert!(form.cursor(40, &theme()).is_none(), "a switch has no caret");
    }
}
