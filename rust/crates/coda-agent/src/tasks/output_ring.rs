//! Bounded, thread-safe, drop-oldest text buffer with absolute char cursors.
//!
//! Adjacent tiny appends are coalesced into bounded-size chunks so a stream of
//! 1-byte appends produces only O(capacity / chunk_target) nodes rather than
//! one node per byte. Chunks are evicted from the front once total byte length
//! exceeds the cap. A single append larger than the cap is trimmed to the newest
//! valid UTF-8 suffix that fits.
//!
//! Absolute UTF-8-char cursor semantics: `total_chars` counts every character
//! ever appended (including trimmed/evicted), and `dropped_chars` counts
//! characters removed from the front. All chunk boundaries fall on Unicode
//! code-point boundaries.

use std::collections::VecDeque;
use std::sync::Mutex;

/// 1 MiB default — matches the C# `DefaultMaxBytes`.
pub const DEFAULT_MAX_BYTES: u64 = 1 << 20;

/// Upper bound on the payload coalesced into a single chunk (16 KiB).
const MAX_CHUNK_TARGET: usize = 16 * 1024;

struct Chunk {
    text: String,
    byte_len: usize,
    /// Absolute char offset of the first character in this chunk.
    start_char: u64,
}

impl Chunk {
    fn char_len(&self) -> u64 {
        self.text.chars().count() as u64
    }
}

struct RingState {
    chunks: VecDeque<Chunk>,
    byte_len: u64,
    /// Total chars ever appended, including dropped/trimmed.
    total_chars: u64,
    /// Chars removed from the front so far.
    dropped_chars: u64,
}

pub struct OutputRing {
    state: Mutex<RingState>,
    max_bytes: u64,
    /// Target chunk payload size for coalescing (≤ `MAX_CHUNK_TARGET`).
    chunk_target: usize,
}

impl OutputRing {
    pub fn new(max_bytes: u64) -> Self {
        assert!(max_bytes > 0, "max_bytes must be positive");
        let chunk_target = ((max_bytes / 16) as usize).clamp(1, MAX_CHUNK_TARGET);
        Self {
            state: Mutex::new(RingState {
                chunks: VecDeque::new(),
                byte_len: 0,
                total_chars: 0,
                dropped_chars: 0,
            }),
            max_bytes,
            chunk_target,
        }
    }

    /// Total characters ever appended (including dropped/trimmed).
    pub fn total_chars(&self) -> u64 {
        self.state.lock().unwrap().total_chars
    }

    /// Characters removed from the front so far.
    pub fn dropped_chars(&self) -> u64 {
        self.state.lock().unwrap().dropped_chars
    }

    /// Number of internal chunk nodes (for bounding tests).
    #[cfg(test)]
    pub fn chunk_count(&self) -> usize {
        self.state.lock().unwrap().chunks.len()
    }

    /// Append text, evicting old chunks as needed to stay within the byte cap.
    pub fn append(&self, text: &str) {
        if text.is_empty() {
            return;
        }
        let bytes = text.len(); // UTF-8 byte count
        let mut s = self.state.lock().unwrap();
        let start_char = s.total_chars;
        s.total_chars += text.chars().count() as u64;

        // Coalesce into the still-growing tail chunk when it is under the target.
        if let Some(last) = s.chunks.back_mut() {
            if last.byte_len < self.chunk_target {
                last.text.push_str(text);
                last.byte_len += bytes;
                s.byte_len += bytes as u64;
                evict_while_over_cap(&mut s, self.max_bytes);
                trim_newest_if_oversized(&mut s, self.max_bytes);
                return;
            }
        }

        s.chunks.push_back(Chunk {
            text: text.to_owned(),
            byte_len: bytes,
            start_char,
        });
        s.byte_len += bytes as u64;
        evict_while_over_cap(&mut s, self.max_bytes);
        trim_newest_if_oversized(&mut s, self.max_bytes);
    }

    /// Read all buffered text at or after the absolute character offset `cursor`.
    ///
    /// Returns `(text, next_cursor, truncated)` where:
    /// - `text` is the concatenated text from `cursor` onward
    /// - `next_cursor` is the cursor to pass on the next call
    /// - `truncated` is true when data before `cursor` was already evicted
    pub fn read_from(&self, cursor: u64) -> (String, u64, bool) {
        let s = self.state.lock().unwrap();
        read_from_locked(&s, cursor)
    }

    /// Returns the last `max_chars` characters currently buffered.
    pub fn peek(&self, max_chars: usize) -> String {
        if max_chars == 0 {
            return String::new();
        }
        let s = self.state.lock().unwrap();
        let start = s.dropped_chars.max(s.total_chars.saturating_sub(max_chars as u64));
        let (text, _, _) = read_from_locked(&s, start);

        // Trim from the front if we ended up with more than max_chars (the start
        // calculation is char-approximate; read_from_locked returns by byte slice).
        let count = text.chars().count();
        if count > max_chars {
            let skip = count - max_chars;
            let byte_offset = text
                .char_indices()
                .nth(skip)
                .map(|(i, _)| i)
                .unwrap_or(text.len());
            text[byte_offset..].to_owned()
        } else {
            text
        }
    }
}

// ── Internal helpers (operate on the locked state) ───────────────────────────

fn read_from_locked(s: &RingState, cursor: u64) -> (String, u64, bool) {
    let truncated = cursor < s.dropped_chars;
    let from = cursor.max(s.dropped_chars);
    if from >= s.total_chars {
        return (String::new(), s.total_chars, truncated);
    }

    let mut result = String::new();
    for chunk in &s.chunks {
        let chunk_end = chunk.start_char + chunk.char_len();
        if chunk_end <= from {
            continue;
        }
        let local_start_chars = (from.saturating_sub(chunk.start_char)) as usize;
        let byte_start = chunk
            .text
            .char_indices()
            .nth(local_start_chars)
            .map(|(i, _)| i)
            .unwrap_or(chunk.text.len());
        result.push_str(&chunk.text[byte_start..]);
    }
    (result, s.total_chars, truncated)
}

/// Evict whole chunks from the front until byte_len ≤ max_bytes.
/// Always keeps at least the last chunk so an oversized sole chunk can be
/// trimmed by `trim_newest_if_oversized` instead.
fn evict_while_over_cap(s: &mut RingState, max_bytes: u64) {
    while s.byte_len > max_bytes && s.chunks.len() > 1 {
        let first = s.chunks.pop_front().unwrap();
        s.byte_len -= first.byte_len as u64;
        s.dropped_chars = first.start_char + first.char_len();
    }
}

/// When the sole remaining chunk still exceeds the cap (single oversized append),
/// trim its prefix to retain only the newest UTF-8-valid suffix that fits.
fn trim_newest_if_oversized(s: &mut RingState, max_bytes: u64) {
    if s.chunks.len() != 1 {
        return;
    }
    let only = s.chunks.back_mut().unwrap();
    if only.byte_len as u64 <= max_bytes {
        return;
    }

    let start_byte = suffix_start_fitting(&only.text, max_bytes as usize);
    if start_byte == 0 {
        return;
    }

    let dropped_chars = only.text[..start_byte].chars().count() as u64;
    let retained_bytes = only.text.len() - start_byte;
    let retained = only.text[start_byte..].to_owned();
    s.byte_len -= (only.byte_len - retained_bytes) as u64;
    only.text = retained;
    only.byte_len = retained_bytes;
    only.start_char += dropped_chars;
    s.dropped_chars = only.start_char;
}

/// Returns the byte index of the newest suffix of `s` whose UTF-8 encoding fits
/// in `max_bytes`, cut on a code-point boundary. Returns 0 if even the last
/// code point alone exceeds the cap (retain the whole string — mirrors C#).
fn suffix_start_fitting(s: &str, max_bytes: usize) -> usize {
    let mut bytes: usize = 0;
    let mut result = s.len(); // default: trim everything (start at end)

    // Walk code points from the back, accumulating byte counts.
    for (byte_idx, ch) in s.char_indices().rev() {
        let cp_bytes = ch.len_utf8();
        if bytes + cp_bytes > max_bytes {
            // This code point would push us over.
            if result == s.len() {
                // The very last rune alone exceeds the cap — retain it anyway.
                result = byte_idx;
            }
            break;
        }
        bytes += cp_bytes;
        result = byte_idx;
    }

    result
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    fn ring(max_bytes: u64) -> OutputRing {
        OutputRing::new(max_bytes)
    }

    // ── basic ─────────────────────────────────────────────────────────────────

    #[test]
    fn fresh_ring_is_empty() {
        let r = ring(1024);
        assert_eq!(r.total_chars(), 0);
        assert_eq!(r.dropped_chars(), 0);
        let (text, next, trunc) = r.read_from(0);
        assert!(text.is_empty());
        assert_eq!(next, 0);
        assert!(!trunc);
    }

    #[test]
    fn append_increments_total_chars() {
        let r = ring(1024);
        r.append("hello");
        assert_eq!(r.total_chars(), 5);
        r.append(" world");
        assert_eq!(r.total_chars(), 11);
    }

    #[test]
    fn read_from_zero_returns_all_text() {
        let r = ring(1024);
        r.append("hello world");
        let (text, next, trunc) = r.read_from(0);
        assert_eq!(text, "hello world");
        assert_eq!(next, 11);
        assert!(!trunc);
    }

    #[test]
    fn read_from_mid_cursor_returns_tail() {
        let r = ring(1024);
        r.append("abcdef");
        let (text, next, trunc) = r.read_from(3);
        assert_eq!(text, "def");
        assert_eq!(next, 6);
        assert!(!trunc);
    }

    #[test]
    fn read_from_past_end_returns_empty() {
        let r = ring(1024);
        r.append("abc");
        let (text, next, trunc) = r.read_from(100);
        assert!(text.is_empty());
        assert_eq!(next, 3);
        assert!(!trunc);
    }

    #[test]
    fn peek_returns_last_n_chars() {
        let r = ring(4096);
        r.append("abcdefgh");
        assert_eq!(r.peek(3), "fgh");
        assert_eq!(r.peek(8), "abcdefgh");
        assert_eq!(r.peek(100), "abcdefgh");
        assert_eq!(r.peek(0), "");
    }

    // ── memory bounding ───────────────────────────────────────────────────────

    /// Emitting far more bytes than the cap must never grow memory past the cap.
    #[test]
    fn large_volume_stays_within_cap() {
        let cap: u64 = 4096;
        let r = ring(cap);

        // Append 100× the cap in 512-byte chunks.
        let chunk = "x".repeat(512);
        for _ in 0..800 {
            r.append(&chunk);
        }

        let s = r.state.lock().unwrap();
        assert!(
            s.byte_len <= cap,
            "byte_len {} exceeded cap {}",
            s.byte_len,
            cap
        );
        // Overflow must be accounted for.
        assert!(s.dropped_chars > 0, "expected some drops after overflow");
    }

    #[test]
    fn chunk_count_stays_bounded() {
        let cap: u64 = 4096;
        let r = ring(cap);
        for _ in 0..10_000 {
            r.append("x");
        }
        // At most a few chunks should survive.
        assert!(r.chunk_count() < 32, "too many chunks: {}", r.chunk_count());
    }

    // ── truncation flag ────────────────────────────────────────────────────────

    #[test]
    fn truncated_is_true_when_cursor_is_before_dropped() {
        let cap: u64 = 100;
        let r = ring(cap);
        r.append(&"x".repeat(200));
        let dropped = r.dropped_chars();
        assert!(dropped > 0);
        let (_, _, trunc) = r.read_from(dropped - 1);
        assert!(trunc);
        let (_, _, trunc) = r.read_from(dropped);
        assert!(!trunc);
    }

    // ── consecutive reads are cursor-monotonic ────────────────────────────────

    #[test]
    fn incremental_reads_cover_all_output() {
        let r = ring(4096);
        r.append("aaa");
        let (t1, c1, _) = r.read_from(0);
        assert_eq!(t1, "aaa");
        r.append("bbb");
        let (t2, c2, _) = r.read_from(c1);
        assert_eq!(t2, "bbb");
        r.append("ccc");
        let (t3, _, _) = r.read_from(c2);
        assert_eq!(t3, "ccc");
    }

    // ── single oversized append is trimmed, not rejected ─────────────────────

    #[test]
    fn oversized_single_append_is_trimmed_to_cap() {
        let cap: u64 = 64;
        let r = ring(cap);
        r.append(&"z".repeat(1000));
        let s = r.state.lock().unwrap();
        assert!(s.byte_len <= cap, "byte_len {} > cap {}", s.byte_len, cap);
    }
}
