//! Deterministic, profile-aware selection of the credential backend.
//!
//! # Why this exists
//!
//! Credentials for one profile must always live in one place. Earlier code
//! picked the backend by looking at *which credential files happened to
//! exist* — DPAPI when a Copilot file was there, the keyring otherwise — so
//! the answer changed as the user logged in and out, and a credential written
//! by one backend became invisible to the next call. The rule here is fixed
//! before anything is read:
//!
//! | host        | primary backend                                   |
//! |-------------|---------------------------------------------------|
//! | Windows     | DPAPI files — the format the .NET build writes     |
//! | everything else | AES-256-GCM files — the .NET `FileTokenStore` format |
//!
//! Nothing about the *contents* of the credential directory can change that.
//! What the contents can do is either (a) add a verified adoption source for
//! credentials an earlier build left behind, or (b) stop us dead with an
//! actionable error when the directory holds something we cannot safely
//! touch. Never a silent new empty backend.
//!
//! # Isolation
//!
//! A profile is *explicit* when the caller named it (`CODA_HOME`, a test, the
//! parity harness). An explicit profile never consults machine-global state:
//! the keyring service name (`coda-auth`) is not profile-scoped, so reading it
//! from an isolated run would surface the real user's credential — and writing
//! it would leak a test credential into the user's machine.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::coordination::{CommitCoordinator, FileCoordinator};
use crate::error::AuthError;
use crate::store::encrypted_file::EncryptedFileStore;
use crate::store::keyring::KeyringStore;
use crate::store::profile_store::{LegacyEntry, ProfileCredentialStore};
use crate::store::{CredentialStore, DpapiStore};

/// Environment variable that forces a backend, for hermetic CLI tests and for
/// hosts whose default is wrong.
pub const CODA_CREDENTIAL_BACKEND_ENV: &str = "CODA_CREDENTIAL_BACKEND";

/// The backend that owns a profile's credentials.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BackendKind {
    /// Windows DPAPI files (`.cred` per key), the .NET Windows format.
    Dpapi,
    /// AES-256-GCM files plus `key.bin`, the .NET `FileTokenStore` format.
    EncryptedFile,
}

/// A credential source an earlier build may have written, to be adopted into
/// the primary on first read and removed on logout.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LegacySource {
    /// AES files an earlier Rust build wrote into the profile's credential
    /// directory (identified by the `key.bin` it left there).
    EncryptedFileDir(PathBuf),
    /// The machine-global `coda-auth` keyring service an earlier Rust build
    /// used. Only ever for the default profile — the service name carries no
    /// profile, so an isolated profile must not touch it.
    Keyring,
}

/// The decision, taken before anything is opened or written.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StoreLayout {
    pub primary: BackendKind,
    pub primary_dir: PathBuf,
    pub legacy: Vec<LegacySource>,
    /// Adoption sources that exist but cannot be used, and why. Reported to
    /// the log so a user whose old credentials are unreachable is told, rather
    /// than silently meeting an empty store — and never "fixed" by deleting
    /// anything.
    pub unusable_legacy: Vec<String>,
}

/// The profile whose credentials are being addressed.
#[derive(Debug, Clone)]
pub struct Profile {
    root: PathBuf,
    explicit: bool,
}

impl Profile {
    /// A profile at `root`; `explicit` marks a caller-chosen profile that must
    /// stay isolated from machine-global state.
    pub fn new(root: impl Into<PathBuf>, explicit: bool) -> Self {
        Self { root: root.into(), explicit }
    }

    /// An isolated profile: tests, the parity harness, `CODA_HOME` runs.
    pub fn isolated(root: impl Into<PathBuf>) -> Self {
        Self::new(root, true)
    }

    /// The profile this process runs under: `CODA_HOME` when set (explicit),
    /// otherwise the user's home (default).
    pub fn from_env() -> Self {
        let explicit = std::env::var_os(crate::home::CODA_HOME_ENV)
            .is_some_and(|value| !value.is_empty());
        Self { root: crate::home::coda_home(), explicit }
    }

    /// The profile root (the directory that contains `.coda`).
    pub fn root(&self) -> &Path {
        &self.root
    }

    /// `<root>/.coda/credentials` — the directory both builds use.
    pub fn credentials_dir(&self) -> PathBuf {
        self.root.join(".coda").join("credentials")
    }

    /// Whether the caller named this profile explicitly.
    pub fn is_explicit(&self) -> bool {
        self.explicit
    }
}

impl Default for Profile {
    fn default() -> Self {
        Self::from_env()
    }
}

/// What the credential directory actually contains, as far as backend choice
/// is concerned. Gathered once, so the decision cannot drift between checks.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct DirectoryFacts {
    /// Size of `key.bin`, when one is present.
    pub key_file_len: Option<u64>,
    /// Whether any `*.cred` file is present.
    pub has_credential_files: bool,
}

impl DirectoryFacts {
    /// Reads the facts from `dir`. A directory that does not exist yet is
    /// simply empty — that is not an error.
    pub fn inspect(dir: &Path) -> Result<Self, AuthError> {
        let entries = match std::fs::read_dir(dir) {
            Ok(entries) => entries,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(Self::default()),
            Err(e) => return Err(AuthError::Io(e)),
        };

        Self::inspect_entries(dir, entries)
    }

    fn inspect_entries(
        dir: &Path,
        entries: impl IntoIterator<Item = std::io::Result<std::fs::DirEntry>>,
    ) -> Result<Self, AuthError> {
        let mut facts = Self::default();
        for entry in entries {
            let entry = entry?;
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) == Some("cred") {
                facts.has_credential_files = true;
            }

        }
        // Read after enumerating credentials: a directory iterator may omit
        // a key that was concurrently published before the first credential.
        facts.key_file_len = match std::fs::metadata(dir.join("key.bin")) {
            Ok(metadata) => Some(metadata.len()),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => None,
            Err(error) => return Err(AuthError::Io(error)),
        };
        Ok(facts)
    }
}

#[cfg(test)]
mod directory_observation_tests {
    use super::*;

    #[tokio::test]
    async fn key_metadata_is_rechecked_after_a_racing_directory_enumeration() {
        let dir = tempfile::tempdir().unwrap();
        let store = EncryptedFileStore::new(dir.path()).unwrap();
        store.set("llmauth:claude-ai", "test credential").await.unwrap();
        // A directory iterator may miss a concurrently published key while
        // subsequently observing that writer's first credential.
        let entries = std::fs::read_dir(dir.path()).unwrap()
            .map(|entry| entry.unwrap())
            .filter(|entry| entry.path().extension().is_some_and(|extension| extension == "cred"))
            .map(Ok);
        let facts = DirectoryFacts::inspect_entries(dir.path(), entries).unwrap();
        assert!(facts.has_credential_files);
        assert_eq!(facts.key_file_len, Some(32));
    }
}

/// The storage a profile resolved to.
pub struct AuthStorage {
    /// The store every reader and writer of this profile must use.
    pub store: Arc<dyn CredentialStore>,
    /// The same object as [`AuthStorage::store`], typed.
    ///
    /// A compound transaction (a login is one) needs operations no
    /// `CredentialStore` has: primary-only restoration and retirement-marker
    /// control, so a rollback can put presence, bytes and metadata back exactly
    /// as it found them. Those live on [`ProfileCredentialStore`] and are
    /// reached through here rather than through a magic key smuggled into
    /// `set`/`delete`.
    pub profile: Arc<ProfileCredentialStore>,
    /// Serializes credential commits with every other process sharing the
    /// profile, so a refresh cannot undo a logout that happened elsewhere.
    pub coordinator: Arc<dyn CommitCoordinator>,
    /// Which backend owns the credentials.
    pub backend: BackendKind,
    /// Where the primary backend keeps them.
    pub primary_dir: PathBuf,
}

impl std::fmt::Debug for AuthStorage {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AuthStorage")
            .field("backend", &self.backend)
            .field("primary_dir", &self.primary_dir)
            .finish()
    }
}

impl AuthStorage {
    /// Storage over a single primary store with process-local coordination.
    ///
    /// For tests and for embedders that supply their own store. Production
    /// code uses [`open_profile_storage`], which resolves the backend, the
    /// adoption sources and the cross-process coordinator for a profile.
    pub fn over_primary(
        primary: Arc<dyn CredentialStore>,
        backend: BackendKind,
        primary_dir: impl Into<PathBuf>,
    ) -> Self {
        let coordinator: Arc<dyn CommitCoordinator> =
            Arc::new(crate::coordination::LocalCoordinator::new());
        let profile = Arc::new(ProfileCredentialStore::with_primary(
            primary,
            Arc::clone(&coordinator),
        ));
        Self {
            store: Arc::clone(&profile) as Arc<dyn CredentialStore>,
            profile,
            coordinator,
            backend,
            primary_dir: primary_dir.into(),
        }
    }
}

/// The facts about every directory a profile may touch.
///
/// Gathered once, from the directories that are actually used: the refusal
/// rules apply to the directory the primary writes to, and the profile's
/// default credential directory only ever decides whether there is something
/// to adopt.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ProfileFacts {
    /// The directory the chosen primary backend will read and write.
    pub primary: DirectoryFacts,
    /// The profile's `.coda/credentials` directory. Equal to `primary` unless
    /// a backend that does not own that directory was chosen.
    pub credentials: DirectoryFacts,
}

impl ProfileFacts {
    /// Reads the facts for `profile` given the backend it will use.
    pub fn inspect(profile: &Profile, backend: BackendKind) -> Result<Self, AuthError> {
        let credentials_dir = profile.credentials_dir();
        let primary_dir = primary_dir_for(profile, backend);
        let credentials = DirectoryFacts::inspect(&credentials_dir)?;
        let primary = if primary_dir == credentials_dir {
            credentials
        } else {
            DirectoryFacts::inspect(&primary_dir)?
        };
        Ok(Self { primary, credentials })
    }
}

/// The backend named by an explicit override, or this host's default.
///
/// An unrecognised name is an error: silently using a different backend from
/// the one the caller asked for would write credentials somewhere they will
/// never be looked for.
pub fn resolve_backend(backend_override: Option<&str>) -> Result<BackendKind, AuthError> {
    match backend_override.map(str::trim) {
        None | Some("") | Some("auto") => Ok(platform_default_backend()),
        Some("dpapi") => {
            if cfg!(windows) {
                Ok(BackendKind::Dpapi)
            } else {
                Err(AuthError::StoreBackendUnknown { value: "dpapi".into() })
            }
        }
        Some("file") | Some("encrypted-file") => Ok(BackendKind::EncryptedFile),
        Some(other) => Err(AuthError::StoreBackendUnknown { value: other.to_owned() }),
    }
}

/// Where `backend` keeps this profile's credentials.
///
/// On Windows the profile's credential directory belongs to DPAPI: an AES
/// store writing `.cred` files there would replace DPAPI credentials with a
/// format the .NET build cannot read, so it gets its own directory.
pub fn primary_dir_for(profile: &Profile, backend: BackendKind) -> PathBuf {
    match backend {
        BackendKind::Dpapi => profile.credentials_dir(),
        BackendKind::EncryptedFile if cfg!(windows) => {
            profile.root().join(".coda").join("credentials-aes")
        }
        BackendKind::EncryptedFile => profile.credentials_dir(),
    }
}

/// Decides the layout for `profile`, given the backend and what the relevant
/// directories contain.
///
/// Pure: it reads nothing and writes nothing, so the decision is testable and
/// cannot depend on the machine it runs on beyond the target platform.
pub fn resolve_store_layout(
    profile: &Profile,
    backend: BackendKind,
    facts: ProfileFacts,
) -> Result<StoreLayout, AuthError> {
    let credentials_dir = profile.credentials_dir();
    let primary_dir = primary_dir_for(profile, backend);

    // An unreadable key file in the directory the primary itself uses is
    // ambiguous: the `.cred` files beside it may be encrypted with it, so
    // writing there could destroy credentials that are still recoverable.
    // Keep the key — it may be the only way back to them.
    if let Some(len) = facts.primary.key_file_len {
        if len != 32 {
            return Err(AuthError::incompatible(
                &primary_dir.join("key.bin"),
                format!(
                    "the key file is {len} bytes, not the 32-byte key this build writes, so the \
                     credentials beside it may be unreadable here. Nothing was changed; keep this \
                     file — it may be needed to recover them"
                ),
            ));
        }
    }

    let mut legacy = Vec::new();
    let mut unusable_legacy = Vec::new();

    if primary_dir == credentials_dir {
        // A 32-byte key in the DPAPI directory means an earlier Rust build
        // stored AES credentials there. Keep them readable instead of
        // presenting an empty store to a user who is, as far as they know,
        // signed in.
        if backend == BackendKind::Dpapi && facts.credentials.key_file_len == Some(32) {
            legacy.push(LegacySource::EncryptedFileDir(credentials_dir.clone()));
        }
    } else if facts.credentials.has_credential_files {
        // A backend that does not own the profile's credential directory must
        // leave it alone, but the user should hear that credentials there are
        // not being used.
        unusable_legacy.push(format!(
            "credentials in {} are not readable by the '{}' backend and were left untouched",
            credentials_dir.display(),
            match backend {
                BackendKind::Dpapi => "dpapi",
                BackendKind::EncryptedFile => "file",
            }
        ));
    }

    if !profile.is_explicit() {
        legacy.push(LegacySource::Keyring);
    }

    Ok(StoreLayout { primary: backend, primary_dir, legacy, unusable_legacy })
}

/// The backend for this host, independent of anything on disk.
fn platform_default_backend() -> BackendKind {
    if cfg!(windows) {
        BackendKind::Dpapi
    } else {
        BackendKind::EncryptedFile
    }
}

/// Opens the storage for `profile`, honouring `CODA_CREDENTIAL_BACKEND`.
pub fn open_profile_storage(profile: &Profile) -> Result<AuthStorage, AuthError> {
    let backend_override = std::env::var(CODA_CREDENTIAL_BACKEND_ENV).ok();
    open_profile_storage_with(profile, backend_override.as_deref())
}

/// Opens the storage for `profile` with an explicit backend choice.
///
/// Errors are returned, never swallowed: a caller that cannot open the store
/// must say so, because the alternative — an empty store — looks exactly like
/// a logged-out user and invites overwriting a credential that is still there.
pub fn open_profile_storage_with(
    profile: &Profile,
    backend_override: Option<&str>,
) -> Result<AuthStorage, AuthError> {
    let backend = resolve_backend(backend_override)?;
    let facts = ProfileFacts::inspect(profile, backend)?;
    let layout = resolve_store_layout(profile, backend, facts)?;
    open_layout(&layout)
}

/// Opens the storage described by `layout`.
pub fn open_layout(layout: &StoreLayout) -> Result<AuthStorage, AuthError> {
    for reason in &layout.unusable_legacy {
        tracing::warn!("credential storage: {reason}");
    }

    let primary: Arc<dyn CredentialStore> = match layout.primary {
        BackendKind::Dpapi => Arc::new(DpapiStore::with_directory(&layout.primary_dir)),
        BackendKind::EncryptedFile => {
            Arc::new(EncryptedFileStore::new(&layout.primary_dir)?)
        }
    };

    let mut legacy = Vec::new();
    for source in &layout.legacy {
        match source {
            LegacySource::EncryptedFileDir(dir) => legacy.push(LegacyEntry {
                store: Arc::new(EncryptedFileStore::new(dir)?),
                // The AES files sit at the very paths the DPAPI primary uses,
                // so adopting a credential *is* its retirement: deleting
                // afterwards would delete what was just written.
                shares_primary_path: *dir == layout.primary_dir,
            }),
            LegacySource::Keyring => legacy.push(LegacyEntry {
                store: Arc::new(KeyringStore::new()),
                shares_primary_path: false,
            }),
        }
    }

    // One coordinator for the profile: the store's migrations and the
    // manager's commits must take the same section, or a migration could
    // resurrect a credential a logout just removed.
    let coordinator: Arc<dyn CommitCoordinator> =
        Arc::new(FileCoordinator::new(&layout.primary_dir));

    let profile = Arc::new(ProfileCredentialStore::new(
        primary,
        legacy,
        Arc::clone(&coordinator),
    ));

    Ok(AuthStorage {
        store: Arc::clone(&profile) as Arc<dyn CredentialStore>,
        profile,
        coordinator,
        backend: layout.primary,
        primary_dir: layout.primary_dir.clone(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn profile() -> Profile {
        Profile::isolated(std::path::Path::new("/profile-root"))
    }

    fn layout_with(facts: ProfileFacts) -> Result<StoreLayout, AuthError> {
        resolve_store_layout(&profile(), platform_default_backend(), facts)
    }

    fn facts(key_file_len: Option<u64>, has_credential_files: bool) -> ProfileFacts {
        let dir = DirectoryFacts { key_file_len, has_credential_files };
        ProfileFacts { primary: dir, credentials: dir }
    }

    #[test]
    fn the_platform_decides_the_primary_backend() {
        let layout = layout_with(ProfileFacts::default()).unwrap();
        assert_eq!(layout.primary, platform_default_backend());
        assert_eq!(layout.primary_dir, profile().credentials_dir());
    }

    #[test]
    fn credential_files_never_change_the_primary_backend() {
        assert_eq!(
            layout_with(facts(Some(32), true)).unwrap().primary,
            layout_with(ProfileFacts::default()).unwrap().primary,
        );
    }

    #[test]
    fn a_key_file_of_the_wrong_length_in_the_primary_directory_stops_the_factory() {
        let err = layout_with(facts(Some(210), true)).unwrap_err();
        assert!(matches!(err, AuthError::StoreIncompatible { .. }), "got {err:?}");
    }

    #[test]
    fn an_unknown_backend_name_is_rejected() {
        let err = resolve_backend(Some("vault")).unwrap_err();
        assert!(matches!(err, AuthError::StoreBackendUnknown { .. }), "got {err:?}");
    }

    #[cfg(windows)]
    #[test]
    fn a_forced_file_backend_keeps_out_of_the_dpapi_directory() {
        let layout =
            resolve_store_layout(&profile(), BackendKind::EncryptedFile, ProfileFacts::default())
                .unwrap();
        assert_eq!(layout.primary, BackendKind::EncryptedFile);
        assert_ne!(
            layout.primary_dir,
            profile().credentials_dir(),
            "an AES store must not write into the DPAPI directory"
        );
    }

    /// A foreign key in a directory the primary does not use must not stop it,
    /// but the credentials there must be reported as unreachable.
    #[cfg(windows)]
    #[test]
    fn a_foreign_key_outside_the_primary_directory_is_reported_not_fatal() {
        let facts = ProfileFacts {
            primary: DirectoryFacts::default(),
            credentials: DirectoryFacts { key_file_len: Some(71), has_credential_files: true },
        };
        let layout =
            resolve_store_layout(&profile(), BackendKind::EncryptedFile, facts).unwrap();
        assert!(layout.legacy.is_empty());
        assert_eq!(layout.unusable_legacy.len(), 1, "the user must be told: {layout:?}");
    }

    #[cfg(windows)]
    #[test]
    fn earlier_rust_aes_credentials_become_an_adoption_source() {
        let layout = layout_with(facts(Some(32), true)).unwrap();
        assert!(layout
            .legacy
            .contains(&LegacySource::EncryptedFileDir(profile().credentials_dir())));
    }
}
