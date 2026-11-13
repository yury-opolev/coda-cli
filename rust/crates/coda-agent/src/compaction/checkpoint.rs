//! Where the pre-compaction history is persisted before a compaction commits.

use coda_llm::Message;

#[derive(Debug, Clone)]
pub struct CheckpointMeta {
    /// `"auto"`, `"overflow"` or `"manual"`.
    pub trigger: &'static str,
    pub messages_before: usize,
    pub estimated_tokens_before: usize,
}

/// The saved checkpoint, named without its path.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckpointRef {
    pub name: String,
}

#[async_trait::async_trait]
pub trait CheckpointSink: Send + Sync {
    async fn save(&self, history: &[Message], meta: &CheckpointMeta) -> anyhow::Result<CheckpointRef>;
}
