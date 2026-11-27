//! Minimal raw-HTTP server for hermetic auth tests.

use std::sync::{Arc, Mutex};

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

/// A route handler: `(path, port) -> (status, json body)`.
pub(crate) type Handler = dyn Fn(&str, u16) -> (u16, String) + Send + Sync;

/// A parsed request as seen by a [`RawHandler`].
#[derive(Debug, Clone)]
pub(crate) struct TestRequest {
    pub method: String,
    pub path: String,
    /// Header names are lower-cased.
    pub headers: Vec<(String, String)>,
    pub body: String,
}

impl TestRequest {
    pub(crate) fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_str())
    }
}

/// A response produced by a [`RawHandler`]. `Content-Type: application/json`
/// is always sent.
#[derive(Debug, Clone)]
pub(crate) struct TestResponse {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    pub body: String,
}

impl TestResponse {
    pub(crate) fn json(status: u16, body: impl Into<String>) -> Self {
        Self { status, headers: Vec::new(), body: body.into() }
    }
}

/// A full-request handler: `(request, port) -> response`.
pub(crate) type RawHandler = dyn Fn(&TestRequest, u16) -> TestResponse + Send + Sync;

/// Spawn a loopback server that answers every request via `handler` and
/// return its port. The server runs until the test runtime shuts down.
pub(crate) async fn spawn_server(handler: Arc<Handler>) -> u16 {
    spawn_recording_server(handler).await.0
}

/// Requests seen by a recording server as `(path, body)` pairs.
pub(crate) type Requests = Arc<Mutex<Vec<(String, String)>>>;

/// Like [`spawn_server`], but also records every request's path and body.
pub(crate) async fn spawn_recording_server(handler: Arc<Handler>) -> (u16, Requests) {
    spawn_raw_server(Arc::new(move |req: &TestRequest, port| {
        let (status, body) = handler(&req.path, port);
        TestResponse::json(status, body)
    }))
    .await
}

/// Like [`spawn_recording_server`], but the handler sees the method, headers
/// and body and controls the response headers.
pub(crate) async fn spawn_raw_server(handler: Arc<RawHandler>) -> (u16, Requests) {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();
    let requests: Requests = Arc::default();
    let recorded = Arc::clone(&requests);
    tokio::spawn(async move {
        loop {
            let Ok((mut stream, _)) = listener.accept().await else { return };
            let handler = Arc::clone(&handler);
            let recorded = Arc::clone(&recorded);
            tokio::spawn(async move {
                let mut buf = Vec::new();
                let mut chunk = [0u8; 4096];
                let head_end = loop {
                    if let Some(i) = buf.windows(4).position(|w| w == b"\r\n\r\n") {
                        break i + 4;
                    }
                    match stream.read(&mut chunk).await {
                        Ok(0) | Err(_) => return,
                        Ok(n) => buf.extend_from_slice(&chunk[..n]),
                    }
                };
                let head = String::from_utf8_lossy(&buf[..head_end]).into_owned();
                let headers: Vec<(String, String)> = head
                    .lines()
                    .skip(1)
                    .filter_map(|l| l.split_once(':'))
                    .map(|(k, v)| (k.trim().to_ascii_lowercase(), v.trim().to_owned()))
                    .collect();
                let content_length = headers
                    .iter()
                    .find(|(k, _)| k == "content-length")
                    .and_then(|(_, v)| v.parse::<usize>().ok())
                    .unwrap_or(0);
                while buf.len() < head_end + content_length {
                    match stream.read(&mut chunk).await {
                        Ok(0) | Err(_) => break,
                        Ok(n) => buf.extend_from_slice(&chunk[..n]),
                    }
                }
                let body = String::from_utf8_lossy(&buf[head_end..]).into_owned();
                let mut request_line = head.lines().next().unwrap_or("").split_whitespace();
                let method = request_line.next().unwrap_or("GET").to_owned();
                let path = request_line
                    .next()
                    .unwrap_or("/")
                    .split('?')
                    .next()
                    .unwrap_or("/")
                    .to_owned();
                recorded
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .push((path.clone(), body.clone()));
                let request = TestRequest { method, path, headers, body };
                let response = handler(&request, port);
                let reason = if response.status < 400 { "OK" } else { "Error" };
                let mut out = format!(
                    "HTTP/1.1 {} {reason}\r\nContent-Type: application/json\r\n\
                     Content-Length: {}\r\nConnection: close\r\n",
                    response.status,
                    response.body.len()
                );
                for (k, v) in &response.headers {
                    out.push_str(&format!("{k}: {v}\r\n"));
                }
                out.push_str("\r\n");
                out.push_str(&response.body);
                let _ = stream.write_all(out.as_bytes()).await;
                let _ = stream.shutdown().await;
            });
        }
    });
    (port, requests)
}
