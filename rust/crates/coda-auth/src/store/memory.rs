//! In-memory [`CredentialStore`] for tests.
//!
//! Stores credentials in a plain `HashMap` protected by a `Mutex`.  This
//! avoids touching the OS keyring or the filesystem in test code.

use std::collections::HashMap;
use std::sync::Mutex;

use async_trait::async_trait;

use crate::error::AuthError;
use crate::store::CredentialStore;

/// Thread-safe in-memory store, suitable for tests.
#[derive(Default)]
pub struct InMemoryStore {
    map: Mutex<HashMap<String, String>>,
}

impl InMemoryStore {
    pub fn new() -> Self {
        Self::default()
    }
}

#[async_trait]
impl CredentialStore for InMemoryStore {
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
        Ok(self.map.lock().unwrap().get(key).cloned())
    }

    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
        self.map
            .lock()
            .unwrap()
            .insert(key.to_string(), value.to_string());
        Ok(())
    }

    async fn delete(&self, key: &str) -> Result<(), AuthError> {
        self.map.lock().unwrap().remove(key);
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn stores_and_retrieves() {
        let s = InMemoryStore::new();
        s.set("k", "v").await.unwrap();
        assert_eq!(s.get("k").await.unwrap(), Some("v".into()));
    }

    #[tokio::test]
    async fn missing_returns_none() {
        let s = InMemoryStore::new();
        assert!(s.get("absent").await.unwrap().is_none());
    }

    #[tokio::test]
    async fn delete_removes_entry() {
        let s = InMemoryStore::new();
        s.set("x", "y").await.unwrap();
        s.delete("x").await.unwrap();
        assert!(s.get("x").await.unwrap().is_none());
    }
}
