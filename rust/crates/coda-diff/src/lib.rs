//! Differential testing: the C# and Rust engines must answer identically.
//!
//! Unit tests prove each engine behaves as *its own* tests expect. That is not
//! the same as the two agreeing, and a test written beside an implementation
//! tends to encode that implementation's assumptions. This port has already
//! produced a concrete example: the Rust engine reported
//! `serverInfo: "coda-serve"` where the C# reports `"coda"`, and the Rust unit
//! test had pinned the wrong value — so it agreed with the bug. Only running
//! the *same* exchange against *both* engines exposed it.
//!
//! This harness sends an identical request sequence to both and compares the
//! normalised responses. It is the strongest parity evidence available, and it
//! is only available while both engines exist.
//!
//! # Scope
//!
//! Deliberately limited to the **deterministic** surface: the handshake,
//! history, models, interrupt, steering, goals, effort, schedules, listings
//! and error codes. Anything that requires a live model is excluded, because a
//! provider's output is not reproducible and a flaky parity test is worse than
//! none — it teaches you to ignore red.

use std::time::Duration;

use anyhow::{Context, Result};
use coda_client::{Engine, EngineCommand};
use serde_json::{json, Value};

/// How long any single request may take before the comparison is abandoned.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(60);

/// One request in a scenario.
#[derive(Debug, Clone)]
pub struct Step {
    pub method: &'static str,
    pub params: Option<Value>,
}

impl Step {
    pub fn new(method: &'static str, params: Option<Value>) -> Self {
        Self { method, params }
    }
}

/// The outcome of one step, reduced to what parity actually requires.
///
/// A successful result is compared structurally; an error is compared by
/// **code only**. Error *messages* are prose and legitimately differ between
/// implementations — requiring them to match would pin wording rather than
/// behaviour, and would fail on a harmless rephrasing.
#[derive(Debug, Clone, PartialEq)]
pub enum StepOutcome {
    Ok(Value),
    Error { code: i64 },
}

/// Replaces values that legitimately differ between two runs.
///
/// Session ids, timestamps, durations and generated identifiers are volatile
/// by nature; comparing them would fail every time regardless of parity. They
/// are replaced by a marker rather than deleted so that a field *present in
/// one engine and absent in the other* is still caught.
///
/// Human-readable prose is handled differently again. A field like `note` or
/// `message` carries wording that may reasonably differ between two
/// implementations, so comparing it would pin phrasing rather than behaviour.
/// Non-empty prose collapses to a marker — but an **empty string stays empty**,
/// because "" and "absent" are different on the wire and that distinction has
/// already caught one real bug here.
pub fn normalize(value: &Value) -> Value {
    const VOLATILE: &[&str] = &[
        "sessionId",
        "messageId",
        "taskId",
        "activeTaskId",
        "id",
        "rootTurnId",
        "activityId",
        "callId",
        "sourceId",
        "timestamp",
        "enqueuedAt",
        "nextRunUtc",
        "elapsedMs",
        "elapsedSeconds",
        "telemetryLogPath",
    ];
    const PROSE: &[&str] = &["note", "message", "reason", "error", "description", "summary"];

    match value {
        Value::Object(map) => {
            let mut out = serde_json::Map::new();
            for (key, val) in map {
                if VOLATILE.contains(&key.as_str()) {
                    out.insert(key.clone(), Value::String("<volatile>".into()));
                } else if PROSE.contains(&key.as_str()) {
                    let collapsed = match val.as_str() {
                        Some("") => Value::String(String::new()),
                        Some(_) => Value::String("<prose>".into()),
                        None => normalize(val),
                    };
                    out.insert(key.clone(), collapsed);
                } else {
                    out.insert(key.clone(), normalize(val));
                }
            }
            Value::Object(out)
        }
        Value::Array(items) => Value::Array(items.iter().map(normalize).collect()),
        other => other.clone(),
    }
}

/// A running engine under test.
pub struct EngineUnderTest {
    engine: Engine,
    connection: coda_client::Connection,
}

impl EngineUnderTest {
    /// Starts `<program> serve` and completes the handshake.
    pub async fn start(program: &std::ffi::OsStr) -> Result<Self> {
        let command = EngineCommand::new(program)
            .arg("serve")
            .working_dir(std::env::temp_dir());
        let (engine, _inbound) = Engine::spawn(command).context("failed to spawn the engine")?;
        let connection = engine.connection();

        let params = serde_json::to_value(coda_proto::messages::InitializeParams::new("coda-diff"))
            .context("failed to serialise the handshake")?;
        tokio::time::timeout(
            REQUEST_TIMEOUT,
            connection.request(coda_proto::messages::method::INITIALIZE, Some(params)),
        )
        .await
        .context("initialize timed out")?
        .context("initialize failed")?;

        Ok(Self { engine, connection })
    }

    /// Runs one step and reduces it to a comparable outcome.
    pub async fn run(&self, step: &Step) -> Result<StepOutcome> {
        let result = tokio::time::timeout(
            REQUEST_TIMEOUT,
            self.connection.request(step.method, step.params.clone()),
        )
        .await
        .with_context(|| format!("{} timed out", step.method))?;

        Ok(match result {
            Ok(value) => StepOutcome::Ok(normalize(&value)),
            Err(coda_client::ClientError::Rpc(error)) => StepOutcome::Error { code: error.code },
            Err(other) => return Err(other).context("transport failure"),
        })
    }

    pub async fn shutdown(self) {
        let _ = self.engine.shutdown(Duration::from_secs(5)).await;
    }
}

/// Locates an engine, returning `None` so a test can skip rather than fail.
///
/// The C# engine will not be present forever — it is being replaced — so a
/// missing engine must not turn into a red suite.
pub fn find_engine(program: &str) -> Option<std::ffi::OsString> {
    let status = std::process::Command::new(program)
        .arg("--version")
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status();
    matches!(status, Ok(s) if s.success()).then(|| program.into())
}

/// Runs every step against both engines and returns the disagreements.
///
/// Returns `(step_index, method, csharp, rust)` for each mismatch, so a
/// failure names the exact exchange rather than dumping two transcripts.
pub async fn compare(
    csharp: &EngineUnderTest,
    rust: &EngineUnderTest,
    steps: &[Step],
) -> Result<Vec<(usize, &'static str, StepOutcome, StepOutcome)>> {
    let mut mismatches = Vec::new();
    for (index, step) in steps.iter().enumerate() {
        let left = csharp.run(step).await?;
        let right = rust.run(step).await?;
        if left != right {
            mismatches.push((index, step.method, left, right));
        }
    }
    Ok(mismatches)
}

/// Methods whose divergence is a **known, declared gap** rather than a defect.
///
/// Each entry is a Rust feature that is not wired yet, listed in `rust/README.md`.
/// The harness tolerates exactly these and fails on anything else, so a new
/// divergence is loud while known work-in-progress does not keep the suite red.
/// A permanently-failing test gets ignored, and an ignored test protects
/// nothing.
///
/// **This list must shrink to empty before the C# engine is removed.** When a
/// gap is closed, the harness fails until its entry is deleted — which keeps
/// the list honest rather than letting it rot into a permanent excuse.
pub const KNOWN_GAPS: &[(&str, &str)] = &[
    (
        "session/models",
        "the Rust engine has no provider/credential stack wired, so it returns the builtin (empty) catalogue",
    ),
    (
        "model/reasoningCapability",
        "the Rust engine has no per-model metadata, so it reports no reasoning support",
    ),
    (
        "skills/list",
        "the Rust engine has no skill registry wired, so it returns an empty list",
    ),
];

/// The deterministic scenario: everything that does not need a live model.
pub fn deterministic_scenario() -> Vec<Step> {    use coda_proto::messages::method;

    vec![
        Step::new(method::HISTORY, Some(json!({}))),
        Step::new(method::MESSAGES, Some(json!({ "sinceIndex": 0 }))),
        Step::new(method::MODELS, Some(json!({ "refresh": false }))),
        Step::new(method::REASONING_CAPABILITY, Some(json!({}))),
        Step::new(method::INTERRUPT, Some(json!({}))),
        Step::new(method::RECALL_STEERING, Some(json!({}))),
        // Steering with no turn running must be refused by both.
        Step::new(method::STEER, Some(json!({ "text": "hello" }))),
        // Goal validation, including the invalid-duration error code.
        Step::new(method::SET_GOAL, Some(json!({ "goal": "ship it" }))),
        Step::new(method::SET_GOAL, Some(json!({ "goal": "x", "maxDuration": "not-a-duration" }))),
        Step::new(method::SET_GOAL, Some(json!({}))),
        // Effort: an unsupported value is ok:false, NOT an error.
        Step::new(method::SET_EFFORT, Some(json!({ "effort": "medium" }))),
        Step::new(method::SET_EFFORT, Some(json!({ "effort": "not-a-level" }))),
        Step::new(method::SET_EFFORT, Some(json!({}))),
        // Listings.
        Step::new(method::SCHEDULE_LIST, Some(json!({}))),
        Step::new(method::HOOKS_LIST, Some(json!({}))),
        Step::new(method::SKILLS_LIST, Some(json!({}))),
        Step::new(method::PLUGINS_LIST, Some(json!({}))),
        // Error paths.
        Step::new("does/notExist", Some(json!({}))),
        Step::new(method::HOOKS_INFO, Some(json!({ "index": 9999 }))),
        Step::new(method::HOOKS_TRUST, Some(json!({}))),
        Step::new(method::SCHEDULE_DELETE, Some(json!({ "id": "no-such-schedule" }))),
        Step::new(method::SCHEDULE_CREATE, Some(json!({ "prompt": "x" }))),
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn volatile_fields_are_replaced_not_dropped() {
        let value = json!({ "sessionId": "abc", "serverInfo": "coda" });
        let normalized = normalize(&value);
        assert_eq!(normalized["sessionId"], "<volatile>");
        assert_eq!(normalized["serverInfo"], "coda");
    }

    /// Replacing rather than deleting matters: a field present in one engine
    /// and missing in the other must still be caught.
    #[test]
    fn a_missing_volatile_field_still_differs_from_a_present_one() {
        let with = normalize(&json!({ "sessionId": "abc" }));
        let without = normalize(&json!({}));
        assert_ne!(with, without);
    }

    #[test]
    fn normalization_reaches_into_arrays_and_nested_objects() {
        let value = json!({
            "schedules": [ { "id": "s1", "name": "nightly" } ],
            "nested": { "taskId": "t1", "kind": "interval" }
        });
        let normalized = normalize(&value);
        assert_eq!(normalized["schedules"][0]["id"], "<volatile>");
        assert_eq!(normalized["schedules"][0]["name"], "nightly");
        assert_eq!(normalized["nested"]["taskId"], "<volatile>");
        assert_eq!(normalized["nested"]["kind"], "interval");
    }

    /// Errors compare by code only — messages are prose and may legitimately
    /// differ between two implementations.
    #[test]
    fn errors_compare_by_code_not_message() {
        assert_eq!(StepOutcome::Error { code: -32602 }, StepOutcome::Error { code: -32602 });
        assert_ne!(StepOutcome::Error { code: -32602 }, StepOutcome::Error { code: -32603 });
    }

    /// Differently-worded prose must not be reported as a parity break.
    #[test]
    fn differing_prose_compares_equal() {
        let a = normalize(&json!({ "ok": false, "note": "Invalid effort level 'x'" }));
        let b = normalize(&json!({ "ok": false, "note": "unsupported effort: x" }));
        assert_eq!(a, b, "wording differences are not behaviour differences");
    }

    /// But an empty string is not the same as an absent field: the C# engine
    /// emits `note: ""` where the Rust engine omitted it entirely, and that
    /// distinction is a genuine wire difference this harness caught.
    #[test]
    fn an_empty_prose_field_differs_from_an_absent_one() {
        let empty = normalize(&json!({ "ok": true, "note": "" }));
        let absent = normalize(&json!({ "ok": true }));
        assert_ne!(empty, absent, "an empty string and an absent field differ on the wire");
    }

    /// And an empty string is not the same as prose either.
    #[test]
    fn an_empty_prose_field_differs_from_a_populated_one() {
        let empty = normalize(&json!({ "note": "" }));
        let populated = normalize(&json!({ "note": "something" }));
        assert_ne!(empty, populated);
    }

    #[test]
    fn the_scenario_covers_both_success_and_error_paths() {
        let steps = deterministic_scenario();
        assert!(steps.len() >= 20, "the scenario should be substantial");
        assert!(
            steps.iter().any(|s| s.method == "does/notExist"),
            "an unknown method must be exercised"
        );
    }
}
