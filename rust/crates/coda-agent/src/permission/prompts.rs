//! Permission prompt chain: the layered decision logic.
//!
//! The chain is assembled outermost-first. Each layer either makes a final
//! decision or delegates to the next layer inward. With no inner prompt
//! (headless), any `Ask` from a mode or classifier becomes a denial.
//!
//! Composition order (outermost → innermost):
//! 1. `RulesPermissionPrompt` — deny rules block, allow rules bypass inner.
//! 2. `LiveBypassClassifierPermissionPrompt` — routes to classifier in bypass
//!    mode, otherwise falls through to `ModePermissionPrompt`.
//! 3. `ModePermissionPrompt` — translates mode to allow/deny/ask; headless
//!    if inner is `None`.
//! 4. Interactive inner prompt (TUI/user) — `None` in headless runs.

use std::sync::Arc;

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::permission::{
    PermissionDecision, PermissionMode, PermissionModeState, PermissionPrompt,
    ToolActionClassifier,
};
use crate::permission::policy;
use crate::permission::rules::PermissionRuleStore;
use crate::tool::Tool;

// ── ModePermissionPrompt ─────────────────────────────────────────────────────

/// Applies the current mode via `PermissionPolicy`, delegating to the inner
/// prompt only when the decision is `Ask`. With no inner prompt (headless), an
/// `Ask` becomes deny — keeping the fail-closed guarantee.
pub struct ModePermissionPrompt {
    state: Arc<PermissionModeState>,
    inner: Option<Arc<dyn PermissionPrompt>>,
}

impl ModePermissionPrompt {
    pub fn new(mode: PermissionMode, inner: Option<Arc<dyn PermissionPrompt>>) -> Self {
        Self { state: Arc::new(PermissionModeState::new(mode)), inner }
    }

    pub fn new_with_state(
        state: Arc<PermissionModeState>,
        inner: Option<Arc<dyn PermissionPrompt>>,
    ) -> Self {
        Self { state, inner }
    }

    pub fn current_mode(&self) -> PermissionMode {
        self.state.get()
    }
}

#[async_trait]
impl PermissionPrompt for ModePermissionPrompt {
    async fn request(
        &self,
        tool: &dyn Tool,
        input_preview: &str,
        cancel: CancellationToken,
    ) -> bool {
        match policy::decide(self.state.get(), tool) {
            PermissionDecision::Allow => true,
            PermissionDecision::Deny => false,
            PermissionDecision::Ask => match &self.inner {
                Some(inner) => inner.request(tool, input_preview, cancel).await,
                // Headless: no one to ask → deny for safety.
                None => false,
            },
        }
    }
}

// ── RulesPermissionPrompt ────────────────────────────────────────────────────

/// Evaluates live allow/deny rules before delegating to the inner prompt.
///
/// Evaluation order:
/// 1. Any **deny** rule matches → deny immediately (deny always beats allow).
/// 2. Any **allow** rule matches → allow (inner not consulted).
/// 3. No rule matches → delegate inward.
pub struct RulesPermissionPrompt {
    rules: Arc<PermissionRuleStore>,
    inner: Arc<dyn PermissionPrompt>,
}

impl RulesPermissionPrompt {
    pub fn new(rules: Arc<PermissionRuleStore>, inner: Arc<dyn PermissionPrompt>) -> Self {
        Self { rules, inner }
    }
}

#[async_trait]
impl PermissionPrompt for RulesPermissionPrompt {
    async fn request(
        &self,
        tool: &dyn Tool,
        input_preview: &str,
        cancel: CancellationToken,
    ) -> bool {
        // Snapshot both lists in a single lock acquisition for consistency.
        let (deny, allow) = self.rules.snapshot();
        for rule in &deny {
            if rule.matches(tool.name(), input_preview) {
                return false;
            }
        }
        for rule in &allow {
            if rule.matches(tool.name(), input_preview) {
                return true;
            }
        }
        self.inner.request(tool, input_preview, cancel).await
    }
}

// ── ClassifierPermissionPrompt ───────────────────────────────────────────────

/// Classifies each action via the LLM safety classifier. Safe actions allow
/// automatically; risky ones escalate to the inner prompt (or deny headless).
pub struct ClassifierPermissionPrompt {
    classifier: Arc<dyn ToolActionClassifier>,
    inner: Option<Arc<dyn PermissionPrompt>>,
}

impl ClassifierPermissionPrompt {
    pub fn new(
        classifier: Arc<dyn ToolActionClassifier>,
        inner: Option<Arc<dyn PermissionPrompt>>,
    ) -> Self {
        Self { classifier, inner }
    }
}

#[async_trait]
impl PermissionPrompt for ClassifierPermissionPrompt {
    async fn request(
        &self,
        tool: &dyn Tool,
        input_preview: &str,
        cancel: CancellationToken,
    ) -> bool {
        let verdict = self.classifier.classify(tool.name(), input_preview, cancel.clone()).await;
        match verdict.decision {
            PermissionDecision::Allow => true,
            PermissionDecision::Deny => false,
            PermissionDecision::Ask => match &self.inner {
                Some(inner) => inner.request(tool, input_preview, cancel).await,
                None => false,
            },
        }
    }
}

// ── LiveBypassClassifierPermissionPrompt ─────────────────────────────────────

/// Dispatches per request based on the live mode.
///
/// - `BypassPermissions` → route to `ClassifierPermissionPrompt` (auto-allow
///   safe, confirm risky).
/// - Any other mode → route to `ModePermissionPrompt` (mode policy + inner).
///
/// The mode is re-read on **every** request so a mid-run switch from
/// `Default → Bypass` is reflected immediately on the very next call.
pub struct LiveBypassClassifierPermissionPrompt {
    state: Arc<PermissionModeState>,
    bypass_prompt: ClassifierPermissionPrompt,
    mode_prompt: ModePermissionPrompt,
}

impl LiveBypassClassifierPermissionPrompt {
    pub fn new(
        state: Arc<PermissionModeState>,
        classifier: Arc<dyn ToolActionClassifier>,
        inner: Option<Arc<dyn PermissionPrompt>>,
    ) -> Self {
        Self {
            bypass_prompt: ClassifierPermissionPrompt::new(
                Arc::clone(&classifier),
                inner.clone(),
            ),
            mode_prompt: ModePermissionPrompt::new_with_state(Arc::clone(&state), inner),
            state,
        }
    }

    pub fn current_mode(&self) -> PermissionMode {
        self.state.get()
    }
}

#[async_trait]
impl PermissionPrompt for LiveBypassClassifierPermissionPrompt {
    async fn request(
        &self,
        tool: &dyn Tool,
        input_preview: &str,
        cancel: CancellationToken,
    ) -> bool {
        if self.state.get() == PermissionMode::BypassPermissions {
            self.bypass_prompt.request(tool, input_preview, cancel).await
        } else {
            self.mode_prompt.request(tool, input_preview, cancel).await
        }
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicBool, Ordering};

    use crate::permission::{PermissionMode::*, ToolActionVerdict};
    use crate::permission::rules::PermissionRule;
    use crate::tool::{ToolContext, ToolOutcome, ToolResult};

    // ── Shared test helpers ──────────────────────────────────────────────────

    struct MockTool {
        name: &'static str,
        read_only: bool,
    }

    #[async_trait]
    impl crate::tool::Tool for MockTool {
        fn name(&self) -> &str {
            self.name
        }
        fn description(&self) -> &str {
            ""
        }
        fn input_schema_json(&self) -> &str {
            "{}"
        }
        fn is_read_only(&self) -> bool {
            self.read_only
        }
        async fn execute(
            &self,
            _: &serde_json::Value,
            _: &ToolContext,
            _: CancellationToken,
        ) -> ToolOutcome {
            ToolResult::ok("")
        }
    }

    fn mutating(name: &'static str) -> MockTool {
        MockTool { name, read_only: false }
    }

    fn read_only(name: &'static str) -> MockTool {
        MockTool { name, read_only: true }
    }

    /// A prompt that panics if it is ever called; used to verify inner is not consulted.
    struct PanicPrompt;

    #[async_trait]
    impl PermissionPrompt for PanicPrompt {
        async fn request(&self, _: &dyn Tool, _: &str, _: CancellationToken) -> bool {
            panic!("inner prompt must not be consulted");
        }
    }

    /// A prompt that always returns a fixed value.
    struct ConstPrompt(bool);

    #[async_trait]
    impl PermissionPrompt for ConstPrompt {
        async fn request(&self, _: &dyn Tool, _: &str, _: CancellationToken) -> bool {
            self.0
        }
    }

    /// A classifier that records whether it was called and returns a fixed verdict.
    struct TrackingClassifier {
        called: AtomicBool,
        verdict: ToolActionVerdict,
    }

    impl TrackingClassifier {
        fn new(verdict: ToolActionVerdict) -> Self {
            Self { called: AtomicBool::new(false), verdict }
        }
        fn was_called(&self) -> bool {
            self.called.load(Ordering::SeqCst)
        }
    }

    #[async_trait]
    impl ToolActionClassifier for TrackingClassifier {
        async fn classify(
            &self,
            _: &str,
            _: &str,
            _: CancellationToken,
        ) -> ToolActionVerdict {
            self.called.store(true, Ordering::SeqCst);
            self.verdict.clone()
        }
    }

    // ── ModePermissionPrompt tests ───────────────────────────────────────────

    // Spec §8 item 9: read-only tools never reach the inner prompt.
    #[tokio::test]
    async fn read_only_tool_bypasses_inner_prompt() {
        let inner = Arc::new(PanicPrompt);
        let prompt = ModePermissionPrompt::new(Default, Some(inner));
        // read_only = true → PermissionPolicy returns Allow → inner never called.
        assert!(prompt.request(&read_only("read_file"), "{}", CancellationToken::new()).await);
    }

    // Spec §8 item 14: headless Ask → deny.
    #[tokio::test]
    async fn headless_ask_becomes_deny() {
        // Default mode → Ask for mutating tools → no inner → deny.
        let prompt = ModePermissionPrompt::new(Default, None);
        assert!(!prompt.request(&mutating("run_command"), "{}", CancellationToken::new()).await);
    }

    #[tokio::test]
    async fn bypass_mode_allows_without_inner() {
        let prompt = ModePermissionPrompt::new(BypassPermissions, None);
        assert!(prompt.request(&mutating("anything"), "{}", CancellationToken::new()).await);
    }

    #[tokio::test]
    async fn plan_mode_denies_without_inner() {
        let prompt = ModePermissionPrompt::new(Plan, None);
        assert!(!prompt.request(&mutating("edit_file"), "{}", CancellationToken::new()).await);
    }

    #[tokio::test]
    async fn ask_delegates_to_inner_when_present() {
        let inner = Arc::new(ConstPrompt(true));
        let prompt = ModePermissionPrompt::new(Default, Some(inner));
        // Default → Ask → inner returns true.
        assert!(prompt.request(&mutating("run_cmd"), "{}", CancellationToken::new()).await);
    }

    // ── RulesPermissionPrompt tests ──────────────────────────────────────────

    // Spec §8 item 12: deny rule blocks before inner is consulted.
    #[tokio::test]
    async fn deny_rule_blocks_before_inner() {
        let store = Arc::new(PermissionRuleStore::new(
            [],
            [PermissionRule::parse("dangerous")],
        ));
        let inner = Arc::new(PanicPrompt); // would panic if called
        let prompt = RulesPermissionPrompt::new(store, inner);
        assert!(!prompt.request(&mutating("dangerous"), "{}", CancellationToken::new()).await);
    }

    #[tokio::test]
    async fn allow_rule_bypasses_inner() {
        let store = Arc::new(PermissionRuleStore::new(
            [PermissionRule::parse("safe")],
            [],
        ));
        let inner = Arc::new(PanicPrompt);
        let prompt = RulesPermissionPrompt::new(store, inner);
        assert!(prompt.request(&mutating("safe"), "{}", CancellationToken::new()).await);
    }

    #[tokio::test]
    async fn no_matching_rule_delegates_to_inner() {
        let store = Arc::new(PermissionRuleStore::default());
        let inner = Arc::new(ConstPrompt(true));
        let prompt = RulesPermissionPrompt::new(store, inner);
        assert!(prompt.request(&mutating("unknown"), "{}", CancellationToken::new()).await);
    }

    // Spec §8 item 12: deny beats allow in the rules prompt.
    #[tokio::test]
    async fn deny_beats_allow_in_rules_prompt() {
        let store = Arc::new(PermissionRuleStore::new(
            [PermissionRule::parse("tool")],
            [PermissionRule::parse("tool")],
        ));
        let inner = Arc::new(PanicPrompt);
        let prompt = RulesPermissionPrompt::new(store, inner);
        assert!(!prompt.request(&mutating("tool"), "{}", CancellationToken::new()).await);
    }

    // ── ClassifierPermissionPrompt tests ─────────────────────────────────────

    #[tokio::test]
    async fn classifier_allow_permits_without_inner() {
        let classifier = Arc::new(TrackingClassifier::new(ToolActionVerdict::allow()));
        let prompt = ClassifierPermissionPrompt::new(classifier, None);
        assert!(prompt.request(&mutating("tool"), "{}", CancellationToken::new()).await);
    }

    // Spec §8 item 14: classifier Ask + headless → deny.
    #[tokio::test]
    async fn classifier_ask_headless_denies() {
        let classifier = Arc::new(TrackingClassifier::new(
            ToolActionVerdict::ask("risky"),
        ));
        let prompt = ClassifierPermissionPrompt::new(classifier, None);
        assert!(!prompt.request(&mutating("tool"), "{}", CancellationToken::new()).await);
    }

    #[tokio::test]
    async fn classifier_ask_with_inner_delegates() {
        let classifier = Arc::new(TrackingClassifier::new(
            ToolActionVerdict::ask("needs confirm"),
        ));
        let inner = Arc::new(ConstPrompt(true));
        let prompt = ClassifierPermissionPrompt::new(classifier, Some(inner));
        assert!(prompt.request(&mutating("tool"), "{}", CancellationToken::new()).await);
    }

    // ── LiveBypassClassifierPermissionPrompt tests ───────────────────────────

    // Spec §8 item 16: switching mode routes the next decision through classifier.
    #[tokio::test]
    async fn default_mode_does_not_call_classifier() {
        let state = Arc::new(PermissionModeState::new(Default));
        let classifier = Arc::new(TrackingClassifier::new(ToolActionVerdict::allow()));
        let prompt = LiveBypassClassifierPermissionPrompt::new(
            Arc::clone(&state),
            classifier.clone() as Arc<dyn ToolActionClassifier>,
            None,
        );
        // Default mode → ModePermissionPrompt (Ask → headless → deny). Classifier not called.
        let _ = prompt.request(&mutating("cmd"), "{}", CancellationToken::new()).await;
        assert!(!classifier.was_called(), "classifier must not be called outside bypass mode");
    }

    #[tokio::test]
    async fn bypass_mode_routes_through_classifier() {
        let state = Arc::new(PermissionModeState::new(BypassPermissions));
        let classifier = Arc::new(TrackingClassifier::new(ToolActionVerdict::allow()));
        let prompt = LiveBypassClassifierPermissionPrompt::new(
            Arc::clone(&state),
            classifier.clone() as Arc<dyn ToolActionClassifier>,
            None,
        );
        let _ = prompt.request(&mutating("cmd"), "{}", CancellationToken::new()).await;
        assert!(classifier.was_called());
    }

    #[tokio::test]
    async fn live_mode_switch_from_default_to_bypass_calls_classifier_on_next_request() {
        let state = Arc::new(PermissionModeState::new(Default));
        let classifier = Arc::new(TrackingClassifier::new(ToolActionVerdict::allow()));
        let prompt = LiveBypassClassifierPermissionPrompt::new(
            Arc::clone(&state),
            classifier.clone() as Arc<dyn ToolActionClassifier>,
            None,
        );

        // First request in Default mode: classifier NOT called.
        let _ = prompt.request(&mutating("cmd"), "{}", CancellationToken::new()).await;
        assert!(!classifier.was_called());

        // Switch to Bypass.
        state.set(BypassPermissions);

        // Second request: classifier IS called.
        let _ = prompt.request(&mutating("cmd"), "{}", CancellationToken::new()).await;
        assert!(classifier.was_called());
    }
}
