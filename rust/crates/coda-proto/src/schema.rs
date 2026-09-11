//! Feature-gated JSON schemas derived from the public wire DTOs.

mod legacy;
mod catalog;

use std::collections::BTreeMap;

use serde_json::Value;
use crate::state_events::{
    ActivityEvent, ConfigChangedEvent, EventsDroppedEvent, LifecycleEvent,
    RequestPendingEvent, RequestResolvedEvent, Sequenced, SessionChangedEvent,
    SteeringQueueEvent, TurnEndedEvent,
};

pub fn catalog_document() -> Value {
    catalog::document()
}

/// Generate schemas without touching the filesystem or engine state.
pub fn documents() -> BTreeMap<&'static str, Value> {
    let mut schemas = BTreeMap::from([
        ("StateSnapshot.json", schemars::schema_for!(crate::state::StateSnapshot).to_value()),
        ("GetEventsResult.json", schemars::schema_for!(crate::events::GetEventsResult).to_value()),
        ("EventEnvelope.json", schemars::schema_for!(crate::events::EventEnvelope).to_value()),
        ("GetHistoryResult.json", schemars::schema_for!(crate::history::GetHistoryResult).to_value()),
        ("ListSessionsResult.json", schemars::schema_for!(crate::history::ListSessionsResult).to_value()),
        ("ConfigDescribeResult.json", schemars::schema_for!(crate::config::ConfigDescribeResult).to_value()),
        ("ConfigSetResult.json", schemars::schema_for!(crate::config::ConfigSetResult).to_value()),
        ("McpListResult.json", schemars::schema_for!(crate::mcp::McpListResult).to_value()),
        ("InitializeParams.json", schemars::schema_for!(crate::messages::InitializeParams).to_value()),
        ("InitializeResult.json", schemars::schema_for!(crate::messages::InitializeResult).to_value()),
        ("InitializeResponse.json", schemars::schema_for!(crate::messages::InitializeResponse).to_value()),
        ("SetModelParams.json", schemars::schema_for!(crate::requests::SetModelParams).to_value()),
        ("SetModelResult.json", schemars::schema_for!(crate::responses::SetModelResult).to_value()),
        ("HooksInfoParams.json", schemars::schema_for!(crate::requests::HooksInfoParams).to_value()),
        ("HooksInfoResult.json", schemars::schema_for!(crate::messages::WireHook).to_value()),
        ("HooksTrustParams.json", schemars::schema_for!(crate::requests::HooksTrustParams).to_value()),
        ("HooksTrustResult.json", schemars::schema_for!(crate::responses::HooksTrustResult).to_value()),
        ("ForkParams.json", schemars::schema_for!(crate::requests::ForkParams).to_value()),
        ("ForkResponse.json", schemars::schema_for!(crate::responses::ForkResponse).to_value()),
        ("RewindParams.json", schemars::schema_for!(crate::requests::RewindParams).to_value()),
        ("RewindResponse.json", schemars::schema_for!(crate::responses::RewindResponse).to_value()),
        ("GetStateParams.json", schemars::schema_for!(crate::requests::GetStateParams).to_value()),
        ("GetEventsParams.json", schemars::schema_for!(crate::requests::GetEventsParams).to_value()),
        ("GetHistoryParams.json", schemars::schema_for!(crate::requests::GetHistoryParams).to_value()),
        ("ListSessionsParams.json", schemars::schema_for!(crate::requests::ListSessionsParams).to_value()),
        ("ResolveRequestParams.json", schemars::schema_for!(crate::requests::ResolveRequestParams).to_value()),
        ("CancelRequestParams.json", schemars::schema_for!(crate::requests::CancelRequestParams).to_value()),
        ("ConfigSetParams.json", schemars::schema_for!(crate::requests::ConfigSetParams).to_value()),
        ("GetPendingRequestsResult.json", schemars::schema_for!(crate::requests::GetPendingRequestsResult).to_value()),
        ("ResolveRequestResult.json", schemars::schema_for!(crate::requests::ResolveRequestResult).to_value()),
        ("CancelRequestResult.json", schemars::schema_for!(crate::requests::CancelRequestResult).to_value()),
        ("ActivityEvent.json", schemars::schema_for!(Sequenced<ActivityEvent>).to_value()),
        ("LifecycleEvent.json", schemars::schema_for!(Sequenced<LifecycleEvent>).to_value()),
        ("SteeringQueueEvent.json", schemars::schema_for!(Sequenced<SteeringQueueEvent>).to_value()),
        ("ConfigChangedEvent.json", schemars::schema_for!(Sequenced<ConfigChangedEvent>).to_value()),
        ("SessionChangedEvent.json", schemars::schema_for!(Sequenced<SessionChangedEvent>).to_value()),
        ("TurnEndedEvent.json", schemars::schema_for!(Sequenced<TurnEndedEvent>).to_value()),
        ("RequestPendingEvent.json", schemars::schema_for!(Sequenced<RequestPendingEvent>).to_value()),
        ("RequestResolvedEvent.json", schemars::schema_for!(Sequenced<RequestResolvedEvent>).to_value()),
        ("EventsDroppedEvent.json", schemars::schema_for!(Sequenced<EventsDroppedEvent>).to_value()),
    ]);
    for (name, schema) in legacy::documents() {
        assert!(schemas.insert(name, schema).is_none(), "duplicate schema name: {name}");
    }
    for (name, schema) in crate::events::schema::documents() {
        assert!(schemas.insert(name, schema).is_none(), "duplicate schema name: {name}");
    }
    for (name, schema) in &mut schemas {
        schema["title"] = Value::from(name.trim_end_matches(".json"));
    }
    schemas
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapshot_schema_preserves_required_camel_case_wire_fields() {
        let schemas = documents();
        let snapshot = &schemas["StateSnapshot.json"];
        let properties = snapshot["properties"].as_object().unwrap();
        assert!(properties.contains_key("engineInstanceId"));
        assert!(properties.contains_key("historyEpoch"));
        assert!(!properties.contains_key("engine_instance_id"));
        assert_eq!(properties["cursor"]["type"], "integer");
        let required = snapshot["required"].as_array().unwrap();
        for field in ["engineInstanceId", "cursor", "historyEpoch", "steering", "initialized"] {
            assert!(required.iter().any(|item| item == field), "{field} must be required");
        }
    }

    #[test]
    fn history_schema_keeps_explicit_omissions_but_no_opaque_reasoning_fields() {
        let schemas = documents();
        let variants = schemas["GetHistoryResult.json"]["$defs"]["HistoryBlock"]["oneOf"]
            .as_array().unwrap();
        let mut omission = false;
        for variant in variants {
            let properties = variant["properties"].as_object().unwrap();
            omission |= properties.contains_key("omittedReason");
            for forbidden in ["signature", "encryptedContent", "encrypted_content", "data"] {
                assert!(!properties.contains_key(forbidden), "{forbidden} must stay internal");
            }
        }
        assert!(omission, "truncation metadata must remain public");
    }

    #[test]
    fn schedule_run_limits_require_at_least_one_run_in_the_schema() {
        let schemas = documents();
        for name in ["ScheduleCreateParams.json", "ScheduledTask.json"] {
            assert_eq!(schemas[name]["properties"]["maxRuns"]["minimum"].as_f64(), Some(1.0), "{name}");
        }
    }

    #[test]
    fn generated_files_match_wire_schemas() {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .ancestors().nth(3).unwrap().join("docs").join("protocol").join("schemas");
        for (name, schema) in documents() {
            let path = root.join(name);
            let contents = std::fs::read_to_string(&path).unwrap_or_else(|error| {
                panic!("{}: {error}; run emit_schemas with the schema feature", path.display())
            });
            let checked_in: serde_json::Value = serde_json::from_str(&contents).unwrap();
            assert_eq!(checked_in, schema, "schema drift in {name}; regenerate deliberately");
        }
        let mut actual: Vec<_> = std::fs::read_dir(root).unwrap().map(|entry| entry.unwrap().path())
            .filter(|path| path.extension().is_some_and(|extension| extension == "json"))
            .map(|path| path.file_name().unwrap().to_string_lossy().into_owned())
            .collect();
        actual.sort();
        let expected: Vec<_> = documents().keys().map(|name| name.to_string()).collect();
        assert_eq!(actual, expected, "remove obsolete schemas deliberately");
    }

    #[test]
    fn additive_requests_have_shared_schemas_and_omit_absent_fences() {
        let schemas = documents();
        for name in [
            "GetStateParams.json", "GetEventsParams.json", "GetHistoryParams.json",
            "ListSessionsParams.json", "ResolveRequestParams.json", "CancelRequestParams.json",
            "ConfigSetParams.json",
        ] {
            assert!(schemas.contains_key(name), "missing request schema: {name}");
        }
        let history = crate::requests::GetHistoryParams {
            history_epoch: Some(7), expected_history_length: Some(42),
            ..Default::default()
        };
        let value = serde_json::to_value(history).unwrap();
        assert_eq!(value, serde_json::json!({ "historyEpoch": 7, "expectedHistoryLength": 42 }));
    }

    #[test]
    fn pending_interaction_results_have_schemas() {
        let schemas = documents();
        for name in [
            "GetPendingRequestsResult.json", "ResolveRequestResult.json", "CancelRequestResult.json",
        ] {
            assert!(schemas.contains_key(name), "missing interaction result schema: {name}");
        }
        let result = crate::requests::CancelRequestResult {
            ok: true, request_id: "request-1".into(), applied_default: "noAnswer".into(),
            outcome: "noAnswer.declined".into(), reason: None,
        };
        assert_eq!(serde_json::to_value(result).unwrap(), serde_json::json!({
            "ok": true, "requestId": "request-1", "appliedDefault": "noAnswer",
            "outcome": "noAnswer.declined", "reason": null,
        }));
    }

    #[test]
    fn gated_event_schemas_include_sequence_and_instance_metadata() {
        let schemas = documents();
        for name in [
            "ActivityEvent.json", "LifecycleEvent.json", "SteeringQueueEvent.json",
            "ConfigChangedEvent.json", "SessionChangedEvent.json", "TurnEndedEvent.json",
            "RequestPendingEvent.json", "RequestResolvedEvent.json", "EventsDroppedEvent.json",
        ] {
            let schema = schemas.get(name).unwrap_or_else(|| panic!("missing event schema: {name}"));
            let properties = schema["properties"].as_object().unwrap();
            assert!(properties.contains_key("seq"), "{name}");
            assert!(properties.contains_key("engineInstanceId"), "{name}");
            assert!(!properties.contains_key("engine_instance_id"), "{name}");
        }
    }

    #[test]
    fn schema_titles_are_unique_and_match_their_file_names() {
        for (name, schema) in documents() {
            assert_eq!(schema["title"], name.trim_end_matches(".json"));
        }
    }

    #[test]
    fn server_initialize_schema_requires_the_advertised_contract_metadata() {
        let schemas = documents();
        let schema = schemas.get("InitializeResponse.json")
            .expect("the server response must have its own schema, not a tolerant reader proxy");
        let required = schema["required"].as_array().unwrap();
        for field in ["contractVersion", "engineInstanceId", "eventCursor", "capabilities"] {
            assert!(required.iter().any(|name| name == field), "{field}");
        }
        assert!(!required.iter().any(|name| name == "telemetryLogPath"));
    }

    #[test]
    fn legacy_client_contracts_are_included_without_losing_steering_rejections() {
        let schemas = documents();
        for name in [
            "PromptParams.json", "PromptResult.json", "SteerResult.json",
            "RecallSteeringResult.json", "ModelsResult.json", "ReasoningCapabilityResult.json",
            "ScheduleListResult.json", "HooksListResult.json", "PermissionRequest.json",
            "QuestionResponse.json", "PlanApprovalResponse.json",
        ] {
            assert!(schemas.contains_key(name), "missing legacy client schema: {name}");
        }
        assert!(schemas["SteerResult.json"]["properties"].get("rejectedReason").is_some());
    }

    #[test]
    fn legacy_event_payloads_keep_sequence_and_correlation_fields() {
        let schemas = documents();
        for name in [
            "AssistantTextEvent.json", "ToolCallEvent.json", "ToolResultEvent.json",
            "TurnCompleteEvent.json", "ErrorEvent.json", "ScheduleLifecycleEvent.json",
            "PostCompactContextInjectedEvent.json",
        ] {
            assert!(schemas.contains_key(name), "missing event schema: {name}");
            assert!(schemas[name]["properties"].get("seq").is_some());
        }
        let properties = schemas["ToolCallEvent.json"]["properties"].as_object().unwrap();
        for name in ["toolName", "inputJson", "rootTurnId", "activityId", "callId", "sourceId"] {
            assert!(properties.contains_key(name), "{name}");
        }
    }

    #[test]
    fn model_hook_and_session_mutation_schemas_are_present() {
        let schemas = documents();
        for name in [
            "SetModelParams.json", "SetModelResult.json", "HooksInfoParams.json",
            "HooksInfoResult.json", "HooksTrustParams.json", "HooksTrustResult.json",
            "ForkParams.json", "ForkResponse.json", "RewindParams.json", "RewindResponse.json",
        ] {
            assert!(schemas.contains_key(name), "missing mutation schema: {name}");
        }
    }

    #[test]
    fn machine_catalog_covers_routes_and_links_existing_schemas() {
        let repository = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).ancestors().nth(3).unwrap();
        let catalog_path = repository.join("docs").join("protocol").join("catalog.json");
        let text = std::fs::read_to_string(catalog_path)
            .expect("the machine-readable protocol catalog must be emitted");
        let index: Value = serde_json::from_str(&text).unwrap();
        assert_eq!(index, catalog_document(), "machine catalog drift; regenerate deliberately");
        let schemas = documents();
        for section in ["methods", "serverRequests", "events"] {
            for entry in index[section].as_array().unwrap() {
                for field in ["paramsSchema", "resultSchema"] {
                    if let Some(path) = entry[field].as_str() {
                        let file = path.strip_prefix("schemas/").unwrap();
                        assert!(schemas.contains_key(file), "{section}: {path}");
                    }
                }
                for entry in index["supportSchemas"].as_array().unwrap() {
                    let file = entry["schema"].as_str().unwrap().strip_prefix("schemas/").unwrap();
                    assert!(schemas.contains_key(file), "missing support schema: {file}");
                }
            }
        }
        let dispatch = std::fs::read_to_string(
            repository.join("rust").join("crates").join("coda-serve").join("src").join("dispatch.rs")
        ).unwrap();
        let production = dispatch.split("#[cfg(test)]").next().unwrap();
        let mut routed: Vec<String> = production.lines().filter_map(|line| {
            let tail = line.trim().strip_prefix('"')?;
            let (name, suffix) = tail.split_once('"')?;
            suffix.trim_start().starts_with("=>").then(|| name.to_owned())
        }).collect();
        routed.sort();
        routed.dedup();
        let mut indexed: Vec<String> = index["methods"].as_array().unwrap().iter()
            .map(|entry| entry["method"].as_str().unwrap().to_owned()).collect();
        indexed.sort();
        assert_eq!(indexed, routed, "RPC routes and catalog must change together");
        for (section, file, prefix) in [
            ("events", "events.rs", "event/"),
            ("serverRequests", "messages.rs", "request/"),
        ] {
            let source = std::fs::read_to_string(
                std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src").join(file)
            ).unwrap();
            let declared: std::collections::BTreeSet<_> = source.split(';').filter_map(|part| {
                let (_, constant) = part.rsplit_once("pub const ")?;
                let (_, value) = constant.split_once('=')?;
                let quoted = value.trim().strip_prefix('"')?;
                let (name, _) = quoted.split_once('"')?;
                name.starts_with(prefix).then(|| name.to_owned())
            }).collect();
            let mut listed: Vec<_> = index[section].as_array().unwrap().iter()
                .map(|entry| entry["method"].as_str().unwrap().to_owned()).collect();
            listed.sort();
            assert_eq!(listed, declared.into_iter().collect::<Vec<_>>(), "{section} inventory drift");
        }
    }
}
