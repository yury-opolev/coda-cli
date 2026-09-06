//! Opt-in differential test: the legacy C# engine and the Rust engine must
//! answer identically on the protocol they share.
//!
//! # Why this test is `#[ignore]` by default
//!
//! It needs an external, built artifact — the legacy C# `Coda.Tui` engine —
//! that a routine `cargo test --workspace` cannot assume is present. The old
//! design "skipped" (returned success) when an engine was missing, which is a
//! comparison that silently proves nothing while reporting green. That is worse
//! than not running: it masquerades as parity evidence.
//!
//! So the routine suite does **not** run this test at all (it is reported as
//! ignored, honestly), and running it is an explicit opt-in:
//!
//! ```text
//! # 1. Build the legacy reference into an isolated directory (no version bump,
//! #    primary publish/tool outputs untouched):
//! dotnet publish src\Coda.Tui\Coda.Tui.csproj -c Release -o artifacts\legacy-parity
//!
//! # 2. Build the Rust engine:
//! cargo build --release -p coda
//!
//! # 3. Run the parity test, pointing it at the two explicit artifacts (from
//! #    the rust\ directory, so the default Rust engine path resolves):
//! $env:CODA_CSHARP_ENGINE = "..\artifacts\legacy-parity\Coda.Tui.exe"
//! cargo test -p coda-diff --test parity -- --ignored --nocapture
//! ```
//!
//! When opted in, it **fails loudly** if the reference is absent or
//! misconfigured — a missing reference is a setup error, never a pass.
//!
//! # Isolation
//!
//! Both engines run inside a single [`IsolatedProfile`]: an empty temp home
//! (`CODA_HOME`/`CODA_SETTINGS_DIR`) with only a fixture `settings.json` and a
//! fixture model catalogue (`CODA_MODELS_PATH`), the network model refresh
//! disabled (`CODA_DISABLE_MODELS_FETCH`), and every inherited credential/serve
//! override stripped from the child environment. The empty credential directory
//! means neither engine finds a credential, so neither builds a live client or
//! reaches an OS keyring or the network. The isolation is then *verified*, not
//! assumed (see `assert_isolation`).

use coda_diff::{
    artifacts_are_identical, compare, csharp_reference, deterministic_scenario, rust_engine,
    rust_only_scenario, validate_extension_schema, EngineUnderTest, IsolatedProfile, Step,
    StepOutcome, FIXTURE_CATALOG_MODEL_IDS, FIXTURE_MODEL, FIXTURE_PROVIDER, KNOWN_GAPS,
};
use serde_json::{json, Value};

/// The common protocol must match exactly, the identity guard must hold, and
/// the Rust-only surface must diverge in exactly the declared way.
///
/// One test drives everything because the two engines are expensive to start
/// and the sub-checks share them.
#[tokio::test(flavor = "multi_thread")]
#[ignore = "differential parity: opt in with --ignored after building the C# reference \
            (set CODA_CSHARP_ENGINE) and the Rust engine"]
async fn both_engines_answer_the_shared_protocol_identically() {
    // Resolve both engines explicitly. `expect` (not skip) so an opted-in run
    // with a missing or mis-set reference fails loudly with a clear message.
    let csharp_spec = csharp_reference().expect("resolve the C# reference (CODA_CSHARP_ENGINE)");
    let rust_spec = rust_engine().expect("resolve the Rust engine");

    // Identity guard: refuse to compare an engine against itself. Both share
    // version.json, so this is deliberately by artifact identity, not version.
    let identical = artifacts_are_identical(&csharp_spec.identity, &rust_spec.identity)
        .expect("fingerprint both engines");
    assert!(
        !identical,
        "the C# reference ({}) and the Rust engine ({}) are the same artifact — a comparison \
         of an engine against itself proves nothing",
        csharp_spec.identity.display(),
        rust_spec.identity.display()
    );
    eprintln!("C#   reference: {}", csharp_spec.identity.display());
    eprintln!("Rust engine:    {}", rust_spec.identity.display());

    // One hermetic profile shared by both engines: identical deterministic
    // inputs, empty credential store, fixture catalogue, no network.
    let profile = IsolatedProfile::create().expect("create the isolated profile");
    eprintln!("Isolated home:  {}", profile.home().display());

    let csharp = EngineUnderTest::start(&csharp_spec, &profile).await.expect("start the C# engine");
    let rust = EngineUnderTest::start(&rust_spec, &profile).await.expect("start the Rust engine");

    let mut report = String::new();

    // ── 0. Isolation is verified, not assumed. A leak into the real profile, a
    //       live client, or a network fetch would change the source, the active
    //       model, or the listed ids. ──────────────────────────────────────────
    assert_isolation(&csharp, "C#", &mut report).await;
    assert_isolation(&rust, "Rust", &mut report).await;

    // Applying a valid effort level to an *indeterminate*-capability model is a
    // documented, directional divergence — pinned so a change on either side is
    // caught rather than silently absorbed.
    assert_indeterminate_effort_divergence(&csharp, &rust, &mut report).await;

    // ── 1. Common protocol: exact equality after projecting Rust extensions ──
    let steps = deterministic_scenario();
    let mismatches = compare(&csharp, &rust, &steps).await.expect("run the scenario");

    let known: Vec<&str> = KNOWN_GAPS.iter().map(|(method, _)| *method).collect();
    let unexpected: Vec<_> =
        mismatches.iter().filter(|(_, method, _, _)| !known.contains(method)).collect();
    let diverged: Vec<&str> = mismatches.iter().map(|(_, method, _, _)| *method).collect();
    let closed: Vec<_> =
        KNOWN_GAPS.iter().filter(|(method, _)| !diverged.contains(method)).collect();

    // ── 2. Rust-only surface: the C# engine must NOT implement these; the Rust
    //       engine must answer with a well-shaped result. ─────────────────────
    let mut extension_report = String::new();
    for step in rust_only_scenario() {
        let cs = csharp.run(&step).await.expect("C# rust-only step");
        let rs = rust.run(&step).await.expect("Rust rust-only step");
        if !matches!(cs, StepOutcome::Error { code: -32601 }) {
            extension_report.push_str(&format!(
                "{}: expected the C# engine to report method-not-found (-32601), got {cs:?}\n",
                step.method
            ));
        }
        match &rs {
            StepOutcome::Ok(v) => {
                if v.get("ok").and_then(Value::as_bool) != Some(true) {
                    extension_report
                        .push_str(&format!("{}: Rust result missing ok:true — {rs:?}\n", step.method));
                }
                if !v.get("cleared").map(Value::is_boolean).unwrap_or(false) {
                    extension_report.push_str(&format!(
                        "{}: Rust result missing boolean `cleared` — {rs:?}\n",
                        step.method
                    ));
                }
            }
            other => extension_report
                .push_str(&format!("{}: expected a Rust result, got {other:?}\n", step.method)),
        }
    }

    // ── 3. Extension schema: the additive Rust fields must be present and
    //       well-typed on the Rust engine (they are projected out of the common
    //       comparison, so this is where they are actually verified). ─────────
    assert_rust_extension_schema(&rust, &mut extension_report).await;

    csharp.shutdown().await;
    rust.shutdown().await;

    // ── Assemble one report so a failure names every problem at once. ────────
    if !unexpected.is_empty() {
        report.push_str(&format!(
            "{} undeclared divergence(s) on the shared protocol:\n\n",
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
    if !extension_report.is_empty() {
        report.push_str("\nRust-extension contract problems:\n");
        report.push_str(&extension_report);
    }

    assert!(report.is_empty(), "{report}");
}

/// Verifies the additive fields the Rust engine sends have the right *shape*.
///
/// These fields are projected out of the common comparison, so without this
/// they would be neither compared nor checked. This restores the guarantee:
/// they are extensions, and they are *verified*, not merely tolerated.
async fn assert_rust_extension_schema(rust: &EngineUnderTest, report: &mut String) {
    for (method, params) in [
        ("model/reasoningCapability", json!({})),
        ("session/setEffort", json!({ "effort": "medium" })),
        ("session/models", json!({ "refresh": false })),
    ] {
        let outcome = rust.run(&Step::new(method, Some(params))).await.expect(method);
        match &outcome {
            StepOutcome::Ok(v) => {
                for problem in validate_extension_schema(method, v) {
                    report.push_str(&problem);
                    report.push('\n');
                }
            }
            other => report.push_str(&format!("{method}: expected a result, got {other:?}\n")),
        }
    }
}

/// Pins the one deliberate, model-capability-dependent divergence on
/// `session/setEffort`.
///
/// Applying a *valid* effort level (`medium`) to the active model requires
/// knowing that model's reasoning capability. Under the isolated profile the
/// `github-copilot` model is *indeterminate* (no live model list confirms its
/// levels), and the two engines legitimately differ:
///
/// * the **C# engine won't apply a level it cannot verify** → `ok:false`;
/// * the **Rust engine is optimistic under indeterminacy** → `ok:true`, and
///   reports the level it applied.
///
/// This is documented in `rust/README.md`. Pinning it here means a *change* to
/// either engine's behaviour fails loudly, rather than the difference being
/// swept under a blanket exclusion.
async fn assert_indeterminate_effort_divergence(
    csharp: &EngineUnderTest,
    rust: &EngineUnderTest,
    report: &mut String,
) {
    let step = Step::new("session/setEffort", Some(json!({ "effort": "medium" })));

    let cs = csharp.run(&step).await.expect("C# setEffort");
    if let StepOutcome::Ok(v) = &cs {
        if v.get("ok").and_then(Value::as_bool) != Some(false) {
            report.push_str(&format!(
                "session/setEffort(medium): expected C# to decline (ok:false) for an \
                 indeterminate model, got {cs:?}\n"
            ));
        }
    } else {
        report.push_str(&format!("session/setEffort(medium): expected a C# result, got {cs:?}\n"));
    }

    let rs = rust.run(&step).await.expect("Rust setEffort");
    if let StepOutcome::Ok(v) = &rs {
        if v.get("ok").and_then(Value::as_bool) != Some(true) {
            report.push_str(&format!(
                "session/setEffort(medium): expected Rust to apply optimistically (ok:true) for \
                 an indeterminate model, got {rs:?}\n"
            ));
        }
    } else {
        report.push_str(&format!("session/setEffort(medium): expected a Rust result, got {rs:?}\n"));
    }
}

/// Verifies the isolation contract on one engine: a fully offline, no-auth run
/// bound to the fixture profile. A leak into the real profile, a live client, or
/// a network fetch would break one of these.
async fn assert_isolation(engine: &EngineUnderTest, label: &str, report: &mut String) {
    let outcome = engine
        .run(&Step::new("session/models", Some(json!({ "refresh": false }))))
        .await
        .expect("session/models");
    let StepOutcome::Ok(v) = &outcome else {
        report.push_str(&format!("{label}: session/models did not return a result: {outcome:?}\n"));
        return;
    };

    // Offline, no-auth: the list must come from the catalogue, never a live
    // (network/credentialed) fetch.
    match v.get("source").and_then(Value::as_str) {
        Some("catalog") => {}
        other => report.push_str(&format!(
            "{label}: session/models source is {other:?}, expected \"catalog\" — a live client or \
             network fetch means auth/network was not isolated\n"
        )),
    }

    // The active model is the sentinel from the FIXTURE settings — proof the
    // engine read the isolated profile, not the developer's real ~/.coda.
    match v.get("model").and_then(Value::as_str) {
        Some(FIXTURE_MODEL) => {}
        other => report.push_str(&format!(
            "{label}: active model is {other:?}, expected the fixture sentinel {FIXTURE_MODEL:?} — \
             the engine read a settings file other than the isolated one\n"
        )),
    }

    match v.get("providerId").and_then(Value::as_str) {
        Some(FIXTURE_PROVIDER) => {}
        other => report.push_str(&format!(
            "{label}: providerId is {other:?}, expected the fixture provider {FIXTURE_PROVIDER:?}\n"
        )),
    }

    // Exactly the fixture catalogue's ids — nothing from the real machine.
    let mut ids: Vec<&str> = v
        .get("models")
        .and_then(Value::as_array)
        .map(|rows| rows.iter().filter_map(|m| m.get("id").and_then(Value::as_str)).collect())
        .unwrap_or_default();
    ids.sort_unstable();
    if ids != FIXTURE_CATALOG_MODEL_IDS {
        report.push_str(&format!(
            "{label}: model ids {ids:?} != fixture {FIXTURE_CATALOG_MODEL_IDS:?} — the catalogue was \
             not pinned to the fixture (real-machine models leaked in)\n"
        ));
    }
}
