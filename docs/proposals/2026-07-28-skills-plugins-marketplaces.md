# Proposal: Skills, plugins and marketplaces

- **Date:** 2026-07-28
- **Status:** Backlog — not started. No code written.
- **Author:** Yury Opolev (design exploration, Coda)
- **Scope:** `Coda.Tui` (`Skills/`, `Plugins/`, the five existing commands), `Coda.Agent` (a skill
  tool, per-skill tool scoping, subagent forking), `Coda.Sdk` (session wiring).
- **Companion:** [`2026-07-28-agent-hooks-system.md`](2026-07-28-agent-hooks-system.md). The two meet
  at plugin-shipped hooks and skill-scoped hooks; neither is blocked on the other.

## 1. Summary

Coda already discovers skills, loads plugins, and installs from marketplaces. The foundation is real
and does not need replacing. What it lacks is the thing that makes these features worth having in the
other harnesses:

**The model cannot invoke a skill.** A Coda skill fires only when a human types `/skill <name>`. In
Claude Code, Copilot CLI and Gemini CLI the agent chooses skills itself, from descriptions kept in
context, loading the body only when one fires. That single difference is what separates "a macro the
user remembers to run" from "a capability the agent has".

The second gap is what a plugin may contain. A Coda plugin ships skills and LSP servers. Coda already
*has* subagents, MCP, output styles, themes and (per the companion proposal) hooks — plugins simply
cannot carry them. Most of the work here is exposure, not new subsystems.

## 2. Current state (verified on `fcbd8c9`)

| Concern | Today | File |
|---|---|---|
| Skill file | `SKILL.md` per directory, optional `---` frontmatter | `src/Coda.Tui/Skills/SkillLoader.cs:9,122-172` |
| Skill frontmatter | **`name` and `description` only**; everything else ignored | `SkillLoader.cs:146-157` |
| Skill model | `SkillDefinition(Name, Description, Body)` — three strings | `src/Coda.Tui/Skills/SkillDefinition.cs:4` |
| **Skill invocation** | **User-only**, `/skill <name>`. No skill tool exists in `Coda.Agent` | `src/Coda.Tui/Commands/SkillCommand.cs:15` |
| Progressive disclosure | None — descriptions never reach the model, so it cannot know skills exist | — |
| Skill precedence | `~/.claude/skills` (read-only) < `~/.coda/skills` < plugin skills < `.coda/skills` | `SkillLoader.cs:46-77` |
| Skill extras | None — no bundled scripts, arguments, tool scoping, or model override | — |
| Plugin manifest | `plugin.json` parsed for `name`, `version`, `description` — **three fields** | `src/Coda.Tui/Plugins/PluginLoader.cs:99-118` |
| Plugin model | `PluginInfo(Name, Version, Description, Directory)` | `src/Coda.Tui/Plugins/PluginInfo.cs:4` |
| Plugin contents | `skills/` and LSP servers | `PluginLoader.cs:45`, `src/Coda.Agent/Lsp/PluginLspServerLoader.cs` |
| Plugin discovery | `~/.coda/plugins/*/` and `<cwd>/.coda/plugins/*/`; project wins by name | `PluginLoader.cs:16-26` |
| Plugin lifecycle | install (local dir or git URL), remove. **No enable/disable, no update** | `src/Coda.Tui/Commands/PluginCommand.cs:31-40` |
| Marketplace | `list`, `add`, `remove`, `browse`, `install`; entries carry name/source/description/version/category/tags | `src/Coda.Tui/Commands/MarketplaceCommand.cs`, `Plugins/MarketplacePluginEntry.cs:1-8` |
| Marketplace state | `KnownMarketplaceEntry(Source, InstallLocation, LastUpdated)` | `Plugins/KnownMarketplaceEntry.cs:4` |
| Marketplace integrity | No commit pinning, no reserved names, no provenance | — |
| Trust | Path-traversal defence on plugin names and git URLs only; no trust prompt | `Plugins/PluginInstaller.cs:35,70` |
| Custom slash commands | **None** — every command is a compiled `ISlashCommand` | `src/Coda.Tui/Repl/SlashCommandRegistry.cs` |

## 3. Prior art (researched 2026-07-28)

### Skill invocation

| | Model invokes? | Mechanism | Preloaded into context | On fire |
|---|---|---|---|---|
| **Claude Code** | ✅ | `Skill` tool | `name` + `description` + `when_to_use`, capped at **1 536 chars** combined | Rendered body enters the conversation and **stays for the session**; re-invocation adds only an "already loaded" note. After compaction the latest invocation of each skill is re-attached (5 000 tokens each, 25 000 combined) |
| **Copilot CLI** | ✅ | `skill` tool | Payload unverified | `SKILL.md` injected; all files in the skill directory become available |
| **Gemini CLI** | ✅ | `activate_skill`, whose `name` argument is an **enum built from the discovered skill names** | `name` + `description` of every enabled skill, injected **into the system prompt** at session start | **User consent prompt naming the skill and the directory it will gain access to**; on approval body + folder structure enter history and the directory joins the allowed file paths |

All three also allow user invocation as `/name`. Gemini's `/name` is a thin wrapper that calls
`activate_skill` and turns any arguments into a follow-up prompt rather than a substitution
(`packages/cli/src/services/SkillCommandLoader.ts:24-55`).

### Skill frontmatter — field counts: Claude **19**, Copilot **4**, Gemini **2**, Coda **2**

Claude's set is the interesting one:

| Field | Meaning |
|---|---|
| `allowed-tools` | Pre-approve tools for the invoking turn only |
| `disallowed-tools` | **Removes** tools from the pool while the skill is active |
| `model`, `effort` | Override for the rest of the turn, or `inherit` |
| `disable-model-invocation` | User-only; also drops the description from context |
| `user-invocable: false` | Model-only; hidden from the `/` menu |
| `context: fork` + `agent` + `background` | Run the skill in a subagent |
| `hooks` | **Hooks scoped to the skill's lifecycle** |
| `paths` | Globs limiting *automatic* activation |
| `when_to_use` | Extra trigger phrases, appended to the description |
| `argument-hint`, `arguments` | Autocomplete hint and named positional args for `$name` |
| `shell` | `bash` \| `powershell` for `` !`cmd` `` interpolation |

### What a plugin may contain

| Component | Claude Code | Copilot CLI | Gemini CLI | **Coda today** |
|---|---|---|---|---|
| Skills | ✅ | ✅ | ✅ | ✅ |
| Slash commands | ✅ (merged into skills) | ✅ | ✅ (separate `.toml`) | ❌ |
| Subagents | ✅ | ✅ | ✅ preview | ❌ |
| Hooks | ✅ | ✅ | ✅ | ❌ |
| MCP servers | ✅ | ✅ | ✅ | ❌ |
| LSP servers | ✅ | ✅ | ❌ | ✅ |
| Output styles | ✅ | ❌ | ❌ | ❌ |
| Themes | ✅ experimental | ❌ | ✅ | ❌ |
| Typed user config | ✅ 7 types, keychain | ❌ | ✅ `settings[]` | ❌ |
| `bin/` on PATH | ✅ | ❌ | ❌ | ❌ |
| Plugin dependencies | ✅ semver + `prune` | ❌ | ❌ | ❌ |
| Global tool exclusion | ❌ | ❌ | ✅ `excludeTools` | ❌ |
| Policy rules | ❌ | ❌ | ✅ `policies/*.toml` | ❌ |

### Notable

- **Claude Code merged custom commands into skills** — one concept, one namespace, one `/name`.
  (Exact version unverified; the docs state it plainly and version markers place it in v2.1.x.)
- **Claude ignores unrecognized top-level manifest fields by design**, so one `plugin.json` can also
  serve as a VS Code, npm or MCPB manifest, with `validate --strict` for authors who want warnings.
- **Copilot deliberately reads Claude's paths**: `.claude/skills/`, `.agents/skills/`,
  `.claude/agents/`, `.claude/commands/`, `.claude-plugin/plugin.json`,
  `.claude-plugin/marketplace.json`, and aliases `${CLAUDE_PLUGIN_DATA}`. `.agents/` is the emerging
  vendor-neutral prefix, which Gemini also reads.
- **No harness has cryptographic signing.** The closest are Claude's SHA-pinned community catalog and
  Copilot's `sha` field (full 40-char commit, *"immune to force-pushes or tag/branch moves"*).
- **Claude forbids plugin-shipped agents from declaring `hooks`, `mcpServers` or `permissionMode`**,
  explicitly for security. Plugin-level hooks are allowed; agent-scoped ones are not.
- **Gemini has no marketplace at all** — distribution is a GitHub URL or local path.
- Housekeeping worth stealing: Claude's **14-day orphan grace period** for cached plugin versions so
  concurrent sessions survive an update; **reserved marketplace names re-checked on every load** with
  lookalike blocking; a `renames` map (Gemini: `migratedTo`) so a moved repo doesn't strand users;
  and Copilot refusing `marketplace remove` while dependent plugins remain installed.

## 4. Skills

### 4.1 Model invocation

A `skill` tool is added to the agent's toolset, present only when at least one skill is discovered.
Its `name` parameter is an **enum of the discovered skill names** — Gemini's approach, and the
cheapest correct one: the model cannot hallucinate a skill that does not exist, and the tool
description stays constant regardless of how many skills are installed.

Context economics follow Claude:

- **Preloaded:** `name` + `description` (+ `when_to_use` when present) per skill, with a combined
  cap. Beyond the cap, skills are still invocable by name but stop being advertised.
- **On fire:** the body enters the conversation once and stays for the session; re-invocation
  produces an "already loaded" note rather than a second copy.
- **After compaction:** the most recent invocation of each skill is re-attached within a budget.
  Without this, `PostCompact` in the companion proposal is the only thing standing between a
  compacted session and a silently de-skilled agent.

### 4.2 Frontmatter

Proposed set, ordered by value:

| Field | Behaviour |
|---|---|
| `name`, `description` | As today |
| `when_to_use` | Appended to the description for routing only |
| `disable-model-invocation` | User-only; the description is dropped from context entirely |
| `user-invocable` | `false` ⇒ model-only, hidden from the `/` menu |
| `allowed-tools` | Pre-approved for the invoking turn |
| `disallowed-tools` | Removed from the pool while the skill is active |
| `model`, `effort` | Override for the rest of the turn, or `inherit` |
| `argument-hint`, `arguments` | Completion hint; named positional args for `$name` / `$ARGUMENTS` |
| `context: fork` + `agent` | Run the skill in a subagent instead of inline |
| `hooks` | Hooks scoped to the skill's lifecycle (companion proposal) |
| `paths` | Globs limiting automatic activation to matching workspaces |

Parsing must stay **forward-compatible**: unknown keys are ignored, never fatal, so a skill authored
for a newer Coda or for another harness still loads. The current hand-rolled `TryParseYamlValue`
(`SkillLoader.cs:174`) handles only `key: value` and will not survive lists — `allowed-tools` alone
forces a real YAML subset parser.

### 4.3 Bundled resources

The skill directory becomes visible to the agent when the skill fires, as in Copilot and Gemini.
Gemini's consent prompt — which names *the directory the agent will gain access to* — is the right
model, because a skill granting filesystem reach is a permission decision, not a preference.

## 5. Plugins

### 5.1 Manifest

`plugin.json` grows from three fields to a component map. Every path is relative and defaults to a
conventional directory, so a plugin that follows convention needs no manifest entries at all:

| Field | Meaning |
|---|---|
| `name` | Required, kebab-case |
| `version`, `description`, `author`, `homepage`, `repository`, `license`, `keywords` | Metadata |
| `displayName` | Human-readable label |
| `defaultEnabled` | Install-but-off when `false` |
| `skills` | **Adds to** the default `skills/` scan |
| `commands`, `agents`, `outputStyles`, `themes` | **Replace** their default directories |
| `hooks`, `mcpServers`, `lspServers` | Path or inline configuration |
| `userConfig` | Typed install-time prompts, secrets to the existing credential store |
| `dependencies` | Other plugins, optional semver |

`${CODA_PLUGIN_ROOT}`, `${CODA_PLUGIN_DATA}` and `${CODA_PROJECT_DIR}` interpolate in every path and
command. Unrecognized top-level fields are ignored by design (Claude's rule), so one manifest can
serve several ecosystems.

### 5.2 What a Coda plugin should carry

Ordered by value-per-unit-work, given that Coda already owns every subsystem involved:

1. **Hooks** — the distribution mechanism for the companion proposal. A team ships its PII gate as a plugin.
2. **Subagents** — Coda has `SubagentHost` and depth-bounded nesting; a plugin defining a reviewer agent is pure exposure.
3. **MCP servers** — Coda already merges user and project `.mcp.json`; a plugin becomes a third source.
4. **Slash commands** — see §7.
5. **Output styles** and **themes** — both already exist as first-class concepts.

Following Claude's precedent, a **plugin-shipped subagent may not declare its own hooks, MCP servers
or permission mode**. Plugin-level hooks are visible in one place and can be reviewed; agent-scoped
ones hide execution inside a definition that reads like configuration.

### 5.3 Lifecycle

`enable` / `disable` / `update` / version pinning, none of which exist today. Claude's **14-day
orphan grace period** for superseded plugin versions is worth copying directly: without it, updating
a plugin breaks every session that is already running against the old copy.

## 6. Marketplaces

Coda's marketplace works. What it lacks is integrity and hygiene:

| Gap | Prior art |
|---|---|
| No commit pinning | Copilot's `sha`, full 40 chars, *"immune to force-pushes or tag/branch moves"* |
| No reserved names | Claude reserves a list **and re-checks on every load**, blocking lookalikes such as `official-claude-plugins` |
| No relocation path | Claude's `renames` map; Gemini's `migratedTo` |
| No dependency check on removal | Copilot refuses `marketplace remove` while dependent plugins remain, unless `--force` |
| No refresh semantics | `lastUpdated` is stored but nothing consumes it |
| No search | `browse` lists; there is no query across marketplaces |

Signing is deliberately **not** proposed. No harness has it, and a SHA-pinned entry plus a visible
trust prompt closes most of the same gap at a fraction of the cost.

## 7. Command surface

Coda has `/skill`, `/skills`, `/plugin`, `/plugins`, `/marketplace`. The pattern to match is the
existing `/mcp` command, which already does list / info / add / edit / remove / enable / disable /
start / stop / restart.

| Command | Subcommands |
|---|---|
| `/skills` | `list`, `info <name>`, `enable <name>`, `disable <name>`, `reload`, `new <name>`, `validate <path>` |
| `/skill <name> [args]` | Unchanged — run a skill directly |
| `/plugin` | `list`, `info <name>`, `install <source>`, `remove <name>`, `enable`, `disable`, `update [<name>\|--all]`, `validate <path>`, `new <name>` |
| `/marketplace` | `list`, `add <source>`, `remove <name> [--force]`, `browse <name>`, `search <query>`, `refresh [<name>]`, `install <plugin> <marketplace>` |

`/skills` and `/plugin` should also gain interactive overlays, as `/mcp`, `/tasks` and `/schedule`
already have — a list with enable/disable toggles is far better keyboard UX than typed subcommands.

**Custom slash commands.** Coda has none: every command is a compiled `ISlashCommand`. Claude solved
this by merging commands into skills — one concept, one namespace, one `/name`. Coda is unusually
well placed to do the same, because `/skill <name>` already exists; the change is to register each
user-invocable skill as a first-class `/name` in `SlashCommandRegistry` rather than hiding it behind
`/skill`. That yields custom slash commands with no second subsystem.

## 8. Cross-ecosystem compatibility

Coda already reads `~/.claude/skills` read-only, which is the right instinct. Extending it costs a
path list and buys the entire existing ecosystem:

- `.claude-plugin/plugin.json` and `.claude-plugin/marketplace.json` as recognized manifest locations
- `.agents/skills/` — the emerging vendor-neutral prefix, read by both Copilot and Gemini
- `.claude/agents/`, `.claude/commands/`
- `${CLAUDE_PLUGIN_ROOT}` / `${CLAUDE_PLUGIN_DATA}` as aliases

Precedence stays as it is today — foreign locations lowest, Coda's own highest — so a Coda-native
definition always wins.

## 9. Trust and security

Plugins execute third-party code, and once they can ship hooks they execute it **on every turn**.
Today the only defence is path-traversal validation on names and git URLs.

| Control | Behaviour |
|---|---|
| Install-time trust | Show the inventory — *"this plugin provides 2 skills, 1 hook, 1 MCP server"* — and require explicit approval before anything runs |
| Project-scope trust | A plugin in `<cwd>/.coda/plugins` is untrusted until the workspace is trusted; cloning a repo must not grant execution |
| Component gating | Hooks and MCP servers are approved separately from skills. A skill is a prompt; a hook is a subprocess |
| Skill directory access | Consent names the directory the agent gains access to (Gemini's model) |
| Agent-scoped hooks | Forbidden — plugin-level only (Claude's rule, §5.2) |
| Marketplace pinning | Entries resolve to a full commit SHA |
| Secrets | `userConfig` values marked sensitive go to the existing credential store, never the manifest |

## 10. Backlog

### Phase 0 — Skill format

- [ ] Real YAML-subset frontmatter parser: lists, quoting, unknown keys ignored not fatal
- [ ] Widen `SkillDefinition` beyond three strings; carry source path and origin
- [ ] `when_to_use`, `argument-hint`, `arguments` with `$ARGUMENTS` / `$name` substitution
- [ ] `/skills validate <path>` and `/skills new <name>`

### Phase 1 — Model-invocable skills *(the headline gap)*

- [ ] `skill` tool with a name enum built from discovered skills
- [ ] Description preloading with a combined cap; drop advertising beyond it
- [ ] Body loaded once per session; "already loaded" on re-invocation
- [ ] Re-attach most-recent invocations after compaction, within budget
- [ ] `disable-model-invocation` and `user-invocable` opt-outs
- [ ] Skill invocations visible in the transcript as tool activity

### Phase 2 — Skill capabilities

- [ ] `allowed-tools` / `disallowed-tools` scoped to the invoking turn
- [ ] `model` / `effort` override
- [ ] `context: fork` + `agent` — run a skill in a subagent
- [ ] Bundled-resource access with a consent prompt naming the directory
- [ ] `paths` globs limiting automatic activation

### Phase 3 — Plugin manifest and lifecycle

- [ ] Component-map manifest (§5.1); unknown top-level fields ignored
- [ ] `${CODA_PLUGIN_ROOT}` / `${CODA_PLUGIN_DATA}` / `${CODA_PROJECT_DIR}` interpolation
- [ ] `enable` / `disable` / `update`, version pinning, 14-day orphan grace period
- [ ] `userConfig` typed prompts with secrets in the credential store
- [ ] `dependencies` with semver and `prune`

### Phase 4 — Plugin-supplied components

- [ ] Subagents from `agents/` (may **not** declare hooks, MCP servers or permission mode)
- [ ] MCP servers as a third source alongside user and project `.mcp.json`
- [ ] Hooks (requires companion proposal Phase 0)
- [ ] Output styles and themes

### Phase 5 — Custom slash commands

- [ ] Register user-invocable skills as first-class `/name` entries in `SlashCommandRegistry`
- [ ] Name-collision policy against built-in commands
- [ ] Completion metadata from `argument-hint`

### Phase 6 — Marketplace integrity

- [ ] Full-SHA pinning for entries
- [ ] Reserved names, re-checked on load, with lookalike blocking
- [ ] `renames` / `migratedTo` relocation
- [ ] `refresh` consuming the stored `lastUpdated`
- [ ] `search` across configured marketplaces
- [ ] Refuse `remove` while dependent plugins are installed, unless `--force`

### Phase 7 — Trust

- [ ] Install-time inventory prompt with per-component approval
- [ ] Workspace trust gate for project-scoped plugins
- [ ] `/plugin info` showing provided components, origin, pinned SHA, trust state

### Phase 8 — Surfacing and compatibility

- [ ] Interactive `/skills` and `/plugin` overlays, matching `/mcp` and `/tasks`
- [ ] Read `.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json`, `.agents/skills/`, `.claude/agents/`, `.claude/commands/`
- [ ] `${CLAUDE_PLUGIN_ROOT}` / `${CLAUDE_PLUGIN_DATA}` aliases
- [ ] `serve` parity for skill invocation and plugin state
- [ ] Documentation: authoring guide for skills and plugins

## 11. Open questions

1. **Do skills and slash commands merge?** Claude merged them; Gemini keeps them separate. Merging
   gives Coda custom slash commands for free (§7) but means every skill occupies the `/` namespace,
   where Coda already has 39 built-ins. A `user-invocable: false` default for model-oriented skills
   may be the reconciliation.
2. **Preload budget.** Claude caps combined skill metadata at 1 536 characters; Gemini injects every
   enabled skill into the system prompt with no documented cap. Coda's budget should probably scale
   with the context window rather than being a constant.
3. **Where do plugin skills sit in precedence** once plugins can be enabled and disabled? Today
   plugin skills outrank user skills (`SkillLoader.cs:61-69`), which means installing a plugin
   silently overrides a skill the user wrote. That ordering deserves revisiting.
4. **Does a forked skill (`context: fork`) count against subagent depth?** Coda bounds nesting at
   depth 2. A skill that forks from inside a subagent would need either a depth exemption or a
   documented failure.
5. **Marketplace of one.** Should Coda ship a first-party marketplace, and if so, is it a curated
   catalog or an index of community repos? Claude runs three tiers; Copilot ships undeletable
   defaults; Gemini has none.
