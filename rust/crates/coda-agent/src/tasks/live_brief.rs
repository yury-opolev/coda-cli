//! Compact, model-facing descriptions of the tasks a conversation has running
//! (non-blocking runtime, Phase 3).
//!
//! - [`live_task_brief`] prefixes a steering or user message delivered while
//!   the agent has work running (§6.9): a running process cannot be changed
//!   by a message, so the model must see what is already running, with which
//!   parameters, and decide to stop/restart rather than assume a change took.
//! - [`task_snapshot`] is the live task table a `/btw` side question sees
//!   (§6.8), so "are the reviews still running?" is answered from facts.
//!
//! Both are bounded and use only the secret-redacted output tail.

use std::time::SystemTime;

use super::managed_task::{TaskKind, TaskRunStatus, TaskSnapshot};
use super::task_manager::TaskManager;

/// Most tasks listed in a steering prefix; the rest are counted.
const BRIEF_MAX_TASKS: usize = 10;
/// Longest description shown for one task.
const DESCRIPTION_CHARS: usize = 120;
/// Most finished tasks shown in a side-question snapshot (newest first).
const SNAPSHOT_MAX_FINISHED: usize = 8;
/// Most running tasks shown in a side-question snapshot.
const SNAPSHOT_MAX_RUNNING: usize = 24;
/// Output lines shown per task in a side-question snapshot.
const SNAPSHOT_TAIL_LINES: usize = 5;
/// Longest single output line shown in a snapshot.
const SNAPSHOT_LINE_CHARS: usize = 200;

/// Formats a duration in whole seconds as `45s`, `3m 07s` or `2h 05m`.
pub fn format_age(secs: u64) -> String {
    match secs {
        s if s < 60 => format!("{s}s"),
        s if s < 3600 => format!("{}m {:02}s", s / 60, s % 60),
        s => format!("{}h {:02}m", s / 3600, (s % 3600) / 60),
    }
}

fn age_of(snapshot: &TaskSnapshot, now: SystemTime) -> u64 {
    let end = snapshot.ended_at.unwrap_or(now);
    end.duration_since(snapshot.started_at).map(|d| d.as_secs()).unwrap_or(0)
}

fn one_line(text: &str, max_chars: usize) -> String {
    let line = text.lines().map(str::trim).find(|l| !l.is_empty()).unwrap_or("");
    let mut out: String = line.chars().take(max_chars).collect();
    if line.chars().count() > max_chars {
        out.push('…');
    }
    out
}

fn status_word(status: TaskRunStatus) -> &'static str {
    status.as_str()
}

/// The running tasks `owner` started (`None` = the main agent), as a prefix
/// for a message delivered to that agent — or `None` when nothing it started
/// is running, so a message is delivered unchanged.
pub fn live_task_brief(manager: &TaskManager, owner: Option<&str>) -> Option<String> {
    let now = SystemTime::now();
    let running: Vec<TaskSnapshot> = manager
        .live_children(owner)
        .iter()
        .map(|task| task.to_snapshot())
        .filter(|s| s.status == TaskRunStatus::Running)
        .collect();
    if running.is_empty() {
        return None;
    }
    let mut text = String::from(
        "[live-tasks] Tasks you started are still running, with the parameters they were \
         started with. The message below cannot change a running task: if it asks for \
         different parameters, stop the task (task_stop) and start it again, and do not \
         assume the change already took effect.\n",
    );
    for s in running.iter().take(BRIEF_MAX_TASKS) {
        text.push_str(&format!(
            "- {} {} \"{}\" — running {}\n",
            s.id,
            s.kind.as_str(),
            one_line(&s.description, DESCRIPTION_CHARS),
            format_age(age_of(s, now)),
        ));
    }
    if running.len() > BRIEF_MAX_TASKS {
        text.push_str(&format!("- … and {} more (task_list)\n", running.len() - BRIEF_MAX_TASKS));
    }
    text.push_str("[/live-tasks]");
    Some(text)
}

/// Every running task and the most recently finished ones, with owner,
/// status, age and the last few redacted output lines — the live state a
/// side question is answered from. Says so plainly when nothing has run.
pub fn task_snapshot(manager: &TaskManager) -> String {
    let now = SystemTime::now();
    let all = manager.list_with_output(4_000);
    let (running, finished): (Vec<_>, Vec<_>) =
        all.into_iter().partition(|(s, _)| s.status == TaskRunStatus::Running);
    if running.is_empty() && finished.is_empty() {
        return "No tasks have been started in this session.".to_owned();
    }
    let mut text = String::new();
    if running.is_empty() {
        text.push_str("Running tasks: none.\n");
    } else {
        text.push_str(&format!("Running tasks ({}):\n", running.len()));
        for (s, tail) in running.iter().take(SNAPSHOT_MAX_RUNNING) {
            push_task(&mut text, s, tail, now);
        }
        if running.len() > SNAPSHOT_MAX_RUNNING {
            text.push_str(&format!("- … and {} more\n", running.len() - SNAPSHOT_MAX_RUNNING));
        }
    }
    if !finished.is_empty() {
        text.push_str("Recently finished tasks:\n");
        for (s, tail) in finished.iter().rev().take(SNAPSHOT_MAX_FINISHED) {
            push_task(&mut text, s, tail, now);
        }
    }
    text
}

fn push_task(text: &mut String, s: &TaskSnapshot, tail: &str, now: SystemTime) {
    let owner = match (&s.parent_id, s.kind) {
        (_, TaskKind::Scheduled) => "schedule".to_owned(),
        (Some(parent), _) => parent.clone(),
        (None, _) => "main agent".to_owned(),
    };
    let age = format_age(age_of(s, now));
    let timing = if s.status == TaskRunStatus::Running {
        format!("for {age}")
    } else {
        format!("after {age}")
    };
    text.push_str(&format!(
        "- {} [{}] \"{}\" — {} {timing}; started by {owner}\n",
        s.id,
        s.kind.as_str(),
        one_line(&s.description, DESCRIPTION_CHARS),
        status_word(s.status),
    ));
    let lines: Vec<&str> = tail.lines().filter(|l| !l.trim().is_empty()).collect();
    for line in &lines[lines.len().saturating_sub(SNAPSHOT_TAIL_LINES)..] {
        let mut shown: String = line.chars().take(SNAPSHOT_LINE_CHARS).collect();
        if line.chars().count() > SNAPSHOT_LINE_CHARS {
            shown.push('…');
        }
        text.push_str(&format!("    | {shown}\n"));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tasks::{TaskExecutionMode, TaskManager};

    fn manager() -> std::sync::Arc<TaskManager> {
        TaskManager::new("live-brief", Some(std::env::temp_dir().join("coda-live-brief-tests")), 64 * 1024, 16)
    }

    #[test]
    fn no_live_tasks_means_no_prefix() {
        let m = manager();
        assert_eq!(live_task_brief(&m, None), None);
        let done = m.register(TaskKind::Shell, "cargo build", None, TaskExecutionMode::Background).unwrap();
        m.complete(&done.id, Some("exit code: 0".into()));
        assert_eq!(live_task_brief(&m, None), None, "finished work is not live");
    }

    #[test]
    fn the_prefix_lists_only_the_owners_running_tasks() {
        let m = manager();
        let review = m
            .register(TaskKind::Shell, "pwsh review.ps1 -Post", None, TaskExecutionMode::Background)
            .unwrap();
        let agent = m.register(TaskKind::Subagent, "deep review", None, TaskExecutionMode::Background).unwrap();
        let grandchild = m
            .register(TaskKind::Shell, "child build", Some(&agent.id), TaskExecutionMode::Background)
            .unwrap();
        let brief = live_task_brief(&m, None).expect("live tasks");
        assert!(brief.starts_with("[live-tasks]"), "{brief}");
        assert!(brief.contains(&review.id) && brief.contains("pwsh review.ps1 -Post"), "{brief}");
        assert!(brief.contains("shell") && brief.contains("running"), "{brief}");
        assert!(brief.contains(&agent.id) && brief.contains("subagent"), "{brief}");
        assert!(brief.contains("task_stop"), "the model is told how to apply a change: {brief}");
        assert!(!brief.contains(&grandchild.id), "a child's own work is its business: {brief}");

        let child_brief = live_task_brief(&m, Some(&agent.id)).expect("the subagent's task");
        assert!(child_brief.contains(&grandchild.id) && !child_brief.contains(&review.id));
    }

    #[test]
    fn the_snapshot_shows_owner_status_age_and_the_output_tail() {
        let m = manager();
        let review = m.register(TaskKind::Shell, "owasp review", None, TaskExecutionMode::Background).unwrap();
        m.append_output(&review.id, "line 1\nscanning PR 42\n");
        let agent = m.register(TaskKind::Subagent, "deep review", None, TaskExecutionMode::Background).unwrap();
        let child = m.register(TaskKind::Shell, "lint", Some(&agent.id), TaskExecutionMode::Background).unwrap();
        m.fail(&child.id, Some("exit code: 1".into()));

        let snapshot = task_snapshot(&m);
        assert!(snapshot.contains("Running tasks (2)"), "{snapshot}");
        assert!(snapshot.contains(&review.id) && snapshot.contains("started by main agent"), "{snapshot}");
        assert!(snapshot.contains("| scanning PR 42"), "{snapshot}");
        assert!(snapshot.contains("Recently finished") && snapshot.contains("failed"), "{snapshot}");
        assert!(snapshot.contains(&format!("started by {}", agent.id)), "{snapshot}");
    }

    #[test]
    fn an_empty_session_says_so() {
        assert_eq!(task_snapshot(&manager()), "No tasks have been started in this session.");
    }

    #[test]
    fn ages_read_naturally() {
        assert_eq!(format_age(45), "45s");
        assert_eq!(format_age(187), "3m 07s");
        assert_eq!(format_age(7500), "2h 05m");
    }
}
