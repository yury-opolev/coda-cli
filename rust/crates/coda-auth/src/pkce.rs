//! PKCE (RFC 7636) helpers.
//!
//! Mirrors the C# `Pkce` class:
//! - Verifier: `base64url(random_bytes(32))` — 43 URL-safe ASCII characters.
//! - Challenge: `base64url(SHA-256(verifier_ascii_bytes))` (S256 method).
//! - State: `base64url(random_bytes(32))`.
//!
//! The verifier is generated from 32 raw bytes, yielding 43 base64url chars.
//! When computing the challenge the verifier is treated as an ASCII byte string
//! (base64url output is pure ASCII, so ASCII == UTF-8 here), which matches
//! the TypeScript client that `encodeURIComponent`s the string as UTF-8.

use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use base64::Engine;
use rand::RngCore;
use sha2::{Digest, Sha256};

/// Generate a PKCE code verifier: `base64url(random_bytes(32))`.
pub fn generate_code_verifier() -> String {
    let mut bytes = [0u8; 32];
    rand::thread_rng().fill_bytes(&mut bytes);
    base64url(&bytes)
}

/// Generate a PKCE S256 code challenge: `base64url(SHA-256(verifier_bytes))`.
pub fn generate_code_challenge(verifier: &str) -> String {
    let hash = Sha256::digest(verifier.as_bytes());
    base64url(&hash)
}

/// Generate a random OAuth state value: `base64url(random_bytes(32))`.
pub fn generate_state() -> String {
    let mut bytes = [0u8; 32];
    rand::thread_rng().fill_bytes(&mut bytes);
    base64url(&bytes)
}

/// Base64url-encode bytes without padding.
fn base64url(bytes: &[u8]) -> String {
    URL_SAFE_NO_PAD.encode(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// RFC 7636 Appendix B known test vector.
    ///
    /// Verifier: `dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk`
    /// Expected challenge (S256): `E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM`
    #[test]
    fn challenge_matches_rfc7636_known_vector() {
        let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        let expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
        assert_eq!(generate_code_challenge(verifier), expected);
    }

    #[test]
    fn verifier_uses_only_base64url_characters() {
        for _ in 0..50 {
            let v = generate_code_verifier();
            assert!(
                v.chars().all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_'),
                "verifier contained non-URL-safe characters: {v}"
            );
        }
    }

    #[test]
    fn verifier_has_expected_length() {
        // 32 raw bytes → ceil(32 * 4/3) = 43 base64url chars (no padding).
        let v = generate_code_verifier();
        assert_eq!(v.len(), 43, "verifier length must be 43 characters");
    }

    #[test]
    fn state_is_different_on_every_call() {
        let s1 = generate_state();
        let s2 = generate_state();
        // Not guaranteed, but with 256 bits of entropy a collision is negligible.
        assert_ne!(s1, s2, "two sequential states must not be equal");
    }

    #[test]
    fn challenge_uses_only_base64url_characters() {
        for _ in 0..20 {
            let v = generate_code_verifier();
            let c = generate_code_challenge(&v);
            assert!(
                c.chars().all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_'),
                "challenge contained non-URL-safe characters: {c}"
            );
        }
    }
}
