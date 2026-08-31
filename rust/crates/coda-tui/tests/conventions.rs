//! Conventions enforced by test rather than by discipline.
//!
//! An unenforced convention decays. These two are greppable, so they are
//! cheap to enforce and expensive to violate by accident.

use std::path::Path;

/// Walks every `.rs` file under `src/`, returning (path, source).
fn sources() -> Vec<(String, String)> {
    fn walk(dir: &Path, out: &mut Vec<(String, String)>) {
        for entry in std::fs::read_dir(dir).expect("read src dir") {
            let path = entry.expect("dir entry").path();
            if path.is_dir() {
                walk(&path, out);
            } else if path.extension().is_some_and(|e| e == "rs") {
                let text = std::fs::read_to_string(&path).expect("read source");
                out.push((path.display().to_string(), text));
            }
        }
    }
    let mut out = Vec::new();
    walk(Path::new("src"), &mut out);
    out
}

/// Strips `#[cfg(test)]` modules so test fixtures are not judged as UI code.
///
/// Tests legitimately contain glyph literals — CJK strings for width
/// assertions, for example — and holding them to the UI rule would force
/// pointless indirection in test data.
///
/// Brace-matches each test module rather than truncating the file at the first
/// `#[cfg(test)]`. Truncating looks equivalent while every file happens to keep
/// one test module at the bottom, but a single test-only `use` near the top —
/// an ordinary thing to write — would silently disable every rule below it for
/// the rest of the file, with nothing to tell the author.
fn without_test_modules(source: &str) -> String {
    let mut out = String::with_capacity(source.len());
    let mut rest = source;

    while let Some(at) = rest.find("#[cfg(test)]") {
        out.push_str(&rest[..at]);
        let after = &rest[at + "#[cfg(test)]".len()..];

        // Only a module is skipped wholesale. A test-only `use` or `const` is
        // a single item, so dropping the rest of the file for it would be the
        // very bug this avoids; skip just the attribute and keep scanning.
        let Some(brace) = after.find('{') else {
            rest = after;
            continue;
        };
        if !after[..brace].contains("mod ") {
            rest = after;
            continue;
        }

        let mut depth = 0usize;
        let mut end = None;
        for (index, ch) in after[brace..].char_indices() {
            match ch {
                '{' => depth += 1,
                '}' => {
                    depth -= 1;
                    if depth == 0 {
                        end = Some(brace + index + 1);
                        break;
                    }
                }
                _ => {}
            }
        }
        match end {
            Some(end) => rest = &after[end..],
            // Unbalanced braces: drop the remainder rather than risk a false
            // pass on a file we cannot parse.
            None => return out,
        }
    }

    out.push_str(rest);
    out
}

/// Drops comment text, so a constant may document itself with the glyph it
/// names without the documentation counting as a violation.
///
/// Quote-aware. Treating the first `//` on a line as a comment start looks
/// right until a string contains a URL: `"see https://example.com ✓"` would
/// have everything from `//` discarded, masking the glyph after it.
fn without_comments(source: &str) -> String {
    let mut out = String::with_capacity(source.len());

    for line in source.lines() {
        let mut in_string = false;
        let mut in_char = false;
        let mut escaped = false;
        let mut cut = line.len();
        let bytes: Vec<char> = line.chars().collect();

        for i in 0..bytes.len() {
            let ch = bytes[i];
            if escaped {
                escaped = false;
                continue;
            }
            match ch {
                '\\' if in_string || in_char => escaped = true,
                '"' if !in_char => in_string = !in_string,
                '\'' if !in_string => in_char = !in_char,
                '/' if !in_string && !in_char && i + 1 < bytes.len() && bytes[i + 1] == '/' => {
                    cut = line
                        .char_indices()
                        .nth(i)
                        .map(|(byte, _)| byte)
                        .unwrap_or(line.len());
                    break;
                }
                _ => {}
            }
        }
        out.push_str(&line[..cut]);
        out.push('\n');
    }
    out
}

#[test]
fn glyph_literals_live_only_in_the_glyph_table() {
    let offenders: Vec<String> = sources()
        .into_iter()
        .filter(|(path, _)| !path.replace('\\', "/").ends_with("render/glyphs.rs"))
        .filter_map(|(path, source)| {
            let code = without_comments(&without_test_modules(&source));
            code.contains("\\u{").then_some(path)
        })
        .collect();

    assert!(
        offenders.is_empty(),
        "escaped glyph literals must live in render/glyphs.rs, found them in: {offenders:#?}"
    );
}

#[test]
fn colours_come_from_the_theme_not_from_literals() {
    // A ratchet. The convention already holds; this keeps it holding. A
    // surface that hard-codes a colour is invisible in one theme and garish in
    // another, and nothing about reading the diff would reveal it.
    let offenders: Vec<String> = sources()
        .into_iter()
        .filter_map(|(path, source)| {
            let code = without_comments(&without_test_modules(&source));
            code.contains("Color::").then_some(path)
        })
        .collect();

    assert!(
        offenders.is_empty(),
        "colours must come from a theme Role, not a literal; found Color:: in: {offenders:#?}"
    );
}

/// Characters allowed to appear raw outside the glyph table.
///
/// These occur inside hint and message prose — `"↑/↓ k/j move · Enter select"`,
/// `"Restarting the engine…"` — where spelling them as constants would make
/// the sentence unreadable for no gain.
///
/// An allowlist rather than a blocklist: listing the *enforced* glyphs meant a
/// brand-new glyph pasted as a raw character passed both rules, so the
/// convention only looked closed. Anything non-ASCII that is not deliberately
/// exempted now has to come from the table.
const PROSE_EXEMPT: &[char] = &[
    '\u{2191}', // ↑ in hint text
    '\u{2193}', // ↓ in hint text
    '\u{00B7}', // · separator
    '\u{2026}', // … ellipsis
    '\u{2014}', // — em dash
    '\u{2013}', // – en dash
    '\u{2018}', // ' typographic quotes
    '\u{2019}', // '
    '\u{201C}', // "
    '\u{201D}', // "
    '\u{26A0}', // ⚠ warning in a message
];

#[test]
fn raw_glyph_characters_live_only_in_the_glyph_table() {
    // The companion to the test above, and the one that actually bites: an
    // escape is easy to grep for, so the tempting way around the rule is to
    // paste the character itself. Both spellings have to be closed, and the
    // raw one has to be closed for glyphs nobody has thought of yet.
    let mut offenders: Vec<String> = Vec::new();

    for (path, source) in sources() {
        if path.replace('\\', "/").ends_with("render/glyphs.rs") {
            continue;
        }
        let code = without_comments(&without_test_modules(&source));
        for (index, line) in code.lines().enumerate() {
            if let Some(glyph) = line
                .chars()
                .find(|c| !c.is_ascii() && !PROSE_EXEMPT.contains(c))
            {
                offenders.push(format!(
                    "{path}:{} contains {glyph:?} ({:#06X})",
                    index + 1,
                    glyph as u32
                ));
            }
        }
    }

    assert!(
        offenders.is_empty(),
        "non-ASCII glyphs must come from render::glyphs, or be added to \
         PROSE_EXEMPT if they are prose punctuation. Found:\n{}",
        offenders.join("\n")
    );
}

/// The key context an open surface must produce.
///
/// Regression guard: while a surface is open the composer must not have focus.
/// Without this, every key the surface declines is resolved as composer
/// editing and typed into a composer the user cannot see — letters inserted,
/// Backspace deleting, Up loading a past submission, all behind a modal and
/// submitted when it closes.
#[test]
fn an_open_surface_takes_focus_away_from_the_composer() {
    use coda_tui::keymap::{resolve, Action, Focus, KeyContext};
    use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

    let overlay = KeyContext {
        focus: Focus::Surface,
        busy: false,
        composer_empty: true,
        on_first_line: true,
        on_last_line: true,
        armed: None,
    };

    let plain = |code| resolve(KeyEvent::new(code, KeyModifiers::NONE), overlay);

    assert!(
        matches!(plain(KeyCode::Char('a')), Action::None),
        "a letter reached the composer behind an open surface"
    );
    assert!(
        matches!(plain(KeyCode::Backspace), Action::None),
        "Backspace edited the composer behind an open surface"
    );
    assert!(
        matches!(plain(KeyCode::Up), Action::None),
        "Up loaded history into the composer behind an open surface"
    );

    // Ctrl+C must still reach the global handler, or an open surface would
    // make the session unquittable.
    let ctrl_c = resolve(
        KeyEvent::new(KeyCode::Char('c'), KeyModifiers::CONTROL),
        overlay,
    );
    assert!(
        !matches!(ctrl_c, Action::None),
        "Ctrl+C was swallowed by an open surface"
    );
}
