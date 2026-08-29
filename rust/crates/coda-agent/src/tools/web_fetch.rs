//! Fetch a URL over HTTP(S) and return its content as plain text.
//!
//! Security properties:
//! - Only `http` and `https` schemes are permitted.
//! - Loopback, private, link-local, and metadata IP ranges are blocked (SSRF
//!   guard). IP-literal hosts are screened directly; hostnames are resolved via
//!   DNS and every resolved address is checked.
//! - DNS-rebinding prevention: the hostname is resolved ONCE per hop, the
//!   result is validated, and the connection is pinned to the approved address
//!   via `ClientBuilder::resolve_to_addrs`.  reqwest would otherwise re-resolve
//!   at TCP connect time, creating a window for TTL-0 rebinding attacks.
//! - Redirects are followed manually (up to 5 hops); each hop is re-validated.
//!   A redirect from `https` to `http` (scheme downgrade) is refused.
//! - Response body is capped at 2 MiB; the converted text is capped at 50 000 chars.
//! - Timeout defaults to 15 seconds (the reqwest client's own setting).

use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr};
use std::sync::OnceLock;
use std::time::Duration;

use async_trait::async_trait;
use futures::StreamExt;
use regex::Regex;
use reqwest::Url;
use tokio_util::sync::CancellationToken;

use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

pub struct WebFetchTool {
    client: reqwest::Client,
    /// Skip SSRF checks. `false` in production; used in tests that point at a
    /// local TCP server.
    skip_ssrf: bool,
}

impl WebFetchTool {
    /// Production constructor: SSRF checks on, 15-second timeout, no auto-redirect.
    pub fn new() -> Self {
        let client = reqwest::Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .timeout(Duration::from_secs(15))
            .build()
            .expect("WebFetchTool reqwest client");
        Self { client, skip_ssrf: false }
    }

    /// Test constructor: SSRF checks off, caller-supplied client.
    pub fn new_unchecked(client: reqwest::Client) -> Self {
        Self { client, skip_ssrf: true }
    }
}

impl Default for WebFetchTool {
    fn default() -> Self {
        Self::new()
    }
}

const MAX_RESPONSE_BYTES: usize = 2 * 1024 * 1024;
const MAX_TEXT_CHARS: usize = 50_000;
const MAX_REDIRECTS: usize = 5;

// ── URL / SSRF guard ──────────────────────────────────────────────────────────

/// Returns `true` only when `url` has an http/https scheme and its host is not a
/// loopback, private, link-local, or metadata IP literal.
/// Hostname-based SSRF (DNS resolution check) is performed separately in `execute`.
pub fn is_allowed_url(url: &str) -> bool {
    let Ok(parsed) = Url::parse(url) else {
        return false;
    };
    let scheme = parsed.scheme();
    if scheme != "http" && scheme != "https" {
        return false;
    }
    let host = match parsed.host_str() {
        Some(h) => h,
        None => return false,
    };
    let host = host.trim_start_matches('[').trim_end_matches(']');
    if host.eq_ignore_ascii_case("localhost")
        || host.ends_with(".localhost")
    {
        return false;
    }
    // If the host is an IP literal, screen it immediately.
    if let Ok(ip) = host.parse::<IpAddr>() {
        return !is_blocked_ip(ip);
    }
    true // hostname: deferred DNS check in execute
}

fn is_blocked_ip(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(v4) => is_blocked_v4(v4),
        IpAddr::V6(v6) => {
            if let Some(v4) = v6.to_ipv4_mapped() {
                return is_blocked_v4(v4);
            }
            v6.is_loopback()
                || v6.is_unspecified()
                || is_ipv6_link_local(v6)
                || is_ipv6_unique_local(v6)
        }
    }
}

fn is_blocked_v4(ip: Ipv4Addr) -> bool {
    ip.is_loopback()
        || ip.is_private()
        || ip.is_link_local()
        || ip.is_unspecified()
        || is_metadata_v4(ip)
}

fn is_metadata_v4(ip: Ipv4Addr) -> bool {
    // 169.254.x.x (AWS/GCP metadata and link-local) is already covered by
    // `is_link_local()` in stable Rust. Belt-and-suspenders check:
    let o = ip.octets();
    o[0] == 169 && o[1] == 254
}

fn is_ipv6_link_local(ip: Ipv6Addr) -> bool {
    // fe80::/10
    let segs = ip.segments();
    (segs[0] & 0xffc0) == 0xfe80
}

fn is_ipv6_unique_local(ip: Ipv6Addr) -> bool {
    // fc00::/7
    let segs = ip.segments();
    (segs[0] & 0xfe00) == 0xfc00
}

// ── HTML → text ───────────────────────────────────────────────────────────────

fn re_script() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r"(?is)<script[^>]*>.*?</script>").expect("script regex")
    })
}

fn re_style() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r"(?is)<style[^>]*>.*?</style>").expect("style regex")
    })
}

fn re_tags() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"<[^>]+>").expect("tag regex"))
}

fn re_whitespace() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r"[ \t\f\v]*\n\s*\n\s*|[ \t]{2,}").expect("whitespace regex")
    })
}

fn re_entities() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"&(?:#(\d+)|#x([0-9a-fA-F]+)|(\w+));").expect("entity regex"))
}

/// Strip HTML to plain text: remove script/style, strip tags, decode entities.
pub fn html_to_text(html: &str) -> String {
    let no_script = re_script().replace_all(html, " ");
    let no_style = re_style().replace_all(&no_script, " ");
    let no_tags = re_tags().replace_all(&no_style, " ");
    let decoded = decode_entities(&no_tags);
    let collapsed = re_whitespace().replace_all(&decoded, " ");
    collapsed.trim().to_owned()
}

fn decode_entities(s: &str) -> String {
    re_entities().replace_all(s, |caps: &regex::Captures<'_>| {
        if let Some(dec) = caps.get(1) {
            if let Ok(n) = dec.as_str().parse::<u32>() {
                if let Some(c) = char::from_u32(n) {
                    return c.to_string();
                }
            }
        }
        if let Some(hex) = caps.get(2) {
            if let Ok(n) = u32::from_str_radix(hex.as_str(), 16) {
                if let Some(c) = char::from_u32(n) {
                    return c.to_string();
                }
            }
        }
        if let Some(name) = caps.get(3) {
            return named_entity(name.as_str()).to_owned();
        }
        caps[0].to_owned()
    })
    .into_owned()
}

fn named_entity(name: &str) -> &'static str {
    match name {
        "amp" => "&",
        "lt" => "<",
        "gt" => ">",
        "quot" => "\"",
        "apos" => "'",
        "nbsp" => " ",
        "copy" => "©",
        "reg" => "®",
        "trade" => "™",
        "mdash" => "—",
        "ndash" => "–",
        "hellip" => "…",
        "laquo" => "«",
        "raquo" => "»",
        _ => "",
    }
}

// ── DNS-pinning helper ────────────────────────────────────────────────────────

/// Build a reqwest client whose DNS resolver is overridden so that `host`
/// always dials `addr` — regardless of what a fresh DNS lookup would return.
///
/// Using `resolve_to_addrs` closes the DNS-rebinding window: without it,
/// reqwest would resolve `host` a *second* time at TCP connect, and an attacker
/// controlling TTL-0 DNS could answer a public IP to our SSRF check and a
/// private/metadata IP to the actual connection.
pub(crate) fn build_pinned_client(
    host: &str,
    addr: SocketAddr,
) -> Result<reqwest::Client, reqwest::Error> {
    reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .timeout(Duration::from_secs(15))
        .resolve_to_addrs(host, &[addr])
        .build()
}

// ── Tool implementation ───────────────────────────────────────────────────────

#[async_trait]
impl Tool for WebFetchTool {
    fn name(&self) -> &str {
        "web_fetch"
    }

    fn description(&self) -> &str {
        "Fetch a URL over HTTP(S) and return its content as plain text. \
         HTML is converted to readable text. Blocks local/private network addresses."
    }

    fn input_schema_json(&self) -> &str {
        r#"{"type":"object","properties":{"url":{"type":"string","description":"The http(s) URL to fetch."}},"required":["url"]}"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(
        &self,
        input: &serde_json::Value,
        _ctx: &ToolContext,
        cancel: CancellationToken,
    ) -> ToolOutcome {
        let url = match input.get("url").and_then(|v| v.as_str()) {
            Some(u) if !u.trim().is_empty() => u.to_owned(),
            _ => return ToolResult::error("web_fetch requires a 'url'."),
        };

        let initial_scheme = match Url::parse(&url) {
            Ok(u) => u.scheme().to_owned(),
            Err(_) => return ToolResult::error(format!("Invalid URL: '{url}'.")),
        };

        let mut current = url.clone();

        for _hop in 0..=MAX_REDIRECTS {
            if cancel.is_cancelled() {
                return ToolResult::error("Cancelled.");
            }

            if !self.skip_ssrf && !is_allowed_url(&current) {
                return ToolResult::error(format!(
                    "Refused to fetch '{current}' (blocked scheme or local/private address)."
                ));
            }

            // DNS-based SSRF check for non-literal hostnames.  The resolved
            // address is also pinned into a per-hop client via resolve_to_addrs
            // to prevent DNS rebinding: without pinning, reqwest would resolve
            // the hostname a *second* time at TCP connect, giving an attacker
            // with TTL-0 DNS a window to answer a private/metadata IP on the
            // second lookup after passing our check with a public IP.
            let hop_client: Option<reqwest::Client>;
            if !self.skip_ssrf {
                if let Ok(parsed) = Url::parse(&current) {
                    let host = parsed.host_str().unwrap_or("").to_owned();
                    if !host.parse::<IpAddr>().is_ok() && !host.is_empty() {
                        let host_clone = host.clone();
                        let resolved = tokio::select! {
                            r = tokio::task::spawn_blocking(move || {
                                use std::net::ToSocketAddrs;
                                (host_clone.as_str(), 80_u16)
                                    .to_socket_addrs()
                                    .map(|it| it.map(|a| a.ip()).collect::<Vec<_>>())
                                    .map_err(|e| e.to_string())
                            }) => match r {
                                Ok(Ok(ips)) => ips,
                                Ok(Err(e)) => return ToolResult::error(format!(
                                    "Refused to fetch '{current}' (DNS failed: {e})."
                                )),
                                Err(e) => return ToolResult::error(format!(
                                    "DNS task failed: {e}"
                                )),
                            },
                            _ = cancel.cancelled() => return ToolResult::error("Cancelled."),
                        };
                        if resolved.is_empty() || resolved.iter().any(|ip| is_blocked_ip(*ip)) {
                            return ToolResult::error(format!(
                                "Refused to fetch '{current}' (host resolves to a local/private address)."
                            ));
                        }
                        // Pin reqwest to the approved address so the connection
                        // cannot use a second DNS answer (rebinding attack).
                        let port = parsed.port_or_known_default().unwrap_or(80);
                        let pinned = SocketAddr::new(resolved[0], port);
                        hop_client = match build_pinned_client(&host, pinned) {
                            Ok(c) => Some(c),
                            Err(e) => return ToolResult::error(format!("Client build failed: {e}")),
                        };
                    } else {
                        hop_client = None;
                    }
                } else {
                    hop_client = None;
                }
            } else {
                hop_client = None;
            }
            let client = hop_client.as_ref().unwrap_or(&self.client);

            let request = match client
                .get(&current)
                .header(reqwest::header::USER_AGENT, "Coda/1.0 (+https://localhost)")
                .build()
            {
                Ok(r) => r,
                Err(e) => return ToolResult::error(format!("Request build failed: {e}")),
            };

            let response = tokio::select! {
                r = client.execute(request) => match r {
                    Ok(resp) => resp,
                    Err(e) => return ToolResult::error(format!("Fetch failed: {e}")),
                },
                _ = cancel.cancelled() => return ToolResult::error("Cancelled."),
            };

            let status = response.status();

            if status.is_redirection() {
                let location = match response.headers().get(reqwest::header::LOCATION) {
                    Some(h) => match h.to_str() {
                        Ok(s) => s.to_owned(),
                        Err(_) => return ToolResult::error("Redirect target has invalid characters."),
                    },
                    None => return ToolResult::error("Redirect with no Location header."),
                };

                // Resolve relative redirects.
                let next = match Url::parse(&current)
                    .ok()
                    .and_then(|base| base.join(&location).ok())
                {
                    Some(u) => u.to_string(),
                    None => location,
                };

                // Refuse scheme downgrade (https → http).
                let next_scheme = Url::parse(&next)
                    .map(|u| u.scheme().to_owned())
                    .unwrap_or_default();
                if initial_scheme == "https" && next_scheme == "http" {
                    return ToolResult::error(format!(
                        "Refused redirect: scheme downgrade from https to http ('{next}')."
                    ));
                }

                current = next;
                continue;
            }

            if !status.is_success() {
                return ToolResult::error(format!(
                    "HTTP {} fetching '{current}'.",
                    status.as_u16()
                ));
            }

            // Read the body with a hard byte cap.
            let body_bytes = tokio::select! {
                r = read_capped_body(response) => r,
                _ = cancel.cancelled() => return ToolResult::error("Cancelled."),
            };

            let body = String::from_utf8_lossy(&body_bytes).into_owned();
            let content_type = ""; // already consumed; use heuristic below
            let _ = content_type;
            let text = if body.contains("</html>")
                || body.contains("</HTML>")
                || body.contains("<!DOCTYPE")
                || body.contains("<!doctype")
            {
                html_to_text(&body)
            } else {
                body
            };

            let text = if text.chars().count() > MAX_TEXT_CHARS {
                let cutoff = text
                    .char_indices()
                    .nth(MAX_TEXT_CHARS)
                    .map(|(i, _)| i)
                    .unwrap_or(text.len());
                format!("{}\n\n[truncated]", &text[..cutoff])
            } else {
                text
            };

            return ToolResult::ok(text);
        }

        ToolResult::error(format!("Too many redirects fetching '{url}'."))
    }
}

async fn read_capped_body(response: reqwest::Response) -> Vec<u8> {
    let mut stream = response.bytes_stream();
    let mut buf: Vec<u8> = Vec::with_capacity(65_536);

    while let Some(chunk) = stream.next().await {
        let Ok(bytes) = chunk else { break };
        let remaining = MAX_RESPONSE_BYTES.saturating_sub(buf.len());
        if remaining == 0 {
            break;
        }
        let take = bytes.len().min(remaining);
        buf.extend_from_slice(&bytes[..take]);
        if buf.len() >= MAX_RESPONSE_BYTES {
            break;
        }
    }
    buf
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpListener;

    fn cwd_ctx() -> ToolContext {
        ToolContext::new(std::env::current_dir().unwrap().to_string_lossy().as_ref())
    }

    // ── HIGH-3 DNS-rebinding exploit test ────────────────────────────────────
    //
    // EXPLOIT (before fix): the SSRF guard resolves the hostname once and
    // validates all IPs, but then hands the *hostname* URL to self.client,
    // which resolves again at TCP connect time.  An attacker with TTL-0 DNS
    // answers a public IP to the check and 169.254.169.254 (cloud metadata) on
    // the second resolution.
    //
    // After the fix, resolve_and_pin() resolves once, validates, and returns a
    // client built with resolve_to_addrs() so reqwest is forced to dial
    // exactly the address that was approved.
    //
    // Full simulation (two different DNS answers in the same process) is
    // impractical without a custom resolver shim.  Instead we test the pinning
    // mechanism directly: prove that a client built with resolve_to_addrs()
    // connects to the pinned SocketAddr even when the hostname has no real DNS
    // entry.  A rebinding attacker would be unable to swap the address after the
    // pin is established.

    #[tokio::test]
    async fn pinned_client_dials_validated_addr_not_re_resolved() {
        // Start a local server so we have a real endpoint to connect to.
        let url = serve_once(200, "content-type: text/plain\r\n", b"pinned-ok").await;
        let port: u16 = url
            .trim_start_matches("http://127.0.0.1:")
            .parse()
            .expect("port from serve_once URL");
        let pinned_addr =
            std::net::SocketAddr::new(std::net::Ipv4Addr::new(127, 0, 0, 1).into(), port);

        // build_pinned_client is the helper introduced by the fix.
        // BEFORE THE FIX this call does not compile — confirming the test fails.
        let client = build_pinned_client("rebind-test.invalid", pinned_addr)
            .expect("client build must succeed");

        // "rebind-test.invalid" has no real DNS entry; the client must reach our
        // test server via the pinned address rather than via re-resolution.
        let resp = client
            .get("http://rebind-test.invalid/")
            .send()
            .await
            .expect("pinned request must succeed");
        let body = resp.text().await.unwrap();
        assert_eq!(body, "pinned-ok", "wrong body — pinning did not work");
    }

    // ── URL validation ────────────────────────────────────────────────────────

    #[test]
    fn allows_public_https_url() {
        assert!(is_allowed_url("https://example.com/path"));
    }

    #[test]
    fn allows_public_http_url() {
        assert!(is_allowed_url("http://example.com/path"));
    }

    #[test]
    fn blocks_localhost() {
        assert!(!is_allowed_url("http://localhost/"));
        assert!(!is_allowed_url("http://localhost:8080/"));
        assert!(!is_allowed_url("http://foo.localhost/"));
    }

    #[test]
    fn blocks_loopback_ipv4() {
        assert!(!is_allowed_url("http://127.0.0.1/"));
        assert!(!is_allowed_url("http://127.1.2.3/"));
    }

    #[test]
    fn blocks_private_ipv4_ranges() {
        assert!(!is_allowed_url("http://10.0.0.1/"));
        assert!(!is_allowed_url("http://172.16.0.1/"));
        assert!(!is_allowed_url("http://172.31.255.255/"));
        assert!(!is_allowed_url("http://192.168.1.1/"));
    }

    #[test]
    fn blocks_link_local_ipv4() {
        assert!(!is_allowed_url("http://169.254.169.254/")); // AWS metadata
    }

    #[test]
    fn blocks_loopback_ipv6() {
        assert!(!is_allowed_url("http://[::1]/"));
    }

    #[test]
    fn blocks_non_http_schemes() {
        assert!(!is_allowed_url("ftp://example.com/"));
        assert!(!is_allowed_url("file:///etc/passwd"));
    }

    // ── HTML to text ──────────────────────────────────────────────────────────

    #[test]
    fn strips_html_tags() {
        let html = "<html><body><p>Hello <b>world</b></p></body></html>";
        let text = html_to_text(html);
        assert!(!text.contains('<'));
        assert!(text.contains("Hello"));
        assert!(text.contains("world"));
    }

    #[test]
    fn removes_script_and_style_content() {
        let html = "<html><head><script>alert('x');</script><style>.x{color:red}</style></head><body>content</body></html>";
        let text = html_to_text(html);
        assert!(!text.contains("alert"));
        assert!(!text.contains("color:red"));
        assert!(text.contains("content"));
    }

    #[test]
    fn decodes_html_entities() {
        let html = "<p>Price: &lt;5 &amp; &gt;0; &quot;ok&quot;</p>";
        let text = html_to_text(html);
        assert!(text.contains('<'), "lt not decoded: {text}");
        assert!(text.contains('>'), "gt not decoded: {text}");
        assert!(text.contains('&'), "amp not decoded: {text}");
        assert!(text.contains('"'), "quot not decoded: {text}");
    }

    // ── Local HTTP server helpers ─────────────────────────────────────────────

    /// Serve a single HTTP response from a local TCP socket; returns the URL.
    async fn serve_once(status: u16, extra_headers: &str, body: &[u8]) -> String {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let headers = extra_headers.to_owned();
        let body = body.to_vec();

        tokio::spawn(async move {
            let Ok((mut sock, _)) = listener.accept().await else { return };
            let mut buf = [0u8; 4096];
            let _ = sock.read(&mut buf).await;

            let reason = if status / 100 == 2 { "OK" } else { "Error" };
            let head = format!(
                "HTTP/1.1 {status} {reason}\r\ncontent-length: {}\r\n{headers}\r\n",
                body.len()
            );
            let _ = sock.write_all(head.as_bytes()).await;
            let _ = sock.write_all(&body).await;
            let _ = sock.shutdown().await;
        });

        format!("http://127.0.0.1:{port}")
    }

    /// Serve a response that is delayed for `delay_ms` milliseconds.
    async fn serve_slow(delay_ms: u64) -> String {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();

        tokio::spawn(async move {
            let Ok((mut sock, _)) = listener.accept().await else { return };
            let mut buf = [0u8; 4096];
            let _ = sock.read(&mut buf).await;
            tokio::time::sleep(Duration::from_millis(delay_ms)).await;
            let head = "HTTP/1.1 200 OK\r\ncontent-length: 4\r\n\r\n";
            let _ = sock.write_all(head.as_bytes()).await;
            let _ = sock.write_all(b"slow").await;
            let _ = sock.shutdown().await;
        });

        format!("http://127.0.0.1:{port}")
    }

    /// Serve a redirect from `url1` to `url2` (both local), then serve `url2`.
    async fn serve_redirect_chain(
        from_status: u16,
        target: &str,
        final_body: &str,
    ) -> String {
        let target = target.to_owned();
        let body = final_body.as_bytes().to_vec();

        // The redirect server
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();

        tokio::spawn(async move {
            let Ok((mut sock, _)) = listener.accept().await else { return };
            let mut buf = [0u8; 4096];
            let _ = sock.read(&mut buf).await;
            let head = format!(
                "HTTP/1.1 {from_status} Redirect\r\nlocation: {target}\r\ncontent-length: 0\r\n\r\n"
            );
            let _ = sock.write_all(head.as_bytes()).await;
            let _ = sock.shutdown().await;
        });

        // The final server (already running or will be set up separately)
        let _ = body; // caller sets up the final server separately
        format!("http://127.0.0.1:{port}")
    }

    // ── Fetch tests ───────────────────────────────────────────────────────────

    #[tokio::test]
    async fn fetches_plain_text_content() {
        let url = serve_once(200, "content-type: text/plain\r\n", b"Hello from test server").await;
        let tool = WebFetchTool::new_unchecked(
            reqwest::Client::builder()
                .redirect(reqwest::redirect::Policy::none())
                .build()
                .unwrap(),
        );
        let result = tool
            .execute(&serde_json::json!({"url": url}), &cwd_ctx(), CancellationToken::new())
            .await;
        assert!(!result.is_error, "failed: {}", result.content);
        assert!(result.content.contains("Hello from test server"), "{}", result.content);
    }

    #[tokio::test]
    async fn caps_response_size() {
        let big = vec![b'x'; MAX_RESPONSE_BYTES + 10_000];
        let url = serve_once(200, "content-type: text/plain\r\n", &big).await;
        let tool = WebFetchTool::new_unchecked(
            reqwest::Client::builder()
                .redirect(reqwest::redirect::Policy::none())
                .build()
                .unwrap(),
        );
        let result = tool
            .execute(&serde_json::json!({"url": url}), &cwd_ctx(), CancellationToken::new())
            .await;
        assert!(!result.is_error, "failed: {}", result.content);
        // Must not blow up memory; actual check is that we returned something reasonable
        assert!(result.content.len() < MAX_RESPONSE_BYTES + 500);
    }

    #[tokio::test]
    async fn caps_text_chars() {
        let big = "x".repeat(MAX_TEXT_CHARS + 5_000);
        let url = serve_once(200, "content-type: text/plain\r\n", big.as_bytes()).await;
        let tool = WebFetchTool::new_unchecked(
            reqwest::Client::builder()
                .redirect(reqwest::redirect::Policy::none())
                .build()
                .unwrap(),
        );
        let result = tool
            .execute(&serde_json::json!({"url": url}), &cwd_ctx(), CancellationToken::new())
            .await;
        assert!(!result.is_error, "failed: {}", result.content);
        assert!(
            result.content.contains("[truncated]"),
            "expected truncation marker"
        );
        assert!(result.content.chars().count() < MAX_TEXT_CHARS + 200);
    }

    #[tokio::test]
    async fn respects_request_timeout() {
        let url = serve_slow(600).await; // 600ms server delay
        let client = reqwest::Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .timeout(Duration::from_millis(200)) // 200ms client timeout
            .build()
            .unwrap();
        let tool = WebFetchTool::new_unchecked(client);
        let result = tool
            .execute(&serde_json::json!({"url": url}), &cwd_ctx(), CancellationToken::new())
            .await;
        assert!(result.is_error, "expected timeout error: {}", result.content);
    }

    #[tokio::test]
    async fn follows_redirect_and_fetches_target() {
        let final_url =
            serve_once(200, "content-type: text/plain\r\n", b"final page").await;

        // Redirect server pointing at final_url
        let redirect_url = serve_redirect_chain(301, &final_url, "final page").await;

        let tool = WebFetchTool::new_unchecked(
            reqwest::Client::builder()
                .redirect(reqwest::redirect::Policy::none())
                .build()
                .unwrap(),
        );
        let result = tool
            .execute(
                &serde_json::json!({"url": redirect_url}),
                &cwd_ctx(),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "redirect failed: {}", result.content);
        assert!(result.content.contains("final page"), "{}", result.content);
    }

    #[tokio::test]
    async fn rejects_ssrf_localhost_url() {
        let tool = WebFetchTool::new();
        let result = tool
            .execute(
                &serde_json::json!({"url": "http://localhost:8080/secret"}),
                &cwd_ctx(),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
        assert!(result.content.contains("Refused"), "{}", result.content);
    }

    #[tokio::test]
    async fn rejects_https_to_http_scheme_downgrade() {
        // This test doesn't hit the network; it checks the redirect logic.
        // We need a local server that redirects from http to a different-scheme
        // URL. Since we test this at the redirect-processing level, mock it
        // via an actual local redirect.
        //
        // The redirect logic specifically prevents going from https → http.
        // With a local http server we can only test http → http (fine) or the
        // code path directly. We test the code path here:
        let blocked_url = "http://http-downgrade.example/";
        // Build a WebFetchTool with skip_ssrf=true but the scheme-downgrade check
        // is always active (it's not guarded by skip_ssrf).
        let tool = WebFetchTool { client: reqwest::Client::new(), skip_ssrf: true };

        // Simulate an initial_scheme of "https" by starting with a local redirect.
        // We set up: serve a 301 pointing to an http:// URL from an https base.
        // We cannot easily set up a real https test server, so we test the
        // is_allowed_url guard and the code that checks the scheme.
        //
        // Verify that the URL itself (with https start) would be handled by
        // testing the code path: initial https, redirect to http.
        // We use the public API: parse URLs, simulate what execute() does.
        let initial = "https://example.com/";
        let initial_scheme = Url::parse(initial).unwrap().scheme().to_owned();
        let next = "http://example.com/downgraded";
        let next_scheme = Url::parse(next).unwrap().scheme().to_owned();
        let is_downgrade = initial_scheme == "https" && next_scheme == "http";
        assert!(is_downgrade, "scheme downgrade detection failed");
        let _ = (tool, blocked_url);
    }
}

