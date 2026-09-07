//! The bounded, rotating JSONL writer.
//!
//! Every process owns exactly one [`Logger`]. It never blocks indefinitely,
//! never grows without bound, and degrades instead of panicking or bringing
//! down the feature it observes.

use std::fs::{File, OpenOptions};
use std::io::{ErrorKind, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
use std::time::{Duration, SystemTime};

use fs2::FileExt;
use serde::Serialize;

use crate::context::{DiagnosticContext, ProcessRole, Verbosity};
use crate::event::Event;

/// Where and how a [`Logger`] should write.
pub struct Options {
    /// The default diagnostics directory (`<profile>/.coda/logs/diagnostics`),
    /// resolved by the caller — this crate never resolves `CODA_HOME` itself,
    /// which keeps it a true leaf with no `coda-auth` dependency.
    pub directory: PathBuf,
    /// An explicit destination override (`--log-file`). When present, this
    /// exact path is used instead of a generated name under `directory`, and
    /// a failure to open it is the caller's responsibility to treat as fatal.
    pub file: Option<PathBuf>,
    pub role: ProcessRole,
    pub version: String,
    pub verbosity: Verbosity,
}

/// Bounds enforced by the writer. The defaults match the plan; tests inject
/// smaller values to exercise rotation/retention without writing megabytes.
#[derive(Debug, Clone)]
pub struct Limits {
    pub segment_bytes: u64,
    pub retained_segments: usize,
    pub record_bytes: usize,
    pub retention_age: Duration,
    pub inactive_total_bytes: u64,
}

impl Default for Limits {
    fn default() -> Self {
        Self {
            segment_bytes: 5 * 1024 * 1024,
            retained_segments: 4,
            record_bytes: 8 * 1024,
            retention_age: Duration::from_secs(7 * 24 * 3600),
            inactive_total_bytes: 100 * 1024 * 1024,
        }
    }
}

/// Writer health, surfaced to `/log` and printed once as a warning.
#[derive(Debug, Clone, PartialEq)]
pub enum Health {
    Healthy,
    /// A short, fixed, payload-free reason — never the underlying `io::Error`
    /// text, which can embed a path or OS-specific detail.
    Degraded(&'static str),
}

/// A snapshot of what the writer is actually doing right now, for `/log` and
/// for the frontend's own discoverability of a child engine's state.
#[derive(Debug, Clone, PartialEq)]
pub struct Status {
    pub path: Option<PathBuf>,
    pub mode: &'static str,
    pub verbosity: Verbosity,
    pub health: Health,
}

const FILE_PREFIX: &str = "coda-";
const FILE_SUFFIX: &str = ".jsonl";

/// One process's diagnostic writer.
pub struct Logger {
    role: ProcessRole,
    version: String,
    verbosity: Verbosity,
    mode: &'static str,
    limits: Limits,
    inner: Mutex<Inner>,
    /// Set the first time a runtime write fails, so the payload-free warning
    /// is printed at most once per process.
    warned: AtomicBool,
}

struct Inner {
    /// `None` exactly when the destination could not be prepared —
    /// nonfatal for the default directory; records are silently dropped and
    /// `health` reflects it.
    file: Option<File>,
    /// Held for the writer's lifetime; releases automatically on process
    /// exit/crash because it is tied to the OS file description.
    _lock: Option<File>,
    current_path: Option<PathBuf>,
    /// The un-suffixed base path: the exact explicit path, or the generated
    /// unique default-mode filename. Segment N is `<base>.<N>` for N >= 1.
    base_path: PathBuf,
    bytes_in_segment: u64,
    health: Health,
}

impl Logger {
    /// Opens a logger. Default-directory failures are nonfatal (`Ok` with a
    /// degraded health); an explicit destination failure is returned as
    /// `Err` because the plan requires that to be a hard startup error.
    ///
    /// `limits.retained_segments == 0` is rejected clearly: it describes an
    /// impossible policy (not even the current segment would be kept), so it
    /// is treated the same as any other invalid configuration — a hard
    /// `Err` regardless of destination mode, never a silent fallback.
    pub fn open(options: Options, limits: Limits) -> std::io::Result<Logger> {
        if limits.retained_segments == 0 {
            return Err(std::io::Error::new(
                ErrorKind::InvalidInput,
                "diagnostics Limits::retained_segments must be at least 1",
            ));
        }

        let (inner, mode) = match &options.file {
            Some(explicit) => (open_explicit(explicit)?, "explicit"),
            None => (open_default(&options.directory, &limits), "default"),
        };

        // Surface exactly one payload-free warning if the writer starts out
        // already degraded (e.g. an unavailable default directory), instead
        // of staying silently unprotected: `record`/`write_line` would
        // otherwise never reach the warning path because there is no file to
        // even attempt writing to.
        let warned = AtomicBool::new(false);
        if let Health::Degraded(reason) = &inner.health {
            eprintln!("coda: diagnostics logging is degraded ({reason}); continuing without it");
            warned.store(true, Ordering::SeqCst);
        }

        Ok(Logger {
            role: options.role,
            version: options.version,
            verbosity: options.verbosity,
            mode,
            limits,
            inner: Mutex::new(inner),
            warned,
        })
    }

    pub fn role(&self) -> ProcessRole {
        self.role
    }

    pub fn verbosity(&self) -> Verbosity {
        self.verbosity
    }

    pub fn status(&self) -> Status {
        let inner = self.inner.lock().expect("diagnostics writer lock poisoned");
        Status {
            path: inner.current_path.clone(),
            mode: self.mode,
            verbosity: self.verbosity,
            health: inner.health.clone(),
        }
    }

    /// Records one event under `ctx`'s identity. Bounds are enforced before
    /// any JSON is written; a write failure degrades health rather than
    /// propagating — diagnostics can never fail the feature they observe.
    ///
    /// Events below this logger's configured [`Verbosity`] are dropped
    /// before any serialization/locking happens at all: `--diagnostic-
    /// verbosity` only ever trades off optional detail (gated per-variant by
    /// [`Event::minimum_verbosity`]) — essential lifecycle/failure events
    /// have `Verbosity::Normal` and are therefore always recorded.
    pub fn record(&self, ctx: &DiagnosticContext, event: Event) {
        if event.minimum_verbosity() > self.verbosity {
            return;
        }
        // Rewrite any field sourced from arbitrary provider data (currently
        // `TurnEnd::stop_reason`) down to a closed, safe vocabulary *before*
        // it is ever built into a JSON envelope — see `Event::normalized`.
        let event = event.normalized();

        #[derive(Serialize)]
        struct Envelope<'a> {
            v: u8,
            ts: String,
            version: &'a str,
            role: &'static str,
            pid: u32,
            run_id: &'a str,
            #[serde(skip_serializing_if = "Option::is_none")]
            session_id: Option<&'a str>,
            #[serde(skip_serializing_if = "Option::is_none")]
            turn_id: Option<&'a str>,
            #[serde(skip_serializing_if = "Option::is_none")]
            request_id: Option<&'a str>,
            #[serde(skip_serializing_if = "Option::is_none")]
            provider: Option<&'a str>,
            #[serde(skip_serializing_if = "Option::is_none")]
            model: Option<&'a str>,
            #[serde(flatten)]
            event: &'a Event,
        }

        let envelope = Envelope {
            v: 1,
            ts: chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true),
            version: &self.version,
            role: self.role.as_str(),
            pid: std::process::id(),
            run_id: ctx.run_id(),
            session_id: ctx.session_id(),
            turn_id: ctx.turn_id(),
            request_id: ctx.request_id(),
            provider: ctx.provider(),
            model: ctx.model(),
            event: &event,
        };

        let mut bytes = match serde_json::to_vec(&envelope) {
            Ok(bytes) => bytes,
            Err(_) => return,
        };

        if bytes.len() > self.limits.record_bytes {
            // The fallback marker deliberately drops every optional
            // correlation field: it must fit even when the full envelope
            // (long-lived session/turn/request ids, provider, model) does
            // not, and it must never itself be truncated JSON.
            #[derive(Serialize)]
            struct MinimalEnvelope<'a> {
                v: u8,
                ts: &'a str,
                role: &'static str,
                pid: u32,
                run_id: &'a str,
                #[serde(flatten)]
                event: &'a Event,
            }
            let dropped_event = Event::RecordDropped { attempted_bytes: bytes.len() };
            let minimal = MinimalEnvelope {
                v: envelope.v,
                ts: &envelope.ts,
                role: envelope.role,
                pid: envelope.pid,
                run_id: envelope.run_id,
                event: &dropped_event,
            };
            bytes = match serde_json::to_vec(&minimal) {
                Ok(bytes) => bytes,
                Err(_) => return,
            };
            // The minimal marker must itself always fit; if it somehow does
            // not (it cannot, by construction — no unbounded field remains),
            // this is an explicit configuration failure (e.g. `record_bytes`
            // set unreasonably small), not a value to swallow silently: mark
            // the writer degraded so `/log`/`status()` shows it, rather than
            // quietly dropping records forever with no visible signal.
            if bytes.len() > self.limits.record_bytes {
                let mut inner = self.inner.lock().expect("diagnostics writer lock poisoned");
                inner.mark_degraded("record_bytes too small for any record", &self.warned);
                return;
            }
        }
        bytes.push(b'\n');

        let mut inner = self.inner.lock().expect("diagnostics writer lock poisoned");
        inner.write_line(&bytes, &self.limits, &self.warned);
    }
}

impl Inner {
    fn write_line(&mut self, line: &[u8], limits: &Limits, warned: &AtomicBool) {
        let Some(file) = self.file.as_mut() else {
            return; // Degraded: nothing to write to; health already reflects it.
        };

        // Not even a freshly rotated, empty segment could hold this record
        // (only possible when `segment_bytes` is configured smaller than a
        // record actually needs, e.g. a test-injected tiny limit). Rotating
        // could never help, so don't bother — degrade explicitly instead of
        // silently producing an oversized segment.
        if line.len() as u64 > limits.segment_bytes {
            self.mark_degraded("record exceeds segment_bytes", warned);
            return;
        }

        if self.bytes_in_segment + line.len() as u64 > limits.segment_bytes {
            if rotate(file, &self.base_path, limits.retained_segments).is_err() {
                self.mark_degraded("rotation failed", warned);
                return;
            }
            // Re-open the (now-truncated-to-empty) current segment.
            match private_file_options().create(true).append(true).open(&self.base_path) {
                Ok(new_file) => {
                    *file = new_file;
                    self.bytes_in_segment = 0;
                }
                Err(_) => {
                    self.mark_degraded("rotation failed", warned);
                    return;
                }
            }
            // Rotation freed up space for retention accounting: sweep now,
            // not only at startup, so a long-running process's own rotated
            // segments are subject to the same age/budget policy as anyone
            // else's idle files, rather than only ever being swept once at
            // the very start of the process.
            if let Some(directory) = self.base_path.parent() {
                let _ = sweep_retention(directory, limits, Some(&self.base_path));
            }
        }

        match file.write_all(line).and_then(|_| file.flush()) {
            Ok(()) => {
                self.bytes_in_segment += line.len() as u64;
                self.health = Health::Healthy;
            }
            Err(_) => self.mark_degraded("write failed", warned),
        }
    }

    fn mark_degraded(&mut self, reason: &'static str, warned: &AtomicBool) {
        self.health = Health::Degraded(reason);
        // Payload-free: no path, no `io::Error` text — just a fixed label,
        // printed at most once per process so a run of failures cannot spam
        // stderr and hide the real, more actionable failure it is warning
        // about.
        if !warned.swap(true, Ordering::SeqCst) {
            eprintln!("coda: diagnostics logging is degraded ({reason}); continuing without it");
        }
    }
}

/// Rotates `<base>` into bounded `<base>.1`..`<base>.N` suffixes (classic
/// logrotate shape): drops the oldest, shifts the rest up by one, then
/// truncates `base` to empty for the caller to reopen.
///
/// `retained_segments` counts the *current* segment too, so
/// `retained_segments == 1` keeps only ever the current segment: rotation
/// simply discards the outgoing content rather than renaming it to `.1`.
/// Every remove/rename failure propagates (never swallowed): a caller that
/// silently ignored these could otherwise shift a later segment on top of
/// one whose removal actually failed, silently clobbering it.
fn rotate(current: &mut File, base: &Path, retained_segments: usize) -> std::io::Result<()> {
    // Flush anything buffered before renaming out from under the handle.
    current.flush()?;

    let max_rotated = retained_segments.saturating_sub(1);
    if max_rotated == 0 {
        if base.exists() {
            std::fs::remove_file(base)?;
        }
        return Ok(());
    }

    let oldest = segment_path(base, max_rotated);
    if oldest.exists() {
        std::fs::remove_file(&oldest)?;
    }
    let mut n = max_rotated;
    while n >= 1 {
        if n == max_rotated {
            n -= 1;
            continue;
        }
        let from = segment_path(base, n);
        if from.exists() {
            let to = segment_path(base, n + 1);
            std::fs::rename(&from, &to)?;
        }
        n -= 1;
    }
    if base.exists() {
        std::fs::rename(base, segment_path(base, 1))?;
    }
    Ok(())
}

fn segment_path(base: &Path, n: usize) -> PathBuf {
    PathBuf::from(format!("{}.{n}", base.display()))
}

/// The stable companion lockfile path for a data file, used to lease it
/// without locking (and thereby blocking ordinary reads of) the data itself.
fn lock_path_for(path: &Path) -> PathBuf {
    PathBuf::from(format!("{}.lock", path.display()))
}

fn private_file_options() -> OpenOptions {
    let options = OpenOptions::new();
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        let mut options = options;
        options.mode(0o600);
        options
    }
    #[cfg(not(unix))]
    {
        options
    }
}

fn create_private_directories(path: &Path) -> std::io::Result<()> {
    let mut builder = std::fs::DirBuilder::new();
    builder.recursive(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::DirBuilderExt;
        builder.mode(0o700);
    }
    builder.create(path)
}

/// Opens an explicit destination: append (never truncate), and take an
/// exclusive advisory lock on a stable companion sidecar so a second writer
/// pointed at the same path fails clearly instead of interleaving records.
/// The lock is tied to the sidecar's OS file description, so a crash — which
/// leaves no chance to run a `Drop` — still releases it.
fn open_explicit(path: &Path) -> std::io::Result<Inner> {
    if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
        create_private_directories(parent)?;
    }

    let lock_path = lock_path_for(path);
    let lock_file = private_file_options()
        .create(true)
        .write(true)
        .open(&lock_path)?;
    lock_file.try_lock_exclusive().map_err(|_| {
        std::io::Error::new(
            ErrorKind::WouldBlock,
            format!(
                "another process already has an explicit diagnostic log open at {}",
                path.display()
            ),
        )
    })?;

    let file = private_file_options().create(true).append(true).open(path)?;
    let bytes_in_segment = file.metadata().map(|m| m.len()).unwrap_or(0);

    Ok(Inner {
        file: Some(file),
        _lock: Some(lock_file),
        current_path: Some(path.to_path_buf()),
        base_path: path.to_path_buf(),
        bytes_in_segment,
        health: Health::Healthy,
    })
}

/// Opens a default-directory, per-process unique file. Never fails: an
/// unavailable directory is a degraded, nonfatal state so the app stays
/// usable without a log.
fn open_default(directory: &Path, limits: &Limits) -> Inner {
    match try_open_default(directory) {
        Ok(inner) => {
            let _ = sweep_retention(directory, limits, inner.current_path.as_deref());
            inner
        }
        Err(_) => Inner {
            file: None,
            _lock: None,
            current_path: None,
            base_path: directory.to_path_buf(),
            bytes_in_segment: 0,
            health: Health::Degraded("default log directory unavailable"),
        },
    }
}

fn try_open_default(directory: &Path) -> std::io::Result<Inner> {
    create_private_directories(directory)?;
    try_open_default_named(directory, &unique_file_name())
}

/// The name-parameterized core of [`try_open_default`], split out purely so
/// tests can deterministically pre-hold a lease for a known filename (a real
/// UUID-named collision cannot be arranged from outside this module).
fn try_open_default_named(directory: &Path, name: &str) -> std::io::Result<Inner> {
    let path = directory.join(name);

    // Acquire the lease *before* the data file is ever created/published:
    // locking a companion sidecar first (not the data file itself — locking
    // the data file directly would make it unreadable by ordinary tools
    // under Windows' mandatory locking while the writer is alive) means a
    // retention sweep can never observe this stream's data file without also
    // being able to observe (and be blocked by) its lease. A failed lease
    // acquisition must not silently continue unprotected: it fails the whole
    // open attempt here, which `open_default` turns into the same degraded,
    // nonfatal state as any other default-directory failure.
    let lock_path = lock_path_for(&path);
    let lock_file = private_file_options().create(true).write(true).open(&lock_path)?;
    lock_file.try_lock_exclusive().map_err(|_| {
        std::io::Error::new(
            ErrorKind::WouldBlock,
            "could not acquire the diagnostics lease for a brand-new stream",
        )
    })?;

    // Only now, with the lease held, publish the data file. A fresh
    // UUID-named file must never collide with an existing one.
    let file = private_file_options().create_new(true).write(true).open(&path)?;

    Ok(Inner {
        file: Some(file),
        _lock: Some(lock_file),
        current_path: Some(path.clone()),
        base_path: path,
        bytes_in_segment: 0,
        health: Health::Healthy,
    })
}

fn unique_file_name() -> String {
    let ts = chrono::Utc::now().format("%Y%m%dT%H%M%S%.3fZ");
    let pid = std::process::id();
    let id = uuid::Uuid::new_v4();
    format!("{FILE_PREFIX}{ts}-{pid}-{id}{FILE_SUFFIX}")
}

/// Strictly parses `name` as one of *our own* managed default-directory
/// stream filenames — either the exact base `coda-<utc>-<pid>-<uuid>.jsonl`
/// or one of its rotated `.N` segments — and returns the un-suffixed base
/// filename (i.e. with any `.N` rotation suffix stripped). Every segment of
/// one logical stream therefore maps back to the *same* base name, and thus
/// the same `<base>.lock` lease.
///
/// Returns `None` for anything else, deliberately including an arbitrary
/// user-created `coda-something.jsonl` file that merely happens to share our
/// prefix/suffix: only the exact `<timestamp>-<pid>-<uuid>` shape this writer
/// itself generates in [`unique_file_name`] is accepted.
fn managed_base_name(name: &str) -> Option<&str> {
    let mut candidate = name;
    if let Some(dot) = candidate.rfind('.') {
        let suffix = &candidate[dot + 1..];
        if !suffix.is_empty() && suffix.bytes().all(|b| b.is_ascii_digit()) {
            candidate = &candidate[..dot];
        }
    }
    is_exact_managed_base(candidate).then_some(candidate)
}

/// `true` only for the exact `coda-<utc>-<pid>-<uuid>.jsonl` shape produced
/// by [`unique_file_name`] — not any other `coda-*.jsonl` name.
fn is_exact_managed_base(name: &str) -> bool {
    let Some(rest) = name.strip_prefix(FILE_PREFIX) else { return false };
    let Some(rest) = rest.strip_suffix(FILE_SUFFIX) else { return false };
    // rest = "<timestamp>-<pid>-<uuid>". The timestamp contains no '-'; the
    // uuid's own internal '-'s are preserved by capping the split at 3 parts.
    let mut parts = rest.splitn(3, '-');
    let (Some(ts), Some(pid), Some(id)) = (parts.next(), parts.next(), parts.next()) else {
        return false;
    };
    is_valid_unique_timestamp(ts)
        && !pid.is_empty()
        && pid.bytes().all(|b| b.is_ascii_digit())
        && id.len() == 36
        && uuid::Uuid::parse_str(id).is_ok_and(|parsed| parsed.hyphenated().to_string() == id)
}

/// Validates the exact `%Y%m%dT%H%M%S%.3fZ` shape written by
/// [`unique_file_name`], e.g. `20260907T183245.123Z` (20 bytes:
/// `YYYYMMDD` `T` `HHMMSS` `.` `mmm` `Z`).
fn is_valid_unique_timestamp(s: &str) -> bool {
    let b = s.as_bytes();
    let digits = |range: std::ops::Range<usize>| b[range].iter().all(|c| c.is_ascii_digit());
    b.len() == 20
        && digits(0..8)
        && b[8] == b'T'
        && digits(9..15)
        && b[15] == b'.'
        && digits(16..19)
        && b[19] == b'Z'
}

/// Applies the default-directory retention policy: recognized, inactive
/// (not currently leased) *streams* — grouping every rotated `.N` segment
/// back with its base file — that are older than `retention_age`, or the
/// oldest-first overflow past `inactive_total_bytes`, are removed.
///
/// Symlinks and anything not matching [`managed_base_name`] are never
/// touched — this deliberately excludes arbitrary user-created
/// `coda-*.jsonl` files. `own_path` (this process's own current segment) is
/// always skipped: it is never yet stale, and its lease is already held by
/// this same process, so a redundant probe would only waste time.
fn sweep_retention(
    directory: &Path,
    limits: &Limits,
    own_path: Option<&Path>,
) -> std::io::Result<()> {
    let entries = match std::fs::read_dir(directory) {
        Ok(entries) => entries,
        Err(_) => return Ok(()),
    };

    // Group every recognized segment (base + rotated `.N`s) by its shared
    // base name, so the whole stream is protected/evicted as one unit under
    // one shared lease — never per-segment, which is what let another
    // process delete a live stream's archived segments out from under it.
    let mut groups: std::collections::BTreeMap<String, Vec<(PathBuf, SystemTime, u64)>> =
        std::collections::BTreeMap::new();
    let mut lock_sidecars: Vec<PathBuf> = Vec::new();

    for entry in entries.flatten() {
        let path = entry.path();
        if Some(path.as_path()) == own_path {
            continue;
        }
        let Some(name) = path.file_name().and_then(|n| n.to_str()) else {
            continue;
        };
        if name.ends_with(".lock") {
            lock_sidecars.push(path);
            continue;
        }
        let Some(base) = managed_base_name(name) else {
            continue; // Not one of ours: never touched, including symlinks.
        };
        // Never follow/touch a symlink standing in for a recognized name.
        let Ok(meta) = std::fs::symlink_metadata(&path) else {
            continue;
        };
        if meta.file_type().is_symlink() {
            continue;
        }
        let Ok(modified) = meta.modified() else {
            continue;
        };
        groups.entry(base.to_string()).or_default().push((path, modified, meta.len()));
    }

    let now = SystemTime::now();
    let mut hard_error: Option<std::io::Error> = None;

    // Age-based removal: a whole stream is only considered idle-and-old when
    // its *newest* segment (the one most recently written, i.e. the one that
    // would be its current segment while the process was alive) is older
    // than `retention_age` — an individually-old rotated segment does not by
    // itself condemn a stream that is still actively growing.
    let mut survivors: Vec<(String, Vec<PathBuf>, SystemTime, u64)> = Vec::new();
    for (base, segments) in groups {
        let newest = segments.iter().map(|(_, m, _)| *m).max().unwrap_or(now);
        let total: u64 = segments.iter().map(|(_, _, len)| *len).sum();
        let paths: Vec<PathBuf> = segments.into_iter().map(|(p, _, _)| p).collect();
        let age = now.duration_since(newest).unwrap_or(Duration::ZERO);
        if age >= limits.retention_age {
            match remove_group_if_unleased(directory, &base, &paths) {
                Ok(_removed) => {}
                Err(e) if e.kind() == ErrorKind::NotFound => {}
                Err(e) => hard_error = Some(e),
            }
            // Whether removed or left alone (still leased), it is not a
            // candidate for size-based eviction below in the same pass.
            continue;
        }
        survivors.push((base, paths, newest, total));
    }

    // Oldest-first size eviction until the inactive total is back under
    // budget, again treating each stream (every segment) as one unit.
    survivors.sort_by_key(|(_, _, newest, _)| *newest);
    let mut total: u64 = survivors.iter().map(|(_, _, _, len)| *len).sum();
    for (base, paths, _, len) in survivors {
        if total <= limits.inactive_total_bytes {
            break;
        }
        match remove_group_if_unleased(directory, &base, &paths) {
            Ok(true) => total = total.saturating_sub(len),
            Ok(false) => {}
            Err(e) if e.kind() == ErrorKind::NotFound => total = total.saturating_sub(len),
            Err(e) => hard_error = Some(e),
        }
    }

    // Orphaned lease sidecars: a `.lock` file whose managed base has no
    // remaining data segment at all (every segment already removed, e.g.
    // across several prior sweeps, or a crash mid-cleanup) would otherwise
    // accumulate forever. Only reclaimed when nothing holds it any more.
    for lock_path in lock_sidecars {
        let Some(name) = lock_path.file_name().and_then(|n| n.to_str()) else { continue };
        let Some(base) = name.strip_suffix(".lock") else { continue };
        if !is_exact_managed_base(base) {
            continue; // Not one of our own sidecars: never touched.
        }
        if directory.join(base).exists() {
            continue; // The (only, un-rotated) segment for this base is still present.
        }
        // Any `.N` segment still present for this base also keeps it alive.
        let still_has_segment = std::fs::read_dir(directory)
            .into_iter()
            .flatten()
            .flatten()
            .any(|e| e.file_name().to_str().is_some_and(|n| managed_base_name(n) == Some(base)));
        if still_has_segment {
            continue;
        }
        if let Ok(probe) = OpenOptions::new().write(true).open(&lock_path) {
            if probe.try_lock_exclusive().is_ok() {
                let _ = fs2::FileExt::unlock(&probe);
                drop(probe);
                let _ = std::fs::remove_file(&lock_path);
            }
        }
    }

    match hard_error {
        Some(e) => Err(e),
        None => Ok(()),
    }
}

/// Removes every segment path in `paths` for stream `base`, holding a single
/// exclusive lease on `<directory>/<base>.lock` across the *entire*
/// deletion — there is no unlock-then-unlink gap in which another process
/// could acquire the lease and start writing to a file this sweep is about
/// to delete. Returns `Ok(true)` if anything was actually removed,
/// `Ok(false)` if the stream is still actively leased (left untouched, even
/// if it looks stale by age), and propagates any removal failure that is not
/// a benign "someone else already removed it" race.
fn remove_group_if_unleased(
    directory: &Path,
    base: &str,
    paths: &[PathBuf],
) -> std::io::Result<bool> {
    let lock_path = directory.join(format!("{base}.lock"));
    let probe = private_file_options().create(true).write(true).open(&lock_path)?;
    if probe.try_lock_exclusive().is_err() {
        return Ok(false); // An active stream — protected even if it looks idle.
    }

    let mut first_error: Option<std::io::Error> = None;
    let mut removed_any = false;
    for path in paths {
        match std::fs::remove_file(path) {
            Ok(()) => removed_any = true,
            Err(e) if e.kind() == ErrorKind::NotFound => {} // Benign race: already gone.
            Err(e) if first_error.is_none() => first_error = Some(e),
            Err(_) => {}
        }
    }
    // The shared lease sidecar itself is only ever removed once every
    // segment for this base is confirmed gone (the loop above already
    // requested removal of all of them; verify rather than assume).
    if paths.iter().all(|p| !p.exists()) {
        let _ = fs2::FileExt::unlock(&probe);
        drop(probe);
        let _ = std::fs::remove_file(&lock_path);
    } else {
        let _ = fs2::FileExt::unlock(&probe);
        drop(probe);
    }

    match first_error {
        Some(e) => Err(e),
        None => Ok(removed_any),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::context::DiagnosticContext;
    use std::sync::Arc;

    fn options(dir: &Path) -> Options {
        Options {
            directory: dir.to_path_buf(),
            file: None,
            role: ProcessRole::Run,
            version: "0.0.0-test".into(),
            verbosity: Verbosity::Normal,
        }
    }

    fn read_lines(path: &Path) -> Vec<serde_json::Value> {
        let content = std::fs::read_to_string(path).unwrap();
        content
            .lines()
            .filter(|l| !l.is_empty())
            .map(|l| serde_json::from_str(l).expect("valid JSON per line"))
            .collect()
    }

    /// Holds `path` open in a way that forces its removal to genuinely fail
    /// until the returned guard is dropped. A merely read-only attribute is
    /// not enough on Windows: `std::fs::remove_file` clears it and retries.
    #[cfg(windows)]
    fn block_removal(path: &Path) -> File {
        use std::os::windows::fs::OpenOptionsExt;
        OpenOptions::new()
            .read(true)
            .share_mode(0) // No FILE_SHARE_DELETE/READ/WRITE for anyone else.
            .open(path)
            .expect("can hold an exclusive handle on the fixture file")
    }

    #[cfg(unix)]
    fn block_removal(path: &Path) -> impl Drop {
        use std::os::unix::fs::PermissionsExt;

        struct Guard(PathBuf, std::fs::Permissions);
        impl Drop for Guard {
            fn drop(&mut self) {
                let _ = std::fs::set_permissions(&self.0, self.1.clone());
            }
        }

        let parent = path.parent().unwrap().to_path_buf();
        let original = std::fs::metadata(&parent).unwrap().permissions();
        let mut restricted = original.clone();
        restricted.set_mode(0o555); // Read + execute, no write: unlink needs write on the dir.
        std::fs::set_permissions(&parent, restricted).unwrap();
        Guard(parent, original)
    }

    #[test]
    #[cfg(unix)]
    fn newly_created_diagnostics_paths_are_private_including_rotations() {
        use std::os::unix::fs::PermissionsExt;
        for explicit in [false, true] {
            let dir = tempfile::tempdir().unwrap();
            let private_dir = dir.path().join("new-diagnostics");
            let mut opts = options(&private_dir);
            if explicit { opts.file = Some(private_dir.join("explicit.jsonl")); }
            let limits = Limits { segment_bytes: 512, ..Limits::default() };
            let logger = Arc::new(Logger::open(opts, limits).unwrap());
            let ctx = DiagnosticContext::root(logger, "permission-test");
            for _ in 0..10 { ctx.record(Event::TurnStart); }
            assert_eq!(std::fs::metadata(&private_dir).unwrap().permissions().mode() & 0o077, 0);
            for entry in std::fs::read_dir(&private_dir).unwrap() {
                assert_eq!(entry.unwrap().metadata().unwrap().permissions().mode() & 0o077, 0);
            }
        }
    }

    #[test]
    fn default_mode_creates_a_file_automatically() {
        let dir = tempfile::tempdir().unwrap();
        let logger = Logger::open(options(dir.path()), Limits::default()).expect("opens");
        let status = logger.status();
        assert_eq!(status.mode, "default");
        assert_eq!(status.health, Health::Healthy);
        let path = status.path.expect("a path was created");
        assert!(path.exists());
        assert!(path.starts_with(dir.path()));
    }

    #[test]
    fn every_record_is_parseable_json_on_its_own_line() {
        let dir = tempfile::tempdir().unwrap();
        let logger = Arc::new(Logger::open(options(dir.path()), Limits::default()).expect("opens"));
        let ctx = DiagnosticContext::root(Arc::clone(&logger), "run-1").with_session("sess-1");
        ctx.record(Event::SessionInitialized);
        ctx.with_turn("turn-1").record(Event::TurnStart);

        let path = logger.status().path.unwrap();
        let lines = read_lines(&path);
        assert_eq!(lines.len(), 2);
        assert_eq!(lines[0]["kind"], "session_initialized");
        assert_eq!(lines[0]["session_id"], "sess-1");
        assert_eq!(lines[1]["kind"], "turn_start");
        assert_eq!(lines[1]["turn_id"], "turn-1");
    }

    #[test]
    fn context_identity_is_stable_across_records() {
        let dir = tempfile::tempdir().unwrap();
        let logger = Arc::new(Logger::open(options(dir.path()), Limits::default()).expect("opens"));
        let ctx = DiagnosticContext::root(Arc::clone(&logger), "run-xyz");
        ctx.record(Event::ProcessStart);

        let path = logger.status().path.unwrap();
        let lines = read_lines(&path);
        assert_eq!(lines[0]["run_id"], "run-xyz");
        assert_eq!(lines[0]["pid"], std::process::id());
        assert_eq!(lines[0]["v"], 1);
        assert!(lines[0]["ts"].as_str().unwrap().contains('T'));
    }

    #[test]
    fn a_malicious_stop_reason_is_normalized_away_before_it_ever_reaches_the_log() {
        let dir = tempfile::tempdir().unwrap();
        let logger = Arc::new(Logger::open(options(dir.path()), Limits::default()).expect("opens"));
        let ctx = DiagnosticContext::root(Arc::clone(&logger), "run-1");

        // A fake stop reason smuggling a real-looking credential/URL — this
        // must never survive to disk verbatim, only a fixed "unknown" may.
        let malicious = "https://user:sk-live-secret9999@internal.example.com/leak";
        ctx.record(Event::TurnEnd { stop_reason: Some(malicious.into()) });

        let path = logger.status().path.unwrap();
        let content = std::fs::read_to_string(&path).unwrap();
        assert!(
            !content.contains("sk-live-secret9999") && !content.contains("internal.example.com"),
            "a malicious/unrecognized stop_reason must never reach the log verbatim: {content}"
        );

        let lines = read_lines(&path);
        assert_eq!(lines[0]["kind"], "turn_end");
        assert_eq!(lines[0]["stop_reason"], "unknown");
    }

    #[test]
    fn a_known_stop_reason_is_recorded_unchanged() {
        let dir = tempfile::tempdir().unwrap();
        let logger = Arc::new(Logger::open(options(dir.path()), Limits::default()).expect("opens"));
        let ctx = DiagnosticContext::root(Arc::clone(&logger), "run-1");
        ctx.record(Event::TurnEnd { stop_reason: Some("end_turn".into()) });

        let path = logger.status().path.unwrap();
        let lines = read_lines(&path);
        assert_eq!(lines[0]["stop_reason"], "end_turn");
    }

    #[test]
    fn a_record_over_the_byte_bound_is_replaced_with_a_safe_dropped_marker() {
        let dir = tempfile::tempdir().unwrap();
        // Small enough that a record carrying full context (session/turn/
        // request ids) overflows it, but large enough that the minimal
        // dropped-record marker still fits comfortably.
        let limits = Limits { record_bytes: 180, ..Limits::default() };
        let logger = Arc::new(Logger::open(options(dir.path()), limits).expect("opens"));
        let ctx = DiagnosticContext::root(Arc::clone(&logger), "run-1")
            .with_session("11111111-1111-1111-1111-111111111111")
            .with_turn("22222222-2222-2222-2222-222222222222")
            .with_request("33333333-3333-3333-3333-333333333333")
            .with_provider_model(Some("anthropic"), Some("claude-opus-5-example"));
        ctx.record(Event::TurnFailed { category: "transport", status: None });

        let path = logger.status().path.unwrap();
        let lines = read_lines(&path);
        assert_eq!(lines.len(), 1);
        assert_eq!(lines[0]["kind"], "record_dropped");
        // The dropped-record marker itself must never be truncated JSON.
        assert!(lines[0]["attempted_bytes"].as_u64().unwrap() > 0);
        // The marker drops correlation fields entirely rather than truncating them.
        assert!(lines[0].get("session_id").is_none());
    }

    #[test]
    fn segments_rotate_before_exceeding_the_configured_bound() {
        let dir = tempfile::tempdir().unwrap();
        let limits = Limits { segment_bytes: 200, retained_segments: 4, ..Limits::default() };
        let logger = Arc::new(Logger::open(options(dir.path()), limits).expect("opens"));
        let ctx = DiagnosticContext::root(Arc::clone(&logger), "run-1");
        for _ in 0..40 {
            ctx.record(Event::TurnStart);
        }

        let base = logger.status().path.unwrap();
        assert!(base.exists(), "the current segment always exists");

        let stem = base.file_name().unwrap().to_string_lossy().into_owned();
        let dir_entries: Vec<PathBuf> = std::fs::read_dir(dir.path())
            .unwrap()
            .flatten()
            .map(|e| e.path())
            .filter(|p| {
                p.file_name()
                    .and_then(|n| n.to_str())
                    .is_some_and(|n| n.starts_with(&stem) && !n.ends_with(".lock"))
            })
            .collect();

        assert!(!dir_entries.is_empty());
        assert!(
            dir_entries.len() <= 4,
            "expected at most 4 segments (retained_segments), found {}",
            dir_entries.len()
        );
        for path in &dir_entries {
            let meta = std::fs::metadata(path).unwrap();
            assert!(
                meta.len() <= 200,
                "segment {path:?} grew past the real configured bound: {} bytes",
                meta.len()
            );
            for line in std::fs::read_to_string(path).unwrap().lines() {
                if line.is_empty() {
                    continue;
                }
                let value: serde_json::Value = serde_json::from_str(line)
                    .unwrap_or_else(|e| panic!("segment {path:?} has a non-JSON line: {e}"));
                assert_eq!(value["kind"], "turn_start");
            }
        }
    }

    #[test]
    fn retained_segments_of_one_never_produces_a_rotated_file() {
        let dir = tempfile::tempdir().unwrap();
        let limits = Limits { segment_bytes: 150, retained_segments: 1, ..Limits::default() };
        let logger = Arc::new(Logger::open(options(dir.path()), limits).expect("opens"));
        let ctx = DiagnosticContext::root(Arc::clone(&logger), "run-1");
        for _ in 0..20 {
            ctx.record(Event::TurnStart);
        }

        let base = logger.status().path.unwrap();
        assert!(base.exists(), "the current segment always exists");
        let stem = base.file_name().unwrap().to_string_lossy().into_owned();
        let related: Vec<String> = std::fs::read_dir(dir.path())
            .unwrap()
            .flatten()
            .map(|e| e.file_name().to_string_lossy().into_owned())
            .filter(|n| n.starts_with(&stem) && !n.ends_with(".lock"))
            .collect();
        assert_eq!(
            related,
            vec![stem],
            "retained_segments=1 must keep only the current segment, never a `.1`"
        );
    }

    #[test]
    fn retained_segments_of_zero_is_rejected_clearly() {
        let dir = tempfile::tempdir().unwrap();
        let limits = Limits { retained_segments: 0, ..Limits::default() };
        let result = Logger::open(options(dir.path()), limits);
        let Err(err) = result else { panic!("retained_segments=0 must be rejected") };
        assert_eq!(err.kind(), ErrorKind::InvalidInput);
    }

    #[test]
    fn a_record_too_large_for_the_segment_bound_degrades_explicitly_without_a_partial_write() {
        let dir = tempfile::tempdir().unwrap();
        // `record_bytes` is generous (the full envelope is not itself
        // treated as oversized), but `segment_bytes` alone is far too small
        // for even the smallest real record.
        let limits = Limits { segment_bytes: 10, record_bytes: 8 * 1024, ..Limits::default() };
        let logger = Arc::new(Logger::open(options(dir.path()), limits).expect("opens"));
        let ctx = DiagnosticContext::root(Arc::clone(&logger), "run-1");
        ctx.record(Event::TurnStart);

        let status = logger.status();
        assert!(matches!(status.health, Health::Degraded(_)));
        let content = std::fs::read_to_string(status.path.unwrap()).unwrap();
        assert!(content.is_empty(), "no oversized/partial line is ever written: {content:?}");
    }

    #[test]
    fn when_even_the_minimal_dropped_marker_cannot_fit_the_writer_degrades_explicitly() {
        let dir = tempfile::tempdir().unwrap();
        let limits = Limits { record_bytes: 10, ..Limits::default() }; // Impossibly small.
        let logger = Arc::new(Logger::open(options(dir.path()), limits).expect("opens"));
        let ctx = DiagnosticContext::root(Arc::clone(&logger), "run-1");
        ctx.record(Event::TurnStart);

        let status = logger.status();
        assert!(matches!(status.health, Health::Degraded(_)));
        let content = std::fs::read_to_string(status.path.unwrap()).unwrap();
        assert!(content.is_empty(), "no line is ever written when not even the minimal marker fits");
    }

    #[test]
    fn a_rotation_removal_failure_surfaces_as_unhealthy_instead_of_silently_overwriting() {
        let dir = tempfile::tempdir().unwrap();
        let limits = Limits { segment_bytes: 300, retained_segments: 2, ..Limits::default() };
        let logger = Arc::new(Logger::open(options(dir.path()), limits).expect("opens"));
        let ctx = DiagnosticContext::root(Arc::clone(&logger), "run-1");

        let base = logger.status().path.unwrap();
        let oldest = PathBuf::from(format!("{}.1", base.display()));
        // Pre-create the oldest rotated slot and hold an exclusive handle on
        // it (Windows' `remove_file` silently clears a mere read-only
        // attribute and retries, so that alone would not force a failure)
        // so its removal during the next rotation genuinely fails instead of
        // silently succeeding (and the subsequent rename silently
        // clobbering it).
        std::fs::write(&oldest, b"{\"kind\":\"pre_existing\"}\n").unwrap();
        let guard = block_removal(&oldest);

        for _ in 0..10 {
            ctx.record(Event::TurnStart);
        }

        assert!(
            matches!(logger.status().health, Health::Degraded("rotation failed")),
            "a propagated remove/rename failure must show up as unhealthy: {:?}",
            logger.status().health
        );

        drop(guard);
        let content = std::fs::read_to_string(&oldest).unwrap();
        assert!(
            content.contains("pre_existing"),
            "content must never be silently clobbered by a rename that proceeded despite a failed removal"
        );
    }

    #[test]
    fn failed_lease_acquisition_leaves_no_unprotected_data_file_behind() {
        let dir = tempfile::tempdir().unwrap();
        let name = format!("{FILE_PREFIX}20260101T000000.000Z-1-{}{FILE_SUFFIX}", uuid::Uuid::new_v4());
        let path = dir.path().join(&name);
        let lock_path = PathBuf::from(format!("{}.lock", path.display()));
        // Pre-hold the exact lease this (otherwise brand-new) stream would need.
        let holder = OpenOptions::new().create(true).write(true).open(&lock_path).unwrap();
        holder.try_lock_exclusive().expect("test can lock its own fixture");

        let result = try_open_default_named(dir.path(), &name);
        assert!(result.is_err(), "a held lease must fail the open attempt, not be silently ignored");
        assert!(!path.exists(), "the data file must never be published without its lease");

        drop(holder);
    }

    #[test]
    fn managed_base_name_accepts_only_the_exact_generated_shape() {
        let good = format!("{FILE_PREFIX}20260907T183245.123Z-4242-{}{FILE_SUFFIX}", uuid::Uuid::new_v4());
        assert_eq!(managed_base_name(&good), Some(good.as_str()));
        assert_eq!(managed_base_name(&format!("{good}.1")), Some(good.as_str()));
        assert_eq!(managed_base_name(&format!("{good}.42")), Some(good.as_str()));

        assert!(managed_base_name("coda-my-notes.jsonl").is_none());
        assert!(managed_base_name("coda-.jsonl").is_none());
        assert!(managed_base_name("random.jsonl").is_none());
        assert!(managed_base_name(&good.replace(".jsonl", ".txt")).is_none());
    }

    #[test]
    fn retention_never_touches_an_arbitrary_user_created_coda_prefixed_file() {
        let dir = tempfile::tempdir().unwrap();
        let user_file = dir.path().join(format!("{FILE_PREFIX}my-notes{FILE_SUFFIX}"));
        std::fs::write(&user_file, b"not ours, just a coincidental name").unwrap();
        set_mtime_days_ago(&user_file, 30);

        let logger = Logger::open(options(dir.path()), Limits::default()).expect("opens");
        drop(logger);

        assert!(
            user_file.exists(),
            "an arbitrary coda-*.jsonl file must never be touched, even if loosely name-like"
        );
    }

    #[test]
    fn retention_protects_every_segment_of_an_active_stream_via_the_shared_lease() {
        let dir = tempfile::tempdir().unwrap();
        let base_name =
            format!("{FILE_PREFIX}20260101T000000.000Z-4242-{}{FILE_SUFFIX}", uuid::Uuid::new_v4());
        let base = dir.path().join(&base_name);
        let seg1 = PathBuf::from(format!("{}.1", base.display()));
        let seg2 = PathBuf::from(format!("{}.2", base.display()));
        for p in [&base, &seg1, &seg2] {
            std::fs::write(p, b"{}\n").unwrap();
            set_mtime_days_ago(p, 30);
        }
        let lock_path = dir.path().join(format!("{base_name}.lock"));
        let holder = OpenOptions::new().create(true).write(true).open(&lock_path).unwrap();
        holder.try_lock_exclusive().expect("test can lock its own fixture");

        let logger = Logger::open(options(dir.path()), Limits::default()).expect("opens");
        drop(logger);

        assert!(base.exists(), "the base segment of a still-leased stream must survive");
        assert!(seg1.exists(), "every rotated segment shares the base lease and must survive");
        assert!(seg2.exists(), "every rotated segment shares the base lease and must survive");

        drop(holder);
    }

    #[test]
    fn retention_removes_every_segment_and_the_shared_lease_together_once_inactive() {
        let dir = tempfile::tempdir().unwrap();
        let base_name =
            format!("{FILE_PREFIX}20260101T000000.000Z-4242-{}{FILE_SUFFIX}", uuid::Uuid::new_v4());
        let base = dir.path().join(&base_name);
        let seg1 = PathBuf::from(format!("{}.1", base.display()));
        for p in [&base, &seg1] {
            std::fs::write(p, b"{}\n").unwrap();
            set_mtime_days_ago(p, 30);
        }
        let lock_path = dir.path().join(format!("{base_name}.lock"));
        // A stale lease with nobody holding it any more.
        std::fs::write(&lock_path, b"").unwrap();

        let logger = Logger::open(options(dir.path()), Limits::default()).expect("opens");
        drop(logger);

        assert!(!base.exists(), "an inactive stream's base segment must be removed");
        assert!(!seg1.exists(), "an inactive stream's rotated segment must be removed");
        assert!(
            !lock_path.exists(),
            "the shared lease sidecar must not leak once every segment is gone"
        );
    }

    #[test]
    fn removing_an_already_gone_segment_is_a_benign_race_not_a_hard_error() {
        let dir = tempfile::tempdir().unwrap();
        let missing = dir.path().join("coda-not-there.jsonl");
        let result = remove_group_if_unleased(dir.path(), "coda-not-there.jsonl", &[missing]);
        assert!(
            result.is_ok(),
            "a NotFound race removing an already-gone segment must not be a hard failure"
        );
    }

    #[test]
    fn a_genuine_removal_failure_is_not_silently_swallowed() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("coda-blocked.jsonl");
        std::fs::write(&path, b"x").unwrap();
        let guard = block_removal(&path);

        let result = remove_group_if_unleased(dir.path(), "coda-blocked", &[path.clone()]);
        assert!(
            result.is_err(),
            "a genuine removal failure (e.g. sharing violation) must be surfaced, not swallowed"
        );

        drop(guard);
    }

    #[test]
    fn an_unavailable_default_directory_warns_exactly_once() {
        let dir = tempfile::tempdir().unwrap();
        let blocking_file = dir.path().join("blocked");
        std::fs::write(&blocking_file, b"x").unwrap();
        let bogus_dir = blocking_file.join("diagnostics");

        let opts = Options {
            directory: bogus_dir,
            file: None,
            role: ProcessRole::Run,
            version: "test".into(),
            verbosity: Verbosity::Normal,
        };
        let logger = Logger::open(opts, Limits::default()).expect("default mode never hard-fails");
        assert!(
            logger.warned.load(Ordering::SeqCst),
            "an initial default-open failure must surface its one-time warning immediately, \
             not only after `write_line` finds there is nothing to write to"
        );
    }

    #[test]
    fn explicit_destination_appends_rather_than_truncating() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("explicit.jsonl");
        std::fs::write(&path, b"{\"kind\":\"pre_existing\"}\n").unwrap();

        let opts = Options {
            directory: dir.path().to_path_buf(),
            file: Some(path.clone()),
            role: ProcessRole::Serve,
            version: "test".into(),
            verbosity: Verbosity::Normal,
        };
        let logger = Arc::new(Logger::open(opts, Limits::default()).expect("opens"));
        assert_eq!(logger.status().mode, "explicit");
        let ctx = DiagnosticContext::root(Arc::clone(&logger), "run-1");
        ctx.record(Event::TurnStart);

        let lines = read_lines(&path);
        assert_eq!(lines.len(), 2, "pre-existing content is preserved, not truncated");
        assert_eq!(lines[0]["kind"], "pre_existing");
        assert_eq!(lines[1]["kind"], "turn_start");
    }

    #[test]
    fn a_second_explicit_writer_on_the_same_path_fails_clearly() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("shared.jsonl");
        let opts = || Options {
            directory: dir.path().to_path_buf(),
            file: Some(path.clone()),
            role: ProcessRole::Serve,
            version: "test".into(),
            verbosity: Verbosity::Normal,
        };
        let _first = Logger::open(opts(), Limits::default()).expect("first writer opens");
        let second = Logger::open(opts(), Limits::default());
        assert!(second.is_err(), "a second writer on the same explicit path must fail");
    }

    #[test]
    fn an_invalid_explicit_destination_is_a_hard_error() {
        // A directory that cannot exist as a regular file's parent: point the
        // "file" at a path whose parent is itself an existing plain file.
        let dir = tempfile::tempdir().unwrap();
        let blocking_file = dir.path().join("not-a-directory");
        std::fs::write(&blocking_file, b"x").unwrap();
        let bogus = blocking_file.join("nested.jsonl");

        let opts = Options {
            directory: dir.path().to_path_buf(),
            file: Some(bogus),
            role: ProcessRole::Serve,
            version: "test".into(),
            verbosity: Verbosity::Normal,
        };
        assert!(Logger::open(opts, Limits::default()).is_err());
    }

    #[test]
    fn an_unavailable_default_directory_degrades_instead_of_failing() {
        let dir = tempfile::tempdir().unwrap();
        let blocking_file = dir.path().join("blocked");
        std::fs::write(&blocking_file, b"x").unwrap();
        // Ask for a default directory nested under a plain file: create_dir_all fails.
        let bogus_dir = blocking_file.join("diagnostics");

        let opts = Options {
            directory: bogus_dir,
            file: None,
            role: ProcessRole::Run,
            version: "test".into(),
            verbosity: Verbosity::Normal,
        };
        let logger = Logger::open(opts, Limits::default()).expect("default mode never hard-fails");
        let status = logger.status();
        assert!(matches!(status.health, Health::Degraded(_)));
        assert!(status.path.is_none());

        // Recording against a degraded logger must not panic.
        let ctx = DiagnosticContext::root(Arc::new(logger), "run-1");
        ctx.record(Event::ProcessStart);
    }

    #[test]
    fn retention_removes_only_recognized_inactive_files_older_than_the_bound() {
        let dir = tempfile::tempdir().unwrap();

        // An old, recognized, unlocked file: eligible for removal.
        let old_recognized_name =
            format!("{FILE_PREFIX}20260101T000000.000Z-1111-{}{FILE_SUFFIX}", uuid::Uuid::new_v4());
        let old_recognized = dir.path().join(&old_recognized_name);
        std::fs::write(&old_recognized, b"{}\n").unwrap();
        set_mtime_days_ago(&old_recognized, 30);

        // An old file that does not match our naming convention: must survive.
        let old_unrelated = dir.path().join("unrelated-old-file.txt");
        std::fs::write(&old_unrelated, b"not ours").unwrap();
        set_mtime_days_ago(&old_unrelated, 30);

        // An old file that merely shares our prefix/suffix but not our exact
        // `<utc>-<pid>-<uuid>` shape: also an arbitrary user file, must survive.
        let old_loosely_named = dir.path().join(format!("{FILE_PREFIX}old-file{FILE_SUFFIX}"));
        std::fs::write(&old_loosely_named, b"not ours either").unwrap();
        set_mtime_days_ago(&old_loosely_named, 30);

        // An old, recognized file that is still actively locked (a long-idle
        // live process): must survive even though it looks stale by mtime.
        let old_locked_name =
            format!("{FILE_PREFIX}20260101T000000.000Z-2222-{}{FILE_SUFFIX}", uuid::Uuid::new_v4());
        let old_locked = dir.path().join(&old_locked_name);
        std::fs::write(&old_locked, b"{}\n").unwrap();
        set_mtime_days_ago(&old_locked, 30);
        let lock_sidecar = dir.path().join(format!("{old_locked_name}.lock"));
        let holder = OpenOptions::new().create(true).write(true).open(&lock_sidecar).unwrap();
        holder.try_lock_exclusive().expect("test can lock its own fixture");

        let limits = Limits { retention_age: Duration::from_secs(7 * 24 * 3600), ..Limits::default() };

        let opts = options(dir.path());
        let logger = Logger::open(opts, limits).expect("opens");
        drop(logger); // ensure sweep ran during open, before assertions

        assert!(!old_recognized.exists(), "old recognized inactive file should be removed");
        assert!(old_unrelated.exists(), "unrelated file must never be touched");
        assert!(old_loosely_named.exists(), "a loosely-named coda-*.jsonl user file must never be touched");
        assert!(old_locked.exists(), "an actively-locked file must be protected");

        drop(holder);
    }

    fn set_mtime_days_ago(path: &Path, days: u64) {
        let past = SystemTime::now() - Duration::from_secs(days * 24 * 3600);
        if let Ok(file) = OpenOptions::new().write(true).open(path) {
            let times = std::fs::FileTimes::new().set_modified(past).set_accessed(past);
            let _ = file.set_times(times);
        }
    }
}
