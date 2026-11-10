//! Task titles: short single-line labels identifying a task (not its prompt).

/// Longest accepted title, in Unicode scalar values after trimming.
pub const MAX_TASK_TITLE_CHARS: usize = 80;

fn is_forbidden(c: char) -> bool {
    c.is_control()
        || matches!(
            c,
            '\u{2028}' | '\u{2029}' | '\u{202A}'..='\u{202E}' | '\u{2066}'..='\u{2069}'
        )
}

/// Validates a task title, returning it trimmed.
pub fn validate_task_title(raw: &str) -> Result<String, String> {
    let title = raw.trim();
    if title.is_empty() {
        return Err(
            "Missing required 'title': give the task a short single-line label \
             (3-8 words, at most 80 characters), for example \"Review authentication flow\"."
                .to_owned(),
        );
    }
    if title.chars().any(is_forbidden) {
        return Err(
            "Invalid task title: it must be a single line without control characters.".to_owned(),
        );
    }
    let count = title.chars().count();
    if count > MAX_TASK_TITLE_CHARS {
        return Err(format!(
            "Invalid task title: it is {count} characters; the limit is {MAX_TASK_TITLE_CHARS}. \
             Use a short label (3-8 words) and keep details in the prompt."
        ));
    }
    Ok(title.to_owned())
}

/// A valid title derived deterministically from `text` for internal producers.
pub fn derive_task_title(text: &str) -> String {
    let line = text
        .lines()
        .map(str::trim)
        .find(|l| !l.is_empty())
        .unwrap_or("");
    let cleaned: String = line
        .chars()
        .map(|c| if is_forbidden(c) { ' ' } else { c })
        .collect();
    let collapsed = cleaned.split_whitespace().collect::<Vec<_>>().join(" ");
    if collapsed.is_empty() {
        return "Untitled task".to_owned();
    }
    if collapsed.chars().count() <= MAX_TASK_TITLE_CHARS {
        return collapsed;
    }
    let mut out: String = collapsed.chars().take(MAX_TASK_TITLE_CHARS - 1).collect();
    out = out.trim_end().to_owned();
    out.push('…');
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_and_trims_a_short_label() {
        assert_eq!(
            validate_task_title("  Review authentication flow ").unwrap(),
            "Review authentication flow"
        );
    }

    #[test]
    fn rejects_missing_blank_and_whitespace() {
        for t in ["", "   ", "\u{3000}"] {
            assert!(validate_task_title(t)
                .unwrap_err()
                .contains("Missing required 'title'"));
        }
    }

    #[test]
    fn rejects_line_breaks_and_controls() {
        for t in [
            "a\nb",
            "a\rb",
            "a\tb",
            "a\u{1b}[2Jb",
            "a\u{7f}b",
            "a\u{85}b",
            "a\u{2028}b",
            "a\u{2029}b",
            "a\u{202e}b",
            "a\u{2066}b",
        ] {
            assert!(
                validate_task_title(t).unwrap_err().contains("single line"),
                "{t:?}"
            );
        }
    }

    #[test]
    fn bound_is_80_characters_not_bytes() {
        assert!(validate_task_title(&"é".repeat(80)).is_ok());
        assert!(validate_task_title(&"界".repeat(80)).is_ok());
        let err = validate_task_title(&"x".repeat(81)).unwrap_err();
        assert!(err.contains("81") && err.contains("80"), "{err}");
    }

    #[test]
    fn derive_takes_first_line_collapses_and_bounds() {
        assert_eq!(
            derive_task_title("\n  cargo   test -p x\nsecond"),
            "cargo test -p x"
        );
        assert_eq!(derive_task_title("a\u{1b}b"), "a b");
        let long = derive_task_title(&"y".repeat(200));
        assert_eq!(long.chars().count(), 80);
        assert!(long.ends_with('…'));
        assert!(validate_task_title(&long).is_ok());
        assert_eq!(derive_task_title("   "), "Untitled task");
    }
}
