//! Microsoft Entra ID (Azure AD) helpers for MCP authentication.
//!
//! Entra's v2.0 endpoints deviate from generic OAuth 2.1 in a few ways that
//! matter to MCP clients: they reject the RFC 8707 `resource` parameter
//! (resources are expressed through scopes such as `api://x/.default`), they
//! only issue refresh tokens when `offline_access` is requested, and they do
//! not advertise RFC 9207 `iss` callback support. These pure helpers let the
//! OAuth and broker flows recognise Entra authorities and adapt accordingly.

use base64::Engine as _;
use reqwest::Url;

/// Known Entra ID login hosts (public, sovereign and pre-production clouds).
const ENTRA_HOSTS: &[&str] = &[
    "login.microsoftonline.com",
    "login.microsoftonline.us",
    "login.chinacloudapi.cn",
    "login.partner.microsoftonline.cn",
    "login.microsoftonline.de",
    "login.windows.net",
    "login.microsoft.com",
    "login.windows-ppe.net",
    "login.sovcloud-identity.fr",
    "login.sovcloud-identity.de",
    "login.sovcloud-identity.sg",
];

/// Whether `host` is a known Entra ID login host (case-insensitive).
pub fn is_entra_host(host: &str) -> bool {
    ENTRA_HOSTS.iter().any(|h| h.eq_ignore_ascii_case(host))
}

/// An Entra authority: login host plus tenant (GUID, domain, `organizations`,
/// `common` or `consumers`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EntraAuthority {
    pub host: String,
    pub tenant: String,
}

impl EntraAuthority {
    /// Parse `https://<entra-host>/<tenant>[/v2.0][/]`.
    ///
    /// Returns `None` for non-https URLs, non-Entra hosts, a missing tenant or
    /// any other extra path segment.
    pub fn parse(issuer: &str) -> Option<Self> {
        parse_with_version(issuer).map(|(authority, _)| authority)
    }

    /// The same authority with a different tenant.
    pub fn with_tenant(&self, tenant: &str) -> Self {
        Self { host: self.host.clone(), tenant: tenant.to_owned() }
    }

    /// `https://<host>/<tenant>`.
    pub fn authority_url(&self) -> String {
        format!("https://{}/{}", self.host, self.tenant)
    }

    /// `https://<host>/<tenant>/v2.0`.
    pub fn issuer_v2(&self) -> String {
        format!("{}/v2.0", self.authority_url())
    }
}

/// Parse an authority and report whether its path carried the `/v2.0` suffix.
fn parse_with_version(issuer: &str) -> Option<(EntraAuthority, bool)> {
    let url = Url::parse(issuer.trim()).ok()?;
    if url.scheme() != "https"
        || !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return None;
    }
    let host = url.host_str()?;
    if !is_entra_host(host) {
        return None;
    }
    // `url.port()` is `None` for the default port; anything explicit is unusual
    // enough for an Entra issuer that we refuse to treat it as one.
    if url.port().is_some() {
        return None;
    }

    let path = url.path();
    let path = path.strip_suffix('/').unwrap_or(path);
    let mut segments = path.strip_prefix('/')?.split('/');
    let tenant = segments.next().filter(|t| !t.is_empty())?;
    let v2 = match segments.next() {
        None => false,
        Some("v2.0") => true,
        Some(_) => return None,
    };
    if segments.next().is_some() {
        return None;
    }
    Some((EntraAuthority { host: host.to_ascii_lowercase(), tenant: tenant.to_owned() }, v2))
}

/// True for an Entra v2.0 issuer (`https://<entra-host>/<tenant>/v2.0[/]`).
pub fn is_entra_v2_issuer(issuer: &str) -> bool {
    matches!(parse_with_version(issuer), Some((_, true)))
}

/// Pick the scopes to request from Entra.
///
/// Priority: configured scopes → challenge `scope` (whitespace-separated) →
/// PRM `scopes_supported` → `<resource origin>/.default`. Never adds
/// `offline_access`; use [`ensure_offline_access`] where refresh tokens are needed.
pub fn derive_scopes(
    config: &[String],
    challenge_scope: Option<&str>,
    prm_scopes: &[String],
    resource: &str,
) -> Vec<String> {
    if !config.is_empty() {
        return config.to_vec();
    }
    if let Some(scope) = challenge_scope {
        let from_challenge: Vec<String> = scope.split_whitespace().map(str::to_owned).collect();
        if !from_challenge.is_empty() {
            return from_challenge;
        }
    }
    if !prm_scopes.is_empty() {
        return prm_scopes.to_vec();
    }
    match Url::parse(resource) {
        Ok(url) if url.has_host() => {
            vec![format!("{}/.default", url.origin().ascii_serialization())]
        }
        _ => Vec::new(),
    }
}

/// Append `offline_access` unless it is already present.
pub fn ensure_offline_access(mut scopes: Vec<String>) -> Vec<String> {
    if !scopes.iter().any(|s| s == "offline_access") {
        scopes.push("offline_access".to_owned());
    }
    scopes
}

/// Read the `exp` claim of a JWT without verifying its signature.
///
/// Only used to report an approximate expiry for status surfaces; returns
/// `None` on any decoding error.
pub fn jwt_expiry_unix(token: &str) -> Option<i64> {
    let mut parts = token.split('.');
    let _header = parts.next()?;
    let payload = parts.next()?;
    parts.next()?;
    let payload = payload.trim_end_matches('=');
    let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD.decode(payload).ok()?;
    let json: serde_json::Value = serde_json::from_slice(&bytes).ok()?;
    let exp = json.get("exp")?;
    exp.as_i64().or_else(|| exp.as_f64().map(|f| f as i64))
}

#[cfg(test)]
mod tests {
    use super::*;

    const GUID: &str = "72f988bf-86f1-41af-91ab-2d7cd011db47";

    #[test]
    fn parse_guid_tenant_with_v2() {
        let a = EntraAuthority::parse(&format!("https://login.microsoftonline.com/{GUID}/v2.0"))
            .expect("parses");
        assert_eq!(a.host, "login.microsoftonline.com");
        assert_eq!(a.tenant, GUID);
    }

    #[test]
    fn parse_organizations_and_trailing_slash() {
        let a = EntraAuthority::parse("https://login.microsoftonline.com/organizations/v2.0/")
            .expect("parses");
        assert_eq!(a.tenant, "organizations");
        let b = EntraAuthority::parse("https://LOGIN.microsoftonline.us/contoso.com/").expect("parses");
        assert_eq!(b.host, "login.microsoftonline.us");
        assert_eq!(b.tenant, "contoso.com");
    }

    #[test]
    fn parse_rejects_non_entra_non_https_and_missing_tenant() {
        assert_eq!(EntraAuthority::parse("https://example.com/t/v2.0"), None);
        assert_eq!(EntraAuthority::parse("http://login.microsoftonline.com/t/v2.0"), None);
        assert_eq!(EntraAuthority::parse("https://login.microsoftonline.com/"), None);
        assert_eq!(EntraAuthority::parse("https://login.microsoftonline.com"), None);
        assert_eq!(EntraAuthority::parse("https://login.microsoftonline.com/t/oauth2/v2.0"), None);
        assert_eq!(EntraAuthority::parse("not a url"), None);
    }

    #[test]
    fn is_entra_v2_issuer_distinguishes_versions_and_hosts() {
        assert!(is_entra_v2_issuer(&format!("https://login.microsoftonline.com/{GUID}/v2.0")));
        assert!(is_entra_v2_issuer("https://login.microsoftonline.com/common/v2.0/"));
        assert!(!is_entra_v2_issuer(&format!("https://sts.windows.net/{GUID}/")));
        assert!(!is_entra_v2_issuer(&format!("https://login.microsoftonline.com/{GUID}")));
        assert!(!is_entra_v2_issuer("https://as.example.com/t/v2.0"));
    }

    #[test]
    fn with_tenant_and_urls() {
        let a = EntraAuthority::parse("https://login.microsoftonline.com/common/v2.0").unwrap();
        let b = a.with_tenant("contoso.com");
        assert_eq!(b.authority_url(), "https://login.microsoftonline.com/contoso.com");
        assert_eq!(b.issuer_v2(), "https://login.microsoftonline.com/contoso.com/v2.0");
        assert_eq!(a.tenant, "common");
    }

    #[test]
    fn is_entra_host_is_case_insensitive() {
        assert!(is_entra_host("Login.MicrosoftOnline.com"));
        assert!(is_entra_host("login.sovcloud-identity.sg"));
        assert!(!is_entra_host("login.example.com"));
    }

    #[test]
    fn derive_scopes_priority_chain() {
        let cfg = vec!["api://cfg/.default".to_string()];
        let prm = vec!["api://prm/read".to_string()];
        let res = "https://mcp.example.com/mcp/v1";
        assert_eq!(derive_scopes(&cfg, Some("a b"), &prm, res), cfg);
        assert_eq!(derive_scopes(&[], Some(" a  b "), &prm, res), vec!["a", "b"]);
        assert_eq!(derive_scopes(&[], Some("   "), &prm, res), prm);
        assert_eq!(derive_scopes(&[], None, &prm, res), prm);
        assert_eq!(derive_scopes(&[], None, &[], res), vec!["https://mcp.example.com/.default"]);
        assert_eq!(
            derive_scopes(&[], None, &[], "https://mcp.example.com:8443/x"),
            vec!["https://mcp.example.com:8443/.default"]
        );
        assert!(derive_scopes(&[], None, &[], "nonsense").is_empty());
    }

    #[test]
    fn ensure_offline_access_is_idempotent() {
        let once = ensure_offline_access(vec!["a".into()]);
        assert_eq!(once, vec!["a", "offline_access"]);
        assert_eq!(ensure_offline_access(once.clone()), once);
    }

    #[test]
    fn jwt_expiry_reads_exp_claim() {
        let enc = |s: &str| base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(s);
        let token = format!(
            "{}.{}.sig",
            enc(r#"{"alg":"none"}"#),
            enc(r#"{"exp":1700000000,"aud":"x"}"#)
        );
        assert_eq!(jwt_expiry_unix(&token), Some(1_700_000_000));
        let padded = format!(
            "{}.{}.sig",
            enc("{}"),
            base64::engine::general_purpose::URL_SAFE.encode(r#"{"exp":1}"#)
        );
        assert_eq!(jwt_expiry_unix(&padded), Some(1));
    }

    #[test]
    fn jwt_expiry_none_on_garbage() {
        assert_eq!(jwt_expiry_unix("garbage"), None);
        assert_eq!(jwt_expiry_unix("a.b.c"), None);
        assert_eq!(jwt_expiry_unix(""), None);
        let enc = |s: &str| base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(s);
        assert_eq!(jwt_expiry_unix(&format!("x.{}.y", enc(r#"{"nbf":1}"#))), None);
    }
}
