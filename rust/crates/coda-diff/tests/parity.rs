//! The two engines must answer identically.
//!
//! Skips unless both are available: `coda` on PATH for the C# engine, and the
//! Rust engine from `CODA_RUST_ENGINE` or the default release build. The C#
//! engine is being removed, so its absence must not turn the suite red.

use coda_diff::{compare, deterministic_scenario, find_engine, EngineUnderTest, KNOWN_GAPS};

/// Locates the Rust engine: an explicit override, else the release build.
fn rust_engine() -> Option<std::ffi::OsString> {
    if let Ok(explicit) = std::env::var("CODA_RUST_ENGINE") {
        return find_engine(&explicit);
    }
    let built = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../target/release/coda.exe")
        .canonicalize()
        .ok()?;
    find_engine(built.to_str()?)
}

/// Every exchange must match, except the gaps we have explicitly declared.
///
/// The test fails on **two** conditions, and the second matters as much as the
/// first: an undeclared divergence means a regression, and a declared gap that
/// no longer diverges means the list is stale. Without that second check the
/// known-gaps list would quietly decay into a permanent excuse.
#[tokio::test(flavor = "multi_thread")]
async fn both_engines_answer_the_deterministic_scenario_identically() {
    let Some(csharp_path) = find_engine("coda") else {
        eprintln!("skipping: no C# `coda` engine on PATH");
        return;
    };
    let Some(rust_path) = rust_engine() else {
        eprintln!("skipping: no Rust engine; run `cargo build --release -p coda`");
        return;
    };

    let csharp = EngineUnderTest::start(&csharp_path).await.expect("start the C# engine");
    let rust = EngineUnderTest::start(&rust_path).await.expect("start the Rust engine");

    let steps = deterministic_scenario();
    let mismatches = compare(&csharp, &rust, &steps).await.expect("run the scenario");

    csharp.shutdown().await;
    rust.shutdown().await;

    let known: Vec<&str> = KNOWN_GAPS.iter().map(|(method, _)| *method).collect();

    let unexpected: Vec<_> =
        mismatches.iter().filter(|(_, method, _, _)| !known.contains(method)).collect();

    let diverged: Vec<&str> = mismatches.iter().map(|(_, method, _, _)| *method).collect();
    let closed: Vec<_> =
        KNOWN_GAPS.iter().filter(|(method, _)| !diverged.contains(method)).collect();

    let mut report = String::new();

    if !unexpected.is_empty() {
        report.push_str(&format!(
            "{} undeclared divergence(s) between the C# and Rust engines:\n\n",
            unexpected.len()
        ));
        for (index, method, left, right) in &unexpected {
            report.push_str(&format!(
                "step {index} — {method}\n  C#:   {left:?}\n  Rust: {right:?}\n\n"
            ));
        }
    }

    if !closed.is_empty() {
        report.push_str(
            "These methods are listed in KNOWN_GAPS but now agree — remove them from the \
             list so it keeps meaning something:\n",
        );
        for (method, why) in &closed {
            report.push_str(&format!("  {method}  (was: {why})\n"));
        }
    }

    assert!(report.is_empty(), "{report}");
}
