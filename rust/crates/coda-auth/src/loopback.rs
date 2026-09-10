//! Loopback redirect listener for the OAuth authorization-code flow.
//!
//! Binds a TCP listener on `127.0.0.1:0` (loopback only — never `0.0.0.0`)
//! and waits for the authorization server to redirect the browser to
//! `http://localhost:<port>/callback?code=...&state=...`.
//!
//! The listener answers exactly one `/callback` request. Two shapes are
//! available:
//!
//! - [`LoopbackListener::wait_for_callback`] — capture the redirect and send a
//!   neutral acknowledgement immediately. The page never claims the sign-in
//!   succeeded, because at that point nothing has been validated: the `state`
//!   has not been compared and no token has been exchanged.
//! - [`LoopbackListener::wait_for_callback_pending`] — capture the redirect and
//!   keep the browser's connection open as a [`PendingCallback`], so the caller
//!   can validate the redirect, exchange the code, and only then answer with
//!   the real verdict via [`PendingCallback::respond`].
//!
//! If the caller's timeout fires before the redirect arrives — including a
//! client that connects and then stalls mid-request — the future returns
//! [`AuthError::LoginCancelled`]; the timeout bounds the accept *and* the
//! request read, so no peer can hold the login open.
//!
//! Dropping the listener (or the future awaiting it) closes the socket and any
//! half-answered browser connection, which is what makes an outer
//! `tokio::select!` on a cancellation token a correct way to cancel a login.
//!
//! **Security**: binding to `127.0.0.1` (not `0.0.0.0`) ensures that no
//! other machine on the network can race the user to deliver the callback.

use std::collections::HashMap;
use std::time::Duration;

use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};
use tokio::net::tcp::OwnedWriteHalf;
use tokio::net::TcpListener;

use crate::error::AuthError;

/// Upper bound on the request line and each header line. A browser redirect is
/// small; anything larger is a client trying to make us buffer.
const MAX_LINE_BYTES: u64 = 16 * 1024;

/// Upper bound on the number of header lines read before the blank line.
const MAX_HEADER_LINES: usize = 100;

/// How long a single accepted connection may take to deliver its request
/// before it is dropped and the listener goes back to accepting.
///
/// The redirect is delivered by a browser on the same machine, so this only
/// needs to cover local scheduling — three seconds is generous. It exists so a
/// peer that connects and stays silent cannot starve the real callback, which
/// arrives on a later connection.
const PER_CONNECTION_READ_TIMEOUT: Duration = Duration::from_secs(3);

/// A captured OAuth redirect.
#[derive(Debug, Clone)]
pub struct RedirectResult {
    pub code: Option<String>,
    pub state: Option<String>,
    pub error: Option<String>,
    pub iss: Option<String>,
}

/// The outcome the caller reached after validating a captured redirect.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CallbackVerdict {
    /// A credential was obtained.
    Success,
    /// The login failed (state mismatch, missing code, exchange rejected).
    Failure,
}

/// A captured redirect whose browser connection is still open.
///
/// The caller inspects [`PendingCallback::redirect`], performs whatever
/// validation and token exchange it needs, and then calls
/// [`PendingCallback::respond`] with the real verdict. Dropping it instead
/// closes the connection without a reply, which is the correct behaviour on
/// cancellation.
pub struct PendingCallback {
    result: RedirectResult,
    writer: OwnedWriteHalf,
}

impl PendingCallback {
    /// The captured redirect parameters. Nothing has been validated yet.
    pub fn redirect(&self) -> &RedirectResult {
        &self.result
    }

    /// Answer the browser with the final verdict and close the connection.
    pub async fn respond(mut self, verdict: CallbackVerdict) {
        let (status, body) = match verdict {
            CallbackVerdict::Success => ("200 OK", page("Signed in", "You can close this window and return to the application.")),
            CallbackVerdict::Failure => (
                "400 Bad Request",
                page("Sign-in failed", "Return to the application for details."),
            ),
        };
        let _ = self.writer.write_all(http_response(status, &body).as_bytes()).await;
        let _ = self.writer.flush().await;
    }
}

/// One-shot loopback listener for the OAuth authorization-code callback.
pub struct LoopbackListener {
    listener: TcpListener,
    /// The port the OS assigned.
    pub port: u16,
}

impl LoopbackListener {
    /// Binds a listener on a random free port on `127.0.0.1`.
    pub async fn bind() -> Result<Self, AuthError> {
        // Explicitly bind to 127.0.0.1 so the socket is not reachable from
        // the network — only the local browser can complete the redirect.
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .map_err(|e| AuthError::Transport(format!("failed to bind loopback listener: {e}")))?;

        let port = listener.local_addr().map_err(|e| {
            AuthError::Transport(format!("failed to get listener address: {e}"))
        })?.port();

        Ok(Self { listener, port })
    }

    /// The full `redirect_uri` to use in the authorization URL.
    pub fn redirect_uri(&self) -> String {
        format!("http://localhost:{}/callback", self.port)
    }

    /// Wait up to `timeout` for the browser to hit `/callback`, acknowledging
    /// the request immediately.
    ///
    /// All other paths return 404 and keep waiting. The acknowledgement page is
    /// deliberately neutral — it reports that the response was received, not
    /// that the sign-in succeeded, because the caller has not validated
    /// anything yet. Callers that want the browser to see the real verdict
    /// should use [`LoopbackListener::wait_for_callback_pending`].
    pub async fn wait_for_callback(self, timeout: Duration) -> Result<RedirectResult, AuthError> {
        let mut pending = self.wait_for_callback_pending(timeout).await?;
        // An `error=` parameter is the authorization server's own verdict, so
        // reporting failure here is honest. Everything else gets a neutral
        // acknowledgement: the caller has not yet checked the state or
        // exchanged the code, so success is not yet knowable.
        let (status, body) = if pending.result.error.is_some() {
            (
                "400 Bad Request",
                page("Sign-in failed", "The authorization server returned an error."),
            )
        } else {
            (
                "200 OK",
                page(
                    "Response received",
                    "You can close this window and return to the application.",
                ),
            )
        };
        let _ = pending.writer.write_all(http_response(status, &body).as_bytes()).await;
        let _ = pending.writer.flush().await;
        Ok(pending.result)
    }

    /// Wait up to `timeout` for the browser to hit `/callback`, leaving the
    /// browser connection open so the caller can answer with a verdict once the
    /// redirect has been validated and the code exchanged.
    pub async fn wait_for_callback_pending(
        self,
        timeout: Duration,
    ) -> Result<PendingCallback, AuthError> {
        tokio::time::timeout(timeout, self.serve())
            .await
            .map_err(|_| {
                AuthError::LoginCancelled("timed out waiting for the OAuth redirect".into())
            })?
    }

    async fn serve(self) -> Result<PendingCallback, AuthError> {
        loop {
            let (stream, _) = self.listener.accept().await.map_err(|e| {
                // An accept failure is a socket problem, not the user
                // abandoning the login; classifying it as cancellation would
                // tell them they gave up when the OS refused us a connection.
                AuthError::Transport(format!("loopback listener failed: {e}"))
            })?;

            // Each connection gets its own short read budget. A peer that
            // connects and sends nothing — a browser pre-connect, a port
            // scanner, a corporate health probe — would otherwise hold the
            // single-threaded accept loop for the caller's entire login budget
            // and starve the real redirect, which arrives on a later
            // connection. On expiry we drop this connection and go back to
            // accepting; the caller's overall timeout still bounds the whole
            // wait.
            match tokio::time::timeout(PER_CONNECTION_READ_TIMEOUT, read_request(stream)).await {
                Ok(Some((result, writer))) => return Ok(PendingCallback { result, writer }),
                // Wrong path (already answered with 404), unreadable, or the
                // read budget expired: keep accepting.
                Ok(None) | Err(_) => continue,
            }
        }
    }
}

/// Reads one HTTP request from an accepted connection.
///
/// Returns `None` for anything that is not a `/callback` request (answering
/// 404 where appropriate), so the caller can keep waiting for the real
/// redirect.
async fn read_request(
    stream: tokio::net::TcpStream,
) -> Option<(RedirectResult, OwnedWriteHalf)> {
    let (reader, mut writer) = stream.into_split();
    // Bound the request line and every header line: a peer that opens a
    // socket and dribbles bytes must not be able to make us buffer.
    let mut lines = BufReader::new(reader.take(MAX_LINE_BYTES * 2)).lines();

    // Read the first line: "GET /path?query HTTP/1.1"
    let request_line = match lines.next_line().await {
        Ok(Some(line)) if line.len() as u64 <= MAX_LINE_BYTES => line,
        _ => return None, // malformed, oversized, or closed
    };

    // Skip remaining headers.
    let mut header_lines = 0usize;
    loop {
        match lines.next_line().await {
            Ok(Some(line)) if line.is_empty() => break,
            Ok(Some(_)) => {
                header_lines += 1;
                if header_lines > MAX_HEADER_LINES {
                    break;
                }
            }
            _ => break,
        }
    }

    let (method_path, _) = request_line.split_once(" HTTP/")?;
    let (_, path_and_query) = method_path.split_once(' ').unwrap_or(("", method_path));
    let (path, query) = path_and_query.split_once('?').unwrap_or((path_and_query, ""));

    if path != "/callback" {
        let _ = writer.write_all(b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n").await;
        return None;
    }

    let params = parse_query(query);
    let result = RedirectResult {
        code: params.get("code").cloned(),
        state: params.get("state").cloned(),
        error: params.get("error").cloned(),
        iss: params.get("iss").cloned(),
    };
    Some((result, writer))
}

fn page(title: &str, message: &str) -> String {
    format!(
        "<html><head><title>{title}</title></head>\
         <body style=\"font-family:sans-serif\">\
         <h2>{title}</h2>\
         <p>{message}</p>\
         </body></html>"
    )
}

fn http_response(status: &str, body: &str) -> String {
    format!(
        "HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
        body.len()
    )
}

/// Parse an `application/x-www-form-urlencoded` query string into a map.
fn parse_query(query: &str) -> HashMap<String, String> {
    let mut map = HashMap::new();
    for pair in query.split('&').filter(|s| !s.is_empty()) {
        let (k, v) = pair.split_once('=').unwrap_or((pair, ""));
        map.insert(url_decode(k), url_decode(v));
    }
    map
}

fn url_decode(s: &str) -> String {
    // Percent-decode into a byte buffer and interpret the result as UTF-8.
    // Decoding each escape as a `char` would treat every byte as Latin-1 and
    // silently mangle any non-ASCII value — an `iss` or `error_description`
    // with a non-ASCII character would be corrupted rather than preserved.
    // Invalid UTF-8 is replaced rather than rejected: the caller compares the
    // state and code for equality, and a corrupted value simply fails to match.
    let mut out: Vec<u8> = Vec::with_capacity(s.len());
    let bytes = s.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let Ok(hex) = std::str::from_utf8(&bytes[i + 1..i + 3]) {
                if let Ok(byte) = u8::from_str_radix(hex, 16) {
                    out.push(byte);
                    i += 3;
                    continue;
                }
            }
        } else if bytes[i] == b'+' {
            out.push(b' ');
            i += 1;
            continue;
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn listener_binds_to_127_0_0_1_only() {
        let listener = LoopbackListener::bind().await.expect("bind");
        // Check the local address is on loopback.
        let addr = listener.listener.local_addr().expect("addr");
        assert_eq!(addr.ip().to_string(), "127.0.0.1");
    }

    #[tokio::test]
    async fn captures_code_and_state_from_callback() {
        let listener = LoopbackListener::bind().await.expect("bind");
        let port = listener.port;

        // Simulate the browser redirect.
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(10)).await;
            let mut stream = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
                .await
                .expect("connect");
            stream
                .write_all(b"GET /callback?code=authcode123&state=mystate HTTP/1.1\r\nHost: localhost\r\n\r\n")
                .await
                .expect("write");
        });

        let result = listener
            .wait_for_callback(Duration::from_secs(5))
            .await
            .expect("callback");

        assert_eq!(result.code.as_deref(), Some("authcode123"));
        assert_eq!(result.state.as_deref(), Some("mystate"));
        assert!(result.error.is_none());
    }

    #[tokio::test]
    async fn non_callback_path_returns_404_and_keeps_listening() {
        let listener = LoopbackListener::bind().await.expect("bind");
        let port = listener.port;

        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(10)).await;
            // First request: wrong path.
            {
                let mut s = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
                    .await
                    .expect("connect");
                s.write_all(b"GET /other HTTP/1.1\r\nHost: localhost\r\n\r\n")
                    .await
                    .expect("write");
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
            // Second request: the real callback.
            let mut s = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
                .await
                .expect("connect");
            s.write_all(b"GET /callback?code=c&state=s HTTP/1.1\r\nHost: localhost\r\n\r\n")
                .await
                .expect("write");
        });

        let result = listener
            .wait_for_callback(Duration::from_secs(5))
            .await
            .expect("callback");

        assert_eq!(result.code.as_deref(), Some("c"));
    }

    #[tokio::test]
    async fn timeout_returns_login_cancelled() {
        let listener = LoopbackListener::bind().await.expect("bind");
        let err = listener
            .wait_for_callback(Duration::from_millis(50))
            .await
            .expect_err("should time out");
        assert!(
            matches!(err, AuthError::LoginCancelled(_)),
            "expected LoginCancelled, got {err:?}"
        );
    }

    /// The immediate acknowledgement must not claim the sign-in succeeded:
    /// at that point the state has not been checked and no token exchanged.
    #[tokio::test]
    async fn the_immediate_acknowledgement_does_not_claim_success() {
        let listener = LoopbackListener::bind().await.expect("bind");
        let port = listener.port;

        let browser = tokio::spawn(async move {
            let mut stream = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
                .await
                .expect("connect");
            stream
                .write_all(b"GET /callback?code=c&state=s HTTP/1.1\r\nHost: localhost\r\n\r\n")
                .await
                .expect("write");
            let mut page = String::new();
            let _ = stream.read_to_string(&mut page).await;
            page
        });

        listener.wait_for_callback(Duration::from_secs(5)).await.expect("callback");
        let page = browser.await.expect("browser");
        assert!(!page.contains("Signed in"), "premature success claim: {page}");
        assert!(page.contains("Response received"), "expected a neutral ack: {page}");
    }

    #[tokio::test]
    async fn a_pending_callback_defers_the_page_until_the_verdict() {
        let listener = LoopbackListener::bind().await.expect("bind");
        let port = listener.port;

        let browser = tokio::spawn(async move {
            let mut stream = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
                .await
                .expect("connect");
            stream
                .write_all(b"GET /callback?code=c&state=s HTTP/1.1\r\nHost: localhost\r\n\r\n")
                .await
                .expect("write");
            let mut page = String::new();
            let _ = stream.read_to_string(&mut page).await;
            page
        });

        let pending = listener
            .wait_for_callback_pending(Duration::from_secs(5))
            .await
            .expect("pending callback");
        assert_eq!(pending.redirect().code.as_deref(), Some("c"));
        pending.respond(CallbackVerdict::Success).await;

        let page = browser.await.expect("browser");
        assert!(page.contains("Signed in"), "verdict page expected: {page}");
    }

    /// An `error=` redirect is the server's own verdict, so the immediate
    /// acknowledgement may (and must) report the failure.
    #[tokio::test]
    async fn an_error_redirect_is_acknowledged_as_a_failure() {
        let listener = LoopbackListener::bind().await.expect("bind");
        let port = listener.port;

        let browser = tokio::spawn(async move {
            let mut stream = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
                .await
                .expect("connect");
            stream
                .write_all(b"GET /callback?error=access_denied HTTP/1.1\r\nHost: localhost\r\n\r\n")
                .await
                .expect("write");
            let mut page = String::new();
            let _ = stream.read_to_string(&mut page).await;
            page
        });

        let result = listener.wait_for_callback(Duration::from_secs(5)).await.expect("callback");
        assert_eq!(result.error.as_deref(), Some("access_denied"));
        let page = browser.await.expect("browser");
        assert!(page.contains("400 Bad Request"), "expected a failure status: {page}");
        assert!(!page.contains("<h2>Signed in</h2>"), "must not claim success: {page}");
    }

    /// A peer that opens the socket and never terminates its headers must not
    /// hold the login open beyond the caller's timeout.
    #[tokio::test]
    async fn a_stalled_request_still_times_out() {        let listener = LoopbackListener::bind().await.expect("bind");
        let port = listener.port;

        let stall = tokio::spawn(async move {
            let mut stream = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
                .await
                .expect("connect");
            let _ = stream.write_all(b"GET /callback?code=c HTTP/1.1\r\nHost: x\r\n").await;
            tokio::time::sleep(Duration::from_secs(30)).await;
        });

        let err = listener
            .wait_for_callback(Duration::from_millis(200))
            .await
            .expect_err("stalled request must time out");
        stall.abort();
        assert!(matches!(err, AuthError::LoginCancelled(_)), "got {err:?}");
    }

    #[test]
    fn parse_query_handles_percent_encoding() {
        let q = "code=abc%3D%3D&state=xy+z";
        let map = parse_query(q);
        assert_eq!(map.get("code").map(String::as_str), Some("abc=="));
        assert_eq!(map.get("state").map(String::as_str), Some("xy z"));
    }

    /// Multi-byte UTF-8 arrives as a sequence of percent escapes; decoding each
    /// byte as a `char` would produce Latin-1 mojibake instead of the value the
    /// authorization server sent.
    #[test]
    fn parse_query_decodes_multi_byte_utf8() {
        let map = parse_query("iss=https%3A%2F%2Fissuer.example%2Fcaf%C3%A9&state=%E2%9C%93");
        assert_eq!(map.get("iss").map(String::as_str), Some("https://issuer.example/café"));
        assert_eq!(map.get("state").map(String::as_str), Some("✓"));
    }

    #[test]
    fn parse_query_replaces_invalid_utf8_rather_than_corrupting_silently() {
        // A lone 0xFF is not valid UTF-8; it must not become U+00FF.
        let map = parse_query("state=%FF");
        assert_eq!(map.get("state").map(String::as_str), Some("\u{fffd}"));
    }

    /// A peer that connects and never speaks must not starve the real
    /// redirect, which arrives on a subsequent connection.
    #[tokio::test]
    async fn a_silent_connection_does_not_starve_a_later_callback() {
        let listener = LoopbackListener::bind().await.expect("bind");
        let port = listener.port;

        let silent = tokio::spawn(async move {
            let _s = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
                .await
                .expect("connect");
            tokio::time::sleep(Duration::from_secs(120)).await;
        });
        tokio::time::sleep(Duration::from_millis(50)).await;

        tokio::spawn(async move {
            let mut s = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
                .await
                .expect("connect");
            let _ = s
                .write_all(b"GET /callback?code=late&state=s HTTP/1.1\r\nHost: localhost\r\n\r\n")
                .await;
            let mut page = String::new();
            let _ = s.read_to_string(&mut page).await;
        });

        let result = listener
            .wait_for_callback(Duration::from_secs(30))
            .await
            .expect("the later callback must still be served");
        silent.abort();
        assert_eq!(result.code.as_deref(), Some("late"));
    }
}
