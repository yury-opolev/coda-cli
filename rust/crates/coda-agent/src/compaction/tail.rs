//! Exchange segmentation and protected-tail selection.
//!
//! # Invariants
//! - The last [`PROTECTED_EXCHANGES`] exchanges always survive verbatim, so a
//!   short confirmation ("yes, proceed") keeps the exchange that gives it its
//!   meaning.
//! - The tail never splits a `ToolUse` from its `ToolResult`.

use std::collections::{HashMap, HashSet};

use coda_llm::{Content, Message, Role};

use super::TokenEstimator;

/// Older exchanges are added to the tail while it stays within this estimate.
pub const TAIL_TARGET_TOKENS: usize = 16_000;

/// Exchanges kept verbatim regardless of the target.
pub const PROTECTED_EXCHANGES: usize = 2;

/// Bytes kept from the start of an oversized tail tool result.
pub const TAIL_PREVIEW_HEAD_BYTES: usize = 6 * 1024;

/// Bytes kept from the end of an oversized tail tool result.
pub const TAIL_PREVIEW_TAIL_BYTES: usize = 2 * 1024;

/// A user message with at least one non-blank Text block and no ToolResult block.
///
/// Runtime injections (steering, task completions, PostCompact context) are
/// user text too, and so start exchanges; tool-result messages never do.
pub fn is_exchange_start(msg: &Message) -> bool {
    msg.role == Role::User
        && msg
            .content
            .iter()
            .any(|b| matches!(b, Content::Text(t) if !t.trim().is_empty()))
        && !msg.content.iter().any(|b| matches!(b, Content::ToolResult { .. }))
}

/// Ascending indices `i` where `is_exchange_start(history[i])`, `history[i..]`
/// has no unpaired `ToolUse`, and no `ToolUse` in `history[..i]` has its
/// `ToolResult` in `history[i..]`.
pub fn exchange_boundaries(history: &[Message]) -> Vec<usize> {
    // `blocked[i]` counts the tool pairs a cut at `i` would split, built as a
    // difference array: a use at `u` answered at `r > u` blocks cuts in
    // `u+1..=r`; a use never answered after it blocks every cut in `0..=u`.
    let mut diff = vec![0isize; history.len() + 2];
    let mut open: HashMap<&str, Vec<usize>> = HashMap::new();
    for (idx, msg) in history.iter().enumerate() {
        for block in &msg.content {
            match block {
                Content::ToolUse { id, .. } => open.entry(id.as_str()).or_default().push(idx),
                Content::ToolResult { tool_use_id, .. } => {
                    if let Some(u) = open.get_mut(tool_use_id.as_str()).and_then(Vec::pop) {
                        // A result in the same message as its use leaves no
                        // cut between them.
                        if idx > u {
                            diff[u + 1] += 1;
                            diff[idx + 1] -= 1;
                        }
                    }
                }
                _ => {}
            }
        }
    }
    for uses in open.values() {
        for &u in uses {
            diff[0] += 1;
            diff[u + 1] -= 1;
        }
    }

    let mut boundaries = Vec::new();
    let mut blocked = 0isize;
    for (idx, msg) in history.iter().enumerate() {
        blocked += diff[idx];
        if blocked == 0 && is_exchange_start(msg) {
            boundaries.push(idx);
        }
    }
    boundaries
}

/// `compaction_tail_start_with(history, TAIL_TARGET_TOKENS)`.
pub fn compaction_tail_start(history: &[Message]) -> usize {
    compaction_tail_start_with(history, TAIL_TARGET_TOKENS)
}

/// Index where the verbatim tail begins.
///
/// The protected start is the second-to-last exchange boundary (the only one
/// when there is one; `0` when there is none). The tail then extends backwards
/// one whole exchange at a time while `TokenEstimator::estimate(&history[b..])`
/// stays within `target_tokens`.
pub fn compaction_tail_start_with(history: &[Message], target_tokens: usize) -> usize {
    let boundaries = exchange_boundaries(history);
    if boundaries.is_empty() {
        return 0;
    }
    let mut pos = boundaries.len().saturating_sub(PROTECTED_EXCHANGES);
    while pos > 0 && TokenEstimator::estimate(&history[boundaries[pos - 1]..]) <= target_tokens {
        pos -= 1;
    }
    boundaries[pos]
}

/// What a shortened tail result's marker may say about the full output.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CheckpointNote<'a> {
    /// No checkpoint is written; the marker claims none.
    None,
    /// A checkpoint will be written; its name is not known yet.
    Pending,
    /// The checkpoint file that holds the full output.
    Named(&'a str),
}

const MARKER_PREFIX: &str = "[coda: ";
const MARKER_OMITTED: &str = " bytes of this tool output were omitted during compaction";

fn preview_marker(omitted: usize, note: CheckpointNote<'_>) -> String {
    let kept = "; the complete output is kept in the local pre-compaction checkpoint";
    match note {
        CheckpointNote::None => format!("{MARKER_PREFIX}{omitted}{MARKER_OMITTED}]"),
        CheckpointNote::Pending => format!("{MARKER_PREFIX}{omitted}{MARKER_OMITTED}{kept}]"),
        CheckpointNote::Named(name) => {
            format!("{MARKER_PREFIX}{omitted}{MARKER_OMITTED}{kept} {name}]")
        }
    }
}

/// Whether `content` already carries a preview marker from an earlier
/// compaction.
pub fn is_tail_preview(content: &str) -> bool {
    content.match_indices(MARKER_PREFIX).any(|(at, _)| {
        let rest = &content[at + MARKER_PREFIX.len()..];
        let digits = rest.bytes().take_while(u8::is_ascii_digit).count();
        digits > 0 && rest[digits..].starts_with(MARKER_OMITTED)
    })
}

/// Shortens `ToolResult` contents in `tail`, oldest first, until the estimated
/// tokens saved reach `tokens_to_free` (or nothing shortenable is left).
///
/// Only results longer than head + tail preview bytes are touched; each keeps
/// its first 6 KiB and last 2 KiB around a marker naming the omission and,
/// per `note`, the checkpoint that holds the full output. A result already
/// shortened by an earlier compaction keeps its original marker. User and
/// assistant text is never shortened. Returns how many results were shortened.
pub fn preview_tail_tool_results(
    tail: &mut [Message],
    tokens_to_free: usize,
    note: CheckpointNote<'_>,
) -> usize {
    let mut freed = 0usize;
    let mut shortened = 0usize;
    for msg in tail.iter_mut() {
        for block in msg.content.iter_mut() {
            if freed >= tokens_to_free {
                return shortened;
            }
            let Content::ToolResult { content, .. } = block else { continue };
            if content.len() <= TAIL_PREVIEW_HEAD_BYTES + TAIL_PREVIEW_TAIL_BYTES
                || is_tail_preview(content)
            {
                continue;
            }
            let (head, _) = coda_proto::history::truncate_text(content, TAIL_PREVIEW_HEAD_BYTES);
            let mut suffix_start = content.len() - TAIL_PREVIEW_TAIL_BYTES;
            while !content.is_char_boundary(suffix_start) {
                suffix_start += 1;
            }
            let suffix = &content[suffix_start..];
            let omitted = suffix_start - head.len();
            let preview = format!("{head}\n{}\n{suffix}", preview_marker(omitted, note));
            if preview.len() >= content.len() {
                continue;
            }
            freed += (content.len() - preview.len()) / 4;
            *content = preview;
            shortened += 1;
        }
    }
    shortened
}

/// Returns `true` when the slice contains a `ToolUse` with no matching
/// `ToolResult` anywhere in the same slice.
pub fn has_unpaired_tool_use(slice: &[Message]) -> bool {
    let mut pending: HashSet<&str> = HashSet::new();
    for msg in slice {
        for block in &msg.content {
            match block {
                Content::ToolUse { id, .. } => {
                    pending.insert(id.as_str());
                }
                Content::ToolResult { tool_use_id, .. } => {
                    pending.remove(tool_use_id.as_str());
                }
                _ => {}
            }
        }
    }
    !pending.is_empty()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn user(text: &str) -> Message {
        Message::user(text)
    }

    fn assistant(text: &str) -> Message {
        Message::assistant(text)
    }

    fn tool_use(id: &str, name: &str) -> Message {
        Message::new(
            Role::Assistant,
            vec![Content::ToolUse {
                id: id.into(),
                name: name.into(),
                input_json: "{}".into(),
                correlation: Default::default(),
            }],
        )
    }

    fn tool_result(id: &str, content: &str) -> Message {
        Message::new(
            Role::User,
            vec![Content::ToolResult {
                tool_use_id: id.into(),
                content: content.into(),
                is_error: false,
                correlation: Default::default(),
                status: None,
            }],
        )
    }

    fn texts(slice: &[Message]) -> Vec<String> {
        slice.iter().map(Message::text).collect()
    }

    fn result_content(msg: &Message) -> &str {
        match &msg.content[0] {
            Content::ToolResult { content, .. } => content,
            other => panic!("expected a tool result, got {other:?}"),
        }
    }

    /// BUG-2: the five-message probe from the report. The old selection kept
    /// "Yes, proceed." and dropped the answer it was agreeing to.
    #[test]
    fn the_report_probe_keeps_the_previous_exchange() {
        let h = vec![
            user("Earlier unrelated question"),
            assistant("Earlier unrelated answer"),
            user("Which formatting option should I apply?"),
            assistant("Apply option B; leave option A unchanged."),
            user("Yes, proceed."),
        ];
        let start = compaction_tail_start_with(&h, 0);
        assert_eq!(start, 2);
        assert!(texts(&h[start..]).iter().any(|t| t == "Apply option B; leave option A unchanged."));
    }

    #[test]
    fn everything_fitting_the_target_still_keeps_two_exchanges() {
        let mut h = Vec::new();
        for i in 0..6 {
            h.push(user(&format!("question {i}")));
            h.push(assistant(&format!("answer {i}")));
        }
        h.push(user("yes, do it"));

        assert_eq!(compaction_tail_start(&h), 0, "everything fits the default target");
        let start = compaction_tail_start_with(&h, 0);
        assert_eq!(start, 10, "the exchange before \"yes, do it\" is protected");
        assert_eq!(h[start].text(), "question 5");
    }

    #[test]
    fn tool_results_are_not_exchange_boundaries() {
        let h = vec![
            user("look"),
            tool_use("c1", "read_file"),
            tool_result("c1", "contents"),
            assistant("seen"),
            user("ok"),
        ];
        assert_eq!(exchange_boundaries(&h), vec![0, 4]);
    }

    #[test]
    fn tail_never_orphans_a_tool_pair() {
        let mut h = Vec::new();
        for i in 0..3 {
            let id = format!("c{i}");
            h.push(user(&format!("step {i}")));
            h.push(tool_use(&id, "grep"));
            h.push(tool_result(&id, &"x".repeat(200)));
            h.push(assistant(&format!("did {i}")));
            h.push(user("steer: keep going"));
            h.push(tool_use(&format!("{id}b"), "read"));
            h.push(tool_result(&format!("{id}b"), "y"));
        }
        h.push(assistant("finished"));

        for len in 0..=h.len() {
            let prefix = &h[..len];
            for target in [0, 10, 1_000_000] {
                let cut = compaction_tail_start_with(prefix, target);
                let tail = &prefix[cut..];
                // A dangling call (still in flight) is kept with everything
                // before it; any real cut must leave the tail fully paired.
                assert!(
                    cut == 0 || !has_unpaired_tool_use(tail),
                    "len={len} target={target} cut={cut}"
                );
                let uses: HashSet<&str> = tail
                    .iter()
                    .flat_map(|m| &m.content)
                    .filter_map(|c| match c {
                        Content::ToolUse { id, .. } => Some(id.as_str()),
                        _ => None,
                    })
                    .collect();
                for c in tail.iter().flat_map(|m| &m.content) {
                    if let Content::ToolResult { tool_use_id, .. } = c {
                        assert!(
                            uses.contains(tool_use_id.as_str()),
                            "len={len} target={target} cut={cut}: result {tool_use_id} lost its call"
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn the_target_extends_by_whole_exchanges() {
        // Four exchanges of ~1,000 tokens each (two 2,000-char messages).
        let mut h = Vec::new();
        for _ in 0..4 {
            h.push(user(&"u".repeat(2_000)));
            h.push(assistant(&"a".repeat(2_000)));
        }
        // Two protected exchanges ≈ 2,000 tokens; a third would be ≈ 3,000.
        assert_eq!(compaction_tail_start_with(&h, 2_500), 4);
        assert_eq!(compaction_tail_start_with(&h, 3_000), 2);
        assert_eq!(compaction_tail_start_with(&h, 1_000_000), 0);
    }

    /// An incomplete exchange (tool_use with no result) is entirely preserved.
    #[test]
    fn an_incomplete_exchange_is_fully_preserved() {
        let h = vec![user("initial message"), tool_use("c1", "read_file")];
        assert_eq!(compaction_tail_start_with(&h, 0), 0, "incomplete exchange forces full preservation");
    }

    #[test]
    fn tail_includes_tool_use_and_its_result() {
        let h = vec![
            user("do something"),
            tool_use("c1", "read_file"),
            tool_result("c1", "file contents"),
            assistant("done"),
        ];
        let tail = &h[compaction_tail_start_with(&h, 0)..];
        let has_use = tail.iter().any(|m| {
            m.content.iter().any(|b| matches!(b, Content::ToolUse { id, .. } if id == "c1"))
        });
        let has_result = tail.iter().any(|m| {
            m.content
                .iter()
                .any(|b| matches!(b, Content::ToolResult { tool_use_id, .. } if tool_use_id == "c1"))
        });
        assert!(has_use, "tail must include the tool_use block");
        assert!(has_result, "tail must include the matching tool_result");
    }

    #[test]
    fn oversized_tail_tool_results_get_marked_previews() {
        let big: String = (0..100_000).map(|i| (b'a' + (i % 26) as u8) as char).collect();
        let mut tail = vec![
            user("run it"),
            tool_use("c1", "cat"),
            tool_result("c1", &big),
            tool_use("c2", "echo"),
            tool_result("c2", "0123456789"),
        ];

        assert_eq!(preview_tail_tool_results(&mut tail, 1, CheckpointNote::Named("x.json")), 1);

        let shortened = result_content(&tail[2]);
        assert!(shortened.starts_with(&big[..TAIL_PREVIEW_HEAD_BYTES]));
        assert!(shortened.contains("bytes of this tool output were omitted"));
        assert!(shortened.contains("x.json"));
        assert!(shortened.ends_with(&big[big.len() - TAIL_PREVIEW_TAIL_BYTES..]));
        assert!(shortened.len() < big.len());
        assert_eq!(result_content(&tail[4]), "0123456789");
        assert!(matches!(&tail[1].content[0], Content::ToolUse { id, .. } if id == "c1"));
        assert!(matches!(&tail[2].content[0], Content::ToolResult { tool_use_id, .. } if tool_use_id == "c1"));
    }

    #[test]
    fn an_already_shortened_result_is_not_shortened_again() {
        let big: String = (0..100_000).map(|i| (b'a' + (i % 26) as u8) as char).collect();
        let mut tail = vec![tool_use("c1", "cat"), tool_result("c1", &big)];

        assert_eq!(preview_tail_tool_results(&mut tail, 1, CheckpointNote::Named("first.json")), 1);
        let first = result_content(&tail[1]).to_owned();
        assert!(first.len() > TAIL_PREVIEW_HEAD_BYTES + TAIL_PREVIEW_TAIL_BYTES);
        assert!(is_tail_preview(&first));

        assert_eq!(
            preview_tail_tool_results(&mut tail, usize::MAX, CheckpointNote::Named("second.json")),
            0
        );
        let second = result_content(&tail[1]);
        assert_eq!(second, first);
        let omitted = big.len() - TAIL_PREVIEW_HEAD_BYTES - TAIL_PREVIEW_TAIL_BYTES;
        assert!(second.contains(&format!("[coda: {omitted} bytes of this tool output were omitted")));
        assert!(second.contains("first.json"));
        assert!(!second.contains("second.json"));
    }

    #[test]
    fn without_a_checkpoint_the_marker_claims_none() {
        let big = "z".repeat(50_000);
        let mut tail = vec![tool_result("c1", &big)];
        assert_eq!(preview_tail_tool_results(&mut tail, 1, CheckpointNote::None), 1);
        let shortened = result_content(&tail[0]);
        assert!(shortened.contains(&format!(
            "[coda: {} bytes of this tool output were omitted during compaction]",
            big.len() - TAIL_PREVIEW_HEAD_BYTES - TAIL_PREVIEW_TAIL_BYTES
        )));
        assert!(!shortened.contains("checkpoint"));
        assert!(is_tail_preview(shortened));
        assert!(!is_tail_preview("[coda: many bytes of this tool output were omitted during compaction]"));
    }

    #[test]
    fn previews_respect_utf8() {
        let wide = "\u{2500}".repeat(20_000);
        let mut tail = vec![tool_result("c1", &wide)];
        assert_eq!(preview_tail_tool_results(&mut tail, 1, CheckpointNote::None), 1);
        let shortened = result_content(&tail[0]);
        assert!(shortened.len() < wide.len());
        assert!(shortened.starts_with('\u{2500}') && shortened.ends_with('\u{2500}'));
    }
}
