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
        // The attribute must sit directly on the module, so only the item it
        // decorates is inspected. Searching the whole span up to the next `{`
        // instead would let `#[cfg(test)] use x;` followed anywhere later by a
        // `mod y;` declaration read as a test module, and brace-skip the
        // production code that followed.
        let is_module = after[..brace]
            .split_whitespace()
            .find(|token| !token.starts_with("pub"))
            .is_some_and(|token| token == "mod");
        if !is_module {
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

/// `app/mod.rs` must stay the event loop, not the whole application.
///
/// A ceiling rather than a target, and deliberately generous: the point is to
/// notice when a responsibility drifts back in, not to police every line.
/// Before the split this file held the loop, nineteen slash commands, engine
/// RPC, the clipboard, pointer gestures and browser orchestration, and nothing
/// about reading it said which of those you were in.
#[test]
fn the_application_module_stays_a_shell() {
    const CEILING: usize = 1_600;

    let source = std::fs::read_to_string("src/app/mod.rs").expect("read app/mod.rs");
    let production = without_test_modules(&source).lines().count();

    assert!(
        production <= CEILING,
        "app/mod.rs is {production} production lines, over the {CEILING}-line ceiling. \
         Something with its own responsibility has drifted back in; move it to a \
         sibling under app/ rather than raising this number."
    );
}

/// Row behaviour must not be looked up by browser kind.
///
/// Before this, pressing a key asked "which browser is open?" and then chose
/// what to do — five separate matches on `BrowserKind`, one per verb. A ninth
/// browser had to be remembered in all five, and forgetting one gave a browser
/// whose key silently did nothing: invisible until someone pressed it.
///
/// Now a browser declares its actions when it is built, so the only remaining
/// reasons to ask for the kind are "which browser is open" questions —
/// rebuilding it on reload, and noticing that a save affects the open list.
/// Counting those calls is a proxy for the property, because behaviour
/// dispatch always needed one.
///
/// The needle omits the `(` deliberately. This project runs no rustfmt, so
/// `browser_kind ()` is valid Rust that a `browser_kind()` search would miss.
#[test]
fn row_behaviour_is_not_looked_up_by_browser_kind() {
    const ALLOWED: usize = 2;

    let mut calls = 0usize;
    let mut sites: Vec<String> = Vec::new();
    for (path, source) in sources() {
        let code = without_comments(&without_test_modules(&source));
        for (index, line) in code.lines().enumerate() {
            // The declaration is not a call.
            if line.contains("fn browser_kind") {
                continue;
            }
            let n = line.matches("browser_kind").count();
            if n > 0 {
                calls += n;
                sites.push(format!("{path}:{}", index + 1));
            }
        }
    }

    assert!(
        calls <= ALLOWED,
        "browser_kind is called {calls} times, over the {ALLOWED} allowed:\n{}\n\
         Row behaviour belongs on the browser that raises it — give it a RowActions \
         at construction rather than asking which kind is open.",
        sites.join("\n")
    );
}

/// A browser surface must be constructed in exactly one place.
///
/// `BrowserSurface::new` alone yields a browser whose rows raise nothing: the
/// actions are attached separately, so a second construction site is a browser
/// that draws correctly and whose every key silently does nothing.
///
/// This is not hypothetical. `reload_browser` was written this way — opening a
/// browser attached its actions, reloading it did not, so pressing `r` left a
/// browser that looked identical and had gone inert. Nothing failed; the keys
/// just stopped. One constructor removes the chance to get it wrong.
///
/// The needle omits the `(` deliberately: this project runs no rustfmt, so
/// `BrowserSurface::new\n(..)` is valid Rust that would slip past it.
#[test]
fn a_browser_surface_is_built_in_one_place() {
    let mut sites: Vec<String> = Vec::new();
    for (path, source) in sources() {
        let code = without_comments(&without_test_modules(&source));
        for (index, line) in code.lines().enumerate() {
            if line.contains("BrowserSurface::new") {
                sites.push(format!("{path}:{}", index + 1));
            }
        }
    }

    assert!(
        sites.len() <= 1,
        "BrowserSurface::new is called from {} places:\n{}\n\
         Build browser surfaces through the one helper that also attaches their \
         row actions, or a browser reachable by the other path will draw fine \
         and respond to nothing.",
        sites.len(),
        sites.join("\n")
    );
}

/// A foldable block is worthless if nothing calls the toggle.
///
/// `toggle_fold_at_click` cannot be exercised by a unit test — reaching it
/// needs an `App`, and building one spawns a real engine. That is precisely
/// the shape of bug this codebase keeps producing: a complete, well-tested
/// unit that nothing calls. So the wiring itself is asserted here.
#[test]
fn the_pointer_handler_offers_a_click_to_fold() {
    let source = std::fs::read_to_string("src/app/clipboard.rs").expect("read clipboard.rs");
    let code = without_test_modules(&source);
    let start = code
        .find("fn decide_pointer_action")
        .expect("decide_pointer_action is gone; this rule needs rewriting");
    // Up to the next item at the same indentation, so only this function counts.
    let body = &code[start..];
    let end = body.find("\n    pub(super) fn ").unwrap_or(body.len());

    assert!(
        body[..end].contains("toggle_fold_at_click"),
        "decide_pointer_action never calls toggle_fold_at_click, so clicking a \
         thinking block does nothing. The fold would be unreachable."
    );
}

/// An indicator nothing advances is a static picture of a spinner.
///
/// `UiState::spinner` is drawn by the renderer but advanced by the event loop,
/// and the loop cannot be unit tested — running it needs a live engine and a
/// terminal. Both halves are asserted here: the frame has to move, and the
/// loop has to wake up to move it. Dropping the wakeup is the subtler of the
/// two, and leaves the indicator frozen through exactly the long silences it
/// exists to cover.
#[test]
fn the_event_loop_drives_the_working_indicator() {
    let source = std::fs::read_to_string("src/app/mod.rs").expect("read app/mod.rs");
    let code = without_comments(&without_test_modules(&source));
    let start = code
        .find("pub async fn run")
        .expect("App::run is gone; this rule needs rewriting");
    let body = &code[start..];
    // Stop at the next sibling item. A comment banner would be the obvious
    // boundary, but `without_comments` has already removed it — and falling
    // back to "rest of the file" would let a call anywhere in `App` satisfy
    // this, which is not what is being asserted.
    let end = ["\n    fn ", "\n    pub fn ", "\n    pub(super) fn ", "\n    async fn "]
        .iter()
        .filter_map(|needle| body.find(needle))
        .min()
        .unwrap_or(body.len());
    let run = &body[..end];

    for required in ["tick_spinner", "arm_spinner_wakeup"] {
        assert!(
            run.contains(required),
            "App::run never calls {required}, so the working indicator does not \
             animate. It would draw one frame and hold it."
        );
    }
}

/// A transcript change must pass through the reducer, or it will not be drawn.
///
/// `App::apply` is what invalidates the cached rows; `redraw` otherwise reuses
/// them and only rebuilds on a width change. The fold was written as a direct
/// call on the transcript and so flipped the block internally while the screen
/// stayed exactly as it was — a click did nothing at all on a finished turn,
/// and appeared to work intermittently during one only because streaming
/// events happened to invalidate the cache.
#[test]
fn only_the_reducer_mutates_the_transcript_fold() {
    let mut offenders: Vec<String> = Vec::new();
    for (path, source) in sources() {
        if path.replace('\\', "/").ends_with("src/state.rs") {
            continue;
        }
        let code = without_comments(&without_test_modules(&source));
        for (index, line) in code.lines().enumerate() {
            // The declaration is not a call.
            if line.contains("fn toggle_fold") {
                continue;
            }
            if line.contains("toggle_fold(") {
                offenders.push(format!("{path}:{}", index + 1));
            }
        }
    }

    assert!(
        offenders.is_empty(),
        "toggle_fold is called outside the reducer:\n{}\n\
         Send a UiEvent instead. Mutating the transcript directly skips the \
         layout invalidation in App::apply, so the fold changes nothing on \
         screen.",
        offenders.join("\n")
    );
}
