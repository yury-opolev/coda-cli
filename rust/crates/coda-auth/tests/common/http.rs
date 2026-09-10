//! A tiny local HTTP server and loopback helpers, shared by the login tests.
//!
//! Nothing here talks to a real provider: every endpoint is a socket bound on
//! `127.0.0.1:0` that answers a scripted list of bodies.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;
use tokio::sync::mpsc;

/// A local HTTP server answering a scripted list of `(status, body)` pairs;
/// the last one repeats.
pub struct FakeHttp {
    pub base_url: String,
    requests: mpsc::UnboundedReceiver<String>,
    hits: Arc<AtomicUsize>,
}

impl FakeHttp {
    pub async fn start(responses: Vec<(u16, String)>) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let port = listener.local_addr().expect("addr").port();
        let (tx, rx) = mpsc::unbounded_channel();
        let hits = Arc::new(AtomicUsize::new(0));
        let hits_task = Arc::clone(&hits);

        tokio::spawn(async move {
            let mut index = 0usize;
            loop {
                let Ok((mut socket, _)) = listener.accept().await else {
                    break;
                };
                let mut buf = vec![0u8; 8192];
                let read = socket.read(&mut buf).await.unwrap_or(0);
                let request = String::from_utf8_lossy(&buf[..read]).to_string();
                let _ = tx.send(request);
                hits_task.fetch_add(1, Ordering::SeqCst);

                let (status, payload) = responses
                    .get(index)
                    .cloned()
                    .unwrap_or_else(|| responses.last().cloned().expect("a response"));
                index += 1;

                let response = format!(
                    "HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{payload}",
                    payload.len()
                );
                let _ = socket.write_all(response.as_bytes()).await;
            }
        });

        Self { base_url: format!("http://127.0.0.1:{port}"), requests: rx, hits }
    }

    pub fn hits(&self) -> usize {
        self.hits.load(Ordering::SeqCst)
    }

    pub fn next_request(&mut self) -> Option<String> {
        self.requests.try_recv().ok()
    }
}

/// Performs a real HTTP GET against a loopback URL and returns the raw
/// response text.
pub async fn http_get(url: &str) -> String {
    let rest = url.strip_prefix("http://").expect("http url");
    let (authority, path) = rest.split_once('/').unwrap_or((rest, ""));
    let port = authority.rsplit_once(':').expect("port").1;
    let mut stream = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
        .await
        .expect("connect to loopback listener");
    let request = format!("GET /{path} HTTP/1.1\r\nHost: {authority}\r\nConnection: close\r\n\r\n");
    stream.write_all(request.as_bytes()).await.expect("write request");
    let mut response = String::new();
    let _ = stream.read_to_string(&mut response).await;
    response
}
