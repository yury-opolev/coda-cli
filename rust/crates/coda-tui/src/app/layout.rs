//! Settled frame geometry shared by transcript reflow, drawing and hit-testing.

use ratatui::layout::Rect;

use super::App;
use crate::render::draw;
use crate::transcript::TranscriptStyle;

impl App {
    pub(super) fn layout_frame(&mut self, area: Rect) -> draw::Regions {
        let composer_lines = self
            .composer
            .visual_line_count(draw::composer_text_width(area.width));
        let pending = self.state.queued.len() + self.state.unsent.len();
        let full_regions =
            draw::layout_with_pending(area, composer_lines, false, self.state.is_busy(), pending);
        let full_width = full_regions.transcript.width as usize;
        let invalid = self.laid_out_width == 0 || self.frame_width != full_width;
        let style = TranscriptStyle::Cards;
        let full_render = invalid.then(|| {
            self.state.transcript.render_with_block_starts_styled(
                full_width,
                self.state.display_mode,
                style,
            )
        });
        if let Some((rows, _)) = &full_render {
            self.full_width_row_count = rows.len();
            self.frame_width = full_width;
        }
        // Narrowing can switch tables to stacked cells and make them fit.
        // Decide from full-width content so the scrollbar cannot oscillate.
        let regions = if self.full_width_row_count > full_regions.transcript.height as usize {
            draw::layout_with_pending(area, composer_lines, true, self.state.is_busy(), pending)
        } else {
            full_regions
        };
        let width = regions.transcript.width as usize;
        let height = regions.transcript.height as usize;

        if invalid || width != self.laid_out_width {
            let was_following = self.viewport.is_following();
            let (rows, starts) = full_render
                .filter(|_| width == full_width)
                .unwrap_or_else(|| {
                    self.state.transcript.render_with_block_starts_styled(
                        width,
                        self.state.display_mode,
                        style,
                    )
                });
            self.rows = rows;
            self.block_starts = starts;
            self.laid_out_width = width;

            if was_following {
                self.viewport.update(self.rows.len(), height);
            } else {
                let anchor_row = self.detached_anchor.and_then(|anchor| {
                    self.block_starts
                        .get(anchor.block_index)
                        .map(|&start| start + anchor.row_within_block)
                });
                self.viewport
                    .update_with_anchor(self.rows.len(), height, anchor_row.unwrap_or(0));
            }
        } else {
            self.viewport.update(self.rows.len(), height);
        }
        regions
    }
}
