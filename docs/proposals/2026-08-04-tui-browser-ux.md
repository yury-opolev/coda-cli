# Proposal: TUI browser UX — styled lists and a real MCP editor

- **Date:** 2026-08-04
- **Status:** Draft — reviewed 2026-08-04; awaiting decisions on §11 (D1–D8).
- **Author:** Yury Opolev (design exploration, Coda)
- **Scope:** `Coda.Tui` — `Ui/Shells/SelectableTextView.cs`, `ISelectableOverlay`, a new shared
  browser foundation, `Ui/Mcp/*`, `Ui/Skills/*`, `Ui/Plugins/*`, `Ui/Tasks/*`, `Ui/Schedule/*`,
  `Ui/Prompts/*`, `Ui/Rendering/*` (glyphs and theme roles), `Commands/ModelCommand.cs`.
- **Verified against:** `main` @ `44cc369`.

## 1. Summary

The five browser overlays (`/mcp`, `/skills`, `/plugin`, `/tasks`, `/schedule`) and the model picker
work, but they are text dumps: every row is drawn with **one uniform colour**, state is spelled out
in bracketed words rather than shown, and the MCP editor is not really an editor — it has no cursor,
typing can only append to the end of a value, and it shows all fourteen fields regardless of whether
they apply to the server's transport.

They are also five independent implementations of the same overlay. `ISelectableOverlay` exposes a
single property and its own doc comment concedes it is "the single thing they share"
(`ISelectableOverlay.cs:8-19`). Everything else — `Show`, `Hide`, `ApplyTheme`, the fault-swallowing
`Observe` continuation, `PageStep = 10`, selection movement, footer rendering — is copy-pasted. The
result is user-visible drift: three different selection markers, `q` closing three of five browsers,
`Space` meaning three different things, `r` meaning *reload* in one browser and *dismiss* in another.

This proposal does four things:

1. Give `SelectableTextView` **per-span attributes**, so any row can carry colour and emphasis.
2. Introduce a **shared browser foundation** that owns the duplicated behaviour, and a single
   status/type/enabled **symbol and colour vocabulary** used by every browser.
3. Rebuild the **MCP editor as a real form**: a visible caret, full text editing, field navigation,
   a highlighted focused field, radio-style pickers for fixed-value fields, add/remove/reorder for
   list-valued fields, and **fields that appear or disappear with the transport**.
4. Give **model selection a real browser** instead of the generic prompt overlay, which today has no
   scrolling at all — a long model list simply overflows the screen.

## 2. Current state (verified on `44cc369`)

### 2.1 Rendering

| Concern | Today | File |
|---|---|---|
| Overlay composition | Three `Label`s + one `SelectableTextView`; every label `CanFocus = false` | `McpBrowserOverlay.cs:14-97` |
| Draw model | Build a `List<string>`, join, one `SetText` per frame | `McpBrowserOverlay.cs:266-291` |
| Body colour | **One attribute for the whole body**; a second only for mouse-drag selection | `SelectableTextView.cs:39,209,214-262` |
| Interactive widgets | **None** — no `TextField`, `ListView`, `RadioGroup`, `CheckBox` anywhere | — |
| Caret | **None in any editable field.** The only caret-like glyph anywhere is Tasks' fixed-position `▏` drawn after the last character of the steering buffer | `TaskBrowserOverlay.cs:404` |
| Focus | Only the overlay is focusable; "focus" inside it is a manually tracked enum rendered as `>` | `McpBrowserOverlay.cs:46,152`, `SelectableTextView.cs:55` |
| Key swallowing | `OnKeyDown` returns `true` for every key while visible; shell short-circuits | `McpBrowserOverlay.cs:243`, `TerminalGuiShellBase.cs:707-718` |
| Mouse | `OnMouseEvent => this.Visible` — everything swallowed; no hit-test → row mapping | `McpBrowserOverlay.cs:253` |
| Focus restoration | Shell calls `SetFocus()` on the overlay on **every** controller change | `TerminalGuiShellBase.cs:1807-1830` |
| Sanitization | `SanitizeSingleLine` collapses whitespace runs and trims, then `SetLines` sanitizes again | `TerminalTextSanitizer.cs:133-137`, `SelectableTextView.cs:179-181` |
| Scroll coupling | The list keeps the selection visible by finding the line that `StartsWith("> ")` | `McpBrowserOverlay.cs:329` |

### 2.2 Duplication across the five browsers

Only `ISelectableOverlay { SelectableTextView Body; }` is shared. Independently reimplemented in
each overlay: `PageStep = 10`, the header/body/footer triple, `Visible/CanFocus/Dim.Fill/Rounded`
construction, `ApplyTheme`, `Show`, `Hide`, `Teardown`, `Dispose`, the `Observe` fault-swallowing
continuation (verbatim in all five), `OnControllerChanged → app.Invoke`, and selection movement.

Line-windowing is worse than duplicated — it is **inconsistent**: MCP has a real `Window(...)`
function with three offsets (`McpBrowserOverlay.cs:566-594`), Tasks has its own `BuildOutputWindow`,
and **Skills, Plugins and Schedule have none at all** — they dump every row into one `SetText`
(`SkillBrowserOverlay.cs:255-269`). Shell wiring is hand-enumerated rather than polymorphic
(`TerminalGuiShellBase.cs:726-747`).

### 2.3 User-visible inconsistencies

| Concern | Divergence |
|---|---|
| Selection marker | `▶ ` (Skills, Plugins, Schedule) vs `>` (Tasks, MCP, prompt) vs `●` = *current* in the model picker, which is also the Tasks tree glyph — one glyph, two meanings |
| Close key | `Esc` or `q` (Skills, Plugins, Schedule); `Esc` only (Tasks, MCP); `q` stops working in Skills/Plugins **detail** |
| Vim keys | `k`/`j` in Skills, Plugins, Schedule; not in Tasks, MCP, prompt |
| `Space` | Toggle enabled (Plugins, MCP) · **silently nothing** (Skills, `SkillBrowserOverlay.cs:200-202`) · submit (prompt) |
| `r` | *Reload* (Skills) vs *dismiss* (Tasks) |
| `u` | *Update plugin* (Plugins) vs *re-authenticate* (MCP) |
| Home/End, paging | Absent in Schedule; absent in Skills/Plugins **detail**; absent entirely from the model picker |
| Footers | Five dialects: "navigate" vs "move", "Enter detail" vs "Enter open", leading space or not; **the model picker has no footer at all** |
| Status surfacing | Dedicated label (MCP) · *replaces* the footer, so key hints vanish (Skills, Plugins, Schedule) · appended to the body (Tasks) · ignored in Skills detail |
| Empty states | Four wordings; only Schedule offers a next step |

### 2.4 The MCP editor

| Concern | Today | File |
|---|---|---|
| Field set | A single flat list of 14 fields rendered **unconditionally** — an stdio server still shows `Url`, `AuthMode`, `ClientId`, `Scopes`, `BearerToken` | `McpBrowserController.cs:17-33`, `McpBrowserOverlay.cs:405-446` |
| Field label | The raw enum name, e.g. `> BearerToken:` | `McpBrowserOverlay.cs:448-461` |
| Focused field | `>` marker only — no highlight, no caret | same |
| Typing | Printable rune → **append to end of string**. No left/right, no mid-string edit, no selection, no paste | `McpBrowserKeyMap.cs:51-70`, `McpBrowserController.cs:~380` |
| Backspace / Delete | Drop last char / **clear the entire field** | `McpBrowserController.cs:~393,~397` |
| Editable fields | Only `Name`, `Command`, `Url`, `ClientId`, `Scopes[i]`, `Arguments[i]`, and the *name* half of env/header pairs | `McpBrowserController.cs:~882-920` |
| Fixed-value fields | No picker — `Enter` cycles: Scope Project↔User (no-op when editing), Transport Http↔Stdio, Auth None→OAuth→None | `McpBrowserController.cs:~800-858` |
| List-valued fields | `Ctrl+N` add, `Ctrl+R` remove, `Ctrl+↑/↓` move between items, `Ctrl+←/→` switch name/value half. **No reorder** | `McpBrowserController.cs:~660-790` |
| Secrets | Never inline — a modal prompt; rendered `*****`, `(removed)` or `(unchanged)`. `MaskedSecret` discards its argument entirely | `McpBrowserController.cs:~614`, `McpBrowserOverlay.cs:656-664` |
| Validation | `PrepareX` returns an `McpEditPreview` carrying **`Warnings`** — prepare and commit run in one expression and **the warnings are never shown** | `McpBrowserController.cs:~589-609` |
| Plain arrows | Do nothing in the editor (control runes map to `None`) | `McpBrowserKeyMap.cs:51-70` |

### 2.5 The MCP list, and "current"

Rows read `> name [project] stdio enabled effective connection=Connected error=…`
(`McpBrowserOverlay.cs:292-338`). Everything is a word; nothing is a colour or a symbol. Tool counts
appear only in the detail pane.

**There is no notion of a "current" MCP server.** The two state axes that exist are
`IsEffective` — whether a project-scope entry shadows a same-named user-scope entry — and
`Connection` (`Overridden | Disconnected | Connected | Error`, `McpManagementModels.cs:12-18`).
Neither is visually distinguished. See decision **D3**.

### 2.6 Model selection

There is no model browser. `/model` builds a `UiPromptRequest.Select` and hands it to the generic
`PromptOverlay` (`ModelCommand.cs:52-160`). Consequences:

- **No scrolling or windowing** — every option goes into one `SetText`; a long list overflows
  (`PromptOverlay.cs:150-197`).
- **No Home/End/PageUp/PageDown, no type-to-filter, no footer** — the user gets no key hints at all.
- The current model *is* marked, as `● id — Display · 200K ctx · Current`
  (`UiPromptOptionFormatter.cs:11-30`).
- Provenance (`live` / `models.dev catalog` / `built-in`) and the built-in-fallback warning are
  printed on the **transcript** path but **not** in the picker (`ModelCommand.cs:185-211`).
- Metadata is limited to display name and context limit; `ReasoningLevels` is carried in
  `ModelListEntry` but never displayed.

### 2.7 Glyphs and theme

`TranscriptGlyphs` (`TranscriptGlyphs.cs:26-27`) is the only formal glyph set and the only one with
an ASCII fallback (`For(bool unicodeOutput)`). Everything else is hard-coded with no fallback:
`▶`, `>`, `●`, `└`, `•`, `▏`, `✎`.

The theme layer is in good shape and is **not** the bottleneck: `TuiPalette` supplies
`Success | Warn | Error | Dim | Accent`, and `TuiTheme` resolves semantic roles through it plus
`SelectionText`/`SelectionBackground`, with true-colour values and 16-colour fallbacks
(`TuiTheme.cs:60-90,180-200`). The browsers simply never use any of it — they take `SurfaceScheme`
and draw everything in one attribute.

A `●` running / `✓` completed / `✗` failed convention already exists in the **Spectre** command
surfaces (`TasksCommand.cs:119-121`, `StatusCommand.cs:38`, `ScheduleCommand.cs:87-88`) and is not
reused by the TUI.

## 3. What was asked for, and what blocks it

| Ask | Blocker today |
|---|---|
| Nicer list navigation | Three browsers have no windowing; the model picker has no scrolling at all |
| Distinguish the current MCP | No "current" concept exists; `IsEffective` is a word in a row |
| Distinct colours and symbols for enabled/disabled, status, type | `SelectableTextView` draws the body with a single attribute |
| Cursor visible in text fields | No caret exists in any editable field |
| Navigate fields with up/down/Tab/Shift-Tab | Tab/Shift-Tab work; plain arrows are dead in the editor |
| Highlight the current field | Only a `>` marker |
| Dropdown / radio for fixed values | `Enter` blind-cycles the value |
| Variable-length argument lists | Add/remove exist (`Ctrl+N`/`Ctrl+R`); no reorder, and discoverability is nil |
| UI changes with MCP type | All 14 fields always render |

Everything else follows from the first two rows: **per-span attributes** and **a caret**.

## 4. Rendering approach — DECIDED: real Terminal.Gui widgets (Option A)

**Every capability in the request is natively supported by Terminal.Gui 2.4.17**, verified by
reflecting on `Terminal.Gui.dll` (`lib/net10.0`) rather than by reading documentation. The codebase
uses **none** of it today — a repo-wide search for `new TextField(`, `new ListView(`, `new CheckBox(`
and friends returns zero hits. Every overlay is `Label` + a custom text view.

### 4.1 What the toolkit already provides

| Ask | Native support in 2.4.17 |
|---|---|
| Cursor visible in text fields | `TextField.InsertionPoint` drives the **real hardware cursor**; `TextField.DefaultCursorStyle` (`CursorStyle`) selects its shape |
| Navigate fields via Tab/Shift-Tab/up/down | Terminal.Gui focus traversal, with `SelectorBase.TabBehavior` per widget |
| Highlight the current field | `Scheme.Focus` — focus styling is a scheme state, not something we draw |
| "Dropdown" for fixed-value fields | **`DropDownList`** (`: TextField`, with `Source : IListDataSource` and `KeystrokeNavigator`) |
| "Radiobutton" for fixed-value fields | **`OptionSelector` / `OptionSelector<T>`** (`: SelectorBase`, with `Value`, `Values`, `Labels`, `Orientation`, `ValueChanged`) — this is the v2 replacement for v1's `RadioGroup`, **which no longer exists** |
| Distinct colours per row and per cell | **`TableView`** + `TableStyle.RowColorGetter` (`RowColorGetterDelegate` → `Scheme` per row) and `ColumnStyle.ColorGetter` (`CellColorGetterDelegate` → `Scheme` per cell) |
| Aligned, truncating columns | `ColumnStyle.Alignment`, `MinWidth`, `MaxWidth`, `TruncationIndicator`, `RepresentationGetter` |
| Variable-length argument lists | Dynamic child views in a container, plus `ListView`/`IListDataSource` where a list widget fits |
| Secret fields | **`TextField.Secret`** — native masked input |
| Read-only / disabled fields | `TextField.ReadOnly` |
| Scrolling, selection, mouse | Built into `TableView`, `ListView`, `TextField` |

Two consequences worth stating plainly:

- **`RadioGroup` is gone.** Anything written against v1 muscle memory will not compile. The
  equivalents are `OptionSelector` (inline, all options visible) and `DropDownList` (collapsed until
  opened). §8.4 picks between them per field.
- **The hardware cursor comes for free**, so the accessibility/IME concern that would have needed a
  separate decision under a hand-drawn caret simply does not arise. Screen readers and IME
  composition follow the real cursor.

### 4.2 The six obstacles, and how each is retired

These are real and verified; the work is bounded and each has an obvious landing point.

| Obstacle | Resolution |
|---|---|
| Overlay swallows every key (`McpBrowserOverlay.cs:243`) and the shell short-circuits while a browser is visible (`TerminalGuiShellBase.cs:707-718`) | The overlay stops blanket-returning `true`. It handles its own accelerators (close, filter, mode switches) and returns `false` for everything else so focused children receive input. The shell keeps short-circuiting *global* chords only. |
| Shell calls `SetFocus()` on the overlay on every controller change (`TerminalGuiShellBase.cs:1807-1830`) | **Not actually a problem** — verified during implementation: Terminal.Gui restores focus to the most-recently-focused *descendant*, so a focused table row or editor field keeps focus. No change needed; a test locks the behaviour. |
| `OnMouseEvent => this.Visible` swallows all mouse (`McpBrowserOverlay.cs:253`) | Removed. Mouse routes to children, which is how row-click, option-click and click-to-position-caret arrive **for free** — §12 moves this from "future work" to "falls out of the migration". |
| Terminal.Gui's Tab traversal collides with the editor's Tab = next-field mapping (`McpBrowserKeyMap.cs:54-55`) | Delete the mapping. Tab/Shift-Tab become ordinary focus traversal, which is exactly the requested behaviour. `↑`/`↓` are bound to move focus between fields as an additional affordance. |
| The test suite asserts on text seams (`BodyText`, `VisibleTextForTest`) | The largest single cost. Text seams are retained where a widget still renders text, and assertions move to widget state (`TextField.Value`, `OptionSelector.Value`, `TableView.SelectedRow`) where they do not. See §13. |
| Secrets must never sit in a live field | `TextField.Secret` masks *display*, but the widget still holds plaintext in memory — weaker than today, where `McpSecretReplacement.ToString()` returns `*****` and the value is reachable only via `internal RevealForCommit()` (`McpManagementModels.cs:52-68`). **The modal secret-prompt flow is therefore retained unchanged** (§8.6); no secret is ever bound to a `TextField`. |

### 4.3 What this changes about sanitization

Under a hand-drawn caret, sanitization was a correctness prerequisite: the indexed string had to be
the drawn string. With `TextField` owning both the buffer and the caret, that class of desync
disappears — the widget indexes what it draws.

Sanitization remains, in two narrower places:

- **On the way in.** Values loaded from config into a `TextField` are stripped of ANSI and control
  characters, `\t` mapped to a space, and `\r`/`\n` collapsed, so a hostile config cannot smuggle
  escape sequences into an editable buffer or split a single-line field.
- **On the way out.** Read-only rendering (list cells, detail rows) keeps `SafeSingle`
  (`SanitizeSingleLine`), whose whitespace-collapsing is now harmless because nothing indexes into it.

Grapheme/wide-character handling moves to the toolkit, which already handles it.

## 5. Feature 1 — a shared browser foundation

Extract what all five copy today: the header/body/footer construction, `ApplyTheme`,
`Show`/`Hide`/`Teardown`/`Dispose`, the `Observe` continuation, `OnControllerChanged → app.Invoke`
marshalling, and the key-routing shape.

**Scrolling and selection are no longer ours to own.** Under the widget approach, `TableView` and
`ListView` bring their own viewport, scrolling, paging and selection, so the hand-rolled
`Window(...)` (`McpBrowserOverlay.cs:566-594`), the three scroll offsets, and the `StartsWith("> ")`
selection scan (`:329`) are all deleted rather than promoted — and Skills, Plugins and Schedule get
scrolling for the first time simply by adopting the widget.

**Composition, not a base class.** The five overlays differ along four axes a single base class would
have to special-case anyway:

- **Status area size.** MCP allocates `Dim.Fill(2)` for a dedicated status label
  (`McpBrowserOverlay.cs:64`); the other four use `Dim.Fill(1)`. Imposing MCP's model everywhere
  costs one body row on all four — a real regression on a 24-row terminal, which is also the size
  the whole test suite runs at. Status presentation stays per-browser (§10 only requires that a
  status message never *replaces* the key hints).
- **Detail pane.** Schedule has none — `Enter` is unmapped there.
- **Teardown extras.** Tasks calls `ReleaseAttachment()` in `Hide` (`TaskBrowserOverlay.cs:165`),
  which a shared teardown must invoke through an overridable hook.
- **Editor mode.** Only MCP has one.

`TerminalGuiShellBase`'s hand-enumerated `HasVisibleBrowserOverlay()`/`VisibleBrowserOverlay()`
(`:726-747`) collapse to iteration over a registered list, and the focus-restoration handler
(`:1807-1830`) stops re-focusing the overlay on every controller change — mandatory once children
can hold focus.

## 6. Feature 2 — a symbol and colour vocabulary

One vocabulary, defined once, with an ASCII fallback in the `TranscriptGlyphs.For(unicode)` style,
used by every browser. Colour is delivered as a **`Scheme` per state**, built from the existing
palette roles, because that is the unit `TableStyle.RowColorGetter` and `ColumnStyle.ColorGetter`
consume.

| State | Glyph | ASCII | Role |
|---|---|---|---|
| Healthy / connected / running | `●` | `*` | `Palette.Success` |
| Enabled but idle / disconnected | `○` | `o` | `Palette.Dim` |
| Disabled | `⊘` | `x` | `Palette.Dim` |
| Error | `✗` | `!` | `Palette.Error` |
| Needs attention (untrusted, auth expiring) | `!` | `!` | `Palette.Warn` |
| Overridden by a higher-precedence scope | `↑` | `^` | `Palette.Dim`, whole row dimmed |

Additional conventions:

- **Selection** is the widget's own selected-row styling, not a marker character — `TableView` and
  `ListView` handle it. `▶`/`>` disappear entirely.
- **Current/active item** keeps `●` in `Palette.Accent`, matching the model picker today
  (`UiPromptOptionFormatter.cs:11`). With selection now a real highlight, the `●`/selection glyph
  collision noted in §2.3 disappears.
- **Type/transport tags** (`stdio`, `http`, `project`, `user`, `plugin`) get their own column, with
  the transport in `Palette.Accent` via `ColumnStyle.ColorGetter` so it is scannable down the column.
- Descriptions and secondary metadata use `Palette.Dim` so the name column dominates.

Every glyph resolves through the existing unicode-capability seam; no browser hard-codes one.

## 7. Feature 3 — the MCP list as a table

The list becomes a `TableView` over an `ITableSource` projected from `McpBrowserState.Servers`:

| Column | Content | Styling |
|---|---|---|
| Status | State glyph | `ColumnStyle.ColorGetter` → success / dim / error |
| Name | Server name | Default; whole row dimmed when overridden |
| Transport | `stdio` / `http` | `Palette.Accent` |
| Scope | `project` / `user` | `Palette.Dim` |
| Tools | Tool / prompt / resource counts | `Palette.Dim` |
| Error | Last error, truncated | `Palette.Error` |

- `TableStyle.RowColorGetter` supplies the per-row scheme (disabled and overridden rows dim wholesale).
- `ColumnStyle.MinWidth`/`MaxWidth`/`TruncationIndicator` give real column alignment and truncation —
  today rows are space-joined strings that ragged out at any width.
- **Tool/prompt/resource counts move into the list.** They exist only in detail today, and they are
  the single most useful "is this server actually working" signal.
- Scrolling, selection and mouse row-click come from the widget, replacing the hand-rolled
  `Window(...)` function and the `StartsWith("> ")` selection scan (`McpBrowserOverlay.cs:329,566-594`).

## 8. Feature 4 — the MCP editor as a real form

### 8.1 Transport-driven field sets

The flat 14-field list is replaced by a field set computed from the draft's transport:

| Section | stdio | http |
|---|---|---|
| Identity | Scope, Name, Transport, Enabled | Scope, Name, Transport, Enabled |
| Launch | **Command**, **Arguments[]**, **Environment{}** | — |
| Endpoint | — | **Url**, **Headers{}** |
| Auth | — | **AuthMode**, and when `AuthMode != None`: **ClientId**, **Scopes[]**, **BearerToken** |
| Actions | Save, Cancel | Save, Cancel |

**`Environment` is stdio-only.** `McpStdioServerConfig` has `Env`; `McpHttpServerConfig` has only
`Headers` and `Auth`, and `NormalizeHttpDraft` unconditionally zeroes it
(`McpManagementService.cs:820`: `Environment = ImmutableArray<McpNamedSecretDraft>.Empty`). Rendering
an Environment section for an http draft would let a user type values that are silently discarded at
commit. (The current *detail* pane does show Environment for http — `McpBrowserOverlay.cs:380-391` —
which is misleading and should be dropped too.)

Changing Transport recomputes the field set; the focused field falls back to the nearest surviving
field. Hidden values stay in the draft so a mis-toggle-and-back is not destructive.

### 8.2 Changing transport while editing must be warned, not silent

Draft-level retention does **not** survive commit. Both `NormalizeStdioDraft`
(`McpManagementService.cs:723`) and `NormalizeHttpDraft` (`:776`) erase the opposite transport's
fields at *Prepare* time — for stdio: `Url = null`, `Headers = empty`, `AuthMode = None`,
`ClientId = null`, `Scopes = empty`, `BearerToken = Unchanged`; the mirror for http.

Today `ChangeTransport` (`McpBrowserController.cs:~820-836`) has no Edit-mode guard, and making it a
first-class selector widget (§8.4) makes it far easier to hit by accident. An edit that toggles
transport and saves silently destroys the URL, auth mode, client id, scopes and stored bearer token.

**Transport stays changeable in Edit mode, and the service learns to warn about it.** Today
`CreateScopeWarnings` (`:1074`) is the only warning producer and only covers scope shadowing. It
gains a sibling that fires when an Edit-mode draft's transport differs from the original, naming
exactly which fields will be dropped. The §8.7 confirm gate then makes Save an explicit,
informed choice rather than a silent one.

This means the warning path is **load-bearing, not cosmetic**: §8.7 must ship together with this, or
the data loss remains unguarded. See **D3**.

### 8.3 Text editing and field navigation

Text fields become `TextField`, so the entire class of hand-rolled editing behaviour disappears:

- **The caret is the real terminal cursor**, positioned by `TextField.InsertionPoint`, with its shape
  controlled by `DefaultCursorStyle`. Screen readers and IME composition follow it natively.
- `←`/`→`, `Home`/`End`, word jumps, insert at caret, `Backspace`, selection, paste and a context
  menu all come from the widget. In particular `Delete` deletes **at** the caret instead of clearing
  the whole field — today's behaviour is a genuine footgun (`McpBrowserController.cs:~397`).
- **`Tab`/`Shift+Tab` are ordinary focus traversal**; the editor's custom Tab mapping is deleted.
  `↑`/`↓` are additionally bound to move focus between fields, as requested.
- **The focused field is highlighted by `Scheme.Focus`** — focus styling is a scheme state, not
  something the renderer computes. The `>` marker disappears.
- `Enter` on a text field advances focus; on `Save`/`Cancel` it activates.

### 8.4 Fixed-value fields as pickers

`Scope`, `Transport`, `AuthMode` and `Enabled` become real selector widgets rather than blind cycling:

- **`OptionSelector`** for small option sets where seeing every choice helps — `Transport`
  (stdio/http), `Scope` (project/user), `AuthMode` (none/OAuth). `SelectorBase.Orientation`
  renders them horizontally so a field occupies one row. `ValueChanged` drives the draft update and,
  for `Transport`, the field-set recomputation.
- **`CheckBox`** for `Enabled`.
- **`DropDownList`** is held in reserve for any fixed-value field whose option set grows past what
  fits on a row; it derives from `TextField`, so it also gives type-to-narrow via
  `KeystrokeNavigator`.

`Scope` is non-interactive in Edit mode (as today, `McpBrowserController.cs:~800-818`), rendered
through the disabled scheme rather than silently ignoring input. `Transport` stays interactive and is
guarded by the transport-change warning instead (§8.2, D3).

### 8.5 List- and map-valued fields

`Arguments[]`, `Scopes[]`, `Environment{}`, `Headers{}` become a container of per-item rows, each
row holding a real `TextField` (two for map entries: name and value). Adding a row constructs a
widget; removing disposes it; focus traversal walks them naturally.

- `Ctrl+N` insert after the current item · `Ctrl+R` remove · **`Alt+↑`/`Alt+↓` reorder** (new —
  argument order is semantically significant for stdio). Moving *between* items and between the
  name/value halves is now just Tab/Shift-Tab and `↑`/`↓`, so `Ctrl+↑/↓/←/→` are retired.
- Every binding is advertised in a context-sensitive footer that changes with the focused field —
  the current ones are undiscoverable.
- Secrets keep the modal-prompt path unchanged (§8.6): no secret is ever bound to a `TextField`,
  even a `Secret`-masked one, and secret rows keep rendering `*****` / `(removed)` / `(unchanged)`.

**⚠ Reorder must move item *identity*, not values.** `Args`/`Scopes` are shadowed by parallel
`McpDraftListItem` arrays, and commit resolves each edited item's original raw value by GUID
(`MergeIdentifiedDraftListValues`, `McpManagementService.cs:3266-3290`: it maps
`baselineItems[i].Id → originalValues[i]`, then rewrites each edited item back to its raw value).
That GUID map is what preserves the true value of a redacted entry through an edit.

The obvious implementation — swap the `.Value` strings and leave the items in place — silently
corrupts data: after a swap, `G_A`'s value no longer matches its baseline display value, so the
merge falls through to the *safe display value*, and a redaction sentinel gets persisted into
`mcp.json` as a real argument. Reorder must therefore move the `McpDraftListItem` objects
themselves, so their GUIDs travel with them, keeping `Args` and `ArgumentItems` lock-stepped. §13
covers this with a dedicated test against a draft loaded from a config containing redacted values.

### 8.6 Secrets stay on the modal path

`TextField.Secret` exists and masks display, but the widget holds plaintext in its buffer. Today's
model is stronger: `McpSecretReplacement.ToString()` returns `*****` and the value is reachable only
through `internal RevealForCommit()` (`McpManagementModels.cs:52-68`), so a secret never exists as a
readable string in the UI layer at all.

Bearer tokens and env/header *values* therefore keep the existing modal-prompt flow
(`UiPromptRequest.Text(..., secret: true)`, `McpBrowserController.cs:~614`). Adopting widgets
everywhere else does not justify weakening this.

### 8.7 Surfacing validation warnings

`PrepareAddAsync`/`PrepareEditAsync` already return an `McpEditPreview` carrying `Warnings`, and the
browser discards them by preparing and committing in a single expression
(`McpBrowserController.cs:~589-609`). Split the two: on `Save`, prepare, and if warnings exist show
them and require confirmation before commit — matching the prepare → confirm → commit flow that
delete and reauth already use (`:~455-480`).

**But not with the lease held across the prompt.** Delete and reauth hold the idle-gate lease for
the whole prepare → confirm → commit sequence, which is right for a quick yes/no. A user reviewing
save warnings may sit on that dialog for a long time, and `MutateWithLeaseAsync`
(`McpBrowserController.cs:~792-798`) would block every other MCP action *and* fail any turn the user
starts with "MCP changes are unavailable while a turn is running". Save therefore releases the lease
around the confirmation and re-acquires it to commit, which means **commit must re-validate** —
the configuration may have changed in the gap, and `McpEditPreview` already carries a `Revision` for
exactly this kind of staleness check.

## 9. Feature 5 — a model browser

Replace the generic prompt with a first-class browser consistent with the others, addressing the
concrete gaps in §2.6: windowed scrolling, `Home`/`End`/`PageUp`/`PageDown`, a footer with key
hints, and **type-to-filter** (the one navigation aid a 100-model Copilot list genuinely needs).

Rows show the status glyph for the current model, the id, display name, context limit, and reasoning
levels (carried in `ModelListEntry` and never shown today). The **provenance line and built-in
fallback warning move into the browser header** — a user picking from a stale built-in list should
be told, and today only the non-interactive transcript path says so.

`/model <id>` keeps working unchanged, and the Spectre path stays for non-interactive hosts.

## 10. Feature 6 — consistency pass

Bindings are **per view**, not global. This matters: in the editor, printable runes are *text*
(`McpBrowserKeyMap.cs:63-68`), so a global `q`-closes rule would close the browser while the user
types "quilt" into a server name, and a global `/`-filters rule would fire on `/usr/local/bin/mcp-fs`.

**List view** — identical across every browser:

| Action | Binding |
|---|---|
| Close | `Esc` and `q` |
| Move | `↑`/`↓` and `k`/`j` |
| Page / jump | `PgUp`/`PgDn`, `Home`/`End` |
| Open detail | `Enter` |
| Toggle enabled | `Space` — and where a browser cannot toggle, it says so in the status row instead of doing nothing silently (Skills today, `SkillBrowserOverlay.cs:200-202`) |
| Reload | `r` |
| Filter | `/` |

**Detail view** — `Esc`/`q` back, plus the same movement and paging keys (absent today in Skills and
Plugins detail).

**Filter mode** — entered with `/`; keys route to the filter buffer; `Esc` exits *filter* and returns
to the list, and only a second `Esc` closes the browser. Without this, filter is a black hole.

**Editor view** — printable runes are text; only the modified/named keys from §8 are bound.

Footers, headers and empty-state messages get one voice, and every advertised action is actually
bound. Empty states name the next step, as Schedule's already does.

## 11. Decisions

**D1 — rendering approach. DECIDED: Option A, real Terminal.Gui widgets** (user choice, 2026-08-04).
Reflection on `Terminal.Gui.dll` 2.4.17 confirms native support for every clause of the request:
`TextField` (real cursor via `InsertionPoint`, `DefaultCursorStyle`), focus traversal for
Tab/Shift-Tab, `Scheme.Focus` for the focused-field highlight, `OptionSelector` and `DropDownList`
for fixed-value fields, and `TableView` with `RowColorGetter`/`ColorGetter` for per-row and per-cell
colour. Mouse support arrives as a side effect. The costs are the six obstacles in §4.2 and the test
migration in §13.

**D2 — sanitization. DECIDED: no caret-index sanitizer needed** (superseded by D1). `TextField` owns
both buffer and caret, so the desync class disappears. Sanitization narrows to stripping ANSI and
control characters on load into a field, and `SafeSingle` on read-only rendering (§4.3).

**D5 — hardware cursor. DECIDED: moot** (superseded by D1). Terminal.Gui drives the real cursor for
the focused `TextField`, so IME and screen-reader support come for free.

**D6 — filter modality. DECIDED: `Esc` exits filter before it closes the browser** (§10).

Still open:

- **D3 — Transport in Edit mode. DECIDED: stays changeable, guarded by a new service warning**
  (user choice, 2026-08-04). `CreateScopeWarnings` (`McpManagementService.cs:1074`) gains a sibling
  that fires when an Edit-mode draft's transport differs from the original and names the fields that
  will be dropped; §8.7's confirm gate turns Save into an informed choice. **The warning path is
  load-bearing and must ship with this** — without it the data loss is unguarded.
- **D4 — what "current MCP" means. DECIDED: both** (recommendation adopted; question skipped, so
  revisit if the result is not what was meant). Selection becomes the table's own highlight rather
  than a `>` prefix, **and** `IsEffective == false` renders as a dimmed row with the `↑` glyph
  instead of the word `overridden` (§6, §7). Neither is wasted work under either reading of the ask.
- **D7 — scope and sequencing.** All five browsers plus the model picker at once, or MCP first as
  the pilot? *Recommended: foundation + MCP first — it is the only one with an editor, so it
  exercises every widget type and the whole test-migration pattern before four other migrations
  depend on the result.*
- **D8 — `r` rebinding.** `r` = *reload* becomes universal, which changes behaviour in **three**
  browsers: Tasks currently uses `r` for *dismiss* (`TaskBrowserKeyMap.cs:73`), and MCP and Plugins
  have no `r` today. Rebind Tasks' dismiss to `d`, or leave Tasks inconsistent?
  *Recommended: rebind.*

## 12. Out of scope / follow-ups

- **Editing skills and plugins.** The original request named "models, mcp and skills and plugins".
  This proposal gives all four consistent, legible, colour-coded lists, but a real editor **only for
  MCP** — because MCP is the only one with a mutable config the TUI already owns end to end
  (`IMcpManagementService`'s prepare/confirm/commit). Skills stay frontmatter-driven (there is no
  runtime toggle to build on — `SkillBrowserOverlay.cs:200-202`), and plugins keep their existing
  toggle and update actions. Editors for both are a deliberate deferral, not an oversight.
- **Plugin trust management.** The browser shows `trusted`/`untrusted` but has no approve action
  (`PluginBrowserController.cs:64-80`); granting trust from the browser is a separate design.
- **Mouse interaction is now in scope, not deferred.** Removing the mouse swallow
  (`McpBrowserOverlay.cs:253`) lets clicks reach children, so row-click, option-click and
  click-to-position-caret arrive as a side effect of the widget migration rather than as separate
  work.
- **The Tasks steering caret.** Tasks already draws a fixed-position `▏` after the last character
  (`TaskBrowserOverlay.cs:404`) — the one caret-like thing that exists today. It should become a real
  `TextField` once the pattern is established, but it is not migrated here.
- **Serve parity.** Per the standing rule this is a TUI-only proposal; the underlying management
  operations already exist behind `IMcpManagementService` and are unchanged. No serve surface is
  added or altered.
- **Theme customisation of the new roles** — the vocabulary in §6 resolves through existing palette
  roles, so themes inherit it. Per-role overrides for browser chrome are a follow-up.

## 13. Test strategy

**This is the largest single cost of the widget approach and must be planned, not discovered.** The
current suite asserts almost entirely on text seams — `Assert.Contains("untrusted", overlay.BodyText)`
(`PluginBrowserOverlayTests.cs:76`), `overlay.VisibleTextForTest` (`McpBrowserOverlayTests.cs:90-101`).
Once rows are table cells and fields are widgets, those seams no longer describe what is on screen.

The migration has three tiers:

1. **Keep the harness.** Real Terminal.Gui app, ANSI driver at a fixed 80×24, hermetic temp working
   directory, `NewKeyDownEvent` injection, controller-state assertions — all unchanged and still
   correct (`SkillBrowserOverlayTests.cs:22-30,96`).
2. **Assert widget state where widgets own it** — `TextField.Value`, `TextField.InsertionPoint`,
   `OptionSelector.Value`, `CheckBox.CheckedState`, `TableView.SelectedRow`, and which view holds
   focus. This is *better* than string matching: it tests intent rather than layout.
3. **Keep a rendered-text seam for regressions that are about pixels, not state** — the existing
   driver cell-scrape helper (`McpBrowserOverlayTests.cs:334-350`) already does this and is the right
   tool for "no secret on screen", "no `\u001b`", and narrow-terminal survival.

| Area | Cases |
|---|---|
| Table styling | `RowColorGetter` returns the dim scheme for disabled and overridden rows; `ColumnStyle.ColorGetter` gives the status cell success/error; columns truncate rather than ragging at narrow widths; ASCII mode substitutes glyphs |
| Caret and editing | `InsertionPoint` after typing, `Home`/`End`, mid-string insert, `Delete` removes one character and **does not clear the field**; values with tabs, consecutive spaces and wide/CJK characters edit correctly |
| Focus traversal | `Tab`/`Shift+Tab` and `↑`/`↓` move focus in declaration order and wrap; the focused field shows the focus scheme; **the shell no longer steals focus on controller changes** |
| Selectors | `OptionSelector.Value` reflects the draft and `ValueChanged` updates it; `Transport` and `Scope` are non-interactive in Edit mode; `CheckBox` toggles `Enabled` |
| Transport-driven fields | stdio shows no `Url`/`Headers`/`Auth*` widgets; **http shows no `Command`/`Arguments`/`Environment`** (§8.1); changing transport in Add mode adds/removes child views and re-anchors focus; `AuthMode = None` hides its dependants |
| Transport change | Editing an http server, switching to stdio and saving raises a transport-change warning naming the dropped fields; confirming commits, cancelling keeps the draft; no warning when transport is unchanged |
| List fields | Add constructs a row widget, remove disposes it, focus survives both; **reordering a draft loaded from a config containing redacted values preserves the original raw values through commit** (GUID identity travels with the item — §8.5) |
| Secrets | No secret is ever bound to a `TextField`, `Secret`-masked or otherwise; the modal prompt still drives replacement; secrets never appear in a driver cell-scrape |
| Warnings | A preview with warnings shows them and requires confirmation before commit; **the idle-gate lease is not held across the confirmation**; commit re-validates against the preview `Revision` and fails cleanly if the config moved |
| Key routing | The overlay no longer returns `true` for every key: accelerators are handled, everything else reaches the focused child; **`q`, `k`, `j`, `r`, `/` are inserted as text when a `TextField` has focus** |
| Mouse | Row click selects; option click selects; click in a text field positions the caret — all newly possible once the mouse swallow is removed |
| Model browser | Scrolls beyond the viewport; filter narrows rows; `Esc` exits filter before closing; current model marked; provenance and built-in-fallback warning in the header |
| Consistency | Table-driven over every browser: `q`/`Esc` close from list *and* detail; `k`/`j` move; paging bound; every footer-advertised key is actually bound |
| Regression | No secret in any rendered output; no `\u001b`; markup like `[red]` survives literally; narrow-terminal (28×8, 24×8) layout still shows selection and status |
