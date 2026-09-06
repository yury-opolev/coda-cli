//! Differential testing: the legacy C# engine and the Rust engine must answer
//! identically on the protocol they share.
//!
//! Unit tests prove each engine behaves as *its own* tests expect. That is not
//! the same as the two agreeing, and a test written beside an implementation
//! tends to encode that implementation's assumptions. This port has already
//! produced a concrete example: the Rust engine reported
//! `serverInfo: "coda-serve"` where the C# reports `"coda"`, and the Rust unit
//! test had pinned the wrong value — so it agreed with the bug. Only running
//! the *same* exchange against *both* engines exposed it.
//!
//! This harness sends an identical request sequence to both and compares the
//! normalised responses. It is the strongest parity evidence available, and it
//! is only available while both engines exist.
//!
//! # The two engines are found explicitly — never by PATH
//!
//! The installed `coda` on `PATH` is now the **Rust** engine. Resolving the C#
//! reference from `PATH` would therefore compare the Rust engine against
//! itself and call it parity. So the reference is taken **only** from
//! `CODA_CSHARP_ENGINE`, must be a genuine .NET `Coda.Tui` artifact
//! ([`looks_like_dotnet_reference`]), and must not be the same file as the Rust
//! engine ([`artifacts_are_identical`]). Both engines share `version.json`, so
//! an equal `--version` is *expected* and is never used to tell them apart.
//!
//! # Scope
//!
//! Deliberately limited to the **deterministic** surface: the handshake,
//! history, models, interrupt, steering, goals, effort, schedules, listings
//! and error codes. Anything that requires a live model is excluded, because a
//! provider's output is not reproducible and a flaky parity test is worse than
//! none — it teaches you to ignore red.
//!
//! # Additive Rust extensions
//!
//! The Rust engine is a *superset* of the C# contract on a few methods: it adds
//! fields the older C# engine never sent (see [`RUST_EXTENSIONS`]). These are
//! deliberate, permanent protocol extensions — not a version lag and not a
//! blanket "ignore extras". The common contract is compared for exact equality
//! after the declared extension fields are projected away
//! ([`project_common`]); the extension fields themselves are verified for shape
//! by the extension schema tests in `tests/parity.rs`. Anything undeclared
//! still fails loudly, and every field the C# engine emits must still be
//! reproduced exactly.

use std::ffi::{OsStr, OsString};
use std::path::{Path, PathBuf};
use std::time::Duration;

use anyhow::{bail, Context, Result};
use coda_client::{Engine, EngineCommand};
use serde_json::{json, Value};

/// How long any single request may take before the comparison is abandoned.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(60);

/// One request in a scenario.
#[derive(Debug, Clone)]
pub struct Step {
    pub method: &'static str,
    pub params: Option<Value>,
}

impl Step {
    pub fn new(method: &'static str, params: Option<Value>) -> Self {
        Self { method, params }
    }
}

/// The outcome of one step, reduced to what parity actually requires.
///
/// A successful result is compared structurally; an error is compared by
/// **code only**. Error *messages* are prose and legitimately differ between
/// implementations — requiring them to match would pin wording rather than
/// behaviour, and would fail on a harmless rephrasing.
#[derive(Debug, Clone, PartialEq)]
pub enum StepOutcome {
    Ok(Value),
    Error { code: i64 },
}

/// Replaces values that legitimately differ between two runs, **without**
/// erasing values that carry meaning.
///
/// The distinction this function draws is the whole point of it:
///
/// * **Volatile identifiers** — a *generated* session id, task id, turn id or
///   schedule id — are replaced by a marker, because they are freshly minted on
///   every run and comparing them would fail regardless of parity. They are
///   *replaced*, not deleted, so a field present in one engine and absent in the
///   other is still caught.
///
/// * **A model id, entity name, `displayName`, `description` or `summary` is
///   NOT volatile** — it is the static, meaningful value the comparison exists
///   to check. Erasing `id` wholesale (as an earlier version did) would let a
///   Rust engine that renamed `claude-opus-5` to anything at all still "match".
///   So `id` is only treated as volatile on the methods that mint one
///   ([`method_volatile_ids`]); on `session/models` it is compared literally.
///
/// * **Free prose** — `note`, `message`, `reason`, `error` — is wording that may
///   legitimately differ between implementations, so non-empty prose collapses
///   to a marker. `description`/`summary` are deliberately *not* in this set:
///   they are structured discriminators and are compared literally. An **empty
///   string stays empty**, because "" and "absent" are different on the wire and
///   that distinction has already caught a real bug here.
///
/// Callers should use [`normalize_for`], which additionally scopes the volatile
/// ids to the method that produced the value.
pub fn normalize(value: &Value) -> Value {
    normalize_inner(value, &[])
}

/// Volatile identifiers that are generated fresh on every run regardless of the
/// method. Notably this list does **not** contain a bare `id`: a model id is
/// static and meaningful, and blanking it would defeat the comparison.
const VOLATILE_ALWAYS: &[&str] = &[
    "sessionId",
    "messageId",
    "taskId",
    "activeTaskId",
    "rootTurnId",
    "activityId",
    "callId",
    "sourceId",
    "timestamp",
    "enqueuedAt",
    "nextRunUtc",
    "elapsedMs",
    "elapsedSeconds",
    "telemetryLogPath",
];

/// Prose fields whose exact wording is not behaviour. `description`/`summary`
/// are intentionally absent — they are compared literally as discriminators.
const PROSE: &[&str] = &["note", "message", "reason", "error"];

/// The extra keys that are volatile *for a given method* — a generated `id`
/// returned by the schedule surface, or the ids threaded through the history and
/// message listings. On every other method (notably `session/models`) `id` is a
/// static value and is compared literally.
fn method_volatile_ids(method: &str) -> &'static [&'static str] {
    match method {
        "schedule/create" | "schedule/list" | "schedule/delete" | "session/history"
        | "session/messages" => &["id"],
        _ => &[],
    }
}

/// [`normalize`] with the volatile-id set scoped to the method that produced the
/// value. This is what the harness uses; the bare [`normalize`] is kept for the
/// unit tests and treats only the always-volatile keys as volatile.
pub fn normalize_for(method: &str, value: &Value) -> Value {
    normalize_inner(value, method_volatile_ids(method))
}

fn normalize_inner(value: &Value, extra_volatile: &[&str]) -> Value {
    match value {
        Value::Object(map) => {
            let mut out = serde_json::Map::new();
            for (key, val) in map {
                if VOLATILE_ALWAYS.contains(&key.as_str()) || extra_volatile.contains(&key.as_str())
                {
                    out.insert(key.clone(), Value::String("<volatile>".into()));
                } else if PROSE.contains(&key.as_str()) {
                    let collapsed = match val.as_str() {
                        Some("") => Value::String(String::new()),
                        Some(_) => Value::String("<prose>".into()),
                        None => normalize_inner(val, extra_volatile),
                    };
                    out.insert(key.clone(), collapsed);
                } else {
                    out.insert(key.clone(), normalize_inner(val, extra_volatile));
                }
            }
            Value::Object(out)
        }
        Value::Array(items) => {
            Value::Array(items.iter().map(|v| normalize_inner(v, extra_volatile)).collect())
        }
        other => other.clone(),
    }
}

// ───────────────────────── engine launch + identity ─────────────────────────

/// How to launch one engine under test.
///
/// `program` + `leading_args` form the command prefix: a native binary is run
/// directly (`coda.exe`, empty `leading_args`); a managed `.dll` is run through
/// `dotnet` (`program = dotnet`, `leading_args = [<dll>]`). `identity` is the
/// artifact that establishes *which* engine this is, used by the identity
/// guard — for the C# reference that is the `.exe`/`.dll`, for Rust the binary.
#[derive(Debug, Clone)]
pub struct LaunchSpec {
    pub program: OsString,
    pub leading_args: Vec<OsString>,
    pub identity: PathBuf,
    /// When set, `serve` is started with `--provider <id>`.
    ///
    /// The C# engine resolves its provider *only* from `--provider` or a stored
    /// credential — a settings `defaultProvider` is deliberately not a selector
    /// — so with no credential it fails fast ("Not signed in") unless a provider
    /// is named. Under the isolated profile there is no credential (that is the
    /// point), so the C# engine is given the fixture provider explicitly. This
    /// is offline-safe: with an empty credential store the copilot client's
    /// `ListModelsAsync` throws locally before any HTTP call, so the model list
    /// still resolves from the catalogue. The Rust engine needs no flag — with
    /// no credential it simply builds no client — and is deliberately *not*
    /// given one, so it never runs the provider credential probe (which could
    /// reach the OS keyring).
    pub offline_provider: Option<&'static str>,
}

impl LaunchSpec {
    /// The Rust engine: run the binary directly.
    pub fn rust(binary: &Path) -> Self {
        Self {
            program: binary.as_os_str().to_owned(),
            leading_args: Vec::new(),
            identity: binary.to_owned(),
            offline_provider: None,
        }
    }

    /// The legacy C# engine. A `Coda.Tui` apphost (`.exe`) is run directly; a
    /// managed `.dll` is run through `dotnet`.
    pub fn legacy_dotnet(path: &Path) -> Self {
        let is_dll =
            path.extension().and_then(OsStr::to_str).is_some_and(|e| e.eq_ignore_ascii_case("dll"));
        let (program, leading_args) = if is_dll {
            (OsString::from("dotnet"), vec![path.as_os_str().to_owned()])
        } else {
            (path.as_os_str().to_owned(), Vec::new())
        };
        Self { program, leading_args, identity: path.to_owned(), offline_provider: Some(FIXTURE_PROVIDER) }
    }
}

/// True when `path` is a genuine .NET managed artifact — the legacy `Coda.Tui`
/// engine — established by **positive evidence**, never by file suffix alone.
///
/// A `.dll` (or `.exe`) suffix is trivially forgeable — the Rust `coda.exe`
/// could be copied to `Coda.Tui.exe` — so it is deliberately insufficient. Both
/// of these must hold:
///
/// 1. a sibling `Coda.Tui.runtimeconfig.json`, which only a *published* .NET app
///    emits; and
/// 2. the associated managed assembly is a valid **managed PE** — its PE header
///    carries a non-empty CLI (CLR) data directory. For a `.dll` reference that
///    is the file itself; for the native `Coda.Tui.exe` apphost (which is *not*
///    managed) it is the sibling `Coda.Tui.dll` the runtimeconfig belongs to.
///
/// This is what stops an accidentally-configured Rust binary from masquerading
/// as the C# reference and turning a self-comparison green.
pub fn looks_like_dotnet_reference(path: &Path) -> bool {
    if !has_runtimeconfig_sibling(path) {
        return false;
    }
    match managed_assembly_for(path) {
        Some(assembly) => is_managed_pe(&assembly),
        None => false,
    }
}

/// The published-.NET marker: a sibling `Coda.Tui.runtimeconfig.json`.
fn has_runtimeconfig_sibling(path: &Path) -> bool {
    let Some(parent) = path.parent() else {
        return false;
    };
    let Ok(entries) = std::fs::read_dir(parent) else {
        return false;
    };
    entries.filter_map(|e| e.ok()).any(|e| {
        e.file_name()
            .to_str()
            .is_some_and(|n| n.eq_ignore_ascii_case("Coda.Tui.runtimeconfig.json"))
    })
}

/// The managed assembly whose metadata proves this is .NET: the `.dll` itself,
/// or the sibling `<stem>.dll` next to a native apphost `.exe`.
fn managed_assembly_for(path: &Path) -> Option<PathBuf> {
    let ext = path.extension().and_then(OsStr::to_str);
    if ext.is_some_and(|e| e.eq_ignore_ascii_case("dll")) {
        return Some(path.to_owned());
    }
    let stem = path.file_stem()?;
    let mut dll = OsString::from(stem);
    dll.push(".dll");
    Some(path.with_file_name(dll))
}

/// True when `path` is a valid managed PE: its PE optional header carries a
/// non-empty CLI header (COM descriptor) data directory (index 14), the marker
/// every .NET assembly has and a native binary never does.
///
/// Parses only the headers — it never loads or executes the file.
fn is_managed_pe(path: &Path) -> bool {
    let Ok(bytes) = std::fs::read(path) else {
        return false;
    };
    matches!(clr_data_directory(&bytes), Some((rva, size)) if rva != 0 && size != 0)
}

/// Reads the (RVA, size) of PE data directory 14 (the CLR runtime header).
/// Returns `None` for anything that is not a well-formed PE.
fn clr_data_directory(b: &[u8]) -> Option<(u32, u32)> {
    let read_u16 = |o: usize| -> Option<u16> { Some(u16::from_le_bytes(b.get(o..o + 2)?.try_into().ok()?)) };
    let read_u32 = |o: usize| -> Option<u32> { Some(u32::from_le_bytes(b.get(o..o + 4)?.try_into().ok()?)) };

    if b.get(0..2)? != b"MZ" {
        return None;
    }
    let e_lfanew = read_u32(0x3C)? as usize;
    if b.get(e_lfanew..e_lfanew + 4)? != b"PE\0\0" {
        return None;
    }
    // COFF header is 20 bytes after the PE signature; the optional header follows.
    let opt = e_lfanew + 4 + 20;
    let magic = read_u16(opt)?;
    // Data directories begin after the optional header's fixed part: 96 bytes
    // for PE32 (magic 0x10b), 112 for PE32+ (0x20b).
    let data_dirs = match magic {
        0x10b => opt + 96,
        0x20b => opt + 112,
        _ => return None,
    };
    // Directory 14 (0-based) is the CLR runtime header; each entry is 8 bytes.
    let clr = data_dirs + 14 * 8;
    Some((read_u32(clr)?, read_u32(clr + 4)?))
}

/// A length-and-content fingerprint of a file. FNV-1a keeps this dependency-free.
fn fingerprint(path: &Path) -> Result<(u64, u64)> {
    let bytes = std::fs::read(path).with_context(|| format!("reading {}", path.display()))?;
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for b in &bytes {
        hash ^= u64::from(*b);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    Ok((bytes.len() as u64, hash))
}

/// True when two paths resolve to the same program — the same file on disk, or
/// byte-for-byte identical content.
///
/// The identity guard: if the "C#" reference and the "Rust" engine are the same
/// artifact, the comparison would prove nothing (an engine always agrees with
/// itself), so the harness must refuse to run rather than report a green suite.
pub fn artifacts_are_identical(a: &Path, b: &Path) -> Result<bool> {
    if let (Ok(ca), Ok(cb)) = (a.canonicalize(), b.canonicalize()) {
        if ca == cb {
            return Ok(true);
        }
    }
    Ok(fingerprint(a)? == fingerprint(b)?)
}

/// Confirms an engine launches, by running `<prefix> --version`.
///
/// Returns the exit success only; the version *string* is deliberately ignored
/// because both engines share `version.json` and report the same number.
fn engine_launches(spec: &LaunchSpec) -> bool {
    let status = std::process::Command::new(&spec.program)
        .args(&spec.leading_args)
        .arg("--version")
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status();
    matches!(status, Ok(s) if s.success())
}

/// Resolves the legacy C# reference from `CODA_CSHARP_ENGINE` — and nowhere else.
///
/// Fails loudly rather than skipping: an opted-in parity run with no reference
/// is a misconfiguration, not a pass.
pub fn csharp_reference() -> Result<LaunchSpec> {
    let raw = std::env::var_os("CODA_CSHARP_ENGINE").context(
        "CODA_CSHARP_ENGINE is not set. Point it at the built legacy reference, e.g. \
         artifacts\\legacy-parity\\Coda.Tui.exe (or Coda.Tui.dll). The `coda` on PATH is now \
         the Rust engine and must never be used as the C# reference.",
    )?;
    let path = PathBuf::from(&raw);
    if !path.exists() {
        bail!("CODA_CSHARP_ENGINE points at {}, which does not exist", path.display());
    }
    if !looks_like_dotnet_reference(&path) {
        bail!(
            "CODA_CSHARP_ENGINE points at {}, which is not a .NET Coda.Tui reference \
             (no sibling Coda.Tui.runtimeconfig.json and not a .dll). Refusing to compare \
             the Rust engine against a non-.NET binary that might be the Rust engine itself.",
            path.display()
        );
    }
    let spec = LaunchSpec::legacy_dotnet(&path);
    if !engine_launches(&spec) {
        bail!("the C# reference at {} did not run `--version` successfully", path.display());
    }
    Ok(spec)
}

/// Resolves the Rust engine: `CODA_RUST_ENGINE`, else the release build.
///
/// Fails loudly rather than skipping, for the same reason as the C# side.
pub fn rust_engine() -> Result<LaunchSpec> {
    let path = if let Some(explicit) = std::env::var_os("CODA_RUST_ENGINE") {
        PathBuf::from(explicit)
    } else {
        Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/release/coda.exe")
    };
    if !path.exists() {
        bail!(
            "Rust engine not found at {}. Build it with `cargo build --release -p coda`, or set \
             CODA_RUST_ENGINE to an explicit path.",
            path.display()
        );
    }
    let path = path.canonicalize().unwrap_or(path);
    let spec = LaunchSpec::rust(&path);
    if !engine_launches(&spec) {
        bail!("the Rust engine at {} did not run `--version` successfully", path.display());
    }
    Ok(spec)
}

/// A running engine under test.
pub struct EngineUnderTest {
    engine: Engine,
    connection: coda_client::Connection,
}

/// The provider both engines resolve to under the isolated fixture profile.
pub const FIXTURE_PROVIDER: &str = "github-copilot";

/// The active model both engines must report under the isolated fixture
/// profile. It is a **sentinel**: it appears in no real settings file, so if
/// either engine read the developer's real `~/.coda/settings.json` instead of
/// the isolated one, `session/models.model` would not be this value and the
/// isolation contract check would fail loudly.
pub const FIXTURE_MODEL: &str = "coda-parity-sentinel-model";

/// The model ids the fixture catalogue offers for [`FIXTURE_PROVIDER`], in
/// id-sorted order. Both engines must list exactly these (and nothing from the
/// real machine) when pinned to the fixture catalogue via `CODA_MODELS_PATH`.
pub const FIXTURE_CATALOG_MODEL_IDS: &[&str] = &["parity-model-alpha", "parity-model-beta"];

/// An isolated, throwaway Coda profile that forces a serve process off every
/// real user root and off auth/network.
///
/// # Why this exists
///
/// A parity comparison is only honest if it is *hermetic*. An earlier harness
/// merely gave each engine a fresh working directory and declared it isolated —
/// but `serve` reads far more than the working directory. Both engines probe
/// `~/.coda` (settings, credentials, the model cache, skills, plugins, hooks)
/// and, given a credential, will fetch models over the network. On Windows the
/// obvious redirect — `USERPROFILE` — does **not** work (both the Rust
/// `directories` crate and C# `SpecialFolder.UserProfile` resolve the profile
/// from the user token, verified empirically), so the real profile leaks in.
///
/// This profile closes every one of those seams:
///
/// * **Home / settings / credentials / cache / skills / plugins** are redirected
///   to an empty temp root via `CODA_HOME` (the Rust engine's profile-root
///   override) and `CODA_SETTINGS_DIR` (the C# settings seam). The credential
///   directory it creates is empty, so neither engine finds a stored credential
///   and neither builds a live client.
/// * **The model catalogue** is pinned to a small deterministic fixture via
///   `CODA_MODELS_PATH` (honored by both engines), and the background
///   models.dev refresh is disabled via `CODA_DISABLE_MODELS_FETCH`. With no
///   credential the model list resolves from this catalogue, so both report
///   `source: "catalog"` with the same ids — never a live, network-derived list.
/// * **Inherited credentials and configuration** (`ANTHROPIC_API_KEY`,
///   `CODA_SERVE_API_KEY`, every other `CODA_SERVE_*`) are *removed* from the
///   child environment — not merely blanked, because an empty variable is still
///   present and some readers treat that differently from absence.
///
/// The single fixture settings file names [`FIXTURE_PROVIDER`]/[`FIXTURE_MODEL`]
/// so both engines resolve the same active model deterministically (and so the
/// C# engine, which refuses to start with no provider and no credential, starts
/// at all).
///
/// The directory is removed on drop.
pub struct IsolatedProfile {
    home: PathBuf,
    catalog: PathBuf,
}

impl IsolatedProfile {
    /// Materialises the fixtures under a fresh temp home. Both engines share one
    /// profile so the deterministic values they read (settings, catalogue)
    /// are identical and a genuine divergence is not masked by different inputs.
    pub fn create() -> Result<Self> {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        let home = std::env::temp_dir().join(format!("coda-parity-home-{}-{stamp}", std::process::id()));
        let coda = home.join(".coda");
        // Create the state dirs the engines read, empty, so "no credential / no
        // skill / no plugin" is a real, isolated answer rather than the machine's.
        for sub in ["credentials", "skills", "plugins", "cache"] {
            std::fs::create_dir_all(coda.join(sub))
                .with_context(|| format!("creating isolated .coda/{sub}"))?;
        }
        let settings = format!(
            "{{\n  \"defaultProvider\": \"{FIXTURE_PROVIDER}\",\n  \"modelByProvider\": {{ \"{FIXTURE_PROVIDER}\": \"{FIXTURE_MODEL}\" }}\n}}\n"
        );
        std::fs::write(coda.join("settings.json"), settings)
            .context("writing isolated settings.json")?;

        // A models.dev-shaped catalogue with one provider and two models, in
        // id-sorted order, understood identically by both engines' parsers.
        let catalog = home.join("models-fixture.json");
        std::fs::write(&catalog, FIXTURE_CATALOG_JSON).context("writing fixture catalogue")?;

        Ok(Self { home, catalog })
    }

    /// The profile root (parent of `.coda`).
    pub fn home(&self) -> &Path {
        &self.home
    }

    /// The fixture catalogue file (`CODA_MODELS_PATH`).
    pub fn catalog(&self) -> &Path {
        &self.catalog
    }
}

impl Drop for IsolatedProfile {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.home);
    }
}

/// The fixture models.dev catalogue. One provider, two models, id-sorted, with
/// finite non-negative costs — the shape both engines' catalogue parsers accept.
const FIXTURE_CATALOG_JSON: &str = r#"{
  "github-copilot": {
    "models": {
      "parity-model-alpha": {
        "name": "Parity Model Alpha",
        "limit": { "context": 100000 },
        "cost": { "input": 1.0, "output": 2.0 }
      },
      "parity-model-beta": {
        "name": "Parity Model Beta",
        "limit": { "context": 200000 },
        "cost": { "input": 3.0, "output": 4.0 }
      }
    }
  }
}
"#;

/// The inherited environment variables a hermetic serve child must not see:
/// credentials and every serve override. Removed (not blanked) so absence is
/// real.
const STRIPPED_ENV: &[&str] = &[
    "ANTHROPIC_API_KEY",
    "CODA_SERVE_API_KEY",
    "CODA_SERVE_PROVIDER",
    "CODA_SERVE_MODEL",
    "CODA_SERVE_EFFORT",
    "CODA_SERVE_PERMISSION_MODE",
    "CODA_SERVE_SYSTEM_PROMPT",
    "CODA_SERVE_GOAL",
    "CODA_SERVE_GOAL_TIMEOUT",
    "CODA_SERVE_GOAL_MAX_CONTINUATIONS",
    "CODA_SERVE_ENDPOINT",
];

impl EngineUnderTest {
    /// Starts `<spec> serve --no-mcp` inside the [`IsolatedProfile`] and
    /// completes the handshake.
    ///
    /// Isolation is the whole point: the child is forced off every real user
    /// root (`CODA_HOME`/`CODA_SETTINGS_DIR`), off the network model refresh
    /// (`CODA_DISABLE_MODELS_FETCH`) and onto the fixture catalogue
    /// (`CODA_MODELS_PATH`), MCP is disabled (`--no-mcp` +
    /// `CODA_SERVE_DISABLE_MCP`/`CODA_DISABLE_PROJECT_MCP`), and all inherited
    /// credentials/overrides are stripped ([`STRIPPED_ENV`]). With an empty
    /// isolated credential directory neither engine finds a credential, so
    /// neither builds a live client — nothing here reaches a model, an OS
    /// keyring or the network. The scenario is purely deterministic.
    pub async fn start(spec: &LaunchSpec, profile: &IsolatedProfile) -> Result<Self> {
        // A distinct, filesystem-safe working directory per engine so neither
        // sees the other's project `.coda` state. The identity file stem is
        // sanitised because `Coda.Tui.exe` and `coda.exe` both contain
        // characters that are fine in a filename but must not be pasted raw
        // into a new path.
        let tag: String = spec
            .identity
            .file_stem()
            .and_then(OsStr::to_str)
            .unwrap_or("engine")
            .chars()
            .map(|c| if c.is_ascii_alphanumeric() { c } else { '_' })
            .collect();
        let workdir =
            std::env::temp_dir().join(format!("coda-diff-wd-{}-{tag}", std::process::id()));
        std::fs::create_dir_all(&workdir).ok();

        let mut command = EngineCommand::new(&spec.program)
            .args(spec.leading_args.iter().cloned())
            .arg("serve")
            .arg("--no-mcp");
        if let Some(provider) = spec.offline_provider {
            // The C# engine needs an explicit provider to start without a
            // credential; offline-safe (see `LaunchSpec::offline_provider`).
            command = command.arg("--provider").arg(provider);
        }
        command = command
            .working_dir(workdir)
            // Redirect every user root to the isolated profile.
            .env("CODA_HOME", profile.home().as_os_str().to_owned())
            .env("CODA_SETTINGS_DIR", profile.home().as_os_str().to_owned())
            // Pin the catalogue and forbid the background network refresh.
            .env("CODA_MODELS_PATH", profile.catalog().as_os_str().to_owned())
            .env("CODA_DISABLE_MODELS_FETCH", "1")
            // No MCP.
            .env("CODA_SERVE_DISABLE_MCP", "1")
            .env("CODA_DISABLE_PROJECT_MCP", "1");
        for key in STRIPPED_ENV {
            command = command.env_remove(*key);
        }

        let (engine, _inbound) = Engine::spawn(command).context("failed to spawn the engine")?;
        let connection = engine.connection();

        let params = serde_json::to_value(coda_proto::messages::InitializeParams::new("coda-diff"))
            .context("failed to serialise the handshake")?;
        tokio::time::timeout(
            REQUEST_TIMEOUT,
            connection.request(coda_proto::messages::method::INITIALIZE, Some(params)),
        )
        .await
        .context("initialize timed out")?
        .context("initialize failed")?;

        Ok(Self { engine, connection })
    }

    /// Runs one step and reduces it to a comparable outcome.
    pub async fn run(&self, step: &Step) -> Result<StepOutcome> {
        let result = tokio::time::timeout(
            REQUEST_TIMEOUT,
            self.connection.request(step.method, step.params.clone()),
        )
        .await
        .with_context(|| format!("{} timed out", step.method))?;

        Ok(match result {
            Ok(value) => StepOutcome::Ok(normalize_for(step.method, &value)),
            Err(coda_client::ClientError::Rpc(error)) => StepOutcome::Error { code: error.code },
            Err(other) => return Err(other).context("transport failure"),
        })
    }

    pub async fn shutdown(self) {
        let _ = self.engine.shutdown(Duration::from_secs(5)).await;
    }
}

// ─────────────────────────── additive extensions ────────────────────────────

/// A method on which the Rust engine sends more than the C# engine ever did.
///
/// `top_level` fields are added to the response object; `per_row` names an
/// array field whose elements carry extra fields (the model rows of
/// `session/models`). Everything else in the response is part of the *common*
/// contract and must match exactly.
#[derive(Debug, Clone, Copy)]
pub struct Extension {
    pub method: &'static str,
    pub top_level: &'static [&'static str],
    pub per_row: Option<(&'static str, &'static [&'static str])>,
}

/// The complete, declared set of Rust protocol extensions.
///
/// This is the honest replacement for the old `RUST_ADDITIONS` allowance, which
/// was justified as a temporary lag ("the C# source already matches, only the
/// installed tool is stale"). Rebuilding the reference from source disproved
/// that: the C# engine genuinely does **not** send these fields, so they are
/// permanent extensions, not lag.
///
/// Why this is not a blanket "ignore extras":
/// - the list is explicit and per-method — an extra field on any *other* method,
///   or an *undeclared* extra on these methods, still fails the comparison;
/// - every field the C# engine emits must still be reproduced by Rust exactly,
///   so this cannot hide a dropped or changed shared field;
/// - each extension is independently checked for shape by the extension schema
///   tests, so it is *verified*, not merely tolerated.
pub const RUST_EXTENSIONS: &[Extension] = &[
    // The model rows carry pricing and the reasoning levels the model accepts;
    // the active `model`/`providerId` at the top level are now COMMON (both
    // engines send them) and are therefore deliberately absent here so they are
    // compared normally.
    Extension {
        method: "session/models",
        top_level: &[],
        per_row: Some(("models", &["reasoningLevels", "inputCost", "outputCost", "effort"])),
    },
    // The picker needs the active level, whether the answer is only provisional
    // (`indeterminate`), and the canonical (model, providerId) it is keyed on.
    Extension {
        method: "model/reasoningCapability",
        top_level: &["current", "indeterminate", "model", "providerId"],
        per_row: None,
    },
    // The effective level actually in force after clamping — the C# engine only
    // echoes what was `applied`.
    Extension { method: "session/setEffort", top_level: &["current"], per_row: None },
];

/// Removes the declared Rust-extension fields from one outcome, leaving the
/// common contract.
///
/// Applied to the **Rust** side only. The C# side has none of these fields, so
/// if the C# engine ever *did* start sending one, projecting only Rust would
/// leave it present on the C# side and absent on Rust — a loud mismatch that
/// correctly forces the field to be reclassified as common rather than silently
/// swallowed.
pub fn project_common(method: &str, outcome: &StepOutcome) -> StepOutcome {
    let Some(ext) = RUST_EXTENSIONS.iter().find(|e| e.method == method) else {
        return outcome.clone();
    };
    let StepOutcome::Ok(value) = outcome else {
        return outcome.clone();
    };
    let mut value = value.clone();
    if let Some(obj) = value.as_object_mut() {
        for field in ext.top_level {
            obj.remove(*field);
        }
        if let Some((array, fields)) = ext.per_row {
            if let Some(Value::Array(rows)) = obj.get_mut(array) {
                for row in rows.iter_mut() {
                    if let Some(row_obj) = row.as_object_mut() {
                        for field in fields {
                            row_obj.remove(*field);
                        }
                    }
                }
            }
        }
    }
    StepOutcome::Ok(value)
}

/// Validates the shape of the declared Rust-extension fields for `method`.
///
/// Because the extension fields are *projected out* of the common comparison
/// ([`project_common`]), they would otherwise be neither compared nor checked —
/// a Rust engine could emit a garbage `inputCost` or a wrongly-typed `current`
/// and nothing would notice. This restores the guarantee: an extension is
/// *verified*, not merely tolerated.
///
/// Returns a list of human-readable problems; empty means valid. Pure, so the
/// live parity test and the unit tests share one definition. The rules mirror
/// what each field is documented to be in `RUST_EXTENSIONS`/`rust/README.md`:
///
/// * `model/reasoningCapability` — `current` (string|null), `indeterminate`
///   (bool), `model` (non-empty string) and `providerId` (non-empty string) are
///   **required**; a dropped one is an error.
/// * `session/setEffort` — `current` (string|null) is **required**.
/// * `session/models` per row — the extension fields are *optional* (present
///   only when applicable), but when present must be well-typed: `reasoningLevels`
///   an array of strings, `inputCost`/`outputCost` a finite non-negative number,
///   `effort` a string.
pub fn validate_extension_schema(method: &str, value: &Value) -> Vec<String> {
    let mut problems = Vec::new();
    match method {
        "model/reasoningCapability" => {
            require_string_or_null(value, "current", method, &mut problems);
            require_bool(value, "indeterminate", method, &mut problems);
            require_nonempty_string(value, "model", method, &mut problems);
            require_nonempty_string(value, "providerId", method, &mut problems);
        }
        "session/setEffort" => {
            require_string_or_null(value, "current", method, &mut problems);
        }
        "session/models" => {
            let Some(rows) = value.get("models").and_then(Value::as_array) else {
                problems.push("session/models: `models` is not an array".into());
                return problems;
            };
            for (i, row) in rows.iter().enumerate() {
                validate_model_row(i, row, &mut problems);
            }
        }
        _ => {}
    }
    problems
}

fn validate_model_row(i: usize, row: &Value, problems: &mut Vec<String>) {
    // reasoningLevels: optional; when present an array of strings.
    if let Some(levels) = row.get("reasoningLevels") {
        match levels.as_array() {
            Some(items) => {
                if !items.iter().all(Value::is_string) {
                    problems.push(format!(
                        "session/models[{i}]: reasoningLevels must be an array of strings"
                    ));
                }
            }
            None => problems
                .push(format!("session/models[{i}]: reasoningLevels must be an array")),
        }
    }
    // costs: optional; when present a finite, non-negative number.
    for cost in ["inputCost", "outputCost"] {
        if let Some(v) = row.get(cost) {
            match v.as_f64() {
                Some(n) if n.is_finite() && n >= 0.0 => {}
                _ => problems.push(format!(
                    "session/models[{i}]: {cost} must be a finite non-negative number"
                )),
            }
        }
    }
    // effort: optional; when present a string.
    if let Some(v) = row.get("effort") {
        if !v.is_string() {
            problems.push(format!("session/models[{i}]: effort must be a string"));
        }
    }
}

fn require_bool(v: &Value, key: &str, method: &str, problems: &mut Vec<String>) {
    match v.get(key) {
        Some(x) if x.is_boolean() => {}
        Some(_) => problems.push(format!("{method}: `{key}` must be a boolean")),
        None => problems.push(format!("{method}: required extension `{key}` is missing")),
    }
}

fn require_nonempty_string(v: &Value, key: &str, method: &str, problems: &mut Vec<String>) {
    match v.get(key).and_then(Value::as_str) {
        Some(s) if !s.is_empty() => {}
        Some(_) => problems.push(format!("{method}: `{key}` must be a non-empty string")),
        None => problems.push(format!("{method}: required extension `{key}` is missing or not a string")),
    }
}

fn require_string_or_null(v: &Value, key: &str, method: &str, problems: &mut Vec<String>) {
    match v.get(key) {
        Some(x) if x.is_string() || x.is_null() => {}
        Some(_) => problems.push(format!("{method}: `{key}` must be a string or null")),
        None => problems.push(format!("{method}: required extension `{key}` is missing")),
    }
}
/// The model *set* and each model's fields are part of the contract; the *order*
/// the provider/catalogue happens to enumerate them in is not (the Rust engine
/// sorts, the C# engine preserves catalogue order). Sorting both sides by id
/// before comparison makes the check about the models, not their ordering —
/// this is a canonicalisation, not an exclusion: a changed, added or removed id
/// still diverges.
fn sort_models_by_id(outcome: &mut StepOutcome) {
    if let StepOutcome::Ok(Value::Object(map)) = outcome {
        if let Some(Value::Array(rows)) = map.get_mut("models") {
            rows.sort_by(|a, b| {
                let ida = a.get("id").and_then(Value::as_str).unwrap_or("");
                let idb = b.get("id").and_then(Value::as_str).unwrap_or("");
                ida.cmp(idb)
            });
        }
    }
}

/// Runs every step against both engines and returns the disagreements on the
/// **common** contract.
///
/// The Rust response is projected onto the common contract before comparison;
/// the C# response is compared as-is. Returns `(step_index, method, csharp,
/// rust_common)` for each mismatch, so a failure names the exact exchange
/// rather than dumping two transcripts.
pub async fn compare(
    csharp: &EngineUnderTest,
    rust: &EngineUnderTest,
    steps: &[Step],
) -> Result<Vec<(usize, &'static str, StepOutcome, StepOutcome)>> {
    let mut mismatches = Vec::new();
    for (index, step) in steps.iter().enumerate() {
        let mut left = csharp.run(step).await?;
        let mut right = project_common(step.method, &rust.run(step).await?);
        if step.method == "session/models" {
            sort_models_by_id(&mut left);
            sort_models_by_id(&mut right);
        }
        if left != right {
            mismatches.push((index, step.method, left, right));
        }
    }
    Ok(mismatches)
}

/// Methods whose divergence is a **known, declared gap** rather than a defect.
///
/// Each entry is a Rust feature that is not wired yet, listed in `rust/README.md`.
/// The harness tolerates exactly these and fails on anything else, so a new
/// divergence is loud while known work-in-progress does not keep the suite red.
///
/// **This list must shrink to empty before the C# engine is removed.** When a
/// gap is closed, the harness fails until its entry is deleted — which keeps
/// the list honest rather than letting it rot into a permanent excuse.
pub const KNOWN_GAPS: &[(&str, &str)] = &[];

/// The deterministic scenario shared by both engines: everything that does not
/// need a live model, and whose *common* contract both implement.
///
/// `session/setSystemPrompt` is **not** here: it is a Rust-only RPC (the C#
/// engine answers "method not found"), so it belongs to the Rust-extension
/// scenario, not the common one. See [`rust_only_scenario`].
pub fn deterministic_scenario() -> Vec<Step> {
    use coda_proto::messages::method;

    vec![
        Step::new(method::HISTORY, Some(json!({}))),
        Step::new(method::MESSAGES, Some(json!({ "sinceIndex": 0 }))),
        Step::new(method::MODELS, Some(json!({ "refresh": false }))),
        Step::new(method::REASONING_CAPABILITY, Some(json!({}))),
        Step::new(method::INTERRUPT, Some(json!({}))),
        Step::new(method::RECALL_STEERING, Some(json!({}))),
        // Steering with no turn running must be refused by both.
        Step::new(method::STEER, Some(json!({ "text": "hello" }))),
        // Goal validation, including the invalid-duration error code.
        Step::new(method::SET_GOAL, Some(json!({ "goal": "ship it" }))),
        Step::new(method::SET_GOAL, Some(json!({ "goal": "x", "maxDuration": "not-a-duration" }))),
        Step::new(method::SET_GOAL, Some(json!({}))),
        // Effort validation that does NOT depend on a live model. An unknown
        // level is `ok:false` on both; clearing (no `effort`) is accepted on
        // both. Applying a *valid* level, by contrast, depends on the active
        // model's reasoning capability — which for `github-copilot` is only
        // known from a live model list. Under the isolated (no-client) profile
        // that capability is *indeterminate*, and the two engines legitimately
        // differ there (see `assert_indeterminate_effort_divergence` in the
        // parity test): it is out of scope for the deterministic comparison,
        // exactly as live model output is.
        Step::new(method::SET_EFFORT, Some(json!({ "effort": "not-a-level" }))),
        Step::new(method::SET_EFFORT, Some(json!({}))),
        // Listings.
        Step::new(method::SCHEDULE_LIST, Some(json!({}))),
        Step::new(method::HOOKS_LIST, Some(json!({}))),
        Step::new(method::SKILLS_LIST, Some(json!({}))),
        Step::new(method::PLUGINS_LIST, Some(json!({}))),
        // Error paths.
        Step::new("does/notExist", Some(json!({}))),
        Step::new(method::HOOKS_INFO, Some(json!({ "index": 9999 }))),
        Step::new(method::HOOKS_TRUST, Some(json!({}))),
        Step::new(method::SCHEDULE_DELETE, Some(json!({ "id": "no-such-schedule" }))),
        Step::new(method::SCHEDULE_CREATE, Some(json!({ "prompt": "x" }))),
    ]
}

/// The Rust-only surface: methods the C# engine never implemented.
///
/// Compared differently from the common scenario — here the *expected* answer
/// is that the C# engine says "method not found" (`-32601`) while the Rust
/// engine returns a real result. The harness asserts exactly that divergence
/// and schema-checks the Rust result, rather than pretending the two agree.
pub fn rust_only_scenario() -> Vec<Step> {
    use coda_proto::messages::method;
    vec![
        Step::new(method::SET_SYSTEM_PROMPT, Some(json!({ "text": "you are a test" }))),
        Step::new(method::SET_SYSTEM_PROMPT, Some(json!({}))),
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn volatile_fields_are_replaced_not_dropped() {
        let value = json!({ "sessionId": "abc", "serverInfo": "coda" });
        let normalized = normalize(&value);
        assert_eq!(normalized["sessionId"], "<volatile>");
        assert_eq!(normalized["serverInfo"], "coda");
    }

    /// Replacing rather than deleting matters: a field present in one engine
    /// and missing in the other must still be caught.
    #[test]
    fn a_missing_volatile_field_still_differs_from_a_present_one() {
        let with = normalize(&json!({ "sessionId": "abc" }));
        let without = normalize(&json!({}));
        assert_ne!(with, without);
    }

    #[test]
    fn normalization_reaches_into_arrays_and_nested_objects() {
        // On a schedule listing the `id` is a generated identifier and IS
        // volatile; the human `name` is not.
        let value = json!({
            "schedules": [ { "id": "s1", "name": "nightly" } ],
            "nested": { "taskId": "t1", "kind": "interval" }
        });
        let normalized = normalize_for("schedule/list", &value);
        assert_eq!(normalized["schedules"][0]["id"], "<volatile>");
        assert_eq!(normalized["schedules"][0]["name"], "nightly");
        assert_eq!(normalized["nested"]["taskId"], "<volatile>");
        assert_eq!(normalized["nested"]["kind"], "interval");
    }

    /// A model id on `session/models` is a STATIC, meaningful value — it must be
    /// compared literally, never blanked. This is the core of Finding 4: the
    /// earlier harness blanked every `id` and would have let a renamed model
    /// pass as parity.
    #[test]
    fn a_model_id_is_never_treated_as_volatile() {
        let value = json!({ "models": [ { "id": "claude-opus-5", "displayName": "Opus" } ] });
        let normalized = normalize_for("session/models", &value);
        assert_eq!(normalized["models"][0]["id"], "claude-opus-5");
    }

    /// And changing a model id is therefore a divergence, not a wash.
    #[test]
    fn changing_a_model_id_diverges() {
        let a = normalize_for("session/models", &json!({ "models": [ { "id": "opus", "displayName": "X" } ] }));
        let b = normalize_for("session/models", &json!({ "models": [ { "id": "sonnet", "displayName": "X" } ] }));
        assert_ne!(a, b, "a different model id must be caught");
    }

    /// A `description`/`summary` is a structured discriminator, compared
    /// literally: a stable one matches, a changed one diverges (Finding 4).
    #[test]
    fn descriptions_are_compared_literally_not_collapsed() {
        let stable_a = normalize_for("skills/list", &json!({ "description": "runs the linter" }));
        let stable_b = normalize_for("skills/list", &json!({ "description": "runs the linter" }));
        assert_eq!(stable_a, stable_b, "an identical description must match");

        let changed = normalize_for("skills/list", &json!({ "description": "runs the tests" }));
        assert_ne!(stable_a, changed, "a changed description must diverge");
    }

    /// An unknown extra field yields a divergence — nothing is silently swallowed.
    #[test]
    fn an_unknown_extra_field_diverges() {
        let a = normalize_for("session/models", &json!({ "models": [ { "id": "m" } ] }));
        let b = normalize_for("session/models", &json!({ "models": [ { "id": "m", "surprise": 1 } ] }));
        assert_ne!(a, b, "an unexpected field must be caught");
    }

    /// Errors compare by code only — messages are prose and may legitimately
    /// differ between two implementations.
    #[test]
    fn errors_compare_by_code_not_message() {
        assert_eq!(StepOutcome::Error { code: -32602 }, StepOutcome::Error { code: -32602 });
        assert_ne!(StepOutcome::Error { code: -32602 }, StepOutcome::Error { code: -32603 });
    }

    /// Differently-worded prose must not be reported as a parity break.
    #[test]
    fn differing_prose_compares_equal() {
        let a = normalize(&json!({ "ok": false, "note": "Invalid effort level 'x'" }));
        let b = normalize(&json!({ "ok": false, "note": "unsupported effort: x" }));
        assert_eq!(a, b, "wording differences are not behaviour differences");
    }

    /// But an empty string is not the same as an absent field: the C# engine
    /// emits `note: ""` where the Rust engine omitted it entirely, and that
    /// distinction is a genuine wire difference this harness caught.
    #[test]
    fn an_empty_prose_field_differs_from_an_absent_one() {
        let empty = normalize(&json!({ "ok": true, "note": "" }));
        let absent = normalize(&json!({ "ok": true }));
        assert_ne!(empty, absent, "an empty string and an absent field differ on the wire");
    }

    /// And an empty string is not the same as prose either.
    #[test]
    fn an_empty_prose_field_differs_from_a_populated_one() {
        let empty = normalize(&json!({ "note": "" }));
        let populated = normalize(&json!({ "note": "something" }));
        assert_ne!(empty, populated);
    }

    #[test]
    fn the_scenario_covers_both_success_and_error_paths() {
        let steps = deterministic_scenario();
        assert!(steps.len() >= 20, "the scenario should be substantial");
        assert!(
            steps.iter().any(|s| s.method == "does/notExist"),
            "an unknown method must be exercised"
        );
    }

    // ── extension projection ────────────────────────────────────────────────

    /// A declared top-level extension is removed so the common contract matches.
    #[test]
    fn a_declared_top_level_extension_is_projected_away() {
        let csharp = StepOutcome::Ok(json!({ "supported": true, "levels": ["low"] }));
        let rust = StepOutcome::Ok(json!({
            "supported": true, "levels": ["low"],
            "current": null, "indeterminate": false, "model": "m", "providerId": "p"
        }));
        assert_eq!(csharp, project_common("model/reasoningCapability", &rust));
    }

    /// A declared per-row extension is removed from every model row.
    #[test]
    fn a_declared_per_row_extension_is_projected_away() {
        let csharp = StepOutcome::Ok(json!({
            "source": "live", "model": "m", "providerId": "p",
            "models": [ { "id": "a", "displayName": "A", "contextLimit": 10 } ]
        }));
        let rust = StepOutcome::Ok(json!({
            "source": "live", "model": "m", "providerId": "p",
            "models": [ {
                "id": "a", "displayName": "A", "contextLimit": 10,
                "reasoningLevels": ["low"], "inputCost": 1, "outputCost": 2, "effort": "high"
            } ]
        }));
        assert_eq!(csharp, project_common("session/models", &rust));
    }

    /// The active model/providerId of `session/models` are COMMON, not
    /// extensions: a disagreement on them must still fail.
    #[test]
    fn the_active_model_of_the_listing_is_still_compared() {
        let csharp = StepOutcome::Ok(json!({ "models": [], "model": "opus", "providerId": "p" }));
        let rust = StepOutcome::Ok(json!({ "models": [], "model": "sonnet", "providerId": "p" }));
        assert_ne!(csharp, project_common("session/models", &rust), "a shared field was swallowed");
    }

    /// An undeclared extra on an extension method is never projected away.
    #[test]
    fn an_undeclared_field_is_never_projected_away() {
        let csharp = StepOutcome::Ok(json!({ "supported": true }));
        let rust = StepOutcome::Ok(json!({ "supported": true, "surprise": true }));
        assert_ne!(
            csharp,
            project_common("model/reasoningCapability", &rust),
            "an undeclared field was swallowed"
        );
    }

    /// A shared field the Rust engine drops must still fail — projection only
    /// removes the declared extras, it does not add missing fields.
    #[test]
    fn a_dropped_shared_field_still_fails() {
        let csharp = StepOutcome::Ok(json!({ "supported": true, "levels": ["low"] }));
        let rust = StepOutcome::Ok(json!({ "supported": true }));
        assert_ne!(csharp, project_common("model/reasoningCapability", &rust));
    }

    /// Extensions declared for one method do not leak to another.
    #[test]
    fn extensions_declared_for_one_method_do_not_leak() {
        let csharp = StepOutcome::Ok(json!({ "supported": true }));
        let rust = StepOutcome::Ok(json!({ "supported": true, "current": "high" }));
        // `current` is an extension of setEffort/reasoningCapability, not history.
        assert_ne!(csharp, project_common("session/history", &rust));
    }

    /// Errors pass through projection untouched (nothing to strip).
    #[test]
    fn projection_leaves_errors_untouched() {
        let err = StepOutcome::Error { code: -32601 };
        assert_eq!(err, project_common("session/setEffort", &err));
    }

    // ── extension schema validation (Finding 2) ─────────────────────────────

    #[test]
    fn a_well_shaped_reasoning_capability_validates() {
        let v = json!({
            "supported": true, "current": "high", "indeterminate": false,
            "model": "m", "providerId": "p"
        });
        assert!(validate_extension_schema("model/reasoningCapability", &v).is_empty());
        // `current` may also be null.
        let v2 = json!({ "current": null, "indeterminate": true, "model": "m", "providerId": "p" });
        assert!(validate_extension_schema("model/reasoningCapability", &v2).is_empty());
    }

    #[test]
    fn a_dropped_required_reasoning_field_fails() {
        // indeterminate missing entirely.
        let v = json!({ "current": "high", "model": "m", "providerId": "p" });
        assert!(!validate_extension_schema("model/reasoningCapability", &v).is_empty());
    }

    #[test]
    fn a_mistyped_reasoning_field_fails() {
        // current as a number, indeterminate as a string, providerId empty.
        let v = json!({ "current": 3, "indeterminate": "no", "model": "m", "providerId": "" });
        let problems = validate_extension_schema("model/reasoningCapability", &v);
        assert!(problems.len() >= 3, "each malformed field must be reported: {problems:?}");
    }

    #[test]
    fn set_effort_requires_a_current_string_or_null() {
        assert!(validate_extension_schema("session/setEffort", &json!({ "current": "medium" })).is_empty());
        assert!(validate_extension_schema("session/setEffort", &json!({ "current": null })).is_empty());
        assert!(!validate_extension_schema("session/setEffort", &json!({ "current": 7 })).is_empty());
        assert!(!validate_extension_schema("session/setEffort", &json!({ "ok": true })).is_empty());
    }

    #[test]
    fn well_shaped_model_rows_validate() {
        let v = json!({ "models": [
            { "id": "a", "inputCost": 1.5, "outputCost": 0.0, "reasoningLevels": ["low", "high"], "effort": "high" },
            { "id": "b" }
        ] });
        assert!(validate_extension_schema("session/models", &v).is_empty());
    }

    #[test]
    fn a_negative_or_nonnumeric_cost_is_not_silently_projected() {
        let neg = json!({ "models": [ { "id": "a", "inputCost": -1.0 } ] });
        assert!(!validate_extension_schema("session/models", &neg).is_empty());
        let str_cost = json!({ "models": [ { "id": "a", "outputCost": "cheap" } ] });
        assert!(!validate_extension_schema("session/models", &str_cost).is_empty());
    }

    #[test]
    fn a_wrong_effort_or_reasoning_levels_type_fails() {
        let bad_effort = json!({ "models": [ { "id": "a", "effort": 5 } ] });
        assert!(!validate_extension_schema("session/models", &bad_effort).is_empty());
        let bad_levels = json!({ "models": [ { "id": "a", "reasoningLevels": [1, 2] } ] });
        assert!(!validate_extension_schema("session/models", &bad_levels).is_empty());
        let not_array = json!({ "models": [ { "id": "a", "reasoningLevels": "low" } ] });
        assert!(!validate_extension_schema("session/models", &not_array).is_empty());
    }

    // ── isolated fixture profile (Finding 1) ────────────────────────────────

    /// The fixture profile writes an isolated home whose settings name the
    /// sentinel model — proving the harness has a real, redirectable root rather
    /// than reading the developer's `~/.coda`.
    #[test]
    fn the_isolated_profile_materialises_its_fixtures() {
        let profile = IsolatedProfile::create().expect("create profile");
        let coda = profile.home().join(".coda");
        assert!(coda.join("settings.json").exists(), "settings fixture must exist");
        assert!(coda.join("credentials").is_dir(), "an EMPTY credential dir isolates auth");
        assert!(profile.catalog().exists(), "catalogue fixture must exist");

        let settings = std::fs::read_to_string(coda.join("settings.json")).unwrap();
        assert!(settings.contains(FIXTURE_MODEL), "settings must pin the sentinel model");
        assert!(settings.contains(FIXTURE_PROVIDER), "settings must pin the fixture provider");

        let catalog = std::fs::read_to_string(profile.catalog()).unwrap();
        for id in FIXTURE_CATALOG_MODEL_IDS {
            assert!(catalog.contains(id), "catalogue must offer {id}");
        }

        // The credential directory is empty: no stored credential can be found,
        // so neither engine builds a live client.
        let creds: Vec<_> = std::fs::read_dir(coda.join("credentials")).unwrap().collect();
        assert!(creds.is_empty(), "the isolated credential store must start empty");

        let home = profile.home().to_path_buf();
        drop(profile);
        assert!(!home.exists(), "the profile must be removed on drop");
    }

    // ── identity guard ──────────────────────────────────────────────────────

    /// The core guarantee: the *same* artifact is recognised as identical, so
    /// the harness will refuse to compare an engine against itself.
    #[test]
    fn the_same_artifact_is_rejected_as_identical() {
        let me = Path::new(env!("CARGO_MANIFEST_DIR")).join("Cargo.toml");
        assert!(
            artifacts_are_identical(&me, &me).expect("fingerprint"),
            "the same file must be recognised as the same artifact"
        );
    }

    /// Two genuinely different artifacts are not identical, so a real C#-vs-Rust
    /// pair is allowed through.
    #[test]
    fn two_different_artifacts_are_not_identical() {
        let a = Path::new(env!("CARGO_MANIFEST_DIR")).join("Cargo.toml");
        let b = Path::new(env!("CARGO_MANIFEST_DIR")).join("src/lib.rs");
        assert!(
            !artifacts_are_identical(&a, &b).expect("fingerprint"),
            "two different files must not be treated as the same artifact"
        );
    }

    /// Builds a byte buffer that parses as a valid managed PE (its data
    /// directory 14 — the CLR header — is non-empty). When `managed` is false it
    /// is a well-formed *native* PE (CLR directory zeroed), which must be
    /// rejected. This is enough to exercise [`is_managed_pe`] without a real
    /// compiler in the loop.
    fn synthetic_pe(managed: bool) -> Vec<u8> {
        let e_lfanew: usize = 0x80;
        let opt = e_lfanew + 4 + 20; // after PE sig + COFF header
        let data_dirs = opt + 112; // PE32+
        let clr = data_dirs + 14 * 8;
        let mut b = vec![0u8; clr + 8];
        b[0] = b'M';
        b[1] = b'Z';
        b[0x3C..0x40].copy_from_slice(&(e_lfanew as u32).to_le_bytes());
        b[e_lfanew..e_lfanew + 4].copy_from_slice(b"PE\0\0");
        b[opt..opt + 2].copy_from_slice(&0x20bu16.to_le_bytes()); // PE32+
        if managed {
            b[clr..clr + 4].copy_from_slice(&0x2000u32.to_le_bytes()); // rva
            b[clr + 4..clr + 8].copy_from_slice(&0x48u32.to_le_bytes()); // size
        }
        b
    }

    fn write_temp(name: &str, bytes: &[u8]) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "coda-diff-pe-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join(name);
        std::fs::write(&path, bytes).unwrap();
        path
    }

    /// A managed PE is recognised; a native PE with the same bytes minus the CLR
    /// header is not — proving suffix is not what's being trusted.
    #[test]
    fn managed_pe_metadata_is_required() {
        let managed = write_temp("managed.dll", &synthetic_pe(true));
        assert!(is_managed_pe(&managed), "a PE with a CLR data directory is managed");

        let native = write_temp("native.dll", &synthetic_pe(false));
        assert!(!is_managed_pe(&native), "a PE with no CLR data directory is not managed");

        let _ = std::fs::remove_dir_all(managed.parent().unwrap());
        let _ = std::fs::remove_dir_all(native.parent().unwrap());
    }

    /// A `.dll` reference is accepted only with BOTH the runtimeconfig sibling
    /// AND valid managed-PE metadata — a suffix alone is rejected.
    #[test]
    fn a_dll_needs_runtimeconfig_and_managed_metadata() {
        let dir = std::env::temp_dir().join(format!(
            "coda-diff-ref-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        let dll = dir.join("Coda.Tui.dll");

        // Suffix only, no runtimeconfig, not even a real PE → rejected.
        std::fs::write(&dll, b"not a real assembly").unwrap();
        assert!(!looks_like_dotnet_reference(&dll), "suffix alone is insufficient");

        // Add the runtimeconfig but still a non-managed body → rejected.
        std::fs::write(dir.join("Coda.Tui.runtimeconfig.json"), "{}").unwrap();
        std::fs::write(&dll, synthetic_pe(false)).unwrap();
        assert!(!looks_like_dotnet_reference(&dll), "runtimeconfig without managed metadata is insufficient");

        // Now a genuine managed PE beside the runtimeconfig → accepted.
        std::fs::write(&dll, synthetic_pe(true)).unwrap();
        assert!(looks_like_dotnet_reference(&dll), "runtimeconfig + managed metadata is a .NET reference");

        // The native apphost .exe is accepted via its sibling managed .dll.
        let exe = dir.join("Coda.Tui.exe");
        std::fs::write(&exe, synthetic_pe(false)).unwrap(); // apphost is native
        assert!(looks_like_dotnet_reference(&exe), "the apphost is proven by its sibling managed dll");

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// A bare file with no .NET metadata sibling is not a .NET reference — this
    /// is what stops a stray Rust `coda.exe` from posing as the C# engine.
    #[test]
    fn a_plain_binary_is_not_a_dotnet_reference() {
        let not_dotnet = Path::new(env!("CARGO_MANIFEST_DIR")).join("Cargo.toml");
        assert!(!looks_like_dotnet_reference(&not_dotnet));
    }

    /// The C# reference must not be resolvable from `PATH`: with the variable
    /// unset, resolution fails loudly instead of quietly picking up the Rust
    /// `coda` that now lives on `PATH`.
    #[test]
    fn csharp_reference_is_never_taken_from_path() {
        // Only assert the contract when the variable is genuinely absent, so a
        // developer who exported it for a manual run does not see a spurious
        // failure.
        if std::env::var_os("CODA_CSHARP_ENGINE").is_none() {
            assert!(
                csharp_reference().is_err(),
                "without CODA_CSHARP_ENGINE the C# reference must not resolve"
            );
        }
    }

    /// The launch spec for a `.dll` runs it through `dotnet`; an `.exe` apphost
    /// runs directly.
    #[test]
    fn legacy_launch_uses_dotnet_only_for_a_dll() {
        let dll = LaunchSpec::legacy_dotnet(Path::new("x/Coda.Tui.dll"));
        assert_eq!(dll.program, OsString::from("dotnet"));
        assert_eq!(dll.leading_args, vec![OsString::from("x/Coda.Tui.dll")]);

        let exe = LaunchSpec::legacy_dotnet(Path::new("x/Coda.Tui.exe"));
        assert_eq!(exe.program, OsString::from("x/Coda.Tui.exe"));
        assert!(exe.leading_args.is_empty());
    }
}
