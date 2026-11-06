//! Installing plugins from git, receipts, and content hashes.
//!
//! A git install checks the repository out into `~/.coda/plugins/<name>/`
//! and records where it came from in `.coda-plugin.json`:
//! `{ source: "git", url, ref, commit, installedAt, contentHash }`.

use std::path::{Path, PathBuf};
use std::process::Command;

use serde_json::{json, Value};
use sha2::{Digest, Sha256};

/// The receipt file name inside an installed plugin.
pub const RECEIPT_FILE: &str = ".coda-plugin.json";

/// `true` for strings that name a git repository rather than a local path.
pub fn looks_like_git_url(source: &str) -> bool {
    let s = source.trim();
    ["https://", "http://", "git@", "ssh://", "git://", "file://"].iter().any(|p| s.starts_with(p))
        || s.split('#').next().is_some_and(|u| u.ends_with(".git"))
}

/// Splits `url#ref` into the URL and the optional ref.
pub fn split_ref(source: &str) -> (String, Option<String>) {
    match source.rsplit_once('#') {
        Some((url, r)) if !r.is_empty() && !url.is_empty() => (url.to_owned(), Some(r.to_owned())),
        _ => (source.to_owned(), None),
    }
}

/// Runs `git` with `args` (in `dir` when given); stdout on success.
pub fn git(dir: Option<&Path>, args: &[&str]) -> Result<String, String> {
    let mut cmd = Command::new("git");
    if let Some(d) = dir {
        cmd.arg("-C").arg(d);
    }
    let out = cmd
        .args(args)
        .env("GIT_TERMINAL_PROMPT", "0")
        .output()
        .map_err(|e| format!("cannot run git: {e}"))?;
    if out.status.success() {
        Ok(String::from_utf8_lossy(&out.stdout).trim().to_owned())
    } else {
        Err(format!("git {} failed: {}", args.join(" "), String::from_utf8_lossy(&out.stderr).trim()))
    }
}

/// Clones `url` (at `git_ref` when given) into `dest`, shallowly.
pub fn clone(url: &str, git_ref: Option<&str>, dest: &Path) -> Result<(), String> {
    let dest_s = dest.to_string_lossy().into_owned();
    let mut args = vec!["clone", "--depth", "1", "--quiet"];
    if let Some(r) = git_ref {
        args.extend(["--branch", r]);
    }
    args.extend([url, dest_s.as_str()]);
    git(None, &args).map(|_| ())
}

/// Moves a checkout to the commit `git_ref` (or the remote's default branch)
/// currently points at. Returns the new commit.
pub fn fetch_and_reset(dir: &Path, git_ref: Option<&str>) -> Result<String, String> {
    git(Some(dir), &["fetch", "--depth", "1", "--quiet", "origin", git_ref.unwrap_or("HEAD")])?;
    git(Some(dir), &["reset", "--hard", "--quiet", "FETCH_HEAD"])?;
    head_commit(dir)
}

pub fn head_commit(dir: &Path) -> Result<String, String> {
    git(Some(dir), &["rev-parse", "HEAD"])
}

/// SHA-256 over every file's relative path and bytes (sorted; `.git` and the
/// receipt excluded), so any content change is visible in the receipt.
pub fn content_hash(root: &Path) -> String {
    let mut files = Vec::new();
    collect(root, root, &mut files);
    files.sort();
    let mut hasher = Sha256::new();
    for rel in files {
        hasher.update(rel.as_bytes());
        hasher.update([0]);
        if let Ok(bytes) = std::fs::read(root.join(&rel)) {
            hasher.update(&bytes);
        }
        hasher.update([0]);
    }
    super::trust::hex(&hasher.finalize())
}

fn collect(root: &Path, dir: &Path, out: &mut Vec<String>) {
    let Ok(entries) = std::fs::read_dir(dir) else { return };
    for entry in entries.flatten() {
        let name = entry.file_name();
        if name == ".git" || name == RECEIPT_FILE {
            continue;
        }
        let path = entry.path();
        match entry.file_type() {
            Ok(t) if t.is_dir() => collect(root, &path, out),
            Ok(t) if t.is_file() => {
                if let Ok(rel) = path.strip_prefix(root) {
                    out.push(rel.to_string_lossy().replace('\\', "/"));
                }
            }
            _ => {}
        }
    }
}

/// Reads a plugin's receipt, if it has one.
pub fn read_receipt(root: &Path) -> Option<Value> {
    let text = std::fs::read_to_string(root.join(RECEIPT_FILE)).ok()?;
    serde_json::from_str(&text).ok()
}

pub fn write_receipt(root: &Path, url: &str, git_ref: Option<&str>, commit: &str) -> Result<(), String> {
    let receipt = json!({
        "source": "git",
        "url": url,
        "ref": git_ref,
        "commit": commit,
        "installedAt": chrono::Utc::now().to_rfc3339(),
        "contentHash": content_hash(root),
    });
    std::fs::write(root.join(RECEIPT_FILE), serde_json::to_string_pretty(&receipt).unwrap_or_default())
        .map_err(|e| format!("cannot write the install receipt: {e}"))
}

/// A unique staging directory under `plugins_dir` (hidden, so discovery
/// skips it).
pub fn staging_dir(plugins_dir: &Path) -> PathBuf {
    plugins_dir.join(format!(".staging-{}", uuid::Uuid::new_v4().simple()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn git_sources_are_recognised() {
        assert!(looks_like_git_url("https://github.com/o/r"));
        assert!(looks_like_git_url("git@github.com:o/r.git"));
        assert!(looks_like_git_url("C:/src/repo.git#v1"));
        assert!(!looks_like_git_url("C:/src/plugin"));
        assert!(!looks_like_git_url("review@local"));
        assert_eq!(split_ref("https://x/r.git#v2"), ("https://x/r.git".to_owned(), Some("v2".to_owned())));
        assert_eq!(split_ref("https://x/r.git"), ("https://x/r.git".to_owned(), None));
    }

    #[test]
    fn the_content_hash_tracks_content_but_not_git_or_the_receipt() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("a.md"), "one").unwrap();
        let h1 = content_hash(dir.path());
        std::fs::create_dir_all(dir.path().join(".git")).unwrap();
        std::fs::write(dir.path().join(".git/HEAD"), "x").unwrap();
        std::fs::write(dir.path().join(RECEIPT_FILE), "{}").unwrap();
        assert_eq!(h1, content_hash(dir.path()));
        std::fs::write(dir.path().join("a.md"), "two").unwrap();
        assert_ne!(h1, content_hash(dir.path()));
    }
}
