# Skills and plugins reference

Skills are single Markdown files that package a reusable prompt with optional argument, tool,
model, and routing metadata. Plugins bundle skills, commands, agents, hooks, themes, and other
components under one manifest so they can be installed, versioned, and trusted as a unit.

This document is the authoring reference for both. It covers the on-disk formats, discovery and
precedence rules, the trust model, the `/skills`, `/plugin`, and `/marketplace` commands, the
serve-protocol surface, and worked examples.

---

## Contents

- [Skills](#skills)
  - [File layout](#file-layout)
  - [Frontmatter reference](#frontmatter-reference)
  - [Argument substitution](#argument-substitution)
  - [Discovery and precedence](#discovery-and-precedence)
  - [Foreign skill sources](#foreign-skill-sources)
- [Plugins](#plugins)
  - [Discovery](#discovery)
  - [Settings](#settings)
  - [Manifest reference](#manifest-reference)
  - [Components](#components)
  - [The model's view](#the-models-view)
  - [Variable interpolation](#variable-interpolation)
- [Marketplaces](#marketplaces)
- [Trust model](#trust-model)
- [Commands](#commands)
  - [`/skills`](#skills-command)
  - [`/plugin`](#plugin-command)
  - [`coda plugin`](#coda-plugin-cli)
  - [`/marketplace`](#marketplace-command)
  - [Interactive browsers](#interactive-browsers)
- [Serve protocol](#serve-protocol)
- [Worked examples](#worked-examples)

---

## Skills

### File layout

A skill is a `SKILL.md` file with a YAML frontmatter block followed by the prompt body:

```markdown
---
name: summarize-diff
description: Summarize a git diff into a changelog entry.
argument-hint: <base-ref> [head-ref]
arguments: [base, head]
---
Read the diff between $base and $head and produce a single changelog line.
```

The skill's directory name and the `name` field should match. The body is the prompt that runs
when the skill is invoked; it is sent verbatim after argument substitution.

### Frontmatter reference

All keys are optional except `name` and `description`. Unrecognised keys are preserved (so a
skill authored for a newer Coda version or another harness still loads) and surfaced under
"unknown fields" in `/skills info`.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `name` | string | — | Kebab-case identifier. **Required.** Must match the directory name. |
| `description` | string | — | One-line summary shown in listings and advertised to the model. **Required.** |
| `when-to-use` | string | none | Extra routing text appended to `description` when the skill is advertised to the model. |
| `argument-hint` | string | none | Completion hint shown in `/skills` listings, e.g. `<filename> [options]`. |
| `arguments` | list | empty | Named positional arguments for `$name` substitution in the body. |
| `disable-model-invocation` | bool | `false` | When `true`, the skill is hidden from the model-facing `skill` tool but still runnable via `/skill <name>`. |
| `user-invocable` | bool | `true` | When `false`, the skill is model-only: absent from `/skills` and rejected by `/skill <name>`, but present in the `skill` tool. |
| `allowed-tools` | list | empty | Tools pre-approved for the invoking turn (skips the permission prompt). Never widens a hook-imposed denial. |
| `disallowed-tools` | list | empty | Tools removed from the pool for the invoking turn. Unioned with hook denial lists. |
| `model` | string | inherit | Model override for the turn. `inherit` is normalised to "use the session default". |
| `effort` | string | inherit | Reasoning-effort override for the turn. `inherit` uses the session default. |
| `context` | string | `inline` | Execution mode: `inline` runs the body in the current turn; `fork` runs it in a forked subagent. |
| `agent` | string | general-purpose | Subagent type used when `context: fork`. |
| `paths` | list | empty | Glob patterns restricting which workspaces the skill is advertised to the model in. User invocation via `/skill` is never filtered. |

> `disable-model-invocation` and `user-invocable` are frontmatter-driven, not runtime toggles.
> `/skills enable` and `/skills disable` edit the `disable-model-invocation` flag in place; there
> is no separate on/off state store for skills.

### Argument substitution

The body supports positional substitution before it is sent:

| Form | Expands to |
| --- | --- |
| `$$` | A literal `$`. |
| `$ARGUMENTS` | All supplied values joined with a single space. |
| `$1`, `$2`, … | The nth positional argument (empty string when absent). |
| `$name` | The argument declared under `arguments` with that name. |

Unrecognised `$identifier` forms expand to an empty string; a bare `$` not followed by
an identifier character is kept literally. Substitution runs only when at least one argument
is supplied or the skill declares named `arguments` in its frontmatter — so literal dollar
signs in skill bodies (e.g. `$100`) are preserved when the skill takes no arguments.

> **Note:** `${N:-default}` brace-and-fallback syntax is not currently supported. Use an
> explicit `$name` declaration with a default in the body, or guard the reference with an
> `$ARGUMENTS` check in a shell snippet.

### Discovery and precedence

Skills are discovered from several layers. When two layers define a skill with the same name, the
higher-precedence layer wins. Ascending precedence (later overrides earlier):

| Precedence | Origin | Location | Writable |
| --- | --- | --- | --- |
| 1 (lowest) | `Foreign` | Foreign-ecosystem paths (see below) | Read-only |
| 2 | `Claude` | `~/.claude/skills/` | Read-only |
| 3 | `User` | `~/.coda/skills/` | Yes |
| 4 | `Plugin` | A plugin's bundled `skills/` directory | Via the plugin |
| 5 (highest) | `Project` | `<project>/.coda/skills/` | Yes |

So a project skill shadows a user skill of the same name, which shadows a foreign skill. `/skills
info <name>` reports the winning origin and source path.

### Foreign skill sources

To interoperate with other harnesses, Coda also reads (read-only, lowest precedence):

- `<project>/.agents/skills/` — project-level foreign skills.
- `~/.claude/agents/` — Claude subagent definitions, read as skills.
- `~/.claude/commands/` — Claude command definitions, read as skills.

Foreign skills load with `SkillOrigin.Foreign` and are always overridable by a Coda-native skill
of the same name. The Claude base directory is overridable with the `CODA_CLAUDE_SKILLS_DIR`
environment variable (its parent is used to locate `agents/` and `commands/`); the user directory
is overridable with `CODA_USER_SKILLS_DIR`.

---

## Plugins

A plugin is a directory in the Claude plugin format: a manifest at `.claude-plugin/plugin.json`
(a root `plugin.json` is accepted too) plus component folders. The same directory works in other
Claude-format CLIs and in Coda, and it is **read in place** — nothing is copied, so a skill can
reference sibling files by relative path and a local checkout can be edited and tested directly.

```
deep-review/
  .claude-plugin/plugin.json
  skills/review/SKILL.md
  skills/extreme-review/SKILL.md      # may read ../review/SKILL.md
  agents/advocate.md  agents/skeptic.md  agents/architect.md
  commands/*.md                       # optional, loaded like skills
  hooks/hooks.json                    # optional, needs approval
  .mcp.json                           # optional, needs approval
```

### Discovery

Plugins are discovered once per engine instance (no hot reload: start a new session to apply
changes), in this order — a later plugin with the same name replaces an earlier one, and the
replacement is reported in `plugins/list` `notes`:

1. **User:** `~/.coda/plugins/<dir>/` (honours `CODA_HOME`). Git installs land here.
2. **Settings paths:** each `plugins.paths` entry is a plugin directory, or a directory whose
   children are plugin directories.
3. **Marketplaces:** each `plugins.marketplaces.<id>` — a local directory (`source: "directory"`)
   or a git repository (`source: "git"`, cloned to `~/.coda/marketplaces/<id>`). The plugins it
   offers are the relative `source` entries of `.claude-plugin/marketplace.json`, or every plugin
   under `<path>/plugins/` when there is no catalogue file. Remote (`github`, `url`) entries in a
   catalogue are reported as errors.
4. **Project:** `<project>/.coda/plugins/<dir>/` — untrusted, like project skills.

One malformed plugin never hides the others: it is listed with its `errors` and contributes
nothing.

### Settings

Only the **user** settings file (`~/.coda/settings.json`) is read, so a project cannot point Coda
at arbitrary directories:

```jsonc
"plugins": {
  "enabled": { "deep-review": true, "owasp-pr-review": false },
  "paths": [ "D:/src/my-marketplace/plugins/owasp-pr-review" ],
  "marketplaces": {
    "local-dev": { "source": "directory", "path": "D:/src/my-marketplace" },
    "team":      { "source": "git", "url": "https://github.com/org/plugins.git" }
  }
}
```

Enablement: an explicit `enabled` entry wins. Otherwise marketplace plugins are **off** until
enabled (a marketplace is a catalogue — `coda plugin install <name>@<id>` enables one) and every
other source is on. The legacy `~/.coda/plugin-state.json` lists written by the TUI's
`/plugin enable|disable` are honoured when `enabled` has no entry. A disabled plugin is listed
but contributes no component.

### Manifest reference

Only `name` is required; unknown keys are ignored (logged at debug level).

| Field | Type | Meaning |
| --- | --- | --- |
| `name` | string | Plugin name: letters, digits, `-`, `_`, `.`. Prefixes its skills and agents. |
| `version` | string | Shown in listings and stamped on hooks. |
| `description` | string | One-line summary. |
| `author` | string or `{ name }` | Author. |
| `skills` | path or paths | Extra skill directories, **in addition to** `./skills/`. |
| `commands` | path or paths | Extra command directories, in addition to `./commands/`. |
| `agents` | path or paths | Extra agent directories, in addition to `./agents/`. |
| `mcpServers` | object or path | Inline `{ name: server }` map, or a JSON file holding `{ "mcpServers": {…} }` or the bare map. Default: `./.mcp.json` if present. |
| `hooks` | object or path | Claude hooks block (`{ "hooks": { "<Event>": [ … ] } }` or the bare event map), or a path to one. Default: `./hooks/hooks.json` if present. |
| `lspServers` | object, path or array | LSP servers (also `./.lsp.json`). |

Every path must stay inside the plugin directory: absolute paths and `..` escapes are rejected
with a per-plugin error, and the rest of the plugin still loads.

### Components

- **Skills and commands** are listed by `skills/list` as `<plugin>:<name>` with `origin: "plugin"`
  and `plugin: "<plugin>"`, so two plugins shipping `review` never shadow each other. A short
  name resolves by precedence — project > plugin > user > claude > foreign — and when two plugin
  skills share the winning short name the call fails and names both.
- **Agents** (`agents/*.md`, Claude format: `name`, `description`, body = system prompt; Coda's
  `type` key is also accepted, the file stem is the fallback) register as `<plugin>:<agent>`
  subagent types for the `task` tool, whose description lists them. A unique short name
  (`skeptic`) is accepted; an ambiguous one is refused. Built-in types always win. `hooks`,
  `mcpServers`, `permissions` and `permissionMode` keys in an agent file are ignored. An agent's
  `model` is never applied (Claude aliases such as `sonnet` are not provider model ids); `tools`
  is not mapped.
- **MCP servers** start as `plugin_<plugin>_<server>` (tools `mcp__plugin_<plugin>_<server>__*`)
  once approved. A user or project server of the same name wins.
- **Hooks** use the Claude format; `command` handlers are converted into Coda hooks that carry
  their plugin as `plugin_origin` and appear in `hooks/list` with `plugin`. `SessionStart` fires
  once per engine instance, at the first prompt (`source: "startup"`, fail-open); each hook's
  `hookSpecificOutput.additionalContext` is prepended to that prompt. Other events go through
  the same pipeline as user hooks.
- **LSP servers** are merged into the LSP configuration as `plugin:<plugin>:<server>` once approved.

### The model's view

The `skill` tool lists every model-invocable skill (`name: description`) and loads one on demand.
It re-reads the file at call time (edits apply on the next call) and returns:

```
Base directory for this skill: C:\src\deep-review\skills\extreme-review
Plugin: deep-review (root: C:\src\deep-review; also available as ${CLAUDE_PLUGIN_ROOT} / ${CODA_PLUGIN_ROOT})
Subagent types from this plugin (use with the `task` tool's subagentType): deep-review:advocate, …

<skill body>
```

Plugin roots (and the directories of user-level skills) are added to the file tools' granted
directories, so `read_file ../review/SKILL.md` or reading `scripts/helper.ps1` works without
leaving default permission mode. Running a script still goes through the normal `run_command`
permission prompt. The TUI's `/skill <name>` runs local project/user skills directly and hands any
other name (for example `deep-review:review`) to the model's `skill` tool.

### Variable interpolation

`${CLAUDE_PLUGIN_ROOT}` and `${CODA_PLUGIN_ROOT}` expand to the plugin directory in skill and
agent bodies and in every string of MCP server, hook and LSP definitions; both are also exported
to the processes those start. `${CLAUDE_SKILL_DIR}` expands to the skill's directory in a skill body.

---

## Marketplaces

A marketplace is a directory (or git repository) that offers plugins. Register one with
`coda plugin marketplace add <id> --path <dir>` or `--git <url>`, then enable a plugin from it with
`coda plugin install <name>@<id>`. `coda plugin update` pulls git marketplaces. The TUI's
`/marketplace` command manages the older `marketplaces` settings key and is unrelated to plugin
loading.

---

## Trust model

Coda never silently runs third-party code:

- **Prompt content** — skills, commands and agents — loads like user skills (project plugins like
  project skills). It cannot execute anything by itself; tools it asks for go through the normal
  permission checks.
- **Executable components** — each MCP server, each LSP server and a plugin's hooks block — need
  **approval**. An approval is keyed by SHA-256 of the plugin manifest bytes plus the component's
  kind, name and definition (and, for project plugins, the plugin directory, binding it to one
  checkout). Any change to the manifest or the definition yields a new key, so it must be
  approved again. Approvals live in `~/.coda/plugin-trust.json` under
  `pluginApprovals: { "<hash>": ["mcpServer" | "lspServer" | "hook"] }`.
- The engine never prompts: unapproved components are not started (`plugins/list` reports them in
  `pendingApproval`; their hooks are listed by `hooks/list` disabled and untrusted). Approve with
  `coda plugin approve <name>` (asks y/N on a terminal; `--yes` for scripts); `install` and
  `update` run the same step.
- Hook trust for ordinary project hooks (`hooks/trust`) and skill trust (`skills/trust`) are
  unchanged.

---

## Commands

### `/skills` command {#skills-command}

```
/skills [list | info <name> | enable <name> | disable <name> | reload | validate <path> | new <name>]
```

| Subcommand | Effect |
| --- | --- |
| _(none)_ / `list` | List discovered skills with name, description, and argument hint. |
| `info <name>` | Show origin, source path, argument hint, and flags for a skill. |
| `enable <name>` | Clear `disable-model-invocation` in the skill's frontmatter. |
| `disable <name>` | Set `disable-model-invocation` in the skill's frontmatter. |
| `reload` | Re-scan skill directories and re-register skill-derived slash commands. |
| `validate <path>` | Parse and validate the `SKILL.md` at `<path>` (or `<path>/SKILL.md`). |
| `new <name>` | Scaffold `<cwd>/.coda/skills/<name>/SKILL.md`. |

### `/plugin` command {#plugin-command}

```
/plugin [list | info <name> | enable <name> | disable <name>]
```

`list` shows each plugin's version, source, state, component counts and pending approvals (from
`plugins/list`); `enable`/`disable` write `~/.coda/plugin-state.json` and apply to the next
engine instance. Everything else is the `coda plugin` command line:

### `coda plugin` command line {#coda-plugin-cli}

| Command | Effect |
| --- | --- |
| `coda plugin list [--json]` | Discovered plugins with source, root, state, trust, components and errors (`--json` prints the `plugins/list` result). |
| `coda plugin install <path>` | Adds the directory to `plugins.paths` (loaded in place). |
| `coda plugin install <git-url>[#ref]` | Clones into `~/.coda/plugins/<name>/` and writes `.coda-plugin.json` (`source`, `url`, `ref`, `commit`, `installedAt`, `contentHash`). |
| `coda plugin install <name>@<marketplace>` | Enables a plugin a registered marketplace offers. |
| `coda plugin enable|disable <name>` | Sets `plugins.enabled.<name>`. |
| `coda plugin update [<name>] [--yes]` | Fetches git installs to their ref's latest commit (updating the receipt) and pulls git marketplaces, then asks again for any executable component whose approval no longer matches. |
| `coda plugin remove <name>` | Deletes a `~/.coda/plugins/` install, drops a `plugins.paths` reference, or disables a marketplace plugin. Project plugins are removed by deleting their directory. |
| `coda plugin approve <name> [--yes]` | Shows and approves pending executable components. |
| `coda plugin marketplace add <id> --path <dir> \| --git <url>` | Registers a marketplace (a git one is cloned). |
| `coda plugin marketplace list \| remove <id> \| update [<id>]` | Lists, unregisters (deleting a clone) or pulls marketplaces. |

All commands edit `~/.coda/settings.json` with locked delta writes and take effect in the next
session.

### `/marketplace` command {#marketplace-command}

See [Marketplaces](#marketplaces).

### Interactive browsers

Typing `/skills` or `/plugin` with **no arguments** opens an interactive overlay instead of listing
to the transcript. Any subcommand (e.g. `/skills list`, `/plugin info foo`) goes through the normal
command path.

**Skills browser** (`/skills`):

| Key | Action |
| --- | --- |
| ↑ / ↓ (or `k` / `j`) | Move selection. |
| PgUp / PgDn, Home / End | Jump. |
| Enter | Open the detail view (frontmatter, source path, argument hint). |
| `r` | Reload skills. |
| Esc | Return to the list from detail, then close. |

**Plugins browser** (`/plugin`):

| Key | Action |
| --- | --- |
| ↑ / ↓ | Move selection. |
| Enter | Open the detail view (version, enabled, trust, external state). |
| Space | Toggle the selected plugin enabled/disabled. |
| `u` | Update the selected plugin. |
| Esc | Return to the list from detail, then close. |

---

## Serve protocol

| Method | Result |
| --- | --- |
| `skills/list` | `{ skills: [{ name, description, origin, enabled, userInvocable, sourcePath?, argumentHint?, plugin? }] }` |
| `plugins/list` | `{ plugins: [ { name, version?, description?, source, root, enabled, trusted, trust, isExternal, components: { skills, commands, agents, mcpServers, hooks, lspServers }, pendingApproval, notes, errors } ], errors }` |
| `hooks/list` | Plugin hooks carry `plugin`; `trusted` is `false` until approved. |
| `skills/trust` | Refused in an unattended serve session (no interactive callback). |

`source` is `user`, `project`, `path` or `marketplace:<id>`; `trust` is `notRequired`, `approved`,
`partial` or `pending`; `trusted` is `true` when nothing is pending. Components are qualified
names (`deep-review:review`), hook event names, and server names. See
[`docs/serve-protocol.md`](serve-protocol.md) for the wire-level details.

---

## Worked examples

### A user-invocable skill with an argument

`~/.coda/skills/changelog/SKILL.md`:

```markdown
---
name: changelog
description: Turn a diff range into a changelog line.
argument-hint: <base-ref> [head-ref]
arguments: [base, head]
allowed-tools: [shell]
---
Run `git log --oneline $base..$head` and write one changelog bullet summarising the range.
```

Invoke it directly (`user-invocable` defaults to true):

```
/changelog v1.2.0 HEAD
```

### A model-only routing skill

```markdown
---
name: security-triage
description: Assess a code change for security impact.
when-to-use: When the user asks whether a change is safe to merge.
user-invocable: false
model: gpt-5
effort: high
---
Review the pending change and report exploitable risks with severity and confidence.
```

This skill never appears in `/skills` and cannot be run with `/skill`, but the model may route to it
via the `skill` tool.

### Loading two review plugins from a local checkout

```jsonc
// ~/.coda/settings.json
"plugins": {
  "paths": [ "D:/src/reviews/plugins/deep-review", "D:/src/reviews/plugins/owasp-pr-review" ]
}
```

A new session lists both in `plugins/list`; `deep-review:review` can start the
`deep-review:advocate`, `deep-review:skeptic` and `deep-review:architect` agents in parallel, and
the OWASP skill reads `../teams-owasp-pr-review-monitor/reviewer-contract.md` from the plugin
directory. Editing a file under `D:/src/reviews` takes effect in the next session (skill bodies
are even re-read on each `skill` call); `coda plugin disable deep-review` removes all of its
components from the next session.
