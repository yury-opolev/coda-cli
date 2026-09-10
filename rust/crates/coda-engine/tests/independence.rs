//! Guards `coda-engine`'s core-only claim: no TUI/render/clipboard/agent-
//! frontend crate is reachable through its **normal** (non-dev, non-build)
//! dependency graph. This is what makes "TUI-free" a checkable fact rather
//! than an assertion in a doc comment.
//!
//! Checks normal dependencies (`-e normal`), which are linked into the
//! executable. Cross-binary checks live in coda/tests/engine_parity.rs.

use std::process::Command;

/// The crates a genuinely core-only engine must never link. Each one pulls
/// in a terminal renderer, clipboard, or image codec — none of which a
/// headless JSON-RPC-over-stdio engine needs.
const BANNED: &[&str] = &["coda-tui", "coda-render", "ratatui", "crossterm", "arboard", "png"];

fn workspace_root() -> std::path::PathBuf {
    // crates/coda-engine/tests -> crates/coda-engine -> crates -> rust
    std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .and_then(|p| p.parent())
        .expect("rust/crates/coda-engine has two ancestors")
        .to_path_buf()
}

#[test]
fn coda_engine_never_resolves_a_tui_render_or_image_crate_on_its_normal_deps() {
    assert_independent("coda-engine", BANNED);
}

#[test]
fn bootstrap_never_depends_on_engine_execution_or_tui_crates() {
    assert_independent("coda-boot", &["coda-agent", "coda-serve", "coda-tui", "coda-render"]);
}

fn assert_independent(package: &str, banned_crates: &[&str]) {
    let cargo = std::env::var("CARGO").unwrap_or_else(|_| "cargo".to_string());
    let root = workspace_root();

    let output = Command::new(&cargo)
        .args([
            "tree",
            "-p",
            package,
            "-e",
            "normal",
            "--prefix",
            "none",
            "--locked",
        ])
        .current_dir(&root)
        .output()
        .expect("cargo tree runs");

    assert!(
        output.status.success(),
        "cargo tree failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );

    let tree = String::from_utf8_lossy(&output.stdout);
    for banned in banned_crates {
        assert!(
            !tree.lines().any(|line| {
                let name = line.split_whitespace().next().unwrap_or("");
                name == *banned
            }),
            "{package}'s normal dependency tree must not contain '{banned}', but it does:\n{tree}"
        );
    }
}
