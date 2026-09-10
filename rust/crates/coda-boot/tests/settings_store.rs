//! Cooperative settings writes.
//!
//! `settings.json` has several writers in different processes: the TUI (theme,
//! model, effort), and the auth transaction (`defaultProvider`,
//! `githubEnterpriseDomain`). The dangerous pattern is a writer that loads the
//! whole document, keeps it, and writes it back later: everything anyone else
//! changed in between is reverted, silently.

use std::sync::Arc;

use coda_auth::service::{AuthSettings, AuthSettingsPatch, AuthSettingsPort};
use coda_boot::settings_store::{self, SettingsError, SettingsFile};
use serde_json::json;

fn write(path: &std::path::Path, text: &str) {
    std::fs::write(path, text).expect("write settings");
}

fn read(path: &std::path::Path) -> serde_json::Value {
    let text = std::fs::read_to_string(path).expect("read settings");
    serde_json::from_str(text.strip_prefix('\u{feff}').unwrap_or(&text)).expect("valid json")
}

#[test]
fn a_patch_changes_only_the_keys_it_names() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(
        &path,
        &json!({
            "theme": "dark",
            "defaultModel": "claude-opus-5",
            "modelByProvider": { "github-copilot": "gpt-5" },
            "somethingThisBuildHasNeverHeardOf": { "nested": [1, 2, 3] }
        })
        .to_string(),
    );

    let file = SettingsFile::at(&path);
    file.apply(
        &AuthSettingsPatch::empty()
            .with_default_provider(Some("claude-ai".into()))
            .with_github_enterprise_domain(Some("octocorp.ghe.com".into())),
    )
    .expect("apply");

    let value = read(&path);
    assert_eq!(value["defaultProvider"], "claude-ai");
    assert_eq!(value["githubEnterpriseDomain"], "octocorp.ghe.com");
    assert_eq!(value["theme"], "dark");
    assert_eq!(value["modelByProvider"]["github-copilot"], "gpt-5");
    assert_eq!(value["somethingThisBuildHasNeverHeardOf"]["nested"][2], 3);
}

#[test]
fn removing_a_key_removes_only_that_key() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(
        &path,
        &json!({ "defaultProvider": "github-copilot", "githubEnterpriseDomain": "octocorp.ghe.com", "theme": "light" })
            .to_string(),
    );

    let file = SettingsFile::at(&path);
    file.apply(&AuthSettingsPatch::empty().with_github_enterprise_domain(None))
        .expect("apply");

    let value = read(&path);
    assert!(value.get("githubEnterpriseDomain").is_none());
    assert_eq!(value["defaultProvider"], "github-copilot");
    assert_eq!(value["theme"], "light");
}

#[test]
fn a_missing_file_is_created_with_only_the_owned_keys() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("nested").join("settings.json");

    let file = SettingsFile::at(&path);
    assert_eq!(file.load().expect("load"), AuthSettings::default());
    file.apply(&AuthSettingsPatch::empty().with_default_provider(Some("anthropic".into())))
        .expect("apply");

    assert_eq!(read(&path), json!({ "defaultProvider": "anthropic" }));
}

#[test]
fn a_byte_order_mark_is_tolerated_and_not_duplicated() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(&path, &format!("\u{feff}{}", json!({ "theme": "dark" })));

    let file = SettingsFile::at(&path);
    file.apply(&AuthSettingsPatch::empty().with_default_provider(Some("claude-ai".into())))
        .expect("apply");

    let raw = std::fs::read_to_string(&path).unwrap();
    assert_eq!(raw.matches('\u{feff}').count(), 0, "the BOM is not re-emitted: {raw:?}");
    assert_eq!(read(&path)["theme"], "dark");
}

#[test]
fn invalid_json_is_an_error_and_the_file_is_left_alone() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(&path, "{ this is not json");

    let file = SettingsFile::at(&path);
    assert!(matches!(file.read(), Err(SettingsError::Parse { .. })));
    let error = settings_store::apply_patch(
        &path,
        &AuthSettingsPatch::empty().with_default_provider(Some("claude-ai".into())),
    )
    .expect_err("a corrupt file must not be replaced with an empty object");
    assert!(matches!(error, SettingsError::Parse { .. }), "{error:?}");
    assert_eq!(std::fs::read_to_string(&path).unwrap(), "{ this is not json");
}

#[test]
fn a_non_object_root_is_an_error_not_an_empty_object() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(&path, "[1, 2, 3]");

    let file = SettingsFile::at(&path);
    assert!(matches!(file.read(), Err(SettingsError::NotAnObject { .. })));
    assert!(file
        .apply(&AuthSettingsPatch::empty().with_default_provider(Some("claude-ai".into())))
        .is_err());
    assert_eq!(std::fs::read_to_string(&path).unwrap(), "[1, 2, 3]");
}

#[test]
fn a_non_string_owned_key_is_reported_rather_than_read_as_absent() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(&path, &json!({ "defaultProvider": 7 }).to_string());

    let file = SettingsFile::at(&path);
    assert!(matches!(file.read(), Err(SettingsError::NotAString { .. })));
}

#[test]
fn an_owned_key_written_by_another_process_is_not_reverted_by_a_stale_snapshot() {
    // This is the TUI's failure mode: load the whole document, sit on it, and
    // write it back after an auth commit has changed defaultProvider.
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(&path, &json!({ "theme": "dark", "defaultProvider": "github-copilot" }).to_string());

    let original = settings_store::read_document(&path).expect("read").expect("present");
    let mut edited = original.clone();
    edited["theme"] = json!("light");

    // Meanwhile, an auth commit lands.
    SettingsFile::at(&path)
        .apply(&AuthSettingsPatch::empty().with_default_provider(Some("claude-ai".into())))
        .expect("apply");

    // The TUI saves its own change as a delta against what it loaded.
    settings_store::save_changes(&path, &original, &edited).expect("save");

    let value = read(&path);
    assert_eq!(value["theme"], "light", "the TUI's own change must land");
    assert_eq!(
        value["defaultProvider"], "claude-ai",
        "a stale root snapshot must not revert the committed provider"
    );
}

#[test]
fn a_delta_save_preserves_a_concurrent_edit_to_a_sibling_key() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(
        &path,
        &json!({ "modelByProvider": { "github-copilot": "gpt-5", "claude-ai": "opus" } }).to_string(),
    );

    let original = settings_store::read_document(&path).expect("read").expect("present");
    let mut edited = original.clone();
    edited["modelByProvider"]["claude-ai"] = json!("opus-5.1");

    // Another process changes a different provider's row.
    let mut concurrent = original.clone();
    concurrent["modelByProvider"]["github-copilot"] = json!("gpt-6");
    settings_store::save_changes(&path, &original, &concurrent).expect("save concurrent");

    settings_store::save_changes(&path, &original, &edited).expect("save");

    let value = read(&path);
    assert_eq!(value["modelByProvider"]["claude-ai"], "opus-5.1");
    assert_eq!(
        value["modelByProvider"]["github-copilot"], "gpt-6",
        "an unrelated concurrent edit must survive"
    );
}

#[test]
fn a_delta_save_honours_a_deletion() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(&path, &json!({ "theme": "dark", "outputStyle": "verbose" }).to_string());

    let original = settings_store::read_document(&path).expect("read").expect("present");
    let mut edited = original.clone();
    edited.as_object_mut().unwrap().remove("outputStyle");

    settings_store::save_changes(&path, &original, &edited).expect("save");

    let value = read(&path);
    assert!(value.get("outputStyle").is_none());
    assert_eq!(value["theme"], "dark");
}

#[test]
fn an_auth_rollback_does_not_wipe_a_concurrent_unrelated_edit() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(&path, &json!({ "defaultProvider": "github-copilot" }).to_string());

    let port: Arc<dyn AuthSettingsPort> = Arc::new(SettingsFile::at(&path));
    let before = port.load().expect("load");

    port.apply(&AuthSettingsPatch::empty().with_default_provider(Some("claude-ai".into())))
        .expect("commit");

    // The TUI writes a theme while the commit is being undone.
    let original = json!({});
    let edited = json!({ "theme": "solarized" });
    settings_store::save_changes(&path, &original, &edited).expect("save");

    // The auth transaction rolls its own change back.
    let inverse = AuthSettingsPatch::empty()
        .with_default_provider(Some("claude-ai".into()))
        .inverse_of(&before);
    port.apply(&inverse).expect("rollback");

    let value = read(&path);
    assert_eq!(value["defaultProvider"], "github-copilot", "the rollback restores its own key");
    assert_eq!(value["theme"], "solarized", "and leaves everyone else's alone");
}

#[test]
fn writes_are_atomic_and_leave_no_scratch_file() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(&path, &json!({ "theme": "dark" }).to_string());

    SettingsFile::at(&path)
        .apply(&AuthSettingsPatch::empty().with_default_provider(Some("claude-ai".into())))
        .expect("apply");

    let leftovers: Vec<_> = std::fs::read_dir(dir.path())
        .unwrap()
        .filter_map(Result::ok)
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .filter(|name| name != "settings.json" && !name.ends_with(".lock"))
        .collect();
    assert!(leftovers.is_empty(), "unexpected files left behind: {leftovers:?}");
}

#[test]
fn concurrent_writers_do_not_lose_each_others_keys() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write(&path, &json!({}).to_string());

    let threads: Vec<_> = (0..8)
        .map(|index| {
            let path = path.clone();
            std::thread::spawn(move || {
                let original = json!({});
                let mut edited = serde_json::Map::new();
                edited.insert(format!("key{index}"), json!(index));
                settings_store::save_changes(&path, &original, &serde_json::Value::Object(edited))
                    .expect("save");
            })
        })
        .collect();
    for thread in threads {
        thread.join().expect("thread");
    }

    let value = read(&path);
    for index in 0..8 {
        assert_eq!(value[format!("key{index}")], index, "writer {index} lost its key: {value}");
    }
}
