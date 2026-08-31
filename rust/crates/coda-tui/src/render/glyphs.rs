//! Every glyph the interface draws.
//!
//! Kept in one place so a change to the visual language is one edit rather
//! than a search across eight files, and so a reviewer can see the whole
//! vocabulary at once. Pinned by `tests/conventions.rs`.
//!
//! # What is enforced
//!
//! The conventions test rejects the *symbol* glyphs below appearing raw
//! anywhere else in the crate — the marks that carry chrome and state, where a
//! stray variant is a visual bug.
//!
//! Directional arrows, the middot separator, the ellipsis and the em dash are
//! **not** enforced. They appear inside hint prose such as
//! `"↑/↓ k/j move · Enter select"`, where spelling them as constants would
//! make the sentence unreadable for no gain. They are still defined here so
//! the vocabulary is documented in one place.
//!
//! # Composites
//!
//! Controls need padded and bracketed forms — `(●)`, `[● ]`, `"❯ "`. Those
//! live here too rather than being assembled at the call site: assembling them
//! inline is exactly how a raw glyph gets reintroduced, and it puts the
//! spacing rules somewhere a reviewer can see them side by side.

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

// --- Composites ----------------------------------------------------------
//
// Padded and bracketed forms. Assembled here rather than at the call site,
// because assembling inline is how a raw glyph gets reintroduced, and because
// the spacing rules are easier to keep consistent when they sit together.

/// Focus gutter on the active control.
pub const FOCUS_MARKER: &str = "\u{276F} "; // "❯ "
/// Same width as [`FOCUS_MARKER`], so unfocused rows stay aligned.
pub const FOCUS_BLANK: &str = "  ";

/// Composer prompt with its one-space inset, keeping the glyph off the edge.
pub const PROMPT_PADDED: &str = " \u{276F} "; // " ❯ "
/// Busy composer prompt, same width as [`PROMPT_PADDED`].
pub const BUSY_PADDED: &str = " \u{22EF} "; // " ⋯ "
/// Composer continuation indent, matching [`PROMPT_PADDED`]'s width so
/// wrapped lines align under the first line's text.
pub const PROMPT_CONTINUATION: &str = "   ";

/// Marks the highlighted option in an open dropdown.
pub const OPTION_MARKER: &str = "\u{203A} "; // "› "
/// Same width as [`OPTION_MARKER`], for unhighlighted options.
pub const OPTION_BLANK: &str = "  ";

/// A chosen radio option. Filled, so the choice survives a monochrome
/// terminal rather than depending on colour.
pub const RADIO_ON: &str = "(\u{25CF})"; // "(●)"
/// An unchosen radio option.
pub const RADIO_OFF: &str = "( )";

/// A switch that is on. The knob's position carries the state, so the switch
/// stays readable where colour is not.
pub const SWITCH_ON: &str = "[ \u{25CF}]"; // "[ ●]"
/// A switch that is off.
pub const SWITCH_OFF: &str = "[\u{25CF} ]"; // "[● ]"

/// Both arrows, for hint text such as "↑↓: choose".
pub const ARROWS_VERTICAL: &str = "\u{2191}\u{2193}"; // "↑↓"

/// Separates a thing from its outcome: "question → answer".
pub const ARROW_RIGHT: &str = "\u{2192}"; // →

/// Marks model reasoning in the transcript.
pub const THINKING: &str = "\u{1F4AD}"; // 💭

// --- Brand ---------------------------------------------------------------

/// The six-line wordmark spelling "Coda", drawn in box-drawing characters.
///
/// Art rather than a symbol, but it lives here for the same reason everything
/// else does: it is visual vocabulary, and keeping it with the rest means the
/// glyph rule needs no exemption for the one file that would otherwise be full
/// of raw box-drawing characters.
pub const WORDMARK: &[&str] = &[
    " \u{250C}\u{2500}\u{2500}\u{2500}\u{2510}      \u{250C}\u{2510}",
    " \u{2502}\u{252C}\u{2500}\u{2510}\u{2502}\u{250C}\u{2500}\u{2500}\u{2510}\u{250C}\u{2500}\u{2518}\u{2502}\u{250C}\u{2500}\u{2500}\u{2510}",
    " \u{2502}\u{2502} \u{2514}\u{2518}\u{2502}\u{252C}\u{2510}\u{2502}\u{2502}\u{252C}\u{2510}\u{2502}\u{2502}\u{252C}\u{2510}\u{2502}",
    " \u{2502}\u{2502} \u{250C}\u{2510}\u{2502}\u{2502}\u{2502}\u{2502}\u{2502}\u{2502}\u{2502}\u{2502}\u{2502}\u{2502}\u{2502}\u{2502}",
    " \u{2502}\u{2514}\u{2500}\u{2534}\u{2502}\u{2502}\u{2514}\u{2534}\u{2502}\u{2502}\u{2514}\u{2534}\u{2502}\u{2502}\u{2514}\u{2534}\u{2514}\u{2510}",
    " \u{2514}\u{2500}\u{2500}\u{2500}\u{2518}\u{2514}\u{2500}\u{2500}\u{2518}\u{2514}\u{2500}\u{2500}\u{2518}\u{2514}\u{2500}\u{2500}\u{2500}\u{2518}",
];
