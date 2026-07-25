# Theme System Design

**Date:** 2026-07-25
**Status:** Approved

## Goal

Let users choose the TUI color scheme. Today the interactive shell has a single immutable "Warm Ember"
palette (`TuiTheme.WarmEmber`) hard-referenced by ~10 views, and the non-interactive console output has a
separate static Spectre `Theme`. This feature turns theming into a **selectable, live-switchable system**
with a neutral default, while keeping Warm Ember as an option, and makes one selection drive **both**
rendering layers so the interactive shell and slash-command output stay consistent.

This is the **foundation** for the callouts spec that follows: callouts add per-type roles to the theme and
render them with themed colors.

## Scope and architecture

Two rendering layers are unified under one selection:

- **Interactive shell** — `TuiTheme` (Terminal.Gui semantic roles) already exists as a rich palette.
- **Console/slash output** — the Spectre `Theme` helpers (accent, dim, success, warn, error).

A `CodaTheme` is a named palette that projects into **both**: it carries a `TuiTheme` instance and a small
Spectre color set. A registry owns the built-ins and a swappable `Current`; switching raises a `Changed`
event that the shell uses to rebuild schemes and that the Spectre helpers read live.

Bounded components:

- `CodaTheme` owns one palette's projection into the two layers;
- `CodaThemes` registry owns the built-ins, `Current`, and the `Changed` event;
- the shell owns scheme rebuild on `Changed`;
- the `/theme` command owns selection UX and persistence;
- settings own the persisted `theme` key.

Theming is a **TUI + console rendering concern only** — no serve surface (a serve client themes itself),
exactly like display verbosity.

## The unified theme model

```
CodaTheme
  Name        : string          // stable id used in settings + /theme
  DisplayName : string          // human label in the picker
  Tui         : TuiTheme        // Terminal.Gui semantic roles (existing shape)
  Console     : ConsolePalette  // accent, dim, success, warn, error (hex for Spectre markup)
```

`TuiTheme` keeps its current shape (roles + `Resolve`/`Attribute`/scheme builders) but is no longer a private
singleton — each built-in constructs one. `ConsolePalette` is a small record of Spectre-compatible colors
consumed by the console `Theme` helpers.

Every built-in must define **every** role. A parity test enforces this so no theme can ship missing a role
(extends the existing `TuiThemeTests`).

## Registry and live switching

```
static class CodaThemes
  IReadOnlyList<CodaTheme> All        // the built-ins
  CodaTheme Current                   // swappable; defaults to Default
  event Action Changed                // raised after Current changes
  bool TryGet(string name, out CodaTheme)
  void Set(CodaTheme)                 // sets Current + raises Changed
```

- Consumers change `TuiTheme.WarmEmber` → `CodaThemes.Current.Tui`. This is the bulk of the refactor: a
  mechanical replacement across the ~10 views (shells, overlays, transcript, composer, status, completion,
  scrollbar).
- The shell subscribes to `Changed` and rebuilds the schemes it applies (`SurfaceScheme`, `ComposerScheme`,
  `PromptScheme`, scrollbar/jump attributes) from the new `Current.Tui`, then triggers a redraw.
- The Spectre `Theme` static helpers become properties that read `CodaThemes.Current.Console` (accent, dim,
  success, warn, error), so slash-command output re-themes on switch with no per-call plumbing.
- Single-process TUI makes the swappable static safe; tests set and reset `Current` around each case.

## Built-in themes (3)

1. **Default** (new; the default). Neutral white-ish foreground for assistant text and most chrome, with
   **~3 accents by message nature**: one primary accent hue (user message, prompt glyph, headings,
   selection/scrollbar), plus semantic **red = error/approval** and **amber = warning/question**; success
   green where already used. Most of the screen is neutral; color carries meaning. Backgrounds stay
   near-black with the existing subtle user/composer panel differentiation.
2. **Warm Ember** (existing palette, preserved exactly) — now one selectable option rather than the only one.
3. **Cool/Dark** (new) — a cool-hued alternative (blue/teal/slate accents) over a dark background, degrading
   cleanly to 16 colors like the others.

Exact RGB values and 16-color fallbacks are tuned during implementation using the real TUI preview; this
spec fixes the **role set and accent policy**, not final hex. Each theme reuses the existing
`TuiThemeColor(TrueColor, Fallback)` mechanism.

## `/theme` command

Register a `/theme` slash command.

- **Interactive shell, bare `/theme`** — opens a **live-preview picker** (reusing the existing picker/overlay
  infrastructure): a list of `DisplayName`s. Moving the selection calls `CodaThemes.Set(candidate)` so the
  whole TUI re-themes **instantly**. **Enter** commits and persists; **Esc** reverts to the theme that was
  active when the picker opened (restore the captured `Current`, no persistence).
- **`/theme <name>`** — switches directly to a named theme and persists (scriptable; also the form used in
  non-interactive shells). Unknown name → a warning listing valid names; `Current` unchanged.
- **Non-interactive (plain/Spectre/legacy)** — bare `/theme` prints the list with the current one marked;
  `/theme <name>` sets by name. No live picker.

## Persistence

A `theme` key in `settings.json`, resolved at startup with the same pattern as `toolDisplayMode`:

- missing/blank → **Default**, valid (no warning);
- a recognized name → that theme;
- an unrecognized non-blank value → **Default** + a one-time invalid-value warning.

`/theme` commit and `/theme <name>` write the key. The resolver is a pure, separately tested function.

## Error handling

- Invalid persisted value → Default + warning (resolver).
- Esc in the live picker → restore the pre-open `Current` (captured on open); no settings write.
- `Changed` fired mid-frame → the shell rebuilds schemes and redraws; role reads happen at
  scheme-build/attribute time so a switch never leaves a view on a stale scheme.
- A theme missing a role is impossible by construction and is guarded by the parity test.

## Testing

- **Registry/resolver:** built-ins present; `TryGet`/`Set`/`Changed`; settings round-trip; invalid-value
  fallback + warning.
- **Role parity:** every built-in defines every `TuiTheme` role and every `ConsolePalette` field (extends
  `TuiThemeTests`).
- **Live switch:** `Set` raises `Changed`; the shell rebuilds schemes from the new `Current` (a shell test
  asserting the applied scheme's attributes change with the theme).
- **`/theme` command:** picker commit persists; Esc reverts without persisting; direct `/theme <name>`;
  unknown name warns; non-interactive list/set.
- **Spectre integration:** the console `Theme` helpers emit the current theme's accent/dim/etc. after a
  switch.

## Out of scope (v1)

- User-defined custom themes / per-role overrides in `settings.json` (future; the registry stays open to it).
- Callouts (separate spec, built on this theme's roles).
- Per-provider or per-mode automatic theme switching.
