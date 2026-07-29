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
  - [Manifest reference](#manifest-reference)
  - [Component directories](#component-directories)
  - [User configuration](#user-configuration)
  - [Variable interpolation](#variable-interpolation)
  - [Foreign plugin sources](#foreign-plugin-sources)
- [Marketplaces](#marketplaces)
- [Trust model](#trust-model)
- [Commands](#commands)
  - [`/skills`](#skills-command)
  - [`/plugin`](#plugin-command)
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

A plugin is a directory containing a `plugin.json` manifest plus the component files it references.
Coda-native plugins live under `.coda/plugins/<name>/` (project) or `~/.coda/plugins/<name>/`
(user).

### Manifest reference

`plugin.json` is a component-map manifest. **Unrecognised top-level fields are ignored by design**,
so one `plugin.json` can double as a `package.json` or another ecosystem's descriptor.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `name` | string | — | Unique kebab-case identifier. **Required.** |
| `version` | string | `0.0.0` | Semantic version. |
| `description` | string | empty | One-line summary. |
| `displayName` | string | none | Human-friendly name for listings. |
| `author` | string | none | Author name or handle. |
| `homepage` | string | none | Project or documentation URL. |
| `repository` | string | none | Source repository URL. |
| `license` | string | none | SPDX license identifier. |
| `keywords` | list | empty | Search keywords for marketplace listings. |
| `defaultEnabled` | bool | `true` | When `false`, the plugin installs disabled until the user enables it. |

### Component directories

Component paths are relative to the plugin directory. The `skills` array is **additive** — its
entries are scanned in addition to the default `skills/` subdirectory. The other component fields
are **exclusive** — a declared path *replaces* the default directory for that component.

| Field | Type | Behaviour | Default directory |
| --- | --- | --- | --- |
| `skills` | list | Additive extra skill directories. | `skills/` |
| `commands` | string | Replaces the commands directory. | `commands/` |
| `agents` | string | Replaces the sub-agent directory. | `agents/` |
| `outputStyles` | string | Replaces the output-style directory. | `output-styles/` |
| `themes` | string | Replaces the theme directory. | `themes/` |
| `hooks` | list | Inline or path-based hook configurations. | — |
| `mcpServers` | list | MCP server configurations. | — |
| `lspServers` | list | LSP server configurations. | — |

#### Plugin commands

Each `.md` file in the commands directory is parsed identically to a user skill file (same
YAML-subset frontmatter, same argument-substitution semantics). The file stem is the fallback
command name when no `name` field is present in the frontmatter.

**Trust requirement.** Commands expand into a prompt the model acts on, so they are executable
content. A plugin must have the `SlashCommand` component class approved (or have no approval
record at all, which grants all-approved for backward compatibility) for its commands to register.
A project-scoped plugin in an untrusted workspace contributes no commands.

**Collision rule.** A plugin command whose name matches any built-in or skill-derived command is
silently skipped with a diagnostic warning visible in the TUI. The built-in name always wins.
Plugin commands from different plugins that share the same name are resolved first-wins (earlier
in the enabled-plugin list wins). No namespace prefix is added — the author is responsible for
choosing a name that does not conflict.

**`/plugin validate`** counts the `*.md` files in the declared commands directory and reports
the total in the component inventory.

### User configuration

`userConfig` declares install-time prompts. Each entry is an object:

| Field | Type | Meaning |
| --- | --- | --- |
| `key` | string | Settings key the answer is stored under. |
| `type` | enum | `string`, `boolean`, `number`, `choice`, or `secret`. |
| `label` | string | Prompt text. |
| `required` | bool | Whether an answer is mandatory. |
| `default` | string | Default value when the user accepts. |
| `options` | list | Allowed values for `choice`. |

`secret` values are stored in the OS credential store and never written to `settings.json` or any
other plaintext file.

`dependencies` declares required plugins, each with a `pluginName` and an optional semver range.

### Variable interpolation

Manifest string values are interpolated at load time:

| Placeholder | Expands to |
| --- | --- |
| `${CODA_PLUGIN_ROOT}` | Absolute path of the plugin directory. |
| `${CODA_PLUGIN_DATA}` | Per-plugin writable data directory. |
| `${CLAUDE_PLUGIN_ROOT}` | Alias for `${CODA_PLUGIN_ROOT}` — accepted for Claude Code compatibility. |
| `${CLAUDE_PLUGIN_DATA}` | Alias for `${CODA_PLUGIN_DATA}`. |

The `CLAUDE_*` aliases let a manifest written for Claude Code interpolate correctly under Coda
without edits.

### Foreign plugin sources

Coda discovers a single foreign plugin per directory from a `.claude-plugin/plugin.json` manifest.
The plugin directory *is* the `.claude-plugin/` directory. Foreign plugins load at lower
precedence than Coda-native plugins (a native plugin of the same name wins) and are marked
`isExternal`. The project location scanned is `<project>/.claude-plugin/plugin.json`.

---

## Marketplaces

A marketplace is a source (git URL or local path) that lists installable plugins. Manage them with
`/marketplace`:

| Subcommand | Effect |
| --- | --- |
| `add <source>` | Register a marketplace by git URL or local path. |
| `list` | List registered marketplaces. |
| `remove <name> [--force]` | Unregister a marketplace. |
| `browse <name>` | List the plugins a marketplace offers. |
| `search <query>` | Search across registered marketplaces. |
| `install <plugin> <marketplace>` | Install a plugin from a named marketplace. |
| `refresh [<name>]` | Re-fetch marketplace manifests. |

---

## Trust model

Coda never silently runs third-party code. Two independent gates apply:

**Plugins** — installing or updating a plugin that contributes executable components (hooks, MCP
servers, commands) triggers a per-class approval prompt. Approvals are keyed by a content hash of
the plugin's name and version and persisted, so a re-install of the same version is not re-prompted,
but an updated version is. `/plugin approve <name>` re-runs the prompt for a plugin installed with
withheld components.

**Skills** — a foreign or otherwise untrusted skill is gated before the model may invoke it. In an
interactive session the user is prompted. In an unattended session (for example `coda serve`) there
is no interactive callback, so the gate **refuses** rather than silently granting: the skill still
appears in listings, but model invocation is blocked. Trust is answered over the protocol only when
the client provides a callback — never assumed.

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
/plugin [list | info <name> | install <source> | remove <name> | enable <name> |
         disable <name> | update <name> | prune | approve <name> | validate <path> | new <name>]
```

| Subcommand | Effect |
| --- | --- |
| _(none)_ / `list` | List installed plugins. |
| `info <name>` | Show components, config, and trust state for a plugin. |
| `install <source>` | Install from a local directory path or a git URL. |
| `remove <name>` | Uninstall a plugin. |
| `enable <name>` | Enable a plugin. |
| `disable <name>` | Disable a plugin without removing it. |
| `update <name>` | Update a git-installed plugin to the latest version. |
| `prune` | List dependency-only plugins no longer required by anything. |
| `approve <name>` | Re-run the per-class approval prompt for a plugin. |
| `validate <path>` | Parse and validate the `plugin.json` at `<path>` (or `<path>/.claude-plugin/plugin.json`). |
| `new <name>` | Scaffold `<cwd>/.coda/plugins/<name>/plugin.json`. |

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

`coda serve` exposes read-only skill and plugin surfaces to an orchestrator over JSON-RPC:

| Method | Result |
| --- | --- |
| `skills/list` | `{ skills: [{ name, description, origin, enabled, userInvocable, sourcePath, argumentHint }] }` |
| `plugins/list` | `{ plugins: [{ name, version, enabled, trusted, isExternal }] }` |
| `skills/trust` | Refused in an unattended serve session (no interactive callback); trust must be granted through the interactive gate. |

`skills/list` includes foreign skills, but an untrusted skill's model invocation remains blocked by
the unattended gate. See [`docs/serve-protocol.md`](serve-protocol.md) for the wire-level details.

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

### A plugin bundling a skill and a command

`.coda/plugins/release-tools/plugin.json`:

```json
{
  "name": "release-tools",
  "version": "1.0.0",
  "description": "Release automation helpers.",
  "author": "example",
  "license": "MIT",
  "commands": "commands",
  "skills": ["extra-skills"],
  "userConfig": [
    { "key": "release.channel", "type": "choice", "label": "Release channel",
      "required": true, "default": "stable", "options": ["stable", "beta"] }
  ]
}
```

`skills` is additive, so both `skills/` and `extra-skills/` are scanned. `commands` replaces the
default command directory. Install it and approve its executable components:

```
/plugin install .coda/plugins/release-tools
/plugin info release-tools
```
