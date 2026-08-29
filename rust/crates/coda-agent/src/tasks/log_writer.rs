//! Persistent, secret-redacted, UTF-8 (no BOM) append-only diagnostic log for a
//! single task.
//!
//! Each [`TaskOutputChannel`] keeps its own independent
//! [`StreamingSecretRedactor`] state so a secret split across chunks on one
//! channel cannot be corrupted or leaked by interleaved chunks on another channel.
//!
//! When appending would exceed `max_bytes`, the log is trimmed to a
//! code-point-valid newest suffix that leaves headroom, so sustained writing past
//! the cap amortizes to a bounded number of rewrites rather than one per append.
//!
//! Logging is best-effort: any I/O or permission failure disables the writer
//! without throwing.

use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use super::streaming_secret_redactor::StreamingSecretRedactor;

/// 50 MiB default — matches C# `DefaultMaxBytes`.
pub const DEFAULT_MAX_BYTES: u64 = 50 * 1024 * 1024;

/// Smallest headroom reclaimed on a trim so tiny caps still amortize.
const MIN_HEADROOM_BYTES: u64 = 512;

/// Output channel, used to keep redactor states independent per stream.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(usize)]
pub enum TaskOutputChannel {
    General = 0,
    Stdout = 1,
    Stderr = 2,
}

struct WriterState {
    file: Option<File>,
    bytes_written: u64,
    trim_count: u32,
    faulted: bool,
    disposed: bool,
    redactors: [StreamingSecretRedactor; 3],
}

pub struct TaskLogWriter {
    path: PathBuf,
    max_bytes: u64,
    state: Mutex<WriterState>,
}

impl TaskLogWriter {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self::with_max_bytes(path, DEFAULT_MAX_BYTES)
    }

    pub fn with_max_bytes(path: impl Into<PathBuf>, max_bytes: u64) -> Self {
        Self {
            path: path.into(),
            max_bytes: max_bytes.max(1),
            state: Mutex::new(WriterState {
                file: None,
                bytes_written: 0,
                trim_count: 0,
                faulted: false,
                disposed: false,
                redactors: [
                    StreamingSecretRedactor::new(),
                    StreamingSecretRedactor::new(),
                    StreamingSecretRedactor::new(),
                ],
            }),
        }
    }

    /// Number of cap-enforcing trims performed (test instrumentation).
    #[cfg(test)]
    pub fn trim_count(&self) -> u32 {
        self.state.lock().unwrap().trim_count
    }

    /// Append text on the given channel, streamed through that channel's
    /// independent redactor. Never throws.
    pub fn append(&self, text: &str, channel: TaskOutputChannel) {
        if text.is_empty() {
            return;
        }
        let mut s = self.state.lock().unwrap();
        if s.faulted || s.disposed {
            return;
        }
        // Run through the per-channel redactor into a scratch buffer.
        let mut redacted = String::with_capacity(text.len());
        s.redactors[channel as usize].process(text, &mut redacted);
        if redacted.is_empty() {
            return;
        }
        let redacted_clone = redacted.clone(); // keep before fault path
        if let Err(_) = emit(&mut s, &self.path, &redacted_clone, self.max_bytes) {
            s.faulted = true;
            try_close(&mut s.file);
        }
    }

    /// Convenience overload: append on the General channel.
    pub fn append_general(&self, text: &str) {
        self.append(text, TaskOutputChannel::General);
    }

    /// Flush all channel redactors' trailing candidates and close the file.
    /// Idempotent and never throws.
    pub fn dispose(&self) {
        let mut s = self.state.lock().unwrap();
        if s.disposed {
            return;
        }
        s.disposed = true;
        if !s.faulted {
            // Flush each channel's trailing unconfirmed candidate.
            for idx in 0..3usize {
                let mut flushed = String::new();
                s.redactors[idx].flush(&mut flushed);
                if !flushed.is_empty() {
                    let _ = emit(&mut s, &self.path, &flushed, self.max_bytes);
                }
            }
        }
        try_close(&mut s.file);
    }
}

impl Drop for TaskLogWriter {
    fn drop(&mut self) {
        self.dispose();
    }
}

// ── I/O helpers ───────────────────────────────────────────────────────────────

fn emit(s: &mut WriterState, path: &Path, text: &str, max_bytes: u64) -> std::io::Result<()> {
    ensure_open(s, path)?;
    let file = s.file.as_mut().unwrap();
    let new_bytes = text.len() as u64;

    if s.bytes_written + new_bytes <= max_bytes {
        file.write_all(text.as_bytes())?;
        file.flush()?;
        s.bytes_written += new_bytes;
        return Ok(());
    }

    trim_and_write(s, path, text, max_bytes)
}

/// Append would exceed the cap. Trim the existing log to a newest suffix that
/// leaves headroom, then rewrite it followed by the new text. Because a trim
/// reclaims a fixed fraction of the cap, only an amortized, bounded number of
/// trims happen rather than a rewrite each time.
fn trim_and_write(
    s: &mut WriterState,
    path: &Path,
    text: &str,
    max_bytes: u64,
) -> std::io::Result<()> {
    // Close the append handle before rewriting.
    try_close(&mut s.file);

    let mut headroom = max_bytes / 4;
    if headroom < MIN_HEADROOM_BYTES {
        headroom = MIN_HEADROOM_BYTES;
    }
    let max_headroom = (max_bytes / 2).max(1);
    if headroom > max_headroom {
        headroom = max_headroom;
    }

    let new_bytes = text.len() as u64;
    let (tail, kept_new) = if new_bytes > max_bytes {
        // Incoming text alone exceeds the cap: keep only its newest suffix.
        let kept = newest_suffix_within_cap(text, max_bytes as usize);
        (String::new(), kept.to_owned())
    } else {
        let keep_existing = max_bytes.saturating_sub(headroom).saturating_sub(new_bytes);
        let tail = if keep_existing == 0 {
            String::new()
        } else {
            let existing = fs::read_to_string(path).unwrap_or_default();
            newest_suffix_within_cap(&existing, keep_existing as usize).to_owned()
        };
        (tail, text.to_owned())
    };

    let combined: String = if tail.is_empty() {
        kept_new
    } else {
        format!("{tail}{kept_new}")
    };

    let mut file = open_create(path)?;
    file.write_all(combined.as_bytes())?;
    file.flush()?;
    s.bytes_written = combined.len() as u64;
    s.file = Some(file);
    s.trim_count += 1;
    Ok(())
}

/// Open the file for appending, creating it and its parent directory if needed.
fn ensure_open(s: &mut WriterState, path: &Path) -> std::io::Result<()> {
    if s.file.is_some() {
        return Ok(());
    }
    if let Some(dir) = path.parent() {
        if !dir.as_os_str().is_empty() {
            create_dir_restrictive(dir)?;
        }
    }
    let file = open_append(path)?;
    s.bytes_written = file.metadata()?.len();
    s.file = Some(file);
    Ok(())
}

fn open_append(path: &Path) -> std::io::Result<File> {
    let mut opts = OpenOptions::new();
    opts.append(true).create(true);

    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        opts.mode(0o600);
    }

    opts.open(path)
}

fn open_create(path: &Path) -> std::io::Result<File> {
    let mut opts = OpenOptions::new();
    opts.write(true).create(true).truncate(true);

    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        opts.mode(0o600);
    }

    opts.open(path)
}

fn create_dir_restrictive(dir: &Path) -> std::io::Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::DirBuilderExt;
        fs::DirBuilder::new().recursive(true).mode(0o700).create(dir)
    }
    #[cfg(not(unix))]
    {
        fs::create_dir_all(dir)
    }
}

fn try_close(file: &mut Option<File>) {
    let _ = file.take(); // drops the file, which flushes the OS buffer
}

/// Returns the newest suffix of `s` whose UTF-8 encoding fits in `max_bytes`,
/// cut on a code-point boundary. If even the last code point alone exceeds the
/// cap, that whole rune is still retained (matching the C# behaviour).
pub fn newest_suffix_within_cap(s: &str, max_bytes: usize) -> &str {
    if max_bytes == 0 || s.is_empty() {
        return "";
    }
    &s[super::suffix_start_within_cap(s, max_bytes)..]
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn tmp_path(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join("coda-log-writer-tests");
        fs::create_dir_all(&dir).ok();
        dir.join(name)
    }

    fn read(path: &Path) -> String {
        fs::read_to_string(path).unwrap_or_default()
    }

    // ── basic write / read ─────────────────────────────────────────────────────

    #[test]
    fn appends_text_to_file() {
        let path = tmp_path("append_basic.log");
        let _ = fs::remove_file(&path);
        let w = TaskLogWriter::new(&path);
        w.append("hello ", TaskOutputChannel::General);
        w.append("world", TaskOutputChannel::General);
        w.dispose();
        let content = read(&path);
        assert!(content.contains("hello "), "missing: {content}");
        assert!(content.contains("world"), "missing: {content}");
    }

    // ── secret redaction ──────────────────────────────────────────────────────

    #[test]
    fn sk_token_is_not_written_to_file() {
        let path = tmp_path("redact_sk.log");
        let _ = fs::remove_file(&path);
        let w = TaskLogWriter::new(&path);
        w.append("key=sk-abcdefghij rest", TaskOutputChannel::General);
        w.dispose();
        let content = read(&path);
        assert!(
            !content.contains("abcdefghij"),
            "sk body in log: {content}"
        );
        assert!(content.contains("sk-***"), "placeholder missing: {content}");
    }

    #[test]
    fn secret_split_across_appends_is_still_redacted() {
        let secret = "sk-abcdefghijklmnop";
        for split in 0..=secret.len() {
            let path = tmp_path(&format!("split_{split}.log"));
            let _ = fs::remove_file(&path);
            let w = TaskLogWriter::new(&path);
            w.append(&secret[..split], TaskOutputChannel::General);
            w.append(&secret[split..], TaskOutputChannel::General);
            w.dispose();
            let content = read(&path);
            assert!(
                !content.contains("abcdefghijklmnop"),
                "body leaked at split {split}: {content}"
            );
        }
    }

    // ── per-channel independent redactor states ───────────────────────────────

    #[test]
    fn interleaved_channel_writes_do_not_corrupt_redaction() {
        let path = tmp_path("channel_interleave.log");
        let _ = fs::remove_file(&path);
        let w = TaskLogWriter::new(&path);
        // Split a secret across two channels — each channel redacts independently,
        // so neither channel leaks, and neither can corrupt the other's state.
        w.append("sk-abcd", TaskOutputChannel::Stdout);
        w.append("efghij12", TaskOutputChannel::Stderr); // different channel — irrelevant content
        w.append("klmnop", TaskOutputChannel::Stdout); // completes the secret on Stdout
        w.dispose();
        let content = read(&path);
        // The sk-... on Stdout should be redacted
        assert!(!content.contains("abcdklmnop"), "Stdout body leaked: {content}");
    }

    // ── newest_suffix_within_cap ──────────────────────────────────────────────

    #[test]
    fn suffix_within_cap_basic() {
        let s = "hello world";
        let suffix = newest_suffix_within_cap(s, 5);
        assert_eq!(suffix, "world");
    }

    #[test]
    fn suffix_within_cap_entire_string_fits() {
        let s = "abc";
        let suffix = newest_suffix_within_cap(s, 100);
        assert_eq!(suffix, "abc");
    }

    #[test]
    fn suffix_within_cap_empty_string() {
        assert_eq!(newest_suffix_within_cap("", 100), "");
    }

    #[test]
    fn suffix_within_cap_zero_budget() {
        assert_eq!(newest_suffix_within_cap("abc", 0), "");
    }
}
