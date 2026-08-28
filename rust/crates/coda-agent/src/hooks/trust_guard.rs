//! Hook trust guard: enforces trust before execution.
//!
//! Matches C# `HookTrustGuard.cs`.
//!
//! User-authored hooks (`scope = User`, no `plugin_origin`) are trusted
//! implicitly — the user wrote them.  Project-scoped hooks and any hook that
//! originated from a third-party plugin require an explicit trust decision.
//!
//! In headless / unattended mode (no interactive callback) an untrusted hook
//! is refused and the refusal is recorded in `session_denials` to suppress
//! repeated log entries.

use std::collections::HashSet;
use std::sync::{Arc, Mutex};

use super::{HookContentHash, UserHook};
use super::trust_store::HookTrustStore;
use super::HookScope;

// ─────────────────────────────────────────────────────────────────────────────
// HookTrustGuard
// ─────────────────────────────────────────────────────────────────────────────

pub struct HookTrustGuard {
    store: Arc<dyn HookTrustStore>,
    project_path: String,
    /// Optional interactive callback.  `None` → headless (refuse untrusted).
    prompt_callback: Option<Arc<dyn Fn(&UserHook) -> bool + Send + Sync>>,
    /// Session-scoped denial cache so a denied hook does not re-prompt every call.
    session_denials: Mutex<HashSet<String>>,
}

impl HookTrustGuard {
    pub fn new(
        store: Arc<dyn HookTrustStore>,
        project_path: impl Into<String>,
        prompt_callback: Option<Arc<dyn Fn(&UserHook) -> bool + Send + Sync>>,
    ) -> Self {
        Self {
            store,
            project_path: project_path.into(),
            prompt_callback,
            session_denials: Mutex::new(HashSet::new()),
        }
    }

    /// Returns `true` when the hook is permitted to execute.
    ///
    /// User-scoped, non-plugin hooks are always permitted.  Project-scoped or
    /// plugin hooks must be in the trust store or interactively approved.
    pub fn can_run(&self, hook: &UserHook) -> bool {
        // User-authored hooks (user scope, no plugin origin) are trusted implicitly.
        if hook.scope != HookScope::Project && hook.plugin_origin.is_none() {
            return true;
        }

        let hash = HookContentHash::compute(hook);
        if self.store.is_trusted(&self.project_path, &hash) {
            return true;
        }

        // Check session-level denial cache.
        if self.session_denials.lock().unwrap().contains(&hash) {
            return false;
        }

        // Not yet trusted — prompt if interactive; refuse if headless.
        if let Some(cb) = &self.prompt_callback {
            let granted = cb(hook);
            if granted {
                self.store.trust(&self.project_path, &hash);
            } else {
                self.session_denials.lock().unwrap().insert(hash);
            }
            return granted;
        }

        // Headless: refuse and cache to suppress repeated entries.
        self.session_denials.lock().unwrap().insert(hash);
        false
    }

    /// Revoke trust for a hook (e.g. after the user disables it via `/hooks disable`).
    pub fn revoke(&self, hook: &UserHook) {
        let hash = HookContentHash::compute(hook);
        self.store.revoke(&self.project_path, &hash);
        self.session_denials.lock().unwrap().remove(&hash);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hooks::{HookScope, InMemoryHookTrustStore};

    fn make_project_hook(command: &str) -> UserHook {
        UserHook {
            event: "PreToolUse".into(),
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
            scope: HookScope::Project,
            plugin_origin: None,
        }
    }

    fn make_user_hook(command: &str) -> UserHook {
        let mut h = make_project_hook(command);
        h.scope = HookScope::User;
        h
    }

    fn make_plugin_hook(command: &str) -> UserHook {
        let mut h = make_user_hook(command);
        h.plugin_origin = Some(("my-plugin".into(), "1.0.0".into()));
        h
    }

    // User-authored hook always permitted — no trust store lookup needed.
    #[test]
    fn user_hook_is_trusted_implicitly() {
        let store = Arc::new(InMemoryHookTrustStore::new());
        let guard = HookTrustGuard::new(store, "/project", None);
        let hook = make_user_hook("echo hi");
        assert!(guard.can_run(&hook));
    }

    // An untrusted project hook must not run in headless mode.
    #[test]
    fn untrusted_project_hook_is_refused_headless() {
        let store = Arc::new(InMemoryHookTrustStore::new());
        let guard = HookTrustGuard::new(store, "/project", None);
        let hook = make_project_hook("rm -rf /");
        assert!(!guard.can_run(&hook), "untrusted project hook must be refused");
    }

    // After trust is granted (via the store directly), the hook is allowed.
    #[test]
    fn trusted_project_hook_is_allowed() {
        let store = Arc::new(InMemoryHookTrustStore::new());
        let hook = make_project_hook("./audit.sh");
        let hash = HookContentHash::compute(&hook);
        store.trust("/project", &hash);

        let guard = HookTrustGuard::new(store, "/project", None);
        assert!(guard.can_run(&hook));
    }

    // A hook modified after trust was granted loses trust (hash changes).
    #[test]
    fn modified_hook_loses_trust() {
        let store = Arc::new(InMemoryHookTrustStore::new());
        let original = make_project_hook("./audit.sh");
        let hash = HookContentHash::compute(&original);
        store.trust("/project", &hash);

        let modified = make_project_hook("./audit.sh && rm -rf /");
        let guard = HookTrustGuard::new(store, "/project", None);
        assert!(!guard.can_run(&modified), "modified hook must lose trust");
    }

    // Interactive approval grants and persists trust.
    #[test]
    fn interactive_approval_persists_trust() {
        let store = Arc::new(InMemoryHookTrustStore::new());
        let hook = make_project_hook("./policy.sh");
        let hash = HookContentHash::compute(&hook);

        // Callback always approves.
        let cb: Arc<dyn Fn(&UserHook) -> bool + Send + Sync> = Arc::new(|_| true);
        let guard = HookTrustGuard::new(store.clone(), "/project", Some(cb));
        assert!(guard.can_run(&hook));
        // The store now holds the hash.
        assert!(store.is_trusted("/project", &hash));
    }

    // A denied hook is cached for the session and not re-prompted.
    #[test]
    fn denial_is_cached_for_session() {
        let store = Arc::new(InMemoryHookTrustStore::new());
        let hook = make_project_hook("bad.sh");
        let call_count = Arc::new(std::sync::atomic::AtomicU32::new(0));
        let cc = call_count.clone();
        let cb: Arc<dyn Fn(&UserHook) -> bool + Send + Sync> =
            Arc::new(move |_| { cc.fetch_add(1, std::sync::atomic::Ordering::SeqCst); false });
        let guard = HookTrustGuard::new(store, "/project", Some(cb));

        assert!(!guard.can_run(&hook));
        assert!(!guard.can_run(&hook)); // second call — must use cache
        assert_eq!(
            call_count.load(std::sync::atomic::Ordering::SeqCst),
            1,
            "prompt must only be shown once"
        );
    }

    // Plugin-origin user hooks also require trust.
    #[test]
    fn plugin_hook_requires_trust_even_if_user_scoped() {
        let store = Arc::new(InMemoryHookTrustStore::new());
        let guard = HookTrustGuard::new(store, "/project", None);
        let hook = make_plugin_hook("plugin-hook.sh");
        assert!(!guard.can_run(&hook), "plugin hook must require trust");
    }

    // revoke removes the hook from the trust store.
    #[test]
    fn revoke_removes_trust() {
        let store = Arc::new(InMemoryHookTrustStore::new());
        let hook = make_project_hook("audit.sh");
        let hash = HookContentHash::compute(&hook);
        store.trust("/project", &hash);

        let guard = HookTrustGuard::new(store.clone(), "/project", None);
        assert!(guard.can_run(&hook)); // was trusted
        guard.revoke(&hook);
        assert!(!guard.can_run(&hook)); // now untrusted
        assert!(!store.is_trusted("/project", &hash));
    }
}
