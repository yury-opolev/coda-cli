//! Session-id validation and minting.
//!
//! Mirrors C# `Coda.Sdk.SessionIds` exactly so the two engines share the same
//! guard and produce the same id format.

use uuid::Uuid;

/// Returns `true` when `id` is safe to use as a file-name component.
///
/// Rejects:
/// - empty strings
/// - any path-separator character (`/` or `\`), which prevents `../` traversal
/// - any Windows-invalid filename character (`<>:"|?*` or ASCII controls)
/// - the special directory names `.` and `..`
/// - anything whose final path component differs from the whole string (extra
///   `Path::file_name` check that catches edge cases like trailing separators)
pub fn is_valid(id: &str) -> bool {
    if id.is_empty() {
        return false;
    }
    // Reject OS path separators — the primary traversal vector.
    if id.contains('/') || id.contains('\\') {
        return false;
    }
    // Reject special directory names.
    if id == "." || id == ".." {
        return false;
    }
    // Reject Windows-invalid filename characters and ASCII control characters.
    const INVALID: &[char] = &['<', '>', ':', '"', '|', '?', '*'];
    if id.chars().any(|c| INVALID.contains(&c) || (c as u32) < 32) {
        return false;
    }
    // Final guard: std::path::Path::file_name must equal the whole string,
    // which catches platform-specific edge cases not already caught above.
    std::path::Path::new(id).file_name().and_then(|n| n.to_str()) == Some(id)
}

/// Mint a fresh session id: a 12-character lowercase-hex token, matching
/// C#'s `Guid.NewGuid().ToString("N")[..12]`.
pub fn new_id() -> String {
    let hex = Uuid::new_v4().simple().to_string();
    hex[..12].to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    // ── Validity ─────────────────────────────────────────────────────────────

    #[test]
    fn valid_simple_alphanumeric() {
        assert!(is_valid("abc123"));
        assert!(is_valid("9cb6ee1f1779"));
    }

    #[test]
    fn valid_with_hyphens_and_underscores() {
        assert!(is_valid("my-session_01"));
    }

    #[test]
    fn valid_full_uuid() {
        assert!(is_valid("550e8400-e29b-41d4-a716-446655440000"));
    }

    #[test]
    fn rejects_empty() {
        assert!(!is_valid(""));
    }

    #[test]
    fn rejects_dot() {
        assert!(!is_valid("."));
        assert!(!is_valid(".."));
    }

    // ── Security: path traversal ─────────────────────────────────────────────
    // These are the mutation-verified security tests required by the spec.

    #[test]
    fn rejects_forward_slash_traversal() {
        assert!(!is_valid("../../etc/passwd"));
        assert!(!is_valid("../secret"));
        assert!(!is_valid("a/b"));
    }

    #[test]
    fn rejects_backslash_traversal() {
        assert!(!is_valid(r"..\..\secret"));
        assert!(!is_valid(r"a\b"));
    }

    #[test]
    fn rejects_windows_invalid_chars() {
        for c in ['<', '>', ':', '"', '|', '?', '*'] {
            let id = format!("bad{c}id");
            assert!(!is_valid(&id), "expected invalid for char {c:?}");
        }
    }

    #[test]
    fn rejects_control_characters() {
        assert!(!is_valid("bad\x00id"));
        assert!(!is_valid("bad\x1fid"));
    }

    #[test]
    fn new_id_is_twelve_lowercase_hex_chars() {
        let id = new_id();
        assert_eq!(id.len(), 12, "id must be 12 characters");
        assert!(
            id.chars().all(|c| c.is_ascii_hexdigit() && !c.is_uppercase()),
            "id must be lowercase hex: {id:?}"
        );
    }

    #[test]
    fn new_id_is_valid() {
        for _ in 0..10 {
            assert!(is_valid(&new_id()));
        }
    }

    #[test]
    fn new_ids_are_unique() {
        let a = new_id();
        let b = new_id();
        assert_ne!(a, b);
    }
}
