//! Loopback redirect listener for the OAuth authorization-code flow.
//!
//! Binds a TCP listener on `127.0.0.1:0` (loopback only — never `0.0.0.0`)
//! and waits for the authorization server to redirect the browser to
//! `http://localhost:<port>/callback?code=...&state=...`.
//!
//! After receiving exactly one callback request, it sends a small HTML
//! success page and shuts down the listener.  If the caller's timeout fires
//! before the redirect arrives the future returns
//! [`AuthError::LoginCancelled`].
//!
//! **Security**: binding to `127.0.0.1` (not `0.0.0.0`) ensures that no
//! other machine on the network can race the user to deliver the callback.

use std::collections::HashMap;
use std::time::Duration;

use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::TcpListener;

use crate::error::AuthError;

/// A captured OAuth redirect.
#[derive(Debug, Clone)]
pub struct RedirectResult {
    pub code: Option<String>,
    pub state: Option<String>,
    pub error: Option<String>,
    pub iss: Option<String>,
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

    /// Wait up to `timeout` for the browser to hit `/callback`.
    ///
    /// All other paths return 404 and keep waiting.  The first `/callback`
    /// request is answered with an HTML success/error page, and the result is
    /// returned.
    pub async fn wait_for_callback(self, timeout: Duration) -> Result<RedirectResult, AuthError> {
        tokio::time::timeout(timeout, self.serve())
            .await
            .map_err(|_| AuthError::LoginCancelled("timed out waiting for the OAuth redirect".into()))?
    }

    async fn serve(self) -> Result<RedirectResult, AuthError> {
        loop {
            let (stream, _) = self.listener.accept().await.map_err(|e| {
                AuthError::LoginCancelled(format!("loopback listener failed: {e}"))
            })?;

            let (reader, mut writer) = stream.into_split();
            let mut lines = BufReader::new(reader).lines();

            // Read the first line: "GET /path?query HTTP/1.1"
            let request_line = match lines.next_line().await {
                Ok(Some(line)) => line,
                _ => continue, // malformed request — keep listening
            };

            // Skip remaining headers.
            while let Ok(Some(line)) = lines.next_line().await {
                if line.is_empty() {
                    break;
                }
            }

            let Some((method_path, _)) = request_line.split_once(" HTTP/") else {
                continue;
            };
            let (_, path_and_query) = method_path.split_once(' ').unwrap_or(("", method_path));
            let (path, query) = path_and_query.split_once('?').unwrap_or((path_and_query, ""));

            if path != "/callback" {
                let _ = writer.write_all(b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n").await;
                continue;
            }

            let params = parse_query(query);
            let result = RedirectResult {
                code: params.get("code").cloned(),
                state: params.get("state").cloned(),
                error: params.get("error").cloned(),
                iss: params.get("iss").cloned(),
            };

            let (status, body) = if result.error.is_none() {
                (
                    "200 OK",
                    "<html><head><title>Signed in</title></head>\
                     <body style=\"font-family:sans-serif\">\
                     <h2>Signed in</h2>\
                     <p>You can close this window and return to the application.</p>\
                     </body></html>",
                )
            } else {
                (
                    "400 Bad Request",
                    "<html><head><title>Sign-in failed</title></head>\
                     <body style=\"font-family:sans-serif\">\
                     <h2>Sign-in failed</h2>\
                     <p>An error was returned from the authorization server.</p>\
                     </body></html>",
                )
            };

            let response = format!(
                "HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                body.len()
            );
            let _ = writer.write_all(response.as_bytes()).await;
            return Ok(result);
        }
    }
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
    // Minimal percent-decoder.  Only decode ASCII percent-encoded sequences;
    // anything that fails to decode is passed through as-is.
    let mut out = String::with_capacity(s.len());
    let bytes = s.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let Ok(hex) = std::str::from_utf8(&bytes[i + 1..i + 3]) {
                if let Ok(byte) = u8::from_str_radix(hex, 16) {
                    out.push(byte as char);
                    i += 3;
                    continue;
                }
            }
        } else if bytes[i] == b'+' {
            out.push(' ');
            i += 1;
            continue;
        }
        out.push(bytes[i] as char);
        i += 1;
    }
    out
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

    #[test]
    fn parse_query_handles_percent_encoding() {
        let q = "code=abc%3D%3D&state=xy+z";
        let map = parse_query(q);
        assert_eq!(map.get("code").map(String::as_str), Some("abc=="));
        assert_eq!(map.get("state").map(String::as_str), Some("xy z"));
    }
}
