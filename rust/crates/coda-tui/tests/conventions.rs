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
            // A file's UTF-8 BOM is an encoding marker, not a rendered glyph.
            code.replace("\\u{feff}", "").contains("\\u{").then_some(path)
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

/// The source of one `App` method, from its signature to the next sibling item.
///
/// Comment-stripped source, so a doc comment cannot satisfy — or violate — a
/// rule about what the code does. Falling back to "the rest of the file" is
/// deliberately not an option: a call anywhere else in `impl App` would then
/// pass a rule that is about this method.
fn app_method<'a>(code: &'a str, signature: &str) -> &'a str {
    let start = code
        .find(signature)
        .unwrap_or_else(|| panic!("`{signature}` is gone; this rule needs rewriting"));
    let body = &code[start + signature.len()..];
    let mut offset = 0usize;
    for line in body.split_inclusive('\n') {
        let sibling = line.starts_with("    ")
            && !line.starts_with("     ")
            && (line.trim_start().starts_with("fn ") || line.trim_start().contains(" fn "));
        if offset > 0 && sibling {
            return &code[start..start + signature.len() + offset];
        }
        offset += line.len();
    }
    &code[start..]
}

/// A session must never be torn down by accident.
///
/// The teardown — declining the requests the live engine is blocked on,
/// discarding the ones whose engine cannot be identified, then asking the
/// engine to stop — once lived in a method with no callers at all. In
/// production every outstanding responder was therefore cancelled by `Drop`
/// instead: indiscriminately, in field-declaration order, and including
/// handles whose numeric id addresses a different request in a replaced
/// process. `App::run` must have exactly one exit, and it must go through
/// `finish`.
#[test]
fn the_run_loop_cannot_exit_without_closing_the_session_out() {
    let source = std::fs::read_to_string("src/app/mod.rs").expect("read app/mod.rs");
    let code = without_comments(&without_test_modules(&source));

    let run = app_method(&code, "pub async fn run");
    assert!(
        run.contains("self.finish("),
        "App::run no longer tears the session down; the engine would be left \
         waiting on decisions nobody will answer."
    );
    assert!(
        !run.contains('?'),
        "App::run has an early `?`, which returns without closing the session \
         out. Bind the result and let `finish` run first."
    );

    let finish = app_method(&code, "async fn finish");
    let before_propagation = finish
        .split("outcome?")
        .next()
        .expect("finish must propagate the run's own outcome");
    assert!(
        before_propagation.contains("close_out("),
        "the teardown must happen before a failing run propagates its error, \
         or the failing path is exactly the one that skips it."
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
    let run = app_method(&code, "async fn event_loop");

    for required in ["tick_spinner", "tick_thinking", "arm_spinner_wakeup"] {
        assert!(
            run.contains(required),
            "the event loop never calls {required}, so the working indicator or \
             thinking timer would freeze between engine events."
        );
    }
    let thinking_tick = run.split("if self.state.tick_thinking").nth(1)
        .expect("thinking clock must report when the displayed second changes");
    let update = thinking_tick.split('}').next().expect("thinking tick body");
    assert!(update.contains("self.laid_out_width = 0"));
    assert!(update.contains("self.dirty = true"));
}

/// A transcript change must pass through the reducer, or it will not be drawn.///
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

// ---------------------------------------------------------------------------
// The TUI is an ordinary API client (serve API implementation plan, 2.8)
// ---------------------------------------------------------------------------
//
// These are the rules that keep this front-end honest. Every one of them was
// true by inspection at the moment it was written; the point is that they stay
// true when someone reaches for the convenient shortcut a year from now.

/// Paths under `src/` that are allowed to touch machine-local files.
///
/// Exactly one: the local maintenance adapter. The MCP editor has to show what
/// is written in `.mcp.json` — including the unresolved `coda-secret:`
/// references the engine deliberately never returns — so *some* code has to
/// read that file. Confining it to one directory is what makes "the rest of
/// the UI reaches the engine only through the public API" checkable.
const LOCAL_ADAPTER: &str = "src/local/";

fn is_local_adapter(path: &str) -> bool {
    path.replace('\\', "/").contains(LOCAL_ADAPTER)
}

/// The agent runtime must not be reachable from the terminal front-end.
///
/// This is the execution-path guarantee: `coda` starts a real `coda serve`
/// child and drives it over the documented protocol, so there is no
/// in-process path a change could accidentally take instead. A `coda_agent::`
/// call anywhere in `src/` would be exactly that path reappearing.
#[test]
fn the_terminal_front_end_contains_no_in_process_agent_path() {
    let mut offenders: Vec<String> = Vec::new();
    for (path, source) in sources() {
        let code = without_comments(&without_test_modules(&source));
        for (index, line) in code.lines().enumerate() {
            for needle in ["coda_agent::", "AgentLoop", "SessionTranscriptStore"] {
                if line.contains(needle) {
                    offenders.push(format!("{path}:{} uses {needle}", index + 1));
                }
            }
        }
    }

    assert!(
        offenders.is_empty(),
        "the TUI must reach the engine only through the public serve API:\n{}\n\
         Session listing is `session/listSessions`, the conversation is \
         `session/getHistory`, and there is no in-process agent.",
        offenders.join("\n")
    );
}

/// Session storage is the engine's business, not this client's.
///
/// Constructing a `.coda/sessions` path is the shape the removed
/// `startup::resolve` had: it worked, and it meant an external orchestrator
/// driving the same API could not do what the TUI did.
#[test]
fn the_front_end_never_builds_a_session_storage_path() {
    let mut offenders: Vec<String> = Vec::new();
    for (path, source) in sources() {
        let code = without_comments(&without_test_modules(&source));
        for (index, line) in code.lines().enumerate() {
            if line.contains(".coda/sessions") || line.contains("sessions_dir") {
                offenders.push(format!("{path}:{}", index + 1));
            }
        }
    }
    assert!(
        offenders.is_empty(),
        "a session storage path is constructed in:\n{}\n\
         Ask the engine instead: session/listSessions and session/getHistory.",
        offenders.join("\n")
    );
}

/// MCP file parsing stays inside the local maintenance adapter.
#[test]
fn mcp_file_parsing_is_confined_to_the_local_adapter() {
    let mut offenders: Vec<String> = Vec::new();
    for (path, source) in sources() {
        if is_local_adapter(&path) {
            continue;
        }
        let code = without_comments(&without_test_modules(&source));
        for (index, line) in code.lines().enumerate() {
            if line.contains("coda_mcp::") {
                offenders.push(format!("{path}:{}", index + 1));
            }
        }
    }
    assert!(
        offenders.is_empty(),
        "coda_mcp is used outside {LOCAL_ADAPTER}:\n{}\n\
         The display path for an ordinary session is the engine's mcp/list; \
         file parsing belongs to the local maintenance adapter.",
        offenders.join("\n")
    );
}

/// And the adapter uses it only for the file format.
#[test]
fn the_local_adapter_parses_configuration_and_runs_nothing() {
    for (path, source) in sources() {
        if !is_local_adapter(&path) {
            continue;
        }
        let code = without_comments(&without_test_modules(&source));
        for (index, line) in code.lines().enumerate() {
            if !line.contains("coda_mcp::") {
                continue;
            }
            assert!(
                line.contains("coda_mcp::config"),
                "{path}:{} reaches into coda_mcp beyond ::config. The adapter \
                 parses a file format; it must not start or talk to a server.",
                index + 1
            );
        }
    }
}

/// Reads a crate manifest's `[dependencies]` section only.
fn dependencies_section(manifest: &str) -> String {
    let mut out = String::new();
    let mut inside = false;
    for line in manifest.lines() {
        let trimmed = line.trim();
        if trimmed.starts_with('[') {
            // `[dependencies]` and `[dependencies.foo]`, but not
            // `[dev-dependencies]` or `[build-dependencies]`.
            inside = trimmed == "[dependencies]" || trimmed.starts_with("[dependencies.");
            continue;
        }
        if inside {
            out.push_str(line);
            out.push('\n');
        }
    }
    out
}

/// The execution-path allow-list, as a manifest fact.
///
/// Deliberately an allow-list rather than a blanket ban: `coda-mcp` legitimately
/// does not depend on the agent, `coda-serve` legitimately does — it *is* the
/// engine — and `coda-tui` keeps it as a **dev**-dependency so its tests can
/// write real saved transcripts for the engine to list. Extending the list is a
/// written decision, which is the point.
#[test]
fn only_the_engine_crate_depends_on_the_agent_runtime() {
    const ALLOWED: &[&str] = &["coda-serve", "coda-engine", "coda"];

    let crates_dir = Path::new("..");
    let mut offenders: Vec<String> = Vec::new();
    for entry in std::fs::read_dir(crates_dir).expect("read crates dir") {
        let dir = entry.expect("dir entry").path();
        let manifest_path = dir.join("Cargo.toml");
        if !manifest_path.is_file() {
            continue;
        }
        let name = dir.file_name().expect("crate dir name").to_string_lossy().to_string();
        if ALLOWED.contains(&name.as_str()) {
            continue;
        }
        let manifest = std::fs::read_to_string(&manifest_path).expect("read manifest");
        if dependencies_section(&manifest).contains("coda-agent") {
            offenders.push(name);
        }
    }

    assert!(
        offenders.is_empty(),
        "these crates list coda-agent in [dependencies]: {offenders:?}\n\
         The agent runtime is reachable only through the engine. If a new crate \
         genuinely needs it, add it to ALLOWED here with the reason."
    );
}

/// `coda-tui` specifically: a dev-dependency, never a dependency.
#[test]
fn the_agent_runtime_is_a_test_fixture_dependency_only() {
    let manifest = std::fs::read_to_string("Cargo.toml").expect("read coda-tui Cargo.toml");
    assert!(
        !dependencies_section(&manifest).contains("coda-agent"),
        "coda-tui lists coda-agent in [dependencies]. It belongs in \
         [dev-dependencies]: tests write saved transcripts as fixtures, the \
         shipped binary must contain no in-process agent."
    );
    assert!(
        manifest.contains("[dev-dependencies]"),
        "the dev-dependency section this rule refers to is gone; rewrite the rule"
    );
}

/// The boot crate must not become the laundry.
///
/// Moving the transcript reads into `coda-boot` and calling *that* from the
/// TUI would satisfy every rule above while changing nothing about who reads
/// the engine's private files. So the boot crate is checked directly, not by
/// inspecting the TUI's direct dependencies.
#[test]
fn the_boot_crate_reads_no_private_engine_files() {
    let boot = Path::new("../coda-boot");
    let manifest =
        std::fs::read_to_string(boot.join("Cargo.toml")).expect("read coda-boot Cargo.toml");
    assert!(
        !manifest.contains("coda-agent"),
        "coda-boot depends on coda-agent. It is the shared core *boundary*; \
         pulling the agent in there re-creates the in-process path one level down."
    );

    let mut offenders: Vec<String> = Vec::new();
    fn walk(dir: &Path, out: &mut Vec<(String, String)>) {
        for entry in std::fs::read_dir(dir).expect("read dir") {
            let path = entry.expect("dir entry").path();
            if path.is_dir() {
                walk(&path, out);
            } else if path.extension().is_some_and(|e| e == "rs") {
                out.push((
                    path.display().to_string(),
                    std::fs::read_to_string(&path).expect("read source"),
                ));
            }
        }
    }
    let mut files = Vec::new();
    walk(&boot.join("src"), &mut files);
    for (path, source) in files {
        let code = without_comments(&without_test_modules(&source));
        for (index, line) in code.lines().enumerate() {
            for needle in ["SessionTranscriptStore", ".coda/sessions", "coda_agent::"] {
                if line.contains(needle) {
                    offenders.push(format!("{path}:{} uses {needle}", index + 1));
                }
            }
        }
    }
    assert!(
        offenders.is_empty(),
        "coda-boot reads engine-private session state:\n{}\n\
         It carries diagnostics, version, ServeArgs and pure flag parsing. \
         Session resolution is an RPC.",
        offenders.join("\n")
    );
}

/// Local maintenance is gated on the client's launch mode, never on a server
/// claim.
///
/// An engine cannot know whether its client is a local terminal or a browser
/// on another continent, so an engine-advertised `isLocal` would be a claim it
/// is not entitled to make — and would let a misconfigured or hostile engine
/// talk this client into writing files it should not touch.
#[test]
fn the_local_maintenance_gate_is_never_derived_from_the_engine() {
    for (path, source) in sources() {
        let code = without_comments(&without_test_modules(&source));
        for (index, line) in code.lines().enumerate() {
            assert!(
                !line.contains("isLocal") && !line.contains("is_local_engine"),
                "{path}:{} derives locality from the engine. AccessMode is chosen \
                 by this client from how it launched.",
                index + 1
            );
        }
    }
}
/// Both launchers reach the preflight **before** they spawn an engine.
///
/// The behaviour is tested for real against the shared seam
/// (`preflight::prepare_launch_with`, in `coda/tests/tui_client.rs`, with a
/// marker file that proves no child was started). This is the structural half:
/// a binary that went back to spawning first would still compile and still
/// pass every behavioural test written against the seam, because it simply
/// would not be using it.
#[test]
fn every_interactive_launcher_runs_the_preflight_before_it_starts_an_engine() {
    let launchers = [
        ("coda-tui", "src/main.rs"),
        ("coda", "../coda/src/main.rs"),
    ];
    for (name, path) in launchers {
        let source = std::fs::read_to_string(path).unwrap_or_else(|_| panic!("read {path}"));
        let code = without_comments(&without_test_modules(&source));
        let preflight = code
            .find("prepare_launch")
            .unwrap_or_else(|| panic!("{name} does not run the launch preflight at all"));
        // Whichever way this binary starts its engine.
        let spawn = ["App::boot(", "startup::connect(", "App::connect("]
            .into_iter()
            .filter_map(|needle| code.find(needle))
            .min()
            .unwrap_or_else(|| panic!("{name} does not start an engine in a way this test knows"));
        assert!(
            preflight < spawn,
            "{name} spawns its engine before the preflight, so a launch whose selected \
             provider has no credential exits on the engine's own error with the screen \
             that could fix it unreachable"
        );
    }
}
