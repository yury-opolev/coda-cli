# Clickable Links Design

**Date:** 2026-07-25
**Status:** Approved

## Goal

Make URLs in assistant output actionable in the interactive TUI: left-click opens a link in the OS browser,
right-click opens a small context menu (copy / open / open-in-private-window), and links that hide their
destination are visibly marked. Today markdown links render as plain text with the URL dropped, and raw
OSC 8 hyperlink escapes are stripped for anti-spoofing, so nothing is clickable.

Terminal.Gui's cell-based driver has no inline OSC 8 hyperlink support, so — unlike terminals that render
OSC 8 emitted by other CLIs — Coda implements link interaction itself, which also gives full control over
security and the context menu.

## Scope and architecture

The change is confined to the interactive TUI transcript: link extraction in the formatter, a `Link` (and a
deceptive-link) theme role, and click/right-click interaction in the transcript view. It builds on existing
seams: mouse hit-testing (`ToTranscriptPosition`), the native context-menu primitive already used by the
composer, the clipboard writer, and the theme system's roles.

Two planes, same split as themes and callouts:

- **Content (serve-forwarded):** URLs are ordinary assistant markdown; serve forwards them verbatim and a
  serve client handles links itself. No serve surface.
- **Interaction (TUI-only):** extraction, styling, click-to-open, and the context menu.

Bounded components:

- `TranscriptBlockFormatter` owns link extraction and span metadata;
- `TranscriptRenderLine` carries link spans;
- the transcript view owns hit-testing, click-to-open, and the context menu;
- `IUrlOpener` and `IPrivateBrowserResolver` own the OS integration (injectable seams);
- `CodaTheme`/`TuiTheme` own the `Link` and deceptive-link roles.

## Link extraction (formatter)

- Enable `UseAutoLinks()` on the Markdig pipeline (currently a bare `MarkdownPipelineBuilder().Build()`), so
  bare `https://…`/`http://…` URLs parse as link nodes in addition to markdown `[text](url)`.
- Extend inline rendering (the `LinkInline` case, which today renders the text and drops the URL): continue
  rendering the display text, but record the URL and the rendered column span.
- `TranscriptRenderLine` gains an optional `IReadOnlyList<LinkSpan>` where
  `LinkSpan(int StartColumn, int EndColumn, string Url, bool TextMatchesUrl)`.
- Wrapping: a link whose text wraps across multiple render lines produces **one `LinkSpan` per render line**,
  all carrying the same URL. The wrap path (`AppendWrapped`/`WrapLine`) must thread link metadata through so
  each produced line gets the sub-span that falls on it. Clicking any segment opens the same URL.
- `TextMatchesUrl` is computed at extraction: true for a bare URL (autolink), or when the display text,
  trimmed and case-insensitively, equals the URL or equals the URL's host/authority. False otherwise
  (a "deceptive" link whose visible text hides its destination).

## Styling (theme roles)

- New `Link` theme role — a distinct color per theme (accent/blue family), applied to honest link spans.
- New deceptive-link treatment — reuses the theme's caution hue plus a trailing caution glyph `⚠`
  (width-safe, ASCII fallback `!`) appended after a deceptive link's text, so a hidden destination is
  visible at a glance without opening anything.
- Underline is applied to link spans where the driver supports the text style; color alone carries the
  affordance otherwise.
- Link spans are painted by slicing each row into sub-segments and applying the link attribute, reusing the
  same segment-slicing draw path the selection highlight already uses. Both roles are defined in every
  built-in theme (enforced by the theme role-parity test).

## Interaction (transcript view)

Hit-testing reuses `ToTranscriptPosition(mouse)` to map a pointer event to `(GlobalRow, Column)`, then tests
the row's `LinkSpan`s. A link hit takes precedence over the existing expansion-toggle within its span; a
pointer event outside every span keeps today's behavior (selection, expansion, scrollbar).

- **Left-click** (released without a drag, no active selection) on a link:
  - honest (`TextMatchesUrl`) → open immediately;
  - deceptive → a confirmation overlay showing the real URL (reusing the existing prompt infra); open on
    confirm.
- **Right-click** on a link → a small **context menu anchored at the pointer** (the native context-menu
  primitive already used by the composer via `ShowComposerContextMenu(screenPosition)`; it is a compact
  floating popup over only the cells it needs, dismissed on selection or click-away — **not** a full-screen
  overlay). Contents:
  - a disabled header showing the destination URL (truncated to width);
  - **Copy link** — writes the URL through the existing clipboard writer seam
    (`app.Clipboard.TrySetClipboardData`) with the standard `ClipboardStatusText` status
    ("… copied to clipboard" / "Clipboard unavailable");
  - **Open** — opens the URL; no extra deceptive confirmation here because the header already shows the true
    destination;
  - **Open in private window** — present **only** when `IPrivateBrowserResolver` resolves a private-capable
    browser; hidden otherwise.

Right-click outside any link span opens no menu (existing behavior).

## OS integration (seams)

- **`IUrlOpener`** — opens an http/https URL in the OS default browser via
  `Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })` (no shell string, so no
  command injection). Injectable so tests never launch a real browser.
- **`IPrivateBrowserResolver`** — best-effort detection of a private-capable browser by probing known install
  locations per platform (on Windows, registry `App Paths` / standard `Program Files` locations for
  Chrome, Edge, Firefox, Brave). Returns the first match with its private-mode flag
  (`--incognito` / `--inprivate` / `-private-window` / `--incognito`), or `null` when none is found (→ the
  private-window menu item is hidden). No user setting; injectable for tests. The private launch uses the
  resolved executable path plus the flag and the URL as a **separate argument** (never a shell string).

## Security

- **http/https only.** Every open path (left-click, menu Open, menu private-open) validates the URL with
  `Uri.TryCreate(..., UriKind.Absolute)` and an `http`/`https` scheme check before launching; anything else
  is refused with a brief notice.
- No shell interpolation anywhere — `UseShellExecute` with the URL as `FileName` for the default browser, and
  an explicit exe path + argument list for the private launch.
- Deceptive links are surfaced both visually (marker) and at open time (confirmation), mitigating link-text
  spoofing from model output.
- URLs originate from Markdig's parsed link nodes (already sanitized of control characters by the transcript
  sanitizer); malformed URLs fail the `Uri.TryCreate` check and are not opened.

## Parity / serve

TUI-only interaction. Serve forwards assistant markdown (URLs intact); downstream clients render and handle
links themselves. No serve methods or events. (OSC 8 emission for the plain/pipe non-interactive renderer is
a possible future and is out of scope.)

## Error handling

- Non-http(s) or malformed URL → not opened; brief status notice.
- Browser or private-browser launch failure (`Process.Start` throws) → swallowed with a brief error notice;
  never crashes the TUI.
- Clipboard unavailable on Copy → the standard "Clipboard unavailable" warning (existing behavior).
- A link wrapped across lines → every sub-span opens the same URL.
- Pointer event outside any link span → existing selection/expansion/scrollbar behavior.

## Testing

- **Extraction:** markdown link URL + span captured; bare URL autolinked; a link wrapped across lines yields
  multiple sub-spans sharing one URL; `TextMatchesUrl` classification (bare URL, text==url, text==host,
  deceptive).
- **Styling:** honest link drawn in the `Link` attribute; deceptive link drawn with the caution treatment +
  trailing glyph (and ASCII fallback); link segments sliced correctly alongside a selection highlight; theme
  role-parity includes the new roles.
- **Interaction:** left-click honest opens; left-click deceptive confirms then opens; right-click opens a
  compact anchored menu (not full-screen) with the correct items; menu **Open in private window** hidden when
  the resolver returns null and shown when it resolves; **Copy link** writes the URL and reports status;
  precedence over expansion-toggle; click/right-click outside a span falls through.
- **Security:** non-http(s) and malformed URLs refused on every open path; `IUrlOpener` and
  `IPrivateBrowserResolver` seams mocked so no real browser launches; private launch passes the URL as a
  separate argument.
- **Serve:** assistant markdown with links passes through unchanged (no special handling on the wire).

## Out of scope (v1)

- Keyboard link-picker (fast-follow).
- OSC 8 emission for the plain/pipe (non-interactive) renderer.
- `owner/repo#123` → GitHub URL resolution.
- Hover URL preview.
- A user-configurable private-browser command, or non-default default-browser selection.
- Non-http schemes (mailto, file, …).
