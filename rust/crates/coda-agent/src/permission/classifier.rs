//! Tool action classifier: the LLM safety classifier for bypass/"yolo" mode.
//!
//! The classifier is an isolated single-turn model call that decides whether an
//! action is safe to auto-approve (`ALLOW`) or must be confirmed (`ASK`). It
//! **never** returns `Deny` directly — `Ask` is the safe middle ground, and a
//! headless host converts that to a denial.
//!
//! Fail-closed: any failure (network error, unparseable reply, empty output)
//! becomes an `Ask` verdict. `ALLOW` is the only string that grants automatic
//! approval.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::permission::PermissionDecision;

/// The verdict returned by the classifier for one proposed tool action.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolActionVerdict {
    pub decision: PermissionDecision,
    /// Human-readable reason; populated when `decision == Ask`.
    pub reason: Option<String>,
}

impl ToolActionVerdict {
    pub fn allow() -> Self {
        Self { decision: PermissionDecision::Allow, reason: None }
    }

    pub fn ask(reason: impl Into<String>) -> Self {
        Self { decision: PermissionDecision::Ask, reason: Some(reason.into()) }
    }
}

/// Classifies a single tool action as safe (Allow) or risky (Ask).
///
/// The classifier never returns `Deny` — `Ask` is the conservative middle that
/// lets the user decide (or that a headless host converts to a denial).
#[async_trait]
pub trait ToolActionClassifier: Send + Sync {
    async fn classify(
        &self,
        tool_name: &str,
        input_json: &str,
        cancel: CancellationToken,
    ) -> ToolActionVerdict;
}

// ── ForkedAgent seam ─────────────────────────────────────────────────────────

/// An isolated single-turn model call.
///
/// The loop wires this up; in tests it can be mocked. This is the seam between
/// the classifier and the LLM client — defined here, implemented in the loop spec.
#[async_trait]
pub trait ForkedAgent: Send + Sync {
    /// Run a single-turn conversation with the given system prompt and messages.
    ///
    /// Returns the full response text, or an error if the call fails.
    async fn run(
        &self,
        system: &str,
        messages: Vec<coda_llm::Message>,
        cancel: CancellationToken,
    ) -> anyhow::Result<String>;
}

// ── Prompt builder / parser ───────────────────────────────────────────────────

/// The system prompt, user-message builder, and fail-closed response parser for
/// the safety classifier.
pub struct ToolActionClassifierPrompt;

impl ToolActionClassifierPrompt {
    pub const SYSTEM_PROMPT: &'static str = "\
You are a safety classifier for an autonomous coding agent running in an \
auto-approve mode. You are shown ONE tool action the agent wants to take. \
Decide whether it is safe to run automatically, or whether a human must \
confirm it first.

Auto-approve (ALLOW) routine, low-risk, reversible actions: reading or \
searching files, creating or editing files within the project, running \
safe build/test/read-only commands.

Require confirmation (ASK) for anything risky, hard to reverse, or with \
blast radius beyond the local project: deleting files or directories, \
`rm -rf`, force-pushing or resetting git, dropping databases, killing \
processes, modifying CI/CD, sending network requests that change remote \
state, installing/removing global software, or anything that exfiltrates \
data or could be destructive.

When in doubt, choose ASK. It is far better to over-confirm than to allow a \
destructive action.

Respond with EXACTLY ONE line:
- `ALLOW` if it is safe to auto-approve.
- `ASK: <short reason>` if a human must confirm.
Output nothing else.";

    pub fn build_user_message(tool_name: &str, input_json: &str) -> String {
        format!("Tool: {tool_name}\nInput: {input_json}\n\nClassify this action.")
    }

    /// Parse the model's reply — fail-closed.
    ///
    /// Only an explicit leading `ALLOW` (case-insensitive) grants automatic
    /// approval. `ASK`, `BLOCK`, `DENY`, empty output, prose, or anything
    /// unparseable all resolve to `Ask`, which a headless host converts to deny.
    pub fn parse(response: &str) -> ToolActionVerdict {
        if response.trim().is_empty() {
            return ToolActionVerdict::ask("Classifier returned no output — blocking for safety.");
        }
        let first_line = response.trim().split('\n').next().unwrap_or("").trim();
        if first_line.eq_ignore_ascii_case("ALLOW") {
            return ToolActionVerdict::allow();
        }
        if first_line.to_ascii_uppercase().starts_with("ASK")
            || first_line.to_ascii_uppercase().starts_with("BLOCK")
            || first_line.to_ascii_uppercase().starts_with("DENY")
        {
            let reason = extract_reason(first_line);
            let reason = if reason.is_empty() {
                "Flagged for confirmation.".to_owned()
            } else {
                reason
            };
            return ToolActionVerdict::ask(reason);
        }
        ToolActionVerdict::ask("Classifier output unparseable — blocking for safety.")
    }
}

fn extract_reason(line: &str) -> String {
    line.find(':')
        .filter(|&i| i + 1 < line.len())
        .map(|i| line[i + 1..].trim().to_owned())
        .unwrap_or_default()
}

// ── LLM-backed classifier ─────────────────────────────────────────────────────

/// Classifies a tool action with a single isolated model call.
///
/// Any call failure (API error, network issue, etc.) is caught and converted
/// to an `Ask` verdict — the classifier fails closed, never open.
pub struct LlmToolActionClassifier {
    fork: std::sync::Arc<dyn ForkedAgent>,
}

impl LlmToolActionClassifier {
    pub fn new(fork: std::sync::Arc<dyn ForkedAgent>) -> Self {
        Self { fork }
    }
}

#[async_trait]
impl ToolActionClassifier for LlmToolActionClassifier {
    async fn classify(
        &self,
        tool_name: &str,
        input_json: &str,
        cancel: CancellationToken,
    ) -> ToolActionVerdict {
        let user_msg = ToolActionClassifierPrompt::build_user_message(tool_name, input_json);
        match self.fork.run(
            ToolActionClassifierPrompt::SYSTEM_PROMPT,
            vec![coda_llm::Message::user(user_msg)],
            cancel,
        ).await {
            Ok(response) => ToolActionClassifierPrompt::parse(&response),
            Err(e) => ToolActionVerdict::ask(format!(
                "Classifier unavailable ({e}) — blocking for safety."
            )),
        }
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::permission::PermissionDecision::*;

    // Spec §8 item 13: classifier fails closed.

    #[test]
    fn allow_only_parses_to_allow() {
        let v = ToolActionClassifierPrompt::parse("ALLOW");
        assert_eq!(v.decision, Allow);
        assert!(v.reason.is_none());
    }

    #[test]
    fn allow_is_case_insensitive() {
        assert_eq!(ToolActionClassifierPrompt::parse("allow").decision, Allow);
        assert_eq!(ToolActionClassifierPrompt::parse("Allow").decision, Allow);
    }

    #[test]
    fn ask_prefix_parses_to_ask_not_allow() {
        let v = ToolActionClassifierPrompt::parse("ASK: too risky");
        assert_eq!(v.decision, Ask);
        assert_eq!(v.reason.as_deref(), Some("too risky"));
    }

    #[test]
    fn block_prefix_parses_to_ask() {
        let v = ToolActionClassifierPrompt::parse("BLOCK: destructive");
        assert_eq!(v.decision, Ask);
    }

    // The classifier never returns Deny directly — only Allow or Ask.
    #[test]
    fn deny_prefix_maps_to_ask_not_deny() {
        let v = ToolActionClassifierPrompt::parse("DENY: dangerous");
        assert_eq!(v.decision, Ask, "classifier must never return Deny directly");
    }

    #[test]
    fn empty_response_is_ask() {
        let v = ToolActionClassifierPrompt::parse("");
        assert_eq!(v.decision, Ask);
        assert!(v.reason.as_deref().unwrap().contains("no output"));
    }

    #[test]
    fn whitespace_only_response_is_ask() {
        let v = ToolActionClassifierPrompt::parse("   \n  ");
        assert_eq!(v.decision, Ask);
    }

    #[test]
    fn unparseable_prose_is_ask() {
        let v = ToolActionClassifierPrompt::parse("Sure, I think that's fine.");
        assert_eq!(v.decision, Ask);
        assert!(v.reason.as_deref().unwrap().contains("unparseable"));
    }

    #[test]
    fn allow_with_trailing_content_is_not_allow() {
        // "ALLOW but with a reason" is prose, not a clean ALLOW verdict.
        let v = ToolActionClassifierPrompt::parse("ALLOW but be careful");
        assert_eq!(v.decision, Ask);
    }

    #[test]
    fn ask_without_reason_gets_default_reason() {
        let v = ToolActionClassifierPrompt::parse("ASK");
        assert_eq!(v.decision, Ask);
        assert!(!v.reason.as_deref().unwrap().is_empty());
    }

    #[test]
    fn multiline_response_only_first_line_matters() {
        let v = ToolActionClassifierPrompt::parse("ALLOW\nSome explanation here");
        assert_eq!(v.decision, Allow);
    }

    // Classifier fail-closed: unavailable → Ask.
    #[tokio::test]
    async fn llm_classifier_fail_closed_on_error() {
        struct FailingAgent;

        #[async_trait]
        impl ForkedAgent for FailingAgent {
            async fn run(
                &self,
                _: &str,
                _: Vec<coda_llm::Message>,
                _: CancellationToken,
            ) -> anyhow::Result<String> {
                Err(anyhow::anyhow!("connection refused"))
            }
        }

        let classifier =
            LlmToolActionClassifier::new(std::sync::Arc::new(FailingAgent));
        let verdict = classifier.classify("tool", "{}", CancellationToken::new()).await;
        assert_eq!(verdict.decision, Ask);
        assert!(verdict.reason.as_deref().unwrap().contains("Classifier unavailable"));
    }

    #[tokio::test]
    async fn llm_classifier_returns_allow_on_allow_response() {
        struct AllowAgent;

        #[async_trait]
        impl ForkedAgent for AllowAgent {
            async fn run(
                &self,
                _: &str,
                _: Vec<coda_llm::Message>,
                _: CancellationToken,
            ) -> anyhow::Result<String> {
                Ok("ALLOW".to_owned())
            }
        }

        let classifier = LlmToolActionClassifier::new(std::sync::Arc::new(AllowAgent));
        let verdict = classifier.classify("read_file", "{}", CancellationToken::new()).await;
        assert_eq!(verdict.decision, Allow);
    }

    #[tokio::test]
    async fn llm_classifier_returns_ask_on_unparseable_response() {
        struct BadAgent;

        #[async_trait]
        impl ForkedAgent for BadAgent {
            async fn run(
                &self,
                _: &str,
                _: Vec<coda_llm::Message>,
                _: CancellationToken,
            ) -> anyhow::Result<String> {
                Ok("I think this is fine".to_owned())
            }
        }

        let classifier = LlmToolActionClassifier::new(std::sync::Arc::new(BadAgent));
        let verdict = classifier.classify("edit_file", "{}", CancellationToken::new()).await;
        assert_eq!(verdict.decision, Ask);
    }
}
