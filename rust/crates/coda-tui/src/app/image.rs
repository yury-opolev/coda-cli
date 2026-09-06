//! Image staging helpers shared by `/image` and clipboard paste.
//!
//! Collecting these in one place enforces a single 5 MB limit and a single
//! `[Image N]` token format across every path that stages an image.

use coda_proto::messages;

use super::App;

/// Maximum image-file bytes before base64 encoding, shared with `/image`.
pub(in crate::app) const MAX_IMAGE_BYTES: usize = 5 * 1024 * 1024;

/// Maximum raw pixel count before encoding, to bound memory allocation.
///
/// 16 M pixels x 4 bytes/pixel = 64 MiB raw RGBA — large enough for any
/// screenshot that fits on a modern display, small enough to encode promptly.
const MAX_PIXEL_COUNT: usize = 16 * 1024 * 1024;

pub(super) fn images_for_draft(images: &[messages::WireImage], text: &str) -> Vec<messages::WireImage> {
    images.iter().enumerate()
        .filter(|(index, _)| text.contains(&format!("[Image {}]", index + 1)))
        .map(|(_, image)| image.clone())
        .collect()
}

impl App {
    /// Stages encoded image bytes as an attachment and inserts the placeholder
    /// token into the composer draft.
    ///
    /// Returns the 1-based attachment label so the caller can use it in a
    /// confirmation notice.  The same label appears in the `[Image N]` token
    /// inserted at the current cursor position.
    pub(in crate::app) fn stage_image_bytes(&mut self, media_type: &str, bytes: &[u8]) -> usize {
        let label = self.staged_images.len() + 1;
        self.staged_images.push(messages::WireImage {
            media_type: media_type.to_string(),
            base64: base64_encode(bytes),
        });
        let token = format!("[Image {label}]");
        if !self.composer.is_empty() {
            self.composer.insert(" ");
        }
        self.composer.insert(&token);
        label
    }
}

/// Encodes RGBA8 pixel data as a PNG byte stream.
///
/// Returns `Err` for:
/// - dimensions that would overflow `usize` or `u32`
/// - pixel counts beyond `MAX_PIXEL_COUNT` (bounds the allocations)
/// - buffer lengths that do not match `width x height x 4`
/// - PNG encoder failures (corrupt state, I/O)
pub(in crate::app) fn rgba_to_png(
    width: usize,
    height: usize,
    rgba: &[u8],
) -> Result<Vec<u8>, String> {
    // Cap raw pixel memory first, before any allocation that depends on it.
    let pixel_count = width
        .checked_mul(height)
        .ok_or_else(|| format!("Image dimensions overflow ({width}x{height})"))?;

    if pixel_count > MAX_PIXEL_COUNT {
        return Err(format!(
            "Image too large ({width}x{height} = {pixel_count} pixels, max {MAX_PIXEL_COUNT})"
        ));
    }

    // Verify the buffer matches the declared geometry before encoding.
    let expected = pixel_count
        .checked_mul(4)
        .ok_or_else(|| format!("Byte count overflow ({pixel_count}x4)"))?;
    if rgba.len() != expected {
        return Err(format!(
            "RGBA buffer length mismatch: expected {expected}, got {}",
            rgba.len()
        ));
    }

    let w = u32::try_from(width).map_err(|_| format!("Width {width} exceeds u32"))?;
    let h = u32::try_from(height).map_err(|_| format!("Height {height} exceeds u32"))?;

    let mut buf = Vec::new();
    let mut encoder = png::Encoder::new(&mut buf, w, h);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    let mut writer = encoder
        .write_header()
        .map_err(|e| format!("PNG header error: {e}"))?;
    writer
        .write_image_data(rgba)
        .map_err(|e| format!("PNG encode error: {e}"))?;
    drop(writer);
    Ok(buf)
}

/// Encodes bytes as standard (RFC 4648) base64 with `=` padding.
pub(in crate::app) fn base64_encode(data: &[u8]) -> String {
    const TABLE: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity((data.len() + 2) / 3 * 4);
    for chunk in data.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = chunk.get(1).copied().unwrap_or(0) as u32;
        let b2 = chunk.get(2).copied().unwrap_or(0) as u32;
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(TABLE[((n >> 18) & 0x3F) as usize] as char);
        out.push(TABLE[((n >> 12) & 0x3F) as usize] as char);
        out.push(if chunk.len() > 1 { TABLE[((n >> 6) & 0x3F) as usize] as char } else { '=' });
        out.push(if chunk.len() > 2 { TABLE[(n & 0x3F) as usize] as char } else { '=' });
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn deleting_a_placeholder_excludes_its_image_without_consuming_the_draft() {
        let staged = vec![
            messages::WireImage { media_type: "image/png".into(), base64: "first".into() },
            messages::WireImage { media_type: "image/png".into(), base64: "second".into() },
        ];
        let outgoing = images_for_draft(&staged, "Explain [Image 2]");
        assert_eq!(outgoing.len(), 1);
        assert_eq!(outgoing[0].base64, "second");
        assert_eq!(staged.len(), 2, "failed sends must leave the original staging intact");
        assert!(images_for_draft(&staged, "").is_empty());
    }

    // ── PNG encoding ────────────────────────────────────────────────────────

    /// Minimal 1x1 red pixel (RGBA8).
    fn red_pixel() -> Vec<u8> {
        vec![255, 0, 0, 255]
    }

    /// 2x2 RGBA image.
    fn two_by_two() -> Vec<u8> {
        vec![
            255, 0, 0, 255, // red
            0, 255, 0, 255, // green
            0, 0, 255, 255, // blue
            255, 255, 0, 255, // yellow
        ]
    }

    #[test]
    fn valid_1x1_encodes_to_png_with_correct_signature() {
        let png = rgba_to_png(1, 1, &red_pixel()).expect("encode");
        // PNG magic number.
        assert!(
            png.starts_with(&[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "expected PNG signature"
        );
    }

    #[test]
    fn valid_2x2_encodes_and_ihdr_carries_dimensions() {
        let png = rgba_to_png(2, 2, &two_by_two()).expect("encode");
        // IHDR width/height are at bytes 16–19 and 20–23 respectively.
        assert!(png.len() > 24, "PNG too short");
        let w = u32::from_be_bytes(png[16..20].try_into().unwrap());
        let h = u32::from_be_bytes(png[20..24].try_into().unwrap());
        assert_eq!(w, 2, "IHDR width");
        assert_eq!(h, 2, "IHDR height");
    }

    #[test]
    fn zero_dimensions_produce_a_codec_error() {
        // The png crate rejects zero-width images. That is fine — there is
        // nothing useful to stage from a 0x0 clipboard capture.
        let err = rgba_to_png(0, 0, &[]).unwrap_err();
        assert!(!err.is_empty(), "expected a non-empty error message");
    }

    #[test]
    fn buffer_length_mismatch_is_rejected() {
        // 2x2 requires 16 bytes; providing 4 should fail.
        let err = rgba_to_png(2, 2, &red_pixel()).unwrap_err();
        assert!(err.contains("mismatch"), "expected mismatch error, got: {err}");
    }

    #[test]
    fn oversized_pixel_count_is_rejected_before_buffer_check() {
        // 4097x4097 = 16,785,409 pixels > MAX_PIXEL_COUNT (16 M).
        // Buffer is empty (wrong length), but the pixel-count cap fires first.
        let err = rgba_to_png(4097, 4097, &[]).unwrap_err();
        assert!(err.contains("large") || err.contains("overflow"), "got: {err}");
    }

    #[test]
    fn exactly_at_pixel_cap_is_accepted_with_correct_buffer() {
        // 4096x4096 = 16,777,216 pixels = MAX_PIXEL_COUNT exactly.
        let w = 4096usize;
        let h = 4096usize;
        let buf = vec![0u8; w * h * 4]; // all-black transparent
        let result = rgba_to_png(w, h, &buf);
        assert!(result.is_ok(), "exactly at cap should be accepted: {:?}", result);
    }

    #[test]
    fn one_over_pixel_cap_is_rejected() {
        let err = rgba_to_png(4097, 4096, &[]).unwrap_err();
        assert!(err.contains("large") || err.contains("overflow"), "got: {err}");
    }

    // ── base64 ──────────────────────────────────────────────────────────────

    #[test]
    fn base64_encodes_rfc_4648_test_vectors() {
        assert_eq!(base64_encode(b""), "");
        assert_eq!(base64_encode(b"f"), "Zg==");
        assert_eq!(base64_encode(b"fo"), "Zm8=");
        assert_eq!(base64_encode(b"foo"), "Zm9v");
        assert_eq!(base64_encode(b"foob"), "Zm9vYg==");
        assert_eq!(base64_encode(b"fooba"), "Zm9vYmE=");
        assert_eq!(base64_encode(b"foobar"), "Zm9vYmFy");
    }

    #[test]
    fn base64_encodes_a_longer_string() {
        assert_eq!(base64_encode(b"Hello, World!"), "SGVsbG8sIFdvcmxkIQ==");
    }

    // ── PNG → base64 round-trip size check ─────────────────────────────────

    #[test]
    fn encoded_png_of_small_image_is_within_5mb_cap() {
        let png = rgba_to_png(1, 1, &red_pixel()).expect("encode");
        assert!(
            png.len() < MAX_IMAGE_BYTES,
            "1x1 PNG unexpectedly large: {} bytes",
            png.len()
        );
    }
}
