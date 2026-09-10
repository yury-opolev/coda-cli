//! A loopback stand-in for GitHub's device-code, token-exchange and Copilot
//! inference endpoints, shared by `coda`'s and `coda-engine`'s `auth_cli`
//! conformance suites (the latter includes this same file by path, so the two
//! binaries are driven against one fixture rather than two lookalikes).
//!
//! Nothing here contacts GitHub, Anthropic, DNS or a browser. Every endpoint
//! is a socket on `127.0.0.1:0`, reached only because the *shipping*
//! `GH_COPILOT_*` endpoint overrides point at it — the same overrides an
//! enterprise proxy uses — so what is exercised is the real login flow, the
//! real credential store, and the real engine, not a mock of any of them.
//!
//! It records the **method, path and headers** of every request it serves, so
//! a test can assert what actually went over the wire: which token
//! authenticated which host. Asserting only that a process exited zero would
//! pass for an engine that authenticated with nothing at all.
//!
//! Lifetime: the accept loop and every connection handler are ordinary
//! threads, joined by [`FakeCopilotHost::drop`] — including when a test panics
//! — so no fixture thread outlives the test that started it.

#![allow(dead_code)]

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

/// One request the fixture served.
#[derive(Clone, Debug)]
pub struct Recorded {
    pub method: String,
    pub path: String,
    pub headers: HashMap<String, String>,
    pub body: String,
}

impl Recorded {
    pub fn authorization(&self) -> &str {
        self.headers.get("authorization").map(String::as_str).unwrap_or("")
    }

    pub fn header(&self, name: &str) -> &str {
        self.headers.get(&name.to_ascii_lowercase()).map(String::as_str).unwrap_or("")
    }
}

/// How the fixture answers the device-grant token endpoint.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum DeviceOutcome {
    /// Authorize on the first poll.
    Authorize,
    /// Refuse: the user denied the grant. A login must write nothing.
    Deny,
    /// Authorize, then refuse the token exchange with a genuine `401`.
    ///
    /// A different failure from a denial: the user did authorize, and the
    /// durable token exists, but the host will not turn it into a Copilot
    /// token. Nothing may be stored on the strength of that.
    RejectExchange,
}

/// The identities this fixture hands out. Fabricated values; nothing here is
/// or resembles a real credential.
#[derive(Clone, Debug)]
pub struct Tokens {
    /// The durable GitHub token the device flow yields.
    pub github: String,
    /// The short-lived Copilot token the exchange yields. This is what every
    /// inference request must carry.
    pub copilot: String,
    /// The Anthropic console API key this fixture accepts, as an `x-api-key`
    /// header. Fabricated: it is a well-formed-looking string and nothing
    /// more, and it is only ever reachable because `ANTHROPIC_BASE_URL` points
    /// at a loopback socket.
    pub api_key: String,
}

/// Paths, relative to the fixture base, that the `GH_COPILOT_*` overrides
/// point at. Spelled the way GitHub spells them so the override values in a
/// test read like the real ones.
pub const DEVICE_CODE_PATH: &str = "/login/device/code";
pub const TOKEN_PATH: &str = "/login/oauth/access_token";
pub const EXCHANGE_PATH: &str = "/copilot_internal/v2/token";

/// The model the fixture advertises for Anthropic-style streaming. Chosen so
/// `resolve_endpoint` routes a turn to `/v1/messages`.
pub const MESSAGES_MODEL: &str = "fixture-messages-model";
/// A second advertised model, so "how many models did the provider list?" is a
/// number a test can pin rather than 1-or-more.
pub const CHAT_MODEL: &str = "fixture-chat-model";
/// How many models `GET /models` advertises.
pub const ADVERTISED_MODELS: usize = 2;

/// The Anthropic console endpoints, spelled the way Anthropic spells them so
/// the `ANTHROPIC_BASE_URL` value in a test reads like a real gateway's.
pub const ANTHROPIC_MODELS_PATH: &str = "/v1/models";
pub const ANTHROPIC_MESSAGES_PATH: &str = "/v1/messages";
/// How many models `GET /v1/models` advertises.
pub const ANTHROPIC_ADVERTISED_MODELS: usize = 2;
/// The models `GET /v1/models` advertises. The first is what a test asks the
/// engine to run, so a turn goes to `/v1/messages` with the API key on it.
pub const ANTHROPIC_MODEL: &str = "fixture-anthropic-model";
pub const ANTHROPIC_SECOND_MODEL: &str = "fixture-anthropic-model-2";

pub struct FakeCopilotHost {
    pub base: String,
    pub tokens: Tokens,
    addr: SocketAddr,
    requests: Arc<Mutex<Vec<Recorded>>>,
    shutdown: Arc<AtomicBool>,
    accept: Option<std::thread::JoinHandle<()>>,
    handlers: Arc<Mutex<Vec<std::thread::JoinHandle<()>>>>,
}

impl FakeCopilotHost {
    pub fn start(tokens: Tokens, outcome: DeviceOutcome) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind the fixture");
        let addr = listener.local_addr().expect("fixture address");
        let requests = Arc::new(Mutex::new(Vec::new()));
        let shutdown = Arc::new(AtomicBool::new(false));
        let handlers: Arc<Mutex<Vec<std::thread::JoinHandle<()>>>> =
            Arc::new(Mutex::new(Vec::new()));

        let accept = {
            let requests = Arc::clone(&requests);
            let shutdown = Arc::clone(&shutdown);
            let handlers = Arc::clone(&handlers);
            let tokens = tokens.clone();
            std::thread::spawn(move || loop {
                let Ok((stream, _)) = listener.accept() else { return };
                if shutdown.load(Ordering::SeqCst) {
                    return;
                }
                let requests = Arc::clone(&requests);
                let tokens = tokens.clone();
                let handle = std::thread::spawn(move || serve_one(stream, &tokens, outcome, &requests));
                let mut held = handlers.lock().expect("handlers poisoned");
                held.retain(|handle| !handle.is_finished());
                held.push(handle);
            })
        };

        Self {
            base: format!("http://{addr}"),
            tokens,
            addr,
            requests,
            shutdown,
            accept: Some(accept),
            handlers,
        }
    }

    /// Every request served so far, oldest first.
    pub fn requests(&self) -> Vec<Recorded> {
        self.requests.lock().expect("requests poisoned").clone()
    }

    pub fn request_count(&self) -> usize {
        self.requests.lock().expect("requests poisoned").len()
    }

    /// Every request for `path`, in order.
    pub fn requests_for(&self, path: &str) -> Vec<Recorded> {
        self.requests().into_iter().filter(|r| r.path == path).collect()
    }

    /// The `GH_COPILOT_*` overrides that point the real resolver at this
    /// fixture. Every endpoint is redirected: a variable left unset would send
    /// that one request to github.com.
    pub fn overrides(&self) -> Vec<(&'static str, String)> {
        vec![
            ("GH_COPILOT_DEVICE_CODE_URL", format!("{}{DEVICE_CODE_PATH}", self.base)),
            ("GH_COPILOT_TOKEN_URL", format!("{}{TOKEN_PATH}", self.base)),
            ("GH_COPILOT_COPILOT_TOKEN_URL", format!("{}{EXCHANGE_PATH}", self.base)),
            ("GH_COPILOT_API_BASE_URL", self.base.clone()),
        ]
    }

    /// The `ANTHROPIC_BASE_URL` that points the real resolver at this fixture.
    ///
    /// One variable, the shipping one: an Anthropic API-key request resolves
    /// its host through it in the CLI's pre-commit probe, the CLI's
    /// post-commit connection check and the engine alike, so a test that sets
    /// it is exercising the product's own configuration seam rather than a
    /// test-only door.
    pub fn anthropic_overrides(&self) -> Vec<(&'static str, String)> {
        vec![("ANTHROPIC_BASE_URL", self.base.clone())]
    }

    /// What the fixture was asked for, for a failure message. Names the
    /// *class* of credential each request carried and never its value: a
    /// diagnostic must not become the one place a token gets printed.
    ///
    /// An empty summary is itself the answer to the most common failure — the
    /// child never reached the fixture at all, and therefore went somewhere
    /// else.
    pub fn wire_summary(&self) -> String {
        let requests = self.requests();
        if requests.is_empty() {
            return "<the fixture was never contacted>".to_owned();
        }
        requests
            .iter()
            .map(|request| {
                let auth = match request.authorization() {
                    "" => match request.header("x-api-key") {
                        "" => "no-auth",
                        value if value == self.tokens.api_key => "anthropic-api-key",
                        _ => "other-api-key",
                    },
                    value if value == format!("Bearer {}", self.tokens.copilot) => "copilot-token",
                    value if value == format!("token {}", self.tokens.github) => "durable-token",
                    _ => "other-credential",
                };
                format!("{} {} [{auth}]", request.method, request.path)
            })
            .collect::<Vec<_>>()
            .join("\n")
    }
}

impl Drop for FakeCopilotHost {
    fn drop(&mut self) {
        self.shutdown.store(true, Ordering::SeqCst);
        // Unblock `accept` with one throwaway connection, then reap.
        let _ = TcpStream::connect(self.addr);
        if let Some(accept) = self.accept.take() {
            let _ = accept.join();
        }
        let handlers = {
            let mut held = self.handlers.lock().expect("handlers poisoned");
            std::mem::take(&mut *held)
        };
        for handle in handlers {
            let _ = handle.join();
        }
    }
}

fn serve_one(
    mut stream: TcpStream,
    tokens: &Tokens,
    outcome: DeviceOutcome,
    requests: &Arc<Mutex<Vec<Recorded>>>,
) {
    // A connection that never sends a request must not hold a thread open for
    // the rest of the run.
    let _ = stream.set_read_timeout(Some(Duration::from_secs(10)));
    let _ = stream.set_write_timeout(Some(Duration::from_secs(10)));

    let Some(request) = read_request(&stream) else { return };
    requests.lock().expect("requests poisoned").push(request.clone());

    let response = respond(&request, tokens, outcome);
    let _ = stream.write_all(&response);
    let _ = stream.flush();
    let _ = stream.shutdown(std::net::Shutdown::Write);
}

fn read_request(stream: &TcpStream) -> Option<Recorded> {
    let mut reader = BufReader::new(stream.try_clone().ok()?);
    let mut start = String::new();
    if reader.read_line(&mut start).ok()? == 0 {
        return None;
    }
    let mut parts = start.split_whitespace();
    let method = parts.next()?.to_owned();
    let target = parts.next()?.to_owned();
    let path = target.split(['?', '#']).next().unwrap_or(&target).to_owned();

    let mut headers = HashMap::new();
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line).ok()? == 0 {
            break;
        }
        if line == "\r\n" || line == "\n" {
            break;
        }
        if let Some((name, value)) = line.split_once(':') {
            headers.insert(name.trim().to_ascii_lowercase(), value.trim().to_owned());
        }
    }

    let length: usize = headers
        .get("content-length")
        .and_then(|value| value.parse().ok())
        .unwrap_or(0);
    let mut body = vec![0u8; length];
    if length > 0 && reader.read_exact(&mut body).is_err() {
        body.clear();
    }

    Some(Recorded {
        method,
        path,
        headers,
        body: String::from_utf8_lossy(&body).into_owned(),
    })
}

fn respond(request: &Recorded, tokens: &Tokens, outcome: DeviceOutcome) -> Vec<u8> {
    match request.path.as_str() {
        DEVICE_CODE_PATH => json_response(
            200,
            r#"{"device_code":"fixture-device-code","user_code":"CODA-FIXTURE","verification_uri":"https://example.invalid/device","expires_in":900,"interval":1}"#
                .to_owned(),
        ),
        TOKEN_PATH => match outcome {
            DeviceOutcome::Authorize | DeviceOutcome::RejectExchange => json_response(
                200,
                format!(
                    r#"{{"access_token":"{}","token_type":"bearer","scope":"read:user"}}"#,
                    tokens.github
                ),
            ),
            DeviceOutcome::Deny => json_response(200, r#"{"error":"access_denied"}"#.to_owned()),
        },
        // The durable GitHub token is exchanged here, and only a request that
        // actually carries it may be answered.
        EXCHANGE_PATH => {
            if outcome == DeviceOutcome::RejectExchange {
                // 401 is a real authentication answer, not an absent endpoint:
                // it must not be downgraded into a raw-token login.
                return json_response(401, r#"{"message":"exchange refused"}"#.to_owned());
            }
            if request.authorization() != format!("token {}", tokens.github) {
                return json_response(401, r#"{"message":"bad durable token"}"#.to_owned());
            }
            let expires_at = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_secs() as i64)
                .unwrap_or(0)
                + 24 * 60 * 60;
            json_response(
                200,
                format!(r#"{{"token":"{}","expires_at":{expires_at}}}"#, tokens.copilot),
            )
        }
        // Inference endpoints: the Copilot token is the only thing that opens
        // them, so a request carrying the durable token, another host's token
        // or nothing at all is refused exactly as the real API would.
        "/models" => {
            if !bearer_matches(request, &tokens.copilot) {
                return json_response(401, r#"{"error":{"message":"bad copilot token"}}"#.to_owned());
            }
            json_response(200, models_body())
        }
        "/v1/messages" => {
            // Deliberately shared by both providers: `/v1/messages` is the one
            // path the Anthropic wire format streams on, and the Copilot
            // client speaks the same shape at its own host. Whichever
            // credential arrives has to be a credential this fixture issued —
            // a request with neither, or with the durable GitHub token, is
            // refused exactly as the real API would refuse it.
            if bearer_matches(request, &tokens.copilot) {
                return sse_response(text_turn("4"));
            }
            if api_key_matches(request, &tokens.api_key) {
                return sse_response(text_turn("4"));
            }
            json_response(401, r#"{"error":{"message":"bad copilot token"}}"#.to_owned())
        }
        // The Anthropic console model list: a different endpoint from
        // Copilot's `/models`, reached only because `ANTHROPIC_BASE_URL` says
        // so, and opened only by the `x-api-key` header.
        ANTHROPIC_MODELS_PATH => {
            if !api_key_matches(request, &tokens.api_key) {
                return json_response(
                    401,
                    r#"{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"#
                        .to_owned(),
                );
            }
            json_response(200, anthropic_models_body())
        }
        "/chat/completions" => {
            if !bearer_matches(request, &tokens.copilot) {
                return json_response(401, r#"{"error":{"message":"bad copilot token"}}"#.to_owned());
            }
            json_response(
                400,
                r#"{"error":{"message":"this fixture only streams /v1/messages"}}"#.to_owned(),
            )
        }
        _ => json_response(404, r#"{"error":{"message":"no such fixture endpoint"}}"#.to_owned()),
    }
}

fn bearer_matches(request: &Recorded, token: &str) -> bool {
    request.authorization() == format!("Bearer {token}")
}

/// Whether the request presents the Anthropic console key this fixture
/// accepts. Exact match: a padded or truncated value is a different key, and
/// proving that the engine sends the *normalised* key is the point.
fn api_key_matches(request: &Recorded, api_key: &str) -> bool {
    !api_key.is_empty() && request.header("x-api-key") == api_key
}

/// The Anthropic `/v1/models` shape: `data[].id`, which is what
/// `coda_llm::anthropic::parse_models` reads.
fn anthropic_models_body() -> String {
    format!(
        r#"{{"data":[
            {{"type":"model","id":"{ANTHROPIC_MODEL}","display_name":"Fixture Anthropic","context_window":200000}},
            {{"type":"model","id":"{ANTHROPIC_SECOND_MODEL}","display_name":"Fixture Anthropic 2","context_window":200000}}
        ]}}"#
    )
}

fn models_body() -> String {
    format!(
        r#"{{"data":[
            {{"id":"{MESSAGES_MODEL}","name":"Fixture Messages","capabilities":{{"type":"chat","limits":{{"max_context_window_tokens":200000}}}},"supported_endpoints":["/v1/messages"]}},
            {{"id":"{CHAT_MODEL}","name":"Fixture Chat","capabilities":{{"type":"chat","limits":{{"max_context_window_tokens":128000}}}},"supported_endpoints":["/chat/completions"]}}
        ]}}"#
    )
}

fn sse_event(name: &str, data: &str) -> String {
    format!("event: {name}\ndata: {data}\n\n")
}

/// One complete Anthropic-format streamed turn.
pub fn text_turn(text: &str) -> String {
    sse_event(
        "message_start",
        r#"{"type":"message_start","message":{"usage":{"input_tokens":10}}}"#,
    ) + &sse_event(
        "content_block_start",
        r#"{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"#,
    ) + &sse_event(
        "content_block_delta",
        &format!(
            r#"{{"type":"content_block_delta","index":0,"delta":{{"type":"text_delta","text":"{text}"}}}}"#
        ),
    ) + &sse_event("content_block_stop", r#"{"type":"content_block_stop","index":0}"#)
        + &sse_event(
            "message_delta",
            r#"{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}"#,
        )
        + &sse_event("message_stop", r#"{"type":"message_stop"}"#)
}

fn json_response(status: u16, body: String) -> Vec<u8> {
    let reason = if status == 200 { "OK" } else { "Error" };
    format!(
        "HTTP/1.1 {status} {reason}\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{body}",
        body.len()
    )
    .into_bytes()
}

fn sse_response(body: String) -> Vec<u8> {
    let mut out =
        b"HTTP/1.1 200 OK\r\ncontent-type: text/event-stream\r\nconnection: close\r\n\r\n".to_vec();
    out.extend_from_slice(body.as_bytes());
    out
}

// ─────────────────────────────────────────────────────────────────────────────
// Process helpers shared by both suites
// ─────────────────────────────────────────────────────────────────────────────

/// Ambient variables that must never reach a fixture-driven child: a real
/// key, a real tenant, a real endpoint override, diagnostics redirection, or
/// an ambient proxy that could route "loopback" traffic somewhere real.
///
/// This explicit list is a documented baseline, not the only line of
/// defence: [`has_ambient_scrub_prefix`] additionally strips *any* ambient
/// `CODA_*`, `GH_COPILOT_*` or `ANTHROPIC_*` variable a test did not
/// explicitly set (model catalogue path, config/hooks directories, the
/// identity headers a Copilot request carries — editor/plugin version,
/// integration id, user agent — are all `GH_COPILOT_*` overrides, and every
/// one of them is a value this suite asserts on), so a variable missing from
/// this list is a documentation gap, not a hermeticity gap.
pub const CLEARED_ENV: &[&str] = &[
    "ANTHROPIC_API_KEY",
    "ANTHROPIC_AUTH_TOKEN",
    "ANTHROPIC_BASE_URL",
    "GITHUB_TOKEN",
    "GH_TOKEN",
    "CODA_LOG",
    "CODA_DIAG_DIR",
    "CODA_DIAG_VERBOSITY",
    "CODA_DIAG_RUN_ID",
    "CODA_ENGINE",
    "CODA_CREDENTIAL_BACKEND",
    "GH_COPILOT_ENTERPRISE_DOMAIN",
    "GH_COPILOT_DEVICE_CODE_URL",
    "GH_COPILOT_TOKEN_URL",
    "GH_COPILOT_COPILOT_TOKEN_URL",
    "GH_COPILOT_API_BASE_URL",
    "GH_COPILOT_USE_EXCHANGE",
    "GH_COPILOT_CLIENT_ID",
    "GH_COPILOT_EDITOR_VERSION",
    "GH_COPILOT_PLUGIN_VERSION",
    "GH_COPILOT_INTEGRATION_ID",
    "GH_COPILOT_USER_AGENT",
    "HTTP_PROXY",
    "http_proxy",
    "HTTPS_PROXY",
    "https_proxy",
    "ALL_PROXY",
    "all_proxy",
    "NO_PROXY",
    "no_proxy",
];

/// Prefixes whose ambient value must never reach a fixture-driven child
/// unless the test explicitly supplied it. Prefix-based rather than
/// enumerated in [`CLEARED_ENV`]: the product keeps adding `CODA_*`
/// configuration and `GH_COPILOT_*` overrides, and a fixture that only
/// scrubs yesterday's list is not hermetic against tomorrow's.
const AMBIENT_SCRUB_PREFIXES: &[&str] = &["CODA_", "GH_COPILOT_", "ANTHROPIC_"];

fn has_ambient_scrub_prefix(name: &str) -> bool {
    let upper = name.to_ascii_uppercase();
    AMBIENT_SCRUB_PREFIXES.iter().any(|prefix| upper.starts_with(prefix))
}

/// A loopback address bound, then immediately released, so nothing is
/// listening there: a connection attempt fails fast with "connection
/// refused" rather than hanging on a filtered or black-holed address.
/// Binding first — rather than a hardcoded port such as the conventional
/// "port 1" — costs nothing and can never collide with anything else on
/// this machine already using an ephemeral port.
fn dead_loopback_url() -> String {
    let listener = TcpListener::bind("127.0.0.1:0").expect("reserve a dead-proxy port");
    let port = listener.local_addr().expect("dead-proxy address").port();
    drop(listener);
    format!("http://127.0.0.1:{port}")
}

/// Loopback hosts every fixture in this module binds to, exempted from the
/// dead proxy below so a fixture's own traffic is not itself blackholed.
const PROXY_BYPASS_HOSTS: &str = "127.0.0.1,localhost,::1";

/// The proxy environment every hermetic child must have: every variant of
/// `HTTP(S)_PROXY`/`ALL_PROXY` — upper- and lower-case, since `reqwest` and
/// libcurl do not agree on which they honor — forced to a loopback address
/// nothing is listening on, and `NO_PROXY` bypassing exactly the loopback
/// hosts this module's fixtures use.
///
/// The point is the fail-closed direction: legitimate fixture traffic
/// (loopback) bypasses the proxy and goes straight through, while a request
/// that regresses to a real host — because an endpoint override was missed —
/// does *not* match the bypass list, is handed to the "proxy", and is
/// refused immediately instead of reaching the real internet. Without this,
/// `reqwest` still auto-detects an ambient corporate proxy from the
/// environment and may happily forward a mistaken request to it.
fn proxy_containment_env() -> Vec<(&'static str, String)> {
    let dead = dead_loopback_url();
    vec![
        ("HTTP_PROXY", dead.clone()),
        ("http_proxy", dead.clone()),
        ("HTTPS_PROXY", dead.clone()),
        ("https_proxy", dead.clone()),
        ("ALL_PROXY", dead.clone()),
        ("all_proxy", dead),
        ("NO_PROXY", PROXY_BYPASS_HOSTS.to_owned()),
        ("no_proxy", PROXY_BYPASS_HOSTS.to_owned()),
    ]
}

/// What a finished `auth` process said and returned.
pub struct Outcome {
    pub code: i32,
    pub stdout: String,
    pub stderr: String,
}

impl Outcome {
    pub fn all(&self) -> String {
        format!("{}\n{}", self.stdout, self.stderr)
    }
}

/// How long any child spawned by this module is allowed to run before it is
/// killed and the test fails. Deterministic and well under the default test
/// harness timeout, so a hang is reported as *this* helper's failure rather
/// than a generic suite-wide stall.
const CHILD_DEADLINE: Duration = Duration::from_secs(60);

/// Runs `<exe> auth <args>` against `home`, with the environment scrubbed and
/// the browser explicitly suppressed.
///
/// Bounded two ways: stdin is closed as soon as the caller's line (if any) has
/// been written, so a command that would otherwise wait at a prompt fails
/// instead of hanging; and the wait below is polled against a deadline, so a
/// child that ignores that EOF and hangs anyway — the very failure mode a
/// regressed network containment would produce, since a dead proxy hangs a
/// naive client instead of refusing it — is killed and reaped rather than left
/// to stall the suite. Stdout and stderr are drained on their own threads
/// throughout, so neither pipe filling up can deadlock the poll loop: the same
/// hazard `Child::wait_with_output` avoids internally, which cannot be used
/// here because it has no way to poll for a deadline. Those threads are joined
/// on **every** exit from this function, the deadline panic included, so a
/// failing test never leaves a reader thread attached to a killed child.
pub fn run_auth(
    exe: &std::path::Path,
    home: &std::path::Path,
    args: &[&str],
    env: &[(&str, String)],
) -> Outcome {
    run_auth_with_stdin(exe, home, args, env, None)
}

/// [`run_auth`], optionally feeding one line to the child's stdin.
///
/// `stdin` is what `--api-key-stdin` reads: the *real* command path a
/// non-interactive host uses, so the key never appears in an argv, an
/// environment variable or a terminal echo. It is written and the pipe is
/// closed immediately, on a thread of its own, so a child that never reads it
/// cannot deadlock this helper against a full pipe buffer — and a child that
/// reads more than one line still sees EOF rather than waiting forever.
pub fn run_auth_with_stdin(
    exe: &std::path::Path,
    home: &std::path::Path,
    args: &[&str],
    env: &[(&str, String)],
    stdin: Option<&str>,
) -> Outcome {
    use std::process::{Command, Stdio};

    // Every name this child must end up with a *forced* value for: the
    // profile location, the browser suppression, network containment, and
    // whatever the caller supplied. Every removal below is checked against
    // this set first and skipped, so a scrub can never race a forced
    // assignment no matter what order the two end up applied in.
    let containment = proxy_containment_env();
    let is_forced = |name: &str| {
        name.eq_ignore_ascii_case("CODA_HOME")
            || name.eq_ignore_ascii_case("CODA_AUTH_NO_BROWSER")
            || containment.iter().any(|(key, _)| key.eq_ignore_ascii_case(name))
            || env.iter().any(|(key, _)| key.eq_ignore_ascii_case(name))
    };

    let mut command = Command::new(exe);
    command.arg("auth").args(args).stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped());
    for name in CLEARED_ENV {
        if is_forced(name) {
            continue;
        }
        command.env_remove(name);
    }
    for (key, _) in std::env::vars_os() {
        let name = key.to_string_lossy().into_owned();
        if is_forced(&name) {
            continue;
        }
        if has_ambient_scrub_prefix(&name) {
            command.env_remove(key);
        }
    }
    command.env("CODA_HOME", home).env("CODA_AUTH_NO_BROWSER", "1");
    for (key, value) in &containment {
        command.env(key, value);
    }
    for (key, value) in env {
        command.env(key, value);
    }

    let mut child = command.spawn().expect("the binary under test starts");
    // Written on its own thread and closed there: a child that never reads
    // this would otherwise be able to block the writer once the pipe buffer
    // filled, and a child that keeps reading needs the EOF that closing gives
    // it. The write result is checked — a silently dropped key would turn
    // "the login refused the key" into a test that proves nothing.
    let stdin_pipe = child.stdin.take().expect("piped stdin");
    let stdin_line = stdin.map(str::to_owned);
    let stdin_thread = std::thread::spawn(move || -> std::io::Result<()> {
        let mut pipe = stdin_pipe;
        if let Some(line) = stdin_line {
            pipe.write_all(line.as_bytes())?;
            if !line.ends_with('\n') {
                pipe.write_all(b"\n")?;
            }
            pipe.flush()?;
        }
        // Dropping the pipe here is the EOF the child waits for.
        Ok(())
    });

    let mut stdout_pipe = child.stdout.take().expect("piped stdout");
    let mut stderr_pipe = child.stderr.take().expect("piped stderr");
    let stdout_thread = std::thread::spawn(move || {
        let mut buf = Vec::new();
        let _ = stdout_pipe.read_to_end(&mut buf);
        buf
    });
    let stderr_thread = std::thread::spawn(move || {
        let mut buf = Vec::new();
        let _ = stderr_pipe.read_to_end(&mut buf);
        buf
    });

    let deadline = std::time::Instant::now() + CHILD_DEADLINE;
    let status = loop {
        if let Some(status) = child.try_wait().expect("poll the binary under test") {
            break status;
        }
        if std::time::Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            // Killing the child closes its pipes, so these three return; join
            // them before unwinding rather than leaving reader threads behind
            // for the rest of the suite.
            let _ = stdin_thread.join();
            let _ = stdout_thread.join();
            let _ = stderr_thread.join();
            panic!("run_auth: the binary under test did not exit within {CHILD_DEADLINE:?} (killed)");
        }
        std::thread::sleep(Duration::from_millis(20));
    };

    let stdout = stdout_thread.join().expect("stdout reader thread");
    let stderr = stderr_thread.join().expect("stderr reader thread");
    // A child that exits before reading gives a broken pipe; that is the
    // child's decision and not an error here. Anything else is this helper
    // failing to deliver what the test asked it to deliver, and must not be
    // swallowed.
    match stdin_thread.join().expect("stdin writer thread") {
        Ok(()) => {}
        Err(error) if error.kind() == std::io::ErrorKind::BrokenPipe => {}
        Err(error) => panic!("run_auth: writing the child's stdin failed: {error}"),
    }

    Outcome {
        code: status.code().expect("an exit code"),
        stdout: String::from_utf8_lossy(&stdout).into_owned(),
        stderr: String::from_utf8_lossy(&stderr).into_owned(),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Driving a real engine over the real serve seam
// ─────────────────────────────────────────────────────────────────────────────

/// A hermetic profile: the `CODA_HOME` an `auth` command writes and an engine
/// reads, plus the working directory the engine runs in.
pub struct Sandbox {
    pub home: tempfile::TempDir,
    pub cwd: tempfile::TempDir,
}

impl Sandbox {
    pub fn new() -> Self {
        Self {
            home: tempfile::tempdir().expect("temp CODA_HOME"),
            cwd: tempfile::tempdir().expect("temp cwd"),
        }
    }

    pub fn home(&self) -> &std::path::Path {
        self.home.path()
    }

    pub fn settings_path(&self) -> std::path::PathBuf {
        self.home().join(".coda").join("settings.json")
    }

    /// The settings document, or `None` when the file does not exist.
    pub fn settings(&self) -> Option<serde_json::Value> {
        std::fs::read_to_string(self.settings_path())
            .ok()
            .map(|text| serde_json::from_str(&text).expect("settings.json must be valid JSON"))
    }

    pub fn write_settings(&self, value: serde_json::Value) {
        std::fs::create_dir_all(self.home().join(".coda")).expect("profile dir");
        std::fs::write(self.settings_path(), serde_json::to_string_pretty(&value).unwrap())
            .expect("write settings.json");
    }

    /// `<home>/.coda/credentials/llmauth_<provider>.cred` — the path the .NET
    /// Windows build uses, spelled out here rather than derived, so a change
    /// to the Rust key-to-filename mapping shows up as a failure.
    pub fn credential_path(&self, stored_provider_id: &str) -> std::path::PathBuf {
        self.home()
            .join(".coda")
            .join("credentials")
            .join(format!("llmauth_{stored_provider_id}.cred"))
    }
}

/// A `serve` command for `exe` against this sandbox, with the ambient
/// environment scrubbed and the fixture overrides applied.
///
/// Deliberately **no** `--api-key`/`--endpoint`: the whole point is that the
/// engine finds the credential an `auth` command stored, and resolves its
/// endpoints from the same overrides the login used.
pub fn serve_command(
    exe: &std::path::Path,
    sandbox: &Sandbox,
    env: &[(&str, String)],
    extra_args: &[&str],
) -> coda_client::EngineCommand {
    let mut command = coda_client::EngineCommand::new(exe.as_os_str())
        .arg("serve")
        .arg("--no-mcp")
        .working_dir(sandbox.cwd.path());

    // Every assignment this child must end up with, forced: the profile
    // location, the caller's fixture overrides, and network containment.
    // `EngineCommand` applies every removal in `env_remove` *after* every
    // assignment in `env`, no matter which was added to the command first —
    // so a name that ends up in both lists is always removed, and scrubbing
    // a variable this call is also forcing would silently send the child to
    // the real provider host (or a real, live proxy) exactly the mistake
    // these tests exist to catch. The set below is therefore checked before
    // every removal loop, not derived from call order.
    let mut forced: Vec<(String, String)> =
        vec![("CODA_HOME".to_owned(), sandbox.home().to_string_lossy().into_owned())];
    forced.extend(env.iter().map(|(key, value)| (key.to_string(), value.clone())));
    forced.extend(proxy_containment_env().into_iter().map(|(key, value)| (key.to_owned(), value)));
    let is_forced = |name: &str| forced.iter().any(|(key, _)| key.eq_ignore_ascii_case(name));

    for name in CLEARED_ENV {
        if is_forced(name) {
            continue;
        }
        command = command.env_remove(*name);
    }
    for (key, _) in std::env::vars_os() {
        let name = key.to_string_lossy().into_owned();
        if is_forced(&name) {
            continue;
        }
        if has_ambient_scrub_prefix(&name) {
            command = command.env_remove(key);
        }
    }
    for (key, value) in &forced {
        command = command.env(key.clone(), value.clone());
    }
    for arg in extra_args {
        command = command.arg(*arg);
    }
    command
}

/// Starts an engine and completes the handshake, bounded.
pub async fn open_engine(
    command: coda_client::EngineCommand,
) -> (coda_client::Engine, tokio::sync::mpsc::UnboundedReceiver<coda_client::Inbound>) {
    use coda_proto::messages::{method, InitializeParams};

    let (engine, inbound) = coda_client::Engine::spawn(command).expect("the engine spawns");
    let params = serde_json::to_value(InitializeParams::new("auth-cli-conformance")).unwrap();
    tokio::time::timeout(
        Duration::from_secs(30),
        engine.connection().request(method::INITIALIZE, Some(params)),
    )
    .await
    .expect("initialize must not hang")
    .expect("initialize must succeed");
    (engine, inbound)
}

/// `session/models`, bounded.
pub async fn session_models(connection: &coda_client::Connection) -> serde_json::Value {
    tokio::time::timeout(
        Duration::from_secs(30),
        connection.request(
            coda_proto::messages::method::MODELS,
            Some(serde_json::json!({ "refresh": false })),
        ),
    )
    .await
    .expect("session/models must not hang")
    .expect("session/models must not error")
}

/// Runs one prompt turn to completion, draining the event stream, bounded.
pub async fn run_turn(
    connection: &coda_client::Connection,
    inbound: &mut tokio::sync::mpsc::UnboundedReceiver<coda_client::Inbound>,
    text: &str,
) {
    use coda_proto::messages::{method, PromptParams};

    let params = serde_json::to_value(PromptParams::text(text)).unwrap();
    let pending = connection
        .send_request(method::PROMPT, Some(params))
        .expect("the prompt is accepted");
    let drain = async {
        loop {
            if inbound.recv().await.is_none() {
                break;
            }
        }
    };
    tokio::time::timeout(Duration::from_secs(60), async {
        tokio::select! {
            result = pending => { let _ = result; }
            _ = drain => {}
        }
    })
    .await
    .expect("the prompt turn must not hang");
}

/// Starts `<exe> serve …` with stdin already at end of file and reports how it
/// exited.
///
/// A startup that fails closed never reads stdin, and a startup that succeeds
/// sees EOF and shuts down — so this answers "did this configuration refuse to
/// start?" without leaving a process behind either way.
pub async fn serve_startup_result(
    exe: &std::path::Path,
    sandbox: &Sandbox,
    env: &[(&str, String)],
    extra_args: &[&str],
) -> (Option<i32>, String) {
    use std::process::Stdio;

    let containment = proxy_containment_env();
    let is_forced = |name: &str| {
        name.eq_ignore_ascii_case("CODA_HOME")
            || containment.iter().any(|(key, _)| key.eq_ignore_ascii_case(name))
            || env.iter().any(|(key, _)| key.eq_ignore_ascii_case(name))
    };

    let mut command = tokio::process::Command::new(exe);
    command
        .arg("serve")
        .arg("--no-mcp")
        .args(extra_args)
        .current_dir(sandbox.cwd.path())
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true);
    for name in CLEARED_ENV {
        if is_forced(name) {
            continue;
        }
        command.env_remove(name);
    }
    for (key, _) in std::env::vars_os() {
        let name = key.to_string_lossy().into_owned();
        if is_forced(&name) {
            continue;
        }
        if has_ambient_scrub_prefix(&name) {
            command.env_remove(key);
        }
    }
    command.env("CODA_HOME", sandbox.home());
    for (key, value) in &containment {
        command.env(key, value);
    }
    for (key, value) in env {
        command.env(key, value);
    }

    let child = command.spawn().expect("the binary under test starts");
    let output = tokio::time::timeout(Duration::from_secs(60), child.wait_with_output())
        .await
        .expect("the engine must not hang at startup")
        .expect("the engine exits");
    (
        output.status.code(),
        String::from_utf8_lossy(&output.stderr).into_owned(),
    )
}

// ─────────────────────────────────────────────────────────────────────────────
// The .NET DPAPI producer: an independent writer for the Windows credential
// compatibility test
// ─────────────────────────────────────────────────────────────────────────────

/// Encrypts `plaintext` the way the .NET Windows build's `DpapiTokenStore`
/// does — `ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)`,
/// the exact call in `LlmAuth.Storage.Windows.DpapiTokenStore.SetAsync` — using
/// the real .NET runtime through Windows PowerShell, not the Rust `DpapiStore`
/// writer. Seeding the .NET-shaped document with the Rust writer would only
/// prove the Rust reader agrees with the Rust writer, a tautology; this proves
/// the Rust reader agrees with .NET's own DPAPI call.
///
/// `plaintext` never touches an argv or a process environment: it is written
/// to a throwaway file under `directory` (removed again before this returns)
/// and read back by the script, which writes the ciphertext to a second
/// throwaway file this function reads and also removes. Bounded and reaped
/// exactly as [`run_auth`] is, for the same reason. Panics — never skips — if
/// the .NET DPAPI APIs or `powershell.exe` are unavailable: this suite is
/// Windows-only, and a missing runtime is a broken test host, not grounds to
/// silently pass a store-compatibility claim.
/// A path as a PowerShell **single-quoted** string literal.
///
/// The only escape a single-quoted PowerShell string has is a doubled quote.
/// A temporary directory under a home directory with an apostrophe in it
/// (`C:\Users\O'Brien\…`, which Windows allows) would otherwise close the
/// literal early and hand the rest of the path to the parser as code — a
/// broken test on some machines and a command-injection shape on all of them.
#[cfg(windows)]
fn powershell_literal(path: &std::path::Path) -> String {
    path.display().to_string().replace('\'', "''")
}

#[cfg(windows)]
pub fn dotnet_dpapi_protect(directory: &std::path::Path, plaintext: &str) -> Vec<u8> {
    use std::process::Stdio;

    let input_path = directory.join("dotnet-dpapi-input.tmp");
    let output_path = directory.join("dotnet-dpapi-output.tmp");
    std::fs::write(&input_path, plaintext.as_bytes()).expect("write the fixture plaintext");

    // `$ErrorActionPreference = 'Stop'` turns a missing assembly or a DPAPI
    // failure into a non-zero exit rather than a script that limps on and
    // leaves no output file — the failure this function must not swallow.
    let script = format!(
        "$ErrorActionPreference = 'Stop'\n\
         Add-Type -AssemblyName System.Security\n\
         $plain = [System.IO.File]::ReadAllBytes('{input}')\n\
         $protected = [System.Security.Cryptography.ProtectedData]::Protect(\
             $plain, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)\n\
         [System.IO.File]::WriteAllBytes('{output}', $protected)\n",
        input = powershell_literal(&input_path),
        output = powershell_literal(&output_path),
    );

    let containment = proxy_containment_env();
    let mut command = std::process::Command::new("powershell.exe");
    command
        .arg("-NoProfile")
        .arg("-NonInteractive")
        .arg("-ExecutionPolicy")
        .arg("Bypass")
        .arg("-Command")
        .arg(&script)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for name in CLEARED_ENV {
        if containment.iter().any(|(key, _)| key.eq_ignore_ascii_case(name)) {
            continue;
        }
        command.env_remove(name);
    }
    for (key, _) in std::env::vars_os() {
        let name = key.to_string_lossy().into_owned();
        if containment.iter().any(|(key, _)| key.eq_ignore_ascii_case(&name)) {
            continue;
        }
        if has_ambient_scrub_prefix(&name) {
            command.env_remove(key);
        }
    }
    for (key, value) in &containment {
        command.env(key, value);
    }

    let cleanup_input = || {
        let _ = std::fs::remove_file(&input_path);
    };

    let mut child = match command.spawn() {
        Ok(child) => child,
        Err(source) => {
            cleanup_input();
            panic!(
                ".NET DPAPI producer (powershell.exe) must be available on a Windows test host: {source}"
            );
        }
    };

    let mut stdout_pipe = child.stdout.take().expect("piped stdout");
    let mut stderr_pipe = child.stderr.take().expect("piped stderr");
    let stdout_thread = std::thread::spawn(move || {
        let mut buf = Vec::new();
        let _ = stdout_pipe.read_to_end(&mut buf);
        buf
    });
    let stderr_thread = std::thread::spawn(move || {
        let mut buf = Vec::new();
        let _ = stderr_pipe.read_to_end(&mut buf);
        buf
    });

    let deadline = std::time::Instant::now() + CHILD_DEADLINE;
    let status = loop {
        if let Some(status) = child.try_wait().expect("poll the .NET DPAPI producer") {
            break status;
        }
        if std::time::Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            cleanup_input();
            // Killing the child closes its pipes, so both readers return;
            // join them before unwinding rather than leaving threads behind.
            let _ = stdout_thread.join();
            let _ = stderr_thread.join();
            panic!(".NET DPAPI producer did not exit within {CHILD_DEADLINE:?} (killed)");
        }
        std::thread::sleep(Duration::from_millis(20));
    };

    let stdout = stdout_thread.join().expect("stdout reader thread");
    let stderr = stderr_thread.join().expect("stderr reader thread");
    cleanup_input();

    if !status.success() {
        let _ = std::fs::remove_file(&output_path);
        panic!(
            ".NET DPAPI producer failed (status {:?}):\nstdout: {}\nstderr: {}",
            status.code(),
            String::from_utf8_lossy(&stdout),
            String::from_utf8_lossy(&stderr),
        );
    }

    let ciphertext = std::fs::read(&output_path).expect("read the .NET-produced ciphertext");
    let _ = std::fs::remove_file(&output_path);
    ciphertext
}
