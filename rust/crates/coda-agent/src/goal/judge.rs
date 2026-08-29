//! Goal judge prompt: build the message, parse the verdict.
//!
//! The judge replies with exactly one line: `DONE` or `CONTINUE: <remaining>`.
//! Only a leading `DONE` (whole first line, case-insensitive) counts as
//! complete — ambiguous or prose replies keep the agent working (fail-open).

/// System prompt handed to the judge agent.
pub const SYSTEM_PROMPT: &str = "\
You decide whether an autonomous coding agent has FULLY achieved a stated \
goal. Be strict: only declare completion when nothing material remains.

Respond with EXACTLY ONE line:
- `DONE` if the goal is fully and verifiably complete.
- `CONTINUE: <what still remains>` otherwise.
Output nothing else.";

/// Build the user message sent to the judge for a given `goal` and
/// the model's most recent output.
pub fn build_user_message(goal: &str, recent_output: &str) -> String {
    format!(
        "Goal:\n{goal}\n\nThe agent's most recent output:\n{recent_output}\n\nIs the goal fully complete?"
    )
}

/// True **only** when the judge's entire first line is exactly `DONE`
/// (case-insensitive).  Any other leading word — including `"DONE, but…"` —
/// keeps the agent working.  This is the deliberate fail-open: ambiguity ≠
/// completion.
pub fn is_complete(response: &str) -> bool {
    let Some(first_line) = response.trim().split('\n').next() else {
        return false;
    };
    first_line.trim().eq_ignore_ascii_case("DONE")
}

/// Extract the "what remains" text from a `CONTINUE: <remaining>` response.
/// Falls back to the whole trimmed response when the prefix is absent.
pub fn remaining(response: &str) -> String {
    let trimmed = response.trim();
    if trimmed.is_empty() {
        return "unspecified remaining work".to_owned();
    }
    // Case-insensitive search for "CONTINUE:" prefix.
    let lower = trimmed.to_ascii_lowercase();
    const PREFIX: &str = "continue:";
    if let Some(idx) = lower.find(PREFIX) {
        return trimmed[idx + PREFIX.len()..].trim().to_owned();
    }
    trimmed.to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    // §8 item 18: only leading DONE completes.
    #[test]
    fn done_completes_case_insensitive() {
        assert!(is_complete("DONE"));
        assert!(is_complete("done"));
        assert!(is_complete("Done"));
        assert!(is_complete("  DONE  "));
        assert!(is_complete("DONE\nsome trailing text")); // only first line checked
    }

    #[test]
    fn done_with_additional_words_does_not_complete() {
        // Whole first line must be exactly "DONE".
        assert!(!is_complete("DONE, but needs more work"));
        assert!(!is_complete("DONE."));
        assert!(!is_complete("DONE!"));
    }

    #[test]
    fn continue_response_does_not_complete() {
        assert!(!is_complete("CONTINUE: fix the tests"));
        assert!(!is_complete("continue: more work"));
    }

    #[test]
    fn empty_response_does_not_complete() {
        assert!(!is_complete(""));
        assert!(!is_complete("   "));
    }

    #[test]
    fn prose_response_does_not_complete() {
        assert!(!is_complete("The agent has finished all tasks."));
    }

    // §8 item 18: remaining extracts text after CONTINUE:.
    #[test]
    fn remaining_extracts_after_continue_prefix() {
        assert_eq!(remaining("CONTINUE: finish the tests"), "finish the tests");
        assert_eq!(remaining("continue: more work needed"), "more work needed");
    }

    #[test]
    fn remaining_falls_back_to_trimmed_response() {
        assert_eq!(remaining("some arbitrary prose"), "some arbitrary prose");
    }

    #[test]
    fn remaining_on_empty_gives_unspecified() {
        assert_eq!(remaining(""), "unspecified remaining work");
        assert_eq!(remaining("  "), "unspecified remaining work");
    }
}
