//! Bounded, non-echoing data handling for explicit API-key stdin input.
//! Terminal masking is the console host's responsibility.

use std::io::BufRead;

pub const MAX_API_KEY_BYTES: usize = 16 * 1024;

#[derive(Debug, thiserror::Error)]
pub enum SecretInputError {
    #[error("no API key was supplied")]
    Empty,
    #[error("API-key input exceeds the supported length")]
    TooLong,
    #[error("API-key input must be valid UTF-8")]
    InvalidEncoding,
    #[error("API-key input contains a control character")]
    InvalidControlCharacter,
    #[error("API-key input could not be read ({0:?})")]
    Io(std::io::ErrorKind),
}

/// Read one explicitly requested input line without printing or retaining
/// trailing lines. Refuse oversized input rather than returning a partial key.
pub fn read_api_key_line(reader: impl BufRead) -> Result<coda_auth::Secret<String>, SecretInputError> {
    let mut bytes = Vec::new();
    reader.take((MAX_API_KEY_BYTES + 3) as u64)
        .read_until(b'\n', &mut bytes)
        .map_err(|error| SecretInputError::Io(error.kind()))?;
    if bytes.last() == Some(&b'\n') {
        bytes.pop();
    }
    if bytes.last() == Some(&b'\r') {
        bytes.pop();
    }
    validate_api_key_bytes(bytes)
}

/// The one place collected key bytes become a [`coda_auth::Secret`].
///
/// Shared by the explicit-stdin reader above and the masked terminal prompt in
/// [`crate::console`], so a key typed at a terminal and a key piped in are held
/// to exactly the same bounds, encoding and control-character rules. A failure
/// never quotes the input.
pub fn validate_api_key_bytes(
    bytes: Vec<u8>,
) -> Result<coda_auth::Secret<String>, SecretInputError> {
    if bytes.len() > MAX_API_KEY_BYTES {
        return Err(SecretInputError::TooLong);
    }
    let value = String::from_utf8(bytes).map_err(|_| SecretInputError::InvalidEncoding)?;
    if value.trim().is_empty() {
        return Err(SecretInputError::Empty);
    }
    if value.chars().any(char::is_control) {
        return Err(SecretInputError::InvalidControlCharacter);
    }
    Ok(coda_auth::Secret::new(value))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_one_line_without_retaining_the_line_ending() {
        let key = read_api_key_line(std::io::Cursor::new(b"test-key\r\nunused\n")).unwrap();
        assert_eq!(key.expose(), "test-key");
        assert!(!format!("{key:?}").contains("test-key"));
    }

    #[test]
    fn rejects_empty_malformed_and_control_character_input() {
        for bytes in [
            b"".as_slice(), b"\n", b" \t \r\n", b"bad\0key\n", b"\xff\n",
        ] {
            assert!(read_api_key_line(std::io::Cursor::new(bytes)).is_err());
        }
    }

    #[test]
    fn overlong_input_is_an_error_not_a_truncated_key() {
        let mut boundary = vec![b'x'; MAX_API_KEY_BYTES];
        boundary.extend_from_slice(b"\r\n");
        assert_eq!(
            read_api_key_line(std::io::Cursor::new(boundary)).unwrap().expose().len(),
            MAX_API_KEY_BYTES,
        );
        let bytes = vec![b'x'; MAX_API_KEY_BYTES + 1];
        assert!(matches!(
            read_api_key_line(std::io::Cursor::new(bytes)),
            Err(SecretInputError::TooLong)
        ));
    }

    #[test]
    fn io_error_details_cannot_echo_secret_input() {
        struct Broken;
        impl std::io::Read for Broken {
            fn read(&mut self, _: &mut [u8]) -> std::io::Result<usize> {
                Err(std::io::Error::other("private-key-sentinel"))
            }
        }
        let error = read_api_key_line(std::io::BufReader::new(Broken)).unwrap_err();
        assert!(!error.to_string().contains("private-key-sentinel"));
        assert!(!format!("{error:?}").contains("private-key-sentinel"));
    }
}
