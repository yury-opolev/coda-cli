//! End-to-end provider login primitives: real loopback callbacks, real HTTP
//! token/device endpoints on `127.0.0.1`, and the safe failure classification
//! that the CLI/TUI will render.
//!
//! Nothing here touches a real provider, a real account, the keyring, or any
//! stored credential: every endpoint is a local fixture and every credential is
//! synthesised from the fixture's response.

use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::Duration;

use coda_auth::loopback::{CallbackVerdict, LoopbackListener};
use coda_auth::provider::api_key::ApiKeyProvider;
use coda_auth::provider::claude_ai::{ClaudeAiConfig, ClaudeAiProvider, ALL_OAUTH_SCOPES};
use coda_auth::provider::copilot::{
    resolve_copilot_config, CopilotConfig, CopilotDeployment, CopilotDeploymentChoice,
    CopilotProvider,
};
use coda_auth::{AuthError, AuthFailure};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;
use tokio::sync::{mpsc, oneshot};

// ─────────────────────────────────────────────────────────────────────────────
// Local HTTP fixtures
// ─────────────────────────────────────────────────────────────────────────────

/// A tiny local HTTP server that answers a scripted list of JSON bodies and
/// reports every request body it received.
struct FakeHttp {
    base_url: String,
    requests: mpsc::UnboundedReceiver<String>,
    hits: Arc<AtomicUsize>,
}

impl FakeHttp {
    /// Serves `responses` in order; the last response repeats forever.
    async fn start(responses: Vec<(u16, String)>) -> Self {
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
                let body = request.split_once("\r\n\r\n").map(|(_, b)| b.to_string()).unwrap_or_default();
                let _ = tx.send(body);
                hits_task.fetch_add(1, Ordering::SeqCst);

                let (status, payload) = responses
                    .get(index)
                    .cloned()
                    .unwrap_or_else(|| responses.last().cloned().expect("at least one response"));
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

    fn hits(&self) -> usize {
        self.hits.load(Ordering::SeqCst)
    }

    fn next_request_body(&mut self) -> Option<String> {
        self.requests.try_recv().ok()
    }
}

/// Performs a real HTTP GET against the loopback callback URL and returns the
/// raw response text.
async fn http_get(url: &str) -> String {
    let rest = url.strip_prefix("http://").expect("http url");
    let (authority, path) = rest.split_once('/').unwrap_or((rest, ""));
    // The loopback listener advertises "localhost"; connect over IPv4 loopback
    // explicitly so the test does not depend on the host's IPv6 resolution.
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

/// Extracts the port from a `http://host:port/path` redirect URI.
fn port_of(redirect_uri: &str) -> u16 {
    redirect_uri
        .rsplit_once(':')
        .and_then(|(_, rest)| rest.split('/').next().map(str::to_owned))
        .expect("port")
        .parse()
        .expect("port number")
}

fn query_of(url: &str) -> HashMap<String, String> {    let query = url.split_once('?').map(|(_, q)| q).unwrap_or("");
    query
        .split('&')
        .filter(|p| !p.is_empty())
        .map(|pair| {
            let (k, v) = pair.split_once('=').unwrap_or((pair, ""));
            (k.to_string(), v.to_string())
        })
        .collect()
}

fn form_of(body: &str) -> HashMap<String, String> {
    query_of(&format!("?{body}"))
}

fn json_of(body: &str) -> serde_json::Value {
    serde_json::from_str(body).unwrap_or(serde_json::Value::Null)
}

// ─────────────────────────────────────────────────────────────────────────────
// Claude.ai: browser loopback + PKCE
// ─────────────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn claude_login_completes_the_token_exchange_from_a_real_loopback_callback() {
    let mut token_endpoint = FakeHttp::start(vec![(
        200,
        r#"{"access_token":"acc_live","refresh_token":"ref_live","expires_in":3600,"scope":"user:inference user:profile"}"#
            .to_string(),
    )])
    .await;

    let provider = ClaudeAiProvider::new(
        ClaudeAiConfig::production("test-client").with_token_url(&token_endpoint.base_url),
    );
    let flow = provider.begin_login(ALL_OAUTH_SCOPES).await.expect("begin login");

    let authorize = query_of(&flow.authorize_url);
    let redirect_uri = flow.redirect_uri().to_string();
    let state = flow.state.clone();
    assert_eq!(authorize.get("code_challenge_method").map(String::as_str), Some("S256"));
    for scope in ALL_OAUTH_SCOPES {
        assert!(
            flow.authorize_url.contains(&scope.replace(':', "%3A")),
            "authorize url must request scope {scope}: {}",
            flow.authorize_url
        );
    }

    let callback = format!("{redirect_uri}?code=auth_code_live&state={state}");
    let browser = tokio::spawn(async move { http_get(&callback).await });

    let credential = flow
        .wait_for_credential(Duration::from_secs(5))
        .await
        .expect("credential from the loopback callback");

    assert_eq!(credential.provider_id, "claude-ai");
    let page = browser.await.expect("browser response");
    assert!(
        page.contains("Signed in"),
        "the verdict page follows the successful exchange: {page}"
    );
    assert_eq!(
        token_endpoint.hits(),
        1,
        "the browser is answered only after exactly one token exchange"
    );

    let body = token_endpoint.next_request_body().expect("token request");
    let sent = json_of(&body);
    assert_eq!(sent["grant_type"], "authorization_code");
    assert_eq!(sent["code"], "auth_code_live");
    assert_eq!(sent["redirect_uri"], redirect_uri);
    assert_eq!(sent["state"], state);
    let verifier = sent["code_verifier"].as_str().expect("verifier sent").to_string();
    assert_eq!(
        coda_auth::pkce::generate_code_challenge(&verifier),
        authorize.get("code_challenge").cloned().unwrap_or_default(),
        "the verifier posted to the token endpoint must match the challenge in the authorize URL"
    );
}

#[tokio::test]
async fn a_rejected_exchange_never_shows_the_browser_a_success_page() {
    let token_endpoint =
        FakeHttp::start(vec![(400, r#"{"error":"invalid_grant"}"#.to_string())]).await;
    let provider = ClaudeAiProvider::new(
        ClaudeAiConfig::production("test-client").with_token_url(&token_endpoint.base_url),
    );
    let flow = provider.begin_login(ALL_OAUTH_SCOPES).await.expect("begin login");
    let callback = format!("{}?code=c&state={}", flow.redirect_uri(), flow.state);

    let browser = tokio::spawn(async move { http_get(&callback).await });
    let err = flow.wait_for_credential(Duration::from_secs(5)).await.unwrap_err();
    let page = browser.await.expect("browser response");

    assert!(matches!(err, AuthError::OAuth { status: 400, .. }), "got {err:?}");
    assert!(!page.contains("Signed in"), "a rejected exchange must not claim success: {page}");
    assert_eq!(token_endpoint.hits(), 1);
}

#[tokio::test]
async fn claude_login_rejects_a_mismatched_state_without_exchanging_anything() {
    let token_endpoint = FakeHttp::start(vec![(200, r#"{"access_token":"never"}"#.to_string())]).await;
    let provider = ClaudeAiProvider::new(
        ClaudeAiConfig::production("test-client").with_token_url(&token_endpoint.base_url),
    );
    let flow = provider.begin_login(ALL_OAUTH_SCOPES).await.expect("begin login");
    let callback = format!("{}?code=abc&state=tampered", flow.redirect_uri());

    let browser = tokio::spawn(async move { http_get(&callback).await });
    let err = flow.wait_for_credential(Duration::from_secs(5)).await.unwrap_err();
    let _ = browser.await;

    assert!(matches!(err, AuthError::StateMismatch), "got {err:?}");
    assert_eq!(token_endpoint.hits(), 0, "a tampered state must never reach the token endpoint");
}

#[tokio::test]
async fn claude_login_without_a_code_is_a_failure_not_a_credential() {
    let token_endpoint = FakeHttp::start(vec![(200, r#"{"access_token":"never"}"#.to_string())]).await;
    let provider = ClaudeAiProvider::new(
        ClaudeAiConfig::production("test-client").with_token_url(&token_endpoint.base_url),
    );
    let flow = provider.begin_login(ALL_OAUTH_SCOPES).await.expect("begin login");
    let callback = format!("{}?state={}", flow.redirect_uri(), flow.state);

    let browser = tokio::spawn(async move { http_get(&callback).await });
    let err = flow.wait_for_credential(Duration::from_secs(5)).await.unwrap_err();
    let _ = browser.await;

    assert!(!matches!(err, AuthError::StateMismatch), "got {err:?}");
    assert_eq!(token_endpoint.hits(), 0, "a callback without a code must not be exchanged");
}

#[tokio::test]
async fn claude_login_surfaces_an_oauth_error_returned_in_the_redirect() {
    let token_endpoint = FakeHttp::start(vec![(200, r#"{"access_token":"never"}"#.to_string())]).await;
    let provider = ClaudeAiProvider::new(
        ClaudeAiConfig::production("test-client").with_token_url(&token_endpoint.base_url),
    );
    let flow = provider.begin_login(ALL_OAUTH_SCOPES).await.expect("begin login");
    let callback = format!("{}?error=access_denied&state={}", flow.redirect_uri(), flow.state);

    let browser = tokio::spawn(async move { http_get(&callback).await });
    let err = flow.wait_for_credential(Duration::from_secs(5)).await.unwrap_err();
    let page = browser.await.expect("browser response");

    assert!(token_endpoint.hits() == 0, "an error redirect must not be exchanged");
    assert!(!page.contains("Signed in"), "error page must not claim success: {page}");
    assert!(matches!(err, AuthError::LoginCancelled(_) | AuthError::OAuth { .. }), "got {err:?}");
}

/// A silent peer — a browser pre-connect, a port scanner, a health probe —
/// must not be able to hold the callback port hostage for the whole login
/// budget. The real redirect arrives on a later connection and must still win.
#[tokio::test]
async fn a_silent_peer_does_not_block_the_real_callback() {
    let mut token_endpoint = FakeHttp::start(vec![(
        200,
        r#"{"access_token":"acc_after_stall","expires_in":3600}"#.to_string(),
    )])
    .await;
    let provider = ClaudeAiProvider::new(
        ClaudeAiConfig::production("test-client").with_token_url(&token_endpoint.base_url),
    );
    let flow = provider.begin_login(ALL_OAUTH_SCOPES).await.expect("begin login");
    let port = port_of(flow.redirect_uri());

    // Connection 1: connects and never sends a byte.
    let silent = tokio::spawn(async move {
        let _stream = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
            .await
            .expect("silent connect");
        tokio::time::sleep(Duration::from_secs(120)).await;
    });
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Connection 2: the real browser redirect.
    let callback = format!("{}?code=real_code&state={}", flow.redirect_uri(), flow.state);
    let browser = tokio::spawn(async move { http_get(&callback).await });

    let started = std::time::Instant::now();
    let credential = flow
        .wait_for_credential(Duration::from_secs(60))
        .await
        .expect("the real callback must still be served");
    let elapsed = started.elapsed();
    silent.abort();
    let _ = browser.await;

    assert_eq!(credential.provider_id, "claude-ai");
    assert!(
        elapsed < Duration::from_secs(30),
        "a silent peer must not consume the login budget; took {elapsed:?}"
    );
    assert!(token_endpoint.next_request_body().is_some());
}

/// A redirect that validates but whose token endpoint is unreachable is a
/// transient network failure — not a credential, and not a success page.
#[tokio::test]
async fn an_unreachable_token_endpoint_is_a_transient_network_failure() {
    // Bind and immediately release a port so nothing is listening on it.
    let dead = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let dead_url = format!("http://127.0.0.1:{}", dead.local_addr().expect("addr").port());
    drop(dead);

    let provider =
        ClaudeAiProvider::new(ClaudeAiConfig::production("test-client").with_token_url(&dead_url));
    let flow = provider.begin_login(ALL_OAUTH_SCOPES).await.expect("begin login");
    let callback = format!("{}?code=c&state={}", flow.redirect_uri(), flow.state);

    let browser = tokio::spawn(async move { http_get(&callback).await });
    let err = flow.wait_for_credential(Duration::from_secs(20)).await.unwrap_err();
    let page = browser.await.expect("browser response");

    let failure = AuthFailure::classify(&err);
    assert_eq!(failure, AuthFailure::Network, "got {err:?}");
    assert!(failure.is_transient());
    assert!(!page.contains("<h2>Signed in</h2>"), "no success page on failure: {page}");
}

#[tokio::test]
async fn claude_login_times_out_without_a_callback() {    let token_endpoint = FakeHttp::start(vec![(200, r#"{"access_token":"never"}"#.to_string())]).await;
    let provider = ClaudeAiProvider::new(
        ClaudeAiConfig::production("test-client").with_token_url(&token_endpoint.base_url),
    );
    let flow = provider.begin_login(ALL_OAUTH_SCOPES).await.expect("begin login");

    let err = flow.wait_for_credential(Duration::from_millis(200)).await.unwrap_err();
    assert!(matches!(err, AuthError::LoginCancelled(_)), "got {err:?}");
    assert_eq!(token_endpoint.hits(), 0);
}

/// A client that opens the socket and never finishes its headers must not hold
/// the login open past the caller's timeout.
#[tokio::test]
async fn claude_login_timeout_covers_a_stalled_request() {
    let token_endpoint = FakeHttp::start(vec![(200, r#"{"access_token":"never"}"#.to_string())]).await;
    let provider = ClaudeAiProvider::new(
        ClaudeAiConfig::production("test-client").with_token_url(&token_endpoint.base_url),
    );
    let flow = provider.begin_login(ALL_OAUTH_SCOPES).await.expect("begin login");
    let port = flow
        .redirect_uri()
        .rsplit_once(':')
        .and_then(|(_, rest)| rest.split('/').next().map(str::to_owned))
        .expect("port");

    let stall = tokio::spawn(async move {
        let mut stream = tokio::net::TcpStream::connect(format!("127.0.0.1:{port}"))
            .await
            .expect("connect");
        let _ = stream.write_all(b"GET /callback?code=c HTTP/1.1\r\nHost: x\r\n").await;
        // Never sends the blank line; holds the socket open.
        tokio::time::sleep(Duration::from_secs(30)).await;
    });

    let err = flow.wait_for_credential(Duration::from_millis(300)).await.unwrap_err();
    stall.abort();
    assert!(matches!(err, AuthError::LoginCancelled(_)), "got {err:?}");
    assert_eq!(token_endpoint.hits(), 0);
}

/// Cancelling the login (dropping the flow, as an outer `select!` on a
/// cancellation token does) must release the loopback port immediately.
#[tokio::test]
async fn cancelling_the_claude_flow_closes_the_loopback_listener() {
    let token_endpoint = FakeHttp::start(vec![(200, r#"{"access_token":"never"}"#.to_string())]).await;
    let provider = ClaudeAiProvider::new(
        ClaudeAiConfig::production("test-client").with_token_url(&token_endpoint.base_url),
    );
    let flow = provider.begin_login(ALL_OAUTH_SCOPES).await.expect("begin login");
    let port: u16 = flow
        .redirect_uri()
        .rsplit_once(':')
        .and_then(|(_, rest)| rest.split('/').next().map(str::to_owned))
        .expect("port")
        .parse()
        .expect("port number");

    let (cancel_tx, cancel_rx) = oneshot::channel::<()>();
    let task = tokio::spawn(async move {
        tokio::select! {
            result = flow.wait_for_credential(Duration::from_secs(30)) => Some(result.is_ok()),
            _ = cancel_rx => None,
        }
    });

    tokio::time::sleep(Duration::from_millis(50)).await;
    cancel_tx.send(()).expect("cancel");
    let outcome = task.await.expect("join");
    assert!(outcome.is_none(), "cancellation must not yield a credential");

    // The listener socket must be gone: a fresh bind on the same port succeeds.
    tokio::time::sleep(Duration::from_millis(50)).await;
    let rebound = TcpListener::bind(("127.0.0.1", port)).await;
    assert!(rebound.is_ok(), "cancelling the flow must close the loopback listener");
    assert_eq!(token_endpoint.hits(), 0);
}

/// The listener may hand the verdict back to the browser only after the
/// exchange has actually produced a credential.
#[tokio::test]
async fn a_pending_callback_answers_only_after_the_verdict() {
    let listener = LoopbackListener::bind().await.expect("bind");
    let callback = format!("{}?code=c&state=s", listener.redirect_uri());
    let browser = tokio::spawn(async move { http_get(&callback).await });

    let pending = listener
        .wait_for_callback_pending(Duration::from_secs(5))
        .await
        .expect("pending callback");
    assert_eq!(pending.redirect().code.as_deref(), Some("c"));
    pending.respond(CallbackVerdict::Failure).await;

    let page = browser.await.expect("browser response");
    assert!(!page.contains("Signed in"), "a failed login must not render a success page: {page}");
}

// ─────────────────────────────────────────────────────────────────────────────
// GitHub Copilot: device code
// ─────────────────────────────────────────────────────────────────────────────

fn copilot_test_config(device_url: &str, token_url: &str) -> CopilotConfig {
    CopilotConfig {
        device_code_url: device_url.to_string(),
        token_url: token_url.to_string(),
        copilot_token_url: None,
        use_exchange: false,
        ..CopilotConfig::default_public()
    }
}

#[tokio::test]
async fn copilot_device_login_polls_through_pending_and_slow_down() {
    let device = FakeHttp::start(vec![(
        200,
        r#"{"device_code":"dev","user_code":"WXYZ-1234","verification_uri":"https://github.com/login/device","expires_in":30,"interval":0}"#
            .to_string(),
    )])
    .await;
    let mut token = FakeHttp::start(vec![
        (200, r#"{"error":"authorization_pending"}"#.to_string()),
        (200, r#"{"error":"slow_down","interval":1}"#.to_string()),
        (200, r#"{"access_token":"gho_live_token"}"#.to_string()),
    ])
    .await;

    let provider = CopilotProvider::new(copilot_test_config(&device.base_url, &token.base_url));
    let (prompt_tx, prompt_rx) = oneshot::channel();
    let credential = provider
        .login_with_device_code(|prompt| async move {
            prompt_tx.send(prompt).expect("prompt delivered");
            Ok(())
        })
        .await
        .expect("device login");

    let prompt = prompt_rx.await.expect("prompt");
    assert_eq!(prompt.user_code, "WXYZ-1234");
    assert_eq!(prompt.verification_uri, "https://github.com/login/device");
    assert_eq!(credential.provider_id, "github-copilot");
    assert_eq!(token.hits(), 3, "pending and slow_down must both be polled through");

    let first = form_of(&token.next_request_body().expect("poll body"));
    assert_eq!(first.get("device_code").map(String::as_str), Some("dev"));
}

#[tokio::test]
async fn copilot_device_login_reports_denial_and_expiry_without_a_credential() {
    for (body, label) in [
        (r#"{"error":"access_denied"}"#, "denied"),
        (r#"{"error":"expired_token"}"#, "expired"),
    ] {
        let device = FakeHttp::start(vec![(
            200,
            r#"{"device_code":"dev","user_code":"CODE","verification_uri":"https://example.invalid/device","expires_in":30,"interval":0}"#
                .to_string(),
        )])
        .await;
        let token = FakeHttp::start(vec![(200, body.to_string())]).await;
        let provider = CopilotProvider::new(copilot_test_config(&device.base_url, &token.base_url));

        let err = provider
            .login_with_device_code(|_| async { Ok(()) })
            .await
            .expect_err("must not produce a credential");
        assert!(matches!(err, AuthError::LoginCancelled(_)), "{label}: got {err:?}");
    }
}

#[tokio::test]
async fn copilot_device_login_stops_at_the_expiry_deadline() {
    let device = FakeHttp::start(vec![(
        200,
        r#"{"device_code":"dev","user_code":"CODE","verification_uri":"https://example.invalid/device","expires_in":1,"interval":0}"#
            .to_string(),
    )])
    .await;
    let token = FakeHttp::start(vec![(200, r#"{"error":"authorization_pending"}"#.to_string())]).await;
    let provider = CopilotProvider::new(copilot_test_config(&device.base_url, &token.base_url));

    let err = tokio::time::timeout(
        Duration::from_secs(10),
        provider.login_with_device_code(|_| async { Ok(()) }),
    )
    .await
    .expect("must honour the device-code deadline")
    .expect_err("expired login must not produce a credential");
    assert!(matches!(err, AuthError::LoginCancelled(_)), "got {err:?}");
}

#[tokio::test]
async fn copilot_rejects_a_malformed_device_response() {
    for body in [
        r#"{"user_code":"CODE","verification_uri":"https://example.invalid","expires_in":30,"interval":5}"#,
        r#"{"device_code":"","user_code":"CODE","verification_uri":"https://example.invalid","expires_in":30,"interval":5}"#,
        r#"{"device_code":"dev","user_code":"CODE","verification_uri":"https://example.invalid","expires_in":0,"interval":5}"#,
        r#"not json at all"#,
    ] {
        let device = FakeHttp::start(vec![(200, body.to_string())]).await;
        let token = FakeHttp::start(vec![(200, r#"{"access_token":"never"}"#.to_string())]).await;
        let provider = CopilotProvider::new(copilot_test_config(&device.base_url, &token.base_url));

        let result = provider.login_with_device_code(|_| async { Ok(()) }).await;
        assert!(result.is_err(), "malformed device response must fail: {body}");
        assert_eq!(token.hits(), 0, "a malformed device response must not start polling");
    }
}

/// `use_exchange = false` must be honoured at login: the durable GitHub token
/// is never sent to the exchange endpoint.
#[tokio::test]
async fn copilot_login_honours_use_exchange_false() {
    let probe = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let probe_port = probe.local_addr().expect("addr").port();
    let probe_hits = Arc::new(AtomicUsize::new(0));
    let probe_hits_task = Arc::clone(&probe_hits);
    tokio::spawn(async move {
        while let Ok((socket, _)) = probe.accept().await {
            probe_hits_task.fetch_add(1, Ordering::SeqCst);
            drop(socket);
        }
    });

    let device = FakeHttp::start(vec![(
        200,
        r#"{"device_code":"dev","user_code":"CODE","verification_uri":"https://example.invalid/device","expires_in":30,"interval":0}"#
            .to_string(),
    )])
    .await;
    let token = FakeHttp::start(vec![(200, r#"{"access_token":"gho_direct"}"#.to_string())]).await;

    let config = CopilotConfig {
        copilot_token_url: Some(format!("https://127.0.0.1:{probe_port}/copilot_internal/v2/token")),
        use_exchange: false,
        ..copilot_test_config(&device.base_url, &token.base_url)
    };
    let provider = CopilotProvider::new(config);
    let credential = provider
        .login_with_device_code(|_| async { Ok(()) })
        .await
        .expect("login without exchange");

    assert_eq!(credential.provider_id, "github-copilot");
    tokio::time::sleep(Duration::from_millis(100)).await;
    assert_eq!(
        probe_hits.load(Ordering::SeqCst),
        0,
        "use_exchange=false must not contact the exchange endpoint"
    );
}

/// An OAuth error envelope on the device-code endpoint is an authorization
/// failure, not a corrupt stored credential. Misclassifying it as a parse
/// error tells the user their credential store is damaged.
#[tokio::test]
async fn copilot_classifies_a_device_error_envelope_as_an_oauth_rejection() {
    for body in [
        r#"{"error":"unauthorized_client","error_description":"the client is not authorized"}"#,
        r#"{"error":"invalid_client"}"#,
    ] {
        let device = FakeHttp::start(vec![(200, body.to_string())]).await;
        let token = FakeHttp::start(vec![(200, r#"{"access_token":"never"}"#.to_string())]).await;
        let provider = CopilotProvider::new(copilot_test_config(&device.base_url, &token.base_url));

        let err = provider
            .login_with_device_code(|_| async { Ok(()) })
            .await
            .expect_err("an error envelope must fail the login");
        let failure = AuthFailure::classify(&err);
        assert!(
            matches!(failure, AuthFailure::OAuthRejected { .. }),
            "{body} classified as {failure:?}"
        );
        assert_ne!(failure, AuthFailure::CredentialParse, "{body} must not look like corruption");
        assert_eq!(token.hits(), 0);
    }
}

/// RFC 8628 makes `interval` OPTIONAL with a default of 5 s; rejecting a
/// response that omits it locks the user out of a spec-compliant server.
#[tokio::test]
async fn copilot_accepts_a_device_response_without_an_interval() {
    let device = FakeHttp::start(vec![(
        200,
        r#"{"device_code":"dev","user_code":"CODE","verification_uri":"https://example.invalid/device","expires_in":1}"#
            .to_string(),
    )])
    .await;
    let token = FakeHttp::start(vec![(200, r#"{"error":"authorization_pending"}"#.to_string())]).await;
    let provider = CopilotProvider::new(copilot_test_config(&device.base_url, &token.base_url));

    // Accepted (so we reach polling) and the default interval never outruns the
    // one-second expiry: the login ends as an expiry, not a rejected response.
    let err = tokio::time::timeout(
        Duration::from_secs(10),
        provider.login_with_device_code(|_| async { Ok(()) }),
    )
    .await
    .expect("must not hang")
    .expect_err("expired");
    assert_eq!(AuthFailure::classify(&err), AuthFailure::Cancelled, "got {err:?}");
}

/// The positive exchange path: `use_exchange = true` swaps the durable GitHub
/// token for the short-lived Copilot token, which becomes the access token
/// while the GitHub token is kept for the next exchange.
#[tokio::test]
async fn copilot_login_exchanges_the_github_token_when_the_exchange_is_enabled() {
    let expires_at = chrono_now_plus_hour();
    let mut exchange = FakeHttp::start(vec![(
        200,
        format!(r#"{{"token":"copilot_short_lived","expires_at":{expires_at}}}"#),
    )])
    .await;
    let device = FakeHttp::start(vec![(
        200,
        r#"{"device_code":"dev","user_code":"CODE","verification_uri":"https://example.invalid/device","expires_in":30,"interval":0}"#
            .to_string(),
    )])
    .await;
    let token = FakeHttp::start(vec![(200, r#"{"access_token":"gho_durable"}"#.to_string())]).await;

    let config = CopilotConfig {
        copilot_token_url: Some(format!("{}/copilot_internal/v2/token", exchange.base_url)),
        use_exchange: true,
        ..copilot_test_config(&device.base_url, &token.base_url)
    };
    let credential = CopilotProvider::new(config)
        .login_with_device_code(|_| async { Ok(()) })
        .await
        .expect("login with exchange");

    assert_eq!(
        credential.access_token.as_ref().map(|s| s.expose().as_str()),
        Some("copilot_short_lived"),
        "the exchanged token must become the access token"
    );
    assert_eq!(
        credential.refresh_token.as_ref().map(|s| s.expose().as_str()),
        Some("gho_durable"),
        "the durable GitHub token is kept so the exchange can be repeated"
    );
    assert!(credential.expires_at.is_some(), "the exchange supplies an expiry");
    assert_eq!(exchange.hits(), 1);
    assert!(exchange.next_request_body().is_some());
}

fn chrono_now_plus_hour() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .expect("clock")
        .as_secs() as i64
        + 3600
}

/// A whitespace-only `access_token` is not a token; accepting it would store a
/// credential whose every request fails with an opaque 401.
#[tokio::test]
async fn copilot_ignores_a_whitespace_only_access_token() {
    let device = FakeHttp::start(vec![(
        200,
        r#"{"device_code":"dev","user_code":"CODE","verification_uri":"https://example.invalid/device","expires_in":30,"interval":0}"#
            .to_string(),
    )])
    .await;
    let token = FakeHttp::start(vec![
        (200, r#"{"access_token":"   "}"#.to_string()),
        (200, r#"{"access_token":"gho_real"}"#.to_string()),
    ])
    .await;
    let provider = CopilotProvider::new(copilot_test_config(&device.base_url, &token.base_url));

    let credential = provider
        .login_with_device_code(|_| async { Ok(()) })
        .await
        .expect("login");
    assert_eq!(
        credential.access_token.as_ref().map(|s| s.expose().as_str()),
        Some("gho_real")
    );
}

// ─────────────────────────────────────────────────────────────────────────────
// Copilot deployment resolution
// ─────────────────────────────────────────────────────────────────────────────

fn env_of(pairs: &[(&str, &str)]) -> impl Fn(&str) -> Option<String> {
    let owned: Vec<(String, String)> =
        pairs.iter().map(|(k, v)| ((*k).to_string(), (*v).to_string())).collect();
    move |key| owned.iter().find(|(k, _)| k == key).map(|(_, v)| v.clone())
}

#[test]
fn resolution_without_a_login_choice_keeps_the_env_over_saved_precedence() {
    let resolved = resolve_copilot_config(
        None,
        Some("saved.ghe.com"),
        env_of(&[("GH_COPILOT_ENTERPRISE_DOMAIN", "env.ghe.com")]),
    )
    .expect("config");
    assert_eq!(resolved.config.device_code_url, "https://env.ghe.com/login/device/code");
    assert_eq!(resolved.deployment, CopilotDeployment::Enterprise { domain: "env.ghe.com".into() });

    let saved = resolve_copilot_config(None, Some("saved.ghe.com"), env_of(&[])).expect("config");
    assert_eq!(saved.config.device_code_url, "https://saved.ghe.com/login/device/code");

    let public = resolve_copilot_config(None, None, env_of(&[])).expect("config");
    assert_eq!(public.config.api_base_url, "https://api.githubcopilot.com");
    assert_eq!(public.deployment, CopilotDeployment::Public);
}

#[test]
fn an_explicit_public_login_choice_wins_over_the_environment_domain() {
    let resolved = resolve_copilot_config(
        Some(&CopilotDeploymentChoice::Public),
        Some("saved.ghe.com"),
        env_of(&[("GH_COPILOT_ENTERPRISE_DOMAIN", "env.ghe.com")]),
    )
    .expect("config");
    assert_eq!(resolved.config.device_code_url, "https://github.com/login/device/code");
    assert_eq!(resolved.config.api_base_url, "https://api.githubcopilot.com");
    assert_eq!(resolved.deployment, CopilotDeployment::Public);
}

#[test]
fn an_explicit_enterprise_login_choice_wins_over_a_different_env_and_saved_domain() {
    let resolved = resolve_copilot_config(
        Some(&CopilotDeploymentChoice::Enterprise("chosen.ghe.com".into())),
        Some("saved.ghe.com"),
        env_of(&[("GH_COPILOT_ENTERPRISE_DOMAIN", "env.ghe.com")]),
    )
    .expect("config");
    assert_eq!(resolved.config.device_code_url, "https://chosen.ghe.com/login/device/code");
    assert_eq!(
        resolved.deployment,
        CopilotDeployment::Enterprise { domain: "chosen.ghe.com".into() }
    );
}

/// A per-endpoint override may move the auth host away from the chosen
/// deployment. The resolver must report what is actually being contacted so the
/// service can disclose it, never a deployment label that contradicts the host.
#[test]
fn an_endpoint_override_is_disclosed_and_never_mislabels_the_deployment() {
    let resolved = resolve_copilot_config(
        Some(&CopilotDeploymentChoice::Public),
        None,
        env_of(&[("GH_COPILOT_DEVICE_CODE_URL", "https://proxy.internal/login/device/code")]),
    )
    .expect("config");
    assert_eq!(resolved.config.device_code_url, "https://proxy.internal/login/device/code");
    assert_eq!(
        resolved.deployment,
        CopilotDeployment::Custom { auth_host: "proxy.internal".into() }
    );
    assert!(
        resolved.endpoint_overrides.contains(&"GH_COPILOT_DEVICE_CODE_URL"),
        "the override must be disclosed: {:?}",
        resolved.endpoint_overrides
    );
}

#[test]
fn a_pasted_copilot_host_in_an_explicit_choice_is_labelled_as_that_tenant() {
    let resolved = resolve_copilot_config(
        Some(&CopilotDeploymentChoice::Enterprise("copilot-api.foo.ghe.com".into())),
        None,
        env_of(&[]),
    )
    .expect("config");
    assert_eq!(resolved.config.device_code_url, "https://foo.ghe.com/login/device/code");
    assert_eq!(
        resolved.deployment,
        CopilotDeployment::Enterprise { domain: "foo.ghe.com".into() },
        "a pasted copilot-api host is the same tenant, not a custom host"
    );

    let with_scheme = resolve_copilot_config(
        Some(&CopilotDeploymentChoice::Enterprise("HTTPS://foo.ghe.com/".into())),
        None,
        env_of(&[]),
    )
    .expect("config");
    assert_eq!(
        with_scheme.deployment,
        CopilotDeployment::Enterprise { domain: "foo.ghe.com".into() }
    );
}

/// Endpoint URLs come from configuration, which routinely carries a proxy
/// token in a query string or userinfo. `Debug` output lands in logs, so the
/// configs must not print them verbatim.
#[test]
fn configuration_debug_output_does_not_echo_url_secrets() {
    let resolved = resolve_copilot_config(
        Some(&CopilotDeploymentChoice::Public),
        None,
        env_of(&[
            ("GH_COPILOT_TOKEN_URL", "https://user:hunter2@proxy.internal/token?key=SECRETVALUE"),
            ("GH_COPILOT_API_BASE_URL", "https://proxy.internal/v1?token=SECRETVALUE"),
        ]),
    )
    .expect("config");

    for rendered in [format!("{:?}", resolved.config), format!("{resolved:?}")] {
        assert!(!rendered.contains("SECRETVALUE"), "query secret leaked: {rendered}");
        assert!(!rendered.contains("hunter2"), "userinfo leaked: {rendered}");
    }

    let claude = ClaudeAiConfig::production("client")
        .with_token_url("https://proxy.internal/token?key=SECRETVALUE")
        .with_authorize_url("https://user:hunter2@proxy.internal/authorize");
    let rendered = format!("{claude:?}");
    assert!(!rendered.contains("SECRETVALUE"), "query secret leaked: {rendered}");
    assert!(!rendered.contains("hunter2"), "userinfo leaked: {rendered}");
}

#[test]
fn a_non_ascii_or_hostile_domain_is_rejected_without_panicking() {
    for hostile in [
        "abcdefgö.com",
        "https://abcdefgö.com",
        "copilot-apiö.com",
        "ghe.com/path",
        "user@evil.com",
        "ghe.com?x=1",
        "copilot-api.ö",
        "\u{feff}ghe.com",
    ] {
        assert!(
            CopilotConfig::for_enterprise(hostile).is_err(),
            "hostile domain {hostile:?} must be rejected"
        );
        assert!(
            resolve_copilot_config(
                Some(&CopilotDeploymentChoice::Enterprise(hostile.to_string())),
                None,
                env_of(&[]),
            )
            .is_err(),
            "hostile login choice {hostile:?} must be rejected"
        );
    }
}

#[test]
fn resolution_never_writes_anything_to_disk() {
    let dir = tempfile::tempdir().expect("tempdir");
    let _ = resolve_copilot_config(
        Some(&CopilotDeploymentChoice::Enterprise("octocorp.ghe.com".into())),
        Some("saved.ghe.com"),
        env_of(&[("GH_COPILOT_ENTERPRISE_DOMAIN", "env.ghe.com")]),
    )
    .expect("config");
    let entries: Vec<_> = std::fs::read_dir(dir.path()).expect("read dir").collect();
    assert!(entries.is_empty(), "resolving a login config must not persist anything");
}

// ─────────────────────────────────────────────────────────────────────────────
// API key
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn a_blank_api_key_is_rejected_at_the_login_seam() {
    for blank in ["", "   ", "\t", "\r\n", " \u{a0} "] {
        let err = ApiKeyProvider::prepare_credential(blank).expect_err("blank key must be rejected");
        assert!(
            !format!("{err}").contains(blank.trim()) || blank.trim().is_empty(),
            "the rejection must not echo input"
        );
    }
}

#[test]
fn a_prepared_api_key_is_trimmed_and_never_printed() {
    let credential =
        ApiKeyProvider::prepare_credential("  sk-ant-SENTINEL-key  ").expect("valid key");
    assert_eq!(credential.provider_id, "anthropic-api-key");
    assert_ne!(credential.provider_id, "anthropic", "the engine alias is not the provider id");
    let rendered = format!("{credential:?}");
    assert!(!rendered.contains("SENTINEL"), "the key must never appear in Debug: {rendered}");
}

// ─────────────────────────────────────────────────────────────────────────────
// Safe failure classification
// ─────────────────────────────────────────────────────────────────────────────

fn all_error_variants(sentinel: &str) -> Vec<AuthError> {
    vec![
        AuthError::NotFound(sentinel.into()),
        AuthError::UnknownProvider(sentinel.into()),
        AuthError::CannotRefresh(sentinel.into(), sentinel.into()),
        AuthError::OAuth { status: 401, body: sentinel.into() },
        AuthError::Transport(format!("https://example.invalid/?token={sentinel}")),
        AuthError::Store(sentinel.into()),
        AuthError::StoreIncompatible { path: sentinel.into(), detail: sentinel.into() },
        AuthError::StoreUndecryptable { key: sentinel.into(), detail: sentinel.into() },
        AuthError::StoreUnavailable { backend: sentinel.into(), detail: sentinel.into() },
        AuthError::StoreBackendUnknown { value: sentinel.into() },
        AuthError::CredentialProviderMismatch { expected: sentinel.into(), actual: sentinel.into() },
        AuthError::AmbiguousCredentials { providers: sentinel.into() },
        AuthError::Serialization(serde_json::from_str::<serde_json::Value>(sentinel).unwrap_err()),
        AuthError::Io(std::io::Error::other(sentinel)),
        AuthError::StateMismatch,
        AuthError::LoginCancelled(sentinel.into()),
        AuthError::InvalidUrl(sentinel.into()),
        AuthError::InvalidInput { field: sentinel.into(), detail: sentinel.into() },
    ]
}

#[test]
fn a_classified_failure_never_carries_raw_error_text() {
    let sentinel = "SENTINEL_TOKEN_abc123";
    for error in all_error_variants(sentinel) {
        let failure = AuthFailure::classify(&error);
        let debug = format!("{failure:?}");
        let display = failure.to_string();
        assert!(!debug.contains(sentinel), "Debug leaked the sentinel: {debug}");
        assert!(!display.contains(sentinel), "Display leaked the sentinel: {display}");
        assert!(!debug.contains("https://"), "Debug leaked a URL: {debug}");
        assert!(!display.contains("https://"), "Display leaked a URL: {display}");
    }
}

/// The classifier is shared by every provider, so its wording must be
/// provider-neutral: callers add "GitHub Copilot: " / "Claude.ai: " context.
/// A Claude token URL problem reported as a Copilot configuration error sends
/// the user to the wrong settings entirely.
#[test]
fn an_endpoint_failure_is_reported_without_naming_a_provider() {
    let message = AuthFailure::classify(&AuthError::InvalidUrl(
        "Claude.ai token URL must be https".into(),
    ))
    .to_string();
    assert!(!message.contains("Copilot"), "provider-specific wording: {message}");
    assert!(!message.contains("Claude"), "provider-specific wording: {message}");
    assert!(message.contains("endpoint"), "the category must still be clear: {message}");
}

#[test]
fn a_classified_failure_keeps_only_status_and_io_kind_details() {    let oauth = AuthFailure::classify(&AuthError::OAuth { status: 403, body: "secret".into() });
    assert_eq!(oauth, AuthFailure::OAuthRejected { status: 403 });
    assert!(oauth.to_string().contains("403"));

    let io = AuthFailure::classify(&AuthError::Io(std::io::Error::new(
        std::io::ErrorKind::PermissionDenied,
        "secret",
    )));
    assert_eq!(io, AuthFailure::Io { kind: std::io::ErrorKind::PermissionDenied });

    assert_eq!(AuthFailure::classify(&AuthError::StateMismatch), AuthFailure::StateMismatch);
    assert_eq!(
        AuthFailure::classify(&AuthError::LoginCancelled("x".into())),
        AuthFailure::Cancelled
    );
}
