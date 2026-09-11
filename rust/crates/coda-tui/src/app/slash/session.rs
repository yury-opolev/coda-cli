//! Session commands: resume, fork, rewind, compact, export, diff, image.
//!
//! Grouped because they all act on the conversation itself rather than on
//! configuration or on installed components.

use coda_proto::messages::{self, method};
use serde_json::Value;

use super::super::App;
use crate::surface::browser::BrowserKind;
use crate::commands;
use crate::state::UiEvent;
use crate::transcript::NoticeLevel;

impl App {
    /// `/resume [<id>]` — list or resume a past session.
    ///
    /// No arg → open the sessions browser picker (mirrors C#
    /// `ResumeCommand.HandleNoArgsAsync`). A positive integer N → use the
    /// N-th newest session (1-based). Any other string → treat as a literal
    /// session id.
    pub(super) async fn cmd_resume(&mut self, invocation: &commands::Invocation) {
        // Asked here as well as in `resume_to_session`: opening the picker
        // reads the engine, and choosing a row from it starts a child. During
        // a sign-out neither may happen, and refusing before the browser opens
        // says so where the user typed it.
        if !self.engine_start_allowed("Resuming a session") {
            return;
        }
        let arg = invocation.first().map(str::to_string);

        if let Some(arg) = arg {
            let session_id = self.resolve_resume_target(&arg).await;
            self.resume_to_session(session_id).await;
        } else {
            self.open_browser(BrowserKind::Sessions).await;
        }
    }

    /// Resolves a `/resume` argument to a session id.
    ///
    /// A bare positive integer selects the N-th newest session (1-based); any
    /// other string is returned as-is. The list comes from
    /// `session/listSessions`, so numbering matches what the picker shows and
    /// no transcript directory is read here.
    pub(super) async fn resolve_resume_target(&self, arg: &str) -> String {
        if let Ok(n) = arg.parse::<usize>() {
            // Gated and bounded like every other read: resolving "the third
            // newest" against an engine that is gone, or one that has stopped
            // answering, must fall back to treating the argument literally
            // rather than waiting in the event loop's own arm.
            if n >= 1 && self.engine_connected() {
                if let Ok(result) =
                    self.bounded(crate::api::list_sessions(&self.connection, None)).await
                {
                    if n <= result.sessions.len() {
                        return result.sessions[n - 1].session_id.clone();
                    }
                }
            }
        }
        arg.to_string()
    }

    /// Restarts the engine loading `session_id`, replacing the current
    /// transcript with the engine's own history for it.
    ///
    /// The engine validates the id during `initialize` and answers with a
    /// typed "no such session", so there is no pre-check against a directory
    /// this client should not be reading — and nothing is thrown away until
    /// that answer is in. Clearing first meant a bad id, or an engine that
    /// would not spawn, wiped a perfectly healthy conversation and left the
    /// user with an error message and an empty screen.
    pub(in crate::app) async fn resume_to_session(&mut self, session_id: String) {
        // Before the spawn, not after it: a sign-out's transaction is
        // uncancellable and runs on its own task, so a child started while it
        // is in flight is a child holding a credential that is about to be
        // deleted — and one nobody asked for.
        if !self.engine_start_allowed("Resuming a session") {
            return;
        }
        let intent = coda_boot::SessionIntent::Resume(session_id.clone());
        let booted = match crate::api::boot::boot(self.engine_command.clone(), &intent, "coda-tui")
            .await
        {
            Ok(booted) => booted,
            Err(error) => {
                return self.notice(format!("{error}"), NoticeLevel::Error);
            }
        };

        // Asked again on the far side of the await. Nothing on the loop can
        // start a sign-out while this one is running today, but "the boot is
        // inline" is not a property this function should have to rely on: a
        // replacement adopted after a transaction began would be a live engine
        // on a credential that is being removed.
        if self.auth.is_active() {
            let grace = self.shutdown_grace;
            tokio::spawn(async move {
                let _ = booted.engine.shutdown(grace).await;
            });
            return self.notice(
                "A sign-in or sign-out started while the session was being resumed, so the \
                 engine that had started was stopped and nothing was changed.",
                NoticeLevel::Warning,
            );
        }

        // Committed: the new engine is up, so the conversation on screen is
        // now the wrong one. `adopt_engine` re-reads the replacement's own
        // history over the top of the cleared transcript.
        self.close_browser();
        self.apply(UiEvent::Cleared);
        self.adopt_engine(booted);
        let escaped = coda_render::text::sanitize(&session_id);
        self.notice(format!("Resumed session {escaped}."), NoticeLevel::Info);
        // The resumed session can be on a different provider or model than
        // the one just left; `adopt_engine` already dropped the previous
        // engine's cached names (see `UiEvent::EngineAdopted`), and this is
        // what re-earns them from the process actually running now, exactly
        // as a fresh sign-in does.
        self.load_models().await;
    }

    /// `/fork` — branch the live conversation into a new session.
    ///
    /// Calls `session/fork`; the engine persists the current history under a
    /// fresh id and switches to it.  Mirrors C# `ForkCommand`.
    ///
    /// Gated and bounded like every other engine command: forking a session
    /// there is no engine for cannot mean anything, and awaiting an unbounded
    /// answer would do it in the event loop's own select arm.
    pub(super) async fn cmd_fork(&mut self) {
        if !self.require_engine("/fork") {
            return;
        }
        match self.ask::<Value>("session/fork", Some(serde_json::json!({}))).await {
            Ok(value) => {
                if let Some(new_id) = value.get("newSessionId").and_then(Value::as_str) {
                    let new_id = new_id.to_string();
                    let escaped = coda_render::text::sanitize(&new_id);
                    // Reflect the engine's new session id in the TUI state.
                    self.state.session_id = Some(new_id);
                    self.header_id_selected = false;
                    self.selection.clear();
                    self.dragging = false;
                    self.notice(
                        format!("Forked into a new session {escaped} (original frozen)."),
                        NoticeLevel::Info,
                    );
                } else {
                    self.notice("Fork completed (session ID unknown).", NoticeLevel::Info);
                }
            }
            Err(e) => self.notice(format!("Fork failed: {e}"), NoticeLevel::Error),
        }
    }

    /// `/rewind [<n>]` — remove the last N user exchanges from the conversation.
    ///
    /// Default n = 1.  Mirrors C# `RewindCommand`: validates n, calls
    /// `session/rewind`, then reports how many exchanges were removed.
    pub(super) async fn cmd_rewind(&mut self, invocation: &commands::Invocation) {
        let n = match parse_rewind_n(invocation.first()) {
            Ok(n) => n,
            Err(msg) => {
                self.notice(msg, NoticeLevel::Warning);
                return;
            }
        };
        if !self.require_engine("/rewind") {
            return;
        }

        match self.ask::<Value>("session/rewind", Some(serde_json::json!({ "n": n }))).await {
            Ok(value) => {
                let removed =
                    value.get("removed").and_then(Value::as_u64).unwrap_or(0) as usize;
                let remaining =
                    value.get("remaining").and_then(Value::as_u64).unwrap_or(0) as usize;
                if removed == 0 {
                    self.notice("Nothing to rewind.", NoticeLevel::Info);
                } else {
                    self.notice(
                        format!(
                            "Rewound {removed} exchange(s). {remaining} message(s) remain."
                        ),
                        NoticeLevel::Info,
                    );
                }
            }
            Err(e) => self.notice(format!("Rewind failed: {e}"), NoticeLevel::Error),
        }
    }

    /// `/compact` — ask the engine to summarise the conversation.
    ///
    /// Mirrors C# `CompactCommand`: empty history → "Nothing to compact yet.";
    /// success → "Conversation compacted (N messages kept).";
    /// summariser error → warning with detail.
    pub(super) async fn cmd_compact(&mut self) {
        let result = self
            .fetch::<messages::CompactResult>(
                method::COMPACT,
                Some(serde_json::json!({})),
            )
            .await;

        match result {
            Ok(r) if r.messages_before == 0 => {
                self.notice("Nothing to compact yet.", NoticeLevel::Info);
            }
            Ok(r) if r.error.is_some() => {
                let detail = r.error.unwrap_or_default();
                self.notice(
                    format!("Compaction warning: {detail}"),
                    NoticeLevel::Warning,
                );
            }
            Ok(r) => {
                self.notice(
                    format!(
                        "Conversation compacted ({} messages kept).",
                        r.messages_after
                    ),
                    NoticeLevel::Info,
                );
            }
            Err(e) => self.notice(format!("Compaction failed: {e}"), NoticeLevel::Error),
        }
    }

    /// `/export [<path>]` — write the current conversation to a Markdown file.
    pub(super) async fn cmd_export(&mut self, invocation: &commands::Invocation) {
        let arg = invocation.first().map(str::to_string);
        let project_root = self.paths.project_root.clone();

        // Capture the transcript before spawning; the transcript is !Send.
        let markdown = build_markdown_export(self.state.transcript.blocks());

        if markdown.trim().is_empty() {
            self.notice("Nothing to export yet.", NoticeLevel::Info);
            return;
        }

        let result = tokio::task::spawn_blocking(move || -> std::io::Result<std::path::PathBuf> {
            let path = match arg {
                Some(ref provided) if !provided.is_empty() => {
                    let p = std::path::Path::new(provided);
                    if p.is_absolute() {
                        p.to_path_buf()
                    } else {
                        project_root.join(p)
                    }
                }
                _ => {
                    use time::OffsetDateTime;
                    let now = OffsetDateTime::now_utc();
                    let ts = format!(
                        "{}{:02}{:02}-{:02}{:02}{:02}",
                        now.year(),
                        u8::from(now.month()),
                        now.day(),
                        now.hour(),
                        now.minute(),
                        now.second()
                    );
                    project_root.join(format!("coda-conversation-{ts}.md"))
                }
            };

            if let Some(parent) = path.parent() {
                std::fs::create_dir_all(parent)?;
            }
            std::fs::write(&path, markdown.as_bytes())?;
            Ok(path)
        })
        .await;

        match result {
            Ok(Ok(path)) => self.notice(format!("Conversation exported to {}", path.display()), NoticeLevel::Info),
            Ok(Err(e)) => self.notice(format!("Export failed: {e}"), NoticeLevel::Error),
            Err(_) => self.notice("Export was interrupted.", NoticeLevel::Error),
        }
    }

    /// `/diff` — run `git diff` and render the result with syntax colouring.
    pub(super) async fn cmd_diff(&mut self) {
        let cwd = self.paths.project_root.clone();
        let output = tokio::process::Command::new("git")
            .arg("diff")
            .current_dir(&cwd)
            .output()
            .await;

        match output {
            Ok(out) => {
                let stderr = String::from_utf8_lossy(&out.stderr);
                if !out.status.success() || stderr.contains("not a git repository") {
                    let msg = if stderr.trim().is_empty() {
                        "git exited with a non-zero status. Is this directory a git repository?"
                            .to_string()
                    } else {
                        coda_render::text::sanitize(stderr.trim())
                    };
                    self.notice(msg, NoticeLevel::Error);
                    return;
                }
                let stdout = String::from_utf8_lossy(&out.stdout).into_owned();
                if stdout.trim().is_empty() {
                    self.notice("No uncommitted changes.", NoticeLevel::Info);
                } else {
                    // Sanitize before storage: strips ANSI escapes from coloured git output.
                    let sanitized = coda_render::text::sanitize(&stdout);
                    self.apply(UiEvent::DiffOutput { text: sanitized });
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                self.notice(
                    "git not found. Make sure git is installed and on your PATH.",
                    NoticeLevel::Warning,
                );
            }
            Err(e) => self.notice(format!("Could not run git: {e}"), NoticeLevel::Error),
        }
    }

    /// `/image <path>` — base64-encode an image and stage it for the next turn.
    ///
    /// Maximum size is 5 MB. Accepted formats: .png .jpg/.jpeg .gif .webp.
    pub(super) async fn cmd_image(&mut self, invocation: &commands::Invocation) {
        let Some(path_str) = invocation.first() else {
            self.notice(
                "Usage: /image <path>  — attaches an image to the next turn.",
                NoticeLevel::Warning,
            );
            return;
        };

        let path = std::path::PathBuf::from(path_str);
        let file_name = path
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("image")
            .to_string();

        let result = tokio::task::spawn_blocking(move || -> Result<(String, Vec<u8>), String> {
            let ext = path.extension().and_then(|e| e.to_str()).unwrap_or("");
            let media_type = image_media_type(ext).ok_or_else(|| {
                format!(
                    "File type not supported: '.{ext}'. Supported: .png, .jpg, .jpeg, .gif, .webp"
                )
            })?;

            if !path.exists() {
                return Err(format!("File not found: {}", path.display()));
            }

            let metadata = std::fs::metadata(&path)
                .map_err(|e| format!("Could not read file: {e}"))?;
            const MAX_BYTES: u64 = 5 * 1024 * 1024;
            if metadata.len() > MAX_BYTES {
                let size_mb = metadata.len() as f64 / (1024.0 * 1024.0);
                return Err(format!(
                    "File too large ({size_mb:.1} MB). Maximum size is 5 MB."
                ));
            }

            let bytes = std::fs::read(&path).map_err(|e| format!("Could not read image: {e}"))?;
            Ok((media_type.to_string(), bytes))
        })
        .await;

        match result {
            Ok(Ok((media_type, bytes))) => {
                let token = self.stage_image_bytes(&media_type, &bytes);
                let size_kb = bytes.len() as f64 / 1024.0;
                self.notice(
                    format!(
                        "Attached {file_name} as {token} ({size_kb:.1} KB). It will be sent with your next message."
                    ),
                    NoticeLevel::Info,
                );
            }
            Ok(Err(msg)) => self.notice(msg, NoticeLevel::Error),
            Err(_) => self.notice("Could not read the image file.", NoticeLevel::Error),
        }
    }
}

/// Parses the `n` argument of `/rewind`, returning `Ok(n)` for a valid
/// positive integer or `Err` with a usage hint.
///
/// Mirrors C# `RewindCommand`: defaults to 1 when absent; rejects zero and
/// non-integers with the same usage message.
fn parse_rewind_n(arg: Option<&str>) -> Result<u32, &'static str> {
    match arg {
        None => Ok(1),
        Some(s) => s
            .parse::<u32>()
            .ok()
            .filter(|&v| v >= 1)
            .ok_or("Usage: /rewind [n] where n is a positive integer."),
    }
}

/// Returns the MIME type for a supported image extension, case-insensitively.
fn image_media_type(extension: &str) -> Option<&'static str> {
    match extension.to_lowercase().as_str() {
        "png" => Some("image/png"),
        "jpg" | "jpeg" => Some("image/jpeg"),
        "gif" => Some("image/gif"),
        "webp" => Some("image/webp"),
        _ => None,
    }
}

/// Renders transcript blocks as a Markdown document for `/export`.
pub fn build_markdown_export(blocks: &[crate::transcript::Block]) -> String {
    use crate::transcript::Block;
    let mut out = String::from("# Coda Conversation Export\n\n");
    for block in blocks {
        match block {
            Block::User { text, .. } => {
                out.push_str("## User\n\n");
                out.push_str(text);
                out.push_str("\n\n");
            }
            Block::Assistant { text, .. } => {
                out.push_str("## Assistant\n\n");
                out.push_str(text);
                out.push_str("\n\n");
            }
            Block::Tools { activity, .. } => {
                for call in &activity.calls {
                    out.push_str(&format!("- tool call: {}\n", call.name));
                    if let Some(result) = &call.result {
                        out.push_str(&format!("- tool result: {}\n", result.chars().take(200).collect::<String>()));
                    }
                }
                if !activity.calls.is_empty() {
                    out.push('\n');
                }
            }
            Block::Diff { raw } => {
                out.push_str("```diff\n");
                out.push_str(raw);
                out.push_str("```\n\n");
            }
            // Skip non-content blocks in the export.
            Block::Notice { .. }
            | Block::Permission { .. }
            | Block::Question { .. }
            | Block::CommandOutput { .. }
            | Block::Thinking { .. }
            | Block::Banner { .. }
            | Block::SessionBoundary { .. } => {}
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn image_media_type_maps_supported_extensions() {
        assert_eq!(image_media_type("png"), Some("image/png"));
        assert_eq!(image_media_type("jpg"), Some("image/jpeg"));
        assert_eq!(image_media_type("jpeg"), Some("image/jpeg"));
        assert_eq!(image_media_type("gif"), Some("image/gif"));
        assert_eq!(image_media_type("webp"), Some("image/webp"));
    }

    #[test]
    fn image_media_type_is_case_insensitive() {
        assert_eq!(image_media_type("PNG"), Some("image/png"));
        assert_eq!(image_media_type("JPG"), Some("image/jpeg"));
        assert_eq!(image_media_type("WEBP"), Some("image/webp"));
    }

    #[test]
    fn image_media_type_rejects_unsupported_extensions() {
        assert_eq!(image_media_type("bmp"), None);
        assert_eq!(image_media_type("svg"), None);
        assert_eq!(image_media_type("tiff"), None);
        assert_eq!(image_media_type(""), None);
    }

    #[test]
    fn build_markdown_export_includes_user_and_assistant_turns() {
        use crate::transcript::Block;
        let blocks = vec![
            Block::User {
                text: "Hello".to_string(),
                timestamp: "09:41".to_string(),
                pending: false,
                queue_id: None,
            },
            Block::Assistant {
                text: "World".to_string(),
                complete: true,
            },
        ];
        let md = build_markdown_export(&blocks);
        assert!(md.contains("## User"));
        assert!(md.contains("Hello"));
        assert!(md.contains("## Assistant"));
        assert!(md.contains("World"));
    }

    #[test]
    fn build_markdown_export_is_empty_for_no_content_blocks() {
        use crate::transcript::Block;
        // Notice blocks are not exported.
        let blocks = vec![Block::Notice {
            text: "internal notice".to_string(),
            level: crate::transcript::NoticeLevel::Info,
        }];
        let md = build_markdown_export(&blocks);
        // Only the header line; no turns.
        assert!(!md.contains("## User"));
        assert!(!md.contains("## Assistant"));
    }

    #[test]
    fn rewind_n_defaults_to_one_when_no_arg() {
        assert_eq!(parse_rewind_n(None), Ok(1));
    }

    #[test]
    fn rewind_n_parses_a_valid_positive_integer() {
        assert_eq!(parse_rewind_n(Some("3")), Ok(3));
        assert_eq!(parse_rewind_n(Some("1")), Ok(1));
        assert_eq!(parse_rewind_n(Some("100")), Ok(100));
    }

    #[test]
    fn rewind_n_rejects_zero() {
        assert!(parse_rewind_n(Some("0")).is_err());
    }

    #[test]
    fn rewind_n_rejects_non_integer() {
        assert!(parse_rewind_n(Some("abc")).is_err());
        assert!(parse_rewind_n(Some("1.5")).is_err());
        assert!(parse_rewind_n(Some("")).is_err());
    }

    #[test]
    fn rewind_n_rejects_negative_integers() {
        // u32 parse rejects negative strings.
        assert!(parse_rewind_n(Some("-1")).is_err());
        assert!(parse_rewind_n(Some("-100")).is_err());
    }

    #[test]
    fn rewind_n_error_message_matches_c_sharp() {
        let err = parse_rewind_n(Some("0")).unwrap_err();
        assert!(
            err.contains("positive integer"),
            "error must mention 'positive integer', got: {err}"
        );
    }
}
