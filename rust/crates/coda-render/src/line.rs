//! Gutter glyphs and the row model the transcript is built from.
//!
//! Every rendered row carries a gutter prefix that indicates its origin (user,
//! agent, tool child). The gutter is *reserved before wrapping*, so content is
//! wrapped to `width - gutter_cells` and can never collide with the marker.

use crate::theme::Role;

/// Cells reserved for a top-level marker gutter.
pub const MARKER_CELLS: usize = 3;
/// Cells reserved for a nested (tool child) gutter.
pub const CHILD_CELLS: usize = 5;

/// Which gutter decoration a row carries.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Gutter {
    #[default]
    None,
    /// First row of a user message.
    UserMarker,
    /// First row of an in-progress agent block.
    AgentActive,
    /// First row of a finished agent block.
    AgentComplete,
    /// Wrapped continuation of a top-level row.
    Continuation,
    /// A nested row with more siblings below.
    Child,
    /// The final nested row in a group.
    LastChild,
    /// Wrapped continuation of a nested row.
    ChildContinuation,
}

impl Gutter {
    /// The prefix text, using box-drawing and geometric glyphs.
    pub fn prefix(self) -> &'static str {
        match self {
            Gutter::None => "",
            Gutter::UserMarker => " \u{276F} ",       // ❯
            Gutter::AgentActive => " \u{25CB} ",      // ○
            Gutter::AgentComplete => " \u{25CF} ",    // ●
            Gutter::Continuation => "   ",
            Gutter::Child => "   \u{2502} ",          // │
            Gutter::LastChild => "   \u{2514} ",      // └
            Gutter::ChildContinuation => "     ",
        }
    }

    /// The prefix text for terminals without box-drawing support.
    pub fn ascii_prefix(self) -> &'static str {
        match self {
            Gutter::None => "",
            Gutter::UserMarker => " > ",
            Gutter::AgentActive => " o ",
            Gutter::AgentComplete => " * ",
            Gutter::Continuation => "   ",
            Gutter::Child => "   | ",
            Gutter::LastChild => "   \\ ",
            Gutter::ChildContinuation => "     ",
        }
    }

    /// How many cells this gutter occupies.
    pub fn cells(self) -> usize {
        match self {
            Gutter::None => 0,
            Gutter::UserMarker
            | Gutter::AgentActive
            | Gutter::AgentComplete
            | Gutter::Continuation => MARKER_CELLS,
            Gutter::Child | Gutter::LastChild | Gutter::ChildContinuation => CHILD_CELLS,
        }
    }

    /// Whether this gutter draws a visible marker glyph.
    ///
    /// Marker rows draw their glyph even when the row text is empty; plain
    /// continuation rows do not, so blank lines stay genuinely blank.
    pub fn is_marker(self) -> bool {
        matches!(
            self,
            Gutter::UserMarker | Gutter::AgentActive | Gutter::AgentComplete
        )
    }

    /// The continuation gutter that follows this one on wrapped rows.
    pub fn continuation(self) -> Gutter {
        match self {
            Gutter::None => Gutter::None,
            Gutter::UserMarker
            | Gutter::AgentActive
            | Gutter::AgentComplete
            | Gutter::Continuation => Gutter::Continuation,
            Gutter::Child | Gutter::LastChild | Gutter::ChildContinuation => {
                Gutter::ChildContinuation
            }
        }
    }
}

/// A styled run of text within a rendered row, in cell coordinates.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Span {
    pub start: usize,
    pub end: usize,
    pub role: Role,
}

impl Span {
    pub fn new(start: usize, end: usize, role: Role) -> Self {
        Self { start, end, role }
    }

    /// Shifts the span right, used when a gutter is prepended after wrapping.
    pub fn shifted(&self, by: usize) -> Span {
        Span {
            start: self.start + by,
            end: self.end + by,
            role: self.role,
        }
    }

    pub fn is_empty(&self) -> bool {
        self.end <= self.start
    }
}

/// One fully laid-out transcript row.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RenderLine {
    /// Row text *including* the gutter prefix.
    pub text: String,
    /// Base colour for the row.
    pub role: Role,
    pub gutter: Gutter,
    /// Foreground overrides in cell coordinates (syntax, links).
    pub spans: Vec<Span>,
    /// Cells at the start of the row drawn in [`RenderLine::prefix_role`].
    pub prefix_cells: usize,
    pub prefix_role: Option<Role>,
    /// Paint the row background across the full viewport width.
    pub fill_width: bool,
    /// Background role when [`RenderLine::fill_width`] is set.
    pub background: Option<Role>,
    /// Right-aligned annotation (a timestamp), excluded from copy/selection.
    pub right_text: Option<String>,
    /// A blank row inserted between blocks.
    pub is_separator: bool,
}

impl RenderLine {
    /// A plain row with no gutter.
    pub fn new(text: impl Into<String>, role: Role) -> Self {
        Self {
            text: text.into(),
            role,
            gutter: Gutter::None,
            spans: Vec::new(),
            prefix_cells: 0,
            prefix_role: None,
            fill_width: false,
            background: None,
            right_text: None,
            is_separator: false,
        }
    }

    /// The blank row that separates two transcript blocks.
    pub fn separator() -> Self {
        Self {
            is_separator: true,
            ..Self::new(String::new(), Role::Assistant)
        }
    }

    /// Prepends a gutter, shifting any existing spans to match.
    pub fn with_gutter(mut self, gutter: Gutter) -> Self {
        // A blank continuation row draws no glyph, so it must not be padded
        // into a row of trailing spaces.
        if self.text.is_empty() && !gutter.is_marker() {
            self.gutter = gutter;
            return self;
        }

        let prefix = gutter.prefix();
        let shift = gutter.cells();
        self.text = format!("{prefix}{}", self.text);
        self.spans = self.spans.iter().map(|s| s.shifted(shift)).collect();
        self.prefix_cells += shift;
        self.gutter = gutter;
        self
    }

    pub fn with_spans(mut self, spans: Vec<Span>) -> Self {
        self.spans = spans;
        self
    }

    pub fn with_fill(mut self, background: Role) -> Self {
        self.fill_width = true;
        self.background = Some(background);
        self
    }

    pub fn with_right_text(mut self, text: impl Into<String>) -> Self {
        self.right_text = Some(text.into());
        self
    }

    pub fn with_prefix(mut self, cells: usize, role: Role) -> Self {
        self.prefix_cells = cells;
        self.prefix_role = Some(role);
        self
    }

    /// The role that applies at a given cell, honouring draw priority:
    /// prefix beats spans, spans beat the row role.
    pub fn role_at(&self, cell: usize) -> Role {
        if let Some(prefix_role) = self.prefix_role {
            if cell < self.prefix_cells {
                return prefix_role;
            }
        }
        for span in &self.spans {
            if cell >= span.start && cell < span.end {
                return span.role;
            }
        }
        self.role
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::text::width;

    #[test]
    fn marker_gutters_are_three_cells_wide() {
        for gutter in [
            Gutter::UserMarker,
            Gutter::AgentActive,
            Gutter::AgentComplete,
            Gutter::Continuation,
        ] {
            assert_eq!(gutter.cells(), MARKER_CELLS);
            assert_eq!(width(gutter.prefix()), MARKER_CELLS, "{gutter:?}");
            assert_eq!(width(gutter.ascii_prefix()), MARKER_CELLS, "{gutter:?}");
        }
    }

    #[test]
    fn child_gutters_are_five_cells_wide() {
        for gutter in [Gutter::Child, Gutter::LastChild, Gutter::ChildContinuation] {
            assert_eq!(gutter.cells(), CHILD_CELLS);
            assert_eq!(width(gutter.prefix()), CHILD_CELLS, "{gutter:?}");
            assert_eq!(width(gutter.ascii_prefix()), CHILD_CELLS, "{gutter:?}");
        }
    }

    #[test]
    fn the_empty_gutter_occupies_nothing() {
        assert_eq!(Gutter::None.cells(), 0);
        assert_eq!(Gutter::None.prefix(), "");
    }

    #[test]
    fn only_marker_gutters_draw_a_glyph() {
        assert!(Gutter::UserMarker.is_marker());
        assert!(Gutter::AgentActive.is_marker());
        assert!(Gutter::AgentComplete.is_marker());
        assert!(!Gutter::Continuation.is_marker());
        assert!(!Gutter::Child.is_marker());
        assert!(!Gutter::None.is_marker());
    }

    #[test]
    fn top_level_gutters_continue_as_continuation() {
        assert_eq!(Gutter::UserMarker.continuation(), Gutter::Continuation);
        assert_eq!(Gutter::AgentActive.continuation(), Gutter::Continuation);
        assert_eq!(Gutter::AgentComplete.continuation(), Gutter::Continuation);
    }

    #[test]
    fn child_gutters_continue_as_child_continuation() {
        assert_eq!(Gutter::Child.continuation(), Gutter::ChildContinuation);
        assert_eq!(Gutter::LastChild.continuation(), Gutter::ChildContinuation);
    }

    #[test]
    fn applying_a_gutter_prepends_its_prefix() {
        let line = RenderLine::new("hello", Role::Assistant).with_gutter(Gutter::UserMarker);
        assert_eq!(line.text, " \u{276F} hello");
        assert_eq!(line.prefix_cells, MARKER_CELLS);
    }

    #[test]
    fn applying_a_gutter_shifts_existing_spans() {
        let line = RenderLine::new("let x", Role::Code)
            .with_spans(vec![Span::new(0, 3, Role::SyntaxKeyword)])
            .with_gutter(Gutter::Child);

        assert_eq!(line.spans, vec![Span::new(5, 8, Role::SyntaxKeyword)]);
    }

    #[test]
    fn a_blank_continuation_row_is_not_padded_with_spaces() {
        let line = RenderLine::new("", Role::Assistant).with_gutter(Gutter::Continuation);
        assert_eq!(line.text, "");
    }

    #[test]
    fn a_blank_marker_row_still_draws_its_glyph() {
        let line = RenderLine::new("", Role::Assistant).with_gutter(Gutter::AgentActive);
        assert_eq!(line.text, " \u{25CB} ");
    }

    #[test]
    fn role_lookup_prefers_the_prefix_over_spans_and_the_row_role() {
        let line = RenderLine::new("  +added", Role::DiffAdded)
            .with_spans(vec![Span::new(0, 8, Role::SyntaxString)])
            .with_prefix(3, Role::DiffContext);

        assert_eq!(line.role_at(0), Role::DiffContext);
        assert_eq!(line.role_at(2), Role::DiffContext);
        assert_eq!(line.role_at(3), Role::SyntaxString);
    }

    #[test]
    fn role_lookup_falls_back_to_the_row_role_outside_every_span() {
        let line = RenderLine::new("abcdef", Role::Assistant)
            .with_spans(vec![Span::new(1, 3, Role::SyntaxKeyword)]);

        assert_eq!(line.role_at(0), Role::Assistant);
        assert_eq!(line.role_at(1), Role::SyntaxKeyword);
        assert_eq!(line.role_at(2), Role::SyntaxKeyword);
        assert_eq!(line.role_at(3), Role::Assistant);
    }

    #[test]
    fn a_separator_row_is_blank_and_flagged() {
        let line = RenderLine::separator();
        assert!(line.is_separator);
        assert!(line.text.is_empty());
    }

    #[test]
    fn fill_width_records_its_background_role() {
        let line = RenderLine::new("+ added", Role::DiffAdded).with_fill(Role::DiffAddedBackground);
        assert!(line.fill_width);
        assert_eq!(line.background, Some(Role::DiffAddedBackground));
    }

    #[test]
    fn an_empty_span_is_reported_as_empty() {
        assert!(Span::new(4, 4, Role::Code).is_empty());
        assert!(!Span::new(4, 5, Role::Code).is_empty());
    }
}
