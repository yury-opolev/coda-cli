//! Shared response shapes for model, hook and session mutations.

use serde::{Deserialize, Serialize};

#[derive(Debug, Serialize, Deserialize)]
#[serde(untagged)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum SetModelResult {
    Selected { ok: bool, model: String, effort: Option<String> },
    Refused { ok: bool, note: String },
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct HooksTrustResult {
    pub ok: bool,
    pub project_path: String,
    pub hook_hash: String,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ForkResponse {
    pub ok: bool,
    pub new_session_id: String,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct RewindResponse {
    pub ok: bool,
    pub removed: usize,
    pub remaining: usize,
}
