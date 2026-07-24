# CLI/TUI/MCP Holistic Review Evidence

## Scope, model, range, and methodology

- **Reviewer:** GPT-5.6 Sol (`gpt-5.6-sol`), maximum reasoning, read-only holistic code-review specialist.
- **Base:** `main` at `7069cef`.
- **Reviewed feature:** committed HEAD `e05c51c`, plus the then-uncommitted end-to-end/test-fixture changes in the review worktree.
- **References:** approved design spec `docs/superpowers/specs/2026-07-22-cli-tui-mcp-improvements-design.md` and integration plan `docs/superpowers/plans/2026-07-22-cli-tui-mcp-integration.md`.

The complete diff was inspected against the approved requirements, including exact startup prompt handling, transcript navigation, aggregated tool activity, MCP physical/effective precedence, secret transactions, runtime reconciliation, idle-turn safety, overlay behavior, and compatibility. Production paths and named regression tests were checked with file/line evidence. Each accepted review finding was converted to a failing regression, corrected in the owning code, and rerun before final Release verification.

## Verification and fixture-construction issue

Before review fixes, Release verification was clean after correcting an end-to-end fixture-construction issue:

- build: **0 warnings, 0 errors**;
- `Engine`: **2154/2154**;
- `Coda.Tui`: **1993/1993**;
- `LlmAuth`: **103/103**.

The fixture issue was test-only: dependencies were mutated after the stable service had been constructed, so the fixture did not represent the intended final composition. No production defect was present. The fixture was corrected before holistic review; the final fixture remains deterministic and in-process (`tests/Engine.Tests/CliTuiMcpEndToEndTests.cs:97-149`).

## Accepted findings and evidence

### 1. High — textual MCP mutations bypassed the managed-task idle lease

**Observed defect:** textual `/mcp` mutations could change configuration during a blocking managed task. The new regression was RED: **1 failed, 81 passed**.

**Current evidence:** `McpCommand.RenderManagedMutationAsync` acquires `TryAcquireIdleLease` and rejects mutations while tasks run (`src/Coda.Tui/Commands/McpCommand.cs:374-395`). The shared TUI gate composes the controller and task-manager leases (`src/Coda.Tui/Ui/TuiController.cs:254-307`), and overlay mutations use the same gate (`src/Coda.Tui/Ui/Mcp/McpBrowserController.cs:784-815`). The regression is `TuiControllerTests.Textual_mcp_mutation_rejects_managed_task_then_succeeds_after_completion` (`tests/Coda.Tui.Tests/TuiControllerTests.cs:154-202`); read/refresh paths remain available.

**GREEN:** Coda.Tui **82/82**; Engine idle-lease coverage **1/1**, including `TaskManagerIdleLeaseTests.Idle_lease_blocks_new_scheduled_registration_and_rejects_while_running` (`tests/Engine.Tests/Tasks/TaskManagerIdleLeaseTests.cs:10`).

**Accepted resolution:** all textual and interactive MCP mutations now require the managed-task idle lease; list/info/refresh remain readable while a task is active.

### 2. High — reauthentication could delete foreign non-MCP credentials

**Observed defect:** reauthentication cleanup could treat a foreign `coda-secret:` reference, including `llmauth:` credentials, as MCP-owned. RED coverage included foreign header and bearer-reference failures.

**Current evidence:** ownership is explicitly checked by `McpSecretStore.IsOwnedKey` (`src/Coda.Mcp/McpSecretStore.cs:49-74`). Secret migration and cleanup filter through that ownership predicate (`src/Coda.Tui/Mcp/McpManagementService.cs:2547-2560`, `2623-2657`, `2695-2699`). The regression `Managed_reauthentication_replaces_owned_field_without_claiming_or_deleting_foreign_reference` verifies both foreign-header and foreign-bearer cases (`tests/Coda.Tui.Tests/McpManagementAuthenticationTests.cs:90-133`).

**GREEN:** combined authentication/delete/edit filter **85/85** after the four expected RED failures.

**Accepted resolution:** only canonical or valid versioned keys owned by the selected MCP server field are migrated or deleted; foreign namespaces remain untouched.

### 3. Medium — stable disabled/overridden OAuth rows reported a runtime error

**Observed defect:** OAuth reauthentication conflated physical/config/URL stability with reconnect eligibility. Stable disabled and overridden rows incorrectly returned `SavedWithRuntimeError`. RED coverage included both cases.

**Current evidence:** OAuth revalidation checks revision, selected physical definition, OAuth mode, canonical resource, and effective-config stability (`src/Coda.Tui/Mcp/McpManagementService.cs:1616-1647`). Reconnect is requested only when the revalidated row is both effective and enabled (`src/Coda.Tui/Mcp/McpManagementService.cs:1642-1647`). The regressions are `OAuth_reauthentication_of_disabled_project_row_succeeds_without_reconnecting` (`tests/Coda.Tui.Tests/McpManagementAuthenticationTests.cs:178-200`) and `OAuth_reauthentication_of_overridden_user_row_succeeds_without_reconnecting` (`tests/Coda.Tui.Tests/McpManagementAuthenticationTests.cs:202-231`).

**GREEN:** combined authentication/delete/edit filter **85/85**.

**Accepted resolution:** stable physical/config/URL rows succeed; reconnect is conditional on effective-and-enabled runtime eligibility. Configuration changes during OAuth still produce the explicit saved-with-runtime-error result (`tests/Coda.Tui.Tests/McpManagementAuthenticationTests.cs:233-258`).

### 4. Medium — renaming an overridden enabled row left the newly-effective server disconnected

**Observed defect:** renaming an enabled overridden row to a unique name expected two connects but produced one. RED expected **2**, actual **1**.

**Current evidence:** commit logic detects a final definition becoming newly effective and passes force-start names to reconciliation (`src/Coda.Tui/Mcp/McpManagementService.cs:1237-1281`). Reconciliation starts force-start definitions even without prior runtime intent (`src/Coda.Tui/Mcp/McpManagementService.cs:2166-2227`). The regression is `Renaming_an_enabled_overridden_row_starts_its_newly_effective_definition` (`tests/Coda.Tui.Tests/McpManagementRuntimeTests.cs:54-83`).

**GREEN:** runtime/edit **72/72**.

**Accepted resolution:** the existing effective server remains connected, while the renamed enabled row that becomes effective under its new name is force-started.

## Finding disposition

- **Accepted findings:** 4.
- **Rejected findings:** none.
- **Unresolved findings:** none.

## Requirement-by-requirement final verdict

- **Exact prompts:** inline and file sources, mutual exclusion, exact empty/non-empty precedence, and source errors are covered by `SystemPromptSourceResolverTests` (`tests/Coda.Tui.Tests/SystemPromptSourceResolverTests.cs:8-227`) and `CliTuiMcpSpecComplianceTests.Exact_prompt_precedence_preserves_empty_and_nonempty_overrides` (`:179-203`). The end-to-end test verifies exact empty prompt propagation and two-batch activity coexistence (`tests/Engine.Tests/CliTuiMcpEndToEndTests.cs:23-60`). Persistence/resume/fork/bundle behavior remains covered by the existing session integration matrix.
- **Transcript navigation:** detached/following transitions, unseen reset, global `Ctrl+End`, reserved jump-row layout, and routed click behavior are covered by `TranscriptNavigationChromeTests` (`tests/Coda.Tui.Tests/TranscriptNavigationChromeTests.cs:13-114`) and the broader transcript/navigation suites. Stable activity replacement does not inflate unread state.
- **Summary display:** missing/blank values default to summary while explicit legacy modes remain unchanged (`tests/Coda.Tui.Tests/CliTuiMcpSpecComplianceTests.cs:26-68`). Root activity identity, multi-batch aggregation, source identity, terminal states, orphan handling, and stable replacement are covered by `ToolActivityReducerTests` (`tests/Coda.Tui.Tests/ToolActivityReducerTests.cs:10-314`) and the final end-to-end test (`tests/Engine.Tests/CliTuiMcpEndToEndTests.cs:39-55`). Secret and terminal-control redaction is covered by `CliTuiMcpSpecComplianceTests.Management_and_activity_outputs_redact_secrets_and_terminal_controls` (`:240-270`).
- **MCP manager:** exact bare `/mcp` interception with textual fallback, physical user/project rows, disabled precedence, confirmations, shared management operations, secret ownership, busy-turn safety, edit/rename, immediate effective runtime reconciliation, OAuth/stored/environment/no-auth paths, delete reveal behavior, and runtime error reporting are covered by the compliance, browser, command, read, edit, runtime, delete, and authentication suites. The shared service owns validation, atomic writes, secret handling, reconciliation, and refresh; UI code does not write MCP JSON or credentials directly.
- **Compatibility and maintainability:** existing textual subcommands remain available; only exact bare `/mcp` opens the TUI overlay (`CliTuiMcpSpecComplianceTests.Mcp_interception_is_exact_and_other_forms_remain_textual`, `tests/Coda.Tui.Tests/CliTuiMcpSpecComplianceTests.cs:70-96`). The final end-to-end fixture exercises the normal session, sink, reducer, and MCP management composition without external network, browser, process, or credential-store dependencies (`tests/Engine.Tests/CliTuiMcpEndToEndTests.cs:97-149`).
- **Residual risks:** deterministic tests do not establish availability of external OAuth providers or remote MCP endpoints; those are operational/environmental concerns, not unresolved code findings. No publishing, version-bump, or merge conclusion is made by this review.

Post-fix Release verification: build **0 warnings, 0 errors**; `Engine` **2154/2154**; `Coda.Tui` **1999/1999**; `LlmAuth` **103/103**; **4256 total**.

Verdict: COMPLIANT — all approved semantics are implemented, the complete Release build/test suite passes after holistic review, and no unresolved high-confidence correctness or maintainability finding remains.
