use std::sync::Arc;

use coda_diagnostics::detail::{ErrorBodyDetail, ErrorBodyKind, FieldState};
use coda_diagnostics::{DiagnosticContext, Event, Limits, Logger, Options, ProcessRole, Verbosity};

#[test]
fn writer_revalidates_freely_constructed_error_fields_at_every_verbosity() {
    const CANARY: &str = "SECRET_ERROR_FIELD_CANARY";
    for verbosity in [Verbosity::Normal, Verbosity::Debug, Verbosity::Trace] {
        let directory = tempfile::tempdir().unwrap();
        let logger = Logger::open(Options {
            directory: directory.path().to_owned(),
            file: None,
            role: ProcessRole::Serve,
            version: "test".into(),
            verbosity,
        }, Limits::default()).unwrap();
        let context = DiagnosticContext::root(Arc::new(logger), "run").with_request("request");
        let detail = ErrorBodyDetail {
            body_kind: ErrorBodyKind::Json,
            error_type: FieldState::Recognized { value: CANARY },
            error_code: FieldState::Recognized { value: CANARY },
            parameter: FieldState::Recognized { value: CANARY.into() },
        };
        context.record(Event::HttpFailureDetails {
            dispatch: 1, attempt: 1, status: 400, detail: detail.clone(),
        });
        context.record(Event::RequestFailure {
            category: "client_error", status: Some(400), detail: Some(detail.clone()),
        });
        context.record(Event::StreamFailure {
            category: "client_error", status: Some(400), provider_request_id: None,
            parameter: Some(CANARY.into()), detail: Some(detail),
        });
        let text = std::fs::read_to_string(context.logger().status().path.unwrap()).unwrap();
        assert!(!text.contains(CANARY), "the writer persisted an unvalidated error field");
        let records: Vec<serde_json::Value> = text.lines()
            .map(|line| serde_json::from_str(line).unwrap()).collect();
        let details: Vec<_> = records.iter().filter(|record| record.get("detail").is_some()).collect();
        assert_eq!(details.len(), 3);
        for record in details {
            for field in ["error_type", "error_code", "parameter"] {
                assert_eq!(record["detail"][field]["state"], "unrecognized");
                assert!(record["detail"][field].get("value").is_none());
            }
            assert!(record.get("parameter").is_none());
        }
    }
}
