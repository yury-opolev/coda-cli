//! Every glyph the interface draws.
//!
//! Kept in one place so a change to the visual language is one edit rather
//! than a search across eight files, and so a reviewer can see the whole
//! vocabulary at once. Pinned by `tests/conventions.rs`.

// --- Prompts and focus ---------------------------------------------------

/// Composer prompt, and the focus marker on the active control.
pub const PROMPT: &str = "\u{276F}"; // ❯
/// Composer prompt while the agent is busy.
pub const BUSY: &str = "\u{22EF}"; // ⋯
/// Marks the highlighted row of an open dropdown.
pub const OPTION_SELECTED: &str = "\u{203A}"; // ›

// --- Composer chrome -----------------------------------------------------

/// Composer top edge: a lower half block, so the panel appears to begin half
/// a row above its first content row.
pub const COMPOSER_TOP: &str = "\u{2584}"; // ▄
/// Composer bottom edge, mirroring [`COMPOSER_TOP`].
pub const COMPOSER_BOTTOM: &str = "\u{2580}"; // ▀

// --- Controls ------------------------------------------------------------

/// Filled dot: chosen radio option, switch knob, bullet.
pub const DOT: &str = "\u{25CF}"; // ●
/// Hollow dot: an unset state in a list.
pub const DOT_HOLLOW: &str = "\u{25CB}"; // ○
/// Bullet in prose, and the masking character for secrets.
pub const BULLET: &str = "\u{2022}"; // •
/// Dropdown closed.
pub const CHEVRON_DOWN: &str = "\u{25BC}"; // ▼
/// Dropdown open.
pub const CHEVRON_UP: &str = "\u{25B2}"; // ▲
/// Filled square, for enabled entries in a browser.
pub const SQUARE: &str = "\u{25A0}"; // ■

// --- Status --------------------------------------------------------------

pub const CHECK: &str = "\u{2713}"; // ✓
pub const CROSS: &str = "\u{2717}"; // ✗
pub const ARROW_UP: &str = "\u{2191}"; // ↑
pub const ARROW_DOWN: &str = "\u{2193}"; // ↓
/// Em dash, shown where a value is absent.
pub const EM_DASH: &str = "\u{2014}"; // —

// --- Rules and borders ---------------------------------------------------

/// Horizontal rule inside transcript content.
pub const RULE: &str = "\u{2500}"; // ─
/// Vertical rule; also the modal border's side.
pub const RULE_VERTICAL: &str = "\u{2502}"; // │
/// Full block, used by the scrollbar thumb.
pub const BLOCK: &str = "\u{2588}"; // █
