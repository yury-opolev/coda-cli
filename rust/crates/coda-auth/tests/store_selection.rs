//! Task 1 — deterministic, profile-aware credential storage.
//!
//! These tests pin the properties the whole auth stack depends on:
//!
//! * the backend a profile uses is a function of the *platform and the
//!   profile*, never of which credential files happen to exist;
//! * a credential written by the .NET build stays directly readable;
//! * a store that cannot read what it finds fails loudly and leaves the bytes
//!   on disk untouched, instead of quietly presenting an empty new backend;
//! * an explicitly isolated profile never reaches into the real user's
//!   keyring or default profile.
//!
//! Every test runs against an isolated temporary profile: no real credential,
//! keyring entry, or network call is involved.

use std::path::Path;
use std::sync::Arc;

use coda_auth::error::AuthError;
use coda_auth::store::{
    open_profile_storage_with, primary_dir_for, resolve_backend, resolve_store_layout, BackendKind,
    CredentialStore, EncryptedFileStore, LegacySource, Profile, ProfileFacts,
};

/// The backend this platform must use for a plain profile.
fn expected_platform_backend() -> BackendKind {
    if cfg!(windows) {
        BackendKind::Dpapi
    } else {
        BackendKind::EncryptedFile
    }
}

fn isolated_profile(dir: &Path) -> Profile {
    Profile::isolated(dir)
}

fn open(profile: &Profile) -> coda_auth::store::AuthStorage {
    open_profile_storage_with(profile, None).expect("the isolated profile must open")
}

// ── Backend selection is deterministic ───────────────────────────────────────

/// The chosen backend must not move under the user's feet: it is the same
/// before the first login, after a provider switch, after logout, and after a
/// process restart. A backend that flips mid-session silently loses the
/// credential that was just written.
#[tokio::test]
async fn the_backend_is_stable_before_login_after_switch_after_logout_and_after_restart() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = isolated_profile(dir.path());

    let fresh = open(&profile);
    assert_eq!(fresh.backend, expected_platform_backend());

    fresh
        .store
        .set("llmauth:claude-ai", r#"{"providerId":"claude-ai","kind":"OAuth"}"#)
        .await
        .expect("first login writes");
    let after_login = open(&profile);
    assert_eq!(after_login.backend, fresh.backend, "login must not move the backend");

    after_login
        .store
        .set("llmauth:github-copilot", r#"{"providerId":"github-copilot","kind":"OAuth"}"#)
        .await
        .expect("switching provider writes");
    after_login.store.delete("llmauth:claude-ai").await.expect("switch evicts");
    let after_switch = open(&profile);
    assert_eq!(after_switch.backend, fresh.backend, "a switch must not move the backend");

    after_switch.store.delete("llmauth:github-copilot").await.expect("logout");
    let after_logout = open(&profile);
    assert_eq!(after_logout.backend, fresh.backend, "logout must not move the backend");

    // "Process restart" is simply another factory call over the same profile
    // with nothing cached in this process.
    let after_restart = open(&profile);
    assert_eq!(after_restart.backend, fresh.backend, "a restart must not move the backend");
}

/// The presence of a particular provider's credential file must never be the
/// thing that picks the backend — that is how a store ends up reading files it
/// did not write.
#[test]
fn the_backend_is_not_chosen_from_the_credential_files_that_happen_to_exist() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = isolated_profile(dir.path());
    let creds = profile.credentials_dir();
    std::fs::create_dir_all(&creds).expect("create credential dir");
    std::fs::write(creds.join("llmauth_github-copilot.cred"), b"not-a-real-credential")
        .expect("write decoy");

    let backend = resolve_backend(None).expect("backend");
    let with_decoy = resolve_store_layout(
        &profile,
        backend,
        ProfileFacts::inspect(&profile, backend).expect("facts"),
    )
    .expect("layout");
    let empty = resolve_store_layout(&profile, backend, ProfileFacts::default()).expect("layout");

    assert_eq!(with_decoy.primary, empty.primary);
    assert_eq!(with_decoy.primary, expected_platform_backend());
}

/// An unknown explicit backend must fail loudly rather than falling back to a
/// default the caller did not ask for.
#[test]
fn an_unknown_backend_override_is_a_hard_error() {
    let err = resolve_backend(Some("magic-vault")).expect_err("an unknown backend must not resolve");
    assert!(
        matches!(err, AuthError::StoreBackendUnknown { .. }),
        "expected an unknown-backend error, got {err:?}"
    );
}

/// A backend that keeps its credentials somewhere else must not be blocked by
/// a foreign key file in the *default* credential directory: that directory is
/// not the one it writes to. Refusing there would strand a user whose chosen
/// backend is perfectly usable — and the only "fix" on offer would be deleting
/// an encryption key that may still be the way back to their credentials.
#[cfg(windows)]
#[tokio::test]
async fn a_separated_backend_is_usable_despite_a_foreign_key_in_the_default_directory() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = isolated_profile(dir.path());
    let creds = profile.credentials_dir();
    std::fs::create_dir_all(&creds).expect("create credential dir");
    let foreign_key = creds.join("key.bin");
    let original = vec![3u8; 71];
    std::fs::write(&foreign_key, &original).expect("write foreign key");
    std::fs::write(creds.join("llmauth_claude-ai.cred"), b"foreign").expect("write foreign cred");

    let storage = open_profile_storage_with(&profile, Some("file"))
        .expect("a separated backend must open");
    assert_eq!(storage.backend, BackendKind::EncryptedFile);
    assert_ne!(
        storage.primary_dir,
        creds,
        "the separated backend must keep out of the default credential directory"
    );

    storage.store.set("llmauth:claude-ai", "value").await.expect("set");
    assert_eq!(
        storage.store.get("llmauth:claude-ai").await.expect("get").as_deref(),
        Some("value")
    );
    assert_eq!(
        std::fs::read(&foreign_key).expect("the foreign key must still be there"),
        original,
        "a backend that does not own that directory must never touch its key"
    );
}

/// The refusal still applies to the directory the primary actually uses.
#[test]
fn a_foreign_key_in_the_primary_directory_still_stops_the_factory() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = isolated_profile(dir.path());
    let backend = resolve_backend(None).expect("backend");
    let primary = primary_dir_for(&profile, backend);
    std::fs::create_dir_all(&primary).expect("create primary dir");
    std::fs::write(primary.join("key.bin"), vec![4u8; 90]).expect("write foreign key");

    let err = open_profile_storage_with(&profile, None)
        .expect_err("a foreign key in the primary directory must stop the factory");
    assert!(
        matches!(err, AuthError::StoreIncompatible { .. }),
        "expected an incompatible-storage error, got {err:?}"
    );
}

// ── Isolation ────────────────────────────────────────────────────────────────

/// An explicitly isolated profile (CODA_HOME, tests, the parity harness) must
/// never consult the machine-global keyring: the keyring service name is not
/// profile-scoped, so doing so would leak the real user's credential into an
/// isolated run.
#[test]
fn an_explicit_profile_never_consults_the_global_keyring() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = isolated_profile(dir.path());
    let backend = resolve_backend(None).expect("backend");
    let layout =
        resolve_store_layout(&profile, backend, ProfileFacts::default()).expect("layout");
    assert!(
        !layout.legacy.contains(&LegacySource::Keyring),
        "an isolated profile must not list the global keyring: {layout:?}"
    );
}

/// The default profile, by contrast, must keep the earlier Rust keyring as an
/// explicit adoption source — otherwise a user who logged in with an earlier
/// Rust build silently meets a brand-new empty store.
#[test]
fn the_default_profile_keeps_the_earlier_rust_keyring_as_an_adoption_source() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = Profile::new(dir.path(), false);
    let backend = resolve_backend(None).expect("backend");
    let layout =
        resolve_store_layout(&profile, backend, ProfileFacts::default()).expect("layout");
    assert!(
        layout.legacy.contains(&LegacySource::Keyring),
        "the default profile must be able to adopt earlier keyring credentials: {layout:?}"
    );
}

// ── .NET compatibility (Windows DPAPI) ───────────────────────────────────────

/// The three providers the product ships must round-trip through the real
/// platform backend of an isolated profile.
#[tokio::test]
async fn every_shipped_provider_round_trips_through_the_profile_store() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = isolated_profile(dir.path());
    let storage = open(&profile);

    for provider in ["claude-ai", "github-copilot", "anthropic-api-key"] {
        let key = format!("llmauth:{provider}");
        let value = format!(r#"{{"providerId":"{provider}","kind":"OAuth"}}"#);
        storage.store.set(&key, &value).await.expect("set");
        assert_eq!(
            storage.store.get(&key).await.expect("get").as_deref(),
            Some(value.as_str()),
            "{provider} must round-trip"
        );
    }

    // A second factory call (a fresh process) must see the same credentials.
    let restarted = open(&profile);
    assert!(restarted
        .store
        .get("llmauth:anthropic-api-key")
        .await
        .expect("get")
        .is_some());
}

/// A credential document in the shape the .NET build writes (camelCase
/// properties, PascalCase `kind`) must be readable through the profile store
/// and parse into the Rust credential type — for every provider the product
/// stores, since each has its own credential shape (OAuth pair, device-flow
/// token, API key). The token material below is fabricated; the *shape* is
/// derived from `src/LlmAuth/Credential.cs` and the `JsonSerializerDefaults.Web`
/// options in `CredentialManager.cs`.
#[cfg(windows)]
#[tokio::test]
async fn dotnet_shaped_credentials_are_readable_and_parse_for_every_provider() {
    use coda_auth::{Credential, CredentialKind};

    // (provider, document, expected kind)
    let documents: [(&str, String, CredentialKind); 3] = [
        (
            "claude-ai",
            r#"{"providerId":"claude-ai","kind":"OAuth","accessToken":"fixture-access-token","refreshToken":"fixture-refresh-token","expiresAt":"2999-01-01T00:00:00+00:00","scopes":["user:inference","user:profile"],"account":{"accountUuid":"00000000-0000-0000-0000-000000000001","emailAddress":"fixture@example.invalid"}}"#.to_owned(),
            CredentialKind::OAuth,
        ),
        (
            "github-copilot",
            r#"{"providerId":"github-copilot","kind":"OAuth","accessToken":"fixture-copilot-token","refreshToken":"fixture-github-token","expiresAt":"2999-01-01T00:00:00+00:00","scopes":[]}"#.to_owned(),
            CredentialKind::OAuth,
        ),
        (
            "anthropic-api-key",
            r#"{"providerId":"anthropic-api-key","kind":"ApiKey","apiKey":"sk-ant-fixture-not-a-real-key"}"#.to_owned(),
            CredentialKind::ApiKey,
        ),
    ];

    for (provider, document, expected_kind) in documents {
        let dir = tempfile::tempdir().expect("tempdir");
        let profile = isolated_profile(dir.path());
        let creds = profile.credentials_dir();

        // Write it exactly the way the .NET Windows store does: DPAPI
        // ciphertext at `<credentials>/llmauth_<provider>.cred`.
        let key = format!("llmauth:{provider}");
        let dotnet_store = coda_auth::DpapiStore::with_directory(&creds);
        dotnet_store.set(&key, &document).await.expect("write .NET credential");
        assert!(
            creds.join(format!("llmauth_{provider}.cred")).is_file(),
            "the .NET file name must be used verbatim for {provider}"
        );

        let storage = open(&profile);
        let raw = storage
            .store
            .get(&key)
            .await
            .expect("read")
            .unwrap_or_else(|| panic!("the .NET {provider} credential must be visible"));
        assert_eq!(raw, document);

        let credential: Credential =
            serde_json::from_str(&raw).expect("the Rust type must parse it");
        assert_eq!(credential.provider_id, provider);
        assert_eq!(credential.kind, expected_kind);
        match expected_kind {
            CredentialKind::OAuth => {
                assert!(credential.access_token.is_some(), "{provider} must keep its token");
                assert!(credential.api_key.is_none());
            }
            CredentialKind::ApiKey => {
                assert!(credential.api_key.is_some(), "{provider} must keep its key");
                assert!(credential.access_token.is_none());
            }
        }
    }
}

// ── Adoption of earlier Rust credentials ─────────────────────────────────────

/// A user who logged in with an earlier Rust build (AES files in the profile's
/// credential directory) must not meet an empty store; and once the credential
/// has been adopted, a logout must remove it for good — a fallback copy that
/// reappears after logout is a security bug.
#[tokio::test]
async fn earlier_rust_credentials_are_adopted_and_never_resurface_after_logout() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = isolated_profile(dir.path());
    let creds = profile.credentials_dir();
    std::fs::create_dir_all(&creds).expect("create credential dir");

    // Earlier Rust build state: an AES store with its own key, in the profile's
    // credential directory.
    let earlier = EncryptedFileStore::new(&creds).expect("earlier store");
    earlier
        .set("llmauth:claude-ai", r#"{"providerId":"claude-ai","kind":"OAuth"}"#)
        .await
        .expect("earlier login");
    drop(earlier);

    let storage = open(&profile);
    let adopted = storage
        .store
        .get("llmauth:claude-ai")
        .await
        .expect("read")
        .expect("an earlier credential must remain visible, not silently disappear");
    assert!(adopted.contains("claude-ai"));

    storage.store.delete("llmauth:claude-ai").await.expect("logout");
    assert!(
        storage.store.get("llmauth:claude-ai").await.expect("read").is_none(),
        "logout must remove the credential everywhere it was reachable"
    );

    let restarted = open(&profile);
    assert!(
        restarted.store.get("llmauth:claude-ai").await.expect("read").is_none(),
        "a logged-out credential must not come back from a fallback copy"
    );
}

/// Credentials that do not belong to a provider — MCP server secrets — must
/// survive every provider-store operation untouched.
#[tokio::test]
async fn unrelated_mcp_secrets_survive_provider_credential_writes() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = isolated_profile(dir.path());
    let storage = open(&profile);

    storage.store.set("mcp:github/token", "mcp-secret-value").await.expect("set mcp");
    storage
        .store
        .set("llmauth:claude-ai", r#"{"providerId":"claude-ai","kind":"OAuth"}"#)
        .await
        .expect("login");
    storage.store.delete("llmauth:claude-ai").await.expect("logout");

    assert_eq!(
        storage.store.get("mcp:github/token").await.expect("get").as_deref(),
        Some("mcp-secret-value"),
        "an MCP secret must survive login and logout"
    );

    let restarted = open(&profile);
    assert_eq!(
        restarted.store.get("mcp:github/token").await.expect("get").as_deref(),
        Some("mcp-secret-value"),
        "an MCP secret must survive a restart"
    );
}

/// Sanity: the store handed out by the factory is usable as a plain
/// `Arc<dyn CredentialStore>` by callers such as MCP secret resolution.
#[tokio::test]
async fn the_factory_hands_out_a_shareable_credential_store() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = isolated_profile(dir.path());
    let storage = open(&profile);
    let shared: Arc<dyn CredentialStore> = Arc::clone(&storage.store);
    shared.set("mcp:x/y", "v").await.expect("set");
    assert_eq!(shared.get("mcp:x/y").await.expect("get").as_deref(), Some("v"));
}
