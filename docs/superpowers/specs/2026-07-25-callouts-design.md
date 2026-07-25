# Callouts Design

**Date:** 2026-07-25
**Status:** Approved

## Goal

Render GitHub-style admonition callouts — `> [!NOTE]`, `> [!TIP]`, `> [!IMPORTANT]`, `> [!WARNING]`,
`> [!CAUTION]` — in the interactive TUI transcript with a glyph, a colored title, and a colored left bar,
instead of the current plain-blockquote rendering. This gives assistant markdown a familiar, copy-paste
compatible way to signal emphasis, and is the concrete mechanism for "agent semantic coloring": when the
model emits a callout, the theme colors it.

This spec **builds on the theme system** (`2026-07-25-theme-system-design.md`): callouts add five new theme
roles, one per type.

## Scope and architecture

The change is confined to transcript formatting plus five new theme roles. Two planes, same split as themes:

- **Content (provider-agnostic, serve-forwarded):** the `[!TYPE]` syntax is ordinary assistant markdown. It
  flows through serve unchanged; a serve client renders callouts itself.
- **Rendering (TUI-only):** detection in `TranscriptBlockFormatter`, glyph/label/color/left-bar projection,
  and the theme roles that color them.

Bounded components:

- `TranscriptBlockFormatter` owns callout detection and projection (extends the existing `QuoteBlock` case);
- `TranscriptRole` + `CodaTheme`/`TuiTheme` own the five callout roles and their colors;
- the incremental formatter owns deferred sealing so a partial callout never flickers.

## Detection

In `TranscriptBlockFormatter.AppendBlock`, the `QuoteBlock` case (currently renders inner blocks as plain
assistant text) gains a callout check:

- If the blockquote's **first line is exactly** `[!TYPE]` where `TYPE` is one of `NOTE`, `TIP`, `IMPORTANT`,
  `WARNING`, `CAUTION` (case-insensitive, optional surrounding whitespace), render it as a callout of that
  type.
- Otherwise, fall through to the existing plain-blockquote rendering.

This is faithful to GitHub: the marker must be alone on the first line. `[!NOTE] trailing text`, an unknown
`[!FOO]`, or a marker not on the first line are **not** callouts and render as plain blockquotes (no false
positives). The check inspects the first child `ParagraphBlock`'s leading inline text.

## Rendering

A detected callout renders as:

- **Title row:** `<glyph> LABEL` (e.g. `⚠ WARNING`) in the callout type's themed color, via its
  `TranscriptRole`.
- **Body rows:** every row prefixed with a colored left bar `│ ` (in the callout color); the body text uses
  the readable neutral `Assistant` role. The body is the remainder of the marker paragraph (its lines after
  the `[!TYPE]` line) plus any subsequent child blocks of the quote, wrapped to width under the bar (the bar
  prefix reduces the available text width, like the list indent logic).
- Block spacing around the callout matches the existing inter-block separation.

### Glyphs and labels

| Type | Glyph | ASCII fallback | Label |
|------|-------|----------------|-------|
| NOTE | `ℹ` | `i` | NOTE |
| TIP | `✦` | `*` | TIP |
| IMPORTANT | `‼` | `!!` | IMPORTANT |
| WARNING | `⚠` | `!` | WARNING |
| CAUTION | `⛔` | `x` | CAUTION |

The ASCII fallback is used when the driver cannot render the unicode glyph (the same capability gate the
theme uses for 16-color fallback). Glyphs are width-safe monochrome symbols (not emoji) consistent with the
`/context` category glyphs, so cell math stays exact.

## Theme roles (builds on the theme system)

Add five roles to `CodaTheme`/`TuiTheme`, each a `TuiThemeColor(TrueColor, Fallback)`:

- `CalloutNote`, `CalloutTip`, `CalloutImportant`, `CalloutWarning`, `CalloutCaution`.

Every built-in theme must define all five (enforced by the theme role-parity test). Default hue intent,
mapped into each theme's own palette (Warm Ember warms them, Cool cools them; Default uses its accent + the
standard semantic hues):

- Note → primary accent / blue
- Tip → green
- Important → accent / purple
- Warning → amber
- Caution → red

Exact RGB and 16-color fallbacks are tuned during implementation using the real TUI preview.

Add five matching `TranscriptRole` entries (`CalloutNote` … `CalloutCaution`) so the title and left-bar rows
resolve to the callout color while body rows stay `Assistant`.

**Ordering dependency:** this spec assumes the theme system's role infrastructure exists. If callouts are
implemented first, the five roles are added to today's `TuiTheme` and migrated into `CodaTheme` when the
theme system lands. Preferred order: theme system first.

## Streaming

Callouts must not flicker while the assistant response streams. The incremental markdown formatter (WS3)
already defers "sealing" blocks whose meaning can still change (code blocks, reference links/definitions).
Blockquotes — and therefore callouts — join that deferred set: a quote block is not sealed as a finished
callout until it terminates (a blank line or end of the streamed region). While unsealed, the tail
re-renders from the current parse state on each refresh, exactly as the incremental assistant tail already
does, so a partial `> [!WARN` never paints a broken title.

## Parity / serve

No serve surface. Callout markdown is part of the assistant message content and is forwarded verbatim; the
rendering (glyph, color, bar) is TUI-local, exactly like theme selection. A downstream serve client renders
callouts however it wishes.

## Error handling

- Unknown `[!FOO]` or a marker with trailing text on the first line → not a callout; plain blockquote.
- Marker-only callout (no body) → title row alone, no bar rows.
- Nested callouts or a callout inside a list item → rendered as a plain blockquote in v1 (top-level
  blockquote callouts only).

## Testing

- **Detection:** each of the five types recognized (case-insensitive, optional whitespace); unknown type →
  plain quote; marker with trailing text → plain quote; marker not on the first line → plain quote; empty
  body handled.
- **Rendering:** title row glyph + label + callout role; body rows carry the `│ ` bar and neutral role;
  wrapping under the bar; ASCII glyph fallback; 16-color fallback.
- **Theme parity:** every built-in defines all five callout roles (extends the theme role-parity test).
- **Streaming:** a partial callout never renders a broken title and completes correctly once terminated
  (incremental formatter test).
- **Serve:** callout markdown passes through unchanged (no special handling on the wire).

## Out of scope (v1)

- A system-prompt nudge encouraging the model to use callouts (fast-follow if they're underused).
- Routing Coda's own internal warnings/notifications through the callout renderer.
- Nested callouts and callouts inside list items.
- Callout types beyond the five GitHub types.
