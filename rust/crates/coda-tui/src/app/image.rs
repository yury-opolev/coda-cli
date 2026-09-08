//! Image staging helpers shared by `/image` and clipboard paste.
//!
//! Collecting these in one place enforces a single 5 MB limit and a single
//! placeholder-token format across every path that stages an image.

use coda_proto::messages;

use crate::render::glyphs;
use super::App;

/// Maximum image-file bytes before base64 encoding, shared with `/image`.
pub(in crate::app) const MAX_IMAGE_BYTES: usize = 5 * 1024 * 1024;

/// Maximum raw pixel count before encoding, to bound memory allocation.
///
/// 16 M pixels x 4 bytes/pixel = 64 MiB raw RGBA — large enough for any
/// screenshot that fits on a modern display, small enough to encode promptly.
const MAX_PIXEL_COUNT: usize = 16 * 1024 * 1024;

/// An image staged for the next user turn.
///
/// Identified by a stable per-attachment `id` rather than by its position in
/// `App::staged_images`: the placeholder token embeds that id, so matching a
/// draft's surviving tokens back to their images never depends on nothing
/// having shifted the list — deleting one placeholder (by editing it out of
/// the draft) excludes exactly that image, never a different one that
/// happened to end up at the same index.
#[derive(Debug, Clone)]
pub(in crate::app) struct StagedImage {
    id: String,
    media_type: String,
    base64: String,
}

impl StagedImage {
    /// The placeholder token this image's marker looks for in the draft,
    /// e.g. `[📷 coda-image-a1b2c3d4e5f6.png]`.
    ///
    /// A camera glyph (never a bare index) and the image's own honest,
    /// MIME-derived extension — never a fixed `.png` regardless of what was
    /// actually staged, which would mislabel a JPEG file as something it is
    /// not.
    pub fn marker(&self) -> String {
        format!(
            "[{} coda-image-{}.{}]",
            glyphs::CAMERA,
            self.id,
            extension_for_media_type(&self.media_type)
        )
    }

    fn to_wire(&self) -> messages::WireImage {
        messages::WireImage {
            media_type: self.media_type.clone(),
            base64: self.base64.clone(),
        }
    }
}

/// Maps a staged image's MIME type to the extension its marker shows.
///
/// Every caller today only ever stages one of the four supported types —
/// clipboard paste always encodes PNG, and `/image` rejects anything else
/// before staging — so the fallback below is unreachable in practice. It
/// exists so an unrecognised type is at least never asserted to be a PNG it
/// is not.
fn extension_for_media_type(media_type: &str) -> &'static str {
    match media_type {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => "img",
    }
}

/// A short, stable, per-attachment id.
///
/// Not cryptographic — it only has to distinguish the handful of images a
/// session might stage at once, and to never change for the life of one
/// staged attachment. Twelve hex digits (48 bits) makes an accidental
/// collision within one draft practically impossible while staying short
/// enough to read as a filename.
fn new_attachment_id() -> String {
    uuid::Uuid::new_v4().simple().to_string()[..12].to_string()
}

/// Resolves which staged images a draft still references, by marker text —
/// never by position — so a placeholder edited out of the draft excludes
/// only its own image.
pub(super) fn images_for_draft(images: &[StagedImage], text: &str) -> Vec<messages::WireImage> {
    let mut ordered: Vec<_> = images
        .iter()
        .filter_map(|image| text.find(&image.marker()).map(|position| (position, image)))
        .collect();
    ordered.sort_by_key(|(position, _)| *position);
    ordered.into_iter().map(|(_, image)| image.to_wire()).collect()
}

impl App {
    /// Stages encoded image bytes as an attachment and inserts its
    /// placeholder token into the composer draft.
    ///
    /// Returns the token itself (not just a label), so a caller can quote the
    /// exact text that now identifies this attachment in a confirmation
    /// notice, without reconstructing it and risking it drift out of sync.
    pub(in crate::app) fn stage_image_bytes(&mut self, media_type: &str, bytes: &[u8]) -> String {
        let staged = StagedImage {
            id: new_attachment_id(),
            media_type: media_type.to_string(),
            base64: base64_encode(bytes),
        };
        let token = staged.marker();
        self.staged_images.push(staged);
        if !self.composer.is_empty() {
            self.composer.insert(" ");
        }
        self.composer.insert(&token);
        token
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

    fn staged(id: &str, media_type: &str, base64: &str) -> StagedImage {
        StagedImage {
            id: id.to_string(),
            media_type: media_type.to_string(),
            base64: base64.to_string(),
        }
    }

    // -- Marker format --------------------------------------------------

    #[test]
    fn the_marker_uses_the_camera_glyph_a_stable_id_and_an_honest_extension() {
        let image = staged("a1b2c3d4e5f6", "image/jpeg", "payload");
        assert_eq!(
            image.marker(),
            format!("[{} coda-image-a1b2c3d4e5f6.jpg]", glyphs::CAMERA)
        );
    }

    #[test]
    fn every_supported_media_type_gets_its_own_honest_extension() {
        assert_eq!(extension_for_media_type("image/png"), "png");
        assert_eq!(extension_for_media_type("image/jpeg"), "jpg");
        assert_eq!(extension_for_media_type("image/gif"), "gif");
        assert_eq!(extension_for_media_type("image/webp"), "webp");
    }

    #[test]
    fn a_png_clipboard_screenshot_is_never_labelled_as_a_different_format() {
        let clipboard = staged("id1", "image/png", "png-bytes");
        assert!(clipboard.marker().ends_with(".png]"), "{}", clipboard.marker());
    }

    #[test]
    fn a_non_png_file_keeps_its_own_extension_not_a_relabelled_png() {
        let jpeg = staged("id2", "image/jpeg", "jpeg-bytes");
        assert!(jpeg.marker().ends_with(".jpg]"), "{}", jpeg.marker());
        assert!(!jpeg.marker().contains(".png"), "a JPEG must never be relabelled as a PNG");
    }

    #[test]
    fn an_unrecognised_media_type_never_lies_that_it_is_a_png() {
        assert_ne!(extension_for_media_type("image/bmp"), "png");
    }

    // -- Stable, distinct ids --------------------------------------------

    #[test]
    fn two_staged_attachments_get_two_distinct_stable_ids() {
        let a = new_attachment_id();
        let b = new_attachment_id();
        assert_ne!(a, b, "each staged attachment needs its own unique id");
        assert_eq!(a.len(), 12);
        assert_eq!(b.len(), 12);
    }

    #[test]
    fn an_images_id_never_changes_across_repeated_marker_calls() {
        let image = staged("stable-id", "image/png", "payload");
        assert_eq!(image.marker(), image.marker(), "the marker must be stable, not regenerated");
    }

    // -- Identity by marker, not by position ------------------------------

    #[test]
    fn reordered_markers_preserve_image_associations_after_deletion() {
        let images = vec![
            staged("id-first", "image/png", "first-payload"),
            staged("id-second", "image/jpeg", "second-payload"),
            staged("id-third", "image/gif", "third-payload"),
        ];
        // The middle placeholder was edited out of the draft, and the
        // surviving two are quoted out of order. An index-based scheme would
        // misattribute at least one of these; marker-based lookup cannot.
        let draft = format!(
            "here is the third one {} then the first one {}",
            images[2].marker(),
            images[0].marker()
        );
        let outgoing = images_for_draft(&images, &draft);
        let payloads: Vec<&str> = outgoing.iter().map(|w| w.base64.as_str()).collect();
        // WireImage has no filename: its order must match the visible labels.
        assert_eq!(payloads, vec!["third-payload", "first-payload"]);
        assert_eq!(images.len(), 3, "the original staging list must be untouched");
    }

    #[test]
    fn repeated_markers_preserve_one_attachment_per_image() {
        let images = vec![staged("id-1", "image/png", "payload")];
        let marker = images[0].marker();
        let outgoing = images_for_draft(&images, &format!("{marker} and again {marker}"));
        assert_eq!(outgoing.len(), 1);
        assert_eq!(outgoing[0].base64, "payload");
    }

    #[test]
    fn deleting_a_placeholder_excludes_its_image_without_consuming_the_draft() {
        let images = vec![
            staged("id-1", "image/png", "first"),
            staged("id-2", "image/png", "second"),
        ];
        let outgoing = images_for_draft(&images, &format!("Explain {}", images[1].marker()));
        assert_eq!(outgoing.len(), 1);
        assert_eq!(outgoing[0].base64, "second");
        assert_eq!(images.len(), 2, "failed sends must leave the original staging intact");
        assert!(images_for_draft(&images, "").is_empty());
    }

    // -- Failed-send retry -------------------------------------------------

    #[test]
    fn a_failed_send_can_retry_because_the_marker_and_payload_are_preserved() {
        let images = vec![staged("id-1", "image/png", "payload-1")];
        let draft = format!("send this {}", images[0].marker());

        // First attempt "fails" (the caller restores the draft verbatim and
        // never touches staging on failure — see `App::submit`).
        let first_attempt = images_for_draft(&images, &draft);
        assert_eq!(first_attempt.len(), 1);
        assert_eq!(first_attempt[0].base64, "payload-1");

        // Retried with the exact same draft text: the same marker must still
        // resolve to the very same staged image.
        let retry = images_for_draft(&images, &draft);
        assert_eq!(retry.len(), 1);
        assert_eq!(retry[0].base64, "payload-1");
        assert_eq!(retry[0].media_type, "image/png");
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
