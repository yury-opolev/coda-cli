//! Transcript viewport: what slice of the rendered rows is on screen.
//!
//! The viewport follows the newest content by default, but stops following as
//! soon as the user scrolls back — otherwise arriving output would yank them
//! away from what they were reading. Rows that arrive while detached are
//! counted so the UI can offer a "jump to bottom" hint.

/// A stable anchor into the transcript used to preserve scroll position during
/// a reflow (resize) or streaming growth.
///
/// Ported from `TranscriptViewportAnchor` in C#.  Stores the index of the
/// transcript block whose first row was at (or just above) the viewport top,
/// plus how many wrapped rows into that block the viewport started.  On reflow
/// the block start is looked up in the new layout and the row offset is added
/// to give the new absolute viewport position.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ViewportAnchor {
    /// Index of the transcript block containing the top viewport row.
    pub block_index: usize,
    /// Rows into that block where the viewport started.
    pub row_within_block: usize,
}

/// How the viewport tracks new content.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Follow {
    /// Pinned to the newest row.
    Bottom,
    /// The user has scrolled; the offset is held steady.
    Detached,
}

/// Scroll state over a list of rendered rows.
#[derive(Debug, Clone)]
pub struct Viewport {
    /// Index of the first visible row.
    offset: usize,
    /// Visible height in rows.
    height: usize,
    /// Total rows currently available.
    total: usize,
    follow: Follow,
    /// Rows appended since the user detached.
    unread: usize,
}

impl Default for Viewport {
    fn default() -> Self {
        Self::new()
    }
}

impl Viewport {
    pub fn new() -> Self {
        Self {
            offset: 0,
            height: 0,
            total: 0,
            follow: Follow::Bottom,
            unread: 0,
        }
    }

    pub fn offset(&self) -> usize {
        self.offset
    }

    pub fn height(&self) -> usize {
        self.height
    }

    pub fn total(&self) -> usize {
        self.total
    }

    pub fn follow(&self) -> Follow {
        self.follow
    }

    pub fn is_following(&self) -> bool {
        self.follow == Follow::Bottom
    }

    /// Rows that arrived while the user was scrolled back.
    pub fn unread(&self) -> usize {
        self.unread
    }

    /// Largest valid offset: the position that shows the final row at the
    /// bottom of the viewport.
    pub fn max_offset(&self) -> usize {
        self.total.saturating_sub(self.height)
    }

    /// The visible row range, as a half-open interval.
    pub fn visible_range(&self) -> std::ops::Range<usize> {
        let start = self.offset.min(self.total);
        let end = (start + self.height).min(self.total);
        start..end
    }

    /// Whether the content is taller than the viewport.
    pub fn is_scrollable(&self) -> bool {
        self.total > self.height
    }

    /// Updates the row count and viewport height.
    ///
    /// While following, the offset is recomputed to stay pinned to the bottom;
    /// while detached, it is clamped but otherwise preserved so the user keeps
    /// their place as content grows.
    pub fn update(&mut self, total: usize, height: usize) {
        let grew_by = total.saturating_sub(self.total);
        self.total = total;
        self.height = height;

        match self.follow {
            Follow::Bottom => {
                self.offset = self.max_offset();
                self.unread = 0;
            }
            Follow::Detached => {
                self.unread += grew_by;
                self.offset = self.offset.min(self.max_offset());
                // Content shrank back to fit: nothing to scroll, so re-attach.
                if !self.is_scrollable() {
                    self.attach();
                }
            }
        }
    }

    /// Pins the viewport to the newest row.
    pub fn attach(&mut self) {
        self.follow = Follow::Bottom;
        self.offset = self.max_offset();
        self.unread = 0;
    }

    /// Detaches so the offset stops tracking new content.
    fn detach(&mut self) {
        if self.follow == Follow::Bottom {
            self.follow = Follow::Detached;
            self.unread = 0;
        }
    }

    /// Updates total and height for a detached viewport, placing it at
    /// `anchor_row` instead of clamping the old offset.  Used after a reflow
    /// (resize or streaming growth) when the caller has resolved a stable
    /// block-level anchor to the new global row.
    ///
    /// Equivalent to `ApplyContentLayout` with a resolved anchor in C#.
    pub fn update_with_anchor(&mut self, total: usize, height: usize, anchor_row: usize) {
        let grew_by = total.saturating_sub(self.total);
        self.total = total;
        self.height = height;
        match self.follow {
            Follow::Bottom => {
                self.offset = self.max_offset();
                self.unread = 0;
            }
            Follow::Detached => {
                // Counted here as well as in `update`. This is the path that
                // actually runs while the agent works — every content event
                // reflows — so leaving it out meant the unread count the
                // status line reads was permanently zero, and a user who had
                // scrolled away was never told anything had arrived.
                self.unread += grew_by;
                self.offset = anchor_row.min(self.max_offset());
                if !self.is_scrollable() {
                    self.attach();
                }
            }
        }
    }

    /// Forces the offset to `row` (clamped), without changing the follow mode.
    ///
    /// Used when restoring a detached anchor after a reflow.
    pub fn set_offset_clamped(&mut self, row: usize) {
        self.offset = row.min(self.max_offset());
    }

    /// Scrolls up by `rows`, detaching from the bottom.
    pub fn scroll_up(&mut self, rows: usize) {
        if !self.is_scrollable() {
            return;
        }
        self.detach();
        self.offset = self.offset.saturating_sub(rows);
    }

    /// Scrolls down by `rows`, re-attaching if it reaches the bottom.
    pub fn scroll_down(&mut self, rows: usize) {
        if !self.is_scrollable() {
            return;
        }
        let target = (self.offset + rows).min(self.max_offset());
        self.offset = target;
        if self.offset >= self.max_offset() {
            self.attach();
        }
    }

    pub fn page_up(&mut self) {
        self.scroll_up(self.page_size());
    }

    pub fn page_down(&mut self) {
        self.scroll_down(self.page_size());
    }

    /// Jumps to the first row.
    pub fn scroll_to_top(&mut self) {
        if !self.is_scrollable() {
            return;
        }
        self.detach();
        self.offset = 0;
    }

    /// Jumps to the newest row and resumes following.
    pub fn scroll_to_bottom(&mut self) {
        self.attach();
    }

    /// A page leaves one row of overlap so the reader keeps context.
    fn page_size(&self) -> usize {
        self.height.saturating_sub(1).max(1)
    }

    /// Moves the viewport to `row`, detaching unless it lands at the bottom.
    ///
    /// Re-attaching at the end is what makes a drag to the foot of the track
    /// resume following: parking the offset there while still detached would
    /// leave the thumb pinned at the bottom but the transcript frozen, which
    /// reads as the UI having stopped.
    pub fn scroll_to_row(&mut self, row: usize) {
        if !self.is_scrollable() {
            return;
        }
        let target = row.min(self.max_offset());
        if target >= self.max_offset() {
            self.attach();
        } else {
            self.detach();
            self.offset = target;
        }
    }

    /// Scrolls so the thumb's top row sits at `thumb_top` in a track of
    /// `track` rows — the inverse of [`Self::thumb`], used to drag the thumb.
    ///
    /// `thumb_top` is clamped to the track, so a drag that runs past either
    /// end parks there rather than being ignored: the pointer routinely leaves
    /// the one-cell scrollbar column mid-gesture, and a drag that stopped
    /// responding the moment it did would be unusable.
    pub fn scroll_to_thumb(&mut self, track: usize, thumb_top: usize) {
        let Some((_, size)) = self.thumb(track) else {
            return;
        };
        let span = track.saturating_sub(size);
        let max = self.max_offset();
        let target = if span == 0 {
            max
        } else {
            let progress = thumb_top.min(span) as f64 / span as f64;
            (max as f64 * progress).round() as usize
        };
        self.scroll_to_row(target);
    }

    /// Position of the scrollbar thumb as a fraction, for rendering.
    ///
    /// Returns `None` when everything fits and no scrollbar is needed.
    pub fn thumb(&self, track: usize) -> Option<(usize, usize)> {
        if !self.is_scrollable() || track == 0 {
            return None;
        }

        let visible_ratio = self.height as f64 / self.total as f64;
        let size = ((track as f64 * visible_ratio).round() as usize).clamp(1, track);

        let scrollable = self.max_offset();
        let progress = if scrollable == 0 {
            0.0
        } else {
            self.offset as f64 / scrollable as f64
        };
        let position = ((track - size) as f64 * progress).round() as usize;

        Some((position.min(track - size), size))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn viewport(total: usize, height: usize) -> Viewport {
        let mut viewport = Viewport::new();
        viewport.update(total, height);
        viewport
    }

    #[test]
    fn starts_following_the_bottom() {
        let viewport = Viewport::new();
        assert!(viewport.is_following());
        assert_eq!(viewport.offset(), 0);
    }

    #[test]
    fn following_pins_the_offset_to_the_last_page() {
        let viewport = viewport(100, 10);
        assert_eq!(viewport.offset(), 90);
        assert_eq!(viewport.visible_range(), 90..100);
    }

    #[test]
    fn content_shorter_than_the_viewport_sits_at_the_top() {
        let viewport = viewport(3, 10);
        assert_eq!(viewport.offset(), 0);
        assert_eq!(viewport.visible_range(), 0..3);
        assert!(!viewport.is_scrollable());
    }

    #[test]
    fn scrolling_up_detaches_from_the_bottom() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(5);

        assert!(!viewport.is_following());
        assert_eq!(viewport.offset(), 85);
    }

    #[test]
    fn scrolling_up_stops_at_the_first_row() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(1000);
        assert_eq!(viewport.offset(), 0);
    }

    #[test]
    fn scrolling_back_to_the_bottom_reattaches() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(5);
        viewport.scroll_down(5);

        assert!(viewport.is_following());
        assert_eq!(viewport.offset(), 90);
    }

    #[test]
    fn scrolling_down_never_passes_the_last_row() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(50);
        viewport.scroll_down(1000);
        assert_eq!(viewport.offset(), 90);
    }

    #[test]
    fn a_detached_viewport_holds_its_position_as_content_arrives() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(20);
        let offset = viewport.offset();

        viewport.update(140, 10);

        assert_eq!(viewport.offset(), offset, "the reader was moved");
        assert!(!viewport.is_following());
    }

    #[test]
    fn a_following_viewport_tracks_arriving_content() {
        let mut viewport = viewport(100, 10);
        viewport.update(140, 10);
        assert_eq!(viewport.offset(), 130);
    }

    #[test]
    fn counts_rows_that_arrive_while_detached() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(20);
        assert_eq!(viewport.unread(), 0);

        viewport.update(115, 10);
        assert_eq!(viewport.unread(), 15);

        viewport.update(120, 10);
        assert_eq!(viewport.unread(), 20);
    }

    #[test]
    fn returning_to_the_bottom_clears_the_unread_count() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(20);
        viewport.update(150, 10);
        assert!(viewport.unread() > 0);

        viewport.scroll_to_bottom();
        assert_eq!(viewport.unread(), 0);
        assert!(viewport.is_following());
    }

    #[test]
    fn a_following_viewport_never_accumulates_unread_rows() {
        let mut viewport = viewport(100, 10);
        viewport.update(200, 10);
        assert_eq!(viewport.unread(), 0);
    }

    #[test]
    fn paging_leaves_one_row_of_overlap() {
        let mut viewport = viewport(100, 10);
        viewport.page_up();
        assert_eq!(viewport.offset(), 81);
    }

    #[test]
    fn paging_works_in_a_one_row_viewport() {
        let mut viewport = viewport(100, 1);
        viewport.page_up();
        assert_eq!(viewport.offset(), 98);
    }

    #[test]
    fn jumps_to_the_top_and_the_bottom() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_to_top();
        assert_eq!(viewport.offset(), 0);
        assert!(!viewport.is_following());

        viewport.scroll_to_bottom();
        assert_eq!(viewport.offset(), 90);
        assert!(viewport.is_following());
    }

    #[test]
    fn scrolling_does_nothing_when_everything_fits() {
        let mut viewport = viewport(5, 10);
        viewport.scroll_up(3);
        assert_eq!(viewport.offset(), 0);
        assert!(viewport.is_following(), "should not detach with nothing to scroll");
    }

    #[test]
    fn shrinking_content_reattaches_a_detached_viewport() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(50);
        assert!(!viewport.is_following());

        // e.g. the transcript was cleared.
        viewport.update(4, 10);
        assert!(viewport.is_following());
        assert_eq!(viewport.offset(), 0);
    }

    #[test]
    fn resizing_taller_keeps_the_offset_valid() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(50);
        viewport.update(100, 60);

        assert!(viewport.offset() <= viewport.max_offset());
        assert_eq!(viewport.visible_range().end, viewport.offset() + 60);
    }

    #[test]
    fn the_visible_range_never_exceeds_the_total() {
        let viewport = viewport(3, 10);
        assert_eq!(viewport.visible_range(), 0..3);
    }

    #[test]
    fn an_empty_transcript_has_an_empty_visible_range() {
        let viewport = viewport(0, 10);
        assert_eq!(viewport.visible_range(), 0..0);
        assert!(viewport.thumb(10).is_none());
    }

    #[test]
    fn no_scrollbar_when_everything_fits() {
        assert!(viewport(5, 10).thumb(10).is_none());
    }

    #[test]
    fn the_scrollbar_thumb_sits_at_the_bottom_when_following() {
        let viewport = viewport(100, 10);
        let (position, size) = viewport.thumb(10).expect("a thumb");
        assert_eq!(position + size, 10);
    }

    #[test]
    fn the_scrollbar_thumb_sits_at_the_top_when_scrolled_to_the_start() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_to_top();
        let (position, _) = viewport.thumb(10).expect("a thumb");
        assert_eq!(position, 0);
    }

    #[test]
    fn the_scrollbar_thumb_is_proportional_but_never_vanishes() {
        let viewport = viewport(10_000, 10);
        let (_, size) = viewport.thumb(20).expect("a thumb");
        assert_eq!(size, 1, "a tiny thumb must still be visible");
    }

    #[test]
    fn the_scrollbar_thumb_always_fits_its_track() {
        for total in [11usize, 50, 500, 5000] {
            for offset_steps in [0usize, 1, 7, 100] {
                let mut viewport = viewport(total, 10);
                viewport.scroll_up(offset_steps);
                let track = 10;
                let (position, size) = viewport.thumb(track).expect("a thumb");
                assert!(
                    position + size <= track,
                    "thumb {position}+{size} overflows {track} (total {total})"
                );
            }
        }
    }

    #[test]
    fn dragging_the_thumb_to_the_top_of_its_track_scrolls_to_the_start() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_to_thumb(10, 0);
        assert_eq!(viewport.offset(), 0);
        assert!(!viewport.is_following(), "the top is not the bottom");
    }

    #[test]
    fn dragging_the_thumb_to_the_foot_of_its_track_resumes_following() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_to_top();
        viewport.scroll_to_thumb(10, 10);
        assert_eq!(viewport.offset(), viewport.max_offset());
        assert!(viewport.is_following(), "the bottom re-attaches");
    }

    #[test]
    fn dragging_the_thumb_lands_where_it_was_dropped() {
        // The thumb is its own size, so a track of 10 showing 10 of 100 rows
        // has a one-row thumb and nine rows of travel over 90 scrollable rows.
        let mut viewport = viewport(100, 10);
        viewport.scroll_to_thumb(10, 3);
        assert_eq!(viewport.offset(), 30);
        let (position, _) = viewport.thumb(10).expect("a thumb");
        assert_eq!(position, 3, "the thumb is drawn back where it was dropped");
    }

    #[test]
    fn dragging_past_the_end_of_the_track_parks_at_the_end() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_to_top();
        viewport.scroll_to_thumb(10, 999);
        assert_eq!(viewport.offset(), viewport.max_offset());
    }

    #[test]
    fn dragging_a_thumb_that_fills_its_track_does_nothing() {
        let mut viewport = viewport(5, 10);
        viewport.scroll_to_thumb(10, 4);
        assert_eq!(viewport.offset(), 0, "nothing to scroll");
        assert!(viewport.is_following());
    }

    #[test]
    fn every_track_row_maps_onto_a_valid_offset() {
        for total in [11usize, 50, 500, 5000] {
            let track = 10;
            for row in 0..track {
                let mut viewport = viewport(total, 10);
                viewport.scroll_to_thumb(track, row);
                assert!(
                    viewport.offset() <= viewport.max_offset(),
                    "row {row} of {total} left offset {} past {}",
                    viewport.offset(),
                    viewport.max_offset(),
                );
            }
        }
    }

    // --- anchor / reflow tests ---

    #[test]
    fn update_with_anchor_restores_detached_position_on_reflow() {
        let mut viewport = viewport(100, 10);
        // Scroll to row 30 (detached)
        viewport.scroll_up(60);
        assert_eq!(viewport.offset(), 30);
        assert!(!viewport.is_following());

        // Reflow: content grew to 150 rows (wider → more wrapping reduced, or narrower → more).
        // Anchor says the old row 30 is now row 40.
        viewport.update_with_anchor(150, 10, 40);

        assert_eq!(viewport.offset(), 40);
        assert!(!viewport.is_following());
    }

    #[test]
    fn update_with_anchor_follows_bottom_when_not_detached() {
        let mut viewport = viewport(100, 10);
        assert!(viewport.is_following());

        viewport.update_with_anchor(150, 10, 40);

        // Following mode: bottom pinned, anchor ignored
        assert_eq!(viewport.offset(), 140);
        assert!(viewport.is_following());
    }

    #[test]
    fn update_with_anchor_reattaches_when_content_fits() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(50);
        assert!(!viewport.is_following());

        // Content shrank to 5 rows — nothing to scroll
        viewport.update_with_anchor(5, 10, 0);

        assert!(viewport.is_following());
    }

    #[test]
    fn set_offset_clamped_does_not_exceed_max() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(50);
        // max_offset = 90
        viewport.set_offset_clamped(999);
        assert_eq!(viewport.offset(), 90);
        assert!(!viewport.is_following());
    }

    #[test]
    fn set_offset_clamped_preserves_detached_mode() {
        let mut viewport = viewport(100, 10);
        viewport.scroll_up(50);
        viewport.set_offset_clamped(20);
        assert_eq!(viewport.offset(), 20);
        assert!(!viewport.is_following());
    }

    #[test]
    fn a_detached_viewport_counts_arrivals_through_the_anchored_path_too() {
        // Every content event reflows, so `update_with_anchor` is the path
        // that actually runs while the agent is working -- and it never
        // touched `unread`. The count the status bar reads was therefore
        // always zero, so a user who had scrolled away was never told
        // anything had arrived.
        let mut viewport = Viewport::new();
        viewport.update(100, 10);
        viewport.scroll_up(20);
        assert!(!viewport.is_following(), "scrolling up must detach");

        let anchor = viewport.offset();
        viewport.update_with_anchor(130, 10, anchor);
        assert_eq!(viewport.unread(), 30, "arrivals went uncounted");

        viewport.update_with_anchor(140, 10, anchor);
        assert_eq!(viewport.unread(), 40, "arrivals stopped accumulating");
    }

    #[test]
    fn returning_to_the_bottom_clears_an_anchored_count() {
        let mut viewport = Viewport::new();
        viewport.update(100, 10);
        viewport.scroll_up(20);
        let anchor = viewport.offset();
        viewport.update_with_anchor(150, 10, anchor);
        assert!(viewport.unread() > 0);

        viewport.scroll_to_bottom();
        assert_eq!(viewport.unread(), 0, "the count survived going back to the bottom");
        assert!(viewport.is_following());
    }

    #[test]
    fn content_arriving_does_not_move_a_detached_viewport() {
        // The whole point: a user who scrolled up did so for a reason.
        let mut viewport = Viewport::new();
        viewport.update(100, 10);
        viewport.scroll_up(20);
        let settled = viewport.offset();

        for total in [120, 140, 160] {
            viewport.update_with_anchor(total, 10, settled);
            assert_eq!(
                viewport.offset(),
                settled,
                "the viewport moved when content arrived"
            );
        }
    }
}