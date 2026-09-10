//! Legacy payload readers wrapped in the current Rust engine's metadata.

use std::collections::BTreeMap;

use super::*;
use crate::state_events::Sequenced;

#[derive(schemars::JsonSchema)]
struct EmptyPayload {}

pub(crate) fn documents() -> BTreeMap<&'static str, Value> {
    macro_rules! payloads {
        ($($name:literal => $payload:ty),+ $(,)?) => {
            BTreeMap::from([$(
                ($name, schemars::schema_for!(Sequenced<$payload>).to_value())
            ),+])
        };
    }
    payloads!(
        "AssistantTextEvent.json" => DeltaPayload,
        "AssistantTextCompleteEvent.json" => EmptyPayload,
        "ThinkingEvent.json" => DeltaPayload,
        "ThinkingCompleteEvent.json" => ThinkingCompletePayload,
        "ToolCallEvent.json" => ToolCallPayload,
        "ToolProgressEvent.json" => ToolProgressPayload,
        "ToolResultEvent.json" => ToolResultPayload,
        "TurnCompleteEvent.json" => TurnCompletePayload,
        "StopEvent.json" => StopPayload,
        "UsageEvent.json" => UsagePayload,
        "ErrorEvent.json" => MessagePayload,
        "LimitReachedEvent.json" => LimitReachedPayload,
        "StreamProgressEvent.json" => StreamProgressPayload,
        "SteeringDeliveredEvent.json" => SteeringDeliveredPayload,
        "TaskCompletedEvent.json" => TaskCompletedPayload,
        "ScheduleLifecycleEvent.json" => ScheduleLifecyclePayload,
        "PromptRewrittenEvent.json" => PromptRewrittenPayload,
        "ResponseRewrittenEvent.json" => ResponseRewrittenPayload,
        "ToolInputModifiedEvent.json" => ToolInputModifiedPayload,
        "ToolResultModifiedEvent.json" => ToolResultModifiedPayload,
        "PermissionDecidedEvent.json" => PermissionDecidedPayload,
        "PermissionsUpdatedEvent.json" => PermissionsUpdatedPayload,
        "SubagentBlockedEvent.json" => SubagentBlockedPayload,
        "SubagentResultModifiedEvent.json" => SubagentResultModifiedPayload,
        "CompactionCancelledEvent.json" => CompactionCancelledPayload,
        "PostCompactContextInjectedEvent.json" => PostCompactContextInjectedPayload,
    )
}
