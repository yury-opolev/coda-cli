//! A small, allocation-light syntax highlighter.
//!
//! This is a hand-rolled character scanner rather than a full parser, matching
//! the C# `SyntaxTokenizer`. It only needs to be good enough to colour code
//! blocks and diff bodies, and it must never panic or mis-slice UTF-8 no matter
//! what a model emits.
//!
//! State carries across lines so that block comments and triple-quoted strings
//! highlight correctly in a multi-line block.

use crate::line::Span;
use crate::theme::Role;

/// Languages the tokenizer recognises.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Language {
    #[default]
    None,
    CSharp,
    TypeScript,
    JavaScript,
    Python,
    Json,
    Shell,
    PowerShell,
    Rust,
}

impl Language {
    /// Resolves a fenced-code-block info string such as `rust` or `ts`.
    pub fn from_info_string(info: &str) -> Language {
        let token = info
            .trim()
            .split_whitespace()
            .next()
            .unwrap_or_default()
            .trim_start_matches('{')
            .trim_start_matches('.')
            .to_ascii_lowercase();

        match token.as_str() {
            "csharp" | "cs" | "c#" => Language::CSharp,
            "typescript" | "ts" | "tsx" => Language::TypeScript,
            "javascript" | "js" | "jsx" | "mjs" => Language::JavaScript,
            "python" | "py" => Language::Python,
            "json" | "jsonc" => Language::Json,
            "bash" | "sh" | "shell" | "zsh" => Language::Shell,
            "powershell" | "pwsh" | "ps1" => Language::PowerShell,
            "rust" | "rs" => Language::Rust,
            _ => Language::None,
        }
    }

    /// Resolves a language from a file path, used to highlight diff bodies.
    pub fn from_path(path: &str) -> Language {
        let extension = path
            .rsplit('/')
            .next()
            .unwrap_or(path)
            .rsplit('\\')
            .next()
            .unwrap_or(path)
            .rsplit_once('.')
            .map(|(_, ext)| ext.to_ascii_lowercase())
            .unwrap_or_default();

        match extension.as_str() {
            "cs" | "csx" => Language::CSharp,
            "ts" | "tsx" | "mts" => Language::TypeScript,
            "js" | "jsx" | "mjs" | "cjs" => Language::JavaScript,
            "py" | "pyi" => Language::Python,
            "json" => Language::Json,
            "sh" | "bash" | "zsh" => Language::Shell,
            "ps1" | "psm1" => Language::PowerShell,
            "rs" => Language::Rust,
            _ => Language::None,
        }
    }

    fn keywords(self) -> &'static [&'static str] {
        match self {
            Language::CSharp => &[
                "abstract", "as", "async", "await", "base", "break", "case", "catch", "class",
                "const", "continue", "default", "delegate", "do", "else", "enum", "event",
                "explicit", "extern", "finally", "fixed", "for", "foreach", "get", "goto", "if",
                "implicit", "in", "init", "interface", "internal", "is", "lock", "namespace",
                "new", "operator", "out", "override", "params", "private", "protected", "public",
                "readonly", "record", "ref", "return", "sealed", "set", "sizeof", "stackalloc",
                "static", "struct", "switch", "this", "throw", "try", "typeof", "unchecked",
                "unsafe", "using", "var", "virtual", "volatile", "when", "where", "while", "yield",
                "null", "true", "false",
            ],
            Language::TypeScript | Language::JavaScript => &[
                "as", "async", "await", "break", "case", "catch", "class", "const", "continue",
                "debugger", "default", "delete", "do", "else", "enum", "export", "extends",
                "finally", "for", "from", "function", "if", "implements", "import", "in",
                "instanceof", "interface", "let", "new", "of", "package", "private", "protected",
                "public", "readonly", "return", "satisfies", "static", "super", "switch", "this",
                "throw", "try", "type", "typeof", "var", "void", "while", "with", "yield", "null",
                "true", "false", "undefined",
            ],
            Language::Python => &[
                "and", "as", "assert", "async", "await", "break", "class", "continue", "def",
                "del", "elif", "else", "except", "finally", "for", "from", "global", "if",
                "import", "in", "is", "lambda", "match", "nonlocal", "not", "or", "pass", "raise",
                "return", "try", "while", "with", "yield", "None", "True", "False",
            ],
            Language::Json => &["true", "false", "null"],
            Language::Shell => &[
                "if", "then", "else", "elif", "fi", "for", "while", "until", "do", "done", "case",
                "esac", "function", "in", "return", "export", "local", "readonly", "declare",
                "source", "exit", "shift", "set", "unset", "trap", "echo",
            ],
            Language::PowerShell => &[
                "begin", "break", "catch", "class", "continue", "data", "do", "dynamicparam",
                "else", "elseif", "end", "enum", "exit", "filter", "finally", "for", "foreach",
                "from", "function", "if", "in", "param", "process", "return", "switch", "throw",
                "trap", "try", "until", "using", "while",
            ],
            Language::Rust => &[
                "as", "async", "await", "break", "const", "continue", "crate", "dyn", "else",
                "enum", "extern", "false", "fn", "for", "if", "impl", "in", "let", "loop",
                "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self", "static",
                "struct", "super", "trait", "true", "type", "unsafe", "use", "where", "while",
            ],
            Language::None => &[],
        }
    }

    fn types(self) -> &'static [&'static str] {
        match self {
            Language::CSharp => &[
                "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object",
                "sbyte", "short", "string", "uint", "ulong", "ushort", "void", "dynamic", "Task",
                "List", "Dictionary", "IEnumerable", "Span", "Memory",
            ],
            Language::TypeScript | Language::JavaScript => &[
                "any", "bigint", "boolean", "never", "number", "object", "string", "symbol",
                "unknown", "Array", "Promise", "Record", "Map", "Set", "Date", "RegExp",
            ],
            Language::Python => &[
                "bool", "bytes", "dict", "float", "int", "list", "object", "set", "str", "tuple",
                "type", "Any", "Optional", "Sequence", "Iterable", "Callable",
            ],
            Language::Rust => &[
                "bool", "char", "f32", "f64", "i8", "i16", "i32", "i64", "i128", "isize", "str",
                "u8", "u16", "u32", "u64", "u128", "usize", "String", "Vec", "Option", "Result",
                "Box", "Rc", "Arc", "HashMap", "HashSet",
            ],
            Language::PowerShell => &[
                "int", "string", "bool", "double", "array", "hashtable", "psobject", "void",
            ],
            Language::Json | Language::Shell | Language::None => &[],
        }
    }

    fn line_comment(self) -> Option<&'static str> {
        match self {
            Language::CSharp
            | Language::TypeScript
            | Language::JavaScript
            | Language::Rust => Some("//"),
            Language::Python | Language::Shell | Language::PowerShell => Some("#"),
            Language::Json | Language::None => None,
        }
    }

    fn block_comment(self) -> Option<(&'static str, &'static str)> {
        match self {
            Language::CSharp
            | Language::TypeScript
            | Language::JavaScript
            | Language::Rust => Some(("/*", "*/")),
            Language::PowerShell => Some(("<#", "#>")),
            _ => None,
        }
    }

    /// String delimiters, longest first so `"""` wins over `"`.
    fn string_delimiters(self) -> &'static [(&'static str, bool)] {
        match self {
            Language::Python => &[(r#"""""#, true), ("'''", true), (r#"""#, false), ("'", false)],
            Language::CSharp | Language::TypeScript | Language::JavaScript => {
                &[(r#"""#, false), ("'", false), ("`", true)]
            }
            Language::Rust => &[(r#"""#, false), ("'", false)],
            Language::Json => &[(r#"""#, false)],
            Language::Shell | Language::PowerShell => &[(r#"""#, false), ("'", false)],
            Language::None => &[],
        }
    }

    /// Whether keyword matching is case-sensitive.
    fn case_sensitive(self) -> bool {
        !matches!(self, Language::PowerShell)
    }
}

/// Carries multi-line constructs from one line to the next.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Carry {
    #[default]
    None,
    BlockComment,
    /// An unterminated multi-line string; the delimiter that will close it.
    String(&'static str),
}

/// Tokenises source text into foreground spans.
#[derive(Debug, Clone, Default)]
pub struct Tokenizer {
    language: Language,
    carry: Carry,
}

impl Tokenizer {
    pub fn new(language: Language) -> Self {
        Self {
            language,
            carry: Carry::None,
        }
    }

    /// Discards multi-line state, so an unclosed construct cannot bleed across
    /// a hunk boundary or into an unrelated block.
    pub fn reset(&mut self) {
        self.carry = Carry::None;
    }

    pub fn language(&self) -> Language {
        self.language
    }

    /// Tokenises one line, returning spans in **cell** coordinates.
    pub fn tokenize_line(&mut self, line: &str) -> Vec<Span> {
        if self.language == Language::None {
            return Vec::new();
        }

        // Work in chars, then convert to cells once at the end. Code is
        // overwhelmingly ASCII, so this is cheap in the common case.
        let chars: Vec<char> = line.chars().collect();
        let mut spans: Vec<(usize, usize, Role)> = Vec::new();
        let mut i = 0usize;

        // Resume whatever was left open by the previous line.
        match self.carry {
            Carry::BlockComment => {
                let (_, close) = self.language.block_comment().unwrap_or(("", "*/"));
                match find(&chars, 0, close) {
                    Some(end) => {
                        spans.push((0, end + close.chars().count(), Role::SyntaxComment));
                        i = end + close.chars().count();
                        self.carry = Carry::None;
                    }
                    None => {
                        if !chars.is_empty() {
                            spans.push((0, chars.len(), Role::SyntaxComment));
                        }
                        return to_cell_spans(line, &spans);
                    }
                }
            }
            Carry::String(close) => match find(&chars, 0, close) {
                Some(end) => {
                    spans.push((0, end + close.chars().count(), Role::SyntaxString));
                    i = end + close.chars().count();
                    self.carry = Carry::None;
                }
                None => {
                    if !chars.is_empty() {
                        spans.push((0, chars.len(), Role::SyntaxString));
                    }
                    return to_cell_spans(line, &spans);
                }
            },
            Carry::None => {}
        }

        while i < chars.len() {
            let c = chars[i];

            if c.is_whitespace() {
                i += 1;
                continue;
            }

            // Line comment: consumes the rest of the line.
            if let Some(marker) = self.language.line_comment() {
                if starts_with(&chars, i, marker) {
                    spans.push((i, chars.len(), Role::SyntaxComment));
                    break;
                }
            }

            // Block comment.
            if let Some((open, close)) = self.language.block_comment() {
                if starts_with(&chars, i, open) {
                    let after_open = i + open.chars().count();
                    match find(&chars, after_open, close) {
                        Some(end) => {
                            let stop = end + close.chars().count();
                            spans.push((i, stop, Role::SyntaxComment));
                            i = stop;
                        }
                        None => {
                            spans.push((i, chars.len(), Role::SyntaxComment));
                            self.carry = Carry::BlockComment;
                            i = chars.len();
                        }
                    }
                    continue;
                }
            }

            // String literal. Delimiters are ordered longest-first.
            if let Some((delim, multiline)) = self
                .language
                .string_delimiters()
                .iter()
                .find(|(d, _)| starts_with(&chars, i, d))
                .copied()
            {
                let after_open = i + delim.chars().count();
                match find_unescaped(&chars, after_open, delim) {
                    Some(end) => {
                        let stop = end + delim.chars().count();
                        spans.push((i, stop, Role::SyntaxString));
                        i = stop;
                    }
                    None => {
                        spans.push((i, chars.len(), Role::SyntaxString));
                        if multiline {
                            self.carry = Carry::String(delim);
                        }
                        i = chars.len();
                    }
                }
                continue;
            }

            // Number literal.
            if c.is_ascii_digit() {
                let start = i;
                i = scan_number(&chars, i);
                spans.push((start, i, Role::SyntaxNumber));
                continue;
            }

            // Identifier, keyword or type.
            if is_identifier_start(c) {
                let start = i;
                while i < chars.len() && is_identifier_char(chars[i]) {
                    i += 1;
                }
                let word: String = chars[start..i].iter().collect();
                if let Some(role) = self.classify(&word) {
                    spans.push((start, i, role));
                }
                continue;
            }

            i += 1;
        }

        to_cell_spans(line, &spans)
    }

    fn classify(&self, word: &str) -> Option<Role> {
        let matches = |list: &[&str]| {
            if self.language.case_sensitive() {
                list.contains(&word)
            } else {
                list.iter().any(|k| k.eq_ignore_ascii_case(word))
            }
        };

        if matches(self.language.keywords()) {
            Some(Role::SyntaxKeyword)
        } else if matches(self.language.types()) {
            Some(Role::SyntaxType)
        } else {
            None
        }
    }
}

fn is_identifier_start(c: char) -> bool {
    c.is_alphabetic() || c == '_' || c == '$' || c == '@'
}

fn is_identifier_char(c: char) -> bool {
    c.is_alphanumeric() || c == '_' || c == '$'
}

/// Consumes a numeric literal, including hex, binary, floats and exponents.
fn scan_number(chars: &[char], start: usize) -> usize {
    let mut i = start;

    if chars[i] == '0' && i + 1 < chars.len() && matches!(chars[i + 1], 'x' | 'X' | 'b' | 'B') {
        i += 2;
        while i < chars.len() && (chars[i].is_ascii_alphanumeric() || chars[i] == '_') {
            i += 1;
        }
        return i;
    }

    while i < chars.len() && (chars[i].is_ascii_digit() || chars[i] == '_') {
        i += 1;
    }
    if i < chars.len() && chars[i] == '.' && i + 1 < chars.len() && chars[i + 1].is_ascii_digit() {
        i += 1;
        while i < chars.len() && (chars[i].is_ascii_digit() || chars[i] == '_') {
            i += 1;
        }
    }
    if i < chars.len() && matches!(chars[i], 'e' | 'E') {
        let mut j = i + 1;
        if j < chars.len() && matches!(chars[j], '+' | '-') {
            j += 1;
        }
        if j < chars.len() && chars[j].is_ascii_digit() {
            i = j;
            while i < chars.len() && chars[i].is_ascii_digit() {
                i += 1;
            }
        }
    }
    // Trailing type suffix (1.0f, 10u64, 5L).
    while i < chars.len() && (chars[i].is_ascii_alphanumeric() || chars[i] == '_') {
        i += 1;
    }
    i
}

fn starts_with(chars: &[char], at: usize, needle: &str) -> bool {
    let needle: Vec<char> = needle.chars().collect();
    if at + needle.len() > chars.len() {
        return false;
    }
    chars[at..at + needle.len()] == needle[..]
}

fn find(chars: &[char], from: usize, needle: &str) -> Option<usize> {
    let len = needle.chars().count();
    if len == 0 || from > chars.len() {
        return None;
    }
    (from..=chars.len().saturating_sub(len)).find(|&i| starts_with(chars, i, needle))
}

/// Finds a closing delimiter, skipping backslash-escaped occurrences.
fn find_unescaped(chars: &[char], from: usize, needle: &str) -> Option<usize> {
    let len = needle.chars().count();
    let mut i = from;
    while i + len <= chars.len() {
        if chars[i] == '\\' {
            i += 2;
            continue;
        }
        if starts_with(chars, i, needle) {
            return Some(i);
        }
        i += 1;
    }
    None
}

/// Converts char-index spans into cell-index spans.
fn to_cell_spans(line: &str, spans: &[(usize, usize, Role)]) -> Vec<Span> {
    if spans.is_empty() {
        return Vec::new();
    }

    // Prefix widths: cell offset of each char index.
    let mut offsets = Vec::with_capacity(line.chars().count() + 1);
    let mut cells = 0usize;
    offsets.push(0usize);
    for c in line.chars() {
        cells += crate::text::grapheme_width(&c.to_string());
        offsets.push(cells);
    }

    spans
        .iter()
        .filter_map(|&(start, end, role)| {
            let start_cell = *offsets.get(start)?;
            let end_cell = *offsets.get(end.min(offsets.len() - 1))?;
            (end_cell > start_cell).then(|| Span::new(start_cell, end_cell, role))
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn roles(language: Language, line: &str) -> Vec<(usize, usize, Role)> {
        Tokenizer::new(language)
            .tokenize_line(line)
            .into_iter()
            .map(|s| (s.start, s.end, s.role))
            .collect()
    }

    fn text_of(line: &str, span: &Span) -> String {
        line.chars().skip(span.start).take(span.end - span.start).collect()
    }

    #[test]
    fn resolves_languages_from_info_strings() {
        assert_eq!(Language::from_info_string("rust"), Language::Rust);
        assert_eq!(Language::from_info_string("RS"), Language::Rust);
        assert_eq!(Language::from_info_string("ts"), Language::TypeScript);
        assert_eq!(Language::from_info_string("c#"), Language::CSharp);
        assert_eq!(Language::from_info_string("pwsh"), Language::PowerShell);
        assert_eq!(Language::from_info_string(""), Language::None);
        assert_eq!(Language::from_info_string("brainfuck"), Language::None);
    }

    #[test]
    fn resolves_a_language_from_an_info_string_with_attributes() {
        assert_eq!(Language::from_info_string("python title=x"), Language::Python);
    }

    #[test]
    fn resolves_languages_from_paths() {
        assert_eq!(Language::from_path("src/main.rs"), Language::Rust);
        assert_eq!(Language::from_path(r"src\Program.cs"), Language::CSharp);
        assert_eq!(Language::from_path("a/b/c.tsx"), Language::TypeScript);
        assert_eq!(Language::from_path("Makefile"), Language::None);
    }

    #[test]
    fn highlights_keywords() {
        let spans = roles(Language::Rust, "let x = 1;");
        assert_eq!(spans[0], (0, 3, Role::SyntaxKeyword));
    }

    #[test]
    fn highlights_types() {
        let line = "let v: Vec = x;";
        let spans = Tokenizer::new(Language::Rust).tokenize_line(line);
        let type_span = spans
            .iter()
            .find(|s| s.role == Role::SyntaxType)
            .expect("a type span");
        assert_eq!(text_of(line, type_span), "Vec");
    }

    #[test]
    fn highlights_strings() {
        let line = r#"let s = "hello";"#;
        let spans = Tokenizer::new(Language::Rust).tokenize_line(line);
        let string_span = spans
            .iter()
            .find(|s| s.role == Role::SyntaxString)
            .expect("a string span");
        assert_eq!(text_of(line, string_span), "\"hello\"");
    }

    #[test]
    fn does_not_end_a_string_at_an_escaped_quote() {
        let line = r#""a\"b""#;
        let spans = Tokenizer::new(Language::Rust).tokenize_line(line);
        let string_span = &spans[0];
        assert_eq!(text_of(line, string_span), r#""a\"b""#);
    }

    #[test]
    fn highlights_numbers_including_hex_and_floats() {
        for (line, expected) in [
            ("x = 42", "42"),
            ("x = 0xFF", "0xFF"),
            ("x = 1.5", "1.5"),
            ("x = 1e10", "1e10"),
        ] {
            let spans = Tokenizer::new(Language::Rust).tokenize_line(line);
            let number = spans
                .iter()
                .find(|s| s.role == Role::SyntaxNumber)
                .unwrap_or_else(|| panic!("no number in {line}"));
            assert_eq!(text_of(line, number), expected);
        }
    }

    #[test]
    fn highlights_a_line_comment_to_end_of_line() {
        let line = "let x = 1; // trailing note";
        let spans = Tokenizer::new(Language::Rust).tokenize_line(line);
        let comment = spans
            .iter()
            .find(|s| s.role == Role::SyntaxComment)
            .expect("a comment");
        assert_eq!(text_of(line, comment), "// trailing note");
    }

    #[test]
    fn uses_hash_comments_for_python_and_shell() {
        for language in [Language::Python, Language::Shell] {
            let spans = roles(language, "x = 1 # note");
            assert!(spans.iter().any(|&(_, _, r)| r == Role::SyntaxComment));
        }
    }

    #[test]
    fn carries_a_block_comment_across_lines() {
        let mut tokenizer = Tokenizer::new(Language::Rust);
        let first = tokenizer.tokenize_line("/* start");
        assert_eq!(first[0].role, Role::SyntaxComment);
        assert_eq!(tokenizer.carry, Carry::BlockComment);

        let middle = tokenizer.tokenize_line("still comment");
        assert_eq!(middle[0].role, Role::SyntaxComment);

        let last = tokenizer.tokenize_line("end */ let x = 1;");
        assert_eq!(last[0].role, Role::SyntaxComment);
        assert_eq!(tokenizer.carry, Carry::None);
        assert!(last.iter().any(|s| s.role == Role::SyntaxKeyword));
    }

    #[test]
    fn closes_a_block_comment_on_the_same_line() {
        let mut tokenizer = Tokenizer::new(Language::Rust);
        let spans = tokenizer.tokenize_line("let /* mid */ x = 1;");
        assert_eq!(tokenizer.carry, Carry::None);
        assert!(spans.iter().any(|s| s.role == Role::SyntaxComment));
    }

    #[test]
    fn carries_a_python_triple_quoted_string_across_lines() {
        let mut tokenizer = Tokenizer::new(Language::Python);
        tokenizer.tokenize_line(r#"doc = """start"#);
        assert!(matches!(tokenizer.carry, Carry::String(_)));

        let middle = tokenizer.tokenize_line("inside the docstring");
        assert_eq!(middle[0].role, Role::SyntaxString);

        tokenizer.tokenize_line(r#"end""""#);
        assert_eq!(tokenizer.carry, Carry::None);
    }

    #[test]
    fn does_not_carry_a_single_quoted_string_across_lines() {
        let mut tokenizer = Tokenizer::new(Language::Rust);
        tokenizer.tokenize_line(r#"let s = "unterminated"#);
        assert_eq!(tokenizer.carry, Carry::None);
    }

    #[test]
    fn reset_discards_carried_state() {
        let mut tokenizer = Tokenizer::new(Language::Rust);
        tokenizer.tokenize_line("/* open");
        assert_eq!(tokenizer.carry, Carry::BlockComment);
        tokenizer.reset();
        assert_eq!(tokenizer.carry, Carry::None);
    }

    #[test]
    fn powershell_keywords_are_case_insensitive() {
        assert!(roles(Language::PowerShell, "IF ($x) { }")
            .iter()
            .any(|&(_, _, r)| r == Role::SyntaxKeyword));
        assert!(roles(Language::PowerShell, "if ($x) { }")
            .iter()
            .any(|&(_, _, r)| r == Role::SyntaxKeyword));
    }

    #[test]
    fn rust_keywords_are_case_sensitive() {
        assert!(roles(Language::Rust, "LET x = 1")
            .iter()
            .all(|&(_, _, r)| r != Role::SyntaxKeyword));
    }

    #[test]
    fn produces_no_spans_for_an_unknown_language() {
        assert!(roles(Language::None, "let x = 1; // comment").is_empty());
    }

    #[test]
    fn produces_no_spans_for_a_blank_line() {
        assert!(roles(Language::Rust, "").is_empty());
        assert!(roles(Language::Rust, "    ").is_empty());
    }

    #[test]
    fn reports_spans_in_cell_coordinates_not_char_indices() {
        // The wide prefix occupies two cells per character.
        let line = "日本 let";
        let spans = Tokenizer::new(Language::Rust).tokenize_line(line);
        let keyword = spans
            .iter()
            .find(|s| s.role == Role::SyntaxKeyword)
            .expect("a keyword");
        // "日本 " is 2 + 2 + 1 = 5 cells.
        assert_eq!(keyword.start, 5);
        assert_eq!(keyword.end, 8);
    }

    #[test]
    fn never_panics_on_adversarial_input() {
        let samples = [
            "\"",
            "'",
            "/*",
            "*/",
            "\\",
            "0x",
            "1e",
            "\"\\",
            "```",
            "日本語 /* 漢字",
            "🚀 let 🚀",
            "#",
            "//",
        ];
        for language in [
            Language::Rust,
            Language::Python,
            Language::CSharp,
            Language::Json,
            Language::Shell,
            Language::PowerShell,
            Language::TypeScript,
        ] {
            let mut tokenizer = Tokenizer::new(language);
            for sample in samples {
                let _ = tokenizer.tokenize_line(sample);
            }
        }
    }

    #[test]
    fn spans_never_exceed_the_line_width() {
        let line = "let s = \"日本語\"; // 注釈";
        let cells = crate::text::width(line);
        for span in Tokenizer::new(Language::Rust).tokenize_line(line) {
            assert!(span.end <= cells, "span {span:?} exceeds {cells} cells");
            assert!(span.start < span.end);
        }
    }

    #[test]
    fn json_highlights_keys_as_strings_and_literals_as_keywords() {
        let line = r#"{"a": true, "b": 12}"#;
        let spans = Tokenizer::new(Language::Json).tokenize_line(line);
        assert!(spans.iter().any(|s| s.role == Role::SyntaxString));
        assert!(spans.iter().any(|s| s.role == Role::SyntaxKeyword));
        assert!(spans.iter().any(|s| s.role == Role::SyntaxNumber));
    }
}
