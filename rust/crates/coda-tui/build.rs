//! Stamps the build with the version from the repository's `version.json`.
//!
//! `version.json` is shared with the C# build, so the Rust binary continues the
//! same version line rather than restarting at the crate's own `0.1.0`. A
//! restart would read as a *downgrade* to anything comparing versions, and
//! would be baffling for anyone moving across from the C# build.
//!
//! Reading the file here rather than taking it from an environment variable
//! means a plain `cargo build` reports the right version too, not just builds
//! driven through `build.ps1`.

use std::path::PathBuf;

fn main() {
    let manifest = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap());
    let version_file = manifest
        .join("..")
        .join("..")
        .join("..")
        .join("version.json");

    println!("cargo:rerun-if-changed={}", version_file.display());

    let version = std::fs::read_to_string(&version_file)
        .ok()
        .and_then(|raw| parse_version(&raw))
        // Fall back to the crate version rather than failing the build: a
        // missing version.json should not make the tree unbuildable, for
        // example in a source export that ships only the `rust/` directory.
        .unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_string());

    println!("cargo:rustc-env=CODA_VERSION={version}");
}

/// Pulls `major.minor.build` out of `version.json`.
///
/// Hand-rolled rather than pulling `serde_json` in as a build dependency: the
/// file has a fixed three-integer shape that this crate also generates, so a
/// full parser buys nothing and costs build time on every clean build.
fn parse_version(raw: &str) -> Option<String> {
    let field = |name: &str| -> Option<i64> {
        let start = raw.find(&format!("\"{name}\""))? + name.len() + 2;
        let rest = &raw[start..];
        let colon = rest.find(':')? + 1;
        rest[colon..]
            .trim_start()
            .split(|c: char| !c.is_ascii_digit())
            .next()
            .filter(|digits| !digits.is_empty())
            .and_then(|digits| digits.parse().ok())
    };

    Some(format!(
        "{}.{}.{}",
        field("major")?,
        field("minor")?,
        field("build")?
    ))
}
