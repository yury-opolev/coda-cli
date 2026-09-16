//! The multi-line input editor.
//!
//! The composer owns a text buffer, a cursor, prompt history and a completion
//! popup. It is deliberately free of terminal and engine dependencies so its
//! behaviour can be tested exhaustively without a screen.
//!
//! Positions are tracked as byte offsets into the buffer, but all movement is
//! grapheme-aware: a cursor never lands inside a multi-byte character or splits
//! an emoji.
//!
//! Long lines soft-wrap for display: [`wrap_line`] is the one place that
//! decides where a row breaks, and the composer's rendered text
//! ([`Composer::visual_rows`]), its height ([`Composer::visual_line_count`])
//! and its caret ([`Composer::visual_cursor_position`]) are all read out of
//! those same ranges. Deriving all three from one function is what keeps a
//! resize or a long paste from ever making them disagree.

use std::ops::Range;

use unicode_segmentation::UnicodeSegmentation;

/// Longest prompt history retained.
const HISTORY_LIMIT: usize = 500;

/// What a key press did to the composer.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ComposerAction {
    /// Nothing happened; the key was not handled.
    Ignored,
    /// The buffer or cursor changed.
    Changed,
    /// The user submitted this text.
    Submit(String),
    /// The user asked to cancel/clear.
    Cancelled,
}

/// A completion candidate offered to the user.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Completion {
    /// Text inserted when accepted.
    pub value: String,
    /// Text shown in the popup.
    pub label: String,
    pub description: Option<String>,
}

impl Completion {
    pub fn new(value: impl Into<String>, description: Option<String>) -> Self {
        let value = value.into();
        Self {
            label: value.clone(),
            value,
            description,
        }
    }
}

/// Active completion popup state.
#[derive(Debug, Clone, Default)]
pub struct CompletionState {
    pub candidates: Vec<Completion>,
    pub selected: usize,
    /// Byte range in the buffer that accepting a candidate replaces.
    pub range: (usize, usize),
    /// Whether the user has moved the selection.
    ///
    /// The popup refreshes on every keystroke, so this is what stops a
    /// deliberate choice being reset to the top candidate while the list is
    /// still the same one the choice was made from. It no longer governs
    /// what is highlighted: a candidate is highlighted from the moment the
    /// popup opens, because Tab and Enter both act on it immediately and a
    /// list with nothing marked gives no sign of what they will take.
    pub navigated: bool,
}

impl CompletionState {
    pub fn is_active(&self) -> bool {
        !self.candidates.is_empty()
    }

    pub fn selection(&self) -> Option<&Completion> {
        self.candidates.get(self.selected)
    }

    fn next(&mut self) {
        if !self.candidates.is_empty() {
            self.selected = (self.selected + 1) % self.candidates.len();
            self.navigated = true;
        }
    }

    fn previous(&mut self) {
        if !self.candidates.is_empty() {
            self.selected = self
                .selected
                .checked_sub(1)
                .unwrap_or(self.candidates.len() - 1);
            self.navigated = true;
        }
    }
}

/// The input editor.
#[derive(Debug, Default)]
pub struct Composer {
    buffer: String,
    /// Byte offset of the cursor within `buffer`.
    cursor: usize,
    history: Vec<String>,
    /// Index into `history` while recalling; `None` when editing fresh text.
    history_index: Option<usize>,
    /// Buffer contents saved before history recall began.
    stashed: Option<String>,
    completion: CompletionState,
}

impl Composer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn text(&self) -> &str {
        &self.buffer
    }

    pub fn cursor(&self) -> usize {
        self.cursor
    }

    pub fn is_empty(&self) -> bool {
        self.buffer.is_empty()
    }

    pub fn completion(&self) -> &CompletionState {
        &self.completion
    }

    pub fn history(&self) -> &[String] {
        &self.history
    }

    /// The line and column of the cursor, both zero-based.
    ///
    /// The column is in *cells*, not graphemes, because that is what the
    /// renderer offsets the caret by — a wide character must advance the caret
    /// by two.
    pub fn cursor_position(&self) -> (usize, usize) {
        let before = &self.buffer[..self.cursor];
        let line = before.matches('\n').count();
        let column_start = before.rfind('\n').map_or(0, |i| i + 1);
        let column = coda_render::text::width(&self.buffer[column_start..self.cursor]);
        (line, column)
    }

    /// Moves the cursor to `line` and `cell_column`, clamped to the text.
    ///
    /// The inverse of [`cursor_position`], used to place the caret where the
    /// pointer was clicked. Clicking past the end of a line lands at its end
    /// rather than doing nothing, which is what every editor does and what a
    /// user aiming roughly at a line expects.
    ///
    /// Snaps to the nearer grapheme boundary, so clicking the right half of a
    /// wide character puts the caret after it rather than inside it.
    ///
    /// `line` is a *logical* line, i.e. it assumes one row of text per `'\n'`.
    /// Once a line can soft-wrap, a click instead needs
    /// [`move_cursor_to_visual`], whose row is a row actually drawn on
    /// screen.
    pub fn move_cursor_to(&mut self, line: usize, cell_column: usize) {
        let mut start = 0usize;
        for _ in 0..line {
            match self.buffer[start..].find('\n') {
                Some(offset) => start += offset + 1,
                // Past the last line: clamp to the last one.
                None => break,
            }
        }
        let end = self.buffer[start..]
            .find('\n')
            .map_or(self.buffer.len(), |offset| start + offset);

        self.cursor = (start + column_to_offset(&self.buffer[start..end], cell_column)).min(end);
        // A click is a new intent, so a stale popup should not survive it.
        self.clear_completions();
    }

    /// The visual row and cell column of the cursor after soft-wrapping the
    /// buffer at `text_width`, both zero-based.
    ///
    /// [`cursor_position`] answers the same question in *logical* terms, which
    /// is all the caret needs as long as every line fits on one screen row.
    /// Once a line wraps, the row it lands on no longer matches its index in
    /// `'\n'`-separated text, so the renderer needs this instead. It walks the
    /// exact same [`wrap_line`] ranges used to draw the composer
    /// ([`visual_rows`]) and to size it ([`visual_line_count`]), so the caret
    /// can never drift from the character it marks.
    pub fn visual_cursor_position(&self, text_width: usize) -> (usize, usize) {
        let logical_line = self.buffer[..self.cursor].matches('\n').count();
        let line_start = self.line_start();
        let offset_in_line = self.cursor - line_start;

        let mut visual_row = 0usize;
        for (index, line) in self.buffer.split('\n').enumerate() {
            let ranges = wrap_line(line, text_width);
            if index == logical_line {
                // The last row whose text starts at or before the cursor. A
                // caret sitting exactly on a wrap point then reads as the
                // start of the row that follows, rather than a phantom
                // trailing position on the row before it.
                let row_in_line = ranges
                    .iter()
                    .rposition(|range| range.start <= offset_in_line)
                    .expect("a row always starts at 0, so at least one matches");
                let range = &ranges[row_in_line];
                let end = offset_in_line.clamp(range.start, range.end);
                let column = coda_render::text::width(&line[range.start..end]);
                return (visual_row + row_in_line, column);
            }
            visual_row += ranges.len();
        }
        // Unreachable: `logical_line` counts the `'\n'`s before the cursor, so
        // it always indexes one of `buffer.split('\n')`'s own segments.
        (visual_row, 0)
    }

    /// Moves the cursor to a visual `row` and cell `column`, clamped to the
    /// text — the click counterpart of [`visual_cursor_position`], and to
    /// [`move_cursor_to`] what that method is to [`cursor_position`].
    ///
    /// Needed once text can wrap: the row a pointer lands on is a row of
    /// *wrapped* text, not necessarily a whole logical line, so translating a
    /// click back into a buffer offset has to walk the same [`wrap_line`]
    /// ranges used to draw the composer rather than assume row and logical
    /// line are the same thing.
    pub fn move_cursor_to_visual(&mut self, text_width: usize, visual_row: usize, cell_column: usize) {
        let mut remaining = visual_row;
        let mut line_start = 0usize;
        let mut lines = self.buffer.split('\n').peekable();

        while let Some(line) = lines.next() {
            let ranges = wrap_line(line, text_width);
            if remaining < ranges.len() {
                let range = ranges[remaining].clone();
                let offset = column_to_offset(&line[range.start..range.end], cell_column);
                self.cursor = line_start + range.start + offset;
                self.clear_completions();
                return;
            }
            remaining -= ranges.len();
            line_start += line.len() + 1;
            if lines.peek().is_none() {
                // Past every row the text has: clamp to its very end, mirroring
                // `move_cursor_to`'s "click below the last line" rule.
                self.cursor = self.buffer.len();
                self.clear_completions();
                return;
            }
        }
    }

    pub fn lines(&self) -> impl Iterator<Item = &str> {
        self.buffer.split('\n')
    }

    /// Number of logical lines — segments between `'\n'`s — always at least
    /// one.
    ///
    /// This is *not* how many rows the composer draws once a line can wrap:
    /// see [`visual_line_count`] for that.
    pub fn line_count(&self) -> usize {
        self.buffer.matches('\n').count() + 1
    }

    /// Every visual row the buffer occupies when soft-wrapped at
    /// `text_width`, in the order the composer draws them.
    pub fn visual_rows(&self, text_width: usize) -> Vec<&str> {
        let mut rows = Vec::new();
        for line in self.buffer.split('\n') {
            for range in wrap_line(line, text_width) {
                rows.push(&line[range]);
            }
        }
        rows
    }

    /// Rows the buffer occupies when soft-wrapped at `text_width`, always at
    /// least one per logical line — an empty line still needs a row for the
    /// caret to sit on.
    ///
    /// Feeds the composer's height: `layout_with_pending` grows the panel by
    /// *visual* rows, not by counts of `'\n'`, or a long line would still be
    /// cut off — just vertically instead of off the right edge.
    pub fn visual_line_count(&self, text_width: usize) -> usize {
        self.buffer
            .split('\n')
            .map(|line| wrap_line(line, text_width).len())
            .sum()
    }

    pub fn set_text(&mut self, text: impl Into<String>) {
        self.buffer = text.into();
        self.cursor = self.buffer.len();
        self.completion = CompletionState::default();
    }

    pub fn clear(&mut self) {
        self.buffer.clear();
        self.cursor = 0;
        self.history_index = None;
        self.stashed = None;
        self.completion = CompletionState::default();
    }

    /// Inserts text at the cursor. Used for typed characters and pastes.
    pub fn insert(&mut self, text: &str) {
        // Normalise line endings so a Windows paste does not leave stray \r
        // that would render as a control glyph.
        let text = text.replace("\r\n", "\n").replace('\r', "\n");
        self.buffer.insert_str(self.cursor, &text);
        self.cursor += text.len();
        self.history_index = None;
    }

    pub fn insert_char(&mut self, c: char) {
        let mut buf = [0u8; 4];
        self.insert(c.encode_utf8(&mut buf));
    }

    pub fn insert_newline(&mut self) {
        self.insert("\n");
    }

    /// Deletes the grapheme before the cursor.
    pub fn backspace(&mut self) -> bool {
        let Some(start) = self.previous_boundary(self.cursor) else {
            return false;
        };
        self.buffer.replace_range(start..self.cursor, "");
        self.cursor = start;
        true
    }

    /// Deletes the grapheme after the cursor.
    pub fn delete(&mut self) -> bool {
        let Some(end) = self.next_boundary(self.cursor) else {
            return false;
        };
        self.buffer.replace_range(self.cursor..end, "");
        true
    }

    /// Deletes the word before the cursor.
    pub fn delete_word_back(&mut self) -> bool {
        let start = self.word_start();
        if start == self.cursor {
            return false;
        }
        self.buffer.replace_range(start..self.cursor, "");
        self.cursor = start;
        true
    }

    /// Deletes from the cursor to the start of the line.
    pub fn delete_to_line_start(&mut self) -> bool {
        let start = self.line_start();
        if start == self.cursor {
            return false;
        }
        self.buffer.replace_range(start..self.cursor, "");
        self.cursor = start;
        true
    }

    /// Deletes from the cursor to the end of the line.
    pub fn delete_to_line_end(&mut self) -> bool {
        let end = self.line_end();
        if end == self.cursor {
            return false;
        }
        self.buffer.replace_range(self.cursor..end, "");
        true
    }

    pub fn move_left(&mut self) -> bool {
        match self.previous_boundary(self.cursor) {
            Some(position) => {
                self.cursor = position;
                true
            }
            None => false,
        }
    }

    pub fn move_right(&mut self) -> bool {
        match self.next_boundary(self.cursor) {
            Some(position) => {
                self.cursor = position;
                true
            }
            None => false,
        }
    }

    pub fn move_word_left(&mut self) -> bool {
        let start = self.word_start();
        if start == self.cursor {
            return false;
        }
        self.cursor = start;
        true
    }

    pub fn move_word_right(&mut self) -> bool {
        let end = self.word_end();
        if end == self.cursor {
            return false;
        }
        self.cursor = end;
        true
    }

    pub fn move_line_start(&mut self) -> bool {
        let start = self.line_start();
        let moved = start != self.cursor;
        self.cursor = start;
        moved
    }

    pub fn move_line_end(&mut self) -> bool {
        let end = self.line_end();
        let moved = end != self.cursor;
        self.cursor = end;
        moved
    }

    pub fn move_start(&mut self) {
        self.cursor = 0;
    }

    pub fn move_end(&mut self) {
        self.cursor = self.buffer.len();
    }

    /// Moves the cursor up one line, preserving the column where possible.
    pub fn move_up(&mut self) -> bool {
        let (line, column) = self.cursor_position();
        if line == 0 {
            return false;
        }
        self.cursor = self.offset_of(line - 1, column);
        true
    }

    pub fn move_down(&mut self) -> bool {
        let (line, column) = self.cursor_position();
        if line + 1 >= self.line_count() {
            return false;
        }
        self.cursor = self.offset_of(line + 1, column);
        true
    }

    /// Takes the buffer for submission, recording it in history.
    pub fn take_submission(&mut self) -> String {
        let text = std::mem::take(&mut self.buffer);
        self.cursor = 0;
        self.history_index = None;
        self.stashed = None;
        self.completion = CompletionState::default();

        let trimmed = text.trim();
        if !trimmed.is_empty() && self.history.last().map(String::as_str) != Some(trimmed) {
            self.history.push(trimmed.to_string());
            if self.history.len() > HISTORY_LIMIT {
                self.history.remove(0);
            }
        }
        text
    }

    /// Recalls the previous history entry.
    ///
    /// Only applies when the cursor is on the first line, so Up still moves
    /// within a multi-line draft. Returns false when there is nothing to recall.
    pub fn history_previous(&mut self) -> bool {
        if self.history.is_empty() {
            return false;
        }

        let next_index = match self.history_index {
            None => {
                // Preserve whatever the user had typed so Down restores it.
                self.stashed = Some(self.buffer.clone());
                self.history.len() - 1
            }
            Some(0) => return false,
            Some(index) => index - 1,
        };

        self.history_index = Some(next_index);
        self.buffer = self.history[next_index].clone();
        self.cursor = self.buffer.len();
        true
    }

    /// Moves forward through history, restoring the draft at the end.
    pub fn history_next(&mut self) -> bool {
        let Some(index) = self.history_index else {
            return false;
        };

        if index + 1 < self.history.len() {
            self.history_index = Some(index + 1);
            self.buffer = self.history[index + 1].clone();
        } else {
            self.history_index = None;
            self.buffer = self.stashed.take().unwrap_or_default();
        }
        self.cursor = self.buffer.len();
        true
    }

    /// Seeds history from a previous session.
    pub fn load_history(&mut self, entries: Vec<String>) {
        self.history = entries;
        if self.history.len() > HISTORY_LIMIT {
            let excess = self.history.len() - HISTORY_LIMIT;
            self.history.drain(..excess);
        }
    }

    // -- Completion ---------------------------------------------------------

    /// The word being completed and its byte range, if the cursor is on one.
    ///
    /// Returns the whole line for a leading slash (a command), otherwise the
    /// whitespace-delimited token under the cursor.
    pub fn completion_context(&self) -> Option<(String, (usize, usize))> {
        let line_start = self.line_start();
        let line = &self.buffer[line_start..self.cursor];

        if let Some(rest) = line.strip_prefix('/') {
            // Only the command word itself completes, not its arguments.
            if !rest.contains(char::is_whitespace) {
                return Some((line.to_string(), (line_start, self.cursor)));
            }
            return None;
        }

        let token_start = line
            .rfind(char::is_whitespace)
            .map_or(line_start, |i| line_start + i + 1);
        let token = &self.buffer[token_start..self.cursor];
        if token.is_empty() {
            return None;
        }
        Some((token.to_string(), (token_start, self.cursor)))
    }

    /// Opens the completion popup.
    ///
    /// Preserves `navigated` when the candidate list is unchanged, so that
    /// refreshing on every keystroke does not silently discard the fact that
    /// the user had chosen something.
    pub fn set_completions(&mut self, candidates: Vec<Completion>, range: (usize, usize)) {
        let navigated = self.completion.navigated
            && self.completion.candidates.len() == candidates.len()
            && self
                .completion
                .candidates
                .iter()
                .zip(&candidates)
                .all(|(a, b)| a.value == b.value);
        let selected = if navigated {
            self.completion.selected.min(candidates.len().saturating_sub(1))
        } else {
            0
        };
        self.completion = CompletionState {
            candidates,
            selected,
            range,
            navigated,
        };
    }

    pub fn clear_completions(&mut self) {
        self.completion = CompletionState::default();
    }

    pub fn completion_next(&mut self) {
        self.completion.next();
    }

    pub fn completion_previous(&mut self) {
        self.completion.previous();
    }

    /// Replaces the completion range with the selected candidate.
    ///
    /// Leaves the caret past a single trailing space, so it lands where
    /// arguments go. A command taking no arguments loses nothing, since a
    /// trailing space is trimmed on submit. When a space already follows —
    /// completing the first word of `/mod extra` — none is added, or the
    /// candidate would arrive with a double space behind it.
    pub fn accept_completion(&mut self) -> bool {
        let Some(selected) = self.completion.selection().cloned() else {
            return false;
        };
        let (start, end) = self.completion.range;
        if start > end || end > self.buffer.len() {
            self.clear_completions();
            return false;
        }

        let already_spaced = self.buffer[end..].starts_with(' ');
        let inserted = if already_spaced {
            selected.value.clone()
        } else {
            format!("{} ", selected.value)
        };
        self.buffer.replace_range(start..end, &inserted);
        // Past the space either way: the one just added, or the one found.
        self.cursor = start + inserted.len() + usize::from(already_spaced);
        self.clear_completions();
        true
    }

    // -- Boundaries ---------------------------------------------------------

    fn previous_boundary(&self, at: usize) -> Option<usize> {
        if at == 0 {
            return None;
        }
        self.buffer[..at]
            .grapheme_indices(true)
            .next_back()
            .map(|(i, _)| i)
    }

    fn next_boundary(&self, at: usize) -> Option<usize> {
        if at >= self.buffer.len() {
            return None;
        }
        self.buffer[at..]
            .graphemes(true)
            .next()
            .map(|g| at + g.len())
    }

    fn line_start(&self) -> usize {
        self.buffer[..self.cursor].rfind('\n').map_or(0, |i| i + 1)
    }

    fn line_end(&self) -> usize {
        self.buffer[self.cursor..]
            .find('\n')
            .map_or(self.buffer.len(), |i| self.cursor + i)
    }

    /// Start of the word before the cursor, skipping any whitespace first.
    fn word_start(&self) -> usize {
        let before = &self.buffer[..self.cursor];
        let trimmed = before.trim_end_matches(|c: char| c.is_whitespace() && c != '\n');
        match trimmed.rfind(|c: char| c.is_whitespace()) {
            Some(i) => i + 1,
            None => 0,
        }
    }

    /// End of the word after the cursor.
    fn word_end(&self) -> usize {
        let after = &self.buffer[self.cursor..];
        let leading = after.len() - after.trim_start_matches(char::is_whitespace).len();
        let rest = &after[leading..];
        let word = rest
            .find(char::is_whitespace)
            .unwrap_or(rest.len());
        self.cursor + leading + word
    }

    /// Byte offset of a (line, column) position, clamped to the line's length.
    fn offset_of(&self, line: usize, column: usize) -> usize {
        let mut offset = 0usize;
        for (index, content) in self.buffer.split('\n').enumerate() {
            if index == line {
                let within: usize = content
                    .graphemes(true)
                    .take(column)
                    .map(str::len)
                    .sum();
                return offset + within;
            }
            offset += content.len() + 1;
        }
        self.buffer.len()
    }
}

// -- Wrapping -----------------------------------------------------------

/// Byte range, relative to the start of one logical line, of a single
/// soft-wrapped row.
type RowRange = Range<usize>;

/// Splits one logical line — text with no `'\n'` in it — into the byte
/// ranges its soft-wrapped rows occupy at `width` cells.
///
/// This is the one place that decides where a row ends. [`Composer`]'s
/// rendered text, its height, and its caret are all read out of these same
/// ranges rather than three separate calculations that a resize or a long
/// paste could quietly make disagree.
///
/// The policy, in order of preference:
///
/// 1. Prefer breaking at a space: when the next grapheme would overflow the
///    row, back up to the most recent space seen on this row and end the row
///    there instead, moving the whole pending word to a fresh row.
/// 2. If there is no space to back up to — a single run of non-space text
///    wider than `width` — hard-break at the grapheme boundary the overflow
///    happened on, so a long URL or an unbroken CJK run is still shown in
///    full rather than lost, and a grapheme cluster is never split in half.
/// 3. Exactly one character is ever omitted from the rendered rows: the
///    single space that *is* the break point, in either policy above. Every
///    other character the user typed — including the second, third, ... space
///    of a run — is preserved verbatim. This is an editor, not a prose
///    formatter: silently collapsing whitespace the user actually typed would
///    make the display lie about the buffer.
///
/// A logical line always yields at least one row, even an empty one, so the
/// caret always has somewhere to land.
///
/// `width == 0` cannot fit anything. That is guarded up front, rather than
/// left to fall out of the loop below, so a terminal briefly too narrow to be
/// useful in cannot turn into an unbounded loop or a panic.
fn wrap_line(line: &str, width: usize) -> Vec<RowRange> {
    if width == 0 {
        return vec![0..line.len()];
    }

    let mut rows = Vec::new();
    let mut row_start = 0usize;
    let mut cells = 0usize;
    // Byte offset of the most recent space seen on the current row, if any —
    // the fallback break point when a later word does not fit.
    let mut last_space: Option<usize> = None;

    for (byte_index, grapheme) in line.grapheme_indices(true) {
        let w = coda_render::text::grapheme_width(grapheme);
        let is_space = grapheme == " ";

        // `cells > 0` matters: it is what lets a single grapheme (or word)
        // wider than `width` still land on an otherwise-empty row instead of
        // being endlessly deferred, which is how splitting it is avoided.
        if cells > 0 && cells + w > width {
            if is_space {
                // The overflowing character is itself the separator: the
                // natural, invisible place to break.
                rows.push(row_start..byte_index);
                row_start = byte_index + grapheme.len();
                cells = 0;
                last_space = None;
                continue;
            }
            if let Some(space_at) = last_space.take() {
                // Back up to the last space: the whole word that did not fit
                // moves to a fresh row rather than being split.
                rows.push(row_start..space_at);
                row_start = space_at + 1; // a space is exactly one byte
                cells = coda_render::text::width(&line[row_start..byte_index]);
            } else {
                // No break point on this row at all: a single run wider than
                // `width`. Hard-break here rather than let it hang off the
                // edge — the whole point of the fallback.
                rows.push(row_start..byte_index);
                row_start = byte_index;
                cells = 0;
            }
        }

        if is_space {
            last_space = Some(byte_index);
        }
        cells += w;
    }
    rows.push(row_start..line.len());
    rows
}

/// Byte offset within `text` where `cell_column` lands, snapping to whichever
/// edge of a straddled grapheme is nearer.
///
/// Shared by the logical ([`Composer::move_cursor_to`]) and visual
/// ([`Composer::move_cursor_to_visual`]) click-to-caret paths, so a click
/// resolves the same way whether or not the row it landed on happens to be a
/// wrapped continuation.
fn column_to_offset(text: &str, cell_column: usize) -> usize {
    let mut at = 0usize;
    let mut cells = 0usize;
    for grapheme in text.graphemes(true) {
        let w = coda_render::text::grapheme_width(grapheme);
        if cells + w > cell_column {
            // Inside this grapheme: take whichever edge is nearer.
            if cell_column.saturating_sub(cells) * 2 >= w {
                at += grapheme.len();
            }
            return at;
        }
        cells += w;
        at += grapheme.len();
    }
    at
}

#[cfg(test)]
mod tests {
    use super::*;

    fn composer_with(text: &str) -> Composer {
        let mut composer = Composer::new();
        composer.set_text(text);
        composer
    }

    #[test]
    fn starts_empty_with_the_cursor_at_the_origin() {
        let composer = Composer::new();
        assert!(composer.is_empty());
        assert_eq!(composer.cursor(), 0);
        assert_eq!(composer.cursor_position(), (0, 0));
        assert_eq!(composer.line_count(), 1);
    }

    #[test]
    fn inserts_text_at_the_cursor() {
        let mut composer = Composer::new();
        composer.insert("hello");
        composer.move_left();
        composer.insert("X");
        assert_eq!(composer.text(), "hellXo");
    }

    #[test]
    fn normalises_windows_line_endings_on_paste() {
        let mut composer = Composer::new();
        composer.insert("one\r\ntwo\rthree");
        assert_eq!(composer.text(), "one\ntwo\nthree");
    }

    #[test]
    fn backspace_removes_a_whole_grapheme() {
        let mut composer = composer_with("a🚀");
        assert!(composer.backspace());
        assert_eq!(composer.text(), "a");
    }

    #[test]
    fn backspace_removes_a_whole_zwj_emoji() {
        let mut composer = composer_with("x👨‍👩‍👧");
        assert!(composer.backspace());
        assert_eq!(composer.text(), "x");
    }

    #[test]
    fn backspace_at_the_start_does_nothing() {
        let mut composer = Composer::new();
        assert!(!composer.backspace());
    }

    #[test]
    fn delete_removes_the_grapheme_after_the_cursor() {
        let mut composer = composer_with("ab");
        composer.move_start();
        assert!(composer.delete());
        assert_eq!(composer.text(), "b");
    }

    #[test]
    fn delete_at_the_end_does_nothing() {
        let mut composer = composer_with("ab");
        assert!(!composer.delete());
    }

    #[test]
    fn moves_across_multibyte_characters_without_splitting_them() {
        let mut composer = composer_with("日本語");
        composer.move_start();
        assert!(composer.move_right());
        assert_eq!(composer.cursor(), 3);
        assert!(composer.move_right());
        assert_eq!(composer.cursor(), 6);
        assert!(composer.move_left());
        assert_eq!(composer.cursor(), 3);
    }

    #[test]
    fn stops_at_the_buffer_edges() {
        let mut composer = composer_with("ab");
        composer.move_start();
        assert!(!composer.move_left());
        composer.move_end();
        assert!(!composer.move_right());
    }

    #[test]
    fn deletes_the_previous_word() {
        let mut composer = composer_with("hello brave world");
        assert!(composer.delete_word_back());
        assert_eq!(composer.text(), "hello brave ");
    }

    #[test]
    fn deletes_the_previous_word_across_trailing_spaces() {
        let mut composer = composer_with("hello world   ");
        assert!(composer.delete_word_back());
        assert_eq!(composer.text(), "hello ");
    }

    #[test]
    fn word_deletion_at_the_start_does_nothing() {
        let mut composer = Composer::new();
        assert!(!composer.delete_word_back());
    }

    #[test]
    fn moves_by_word_in_both_directions() {
        let mut composer = composer_with("alpha beta gamma");
        composer.move_start();
        composer.move_word_right();
        assert_eq!(&composer.text()[..composer.cursor()], "alpha");
        composer.move_word_right();
        assert_eq!(&composer.text()[..composer.cursor()], "alpha beta");
        composer.move_word_left();
        assert_eq!(&composer.text()[..composer.cursor()], "alpha ");
    }

    #[test]
    fn tracks_the_cursor_line_and_column() {
        let mut composer = composer_with("one\ntwo\nthree");
        assert_eq!(composer.cursor_position(), (2, 5));
        composer.move_start();
        assert_eq!(composer.cursor_position(), (0, 0));
    }

    #[test]
    fn counts_lines_including_a_trailing_empty_one() {
        assert_eq!(composer_with("a\nb").line_count(), 2);
        assert_eq!(composer_with("a\n").line_count(), 2);
        assert_eq!(composer_with("").line_count(), 1);
    }

    #[test]
    fn moves_between_lines_preserving_the_column() {
        let mut composer = composer_with("alpha\nbeta\ngamma");
        composer.move_start();
        composer.move_right();
        composer.move_right();
        assert_eq!(composer.cursor_position(), (0, 2));

        assert!(composer.move_down());
        assert_eq!(composer.cursor_position(), (1, 2));
        assert!(composer.move_up());
        assert_eq!(composer.cursor_position(), (0, 2));
    }

    #[test]
    fn clamps_the_column_when_moving_to_a_shorter_line() {
        let mut composer = composer_with("longer line\nab");
        composer.move_start();
        composer.move_line_end();
        assert_eq!(composer.cursor_position(), (0, 11));

        composer.move_down();
        assert_eq!(composer.cursor_position(), (1, 2));
    }

    #[test]
    fn refuses_to_move_beyond_the_first_and_last_lines() {
        let mut composer = composer_with("only line");
        assert!(!composer.move_up());
        assert!(!composer.move_down());
    }

    #[test]
    fn moves_to_line_boundaries_not_buffer_boundaries() {
        let mut composer = composer_with("one\ntwo");
        composer.move_line_start();
        assert_eq!(composer.cursor_position(), (1, 0));
        composer.move_line_end();
        assert_eq!(composer.cursor_position(), (1, 3));
    }

    #[test]
    fn deletes_to_the_line_start_and_end() {
        let mut composer = composer_with("keep\nremove this");
        composer.move_line_start();
        composer.move_word_right();
        assert!(composer.delete_to_line_end());
        assert_eq!(composer.text(), "keep\nremove");

        assert!(composer.delete_to_line_start());
        assert_eq!(composer.text(), "keep\n");
    }

    #[test]
    fn submission_returns_the_buffer_and_clears_it() {
        let mut composer = composer_with("send me");
        assert_eq!(composer.take_submission(), "send me");
        assert!(composer.is_empty());
        assert_eq!(composer.cursor(), 0);
    }

    #[test]
    fn submission_records_history() {
        let mut composer = composer_with("first");
        composer.take_submission();
        composer.set_text("second");
        composer.take_submission();

        assert_eq!(composer.history(), &["first", "second"]);
    }

    #[test]
    fn blank_submissions_are_not_recorded() {
        let mut composer = composer_with("   ");
        composer.take_submission();
        assert!(composer.history().is_empty());
    }

    #[test]
    fn consecutive_duplicates_are_not_recorded_twice() {
        let mut composer = Composer::new();
        for _ in 0..3 {
            composer.set_text("same");
            composer.take_submission();
        }
        assert_eq!(composer.history(), &["same"]);
    }

    #[test]
    fn recalls_history_backwards_then_forwards() {
        let mut composer = Composer::new();
        for text in ["one", "two", "three"] {
            composer.set_text(text);
            composer.take_submission();
        }

        assert!(composer.history_previous());
        assert_eq!(composer.text(), "three");
        assert!(composer.history_previous());
        assert_eq!(composer.text(), "two");
        assert!(composer.history_next());
        assert_eq!(composer.text(), "three");
    }

    #[test]
    fn recall_preserves_and_restores_the_draft() {
        let mut composer = Composer::new();
        composer.set_text("old");
        composer.take_submission();
        composer.set_text("draft in progress");

        assert!(composer.history_previous());
        assert_eq!(composer.text(), "old");

        assert!(composer.history_next());
        assert_eq!(composer.text(), "draft in progress");
    }

    #[test]
    fn recall_stops_at_the_oldest_entry() {
        let mut composer = Composer::new();
        composer.set_text("only");
        composer.take_submission();

        assert!(composer.history_previous());
        assert!(!composer.history_previous());
        assert_eq!(composer.text(), "only");
    }

    #[test]
    fn recall_does_nothing_with_no_history() {
        let mut composer = Composer::new();
        assert!(!composer.history_previous());
        assert!(!composer.history_next());
    }

    #[test]
    fn editing_after_recall_stops_tracking_history() {
        let mut composer = Composer::new();
        composer.set_text("old");
        composer.take_submission();
        composer.history_previous();
        composer.insert("er");

        assert!(!composer.history_next(), "editing should detach from history");
        assert_eq!(composer.text(), "older");
    }

    #[test]
    fn a_click_places_the_caret_at_that_cell() {
        let mut composer = Composer::new();
        composer.insert("hello world");
        composer.move_cursor_to(0, 6);
        composer.insert("brave ");
        assert_eq!(composer.text(), "hello brave world");
    }

    #[test]
    fn a_click_past_the_end_of_a_line_lands_at_its_end() {
        // Every editor does this, and it is what someone aiming roughly at a
        // line expects. Doing nothing would feel broken.
        let mut composer = Composer::new();
        composer.insert("hi");
        composer.move_cursor_to(0, 99);
        composer.insert("!");
        assert_eq!(composer.text(), "hi!");
    }

    #[test]
    fn a_click_on_a_later_line_lands_on_that_line() {
        let mut composer = Composer::new();
        composer.insert("one");
        composer.insert_newline();
        composer.insert("two");
        composer.move_cursor_to(0, 0);
        composer.insert(">");
        assert_eq!(composer.text(), ">one\ntwo");

        composer.move_cursor_to(1, 3);
        composer.insert("!");
        assert_eq!(composer.text(), ">one\ntwo!");
    }

    #[test]
    fn a_click_below_the_last_line_clamps_to_it() {
        let mut composer = Composer::new();
        composer.insert("only");
        composer.move_cursor_to(9, 0);
        composer.insert("<");
        assert_eq!(composer.text(), "<only", "a click below the text was lost");
    }

    #[test]
    fn a_click_snaps_to_a_grapheme_boundary() {
        // Clicking the right half of a wide character puts the caret after it,
        // never inside it — a byte index inside a code point would panic.
        let mut composer = Composer::new();
        composer.insert("\u{6587}\u{6587}");
        composer.move_cursor_to(0, 1);
        composer.insert("|");
        assert_eq!(composer.text(), "\u{6587}|\u{6587}");
    }

    #[test]
    fn a_click_dismisses_a_stale_popup() {
        // A click is a new intent; leaving the popup up would let the next
        // Enter act on a selection the user has moved away from.
        let mut composer = Composer::new();
        composer.insert("/y");
        composer.set_completions(vec![Completion::new("/yolo", None)], (0, 2));
        composer.move_cursor_to(0, 0);
        assert!(!composer.completion().is_active());
    }

    #[test]
    fn a_fresh_popup_is_a_hint_not_a_choice() {
        // Until the user moves the selection, Enter must run what they typed
        // rather than accepting a candidate. Accepting silently replaced a
        // fully typed command with itself, so Enter appeared to do nothing.
        let mut composer = Composer::new();
        composer.insert("/yolo");
        composer.set_completions(
            vec![Completion::new("/yolo", None), Completion::new("/yank", None)],
            (0, 5),
        );
        assert!(!composer.completion().navigated);

        composer.completion_next();
        assert!(composer.completion().navigated, "moving did not register");
    }

    #[test]
    fn refreshing_keeps_a_choice_the_user_already_made() {
        // The popup refreshes on every keystroke. Rebuilding it from scratch
        // would drop the user's selection mid-typing.
        let mut composer = Composer::new();
        composer.insert("/y");
        let candidates = vec![Completion::new("/yolo", None), Completion::new("/yank", None)];
        composer.set_completions(candidates.clone(), (0, 2));
        composer.completion_next();
        let chosen = composer.completion().selected;

        composer.set_completions(candidates, (0, 2));
        assert!(composer.completion().navigated, "the choice was forgotten");
        assert_eq!(composer.completion().selected, chosen);
    }

    #[test]
    fn a_changed_candidate_list_starts_over() {
        // Different candidates mean the old selection is meaningless, so the
        // popup goes back to being a hint rather than pointing at whatever
        // now happens to sit at that index.
        let mut composer = Composer::new();
        composer.insert("/y");
        composer.set_completions(
            vec![Completion::new("/yolo", None), Completion::new("/yank", None)],
            (0, 2),
        );
        composer.completion_next();

        composer.set_completions(vec![Completion::new("/yolo", None)], (0, 2));
        assert!(!composer.completion().navigated);
        assert_eq!(composer.completion().selected, 0);
    }

    #[test]
    fn accepting_puts_the_command_in_the_buffer_without_running_it() {
        let mut composer = Composer::new();
        composer.insert("/y");
        composer.set_completions(vec![Completion::new("/yolo", None)], (0, 2));
        assert!(composer.accept_completion());
        assert_eq!(composer.text(), "/yolo ");
        assert!(!composer.completion().is_active(), "the popup stayed open");
    }

    #[test]
    fn loaded_history_is_capped() {
        let mut composer = Composer::new();
        composer.load_history((0..HISTORY_LIMIT + 50).map(|i| i.to_string()).collect());
        assert_eq!(composer.history().len(), HISTORY_LIMIT);
        // The oldest entries are the ones dropped.
        assert_eq!(composer.history()[0], "50");
    }

    #[test]
    fn detects_a_slash_command_completion_context() {
        let composer = composer_with("/mod");
        let (token, range) = composer.completion_context().expect("a context");
        assert_eq!(token, "/mod");
        assert_eq!(range, (0, 4));
    }

    #[test]
    fn stops_completing_a_command_once_it_has_arguments() {
        let composer = composer_with("/model gpt");
        assert!(composer.completion_context().is_none());
    }

    #[test]
    fn detects_a_word_completion_context() {
        let composer = composer_with("look at src/ma");
        let (token, range) = composer.completion_context().expect("a context");
        assert_eq!(token, "src/ma");
        assert_eq!(range, (8, 14));
    }

    #[test]
    fn has_no_completion_context_after_a_space() {
        let composer = composer_with("hello ");
        assert!(composer.completion_context().is_none());
    }

    #[test]
    fn accepting_a_completion_replaces_the_range() {
        let mut composer = composer_with("/mod");
        composer.set_completions(vec![Completion::new("/model", None)], (0, 4));

        assert!(composer.accept_completion());
        // A space follows, so arguments can be typed straight away.
        assert_eq!(composer.text(), "/model ");
        assert_eq!(composer.cursor(), 7);
        assert!(!composer.completion().is_active());
    }

    #[test]
    fn accepting_does_not_double_a_space_that_is_already_there() {
        let mut composer = Composer::new();
        composer.set_text("/mod extra");
        composer.cursor = 4;
        composer.set_completions(vec![Completion::new("/model", None)], (0, 4));

        assert!(composer.accept_completion());
        assert_eq!(composer.text(), "/model extra");
        // Past the existing space, on the argument.
        assert_eq!(composer.cursor(), 7);
    }

    #[test]
    fn accepting_a_completion_preserves_trailing_text() {
        let mut composer = Composer::new();
        composer.set_text("/mod|extra");
        composer.cursor = 4;
        composer.set_completions(vec![Completion::new("/model", None)], (0, 4));

        assert!(composer.accept_completion());
        assert_eq!(composer.text(), "/model |extra");
    }

    #[test]
    fn completion_selection_wraps_in_both_directions() {
        let mut composer = Composer::new();
        composer.set_completions(
            vec![
                Completion::new("a", None),
                Completion::new("b", None),
                Completion::new("c", None),
            ],
            (0, 0),
        );

        assert_eq!(composer.completion().selected, 0);
        composer.completion_next();
        composer.completion_next();
        composer.completion_next();
        assert_eq!(composer.completion().selected, 0, "should wrap forwards");

        composer.completion_previous();
        assert_eq!(composer.completion().selected, 2, "should wrap backwards");
    }

    #[test]
    fn accepting_with_no_candidates_does_nothing() {
        let mut composer = composer_with("text");
        assert!(!composer.accept_completion());
        assert_eq!(composer.text(), "text");
    }

    #[test]
    fn a_stale_completion_range_is_discarded_safely() {
        let mut composer = composer_with("ab");
        // A range beyond the buffer, e.g. after the user deleted text.
        composer.set_completions(vec![Completion::new("x", None)], (0, 99));
        assert!(!composer.accept_completion());
        assert_eq!(composer.text(), "ab");
    }

    #[test]
    fn setting_text_dismisses_any_open_completion() {
        let mut composer = Composer::new();
        composer.set_completions(vec![Completion::new("x", None)], (0, 0));
        composer.set_text("fresh");
        assert!(!composer.completion().is_active());
    }

    #[test]
    fn clearing_resets_everything() {
        let mut composer = composer_with("text");
        composer.set_completions(vec![Completion::new("x", None)], (0, 1));
        composer.clear();

        assert!(composer.is_empty());
        assert_eq!(composer.cursor(), 0);
        assert!(!composer.completion().is_active());
    }

    #[test]
    fn cursor_offsets_always_land_on_character_boundaries() {
        let mut composer = composer_with("日本\n語🚀\nabc");
        composer.move_start();
        // Walk the whole buffer both ways; any bad offset would panic on slice.
        while composer.move_right() {
            let _ = composer.cursor_position();
            let _ = &composer.text()[..composer.cursor()];
        }
        while composer.move_left() {
            let _ = composer.cursor_position();
            let _ = &composer.text()[..composer.cursor()];
        }
        for line in 0..composer.line_count() {
            for column in 0..8 {
                let offset = composer.offset_of(line, column);
                assert!(composer.text().is_char_boundary(offset));
            }
        }
    }

    // -- Wrapping -------------------------------------------------------

    #[test]
    fn a_line_shorter_than_the_width_occupies_one_row() {
        assert_eq!(wrap_line("hello", 10), vec![0..5]);
    }

    #[test]
    fn a_line_longer_than_the_width_wraps_into_the_rows_it_needs() {
        // Ten letters, no spaces to break at, four cells at a time: three rows.
        assert_eq!(wrap_line("aaaaaaaaaa", 4), vec![0..4, 4..8, 8..10]);
    }

    #[test]
    fn wrapping_prefers_a_word_boundary() {
        let text = "hello world";
        let rendered: Vec<&str> = wrap_line(text, 5).into_iter().map(|r| &text[r]).collect();
        assert_eq!(rendered, vec!["hello", "world"]);
    }

    #[test]
    fn a_single_word_longer_than_the_width_is_hard_broken_rather_than_lost() {
        let word = "supercalifragilistic";
        let ranges = wrap_line(word, 7);
        assert!(ranges.len() > 1, "a 21-character word must not fit on one 7-cell row");
        for range in &ranges {
            assert!(coda_render::text::width(&word[range.clone()]) <= 7);
        }
        // Nothing is lost: every byte is accounted for by some row.
        let rebuilt: String = ranges.into_iter().map(|r| &word[r]).collect();
        assert_eq!(rebuilt, word);
    }

    #[test]
    fn a_wide_character_counts_as_two_cells_and_is_never_split() {
        let text = "日本語日本語日本語";
        let ranges = wrap_line(text, 5);
        for range in &ranges {
            assert!(coda_render::text::width(&text[range.clone()]) <= 5);
        }
        let rebuilt: String = ranges.into_iter().map(|r| &text[r]).collect();
        assert_eq!(rebuilt, text, "no character may be dropped or duplicated");
    }

    #[test]
    fn a_grapheme_wider_than_the_whole_row_still_gets_its_own_row() {
        // Each row is one two-cell character even though the budget is only
        // one cell: splitting a grapheme in half is worse than overflowing it.
        assert_eq!(wrap_line("日本", 1), vec![0..3, 3..6]);
    }

    #[test]
    fn interior_whitespace_that_still_fits_is_preserved_verbatim() {
        // This is an editor, not a prose formatter: three typed spaces stay
        // three spaces as long as there is room. Prose wrapping would collapse
        // a run of whitespace down to a single space instead.
        assert_eq!(wrap_line("a   b", 10), vec![0..5]);
    }

    #[test]
    fn a_zero_width_does_not_panic_or_infinite_loop() {
        let ranges = wrap_line("this line would wrap forever if the guard were missing", 0);
        assert_eq!(ranges.len(), 1, "width 0 falls back to one unwrapped row");
    }

    #[test]
    fn a_short_composer_line_occupies_one_visual_row() {
        let composer = composer_with("hi");
        assert_eq!(composer.visual_line_count(80), 1);
    }

    #[test]
    fn visual_line_count_grows_once_a_line_actually_wraps() {
        // `line_count` never changes here -- there is no '\n' -- but the
        // composer must still grow taller, or the wrapped text is clipped
        // vertically instead of running off the right edge: one bug for another.
        let composer = composer_with("hello world");
        assert_eq!(composer.line_count(), 1);
        assert_eq!(composer.visual_line_count(5), 2);
    }

    #[test]
    fn visual_rows_renders_the_wrapped_text() {
        let composer = composer_with("hello world");
        assert_eq!(composer.visual_rows(5), vec!["hello", "world"]);
    }

    #[test]
    fn an_explicit_newline_still_starts_a_new_row() {
        let composer = composer_with("one\ntwo");
        assert_eq!(composer.visual_rows(80), vec!["one", "two"]);
    }

    #[test]
    fn explicit_newlines_combine_correctly_with_soft_wrapping() {
        let composer = composer_with("ab cd\nef gh");
        assert_eq!(composer.visual_rows(4), vec!["ab", "cd", "ef", "gh"]);
        assert_eq!(composer.visual_line_count(4), 4);
    }

    #[test]
    fn the_visual_cursor_sits_at_the_end_of_a_wrapped_row() {
        let mut composer = composer_with("hello world");
        composer.move_start();
        for _ in 0..5 {
            composer.move_right();
        }
        assert_eq!(composer.visual_cursor_position(5), (0, 5));
    }

    #[test]
    fn the_visual_cursor_sits_at_the_start_of_the_next_wrapped_row() {
        let mut composer = composer_with("hello world");
        composer.move_start();
        for _ in 0..6 {
            composer.move_right();
        }
        assert_eq!(composer.visual_cursor_position(5), (1, 0));
    }

    #[test]
    fn the_visual_cursor_sits_at_the_start_of_a_row_after_an_explicit_newline() {
        let mut composer = composer_with("one\ntwo");
        composer.move_start();
        for _ in 0..4 {
            composer.move_right();
        }
        assert_eq!(composer.visual_cursor_position(80), (1, 0));
    }

    #[test]
    fn the_visual_cursor_tracks_a_wrapped_row_after_an_explicit_newline() {
        let mut composer = composer_with("ab cd\nef gh");
        composer.move_end();
        assert_eq!(composer.visual_cursor_position(4), (3, 2));
    }

    #[test]
    fn a_zero_width_does_not_panic_when_computing_visual_geometry() {
        let composer = composer_with("hello world, this must not loop forever");
        // No '\n' in the text, so even the degenerate guard keeps it on one row.
        assert_eq!(composer.visual_line_count(0), 1);
        assert_eq!(composer.visual_cursor_position(0).0, 0);
        assert_eq!(composer.visual_rows(0).len(), 1);
    }

    #[test]
    fn a_visual_click_places_the_caret_within_a_wrapped_row() {
        let mut composer = composer_with("hello world");
        composer.move_cursor_to_visual(5, 1, 2);
        composer.insert("X");
        assert_eq!(composer.text(), "hello woXrld");
    }

    #[test]
    fn a_visual_click_past_a_wrapped_rows_end_lands_at_its_end() {
        let mut composer = composer_with("hello world");
        composer.move_cursor_to_visual(5, 0, 99);
        composer.insert("!");
        assert_eq!(composer.text(), "hello! world");
    }

    #[test]
    fn a_visual_click_below_the_last_row_clamps_to_the_end() {
        let mut composer = composer_with("hello world");
        composer.move_cursor_to_visual(5, 99, 0);
        composer.insert("!");
        assert_eq!(composer.text(), "hello world!");
    }

    #[test]
    fn a_visual_click_on_a_wrapped_continuation_stays_on_its_own_logical_line() {
        // Row 1 is "world" -- the wrapped tail of the FIRST logical line, not
        // logical line 1 ("next"). A click handler that treated a visual row
        // as though it were a logical line would land this in "next" instead.
        let mut composer = composer_with("hello world\nnext");
        composer.move_cursor_to_visual(5, 1, 0);
        composer.insert("X");
        assert_eq!(composer.text(), "hello Xworld\nnext");
    }
}
