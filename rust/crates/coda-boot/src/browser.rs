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
    #[error("the system browser launcher could not be located")]
    LauncherUnavailable,
    #[error("browser launching requires an active async runtime")]
    RuntimeUnavailable,
    #[error("the browser launcher could not start ({0:?})")]
    Launch(std::io::ErrorKind),
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
        let address = OsString::from(url.as_str());

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
}
