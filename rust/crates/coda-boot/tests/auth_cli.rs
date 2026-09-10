//! Host-local `auth` command behaviour: what is decided before a profile is
//! opened, what is disclosed, and which exit code each outcome produces.
//!
//! Nothing here touches a real profile, a real terminal or a provider: the
//! planning stage takes a console port, and the rendering/exit-code stages are
//! pure functions over the service's own result types.

use std::sync::Mutex;

use clap::Parser;
use coda_auth::failure::AuthFailure;
use coda_auth::provider::copilot::{CopilotConfig, CopilotDeployment, CopilotDeploymentChoice};
use coda_auth::service::{
    AuthStatus, CommitInterruption, CommitOutcome, CommitStep, CopilotContextCell, CredentialOrigin,
    CredentialSummary, LogoutFailure, LogoutReport, PrepareFailure, ProviderIdentity,
    ProviderStatus, Selection, SelectionError, SelectionSource, StoredState,
};
use coda_auth::{credential::CredentialKind, Secret};
use coda_boot::auth_args::AuthArgs;
use coda_boot::auth_cli::{
    self, AuthConsole, AuthPlan, PlanError, PromptError, EXIT_CANCELLED, EXIT_FAILED, EXIT_OK,
    EXIT_USAGE,
};

#[derive(Parser)]
struct Cli {
    #[command(flatten)]
    auth: AuthArgs,
}

fn args(argv: &[&str]) -> AuthArgs {
    Cli::try_parse_from(argv).expect("parses").auth
}

/// A console whose answers are scripted, so the picker and the key prompt can
/// be exercised without a terminal.
struct ScriptedConsole {
    interactive: bool,
    stdin_terminal: Option<bool>,
    lines: Mutex<Vec<Result<String, PromptError>>>,
    masked: Mutex<Vec<Result<String, PromptError>>>,
    piped: Mutex<Vec<Result<String, PromptError>>>,
    written: Mutex<Vec<String>>,
}

impl ScriptedConsole {
    fn new(interactive: bool) -> Self {
        Self {
            interactive,
            stdin_terminal: None,
            lines: Mutex::new(Vec::new()),
            masked: Mutex::new(Vec::new()),
            piped: Mutex::new(Vec::new()),
            written: Mutex::new(Vec::new()),
        }
    }

    /// stdin is a terminal even though the console as a whole is not
    /// promptable — the shape a redirected stderr produces.
    #[allow(dead_code)]
    fn with_terminal_stdin(mut self, value: bool) -> Self {
        self.stdin_terminal = Some(value);
        self
    }

    fn with_line(self, value: Result<String, PromptError>) -> Self {
        self.lines.lock().unwrap().push(value);
        self
    }

    fn with_masked(self, value: Result<String, PromptError>) -> Self {
        self.masked.lock().unwrap().push(value);
        self
    }

    fn with_piped(self, value: Result<String, PromptError>) -> Self {
        self.piped.lock().unwrap().push(value);
        self
    }

    fn output(&self) -> String {
        self.written.lock().unwrap().join("\n")
    }
}

fn take(queue: &Mutex<Vec<Result<String, PromptError>>>) -> Result<String, PromptError> {
    let mut queue = queue.lock().unwrap();
    if queue.is_empty() {
        return Err(PromptError::EndOfInput);
    }
    queue.remove(0)
}

impl AuthConsole for ScriptedConsole {
    fn is_interactive(&self) -> bool {
        self.interactive
    }

    fn stdin_is_terminal(&self) -> bool {
        self.stdin_terminal.unwrap_or(self.interactive)
    }

    fn report(&self, text: &str) {
        self.written.lock().unwrap().push(text.to_owned());
    }

    fn notice(&self, text: &str) {
        self.written.lock().unwrap().push(text.to_owned());
    }

    fn read_line(&self, _prompt: &str) -> Result<String, PromptError> {
        take(&self.lines)
    }

    fn read_masked(&self, _prompt: &str) -> Result<Secret<String>, PromptError> {
        take(&self.masked).map(Secret::new)
    }

    fn read_piped_api_key(&self) -> Result<Secret<String>, PromptError> {
        take(&self.piped).map(Secret::new)
    }
}

// ── Planning ─────────────────────────────────────────────────────────────────

#[test]
fn provider_inapplicable_options_are_refused_as_usage_before_any_profile_is_opened() {
    for argv in [
        vec!["auth", "login", "claude", "--public"],
        vec!["auth", "login", "copilot", "--api-key-stdin"],
        vec!["auth", "login", "api-key", "--enterprise-domain", "tenant.ghe.com"],
    ] {
        let console = ScriptedConsole::new(true);
        let error = auth_cli::plan(&args(&argv), &console).expect_err("refused");
        assert_eq!(error.exit_code(), EXIT_USAGE, "{argv:?}");
        assert!(matches!(error, PlanError::Usage(_)), "{argv:?}");
    }
}

#[test]
fn a_provider_is_required_when_there_is_no_terminal_to_pick_on() {
    let console = ScriptedConsole::new(false);
    let error = auth_cli::plan(&args(&["auth", "login"]), &console).expect_err("refused");
    assert_eq!(error.exit_code(), EXIT_USAGE);
    let PlanError::Usage(message) = error else { panic!("usage") };
    assert!(message.to_lowercase().contains("provider"), "{message}");
}

#[test]
fn the_picker_runs_only_on_a_terminal_and_uses_the_shared_alias_table() {
    for (answer, expected) in [
        ("1", ProviderIdentity::ClaudeAi),
        ("2", ProviderIdentity::AnthropicApiKey),
        ("3", ProviderIdentity::GithubCopilot),
        ("copilot", ProviderIdentity::GithubCopilot),
        ("api-key", ProviderIdentity::AnthropicApiKey),
    ] {
        let console = ScriptedConsole::new(true)
            .with_line(Ok(answer.to_owned()))
            .with_masked(Ok("sk-test-key".to_owned()));
        let plan = auth_cli::plan(&args(&["auth", "login"]), &console).expect("planned");
        let AuthPlan::Login(login) = plan else { panic!("login") };
        assert_eq!(login.identity, expected, "{answer}");
    }
}

#[test]
fn an_unknown_picker_answer_is_a_usage_error_not_a_substituted_provider() {
    let console = ScriptedConsole::new(true).with_line(Ok("openai".to_owned()));
    let error = auth_cli::plan(&args(&["auth", "login"]), &console).expect_err("refused");
    assert_eq!(error.exit_code(), EXIT_USAGE);
}

#[test]
fn cancelling_the_picker_or_the_key_prompt_is_the_cancellation_exit_code() {
    for console in [
        ScriptedConsole::new(true).with_line(Err(PromptError::Cancelled)),
        ScriptedConsole::new(true)
            .with_line(Ok("api-key".to_owned()))
            .with_masked(Err(PromptError::Cancelled)),
    ] {
        let error = auth_cli::plan(&args(&["auth", "login"]), &console).expect_err("cancelled");
        assert_eq!(error.exit_code(), EXIT_CANCELLED);
        assert!(matches!(error, PlanError::Cancelled));
    }
}

#[test]
fn an_end_of_input_key_prompt_fails_safely_rather_than_storing_nothing() {
    let console = ScriptedConsole::new(true).with_masked(Err(PromptError::EndOfInput));
    let error = auth_cli::plan(&args(&["auth", "login", "api-key"]), &console).expect_err("fails");
    assert_ne!(error.exit_code(), EXIT_OK);
    assert!(!matches!(error, PlanError::Cancelled));
}

#[test]
fn a_key_may_only_come_from_a_non_terminal_when_stdin_was_named_explicitly() {
    let console = ScriptedConsole::new(false);
    let error = auth_cli::plan(&args(&["auth", "login", "api-key"]), &console).expect_err("refused");
    assert_eq!(error.exit_code(), EXIT_USAGE);
    let PlanError::Usage(message) = error else { panic!("usage") };
    assert!(message.contains("--api-key-stdin"), "{message}");

    let console = ScriptedConsole::new(false).with_piped(Ok("sk-piped-key".to_owned()));
    let plan = auth_cli::plan(&args(&["auth", "login", "api-key", "--api-key-stdin"]), &console)
        .expect("planned");
    let AuthPlan::Login(login) = plan else { panic!("login") };
    assert_eq!(login.identity, ProviderIdentity::AnthropicApiKey);
    assert_eq!(login.api_key.as_ref().map(|key| key.expose().as_str()), Some("sk-piped-key"));
}

#[test]
fn the_environment_key_is_never_collected_or_echoed_and_a_plan_never_debugs_a_key() {
    let console = ScriptedConsole::new(true);
    let plan = auth_cli::plan(&args(&["auth", "login", "api-key", "--use-env"]), &console)
        .expect("planned");
    let AuthPlan::Login(login) = plan else { panic!("login") };
    assert!(login.use_env);
    assert!(login.api_key.is_none(), "an environment login must not read a key from anywhere");

    let console = ScriptedConsole::new(true).with_masked(Ok("sk-private-sentinel".to_owned()));
    let plan = auth_cli::plan(&args(&["auth", "login", "api-key"]), &console).expect("planned");
    let debug = format!("{plan:?}");
    assert!(!debug.contains("sk-private-sentinel"), "{debug}");
    assert!(!console.output().contains("sk-private-sentinel"), "{}", console.output());
}

#[test]
fn a_deployment_choice_is_carried_truthfully_from_the_named_option() {
    let console = ScriptedConsole::new(true);
    let plan = auth_cli::plan(&args(&["auth", "login", "copilot", "--public"]), &console)
        .expect("planned");
    let AuthPlan::Login(login) = plan else { panic!("login") };
    assert_eq!(login.deployment, Some(CopilotDeploymentChoice::Public));

    let plan = auth_cli::plan(
        &args(&["auth", "login", "copilot", "--enterprise-domain", "octocorp.ghe.com"]),
        &ScriptedConsole::new(true),
    )
    .expect("planned");
    let AuthPlan::Login(login) = plan else { panic!("login") };
    assert_eq!(
        login.deployment,
        Some(CopilotDeploymentChoice::Enterprise("octocorp.ghe.com".into()))
    );

    // No option at all leaves the saved default in force rather than inventing
    // a deployment.
    let plan = auth_cli::plan(&args(&["auth", "login", "copilot"]), &ScriptedConsole::new(false))
        .expect("planned");
    let AuthPlan::Login(login) = plan else { panic!("login") };
    assert_eq!(login.deployment, None);
}

/// `--api-key-stdin` says "the key is arriving on a redirected stdin". When
/// stdin is the keyboard, honouring it would read the key in clear text with
/// the terminal echoing every character.
#[test]
fn reading_a_piped_key_is_refused_when_stdin_is_actually_the_keyboard() {
    let console = ScriptedConsole::new(true).with_piped(Ok("sk-would-be-echoed".to_owned()));
    let error = auth_cli::plan(&args(&["auth", "login", "api-key", "--api-key-stdin"]), &console)
        .expect_err("refused");
    assert_eq!(error.exit_code(), EXIT_USAGE);
    let PlanError::Usage(message) = error else { panic!("usage") };
    assert!(message.to_lowercase().contains("stdin is a terminal"), "{message}");
    assert!(!console.output().contains("sk-would-be-echoed"), "{message}");
    // Nothing was asked of the user before the refusal.
    assert!(console.output().is_empty(), "{}", console.output());

    // The option cannot even be spelled without a provider, so the refusal
    // above is the only shape this mistake can take.
    assert!(Cli::try_parse_from(["auth", "login", "--api-key-stdin"]).is_err());
}

/// A terminal that reports itself as one but cannot suppress echo (mintty and
/// friends) must be refused with an alternative, not silently echoed into.
#[test]
fn a_terminal_that_cannot_hide_input_is_given_a_pipe_instruction() {
    let console = ScriptedConsole::new(true).with_masked(Err(PromptError::EchoUnavailable));
    let error =
        auth_cli::plan(&args(&["auth", "login", "api-key"]), &console).expect_err("refused");
    assert_eq!(error.exit_code(), EXIT_USAGE);
    let PlanError::Usage(message) = error else { panic!("usage") };
    assert!(message.contains("--api-key-stdin"), "{message}");
    assert!(message.to_lowercase().contains("pipe"), "{message}");
}

/// Provider metadata and `settings.json` values are not this command's words.
#[test]
fn hostile_stored_values_cannot_forge_or_repaint_the_status_report() {
    // The same report shape with benign values, so only the hostile content
    // differs between the two.
    let mut benign_status = status_fixture();
    benign_status.saved_default = Some("anthropic".into());
    benign_status.github_enterprise_domain = Some("octocorp.ghe.com".into());
    benign_status.providers[1] = ProviderStatus {
        identity: ProviderIdentity::AnthropicApiKey,
        state: StoredState::Present(CredentialSummary {
            kind: CredentialKind::ApiKey,
            expires_at: None,
            scopes: Vec::new(),
            account: Some(coda_auth::credential::AccountInfo {
                email_address: Some("person@example.com".into()),
                account_uuid: None,
                organization_uuid: None,
            }),
        }),
    };
    let benign = auth_cli::render_status(&benign_status, None);

    let mut status = status_fixture();
    status.saved_default =
        Some("anthropic\u{1b}[2J\r\n  Selected provider: github-copilot (verified)".into());
    status.github_enterprise_domain = Some("octocorp.ghe.com\nSigned in as: root".into());
    status.providers[1] = ProviderStatus {
        identity: ProviderIdentity::AnthropicApiKey,
        state: StoredState::Present(CredentialSummary {
            kind: CredentialKind::ApiKey,
            expires_at: None,
            scopes: Vec::new(),
            account: Some(coda_auth::credential::AccountInfo {
                email_address: Some("a\r\nb\u{1b}[31m".into()),
                account_uuid: None,
                organization_uuid: None,
            }),
        }),
    };

    let text = auth_cli::render_status(&status, None);
    assert!(!text.contains('\u{1b}'), "no escape sequence may survive: {text:?}");
    assert!(!text.contains('\r'), "no carriage return may survive: {text:?}");
    // Every line is one this renderer wrote: a stored value cannot add one,
    // and therefore cannot forge a verdict of its own.
    assert_eq!(
        text.lines().count(),
        benign.lines().count(),
        "a stored value must not be able to add a line: {text}"
    );
    let forged = text
        .lines()
        .filter(|line| line.trim_start().starts_with("Selected provider:"))
        .count();
    assert_eq!(forged, 1, "only this renderer may state a verdict: {text}");
    assert!(
        !text.lines().any(|line| line.trim_start().starts_with("Signed in as: root")),
        "{text}"
    );
    // The one legitimate verdict is unchanged by the attempt.
    assert!(text.contains("Selected provider: anthropic (the saved provider choice)"), "{text}");
}

#[test]
fn status_and_logout_need_no_terminal_at_all() {
    let console = ScriptedConsole::new(false);
    assert!(matches!(auth_cli::plan(&args(&["auth", "status"]), &console), Ok(AuthPlan::Status)));
    assert!(matches!(
        auth_cli::plan(&args(&["auth", "logout"]), &console),
        Ok(AuthPlan::Logout(None))
    ));
    assert!(matches!(
        auth_cli::plan(&args(&["auth", "logout", "claude"]), &console),
        Ok(AuthPlan::Logout(Some(ProviderIdentity::ClaudeAi)))
    ));
}

// ── Exit codes ───────────────────────────────────────────────────────────────

#[test]
fn commit_outcomes_map_onto_the_documented_exit_codes() {
    assert_eq!(
        auth_cli::exit_code_for_commit(&CommitOutcome::Committed {
            identity: ProviderIdentity::ClaudeAi,
            replaced: Vec::new(),
            settings_changed: true,
        }),
        EXIT_OK
    );
    for outcome in [
        CommitOutcome::Superseded { current: Some(ProviderIdentity::GithubCopilot) },
        CommitOutcome::Failed {
            step: CommitStep::StoreCredential,
            failure: AuthFailure::Store,
            rolled_back: true,
        },
        CommitOutcome::RestorationFailed {
            failed_step: CommitStep::StoreCredential,
            failure: AuthFailure::Store,
            restore_step: CommitStep::RestoreSettings,
            restore_failure: AuthFailure::Store,
        },
        CommitOutcome::Indeterminate { cause: CommitInterruption::TaskFailed },
    ] {
        assert_eq!(auth_cli::exit_code_for_commit(&outcome), EXIT_FAILED, "{outcome:?}");
    }
}

#[test]
fn an_interrupted_commit_claims_neither_a_restart_nor_an_intact_profile() {
    let outcome = CommitOutcome::Indeterminate { cause: CommitInterruption::TaskFailed };
    let text = auth_cli::render_commit(&outcome);
    assert!(!outcome.engine_may_start());
    assert!(!outcome.profile_is_intact());
    let lowered = text.to_lowercase();
    assert!(lowered.contains("unknown") || lowered.contains("may be incomplete"), "{text}");
    assert!(!lowered.contains("signed in to"), "{text}");
}

#[test]
fn a_cancelled_preparation_is_130_and_every_other_preparation_failure_is_1() {
    assert_eq!(auth_cli::exit_code_for_prepare(&PrepareFailure::Cancelled), EXIT_CANCELLED);
    for failure in [
        PrepareFailure::Rejected(AuthFailure::InvalidInput),
        PrepareFailure::Failed(AuthFailure::Network),
        PrepareFailure::ValidationUnavailable {
            reason: AuthFailure::Network,
            displaces_a_working_account: true,
        },
    ] {
        assert_eq!(auth_cli::exit_code_for_prepare(&failure), EXIT_FAILED, "{failure:?}");
    }
}

/// The service's own wording offers to "continue without checking". This
/// command has no such option, so it must not describe one.
#[test]
fn an_uncheckable_credential_is_explained_in_terms_this_command_can_honour() {
    for displaces in [true, false] {
        let failure = PrepareFailure::ValidationUnavailable {
            reason: AuthFailure::Network,
            displaces_a_working_account: displaces,
        };
        let text = auth_cli::render_prepare_failure(&failure);
        let lowered = text.to_lowercase();
        assert!(lowered.contains("not saved"), "{text}");
        assert!(!lowered.contains("choose to continue"), "{text}");
        assert!(!lowered.contains("choose to replace"), "{text}");
        if displaces {
            assert!(lowered.contains("left connected"), "{text}");
        } else {
            assert!(lowered.contains("nothing was changed"), "{text}");
        }
    }

    // Everything else keeps the service's wording.
    assert_eq!(
        auth_cli::render_prepare_failure(&PrepareFailure::Cancelled),
        PrepareFailure::Cancelled.to_string()
    );
}

#[test]
fn a_failed_logout_that_could_not_be_undone_is_reported_rather_than_swallowed() {
    let failure = LogoutFailure::RestorationFailed {
        failed_step: CommitStep::RemoveDisplaced(ProviderIdentity::ClaudeAi),
        failure: AuthFailure::Store,
        restore_step: CommitStep::RestoreCredential(ProviderIdentity::ClaudeAi),
        restore_failure: AuthFailure::Store,
    };
    let text = auth_cli::render_logout_failure(&failure);
    assert!(text.to_lowercase().contains("may be incomplete"), "{text}");
    assert_eq!(auth_cli::exit_code_for_logout(&failure), EXIT_FAILED);
}

// ── Reporting ────────────────────────────────────────────────────────────────

fn status_fixture() -> AuthStatus {
    AuthStatus {
        providers: vec![
            ProviderStatus { identity: ProviderIdentity::ClaudeAi, state: StoredState::Absent },
            ProviderStatus {
                identity: ProviderIdentity::AnthropicApiKey,
                state: StoredState::Present(CredentialSummary {
                    kind: CredentialKind::ApiKey,
                    expires_at: None,
                    scopes: Vec::new(),
                    account: None,
                }),
            },
            ProviderStatus {
                identity: ProviderIdentity::GithubCopilot,
                state: StoredState::Unreadable(AuthFailure::StoreUndecryptable),
            },
        ],
        selection: Ok(Selection {
            identity: ProviderIdentity::AnthropicApiKey,
            source: SelectionSource::SavedDefault,
            origin: CredentialOrigin::Stored,
        }),
        saved_default: Some("anthropic".into()),
        github_enterprise_domain: None,
        environment_api_key: true,
        anthropic_endpoint: coda_auth::service::endpoint::resolve(None, |_| None),
        settings_error: None,
        provider_context_error: Some(AuthFailure::InvalidEndpoint),
    }
}

/// A saved choice backed by an exported variable is not a stored account, and
/// the status line must not read as though a key were saved for it.
#[test]
fn status_says_when_the_selected_provider_authenticates_from_the_environment() {
    let mut status = status_fixture();
    status.providers[1] = ProviderStatus {
        identity: ProviderIdentity::AnthropicApiKey,
        state: StoredState::Absent,
    };
    status.selection = Ok(Selection {
        identity: ProviderIdentity::AnthropicApiKey,
        source: SelectionSource::SavedDefault,
        origin: CredentialOrigin::Environment,
    });

    let text = auth_cli::render_status(&status, None);
    let line = text
        .lines()
        .find(|line| line.trim_start().starts_with("Selected provider:"))
        .expect("a selection line");
    assert!(line.contains("anthropic"), "{line}");
    assert!(line.contains("ANTHROPIC_API_KEY"), "{line}");
    assert!(line.contains("no key is stored"), "{line}");
}

#[test]
fn status_separates_absent_from_unreadable_and_never_claims_a_running_engine() {
    let text = auth_cli::render_status(&status_fixture(), None);
    assert!(text.contains("Claude.ai subscription"), "{text}");
    assert!(text.to_lowercase().contains("not signed in"), "{text}");
    assert!(text.to_lowercase().contains("could not be read"), "{text}");
    // The unreadable slot must not be described as absent.
    let copilot_line = text
        .lines()
        .find(|line| line.contains("GitHub Copilot"))
        .expect("copilot line");
    assert!(!copilot_line.to_lowercase().contains("not signed in"), "{copilot_line}");
    // Availability of the ambient key is stated as availability only.
    assert!(text.contains("ANTHROPIC_API_KEY"), "{text}");
    // The provider-context fault must be surfaced, not hidden.
    assert!(text.to_lowercase().contains("endpoint"), "{text}");
    // No claim about other processes.
    let lowered = text.to_lowercase();
    assert!(lowered.contains("other processes") || lowered.contains("already running"), "{text}");
    assert!(!lowered.contains("the running engine is using"), "{text}");
}

#[test]
fn status_reports_the_resolved_copilot_deployment_rather_than_re_resolving_it() {
    let cell = CopilotContextCell::initial(
        CopilotConfig::for_enterprise("octocorp.ghe.com").expect("valid tenant"),
        CopilotDeployment::Enterprise { domain: "octocorp.ghe.com".into() },
        None,
    );
    let enterprise = cell.current();
    let text = auth_cli::render_status(&status_fixture(), Some(&enterprise));
    assert!(text.contains("octocorp.ghe.com"), "{text}");

    let cell = CopilotContextCell::initial(
        CopilotConfig::default_public(),
        CopilotDeployment::Public,
        None,
    );
    let public = cell.current();
    let text = auth_cli::render_status(&status_fixture(), Some(&public));
    assert!(text.contains("github.com"), "{text}");
    assert!(!text.contains("octocorp.ghe.com"), "{text}");
}

#[test]
fn a_replacement_is_disclosed_by_name_before_the_switch_completes() {
    let text = auth_cli::render_replacement(
        ProviderIdentity::GithubCopilot,
        &[ProviderIdentity::AnthropicApiKey],
    );
    assert!(text.contains("GitHub Copilot"), "{text}");
    assert!(text.contains("Anthropic API key"), "{text}");
    assert!(text.to_lowercase().contains("remove"), "{text}");
    assert!(auth_cli::render_replacement(ProviderIdentity::ClaudeAi, &[]).is_empty());
}

#[test]
fn a_logout_report_states_what_it_did_not_do() {
    let text = auth_cli::render_logout(&LogoutReport {
        removed: vec![ProviderIdentity::ClaudeAi],
        cleared_default_provider: true,
        environment_api_key_still_available: true,
    });
    assert!(text.contains("ANTHROPIC_API_KEY"), "{text}");
    let lowered = text.to_lowercase();
    // It says what it did *not* do rather than claiming a provider-side
    // revocation or a machine-wide sign-out.
    assert!(lowered.contains("nothing was revoked"), "{text}");
    assert!(lowered.contains("disconnected"), "{text}");
    assert!(!lowered.contains("signed out everywhere"), "{text}");
}

#[test]
fn a_selection_that_cannot_be_read_is_never_rendered_as_signed_out() {
    let mut status = status_fixture();
    status.selection = Err(SelectionError::Unavailable { failure: AuthFailure::StoreUndecryptable });
    let text = auth_cli::render_status(&status, None);
    let lowered = text.to_lowercase();
    assert!(lowered.contains("could not be"), "{text}");
    assert!(!lowered.contains("no provider is configured"), "{text}");
}

// ── The Anthropic API-key endpoint ───────────────────────────────────────────

/// With nothing configured, the report says so plainly — and names the
/// variable that would change it, so a user who *thought* they had set it can
/// see that they have not.
#[test]
fn status_names_the_default_anthropic_endpoint_when_nothing_overrides_it() {
    let text = auth_cli::render_status(&status_fixture(), None);
    assert!(text.contains("api.anthropic.com"), "{text}");
    assert!(text.contains("ANTHROPIC_BASE_URL"), "{text}");
    assert!(text.contains("the default"), "{text}");
}

/// An override is disclosed by **host**, never by URL: a base URL may carry a
/// path, and a path may carry a tenant id or a token-shaped segment.
#[test]
fn status_names_the_override_host_and_never_its_path() {
    let mut status = status_fixture();
    status.anthropic_endpoint = coda_auth::service::endpoint::validate(
        "https://gateway.example.com/tenants/SECRET-PATH-SEGMENT",
        coda_auth::service::EndpointSource::Environment,
    );
    let text = auth_cli::render_status(&status, None);
    assert!(text.contains("gateway.example.com"), "{text}");
    assert!(!text.contains("SECRET-PATH-SEGMENT"), "{text}");
    assert!(text.contains("ANTHROPIC_BASE_URL"), "{text}");
    // The scope is stated: this is not a global redirect.
    assert!(text.contains("Copilot"), "{text}");
}

/// `auth status` must be able to *diagnose* a broken override rather than
/// failing on it: that is exactly when a user needs to read their own
/// configuration back. The refused value is never echoed.
#[test]
fn status_diagnoses_a_refused_override_without_echoing_it() {
    let mut status = status_fixture();
    status.anthropic_endpoint = coda_auth::service::endpoint::validate(
        "https://gateway.example.com/?key=SECRET-IN-QUERY",
        coda_auth::service::EndpointSource::Environment,
    );
    let text = auth_cli::render_status(&status, None);
    assert!(text.contains("ANTHROPIC_BASE_URL"), "{text}");
    assert!(text.to_lowercase().contains("refused"), "{text}");
    assert!(text.contains("fail closed"), "{text}");
    assert!(!text.contains("SECRET-IN-QUERY"), "{text}");
}

/// A login refused for its endpoint says what happened, says nothing was
/// changed, and does not read as a rejected credential — no key was even read.
#[test]
fn a_refused_endpoint_reads_as_a_configuration_fault_not_a_bad_key() {
    let failure = PrepareFailure::EndpointRejected {
        reason: coda_auth::service::EndpointError::InsecureNonLoopback,
    };
    let text = auth_cli::render_prepare_failure(&failure);
    assert!(text.contains("ANTHROPIC_BASE_URL"), "{text}");
    assert!(text.contains("nothing was changed"), "{text}");
    assert!(text.contains("no API key was read or sent"), "{text}");
    assert!(!text.to_lowercase().contains("sign in again"), "{text}");
    assert_eq!(auth_cli::exit_code_for_prepare(&failure), EXIT_FAILED);
}
