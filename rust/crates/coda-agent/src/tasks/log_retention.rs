//! Startup housekeeping for the persistent task-log tree.
//!
//! Deletes logs older than [`MAX_AGE`] and then, newest-first, deletes older
//! logs once the total size exceeds [`GLOBAL_CAP_BYTES`]. Best-effort: missing
//! or unreadable roots and individual delete failures are ignored.

use std::path::Path;
use std::time::{Duration, SystemTime};

/// Files older than this are unconditionally deleted.
pub const MAX_AGE: Duration = Duration::from_secs(7 * 24 * 3600); // 7 days

/// Once total log bytes exceed this, oldest logs are pruned.
pub const GLOBAL_CAP_BYTES: u64 = 512 * 1024 * 1024; // 512 MiB

/// Run retention cleanup under `root`. Best-effort: all errors are swallowed.
pub fn cleanup(root: &Path) {
    cleanup_with_clock(root, MAX_AGE, GLOBAL_CAP_BYTES, SystemTime::now());
}

/// Overload accepting an explicit "now" for deterministic testing.
pub fn cleanup_with_clock(
    root: &Path,
    max_age: Duration,
    global_cap: u64,
    now: SystemTime,
) {
    if !root.exists() {
        return;
    }

    let mut files = match collect_log_files(root) {
        Some(f) => f,
        None => return,
    };

    // Sort newest-first by last-modified time.
    files.sort_by(|a, b| b.modified.cmp(&a.modified));

    // 1) Age-based deletion.
    let mut survivors: Vec<LogFile> = Vec::new();
    for f in files {
        if now
            .duration_since(f.modified)
            .map(|d| d > max_age)
            .unwrap_or(false)
        {
            let _ = std::fs::remove_file(&f.path);
        } else {
            survivors.push(f);
        }
    }

    // 2) Global-cap deletion, newest-first: keep until cap exceeded.
    let mut total: u64 = 0;
    for f in &survivors {
        total += f.size;
        if total > global_cap {
            let _ = std::fs::remove_file(&f.path);
        }
    }
}

struct LogFile {
    path: std::path::PathBuf,
    size: u64,
    modified: SystemTime,
}

fn collect_log_files(root: &Path) -> Option<Vec<LogFile>> {
    let mut files = Vec::new();
    collect_recursive(root, &mut files);
    if files.is_empty() {
        // Empty is fine — not an error.
    }
    Some(files)
}

fn collect_recursive(dir: &Path, out: &mut Vec<LogFile>) {
    let entries = match std::fs::read_dir(dir) {
        Ok(e) => e,
        Err(_) => return,
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            collect_recursive(&path, out);
        } else if path.extension().and_then(|e| e.to_str()) == Some("log") {
            if let Ok(meta) = entry.metadata() {
                let modified = meta.modified().unwrap_or(SystemTime::UNIX_EPOCH);
                out.push(LogFile {
                    path,
                    size: meta.len(),
                    modified,
                });
            }
        }
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn test_dir(name: &str) -> std::path::PathBuf {
        let d = std::env::temp_dir().join(format!("coda-retention-{name}"));
        fs::create_dir_all(&d).ok();
        d
    }

    fn write_file(dir: &Path, name: &str, content: &str) -> std::path::PathBuf {
        let p = dir.join(name);
        fs::write(&p, content).unwrap();
        p
    }

    #[test]
    fn empty_root_does_not_panic() {
        let dir = test_dir("empty");
        cleanup_with_clock(&dir, MAX_AGE, GLOBAL_CAP_BYTES, SystemTime::now());
    }

    #[test]
    fn nonexistent_root_does_not_panic() {
        let p = std::env::temp_dir().join("coda-retention-nonexistent-xyz");
        cleanup_with_clock(&p, MAX_AGE, GLOBAL_CAP_BYTES, SystemTime::now());
    }

    #[test]
    fn old_files_are_deleted() {
        let dir = test_dir("old_files");
        let p = write_file(&dir, "old.log", "x");

        // Advance "now" far beyond the file's modified time (which is actual wall clock).
        // Use a large duration to make the file appear old.
        let future_now = SystemTime::now() + Duration::from_secs(30 * 24 * 3600); // 30 days ahead
        cleanup_with_clock(&dir, Duration::from_secs(1), GLOBAL_CAP_BYTES, future_now);

        assert!(!p.exists(), "old file was not deleted");
    }

    #[test]
    fn recent_files_are_kept_within_age() {
        let dir = test_dir("recent_files");
        let p = write_file(&dir, "recent.log", "x");

        // Now is basically "now", so the file is very recent.
        cleanup_with_clock(&dir, MAX_AGE, GLOBAL_CAP_BYTES, SystemTime::now());

        assert!(p.exists(), "recent file was deleted unexpectedly");
        fs::remove_file(&p).ok();
    }

    #[test]
    fn global_cap_deletes_oldest_files() {
        let dir = test_dir("global_cap");
        // Write 3 files with known sizes; total > cap.
        let a = write_file(&dir, "a.log", &"a".repeat(100));
        let b = write_file(&dir, "b.log", &"b".repeat(100));
        let c = write_file(&dir, "c.log", &"c".repeat(100));
        // Cap is 150 bytes — only one file (100 bytes) fits.
        cleanup_with_clock(&dir, MAX_AGE, 150, SystemTime::now());

        let survivors = [a.exists(), b.exists(), c.exists()]
            .iter()
            .filter(|&&x| x)
            .count();
        assert!(survivors <= 1, "expected at most 1 survivor, got {survivors}");

        for p in [&a, &b, &c] {
            let _ = fs::remove_file(p);
        }
    }
}
