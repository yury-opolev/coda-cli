//! Image payload validation shared by prompts and question answers.

/// The image types every prompt path accepts.
pub(crate) const SUPPORTED_IMAGE_TYPES: [&str; 4] = ["image/png", "image/jpeg", "image/gif", "image/webp"];

/// Validates one wire image (`{ mediaType, base64 }`) exactly as a prompt
/// does, returning its media type and payload. Errors never quote the
/// payload.
pub(crate) fn validate_wire_image(image: &serde_json::Value) -> Result<(String, String), String> {
    let media_type = image["mediaType"].as_str().unwrap_or("");
    if !SUPPORTED_IMAGE_TYPES.contains(&media_type) {
        return Err(format!("unsupported image media type: {media_type}"));
    }
    let base64 = image["base64"].as_str().unwrap_or("");
    validate_base64(base64).map_err(|e| format!("invalid base64 encoding: {e}"))?;
    Ok((media_type.to_owned(), base64.to_owned()))
}

/// Lightweight base64 character-set + length validator.
///
/// Does not actually decode; just checks that all characters are in the
/// standard base64 alphabet and that the string is a multiple of 4 bytes
/// (which rules out truncated/corrupted inputs like `"not valid base64!!"`).
pub(crate) fn validate_base64(s: &str) -> Result<(), String> {
    if s.is_empty() {
        return Ok(());
    }
    let bytes = s.as_bytes();
    // Find where padding starts.
    let content_end = bytes.iter().rposition(|&b| b != b'=').map(|i| i + 1).unwrap_or(0);
    // Content bytes must all be standard base64 characters.
    for &b in &bytes[..content_end] {
        if !matches!(b, b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'+' | b'/') {
            return Err(format!("non-base64 character 0x{b:02x} in image data"));
        }
    }
    // Total length must be a multiple of 4.
    if bytes.len() % 4 != 0 {
        return Err(format!(
            "base64 length {} is not a multiple of 4",
            bytes.len()
        ));
    }
    // At most 2 padding bytes.
    let padding = bytes.len() - content_end;
    if padding > 2 {
        return Err("excessive padding in base64 data".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validate_base64_accepts_valid_input() {
        // A tiny real PNG's base64
        let png_b64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        assert!(validate_base64(png_b64).is_ok());
        assert!(validate_base64("").is_ok());
        assert!(validate_base64("AAAA").is_ok());
        assert!(validate_base64("AAA=").is_ok());
        assert!(validate_base64("AA==").is_ok());
    }

    #[test]
    fn validate_base64_rejects_invalid_characters() {
        // "!" is not a valid base64 character
        assert!(validate_base64("not valid base64!!").is_err());
        // Spaces are not valid
        assert!(validate_base64("AA AA").is_err());
    }
}
