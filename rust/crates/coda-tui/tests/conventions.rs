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
fn without_test_modules(source: &str) -> String {
    match source.find("#[cfg(test)]") {
        Some(at) => source[..at].to_string(),
        None => source.to_string(),
    }
}

/// Drops comment text, so a constant may document itself with the glyph it
/// names without the documentation counting as a violation.
fn without_comments(source: &str) -> String {
    source
        .lines()
        .map(|line| match line.find("//") {
            Some(at) => &line[..at],
            None => line,
        })
        .collect::<Vec<_>>()
        .join("\n")
}

/// The symbol glyphs that must come from `render::glyphs`.
///
/// These carry chrome and state, where a stray variant is a visual bug. Arrows,
/// the middot, the ellipsis and the em dash are deliberately absent: they occur
/// inside hint prose such as `"↑/↓ k/j move · Enter select"`, where spelling
/// them as constants would make the sentence unreadable for no gain.
const ENFORCED: &[char] = &[
    '\u{276F}', // ❯ prompt and focus marker
    '\u{22EF}', // ⋯ busy
    '\u{203A}', // › selected option
    '\u{2584}', // ▄ composer top edge
    '\u{2580}', // ▀ composer bottom edge
    '\u{25CF}', // ● filled dot
    '\u{25CB}', // ○ hollow dot
    '\u{2022}', // • bullet
    '\u{25BC}', // ▼ dropdown closed
    '\u{25B2}', // ▲ dropdown open
    '\u{25A0}', // ■ square
    '\u{2713}', // ✓ check
    '\u{2717}', // ✗ cross
    '\u{2588}', // █ block
];

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
fn raw_glyph_characters_live_only_in_the_glyph_table() {
    // The companion to the test above, and the one that actually bites: an
    // escape is easy to grep for, so the tempting way around the rule is to
    // paste the character itself. Both spellings have to be closed or the
    // convention only looks enforced.
    let mut offenders: Vec<String> = Vec::new();

    for (path, source) in sources() {
        if path.replace('\\', "/").ends_with("render/glyphs.rs") {
            continue;
        }
        let code = without_comments(&without_test_modules(&source));
        for (index, line) in code.lines().enumerate() {
            if let Some(glyph) = line.chars().find(|c| ENFORCED.contains(c)) {
                offenders.push(format!("{path}:{} contains {glyph:?}", index + 1));
            }
        }
    }

    assert!(
        offenders.is_empty(),
        "raw glyph characters must come from render::glyphs, found:\n{}",
        offenders.join("\n")
    );
}
