//! Skill discovery for `skills/list`.
//!
//! Lives in the serve layer rather than the agent crate on purpose: the C#
//! build makes the same split, keeping skill and plugin loading out of the SDK
//! so the engine core does not depend on the host's notion of a workspace.
//!
//! # Precedence
//!
//! Lowest to highest: **foreign < claude < user < plugin < project**. Later
//! entries override earlier ones *by name*, so a project's own skill always
//! wins over one inherited from another ecosystem. Getting this backwards
//! would let a skill installed globally silently shadow a project's.
//!
//! Missing directories are normal, and a malformed file is skipped rather than
//! failing the whole listing — one bad skill must not make every skill
//! invisible.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use serde::Serialize;

/// Where a skill came from. Ordered by precedence, lowest first.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum SkillOrigin {
    /// Another ecosystem's layout (`.agents/skills`, `~/.claude/agents`,
    /// `~/.claude/commands`). Read-only.
    Foreign,
    /// The Claude CLI's skill directory (`~/.claude/skills`). Read-only.
    Claude,
    /// User-level Coda skills (`~/.coda/skills`).
    User,
    /// Bundled with a plugin.
    Plugin,
    /// Project-level (`.coda/skills` under the working directory).
    Project,
}

impl SkillOrigin {
    /// Wire form is lowercase, matching the C# `ToString().ToLowerInvariant()`.
    pub fn as_str(self) -> &'static str {
        match self {
            SkillOrigin::Foreign => "foreign",
            SkillOrigin::Claude => "claude",
            SkillOrigin::User => "user",
            SkillOrigin::Plugin => "plugin",
            SkillOrigin::Project => "project",
        }
    }
}

/// A discovered skill, in the shape `skills/list` returns.
#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct SkillInfo {
    pub name: String,
    pub description: String,
    pub origin: String,
    pub enabled: bool,
    pub user_invocable: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub argument_hint: Option<String>,
}

/// Parsed YAML front matter. Only the fields the wire shape needs.
#[derive(Debug, Default)]
struct FrontMatter {
    name: Option<String>,
    description: Option<String>,
    argument_hint: Option<String>,
    disable_model_invocation: bool,
    user_invocable: Option<bool>,
}

/// Extracts the `---` delimited front matter from a `SKILL.md`.
///
/// Deliberately a small hand-rolled scanner rather than a YAML dependency:
/// the front matter is a flat `key: value` map, and skills are untrusted input
/// from other ecosystems, so a narrow parser that cannot be surprised is
/// preferable to a general one.
fn parse_front_matter(text: &str) -> FrontMatter {
    let mut fm = FrontMatter { user_invocable: None, ..Default::default() };

    let mut lines = text.lines();
    // The document must open with a `---` fence, ignoring a leading BOM.
    let first = lines.next().map(|l| l.trim_start_matches('\u{feff}').trim()).unwrap_or("");
    if first != "---" {
        return fm;
    }

    for line in lines {
        let trimmed = line.trim_end();
        if trimmed.trim() == "---" {
            break;
        }
        let Some((key, value)) = trimmed.split_once(':') else {
            continue;
        };
        let key = key.trim().to_ascii_lowercase();
        let value = value.trim().trim_matches('"').trim_matches('\'').to_owned();
        if value.is_empty() {
            continue;
        }
        match key.as_str() {
            "name" => fm.name = Some(value),
            "description" => fm.description = Some(value),
            "argument-hint" | "argumenthint" | "argument_hint" => fm.argument_hint = Some(value),
            "disable-model-invocation" | "disablemodelinvocation" => {
                fm.disable_model_invocation = value.eq_ignore_ascii_case("true");
            }
            "user-invocable" | "userinvocable" => {
                fm.user_invocable = Some(!value.eq_ignore_ascii_case("false"));
            }
            _ => {}
        }
    }
    fm
}

/// Reads one skill directory, or `None` when it holds no readable `SKILL.md`.
fn load_one(dir: &Path, origin: SkillOrigin, workspace: &Path) -> Option<SkillInfo> {
    let file = dir.join("SKILL.md");
    let text = std::fs::read_to_string(&file).ok()?;
    let fm = parse_front_matter(&text);

    // A skill with no `name` falls back to its directory name, matching C#.
    let name = fm
        .name
        .filter(|n| !n.trim().is_empty())
        .or_else(|| dir.file_name().map(|n| n.to_string_lossy().into_owned()))?;

    Some(SkillInfo {
        name,
        description: fm.description.unwrap_or_default(),
        origin: origin.as_str().to_owned(),
        // `enabled` is the inverse of the opt-out flag, as in C#.
        enabled: !fm.disable_model_invocation,
        user_invocable: fm.user_invocable.unwrap_or(true),
        source_path: workspace_relative(&file, workspace),
        argument_hint: fm.argument_hint,
    })
}

/// Returns the path relative to the workspace, or `None` when outside it.
///
/// Skills from `~/.claude` or `~/.coda` live outside the project, and the C#
/// reports no path for those rather than leaking an absolute home directory
/// path onto the wire.
fn workspace_relative(path: &Path, workspace: &Path) -> Option<String> {
    let workspace = workspace.canonicalize().ok()?;
    let path = path.canonicalize().ok()?;
    let rest = path.strip_prefix(&workspace).ok()?;
    Some(rest.to_string_lossy().replace('\\', "/"))
}

/// Every directory scanned, in precedence order (lowest first).
fn search_paths(workspace: &Path) -> Vec<(PathBuf, SkillOrigin)> {
    let home = directories::UserDirs::new().map(|d| d.home_dir().to_path_buf());
    let mut paths = Vec::new();

    // Foreign ecosystems, lowest precedence.
    paths.push((workspace.join(".agents").join("skills"), SkillOrigin::Foreign));
    if let Some(home) = &home {
        paths.push((home.join(".claude").join("agents"), SkillOrigin::Foreign));
        paths.push((home.join(".claude").join("commands"), SkillOrigin::Foreign));
        paths.push((home.join(".claude").join("skills"), SkillOrigin::Claude));
        paths.push((home.join(".coda").join("skills"), SkillOrigin::User));
    }
    // Project last so it overrides everything.
    paths.push((workspace.join(".coda").join("skills"), SkillOrigin::Project));
    paths
}

/// Discovers every skill visible from `workspace`.
///
/// Results are sorted by name so the listing is stable between calls; an
/// unstable order would make the differential parity suite flap.
pub fn discover(workspace: &Path) -> Vec<SkillInfo> {
    // Keyed by name so a higher-precedence origin replaces a lower one, and
    // BTreeMap gives the stable ordering for free.
    let mut by_name: BTreeMap<String, SkillInfo> = BTreeMap::new();

    for (root, origin) in search_paths(workspace) {
        let Ok(entries) = std::fs::read_dir(&root) else {
            continue; // A missing directory is the normal case.
        };
        for entry in entries.flatten() {
            if !entry.file_type().map(|t| t.is_dir()).unwrap_or(false) {
                continue;
            }
            if let Some(skill) = load_one(&entry.path(), origin, workspace) {
                by_name.insert(skill.name.clone(), skill);
            }
        }
    }

    by_name.into_values().collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TempDir(PathBuf);

    /// A monotonic counter guarantees distinct directories: the wall clock
    /// alone collides under parallel tests on Windows, which advances it on a
    /// ~15 ms tick.
    static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

    impl TempDir {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "coda-skills-test-{}-{}",
                std::process::id(),
                COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
            ));
            let _ = std::fs::remove_dir_all(&path);
            std::fs::create_dir_all(&path).expect("temp dir");
            Self(path)
        }
        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn write_skill(root: &Path, name: &str, body: &str) {
        let dir = root.join(name);
        std::fs::create_dir_all(&dir).expect("skill dir");
        std::fs::write(dir.join("SKILL.md"), body).expect("skill file");
    }

    #[test]
    fn front_matter_fields_are_read() {
        let fm = parse_front_matter(
            "---\nname: demo\ndescription: does a thing\nargument-hint: [file]\n---\n# body\n",
        );
        assert_eq!(fm.name.as_deref(), Some("demo"));
        assert_eq!(fm.description.as_deref(), Some("does a thing"));
        assert_eq!(fm.argument_hint.as_deref(), Some("[file]"));
        assert!(!fm.disable_model_invocation);
    }

    #[test]
    fn a_document_without_front_matter_yields_nothing() {
        let fm = parse_front_matter("# just a heading\n");
        assert!(fm.name.is_none());
        assert!(fm.description.is_none());
    }

    /// `enabled` is the inverse of the opt-out flag, so getting the polarity
    /// wrong would silently expose a skill its author disabled.
    #[test]
    fn disable_model_invocation_makes_the_skill_disabled() {
        let dir = TempDir::new();
        write_skill(
            dir.path(),
            "quiet",
            "---\nname: quiet\ndescription: d\ndisable-model-invocation: true\n---\n",
        );
        let found = load_one(&dir.path().join("quiet"), SkillOrigin::User, dir.path())
            .expect("skill loads");
        assert!(!found.enabled);
        assert!(found.user_invocable, "still invocable by the user directly");
    }

    #[test]
    fn a_skill_without_a_name_falls_back_to_its_directory() {
        let dir = TempDir::new();
        write_skill(dir.path(), "from-dir", "---\ndescription: d\n---\n");
        let found = load_one(&dir.path().join("from-dir"), SkillOrigin::User, dir.path())
            .expect("skill loads");
        assert_eq!(found.name, "from-dir");
    }

    #[test]
    fn a_directory_without_a_skill_file_is_skipped() {
        let dir = TempDir::new();
        std::fs::create_dir_all(dir.path().join("empty")).expect("dir");
        assert!(load_one(&dir.path().join("empty"), SkillOrigin::User, dir.path()).is_none());
    }

    /// One malformed skill must not hide the rest.
    #[test]
    fn a_malformed_skill_does_not_hide_the_others() {
        let dir = TempDir::new();
        let project = dir.path().join(".coda").join("skills");
        std::fs::create_dir_all(&project).expect("dirs");
        write_skill(&project, "good", "---\nname: good\ndescription: fine\n---\n");
        write_skill(&project, "bad", "not front matter at all");

        let found = discover(dir.path());
        assert!(
            found.iter().any(|s| s.name == "good"),
            "the valid skill must still be listed: {found:?}"
        );
    }

    /// Project beats user beats claude. Reversing this would let a globally
    /// installed skill shadow one the project ships.
    #[test]
    fn a_project_skill_overrides_a_lower_precedence_one_of_the_same_name() {
        let dir = TempDir::new();

        let foreign = dir.path().join(".agents").join("skills");
        std::fs::create_dir_all(&foreign).expect("dirs");
        write_skill(&foreign, "shared", "---\nname: shared\ndescription: from foreign\n---\n");

        let project = dir.path().join(".coda").join("skills");
        std::fs::create_dir_all(&project).expect("dirs");
        write_skill(&project, "shared", "---\nname: shared\ndescription: from project\n---\n");

        let found = discover(dir.path());
        let shared: Vec<_> = found.iter().filter(|s| s.name == "shared").collect();
        assert_eq!(shared.len(), 1, "the name must appear once, not twice");
        assert_eq!(shared[0].origin, "project");
        assert_eq!(shared[0].description, "from project");
    }

    /// A project skill reports a workspace-relative path; one from outside the
    /// workspace reports none, rather than leaking a home directory path.
    #[test]
    fn only_in_workspace_skills_report_a_source_path() {
        let dir = TempDir::new();
        let project = dir.path().join(".coda").join("skills");
        std::fs::create_dir_all(&project).expect("dirs");
        write_skill(&project, "local", "---\nname: local\ndescription: d\n---\n");

        let found = discover(dir.path());
        let local = found.iter().find(|s| s.name == "local").expect("found");
        let path = local.source_path.as_deref().expect("in-workspace skill has a path");
        assert!(!path.contains(".."), "path must be relative and contained: {path}");
        assert!(path.ends_with("SKILL.md"), "path points at the skill file: {path}");

        let outside = dir.path().join("elsewhere");
        std::fs::create_dir_all(&outside).expect("dirs");
        write_skill(&outside, "far", "---\nname: far\ndescription: d\n---\n");
        let far = load_one(&outside.join("far"), SkillOrigin::User, &project)
            .expect("loads");
        assert!(far.source_path.is_none(), "a skill outside the workspace reports no path");
    }

    #[test]
    fn discovery_is_stable_and_sorted_by_name() {
        let dir = TempDir::new();
        let project = dir.path().join(".coda").join("skills");
        std::fs::create_dir_all(&project).expect("dirs");
        for name in ["zebra", "alpha", "middle"] {
            write_skill(&project, name, &format!("---\nname: {name}\ndescription: d\n---\n"));
        }
        let names: Vec<String> =
            discover(dir.path()).into_iter().map(|s| s.name).collect();
        let mut sorted = names.clone();
        sorted.sort();
        assert_eq!(names, sorted, "listing must be stable, or parity tests flap");
    }

    #[test]
    fn origins_serialize_in_lowercase() {
        assert_eq!(SkillOrigin::Claude.as_str(), "claude");
        assert_eq!(SkillOrigin::Project.as_str(), "project");
        assert_eq!(SkillOrigin::Foreign.as_str(), "foreign");
    }
}
