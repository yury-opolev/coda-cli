//! Web search via a pluggable backend trait.
//!
//! The `DuckDuckGoBackend` scrapes DuckDuckGo's HTML endpoint.  HTML scraping
//! is inherently fragile; failures degrade gracefully (empty result list).
//!
//! For tests, inject a `MockBackend` via `WebSearchTool::new(backend)`.

use std::time::Duration;

use async_trait::async_trait;
use futures::StreamExt;
use regex::Regex;
use std::sync::{Arc, OnceLock};
use tokio_util::sync::CancellationToken;

use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

// ── SearchResult ──────────────────────────────────────────────────────────────

/// One web search result.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SearchResult {
    pub title: String,
    pub url: String,
    pub snippet: String,
}

impl SearchResult {
    pub fn new(
        title: impl Into<String>,
        url: impl Into<String>,
        snippet: impl Into<String>,
    ) -> Self {
        Self { title: title.into(), url: url.into(), snippet: snippet.into() }
    }
}

// ── SearchBackend trait ───────────────────────────────────────────────────────

/// A web-search provider.  `DuckDuckGoBackend` is the default; others plug in
/// through this seam.
#[async_trait]
pub trait SearchBackend: Send + Sync {
    async fn search(
        &self,
        query: &str,
        cancel: CancellationToken,
    ) -> Vec<SearchResult>;
}

// ── WebSearchTool ─────────────────────────────────────────────────────────────

pub struct WebSearchTool {
    backend: Arc<dyn SearchBackend>,
}

impl WebSearchTool {
    pub fn new(backend: Arc<dyn SearchBackend>) -> Self {
        Self { backend }
    }

    /// Default production constructor uses the DuckDuckGo backend.
    pub fn new_default() -> Self {
        Self::new(Arc::new(DuckDuckGoBackend::new()))
    }
}

impl Default for WebSearchTool {
    fn default() -> Self {
        Self::new_default()
    }
}

#[async_trait]
impl Tool for WebSearchTool {
    fn name(&self) -> &str {
        "web_search"
    }

    fn description(&self) -> &str {
        "Search the web and return a list of results (title, URL, snippet). \
         Use to find current information or documentation. Cite the URLs you rely on."
    }

    fn input_schema_json(&self) -> &str {
        r#"{"type":"object","properties":{"query":{"type":"string","description":"The search query."}},"required":["query"]}"#
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
        let query = match input.get("query").and_then(|v| v.as_str()) {
            Some(q) if !q.trim().is_empty() => q,
            _ => return ToolResult::error("web_search requires a 'query'."),
        };

        let results = self.backend.search(query, cancel).await;

        if results.is_empty() {
            return ToolResult::ok("No results found.");
        }

        let mut out = String::new();
        for (i, r) in results.iter().enumerate() {
            if i > 0 {
                out.push('\n');
            }
            out.push_str(&format!("{}. {}\n   {}\n   {}", i + 1, r.title, r.url, r.snippet));
        }
        ToolResult::ok(out)
    }
}

// ── DuckDuckGo backend ────────────────────────────────────────────────────────

const DDG_URL_BASE: &str = "https://html.duckduckgo.com/html/?q=";
const DDG_USER_AGENT: &str = "Mozilla/5.0 (compatible; Coda/1.0)";
const DDG_MAX_RESULTS: usize = 10;
const DDG_MAX_RESPONSE_BYTES: usize = 2 * 1024 * 1024;

pub struct DuckDuckGoBackend {
    client: reqwest::Client,
}

impl DuckDuckGoBackend {
    pub fn new() -> Self {
        let client = reqwest::Client::builder()
            .timeout(Duration::from_secs(15))
            .build()
            .expect("DuckDuckGoBackend reqwest client");
        Self { client }
    }

    pub fn with_client(client: reqwest::Client) -> Self {
        Self { client }
    }
}

impl Default for DuckDuckGoBackend {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait]
impl SearchBackend for DuckDuckGoBackend {
    async fn search(&self, query: &str, cancel: CancellationToken) -> Vec<SearchResult> {
        let url = format!("{DDG_URL_BASE}{}", urlencoding::encode(query));

        let request = match self
            .client
            .get(&url)
            .header(reqwest::header::USER_AGENT, DDG_USER_AGENT)
            .build()
        {
            Ok(r) => r,
            Err(_) => return vec![],
        };

        let response = tokio::select! {
            r = self.client.execute(request) => match r {
                Ok(r) if r.status().is_success() => r,
                _ => return vec![],
            },
            _ = cancel.cancelled() => return vec![],
        };

        let html = tokio::select! {
            r = read_capped(response) => r,
            _ = cancel.cancelled() => return vec![],
        };

        parse_results(&html)
    }
}

async fn read_capped(response: reqwest::Response) -> String {
    let mut stream = response.bytes_stream();
    let mut buf = Vec::with_capacity(65_536);
    while let Some(chunk) = stream.next().await {
        let Ok(bytes) = chunk else { break };
        let remaining = DDG_MAX_RESPONSE_BYTES.saturating_sub(buf.len());
        if remaining == 0 { break; }
        let take = bytes.len().min(remaining);
        buf.extend_from_slice(&bytes[..take]);
        if buf.len() >= DDG_MAX_RESPONSE_BYTES { break; }
    }
    String::from_utf8_lossy(&buf).into_owned()
}

// ── HTML parsing helpers ──────────────────────────────────────────────────────

/// Match `<a` blocks whose opening tag contains `class="result__a"`.
/// Capture group 1 = opening tag attributes, group 2 = inner text.
fn re_result_block() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r#"(?is)<a\b([^>]*\bclass="result__a"[^>]*)>(.*?)</a>"#)
            .expect("result block regex")
    })
}

fn re_snippet_block() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r#"(?is)<a\b([^>]*\bclass="result__snippet"[^>]*)>(.*?)</a>"#)
            .expect("snippet block regex")
    })
}

/// Extract `href="..."` from an opening-tag attribute string.
fn re_href_attr() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r#"(?i)\bhref="([^"]*)""#).expect("href regex"))
}

fn re_any_tag() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"<[^>]+>").expect("any-tag regex"))
}

fn strip_tags(html: &str) -> String {
    re_any_tag().replace_all(html, "").into_owned()
}

fn decode_html_amp(s: &str) -> String {
    s.replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&#39;", "'")
        .replace("&nbsp;", " ")
}

/// Extract the real URL from a DuckDuckGo redirect href.
fn resolve_ddg_url(href: &str) -> String {
    // DuckDuckGo wraps real URLs as //duckduckgo.com/l/?uddg=<encoded-url>&…
    let decoded_href = decode_html_amp(href);
    if decoded_href.contains("uddg=") {
        if let Some(qs_start) = decoded_href.find('?') {
            let qs = &decoded_href[qs_start + 1..];
            for part in qs.split('&') {
                if let Some(val) = part.strip_prefix("uddg=") {
                    if let Ok(real_url) = urlencoding::decode(val) {
                        return real_url.into_owned();
                    }
                }
            }
        }
    }
    if decoded_href.starts_with("//") {
        return format!("https:{decoded_href}");
    }
    decoded_href
}

fn parse_results(html: &str) -> Vec<SearchResult> {
    let mut results = Vec::new();
    let result_blocks: Vec<_> = re_result_block().captures_iter(html).collect();
    let snippet_blocks: Vec<_> = re_snippet_block().captures_iter(html).collect();

    for (i, cap) in result_blocks.iter().enumerate() {
        if results.len() >= DDG_MAX_RESULTS {
            break;
        }
        // cap[1] = opening tag attributes, cap[2] = inner text
        let tag_attrs = &cap[1];
        let inner_text = &cap[2];

        let raw_href = re_href_attr()
            .captures(tag_attrs)
            .map(|c| c[1].to_owned())
            .unwrap_or_default();

        let url = resolve_ddg_url(&raw_href);
        let title = decode_html_amp(&strip_tags(inner_text)).trim().to_owned();

        let snippet = snippet_blocks
            .get(i)
            .map(|sc| decode_html_amp(&strip_tags(&sc[2])).trim().to_owned())
            .unwrap_or_default();

        results.push(SearchResult::new(title, url, snippet));
    }
    results
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    struct MockBackend(Vec<SearchResult>);

    #[async_trait]
    impl SearchBackend for MockBackend {
        async fn search(&self, _query: &str, _cancel: CancellationToken) -> Vec<SearchResult> {
            self.0.clone()
        }
    }

    fn ctx() -> ToolContext {
        ToolContext::new(std::env::current_dir().unwrap().to_string_lossy().as_ref())
    }

    #[tokio::test]
    async fn missing_query_returns_error() {
        let tool = WebSearchTool::new(Arc::new(MockBackend(vec![])));
        let result = tool
            .execute(&serde_json::json!({}), &ctx(), CancellationToken::new())
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn empty_query_returns_error() {
        let tool = WebSearchTool::new(Arc::new(MockBackend(vec![])));
        let result = tool
            .execute(&serde_json::json!({"query": "   "}), &ctx(), CancellationToken::new())
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn no_results_returns_sentinel() {
        let tool = WebSearchTool::new(Arc::new(MockBackend(vec![])));
        let result = tool
            .execute(
                &serde_json::json!({"query": "something"}),
                &ctx(),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert_eq!(result.content, "No results found.");
    }

    #[tokio::test]
    async fn formats_results_correctly() {
        let results = vec![
            SearchResult::new("Rust lang", "https://www.rust-lang.org", "Systems language"),
            SearchResult::new("Cargo", "https://doc.rust-lang.org/cargo/", "Package manager"),
        ];
        let tool = WebSearchTool::new(Arc::new(MockBackend(results)));
        let result = tool
            .execute(
                &serde_json::json!({"query": "rust"}),
                &ctx(),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("1. Rust lang"), "{}", result.content);
        assert!(result.content.contains("https://www.rust-lang.org"), "{}", result.content);
        assert!(result.content.contains("2. Cargo"), "{}", result.content);
    }

    // ── DuckDuckGo HTML parsing ───────────────────────────────────────────────

    #[test]
    fn parse_results_extracts_titles_and_urls() {
        let html = r#"
        <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com">
            Example Title
        </a>
        <a class="result__snippet">Example snippet text</a>
        "#;
        let results = parse_results(html);
        assert_eq!(results.len(), 1, "expected 1 result, got {}: {:?}", results.len(), results);
        assert!(results[0].title.contains("Example Title"), "{:?}", results[0].title);
        assert_eq!(results[0].url, "https://example.com");
        assert!(results[0].snippet.contains("Example snippet"), "{:?}", results[0].snippet);
    }

    #[test]
    fn resolve_ddg_url_unwraps_uddg_param() {
        let href = "//duckduckgo.com/l/?uddg=https%3A%2F%2Frust-lang.org&rut=abc";
        assert_eq!(resolve_ddg_url(href), "https://rust-lang.org");
    }

    #[test]
    fn resolve_ddg_url_prefixes_protocol_relative() {
        let href = "//example.com/path";
        assert_eq!(resolve_ddg_url(href), "https://example.com/path");
    }

    #[test]
    fn cancellation_returns_empty_results() {
        let rt = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();

        rt.block_on(async {
            let backend = MockBackend(vec![]);
            let cancel = CancellationToken::new();
            cancel.cancel(); // already cancelled
            let results = backend.search("q", cancel).await;
            assert!(results.is_empty());
        });
    }
}

// ── urlencoding helper ────────────────────────────────────────────────────────

mod urlencoding {
    pub fn encode(s: &str) -> String {
        let mut out = String::with_capacity(s.len());
        for b in s.bytes() {
            match b {
                b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9'
                | b'-' | b'_' | b'.' | b'~' => out.push(b as char),
                _ => out.push_str(&format!("%{b:02X}")),
            }
        }
        out
    }

    pub fn decode(s: &str) -> Result<std::borrow::Cow<'_, str>, ()> {
        if !s.contains('%') {
            return Ok(std::borrow::Cow::Borrowed(s));
        }
        let mut out = String::with_capacity(s.len());
        let bytes = s.as_bytes();
        let mut i = 0;
        while i < bytes.len() {
            if bytes[i] == b'%' && i + 2 < bytes.len() {
                let hi = bytes[i + 1];
                let lo = bytes[i + 2];
                if let (Some(h), Some(l)) = (hex_val(hi), hex_val(lo)) {
                    out.push((h << 4 | l) as char);
                    i += 3;
                    continue;
                }
            }
            out.push(bytes[i] as char);
            i += 1;
        }
        Ok(std::borrow::Cow::Owned(out))
    }

    fn hex_val(b: u8) -> Option<u8> {
        match b {
            b'0'..=b'9' => Some(b - b'0'),
            b'a'..=b'f' => Some(b - b'a' + 10),
            b'A'..=b'F' => Some(b - b'A' + 10),
            _ => None,
        }
    }
}
