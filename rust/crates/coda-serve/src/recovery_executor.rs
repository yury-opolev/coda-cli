//! Making an unrecoverable action recoverable, for real.
//!
//! [`RecoveryGuard`] decides *that* an undo is needed; this is what actually
//! creates one. It shells out to `git`, because the actions in question are
//! overwhelmingly git operations and a backup ref costs nothing.
//!
//! The contract inherited from the guard is the important part: **this must
//! never prevent the action**. Every failure is reported as an `Err` string
//! that ends up in the assumption ledger, and the caller proceeds regardless.
//! An operator who asked for an unattended run does not want it halted because
//! a backup ref could not be written.

use std::process::Stdio;

use async_trait::async_trait;
use coda_agent::autonomy::{RecoveryExecutor, RecoveryKind};

/// Creates undo points with `git`.
pub struct GitRecoveryExecutor {
    working_dir: String,
}

impl GitRecoveryExecutor {
    pub fn new(working_dir: impl Into<String>) -> Self {
        Self { working_dir: working_dir.into() }
    }

    /// Run a git command, returning stdout on success or a short error.
    ///
    /// Deliberately terse: the output goes into the ledger, which is rendered
    /// to an operator and summarised back to the model, so a wall of git
    /// diagnostics would be noise in both places.
    async fn git(&self, args: &[&str]) -> Result<String, String> {
        let output = tokio::process::Command::new("git")
            .args(args)
            .current_dir(&self.working_dir)
            .stdin(Stdio::null())
            .output()
            .await
            .map_err(|e| format!("could not run git: {e}"))?;

        if output.status.success() {
            Ok(String::from_utf8_lossy(&output.stdout).trim().to_owned())
        } else {
            let err = String::from_utf8_lossy(&output.stderr);
            Err(err.lines().next().unwrap_or("git failed").trim().to_owned())
        }
    }

    /// A ref name that will not collide with a previous backup in the same run.
    fn backup_ref_name() -> String {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis())
            .unwrap_or(0);
        format!("refs/coda-backup/{stamp}")
    }
}

#[async_trait]
impl RecoveryExecutor for GitRecoveryExecutor {
    async fn create_undo(&self, kind: RecoveryKind, _context: &str) -> Result<String, String> {
        match kind {
            // Pin the current HEAD under a ref of our own. A force-push that
            // overwrites history can then be recovered from this ref, which is
            // the only thing that would otherwise be lost.
            RecoveryKind::GitBackupRef => {
                let head = self.git(&["rev-parse", "HEAD"]).await?;
                let name = Self::backup_ref_name();
                self.git(&["update-ref", &name, &head]).await?;
                Ok(name)
            }
            // `git clean` destroys untracked and uncommitted work, which no ref
            // covers. Stash everything including untracked files, then restore
            // the working tree so the clean still sees what it expected to.
            RecoveryKind::GitStash => {
                let name = format!("coda-backup-{}", Self::backup_ref_name());
                self.git(&["stash", "push", "--include-untracked", "--message", &name]).await?;
                // `stash apply` leaves the stash entry in place as the undo
                // point while returning the tree to its pre-stash state.
                self.git(&["stash", "apply"]).await?;
                Ok(name)
            }
            // A published version or a dropped data store cannot be undone by
            // anything this process can do. Say so plainly rather than
            // inventing a reference that would not help. The action still
            // proceeds; the ledger now records that it had no safety net.
            RecoveryKind::Snapshot => Err(
                "no automatic undo exists for this action; it proceeded without one".to_owned(),
            ),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_backup_ref_name_is_namespaced_and_unique() {
        let a = GitRecoveryExecutor::backup_ref_name();
        assert!(a.starts_with("refs/coda-backup/"), "{a}");
        assert!(!a.ends_with('/'), "the name must carry a stamp: {a}");
    }

    /// The one kind with no possible automatic undo must report that rather
    /// than silently claiming success — the ledger entry is the only record an
    /// operator gets that the action ran unprotected.
    #[tokio::test]
    async fn an_unsnapshotable_action_reports_that_no_undo_exists() {
        let exec = GitRecoveryExecutor::new(".");
        let result = exec.create_undo(RecoveryKind::Snapshot, "npm publish").await;
        let err = result.expect_err("a publish cannot be undone");
        assert!(err.contains("no automatic undo"), "{err}");
    }

    /// A working directory that is not a repository must fail cleanly, not
    /// panic and not hang: the action it guards still has to proceed.
    #[tokio::test]
    async fn a_missing_repository_fails_without_panicking() {
        let dir = std::env::temp_dir().join("coda-recovery-not-a-repo");
        std::fs::create_dir_all(&dir).expect("temp dir");
        let exec = GitRecoveryExecutor::new(dir.to_string_lossy().to_string());

        let result = exec.create_undo(RecoveryKind::GitBackupRef, "git push --force").await;
        assert!(result.is_err(), "a non-repository cannot produce a backup ref");
    }
}
