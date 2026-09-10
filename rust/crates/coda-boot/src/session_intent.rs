//! What the launch flags ask for — the pure part.
//!
//! `SessionIntent` and `from_flags` are pure functions of the parsed CLI
//! flags: no disk access, no engine, not even a working directory. That is
//! what makes them shareable between the unified `coda` binary, the
//! standalone `coda-tui` frontend, and (in principle) `coda-engine` or any
//! other frontend that wants the same `--resume`/`--continue`/`--fork`
//! vocabulary — none of which should need to link the TUI to parse a flag.
//!
//! What is deliberately *not* here: resolving an intent into an actual
//! session id needs `SessionTranscriptStore` disk reads, which is an
//! agent/session-store dependency this crate does not take. That half stays
//! in `coda_tui::startup::resolve`, which re-exports this type rather than
//! defining its own copy.

/// The session a launch asks to open.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SessionIntent {
    /// Start fresh.
    New,
    /// Open this exact session.
    Resume(String),
    /// Open the most recent session in this directory.
    Latest,
    /// Copy a session to a new id and open that, leaving the original alone.
    /// `None` forks the most recent.
    Fork(Option<String>),
}

impl SessionIntent {
    /// Reads the mutually exclusive launch flags.
    ///
    /// Mirrors the C#: an id is optional everywhere it is accepted, and
    /// leaving it off means "the most recent". `--resume` with no id is
    /// therefore the same as `--continue`, which is what a user reaching for
    /// either of them expects.
    pub fn from_flags(
        continue_latest: bool,
        resume: Option<Option<String>>,
        fork: Option<Option<String>>,
    ) -> Self {
        // Checked in this order only to be deterministic; the parser rejects
        // more than one of them, so at most one is ever set.
        if let Some(fork) = fork {
            return SessionIntent::Fork(fork.filter(|id| !id.trim().is_empty()));
        }
        if let Some(resume) = resume {
            return match resume.filter(|id| !id.trim().is_empty()) {
                Some(id) => SessionIntent::Resume(id),
                None => SessionIntent::Latest,
            };
        }
        if continue_latest {
            return SessionIntent::Latest;
        }
        SessionIntent::New
    }

    /// Whether this asks for anything other than a fresh session.
    pub fn wants_a_session(&self) -> bool {
        !matches!(self, SessionIntent::New)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn no_flags_starts_a_fresh_session() {
        assert_eq!(
            SessionIntent::from_flags(false, None, None),
            SessionIntent::New
        );
    }

    #[test]
    fn continue_asks_for_the_most_recent() {
        assert_eq!(
            SessionIntent::from_flags(true, None, None),
            SessionIntent::Latest
        );
    }

    #[test]
    fn resume_with_an_id_asks_for_that_session() {
        assert_eq!(
            SessionIntent::from_flags(false, Some(Some("abc123".into())), None),
            SessionIntent::Resume("abc123".into())
        );
    }

    #[test]
    fn resume_without_an_id_means_the_most_recent() {
        // Same as --continue. A user who types `--resume` and nothing else is
        // asking to get back to what they were doing, not to be told off.
        assert_eq!(
            SessionIntent::from_flags(false, Some(None), None),
            SessionIntent::Latest
        );
    }

    #[test]
    fn a_blank_id_is_treated_as_no_id() {
        // `--resume ""` is a shell accident, not a request for a session whose
        // id is the empty string.
        assert_eq!(
            SessionIntent::from_flags(false, Some(Some("   ".into())), None),
            SessionIntent::Latest
        );
        assert_eq!(
            SessionIntent::from_flags(false, None, Some(Some(String::new()))),
            SessionIntent::Fork(None)
        );
    }

    #[test]
    fn fork_carries_its_optional_source() {
        assert_eq!(
            SessionIntent::from_flags(false, None, Some(Some("abc".into()))),
            SessionIntent::Fork(Some("abc".into()))
        );
        assert_eq!(
            SessionIntent::from_flags(false, None, Some(None)),
            SessionIntent::Fork(None)
        );
    }

    #[test]
    fn only_a_fresh_start_wants_no_session() {
        assert!(!SessionIntent::New.wants_a_session());
        assert!(SessionIntent::Latest.wants_a_session());
        assert!(SessionIntent::Resume("x".into()).wants_a_session());
        assert!(SessionIntent::Fork(None).wants_a_session());
    }
}
