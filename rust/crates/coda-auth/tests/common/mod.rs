//! Shared fixtures for the credential-coordination tests.
//!
//! The "network" here is a semaphore the test controls, so every race is
//! driven by an explicit barrier rather than by timing.

#![allow(dead_code)]

pub mod http;

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use coda_auth::credential::{Credential, CredentialKind};
use coda_auth::error::AuthError;
use coda_auth::provider::AuthProvider;
use coda_auth::secret::Secret;
use coda_auth::store::CredentialStore;

pub const PROVIDER: &str = "claude-ai";
pub const OTHER_PROVIDER: &str = "github-copilot";

/// A provider whose refresh blocks until the test releases it.
pub struct BarrierProvider {
    id: &'static str,
    /// Signalled when a refresh has started.
    started: tokio::sync::Semaphore,
    /// The test adds a permit here to let the refresh finish.
    release: tokio::sync::Semaphore,
    refreshes: AtomicUsize,
    token: &'static str,
}

impl BarrierProvider {
    pub fn new(id: &'static str, token: &'static str) -> Arc<Self> {
        Arc::new(Self {
            id,
            started: tokio::sync::Semaphore::new(0),
            release: tokio::sync::Semaphore::new(0),
            refreshes: AtomicUsize::new(0),
            token,
        })
    }

    pub async fn wait_until_refresh_started(&self) {
        tokio::time::timeout(Duration::from_secs(10), self.started.acquire())
            .await
            .expect("a refresh should have started")
            .expect("semaphore open")
            .forget();
    }

    pub fn let_the_refresh_finish(&self) {
        self.release.add_permits(1);
    }

    pub fn refresh_count(&self) -> usize {
        self.refreshes.load(Ordering::SeqCst)
    }
}

#[async_trait]
impl AuthProvider for BarrierProvider {
    fn provider_id(&self) -> &str {
        self.id
    }

    fn needs_refresh(&self, credential: &Credential) -> bool {
        credential
            .expires_at
            .map(|exp| exp <= chrono::Utc::now() + chrono::Duration::minutes(10))
            .unwrap_or(false)
    }

    async fn refresh(&self, credential: &Credential) -> Result<Credential, AuthError> {
        self.started.add_permits(1);
        let permit = self.release.acquire().await.expect("semaphore open");
        permit.forget();
        self.refreshes.fetch_add(1, Ordering::SeqCst);
        Ok(Credential {
            expires_at: Some(chrono::Utc::now() + chrono::Duration::hours(1)),
            access_token: Some(Secret::new(self.token.into())),
            ..credential.clone()
        })
    }

    fn auth_headers(&self, _: &Credential) -> Result<Vec<(String, String)>, AuthError> {
        Ok(vec![("authorization".into(), "******".into())])
    }
}

/// A credential that is past its expiry and therefore due for a refresh.
pub fn expired(provider_id: &str, token: &str) -> Credential {
    Credential {
        provider_id: provider_id.into(),
        kind: CredentialKind::OAuth,
        access_token: Some(Secret::new(token.into())),
        refresh_token: Some(Secret::new("refresh-token".into())),
        api_key: None,
        expires_at: Some(chrono::Utc::now() - chrono::Duration::minutes(1)),
        scopes: Vec::new(),
        account: None,
    }
}

/// A credential with plenty of life left.
pub fn fresh(provider_id: &str, token: &str) -> Credential {
    Credential {
        expires_at: Some(chrono::Utc::now() + chrono::Duration::hours(4)),
        ..expired(provider_id, token)
    }
}

pub async fn seed(store: &Arc<dyn CredentialStore>, credential: &Credential) {
    store
        .set(
            &format!("llmauth:{}", credential.provider_id),
            &serde_json::to_string(credential).expect("serialize"),
        )
        .await
        .expect("seed");
}

pub fn access_token(credential: &Credential) -> &str {
    credential.access_token.as_ref().expect("token").expose()
}

/// A provider whose refresh returns a credential belonging to someone else —
/// the shape of a provider bug, or of a response that was not what it claimed.
pub struct WrongProviderRefresh {
    id: &'static str,
    returns: &'static str,
}

impl WrongProviderRefresh {
    pub fn new(id: &'static str, returns: &'static str) -> Arc<Self> {
        Arc::new(Self { id, returns })
    }
}

#[async_trait]
impl AuthProvider for WrongProviderRefresh {
    fn provider_id(&self) -> &str {
        self.id
    }

    fn needs_refresh(&self, _: &Credential) -> bool {
        true
    }

    async fn refresh(&self, credential: &Credential) -> Result<Credential, AuthError> {
        Ok(Credential {
            provider_id: self.returns.to_owned(),
            expires_at: Some(chrono::Utc::now() + chrono::Duration::hours(1)),
            ..credential.clone()
        })
    }

    fn auth_headers(&self, _: &Credential) -> Result<Vec<(String, String)>, AuthError> {
        Ok(vec![("authorization".into(), "******".into())])
    }
}

/// A store that pauses inside a chosen write, so a test can hold a caller
/// *inside* its commit section — between the read it based the write on and
/// the write itself — which is where the dangerous interleavings live.
pub struct PausingStore {
    inner: Arc<crate::common::InMemory>,
    pause_on: &'static str,
    armed: std::sync::atomic::AtomicBool,
    entered: tokio::sync::Semaphore,
    release: tokio::sync::Semaphore,
    paused_once: std::sync::atomic::AtomicBool,
}

/// The in-memory store the fixtures wrap.
pub type InMemory = coda_auth::store::InMemoryStore;

impl PausingStore {
    pub fn new(pause_on: &'static str) -> Arc<Self> {
        Arc::new(Self {
            inner: Arc::new(InMemory::new()),
            pause_on,
            armed: std::sync::atomic::AtomicBool::new(false),
            entered: tokio::sync::Semaphore::new(0),
            release: tokio::sync::Semaphore::new(0),
            paused_once: std::sync::atomic::AtomicBool::new(false),
        })
    }

    /// Starts pausing writes; called after the test has finished setting up so
    /// seeding does not trip the barrier.
    pub fn arm(&self) {
        self.armed.store(true, std::sync::atomic::Ordering::SeqCst);
    }

    pub async fn wait_until_paused_in_a_write(&self) {
        tokio::time::timeout(Duration::from_secs(10), self.entered.acquire())
            .await
            .expect("a write should have reached the pause")
            .expect("open")
            .forget();
    }

    pub fn let_the_write_finish(&self) {
        self.release.add_permits(1);
    }
}

#[async_trait]
impl CredentialStore for PausingStore {
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
        self.inner.get(key).await
    }

    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
        if key == self.pause_on
            && self.armed.load(std::sync::atomic::Ordering::SeqCst)
            && !self.paused_once.swap(true, std::sync::atomic::Ordering::SeqCst)
        {
            self.entered.add_permits(1);
            self.release.acquire().await.expect("open").forget();
        }
        self.inner.set(key, value).await
    }

    async fn delete(&self, key: &str) -> Result<(), AuthError> {
        self.inner.delete(key).await
    }
}
