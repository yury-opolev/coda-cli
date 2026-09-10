//! The product version, shared with the C# build via `version.json`.
//!
//! This is the single source `coda`, `coda-tui` and `coda-engine` all report
//! from — `coda_tui::branding::version()` delegates here rather than keeping
//! its own copy, so CLI branding and the headless core always agree.

/// The crate version, matching the C# `Branding.Version`.
pub fn version() -> &'static str {
    // Set by build.rs from the repository's version.json.
    env!("CODA_VERSION")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn version_is_not_empty_and_has_three_dotted_parts() {
        let v = version();
        assert!(!v.is_empty());
        assert_eq!(v.split('.').count(), 3, "expected major.minor.build, got {v}");
    }
}
