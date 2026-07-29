# Proposal: TUI transcript and interaction polish

- **Date:** 2026-07-28
- **Status:** Backlog — not started. Queued behind the hooks, skills/plugins and caching work.
- **Author:** Yury Opolev (observed while running the autonomous implementation session)
- **Scope:** `Coda.Tui` — `Ui/Rendering/TranscriptBlockFormatter.cs`, `ToolActivityPreview.cs`,
  `CodaTheme.cs`/`CodaThemes.cs`, `Ui/Shells/VirtualizedTranscriptView.cs`,
  `TranscriptSelection.cs`, `TerminalGuiShellBase.cs`.
- **Companions:** independent of the three implementation proposals; touches only presentation and
  input, no engine behaviour.

## 1. Summary

Seven observations from watching a long autonomous run in the TUI. They fall into three groups:
**transcript legibility** (gutters, tree connectors, a pinned user message), **colour that means
something** (orange for partial failure, red reserved for actual failure), and **selection that
matches terminal habits** (right-click to copy, a selectable session id).

None of these change what the agent does. All of them change how much a long run can be followed.

## 2. Transcript legibility

### 2.1 Message gutters

Messages currently start hard against the left edge. Give every transcript message a one-space
margin, then a role marker, then a space:

| Role | Marker |
|---|---|
| User | `>` |
| Agent | a round marker — `○` for in-progress, `●` for complete |

So a user line reads ` > the message` and an agent line ` ○ the response`. The leading space is the
point: content flush against the terminal edge is harder to scan and looks unfinished.

### 2.2 Tree connectors for dependent lines

Child lines under a parent entry should be connected, not merely indented:

```
 ○ General-purpose(claude-sonnet-4.6) Fix skills Phase 3 findings
   │ Read PluginManifest.cs   168 lines read
   └ Read PluginInstaller.cs  274 lines read
```

`│` for a continuing child, `└` for the last one. This already reads correctly in the reference
screenshot; the work is making it the formatter's standard shape rather than an accident of one
renderer. Applies to tool activity under an agent turn, and to nested output under a subagent.

Must degrade cleanly when the terminal cannot render box-drawing characters — the existing
capability detection in `TerminalCapabilities` is the right place to decide.

### 2.3 Pin the last user message while the agent writes

During a long turn the prompt that started it scrolls away, and there is nothing on screen saying
what the agent is working on. Pin the most recent user message — or its first line, elided — to the
top of the transcript viewport while output is being produced.

- One line, elided with `…`, with the same `>` marker as the message itself.
- Visible only while the pinned message is scrolled out of view; no duplication when it is already on
  screen.
- Interacts with the assistant-text buffering added in hooks Phase 3 (§8.1 of the hooks proposal):
  when buffering is active there is no streaming text, but the pin is still wanted — it is what tells
  the user which prompt the placeholder belongs to.

## 3. Colour that means something

### 3.1 Approved tool calls must not be red

A tool that ran after being approved in permissions mode currently renders red. Red should mean
failure or a rejected permission, and nothing else. An approved-and-executed tool should be **orange
or yellow** — it is noteworthy, not wrong.

### 3.2 Tool-call summary colour should reflect the mix

The summary line for a batch of tool calls is currently red whenever anything failed. It should be:

| Outcome | Colour |
|---|---|
| All succeeded | green |
| Some failed | orange |
| All failed | red |

Both changes are theme roles, not literals — add them to `TuiTheme`/`CodaThemes` so every palette
defines them rather than hard-coding a colour at the call site.

## 4. Selection and clipboard

### 4.1 Right-click to copy

Copy is currently bound to a **left**-click after a selection. In PowerShell and most Windows
consoles the established gesture is **right**-click. Move it, and keep `Ctrl+C` working as it does
now.

Note the existing behaviour is documented in `README.md` ("when a selection exists, `Ctrl+C`, a
**left-click**, or a **right-click** copies it") — the README must move with the code, and the
right-click-pastes-at-caret behaviour in the composer needs re-checking so the two gestures do not
collide.

### 4.2 The session id should be selectable and copyable

The session id in the header is currently inert text. It should be selectable, and copyable with
either `Ctrl+C` or right-click — it is the single most frequently copied string in the UI, needed for
`/resume`, for bug reports, and for correlating logs.

## 5. Backlog

### Phase 1 — Transcript shape

- [ ] One-space gutter plus role marker for user (`>`) and agent (`○`/`●`) messages
- [ ] Tree connectors (`│`, `└`) for dependent lines, with an ASCII fallback via `TerminalCapabilities`
- [ ] Apply the same shape to nested subagent output

### Phase 2 — Pinned prompt

- [ ] Pin the last user message (first line, elided) to the top of the viewport while output is produced
- [ ] Hide the pin when the message is already visible
- [ ] Verify it behaves under hooks-Phase-3 assistant-text buffering

### Phase 3 — Colour semantics

- [ ] Theme roles for approved-tool and mixed-outcome states
- [ ] Approved tool calls render orange/yellow, not red
- [ ] Summary line: green all-succeeded, orange some-failed, red all-failed
- [ ] Audit remaining red usages so red means only failure or rejection

### Phase 4 — Selection and clipboard

- [ ] Right-click copies a selection; `Ctrl+C` unchanged
- [ ] Re-check the composer's right-click paste so the gestures do not conflict
- [ ] Session id selectable and copyable via `Ctrl+C` or right-click
- [ ] Update `README.md` mouse documentation to match

## 6. Open questions

1. **Does the agent marker distinguish states?** `○` in-progress and `●` complete is proposed, but a
   single marker may read more calmly. Worth trying both.
2. **Should the pin show more than one line** for a multi-line prompt? One line elided is proposed;
   a two-line cap may be better for prompts whose first line is a preamble.
3. **Does right-click-to-copy conflict with the terminal's own context menu** on some hosts, and
   should `--no-mouse` users get a keyboard route to the session id (a `/session id` command, or
   focusing the header)?
