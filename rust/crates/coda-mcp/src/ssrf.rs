//! SSRF validation for outgoing MCP HTTP connections.
//!
//! Rules (mirrors `coda-agent/src/hooks/runner.rs`):
//! - Only `https` is allowed except for loopback (`127.0.0.1`, `::1`, `localhost`).
//! - URLs with embedded credentials are unconditionally rejected.
//! - DNS names are resolved once; every resulting address is checked against
//!   blocked ranges (loopback, RFC-1918, link-local, APIPA/169.254, metadata).
//! - Vetted addresses are returned so the caller can pin the connection and
//!   close the DNS-rebinding window.

use std::net::IpAddr;

use tokio_util::sync::CancellationToken;

/// Validate scheme and credentials for an MCP server URL.
///
/// Unlike the hook variant, there is no per-server allowlist: any `https`
/// host is permitted (subject to SSRF resolution). Plain `http` is only
/// allowed to loopback (`127.0.0.1`, `::1`, or `localhost`).
pub(crate) fn validate_mcp_url(url: &str) -> Result<(), String> {
    let parsed: reqwest::Url = url.parse().map_err(|_| format!("invalid MCP server URL: {url}"))?;

    if !parsed.username().is_empty() || parsed.password().is_some() {
        return Err("MCP server URL must not contain embedded credentials".into());
    }

    let scheme = parsed.scheme();
    if scheme == "http" {
        let host = parsed.host_str().unwrap_or("");
        // reqwest/url crate returns "[::1]" (with brackets) for IPv6 literals.
        let is_loopback = host == "127.0.0.1"
            || host == "::1"
            || host == "[::1]"
            || host.eq_ignore_ascii_case("localhost");
        if !is_loopback {
            return Err(
                "http (non-TLS) is only permitted for loopback MCP servers".into(),
            );
        }
    } else if scheme != "https" {
        return Err(format!("unsupported MCP server URL scheme '{scheme}': only https is allowed"));
    }

    Ok(())
}

/// Resolve the host and reject if any resolved address falls in a blocked range.
///
/// Returns the vetted `IpAddr` list so the caller can pin the `reqwest`
/// client and prevent DNS rebinding between validation and connection.
pub(crate) async fn check_ssrf(
    url: &str,
    cancel: CancellationToken,
) -> Result<Vec<IpAddr>, String> {
    let parsed: reqwest::Url = url.parse().map_err(|_| format!("invalid URL: {url}"))?;
    let host = parsed.host_str().unwrap_or("");

    // Literal IP address: validate directly, no DNS lookup needed.
    if let Ok(ip) = host.parse::<IpAddr>() {
        if is_blocked_address(ip) {
            return Err(format!("SSRF: IP address {ip} is in a blocked range"));
        }
        // No addresses to pin (literal IP bypasses DNS).
        return Ok(Vec::new());
    }

    let port = parsed.port_or_known_default().unwrap_or(443);
    let lookup_target = format!("{host}:{port}");
    let addrs = tokio::select! {
        r = tokio::net::lookup_host(&lookup_target) => {
            r.map_err(|e| format!("SSRF: DNS resolution failed for '{host}': {e}"))?
        }
        _ = cancel.cancelled() => return Err("SSRF check cancelled".into()),
    };

    let addrs: Vec<std::net::SocketAddr> = addrs.collect();
    if addrs.is_empty() {
        return Err(format!("SSRF: DNS resolution returned no addresses for '{host}'"));
    }

    for addr in &addrs {
        if is_blocked_address(addr.ip()) {
            return Err(format!(
                "SSRF: '{host}' resolves to blocked address {}",
                addr.ip()
            ));
        }
    }

    Ok(addrs.iter().map(|a| a.ip()).collect())
}

/// Returns `true` for IP addresses in RFC-1918, loopback, link-local, APIPA,
/// and unspecified ranges that must not be reachable from MCP HTTP.
///
/// Also handles IPv4-mapped (`::ffff:a.b.c.d`) and IPv4-compatible
/// (`::a.b.c.d`, deprecated) IPv6 addresses by checking the embedded IPv4.
pub(crate) fn is_blocked_address(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(v4) => {
            let o = v4.octets();
            o[0] == 127                                         // loopback
                || o[0] == 10                                   // RFC-1918 10/8
                || (o[0] == 172 && (16..=31).contains(&o[1]))  // RFC-1918 172.16/12
                || (o[0] == 192 && o[1] == 168)                // RFC-1918 192.168/16
                || (o[0] == 169 && o[1] == 254)                // link-local/APIPA
                || o[0] == 0                                    // 0.0.0.0/8
        }
        IpAddr::V6(v6) => {
            if v6.is_loopback() || v6.is_unspecified() {
                return true;
            }
            // IPv4-mapped: `::ffff:a.b.c.d`
            if let Some(v4) = v6.to_ipv4_mapped() {
                return is_blocked_address(IpAddr::V4(v4));
            }
            // IPv4-compatible: `::a.b.c.d` (deprecated; segments 0-5 are zero)
            let seg = v6.segments();
            if seg[0..6].iter().all(|s| *s == 0) && (seg[6] != 0 || seg[7] != 0) {
                if let Some(v4) = v6.to_ipv4() {
                    return is_blocked_address(IpAddr::V4(v4));
                }
            }
            // fe80::/10 — link-local unicast
            if seg[0] & 0xffc0 == 0xfe80 {
                return true;
            }
            // fec0::/10 — site-local (deprecated, but still blocked)
            if seg[0] & 0xffc0 == 0xfec0 {
                return true;
            }
            // fc00::/7 — unique-local (ULA, RFC 4193)
            if seg[0] & 0xfe00 == 0xfc00 {
                return true;
            }
            false
        }
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    // ── validate_mcp_url ─────────────────────────────────────────────────────

    #[test]
    fn validate_accepts_https() {
        assert!(validate_mcp_url("https://example.com/mcp").is_ok());
    }

    #[test]
    fn validate_accepts_http_loopback_127() {
        assert!(validate_mcp_url("http://127.0.0.1:8080/mcp").is_ok());
    }

    #[test]
    fn validate_accepts_http_loopback_ipv6() {
        assert!(validate_mcp_url("http://[::1]:8080/mcp").is_ok());
    }

    #[test]
    fn validate_accepts_http_localhost() {
        assert!(validate_mcp_url("http://localhost/mcp").is_ok());
    }

    /// SECURITY: plain http to non-loopback must be rejected.
    #[test]
    fn validate_rejects_http_non_loopback() {
        let err = validate_mcp_url("http://example.com/mcp").unwrap_err();
        assert!(err.contains("loopback"), "error: {err}");
    }

    /// SECURITY: embedded credentials must be rejected.
    #[test]
    fn validate_rejects_embedded_credentials_user() {
        let err = validate_mcp_url("https://user@example.com/mcp").unwrap_err();
        assert!(err.contains("credentials"), "error: {err}");
    }

    /// SECURITY: embedded credentials (user:pass) must be rejected.
    #[test]
    fn validate_rejects_embedded_credentials_user_pass() {
        let err = validate_mcp_url("https://user:pass@example.com/mcp").unwrap_err();
        assert!(err.contains("credentials"), "error: {err}");
    }

    /// SECURITY: non-http/https schemes must be rejected.
    #[test]
    fn validate_rejects_ftp_scheme() {
        let err = validate_mcp_url("ftp://example.com/mcp").unwrap_err();
        assert!(err.contains("scheme"), "error: {err}");
    }

    /// SECURITY: file:// scheme must be rejected.
    #[test]
    fn validate_rejects_file_scheme() {
        assert!(validate_mcp_url("file:///etc/passwd").is_err());
    }

    // ── is_blocked_address ───────────────────────────────────────────────────

    #[test]
    fn blocked_address_table() {
        let cases: &[(&str, bool)] = &[
            ("127.0.0.1", true),
            ("10.0.0.1", true),
            ("172.16.0.1", true),
            ("172.31.255.255", true),
            ("172.15.0.1", false), // just outside 172.16/12
            ("172.32.0.1", false),
            ("192.168.0.1", true),
            ("169.254.0.1", true),  // APIPA / link-local
            ("169.254.169.254", true), // AWS/GCP metadata
            ("0.0.0.1", true),
            ("8.8.8.8", false),
            ("1.1.1.1", false),
            ("::1", true),
            ("fe80::1", true),
            ("fc00::1", true),
            ("fd00::1", true),
        ];
        for (addr, expected) in cases {
            let ip: IpAddr = addr.parse().unwrap_or_else(|_| panic!("parse {addr}"));
            assert_eq!(
                is_blocked_address(ip),
                *expected,
                "is_blocked_address({addr}) should be {expected}"
            );
        }
    }

    /// IPv4-mapped IPv6 address `::ffff:169.254.169.254` must be blocked.
    #[test]
    fn blocked_ipv4_mapped_metadata() {
        let ip: IpAddr = "::ffff:169.254.169.254".parse().unwrap();
        assert!(
            is_blocked_address(ip),
            "IPv4-mapped metadata address must be blocked"
        );
    }

    /// IPv4-mapped private range `::ffff:10.0.0.1` must be blocked.
    #[test]
    fn blocked_ipv4_mapped_private() {
        let ip: IpAddr = "::ffff:10.0.0.1".parse().unwrap();
        assert!(is_blocked_address(ip));
    }

    /// A public IPv4-mapped address must not be blocked.
    #[test]
    fn not_blocked_ipv4_mapped_public() {
        let ip: IpAddr = "::ffff:8.8.8.8".parse().unwrap();
        assert!(!is_blocked_address(ip));
    }

    /// ULA (fc00::/7) must be blocked.
    #[test]
    fn blocked_ula() {
        let ip: IpAddr = "fd12:3456:789a::1".parse().unwrap();
        assert!(is_blocked_address(ip));
    }
}
