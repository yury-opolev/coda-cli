//! `/mcp login|logout <server>` — sign in to or out of one HTTP MCP server.
//!
//! A sign-in waits for the user: the engine opens a browser (`oauth`) or the
//! OS account broker (`entra-broker`) and can take minutes. Awaiting that
//! inline would stop the event loop — no keystrokes, no redraw, no quit — so
//! the request runs on its own task, exactly like `/btw`, and its outcome
//! comes back through a channel the loop selects on.

use std::time::Duration;

use coda_proto::mcp::{McpAuthAction, McpAuthActionResult};
use tokio::sync::mpsc;

use super::App;
use crate::transcript::NoticeLevel;

/// How long the client waits for `mcp/auth`. Longer than the engine's own
/// worst case (a 300 s browser callback, or broker silent + interactive +
/// discovery), so the engine's typed outcome normally arrives first.
const MCP_AUTH_CLIENT_TIMEOUT: Duration = Duration::from_secs(330);

pub(crate) const MCP_USAGE: &str = "Usage: /mcp [login|logout <server>]";

/// One finished (or failed) `mcp/auth` request.
pub(crate) struct McpAuthReply {
    server: String,
    action: McpAuthAction,
    outcome: Result<McpAuthActionResult, String>,
}

/// The channel `mcp/auth` outcomes come back on.
pub(crate) struct McpAuthRequests {
    tx: mpsc::UnboundedSender<McpAuthReply>,
    rx: Option<mpsc::UnboundedReceiver<McpAuthReply>>,
}

impl Default for McpAuthRequests {
    fn default() -> Self {
        let (tx, rx) = mpsc::unbounded_channel();
        Self { tx, rx: Some(rx) }
    }
}

impl McpAuthRequests {
    /// The receiving end, for the event loop to select on. Taken once.
    pub(crate) fn take_replies(&mut self) -> mpsc::UnboundedReceiver<McpAuthReply> {
        self.rx.take().unwrap_or_else(|| mpsc::unbounded_channel().1)
    }
}

/// Parses the arguments of `/mcp`: `login <server>` or `logout <server>`.
pub(crate) fn parse_auth_args(args: &str) -> Option<(McpAuthAction, String)> {
    let words: Vec<&str> = args.split_whitespace().collect();
    let [verb, server] = words.as_slice() else {
        return None;
    };
    let action = if verb.eq_ignore_ascii_case("login") {
        McpAuthAction::Login
    } else if verb.eq_ignore_ascii_case("logout") {
        McpAuthAction::Logout
    } else {
        return None;
    };
    Some((action, (*server).to_owned()))
}

fn verb(action: McpAuthAction) -> &'static str {
    match action {
        McpAuthAction::Login => "login",
        McpAuthAction::Logout => "logout",
    }
}

/// What the user is told about a finished request, and at which level.
fn reply_notice(reply: &McpAuthReply) -> (String, NoticeLevel) {
    match &reply.outcome {
        Ok(result) if result.ok => (result.message.clone(), NoticeLevel::Info),
        Ok(result) => (result.message.clone(), NoticeLevel::Error),
        Err(error) => (
            format!(
                "/mcp {} {} failed: {error}",
                verb(reply.action),
                coda_render::text::sanitize(&reply.server)
            ),
            NoticeLevel::Error,
        ),
    }
}

impl App {
    /// `/mcp login|logout <server>`.
    pub(super) fn cmd_mcp_auth(&mut self, args: &str) {
        let Some((action, server)) = parse_auth_args(args) else {
            self.notice(MCP_USAGE, NoticeLevel::Warning);
            return;
        };
        if !self.require_engine(&format!("/mcp {}", verb(action))) {
            return;
        }
        let shown = coda_render::text::sanitize(&server);
        let started = match action {
            McpAuthAction::Login => format!("Signing in to MCP server '{shown}'…"),
            McpAuthAction::Logout => format!("Signing out of MCP server '{shown}'…"),
        };
        self.notice(started, NoticeLevel::Info);

        let connection = self.connection.clone();
        let tx = self.mcp_auth.tx.clone();
        tokio::spawn(async move {
            let request = crate::api::mcp_auth(&connection, &server, action);
            let outcome = match tokio::time::timeout(MCP_AUTH_CLIENT_TIMEOUT, request).await {
                Ok(Ok(result)) => Ok(result),
                Ok(Err(coda_client::ClientError::Rpc(error))) => Err(error.message),
                Ok(Err(error)) => Err(error.to_string()),
                Err(_) => Err(format!(
                    "the engine did not answer within {} s",
                    MCP_AUTH_CLIENT_TIMEOUT.as_secs()
                )),
            };
            let _ = tx.send(McpAuthReply { server, action, outcome });
        });
        self.dirty = true;
    }

    /// An `mcp/auth` request finished.
    pub(super) fn on_mcp_auth_reply(&mut self, reply: McpAuthReply) {
        let (text, level) = reply_notice(&reply);
        self.notice(text, level);
        self.dirty = true;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::app::serve::tests::{app_with, notices_at, warnings};
    use serde_json::{json, Value};
    use std::sync::{Arc, Mutex};

    #[test]
    fn parses_login_and_logout_with_one_server_name() {
        assert_eq!(parse_auth_args("login docs"), Some((McpAuthAction::Login, "docs".into())));
        assert_eq!(parse_auth_args(" LOGOUT  docs "), Some((McpAuthAction::Logout, "docs".into())));
        for bad in ["", "login", "logout", "list", "info docs", "login a b", "x"] {
            assert_eq!(parse_auth_args(bad), None, "{bad:?}");
        }
    }

    #[test]
    fn a_failed_request_names_the_command_and_the_server() {
        let reply = McpAuthReply {
            server: "docs".into(),
            action: McpAuthAction::Login,
            outcome: Err("the engine connection is closed".into()),
        };
        let (text, level) = reply_notice(&reply);
        assert_eq!(text, "/mcp login docs failed: the engine connection is closed");
        assert_eq!(level, NoticeLevel::Error);
    }

    #[tokio::test]
    async fn mcp_login_asks_the_engine_and_reports_the_outcome() {
        let seen = Arc::new(Mutex::new(None::<Value>));
        let record = seen.clone();
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, move |method, params| {
            if method == "mcp/auth" {
                *record.lock().unwrap() = Some(params.clone());
                if params["action"] == "login" {
                    json!({ "ok": true, "message": "Signed in to 'docs' as user@contoso.com." })
                } else {
                    json!({ "ok": false, "message": "No MCP server named 'docs'." })
                }
            } else {
                json!({})
            }
        });
        let mut replies = harness.app.mcp_auth.take_replies();

        harness.app.run_command(crate::commands::parse("/mcp login docs").unwrap()).await;
        assert!(
            notices_at(&harness.app, NoticeLevel::Info)
                .contains(&"Signing in to MCP server 'docs'…".to_owned()),
            "the wait is announced before it starts"
        );
        let reply = tokio::time::timeout(Duration::from_secs(5), replies.recv()).await.unwrap().unwrap();
        harness.app.on_mcp_auth_reply(reply);
        assert_eq!(*seen.lock().unwrap(), Some(json!({ "server": "docs", "action": "login" })));
        assert!(notices_at(&harness.app, NoticeLevel::Info)
            .contains(&"Signed in to 'docs' as user@contoso.com.".to_owned()));

        harness.app.run_command(crate::commands::parse("/mcp logout docs").unwrap()).await;
        let reply = tokio::time::timeout(Duration::from_secs(5), replies.recv()).await.unwrap().unwrap();
        harness.app.on_mcp_auth_reply(reply);
        assert!(notices_at(&harness.app, NoticeLevel::Error)
            .contains(&"No MCP server named 'docs'.".to_owned()));
    }

    #[tokio::test]
    async fn mcp_with_other_arguments_shows_the_usage_and_asks_nothing() {
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        harness.app.run_command(crate::commands::parse("/mcp list").unwrap()).await;
        harness.app.run_command(crate::commands::parse("/mcp login").unwrap()).await;
        assert_eq!(warnings(&harness.app), vec![MCP_USAGE.to_owned(), MCP_USAGE.to_owned()]);
        assert!(!harness.calls.lock().unwrap().iter().any(|m| m == "mcp/auth"));
    }

    #[tokio::test]
    async fn mcp_login_without_an_engine_says_so() {
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        harness.app.set_engine_connected(false);
        harness.app.run_command(crate::commands::parse("/mcp login docs").unwrap()).await;
        assert!(warnings(&harness.app).iter().any(|w| w.contains("/mcp login needs an engine")));
        assert!(!harness.calls.lock().unwrap().iter().any(|m| m == "mcp/auth"));
    }
}
