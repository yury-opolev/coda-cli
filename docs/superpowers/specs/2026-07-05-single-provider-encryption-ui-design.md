# Single-Provider Model + Windows Credential Encryption + Startup Banner — Design

**Date:** 2026-07-05
**Repo:** coda-cli (github.com/yury-opolev/coda-cli)
**Branch:** `feat/single-provider-encryption-ui`
**Status:** Approved (brainstorming complete)

## Problem

Three related issues, surfaced while debugging a cortex coding-relay failure ("missing
Anthropic API key" even though GitHub Copilot was authenticated):

1. **Provider divergence (root of the reported bug).** coda separates "connected" (which
   `*.cred` files exist) from "default" (`~/.coda/settings.json` `defaultProvider`). The
   default is an independent pointer that can name a provider with **no credential**. The user
   had `defaultProvider = anthropic-api-key` (no credential) while only `github-copilot` was
   authenticated → every session started on `anthropic-api-key` → `CredentialNotFoundException`.
   `LoginCommand.PersistDefaultProvider` is best-effort and **silently swallows** write
   failures, so the default can stay stale after a login.

2. **Windows credential encryption gap.** The AES-256-GCM `FileTokenStore` stores its 256-bit
   key in `key.bin` next to the ciphertext. On Windows, `SetOwnerOnly` and dir-permission
   restriction are **Unix-only** — the key sits unprotected with default ACLs, readable by
   anything running as the user. (The older shipped build v0.1.53 used DPAPI, which is stronger
   on Windows; the AES-GCM build would be a regression there.)

3. **No visibility.** The TUI does not show the active provider, model, or coda version at
   startup, so a wrong provider is invisible until a call fails.

## Approved decisions

1. **Single-provider model** — eliminate the separate "default provider" concept. Exactly one
   connected provider exists at a time; it *is* the active one. (User: "There must not be a
   separate concept for default and connected provider — only one provider connection must
   exist.")
2. **Encryption** — DPAPI-wrap the AES key on Windows + ACL-lock the credentials dir/files to
   the current user. Keep AES-GCM as the cross-platform path.
3. **UI** — the startup banner already shows version + cwd; **add the connected provider +
   model** to it (`coda --version` already exists — no change needed there).
4. **Bridge interaction** — `coda serve` tolerates a stale/unusable `--provider` by falling
   back to the single connection and warning (**option a, now**). Removing the cortex Bridge's
   provider-resolution and `--provider` passing is a **follow-up** (option b, deferred).
5. **Delivery** — implement here, rebuild + reinstall the host `coda` dotnet tool (v0.1.53 →
   new build; one-time re-login as the credential format changes), then bump cortex's
   `lib/coda-cli` submodule and redeploy cortex.

## Design

### 1. Single-provider model

- **Enforce a single credential.** `coda auth login <X>` (and `/provider <X>`, which becomes
  "connect to X") deletes every other `*.cred` before writing X's, so exactly one credential
  ever exists in `~/.coda/credentials/`.
- **Active provider is derived from that one credential.** `settings.json` `defaultProvider`
  is **retired as an independent selector**: it is no longer read to choose the provider. (Left
  tolerated-but-ignored in the file for back-compat; not written going forward.) `defaultModel`
  is unchanged (model is a separate axis).
- **Provider resolution** (`ProviderModelResolver`): `--provider` flag → else the single
  connected provider (from the credential store) → else a clear `ProviderModelNotConfigured`
  error ("Not signed in. Run `coda auth login`."). No settings-file provider lookup.
- **Login-persist is non-silent.** A failure to write settings surfaces a visible warning
  rather than being swallowed.
- **`/provider`** with no args shows the single connected provider; `/provider <id>` connects
  to `<id>` (logging in, replacing the connection). `/login` unchanged except it enforces
  single-credential.

### 2. Bridge tolerance (`coda serve`)

When `coda serve` is launched with `--provider X`:
- If `X` has a credential → use `X` (unchanged).
- If `X` has **no** credential but exactly **one** provider is connected → use the connected
  provider and emit a warning log ("requested provider X has no credential; using <connected>").
- If no provider is connected → the existing clear "not signed in" error.

This makes the cortex relay work regardless of the stale `--provider` the Bridge currently
passes, without a cortex change in this effort.

### 3. Encryption (`FileTokenStore`)

- **`LoadOrCreateKey`**: on Windows, the 32-byte key is stored **DPAPI-wrapped**
  (`ProtectedData.Protect(key, optionalEntropy, DataProtectionScope.CurrentUser)`) and unwrapped
  on read (`Unprotect`). On non-Windows, the raw key with `0600` as today. A key file in the
  wrong format (e.g. legacy raw on Windows) is detected and re-wrapped, or regenerated if
  unreadable.
- **Windows ACLs**: the credentials directory, `key.bin`, and each `.cred` get a Windows branch
  that restricts access to the current user (`FileSecurity` with inheritance disabled), mirroring
  the Unix `0700/0600` intent.
- AES-GCM record format (nonce‖tag‖ciphertext) is unchanged.
- **Migration:** the new build cannot read a legacy v0.1.53 DPAPI `.cred`
  (different format) — `GetAsync` returns null on the format mismatch, so the user re-runs
  `coda auth login` once. Pre-existing multiple creds are reconciled to one on first login.

### 4. Startup banner

- **Banner** (`Banner.cs`) already renders the wordmark + `v{Branding.Version}` + cwd + hints.
  **Add** the **connected provider** (display name + id) and **model** to it. When no credential
  exists, a "not signed in — run `coda auth login`" line replaces the provider/model line.
- **`--version`/`-v` already exists** (`Program.cs` handles it as an immediate no-side-effect
  command before the TUI) — no change.

## Components touched

| Unit | Change |
|---|---|
| `FileTokenStore` | Windows DPAPI key-wrap + Windows ACLs; format-mismatch handling |
| `CredentialManager` / login flow | single-credential enforcement (delete others on login) |
| `LoginCommand` / `ProviderCommand` | non-silent persist; `/provider` = connect; single-cred |
| `ProviderModelResolver` / `SettingsLoader` | resolve provider from the single credential, not `defaultProvider` |
| `ServeHost` (serve provider handling) | tolerate unusable `--provider`, fall back + warn |
| `Banner` (+ its caller) | add connected provider + model to the existing version/cwd banner |

## Testing (TDD)

- `FileTokenStore`: AES-GCM round-trip; **Windows** — key.bin is DPAPI-wrapped (not raw),
  decrypts back, and a foreign/legacy key is handled; ACLs applied (assert restricted where
  testable).
- Single-credential enforcement: after login to B while A exists, only B's `.cred` remains.
- Provider resolution: with one credential and no `--provider`, resolves to that provider; with
  none, throws the not-signed-in error; `--provider` flag overrides.
- Serve fallback: `--provider` with no credential + one connected → uses connected + warns;
  none connected → error.
- Banner: includes the connected provider + model (alongside the existing version); renders
  "not signed in" when no credential.

## Delivery / rollout

1. Implement + green tests on `feat/single-provider-encryption-ui`; merge to coda `main`, push
   to github.
2. `dotnet pack` / rebuild the coda tool; `dotnet tool update` the host `coda` (v0.1.53 → new).
3. One-time `coda auth login` (Copilot) on the host (cred format change).
4. Bump cortex `lib/coda-cli` submodule to the new commit; rebuild + redeploy cortex.
5. Verify end-to-end: banner shows `github-copilot`; a cortex coding session runs on Copilot.

## Explicitly out of scope (follow-up)

- **Cortex Bridge simplification (option b):** remove `CodaModelSettingsStore` /
  `CodaMachineSettingsReader` provider resolution and stop passing `--provider` from
  `CodaServeArgsBuilder`; drop the Settings→Coding provider picker. Separate cortex-side effort.
