//! `coda plugin …` — thin host-local wrappers over discovery, settings,
//! approvals and git installs. Changes apply to the next engine instance.

use std::io::{IsTerminal, Write};
use std::path::{Path, PathBuf};

use clap::{Args, Subcommand};
use serde_json::{json, Map, Value};

use super::install;
use super::settings::git_marketplace_dir;
use super::trust::PluginTrustStore;
use super::{discover_from_settings, PluginDescriptor, PluginSet, PluginSource};

#[derive(Debug, Args)]
pub struct PluginArgs {
    #[command(subcommand)]
    pub command: PluginCommand,
}

#[derive(Debug, Subcommand)]
pub enum PluginCommand {
    /// List discovered plugins, their components and trust state.
    List {
        /// Print the `plugins/list` JSON instead of a table.
        #[arg(long)]
        json: bool,
    },
    /// Install from a local directory (loaded in place), a git URL
    /// (`url[#ref]`, cloned into ~/.coda/plugins/<name>) or `name@marketplace`.
    Install {
        source: String,
        /// Approve executable components (hooks, MCP and LSP servers) without asking.
        #[arg(long)]
        yes: bool,
    },
    /// Enable a plugin (settings `plugins.enabled.<name> = true`).
    Enable { name: String },
    /// Disable a plugin without removing it.
    Disable { name: String },
    /// Update git-installed plugins and git marketplaces (all when no name).
    Update {
        name: Option<String>,
        #[arg(long)]
        yes: bool,
    },
    /// Remove a plugin: deletes a git install, or drops a path reference.
    Remove { name: String },
    /// Approve a plugin's executable components.
    Approve {
        name: String,
        #[arg(long)]
        yes: bool,
    },
    /// Manage plugin marketplaces.
    Marketplace {
        #[command(subcommand)]
        command: MarketplaceCommand,
    },
}

#[derive(Debug, Subcommand)]
pub enum MarketplaceCommand {
    /// Register a marketplace: a local directory or a git repository.
    Add {
        id: String,
        #[arg(long, conflicts_with = "git", required_unless_present = "git")]
        path: Option<PathBuf>,
        #[arg(long)]
        git: Option<String>,
    },
    /// List registered marketplaces.
    List,
    /// Unregister a marketplace (a git clone is deleted).
    Remove { id: String },
    /// Pull git marketplaces (all when no id).
    Update { id: Option<String> },
}

/// Where the CLI reads and writes, injected for tests.
pub struct CliEnv<'a> {
    pub coda_dir: PathBuf,
    pub settings_path: PathBuf,
    pub workspace: PathBuf,
    pub out: &'a mut dyn Write,
    /// Asks a yes/no question; `false` when nobody can answer.
    pub confirm: &'a mut dyn FnMut(&str) -> bool,
}

/// Entry point used by `coda plugin`. Returns the process exit code.
pub fn run(args: PluginArgs) -> i32 {
    let mut stdout = std::io::stdout();
    let mut confirm = |question: &str| -> bool {
        if !std::io::stdin().is_terminal() {
            return false;
        }
        print!("{question} [y/N] ");
        let _ = std::io::stdout().flush();
        let mut line = String::new();
        std::io::stdin().read_line(&mut line).is_ok()
            && matches!(line.trim(), "y" | "Y" | "yes" | "YES")
    };
    let coda_dir = coda_auth::coda_dir();
    let mut env = CliEnv {
        settings_path: coda_dir.join("settings.json"),
        coda_dir,
        workspace: std::env::current_dir().unwrap_or_else(|_| PathBuf::from(".")),
        out: &mut stdout,
        confirm: &mut confirm,
    };
    match execute(&mut env, args.command) {
        Ok(()) => 0,
        Err(e) => {
            eprintln!("coda plugin: {e}");
            1
        }
    }
}

pub fn execute(env: &mut CliEnv, command: PluginCommand) -> Result<(), String> {
    match command {
        PluginCommand::List { json } => list(env, json),
        PluginCommand::Install { source, yes } => install_cmd(env, &source, yes),
        PluginCommand::Enable { name } => set_enabled(env, &name, true),
        PluginCommand::Disable { name } => set_enabled(env, &name, false),
        PluginCommand::Update { name, yes } => update(env, name.as_deref(), yes),
        PluginCommand::Remove { name } => remove(env, &name),
        PluginCommand::Approve { name, yes } => {
            let set = discover(env)?;
            let plugin = find(&set, &name)?;
            approve(env, plugin, yes)
        }
        PluginCommand::Marketplace { command } => marketplace(env, command),
    }
}

// ── settings helpers ─────────────────────────────────────────────────────────

fn read_settings(env: &CliEnv) -> Result<Value, String> {
    coda_boot::settings_store::read_document(&env.settings_path)
        .map(|d| d.unwrap_or_else(|| Value::Object(Map::new())))
        .map_err(|e| e.to_string())
}

/// Applies `edit` to the `plugins` object of the settings, as a locked delta.
fn edit_settings(env: &CliEnv, edit: impl FnOnce(&mut Map<String, Value>)) -> Result<(), String> {
    let original = read_settings(env)?;
    let mut edited = original.clone();
    let root = edited
        .as_object_mut()
        .ok_or("settings.json is not an object")?;
    let plugins = root
        .entry("plugins")
        .or_insert_with(|| Value::Object(Map::new()));
    if !plugins.is_object() {
        return Err("settings `plugins` is not an object".into());
    }
    edit(plugins.as_object_mut().expect("object"));
    if let Some(dir) = env.settings_path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    coda_boot::settings_store::save_changes(&env.settings_path, &original, &edited)
        .map_err(|e| e.to_string())
}

fn object<'a>(map: &'a mut Map<String, Value>, key: &str) -> &'a mut Map<String, Value> {
    let v = map.entry(key).or_insert_with(|| Value::Object(Map::new()));
    if !v.is_object() {
        *v = Value::Object(Map::new());
    }
    v.as_object_mut().expect("object")
}

fn array<'a>(map: &'a mut Map<String, Value>, key: &str) -> &'a mut Vec<Value> {
    let v = map.entry(key).or_insert_with(|| Value::Array(Vec::new()));
    if !v.is_array() {
        *v = Value::Array(Vec::new());
    }
    v.as_array_mut().expect("array")
}

fn discover(env: &CliEnv) -> Result<PluginSet, String> {
    let settings = read_settings(env)?;
    Ok(discover_from_settings(
        &env.workspace,
        &env.coda_dir,
        &settings,
    ))
}

fn find<'a>(set: &'a PluginSet, name: &str) -> Result<&'a PluginDescriptor, String> {
    set.find(name)
        .ok_or_else(|| format!("no plugin named '{name}' (see `coda plugin list`)"))
}

// ── commands ─────────────────────────────────────────────────────────────────

fn list(env: &mut CliEnv, as_json: bool) -> Result<(), String> {
    let set = discover(env)?;
    let trust = PluginTrustStore::load(&env.coda_dir);
    let inventory = super::wiring::inventory(&env.workspace, &set, &trust);
    if as_json {
        writeln!(
            env.out,
            "{}",
            serde_json::to_string_pretty(&inventory).unwrap_or_default()
        )
        .ok();
        return Ok(());
    }
    if set.plugins.is_empty() {
        writeln!(env.out, "No plugins found.").ok();
    }
    for item in inventory["plugins"].as_array().into_iter().flatten() {
        let c = &item["components"];
        let len = |k: &str| c[k].as_array().map_or(0, Vec::len);
        writeln!(
            env.out,
            "{} {}  [{}]  {}  trust: {}\n    {}\n    skills: {}  commands: {}  agents: {}  mcpServers: {}  hooks: {}  lspServers: {}",
            item["name"].as_str().unwrap_or("?"),
            item["version"].as_str().unwrap_or(""),
            item["source"].as_str().unwrap_or("?"),
            if item["enabled"] == true { "enabled" } else { "disabled" },
            item["trust"].as_str().unwrap_or("?"),
            item["root"].as_str().unwrap_or(""),
            len("skills"),
            len("commands"),
            len("agents"),
            len("mcpServers"),
            len("hooks"),
            len("lspServers"),
        )
        .ok();
        for p in item["pendingApproval"].as_array().into_iter().flatten() {
            writeln!(env.out, "    needs approval: {}", p.as_str().unwrap_or("")).ok();
        }
        for e in item["errors"].as_array().into_iter().flatten() {
            writeln!(env.out, "    error: {}", e.as_str().unwrap_or("")).ok();
        }
    }
    for e in &set.errors {
        writeln!(env.out, "error: {e}").ok();
    }
    Ok(())
}

fn set_enabled(env: &mut CliEnv, name: &str, enabled: bool) -> Result<(), String> {
    edit_settings(env, |p| {
        object(p, "enabled").insert(name.to_owned(), Value::Bool(enabled));
    })?;
    let known = discover(env)?.find(name).is_some();
    writeln!(
        env.out,
        "{} plugin '{name}'.{} Start a new session to apply.",
        if enabled { "Enabled" } else { "Disabled" },
        if known {
            ""
        } else {
            " (No such plugin is installed yet.)"
        }
    )
    .ok();
    Ok(())
}

fn install_cmd(env: &mut CliEnv, source: &str, yes: bool) -> Result<(), String> {
    let name = if let Some((plugin, market)) = parse_marketplace_ref(source) {
        install_from_marketplace(env, plugin, market)?
    } else if install::looks_like_git_url(source) {
        install_from_git(env, source)?
    } else {
        install_from_path(env, Path::new(source))?
    };
    let set = discover(env)?;
    let plugin = find(&set, &name)?;
    writeln!(
        env.out,
        "Installed plugin '{}' from {} ({}).",
        plugin.name,
        plugin.source.label(),
        plugin.root.display()
    )
    .ok();
    for e in &plugin.errors {
        writeln!(env.out, "  warning: {e}").ok();
    }
    approve(env, plugin, yes)
}

/// `name@marketplace`, where neither part looks like a path or URL.
fn parse_marketplace_ref(source: &str) -> Option<(&str, &str)> {
    let (plugin, market) = source.split_once('@')?;
    let valid = |s: &str| super::manifest::is_valid_name(s);
    (valid(plugin) && valid(market) && !source.contains('/') && !source.contains('\\'))
        .then_some((plugin, market))
}

fn install_from_marketplace(
    env: &mut CliEnv,
    plugin: &str,
    market: &str,
) -> Result<String, String> {
    let settings = read_settings(env)?;
    let (parsed, _) = super::PluginSettings::from_settings(&settings, &env.coda_dir);
    let m = parsed
        .marketplaces
        .iter()
        .find(|m| m.id == market)
        .ok_or_else(|| {
            format!("no marketplace '{market}' (add it with `coda plugin marketplace add`)")
        })?;
    if let super::MarketplaceKind::Git { url } = &m.kind {
        if !m.path.is_dir() {
            install::clone(url, None, &m.path)?;
        }
    }
    let set = discover(env)?;
    let found = set
        .plugins
        .iter()
        .find(|p| {
            p.name.eq_ignore_ascii_case(plugin)
                && p.source == PluginSource::Marketplace(market.to_owned())
        })
        .ok_or_else(|| format!("marketplace '{market}' offers no plugin '{plugin}'"))?;
    let name = found.name.clone();
    edit_settings(env, |p| {
        object(p, "enabled").insert(name.clone(), Value::Bool(true));
    })?;
    Ok(name)
}

fn install_from_git(env: &mut CliEnv, source: &str) -> Result<String, String> {
    let (url, git_ref) = install::split_ref(source);
    let plugins_dir = env.coda_dir.join("plugins");
    std::fs::create_dir_all(&plugins_dir).map_err(|e| e.to_string())?;
    let staging = install::staging_dir(&plugins_dir);
    let result = (|| {
        install::clone(&url, git_ref.as_deref(), &staging)?;
        let (manifest, _) = super::manifest::load(&staging)
            .ok_or("the repository root is not a plugin (no plugin.json); for a marketplace use `coda plugin marketplace add --git`")??;
        let dest = plugins_dir.join(&manifest.name);
        if dest.exists() {
            return Err(format!(
                "plugin '{}' is already installed at {}",
                manifest.name,
                dest.display()
            ));
        }
        std::fs::rename(&staging, &dest)
            .map_err(|e| format!("cannot move the checkout into place: {e}"))?;
        let commit = install::head_commit(&dest)?;
        install::write_receipt(&dest, &url, git_ref.as_deref(), &commit)?;
        Ok(manifest.name)
    })();
    if staging.exists() {
        let _ = std::fs::remove_dir_all(&staging);
    }
    result
}

fn install_from_path(env: &mut CliEnv, path: &Path) -> Result<String, String> {
    if !path.is_dir() {
        return Err(format!("not a directory: {}", path.display()));
    }
    let root = super::canonical(path);
    let (manifest, _) = super::manifest::load(&root)
        .ok_or_else(|| format!("no plugin.json in {}", root.display()))??;
    let entry = root.to_string_lossy().into_owned();
    let name = manifest.name.clone();
    edit_settings(env, |p| {
        let paths = array(p, "paths");
        if !paths.iter().any(|v| {
            v.as_str()
                .is_some_and(|s| super::canonical(Path::new(s)) == root)
        }) {
            paths.push(Value::String(entry));
        }
        if let Some(Value::Bool(false)) = p.get("enabled").and_then(|e| e.get(&name)) {
            object(p, "enabled").insert(name.clone(), Value::Bool(true));
        }
    })?;
    Ok(manifest.name)
}

/// Shows pending executable components and records approval.
fn approve(env: &mut CliEnv, plugin: &PluginDescriptor, yes: bool) -> Result<(), String> {
    let mut trust = PluginTrustStore::load(&env.coda_dir);
    let pending: Vec<_> = trust.pending(plugin).into_iter().cloned().collect();
    if pending.is_empty() {
        if !plugin.exec.is_empty() {
            writeln!(
                env.out,
                "All executable components of '{}' are approved.",
                plugin.name
            )
            .ok();
        }
        return Ok(());
    }
    writeln!(
        env.out,
        "Plugin '{}' ({}) wants to run:",
        plugin.name,
        plugin.source.label()
    )
    .ok();
    for c in &pending {
        writeln!(env.out, "  {}: {}", c.label(), c.definition).ok();
    }
    let granted = yes || (env.confirm)("Approve these components?");
    if !granted {
        writeln!(
            env.out,
            "Not approved: they will not run. Approve later with `coda plugin approve {}`.",
            plugin.name
        )
        .ok();
        return Ok(());
    }
    for c in &pending {
        trust.approve(plugin, c);
    }
    trust
        .save()
        .map_err(|e| format!("cannot save plugin approvals: {e}"))?;
    writeln!(
        env.out,
        "Approved {} component(s). Start a new session to apply.",
        pending.len()
    )
    .ok();
    Ok(())
}

fn update(env: &mut CliEnv, name: Option<&str>, yes: bool) -> Result<(), String> {
    let settings = read_settings(env)?;
    let (parsed, _) = super::PluginSettings::from_settings(&settings, &env.coda_dir);
    let mut updated = 0;
    for m in &parsed.marketplaces {
        if let super::MarketplaceKind::Git { url } = &m.kind {
            if name.is_some_and(|n| n != m.id) {
                continue;
            }
            if m.path.is_dir() {
                if !install::is_own_checkout(&m.path) {
                    return Err(format!(
                        "marketplace '{}' at {} is not a git checkout of its own",
                        m.id,
                        m.path.display()
                    ));
                }
                install::git(Some(&m.path), &["pull", "--ff-only", "--quiet"])?;
            } else {
                install::clone(url, None, &m.path)?;
            }
            writeln!(env.out, "Updated marketplace '{}'.", m.id).ok();
            updated += 1;
        }
    }
    let set = discover(env)?;
    let user_dir = super::canonical(&env.coda_dir.join("plugins"));
    for plugin in &set.plugins {
        if name.is_some_and(|n| !n.eq_ignore_ascii_case(&plugin.name)) {
            continue;
        }
        // Only git installs this command made: a receipt in a project or
        // referenced directory is just a file someone shipped.
        let installed_here =
            plugin.source == PluginSource::User && plugin.root.starts_with(&user_dir);
        let receipt = if installed_here {
            install::read_receipt(&plugin.root)
        } else {
            None
        };
        if let Some(receipt) = receipt {
            let url = receipt["url"].as_str().unwrap_or_default().to_owned();
            let git_ref = receipt["ref"].as_str().map(str::to_owned);
            let before = receipt["commit"].as_str().unwrap_or_default().to_owned();
            let commit = install::fetch_and_reset(&plugin.root, git_ref.as_deref())?;
            install::write_receipt(&plugin.root, &url, git_ref.as_deref(), &commit)?;
            writeln!(
                env.out,
                "Updated '{}': {} -> {}.",
                plugin.name,
                before.get(..12).unwrap_or(&before),
                commit.get(..12).unwrap_or(&commit)
            )
            .ok();
            updated += 1;
        } else if name.is_some() {
            writeln!(
                env.out,
                "'{}' is loaded in place from {}; nothing to update.",
                plugin.name,
                plugin.root.display()
            )
            .ok();
        }
    }
    if updated == 0 && name.is_none() {
        writeln!(
            env.out,
            "Nothing to update (no git installs or git marketplaces)."
        )
        .ok();
    }
    // Any executable component whose definition or manifest changed now has
    // a new approval key, so it shows up as pending again.
    let set = discover(env)?;
    for plugin in set
        .plugins
        .iter()
        .filter(|p| name.is_none_or(|n| n.eq_ignore_ascii_case(&p.name)))
    {
        approve(env, plugin, yes)?;
    }
    Ok(())
}

fn remove(env: &mut CliEnv, name: &str) -> Result<(), String> {
    let set = discover(env)?;
    let plugin = find(&set, name)?;
    let user_dir = super::canonical(&env.coda_dir.join("plugins"));
    match &plugin.source {
        PluginSource::User if plugin.root.starts_with(&user_dir) => {
            std::fs::remove_dir_all(&plugin.root)
                .map_err(|e| format!("cannot delete {}: {e}", plugin.root.display()))?;
            edit_settings(env, |p| {
                object(p, "enabled").remove(&plugin.name);
            })?;
            writeln!(
                env.out,
                "Removed '{}' ({}).",
                plugin.name,
                plugin.root.display()
            )
            .ok();
        }
        PluginSource::Path => {
            let root = plugin.root.clone();
            let mut dropped = false;
            edit_settings(env, |p| {
                let paths = array(p, "paths");
                let before = paths.len();
                paths.retain(|v| {
                    !v.as_str()
                        .is_some_and(|s| super::canonical(Path::new(s)) == root)
                });
                dropped = paths.len() != before;
                if !dropped {
                    // Referenced through a containing directory: disable instead.
                    object(p, "enabled").insert(plugin.name.clone(), Value::Bool(false));
                }
            })?;
            if dropped {
                writeln!(
                    env.out,
                    "Removed the path reference to '{}'; its files were not touched.",
                    plugin.name
                )
                .ok();
            } else {
                writeln!(
                    env.out,
                    "'{}' comes from a referenced directory; it is now disabled.",
                    plugin.name
                )
                .ok();
            }
        }
        PluginSource::Marketplace(_) => {
            set_enabled(env, &plugin.name, false)?;
        }
        PluginSource::Project | PluginSource::User => {
            return Err(format!(
                "'{}' lives at {}; delete that directory to remove it",
                plugin.name,
                plugin.root.display()
            ));
        }
    }
    Ok(())
}

fn marketplace(env: &mut CliEnv, command: MarketplaceCommand) -> Result<(), String> {
    match command {
        MarketplaceCommand::Add { id, path, git } => {
            if !super::manifest::is_valid_name(&id) {
                return Err(format!("marketplace id '{id}' is not valid"));
            }
            let entry = match (path, git) {
                (Some(path), _) => {
                    if !path.is_dir() {
                        return Err(format!("not a directory: {}", path.display()));
                    }
                    json!({ "source": "directory", "path": super::canonical(&path).to_string_lossy() })
                }
                (None, Some(url)) => {
                    let dest = git_marketplace_dir(&env.coda_dir, &id);
                    if !dest.exists() {
                        install::clone(&url, None, &dest)?;
                    }
                    json!({ "source": "git", "url": url })
                }
                (None, None) => return Err("give --path <dir> or --git <url>".into()),
            };
            edit_settings(env, |p| {
                object(p, "marketplaces").insert(id.clone(), entry);
            })?;
            let set = discover(env)?;
            let offered: Vec<&str> = set
                .plugins
                .iter()
                .filter(|p| p.source == PluginSource::Marketplace(id.clone()))
                .map(|p| p.name.as_str())
                .collect();
            writeln!(
                env.out,
                "Added marketplace '{id}' offering: {}. Install with `coda plugin install <name>@{id}`.",
                if offered.is_empty() { "(nothing)".to_owned() } else { offered.join(", ") }
            )
            .ok();
            Ok(())
        }
        MarketplaceCommand::List => {
            let settings = read_settings(env)?;
            let (parsed, errors) = super::PluginSettings::from_settings(&settings, &env.coda_dir);
            if parsed.marketplaces.is_empty() {
                writeln!(env.out, "No marketplaces registered.").ok();
            }
            for m in &parsed.marketplaces {
                let kind = match &m.kind {
                    super::MarketplaceKind::Directory => "directory".to_owned(),
                    super::MarketplaceKind::Git { url } => format!("git {url}"),
                };
                writeln!(env.out, "{}  {}  {}", m.id, kind, m.path.display()).ok();
            }
            for e in errors {
                writeln!(env.out, "error: {e}").ok();
            }
            Ok(())
        }
        MarketplaceCommand::Remove { id } => {
            let mut existed = false;
            edit_settings(env, |p| {
                existed = object(p, "marketplaces").remove(&id).is_some();
            })?;
            let clone = git_marketplace_dir(&env.coda_dir, &id);
            if clone.is_dir() {
                let _ = std::fs::remove_dir_all(&clone);
            }
            writeln!(
                env.out,
                "{}",
                if existed {
                    format!("Removed marketplace '{id}'.")
                } else {
                    format!("No marketplace '{id}'.")
                }
            )
            .ok();
            Ok(())
        }
        MarketplaceCommand::Update { id } => update(env, id.as_deref(), false),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::plugins::test_support::{plugin, write};

    struct Harness {
        _home: tempfile::TempDir,
        _ws: tempfile::TempDir,
        coda: PathBuf,
        ws: PathBuf,
        out: Vec<u8>,
        answer: bool,
    }

    impl Harness {
        fn new() -> Self {
            let home = tempfile::tempdir().unwrap();
            let ws = tempfile::tempdir().unwrap();
            Self {
                coda: home.path().join(".coda"),
                ws: ws.path().to_path_buf(),
                _home: home,
                _ws: ws,
                out: Vec::new(),
                answer: false,
            }
        }

        fn run(&mut self, args: &[&str]) -> Result<String, String> {
            use clap::Parser;
            #[derive(Parser)]
            struct Cli {
                #[command(subcommand)]
                command: PluginCommand,
            }
            let mut argv = vec!["plugin"];
            argv.extend_from_slice(args);
            let cli = Cli::try_parse_from(argv).map_err(|e| e.to_string())?;
            self.out.clear();
            let answer = self.answer;
            let mut confirm = move |_: &str| answer;
            let mut env = CliEnv {
                settings_path: self.coda.join("settings.json"),
                coda_dir: self.coda.clone(),
                workspace: self.ws.clone(),
                out: &mut self.out,
                confirm: &mut confirm,
            };
            execute(&mut env, cli.command)?;
            Ok(String::from_utf8_lossy(&self.out).into_owned())
        }

        fn settings(&self) -> Value {
            serde_json::from_str(&std::fs::read_to_string(self.coda.join("settings.json")).unwrap())
                .unwrap()
        }
    }

    #[test]
    fn a_local_path_install_is_a_reference_and_can_be_disabled_and_removed() {
        let mut h = Harness::new();
        let src = tempfile::tempdir().unwrap();
        plugin(&src.path().join("p"), "local-one");
        let out = h
            .run(&["install", &src.path().join("p").to_string_lossy()])
            .unwrap();
        assert!(
            out.contains("Installed plugin 'local-one' from path"),
            "{out}"
        );
        assert_eq!(
            h.settings()["plugins"]["paths"].as_array().unwrap().len(),
            1
        );

        h.run(&["disable", "local-one"]).unwrap();
        assert_eq!(h.settings()["plugins"]["enabled"]["local-one"], false);
        let listed = h.run(&["list"]).unwrap();
        assert!(
            listed.contains("local-one 1.0.0  [path]  disabled"),
            "{listed}"
        );
        h.run(&["enable", "local-one"]).unwrap();
        assert_eq!(h.settings()["plugins"]["enabled"]["local-one"], true);

        h.run(&["remove", "local-one"]).unwrap();
        assert!(h.settings()["plugins"]["paths"]
            .as_array()
            .unwrap()
            .is_empty());
        assert!(
            src.path().join("p/.claude-plugin/plugin.json").exists(),
            "files untouched"
        );
    }

    #[test]
    fn executable_components_need_an_answer_or_yes() {
        let mut h = Harness::new();
        let src = tempfile::tempdir().unwrap();
        let root = src.path().join("p");
        plugin(&root, "exec");
        write(
            &root.join(".mcp.json"),
            r#"{"mcpServers":{"srv":{"command":"x"}}}"#,
        );
        let out = h.run(&["install", &root.to_string_lossy()]).unwrap();
        assert!(
            out.contains("mcpServer:srv") && out.contains("Not approved"),
            "{out}"
        );
        assert!(h
            .run(&["list"])
            .unwrap()
            .contains("needs approval: mcpServer:srv"));

        let out = h.run(&["approve", "exec", "--yes"]).unwrap();
        assert!(out.contains("Approved 1 component"), "{out}");
        assert!(h.run(&["list"]).unwrap().contains("trust: approved"));

        // A changed definition asks again.
        write(
            &root.join(".mcp.json"),
            r#"{"mcpServers":{"srv":{"command":"y"}}}"#,
        );
        assert!(h
            .run(&["list"])
            .unwrap()
            .contains("needs approval: mcpServer:srv"));
        h.answer = true;
        assert!(h
            .run(&["approve", "exec"])
            .unwrap()
            .contains("Approved 1 component"));
    }

    #[test]
    fn marketplace_plugins_are_installed_by_enabling_them() {
        let mut h = Harness::new();
        let m = tempfile::tempdir().unwrap();
        plugin(&m.path().join("plugins/a"), "alpha");
        let out = h
            .run(&[
                "marketplace",
                "add",
                "local",
                "--path",
                &m.path().to_string_lossy(),
            ])
            .unwrap();
        assert!(out.contains("offering: alpha"), "{out}");
        assert!(h
            .run(&["list"])
            .unwrap()
            .contains("[marketplace:local]  disabled"));
        h.run(&["install", "alpha@local"]).unwrap();
        assert_eq!(h.settings()["plugins"]["enabled"]["alpha"], true);
        assert!(h
            .run(&["marketplace", "list"])
            .unwrap()
            .contains("local  directory"));
        h.run(&["marketplace", "remove", "local"]).unwrap();
        assert!(h.settings()["plugins"]["marketplaces"]
            .as_object()
            .unwrap()
            .is_empty());
        assert!(h
            .run(&["install", "alpha@nope"])
            .unwrap_err()
            .contains("no marketplace 'nope'"));
    }

    #[test]
    fn update_ignores_receipts_outside_the_user_plugin_directory() {
        let mut h = Harness::new();
        let root = h.ws.join(".coda/plugins/p");
        plugin(&root, "shipped");
        write(
            &root.join(install::RECEIPT_FILE),
            r#"{"source":"git","url":"https://x/r.git","ref":"--upload-pack=evil"}"#,
        );
        let out = h.run(&["update"]).unwrap();
        assert!(out.contains("Nothing to update"), "{out}");
        assert!(root.join(".claude-plugin/plugin.json").exists());
    }

    #[test]
    fn git_installs_write_a_receipt_and_update_to_new_commits() {
        if install::git(None, &["--version"]).is_err() {
            return;
        }
        let mut h = Harness::new();
        let repo = tempfile::tempdir().unwrap();
        plugin(repo.path(), "from-git");
        let g = |args: &[&str]| install::git(Some(repo.path()), args).unwrap();
        g(&["init", "--quiet"]);
        g(&["-c", "user.email=t@t", "-c", "user.name=t", "add", "."]);
        g(&[
            "-c",
            "user.email=t@t",
            "-c",
            "user.name=t",
            "commit",
            "--quiet",
            "-m",
            "one",
        ]);
        let url = format!(
            "file://{}",
            repo.path().to_string_lossy().replace('\\', "/")
        );

        let out = h.run(&["install", &url]).unwrap();
        assert!(
            out.contains("Installed plugin 'from-git' from user"),
            "{out}"
        );
        let root = h.coda.join("plugins/from-git");
        let receipt = install::read_receipt(&root).unwrap();
        assert_eq!(receipt["source"], "git");
        assert_eq!(receipt["commit"], g(&["rev-parse", "HEAD"]));
        assert!(receipt["contentHash"].as_str().unwrap().len() == 64);
        assert!(!h
            .coda
            .join("plugins")
            .read_dir()
            .unwrap()
            .flatten()
            .any(|e| e.file_name().to_string_lossy().starts_with(".staging")));

        write(
            &repo.path().join("skills/review/SKILL.md"),
            "---\nname: review\n---\nv2",
        );
        g(&[
            "-c",
            "user.email=t@t",
            "-c",
            "user.name=t",
            "commit",
            "--quiet",
            "-am",
            "two",
        ]);
        let out = h.run(&["update", "from-git"]).unwrap();
        assert!(out.contains("Updated 'from-git'"), "{out}");
        assert_eq!(
            install::read_receipt(&root).unwrap()["commit"],
            g(&["rev-parse", "HEAD"])
        );
        assert!(std::fs::read_to_string(root.join("skills/review/SKILL.md"))
            .unwrap()
            .ends_with("v2"));

        h.run(&["remove", "from-git"]).unwrap();
        assert!(!root.exists());
    }
}
