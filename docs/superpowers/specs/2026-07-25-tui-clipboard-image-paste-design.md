# TUI Clipboard Image Paste — Design

*Date: 2026-07-25*
*Status: proposed*

## Goal

Let a user paste an image directly from the OS clipboard into the interactive TUI composer (normal
`Ctrl+V` / paste gesture) and have it attached to the next message as a real **vision content block** —
matching Claude CLI / Copilot CLI / OpenCode. Cross-platform: Windows, macOS, Linux.

## Why (mechanism, not convenience)

A model only "sees" an image when it is sent as a base64 **content block** in the message
(`ImageBlock` → Anthropic `image` / OpenAI `input_image`). Coda has **no image-reading tool**, so a
file path in the text would be read as binary garbage — the path workaround does not work. Direct paste
is the *only* working vision path, and matches the harnesses the user referenced (all embed base64
content, none reference a saved file). The entire downstream already exists, so this is almost entirely a
TUI front-end addition.

## Current state (what already works)

- **Message model:** `ImageBlock(string MediaType, string Base64Data)` — `src/LlmClient/ContentBlock.cs:36`.
  Serialized by Anthropic (`AnthropicMessagesClient.cs:468`) and OpenAI Responses
  (`OpenAiResponsesRequest.cs:85`); the OpenAI chat-completions shape degrades to a text placeholder
  `[image attached: {mime}]` (`OpenAiRequest.cs:135-145`).
- **Staging + turn wiring:** `SessionState.PendingImages` (`src/Coda.Tui/Repl/SessionState.cs:74`);
  `AgentRunner.RunTurnAsync` drains them into a multimodal turn and clears on success
  (`src/Coda.Tui/Agent/AgentRunner.cs:293-306`).
- **File ingestion:** `/image <path>` slash command reads a file, base64-encodes, validates, and stages
  an `ImageBlock` (`src/Coda.Tui/Commands/ImageCommand.cs`).
- **Serve ingestion (parity baseline):** `session/prompt` accepts `PromptParams { Text, Images: WireImage[] }`
  (base64 + mime), validates (png/jpeg/gif/webp, ≤5 MB), builds `ImageBlock`s + a trailing `TextBlock`
  (`src/Coda.Sdk/Serve/ServeHost.cs:375-403`).

## What is missing (all TUI front-end)

1. An OS clipboard **image** reader — Terminal.Gui's clipboard is text-only
   (`ClipboardReadResult` carries only `Text`, `src/Coda.Tui/Ui/Shells/ClipboardReadResult.cs`).
2. Composer handling of a non-text (image) paste and its on-screen representation.
3. Wiring the decoded image into staging so it rides the existing multimodal turn.

## Design decisions (from brainstorm)

- **Platforms:** Windows + macOS + Linux in v1.
- **Trigger:** reuse the normal paste (`Ctrl+V` / paste gesture) and **auto-detect** — if the clipboard
  holds an image, attach it; otherwise paste text exactly as today. Prefer the image when both are present.
- **Representation:** an inline **`[Image N]` label token** inserted into the draft at the caret
  (OpenCode-style). **Markers-only semantics:** on send, images go as ordered content blocks and the
  `[Image N]` label text is **kept** in the message (cheap multi-image correlation; Anthropic recommends
  labeling multi-image messages). No interleaving, no protocol change — TUI and serve produce the same
  flat *images + text* structure.

## Non-goals (YAGNI for v1)

- Drag-and-drop and `@path` attachment (the `/image <path>` command already covers file input).
- Position-honoring interleaving of images within the text (kept flat, matching serve).
- An agent-side image-read tool.
- Rendering the image inline in the terminal (not possible; we show a text label).
- Any change to the serve JSON-RPC protocol (it already ingests images).

## Components

### 1. Clipboard image reader (new) — `Coda.Tui`

- Value type `ClipboardImage(string MediaType, string Base64Data, int ByteLength)`.
- Seam `IClipboardImageReader { ClipboardImage? TryRead(); }` — **never throws**; returns `null` on
  absence, unsupported content, tool-missing, or timeout. Bounded child-process timeout (it shells out).
- Per-OS implementations returning PNG base64:
  - **Windows:** `powershell -sta -Command` running `[Windows.Forms.Clipboard]::GetImage()` → save to a
    `MemoryStream` as PNG → `[Convert]::ToBase64String`.
  - **macOS:** `osascript` reading `the clipboard as «class PNGf»` → temp `.png` → base64.
  - **Linux:** try Wayland `wl-paste -t image/png`, then X11 `xclip -selection clipboard -t image/png -o`;
    base64 the bytes. No tool present ⇒ `null`.
- A selector chooses the implementation via `OperatingSystem.IsWindows()/IsMacOS()/IsLinux()`. The real
  reader is constructed only in the composition root; the shell depends on the injected `IClipboardImageReader`
  so tests use a fake. Process execution is itself behind a small injected runner so per-OS command
  construction is unit-testable without a real clipboard.

### 2. Paste interception — `Coda.Tui` (shell/composer)

- Extend the clipboard-paste entry point (`TerminalGuiShellBase.PasteComposerClipboard()` and the composer
  paste gesture) to **first** call `IClipboardImageReader.TryRead()`:
  - image present and valid ⇒ stage it (component 3), insert its `[Image N]` token into the draft, and pin
    a status (e.g. `🖼 image attached · PNG 120 KB`);
  - otherwise ⇒ the existing text-paste path, unchanged.
- Both paste triggers (the `Ctrl+V` key path and the mouse paste gesture) route through this check.
- **Limitation (documented):** image paste requires the app to receive the paste action and read the OS
  clipboard; a terminal that intercepts paste and forwards only text cannot deliver images. On Windows
  Terminal and similar, `Ctrl+V` reaches the app.

### 3. Attachment staging + labels — `Coda.Tui` (`SessionState`)

- Track labels: a per-pending-batch ordered set of `(int Label, ImageBlock)`; the label counter starts at
  1, increments per staged image, and **resets when staging clears** (a successful send), so each message
  starts at `[Image 1]`.
- Token format: the literal `[Image N]` inserted into the draft.
- **Unify `/image <path>`** onto the same model (it also inserts a `[Image N]` token and stages with a
  label) so both ingestion paths behave identically.
- **On submit** (`AgentRunner.RunTurnAsync`):
  - Scan the final draft for `[Image N]` tokens (`\[Image (\d+)\]`).
  - **Tolerant:** only staged images whose token is still present are attached; a deleted token drops its
    image. Order = token order in the draft (so image order matches text order).
  - Build content: the ordered `ImageBlock`s followed by the `TextBlock` (label text **kept**) — the same
    flat *images + text* shape serve produces.
  - Clear staging and reset the label counter on success (existing clear-on-success policy).

### 4. Shared validation — targeted consolidation

- Extract the validation currently duplicated in `ImageCommand` and `ServeHost` (MIME allow-list
  `png|jpeg|gif|webp`, ≤5 MB, valid base64) into a shared `ImageAttachmentValidation` helper used by all
  three ingestion paths (paste, `/image`, serve). The clipboard reader emits PNG, so it passes; oversize or
  unsupported ⇒ rejected with a status notice, no attachment.

## Data flow

```
Ctrl+V / paste gesture
  → IClipboardImageReader.TryRead()
      → image?  ── no ──▶ existing text paste (unchanged)
        │ yes
        ▼
      validate (shared) ── invalid ──▶ status notice, no attach
        │ valid
        ▼
      stage (Label N, ImageBlock) + insert "[Image N]" into draft + status
  … user edits / types …
  → submit → scan draft tokens → collect staged images in token order
      → content = [ImageBlock…] + TextBlock(text incl. labels)
      → multimodal turn → provider serialization → clear staging on success
```

## Parity (TUI ↔ serve)

- Serve already ingests images via `session/prompt` (base64 `WireImage`) into the same flat *images + text*
  structure. The clipboard reader, paste interception, and `[Image N]` UX are TUI-only affordances (serve
  clients already supply base64 directly). The **message structure is identical in both modes**; shared
  validation guarantees identical limits. No serve protocol change is required.

## Error handling / edge cases

- No image on clipboard ⇒ normal text paste.
- Unsupported type / too large ⇒ status notice, no attachment.
- Reader failure / missing Linux tools / timeout ⇒ treated as "no image" ⇒ text paste (optional one-time notice).
- Both text and image on the clipboard ⇒ prefer the image.
- Deleted `[Image N]` token ⇒ that image is dropped on send (tolerant scan).
- A user-typed literal `[Image 3]` with no matching staged image ⇒ no attachment (harmless literal text).
- Active model without vision (Copilot chat-completions) ⇒ existing `[image attached: {mime}]` degradation;
  optional warning when staging on a non-vision model.
- `--no-mouse` ⇒ `Ctrl+V` paste still works; the mouse paste gesture is unavailable (expected).

## Testing

- **Pure/unit:**
  - Per-OS command construction (mock process runner: assert the command/args per OS; parse base64 output).
    No real clipboard.
  - Staging/label logic and tolerant submit-time token scan (ordering, drop-on-delete, reset-on-clear).
  - Shared validation (MIME / size / base64).
- **Integration:**
  - Composer paste branch with an injected fake reader: image ⇒ staged + `[Image N]` inserted + status;
    none ⇒ text paste unchanged.
  - Turn building: draft with tokens + staged images ⇒ ordered `ImageBlock`s + text with labels; deleted
    token ⇒ image dropped.
- Follow existing `Coda.Tui.Tests` / `Engine.Tests` patterns.

## Rollout

- Additive and unflagged: text paste is unchanged whenever the clipboard has no image. Linux gracefully
  no-ops when `wl-paste`/`xclip` are absent.
