# Persisted per-profile colour theme

## Goal

A colour theme chosen in Coda is remembered in the profile's own
`settings.json` (`<CODA_HOME>/.coda/settings.json`, default `~/.coda`) and is
used the next time Coda starts. Two profiles (two `CODA_HOME`s) can therefore
open with different colours.

## Problems today

1. Both binaries start with `Theme::default()` and never read the saved
   `theme` key (`rust/crates/coda/src/main.rs:456`,
   `rust/crates/coda-tui/src/main.rs:35`).
2. The `/settings` form offers `warm-ember`, `cool-slate`, `high-contrast`
   (`rust/crates/coda-tui/src/surface/settings.rs:22`), but the renderer only
   knows `warm-ember` and `cool-dark` (`coda_render::Theme::names()`), so the
   form can save names nothing can render and cannot offer `cool-dark`.
3. Saving the form writes `theme` but does not change the running UI.
4. `/theme <name>` changes the running UI only; it is never saved.

## Design

- **One catalogue.** `coda_render::Theme::names()` / `Theme::by_name()` are
  the only list of themes. The settings form derives its options from them.
- **Startup.** A single library function in `coda-tui` resolves the startup
  theme from the profile's settings, used by both binaries, before the
  preflight/setup screen so even the first-run wizard uses the saved theme.
  Missing file / missing key → default theme, silently. Unknown name or
  unreadable/invalid settings → default theme plus a one-line warning printed
  to stderr before the terminal is taken over (never a failed launch).
- **Live apply.** Saving the settings form applies the chosen theme to the
  running UI. `/theme <name>` applies it and persists it as a client-local
  setting (not gated by access mode, same as the form's appearance half);
  if saving fails the session still switches and the user is told it was not
  saved. The canonical name (`Theme::name`, e.g. `default` → `warm-ember`) is
  what is written.
- Colour depth (`ColorDepth`) is always preserved when the theme changes.

## Tasks

1. Theme catalogue for the settings form.
2. Resolve the startup theme from the profile's settings (both binaries).
3. Apply theme changes live and persist `/theme`.
4. Documentation.

Test commands (from `rust/`): `cargo test -p coda-tui -p coda-render`,
`cargo build -p coda`. Zero new warnings.
