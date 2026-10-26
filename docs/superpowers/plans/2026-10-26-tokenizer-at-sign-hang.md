# Tokenizer `@` Hang Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `coda_render::syntax::Tokenizer` from looping forever on an unquoted `@` (REQUEST CODA bugfix 9), which froze the TUI while rendering a highlighted code fence.

**Root cause:** `is_identifier_start` accepts `@` but `is_identifier_char` does not. The identifier branch of the scan loop in `rust/crates/coda-render/src/syntax.rs` sets `start = i` and then loops on `is_identifier_char`, which makes zero iterations at `@`; `continue` re-enters with the same `i` forever. Introduced by 955273ca (#113). Every other branch of the loop advances `i` (whitespace and fallthrough `+1`, comments/strings jump past the opener or to end of line, numbers consume at least one ASCII digit, and Rust's `is_alphabetic` implies `is_alphanumeric`), so `@` is the only start/continuation mismatch.

**Architecture:** Consume the accepted start character before scanning continuation characters, so identifier slices include their prefix (`@name`, `@class`). Add a defensive progress guard at the top of the scan loop: `debug_assert!` in debug builds, forced one-char advance in release, so any future zero-progress branch cannot hang the UI. Tests run the tokenizer/renderer on a worker thread with a deadline so a regression fails instead of hanging the runner.

**Tech Stack:** Rust, `cargo test -p coda-render`.

---

### Task 1: Regression tests (red), fix (green), progress guard

**Files:**
- Modify: `rust/crates/coda-render/src/syntax.rs` (scan loop, identifier branch, tests module)
- Modify: `rust/crates/coda-render/src/markdown.rs` (tests module only)

- [ ] **Step 1: Add a deadline helper and failing tokenizer tests** in `syntax.rs` `mod tests`:

```rust
    const LANGUAGES: [Language; 8] = [
        Language::CSharp,
        Language::TypeScript,
        Language::JavaScript,
        Language::Python,
        Language::Json,
        Language::Shell,
        Language::PowerShell,
        Language::Rust,
    ];

    /// Tokenizes `lines` (sharing one tokenizer, so carried state is
    /// exercised) on a worker thread, failing instead of hanging the test
    /// runner if the scan loop stops making progress.
    fn tokenize_within_deadline(language: Language, lines: Vec<String>) -> Vec<Vec<Span>> {
        use std::sync::mpsc::{self, RecvTimeoutError};
        let (tx, rx) = mpsc::channel();
        std::thread::spawn(move || {
            let mut tokenizer = Tokenizer::new(language);
            let spans: Vec<Vec<Span>> = lines.iter().map(|l| tokenizer.tokenize_line(l)).collect();
            let _ = tx.send(spans);
        });
        match rx.recv_timeout(std::time::Duration::from_secs(10)) {
            Ok(spans) => spans,
            Err(RecvTimeoutError::Timeout) => panic!("{language:?} tokenizer did not terminate"),
            Err(RecvTimeoutError::Disconnected) => panic!("{language:?} tokenizer panicked"),
        }
    }

    fn keyword_for(language: Language) -> &'static str {
        match language {
            Language::CSharp => "class",
            Language::TypeScript | Language::JavaScript => "const",
            Language::Python => "def",
            Language::Json => "true",
            Language::Shell => "echo",
            Language::PowerShell => "if",
            Language::Rust => "let",
            Language::None => "",
        }
    }
```

Tests (each asserts through `tokenize_within_deadline`):
1. `terminates_on_unquoted_at_signs` — every language, lines `"@"`, `"@name"`, `"@@"`, `"a@@b"`, `"x @"`, `"claude plugin install superpowers@superpowers-marketplace"`.
2. `keeps_scanning_after_an_at_sign` — every language, line `format!("@ {kw} @@42 @{kw}")`: a `SyntaxKeyword` span whose text is exactly `kw`, a `SyntaxNumber` span `"42"`; `@{kw}` is not highlighted as a keyword.
3. `tokenizes_the_reported_powershell_command` — exact command; no span covers text containing `@` with keyword/type role; spans within width.
4. `treats_prefixed_identifiers_as_whole_words` — C# `var @class = 1;` → `var` keyword, `@class` not keyword, `1` number; Python `@property` then `def f(self):` → `def` keyword; PowerShell `$h = @{ a = 1 }` → number `1`.
5. `keeps_quoted_and_commented_at_signs_in_their_spans` — Rust `let s = "a@b"; // c@d` → string `"a@b"` (with quotes), comment `// c@d`; PowerShell `# @{x}` → whole-line comment; Python `x = '@x'` → string `'@x'`.
6. `terminates_on_every_printable_char_and_pair` — chars `' '..='~'` plus `['é', '日', '🚀', '\u{0301}', '\u{216B}', '\u{0663}', '\u{00A0}', 'ª', '\u{200B}']`; per language, each char alone and every ordered pair; spans have `start < end <= text::width(line)`.

- [ ] **Step 2: Run red** — `cargo test -p coda-render syntax` from `rust/`. Expected: tests 1–4 and 6 FAIL with "tokenizer did not terminate" (not hang); 5 passes (non-regression guard).

- [ ] **Step 3: Markdown integration tests** in `markdown.rs` `mod tests` with a `render_within_deadline(markdown, width)` helper mirroring the one above:
- `renders_a_powershell_fence_containing_an_at_sign` — the minimal fence at width 120 returns a `Role::Code` row whose text contains the full command including `@`.
- `renders_a_multi_paragraph_reply_with_an_at_sign_fence` — intro paragraph, the fence, a trailing paragraph (substitute for the saved reply): all three appear; the trailing paragraph proves rendering continued past the fence.

Run: expected FAIL with "did not terminate".

- [ ] **Step 4: Fix** the identifier branch:

```rust
            if is_identifier_start(c) {
                // Consume the start char first: '@' starts an identifier but
                // cannot continue one, so scanning only continuation chars
                // would never advance past it.
                let start = i;
                i += 1;
                while i < chars.len() && is_identifier_char(chars[i]) {
```

- [ ] **Step 5: Progress guard** at the top of the scan loop:

```rust
        let mut last_scanned: Option<usize> = None;
        while i < chars.len() {
            // Every branch must advance `i`. Fail loudly in debug builds, but
            // never let a future mistake hang the UI in release builds.
            if last_scanned == Some(i) {
                debug_assert!(false, "tokenizer made no progress at char {i} of {line:?}");
                i += 1;
                continue;
            }
            last_scanned = Some(i);
```

- [ ] **Step 6: Green** — `cargo test -p coda-render`; all pass, no new warnings. `rustfmt --edition 2021 --check` only on changed files is not required (files not fmt-clean); hand-format changed lines.

- [ ] **Step 7: Commit** `fix(render): stop the syntax tokenizer looping on an unquoted @` with the Copilot co-author trailer.

## Deviations (as implemented)

- Test 2 uses `@ {kw} @@ 42 @{kw}`: with the fix `@42` is one identifier token, so `42` must be separated to be a number.
- Test 6 bounds spans on lines containing U+0301 by per-char width rather than grapheme width: `to_cell_spans` measures per char, a pre-existing mismatch tracked as a follow-up.

