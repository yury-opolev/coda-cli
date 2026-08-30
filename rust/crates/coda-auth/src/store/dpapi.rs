//! Windows DPAPI credential store, compatible with the C# `DpapiTokenStore`.
//!
//! The C# build encrypts each credential with DPAPI under the current user and
//! writes one file per key to `~/.coda/credentials/<safe-key>.cred`. Reading
//! the same files means an existing installation keeps working when the engine
//! is swapped for the Rust one — without this, every user would silently find
//! themselves logged out and have to re-authenticate.
//!
//! DPAPI is keyed to the Windows user rather than the path, so a file copied
//! from the legacy location still decrypts, matching the C# comment.

use async_trait::async_trait;

use crate::error::AuthError;
use crate::store::CredentialStore;

/// `DATA_BLOB` as declared by wincrypt.h.
#[repr(C)]
struct DataBlob {
    cb_data: u32,
    pb_data: *mut u8,
}

#[cfg(windows)]
#[link(name = "crypt32")]
unsafe extern "system" {
    fn CryptProtectData(
        p_data_in: *const DataBlob,
        sz_data_descr: *const u16,
        p_optional_entropy: *const DataBlob,
        pv_reserved: *const core::ffi::c_void,
        p_prompt_struct: *const core::ffi::c_void,
        dw_flags: u32,
        p_data_out: *mut DataBlob,
    ) -> i32;

    fn CryptUnprotectData(
        p_data_in: *const DataBlob,
        pp_szdata_descr: *mut *mut u16,
        p_optional_entropy: *const DataBlob,
        pv_reserved: *const core::ffi::c_void,
        p_prompt_struct: *const core::ffi::c_void,
        dw_flags: u32,
        p_data_out: *mut DataBlob,
    ) -> i32;
}

#[cfg(windows)]
#[link(name = "kernel32")]
unsafe extern "system" {
    fn LocalFree(h_mem: *mut core::ffi::c_void) -> *mut core::ffi::c_void;
}

/// Encrypts with DPAPI under the current user.
#[cfg(windows)]
fn protect(plain: &[u8]) -> Result<Vec<u8>, AuthError> {
    let input = DataBlob {
        cb_data: plain.len() as u32,
        pb_data: plain.as_ptr() as *mut u8,
    };
    let mut output = DataBlob { cb_data: 0, pb_data: std::ptr::null_mut() };

    // SAFETY: `input` points at a live slice for the duration of the call, and
    // `output` is written by the API. On success the returned buffer is owned
    // by us and must be released with LocalFree, which the copy below does
    // before returning.
    let ok = unsafe {
        CryptProtectData(
            &input,
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            0,
            &mut output,
        )
    };
    if ok == 0 {
        return Err(AuthError::store("DPAPI encryption failed"));
    }

    // SAFETY: the API guarantees `pb_data` is valid for `cb_data` bytes.
    let bytes = unsafe { std::slice::from_raw_parts(output.pb_data, output.cb_data as usize) }
        .to_vec();
    // SAFETY: the buffer was allocated by the API and is freed exactly once.
    unsafe { LocalFree(output.pb_data as *mut core::ffi::c_void) };
    Ok(bytes)
}

/// Decrypts DPAPI ciphertext produced for the current user.
#[cfg(windows)]
fn unprotect(cipher: &[u8]) -> Result<Vec<u8>, AuthError> {
    let input = DataBlob {
        cb_data: cipher.len() as u32,
        pb_data: cipher.as_ptr() as *mut u8,
    };
    let mut output = DataBlob { cb_data: 0, pb_data: std::ptr::null_mut() };

    // SAFETY: as `protect`. The description output is not requested (null), so
    // there is no second allocation to release.
    let ok = unsafe {
        CryptUnprotectData(
            &input,
            std::ptr::null_mut(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            0,
            &mut output,
        )
    };
    if ok == 0 {
        // A failure here is expected and benign when the file belongs to a
        // different Windows user, so it must not be treated as corruption.
        return Err(AuthError::store(
            "DPAPI decryption failed; the credential may belong to another user",
        ));
    }

    // SAFETY: the API guarantees `pb_data` is valid for `cb_data` bytes.
    let bytes = unsafe { std::slice::from_raw_parts(output.pb_data, output.cb_data as usize) }
        .to_vec();
    // SAFETY: allocated by the API, freed exactly once.
    unsafe { LocalFree(output.pb_data as *mut core::ffi::c_void) };
    Ok(bytes)
}

/// Credential store backed by DPAPI-encrypted files.
#[derive(Debug, Clone)]
pub struct DpapiStore {
    directory: std::path::PathBuf,
}

impl DpapiStore {
    /// Uses `~/.coda/credentials`, the same directory as the C# build.
    pub fn default_location() -> Self {
        let base = directories::UserDirs::new()
            .map(|d| d.home_dir().to_path_buf())
            .unwrap_or_else(|| std::path::PathBuf::from("."));
        Self { directory: base.join(".coda").join("credentials") }
    }

    pub fn with_directory(directory: impl Into<std::path::PathBuf>) -> Self {
        Self { directory: directory.into() }
    }

    /// Maps a key to its file, replacing characters invalid in a filename.
    ///
    /// Keys look like `llmauth:github-copilot`, and the C# replaces every
    /// invalid character with `_`, yielding `llmauth_github-copilot.cred`.
    /// The set below is the Windows invalid set, which is what
    /// `Path.GetInvalidFileNameChars` returns there.
    fn path_for(&self, key: &str) -> std::path::PathBuf {
        const INVALID: &[char] =
            &['"', '<', '>', '|', ':', '*', '?', '\\', '/', '\0'];
        let safe: String = key
            .chars()
            .map(|c| if INVALID.contains(&c) || (c as u32) < 0x20 { '_' } else { c })
            .collect();
        self.directory.join(format!("{safe}.cred"))
    }

    /// Whether a credential file exists for this key, without decrypting it.
    pub fn contains(&self, key: &str) -> bool {
        self.path_for(key).is_file()
    }
}

impl Default for DpapiStore {
    fn default() -> Self {
        Self::default_location()
    }
}

#[async_trait]
impl CredentialStore for DpapiStore {
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
        let path = self.path_for(key);
        let Ok(cipher) = std::fs::read(&path) else {
            return Ok(None);
        };

        #[cfg(windows)]
        {
            let plain = unprotect(&cipher)?;
            String::from_utf8(plain)
                .map(Some)
                .map_err(|_| AuthError::store("credential is not valid UTF-8"))
        }
        #[cfg(not(windows))]
        {
            let _ = cipher;
            Err(AuthError::store("DPAPI credentials are only readable on Windows"))
        }
    }

    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
        #[cfg(windows)]
        {
            std::fs::create_dir_all(&self.directory)
                .map_err(|e| AuthError::store(format!("cannot create credential directory: {e}")))?;
            let cipher = protect(value.as_bytes())?;
            let path = self.path_for(key);
            std::fs::write(&path, cipher)
                .map_err(|e| AuthError::store(format!("cannot write credential: {e}")))
        }
        #[cfg(not(windows))]
        {
            let _ = (key, value);
            Err(AuthError::store("DPAPI credentials are only writable on Windows"))
        }
    }

    async fn delete(&self, key: &str) -> Result<(), AuthError> {
        let path = self.path_for(key);
        if path.exists() {
            std::fs::remove_file(&path)
                .map_err(|e| AuthError::store(format!("cannot delete credential: {e}")))?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The filename must match the C# `PathFor` exactly, or an existing
    /// installation's credentials are invisible to the Rust engine and the
    /// user is silently logged out.
    #[test]
    fn the_key_maps_to_the_same_filename_as_the_c_sharp_store() {
        let store = DpapiStore::with_directory("/creds");
        let path = store.path_for("llmauth:github-copilot");
        assert_eq!(path.file_name().unwrap(), "llmauth_github-copilot.cred");
    }

    #[test]
    fn every_invalid_filename_character_is_replaced() {
        let store = DpapiStore::with_directory("/creds");
        let path = store.path_for(r#"a:b\c/d*e?f"g<h>i|j"#);
        assert_eq!(path.file_name().unwrap(), "a_b_c_d_e_f_g_h_i_j.cred");
    }

    #[tokio::test]
    async fn a_missing_credential_is_none_not_an_error() {
        let store = DpapiStore::with_directory(std::env::temp_dir().join("coda-dpapi-missing"));
        let got = store.get("llmauth:nope").await.expect("no error");
        assert!(got.is_none(), "a missing credential must read as None");
    }

    #[cfg(windows)]
    #[test]
    fn a_round_trip_recovers_the_original_value() {
        let plain = b"{\"accessToken\":\"secret\"}";
        let cipher = protect(plain).expect("encrypt");
        assert_ne!(cipher.as_slice(), plain, "the stored bytes must be encrypted");
        let recovered = unprotect(&cipher).expect("decrypt");
        assert_eq!(recovered, plain);
    }

    #[cfg(windows)]
    #[test]
    fn corrupt_ciphertext_fails_rather_than_returning_garbage() {
        let cipher = protect(b"value").expect("encrypt");
        let mut corrupt = cipher.clone();
        let last = corrupt.len() - 1;
        corrupt[last] ^= 0xFF;
        assert!(unprotect(&corrupt).is_err(), "tampered ciphertext must not decrypt");
    }

    #[cfg(windows)]
    #[tokio::test]
    async fn a_stored_credential_reads_back_through_the_trait() {
        let dir = std::env::temp_dir().join(format!(
            "coda-dpapi-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        let store = DpapiStore::with_directory(&dir);
        store.set("llmauth:test", "hello").await.expect("set");
        let got = store.get("llmauth:test").await.expect("get");
        assert_eq!(got.as_deref(), Some("hello"));
        store.delete("llmauth:test").await.expect("delete");
        let gone = store.get("llmauth:test").await.expect("get");
        assert!(gone.is_none());
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// Compatibility check against a real installation.
    ///
    /// Skips when the C# build has never logged in, so it is meaningful on a
    /// developer machine without failing on a clean one. This is the test that
    /// proves swapping the engine does not log the user out.
    #[cfg(windows)]
    #[tokio::test]
    async fn the_real_c_sharp_credential_is_readable_when_present() {
        let store = DpapiStore::default_location();
        if !store.contains("llmauth:github-copilot") {
            eprintln!("skipping: no C# Copilot credential on this machine");
            return;
        }

        let value = store
            .get("llmauth:github-copilot")
            .await
            .expect("the C# credential must decrypt for the current user")
            .expect("the credential file exists so it must read back");

        let parsed: serde_json::Value =
            serde_json::from_str(&value).expect("the credential must be JSON");
        let keys: Vec<&String> =
            parsed.as_object().map(|o| o.keys().collect()).unwrap_or_default();
        assert!(
            !keys.is_empty(),
            "the decrypted credential should be a JSON object; got {value:.80}"
        );
        eprintln!("C# credential fields: {keys:?}");
        // Never log token material; the discriminant is not a secret.
        eprintln!("C# credential kind: {:?}", parsed.get("kind"));

        // The real point of this test: the Rust type must actually parse the
        // C# document. A field-naming mismatch here means the engine silently
        // finds no credential and the user appears logged out.
        let credential: crate::credential::Credential = serde_json::from_str(&value)
            .expect("the Rust Credential type must deserialize the C# document");
        assert_eq!(credential.provider_id, "github-copilot");
        assert!(
            credential.access_token.is_some(),
            "an access token must survive deserialization"
        );
    }
}
