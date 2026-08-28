//! Rendering primitives for the Coda TUI: text measurement, markdown, unified
//! diffs, syntax highlighting and the colour theme.

pub mod diff;
pub mod line;
pub mod syntax;
pub mod text;
pub mod theme;

pub use line::{Gutter, RenderLine, Span, CHILD_CELLS, MARKER_CELLS};
pub use theme::{ColorDepth, Role, Theme, ThemeColor};


