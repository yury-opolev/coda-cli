//! Existing client writer/reader contracts, including reverse requests.
//! These retain their compatibility defaults; they are not stricter server
//! validation rules inferred from examples.

use std::collections::BTreeMap;
use serde_json::Value;

pub(super) fn documents() -> BTreeMap<&'static str, Value> {
    macro_rules! contracts {
        ($($ty:ident),+ $(,)?) => {
            BTreeMap::from([$(
                (
                    concat!(stringify!($ty), ".json"),
                    schemars::schema_for!(crate::messages::$ty).to_value(),
                )
            ),+])
        };
    }
    contracts!(
        OkResult, PromptParams, PromptResult, SteerParams, SteerResult,
        RecallSteeringResult, HistoryResult, MessagesParams, MessagesResult,
        ModelsParams, ModelsResult, SetGoalParams, SetGoalResult,
        SetEffortParams, SetEffortResult, SetPermissionModeParams, SetPermissionModeResult,
        SetSystemPromptParams, SetSystemPromptResult, ModelAdjustEffortParams, ModelEffortResult,
        ReasoningCapabilityResult, CompactParams, CompactResult, ScheduleListResult,
        ScheduledTask, ScheduleCreateParams, ScheduleDeleteParams, SkillsListResult,
        PluginsListResult, HooksListResult, PermissionRequest, PermissionResponse,
        QuestionRequest, QuestionResponse, PlanApprovalRequest, PlanApprovalResponse,
    )
}
