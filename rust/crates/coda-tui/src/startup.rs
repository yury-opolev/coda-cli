//! What the launch flags ask for, and turning that into a session id.
//!
//! Kept out of the argument parsers because there are two front-ends — the
//! unified `coda` binary and the standalone `coda-tui` one — and the rules are
//! fiddly enough that two copies would drift. Keeping it here also makes it
//! testable: resolving an intent needs only a directory, not a terminal or a
//! running engine.

use std::path::Path;

use coda_agent::SessionTranscriptStore;

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

/// Why a launch could not open the session it was asked for.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StartupError {
    /// The id was given explicitly and there is no such session.
    NotFound(String),
    /// The directory holds no sessions at all.
    NoSessions,
}

impl std::fmt::Display for StartupError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            // Sanitised: the id came from the command line, and it is about to
            // be printed to a terminal that interprets escape sequences.
            StartupError::NotFound(id) => write!(
                f,
                "No session '{}' in this directory.",
                coda_render::text::sanitize(id)
            ),
            StartupError::NoSessions => {
                write!(f, "No sessions in this directory yet.")
            }
        }
    }
}

impl std::error::Error for StartupError {}

/// The id of the most recent session in `project_root`.
fn latest(project_root: &Path) -> Option<String> {
    SessionTranscriptStore::new(project_root)
        .list()
        .into_iter()
        // `list` makes no ordering promise, so pick explicitly rather than
        // trusting directory order — which is what "most recent" has to mean.
        .max_by_key(|s| s.created_utc)
        .map(|s| s.id)
}

/// Whether a session exists, so an explicit id fails loudly rather than
/// silently starting an empty session the user did not ask for.
fn exists(project_root: &Path, id: &str) -> bool {
    SessionTranscriptStore::new(project_root)
        .list()
        .iter()
        .any(|s| s.id == id)
}

/// Turns an intent into the session id to hand the engine.
///
/// `Ok(None)` means "start fresh". Forking copies the source to a new id and
/// returns that, so the original is left untouched.
pub async fn resolve(
    intent: &SessionIntent,
    project_root: &Path,
) -> Result<Option<String>, StartupError> {
    match intent {
        SessionIntent::New => Ok(None),

        SessionIntent::Resume(id) => {
            if exists(project_root, id) {
                Ok(Some(id.clone()))
            } else {
                Err(StartupError::NotFound(id.clone()))
            }
        }

        SessionIntent::Latest => latest(project_root).map(Some).ok_or(StartupError::NoSessions),

        SessionIntent::Fork(source) => {
            let source = match source {
                Some(id) if exists(project_root, id) => id.clone(),
                Some(id) => return Err(StartupError::NotFound(id.clone())),
                None => latest(project_root).ok_or(StartupError::NoSessions)?,
            };

            let store = SessionTranscriptStore::new(project_root);
            let stored = store
                .load(&source)
                .await
                .ok_or_else(|| StartupError::NotFound(source.clone()))?;

            let new_id = coda_agent::session::fork(
                &project_root.to_string_lossy(),
                Some(&source),
                &stored,
                None,
            )
            .await;
            Ok(Some(new_id))
        }
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

    #[tokio::test]
    async fn a_fresh_start_resolves_to_no_session() {
        let dir = tempfile::tempdir().expect("tempdir");
        let resolved = resolve(&SessionIntent::New, dir.path()).await.expect("resolve");
        assert_eq!(resolved, None);
    }

    #[tokio::test]
    async fn an_unknown_id_is_refused_rather_than_starting_empty() {
        // Silently starting a fresh session would look like the resume worked
        // and the history was lost.
        let dir = tempfile::tempdir().expect("tempdir");
        let error = resolve(&SessionIntent::Resume("nope".into()), dir.path())
            .await
            .expect_err("an unknown id must fail");
        assert_eq!(error, StartupError::NotFound("nope".into()));
    }

    #[tokio::test]
    async fn continuing_with_nothing_to_continue_says_so() {
        let dir = tempfile::tempdir().expect("tempdir");
        let error = resolve(&SessionIntent::Latest, dir.path())
            .await
            .expect_err("an empty directory has nothing to continue");
        assert_eq!(error, StartupError::NoSessions);
    }

    #[tokio::test]
    async fn the_most_recent_session_is_the_one_continued() {
        use coda_llm::{Content, Message, Role};

        let dir = tempfile::tempdir().expect("tempdir");
        let store = SessionTranscriptStore::new(dir.path());
        let message = |text: &str| {
            vec![Message::new(
                Role::User,
                vec![Content::Text(text.into())],
            )]
        };

        store.save("older", &message("first"), None).await.expect("save");
        // The store stamps creation time on write, so a gap guarantees an
        // order rather than relying on filesystem timestamps agreeing.
        tokio::time::sleep(std::time::Duration::from_millis(1100)).await;
        store.save("newer", &message("second"), None).await.expect("save");

        let resolved = resolve(&SessionIntent::Latest, dir.path()).await.expect("resolve");
        assert_eq!(resolved.as_deref(), Some("newer"));
    }

    #[tokio::test]
    async fn forking_opens_a_copy_and_leaves_the_original_alone() {
        use coda_llm::{Content, Message, Role};

        let dir = tempfile::tempdir().expect("tempdir");
        let store = SessionTranscriptStore::new(dir.path());
        let messages = vec![Message::new(
            Role::User,
            vec![Content::Text("original".into())],
        )];
        store.save("source", &messages, None).await.expect("save");

        let forked = resolve(&SessionIntent::Fork(Some("source".into())), dir.path())
            .await
            .expect("fork")
            .expect("a fork always opens a session");

        assert_ne!(forked, "source", "a fork must not reuse the source id");
        assert!(
            store.load("source").await.is_some(),
            "the source session was consumed by forking it"
        );
        let copy = store.load(&forked).await.expect("the fork was not written");
        assert_eq!(copy.len(), messages.len(), "the fork lost the history");
    }
}
