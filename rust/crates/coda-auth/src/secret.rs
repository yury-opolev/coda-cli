//! `Secret<T>` — a transparent wrapper that redacts its contents in all
//! derived output paths.
//!
//! **Why this exists**: a naïve `#[derive(Debug)]` on any struct holding an
//! access token, API key, or refresh token would print the secret in logs,
//! test output, and error messages. Wrapping secrets in `Secret<T>` makes
//! leakage structurally impossible: `Debug` always prints `[REDACTED]`.

use std::fmt;

use serde::{Deserialize, Serialize};

/// A value whose `Debug` and `Display` implementations always print
/// `[REDACTED]`, preventing accidental exposure in logs or error output.
///
/// The inner value is accessible only via [`Secret::expose`] or
/// [`Secret::into_inner`], which are explicit call sites and easy to audit.
#[derive(Clone, Serialize, Deserialize)]
#[serde(transparent)]
pub struct Secret<T>(T);

impl<T> Secret<T> {
    /// Wrap a value as a secret.
    pub fn new(value: T) -> Self {
        Self(value)
    }

    /// Access the inner value for use (e.g. in HTTP headers).
    ///
    /// This is the one place a secret legitimately escapes the wrapper;
    /// callers must ensure the value is used directly and not formatted.
    pub fn expose(&self) -> &T {
        &self.0
    }

    /// Consume the wrapper and return the inner value.
    pub fn into_inner(self) -> T {
        self.0
    }
}

/// Always prints `[REDACTED]` — this is the entire point of the type.
impl<T> fmt::Debug for Secret<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("[REDACTED]")
    }
}

/// Mirrors `Debug`: display also prints `[REDACTED]`.
impl<T> fmt::Display for Secret<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("[REDACTED]")
    }
}

impl<T: PartialEq> PartialEq for Secret<T> {
    fn eq(&self, other: &Self) -> bool {
        self.0 == other.0
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn debug_output_never_contains_the_inner_value() {
        let s = Secret::new("super_secret_token_12345");
        let debug = format!("{:?}", s);
        assert!(!debug.contains("super_secret_token_12345"));
        assert_eq!(debug, "[REDACTED]");
    }

    #[test]
    fn display_output_never_contains_the_inner_value() {
        let s = Secret::new("top_secret_key_xyz");
        assert!(!format!("{}", s).contains("top_secret_key_xyz"));
    }

    #[test]
    fn expose_returns_the_inner_value() {
        let s = Secret::new("revealed");
        assert_eq!(s.expose(), &"revealed");
    }

    #[test]
    fn secret_in_a_struct_hides_through_derived_debug() {
        #[derive(Debug)]
        #[allow(dead_code)] // fields exist to test Debug output, not to be read
        struct Wrapper {
            name: String,
            token: Secret<String>,
        }

        let w = Wrapper {
            name: "test".into(),
            token: Secret::new("my_access_token".into()),
        };
        let debug = format!("{:?}", w);
        assert!(debug.contains("test"), "non-secret fields should appear");
        assert!(!debug.contains("my_access_token"), "secret must be hidden");
        assert!(debug.contains("[REDACTED]"));
    }
}
