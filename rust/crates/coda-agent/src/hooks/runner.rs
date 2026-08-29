//! Hook execution orchestrator.
//!
//! Matches C# `HookBus.cs` (the policy / ordering / merging logic) and
//! `UserHookRunner.cs` (the public façade).
//!
//! # Execution model
//! 1. Filter hooks for the event + tool-name matcher.
//! 2. For each matching hook (in configuration order):
//!    a. Check trust guard (project / plugin hooks).
//!    b. Apply per-hook timeout (hook override → event default).
//!    c. Dispatch to the executor (shell, HTTP, or unsupported handler).
//!    d. Apply fail-open / fail-closed policy on timeout / error.
//!    e. Stop iterating when `continue_execution = false`.
//! 3. Merge outputs by event-specific rules.

use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use super::content_hash::HookContentHash;
use super::matcher::HookMatcher;
use super::output::{
    AgentResponseResult, HookOutput, PermissionDecision, PermissionRequestResult,
    PostToolUseResult, PreCompactResult, PostCompactResult, SubagentStartResult,
    SubagentStopResult, UserHookResult,
};
use super::policy::HookEventPolicy;
use super::trust_guard::HookTrustGuard;
use super::{HookScope, UserHook};

// ─────────────────────────────────────────────────────────────────────────────
// Executor trait
// ─────────────────────────────────────────────────────────────────────────────

/// Runs a single hook command and returns `(exit_code, stdout, stderr)`.
///
/// The trait is injected so tests can use a mock without spawning processes.
#[async_trait]
pub trait HookExecutor: Send + Sync {
    async fn exec(
        &self,
        command: &str,
        payload: &str,
        cancel: CancellationToken,
    ) -> anyhow::Result<(i32, String, String)>;
}

/// Real shell-based executor.  Spawns the system shell, writes payload to
/// stdin, and collects stdout + stderr.
pub struct ShellHookExecutor;

#[async_trait]
impl HookExecutor for ShellHookExecutor {
    async fn exec(
        &self,
        command: &str,
        payload: &str,
        cancel: CancellationToken,
    ) -> anyhow::Result<(i32, String, String)> {
        use tokio::io::AsyncWriteExt;
        use tokio::io::AsyncReadExt;

        #[cfg(windows)]
        let (shell, flag) = ("cmd.exe", "/c");
        #[cfg(not(windows))]
        let (shell, flag) = ("/bin/sh", "-c");

        let mut child = tokio::process::Command::new(shell)
            .arg(flag)
            .arg(command)
            .stdin(std::process::Stdio::piped())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::piped())
            .spawn()?;

        // Write payload then close stdin.
        if let Some(mut stdin) = child.stdin.take() {
            let _ = stdin.write_all(payload.as_bytes()).await;
        }

        // Take the stdout/stderr handles before consuming child in wait().
        let mut stdout_handle = child.stdout.take();
        let mut stderr_handle = child.stderr.take();

        let status = tokio::select! {
            s = child.wait() => s?,
            _ = cancel.cancelled() => {
                let _ = child.kill().await;
                let _ = child.wait().await;
                return Err(anyhow::anyhow!("cancelled"));
            }
        };

        // Drain the pipes after process exit.
        let mut stdout_buf = Vec::new();
        let mut stderr_buf = Vec::new();
        if let Some(h) = &mut stdout_handle {
            let _ = h.read_to_end(&mut stdout_buf).await;
        }
        if let Some(h) = &mut stderr_handle {
            let _ = h.read_to_end(&mut stderr_buf).await;
        }

        let stdout = String::from_utf8_lossy(&stdout_buf).into_owned();
        let stderr = String::from_utf8_lossy(&stderr_buf).into_owned();
        let code = status.code().unwrap_or(-1);
        Ok((code, stdout, stderr))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HookRunner
// ─────────────────────────────────────────────────────────────────────────────

/// Executes user-configured hooks at agent lifecycle events.
pub struct HookRunner {
    hooks: Vec<UserHook>,
    executor: Arc<dyn HookExecutor>,
    trust_guard: Option<Arc<HookTrustGuard>>,

    // Fast-path flags so callers can skip entire event types cheaply.
    pub has_pre_tool_use: bool,
    pub has_post_tool_use: bool,
    pub has_permission_request: bool,
    pub has_user_prompt_submit: bool,
    pub has_stop: bool,
    pub has_agent_response: bool,
    pub has_subagent_start: bool,
    pub has_subagent_stop: bool,
    pub has_pre_compact: bool,
    pub has_post_compact: bool,
    /// True when any `AgentResponse` hook declares `"displayContent"` or
    /// `"modifiedResponse"` in its `mutates` list.
    pub any_hook_mutates_display: bool,
}

impl HookRunner {
    pub fn new(hooks: Vec<UserHook>) -> Self {
        Self::with_executor(hooks, Arc::new(ShellHookExecutor))
    }

    pub fn with_executor(hooks: Vec<UserHook>, executor: Arc<dyn HookExecutor>) -> Self {
        Self::with_executor_and_guard(hooks, executor, None)
    }

    pub fn with_executor_and_guard(
        hooks: Vec<UserHook>,
        executor: Arc<dyn HookExecutor>,
        trust_guard: Option<Arc<HookTrustGuard>>,
    ) -> Self {
        let has = |ev: &str| hooks.iter().any(|h| h.event.eq_ignore_ascii_case(ev));
        let any_mutates_display = hooks.iter().any(|h| {
            h.event.eq_ignore_ascii_case("AgentResponse")
                && h.mutates.as_ref().map_or(false, |m| {
                    m.iter().any(|f| {
                        f.eq_ignore_ascii_case("displayContent")
                            || f.eq_ignore_ascii_case("modifiedResponse")
                    })
                })
        });

        HookRunner {
            has_pre_tool_use: has("PreToolUse"),
            has_post_tool_use: has("PostToolUse"),
            has_permission_request: has("PermissionRequest"),
            has_user_prompt_submit: has("UserPromptSubmit"),
            has_stop: has("Stop"),
            has_agent_response: has("AgentResponse"),
            has_subagent_start: has("SubagentStart"),
            has_subagent_stop: has("SubagentStop"),
            has_pre_compact: has("PreCompact"),
            has_post_compact: has("PostCompact"),
            any_hook_mutates_display: any_mutates_display,
            hooks,
            executor,
            trust_guard,
        }
    }

    // ── Public run methods ────────────────────────────────────────────────────

    /// Runs all matching `PreToolUse` hooks.  Fail-closed.
    pub async fn run_pre_tool_use(
        &self,
        tool_name: &str,
        input_json: &str,
        cancel: CancellationToken,
    ) -> UserHookResult {
        let matching = self.matching("PreToolUse", Some(tool_name));
        if matching.is_empty() {
            return UserHookResult::ALLOW;
        }
        let payload = build_pre_tool_payload(tool_name, input_json);
        let pairs = self.run_with_pairs(&matching, "PreToolUse", &payload, cancel).await;
        merge_pre_tool_use(pairs)
    }

    /// Runs all matching `PostToolUse` hooks.  Fail-open.
    pub async fn run_post_tool_use(
        &self,
        tool_name: &str,
        input_json: &str,
        result_text: &str,
        error_text: Option<&str>,
        cancel: CancellationToken,
    ) -> PostToolUseResult {
        let matching = self.matching("PostToolUse", Some(tool_name));
        if matching.is_empty() {
            return PostToolUseResult::NO_CHANGE;
        }
        let payload = build_post_tool_payload(tool_name, input_json, result_text, error_text);
        match self.run_hooks(&matching, "PostToolUse", &payload, cancel).await {
            Ok(outputs) => merge_post_tool_use(&matching, outputs),
            Err(_) => PostToolUseResult::NO_CHANGE,
        }
    }

    /// Runs all matching `PermissionRequest` hooks.  Fail-closed.
    pub async fn run_permission_request(
        &self,
        tool_name: &str,
        input_json: &str,
        cancel: CancellationToken,
    ) -> PermissionRequestResult {
        let matching = self.matching("PermissionRequest", Some(tool_name));
        if matching.is_empty() {
            return PermissionRequestResult::PROMPT;
        }
        let payload = build_permission_payload(tool_name, input_json);
        match self.run_hooks(&matching, "PermissionRequest", &payload, cancel).await {
            Ok(outputs) => merge_permission_request(&matching, outputs),
            Err(_) => PermissionRequestResult {
                decision: PermissionDecision::Deny,
                reason: Some("permission hook loop failed".into()),
                by_hook_command: None,
            },
        }
    }

    /// Runs all `AgentResponse` hooks.  Fail-open.
    pub async fn run_agent_response(
        &self,
        response: &str,
        stop_reason: Option<&str>,
        cancel: CancellationToken,
    ) -> AgentResponseResult {
        let matching = self.matching("AgentResponse", None);
        if matching.is_empty() {
            return AgentResponseResult::NO_CHANGE;
        }
        let payload = build_agent_response_payload(response, stop_reason);
        match self.run_hooks(&matching, "AgentResponse", &payload, cancel).await {
            Ok(outputs) => merge_agent_response(&matching, outputs),
            Err(_) => AgentResponseResult::NO_CHANGE,
        }
    }

    /// Runs all `SubagentStart` hooks.  Fail-closed.
    pub async fn run_subagent_start(
        &self,
        task_id: &str,
        depth: u32,
        prompt: &str,
        toolset: &[String],
        cancel: CancellationToken,
    ) -> SubagentStartResult {
        let matching = self.matching("SubagentStart", None);
        if matching.is_empty() {
            return SubagentStartResult::ALLOW;
        }
        let payload = build_subagent_start_payload(task_id, depth, prompt, toolset);
        let pairs = self.run_with_pairs(&matching, "SubagentStart", &payload, cancel).await;
        merge_subagent_start(pairs)
    }

    /// Runs all `SubagentStop` hooks.  Fail-open.
    pub async fn run_subagent_stop(
        &self,
        task_id: &str,
        depth: u32,
        result: &str,
        cancel: CancellationToken,
    ) -> SubagentStopResult {
        let matching = self.matching("SubagentStop", None);
        if matching.is_empty() {
            return SubagentStopResult::NO_CHANGE;
        }
        let payload = build_subagent_stop_payload(task_id, depth, result);
        match self.run_hooks(&matching, "SubagentStop", &payload, cancel).await {
            Ok(outputs) => merge_subagent_stop(&matching, outputs),
            Err(_) => SubagentStopResult::NO_CHANGE,
        }
    }

    /// Runs all `PreCompact` hooks.  Fail-open.
    pub async fn run_pre_compact(
        &self,
        trigger: &str,
        tokens_before: usize,
        message_count: usize,
        instructions: Option<&str>,
        cancel: CancellationToken,
    ) -> PreCompactResult {
        let matching = self.matching("PreCompact", None);
        if matching.is_empty() {
            return PreCompactResult::ALLOW;
        }
        let payload = build_pre_compact_payload(trigger, tokens_before, message_count, instructions);
        match self.run_hooks(&matching, "PreCompact", &payload, cancel).await {
            Ok(outputs) => merge_pre_compact(&matching, outputs),
            Err(_) => PreCompactResult::ALLOW,
        }
    }

    /// Runs all `PostCompact` hooks.  Fail-open.
    pub async fn run_post_compact(
        &self,
        tokens_before: usize,
        tokens_after: usize,
        message_count: usize,
        summary: &str,
        cancel: CancellationToken,
    ) -> PostCompactResult {
        let matching = self.matching("PostCompact", None);
        if matching.is_empty() {
            return PostCompactResult::NO_CHANGE;
        }
        let payload = build_post_compact_payload(tokens_before, tokens_after, message_count, summary);
        match self.run_hooks(&matching, "PostCompact", &payload, cancel).await {
            Ok(outputs) => merge_post_compact(outputs),
            Err(_) => PostCompactResult::NO_CHANGE,
        }
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    /// Returns the hooks that match `event_name` and (optionally) `tool_name`.
    fn matching<'a>(&'a self, event_name: &str, tool_name: Option<&str>) -> Vec<&'a UserHook> {
        self.hooks
            .iter()
            .filter(|h| {
                h.enabled
                    && h.event.eq_ignore_ascii_case(event_name)
                    && tool_name
                        .map(|tn| HookMatcher::matches(h.matcher.as_deref(), tn))
                        .unwrap_or(true)
            })
            .collect()
    }

    /// Run hooks in order, stopping on `continue_execution = false`.
    /// Returns `Err(())` only on unexpected loop-level panics (individual hook
    /// timeouts and errors are already absorbed per fail-open policy).
    async fn run_hooks(
        &self,
        hooks: &[&UserHook],
        event_name: &str,
        payload: &str,
        cancel: CancellationToken,
    ) -> Result<Vec<HookOutput>, ()> {
        let mut outputs = Vec::with_capacity(hooks.len());
        for hook in hooks {
            let out = self.run_single(hook, event_name, payload, cancel.clone()).await;
            let should_continue = out.continue_execution;
            outputs.push(out);
            if !should_continue {
                break;
            }
        }
        Ok(outputs)
    }

    /// Run hooks as `(hook, output)` pairs (needed for last-writer-wins merges).
    async fn run_with_pairs<'a>(
        &self,
        hooks: &[&'a UserHook],
        event_name: &str,
        payload: &str,
        cancel: CancellationToken,
    ) -> Vec<(&'a UserHook, HookOutput)> {
        let mut pairs = Vec::with_capacity(hooks.len());
        for hook in hooks {
            let out = self.run_single(hook, event_name, payload, cancel.clone()).await;
            let cont = out.continue_execution;
            pairs.push((*hook, out));
            if !cont {
                break;
            }
        }
        pairs
    }

    /// Run a single hook, applying trust check, timeout, and fail-open policy.
    async fn run_single(
        &self,
        hook: &UserHook,
        event_name: &str,
        payload: &str,
        cancel: CancellationToken,
    ) -> HookOutput {
        let defaults = HookEventPolicy::get(event_name);
        let timeout_secs = hook.timeout_seconds.unwrap_or(defaults.timeout_seconds);
        let fail_open = hook.fail_open.unwrap_or(defaults.fail_open);

        // Trust check for project-scoped / plugin hooks.
        if let Some(guard) = &self.trust_guard {
            if hook.scope == HookScope::Project || hook.plugin_origin.is_some() {
                if !guard.can_run(hook) {
                    // Untrusted: apply fail-open/closed policy.
                    return if fail_open {
                        HookOutput::no_op()
                    } else {
                        HookOutput {
                            decision: Some("block".into()),
                            reason: Some(format!(
                                "untrusted hook '{}' was not granted trust",
                                HookContentHash::hook_id(hook)
                            )),
                            continue_execution: false,
                            specific: None,
                        }
                    };
                }
            }
        }

        let exec_result = self.dispatch(hook, payload, cancel.clone(), timeout_secs).await;

        match exec_result {
            // Hook ran and produced output.
            Ok(stdout) => HookOutput::parse(&stdout),

            // Hook timed out (our own CancellationToken fired, not caller's).
            Err(ExecError::Timeout) => {
                if fail_open {
                    HookOutput::no_op()
                } else {
                    HookOutput {
                        decision: Some("block".into()),
                        reason: Some(format!("hook timed out after {timeout_secs}s")),
                        continue_execution: false,
                        specific: None,
                    }
                }
            }

            // Caller cancellation — propagate by returning a no-op (the caller
            // checks the cancel token and will exit before acting on it).
            Err(ExecError::Cancelled) => HookOutput::no_op(),

            // A handler type this build cannot dispatch has NOT approved
            // anything, so it must take the event's fail-open policy rather
            // than parsing as a silent success.  Treating it as a no-op would
            // turn a fail-closed gate into an allow.
            Err(ExecError::Unsupported(kind)) => {
                if fail_open {
                    HookOutput::no_op()
                } else {
                    HookOutput {
                        decision: Some("block".into()),
                        reason: Some(format!(
                            "hook handler type '{kind}' is not supported by this build"
                        )),
                        continue_execution: false,
                        specific: None,
                    }
                }
            }

            // Any other error.
            Err(ExecError::Other(msg)) => {
                if fail_open {
                    HookOutput::no_op()
                } else {
                    HookOutput {
                        decision: Some("block".into()),
                        reason: Some(format!("hook failed: {msg}")),
                        continue_execution: false,
                        specific: None,
                    }
                }
            }
        }
    }

    /// Dispatch a hook to the appropriate handler.
    async fn dispatch(
        &self,
        hook: &UserHook,
        payload: &str,
        cancel: CancellationToken,
        timeout_secs: u64,
    ) -> Result<String, ExecError> {
        match hook.effective_handler_type() {
            "command" => {
                let cmd = match &hook.command {
                    Some(c) => c.clone(),
                    None => return Err(ExecError::Other("command hook has no command".into())),
                };
                // Create a child cancellation token that fires on timeout.
                let hook_cancel = CancellationToken::new();
                let _guard = cancel.run_until_cancelled(async {
                    tokio::time::sleep(Duration::from_secs(timeout_secs)).await;
                    hook_cancel.cancel();
                });

                let combined = hook_cancel.clone();

                let exec_future = self.executor.exec(&cmd, payload, combined);

                tokio::select! {
                    result = exec_future => {
                        match result {
                            Ok((_code, stdout, _stderr)) => Ok(stdout),
                            Err(e) if e.to_string().contains("cancelled") => {
                                Err(if cancel.is_cancelled() { ExecError::Cancelled } else { ExecError::Timeout })
                            }
                            Err(e) => Err(ExecError::Other(e.to_string())),
                        }
                    }
                    _ = cancel.cancelled() => Err(ExecError::Cancelled),
                    _ = tokio::time::sleep(Duration::from_secs(timeout_secs)) => {
                        hook_cancel.cancel();
                        Err(ExecError::Timeout)
                    }
                }
            }
            // HTTP and agent handler types are not dispatched by this build.
            // Report that explicitly so the caller can apply the event's
            // fail-open policy; returning Ok("") here would parse as a no-op
            // and silently approve a fail-closed gate.
            other => Err(ExecError::Unsupported(other.to_string())),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Internal error type
// ─────────────────────────────────────────────────────────────────────────────

enum ExecError {
    Timeout,
    Cancelled,
    /// The hook declares a handler type this build cannot run.
    Unsupported(String),
    Other(String),
}

// ─────────────────────────────────────────────────────────────────────────────
// Payload builders
// ─────────────────────────────────────────────────────────────────────────────

fn build_pre_tool_payload(tool_name: &str, input_json: &str) -> String {
    let input: serde_json::Value = serde_json::from_str(input_json).unwrap_or(serde_json::Value::Null);
    serde_json::to_string(&serde_json::json!({
        "event": "PreToolUse",
        "toolName": tool_name,
        "input": input,
    }))
    .unwrap_or_default()
}

fn build_post_tool_payload(
    tool_name: &str,
    input_json: &str,
    result_text: &str,
    error_text: Option<&str>,
) -> String {
    let input: serde_json::Value = serde_json::from_str(input_json).unwrap_or(serde_json::Value::Null);
    serde_json::to_string(&serde_json::json!({
        "event": "PostToolUse",
        "toolName": tool_name,
        "input": input,
        "result": result_text,
        "error": error_text,
    }))
    .unwrap_or_default()
}

fn build_permission_payload(tool_name: &str, input_json: &str) -> String {
    let input: serde_json::Value = serde_json::from_str(input_json).unwrap_or(serde_json::Value::Null);
    serde_json::to_string(&serde_json::json!({
        "event": "PermissionRequest",
        "toolName": tool_name,
        "input": input,
    }))
    .unwrap_or_default()
}

fn build_agent_response_payload(response: &str, stop_reason: Option<&str>) -> String {
    serde_json::to_string(&serde_json::json!({
        "event": "AgentResponse",
        "response": response,
        "stopReason": stop_reason,
    }))
    .unwrap_or_default()
}

fn build_subagent_start_payload(
    task_id: &str,
    depth: u32,
    prompt: &str,
    toolset: &[String],
) -> String {
    serde_json::to_string(&serde_json::json!({
        "event": "SubagentStart",
        "taskId": task_id,
        "depth": depth,
        "prompt": prompt,
        "toolset": toolset,
    }))
    .unwrap_or_default()
}

fn build_subagent_stop_payload(task_id: &str, depth: u32, result: &str) -> String {
    serde_json::to_string(&serde_json::json!({
        "event": "SubagentStop",
        "taskId": task_id,
        "depth": depth,
        "result": result,
    }))
    .unwrap_or_default()
}

fn build_pre_compact_payload(
    trigger: &str,
    tokens_before: usize,
    message_count: usize,
    instructions: Option<&str>,
) -> String {
    serde_json::to_string(&serde_json::json!({
        "event": "PreCompact",
        "trigger": trigger,
        "tokensBefore": tokens_before,
        "messageCount": message_count,
        "instructions": instructions,
    }))
    .unwrap_or_default()
}

fn build_post_compact_payload(
    tokens_before: usize,
    tokens_after: usize,
    message_count: usize,
    summary: &str,
) -> String {
    serde_json::to_string(&serde_json::json!({
        "event": "PostCompact",
        "tokensBefore": tokens_before,
        "tokensAfter": tokens_after,
        "messageCount": message_count,
        "summary": summary,
    }))
    .unwrap_or_default()
}

// ─────────────────────────────────────────────────────────────────────────────
// Output mergers
// ─────────────────────────────────────────────────────────────────────────────

fn merge_pre_tool_use(pairs: Vec<(&UserHook, HookOutput)>) -> UserHookResult {
    // First blocking hook wins.
    for (hook, out) in &pairs {
        if out.is_blocking() {
            return UserHookResult {
                block: true,
                reason: out.reason.clone(),
                by_hook_command: Some(HookContentHash::hook_id(hook)),
                modified_input: None,
            };
        }
    }
    // Last writer wins for modifiedInput.
    let modified_input = pairs.iter().rev().find_map(|(_, out)| {
        out.specific.as_ref()?.get("modifiedInput").and_then(|v| {
            if v.is_object() { Some(v.clone()) } else { None }
        })
    });
    let by_hook_command = modified_input.as_ref().and_then(|_| {
        pairs.iter().rev().find_map(|(hook, out)| {
            out.specific
                .as_ref()
                .and_then(|s| s.get("modifiedInput"))
                .filter(|v| v.is_object())
                .map(|_| HookContentHash::hook_id(hook))
        })
    });
    UserHookResult { block: false, reason: None, by_hook_command, modified_input }
}

fn merge_post_tool_use(hooks: &[&UserHook], outputs: Vec<HookOutput>) -> PostToolUseResult {
    let mut result = PostToolUseResult::NO_CHANGE;
    for (hook, out) in hooks.iter().zip(outputs.iter()) {
        if let Some(mr) = out.specific.as_ref().and_then(|s| s.get("modifiedResult")).and_then(|v| v.as_str()) {
            result.modified_result = Some(mr.to_owned());
            result.by_hook_command = Some(HookContentHash::hook_id(hook));
        }
        if out.is_blocking() {
            result.block_reason = out.reason.clone();
            result.by_hook_command = Some(HookContentHash::hook_id(hook));
        }
    }
    result
}

fn merge_permission_request(hooks: &[&UserHook], outputs: Vec<HookOutput>) -> PermissionRequestResult {
    for (hook, out) in hooks.iter().zip(outputs.iter()) {
        let decision = out.decision.as_deref().map(str::to_lowercase);
        match decision.as_deref() {
            Some("allow") => {
                return PermissionRequestResult {
                    decision: PermissionDecision::Allow,
                    reason: out.reason.clone(),
                    by_hook_command: Some(HookContentHash::hook_id(hook)),
                };
            }
            Some("deny") | Some("block") => {
                return PermissionRequestResult {
                    decision: PermissionDecision::Deny,
                    reason: out.reason.clone(),
                    by_hook_command: Some(HookContentHash::hook_id(hook)),
                };
            }
            _ => {}
        }
    }
    PermissionRequestResult::PROMPT
}

fn merge_agent_response(hooks: &[&UserHook], outputs: Vec<HookOutput>) -> AgentResponseResult {
    let mut result = AgentResponseResult::NO_CHANGE;
    for (hook, out) in hooks.iter().zip(outputs.iter()) {
        if let Some(mr) = out.specific.as_ref().and_then(|s| s.get("modifiedResponse")).and_then(|v| v.as_str()) {
            result.modified_response = Some(mr.to_owned());
            result.by_hook_command = Some(HookContentHash::hook_id(hook));
        }
        if let Some(dc) = out.specific.as_ref().and_then(|s| s.get("displayContent")).and_then(|v| v.as_str()) {
            result.display_content = Some(dc.to_owned());
            result.by_hook_command = Some(HookContentHash::hook_id(hook));
        }
    }
    result
}

fn merge_subagent_start(pairs: Vec<(&UserHook, HookOutput)>) -> SubagentStartResult {
    for (hook, out) in &pairs {
        if out.is_blocking() {
            return SubagentStartResult {
                block: true,
                reason: out.reason.clone(),
                by_hook_command: Some(HookContentHash::hook_id(hook)),
                modified_prompt: None,
                additional_context: None,
                append_system_prompt: None,
            };
        }
    }
    let mut result = SubagentStartResult::ALLOW;
    for (hook, out) in &pairs {
        if let Some(mp) = out.specific.as_ref().and_then(|s| s.get("modifiedPrompt")).and_then(|v| v.as_str()) {
            result.modified_prompt = Some(mp.to_owned());
            result.by_hook_command = Some(HookContentHash::hook_id(hook));
        }
        if let Some(ac) = out.specific.as_ref().and_then(|s| s.get("additionalContext")).and_then(|v| v.as_str()) {
            result.additional_context = Some(ac.to_owned());
        }
        if let Some(asp) = out.specific.as_ref().and_then(|s| s.get("appendSystemPrompt")).and_then(|v| v.as_str()) {
            result.append_system_prompt = Some(asp.to_owned());
        }
    }
    result
}

fn merge_subagent_stop(hooks: &[&UserHook], outputs: Vec<HookOutput>) -> SubagentStopResult {
    let mut result = SubagentStopResult::NO_CHANGE;
    for (hook, out) in hooks.iter().zip(outputs.iter()) {
        if let Some(mr) = out.specific.as_ref().and_then(|s| s.get("modifiedResult")).and_then(|v| v.as_str()) {
            result.modified_result = Some(mr.to_owned());
            result.by_hook_command = Some(HookContentHash::hook_id(hook));
        }
        if out.is_blocking() && out.reason.is_some() {
            result.block_reason = out.reason.clone();
        }
    }
    result
}

fn merge_pre_compact(hooks: &[&UserHook], outputs: Vec<HookOutput>) -> PreCompactResult {
    let mut result = PreCompactResult::ALLOW;
    for (hook, out) in hooks.iter().zip(outputs.iter()) {
        if out.is_blocking() {
            result.cancel = true;
            result.by_hook_command = Some(HookContentHash::hook_id(hook));
        }
        if let Some(io) = out.specific.as_ref().and_then(|s| s.get("instructionsOverride")).and_then(|v| v.as_str()) {
            result.instructions_override = Some(io.to_owned());
        }
    }
    result
}

fn merge_post_compact(outputs: Vec<HookOutput>) -> PostCompactResult {
    let mut result = PostCompactResult::NO_CHANGE;
    for out in &outputs {
        if let Some(ac) = out.specific.as_ref().and_then(|s| s.get("additionalContext")).and_then(|v| v.as_str()) {
            result.additional_context = Some(ac.to_owned());
        }
    }
    result
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hooks::trust_store::HookTrustStore;
    use crate::hooks::{HookScope, InMemoryHookTrustStore};
    use std::sync::atomic::{AtomicU32, Ordering};

    // ── Mock executor ─────────────────────────────────────────────────────────

    #[derive(Debug)]
    struct MockExecutor {
        /// Canned responses in order.
        responses: std::sync::Mutex<std::collections::VecDeque<(i32, String)>>,
        call_count: Arc<AtomicU32>,
        /// Optional artificial delay to test timeouts.
        delay_ms: Option<u64>,
    }

    impl MockExecutor {
        fn new(responses: Vec<(i32, &str)>) -> Arc<Self> {
            Arc::new(Self {
                responses: std::sync::Mutex::new(
                    responses.into_iter().map(|(c, s)| (c, s.to_owned())).collect(),
                ),
                call_count: Arc::new(AtomicU32::new(0)),
                delay_ms: None,
            })
        }

        fn with_delay(responses: Vec<(i32, &str)>, delay_ms: u64) -> Arc<Self> {
            let e = Self::new(responses);
            Arc::new(Self { delay_ms: Some(delay_ms), ..Arc::try_unwrap(e).unwrap() })
        }
    }

    #[async_trait]
    impl HookExecutor for MockExecutor {
        async fn exec(
            &self,
            _command: &str,
            _payload: &str,
            cancel: CancellationToken,
        ) -> anyhow::Result<(i32, String, String)> {
            self.call_count.fetch_add(1, Ordering::SeqCst);
            if let Some(delay) = self.delay_ms {
                tokio::select! {
                    _ = tokio::time::sleep(Duration::from_millis(delay)) => {}
                    _ = cancel.cancelled() => return Err(anyhow::anyhow!("cancelled")),
                }
            }
            let (code, stdout) = self
                .responses
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or((0, String::new()));
            Ok((code, stdout, String::new()))
        }
    }

    // ── Hook builder helpers ──────────────────────────────────────────────────

    fn hook(event: &str, command: &str) -> UserHook {
        UserHook {
            event: event.into(),
            command: Some(command.into()),
            matcher: None,
            timeout_seconds: None,
            fail_open: None,
            unattended_decision: None,
            allow_system_prompt_replace: false,
            mutates: None,
            handler_type: None,
            url: None,
            hook_prompt: None,
            agent_type: None,
            enabled: true,
            scope: HookScope::User,
            plugin_origin: None,
        }
    }

    fn hook_with_timeout(event: &str, command: &str, timeout_secs: u64) -> UserHook {
        let mut h = hook(event, command);
        h.timeout_seconds = Some(timeout_secs);
        h
    }

    fn hook_with_matcher(event: &str, command: &str, matcher: &str) -> UserHook {
        let mut h = hook(event, command);
        h.matcher = Some(matcher.into());
        h
    }

    fn project_hook(event: &str, command: &str) -> UserHook {
        let mut h = hook(event, command);
        h.scope = HookScope::Project;
        h
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    // A PreToolUse hook that blocks returns block=true.
    #[tokio::test]
    async fn pre_tool_use_blocking_hook_blocks() {
        let exec = MockExecutor::new(vec![(0, r#"{"decision":"block","reason":"denied"}"#)]);
        let runner = HookRunner::with_executor(vec![hook("PreToolUse", "check.sh")], exec);
        let result = runner
            .run_pre_tool_use("bash", "{}", CancellationToken::new())
            .await;
        assert!(result.block);
        assert_eq!(result.reason.as_deref(), Some("denied"));
    }

    // An allowing hook passes through.
    #[tokio::test]
    async fn pre_tool_use_allow_passes() {
        let exec = MockExecutor::new(vec![(0, r#"{"decision":"allow"}"#)]);
        let runner = HookRunner::with_executor(vec![hook("PreToolUse", "check.sh")], exec);
        let result = runner
            .run_pre_tool_use("bash", "{}", CancellationToken::new())
            .await;
        assert!(!result.block);
    }

    // A tool-name matcher filters out non-matching tools.
    #[tokio::test]
    async fn matcher_skips_non_matching_tool() {
        let exec = MockExecutor::new(vec![]);
        let runner = HookRunner::with_executor(
            vec![hook_with_matcher("PreToolUse", "check.sh", "bash")],
            exec,
        );
        let result = runner
            .run_pre_tool_use("read_file", "{}", CancellationToken::new())
            .await;
        assert!(!result.block, "non-matching tool must not be blocked");
    }

    // Hooks that match run; others don't.
    #[tokio::test]
    async fn matcher_applies_to_matching_tool() {
        let exec = MockExecutor::new(vec![(0, r#"{"decision":"block"}"#)]);
        let runner = HookRunner::with_executor(
            vec![hook_with_matcher("PreToolUse", "check.sh", "bash")],
            exec,
        );
        let result = runner
            .run_pre_tool_use("bash", "{}", CancellationToken::new())
            .await;
        assert!(result.block, "matching tool must be blocked");
    }

    /// CRITICAL: A hook that times out must block (fail-closed) for PreToolUse.
    #[tokio::test]
    async fn pre_tool_use_timeout_is_fail_closed() {
        // Hook takes 2 seconds; we give it 1 ms.
        let exec = MockExecutor::with_delay(vec![], 2000);
        let h = hook_with_timeout("PreToolUse", "slow.sh", 0 /* 0 s → fires immediately */);
        let runner = HookRunner::with_executor(vec![h], exec);
        let result = runner
            .run_pre_tool_use("bash", "{}", CancellationToken::new())
            .await;
        assert!(result.block, "timed-out PreToolUse hook must block (fail-closed)");
    }

    /// PostToolUse timeout is fail-open.
    #[tokio::test]
    async fn post_tool_use_timeout_is_fail_open() {
        let exec = MockExecutor::with_delay(vec![], 2000);
        let h = hook_with_timeout("PostToolUse", "slow.sh", 0);
        let runner = HookRunner::with_executor(vec![h], exec);
        let result = runner
            .run_post_tool_use("bash", "{}", "output", None, CancellationToken::new())
            .await;
        assert!(result.modified_result.is_none(), "fail-open: no modification");
        assert!(result.block_reason.is_none(), "fail-open: no block");
    }

    /// CRITICAL: a handler type this build cannot dispatch must obey the
    /// event's fail-open policy, not silently succeed.
    ///
    /// Only `command` hooks are dispatched today.  Returning an empty stdout
    /// for `agent`/`http` parses as a no-op, which on a fail-closed event
    /// (PreToolUse) reads as "the hook approved this" — so a user who
    /// installed an agent hook precisely to gate tool calls would get a
    /// silent allow.  An undispatchable hook has not approved anything.
    #[tokio::test]
    async fn undispatchable_handler_blocks_on_a_fail_closed_event() {
        let mut h = hook("PreToolUse", "unused.sh");
        h.handler_type = Some("agent".into());
        let runner = HookRunner::with_executor(vec![h], MockExecutor::new(vec![]));
        let result = runner
            .run_pre_tool_use("bash", "{}", CancellationToken::new())
            .await;
        assert!(
            result.block,
            "an agent hook this build cannot run must block a fail-closed event, not allow it"
        );
    }

    /// The same undispatchable hook on a fail-open event stays a no-op.
    #[tokio::test]
    async fn undispatchable_handler_is_a_no_op_on_a_fail_open_event() {
        let mut h = hook("PostToolUse", "unused.sh");
        h.handler_type = Some("http".into());
        let runner = HookRunner::with_executor(vec![h], MockExecutor::new(vec![]));
        let result = runner
            .run_post_tool_use("bash", "{}", "output", None, CancellationToken::new())
            .await;
        assert!(result.block_reason.is_none(), "fail-open event must not block");
        assert!(result.modified_result.is_none(), "fail-open event must not modify");
    }

    /// An untrusted project hook must not run (fail-closed for PreToolUse).
    #[tokio::test]
    async fn untrusted_project_hook_is_blocked_fail_closed() {
        let store = Arc::new(InMemoryHookTrustStore::new());
        let guard = Arc::new(HookTrustGuard::new(store, "/project", None));
        let exec = MockExecutor::new(vec![(0, r#"{"decision":"allow"}"#)]);
        let runner = HookRunner::with_executor_and_guard(
            vec![project_hook("PreToolUse", "audit.sh")],
            exec.clone(),
            Some(guard),
        );
        let result = runner
            .run_pre_tool_use("bash", "{}", CancellationToken::new())
            .await;
        assert!(result.block, "untrusted project hook must block (fail-closed)");
        // The executor must NOT have been called — the trust guard prevented it.
        assert_eq!(
            exec.call_count.load(Ordering::SeqCst),
            0,
            "executor must not be called for untrusted hook"
        );
    }

    /// A trusted project hook runs normally.
    #[tokio::test]
    async fn trusted_project_hook_runs() {
        let store = Arc::new(InMemoryHookTrustStore::new());
        let hook_def = project_hook("PreToolUse", "audit.sh");
        let hash = HookContentHash::compute(&hook_def);
        store.trust("/project", &hash);

        let guard = Arc::new(HookTrustGuard::new(store, "/project", None));
        let exec = MockExecutor::new(vec![(0, r#"{"decision":"allow"}"#)]);
        let runner = HookRunner::with_executor_and_guard(
            vec![hook_def],
            exec.clone(),
            Some(guard),
        );
        let result = runner
            .run_pre_tool_use("bash", "{}", CancellationToken::new())
            .await;
        assert!(!result.block, "trusted hook must allow");
        assert_eq!(exec.call_count.load(Ordering::SeqCst), 1);
    }

    /// SubagentStart is fail-closed.
    #[tokio::test]
    async fn subagent_start_timeout_is_fail_closed() {
        let exec = MockExecutor::with_delay(vec![], 2000);
        let h = hook_with_timeout("SubagentStart", "guard.sh", 0);
        let runner = HookRunner::with_executor(vec![h], exec);
        let result = runner
            .run_subagent_start("task-1", 1, "do the thing", &[], CancellationToken::new())
            .await;
        assert!(result.block, "timed-out SubagentStart hook must block (fail-closed)");
    }

    /// SubagentStop is fail-open.
    #[tokio::test]
    async fn subagent_stop_timeout_is_fail_open() {
        let exec = MockExecutor::with_delay(vec![], 2000);
        let h = hook_with_timeout("SubagentStop", "notify.sh", 0);
        let runner = HookRunner::with_executor(vec![h], exec);
        let result = runner
            .run_subagent_stop("task-1", 1, "result text", CancellationToken::new())
            .await;
        assert!(result.modified_result.is_none(), "fail-open: no modification");
    }

    /// FastPath: no hooks for an event means no allocation, no dispatch.
    #[test]
    fn fast_path_flags_are_set_correctly() {
        let runner = HookRunner::new(vec![
            hook("PreToolUse", "a.sh"),
            hook("Stop", "b.sh"),
        ]);
        assert!(runner.has_pre_tool_use);
        assert!(runner.has_stop);
        assert!(!runner.has_post_tool_use);
        assert!(!runner.has_agent_response);
    }
}
