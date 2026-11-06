# Claude-format plugins Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Discover Claude-format plugins (user root, settings paths, directory/git marketplaces, project root) and load their skills, commands, agents, MCP servers, hooks and LSP servers behind one discovery step, with trust for executable components, `plugins/list`, and a `coda plugin` CLI.

**Architecture:** `coda-serve/src/plugins/` owns discovery, manifest parsing, settings, trust and CLI; it produces a `PluginSet` the host turns into a skill catalogue (new `skill` tool in coda-agent), a `SubagentRegistry` (short/qualified names), approved hooks / MCP / LSP, and the `plugins/list` inventory. Spec: `docs/superpowers/specs/2026-11-06-claude-format-plugins-design.md` (binding).

**Tech Stack:** Rust 2021, serde_json, sha2, tokio, clap; git CLI for installs.

---

## Conventions for every task

- Worktree `C:\Users\yurio\Documents\github\coda-cli\.worktrees\plugins` (branch `feat/plugins`); cargo from `rust\`, always `--offline`.
- TDD: failing test first in the `#[cfg(test)] mod tests` of the file changed; fixtures for host-level tests in `rust\crates\coda-serve\testdata\plugins\`.
- Do NOT `cargo fmt` whole crates; keep new code rustfmt-style by hand. No new warnings (`cargo clippy -p <crate> --offline` must not add warnings in touched code).
- Tests: `cargo test -p coda-agent <filter> --offline`, `cargo test -p coda-serve <filter> --offline`, `cargo test -p coda-proto --features schema --offline`, `cargo test -p coda --offline`, `cargo test -p coda-tui <filter> --offline`.
- Commit after each task, conventional commit, trailer `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.

## File structure

- Create `coda-serve/src/plugins/mod.rs` — `PluginSource`, `PluginDescriptor`, `PluginSet`, `discover_plugins(workspace, home_coda_dir, settings) -> PluginSet`, inventory JSON.
- Create `coda-serve/src/plugins/manifest.rs` — `PluginManifest::load(root) -> Result<(PluginManifest, raw_bytes), String>`, `resolve_contained(root, rel) -> Result<PathBuf, String>`.
- Create `coda-serve/src/plugins/settings.rs` — `PluginSettings::from_settings(&Value) -> (PluginSettings, Vec<String>)`, legacy `plugin-state.json` disabled list.
- Create `coda-serve/src/plugins/components.rs` — MCP / hooks / LSP extraction with `${CLAUDE_PLUGIN_ROOT}`/`${CODA_PLUGIN_ROOT}` expansion; `ExecComponent { kind, name, definition: Value }`.
- Create `coda-serve/src/plugins/trust.rs` — `PluginTrustStore` over `plugin-trust.json` (`pluginApprovals`), `approval_key(...)`.
- Create `coda-serve/src/plugins/cli.rs` + `install.rs` — `coda plugin …` implementation (`PluginArgs` clap types live here too).
- Modify `coda-serve/src/skills.rs` — plugin roots, qualified names, `plugin` field, `resolve(name)`.
- Modify `coda-serve/src/host.rs` — build `PluginSet`, skill tool, registry, hooks, LSP, `plugins_list`, `hooks_list` plugin field, SessionStart.
- Modify `coda-serve/src/mcp.rs` — append approved plugin servers.
- Modify `coda-agent/src/subagents/{plugin_loader,registry,host}.rs`, `tools/{task,skill(new),mod}.rs`, `hooks/{mod,runner}.rs`, `lsp/plugin_loader.rs`.
- Modify `coda-proto/src/messages.rs` (WireSkill.plugin, WirePlugin fields), `schema/catalog.rs` (plugins/list reusable) + regenerate `docs/protocol/{catalog.json,schemas/*}`.
- Modify `coda/src/main.rs` — `Plugin` subcommand.
- Modify `coda-tui/src/app/slash/plugins.rs` — `/skill` fallback to the skill tool; `/plugin list` shows source/errors.
- Docs: `docs/skills-and-plugins.md`, `docs/protocol/catalog.md`, `docs/serve-protocol.md` (plugins/list), `README.md` (CLI line), `version.json` → 0.1.178.

## Tasks

### Task 1: Manifest + containment (`plugins/manifest.rs`)
- [ ] Tests: `.claude-plugin/plugin.json` preferred over root `plugin.json`; root-only layout loads; missing manifest → `None`; invalid JSON → `Err("invalid plugin.json: …")`; missing `name` → Err; `author` string and `{name}` both parsed; `skills` string or array; unknown key ignored; `resolve_contained(root,"../x")` → Err containing `escapes`; absolute path → Err; `./skills` OK.
- [ ] Implement `PluginManifest { name, version, description, author, skills: Vec<String>, agents: Vec<String>, commands: Vec<String>, mcp_servers: Option<Value>, hooks: Option<Value>, lsp_servers: Option<Value>, raw: Value }`, `manifest_path(root) -> Option<PathBuf>`, `load(root) -> Option<Result<(PluginManifest, Vec<u8>), String>>`.
- [ ] Run `cargo test -p coda-serve plugins::manifest --offline`; commit.

### Task 2: Settings + enablement (`plugins/settings.rs`)
- [ ] Tests: parses `enabled`/`paths`/`marketplaces` (directory + git); wrong types produce error strings not panics; `is_enabled(name, source)` rules (explicit wins; marketplace default off; others default on; legacy disabled list disables).
- [ ] Implement `PluginSettings { enabled: BTreeMap<String,bool>, paths: Vec<PathBuf>, marketplaces: Vec<MarketplaceRef{id, kind: Directory|Git, path, url}>, legacy_disabled: BTreeSet<String> }`; git marketplace path = `<coda_dir>/marketplaces/<id>` when `path` absent.
- [ ] Commit.

### Task 3: Discovery (`plugins/mod.rs`)
- [ ] Tests (temp dirs): user root plugin found with source `user`; path entry that is a plugin and a path entry that contains plugins; directory marketplace via `marketplace.json` and via `plugins/*` scan, source `marketplace:<id>`, disabled by default and enabled when `enabled[name]=true`; project root source `project`; later source replaces earlier by name and records shadow note; malformed manifest isolated (other plugins still load, bad one listed with error); missing roots fine; traversal in `skills: ["../../x"]` → descriptor error, other components still load.
- [ ] Implement `discover_plugins(workspace: &Path, coda_dir: &Path, settings: &PluginSettings) -> PluginSet` producing `PluginDescriptor { name, version, description, root (canonical), source, manifest: Option<PluginManifest>, manifest_bytes, enabled, errors, skill_dirs, command_dirs, agent_dirs }`.
- [ ] Commit.

### Task 4: Skills with plugins (`skills.rs`)
- [ ] Tests: plugin skill listed as `deep-review:review`, origin `plugin`, `plugin: deep-review`; two plugins with `review` both listed; plugin `commands/foo.md` listed as `p:foo`; `resolve("review")` with project `review` → project; with only two plugin `review`s → ambiguous error naming both; with one plugin + user → plugin; qualified resolves exactly; disabled plugin contributes nothing.
- [ ] Implement `discover(workspace, plugins: &[&PluginDescriptor])` (+ keep a `discover(workspace)` shim used by tests? No — update callers), `SkillInfo.plugin: Option<String>`, `#[serde(skip)] dir: PathBuf`, `#[serde(skip)] plugin_root: Option<PathBuf>`, `resolve(&[SkillInfo], name) -> Result<&SkillInfo, String>`.
- [ ] Commit.

### Task 5: Agents (`plugin_loader.rs`, `registry.rs`)
- [ ] Tests: Claude agent with `name: skeptic` and no `type` loads as type `skeptic`; file stem fallback; registry: qualified `deep-review:skeptic` resolves; short `skeptic` resolves when unique; ambiguous short → `try_resolve` Err listing both; built-in still wins; `${CLAUDE_PLUGIN_ROOT}` expanded in body via new `load_from_directory_with_root`.
- [ ] Implement; `SubagentRegistry::try_resolve(Option<&str>) -> Result<SubagentDefinition,String>`, `plugin_agents()` accessor; `SubagentHost::with_registry`, used in `start_prompts`/`run_inner`.
- [ ] Commit.

### Task 6: Skill tool + task tool description (coda-agent)
- [ ] Move `bind_skill_args` to `coda-agent/src/tools/skill.rs` (TUI re-exports). Tests: output starts with `Base directory for this skill: <dir>`; plugin skill output contains `Plugin root:` and expands both root vars; `$ARGUMENTS` bound; unknown name → error listing available; ambiguous → error; edits to SKILL.md visible on next call; description lists skills.
- [ ] `SkillCatalogEntry { name, short_name, description, dir, plugin: Option<String>, plugin_root: Option<PathBuf>, model_invocable, origin_rank, plugin_agents: Vec<String> }`; `SkillTool::new(entries)`; resolution identical to Task 4 (shared via coda-agent `skill::resolve_entry`, skills.rs delegates).
- [ ] `TaskTool` keeps unit struct; new `PluginAwareTaskTool { description }` delegating to `start_subagent`.
- [ ] Commit.

### Task 7: Trust store (`plugins/trust.rs`)
- [ ] Tests: `approval_key` changes when manifest bytes change, when component JSON changes, when project root changes (project only); store round-trips with existing `pluginApprovals` content preserved; `approve`/`is_approved`.
- [ ] Commit.

### Task 8: Executable components (`plugins/components.rs`)
- [ ] Tests: inline `mcpServers` and file-path `mcpServers` (both `{mcpServers:{}}` and bare map), escape rejected; stdio server → `McpConnectable` named `plugin_<p>_<s>` with vars expanded and both env vars injected; http server → `McpHttpConnectable`; hooks Claude format (with/without outer `hooks`) → `UserHook`s with `plugin_origin`, expanded command, timeout, matcher, scope; default `hooks/hooks.json` and `.mcp.json` picked up.
- [ ] `PluginSet::exec_components(desc) -> Vec<ExecComponent>`; `pending_approval(desc, store, workspace)`.
- [ ] Commit.

### Task 9: Hooks env + SessionStart (coda-agent hooks)
- [ ] `UserHook.env: HashMap<String,String>` `#[serde(skip)]`; `HookExecutor::exec_with_env` default → `exec`; `ShellHookExecutor` sets env. Test with MockExecutor capturing env.
- [ ] `HookRunner::run_session_start(source, session_id, cwd, cancel) -> SessionStartResult { additional_context }`, `has_session_start`. Tests: merges two hooks' `hookSpecificOutput.additionalContext`; disabled hook never runs; untrusted plugin hook skipped (fail-open).
- [ ] Commit.

### Task 10: Host wiring
- [ ] `ServeHost` gets `plugins: Arc<PluginSet>` built in `build` (settings from `load_settings_value()`, `coda_dir()`); test constructor isolates via `CODA_HOME`-free path: `PluginSet::empty()` unless `build_plugins` is called; add `host.plugins_list()` real inventory; skill tool registered when catalogue non-empty; `PluginAwareTaskTool` when plugin agents exist; registry into both SubagentHosts; plugin hooks into `user_hooks` (approved pre-trusted in `hook_trust_store`, unapproved disabled); `hooks_list` adds `plugin` and fixes `trusted` for plugin hooks; LSP from approved plugin servers; SessionStart once on first prompt.
- [ ] `mcp.rs`: `connect_mcp` appends approved plugin servers (discovered with the same settings).
- [ ] Host test with `testdata/plugins` fixtures: `plugins_list` → 2 plugins, 4 skills, 3 agents; disabled → no components; `skills_list` shows qualified names.
- [ ] Commit.

### Task 11: Proto, schemas, catalog
- [ ] `WireSkill.plugin`, `WirePlugin` new optional fields; `PluginsListResult.errors`; catalog availability `reusable` for plugins/list; regenerate schema files with a temporary untracked emitter; `cargo test -p coda-proto --features schema --offline`.
- [ ] Commit.

### Task 12: CLI
- [ ] `coda plugin` subcommands in `coda-serve/src/plugins/cli.rs` (`PluginArgs` clap), `main.rs` dispatch before runtime like `Auth`. Tests (pure fns with temp dirs + settings file path injection): install local path adds to `paths`; enable/disable writes `enabled`; marketplace add/remove; remove path plugin; install `name@market` enables; git install with a local bare repo (`git init` in temp) writes receipt with commit; approve `--yes` writes trust entries; update re-approval detection.
- [ ] Commit.

### Task 13: TUI
- [ ] `/skill` fallback prompt; `/plugin list` prints source/enabled/trust/errors. Tests for the pure prompt builder.
- [ ] Commit.

### Task 14: Docs, version, verification
- [ ] Docs listed above; `version.json` build 178.
- [ ] Full `cargo test` for coda-agent, coda-serve, coda-proto (schema), coda-tui, coda; `cargo clippy --workspace --offline` (no new warnings in touched files); `dotnet test` only if C# touched (it is not).
- [ ] E2E: isolated `CODA_HOME` + settings referencing fixtures and `ai-review-plugins`; real `coda serve` via stdio → `initialize`, `plugins/list`, `skills/list`, `hooks/list`; then LLM-backed run proving agent spawn and relative reads.
- [ ] Commit, push, PR, merge, install.
