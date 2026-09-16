//! Safe browser launching for host-local provider authentication.

use std::ffi::OsString;
use std::path::PathBuf;
use std::process::Stdio;

#[derive(Debug, thiserror::Error)]
pub enum BrowserLaunchError {
    #[error("the authorization URL is invalid")]
    InvalidUrl,
    #[error("authorization requires HTTPS, or HTTP on a loopback host, without embedded credentials")]
    UnsafeUrl,
    #[error("the {0} scheme is not one this launcher will open")]
    SchemeNotAllowed(String),
    #[error("no installed browser that supports a private window could be located")]
    PrivateModeUnavailable,
    #[error("the system browser launcher could not be located")]
    LauncherUnavailable,
    #[error("browser launching requires an active async runtime")]
    RuntimeUnavailable,
    #[error("the browser launcher could not start ({0:?})")]
    Launch(std::io::ErrorKind),
}

/// Whether a link opens in the browser's ordinary window or a private one.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LinkOpenMode {
    /// The browser's normal window, via the OS default-handler launcher.
    Default,
    /// A private/incognito window. Best-effort: see [`private_browser_available`].
    Private,
}

struct BrowserCommand {
    program: PathBuf,
    args: Vec<OsString>,
}

impl std::fmt::Debug for BrowserCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BrowserCommand")
            .field("program", &self.program.file_name())
            .field("authorization_url", &"[REDACTED]")
            .finish()
    }
}

impl BrowserCommand {
    /// The OS default-handler launcher for an already-validated URL string.
    ///
    /// Shared by the OAuth path and the transcript-link path: both need the
    /// same "open this in whatever the OS registered" behaviour, and both must
    /// spawn it as an argv process with no shell. The URL is one argument, so a
    /// crafted value cannot be read as a second flag or a command.
    fn default_handler(address: OsString) -> Result<Self, BrowserLaunchError> {
        #[cfg(windows)]
        {
            let system_root = std::env::var_os("SystemRoot")
                .map(PathBuf::from).filter(|path| path.is_absolute())
                .ok_or(BrowserLaunchError::LauncherUnavailable)?;
            Ok(Self {
                program: system_root.join("System32").join("rundll32.exe"),
                args: vec![OsString::from("url.dll,FileProtocolHandler"), address],
            })
        }
        #[cfg(target_os = "macos")]
        {
            Ok(Self { program: PathBuf::from("/usr/bin/open"), args: vec![address] })
        }
        #[cfg(all(unix, not(target_os = "macos")))]
        {
            Ok(Self { program: PathBuf::from("/usr/bin/xdg-open"), args: vec![address] })
        }
        #[cfg(not(any(windows, unix)))]
        {
            let _ = address;
            Err(BrowserLaunchError::LauncherUnavailable)
        }
    }

    fn for_url(value: &str) -> Result<Self, BrowserLaunchError> {
        if value.chars().any(char::is_control) || value.contains('\\') {
            return Err(BrowserLaunchError::UnsafeUrl);
        }
        let url = url::Url::parse(value).map_err(|_| BrowserLaunchError::InvalidUrl)?;
        let host = url.host_str().ok_or(BrowserLaunchError::UnsafeUrl)?;
        let loopback = host.eq_ignore_ascii_case("localhost")
            || host.trim_matches(['[', ']']).parse::<std::net::IpAddr>()
                .is_ok_and(|address| address.is_loopback());
        if !url.username().is_empty() || url.password().is_some()
            || !(url.scheme() == "https" || url.scheme() == "http" && loopback)
        {
            return Err(BrowserLaunchError::UnsafeUrl);
        }
        Self::default_handler(OsString::from(url.as_str()))
    }
}

/// Start the system URL launcher without a shell or terminal output.
///
/// Success means the launcher process started, not that the browser opened or
/// authentication succeeded. The host should always show the validated URL in
/// its ephemeral auth UI and monitor/reap the returned child without blocking
/// the login flow for the lifetime of the browser.
pub fn launch_authorization_url(value: &str) -> Result<tokio::process::Child, BrowserLaunchError> {
    let command = BrowserCommand::for_url(value)?;
    tokio::runtime::Handle::try_current().map_err(|_| BrowserLaunchError::RuntimeUnavailable)?;
    tokio::process::Command::new(command.program)
        .args(command.args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|error| BrowserLaunchError::Launch(error.kind()))
}

/// Opens a link from transcript content in the system browser.
///
/// This is the click-to-open path for model-generated links, so it is a
/// security boundary. The URL is validated ([`validate_link_url`]) before a
/// process is ever spawned, and it is passed as a single argv argument to a
/// launcher — no `cmd /c`, no shell, no interpolation — so nothing inside the
/// URL can be interpreted as a command or a second flag.
///
/// [`LinkOpenMode::Private`] opens a private/incognito window when a browser
/// that supports one can be located; otherwise it returns
/// [`BrowserLaunchError::PrivateModeUnavailable`]. It **never** silently opens
/// a normal window and calls it private — the caller is expected to surface
/// that failure to the user.
pub fn open_link(url: &str, mode: LinkOpenMode) -> Result<(), BrowserLaunchError> {
    let validated = validate_link_url(url)?;
    let command = match mode {
        LinkOpenMode::Default => {
            BrowserCommand::default_handler(OsString::from(validated.as_str()))?
        }
        LinkOpenMode::Private => {
            // A private window is a browser feature, not an OS one, and a
            // `mailto:` opens a mail client with no window to be private.
            if !matches!(validated.scheme(), "http" | "https") {
                return Err(BrowserLaunchError::SchemeNotAllowed(validated.scheme().to_string()));
            }
            let browser =
                find_private_browser().ok_or(BrowserLaunchError::PrivateModeUnavailable)?;
            private_command_for(&browser, validated.as_str())
        }
    };
    spawn_detached(command)
}

/// Whether a browser that supports a private window could be located.
///
/// Cheap enough to call while building the right-click menu, so the "open in
/// private window" entry can be shown as disabled — honestly — when no such
/// browser is installed, rather than opening a normal window and lying about it.
pub fn private_browser_available() -> bool {
    find_private_browser().is_some()
}

/// What actions a link supports, for building its context menu.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LinkCapabilities {
    /// The link can be opened at all — its scheme is http, https or mailto.
    pub openable: bool,
    /// A private window can be opened for it — a web scheme *and* a
    /// private-capable browser is installed.
    pub private: bool,
}

/// Resolves a link's capabilities in one place, so the menu never has to reparse
/// the URL or probe the filesystem twice.
///
/// Keeping this beside the validation and browser-detection it depends on is
/// what stops the menu and the launcher from disagreeing about what a link can
/// do — a disabled entry here always matches a refusal in [`open_link`].
pub fn link_capabilities(url: &str) -> LinkCapabilities {
    let scheme = validate_link_url(url).ok().map(|parsed| parsed.scheme().to_string());
    let openable = scheme.is_some();
    let private = matches!(scheme.as_deref(), Some("http") | Some("https"))
        && private_browser_available();
    LinkCapabilities { openable, private }
}

/// Validates a URL from model-generated content before it can reach the OS.
///
/// Allowlist, not blocklist, because past here a process is spawned: `http` and
/// `https` (any host) open in a browser, `mailto:` opens a mail client, and
/// everything else — `file:`, `javascript:`, `data:`, credential-bearing URLs,
/// and anything carrying a control character or a backslash (which some
/// launchers treat as a host separator) — is refused.
fn validate_link_url(raw: &str) -> Result<url::Url, BrowserLaunchError> {
    if raw.chars().any(char::is_control) || raw.contains('\\') {
        return Err(BrowserLaunchError::UnsafeUrl);
    }
    let url = url::Url::parse(raw).map_err(|_| BrowserLaunchError::InvalidUrl)?;
    match url.scheme() {
        "http" | "https" => {
            // A web URL must name a host and must not smuggle credentials past
            // the user in the authority component.
            if url.host_str().is_none() || !url.username().is_empty() || url.password().is_some() {
                return Err(BrowserLaunchError::UnsafeUrl);
            }
        }
        // `mailto:` has no authority to check; the parser accepting it is
        // enough. Control characters were already rejected above.
        "mailto" => {}
        other => return Err(BrowserLaunchError::SchemeNotAllowed(other.to_string())),
    }
    Ok(url)
}

/// A browser we know how to open a private window in, and the flag that does it.
struct PrivateBrowser {
    program: PathBuf,
    /// The single flag that forces a fresh private/incognito window.
    flag: &'static str,
}

/// Builds the argv command that opens `url` in `browser`'s private window.
///
/// `[flag, url]` and nothing more: the URL is one argument, never concatenated
/// onto the flag or a shell string, so a crafted URL cannot inject a second
/// switch. The scheme is already constrained to `http`/`https` by the caller,
/// so the value can never begin with `-` and be mistaken for an option.
fn private_command_for(browser: &PrivateBrowser, url: &str) -> BrowserCommand {
    BrowserCommand {
        program: browser.program.clone(),
        args: vec![OsString::from(browser.flag), OsString::from(url)],
    }
}

/// Locates an installed browser that can open a private/incognito window.
///
/// Best-effort and deliberately conservative. It searches standard install
/// locations rather than reading the user's *default browser* from the
/// registry, because there is no portable, side-effect-free way to resolve the
/// default and then map it to a private-mode flag. The consequence, stated
/// plainly so it is not mistaken for a bug: on a machine whose default browser
/// differs from the first one found here, private mode opens a *different*
/// browser — but it is genuinely private, which is the promise that must hold.
fn find_private_browser() -> Option<PrivateBrowser> {
    #[cfg(windows)]
    {
        // (env var, path under it, private-mode flag), in preference order.
        const CANDIDATES: &[(&str, &str, &str)] = &[
            ("ProgramFiles", r"Google\Chrome\Application\chrome.exe", "--incognito"),
            ("ProgramFiles(x86)", r"Google\Chrome\Application\chrome.exe", "--incognito"),
            ("LocalAppData", r"Google\Chrome\Application\chrome.exe", "--incognito"),
            ("ProgramFiles(x86)", r"Microsoft\Edge\Application\msedge.exe", "--inprivate"),
            ("ProgramFiles", r"Microsoft\Edge\Application\msedge.exe", "--inprivate"),
            ("ProgramFiles", r"Mozilla Firefox\firefox.exe", "-private-window"),
            ("ProgramFiles(x86)", r"Mozilla Firefox\firefox.exe", "-private-window"),
        ];
        for (var, relative, flag) in CANDIDATES {
            if let Some(base) = std::env::var_os(var) {
                let program = PathBuf::from(base).join(relative);
                if program.is_file() {
                    return Some(PrivateBrowser { program, flag });
                }
            }
        }
        None
    }
    #[cfg(not(windows))]
    {
        // On Unix, look up known private-capable browsers on PATH.
        const CANDIDATES: &[(&str, &str)] = &[
            ("google-chrome", "--incognito"),
            ("google-chrome-stable", "--incognito"),
            ("chromium", "--incognito"),
            ("chromium-browser", "--incognito"),
            ("brave-browser", "--incognito"),
            ("microsoft-edge", "--inprivate"),
            ("firefox", "-private-window"),
        ];
        for (name, flag) in CANDIDATES {
            if let Some(program) = which_on_path(name) {
                return Some(PrivateBrowser { program, flag });
            }
        }
        None
    }
}

/// Resolves an executable name against `PATH`, returning the first hit.
#[cfg(not(windows))]
fn which_on_path(name: &str) -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    std::env::split_paths(&path)
        .map(|dir| dir.join(name))
        .find(|candidate| candidate.is_file())
}

/// Spawns a launcher process and detaches from it.
///
/// Uses `std::process` rather than tokio so it works without an async runtime,
/// callable straight from the synchronous pointer handler. The child handle is
/// intentionally dropped: a browser outlives this process, and dropping a
/// `std::process::Child` never kills the program (unlike tokio's kill-on-drop).
/// On Windows — the supported platform — no zombie results; on Unix the
/// short-lived launcher (`xdg-open`) is reaped by the OS when this process
/// exits, while the browser it spawned is reparented and unaffected.
fn spawn_detached(command: BrowserCommand) -> Result<(), BrowserLaunchError> {
    std::process::Command::new(&command.program)
        .args(&command.args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map(|_child| ())
        .map_err(|error| BrowserLaunchError::Launch(error.kind()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_non_web_and_credential_bearing_urls() {
        for value in [
            "javascript:alert(1)", "file:///C:/example.exe", "data:text/plain,hello",
            "https://user:password@example.com/auth", "https://example.com/\n",
            "https://example.com\\@other.invalid", "http://example.com/auth",
            "--unexpected-option", "",
        ] {
            assert!(BrowserCommand::for_url(value).is_err(), "{value}");
        }
    }

    #[test]
    fn loopback_development_urls_are_allowed() {
        for value in [
            "http://127.0.0.1:3210/auth", "http://localhost:3210/auth",
            "http://[::1]:3210/auth",
        ] {
            assert!(BrowserCommand::for_url(value).is_ok(), "{value}");
        }
    }

    #[test]
    fn the_whole_authorization_url_is_one_argument_not_a_shell_command() {
        let url = "https://example.com/authorize?client_id=coda&state=private-state&code_challenge=a_b-9";
        let command = BrowserCommand::for_url(url).unwrap();
        assert_eq!(command.args.last().unwrap(), url);
        assert!(!command.program.to_string_lossy().to_ascii_lowercase().contains("cmd.exe"));
        assert!(command.program.is_absolute());
        #[cfg(windows)]
        assert_eq!(command.args[0], "url.dll,FileProtocolHandler");
    }

    #[test]
    fn debug_does_not_expose_authorization_parameters() {
        let command = BrowserCommand::for_url(
            "https://example.com/authorize?state=private-state&code=private-code"
        ).unwrap();
        let debug = format!("{command:?}");
        assert!(!debug.contains("private-state"));
        assert!(!debug.contains("private-code"));
        assert!(!debug.contains("/authorize"));
    }

    // ── Transcript link opening ──────────────────────────────────────────────

    #[test]
    fn validate_link_url_accepts_web_and_mailto_links() {
        for value in [
            "https://example.com/x",
            "http://example.com",
            "HTTP://Example.COM/y",
            "https://sub.example.com/a/b?c=d#e",
            "mailto:person@example.com",
        ] {
            assert!(validate_link_url(value).is_ok(), "{value}");
        }
    }

    #[test]
    fn validate_link_url_refuses_dangerous_or_malformed_links() {
        for value in [
            "javascript:alert(1)",
            "file:///C:/secret.txt",
            "data:text/html,<b>hi</b>",
            "vbscript:msgbox(1)",
            "not a url",
            "",
            "https://user:pass@example.com/",
            "https://example.com/\u{0007}",
            "https://example.com\\@evil.invalid",
        ] {
            assert!(validate_link_url(value).is_err(), "{value}");
        }
    }

    #[test]
    fn a_refused_scheme_names_itself_without_leaking_the_rest_of_the_url() {
        let error = validate_link_url("javascript:alert(document.cookie)").unwrap_err();
        assert!(matches!(error, BrowserLaunchError::SchemeNotAllowed(ref s) if s == "javascript"));
        // The dangerous payload never rides along in the error message.
        assert!(!format!("{error}").contains("document.cookie"));
    }

    #[test]
    fn a_private_command_passes_the_url_as_one_argument_after_the_flag() {
        let browser = PrivateBrowser {
            program: PathBuf::from("/opt/chrome/chrome"),
            flag: "--incognito",
        };
        let command = private_command_for(&browser, "https://example.com/path?a=b");
        assert_eq!(
            command.args,
            vec![
                OsString::from("--incognito"),
                OsString::from("https://example.com/path?a=b"),
            ],
            "the URL must be a single argument, never concatenated onto the flag"
        );
        assert_eq!(command.program, PathBuf::from("/opt/chrome/chrome"));
    }

    #[test]
    fn the_default_handler_launches_the_url_without_a_shell() {
        let command =
            BrowserCommand::default_handler(OsString::from("https://example.com/x")).unwrap();
        let program = command.program.to_string_lossy().to_ascii_lowercase();
        assert!(!program.contains("cmd.exe"), "no shell may be involved");
        assert!(command.program.is_absolute());
        assert_eq!(command.args.last().unwrap(), "https://example.com/x");
    }

    #[test]
    fn link_capabilities_track_the_openable_scheme() {
        // A web link is openable; whether it can go private depends on an
        // installed browser, which this test does not assume either way.
        assert!(link_capabilities("https://example.com").openable);
        assert!(link_capabilities("mailto:x@example.com").openable);
        // A mailto is never private — there is no browser window to hide.
        assert!(!link_capabilities("mailto:x@example.com").private);
        // Non-web, non-mailto and relative links can do nothing.
        for value in ["file:///etc/passwd", "javascript:1", "./relative", "not a url"] {
            let caps = link_capabilities(value);
            assert!(!caps.openable, "{value} should not be openable");
            assert!(!caps.private, "{value} should not be private-openable");
        }
    }
}
