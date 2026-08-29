//! Unified diff parsing and rendering.
//!
//! Tool results and fenced code blocks frequently contain patches, and showing
//! them as flat monochrome text loses most of their meaning. This module parses
//! the unified format well enough to colour additions, removals and context,
//! and to label each file with the change it represents.

use crate::line::{Gutter, RenderLine};
use crate::syntax::{Language, Tokenizer};
use crate::text;
use crate::theme::Role;

/// Minimum width of the line-number gutter.
const MIN_LINE_NUMBER_WIDTH: usize = 3;
/// Cells needed beyond the line-number gutter before it is worth drawing.
const MIN_CONTENT_CELLS: usize = 4;

/// What happened to a file in a patch.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChangeKind {
    Modification,
    Addition,
    Deletion,
    Rename,
}

impl ChangeKind {
    /// The verb shown in the file header row.
    pub fn label(self) -> &'static str {
        match self {
            ChangeKind::Modification => "Update",
            ChangeKind::Addition => "Create",
            ChangeKind::Deletion => "Delete",
            ChangeKind::Rename => "Rename",
        }
    }
}

/// The role a single body line plays.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LineKind {
    Context,
    Added,
    Removed,
    /// A `\ No newline at end of file` marker.
    NoNewline,
    /// The trailing label on an `@@ ... @@` header.
    SectionHeading,
}

impl LineKind {
    fn marker(self) -> char {
        match self {
            LineKind::Context => ' ',
            LineKind::Added => '+',
            LineKind::Removed => '-',
            LineKind::NoNewline => '\\',
            LineKind::SectionHeading => ' ',
        }
    }

    fn role(self) -> Role {
        match self {
            LineKind::Added => Role::DiffAdded,
            LineKind::Removed => Role::DiffRemoved,
            _ => Role::DiffContext,
        }
    }

    fn background(self) -> Option<Role> {
        match self {
            LineKind::Added => Some(Role::DiffAddedBackground),
            LineKind::Removed => Some(Role::DiffRemovedBackground),
            _ => None,
        }
    }
}

/// A single line of a hunk.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DiffLine {
    pub kind: LineKind,
    pub text: String,
    /// Line number in the old file, when applicable.
    pub old_line: Option<usize>,
    /// Line number in the new file, when applicable.
    pub new_line: Option<usize>,
}

impl DiffLine {
    /// The number shown in the gutter: new-side where it exists, else old-side.
    pub fn display_line(&self) -> Option<usize> {
        self.new_line.or(self.old_line)
    }
}

/// All changes to one file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DiffFile {
    pub path: String,
    pub old_path: Option<String>,
    pub kind: ChangeKind,
    pub lines: Vec<DiffLine>,
}

impl DiffFile {
    pub fn added(&self) -> usize {
        self.lines.iter().filter(|l| l.kind == LineKind::Added).count()
    }

    pub fn removed(&self) -> usize {
        self.lines
            .iter()
            .filter(|l| l.kind == LineKind::Removed)
            .count()
    }

    /// The `Added N lines, removed M lines` summary row text.
    pub fn summary(&self) -> String {
        let added = self.added();
        let removed = self.removed();
        format!(
            "Added {added} line{}, removed {removed} line{}",
            plural(added),
            plural(removed)
        )
    }
}

fn plural(n: usize) -> &'static str {
    if n == 1 {
        ""
    } else {
        "s"
    }
}

/// A parsed patch.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Diff {
    pub files: Vec<DiffFile>,
}

impl Diff {
    pub fn is_empty(&self) -> bool {
        self.files.iter().all(|f| f.lines.is_empty())
    }
}

/// Whether text is confidently a unified diff.
///
/// Requires a real `@@ ... @@` hunk header *and* at least one parsed line, so
/// prose that happens to use leading `+`/`-` bullets is not mistaken for a patch.
pub fn looks_like_diff(text: &str) -> bool {
    if !text.lines().any(is_hunk_header) {
        return false;
    }
    !parse(text).is_empty()
}

fn is_hunk_header(line: &str) -> bool {
    let line = line.trim_end();
    line.starts_with("@@") && line[2..].contains("@@")
}

/// Splits a blob into a leading preamble and the diff that follows it.
///
/// `git show` output and model prose often precede the patch itself; the
/// preamble is preserved rather than discarded.
pub fn split_preamble(text: &str) -> (&str, &str) {
    let mut offset = 0usize;
    for line in text.split_inclusive('\n') {
        let trimmed = line.trim_end();
        if trimmed.starts_with("diff --git")
            || trimmed.starts_with("--- ")
            || trimmed.starts_with("Index: ")
        {
            return (&text[..offset], &text[offset..]);
        }
        offset += line.len();
    }
    (text, "")
}

/// Parses unified diff text.
pub fn parse(text: &str) -> Diff {
    let mut files: Vec<DiffFile> = Vec::new();
    let mut current: Option<DiffFile> = None;
    let mut old_line = 0usize;
    let mut new_line = 0usize;
    let mut in_hunk = false;

    let flush = |current: &mut Option<DiffFile>, files: &mut Vec<DiffFile>| {
        if let Some(file) = current.take() {
            files.push(file);
        }
    };

    for raw in text.lines() {
        let line = raw.strip_suffix('\r').unwrap_or(raw);

        // `diff --git`, `diff --cc` and `diff --combined` all open a new file.
        // `diff --cc` and `diff --combined` are the combined-diff formats; we
        // derive the path from the trailing word rather than from `a/`…`b/`.
        let diff_header = line
            .strip_prefix("diff --git ")
            .or_else(|| line.strip_prefix("diff --cc "))
            .or_else(|| line.strip_prefix("diff --combined "));
        if let Some(rest) = diff_header {
            flush(&mut current, &mut files);
            in_hunk = false;
            let path = if line.starts_with("diff --git ") {
                git_header_path(rest).unwrap_or_else(|| "(unknown)".to_string())
            } else {
                // Combined diff: the rest is just the filename.
                rest.trim().to_string()
            };
            current = Some(DiffFile {
                path,
                old_path: None,
                kind: ChangeKind::Modification,
                lines: Vec::new(),
            });
            continue;
        }

        if let Some(rest) = line.strip_prefix("rename from ") {
            if let Some(file) = current.as_mut() {
                file.kind = ChangeKind::Rename;
                file.old_path = Some(strip_git_prefix(rest.trim()).to_string());
            }
            continue;
        }

        if let Some(rest) = line.strip_prefix("rename to ") {
            if let Some(file) = current.as_mut() {
                file.kind = ChangeKind::Rename;
                file.path = strip_git_prefix(rest.trim()).to_string();
            }
            continue;
        }

        // `---` and `+++` are file-header lines only BEFORE a hunk opens.
        // Inside a hunk a line like `--- sql comment` is a removed body line,
        // not a file-header; processing it as a header would reset `in_hunk`
        // and silently drop the rest of the hunk.
        if !in_hunk {
            if let Some(rest) = line.strip_prefix("--- ") {
                let path = rest.trim();
                if current.is_none() {
                    current = Some(DiffFile {
                        path: String::new(),
                        old_path: None,
                        kind: ChangeKind::Modification,
                        lines: Vec::new(),
                    });
                }
                if let Some(file) = current.as_mut() {
                    if path == "/dev/null" {
                        file.kind = ChangeKind::Addition;
                    } else {
                        file.old_path = Some(strip_git_prefix(path).to_string());
                        if file.path.is_empty() {
                            file.path = strip_git_prefix(path).to_string();
                        }
                    }
                }
                continue;
            }

            if let Some(rest) = line.strip_prefix("+++ ") {
                let path = rest.trim();
                if let Some(file) = current.as_mut() {
                    if path == "/dev/null" {
                        file.kind = ChangeKind::Deletion;
                    } else {
                        let stripped = strip_git_prefix(path).to_string();
                        if file.path.is_empty() || file.path == "(unknown)" {
                            file.path = stripped;
                        }
                    }
                }
                continue;
            }

            // Metadata lines that carry information even without body lines:
            // binary file notices and mode-change stanzas. Capture them so the
            // file is not silently dropped when it has no diff hunks.
            if let Some(file) = current.as_mut() {
                if line.starts_with("Binary files")
                    || line.starts_with("old mode ")
                    || line.starts_with("new mode ")
                    || line.starts_with("new file mode ")
                    || line.starts_with("deleted file mode ")
                {
                    file.lines.push(DiffLine {
                        kind: LineKind::NoNewline,
                        text: line.to_string(),
                        old_line: None,
                        new_line: None,
                    });
                }
            }
        }

        if is_hunk_header(line) {
            in_hunk = true;
            let (old_start, new_start, heading) = parse_hunk_header(line);
            old_line = old_start;
            new_line = new_start;

            if current.is_none() {
                current = Some(DiffFile {
                    path: "(unknown)".to_string(),
                    old_path: None,
                    kind: ChangeKind::Modification,
                    lines: Vec::new(),
                });
            }
            if let Some(heading) = heading {
                if let Some(file) = current.as_mut() {
                    file.lines.push(DiffLine {
                        kind: LineKind::SectionHeading,
                        text: heading,
                        old_line: None,
                        new_line: None,
                    });
                }
            }
            continue;
        }

        if !in_hunk {
            continue;
        }

        let Some(file) = current.as_mut() else {
            continue;
        };

        let mut chars = line.chars();
        match chars.next() {
            Some('+') => {
                file.lines.push(DiffLine {
                    kind: LineKind::Added,
                    text: chars.as_str().to_string(),
                    old_line: None,
                    new_line: Some(new_line),
                });
                new_line += 1;
            }
            Some('-') => {
                file.lines.push(DiffLine {
                    kind: LineKind::Removed,
                    text: chars.as_str().to_string(),
                    old_line: Some(old_line),
                    new_line: None,
                });
                old_line += 1;
            }
            Some(' ') => {
                file.lines.push(DiffLine {
                    kind: LineKind::Context,
                    text: chars.as_str().to_string(),
                    old_line: Some(old_line),
                    new_line: Some(new_line),
                });
                old_line += 1;
                new_line += 1;
            }
            Some('\\') => {
                file.lines.push(DiffLine {
                    kind: LineKind::NoNewline,
                    text: chars.as_str().trim().to_string(),
                    old_line: None,
                    new_line: None,
                });
            }
            // A blank line inside a hunk is an empty context line.
            None => {
                file.lines.push(DiffLine {
                    kind: LineKind::Context,
                    text: String::new(),
                    old_line: Some(old_line),
                    new_line: Some(new_line),
                });
                old_line += 1;
                new_line += 1;
            }
            // Anything else ends the hunk (trailing git metadata, prose).
            Some(_) => in_hunk = false,
        }
    }

    flush(&mut current, &mut files);
    files.retain(|f| !f.lines.is_empty() || f.kind == ChangeKind::Rename);
    Diff { files }
}

/// Extracts the new-side path from a `diff --git a/x b/y` header.
fn git_header_path(rest: &str) -> Option<String> {
    let rest = rest.trim();
    // Take the b/ side, which is the post-change path.
    if let Some(index) = rest.rfind(" b/") {
        return Some(rest[index + 3..].to_string());
    }
    rest.split_whitespace()
        .next_back()
        .map(|p| strip_git_prefix(p).to_string())
}

fn strip_git_prefix(path: &str) -> &str {
    // Trailing tab-separated timestamps appear in plain `diff -u` output.
    let path = path.split('\t').next().unwrap_or(path).trim();
    path.strip_prefix("a/")
        .or_else(|| path.strip_prefix("b/"))
        .unwrap_or(path)
}

/// Parses `@@ -old,count +new,count @@ heading`.
fn parse_hunk_header(line: &str) -> (usize, usize, Option<String>) {
    let body = line.trim_start_matches('@').trim();
    let close = line[2..].find("@@").map(|i| i + 4);
    let heading = close
        .and_then(|i| line.get(i..))
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string);

    let mut old_start = 1usize;
    let mut new_start = 1usize;
    for token in body.split_whitespace() {
        if let Some(rest) = token.strip_prefix('-') {
            old_start = leading_number(rest).unwrap_or(1);
        } else if let Some(rest) = token.strip_prefix('+') {
            new_start = leading_number(rest).unwrap_or(1);
        }
    }
    (old_start, new_start, heading)
}

fn leading_number(text: &str) -> Option<usize> {
    let digits: String = text.chars().take_while(char::is_ascii_digit).collect();
    digits.parse().ok()
}

/// Renders a parsed diff into transcript rows.
///
/// When `embedded` is set the caller owns the gutter (the diff sits inside a
/// tool result), so file headers and summaries are emitted without markers.
pub fn render(diff: &Diff, viewport_width: usize, embedded: bool) -> Vec<RenderLine> {
    let mut lines = Vec::new();
    for file in &diff.files {
        render_file(file, viewport_width, embedded, &mut lines);
    }
    lines
}

fn render_file(
    file: &DiffFile,
    viewport_width: usize,
    embedded: bool,
    out: &mut Vec<RenderLine>,
) {
    // Sanitize file paths so ANSI escapes embedded in a repo path (possible
    // with a malicious repository) never reach the terminal.
    let header = match file.kind {
        ChangeKind::Rename => {
            let from = text::sanitize(file.old_path.as_deref().unwrap_or("?"));
            format!("{}({} → {})", file.kind.label(), from, text::sanitize(&file.path))
        }
        _ => format!("{}({})", file.kind.label(), text::sanitize(&file.path)),
    };

    let header_width = viewport_width.saturating_sub(if embedded { 0 } else { crate::line::MARKER_CELLS });
    for (i, text) in text::wrap(&header, header_width.max(1)).into_iter().enumerate() {
        let line = RenderLine::new(text, Role::DiffHeader);
        out.push(if embedded {
            line
        } else if i == 0 {
            line.with_gutter(Gutter::AgentComplete)
        } else {
            line.with_gutter(Gutter::Continuation)
        });
    }

    let summary_width = viewport_width.saturating_sub(if embedded { 0 } else { crate::line::CHILD_CELLS });
    for (i, text) in text::wrap(&file.summary(), summary_width.max(1))
        .into_iter()
        .enumerate()
    {
        let line = RenderLine::new(text, Role::DiffContext);
        out.push(if embedded {
            line
        } else if i == 0 {
            line.with_gutter(Gutter::LastChild)
        } else {
            line.with_gutter(Gutter::ChildContinuation)
        });
    }

    if file.lines.is_empty() {
        return;
    }

    let gutter_width = line_number_width(file);
    // Drop the number gutter entirely when the viewport cannot afford it.
    let show_numbers = viewport_width >= gutter_width + 3 + MIN_CONTENT_CELLS;
    let prefix_cells = if show_numbers { gutter_width + 3 } else { 2 };
    let content_width = viewport_width.saturating_sub(prefix_cells).max(1);

    let language = Language::from_path(&file.path);
    let mut old_side = Tokenizer::new(language);
    let mut new_side = Tokenizer::new(language);

    for diff_line in &file.lines {
        let spans = highlight(diff_line, &mut old_side, &mut new_side);
        // Sanitize body text so escape sequences in a tracked file's content
        // never reach the terminal (security invariant: untrusted content is
        // always sanitized before rendering).
        let body_text = text::sanitize(&diff_line.text);

        for (i, chunk) in text::wrap_preformatted(&body_text, content_width)
            .into_iter()
            .enumerate()
        {
            let prefix = if show_numbers {
                let number = match (i, diff_line.display_line()) {
                    (0, Some(n)) => n.to_string(),
                    _ => String::new(),
                };
                let marker = if i == 0 { diff_line.kind.marker() } else { ' ' };
                format!("{number:>gutter_width$} {marker} ")
            } else {
                let marker = if i == 0 { diff_line.kind.marker() } else { ' ' };
                format!("{marker} ")
            };

            let mut row = RenderLine::new(format!("{prefix}{chunk}"), diff_line.kind.role())
                .with_prefix(prefix_cells, Role::DiffContext);

            // Only the first visual row carries the highlight spans; they are
            // computed against the unwrapped text.
            if i == 0 && !spans.is_empty() {
                row = row.with_spans(
                    spans
                        .iter()
                        .map(|s| s.shifted(prefix_cells))
                        .filter(|s| s.start < prefix_cells + content_width)
                        .collect(),
                );
            }

            if let Some(background) = diff_line.kind.background() {
                row = row.with_fill(background);
            }

            out.push(row);
        }
    }
}

/// Highlights a body line, keeping the two file versions independent.
///
/// A construct left open on the old side must not leak into the new side, so
/// each side keeps its own tokenizer and context lines feed both.
fn highlight(
    line: &DiffLine,
    old_side: &mut Tokenizer,
    new_side: &mut Tokenizer,
) -> Vec<crate::line::Span> {
    match line.kind {
        LineKind::Added => new_side.tokenize_line(&line.text),
        LineKind::Removed => old_side.tokenize_line(&line.text),
        LineKind::Context => {
            old_side.tokenize_line(&line.text);
            new_side.tokenize_line(&line.text)
        }
        // These are not source text; flush both sides so an unterminated
        // construct cannot span a hunk boundary.
        LineKind::SectionHeading | LineKind::NoNewline => {
            old_side.reset();
            new_side.reset();
            Vec::new()
        }
    }
}

fn line_number_width(file: &DiffFile) -> usize {
    let largest = file
        .lines
        .iter()
        .filter_map(DiffLine::display_line)
        .max()
        .unwrap_or(0);
    let digits = if largest == 0 {
        1
    } else {
        (largest as f64).log10().floor() as usize + 1
    };
    digits.max(MIN_LINE_NUMBER_WIDTH)
}

/// Convenience: parse and render in one step, preserving any preamble.
pub fn render_with_preamble(text: &str, viewport_width: usize, embedded: bool) -> Vec<RenderLine> {
    let (preamble, body) = split_preamble(text);
    let mut lines = Vec::new();

    for raw in preamble.lines() {
        for chunk in text::wrap_preformatted(&text::sanitize(raw), viewport_width.max(1)) {
            lines.push(RenderLine::new(chunk, Role::Code));
        }
    }

    let diff = parse(if body.is_empty() { text } else { body });
    lines.extend(render(&diff, viewport_width, embedded));
    lines
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::text::width;

    const SIMPLE: &str = "\
diff --git a/src/main.rs b/src/main.rs
--- a/src/main.rs
+++ b/src/main.rs
@@ -1,4 +1,5 @@ fn main
 fn main() {
-    let x = 1;
+    let x = 2;
+    let y = 3;
 }
";

    #[test]
    fn parses_a_single_file_patch() {
        let diff = parse(SIMPLE);
        assert_eq!(diff.files.len(), 1);
        assert_eq!(diff.files[0].path, "src/main.rs");
        assert_eq!(diff.files[0].kind, ChangeKind::Modification);
    }

    #[test]
    fn counts_additions_and_removals() {
        let file = &parse(SIMPLE).files[0];
        assert_eq!(file.added(), 2);
        assert_eq!(file.removed(), 1);
        assert_eq!(file.summary(), "Added 2 lines, removed 1 line");
    }

    #[test]
    fn uses_singular_wording_for_one_line() {
        let diff = parse("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n");
        assert_eq!(diff.files[0].summary(), "Added 1 line, removed 1 line");
    }

    #[test]
    fn strips_git_path_prefixes() {
        let diff = parse("--- a/lib/x.rs\n+++ b/lib/x.rs\n@@ -1 +1 @@\n-a\n+b\n");
        assert_eq!(diff.files[0].path, "lib/x.rs");
    }

    #[test]
    fn captures_a_hunk_section_heading() {
        let file = &parse(SIMPLE).files[0];
        assert_eq!(file.lines[0].kind, LineKind::SectionHeading);
        assert_eq!(file.lines[0].text, "fn main");
    }

    #[test]
    fn numbers_lines_from_the_hunk_header() {
        let file = &parse(SIMPLE).files[0];
        let body: Vec<_> = file
            .lines
            .iter()
            .filter(|l| l.kind != LineKind::SectionHeading)
            .collect();

        assert_eq!(body[0].kind, LineKind::Context);
        assert_eq!(body[0].new_line, Some(1));
        assert_eq!(body[1].kind, LineKind::Removed);
        assert_eq!(body[1].old_line, Some(2));
        assert_eq!(body[2].kind, LineKind::Added);
        assert_eq!(body[2].new_line, Some(2));
        assert_eq!(body[3].new_line, Some(3));
    }

    #[test]
    fn detects_a_file_creation() {
        let diff = parse("--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1 @@\n+hello\n");
        assert_eq!(diff.files[0].kind, ChangeKind::Addition);
        assert_eq!(diff.files[0].path, "new.txt");
        assert_eq!(diff.files[0].kind.label(), "Create");
    }

    #[test]
    fn detects_a_file_deletion() {
        let diff = parse("--- a/old.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-gone\n");
        assert_eq!(diff.files[0].kind, ChangeKind::Deletion);
        assert_eq!(diff.files[0].kind.label(), "Delete");
    }

    #[test]
    fn detects_a_rename() {
        let diff = parse(
            "diff --git a/old.rs b/new.rs\nrename from old.rs\nrename to new.rs\n@@ -1 +1 @@\n-a\n+b\n",
        );
        assert_eq!(diff.files[0].kind, ChangeKind::Rename);
        assert_eq!(diff.files[0].path, "new.rs");
        assert_eq!(diff.files[0].old_path.as_deref(), Some("old.rs"));
    }

    #[test]
    fn parses_multiple_files() {
        let diff = parse(
            "diff --git a/a.rs b/a.rs\n@@ -1 +1 @@\n-a\n+b\ndiff --git a/c.rs b/c.rs\n@@ -1 +1 @@\n-c\n+d\n",
        );
        assert_eq!(diff.files.len(), 2);
        assert_eq!(diff.files[0].path, "a.rs");
        assert_eq!(diff.files[1].path, "c.rs");
    }

    #[test]
    fn records_a_no_newline_marker() {
        let diff = parse("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n\\ No newline at end of file\n+b\n");
        assert!(diff.files[0]
            .lines
            .iter()
            .any(|l| l.kind == LineKind::NoNewline));
    }

    #[test]
    fn treats_a_blank_line_in_a_hunk_as_empty_context() {
        let diff = parse("--- a/x\n+++ b/x\n@@ -1,2 +1,2 @@\n a\n\n");
        let blank = diff.files[0].lines.last().expect("a line");
        assert_eq!(blank.kind, LineKind::Context);
        assert!(blank.text.is_empty());
    }

    #[test]
    fn recognises_real_diffs() {
        assert!(looks_like_diff(SIMPLE));
    }

    #[test]
    fn does_not_mistake_prose_bullets_for_a_diff() {
        let prose = "Changes:\n- removed the old path\n+ added a new one\n";
        assert!(!looks_like_diff(prose));
    }

    #[test]
    fn does_not_mistake_a_bare_hunk_marker_for_a_diff() {
        assert!(!looks_like_diff("see @@ this @@ marker in prose"));
    }

    #[test]
    fn splits_a_preamble_from_the_patch() {
        let text = "commit abc123\nAuthor: someone\n\ndiff --git a/x b/x\n@@ -1 +1 @@\n-a\n+b\n";
        let (preamble, body) = split_preamble(text);
        assert!(preamble.starts_with("commit abc123"));
        assert!(body.starts_with("diff --git"));
    }

    #[test]
    fn returns_the_whole_text_as_preamble_when_there_is_no_patch() {
        let (preamble, body) = split_preamble("just some prose\n");
        assert_eq!(preamble, "just some prose\n");
        assert!(body.is_empty());
    }

    #[test]
    fn renders_a_header_and_summary_row() {
        let rows = render(&parse(SIMPLE), 80, true);
        assert_eq!(rows[0].text, "Update(src/main.rs)");
        assert_eq!(rows[0].role, Role::DiffHeader);
        assert_eq!(rows[1].text, "Added 2 lines, removed 1 line");
    }

    #[test]
    fn renders_gutter_markers_when_not_embedded() {
        let rows = render(&parse(SIMPLE), 80, false);
        assert_eq!(rows[0].gutter, Gutter::AgentComplete);
        assert_eq!(rows[1].gutter, Gutter::LastChild);
    }

    #[test]
    fn fills_the_width_for_added_and_removed_rows_only() {
        let rows = render(&parse(SIMPLE), 80, true);
        let added = rows
            .iter()
            .find(|r| r.role == Role::DiffAdded)
            .expect("an added row");
        assert!(added.fill_width);
        assert_eq!(added.background, Some(Role::DiffAddedBackground));

        let context = rows
            .iter()
            .find(|r| r.role == Role::DiffContext && r.text.contains("fn main()"))
            .expect("a context row");
        assert!(!context.fill_width);
    }

    #[test]
    fn draws_line_numbers_and_markers_in_the_gutter() {
        let rows = render(&parse(SIMPLE), 80, true);
        let added = rows
            .iter()
            .find(|r| r.role == Role::DiffAdded)
            .expect("an added row");
        // Three-cell number field, then a space, the marker and a space.
        assert!(added.text.starts_with("  2 + "), "got {:?}", added.text);
        assert_eq!(added.prefix_cells, 6);
        assert_eq!(added.prefix_role, Some(Role::DiffContext));
    }

    #[test]
    fn drops_the_number_gutter_in_a_very_narrow_viewport() {
        let rows = render(&parse(SIMPLE), 8, true);
        let added = rows
            .iter()
            .find(|r| r.role == Role::DiffAdded)
            .expect("an added row");
        assert!(added.text.starts_with("+ "), "got {:?}", added.text);
        assert_eq!(added.prefix_cells, 2);
    }

    #[test]
    fn widens_the_number_gutter_for_large_line_numbers() {
        let diff = parse("--- a/x\n+++ b/x\n@@ -1,1 +12345,1 @@\n+deep\n");
        let rows = render(&diff, 80, true);
        let added = rows
            .iter()
            .find(|r| r.role == Role::DiffAdded)
            .expect("an added row");
        assert!(added.text.starts_with("12345 + "), "got {:?}", added.text);
    }

    #[test]
    fn no_rendered_row_exceeds_the_viewport() {
        for viewport in [10usize, 20, 40, 80] {
            for row in render(&parse(SIMPLE), viewport, true) {
                assert!(
                    width(&row.text) <= viewport,
                    "row {:?} exceeds {viewport}",
                    row.text
                );
            }
        }
    }

    #[test]
    fn applies_syntax_highlighting_to_body_rows() {
        let rows = render(&parse(SIMPLE), 80, true);
        let added = rows
            .iter()
            .find(|r| r.role == Role::DiffAdded)
            .expect("an added row");
        assert!(
            added.spans.iter().any(|s| s.role == Role::SyntaxKeyword),
            "expected `let` to be highlighted, got {:?}",
            added.spans
        );
    }

    #[test]
    fn highlight_spans_start_after_the_line_number_gutter() {
        let rows = render(&parse(SIMPLE), 80, true);
        let added = rows
            .iter()
            .find(|r| r.role == Role::DiffAdded)
            .expect("an added row");
        for span in &added.spans {
            assert!(span.start >= added.prefix_cells, "span {span:?} intrudes");
        }
    }

    #[test]
    fn renders_a_preamble_before_the_patch() {
        let text = "commit abc\n\ndiff --git a/x.rs b/x.rs\n@@ -1 +1 @@\n-a\n+b\n";
        let rows = render_with_preamble(text, 80, true);
        assert!(rows[0].text.contains("commit abc"));
        assert!(rows.iter().any(|r| r.text.contains("Update(x.rs)")));
    }

    #[test]
    fn an_empty_diff_reports_itself_as_empty() {
        assert!(parse("").is_empty());
        assert!(parse("no diff content here").is_empty());
    }

    #[test]
    fn never_panics_on_malformed_headers() {
        for sample in [
            "@@",
            "@@ @@",
            "@@ -x +y @@",
            "--- \n+++ \n@@ -1 +1 @@\n+a\n",
            "diff --git\n",
            "diff --git a/ b/\n@@ -1 +1 @@\n+a\n",
        ] {
            let _ = render(&parse(sample), 40, false);
        }
    }

    // ── Regression fixes ───────────────────────────────────────────────────

    #[test]
    fn a_body_line_starting_with_three_dashes_is_not_treated_as_a_file_header() {
        // "--- some comment" inside a hunk is a removed body line (e.g. SQL
        // comments or Haskell source). It must not reset in_hunk, which would
        // drop every subsequent line in the hunk.
        let patch = concat!(
            "diff --git a/q.sql b/q.sql\n",
            "--- a/q.sql\n",
            "+++ b/q.sql\n",
            "@@ -1,3 +1,3 @@\n",
            " SELECT 1;\n",
            "--- a comment that stays in the hunk\n",
            " SELECT 2;\n"
        );
        let files = parse(patch);
        let file = &files.files[0];
        assert_eq!(file.path, "q.sql");
        // 3 body lines: context, removed (the comment), context
        assert_eq!(file.lines.len(), 3, "lines: {:?}", file.lines);
        assert_eq!(file.lines[1].kind, LineKind::Removed);
        assert_eq!(file.lines[1].text, "-- a comment that stays in the hunk");
    }

    #[test]
    fn a_body_line_starting_with_three_pluses_is_not_treated_as_a_file_header() {
        let patch = concat!(
            "diff --git a/f.c b/f.c\n",
            "--- a/f.c\n",
            "+++ b/f.c\n",
            "@@ -1,2 +1,2 @@\n",
            " unchanged;\n",
            "++++ increment;\n"
        );
        let files = parse(patch);
        let file = &files.files[0];
        assert_eq!(file.lines.len(), 2, "lines: {:?}", file.lines);
        assert_eq!(file.lines[1].kind, LineKind::Added);
        assert_eq!(file.lines[1].text, "+++ increment;");
    }

    #[test]
    fn trailing_newline_does_not_create_extra_context_line() {
        // Real `git diff` output always ends with \n; the final empty segment
        // must not become a spurious blank context line.
        let patch = concat!(
            "diff --git a/a.txt b/a.txt\n",
            "--- a/a.txt\n",
            "+++ b/a.txt\n",
            "@@ -1,1 +1,1 @@\n",
            "-old\n",
            "+new\n"
        );
        let file = &parse(patch).files[0];
        assert_eq!(file.lines.len(), 2, "got {:?}", file.lines);
        assert!(!file.lines.iter().any(|l| l.kind == LineKind::Context));
    }

    #[test]
    fn binary_stanza_reports_the_file() {
        let patch = concat!(
            "diff --git a/img.png b/img.png\n",
            "index 1234567..89abcde 100644\n",
            "Binary files a/img.png and b/img.png differ\n"
        );
        let diff = parse(patch);
        assert_eq!(diff.files.len(), 1);
        let file = &diff.files[0];
        assert_eq!(file.path, "img.png");
        assert!(
            file.lines.iter().any(|l| l.text.contains("Binary")),
            "expected Binary line; got: {:?}",
            file.lines
        );
    }

    #[test]
    fn mode_only_stanza_reports_the_file() {
        let patch = concat!(
            "diff --git a/run.sh b/run.sh\n",
            "old mode 100644\n",
            "new mode 100755\n"
        );
        let diff = parse(patch);
        assert_eq!(diff.files.len(), 1);
        let file = &diff.files[0];
        assert_eq!(file.path, "run.sh");
        assert_eq!(file.lines.len(), 2, "expected old+new mode lines");
    }

    #[test]
    fn combined_diff_cc_header_is_recognized() {
        let patch = concat!(
            "diff --cc f.txt\n",
            "--- a/f.txt\n",
            "+++ b/f.txt\n",
            "@@@ -1,1 -1,1 +1,2 @@@\n",
            "++<<<<<<< HEAD\n",
            " +ours\n"
        );
        let diff = parse(patch);
        assert_eq!(diff.files.len(), 1);
        let file = &diff.files[0];
        assert!(!file.lines.is_empty());
        assert!(
            file.lines.iter().any(|l| l.text.contains("<<<<<<<")),
            "expected merge conflict marker; got: {:?}",
            file.lines
        );
    }

    #[test]
    fn ansi_escapes_in_diff_body_are_stripped_at_render_time() {
        // A file whose content contains ANSI escape sequences (possible in any
        // tracked source file) must not leak those sequences to the terminal.
        let patch = concat!(
            "diff --git a/evil.txt b/evil.txt\n",
            "--- a/evil.txt\n",
            "+++ b/evil.txt\n",
            "@@ -1 +1 @@\n",
            "-clean\n",
            "+payload\u{1b}[2Jmore\n"
        );
        let rows = render(&parse(patch), 80, false);
        for row in &rows {
            assert!(
                !row.text.contains('\u{1b}'),
                "ANSI escape leaked into row: {:?}",
                row.text
            );
        }
    }

    #[test]
    fn ansi_escapes_in_a_file_path_are_stripped_at_render_time() {
        let patch = concat!(
            "diff --git a/ev\u{1b}[2Jil.txt b/ev\u{1b}[2Jil.txt\n",
            "--- a/ev\u{1b}[2Jil.txt\n",
            "+++ b/ev\u{1b}[2Jil.txt\n",
            "@@ -1 +1 @@\n",
            "-old\n",
            "+new\n"
        );
        let rows = render(&parse(patch), 80, false);
        for row in &rows {
            assert!(
                !row.text.contains('\u{1b}'),
                "ANSI escape leaked into row: {:?}",
                row.text
            );
        }
    }
}

