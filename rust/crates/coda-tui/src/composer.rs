//! The multi-line input editor.
//!
//! The composer owns a text buffer, a cursor, prompt history and a completion
//! popup. It is deliberately free of terminal and engine dependencies so its
//! behaviour can be tested exhaustively without a screen.
//!
//! Positions are tracked as byte offsets into the buffer, but all movement is
//! grapheme-aware: a cursor never lands inside a multi-byte character or splits
//! an emoji.

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
        }
    }

    fn previous(&mut self) {
        if !self.candidates.is_empty() {
            self.selected = self
                .selected
                .checked_sub(1)
                .unwrap_or(self.candidates.len() - 1);
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

    /// The line and column of the cursor, both zero-based, in grapheme units.
    pub fn cursor_position(&self) -> (usize, usize) {
        let before = &self.buffer[..self.cursor];
        let line = before.matches('\n').count();
        let column_start = before.rfind('\n').map_or(0, |i| i + 1);
        let column = self.buffer[column_start..self.cursor].graphemes(true).count();
        (line, column)
    }

    pub fn lines(&self) -> impl Iterator<Item = &str> {
        self.buffer.split('\n')
    }

    /// Number of visual lines, always at least one.
    pub fn line_count(&self) -> usize {
        self.buffer.matches('\n').count() + 1
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
    pub fn set_completions(&mut self, candidates: Vec<Completion>, range: (usize, usize)) {
        self.completion = CompletionState {
            candidates,
            selected: 0,
            range,
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
    pub fn accept_completion(&mut self) -> bool {
        let Some(selected) = self.completion.selection().cloned() else {
            return false;
        };
        let (start, end) = self.completion.range;
        if start > end || end > self.buffer.len() {
            self.clear_completions();
            return false;
        }

        self.buffer.replace_range(start..end, &selected.value);
        self.cursor = start + selected.value.len();
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
        assert_eq!(composer.text(), "/model");
        assert_eq!(composer.cursor(), 6);
        assert!(!composer.completion().is_active());
    }

    #[test]
    fn accepting_a_completion_preserves_trailing_text() {
        let mut composer = Composer::new();
        composer.set_text("/mod extra");
        composer.cursor = 4;
        composer.set_completions(vec![Completion::new("/model", None)], (0, 4));

        assert!(composer.accept_completion());
        assert_eq!(composer.text(), "/model extra");
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
}
