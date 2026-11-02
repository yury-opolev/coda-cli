//! Atomic, owner-only file writes for state that must not be readable by
//! other local users (planned-restart files).
//!
//! The bytes go to a fresh temp file in the target's directory whose
//! permissions are restricted *before* anything is written (Unix mode 0600;
//! Windows a protected DACL with a single full-control ACE for the current
//! user), then the temp file is synced and renamed over the target.

use std::io::Write;
use std::path::Path;

/// Atomically write `bytes` to `path`, readable and writable only by the
/// current user. Parent directories are created as needed.
pub async fn write_private_atomic(path: &Path, bytes: &[u8]) -> std::io::Result<()> {
    let path = path.to_path_buf();
    let bytes = bytes.to_vec();
    tokio::task::spawn_blocking(move || write_private_atomic_blocking(&path, &bytes))
        .await
        .map_err(std::io::Error::other)?
}

/// Blocking variant of [`write_private_atomic`].
pub fn write_private_atomic_blocking(path: &Path, bytes: &[u8]) -> std::io::Result<()> {
    let parent = match path.parent() {
        Some(p) if !p.as_os_str().is_empty() => p,
        _ => Path::new("."),
    };
    std::fs::create_dir_all(parent)?;

    let file_name = path
        .file_name()
        .and_then(|s| s.to_str())
        .unwrap_or("private");
    let temp = parent.join(format!(
        ".{file_name}.{}.tmp",
        uuid::Uuid::new_v4().simple()
    ));

    let result = write_temp(&temp, bytes).and_then(|()| std::fs::rename(&temp, path));
    if result.is_err() {
        let _ = std::fs::remove_file(&temp);
    }
    result
}

fn write_temp(temp: &Path, bytes: &[u8]) -> std::io::Result<()> {
    let mut options = std::fs::OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    // The handle is dropped (closed) before the rename: Windows refuses to
    // rename an open file.
    let mut file = options.open(temp)?;
    #[cfg(windows)]
    restrict_to_owner(temp)?;
    file.write_all(bytes)?;
    file.sync_all()
}

/// Replace the DACL of `path` with a protected one granting full control to
/// the current process user only (no inherited ACEs).
#[cfg(windows)]
fn restrict_to_owner(path: &Path) -> std::io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Foundation::{
        CloseHandle, ERROR_INSUFFICIENT_BUFFER, GENERIC_ALL, HANDLE, LocalFree,
    };
    use windows_sys::Win32::Security::Authorization::{
        EXPLICIT_ACCESS_W, SE_FILE_OBJECT, SET_ACCESS, SetEntriesInAclW, SetNamedSecurityInfoW,
        TRUSTEE_IS_SID, TRUSTEE_IS_USER, TRUSTEE_W,
    };
    use windows_sys::Win32::Security::{
        ACL, DACL_SECURITY_INFORMATION, GetTokenInformation, NO_INHERITANCE,
        PROTECTED_DACL_SECURITY_INFORMATION, TOKEN_QUERY, TOKEN_USER, TokenUser,
    };
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

    let mut token: HANDLE = std::ptr::null_mut();
    // SAFETY: GetCurrentProcess returns a pseudo-handle that needs no closing;
    // `token` is a valid out-pointer for the opened token handle.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) } == 0 {
        return Err(std::io::Error::last_os_error());
    }

    let result = (|| {
        let mut len = 0u32;
        // SAFETY: size query: a null buffer of length 0 is allowed and `len`
        // is a valid out-pointer receiving the required size.
        let ok =
            unsafe { GetTokenInformation(token, TokenUser, std::ptr::null_mut(), 0, &mut len) };
        if ok == 0 {
            let err = std::io::Error::last_os_error();
            if err.raw_os_error() != Some(ERROR_INSUFFICIENT_BUFFER as i32) {
                return Err(err);
            }
        }
        // u64 storage keeps the buffer aligned for TOKEN_USER.
        let mut buf = vec![0u64; (len as usize).div_ceil(8)];
        // SAFETY: `buf` is writable for at least `len` bytes and suitably
        // aligned; `len` is a valid out-pointer.
        let ok = unsafe {
            GetTokenInformation(token, TokenUser, buf.as_mut_ptr().cast(), len, &mut len)
        };
        if ok == 0 {
            return Err(std::io::Error::last_os_error());
        }
        // SAFETY: the call succeeded, so `buf` starts with an initialised
        // TOKEN_USER whose SID points into `buf`, which outlives every use.
        let sid = unsafe { (*buf.as_ptr().cast::<TOKEN_USER>()).User.Sid };

        let access = EXPLICIT_ACCESS_W {
            grfAccessPermissions: GENERIC_ALL,
            grfAccessMode: SET_ACCESS,
            grfInheritance: NO_INHERITANCE,
            Trustee: TRUSTEE_W {
                TrusteeForm: TRUSTEE_IS_SID,
                TrusteeType: TRUSTEE_IS_USER,
                ptstrName: sid.cast(),
                ..Default::default()
            },
        };
        let mut acl: *mut ACL = std::ptr::null_mut();
        // SAFETY: `access` is one valid entry whose SID lives in `buf`; a null
        // old ACL is allowed; `acl` receives a LocalAlloc'd ACL freed below.
        let code = unsafe { SetEntriesInAclW(1, &access, std::ptr::null(), &mut acl) };
        if code != 0 {
            return Err(std::io::Error::from_raw_os_error(code as i32));
        }

        let wide: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
        // SAFETY: `wide` is a NUL-terminated UTF-16 path; `acl` is the valid
        // ACL built above; owner, group and SACL are not being set (null).
        let code = unsafe {
            SetNamedSecurityInfoW(
                wide.as_ptr(),
                SE_FILE_OBJECT,
                DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                acl,
                std::ptr::null(),
            )
        };
        // SAFETY: `acl` was allocated by SetEntriesInAclW with LocalAlloc and
        // is not used after this point.
        unsafe { LocalFree(acl.cast()) };
        if code != 0 {
            return Err(std::io::Error::from_raw_os_error(code as i32));
        }
        Ok(())
    })();

    // SAFETY: `token` was opened above and is closed exactly once.
    unsafe { CloseHandle(token) };
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    fn leftover_temps(dir: &Path) -> Vec<String> {
        std::fs::read_dir(dir)
            .unwrap()
            .flatten()
            .map(|e| e.file_name().to_string_lossy().into_owned())
            .filter(|n| n.starts_with('.') && n.ends_with(".tmp"))
            .collect()
    }

    #[tokio::test]
    async fn writes_atomically_and_replaces_existing() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("x.json");

        write_private_atomic(&path, b"a").await.unwrap();
        write_private_atomic(&path, b"b").await.unwrap();

        assert_eq!(std::fs::read(&path).unwrap(), b"b");
        assert!(leftover_temps(dir.path()).is_empty());
    }

    #[test]
    fn creates_parent_directories() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("a").join("b").join("x.json");

        write_private_atomic_blocking(&path, b"{}").unwrap();

        assert_eq!(std::fs::read(&path).unwrap(), b"{}");
    }

    #[cfg(unix)]
    #[test]
    fn file_is_mode_0600() {
        use std::os::unix::fs::PermissionsExt;

        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("x.json");

        write_private_atomic_blocking(&path, b"{}").unwrap();

        let mode = std::fs::metadata(&path).unwrap().permissions().mode();
        assert_eq!(mode & 0o777, 0o600);
    }

    #[cfg(windows)]
    #[test]
    fn file_dacl_is_protected_and_owner_only() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("x.json");

        write_private_atomic_blocking(&path, b"{}").unwrap();

        let out = std::process::Command::new("icacls")
            .arg(&path)
            .output()
            .unwrap();
        assert!(out.status.success(), "icacls failed: {out:?}");
        let text = String::from_utf8_lossy(&out.stdout);
        let path_text = path.to_string_lossy();
        // icacls prints `<path> <ACE>` on the first line and one indented ACE
        // per following line, each ACE as `<principal>:(<flags>)...`.
        let aces: Vec<&str> = text
            .lines()
            .map(|l| l.strip_prefix(path_text.as_ref()).unwrap_or(l).trim())
            .filter(|l| l.contains(":("))
            .collect();
        assert_eq!(aces.len(), 1, "expected one ACE, icacls said:\n{text}");
        let ace = aces[0];
        assert!(!ace.contains("(I)"), "ACE must not be inherited: {ace}");
        for principal in ["Everyone", "Users", "Authenticated Users"] {
            assert!(
                !ace.contains(principal),
                "unexpected ACE for {principal}: {ace}"
            );
        }
    }
}
