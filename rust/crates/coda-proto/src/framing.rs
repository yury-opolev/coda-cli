//! `Content-Length`-delimited framing for the Coda `serve` JSON-RPC stream.
//!
//! The engine speaks the same framing as the Language Server Protocol: each
//! message is preceded by one or more `Name: Value` headers, terminated by a
//! blank line, followed by exactly `Content-Length` bytes of UTF-8 JSON.
//!
//! The decoder is deliberately tolerant on input (bare `\n` line endings and
//! unknown headers are accepted) and strict on output (always `\r\n`), so that
//! we interoperate with the C# host regardless of which side is writing.

use bytes::{Buf, BytesMut};

/// Maximum header block size before we assume the peer is desynchronised.
const MAX_HEADER_BYTES: usize = 8 * 1024;

/// Upper bound on a single message body. The engine streams assistant text in
/// deltas, so legitimate frames are far below this.
const MAX_BODY_BYTES: usize = 64 * 1024 * 1024;

#[derive(Debug, thiserror::Error)]
pub enum FramingError {
    #[error("header block exceeded {MAX_HEADER_BYTES} bytes without a terminator")]
    HeaderTooLarge,
    #[error("message body of {0} bytes exceeds the {MAX_BODY_BYTES} byte limit")]
    BodyTooLarge(usize),
    #[error("header line is not valid UTF-8")]
    HeaderNotUtf8,
    #[error("malformed header line: {0:?}")]
    MalformedHeader(String),
    #[error("missing Content-Length header")]
    MissingContentLength,
    #[error("invalid Content-Length value: {0:?}")]
    InvalidContentLength(String),
}

/// Serialises a payload into a framed message.
pub fn encode_frame(payload: &[u8]) -> Vec<u8> {
    let header = format!("Content-Length: {}\r\n\r\n", payload.len());
    let mut out = Vec::with_capacity(header.len() + payload.len());
    out.extend_from_slice(header.as_bytes());
    out.extend_from_slice(payload);
    out
}

/// Incremental decoder that extracts whole frames from a byte stream.
#[derive(Debug, Default)]
pub struct FrameDecoder {
    buf: BytesMut,
    /// Body length of a frame whose headers we have already consumed.
    pending_body: Option<usize>,
}

impl FrameDecoder {
    pub fn new() -> Self {
        Self::default()
    }

    /// Appends freshly read bytes to the internal buffer.
    pub fn feed(&mut self, bytes: &[u8]) {
        self.buf.extend_from_slice(bytes);
    }

    /// Returns the number of buffered bytes not yet consumed.
    pub fn buffered(&self) -> usize {
        self.buf.len()
    }

    /// Attempts to pull one complete frame out of the buffer.
    ///
    /// Returns `Ok(None)` when more bytes are needed. Errors are fatal for the
    /// connection: the stream is desynchronised and cannot be recovered.
    pub fn next_frame(&mut self) -> Result<Option<Vec<u8>>, FramingError> {
        loop {
            if let Some(len) = self.pending_body {
                if self.buf.len() < len {
                    return Ok(None);
                }
                let body = self.buf.split_to(len).to_vec();
                self.pending_body = None;
                return Ok(Some(body));
            }

            match self.take_headers()? {
                Some(len) => self.pending_body = Some(len),
                None => return Ok(None),
            }
        }
    }

    /// Consumes a header block if one is fully buffered, yielding its
    /// `Content-Length`.
    fn take_headers(&mut self) -> Result<Option<usize>, FramingError> {
        let Some((header_end, body_start)) = find_header_terminator(&self.buf) else {
            if self.buf.len() > MAX_HEADER_BYTES {
                return Err(FramingError::HeaderTooLarge);
            }
            return Ok(None);
        };

        let header_bytes = &self.buf[..header_end];
        let headers = std::str::from_utf8(header_bytes).map_err(|_| FramingError::HeaderNotUtf8)?;

        let mut content_length = None;
        for line in headers.split("\r\n").flat_map(|l| l.split('\n')) {
            if line.is_empty() {
                continue;
            }
            let Some((name, value)) = line.split_once(':') else {
                return Err(FramingError::MalformedHeader(line.to_string()));
            };
            if name.trim().eq_ignore_ascii_case("content-length") {
                let value = value.trim();
                content_length = Some(
                    value
                        .parse::<usize>()
                        .map_err(|_| FramingError::InvalidContentLength(value.to_string()))?,
                );
            }
            // Content-Type and any other headers are accepted and ignored.
        }

        let len = content_length.ok_or(FramingError::MissingContentLength)?;
        if len > MAX_BODY_BYTES {
            return Err(FramingError::BodyTooLarge(len));
        }

        self.buf.advance(body_start);
        Ok(Some(len))
    }
}

/// Locates the blank line ending the header block.
///
/// Returns `(offset of the blank line, offset of the body)`, accepting both
/// `\r\n\r\n` and `\n\n` terminators.
fn find_header_terminator(buf: &[u8]) -> Option<(usize, usize)> {
    for i in 0..buf.len() {
        if buf[i..].starts_with(b"\r\n\r\n") {
            return Some((i, i + 4));
        }
        if buf[i..].starts_with(b"\n\n") {
            return Some((i, i + 2));
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    fn decode_all(chunks: &[&[u8]]) -> Vec<String> {
        let mut decoder = FrameDecoder::new();
        let mut frames = Vec::new();
        for chunk in chunks {
            decoder.feed(chunk);
            while let Some(frame) = decoder.next_frame().expect("decode") {
                frames.push(String::from_utf8(frame).expect("utf8"));
            }
        }
        frames
    }

    #[test]
    fn encodes_with_crlf_and_byte_length() {
        let framed = encode_frame(br#"{"a":1}"#);
        assert_eq!(framed, b"Content-Length: 7\r\n\r\n{\"a\":1}".to_vec());
    }

    #[test]
    fn round_trips_a_single_frame() {
        let framed = encode_frame(br#"{"jsonrpc":"2.0"}"#);
        assert_eq!(decode_all(&[&framed]), vec![r#"{"jsonrpc":"2.0"}"#]);
    }

    #[test]
    fn decodes_frames_split_across_arbitrary_chunk_boundaries() {
        let mut stream = encode_frame(b"{\"one\":1}");
        stream.extend_from_slice(&encode_frame(b"{\"two\":2}"));

        for split in 1..stream.len() {
            let (head, tail) = stream.split_at(split);
            assert_eq!(
                decode_all(&[head, tail]),
                vec!["{\"one\":1}", "{\"two\":2}"],
                "failed when split at byte {split}"
            );
        }
    }

    #[test]
    fn decodes_back_to_back_frames_in_one_chunk() {
        let mut stream = encode_frame(b"{\"a\":1}");
        stream.extend_from_slice(&encode_frame(b"{\"b\":2}"));
        assert_eq!(decode_all(&[&stream]), vec!["{\"a\":1}", "{\"b\":2}"]);
    }

    #[test]
    fn content_length_counts_bytes_not_characters() {
        // Four-byte UTF-8 emoji plus a two-byte character.
        let payload = "{\"t\":\"🚀é\"}".as_bytes();
        let framed = encode_frame(payload);
        let header = String::from_utf8_lossy(&framed[..framed.len() - payload.len()]);
        assert!(header.contains(&format!("Content-Length: {}", payload.len())));
        assert_eq!(
            decode_all(&[&framed]),
            vec![String::from_utf8(payload.to_vec()).unwrap()]
        );
    }

    #[test]
    fn accepts_additional_headers_and_lf_only_terminators() {
        let body = br#"{"ok":true}"#;
        let mut framed =
            format!("Content-Type: application/vscode-jsonrpc; charset=utf-8\nContent-Length: {}\n\n", body.len())
                .into_bytes();
        framed.extend_from_slice(body);
        assert_eq!(decode_all(&[&framed]), vec![r#"{"ok":true}"#]);
    }

    #[test]
    fn accepts_case_insensitive_header_names() {
        let body = br#"{"ok":true}"#;
        let mut framed = format!("content-length: {}\r\n\r\n", body.len()).into_bytes();
        framed.extend_from_slice(body);
        assert_eq!(decode_all(&[&framed]), vec![r#"{"ok":true}"#]);
    }

    #[test]
    fn handles_empty_body() {
        assert_eq!(decode_all(&[&encode_frame(b"")]), vec![""]);
    }

    #[test]
    fn reports_missing_content_length() {
        let mut decoder = FrameDecoder::new();
        decoder.feed(b"Content-Type: application/json\r\n\r\n{}");
        assert!(matches!(
            decoder.next_frame(),
            Err(FramingError::MissingContentLength)
        ));
    }

    #[test]
    fn reports_invalid_content_length() {
        let mut decoder = FrameDecoder::new();
        decoder.feed(b"Content-Length: abc\r\n\r\n");
        assert!(matches!(
            decoder.next_frame(),
            Err(FramingError::InvalidContentLength(_))
        ));
    }

    #[test]
    fn reports_malformed_header_line() {
        let mut decoder = FrameDecoder::new();
        decoder.feed(b"not-a-header\r\n\r\n");
        assert!(matches!(
            decoder.next_frame(),
            Err(FramingError::MalformedHeader(_))
        ));
    }

    #[test]
    fn rejects_an_oversized_header_block() {
        let mut decoder = FrameDecoder::new();
        decoder.feed(&vec![b'x'; MAX_HEADER_BYTES + 1]);
        assert!(matches!(
            decoder.next_frame(),
            Err(FramingError::HeaderTooLarge)
        ));
    }

    #[test]
    fn rejects_an_oversized_body() {
        let mut decoder = FrameDecoder::new();
        decoder.feed(format!("Content-Length: {}\r\n\r\n", MAX_BODY_BYTES + 1).as_bytes());
        assert!(matches!(
            decoder.next_frame(),
            Err(FramingError::BodyTooLarge(_))
        ));
    }

    #[test]
    fn waits_for_a_complete_body() {
        let mut decoder = FrameDecoder::new();
        decoder.feed(b"Content-Length: 10\r\n\r\n{\"a\":");
        assert!(decoder.next_frame().expect("decode").is_none());
        decoder.feed(b"12345");
        assert_eq!(
            decoder.next_frame().expect("decode"),
            Some(b"{\"a\":12345".to_vec())
        );
    }
}
