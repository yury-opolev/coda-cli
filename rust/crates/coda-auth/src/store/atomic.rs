//! Writing a credential without ever exposing a half-written file.
//!
//! A credential is read by other processes at arbitrary moments — another
//! engine, the TUI, a background refresh. Truncating the file in place makes
//! those readers observe an empty or partial file, which a strict reader
//! correctly reports as corruption; and if the write then fails, the previous
//! credential is gone for good.
//!
//! So every credential write goes to a scratch file in the same directory and
//! is moved into place with a rename, which replaces the target atomically on
//! both Windows and Unix. A failure leaves the previous file byte-identical,
//! and the scratch file is removed on the way out.

use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use crate::error::AuthError;

/// Distinguishes scratch files written concurrently by this process.
static SCRATCH_COUNTER: AtomicU64 = AtomicU64::new(0);

/// Extension used for scratch files, so a directory scan can tell them from
/// credentials (`.cred`) and never mistake one for a stored credential.
const SCRATCH_EXT: &str = "tmp";

/// Writes `bytes` to `path`, replacing any previous content atomically.
pub(crate) fn write_atomic(path: &Path, bytes: &[u8]) -> Result<(), AuthError> {
    let directory = path.parent().ok_or_else(|| {
        AuthError::store(format!("{} has no parent directory", path.display()))
    })?;
    let scratch = Scratch::create(directory, path)?;
    scratch.write(bytes)?;
    scratch.commit(path)
}

/// What happened when a file was published with [`write_new_atomic`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Publication {
    /// This caller published the file.
    Created,
    /// Somebody else got there first; the file was left exactly as it is.
    AlreadyExisted,
}

/// Publishes `bytes` at `path` only if nothing is there yet — and only ever as
/// a complete file.
///
/// The guarantee this exists for: `path` is never observable in a partial
/// state. The content is written to a scratch file and flushed to the device
/// first, and the name only appears through a hard link, which fails rather
/// than replaces when the name is taken. So a concurrent reader — another
/// thread, another process, or the factory taking its directory facts — sees
/// either no file at all or the whole thing.
///
/// A rename could not be used: it replaces the target on both platforms, which
/// would overwrite the winner's file.
pub(crate) fn write_new_atomic(path: &Path, bytes: &[u8]) -> Result<Publication, AuthError> {
    let directory = path.parent().ok_or_else(|| {
        AuthError::store(format!("{} has no parent directory", path.display()))
    })?;

    // Never committed: the scratch file is removed on the way out either way,
    // whether we won the race or lost it.
    let mut scratch = Scratch::create(directory, path)?;
    scratch.write(bytes)?;
    scratch.close();

    match std::fs::hard_link(&scratch.path, path) {
        Ok(()) => Ok(Publication::Created),
        Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {
            Ok(Publication::AlreadyExisted)
        }
        Err(e) => Err(AuthError::store(format!(
            "cannot publish {}: {e}. Nothing was changed",
            path.display()
        ))),
    }
}

/// A scratch file that deletes itself unless it is committed.
struct Scratch {
    path: PathBuf,
    file: Option<std::fs::File>,
    committed: bool,
}

impl Scratch {
    fn create(directory: &Path, target: &Path) -> Result<Self, AuthError> {
        let stem = target
            .file_name()
            .map(|n| n.to_string_lossy().to_string())
            .unwrap_or_else(|| "credential".to_owned());

        // A unique name per attempt: two writers in one process, or two
        // processes, must never share a scratch file.
        for attempt in 0..16u32 {
            let unique = SCRATCH_COUNTER.fetch_add(1, Ordering::Relaxed);
            let candidate = directory.join(format!(
                ".{stem}.{}.{unique}.{attempt}.{SCRATCH_EXT}",
                std::process::id()
            ));
            match std::fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&candidate)
            {
                Ok(file) => {
                    let scratch = Self { path: candidate, file: Some(file), committed: false };
                    scratch.restrict_to_owner()?;
                    return Ok(scratch);
                }
                Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
                Err(e) => {
                    return Err(AuthError::store(format!(
                        "cannot create a scratch file in {}: {e}",
                        directory.display()
                    )))
                }
            }
        }
        Err(AuthError::store(format!(
            "cannot create a scratch file in {}: every candidate name was taken",
            directory.display()
        )))
    }

    fn restrict_to_owner(&self) -> Result<(), AuthError> {
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&self.path, std::fs::Permissions::from_mode(0o600))?;
        }
        Ok(())
    }

    fn write(&self, bytes: &[u8]) -> Result<(), AuthError> {
        let mut file = self.file.as_ref().expect("the scratch file is open until commit");
        file.write_all(bytes)
            .map_err(|e| AuthError::store(format!("cannot write {}: {e}", self.path.display())))?;
        // Flush to the device before the rename: a rename that beats its own
        // data to disk can leave an empty file behind a crash.
        file.sync_all()
            .map_err(|e| AuthError::store(format!("cannot flush {}: {e}", self.path.display())))
    }

    fn commit(mut self, target: &Path) -> Result<(), AuthError> {
        // Close before renaming: Windows will not move a file that is open for
        // writing in this process.
        self.file = None;
        std::fs::rename(&self.path, target).map_err(|e| {
            AuthError::store(format!(
                "cannot replace {} with the new credential: {e}",
                target.display()
            ))
        })?;
        self.committed = true;
        Ok(())
    }

    /// Releases the handle, keeping the file on disk for a publication that
    /// does not go through a rename.
    fn close(&mut self) {
        self.file = None;
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        if !self.committed {
            self.file = None;
            let _ = std::fs::remove_file(&self.path);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_replacement_is_all_or_nothing() {
        let dir = std::env::temp_dir().join(format!("coda-atomic-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("value.cred");

        write_atomic(&path, b"first").unwrap();
        write_atomic(&path, b"second-and-longer").unwrap();
        assert_eq!(std::fs::read(&path).unwrap(), b"second-and-longer");

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_new_file_is_published_whole_and_never_replaces_a_winner() {
        let dir = std::env::temp_dir().join(format!("coda-publish-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("key.bin");

        assert_eq!(write_new_atomic(&path, b"first-writer").unwrap(), Publication::Created);
        assert_eq!(std::fs::read(&path).unwrap(), b"first-writer");

        // A second publisher must lose, and must not touch the winner.
        assert_eq!(
            write_new_atomic(&path, b"second-writer").unwrap(),
            Publication::AlreadyExisted
        );
        assert_eq!(
            std::fs::read(&path).unwrap(),
            b"first-writer",
            "the winner's file must be byte-identical"
        );

        // Neither attempt may leave a scratch file behind.
        let leftovers: Vec<_> = std::fs::read_dir(&dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .map(|e| e.file_name().to_string_lossy().to_string())
            .filter(|n| n != "key.bin")
            .collect();
        assert!(leftovers.is_empty(), "scratch files must not survive: {leftovers:?}");

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_failed_write_removes_its_scratch_file() {
        let dir = std::env::temp_dir().join(format!("coda-atomic-fail-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        // A target that is a directory cannot be replaced by a rename.
        let path = dir.join("occupied.cred");
        std::fs::create_dir_all(&path).unwrap();

        assert!(write_atomic(&path, b"value").is_err());

        let leftovers: Vec<_> = std::fs::read_dir(&dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .map(|e| e.file_name().to_string_lossy().to_string())
            .filter(|n| n.ends_with(SCRATCH_EXT))
            .collect();
        assert!(leftovers.is_empty(), "scratch files must not survive: {leftovers:?}");

        let _ = std::fs::remove_dir_all(&dir);
    }
}
