# TUI Browser UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the MCP browser on real Terminal.Gui widgets — a coloured, aligned `TableView` list and a genuine form with cursors, focus traversal and selectors — then migrate the other browsers and the model picker onto the same foundation.

**Spec:** [`docs/proposals/2026-08-04-tui-browser-ux.md`](../../proposals/2026-08-04-tui-browser-ux.md) — decisions D1–D8 are closed there; do not re-litigate them.

**Architecture:** Overlays stop being text dumps. The list becomes a `TableView` over an `ITableSource`, coloured by `TableStyle.RowColorGetter` and `ColumnStyle.ColorGetter`; the editor becomes real child views (`TextField`, `OptionSelector`, `CheckBox`) whose focus traversal *is* the field navigation. The controller keeps owning state and mutations; widgets become the view layer over it, not a replacement for it.

**Tech Stack:** .NET 10, C# 14, Terminal.Gui 2.4.17, xUnit. Test project: `tests/Coda.Tui.Tests`.

**Baseline:** branch `feat/tui-browser-ux` @ `65240b4` (already carries the `❯` glyph and composer panel work), on top of `main` @ `44cc369`.

---

## Terminal.Gui 2.4.17 API notes (verified by reflecting on the shipped assembly, then **corrected by the Task 1 spike**)

Do not trust v1 muscle memory. Verified facts:

- **`RadioGroup` does not exist.** Use `OptionSelector` / `OptionSelector<T>` (`: SelectorBase`, with
  `Value`, `Values`, `Labels`, `Orientation`, `TabBehavior`, `ValueChanged`).
- **`DropDownList`** derives from `TextField` and exposes `Source : IListDataSource` and
  `KeystrokeNavigator`.
- **`TextField`** exposes `Value`, `Text`, `InsertionPoint` (the caret), `Used` (insert vs
  overwrite), `ReadOnly`, `Secret`, `Autocomplete`, `SelectedText`/`SelectedStart`/`SelectedLength`,
  `ScrollOffset`, `DefaultCursorStyle`, and `ValueChanging`/`ValueChanged`/`TextChanging`.
- **`TableView`** styling: `TableStyle.RowColorGetter` (`RowColorGetterDelegate` → `Scheme`),
  `ColumnStyle.ColorGetter` (`CellColorGetterDelegate`), `RepresentationGetter`, `Alignment`,
  `MinWidth`/`MaxWidth`, `TruncationIndicator`, `ShowHeaders`, `HeaderScheme`.
- All live in namespace `Terminal.Gui.Views`.

**Corrections found by the spike — the earlier draft of this plan had these wrong:**

| Assumed | Actual |
|---|---|
| `TableView.SelectedRow` | **Does not exist.** Selection is `TableView.Value : TableSelection`; the row is `view.Value.SelectedCell.Y` |
| `TableStyle.ColumnStyles[DataColumn]` | Keyed by **column index** — `Dictionary<int, ColumnStyle>` |
| `CheckBox.CheckedState` | Property is **`CheckBox.Value : CheckState`** (`CheckBox` also has `RadioStyle`) |
| Tab is handled by the container automatically | In this harness a bare `Tab` key event on the parent does **not** traverse. Call `view.AdvanceFocus(NavigationDirection.Forward\|Backward, TabBehavior.TabStop)`, and set `TabStop = TabBehavior.TabStop` on each field |
| A `TextField` inserts at the caret by default | Two traps: `Used` must be **`true`** for insert (`false` overwrites), and the value must be assigned **after** the field is added to the view tree — setting `Text` in the object initialiser leaves a selection, so the first keystroke replaces a character instead of inserting |

**No Terminal.Gui input widget is used anywhere in the codebase today** — this is greenfield here, so
Task 1 exists to derisk it before anything depends on it.

---

## Invariants that must not regress

Each has a dedicated test in this plan:

1. **No secret is ever bound to a `TextField`**, `Secret`-masked or otherwise (Task 7).
2. **Reordering a list field preserves redacted raw values** — GUID identity must travel with the item (Task 8).
3. **Changing transport in Edit mode warns before it destroys fields** (Task 9).
4. **The shell no longer steals focus from a focused child** on controller changes (Task 2).
5. Secrets never reach the screen, and no `\u001b` ever does (Task 12).

---

## File Structure

- Create `src/Coda.Tui/Ui/Shells/BrowserChrome.cs` — shared header/body/footer/status composition and lifecycle, by composition.
- Create `src/Coda.Tui/Ui/Rendering/StatusGlyphs.cs` — the §6 glyph vocabulary with ASCII fallback.
- Create `src/Coda.Tui/Ui/Rendering/BrowserSchemes.cs` — per-state `Scheme`s built from `TuiPalette`.
- Create `src/Coda.Tui/Ui/Mcp/McpServerTableSource.cs` — `ITableSource` over `McpBrowserState.Servers`.
- Create `src/Coda.Tui/Ui/Mcp/McpEditorForm.cs` — the widget-based editor pane.
- Create `src/Coda.Tui/Ui/Mcp/McpEditorFieldSet.cs` — transport-driven field-set resolution.
- Modify `src/Coda.Tui/Ui/Mcp/McpBrowserOverlay.cs` — host the table and the form; stop blanket key/mouse swallowing.
- Modify `src/Coda.Tui/Ui/Mcp/McpBrowserKeyMap.cs` — drop editor rune/Tab mappings; keep accelerators.
- Modify `src/Coda.Tui/Ui/Mcp/McpBrowserController.cs` — remove hand-rolled text editing; add caret-free draft updates; split prepare/confirm/commit for Save.
- Modify `src/Coda.Tui/Mcp/McpManagementService.cs` — transport-change warning.
- Modify `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs` — focus restoration, key short-circuit, overlay registry.
- Modify `src/Coda.Tui/Ui/Skills/*`, `Ui/Plugins/*`, `Ui/Schedule/*`, `Ui/Tasks/*` — migrate to the foundation (Phase 4).
- Create `src/Coda.Tui/Ui/Models/ModelBrowser{Controller,Overlay,Models}.cs` — the model browser (Phase 5).
- Modify `src/Coda.Tui/Commands/ModelCommand.cs` — route interactive `/model` to the browser.
- Tests: create `McpBrowserTableTests.cs`, `McpEditorFormTests.cs`, `McpEditorFieldSetTests.cs`, `BrowserChromeTests.cs`, `ModelBrowserTests.cs`; modify the existing overlay test files.
- Modify `README.md`; modify `version.json`.

---

## Phase 1 — Foundations

### Task 1: Widget spike — prove the integration before building on it

**Files:** Create `tests/Coda.Tui.Tests/WidgetIntegrationSpikeTests.cs`

This task exists because the repo has never hosted a focusable Terminal.Gui child. It is throwaway
scaffolding whose only job is to fail fast if an assumption in the spec is wrong.

- [ ] **Step 1: Write tests** proving, inside the existing 80×24 ANSI harness, that: a `TextField`
  added to a `View` receives typed keys and updates `Value`/`InsertionPoint`; `Tab` moves focus
  between two `TextField`s; an `OptionSelector` reports `Value` after `←`/`→`; a `TableView` with a
  `RowColorGetter` renders and reports `SelectedRow`; and the driver cell-scrape helper can still
  read what is on screen.
- [ ] **Step 2: Record findings** in the plan if any assumption fails — **stop and report rather than
  working around it**, since Tasks 2–12 all depend on these. *(Done: five corrections recorded in the
  API notes above; `tests/Coda.Tui.Tests/WidgetIntegrationSpikeTests.cs` pins all of them, 16 tests.)*
- [ ] **Step 3: Verify** — `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~WidgetIntegrationSpike"`

### Task 2: Unblock input — stop swallowing keys, mouse and focus

**Files:** Modify `src/Coda.Tui/Ui/Mcp/McpBrowserOverlay.cs`, `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
**Test:** Modify `tests/Coda.Tui.Tests/McpBrowserOverlayTests.cs`

- [ ] **Step 1: Write failing tests** — a focused child receives a printable key while the overlay is
  visible; the overlay still handles its own accelerators (`Esc`); a mouse click reaches a child;
  **a controller `Changed` event does not move focus away from a focused child** (invariant 4);
  showing the overlay still focuses it initially.
- [ ] **Step 2: Implement** — `OnKeyDown` handles accelerators and returns `false` otherwise instead
  of blanket `true` (`:243`); remove the `OnMouseEvent => this.Visible` swallow (`:253`); in
  `TerminalGuiShellBase.OnMcpBrowserChanged` (`:1807-1830`) restore focus **only when the overlay
  does not already contain focus**; narrow the shell's `OnKeyDown` short-circuit (`:707-718`) to
  global chords.
- [ ] **Step 3: Verify** — `--filter "FullyQualifiedName~McpBrowserOverlay"`

**Note:** this task changes behaviour before the widgets exist, so the browser is briefly *less*
guarded. Keep Tasks 2 and 3 in the same commit.

### Task 3: Shared chrome and the glyph/scheme vocabulary

**Files:** Create `BrowserChrome.cs`, `StatusGlyphs.cs`, `BrowserSchemes.cs`
**Test:** Create `tests/Coda.Tui.Tests/BrowserChromeTests.cs`

- [ ] **Step 1: Write failing tests** — `StatusGlyphs.For(unicode: false)` returns the ASCII set and
  every glyph is one cell wide in both sets (mirror `TranscriptGlyphsTests`); each state maps to the
  §6 palette role; `BrowserSchemes` resolves in both true-colour and 16-colour modes; `BrowserChrome`
  composes header/body/footer, applies the theme, and runs an injected teardown hook exactly once.
- [ ] **Step 2: Implement** by **composition, not inheritance** — the five overlays differ in status
  area size, detail pane, teardown extras and editor mode (spec §5). `BrowserChrome` is a helper the
  overlay owns, not a base class it derives from.
- [ ] **Step 3: Verify** — `--filter "FullyQualifiedName~BrowserChrome|FullyQualifiedName~StatusGlyph"`

---

## Phase 2 — The MCP list

### Task 4: `ITableSource` over the server list

**Files:** Create `McpServerTableSource.cs`
**Test:** Create `tests/Coda.Tui.Tests/McpBrowserTableTests.cs`

- [ ] **Step 1: Write failing tests** — column set and order (Status, Name, Transport, Scope, Tools,
  Error); tool/prompt/resource counts appear in the Tools cell (they are detail-only today);
  the status cell uses the glyph for each `McpConnectionState`; a disabled server shows the disabled
  glyph; `IsEffective == false` is reported as overridden; an empty server list yields zero rows.
- [ ] **Step 2: Implement** the source as a pure projection of `McpBrowserState.Servers` — no I/O, so
  it is unit-testable without a driver.
- [ ] **Step 3: Verify** — `--filter "FullyQualifiedName~McpBrowserTable"`

### Task 5: Host the `TableView` and colour it

**Files:** Modify `McpBrowserOverlay.cs`
**Test:** Modify `McpBrowserTableTests.cs`, `McpBrowserOverlayTests.cs`

- [ ] **Step 1: Write failing tests** — `RowColorGetter` returns the dim scheme for disabled and
  overridden rows and the normal scheme otherwise; `ColumnStyle.ColorGetter` gives the status cell
  success/dim/error; the transport column uses the accent role; moving the selection updates
  `TableView.SelectedRow` and the controller's `SelectedKey`; columns truncate at 28×8 and 24×8
  rather than ragging (keep the existing narrow-terminal tests passing).
- [ ] **Step 2: Implement** — replace the list branch of `Render` with the `TableView`. **Delete**
  `Window(...)` (`:566-594`), `listOffset`, and the `StartsWith(SelectionPrefix)` scan (`:323`);
  the widget owns scrolling and selection now.
- [ ] **Step 3: Verify** — `--filter "FullyQualifiedName~McpBrowserTable|FullyQualifiedName~McpBrowserOverlay"`

---

## Phase 3 — The MCP editor

### Task 6: Transport-driven field sets

**Files:** Create `McpEditorFieldSet.cs`
**Test:** Create `tests/Coda.Tui.Tests/McpEditorFieldSetTests.cs`

- [ ] **Step 1: Write failing tests** — stdio yields Identity + Command + Arguments + **Environment**,
  and **no** `Url`/`Headers`/`Auth*`; http yields Identity + Url + Headers + Auth, and **no**
  `Command`/`Arguments`/**`Environment`** (spec §8.1 — `NormalizeHttpDraft` zeroes Environment at
  `McpManagementService.cs:820`, so offering it would silently discard input); `AuthMode = None`
  hides `ClientId`/`Scopes`/`BearerToken`; changing transport recomputes the set.
- [ ] **Step 2: Implement** as a pure function from draft → ordered field descriptors.
- [ ] **Step 3: Verify** — `--filter "FullyQualifiedName~McpEditorFieldSet"`

### Task 7: The editor form

**Files:** Create `McpEditorForm.cs`; modify `McpBrowserOverlay.cs`, `McpBrowserKeyMap.cs`, `McpBrowserController.cs`
**Test:** Create `tests/Coda.Tui.Tests/McpEditorFormTests.cs`

- [ ] **Step 1: Write failing tests**
  - Text: typing updates `TextField.Value` and the draft; `InsertionPoint` moves with `←`/`→`/`Home`/`End`; mid-string insert works; **`Delete` removes one character and does not clear the field** (today it clears — `McpBrowserController.cs:~397`).
  - Navigation: `Tab`/`Shift+Tab` and `↑`/`↓` move focus in field order and wrap; the focused field shows the focus scheme.
  - Selectors: `OptionSelector.Value` reflects the draft and `ValueChanged` writes back; `CheckBox` toggles `Enabled`; `Scope` is non-interactive in Edit mode.
  - Field sets: switching transport adds/removes child views and re-anchors focus to a surviving field.
  - **Invariant 1:** no secret is bound to a `TextField` — bearer/env/header *values* still go through the modal prompt (`UiPromptRequest.Text(..., secret: true)`), and the row renders `*****`/`(removed)`/`(unchanged)`.
  - Text-key regression: `q`, `k`, `j`, `r`, `/` are inserted as text when a `TextField` has focus.
- [ ] **Step 2: Implement.** Delete the controller's hand-rolled editing (`InsertEditorCharacter`, backspace/delete, `MoveEditor`, `EditEditorText`) and the editor rune/Tab mappings in the key map (`:50-70`); the widgets own all of it.
- [ ] **Step 3: Verify** — `--filter "FullyQualifiedName~McpEditorForm|FullyQualifiedName~McpBrowserKeyMap"`

### Task 8: List- and map-valued fields, with safe reorder

**Files:** Modify `McpEditorForm.cs`, `McpBrowserController.cs`
**Test:** Modify `McpEditorFormTests.cs`

- [ ] **Step 1: Write failing tests** — `Ctrl+N` adds a row widget and focuses it; `Ctrl+R` removes and
  disposes it; `Alt+↑`/`Alt+↓` reorder; map entries expose name and value fields reachable by Tab.
  **Invariant 2:** load an edit draft from a config whose argument values are redacted, reorder two
  rows, commit — the original raw values are preserved and no redaction sentinel is persisted.
- [ ] **Step 2: Implement.** Reorder must move the `McpDraftListItem` objects so their GUIDs travel
  with them, keeping `Args`/`ArgumentItems` lock-stepped. Swapping `.Value` strings instead breaks
  `MergeIdentifiedDraftListValues` (`McpManagementService.cs:3266-3290`), which resolves each edited
  item's raw value by GUID — the merge would fall through to the safe display value and write a
  sentinel into `mcp.json`. Retire `Ctrl+↑/↓/←/→`; Tab and arrows cover it now.
- [ ] **Step 3: Verify** — `--filter "FullyQualifiedName~McpEditorForm"`

### Task 9: Transport-change warning and the Save gate

**Files:** Modify `src/Coda.Tui/Mcp/McpManagementService.cs`, `McpBrowserController.cs`
**Test:** Modify `tests/Coda.Tui.Tests/` MCP management tests, `McpEditorFormTests.cs`

- [ ] **Step 1: Write failing tests** — **invariant 3:** an Edit draft whose transport differs from
  the original produces a warning naming the fields that will be dropped; no warning when transport is
  unchanged; Save with warnings requires confirmation, and cancelling keeps the draft intact;
  **the idle-gate lease is not held across the confirmation**; commit re-validates against the
  preview `Revision` and fails cleanly if the config moved underneath.
- [ ] **Step 2: Implement** — add the warning producer alongside `CreateScopeWarnings` (`:1074`), and
  split `SaveEditorAsync` (`McpBrowserController.cs:~589-609`) into prepare → (release lease) →
  confirm → (re-acquire) → commit, mirroring the delete/reauth flow (`:~455-480`) but without
  holding the lease across a long human pause.
- [ ] **Step 3: Verify** — `--filter "FullyQualifiedName~McpManagement|FullyQualifiedName~McpEditorForm"`

---

## Phase 4 — The other browsers

### Task 10: Migrate Skills, Plugins, Schedule and Tasks

**Files:** Modify `Ui/Skills/*`, `Ui/Plugins/*`, `Ui/Schedule/*`, `Ui/Tasks/*`
**Test:** Modify the four overlay test files

- [ ] **Step 1: Write failing tests** — each browser lists through a `TableView` with the shared glyph
  and scheme vocabulary; **Skills, Plugins and Schedule scroll**, which they cannot do today
  (`SkillBrowserOverlay.cs:255-269` dumps every row); a status message no longer replaces the footer;
  Tasks' `ReleaseAttachment` still runs on teardown (`TaskBrowserOverlay.cs:165`).
- [ ] **Step 2: Implement** on `BrowserChrome`. Keep each browser's own status-area size — MCP's
  `Dim.Fill(2)` costs a body row and 24 rows is the test (and common) terminal height.
- [ ] **Step 3: Verify** — `--filter "FullyQualifiedName~SkillBrowser|FullyQualifiedName~PluginBrowser|FullyQualifiedName~ScheduleBrowser|FullyQualifiedName~TaskBrowser"`

### Task 11: Consistency pass

**Files:** Modify the five key maps; modify `ScheduleBrowserOverlay.cs` (which has no key map at all — `:146-176`)
**Test:** Create a table-driven test over all browsers

- [ ] **Step 1: Write failing tests** — per spec §10, **per view, not globally**: list view binds
  `Esc`/`q`, `↑`/`↓`, `k`/`j`, `PgUp`/`PgDn`, `Home`/`End`, `Enter`, `Space`, `r`, `/`; detail view
  binds `Esc`/`q` and the movement keys; filter mode routes keys to the buffer and **`Esc` exits
  filter before it closes the browser**; editor view treats printable runes as text. Every
  footer-advertised key is actually bound. Skills' `Space` reports "skills are frontmatter-driven"
  in the status row instead of doing nothing silently (`SkillBrowserOverlay.cs:200-202`).
- [ ] **Step 2: Implement**, including **D8**: Tasks' `r` (*dismiss*) moves to `d`, and `r` becomes
  *reload* everywhere. Give Schedule a real key map.
- [ ] **Step 3: Verify** — `--filter "FullyQualifiedName~KeyMap|FullyQualifiedName~BrowserConsistency"`

---

## Phase 5 — Model browser and wrap-up

### Task 12: A real model browser

**Files:** Create `Ui/Models/ModelBrowser*.cs`; modify `Commands/ModelCommand.cs`
**Test:** Create `tests/Coda.Tui.Tests/ModelBrowserTests.cs`

- [ ] **Step 1: Write failing tests** — a list longer than the viewport scrolls (the prompt overlay
  cannot — `PromptOverlay.cs:150-197`); `Home`/`End`/paging work; `/` filters and `Esc` exits filter
  first; the current model is marked; **the provenance line and built-in-fallback warning appear in
  the header** (today only the non-interactive transcript path shows them, `ModelCommand.cs:185-211`);
  reasoning levels are displayed; `/model <id>` still bypasses the browser; the Spectre path is
  unchanged for non-interactive hosts. **Invariant 5:** no `\u001b` in rendered output.
- [ ] **Step 2: Implement** on the shared foundation.
- [ ] **Step 3: Verify** — `--filter "FullyQualifiedName~ModelBrowser|FullyQualifiedName~ModelCommand"`

### Task 13: Documentation and version

- [ ] **Step 1: README** — document the browser key bindings (per view), the status glyph vocabulary,
  filter mode, and the `r`/`d` rebinding in Tasks as a breaking change.
- [ ] **Step 2: Version** — bump `version.json` build.

### Task 14: Review

- [ ] **Step 1** — full `Coda.Tui.Tests` and `Engine.Tests`; Release build with `-warnaserror`.
- [ ] **Step 2** — **manual visual check at 80×24 and at a narrow width**, because no automated test
  can confirm the browsers actually look right; this plan changes what the user sees on every screen.
- [ ] **Step 3** — exactly one review subagent over the whole branch; fix critical and important
  findings, defer minor and low.
- [ ] **Step 4** — commit, push, open a PR.

---

## Verification commands

```powershell
# Focused (preferred during implementation)
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~<TestClass>"

# Full suites (final phase only)
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj
dotnet test tests\Engine.Tests\Engine.Tests.csproj

# Release build, warnings as errors
dotnet build src\Coda.Tui\Coda.Tui.csproj -c Release -warnaserror --nologo
```
