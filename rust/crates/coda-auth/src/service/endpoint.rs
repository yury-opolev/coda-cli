//! Where an Anthropic **API-key** request is sent.
//!
//! # Why this exists
//!
//! Copilot has had an endpoint seam since it shipped (`GH_COPILOT_*`, resolved
//! once in [`crate::provider::copilot::resolve_copilot_config`] and shared by
//! the login and the engine). The Anthropic console key had none: the
//! pre-commit probe, the post-commit connection check and the engine each
//! built a client at the hard-coded `https://api.anthropic.com`, so a user
//! behind a gateway could not sign in at all, and no test could prove an
//! API-key login end to end without sending a fabricated key to Anthropic.
//!
//! `ANTHROPIC_BASE_URL` is the variable the ecosystem already uses for this.
//! It is read here, in **one** resolver, so the probe that validates a key and
//! the engine that later spends it cannot disagree about which host receives
//! it.
//!
//! # Precedence
//!
//! 1. an explicit endpoint the caller already has — `coda serve --endpoint`,
//!    which clap still requires be paired with `--api-key`;
//! 2. `ANTHROPIC_BASE_URL` from the process environment;
//! 3. [`DEFAULT_ANTHROPIC_BASE_URL`].
//!
//! The variable configures **this process only**. It is not persisted, it does
//! not persist a key, and it does not travel: a CLI, an engine and a TUI that
//! must all reach the same gateway must each be started with it exported.
//!
//! # Scope — deliberately narrow
//!
//! This resolves the endpoint for the **Anthropic console API key** identity
//! and nothing else. A Claude.ai subscription and GitHub Copilot keep their
//! own endpoint configuration: routing a subscription token or a Copilot token
//! to a host configured for an API-key gateway would hand one provider's
//! credential to another's server.
//!
//! # What is accepted
//!
//! `https` anywhere, `http` only to a literal loopback host (`localhost`,
//! `127.0.0.0/8`, `::1`) — a plaintext hop off this machine would put the key
//! on the wire in clear. Embedded credentials, a query string, a fragment,
//! control characters, backslashes and any other scheme are refused outright.
//! A non-empty value that does not pass is an **error**, never a silent
//! fallback to the default host: a user who pointed Coda at a gateway must not
//! discover their key went to Anthropic instead.
//!
//! # What may be printed
//!
//! The host (and port), never the path. A query string is rejected, but a path
//! is not, and a path can carry a tenant id or a token-shaped segment. Every
//! `Debug`/`Display` on this module's types is host-only for that reason.

use std::fmt;

/// The environment variable that redirects Anthropic API-key requests.
pub const ANTHROPIC_BASE_URL_ENV: &str = "ANTHROPIC_BASE_URL";

/// Where an Anthropic API-key request goes when nothing overrides it.
pub const DEFAULT_ANTHROPIC_BASE_URL: &str = "https://api.anthropic.com";

/// Which of the three inputs decided the endpoint.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EndpointSource {
    /// Nothing was configured; [`DEFAULT_ANTHROPIC_BASE_URL`] is in force.
    Default,
    /// `ANTHROPIC_BASE_URL` is exported in this process.
    Environment,
    /// The caller supplied one (`coda serve --endpoint`).
    Explicit,
}

impl EndpointSource {
    /// A short, safe name for the source. Never a value.
    pub fn label(self) -> &'static str {
        match self {
            Self::Default => "the default Anthropic endpoint",
            Self::Environment => ANTHROPIC_BASE_URL_ENV,
            Self::Explicit => "the endpoint configured for this engine",
        }
    }
}

/// A validated Anthropic API-key endpoint.
///
/// Constructed only by [`validate`] / [`resolve`], so a value of this type has
/// already been through every rule in this module.
#[derive(Clone, PartialEq, Eq)]
pub struct AnthropicEndpoint {
    base_url: String,
    host: String,
    source: EndpointSource,
}

impl AnthropicEndpoint {
    /// The normalised base URL to hand to the HTTP client. The `/v1/...`
    /// suffix is appended by the client, so this never ends in `/`.
    pub fn base_url(&self) -> &str {
        &self.base_url
    }

    /// The `host[:port]` this endpoint contacts. Safe to print.
    pub fn host(&self) -> &str {
        &self.host
    }

    /// Which input decided this endpoint.
    pub fn source(&self) -> EndpointSource {
        self.source
    }

    /// Whether requests go to Anthropic's own host with nothing configured.
    pub fn is_default(&self) -> bool {
        self.source == EndpointSource::Default
    }

    /// One safe sentence naming where API-key requests go, and why.
    ///
    /// This is what a host discloses before a key is sent anywhere, and what
    /// a future TUI can show without re-deriving anything.
    pub fn describe(&self) -> String {
        match self.source {
            EndpointSource::Default => {
                format!("Anthropic API-key requests go to {} (the default).", self.host)
            }
            EndpointSource::Environment => format!(
                "{ANTHROPIC_BASE_URL_ENV} is set in this environment: Anthropic API-key requests \
                 go to {} instead of the default host. It applies to this process only — export \
                 it wherever a Coda command or engine must use the same host.",
                self.host
            ),
            EndpointSource::Explicit => format!(
                "This engine was configured with an explicit endpoint: Anthropic API-key requests \
                 go to {}.",
                self.host
            ),
        }
    }
}

/// Host and source only: a base URL may carry a path, and a path may carry a
/// secret.
impl fmt::Debug for AnthropicEndpoint {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("AnthropicEndpoint")
            .field("host", &self.host)
            .field("source", &self.source)
            .finish_non_exhaustive()
    }
}

impl fmt::Display for AnthropicEndpoint {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.host)
    }
}

/// Why an endpoint was refused.
///
/// A closed set with no borrowed text: the rejected value is the one thing
/// that must not appear in the message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EndpointError {
    /// Not a URL this build can parse.
    Malformed,
    /// A scheme other than `https` or loopback `http`.
    UnsupportedScheme,
    /// Plaintext `http` to something that is not this machine.
    InsecureNonLoopback,
    /// `user:password@` in the authority.
    EmbeddedCredentials,
    /// A query string or a fragment.
    QueryOrFragment,
    /// A control character or a backslash.
    ForbiddenCharacters,
    /// No host at all.
    MissingHost,
}

impl fmt::Display for EndpointError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Malformed => f.write_str("it is not a valid absolute URL"),
            Self::UnsupportedScheme => {
                f.write_str("only https, or http to a loopback address, is accepted")
            }
            Self::InsecureNonLoopback => f.write_str(
                "plain http is accepted only for a loopback address; anything else would put the \
                 API key on the wire in clear text",
            ),
            Self::EmbeddedCredentials => {
                f.write_str("it embeds credentials in the URL, which are not accepted")
            }
            Self::QueryOrFragment => {
                f.write_str("a query string or fragment is not accepted on a base URL")
            }
            Self::ForbiddenCharacters => {
                f.write_str("it contains a backslash or a control character")
            }
            Self::MissingHost => f.write_str("it names no host"),
        }
    }
}

impl std::error::Error for EndpointError {}

/// Validate one candidate value as an Anthropic API-key base URL.
///
/// Pure: no environment, no I/O. `source` is carried through for reporting and
/// changes nothing about the rules — an explicit endpoint is held to exactly
/// the same standard as an exported one.
pub fn validate(raw: &str, source: EndpointSource) -> Result<AnthropicEndpoint, EndpointError> {
    let trimmed = raw.trim();
    // Checked before parsing: `Url` happily percent-encodes some of these, and
    // a value that round-trips into something else is not the value the user
    // typed. A backslash is refused outright because browsers and libraries
    // disagree about whether it separates a path or is part of the host.
    if trimmed.chars().any(|c| c.is_control() || c == '\\') {
        return Err(EndpointError::ForbiddenCharacters);
    }
    let url = url::Url::parse(trimmed).map_err(|_| EndpointError::Malformed)?;

    match url.scheme() {
        "https" | "http" => {}
        _ => return Err(EndpointError::UnsupportedScheme),
    }
    if !url.username().is_empty() || url.password().is_some() {
        return Err(EndpointError::EmbeddedCredentials);
    }
    if url.query().is_some() || url.fragment().is_some() {
        return Err(EndpointError::QueryOrFragment);
    }
    let host = url.host().ok_or(EndpointError::MissingHost)?;
    if url.scheme() == "http" && !is_loopback(&host) {
        return Err(EndpointError::InsecureNonLoopback);
    }

    // `host_str` keeps the brackets an IPv6 literal needs, so this reassembles
    // into something parseable.
    let authority = match url.port() {
        Some(port) => format!("{}:{port}", url.host_str().ok_or(EndpointError::MissingHost)?),
        None => url.host_str().ok_or(EndpointError::MissingHost)?.to_owned(),
    };
    // `Url::path()` is "/" for a bare host; the client appends "/v1/messages",
    // so every trailing slash has to go or the request path doubles up.
    let path = url.path().trim_end_matches('/');

    Ok(AnthropicEndpoint {
        base_url: format!("{}://{authority}{path}", url.scheme()),
        host: authority,
        source,
    })
}

fn is_loopback(host: &url::Host<&str>) -> bool {
    match host {
        url::Host::Domain(name) => name.eq_ignore_ascii_case("localhost"),
        url::Host::Ipv4(ip) => ip.is_loopback(),
        url::Host::Ipv6(ip) => ip.is_loopback(),
    }
}

/// Resolve the endpoint for the Anthropic **API-key** identity.
///
/// `explicit` is an endpoint the caller already holds (`coda serve
/// --endpoint`); `env` reads the process environment. A blank value on either
/// input counts as absent — a variable exported as an empty string is how a
/// shell says "unset", and treating it as a malformed URL would break a
/// working profile. A non-blank value that fails validation is an error.
pub fn resolve(
    explicit: Option<&str>,
    env: impl Fn(&str) -> Option<String>,
) -> Result<AnthropicEndpoint, EndpointError> {
    if let Some(value) = explicit.map(str::trim).filter(|value| !value.is_empty()) {
        return validate(value, EndpointSource::Explicit);
    }
    if let Some(value) = env(ANTHROPIC_BASE_URL_ENV) {
        let trimmed = value.trim();
        if !trimmed.is_empty() {
            return validate(trimmed, EndpointSource::Environment);
        }
    }
    validate(DEFAULT_ANTHROPIC_BASE_URL, EndpointSource::Default)
}

/// [`resolve`] over the service's environment port.
pub fn resolve_in(
    explicit: Option<&str>,
    environment: &dyn crate::service::AuthEnvironment,
) -> Result<AnthropicEndpoint, EndpointError> {
    resolve(explicit, |name| environment.var(name))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::service::MapEnvironment;

    fn no_env(_: &str) -> Option<String> {
        None
    }

    #[test]
    fn nothing_configured_is_anthropics_own_host() {
        let endpoint = resolve(None, no_env).expect("the default is valid");
        assert_eq!(endpoint.base_url(), DEFAULT_ANTHROPIC_BASE_URL);
        assert_eq!(endpoint.host(), "api.anthropic.com");
        assert!(endpoint.is_default());
        assert_eq!(endpoint.source(), EndpointSource::Default);
    }

    #[test]
    fn the_environment_variable_moves_the_host_and_says_so() {
        let env = MapEnvironment::new(&[(ANTHROPIC_BASE_URL_ENV, "https://gateway.example.com")]);
        let endpoint = resolve_in(None, &env).expect("a valid override");
        assert_eq!(endpoint.base_url(), "https://gateway.example.com");
        assert_eq!(endpoint.source(), EndpointSource::Environment);
        assert!(!endpoint.is_default());
        assert!(endpoint.describe().contains("gateway.example.com"), "{}", endpoint.describe());
        assert!(endpoint.describe().contains(ANTHROPIC_BASE_URL_ENV), "{}", endpoint.describe());
    }

    #[test]
    fn an_explicit_endpoint_outranks_the_environment() {
        let env = MapEnvironment::new(&[(ANTHROPIC_BASE_URL_ENV, "https://from-env.example.com")]);
        let endpoint =
            resolve_in(Some("https://explicit.example.com"), &env).expect("a valid override");
        assert_eq!(endpoint.base_url(), "https://explicit.example.com");
        assert_eq!(endpoint.source(), EndpointSource::Explicit);
    }

    #[test]
    fn a_blank_variable_is_unset_rather_than_malformed() {
        for blank in ["", "   ", "\t"] {
            let env = MapEnvironment::new(&[(ANTHROPIC_BASE_URL_ENV, blank)]);
            let endpoint = resolve_in(None, &env).expect("blank means unset");
            assert!(endpoint.is_default(), "{blank:?} was not treated as unset");
        }
    }

    #[test]
    fn loopback_http_is_accepted_and_nothing_else_is() {
        for allowed in [
            "http://127.0.0.1:8080",
            "http://localhost:9000",
            "http://LOCALHOST",
            "http://127.9.9.9",
            "http://[::1]:1234",
        ] {
            assert!(
                validate(allowed, EndpointSource::Environment).is_ok(),
                "{allowed} should be accepted"
            );
        }
        assert_eq!(
            validate("http://gateway.example.com", EndpointSource::Environment),
            Err(EndpointError::InsecureNonLoopback)
        );
        assert_eq!(
            validate("http://169.254.169.254", EndpointSource::Environment),
            Err(EndpointError::InsecureNonLoopback)
        );
    }

    #[test]
    fn the_rejected_shapes_are_rejected() {
        for (raw, expected) in [
            ("not a url", EndpointError::Malformed),
            ("ftp://gateway.example.com", EndpointError::UnsupportedScheme),
            ("file:///c:/keys", EndpointError::UnsupportedScheme),
            ("https://user:pass@gateway.example.com", EndpointError::EmbeddedCredentials),
            ("https://token@gateway.example.com", EndpointError::EmbeddedCredentials),
            ("https://gateway.example.com/?key=SECRET", EndpointError::QueryOrFragment),
            ("https://gateway.example.com/#SECRET", EndpointError::QueryOrFragment),
            ("https://gateway.example.com\\evil", EndpointError::ForbiddenCharacters),
            ("https://gateway.example.com/\u{7}", EndpointError::ForbiddenCharacters),
        ] {
            assert_eq!(
                validate(raw, EndpointSource::Environment),
                Err(expected),
                "{raw} was not rejected as expected"
            );
        }
    }

    #[test]
    fn an_invalid_override_is_an_error_and_never_the_default_host() {
        let env = MapEnvironment::new(&[(ANTHROPIC_BASE_URL_ENV, "http://gateway.example.com")]);
        let error = resolve_in(None, &env).expect_err("must not fall back");
        assert_eq!(error, EndpointError::InsecureNonLoopback);
    }

    #[test]
    fn a_base_path_is_kept_and_a_trailing_slash_is_not() {
        let endpoint =
            validate("https://gateway.example.com/anthropic///", EndpointSource::Environment)
                .expect("a path is allowed");
        assert_eq!(endpoint.base_url(), "https://gateway.example.com/anthropic");
        // The client appends `/v1/messages`; a doubled slash would be a
        // different path on a strict gateway.
        assert!(!endpoint.base_url().ends_with('/'));

        let bare = validate("https://gateway.example.com/", EndpointSource::Environment)
            .expect("a bare host is allowed");
        assert_eq!(bare.base_url(), "https://gateway.example.com");
    }

    #[test]
    fn nothing_that_prints_this_endpoint_can_print_its_path() {
        let endpoint = validate(
            "https://gateway.example.com/tenants/SECRET-TENANT-PATH",
            EndpointSource::Environment,
        )
        .expect("a path is allowed");
        let rendered = format!("{endpoint} {endpoint:?} {}", endpoint.describe());
        assert!(!rendered.contains("SECRET-TENANT-PATH"), "{rendered}");
        assert!(rendered.contains("gateway.example.com"), "{rendered}");
    }

    #[test]
    fn a_rejection_never_echoes_the_value_it_refused() {
        let error = validate("https://gateway.example.com/?key=SECRET-IN-QUERY", EndpointSource::Environment)
            .expect_err("rejected");
        let rendered = format!("{error} {error:?}");
        assert!(!rendered.contains("SECRET-IN-QUERY"), "{rendered}");
    }
}
