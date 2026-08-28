//! Live-switchable permission mode, shared across prompt layers and the loop.
//!
//! The mode is stored as an atomic u8 so reads and writes are tear-free across
//! threads without locking — same semantics as C#'s `Volatile.Read/Write`.

use std::sync::atomic::{AtomicU8, Ordering};
use std::sync::Arc;

use crate::permission::PermissionMode;

/// Thread-safe holder for the active `PermissionMode`.
///
/// Each prompt layer reads the mode on **every** request, not once at
/// construction, so a mid-run mode change (e.g. `/yolo` or `/permissions`)
/// is observed by the next decision of every prompt that shares this instance.
#[derive(Debug)]
pub struct PermissionModeState {
    mode: AtomicU8,
}

impl PermissionModeState {
    pub fn new(initial: PermissionMode) -> Self {
        Self { mode: AtomicU8::new(initial as u8) }
    }

    pub fn get(&self) -> PermissionMode {
        match self.mode.load(Ordering::Acquire) {
            0 => PermissionMode::Default,
            1 => PermissionMode::AcceptEdits,
            2 => PermissionMode::Plan,
            3 => PermissionMode::BypassPermissions,
            // All valid u8 values that are not 0–3 fall back to the safe default.
            _ => PermissionMode::Default,
        }
    }

    pub fn set(&self, mode: PermissionMode) {
        self.mode.store(mode as u8, Ordering::Release);
    }
}

/// Convenience wrapper for the shared, reference-counted state used throughout
/// the permission stack and across spawned tasks.
pub type SharedModeState = Arc<PermissionModeState>;

#[cfg(test)]
mod tests {
    use super::*;
    use crate::permission::PermissionMode::*;

    #[test]
    fn initial_mode_is_readable() {
        let s = PermissionModeState::new(Default);
        assert_eq!(s.get(), Default);
    }

    #[test]
    fn mode_can_be_changed() {
        let s = PermissionModeState::new(Default);
        s.set(BypassPermissions);
        assert_eq!(s.get(), BypassPermissions);
    }

    #[test]
    fn all_modes_survive_round_trip() {
        for mode in [Default, AcceptEdits, Plan, BypassPermissions] {
            let s = PermissionModeState::new(mode);
            assert_eq!(s.get(), mode, "{mode:?} did not survive round-trip");
        }
    }

    #[test]
    fn arc_shared_state_is_visible_across_clones() {
        let s = Arc::new(PermissionModeState::new(Default));
        let s2 = Arc::clone(&s);
        s2.set(Plan);
        assert_eq!(s.get(), Plan);
    }
}
