//! Turning a link click into an action.
//!
//! A link in the transcript is model-generated content, so acting on one is an
//! action with real consequences — a browser opens, a URL reaches the OS. This
//! module owns the whole gesture on the application side: finding the link
//! under a pointer, opening a validated URL, copying it, and raising the
//! right-click menu. The security boundary — scheme validation and argv-only
//! process spawning — lives one layer down in [`coda_boot::browser`], so this
//! file only routes to it and reports the outcome, and the menu that drives it
//! stays a pure [`Surface`](crate::surface::Surface).

use coda_boot::browser::{self, BrowserLaunchError, LinkOpenMode};

use super::App;
use crate::surface::context_menu::{LinkAction, LinkMenuSurface};
use crate::transcript::NoticeLevel;

impl App {
    /// The hyperlink under a pointer position, if the click landed on one.
    ///
    /// Reuses the existing screen-cell → transcript-row mapping, then asks the
    /// row itself whether that cell is inside a link. Returns an owned URL so
    /// the caller is not tied to the row borrow while it acts on the result.
    /// A click on ordinary, non-link text yields `None`, which is what makes a
    /// plain click there do nothing.
    pub(super) fn link_at_pointer(&self, column: u16, row: u16) -> Option<String> {
        let pos = self.mouse_to_selection(column, row)?;
        self.rows.get(pos.row)?.link_at(pos.col).map(str::to_string)
    }

    /// Opens a validated link, reporting any refusal to the user.
    ///
    /// The URL is never handed to the OS without validation: [`coda_boot`]
    /// checks the scheme and spawns an argv process with no shell. A refusal —
    /// a dangerous scheme, or private mode with no private-capable browser — is
    /// surfaced as a notice rather than swallowed, so the user is never told a
    /// window is private when it is not.
    pub(super) fn open_link(&mut self, url: &str, mode: LinkOpenMode) {
        match browser::open_link(url, mode) {
            Ok(()) => {
                let destination = match mode {
                    LinkOpenMode::Default => "browser",
                    LinkOpenMode::Private => "private window",
                };
                self.hint(format!("Opening link in {destination}."));
            }
            Err(BrowserLaunchError::PrivateModeUnavailable) => self.notice(
                "No browser that supports a private window was found, so the link was \
                 not opened. Open it in a normal window instead.",
                NoticeLevel::Warning,
            ),
            Err(BrowserLaunchError::SchemeNotAllowed(scheme)) => self.notice(
                format!(
                    "Refused to open a {scheme} link — only http, https and mailto are allowed."
                ),
                NoticeLevel::Warning,
            ),
            Err(err) => self.notice(
                format!("Could not open the link: {err}"),
                NoticeLevel::Warning,
            ),
        }
    }

    /// Copies a link's destination to the clipboard.
    ///
    /// Uses the same clipboard path as every other copy in the app rather than
    /// introducing a second mechanism.
    pub(super) fn copy_link(&mut self, url: &str) {
        match arboard::Clipboard::new().and_then(|mut clipboard| clipboard.set_text(url)) {
            Ok(()) => self.hint(format!("Copied link — {} characters.", url.chars().count())),
            Err(err) => self.notice(
                format!("Could not access the clipboard: {err}"),
                NoticeLevel::Warning,
            ),
        }
    }

    /// Opens the right-click context menu for a link.
    ///
    /// The link's capabilities are resolved here, where filesystem access is
    /// allowed, and handed to the surface, which must stay pure. So the menu
    /// can disable an entry it could only have had refused.
    pub(super) fn open_link_menu(&mut self, url: String) {
        let capabilities = browser::link_capabilities(&url);
        self.surfaces.push(Box::new(LinkMenuSurface::new(
            url,
            capabilities.openable,
            capabilities.private,
        )));
        self.dirty = true;
    }

    /// Performs the action a link context menu emitted, closing the menu first.
    pub(super) fn perform_link_action(&mut self, url: String, action: LinkAction) {
        // The menu is done the moment a choice is made; close it before acting
        // so a notice or hint is not drawn underneath a stale overlay.
        self.surfaces.pop();
        match action {
            LinkAction::Copy => self.copy_link(&url),
            LinkAction::Open => self.open_link(&url, LinkOpenMode::Default),
            LinkAction::OpenPrivate => self.open_link(&url, LinkOpenMode::Private),
        }
    }
}
