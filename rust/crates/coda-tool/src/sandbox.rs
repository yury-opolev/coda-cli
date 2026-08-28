//! Path sandbox for the Coda tool executor.
//!
//! Tools may only operate inside `working_directory` (or additional granted
//! roots).  Symlinks/junctions on the deepest existing component are resolved
//! so a reparse point that points outside the cwd cannot be used as an escape
//! hatch.
//!
//! This module is extracted from `coda-agent` so that `coda-mcp` and any
//! future thin crates can use sandbox helpers without pulling in the engine.

use std::collections::HashSet;
use std::path::{Component, Path, PathBuf};

/// Resolve a (possibly relative) path against `working_directory` without
/// requiring the path to exist.
pub fn resolve_path(working_directory: &str, path: &str) -> String {
    let p = Path::new(path);
    let full = if p.is_absolute() {
        p.to_path_buf()
    } else {
        Path::new(working_directory).join(path)
    };
    normalize_path(&full).to_string_lossy().into_owned()
}

/// Returns `true` when `full_path` is the same as `root` or is inside it,
/// after resolving symlinks on the deepest existing component.
pub fn is_within_root(root: &str, full_path: &str) -> bool {
    let root_resolved = resolve_final_target(Path::new(root));
    let path_resolved = resolve_final_target(Path::new(full_path));

    // Case-insensitive comparison matches Windows filesystem semantics.
    let root_str = root_resolved.to_string_lossy().to_lowercase();
    let root_str = root_str.trim_end_matches(['/', '\\']).to_string();
    let path_str = path_resolved.to_string_lossy().to_lowercase();

    path_str == root_str
        || path_str.starts_with(&format!("{root_str}\\"))
        || path_str.starts_with(&format!("{root_str}/"))
}

/// Resolve a path and confirm it stays within the allowed roots.
///
/// Returns `Ok(full_path)` when the path is permitted, `Err(reason)` when it
/// would escape the sandbox. Bypass mode (`allow_outside_root = true`) skips
/// the containment check but still resolves the path.
pub fn try_resolve_within_root(
    root: &str,
    path: &str,
    allow_outside_root: bool,
    additional_roots: Option<&HashSet<String>>,
) -> Result<String, String> {
    let full = resolve_path(root, path);
    if allow_outside_root {
        return Ok(full);
    }
    if is_within_root(root, &full) {
        return Ok(full);
    }
    if let Some(extras) = additional_roots {
        for extra in extras {
            if is_within_root(extra, &full) {
                return Ok(full);
            }
        }
    }
    Err(format!(
        "Path '{path}' is outside the working directory and is not allowed. \
         Switch to bypass permissions (/yolo) to allow paths outside the working directory."
    ))
}

/// Resolve through symlinks/junctions on the deepest existing path component.
///
/// Falls back recursively to the parent when the full path does not exist yet,
/// so a new file inside an existing directory is still resolved correctly.
fn resolve_final_target(path: &Path) -> PathBuf {
    if let Ok(canon) = std::fs::canonicalize(path) {
        return canon;
    }
    if let Some(parent) = path.parent() {
        if !parent.as_os_str().is_empty() {
            let parent_resolved = resolve_final_target(parent);
            if let Some(name) = path.file_name() {
                return parent_resolved.join(name);
            }
        }
    }
    normalize_path(path)
}

/// Normalize a path (collapse `..` and `.`) without requiring existence.
///
/// `..` is clamped at the root rather than allowed to pop past it. Popping the
/// prefix would silently turn an absolute path into a relative one, and any
/// caller that later resolved it against the working directory would land
/// somewhere entirely different from what was checked.
fn normalize_path(path: &Path) -> PathBuf {
    let mut components: Vec<Component<'_>> = Vec::new();
    for component in path.components() {
        match component {
            Component::ParentDir => {
                // Never pop the prefix or root: `C:\a\..\..` is `C:\`, not `..`.
                let poppable = !matches!(
                    components.last(),
                    None | Some(Component::Prefix(_)) | Some(Component::RootDir)
                );
                if poppable {
                    components.pop();
                }
            }
            Component::CurDir => {}
            c => components.push(c),
        }
    }
    components.into_iter().collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn relative_path_is_resolved_against_working_directory() {
        let resolved = resolve_path("/project", "src/main.rs");
        assert!(resolved.replace('\\', "/").ends_with("/project/src/main.rs"));
    }

    #[test]
    fn absolute_path_is_preserved() {
        let resolved = resolve_path("/project", "/other/file.rs");
        assert!(resolved.replace('\\', "/").ends_with("/other/file.rs"));
    }

    #[test]
    fn parent_traversal_is_normalized() {
        let resolved = resolve_path("/project", "src/../README.md");
        assert!(resolved.replace('\\', "/").ends_with("/project/README.md"));
    }

    #[test]
    fn bypass_mode_allows_any_path() {
        let result = try_resolve_within_root("/project", "/etc/passwd", true, None);
        assert!(result.is_ok());
    }

    #[test]
    fn sandbox_blocks_traversal_out_of_the_root() {
        let root = std::env::current_dir().unwrap();
        let root_str = root.to_string_lossy().into_owned();

        for escape in [
            "../../../../../../etc/passwd",
            "../outside.txt",
            "subdir/../../outside.txt",
        ] {
            assert!(
                try_resolve_within_root(&root_str, escape, false, None).is_err(),
                "{escape} escaped the sandbox"
            );
        }
    }

    #[test]
    fn sandbox_blocks_an_absolute_path_outside_the_root() {
        let root = std::env::current_dir().unwrap();
        let root_str = root.to_string_lossy().into_owned();
        let outside = if cfg!(windows) {
            r"C:\Windows\System32\drivers\etc\hosts"
        } else {
            "/etc/passwd"
        };

        assert!(try_resolve_within_root(&root_str, outside, false, None).is_err());
    }

    #[test]
    fn containment_is_checked_at_a_path_boundary_not_by_prefix() {
        let root = std::env::temp_dir().join("coda-sandbox-proj");
        let sibling = std::env::temp_dir().join("coda-sandbox-projevil");

        assert!(!is_within_root(
            &root.to_string_lossy(),
            &sibling.to_string_lossy()
        ));
        assert!(is_within_root(
            &root.to_string_lossy(),
            &root.join("inner/file.txt").to_string_lossy()
        ));
    }

    #[test]
    fn a_granted_directory_widens_the_sandbox_only_to_itself() {
        let root = std::env::temp_dir().join("coda-sandbox-root");
        let granted = std::env::temp_dir().join("coda-sandbox-granted");
        let elsewhere = std::env::temp_dir().join("coda-sandbox-elsewhere");
        let granted_list: std::collections::HashSet<String> =
            [granted.to_string_lossy().into_owned()].into_iter().collect();

        let root_str = root.to_string_lossy().into_owned();
        assert!(try_resolve_within_root(
            &root_str,
            &granted.join("file.txt").to_string_lossy(),
            false,
            Some(&granted_list),
        )
        .is_ok());

        assert!(
            try_resolve_within_root(
                &root_str,
                &elsewhere.join("file.txt").to_string_lossy(),
                false,
                Some(&granted_list),
            )
            .is_err(),
            "a granted directory must not open unrelated paths"
        );
    }

    #[test]
    fn excess_parent_segments_clamp_at_the_root_instead_of_going_relative() {
        let normalized = normalize_path(Path::new(if cfg!(windows) {
            r"C:\a\..\..\..\b"
        } else {
            "/a/../../../b"
        }));

        assert!(
            normalized.is_absolute(),
            "{} lost its root",
            normalized.display()
        );
    }

    #[test]
    fn path_inside_root_is_allowed() {
        let root = std::env::current_dir().unwrap().to_string_lossy().into_owned();
        let inside = format!("{root}/subdir/file.txt");
        let result = try_resolve_within_root(&root, &inside, false, None);
        assert!(result.is_ok());
    }
}
