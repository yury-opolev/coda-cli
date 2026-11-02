//! The planned-restart file (`.coda/sessions/<id>.restart.json`).
//!
//! `session/prepareRestart` drains the engine and writes one [`RestartFile`]
//! holding everything that otherwise lives only in memory: schedules,
//! undelivered notifications, protocol-set settings, queued steering, what
//! the drain had to interrupt, and the ids of the subagents saved suspended
//! (their own files sit in `.coda/sessions/<id>.tasks/`). The next engine
//! validates it with [`check`] and tells the model what happened with
//! [`restart_note`].

use chrono::{DateTime, Duration, Utc};
use serde::{Deserialize, Serialize};

/// Format version written by this engine; newer files are refused.
pub const RESTART_FILE_VERSION: u32 = 1;

/// How old a restart file may be and still be applied.
pub const RESTART_MAX_AGE: Duration = Duration::hours(24);

/// How far in the future `preparedAt` may lie (clock skew) and still count.
const RESTART_MAX_FUTURE_SKEW: Duration = Duration::minutes(5);

/// Characters of interrupted output quoted in the `[restart]` note.
const NOTE_OUTPUT_TAIL_CHARS: usize = 300;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RestartFile {
    pub version: u32,
    pub coda_version: String,
    pub session_id: String,
    pub working_dir: String,
    pub prepared_at: DateTime<Utc>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    /// Full `ScheduledTask` JSON, one per definition.
    #[serde(default)]
    pub schedules: Vec<serde_json::Value>,
    #[serde(default)]
    pub messages: Vec<SavedMessage>,
    #[serde(default)]
    pub settings: SavedSettings,
    /// Steering the running turn had not consumed yet.
    #[serde(default)]
    pub steering: Vec<String>,
    #[serde(default)]
    pub interrupted: Interrupted,
    /// Ids of the subagents saved suspended (files in the tasks dir).
    #[serde(default)]
    pub tasks: Vec<String>,
}

/// One undelivered message: a `notify_user` notification (`channel: "user"`)
/// or a pending `ask_main` item (`channel: "main"`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SavedMessage {
    /// `"user"` | `"main"`.
    pub channel: String,
    /// The user ring cursor, or the main inbox sequence number.
    pub cursor: u64,
    pub id: String,
    /// `scheduledTask` | `subagent` | `main` | `taskCompletion`.
    pub source: String,
    pub label: String,
    pub text: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub context: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub task_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schedule_definition_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schedule_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub idempotency_key: Option<String>,
}

impl SavedMessage {
    pub fn from_user(message: &coda_agent::UserMessage) -> Self {
        Self {
            channel: "user".to_owned(),
            cursor: message.cursor,
            id: message.id.clone(),
            source: message.source_kind.to_owned(),
            label: message.label.clone(),
            text: message.body.clone(),
            context: message.context.clone(),
            task_id: message.task_id.clone(),
            schedule_definition_id: message.schedule_definition_id.clone(),
            schedule_name: message.schedule_name.clone(),
            idempotency_key: message.idempotency_key.clone(),
        }
    }

    pub fn from_main(message: &coda_agent::MainMessage) -> Self {
        Self {
            channel: "main".to_owned(),
            cursor: message.seq,
            id: message.id.clone(),
            source: message.source_kind.to_owned(),
            label: message.label.clone(),
            text: message.body.clone(),
            context: message.context.clone(),
            task_id: message.task_id.clone(),
            schedule_definition_id: message.schedule_definition_id.clone(),
            schedule_name: message.schedule_name.clone(),
            idempotency_key: message.idempotency_key.clone(),
        }
    }

    /// The trusted source to re-publish this message under; `None` when the
    /// stored source cannot be rebuilt.
    pub fn message_source(&self) -> Option<coda_agent::MessageSource> {
        coda_agent::MessageSource::from_saved(
            &self.source,
            self.task_id.as_deref(),
            &self.label,
            self.schedule_definition_id.as_deref(),
            self.schedule_name.as_deref(),
        )
    }
}

/// Every undelivered notification after `delivered_cursor` (all retained ones
/// for `0`) followed by every pending main-inbox item. Nothing is consumed.
pub fn snapshot_messages(bus: &coda_agent::MessageBus, delivered_cursor: u64) -> Vec<SavedMessage> {
    let mut saved: Vec<SavedMessage> = bus
        .user_since(delivered_cursor, None)
        .messages
        .iter()
        .map(SavedMessage::from_user)
        .collect();
    saved.extend(bus.pending_main_snapshot().iter().map(SavedMessage::from_main));
    saved
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SavedSettings {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provider: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub effort: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub goal: Option<SavedGoal>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub permission_mode: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub system_prompt: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SavedGoal {
    pub goal: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_duration: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_continuations: Option<i32>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Interrupted {
    #[serde(default)]
    pub tasks: Vec<InterruptedTask>,
    #[serde(default)]
    pub requests: Vec<InterruptedRequest>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InterruptedTask {
    pub task_id: String,
    /// A `TaskKind` token: `shell`, `tool`, `scheduled`, `subagent`.
    pub kind: String,
    pub description: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub command: Option<String>,
    pub started_at: DateTime<Utc>,
    pub output_tail: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InterruptedRequest {
    /// `permission` | `question` | `planApproval`.
    pub kind: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tool_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub question: Option<String>,
}

impl InterruptedRequest {
    /// The saved form of a pending request: `toolName` from a permission's
    /// display, the question text (plain or capped `{text}`) from a question's.
    pub fn from_pending(request: &coda_proto::state::PendingRequestDto) -> Self {
        let text = |value: &serde_json::Value| -> Option<String> {
            value
                .as_str()
                .or_else(|| value.get("text").and_then(serde_json::Value::as_str))
                .map(str::to_owned)
        };
        Self {
            kind: request.kind.as_str().to_owned(),
            tool_name: request.display.get("toolName").and_then(text),
            question: request.display.get("question").and_then(text),
        }
    }
}

/// The outcome of validating a restart file before a restore.
#[derive(Debug, Clone, PartialEq)]
pub enum RestartFileCheck {
    Valid(RestartFile),
    /// `missing` | `unreadable` | `sessionMismatch` | `workingDirMismatch` | `expired`.
    Ignored(&'static str),
    /// The file's format version is newer than [`RESTART_FILE_VERSION`].
    Unsupported(u32),
}

/// Validates the bytes of a restart file (`None` when there is no file) for
/// `session_id` in `working_dir` at `now`. The version is read first and
/// leniently, so a newer file is reported `Unsupported` whatever its shape.
pub fn check(
    bytes: Option<&[u8]>,
    session_id: &str,
    working_dir: &str,
    now: DateTime<Utc>,
) -> RestartFileCheck {
    let Some(bytes) = bytes else { return RestartFileCheck::Ignored("missing") };
    let Ok(value) = serde_json::from_slice::<serde_json::Value>(bytes) else {
        return RestartFileCheck::Ignored("unreadable");
    };
    let Some(version) = value.get("version").and_then(serde_json::Value::as_u64) else {
        return RestartFileCheck::Ignored("unreadable");
    };
    if version > u64::from(RESTART_FILE_VERSION) {
        return RestartFileCheck::Unsupported(u32::try_from(version).unwrap_or(u32::MAX));
    }
    let Ok(file) = serde_json::from_value::<RestartFile>(value) else {
        return RestartFileCheck::Ignored("unreadable");
    };
    if file.session_id != session_id {
        return RestartFileCheck::Ignored("sessionMismatch");
    }
    if normalize_dir(&file.working_dir) != normalize_dir(working_dir) {
        return RestartFileCheck::Ignored("workingDirMismatch");
    }
    if now - file.prepared_at > RESTART_MAX_AGE || file.prepared_at - now > RESTART_MAX_FUTURE_SKEW {
        return RestartFileCheck::Ignored("expired");
    }
    RestartFileCheck::Valid(file)
}

/// One separator style, no trailing separator, and (on Windows, whose file
/// system is case-insensitive) one case.
fn normalize_dir(dir: &str) -> String {
    let unified = dir.trim().replace('\\', "/");
    let trimmed = unified.trim_end_matches('/');
    let trimmed = if trimmed.is_empty() { unified.as_str() } else { trimmed };
    if cfg!(windows) {
        trimmed.to_lowercase()
    } else {
        trimmed.to_owned()
    }
}

/// The `[restart]` message appended to the restored conversation, telling
/// the model what the restart did to its work. `to_version` is the version
/// of the engine that restores.
pub fn restart_note(file: &RestartFile, to_version: &str) -> String {
    let reason = file.reason.as_deref().map(single_line).filter(|r| !r.is_empty());
    let mut lines = vec![match (file.coda_version != to_version, reason) {
        (true, None) => format!(
            "[restart] Coda was restarted for an upgrade ({} → {to_version}).",
            file.coda_version
        ),
        (true, Some(reason)) => format!(
            "[restart] Coda was restarted for an upgrade ({} → {to_version}): {reason}.",
            file.coda_version
        ),
        (false, None) => "[restart] Coda was restarted.".to_owned(),
        (false, Some(reason)) => format!("[restart] Coda was restarted ({reason})."),
    }];
    if !file.steering.is_empty() {
        let quoted: Vec<String> =
            file.steering.iter().map(|t| format!("\"{}\"", single_line(t))).collect();
        lines.push(format!(
            "Steering sent before the restart that was not delivered: {}.",
            quoted.join("; ")
        ));
    }
    if !file.interrupted.tasks.is_empty() {
        let tasks: Vec<String> = file.interrupted.tasks.iter().map(describe_interrupted).collect();
        lines.push(format!("Interrupted: {}.", tasks.join("; ")));
    }
    if !file.interrupted.requests.is_empty() {
        let requests: Vec<String> =
            file.interrupted.requests.iter().map(describe_request).collect();
        lines.push(format!("Declined requests: {}.", requests.join("; ")));
    }
    if !file.tasks.is_empty() {
        lines.push(format!("Suspended subagents restored: {}.", file.tasks.join(", ")));
    }
    if !file.interrupted.tasks.is_empty() || !file.interrupted.requests.is_empty() {
        lines.push("Re-run anything still needed.".to_owned());
    }
    lines.join("\n")
}

fn describe_interrupted(task: &InterruptedTask) -> String {
    let what = match (task.kind.as_str(), &task.command) {
        ("shell", Some(command)) => format!("run_command {}", single_line(command)),
        (kind, _) => format!("{kind} {}", single_line(&task.description)),
    };
    let started = task.started_at.format("%H:%M UTC");
    let tail = single_line(&task.output_tail);
    if tail.is_empty() {
        return format!("{what} (started {started}, no output)");
    }
    let count = tail.chars().count();
    let tail = if count > NOTE_OUTPUT_TAIL_CHARS {
        let kept: String = tail.chars().skip(count - NOTE_OUTPUT_TAIL_CHARS).collect();
        format!("…{kept}")
    } else {
        tail
    };
    format!("{what} (started {started}, last output: {tail})")
}

fn describe_request(request: &InterruptedRequest) -> String {
    match request.kind.as_str() {
        "permission" => match &request.tool_name {
            Some(tool) => format!("permission for {tool}"),
            None => "permission".to_owned(),
        },
        "question" => match &request.question {
            Some(question) => format!("question \"{}\"", single_line(question)),
            None => "question".to_owned(),
        },
        "planApproval" => "plan approval".to_owned(),
        other => other.to_owned(),
    }
}

fn single_line(text: &str) -> String {
    text.split_whitespace().collect::<Vec<_>>().join(" ")
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    fn sample(prepared_at: DateTime<Utc>) -> RestartFile {
        RestartFile {
            version: RESTART_FILE_VERSION,
            coda_version: "0.1.176".into(),
            session_id: "s-1".into(),
            working_dir: r"C:\Work\Repo".into(),
            prepared_at,
            reason: None,
            schedules: Vec::new(),
            messages: Vec::new(),
            settings: SavedSettings::default(),
            steering: Vec::new(),
            interrupted: Interrupted::default(),
            tasks: Vec::new(),
        }
    }

    fn bytes(file: &RestartFile) -> Vec<u8> {
        serde_json::to_vec(file).unwrap()
    }

    #[test]
    fn check_ignores_missing_mismatched_expired_and_refuses_newer() {
        let now = Utc.with_ymd_and_hms(2026, 11, 2, 12, 0, 0).unwrap();
        let file = sample(now - Duration::minutes(3));
        let dir = r"C:\Work\Repo";
        let ok = |b: &[u8], wd: &str| check(Some(b), "s-1", wd, now);

        assert_eq!(check(None, "s-1", dir, now), RestartFileCheck::Ignored("missing"));
        assert_eq!(ok(b"{not json", dir), RestartFileCheck::Ignored("unreadable"));
        assert_eq!(ok(br#"{"version":1}"#, dir), RestartFileCheck::Ignored("unreadable"));
        assert_eq!(ok(&bytes(&file), dir), RestartFileCheck::Valid(file.clone()));
        assert_eq!(
            check(Some(&bytes(&file)), "s-2", dir, now),
            RestartFileCheck::Ignored("sessionMismatch")
        );
        assert_eq!(ok(&bytes(&file), r"C:\Work\Other"), RestartFileCheck::Ignored("workingDirMismatch"));
        let same_dir = if cfg!(windows) { "c:/work/repo/" } else { r"C:\Work\Repo\" };
        assert!(matches!(ok(&bytes(&file), same_dir), RestartFileCheck::Valid(_)));

        let at_limit = sample(now - RESTART_MAX_AGE + Duration::seconds(1));
        assert!(matches!(ok(&bytes(&at_limit), dir), RestartFileCheck::Valid(_)));
        let too_old = sample(now - RESTART_MAX_AGE - Duration::seconds(1));
        assert_eq!(ok(&bytes(&too_old), dir), RestartFileCheck::Ignored("expired"));
        let future = sample(now + Duration::minutes(6));
        assert_eq!(ok(&bytes(&future), dir), RestartFileCheck::Ignored("expired"));
        let skewed = sample(now + Duration::minutes(4));
        assert!(matches!(ok(&bytes(&skewed), dir), RestartFileCheck::Valid(_)));

        assert_eq!(
            ok(br#"{"version":2,"somethingNew":true}"#, dir),
            RestartFileCheck::Unsupported(2),
            "a newer file is refused whatever its shape"
        );
    }

    #[test]
    fn restart_note_lists_steering_interrupted_command_and_versions() {
        let prepared = Utc.with_ymd_and_hms(2026, 11, 2, 12, 0, 0).unwrap();
        let mut file = sample(prepared);
        file.steering = vec!["also check X".into(), "and\nY".into()];
        file.interrupted.tasks.push(InterruptedTask {
            task_id: "task-00000003".into(),
            kind: "shell".into(),
            description: "npm test".into(),
            command: Some("npm test".into()),
            started_at: Utc.with_ymd_and_hms(2026, 11, 2, 12, 1, 30).unwrap(),
            output_tail: format!("{}\n  12 passing\n", "x".repeat(400)),
        });
        file.interrupted.tasks.push(InterruptedTask {
            task_id: "task-00000004".into(),
            kind: "subagent".into(),
            description: "review the diff".into(),
            command: None,
            started_at: Utc.with_ymd_and_hms(2026, 11, 2, 9, 5, 0).unwrap(),
            output_tail: String::new(),
        });
        file.interrupted.requests = vec![
            InterruptedRequest { kind: "permission".into(), tool_name: Some("run_command".into()), question: None },
            InterruptedRequest { kind: "question".into(), tool_name: None, question: Some("Delete it?".into()) },
        ];
        file.tasks = vec!["task-00000002".into()];

        let tail = format!("…{} 12 passing", "x".repeat(300 - " 12 passing".len()));
        assert_eq!(
            restart_note(&file, "0.1.177"),
            format!(
                "[restart] Coda was restarted for an upgrade (0.1.176 → 0.1.177).\n\
                 Steering sent before the restart that was not delivered: \"also check X\"; \"and Y\".\n\
                 Interrupted: run_command npm test (started 12:01 UTC, last output: {tail}); \
                 subagent review the diff (started 09:05 UTC, no output).\n\
                 Declined requests: permission for run_command; question \"Delete it?\".\n\
                 Suspended subagents restored: task-00000002.\n\
                 Re-run anything still needed."
            )
        );

        let mut plain = sample(prepared);
        plain.reason = Some("config reload".into());
        assert_eq!(restart_note(&plain, "0.1.176"), "[restart] Coda was restarted (config reload).");
        plain.reason = None;
        assert_eq!(restart_note(&plain, "0.1.176"), "[restart] Coda was restarted.");
    }
}
