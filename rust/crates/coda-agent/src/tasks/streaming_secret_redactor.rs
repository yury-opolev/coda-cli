//! Incremental, streaming secret redactor for task log output.
//!
//! Mirrors `SecretRedactor.Redact` patterns but operates on an arbitrary stream
//! of characters fed in any-sized chunks, so a credential split across two writes
//! is still correctly redacted. This is the most important correctness property:
//! a secret split at any byte offset must never appear in persistent log files.
//!
//! Two patterns are detected:
//! - `sk-<8+ alphanumeric/dash chars>` → `sk-***`
//! - `[Bb]earer\s+<20+ token chars>` → `Bearer ******`
//!
//! Ordinary text is preserved exactly. An unconfirmed, incomplete candidate held
//! at the end of a write is emitted verbatim only by [`StreamingSecretRedactor::flush`].
//!
//! **Recursion-free design.** The C# original uses mutual recursion between
//! `Step` and `Backtrack`, which is safe on the CLR but overflows the Rust debug
//! stack for long pending buffers. Here the state machine drives an explicit
//! `VecDeque<char>` work queue: `backtrack` pushes chars back to the front of
//! the queue rather than calling `step` recursively, keeping the call stack O(1)
//! regardless of token length.

use std::collections::VecDeque;

const SK_PREFIX: &str = "sk-";
const SK_MIN_BODY: usize = 8;
pub(super) const SK_PLACEHOLDER: &str = "sk-***";

const BEARER_PREFIX_LOWER: &str = "bearer";
const BEARER_MIN_BODY: usize = 20;
pub(super) const BEARER_PLACEHOLDER: &str = "******";

/// Maximum unconfirmed candidate buffer. Only the whitespace run of a `Bearer\s+`
/// prefix can grow without confirming; capping it keeps memory bounded.
const MAX_CANDIDATE: usize = 8192;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Mode {
    Text,
    Sk,
    SkBody,
    DiscardSkBody,
    BearerPrefix,
    BearerSpace,
    BearerSpaceStreaming,
    BearerBody,
    DiscardBearerBody,
    DiscardBearerSpace,
    DiscardBearerEq,
}

/// Per-channel streaming secret redactor. Feed text via [`process`], finish a
/// stream with [`flush`].
pub struct StreamingSecretRedactor {
    pending: String,
    mode: Mode,
    body_count: usize,
    /// Length of trailing run matching the case-insensitive `bearer` prefix.
    bearer_match: usize,
}

impl Default for StreamingSecretRedactor {
    fn default() -> Self {
        Self::new()
    }
}

impl StreamingSecretRedactor {
    pub fn new() -> Self {
        Self {
            pending: String::new(),
            mode: Mode::Text,
            body_count: 0,
            bearer_match: 0,
        }
    }

    /// Process `input`, appending redacted output to `output`.
    pub fn process(&mut self, input: &str, output: &mut String) {
        let mut queue: VecDeque<char> = input.chars().collect();
        self.drain_queue(output, &mut queue);
    }

    /// Drain any incomplete, unconfirmed candidate at end of stream.
    pub fn flush(&mut self, output: &mut String) {
        while !self.pending.is_empty() {
            let held = std::mem::take(&mut self.pending);
            self.mode = Mode::Text;
            self.body_count = 0;
            self.bearer_match = 0;

            let mut chars = held.chars();
            output.push(chars.next().unwrap());
            let mut queue: VecDeque<char> = chars.collect();
            self.drain_queue(output, &mut queue);
        }
        self.mode = Mode::Text;
        self.body_count = 0;
        self.bearer_match = 0;
    }

    fn drain_queue(&mut self, output: &mut String, queue: &mut VecDeque<char>) {
        while let Some(c) = queue.pop_front() {
            self.step(c, output, queue);
        }
    }

    fn step(&mut self, c: char, output: &mut String, queue: &mut VecDeque<char>) {
        match self.mode {
            Mode::Text => {
                if c == 's' {
                    self.mode = Mode::Sk;
                    self.pending.push(c);
                } else if c == 'b' || c == 'B' {
                    self.mode = Mode::BearerPrefix;
                    self.pending.push(c);
                } else {
                    output.push(c);
                }
            }

            Mode::Sk => {
                let expected = SK_PREFIX.chars().nth(self.pending.len()).unwrap();
                if c == expected {
                    self.pending.push(c);
                    if self.pending.len() == SK_PREFIX.len() {
                        self.mode = Mode::SkBody;
                        self.body_count = 0;
                        self.bearer_match = 0;
                    }
                } else {
                    self.backtrack(c, output, queue);
                }
            }

            Mode::SkBody => {
                if is_sk_body(c) {
                    self.pending.push(c);
                    self.bearer_match = advance_bearer_match(self.bearer_match, c);
                    self.body_count += 1;
                    if self.body_count >= SK_MIN_BODY {
                        output.push_str(SK_PLACEHOLDER);
                        self.pending.clear();
                        self.mode = Mode::DiscardSkBody;
                    }
                } else {
                    self.backtrack(c, output, queue);
                }
            }

            Mode::DiscardSkBody => {
                if is_sk_body(c) {
                    self.bearer_match = advance_bearer_match(self.bearer_match, c);
                } else if self.bearer_match == BEARER_PREFIX_LOWER.len() && c.is_whitespace() {
                    self.mode = Mode::DiscardBearerSpace;
                    self.bearer_match = 0;
                } else {
                    self.mode = Mode::Text;
                    self.bearer_match = 0;
                    // Re-process c from Text mode using the queue rather than
                    // calling step recursively.
                    queue.push_front(c);
                }
            }

            Mode::BearerPrefix => {
                let expected = BEARER_PREFIX_LOWER.chars().nth(self.pending.len()).unwrap();
                if c.to_lowercase().next().unwrap() == expected {
                    self.pending.push(c);
                    if self.pending.len() == BEARER_PREFIX_LOWER.len() {
                        self.mode = Mode::BearerSpace;
                    }
                } else {
                    self.backtrack(c, output, queue);
                }
            }

            Mode::BearerSpace => {
                let have_whitespace = self.pending.len() > BEARER_PREFIX_LOWER.len();
                if c.is_whitespace() {
                    if self.pending.len() >= MAX_CANDIDATE {
                        output.push_str(&self.pending);
                        output.push(c);
                        self.pending.clear();
                        self.mode = Mode::BearerSpaceStreaming;
                    } else {
                        self.pending.push(c);
                    }
                } else if have_whitespace && is_bearer_body(c) {
                    self.pending.push(c);
                    self.body_count = 1;
                    self.bearer_match = advance_bearer_match(0, c);
                    self.mode = Mode::BearerBody;
                } else {
                    self.backtrack(c, output, queue);
                }
            }

            Mode::BearerSpaceStreaming => {
                if c.is_whitespace() {
                    output.push(c);
                } else if is_bearer_body(c) {
                    self.pending.push(c);
                    self.body_count = 1;
                    self.bearer_match = advance_bearer_match(0, c);
                    self.mode = Mode::BearerBody;
                } else {
                    output.push(c);
                    self.mode = Mode::Text;
                }
            }

            Mode::BearerBody => {
                if is_bearer_body(c) {
                    self.pending.push(c);
                    self.bearer_match = advance_bearer_match(self.bearer_match, c);
                    self.body_count += 1;
                    if self.body_count >= BEARER_MIN_BODY {
                        output.push_str(BEARER_PLACEHOLDER);
                        self.pending.clear();
                        self.mode = Mode::DiscardBearerBody;
                    }
                } else {
                    self.backtrack(c, output, queue);
                }
            }

            Mode::DiscardBearerBody => {
                if c == '=' {
                    self.mode = Mode::DiscardBearerEq;
                } else if is_bearer_body(c) {
                    self.bearer_match = advance_bearer_match(self.bearer_match, c);
                } else if self.bearer_match == BEARER_PREFIX_LOWER.len() && c.is_whitespace() {
                    self.mode = Mode::DiscardBearerSpace;
                    self.bearer_match = 0;
                } else {
                    self.mode = Mode::Text;
                    self.bearer_match = 0;
                    queue.push_front(c);
                }
            }

            Mode::DiscardBearerSpace => {
                if c.is_whitespace() {
                    // Keep discarding whitespace of a nested "Bearer " prefix.
                } else if is_bearer_body(c) {
                    self.mode = Mode::DiscardBearerBody;
                    self.bearer_match = advance_bearer_match(0, c);
                } else {
                    self.mode = Mode::Text;
                    self.bearer_match = 0;
                    queue.push_front(c);
                }
            }

            Mode::DiscardBearerEq => {
                if c != '=' {
                    self.mode = Mode::Text;
                    queue.push_front(c);
                }
            }
        }
    }

    /// Emit the first held character as ordinary text and push the remainder
    /// plus `c` to the FRONT of the work queue. Non-recursive replacement for
    /// the C# `Backtrack` method.
    fn backtrack(&mut self, c: char, output: &mut String, queue: &mut VecDeque<char>) {
        let held = std::mem::take(&mut self.pending);
        self.mode = Mode::Text;
        self.body_count = 0;
        self.bearer_match = 0;

        let mut chars = held.chars();
        let first = chars.next().unwrap();
        output.push(first);

        // Push remaining held chars + c to the front (in original order).
        // We push in reverse so the first element ends up at the front.
        queue.push_front(c);
        let remainder: Vec<char> = chars.collect();
        for ch in remainder.into_iter().rev() {
            queue.push_front(ch);
        }
    }
}

fn advance_bearer_match(match_len: usize, c: char) -> usize {
    let lower = c.to_lowercase().next().unwrap();
    if let Some(expected) = BEARER_PREFIX_LOWER.chars().nth(match_len) {
        if lower == expected {
            return match_len + 1;
        }
    }
    if lower == BEARER_PREFIX_LOWER.chars().next().unwrap() {
        1
    } else {
        0
    }
}

fn is_sk_body(c: char) -> bool {
    c.is_ascii_alphanumeric() || c == '-'
}

fn is_bearer_body(c: char) -> bool {
    c.is_ascii_alphanumeric() || matches!(c, '-' | '.' | '_' | '~' | '+' | '/')
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    fn redact(input: &str) -> String {
        let mut r = StreamingSecretRedactor::new();
        let mut out = String::new();
        r.process(input, &mut out);
        r.flush(&mut out);
        out
    }

    fn redact_chunks(chunks: &[&str]) -> String {
        let mut r = StreamingSecretRedactor::new();
        let mut out = String::new();
        for chunk in chunks {
            r.process(chunk, &mut out);
        }
        r.flush(&mut out);
        out
    }

    #[test]
    fn plain_text_passes_through() {
        assert_eq!(redact("hello world"), "hello world");
        assert_eq!(redact("no secrets here"), "no secrets here");
    }

    #[test]
    fn empty_input_produces_empty_output() {
        assert_eq!(redact(""), "");
    }

    #[test]
    fn sk_token_is_redacted_in_single_chunk() {
        let out = redact("key=sk-abcdefghij rest");
        assert!(!out.contains("sk-abcdefghij"), "key leaked: {out}");
        assert!(out.contains(SK_PLACEHOLDER), "placeholder missing: {out}");
    }

    #[test]
    fn sk_token_too_short_is_not_redacted() {
        let out = redact("sk-abc");
        assert_eq!(out, "sk-abc");
    }

    #[test]
    fn sk_exact_minimum_body_is_redacted() {
        let out = redact("sk-abcdefgh");
        assert!(!out.contains("abcdefgh"), "body leaked: {out}");
        assert!(out.contains(SK_PLACEHOLDER), "placeholder missing: {out}");
    }

    #[test]
    fn bearer_token_is_redacted_in_single_chunk() {
        let out = redact("Authorization: Bearer abcdefghijklmnopqrst rest");
        assert!(!out.contains("abcdefghijklmnopqrst"), "bearer body leaked: {out}");
        assert!(out.contains(BEARER_PLACEHOLDER), "placeholder missing: {out}");
    }

    #[test]
    fn bearer_too_short_is_not_redacted() {
        let out = redact("Bearer abcdefghij end");
        assert!(!out.contains(BEARER_PLACEHOLDER), "should not be redacted: {out}");
    }

    // ── CRITICAL: chunk-boundary tests ────────────────────────────────────────

    #[test]
    fn sk_token_split_at_every_byte_offset_is_redacted() {
        let secret = "sk-abcdefghijklmnop"; // 16 body chars
        for split in 0..=secret.len() {
            let (a, b) = secret.split_at(split);
            let out = redact_chunks(&[a, b]);
            assert!(
                !out.contains("abcdefghijklmnop"),
                "sk- body leaked at split {split}: {out}"
            );
            assert!(
                out.contains(SK_PLACEHOLDER),
                "placeholder missing at split {split}: {out}"
            );
        }
    }

    #[test]
    fn bearer_token_split_at_every_byte_offset_is_redacted() {
        let secret = "Bearer abcdefghijklmnopqrstuvwxyz"; // 26 body chars
        for split in 0..=secret.len() {
            let (a, b) = secret.split_at(split);
            let out = redact_chunks(&[a, b]);
            assert!(
                !out.contains("abcdefghijklmnopqrstuvwxyz"),
                "bearer body leaked at split {split}: {out}"
            );
            assert!(
                out.contains(BEARER_PLACEHOLDER),
                "placeholder missing at split {split}: {out}"
            );
        }
    }

    #[test]
    fn sk_split_three_way_is_still_redacted() {
        let out = redact_chunks(&["sk-", "abcde", "fghijk"]);
        assert!(!out.contains("abcde"), "body leaked: {out}");
        assert!(out.contains(SK_PLACEHOLDER), "placeholder missing: {out}");
    }

    #[test]
    fn text_before_and_after_secret_is_preserved() {
        let out = redact("prefix sk-abcdefghij suffix");
        assert!(out.starts_with("prefix "), "prefix missing: {out}");
        assert!(out.ends_with(" suffix"), "suffix missing: {out}");
    }

    #[test]
    fn two_consecutive_sk_tokens_are_both_redacted() {
        let out = redact("sk-abcdefghij sk-01234567890");
        assert!(!out.contains("abcdefghij"), "first token leaked: {out}");
        assert!(!out.contains("01234567890"), "second token leaked: {out}");
    }

    #[test]
    fn incomplete_sk_prefix_at_end_is_emitted_verbatim() {
        let out = redact("sk-");
        assert_eq!(out, "sk-");
    }

    #[test]
    fn incomplete_bearer_prefix_at_end_is_emitted_verbatim() {
        let out = redact("bea");
        assert_eq!(out, "bea");
    }
}
