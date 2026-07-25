# Pending Indicator at Start of User Message — Design

*Date: 2026-07-25*
*Status: proposed*

## Goal

Render a queued/pending user message as **`[pending] …`** — the marker at the **start** of the message
(not a right-aligned annotation) and the whole pending message in a **dimmer** font — so it reads clearly
as not-yet-delivered. When the message is actually sent it renders normally (the dim `[pending]` line is
replaced by the normal user message), giving a clear pending → delivered transition.

## Current state

`AppendPendingUser` (`src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs:663-678`) renders the pending
user text with `TranscriptRole.User`, `FillWidth = true`, and a right-aligned **`pending`** annotation
(`RightText`) on the first wrapped line. So today the indicator sits on the right edge and the text uses the
normal (bright) user color.

## Change

1. **Drop** the `RightText = "pending"` right-annotation.
2. **Prefix** the first wrapped line's text with `[pending] `.
3. **Dim** the pending message: render its lines with a new `TranscriptRole.PendingUser` mapped to a dim
   user foreground on the same user fill-width background, so the whole pending block is muted until
   delivered.

## Components

- **`TranscriptRole`** (`src/Coda.Tui/Ui/Rendering`): add `PendingUser`.
- **`TranscriptBlockFormatter.AppendPendingUser`**: use `TranscriptRole.PendingUser`, prefix `[pending] `
  on the first line only, remove the `RightText` annotation. Keep `FillWidth = true` and existing wrapping.
- **`VirtualizedTranscriptView.AttributeFor`** (`:805, :826, :855`): map `PendingUser` → a dim user color
  (reuse the existing dim `theme.TranscriptUserTime` for now) and include `PendingUser` in the fill-width
  **user-background** branch (`role is TranscriptRole.User or TranscriptRole.PendingUser`).
- The attribute memo added earlier resolves the new role automatically.

## Decisions

- The **whole** pending message is dimmed (not just the `[pending]` marker), because the render model is one
  role per line; per-word coloring would need the mixed-attribute path used for selection and isn't worth it
  here. `[pending] ` appears on the **first line only**.
- **No new theme color** is introduced — we reuse the existing dim user color. When the theme system lands,
  `PendingUser` becomes a first-class themed role with its own palette entry.

## Parity (TUI ↔ serve)

Pure TUI transcript rendering. Serve clients render their own transcript, so there is **no serve change**.

## Testing

- `AppendPendingUser`: first line starts with `[pending] `; all pending lines use `TranscriptRole.PendingUser`;
  no `pending` right-annotation is emitted; multi-line pending messages carry the marker only on the first
  line. Update the existing `TranscriptBlockFormatterTests` that assert the old right annotation.
- `AttributeFor(PendingUser)` resolves to the dim color and the user fill-width background.
- Delivered messages (`UserTranscriptBlock`) render unchanged (normal user color + sent-time annotation).

## Non-goals

- No change to sent-message rendering or the sent-time annotation.
- No new theme palette entries until the theme feature (reuse the existing dim color for now).
