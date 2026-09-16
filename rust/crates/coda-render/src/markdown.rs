//! Markdown rendering into transcript rows.
//!
//! Assistant output is markdown, but a terminal transcript is a list of styled
//! rows. This module walks the CommonMark event stream and produces rows whose
//! colour comes from the block context, matching the C# formatter: inline
//! emphasis is *stripped* rather than styled, and only structural elements
//! (headings, code, callouts, links) change colour.

use pulldown_cmark::{CodeBlockKind, Event, Options, Parser, Tag, TagEnd};

use crate::diff;
use crate::line::{LinkSpan, RenderLine, Span};
use crate::syntax::{Language, Tokenizer};
use crate::text;
use crate::theme::Role;

/// A GitHub-style admonition.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Callout {
    Note,
    Tip,
    Important,
    Warning,
    Caution,
}

impl Callout {
    fn parse(marker: &str) -> Option<Callout> {
        match marker.trim().to_ascii_uppercase().as_str() {
            "[!NOTE]" => Some(Callout::Note),
            "[!TIP]" => Some(Callout::Tip),
            "[!IMPORTANT]" => Some(Callout::Important),
            "[!WARNING]" => Some(Callout::Warning),
            "[!CAUTION]" => Some(Callout::Caution),
            _ => None,
        }
    }

    fn glyph(self) -> &'static str {
        match self {
            Callout::Note => "\u{2139}",      // ℹ
            Callout::Tip => "\u{2726}",       // ✦
            Callout::Important => "\u{203C}", // ‼
            Callout::Warning => "\u{26A0}",   // ⚠
            Callout::Caution => "\u{2297}",   // ⊗
        }
    }

    fn label(self) -> &'static str {
        match self {
            Callout::Note => "NOTE",
            Callout::Tip => "TIP",
            Callout::Important => "IMPORTANT",
            Callout::Warning => "WARNING",
            Callout::Caution => "CAUTION",
        }
    }

    fn role(self) -> Role {
        match self {
            Callout::Note => Role::CalloutNote,
            Callout::Tip => Role::CalloutTip,
            Callout::Important => Role::CalloutImportant,
            Callout::Warning => Role::CalloutWarning,
            Callout::Caution => Role::CalloutCaution,
        }
    }
}

/// A link discovered while accumulating inline text.
///
/// Records both what the link *is* (its `url`) and how it must look (the
/// [`Role`] its display text takes — [`Role::Link`], or [`Role::LinkDeceptive`]
/// when the text lies about where it leads). `start`/`end` are `char` offsets
/// into the paragraph buffer; they turn into cell coordinates only once the row
/// they fall on is known, because that is the first point a wide glyph's true
/// width can be measured.
#[derive(Debug, Clone)]
struct InlineLink {
    start: usize,
    end: usize,
    role: Role,
    url: String,
}

/// Accumulated inline text plus the link ranges found inside it.
#[derive(Debug, Default)]
struct InlineBuffer {
    text: String,
    links: Vec<InlineLink>,
}

impl InlineBuffer {
    fn push(&mut self, text: &str) {
        self.text.push_str(text);
    }

    fn is_blank(&self) -> bool {
        self.text.trim().is_empty()
    }

    fn take(&mut self) -> (String, Vec<InlineLink>) {
        (
            std::mem::take(&mut self.text),
            std::mem::take(&mut self.links),
        )
    }
}

/// Renders markdown into rows wrapped to `width` cells.
pub fn render(markdown: &str, width: usize) -> Vec<RenderLine> {
    Renderer::new(width).run(markdown)
}

struct Renderer {
    width: usize,
    out: Vec<RenderLine>,
    inline: InlineBuffer,
    /// Current left indent in cells.
    indent: usize,
    /// One entry per open list: `Some(next_number)` for ordered lists.
    lists: Vec<Option<u64>>,
    /// Set when a list item has started but its marker is not yet emitted.
    pending_marker: Option<String>,
    /// Open fenced code block and its language.
    code_block: Option<Language>,
    code_buffer: String,
    /// Set when the open fence was tagged `diff`/`patch`.
    code_is_diff: bool,
    /// Active callout and its nesting depth in blockquotes.
    callout: Option<Callout>,
    quote_depth: usize,
    /// Role for the current text run.
    role: Role,
    /// True while collecting a heading.
    in_heading: bool,
    /// Link destination currently being collected, for deception checks.
    link_target: Option<String>,
    link_start: Option<usize>,
    in_image: bool,
    /// Suppresses the blank line before the very first block.
    wrote_any: bool,
}

impl Renderer {
    fn new(width: usize) -> Self {
        Self {
            width: width.max(1),
            out: Vec::new(),
            inline: InlineBuffer::default(),
            indent: 0,
            lists: Vec::new(),
            pending_marker: None,
            code_block: None,
            code_buffer: String::new(),
            code_is_diff: false,
            callout: None,
            quote_depth: 0,
            role: Role::Assistant,
            in_heading: false,
            link_target: None,
            link_start: None,
            in_image: false,
            wrote_any: false,
        }
    }

    fn run(mut self, markdown: &str) -> Vec<RenderLine> {
        let mut options = Options::empty();
        options.insert(Options::ENABLE_TABLES);
        options.insert(Options::ENABLE_STRIKETHROUGH);

        for event in Parser::new_ext(markdown, options) {
            self.handle(event);
        }
        self.flush_inline();
        self.out
    }

    fn handle(&mut self, event: Event) {
        match event {
            Event::Start(tag) => self.start(tag),
            Event::End(tag) => self.end(tag),
            Event::Text(text) => {
                if self.code_block.is_some() {
                    self.code_buffer.push_str(&text);
                } else {
                    self.inline.push(&text);
                }
            }
            // Inline code carries no distinct colour in the C# formatter; only
            // its content is emitted.
            Event::Code(code) => self.inline.push(&code),
            Event::SoftBreak => self.inline.push(" "),
            Event::HardBreak => {
                self.flush_inline();
            }
            Event::Rule => {
                self.blank_line();
                let rule = "\u{2500}".repeat(self.available_width().min(self.width));
                self.push_row(rule, Role::DiffContext, Vec::new());
            }
            Event::Html(html) | Event::InlineHtml(html) => {
                // Raw HTML is shown literally rather than interpreted.
                self.inline.push(&html);
            }
            Event::TaskListMarker(checked) => {
                self.inline.push(if checked { "[x] " } else { "[ ] " });
            }
            Event::FootnoteReference(name) => {
                self.inline.push(&format!("[^{name}]"));
            }
            Event::InlineMath(math) | Event::DisplayMath(math) => self.inline.push(&math),
        }
    }

    fn start(&mut self, tag: Tag) {
        match tag {
            Tag::Paragraph => {
                self.blank_line();
            }
            Tag::Heading { level, .. } => {
                self.blank_line();
                self.in_heading = true;
                self.role = Role::Heading;
                let _ = level;
            }
            Tag::BlockQuote(_) => {
                self.quote_depth += 1;
                self.blank_line();
            }
            Tag::CodeBlock(kind) => {
                self.blank_line();
                let language = match &kind {
                    CodeBlockKind::Fenced(info) => Language::from_info_string(info),
                    CodeBlockKind::Indented => Language::None,
                };
                // Remember the raw info string so a `diff` fence can be routed
                // to the diff renderer instead of the syntax highlighter.
                if let CodeBlockKind::Fenced(info) = &kind {
                    let info = info.trim().to_ascii_lowercase();
                    if info == "diff" || info == "patch" {
                        self.code_block = Some(Language::None);
                        self.code_buffer.clear();
                        self.code_is_diff = true;
                        return;
                    }
                }
                self.code_is_diff = false;
                self.code_block = Some(language);
                self.code_buffer.clear();
            }
            Tag::List(start) => {
                if self.lists.is_empty() {
                    self.blank_line();
                }
                self.lists.push(start);
            }
            Tag::Item => {
                let marker = match self.lists.last_mut() {
                    Some(Some(number)) => {
                        let marker = format!("{number}. ");
                        *number += 1;
                        marker
                    }
                    _ => "\u{2022} ".to_string(), // •
                };
                self.pending_marker = Some(marker);
            }
            Tag::Link { dest_url, .. } => {
                self.link_target = Some(dest_url.to_string());
                self.link_start = Some(self.inline.text.chars().count());
            }
            Tag::Image { .. } => {
                self.in_image = true;
            }
            Tag::Table(_) | Tag::TableHead | Tag::TableRow => {
                self.flush_inline();
            }
            Tag::TableCell => {
                if !self.inline.text.is_empty() {
                    self.inline.push(" | ");
                }
            }
            // Emphasis carries no terminal styling; only its text is kept.
            Tag::Emphasis
            | Tag::Strong
            | Tag::Strikethrough
            | Tag::Superscript
            | Tag::Subscript
            | Tag::HtmlBlock
            | Tag::FootnoteDefinition(_)
            | Tag::MetadataBlock(_)
            | Tag::DefinitionList
            | Tag::DefinitionListTitle
            | Tag::DefinitionListDefinition => {}
        }
    }

    fn end(&mut self, tag: TagEnd) {
        match tag {
            TagEnd::Paragraph => self.flush_inline(),
            TagEnd::Heading(_) => {
                self.flush_inline();
                self.in_heading = false;
                self.role = Role::Assistant;
            }
            TagEnd::BlockQuote(_) => {
                self.flush_inline();
                self.quote_depth = self.quote_depth.saturating_sub(1);
                if self.quote_depth == 0 {
                    self.callout = None;
                }
            }
            TagEnd::CodeBlock => self.flush_code_block(),
            TagEnd::List(_) => {
                self.flush_inline();
                self.lists.pop();
            }
            TagEnd::Item => {
                self.flush_inline();
                self.pending_marker = None;
            }
            TagEnd::Link => {
                self.close_link();
            }
            TagEnd::Image => {
                self.in_image = false;
            }
            TagEnd::TableHead => {
                self.flush_table_row(Role::Heading);
                let rule = "\u{2500}".repeat(self.available_width().min(self.width));
                self.push_row(rule, Role::DiffContext, Vec::new());
            }
            TagEnd::TableRow => self.flush_table_row(Role::Assistant),
            _ => {}
        }
    }

    /// Records the link's colour span and destination, flagging deceptive
    /// display text.
    fn close_link(&mut self) {
        let (Some(target), Some(start)) = (self.link_target.take(), self.link_start.take()) else {
            return;
        };
        if self.in_image {
            return;
        }

        let end = self.inline.text.chars().count();
        if end <= start {
            return;
        }

        let display: String = self
            .inline
            .text
            .chars()
            .skip(start)
            .take(end - start)
            .collect();

        if link_text_matches(&display, &target) {
            self.inline.links.push(InlineLink {
                start,
                end,
                role: Role::Link,
                url: target,
            });
        } else {
            // The display text claims to be a different destination; mark it,
            // and let the warning glyph belong to the link so a click anywhere
            // on the flagged run still resolves to the real target.
            self.inline.push("\u{26A0}"); // ⚠
            self.inline.links.push(InlineLink {
                start,
                end: end + 1,
                role: Role::LinkDeceptive,
                url: target,
            });
        }
    }

    /// Cells available for content at the current indent.
    fn available_width(&self) -> usize {
        let bar = if self.callout.is_some() { 2 } else { 0 };
        self.width.saturating_sub(self.indent + bar).max(1)
    }

    fn blank_line(&mut self) {
        self.flush_inline();
        if self.wrote_any && !matches!(self.out.last(), Some(l) if l.text.trim().is_empty()) {
            self.out.push(RenderLine::new(String::new(), Role::Assistant));
        }
    }

    /// Emits the buffered inline text as wrapped rows.
    fn flush_inline(&mut self) {
        if self.inline.is_blank() {
            self.inline.take();
            return;
        }

        let (text, links) = self.inline.take();

        // A blockquote whose first line is `[!NOTE]` becomes a callout.
        if self.quote_depth > 0 && self.callout.is_none() {
            if let Some(callout) = first_token(&text).and_then(Callout::parse) {
                // The title row carries the glyph and label but not the bar;
                // only body rows are prefixed.
                let title = format!("{} {}", callout.glyph(), callout.label());
                for chunk in text::wrap(&title, self.available_width()) {
                    self.push_row(chunk, callout.role(), Vec::new());
                }

                self.callout = Some(callout);

                let rest = text[first_token_len(&text)..].trim().to_string();
                if rest.is_empty() {
                    return;
                }
                self.emit_wrapped(&rest, Vec::new());
                return;
            }
        }

        self.emit_wrapped(&text, links);
    }

    fn emit_wrapped(&mut self, text: &str, links: Vec<InlineLink>) {
        // Sanitize prose text before wrapping. Model output can contain
        // adversarial control characters and ANSI escape sequences; the draw
        // layer must never receive them. Code blocks are already sanitized in
        // flush_code_block(); this closes the same gap for all inline prose
        // that flows through flush_inline → emit_wrapped.
        let sanitized = text::sanitize(text);
        let text = sanitized.as_str();

        let marker = self.pending_marker.take();
        let marker_width = marker.as_deref().map(text::width).unwrap_or(0);
        let budget = self.available_width().saturating_sub(marker_width).max(1);

        let wrapped = text::wrap(text.trim(), budget);
        let mut consumed = 0usize;

        for (i, chunk) in wrapped.iter().enumerate() {
            let lead = match (&marker, i) {
                (Some(marker), 0) => marker.clone(),
                (Some(_), _) => " ".repeat(marker_width),
                (None, _) => String::new(),
            };

            let offset = text::width(&lead);
            let (spans, link_spans) = row_link_spans(chunk, &links, consumed, offset);
            consumed += chunk.chars().count() + 1; // account for the split space
            self.push_row_with_links(format!("{lead}{chunk}"), self.role, spans, link_spans);
        }
    }

    fn flush_table_row(&mut self, role: Role) {
        if self.inline.is_blank() {
            self.inline.take();
            return;
        }
        let (text, _) = self.inline.take();
        let previous = self.role;
        self.role = role;
        for chunk in text::wrap(text.trim(), self.available_width()) {
            self.push_row(chunk, role, Vec::new());
        }
        self.role = previous;
    }

    fn flush_code_block(&mut self) {
        let Some(language) = self.code_block.take() else {
            return;
        };
        let body = std::mem::take(&mut self.code_buffer);

        // A ```diff fence renders as a real diff when it parses as one.
        if self.code_is_diff && diff::looks_like_diff(&body) {
            let rows = diff::render_with_preamble(&body, self.available_width(), true);
            for row in rows {
                self.out.push(row);
                self.wrote_any = true;
            }
            return;
        }

        let mut tokenizer = Tokenizer::new(language);
        for source_line in body.lines() {
            let sanitized = text::sanitize(source_line);
            let spans = tokenizer.tokenize_line(&sanitized);
            let chunks = text::wrap_preformatted(&sanitized, self.available_width());

            for (i, chunk) in chunks.into_iter().enumerate() {
                // Highlight spans are computed against the unwrapped line, so
                // only the first visual row can carry them.
                let spans = if i == 0 { spans.clone() } else { Vec::new() };
                self.push_row(chunk, Role::Code, spans);
            }
        }
    }

    fn push_row(&mut self, text: String, role: Role, spans: Vec<Span>) {
        self.push_row_with_links(text, role, spans, Vec::new());
    }

    fn push_row_with_links(
        &mut self,
        text: String,
        role: Role,
        spans: Vec<Span>,
        links: Vec<LinkSpan>,
    ) {
        let indent = " ".repeat(self.indent);
        let (prefix, prefix_cells, prefix_role) = match self.callout {
            Some(callout) => (
                format!("{indent}\u{2502} "), // │
                self.indent + 2,
                Some(callout.role()),
            ),
            None => (indent, self.indent, None),
        };

        let shift = text::width(&prefix);
        let mut line = RenderLine::new(format!("{prefix}{text}"), role)
            .with_spans(spans.iter().map(|s| s.shifted(shift)).collect())
            .with_links(links.iter().map(|l| l.shifted(shift)).collect());

        if let Some(prefix_role) = prefix_role {
            line = line.with_prefix(prefix_cells, prefix_role);
        }

        self.out.push(line);
        self.wrote_any = true;
    }
}

/// Whether a link's display text honestly represents its destination.
fn link_text_matches(display: &str, target: &str) -> bool {
    let display = display.trim();
    if display.eq_ignore_ascii_case(target) {
        return true;
    }
    // `[example.com](https://example.com/path)` is honest: the text names the host.
    match host_of(target) {
        Some(host) => {
            display.eq_ignore_ascii_case(host)
                || display.eq_ignore_ascii_case(host.trim_start_matches("www."))
        }
        // Relative links, anchors and mailto: are not spoofing a host.
        None => !display.contains("://"),
    }
}

fn host_of(url: &str) -> Option<&str> {
    let rest = url.split_once("://")?.1;
    let host = rest.split(['/', '?', '#']).next()?;
    (!host.is_empty()).then_some(host)
}

fn first_token(text: &str) -> Option<&str> {
    text.trim_start().split_whitespace().next()
}

fn first_token_len(text: &str) -> usize {
    let trimmed = text.trim_start();
    let leading = text.len() - trimmed.len();
    leading + trimmed.split_whitespace().next().map_or(0, str::len)
}

/// Maps link ranges (whole-paragraph `char` offsets) onto one wrapped row, in
/// terminal **cell** coordinates.
///
/// Two coordinate systems meet here. A link is recorded in `char`s while the
/// paragraph is accumulated, but a drawn row is addressed in cells, and a CJK
/// glyph occupies two of them. Converting through the row's own text — summing
/// real glyph widths up to each boundary rather than assuming one char is one
/// cell — is what keeps a link's colour and its clickable range sitting exactly
/// under the text the reader sees, however wide that text is.
///
/// Returns the colour spans and the clickable link spans together because they
/// are derived from the same clip and must never disagree about where a link
/// begins and ends.
fn row_link_spans(
    chunk: &str,
    links: &[InlineLink],
    row_start_char: usize,
    offset_cells: usize,
) -> (Vec<Span>, Vec<LinkSpan>) {
    let row_len_char = chunk.chars().count();
    let row_end_char = row_start_char + row_len_char;

    // Cell offset at each char boundary of the chunk: `cells[i]` is the display
    // width of the first `i` chars. Measured on growing prefixes rather than by
    // summing per-char widths, so a zero-width combining mark stays attached to
    // the base glyph it modifies instead of being miscounted on its own.
    let mut cells = Vec::with_capacity(row_len_char + 1);
    cells.push(0usize);
    for (byte, ch) in chunk.char_indices() {
        cells.push(text::width(&chunk[..byte + ch.len_utf8()]));
    }

    let mut color = Vec::new();
    let mut clickable = Vec::new();
    for link in links {
        let clipped_start = link.start.max(row_start_char);
        let clipped_end = link.end.min(row_end_char);
        if clipped_end <= clipped_start {
            continue;
        }
        let start = cells[clipped_start - row_start_char] + offset_cells;
        let end = cells[clipped_end - row_start_char] + offset_cells;
        color.push(Span::new(start, end, link.role));
        clickable.push(LinkSpan::new(start, end, link.url.clone()));
    }
    (color, clickable)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn texts(markdown: &str, width: usize) -> Vec<String> {
        render(markdown, width)
            .into_iter()
            .map(|l| l.text)
            .collect()
    }

    fn non_blank(markdown: &str, width: usize) -> Vec<String> {
        texts(markdown, width)
            .into_iter()
            .filter(|t| !t.trim().is_empty())
            .collect()
    }

    #[test]
    fn renders_a_plain_paragraph() {
        assert_eq!(non_blank("hello world", 40), vec!["hello world"]);
    }

    #[test]
    fn wraps_a_paragraph_to_the_width() {
        let rows = non_blank("alpha beta gamma delta epsilon", 12);
        for row in &rows {
            assert!(text::width(row) <= 12, "row {row:?} too wide");
        }
        assert!(rows.len() > 1);
    }

    #[test]
    fn strips_inline_emphasis_but_keeps_its_text() {
        assert_eq!(
            non_blank("**bold** and *italic* and ~~struck~~", 60),
            vec!["bold and italic and struck"]
        );
    }

    #[test]
    fn keeps_inline_code_text_without_backticks() {
        assert_eq!(non_blank("use `cargo test` now", 60), vec!["use cargo test now"]);
    }

    #[test]
    fn renders_headings_in_the_heading_role() {
        let rows = render("# Title\n\nbody", 40);
        let heading = rows.iter().find(|r| r.text.contains("Title")).expect("heading");
        assert_eq!(heading.role, Role::Heading);
        assert!(!heading.text.contains('#'), "heading kept its marker");
    }

    #[test]
    fn renders_body_text_in_the_assistant_role() {
        let rows = render("# Title\n\nbody text", 40);
        let body = rows.iter().find(|r| r.text.contains("body")).expect("body");
        assert_eq!(body.role, Role::Assistant);
    }

    #[test]
    fn renders_unordered_list_bullets() {
        let rows = non_blank("- one\n- two", 40);
        assert_eq!(rows, vec!["\u{2022} one", "\u{2022} two"]);
    }

    #[test]
    fn renders_ordered_list_numbers_from_their_start() {
        let rows = non_blank("3. three\n4. four", 40);
        assert_eq!(rows, vec!["3. three", "4. four"]);
    }

    #[test]
    fn aligns_wrapped_list_text_under_the_first_character() {
        let rows = non_blank("- alpha beta gamma delta", 12);
        assert!(rows[0].starts_with("\u{2022} "));
        for row in &rows[1..] {
            assert!(row.starts_with("  "), "continuation {row:?} not aligned");
        }
    }

    #[test]
    fn renders_a_fenced_code_block_in_the_code_role() {
        let rows = render("```rust\nlet x = 1;\n```", 40);
        let code = rows.iter().find(|r| r.text.contains("let x")).expect("code");
        assert_eq!(code.role, Role::Code);
    }

    #[test]
    fn highlights_a_fenced_code_block() {
        let rows = render("```rust\nlet x = 1;\n```", 40);
        let code = rows.iter().find(|r| r.text.contains("let x")).expect("code");
        assert!(code.spans.iter().any(|s| s.role == Role::SyntaxKeyword));
    }

    #[test]
    fn does_not_highlight_a_code_block_without_a_language() {
        let rows = render("```\nlet x = 1;\n```", 40);
        let code = rows.iter().find(|r| r.text.contains("let x")).expect("code");
        assert!(code.spans.is_empty());
    }

    #[test]
    fn preserves_indentation_inside_a_code_block() {
        let rows = render("```rust\n    indented\n```", 40);
        assert!(rows.iter().any(|r| r.text.starts_with("    indented")));
    }

    #[test]
    fn renders_a_diff_fence_as_a_real_diff() {
        let markdown = "```diff\n--- a/x.rs\n+++ b/x.rs\n@@ -1 +1 @@\n-old\n+new\n```";
        let rows = render(markdown, 60);
        assert!(rows.iter().any(|r| r.text.contains("Update(x.rs)")));
        assert!(rows.iter().any(|r| r.role == Role::DiffAdded));
    }

    #[test]
    fn falls_back_to_code_when_a_diff_fence_is_not_a_diff() {
        let rows = render("```diff\njust text\n```", 40);
        assert!(rows.iter().any(|r| r.role == Role::Code));
        assert!(!rows.iter().any(|r| r.role == Role::DiffAdded));
    }

    #[test]
    fn renders_a_note_callout_with_its_glyph_and_bar() {
        let rows = render("> [!NOTE]\n> remember this", 40);
        let title = rows.iter().find(|r| r.text.contains("NOTE")).expect("title");
        assert!(title.text.contains('\u{2139}'));
        assert_eq!(title.role, Role::CalloutNote);

        let body = rows
            .iter()
            .find(|r| r.text.contains("remember this"))
            .expect("body");
        assert!(body.text.starts_with("\u{2502} "), "got {:?}", body.text);
        assert_eq!(body.prefix_role, Some(Role::CalloutNote));
    }

    #[test]
    fn recognises_every_callout_kind() {
        for (marker, role) in [
            ("[!NOTE]", Role::CalloutNote),
            ("[!TIP]", Role::CalloutTip),
            ("[!IMPORTANT]", Role::CalloutImportant),
            ("[!WARNING]", Role::CalloutWarning),
            ("[!CAUTION]", Role::CalloutCaution),
        ] {
            let rows = render(&format!("> {marker}\n> body"), 40);
            assert!(
                rows.iter().any(|r| r.role == role),
                "{marker} did not produce {role:?}"
            );
        }
    }

    #[test]
    fn renders_a_plain_blockquote_without_a_callout_bar() {
        let rows = render("> just a quote", 40);
        assert!(rows.iter().all(|r| r.prefix_role.is_none()));
    }

    #[test]
    fn colours_an_honest_link() {
        let rows = render("[example.com](https://example.com/page)", 60);
        let row = rows.iter().find(|r| !r.spans.is_empty()).expect("a link row");
        assert_eq!(row.spans[0].role, Role::Link);
    }

    #[test]
    fn colours_a_bare_url_link_as_honest() {
        let rows = render("[https://example.com](https://example.com)", 60);
        let row = rows.iter().find(|r| !r.spans.is_empty()).expect("a link row");
        assert_eq!(row.spans[0].role, Role::Link);
    }

    #[test]
    fn flags_a_deceptive_link_with_a_warning_glyph() {
        let rows = render("[bank.com](https://evil.example/phish)", 60);
        let row = rows
            .iter()
            .find(|r| r.text.contains("bank.com"))
            .expect("a link row");
        assert!(row.text.contains('\u{26A0}'), "got {:?}", row.text);
        assert_eq!(row.spans[0].role, Role::LinkDeceptive);
    }

    #[test]
    fn treats_a_relative_link_as_honest() {
        let rows = render("[the docs](./docs/readme.md)", 60);
        let row = rows.iter().find(|r| !r.spans.is_empty()).expect("a link row");
        assert_eq!(row.spans[0].role, Role::Link);
    }

    #[test]
    fn a_link_row_carries_its_destination_url() {
        let rows = render("see the [docs](https://example.com/docs) now", 60);
        let row = rows.iter().find(|r| !r.links.is_empty()).expect("a link row");
        assert_eq!(row.links[0].url, "https://example.com/docs");
    }

    #[test]
    fn a_links_clickable_range_matches_its_ascii_display_text() {
        // The display text names the host, so the link is honest and carries no
        // warning glyph: "example.com" is eleven ASCII cells from the row start.
        let rows = render("[example.com](https://example.com)", 60);
        let row = rows.iter().find(|r| !r.links.is_empty()).expect("a link row");
        assert_eq!((row.links[0].start, row.links[0].end), (0, 11));
        assert_eq!(row.link_at(0), Some("https://example.com"));
        assert_eq!(row.link_at(10), Some("https://example.com"));
        assert_eq!(row.link_at(11), None, "the cell after the link is not clickable");
    }

    #[test]
    fn a_wide_character_in_link_text_does_not_shift_the_clickable_range() {
        // "\u{65E5}\u{672C}\u{8A9E}" is three chars but six cells wide. A range
        // measured in chars would stop at cell three, leaving half the visible
        // link dead to a click. A relative target keeps the link honest (no
        // warning glyph) so the range is exactly the display text.
        let rows = render("[\u{65E5}\u{672C}\u{8A9E}](./page)", 60);
        let row = rows.iter().find(|r| !r.links.is_empty()).expect("a link row");
        assert_eq!((row.links[0].start, row.links[0].end), (0, 6));
        assert_eq!(row.link_at(5), Some("./page"), "the last cell is clickable");
        assert_eq!(row.link_at(6), None, "the cell past the link is not");
    }

    #[test]
    fn wide_text_before_a_link_shifts_it_by_cells_not_chars() {
        // "\u{5B57} " is three cells before the link — a two-cell glyph and a
        // space. Counting chars would place the link two cells too far left.
        let rows = render("\u{5B57} [go](./go)", 60);
        let row = rows.iter().find(|r| !r.links.is_empty()).expect("a link row");
        assert_eq!((row.links[0].start, row.links[0].end), (3, 5));
        assert_eq!(row.link_at(2), None, "the space before the link is not clickable");
        assert_eq!(row.link_at(3), Some("./go"));
    }

    #[test]
    fn a_deceptive_link_still_carries_its_real_destination() {
        let rows = render("[bank.com](https://evil.example/phish)", 60);
        let row = rows.iter().find(|r| !r.links.is_empty()).expect("a link row");
        assert_eq!(row.links[0].url, "https://evil.example/phish");
        // The warning glyph is part of the clickable run, so a click anywhere
        // on the flagged text resolves to the true, non-spoofed target.
        assert_eq!(row.link_at(0), Some("https://evil.example/phish"));
    }

    #[test]
    fn a_link_that_wraps_keeps_its_destination_on_every_row() {
        let rows = render("[alpha beta gamma delta](https://example.com/x)", 16);
        let link_rows: Vec<_> = rows.iter().filter(|r| !r.links.is_empty()).collect();
        assert!(link_rows.len() >= 2, "the label should wrap onto more than one row");
        for row in link_rows {
            assert!(row.links.iter().all(|l| l.url == "https://example.com/x"));
        }
    }

    #[test]
    fn renders_an_image_as_its_alt_text() {
        let rows = non_blank("![a diagram](x.png)", 40);
        assert_eq!(rows, vec!["a diagram"]);
    }

    #[test]
    fn renders_a_horizontal_rule() {
        let rows = render("a\n\n---\n\nb", 10);
        assert!(rows.iter().any(|r| r.text.starts_with('\u{2500}')));
    }

    #[test]
    fn renders_table_headers_in_the_heading_role() {
        let markdown = "| a | b |\n|---|---|\n| 1 | 2 |";
        let rows = render(markdown, 40);
        assert!(rows.iter().any(|r| r.role == Role::Heading));
        assert!(rows.iter().any(|r| r.text.contains('1')));
    }

    #[test]
    fn separates_blocks_with_a_blank_line() {
        let rows = texts("first\n\nsecond", 40);
        let blank_index = rows.iter().position(|r| r.trim().is_empty());
        assert!(blank_index.is_some(), "no blank separator in {rows:?}");
    }

    #[test]
    fn does_not_start_the_output_with_a_blank_line() {
        let rows = texts("hello", 40);
        assert!(!rows[0].trim().is_empty(), "leading blank in {rows:?}");
    }

    #[test]
    fn renders_a_task_list_marker() {
        let rows = non_blank("- [x] done\n- [ ] todo", 40);
        assert!(rows[0].contains("[x] done"));
        assert!(rows[1].contains("[ ] todo"));
    }

    #[test]
    fn no_row_ever_exceeds_the_requested_width() {
        let markdown = "# A fairly long heading that must wrap somewhere\n\n\
             Some body text with a [link](https://example.com/a/very/long/path) inside it.\n\n\
             - a list item that is also quite long and needs to wrap\n\n\
             ```rust\nfn main() { println!(\"a long line of source code here\"); }\n```\n\n\
             > [!WARNING]\n> a callout body that is long enough to wrap as well";

        for width in [8usize, 12, 20, 40, 80] {
            for row in render(markdown, width) {
                assert!(
                    text::width(&row.text) <= width,
                    "row {:?} exceeds width {width}",
                    row.text
                );
            }
        }
    }

    #[test]
    fn handles_empty_input() {
        assert!(render("", 40).is_empty());
    }

    #[test]
    fn never_panics_on_adversarial_markdown() {
        let samples = [
            "```",
            "```rust",
            "> [!",
            "[](",
            "|",
            "|||",
            "- ",
            "#",
            "***",
            "\u{1b}[31mescape",
            "🚀 **bold 🚀**",
            "> > > deep",
        ];
        for sample in samples {
            for width in [1usize, 5, 40] {
                let _ = render(sample, width);
            }
        }
    }

    // Control characters in assistant prose must not survive into rendered output.
    // Adversarial model responses could contain bytes that corrupt the terminal.
    #[test]
    fn control_character_in_prose_is_stripped() {
        // U+0007 (BEL) and U+0001 (SOH) are control characters that must be stripped.
        let rows = non_blank("hello\u{0007}world", 40);
        let combined: String = rows.join("");
        assert!(
            !combined.contains('\u{0007}'),
            "BEL must be stripped from prose; got {combined:?}"
        );
        assert!(combined.contains("hello"), "prose text must survive; got {combined:?}");
        assert!(combined.contains("world"), "prose text must survive; got {combined:?}");
    }

    // ANSI escape sequences in assistant prose must not survive into rendered output.
    #[test]
    fn ansi_escape_in_prose_is_stripped() {
        // ESC [ 3 2 m is the "set foreground green" ANSI sequence.
        let rows = non_blank("before\u{1b}[32mafter", 40);
        let combined: String = rows.join("");
        assert!(
            !combined.contains('\u{1b}'),
            "ANSI escape must be stripped from prose; got {combined:?}"
        );
        assert!(combined.contains("before"), "text before escape must survive");
        assert!(combined.contains("after"), "text after escape must survive");
    }
}
