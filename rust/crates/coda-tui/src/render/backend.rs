//! A backend wrapper that keeps wide glyphs from leaving debris on screen.
//!
//! ratatui diffs frames and only repaints cells it believes changed. A wide
//! glyph (two cells to ratatui) hides the cell after it, and that hidden cell
//! is never written. That is only safe when the terminal agrees the glyph is
//! two cells wide. Terminals disagree about emoji: legacy conhost, VS Code and
//! Windows Terminal's non-grapheme measurement modes draw some of them one cell
//! wide, so the hidden cell keeps whatever an earlier frame left there, and it
//! stays put while the transcript scrolls underneath it.
//!
//! ratatui works around this only for VS16 emoji, and only by clearing the
//! hidden cell *after* the glyph — which, in a terminal that does draw the
//! glyph two cells wide, writes into its right half and erases it.
//!
//! [`StableBackend`] blanks the hidden cells *before* drawing each wide glyph
//! instead. A terminal that draws the glyph wide then paints over the blanks;
//! one that draws it narrow is left with a blank rather than a stale glyph.

use ratatui::backend::{Backend, ClearType, WindowSize};
use ratatui::buffer::Cell;
use ratatui::layout::{Position, Size};
use ratatui::style::Style;
use unicode_width::UnicodeWidthStr;

/// Wraps a backend so wide glyphs never leave stale cells behind them.
pub struct StableBackend<B> {
    inner: B,
}

impl<B> StableBackend<B> {
    pub fn new(inner: B) -> Self {
        Self { inner }
    }
}

/// Reorders one frame's updates so every wide glyph's hidden cells are blanked
/// before the glyph itself is printed.
///
/// A blank update ratatui queued for a hidden cell is dropped: it would land
/// after the glyph and erase it in terminals that measure it correctly.
fn stabilize(updates: Vec<(u16, u16, Cell)>, columns: u16) -> Vec<(u16, u16, Cell)> {
    let hidden: std::collections::HashSet<(u16, u16)> = updates
        .iter()
        .flat_map(|(x, y, cell)| {
            let width = cell.symbol().width() as u16;
            (1..width).map(move |k| (x.saturating_add(k), *y))
        })
        .collect();

    let mut out = Vec::with_capacity(updates.len() + hidden.len());
    for (x, y, cell) in updates {
        if hidden.contains(&(x, y)) && cell.symbol() == " " {
            continue;
        }
        let width = cell.symbol().width() as u16;
        for k in 1..width {
            let column = x.saturating_add(k);
            if column >= columns {
                break;
            }
            let mut blank = Cell::default();
            blank.set_style(Style::default().bg(cell.bg));
            out.push((column, y, blank));
        }
        out.push((x, y, cell));
    }
    out
}

impl<B: Backend> Backend for StableBackend<B> {
    type Error = B::Error;

    fn draw<'a, I>(&mut self, content: I) -> Result<(), Self::Error>
    where
        I: Iterator<Item = (u16, u16, &'a Cell)>,
    {
        let updates: Vec<_> = content.map(|(x, y, cell)| (x, y, cell.clone())).collect();
        let columns = if updates.iter().any(|(_, _, cell)| cell.symbol().width() > 1) {
            self.inner.size()?.width
        } else {
            u16::MAX
        };
        let updates = stabilize(updates, columns);
        self.inner
            .draw(updates.iter().map(|(x, y, cell)| (*x, *y, cell)))
    }

    fn append_lines(&mut self, n: u16) -> Result<(), Self::Error> {
        self.inner.append_lines(n)
    }

    fn hide_cursor(&mut self) -> Result<(), Self::Error> {
        self.inner.hide_cursor()
    }

    fn show_cursor(&mut self) -> Result<(), Self::Error> {
        self.inner.show_cursor()
    }

    fn get_cursor_position(&mut self) -> Result<Position, Self::Error> {
        self.inner.get_cursor_position()
    }

    fn set_cursor_position<P: Into<Position>>(&mut self, position: P) -> Result<(), Self::Error> {
        self.inner.set_cursor_position(position)
    }

    fn clear(&mut self) -> Result<(), Self::Error> {
        self.inner.clear()
    }

    fn clear_region(&mut self, clear_type: ClearType) -> Result<(), Self::Error> {
        self.inner.clear_region(clear_type)
    }

    fn size(&self) -> Result<Size, Self::Error> {
        self.inner.size()
    }

    fn window_size(&mut self) -> Result<WindowSize, Self::Error> {
        self.inner.window_size()
    }

    fn flush(&mut self) -> Result<(), Self::Error> {
        self.inner.flush()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ratatui::text::Line;
    use ratatui::Terminal;

    const CONTINUATION: &str = "\0";

    /// A terminal emulator reduced to what matters here: a cursor, a grid,
    /// and an opinion about how wide each glyph is.
    ///
    /// Cursor movement mirrors ratatui's crossterm backend, which only moves
    /// the cursor when an update is not directly right of the previous one —
    /// so a terminal that advances by a different width than ratatui expected
    /// prints the next contiguous update in the wrong place, as a real one
    /// does.
    struct Emulator {
        grid: Vec<Vec<String>>,
        size: Size,
        /// Whether wide glyphs advance two cells (true) or one (false).
        wide_glyphs: bool,
    }

    impl Emulator {
        fn new(width: u16, height: u16, wide_glyphs: bool) -> Self {
            Self {
                grid: vec![vec![" ".to_string(); width as usize]; height as usize],
                size: Size::new(width, height),
                wide_glyphs,
            }
        }

        fn print(&mut self, x: usize, y: usize, symbol: &str) -> usize {
            let row = &mut self.grid[y];
            // Writing into the right half of a wide glyph erases it.
            if row[x] == CONTINUATION && x > 0 {
                row[x - 1] = " ".to_string();
            }
            // Overwriting a wide glyph's left half erases its right half too.
            if x + 1 < row.len() && row[x + 1] == CONTINUATION {
                row[x + 1] = " ".to_string();
            }
            let advance = if self.wide_glyphs { symbol.width().max(1) } else { 1 };
            row[x] = symbol.to_string();
            for k in 1..advance {
                if x + k < row.len() {
                    row[x + k] = CONTINUATION.to_string();
                }
            }
            x + advance
        }

        fn row(&self, y: usize) -> Vec<&str> {
            self.grid[y].iter().map(String::as_str).collect()
        }
    }

    impl Backend for Emulator {
        type Error = std::io::Error;

        fn draw<'a, I>(&mut self, content: I) -> std::io::Result<()>
        where
            I: Iterator<Item = (u16, u16, &'a Cell)>,
        {
            let mut cursor = (0usize, 0usize);
            let mut last: Option<(u16, u16)> = None;
            for (x, y, cell) in content {
                if !matches!(last, Some((lx, ly)) if x == lx + 1 && y == ly) {
                    cursor = (x as usize, y as usize);
                }
                last = Some((x, y));
                cursor.0 = self.print(cursor.0, cursor.1, cell.symbol());
            }
            Ok(())
        }

        fn hide_cursor(&mut self) -> std::io::Result<()> {
            Ok(())
        }

        fn show_cursor(&mut self) -> std::io::Result<()> {
            Ok(())
        }

        fn get_cursor_position(&mut self) -> std::io::Result<Position> {
            Ok(Position::ORIGIN)
        }

        fn set_cursor_position<P: Into<Position>>(&mut self, _: P) -> std::io::Result<()> {
            Ok(())
        }

        fn clear(&mut self) -> std::io::Result<()> {
            Ok(())
        }

        fn clear_region(&mut self, _: ClearType) -> std::io::Result<()> {
            Ok(())
        }

        fn size(&self) -> std::io::Result<Size> {
            Ok(self.size)
        }

        fn window_size(&mut self) -> std::io::Result<WindowSize> {
            Ok(WindowSize { columns_rows: self.size, pixels: Size::new(0, 0) })
        }

        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }

    fn show<B: Backend>(terminal: &mut Terminal<B>, text: &str) {
        terminal
            .draw(|frame| frame.render_widget(Line::raw(text.to_string()), frame.area()))
            .ok()
            .expect("draw");
    }

    #[test]
    fn a_wide_glyph_drawn_narrow_leaves_no_stale_cell_behind_it() {
        // U+1FAE8 is two cells to ratatui; an older terminal draws it in one,
        // and the cell ratatui considers hidden must not keep the old "b".
        let mut terminal = Terminal::new(StableBackend::new(Emulator::new(6, 1, false))).unwrap();
        show(&mut terminal, "abcdef");
        show(&mut terminal, "\u{1FAE8}cdef");
        let row = terminal.backend().inner.row(0);
        assert_eq!(row, ["\u{1FAE8}", " ", "c", "d", "e", "f"]);
    }

    #[test]
    fn a_vs16_emoji_survives_in_a_terminal_that_draws_it_wide() {
        // ratatui's own VS16 workaround writes a blank into the emoji's right
        // half after printing it, which erases it where it really is wide.
        let mut terminal = Terminal::new(StableBackend::new(Emulator::new(6, 1, true))).unwrap();
        show(&mut terminal, "abcdef");
        show(&mut terminal, "\u{26A0}\u{FE0F}cdef");
        let row = terminal.backend().inner.row(0);
        assert_eq!(row, ["\u{26A0}\u{FE0F}", CONTINUATION, "c", "d", "e", "f"]);
    }

    #[test]
    fn hidden_cells_past_the_last_column_are_not_written() {
        let mut wide = Cell::default();
        wide.set_symbol("\u{1F600}");
        let out = stabilize(vec![(4, 0, wide)], 5);
        assert_eq!(out.len(), 1);
        assert_eq!(out[0].0, 4);
    }
}
