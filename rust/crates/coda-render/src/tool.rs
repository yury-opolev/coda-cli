//! Tool call presentation.
//!
//! Tool activity dominates an agent transcript, so how much of it is shown is
//! configurable. The four display modes trade detail against noise, and each
//! tool gets a human-readable one-line preview derived from its arguments
//! rather than raw JSON.

use serde_json::Value;

use crate::diff;
use crate::line::{Gutter, RenderLine, CHILD_CELLS, MARKER_CELLS};
use crate::text;
use crate::theme::Role;

/// Longest tool preview before truncation, in cells.
pub const PREVIEW_MAX: usize = 128;
/// How many running calls the summary lists before collapsing.
const SUMMARY_MAX_ROWS: usize = 5;

/// How much tool detail to show.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ToolDisplayMode {
    /// Full arguments and complete result bodies.
    Full,
    /// One line per call.
    Compact,
    /// A single rolled-up line per batch.
    #[default]
    Summary,
    /// Nothing at all.
    Hidden,
}

impl ToolDisplayMode {
    /// Parses a settings value, falling back to the default when unrecognised.
    ///
    /// Returns whether the input was understood so callers can warn about a
    /// typo instead of silently using a different mode.
    pub fn parse(raw: Option<&str>) -> (ToolDisplayMode, bool) {
        match raw.map(str::trim) {
            None | Some("") => (ToolDisplayMode::Summary, true),
            Some(value) => match value.to_ascii_lowercase().as_str() {
                "full" | "verbose" => (ToolDisplayMode::Full, true),
                "compact" => (ToolDisplayMode::Compact, true),
                "summary" => (ToolDisplayMode::Summary, true),
                "hidden" | "tiny" => (ToolDisplayMode::Hidden, true),
                _ => (ToolDisplayMode::Summary, false),
            },
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            ToolDisplayMode::Full => "full",
            ToolDisplayMode::Compact => "compact",
            ToolDisplayMode::Summary => "summary",
            ToolDisplayMode::Hidden => "hidden",
        }
    }
}

/// Lifecycle of a single tool invocation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CallStatus {
    Pending,
    AwaitingApproval,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Skipped,
}

impl CallStatus {
    pub fn label(self) -> &'static str {
        match self {
            CallStatus::Pending => "pending",
            CallStatus::AwaitingApproval => "awaiting approval",
            CallStatus::Running => "running",
            CallStatus::Succeeded => "success",
            CallStatus::Failed => "error",
            CallStatus::Cancelled => "cancelled",
            CallStatus::Skipped => "skipped",
        }
    }

    pub fn is_terminal(self) -> bool {
        matches!(
            self,
            CallStatus::Succeeded
                | CallStatus::Failed
                | CallStatus::Cancelled
                | CallStatus::Skipped
        )
    }

    fn role(self) -> Role {
        match self {
            CallStatus::Succeeded => Role::ToolSuccess,
            CallStatus::Failed => Role::Error,
            CallStatus::Cancelled | CallStatus::Skipped => Role::Warning,
            _ => Role::Tool,
        }
    }
}

/// One tool invocation and everything known about it.
#[derive(Debug, Clone)]
pub struct ToolCall {
    pub name: String,
    /// Raw arguments as a JSON string, exactly as the engine sent them.
    pub input_json: String,
    pub status: CallStatus,
    pub result: Option<String>,
    pub is_error: bool,
    pub elapsed_ms: Option<i64>,
}

impl ToolCall {
    pub fn new(name: impl Into<String>, input_json: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            input_json: input_json.into(),
            status: CallStatus::Running,
            result: None,
            is_error: false,
            elapsed_ms: None,
        }
    }

    /// A human-readable one-liner describing what this call is doing.
    ///
    /// Common tools get bespoke phrasing; anything else falls back to the tool
    /// name plus a condensed argument preview.
    pub fn preview(&self) -> String {
        let args = parse_args(&self.input_json);
        let arg = |key: &str| -> Option<String> {
            args.get(key)
                .and_then(Value::as_str)
                .map(|s| normalise(s))
                .filter(|s| !s.is_empty())
        };

        let text = match self.name.as_str() {
            "run_command" => arg("command").map(|c| format!("$ {c}")),
            "read_file" => arg("path").map(|p| format!("Reading {p}")),
            "write_file" => arg("path").map(|p| format!("Writing {p}")),
            "edit" | "edit_file" => arg("path").map(|p| format!("Editing {p}")),
            "notebook_edit" => arg("notebook_path").map(|p| format!("Editing {p}")),
            "grep" | "glob" => arg("pattern").map(|p| format!("Searching for {p}")),
            "web_search" | "tool_search" => arg("query").map(|q| format!("Searching for {q}")),
            "web_fetch" => arg("url").map(|u| format!("Fetching {u}")),
            "skill" => arg("name").map(|n| format!("Loading skill {n}")),
            "task" => arg("description").map(|d| format!("Task: {d}")),
            _ => None,
        };

        let text = text.unwrap_or_else(|| {
            let preview = normalise(&self.input_json);
            if preview.is_empty() || preview == "{}" {
                self.name.clone()
            } else {
                format!("{} {}", self.name, preview)
            }
        });

        text::truncate_with_ellipsis(&text, PREVIEW_MAX)
    }

    fn timing(&self) -> String {
        match (self.elapsed_ms, self.status) {
            (Some(ms), _) => format!(" ({ms}ms)"),
            (None, CallStatus::Running) => " (running)".to_string(),
            _ => String::new(),
        }
    }
}

fn parse_args(input_json: &str) -> Value {
    serde_json::from_str(input_json).unwrap_or(Value::Null)
}

/// Collapses whitespace runs so a preview stays on one line.
fn normalise(text: &str) -> String {
    text.split_whitespace().collect::<Vec<_>>().join(" ")
}

/// A batch of tool calls made in one agent step.
#[derive(Debug, Clone, Default)]
pub struct ToolActivity {
    pub calls: Vec<ToolCall>,
    /// Set once the engine reports the batch finished.
    pub complete: bool,
}

impl ToolActivity {
    pub fn running(&self) -> Vec<&ToolCall> {
        self.calls
            .iter()
            .filter(|c| !c.status.is_terminal())
            .collect()
    }

    pub fn failed(&self) -> usize {
        self.calls
            .iter()
            .filter(|c| c.status == CallStatus::Failed)
            .count()
    }

    pub fn cancelled(&self) -> usize {
        self.calls
            .iter()
            .filter(|c| c.status == CallStatus::Cancelled)
            .count()
    }

    /// `tool` / `tools`, or `shell command(s)` when the batch is all shell.
    fn noun(&self, count: usize) -> String {
        let all_shell = !self.calls.is_empty()
            && self.calls.iter().all(|c| c.name == "run_command");
        match (all_shell, count) {
            (true, 1) => "shell command".to_string(),
            (true, _) => "shell commands".to_string(),
            (false, 1) => "tool".to_string(),
            (false, _) => "tools".to_string(),
        }
    }

    /// The rolled-up headline shown when the batch has finished.
    pub fn summary_headline(&self) -> String {
        let count = self.calls.len();
        let mut suffix = String::new();
        let failed = self.failed();
        let cancelled = self.cancelled();

        if failed > 0 {
            suffix.push_str(&format!(" - {failed} failed"));
        }
        if cancelled > 0 {
            suffix.push_str(if failed > 0 { ", cancelled" } else { " - cancelled" });
        }

        format!("Ran {count} {}{suffix}", self.noun(count))
    }

    fn summary_role(&self) -> Role {
        if self.failed() == self.calls.len() && !self.calls.is_empty() {
            Role::Error
        } else if self.failed() > 0 {
            Role::ToolPartialFailure
        } else if self.cancelled() > 0 {
            Role::Warning
        } else {
            Role::ToolSuccess
        }
    }

    /// Renders the batch according to `mode`.
    pub fn render(&self, mode: ToolDisplayMode, width: usize) -> Vec<RenderLine> {
        match mode {
            ToolDisplayMode::Hidden => Vec::new(),
            ToolDisplayMode::Summary => self.render_summary(width),
            ToolDisplayMode::Compact => self.render_compact(width),
            ToolDisplayMode::Full => self.render_full(width),
        }
    }

    fn render_summary(&self, width: usize) -> Vec<RenderLine> {
        let mut out = Vec::new();
        let content = width.saturating_sub(MARKER_CELLS).max(1);
        let child = width.saturating_sub(CHILD_CELLS).max(1);

        if self.complete {
            let role = self.summary_role();
            for (i, chunk) in text::wrap(&self.summary_headline(), content)
                .into_iter()
                .enumerate()
            {
                out.push(RenderLine::new(chunk, role).with_gutter(if i == 0 {
                    Gutter::AgentComplete
                } else {
                    Gutter::Continuation
                }));
            }
            return out;
        }

        let running = self.running();
        if running.is_empty() {
            // Between batches: show the most recent finished call so the
            // transcript never goes blank mid-turn.
            if let Some(last) = self.calls.last() {
                let outcome = if last.status == CallStatus::Failed {
                    "failed"
                } else {
                    "done"
                };
                let text = format!("{} - {outcome}", last.preview());
                for (i, chunk) in text::wrap(&text, content).into_iter().enumerate() {
                    out.push(RenderLine::new(chunk, Role::Tool).with_gutter(if i == 0 {
                        Gutter::AgentActive
                    } else {
                        Gutter::Continuation
                    }));
                }
            }
            return out;
        }

        let headline = format!("Running {} {}...", running.len(), self.noun(running.len()));
        for (i, chunk) in text::wrap(&headline, content).into_iter().enumerate() {
            out.push(RenderLine::new(chunk, Role::Tool).with_gutter(if i == 0 {
                Gutter::AgentActive
            } else {
                Gutter::Continuation
            }));
        }

        // List the running calls, collapsing the tail when there are too many.
        let overflow = running.len() > SUMMARY_MAX_ROWS;
        let shown = if overflow {
            SUMMARY_MAX_ROWS - 1
        } else {
            running.len()
        };

        for (index, call) in running.iter().take(shown).enumerate() {
            let last = !overflow && index == shown - 1;
            push_child(&mut out, &call.preview(), Role::Tool, child, last);
        }
        if overflow {
            let text = format!("...and {} more", running.len() - shown);
            push_child(&mut out, &text, Role::Notification, child, true);
        }

        out
    }

    fn render_compact(&self, width: usize) -> Vec<RenderLine> {
        let content = width.saturating_sub(MARKER_CELLS).max(1);
        let mut out = Vec::new();

        for call in &self.calls {
            let text = format!(
                "{} [{}]{}",
                call.preview(),
                call.status.label(),
                call.timing()
            );
            let gutter = if call.status.is_terminal() {
                Gutter::AgentComplete
            } else {
                Gutter::AgentActive
            };
            for (i, chunk) in text::wrap(&text, content).into_iter().enumerate() {
                out.push(
                    RenderLine::new(chunk, call.status.role())
                        .with_gutter(if i == 0 { gutter } else { Gutter::Continuation }),
                );
            }
        }
        out
    }

    fn render_full(&self, width: usize) -> Vec<RenderLine> {
        let content = width.saturating_sub(MARKER_CELLS).max(1);
        let child = width.saturating_sub(CHILD_CELLS).max(1);
        let mut out = Vec::new();

        for call in &self.calls {
            let header = format!(
                "{} {} [{}]{}",
                call.name,
                normalise(&call.input_json),
                call.status.label(),
                call.timing()
            );
            let gutter = if call.status.is_terminal() {
                Gutter::AgentComplete
            } else {
                Gutter::AgentActive
            };
            for (i, chunk) in text::wrap(header.trim(), content).into_iter().enumerate() {
                out.push(
                    RenderLine::new(chunk, call.status.role())
                        .with_gutter(if i == 0 { gutter } else { Gutter::Continuation }),
                );
            }

            let Some(result) = call.result.as_deref().filter(|r| !r.trim().is_empty()) else {
                continue;
            };

            // A patch in a tool result is far more useful rendered as a diff.
            if diff::looks_like_diff(result) {
                for row in diff::render_with_preamble(result, child, true) {
                    out.push(row);
                }
                continue;
            }

            let role = if call.is_error { Role::Error } else { Role::Code };
            let body: Vec<&str> = result.lines().collect();
            for (index, source) in body.iter().enumerate() {
                let last = index == body.len() - 1;
                let sanitized = text::sanitize(source);
                for (i, chunk) in text::wrap_preformatted(&sanitized, child)
                    .into_iter()
                    .enumerate()
                {
                    let gutter = match (i, last) {
                        (0, true) => Gutter::LastChild,
                        (0, false) => Gutter::Child,
                        _ => Gutter::ChildContinuation,
                    };
                    out.push(RenderLine::new(chunk, role).with_gutter(gutter));
                }
            }
        }
        out
    }
}

fn push_child(out: &mut Vec<RenderLine>, text: &str, role: Role, width: usize, last: bool) {
    for (i, chunk) in text::wrap(text, width).into_iter().enumerate() {
        let gutter = match (i, last) {
            (0, true) => Gutter::LastChild,
            (0, false) => Gutter::Child,
            _ => Gutter::ChildContinuation,
        };
        out.push(RenderLine::new(chunk, role).with_gutter(gutter));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn call(name: &str, input: &str, status: CallStatus) -> ToolCall {
        ToolCall {
            status,
            ..ToolCall::new(name, input)
        }
    }

    fn batch(calls: Vec<ToolCall>, complete: bool) -> ToolActivity {
        ToolActivity { calls, complete }
    }

    fn texts(lines: &[RenderLine]) -> Vec<String> {
        lines.iter().map(|l| l.text.clone()).collect()
    }

    #[test]
    fn parses_every_display_mode() {
        assert_eq!(ToolDisplayMode::parse(Some("full")).0, ToolDisplayMode::Full);
        assert_eq!(
            ToolDisplayMode::parse(Some("Compact")).0,
            ToolDisplayMode::Compact
        );
        assert_eq!(
            ToolDisplayMode::parse(Some(" summary ")).0,
            ToolDisplayMode::Summary
        );
        assert_eq!(
            ToolDisplayMode::parse(Some("hidden")).0,
            ToolDisplayMode::Hidden
        );
    }

    #[test]
    fn defaults_to_summary_when_unset() {
        let (mode, recognised) = ToolDisplayMode::parse(None);
        assert_eq!(mode, ToolDisplayMode::Summary);
        assert!(recognised, "an absent setting is valid, not a typo");
    }

    #[test]
    fn reports_an_unrecognised_mode_while_still_defaulting() {
        let (mode, recognised) = ToolDisplayMode::parse(Some("loud"));
        assert_eq!(mode, ToolDisplayMode::Summary);
        assert!(!recognised);
    }

    #[test]
    fn previews_a_shell_command_with_a_prompt() {
        let call = ToolCall::new("run_command", r#"{"command":"cargo test"}"#);
        assert_eq!(call.preview(), "$ cargo test");
    }

    #[test]
    fn previews_file_tools_with_their_verbs() {
        let cases = [
            ("read_file", r#"{"path":"a.rs"}"#, "Reading a.rs"),
            ("write_file", r#"{"path":"a.rs"}"#, "Writing a.rs"),
            ("edit", r#"{"path":"a.rs"}"#, "Editing a.rs"),
            ("grep", r#"{"pattern":"TODO"}"#, "Searching for TODO"),
            ("web_search", r#"{"query":"rust"}"#, "Searching for rust"),
            ("skill", r#"{"name":"pdf"}"#, "Loading skill pdf"),
        ];
        for (name, input, expected) in cases {
            assert_eq!(ToolCall::new(name, input).preview(), expected, "{name}");
        }
    }

    #[test]
    fn falls_back_to_the_tool_name_and_arguments() {
        let call = ToolCall::new("custom_tool", r#"{"a":1}"#);
        assert_eq!(call.preview(), r#"custom_tool {"a":1}"#);
    }

    #[test]
    fn falls_back_to_just_the_name_for_empty_arguments() {
        assert_eq!(ToolCall::new("doctor", "{}").preview(), "doctor");
        assert_eq!(ToolCall::new("doctor", "").preview(), "doctor");
    }

    #[test]
    fn survives_arguments_that_are_not_valid_json() {
        let call = ToolCall::new("read_file", "not json at all");
        assert_eq!(call.preview(), "read_file not json at all");
    }

    #[test]
    fn collapses_newlines_in_a_preview() {
        let call = ToolCall::new("run_command", "{\"command\":\"a\\nb\\n  c\"}");
        assert_eq!(call.preview(), "$ a b c");
    }

    #[test]
    fn truncates_an_overlong_preview() {
        let long = "x".repeat(400);
        let call = ToolCall::new("run_command", &format!(r#"{{"command":"{long}"}}"#));
        let preview = call.preview();
        assert_eq!(text::width(&preview), PREVIEW_MAX);
        assert!(preview.ends_with('…'));
    }

    #[test]
    fn hidden_mode_renders_nothing() {
        let activity = batch(vec![call("read_file", "{}", CallStatus::Succeeded)], true);
        assert!(activity.render(ToolDisplayMode::Hidden, 80).is_empty());
    }

    #[test]
    fn summary_mode_rolls_a_finished_batch_into_one_line() {
        let activity = batch(
            vec![
                call("read_file", "{}", CallStatus::Succeeded),
                call("grep", "{}", CallStatus::Succeeded),
            ],
            true,
        );
        let rows = activity.render(ToolDisplayMode::Summary, 80);
        assert_eq!(texts(&rows), vec![" \u{25CF} Ran 2 tools"]);
        assert_eq!(rows[0].role, Role::ToolSuccess);
    }

    #[test]
    fn summary_reports_failures_in_the_headline() {
        let activity = batch(
            vec![
                call("read_file", "{}", CallStatus::Succeeded),
                call("edit", "{}", CallStatus::Failed),
            ],
            true,
        );
        assert_eq!(activity.summary_headline(), "Ran 2 tools - 1 failed");
        let rows = activity.render(ToolDisplayMode::Summary, 80);
        assert_eq!(rows[0].role, Role::ToolPartialFailure);
    }

    #[test]
    fn summary_reports_cancellation() {
        let activity = batch(vec![call("edit", "{}", CallStatus::Cancelled)], true);
        assert_eq!(activity.summary_headline(), "Ran 1 tool - cancelled");
    }

    #[test]
    fn summary_reports_both_failure_and_cancellation() {
        let activity = batch(
            vec![
                call("a", "{}", CallStatus::Failed),
                call("b", "{}", CallStatus::Failed),
                call("c", "{}", CallStatus::Cancelled),
            ],
            true,
        );
        assert_eq!(activity.summary_headline(), "Ran 3 tools - 2 failed, cancelled");
    }

    #[test]
    fn summary_uses_shell_wording_for_an_all_shell_batch() {
        let activity = batch(
            vec![
                call("run_command", "{}", CallStatus::Succeeded),
                call("run_command", "{}", CallStatus::Succeeded),
            ],
            true,
        );
        assert_eq!(activity.summary_headline(), "Ran 2 shell commands");
    }

    #[test]
    fn summary_uses_singular_wording_for_one_call() {
        let activity = batch(vec![call("run_command", "{}", CallStatus::Succeeded)], true);
        assert_eq!(activity.summary_headline(), "Ran 1 shell command");
    }

    #[test]
    fn summary_lists_running_calls_while_active() {
        let activity = batch(
            vec![
                call("read_file", r#"{"path":"a.rs"}"#, CallStatus::Running),
                call("grep", r#"{"pattern":"x"}"#, CallStatus::Running),
            ],
            false,
        );
        let rows = texts(&activity.render(ToolDisplayMode::Summary, 80));
        assert!(rows[0].contains("Running 2 tools..."));
        assert!(rows[1].contains("Reading a.rs"));
        assert!(rows[2].contains("Searching for x"));
    }

    #[test]
    fn summary_collapses_more_than_five_running_calls() {
        let calls = (0..8)
            .map(|i| call("read_file", &format!(r#"{{"path":"f{i}.rs"}}"#), CallStatus::Running))
            .collect();
        let rows = texts(&batch(calls, false).render(ToolDisplayMode::Summary, 80));

        assert!(rows[0].contains("Running 8 tools..."));
        // Headline plus four previews plus the overflow line.
        assert_eq!(rows.len(), 6);
        assert!(rows.last().unwrap().contains("...and 4 more"));
    }

    #[test]
    fn summary_shows_the_last_finished_call_between_batches() {
        let activity = batch(
            vec![call("read_file", r#"{"path":"a.rs"}"#, CallStatus::Succeeded)],
            false,
        );
        let rows = texts(&activity.render(ToolDisplayMode::Summary, 80));
        assert!(rows[0].contains("Reading a.rs - done"));
    }

    #[test]
    fn summary_marks_a_failed_last_call() {
        let activity = batch(vec![call("edit", r#"{"path":"a"}"#, CallStatus::Failed)], false);
        let rows = texts(&activity.render(ToolDisplayMode::Summary, 80));
        assert!(rows[0].contains("- failed"));
    }

    #[test]
    fn compact_mode_renders_one_line_per_call() {
        let mut first = call("read_file", r#"{"path":"a.rs"}"#, CallStatus::Succeeded);
        first.elapsed_ms = Some(12);
        let activity = batch(vec![first, call("grep", "{}", CallStatus::Running)], false);

        let rows = texts(&activity.render(ToolDisplayMode::Compact, 80));
        assert_eq!(rows.len(), 2);
        assert!(rows[0].contains("Reading a.rs [success] (12ms)"));
        assert!(rows[1].contains("[running]"));
    }

    #[test]
    fn full_mode_shows_arguments_and_the_result_body() {
        let mut c = call("read_file", r#"{"path":"a.rs"}"#, CallStatus::Succeeded);
        c.result = Some("line one\nline two".to_string());
        let rows = texts(&batch(vec![c], true).render(ToolDisplayMode::Full, 80));

        assert!(rows[0].contains(r#"read_file {"path":"a.rs"} [success]"#));
        assert!(rows.iter().any(|r| r.contains("line one")));
        assert!(rows.iter().any(|r| r.contains("line two")));
    }

    #[test]
    fn full_mode_marks_the_final_result_row_as_the_last_child() {
        let mut c = call("read_file", "{}", CallStatus::Succeeded);
        c.result = Some("one\ntwo".to_string());
        let rows = batch(vec![c], true).render(ToolDisplayMode::Full, 80);
        assert_eq!(rows.last().unwrap().gutter, Gutter::LastChild);
    }

    #[test]
    fn full_mode_renders_a_patch_result_as_a_diff() {
        let mut c = call("edit", r#"{"path":"x.rs"}"#, CallStatus::Succeeded);
        c.result = Some("--- a/x.rs\n+++ b/x.rs\n@@ -1 +1 @@\n-old\n+new\n".to_string());
        let rows = batch(vec![c], true).render(ToolDisplayMode::Full, 80);

        assert!(rows.iter().any(|r| r.text.contains("Update(x.rs)")));
        assert!(rows.iter().any(|r| r.role == Role::DiffAdded));
    }

    #[test]
    fn full_mode_colours_an_error_result() {
        let mut c = call("run_command", "{}", CallStatus::Failed);
        c.is_error = true;
        c.result = Some("boom".to_string());
        let rows = batch(vec![c], true).render(ToolDisplayMode::Full, 80);
        assert!(rows.iter().any(|r| r.role == Role::Error));
    }

    #[test]
    fn full_mode_strips_escape_sequences_from_a_result() {
        let mut c = call("run_command", "{}", CallStatus::Succeeded);
        c.result = Some("\u{1b}[31mred\u{1b}[0m".to_string());
        let rows = texts(&batch(vec![c], true).render(ToolDisplayMode::Full, 80));
        assert!(rows.iter().any(|r| r.contains("red")));
        assert!(!rows.iter().any(|r| r.contains('\u{1b}')));
    }

    #[test]
    fn no_rendered_row_exceeds_the_viewport_in_any_mode() {
        let mut c = call("run_command", r#"{"command":"a very long command line here"}"#, CallStatus::Succeeded);
        c.result = Some("a fairly long line of tool output that will need wrapping".to_string());
        let activity = batch(
            vec![c, call("grep", r#"{"pattern":"something long"}"#, CallStatus::Running)],
            false,
        );

        for mode in [
            ToolDisplayMode::Full,
            ToolDisplayMode::Compact,
            ToolDisplayMode::Summary,
        ] {
            for width in [10usize, 20, 40, 80] {
                for row in activity.render(mode, width) {
                    assert!(
                        text::width(&row.text) <= width,
                        "{mode:?} row {:?} exceeds {width}",
                        row.text
                    );
                }
            }
        }
    }

    #[test]
    fn an_empty_batch_renders_nothing_in_summary_while_active() {
        assert!(batch(Vec::new(), false)
            .render(ToolDisplayMode::Summary, 80)
            .is_empty());
    }

    #[test]
    fn every_terminal_status_is_reported_as_terminal() {
        for status in [
            CallStatus::Succeeded,
            CallStatus::Failed,
            CallStatus::Cancelled,
            CallStatus::Skipped,
        ] {
            assert!(status.is_terminal(), "{status:?}");
        }
        for status in [
            CallStatus::Pending,
            CallStatus::AwaitingApproval,
            CallStatus::Running,
        ] {
            assert!(!status.is_terminal(), "{status:?}");
        }
    }
}
