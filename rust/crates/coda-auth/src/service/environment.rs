//! The environment seam.
//!
//! Provider selection and login both read process environment variables
//! (`ANTHROPIC_API_KEY`, the `GH_COPILOT_*` overrides). Reading them through a
//! port keeps two promises: a test never depends on — or mutates — the
//! developer's real environment, and the service can be pointed at an explicit
//! set of variables when a host has already resolved them.

use std::collections::HashMap;

/// Reads environment variables.
pub trait AuthEnvironment: Send + Sync + std::fmt::Debug {
    /// The value of `name`, or `None` when it is unset or empty.
    fn var(&self, name: &str) -> Option<String>;
}

/// The real process environment.
#[derive(Debug, Default, Clone, Copy)]
pub struct ProcessEnvironment;

impl AuthEnvironment for ProcessEnvironment {
    fn var(&self, name: &str) -> Option<String> {
        std::env::var(name).ok().filter(|value| !value.trim().is_empty())
    }
}

/// An explicit set of variables, for tests and for hosts that resolved them
/// elsewhere.
///
/// `Debug` prints the names only. The values here are the same material the
/// real environment carries — `ANTHROPIC_API_KEY` above all — and a fixture
/// that prints itself into a failure message is a secret in a log.
#[derive(Default)]
pub struct MapEnvironment {
    values: HashMap<String, String>,
    reads: std::sync::atomic::AtomicUsize,
}

impl std::fmt::Debug for MapEnvironment {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let mut names: Vec<&str> = self.values.keys().map(String::as_str).collect();
        names.sort_unstable();
        f.debug_struct("MapEnvironment")
            .field("names", &names)
            .field("values", &"[REDACTED]")
            .finish()
    }
}

impl MapEnvironment {
    pub fn new(pairs: &[(&str, &str)]) -> Self {
        Self {
            values: pairs
                .iter()
                .map(|(key, value)| ((*key).to_owned(), (*value).to_owned()))
                .collect(),
            reads: std::sync::atomic::AtomicUsize::new(0),
        }
    }

    /// How many lookups were made — proof that the service consults the port
    /// rather than the process.
    pub fn reads(&self) -> usize {
        self.reads.load(std::sync::atomic::Ordering::SeqCst)
    }
}

impl AuthEnvironment for MapEnvironment {
    fn var(&self, name: &str) -> Option<String> {
        self.reads.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        self.values.get(name).cloned().filter(|value| !value.trim().is_empty())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_fixture_environment_never_prints_its_values() {
        let environment = MapEnvironment::new(&[("ANTHROPIC_API_KEY", "sk-ant-SECRET-VALUE")]);
        let rendered = format!("{environment:?}");
        assert!(!rendered.contains("sk-ant-SECRET-VALUE"), "{rendered}");
        assert!(rendered.contains("ANTHROPIC_API_KEY"), "names are still useful: {rendered}");
    }

    #[test]
    fn a_blank_variable_reads_as_unset() {
        let environment = MapEnvironment::new(&[("ANTHROPIC_API_KEY", "   ")]);
        assert_eq!(environment.var("ANTHROPIC_API_KEY"), None);
        assert_eq!(environment.reads(), 1);
    }
}
