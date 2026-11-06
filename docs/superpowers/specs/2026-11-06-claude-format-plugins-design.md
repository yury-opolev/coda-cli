# Claude-format plugins: discovery and loading — design

**Source request:** e-mail "REQUEST CODA plugins" (2026-11-06). Its must-haves 1–7 and should-haves
8–10 are in scope; nice-to-haves 11 (`coda plugin import`) and 12 (`/plugins` TUI view backed by
`plugins/list` — the existing browser already calls it) are follow-ups unless noted.

## Problem

The Rust engine has the parts of a plugin system but nothing ties them together: `SkillOrigin::Plugin`
is never produced, `PluginAgentLoader` and `PluginLspServerLoader` are never called, `plugins/list`
is a stub, and `~/.coda/plugin-trust.json` is not read. In addition the Rust engine has **no
model-facing skill tool** (skills are only reachable by the TUI's `/skill`, which reads
`~/.coda/skills` and `.coda/skills` directly) and **never fires `SessionStart` hooks**. Both gaps
block the request's acceptance scenario, so both are closed here.

## Decisions

| Topic | Decision |
|---|---|
| Where the code lives | New module tree `coda-serve/src/plugins/` (`mod.rs` model + discovery, `manifest.rs`, `settings.rs`, `trust.rs`, `components.rs`, `cli.rs`, `install.rs`). Serve owns workspace/home notions (same reason `skills.rs` lives there). The `coda` binary depends on `coda-serve`, so the CLI wrapper reuses it. Generic, workspace-free pieces (skill tool, registry resolution, SessionStart runner) live in `coda-agent`. |
| Plugin recognition | A directory is a plugin when it has `.claude-plugin/plugin.json`, else a root `plugin.json`. |
| Discovery roots, in order (later wins on name clash) | 1. user `<CODA_HOME>/.coda/plugins/<dir>/`; 2. `plugins.paths[]` from user settings (each entry is a plugin dir, or a directory whose children are plugin dirs); 3. `plugins.marketplaces.<id>` with `source: "directory"` (`path`) or `source: "git"` (cloned into `<CODA_HOME>/.coda/marketplaces/<id>`) — plugins listed by `.claude-plugin/marketplace.json` (`plugins[].source` relative string) or, if absent, every plugin dir under `<path>/plugins/`; 4. project `<workspace>/.coda/plugins/<dir>/`. A name clash replaces the earlier descriptor and records `"shadowed <source> plugin at <root>"` on the winner. |
| Settings | Only the **user** `~/.coda/settings.json` key `plugins` is honoured (a project settings file must not be able to point Coda at arbitrary directories). Shape: `{ "enabled": {name: bool}, "paths": [string], "marketplaces": {id: {source, path?, url?}} }`. Malformed shapes are reported as per-plugin-list errors, never fatal. |
| Enablement | `plugins.enabled[name]` wins when present. Otherwise: marketplace plugins are **disabled** (a marketplace is a catalogue — enable with `coda plugin install name@id`/`enable`); every other source is enabled. Legacy `~/.coda/plugin-state.json` `disabledPlugins` also disables (TUI `/plugin disable` writes there). Disabled plugins are listed but contribute nothing. |
| Manifest subset | `name` (required; falls back to nothing → error), `version`, `description`, `author` (string or `{name}`), `skills`/`agents`/`commands` (string or array of relative paths; skills/commands **additive** to the defaults `./skills/`, `./commands/`; agents default `./agents/`, declared paths additive too), `mcpServers` (object, or string path to a JSON file holding `{mcpServers:{…}}` or the bare map; default `./.mcp.json` if present), `hooks` (object, or path; default `./hooks/hooks.json` if present), `lspServers` (handed to the LSP loader). Unknown keys → `tracing::debug!`, not an error. |
| Path containment | Every manifest path is resolved against the plugin root, canonicalised, and must stay inside the canonical root; otherwise it is rejected with a per-plugin error (`path escapes plugin root: ../x`). Absolute paths are rejected. |
| Failure isolation | Each plugin is parsed independently; any error is pushed to that descriptor's `errors` and discovery continues. A plugin whose manifest cannot be parsed is still listed (`name` = directory name, `errors` non-empty, contributes nothing). |
| Skills | `skills::discover(workspace, &plugins)`. Plugin skills are keyed by their **qualified** name `<plugin>:<skill>` so two plugins shipping `review` never shadow each other, and carry `plugin: "<name>"`, `origin: "plugin"`. Non-plugin skills keep today's keys and precedence. Plugin `commands/*.md` load as skills too (name = front-matter name or file stem). `skills/list` items gain optional `plugin`. |
| Short-name resolution | `name` exact (case-insensitive) on the qualified/plain name first. Otherwise, a short name `X` collects candidates: non-plugin skill `X` (its origin) and every plugin skill whose short name is `X`. The highest origin wins (project > plugin > user > claude > foreign). If that winning tier holds more than one plugin candidate → error `ambiguous skill 'X': deep-review:X, other:X`. |
| Model-facing skill tool | New `skill` tool in `coda-agent` (`tools/skill.rs`). Description lists enabled, model-invocable skills (`name — description`, description capped at 250 chars). Input `{ name, arguments? }`. It re-reads `SKILL.md` **at call time** (edits apply without restart), strips front matter, applies the existing `$ARGUMENTS/$1` binding (moved to coda-agent as `bind_skill_args`, TUI re-exports), expands `${CLAUDE_PLUGIN_ROOT}`, `${CODA_PLUGIN_ROOT}` and `${CLAUDE_SKILL_DIR}`, and returns `Base directory for this skill: <abs dir>` (+ `Plugin root: <abs root>` and the plugin's agent types for plugin skills) followed by the body. Relative references (`../review/SKILL.md`, `scripts/x.ps1`) therefore resolve against a real directory: nothing is copied. Registered only when at least one skill exists. |
| TUI `/skill <name>` | Unchanged for local project/user skills; a name it cannot find locally is delegated to the model as "Invoke the `skill` tool with name … and arguments …", so plugin and namespaced skills work in serve/remote mode without a new RPC. |
| Agents | `PluginAgentLoader` accepts Claude-format agents: `type` → else `name` → else file stem. Registered as `<plugin>:<type>` through `SubagentRegistry`, which gains short-name resolution (unique short name accepted; ambiguous → error listing candidates; unknown → general-purpose fallback, as today; built-ins still win). Guards unchanged (forbidden keys logged/ignored; `model` ignored for project plugins). Plugin agent `model` values are **not applied at run time** (Claude aliases such as `sonnet` are not provider model ids) — documented caveat. `${CLAUDE_PLUGIN_ROOT}`/`${CODA_PLUGIN_ROOT}` are expanded in the agent body. `SubagentHost` gets `with_registry(Arc<SubagentRegistry>)` and resolves through it. The `task` tool registered by the host gets a description suffix listing plugin agent types. |
| Trust (should-have 8) | Skills, commands and agents are prompt content: they load like user skills (project plugins like project skills). **Executable components** — each MCP server, each LSP server, and the plugin's hooks block — need approval. Approval key = `sha256(manifest bytes ‖ kind ‖ component name ‖ canonical component JSON ‖ (project plugins only) canonical root)`, stored in `~/.coda/plugin-trust.json` under the existing `pluginApprovals: {hash: [kinds]}` shape with kinds `mcpServer`, `lspServer`, `hook`. Any byte change to the manifest or the component definition yields a new key → unapproved again. There is no workspace-trust store in the Rust engine; binding project-plugin approvals to the canonical project root is the workspace gate. The engine never prompts (serve is unattended): approval is granted by `coda plugin approve <name>` (or the install/update flow), which shows each component and asks y/N on a TTY (`--yes` for scripts). |
| MCP | Approved plugin servers are added to the MCP connect set as `plugin_<plugin>_<server>` (stdio: `command`, `args`, `env`; http: `url`, `headers`), with `${CLAUDE_PLUGIN_ROOT}`/`${CODA_PLUGIN_ROOT}` expanded in command/args/env/url/headers and both variables injected into stdio env. Unapproved → not started, listed as `pendingApproval`. |
| Hooks (should-have 9) | Claude format `{ "hooks": { "<Event>": [ { "matcher"?, "hooks": [ { "type": "command", "command", "timeout"? } ] } ] } }` (outer `hooks` optional). Converted to `UserHook { event, command (vars expanded), matcher, timeout_seconds, plugin_origin: Some((name, version)), scope: User/Project by plugin source }`. Approved → hash pre-trusted in the session hook trust store so `HookTrustGuard` lets them run; unapproved → appended with `enabled: false` (never run, never block a fail-closed gate) but still shown in `hooks/list` with `plugin`, `trusted: false`. Hooks get `CLAUDE_PLUGIN_ROOT`/`CODA_PLUGIN_ROOT` in their environment (`UserHook.env`, `#[serde(skip)]`, executor sets it). |
| SessionStart | `HookRunner::run_session_start(source, session_id, cwd)` (fail-open) returns merged `hookSpecificOutput.additionalContext`. The host fires it once per engine instance at the first prompt (`source: "startup"`) and prepends the context to that prompt's user message. |
| LSP | `PluginLspServerLoader::load_for_plugin(root, manifest)` (new; supports both manifest layouts, expands `CODA_PLUGIN_ROOT` too) — only approved servers are merged into the LSP config. |
| `plugins/list` | `{ plugins: [ { name, version?, description?, source: "user"\|"project"\|"path"\|"marketplace:<id>", root, enabled, trusted, trust: "notRequired"\|"approved"\|"pending"\|"partial", isExternal: false, components: { skills[], agents[], commands[], mcpServers[], hooks[], lspServers[] }, pendingApproval: [ "mcpServer:x", … ], errors[] } ], errors[] }`. Components are qualified names. Top-level `errors` carries settings-shape problems. `trusted` = no executable component is pending. |
| CLI (should-have 10) | `coda plugin list [--json]`, `install <path\|git-url\|name@marketplace> [--yes]`, `enable <name>`, `disable <name>`, `update [<name>] [--yes]`, `remove <name>`, `approve <name> [--yes]`, `marketplace add <id> (--path <dir>\|--git <url>)`, `marketplace list`, `marketplace remove <id>`. Local path install = add to `plugins.paths` (by reference, edits live). Git install = `git clone` into `~/.coda/plugins/<name>/` + receipt `.coda-plugin.json` `{source:"git", url, ref, commit, installedAt, contentHash}`. `update` = `git fetch` + `reset --hard` to the receipt ref (default remote HEAD) for git installs and `git pull --ff-only` for git marketplaces, then re-runs approval for changed executable components. Settings writes go through `coda_boot::settings_store::save_changes` (locked delta writes). |
| Config | `plugins` is already `AppliesWhen::NewEngineInstance`; no hot reload. |

## Data flow

`ServeHost::build` → `plugins::discover_plugins(workspace, &settings)` → one `PluginSet` (descriptors +
resolved components + trust state) kept on the host. From it: skill catalogue → `skill` tool;
agent defs → `SubagentRegistry` → both `SubagentHost`s and the `task` tool description; approved
hooks → `user_hooks`; approved LSP → `load_lsp_configs`; `plugins/list` serialises it. MCP connects
earlier (`transport.rs` → `connect_mcp`), so `connect_mcp` discovers the same set and appends the
approved plugin servers.

## Testing

Unit tests in each new module (discovery layouts, missing dirs, malformed isolation, traversal,
marketplace manifest + scan, enablement rules, trust hashing/invalidation, hook conversion,
MCP conversion, skill resolution/ambiguity, registry short names, loader `name` fallback,
skill tool output with relative paths). A host-level test runs `plugins/list` against fixtures
under `coda-serve/testdata/plugins/` that mirror the two review plugins (4 skills, 3 agents, a
`scripts/*.ps1` helper). End-to-end: real `coda serve` over stdio with an isolated `CODA_HOME`
whose settings reference the fixtures and `ai-review-plugins` by path / directory marketplace.

## Out of scope / follow-ups

Hot reload; hosted registry; sandboxing; `coda plugin import` (11); richer `/plugins` TUI detail
(12); mapping Claude agent `tools:`/`model:` aliases; hook events other than those the runner
already implements plus `SessionStart`; plain-text (non-JSON) SessionStart output.
