//! The one provider selector, exercised as a table.
//!
//! The engine and the auth service must answer identically, so both call this
//! function. Every case here is a decision that decides *which account* a user
//! is signed in as, which is why none of them may be resolved by iteration
//! order or by a silent fallback.

use coda_auth::service::{
    select_provider, CredentialOrigin, ProviderIdentity, SelectionError, SelectionInputs,
    SelectionSource,
};

fn inputs<'a>() -> SelectionInputs<'a> {
    SelectionInputs::default()
}

#[test]
fn an_explicit_provider_without_a_credential_needs_login_and_never_falls_back() {
    // A Copilot credential is stored and an ambient key is exported: neither
    // may answer a request for Claude.
    let stored = [ProviderIdentity::GithubCopilot];
    let selection = select_provider(SelectionInputs {
        explicit: Some("claude-ai"),
        stored: &stored,
        ambient_api_key: true,
        ..inputs()
    });
    assert_eq!(
        selection,
        Err(SelectionError::NeedsLogin {
            identity: ProviderIdentity::ClaudeAi,
            source: SelectionSource::Explicit,
        })
    );
}

#[test]
fn a_saved_default_without_a_credential_fails_closed() {
    let stored = [ProviderIdentity::GithubCopilot];
    let selection = select_provider(SelectionInputs {
        saved_default: Some("claude-ai"),
        stored: &stored,
        ambient_api_key: true,
        ..inputs()
    });
    assert_eq!(
        selection,
        Err(SelectionError::NeedsLogin {
            identity: ProviderIdentity::ClaudeAi,
            source: SelectionSource::SavedDefault,
        })
    );
}

#[test]
fn an_explicit_choice_wins_over_a_saved_default() {
    let stored = [ProviderIdentity::ClaudeAi, ProviderIdentity::GithubCopilot];
    let selection = select_provider(SelectionInputs {
        explicit: Some("copilot"),
        saved_default: Some("claude-ai"),
        stored: &stored,
        ..inputs()
    })
    .expect("an explicit provider with a stored credential is selectable");
    assert_eq!(selection.identity, ProviderIdentity::GithubCopilot);
    assert_eq!(selection.source, SelectionSource::Explicit);
    assert_eq!(selection.origin, CredentialOrigin::Stored);
}

#[test]
fn an_explicit_api_key_provider_may_use_the_ambient_key() {
    let selection = select_provider(SelectionInputs {
        explicit: Some("anthropic"),
        ambient_api_key: true,
        ..inputs()
    })
    .expect("the environment carries this identity's own credential");
    assert_eq!(selection.identity, ProviderIdentity::AnthropicApiKey);
    assert_eq!(selection.origin, CredentialOrigin::Environment);
}

#[test]
fn an_ambient_key_never_answers_for_the_subscription_identity() {
    let selection = select_provider(SelectionInputs {
        explicit: Some("claude"),
        ambient_api_key: true,
        ..inputs()
    });
    assert!(matches!(selection, Err(SelectionError::NeedsLogin { .. })));
}

#[test]
fn an_unconfigured_start_uses_the_only_stored_provider() {
    let stored = [ProviderIdentity::ClaudeAi];
    let selection = select_provider(SelectionInputs { stored: &stored, ..inputs() })
        .expect("one stored credential is unambiguous");
    assert_eq!(selection.identity, ProviderIdentity::ClaudeAi);
    assert_eq!(selection.source, SelectionSource::SoleStored);
}

#[test]
fn a_deliberate_login_outranks_an_ambient_key_when_nothing_was_chosen() {
    let stored = [ProviderIdentity::GithubCopilot];
    let selection = select_provider(SelectionInputs {
        stored: &stored,
        ambient_api_key: true,
        ..inputs()
    })
    .expect("the stored credential is the account the user signed in to");
    assert_eq!(selection.identity, ProviderIdentity::GithubCopilot);
    assert_eq!(selection.source, SelectionSource::SoleStored);
}

#[test]
fn an_unconfigured_start_with_only_an_ambient_key_uses_it() {
    let selection = select_provider(SelectionInputs { ambient_api_key: true, ..inputs() })
        .expect("an exported key is a usable credential");
    assert_eq!(selection.identity, ProviderIdentity::AnthropicApiKey);
    assert_eq!(selection.source, SelectionSource::AmbientEnvKey);
    assert_eq!(selection.origin, CredentialOrigin::Environment);
}

#[test]
fn several_stored_providers_are_ambiguous_never_a_map_order() {
    let stored = [ProviderIdentity::ClaudeAi, ProviderIdentity::GithubCopilot];
    let selection = select_provider(SelectionInputs { stored: &stored, ..inputs() });
    assert_eq!(
        selection,
        Err(SelectionError::Ambiguous {
            stored: vec![ProviderIdentity::ClaudeAi, ProviderIdentity::GithubCopilot],
        })
    );
}

#[test]
fn nothing_configured_and_nothing_stored_is_not_an_authenticated_fallback() {
    assert_eq!(select_provider(inputs()), Err(SelectionError::NoCredentials));
}

#[test]
fn an_unknown_explicit_provider_is_rejected_rather_than_rewritten() {
    let stored = [ProviderIdentity::GithubCopilot];
    let selection = select_provider(SelectionInputs {
        explicit: Some("openai"),
        stored: &stored,
        ..inputs()
    });
    assert_eq!(
        selection,
        Err(SelectionError::UnknownProvider { requested: "openai".into() })
    );
}

#[test]
fn a_blank_explicit_or_saved_choice_is_not_a_choice() {
    let stored = [ProviderIdentity::GithubCopilot];
    let selection = select_provider(SelectionInputs {
        explicit: Some("   "),
        saved_default: Some(""),
        stored: &stored,
        ..inputs()
    })
    .expect("blank strings must not be treated as a named provider");
    assert_eq!(selection.source, SelectionSource::SoleStored);
}

#[test]
fn the_identities_stay_separate_between_storage_and_the_engine() {
    assert_eq!(ProviderIdentity::ClaudeAi.stored_id(), "claude-ai");
    assert_eq!(ProviderIdentity::ClaudeAi.engine_id(), "claude-ai");
    assert_eq!(ProviderIdentity::AnthropicApiKey.stored_id(), "anthropic-api-key");
    assert_eq!(ProviderIdentity::AnthropicApiKey.engine_id(), "anthropic");
    assert_eq!(ProviderIdentity::GithubCopilot.stored_id(), "github-copilot");
    assert_eq!(ProviderIdentity::GithubCopilot.engine_id(), "github-copilot");
    assert_eq!(ProviderIdentity::ALL.len(), 3);
}

#[test]
fn every_alias_the_product_already_accepted_still_resolves() {
    for (alias, expected) in [
        ("anthropic", ProviderIdentity::AnthropicApiKey),
        ("anthropic-api-key", ProviderIdentity::AnthropicApiKey),
        ("api-key", ProviderIdentity::AnthropicApiKey),
        ("apikey", ProviderIdentity::AnthropicApiKey),
        ("claude-ai", ProviderIdentity::ClaudeAi),
        ("claude", ProviderIdentity::ClaudeAi),
        ("claudeai", ProviderIdentity::ClaudeAi),
        ("anthropic-subscription", ProviderIdentity::ClaudeAi),
        ("subscription", ProviderIdentity::ClaudeAi),
        ("github-copilot", ProviderIdentity::GithubCopilot),
        ("copilot", ProviderIdentity::GithubCopilot),
        ("github", ProviderIdentity::GithubCopilot),
        (" Claude-AI ", ProviderIdentity::ClaudeAi),
    ] {
        assert_eq!(ProviderIdentity::parse(alias), Some(expected), "alias {alias}");
    }
    assert_eq!(ProviderIdentity::parse("openai"), None);
}

#[test]
fn the_api_key_identity_keeps_reading_its_legacy_settings_row() {
    // The canonical settings row is the engine id; the stored id was written
    // by older builds and must still be honoured when the canonical row is
    // absent, or a user's saved model silently changes.
    assert_eq!(
        ProviderIdentity::AnthropicApiKey.settings_model_keys(),
        ["anthropic", "anthropic-api-key"]
    );
    assert_eq!(ProviderIdentity::ClaudeAi.settings_model_keys(), ["claude-ai"]);
    assert_eq!(
        ProviderIdentity::GithubCopilot.settings_model_keys(),
        ["github-copilot"]
    );
}

// ── Unreadable inputs: the selector must fail closed, never fall through ─────
//
// A credential or a settings file that cannot be *read* is not the same as one
// that is not *there*. Reading it as absent is how a machine quietly connects
// to a different account: the saved choice disappears, and the ambient key or
// the one credential that still parses takes over.

use coda_auth::failure::AuthFailure;
use coda_auth::service::{select_in_context, SelectionContext, StoredEntry};

fn entries(
    pairs: &[(ProviderIdentity, StoredEntry)],
) -> Vec<(ProviderIdentity, StoredEntry)> {
    ProviderIdentity::ALL
        .into_iter()
        .map(|identity| {
            let state = pairs
                .iter()
                .find(|(candidate, _)| *candidate == identity)
                .map(|(_, state)| state.clone())
                .unwrap_or(StoredEntry::Absent);
            (identity, state)
        })
        .collect()
}

fn context<'a>(
    entries: &'a [(ProviderIdentity, StoredEntry)],
    saved_default: Result<Option<&'a str>, AuthFailure>,
    ambient_api_key: bool,
) -> SelectionContext<'a> {
    SelectionContext { explicit: None, saved_default, entries, ambient_api_key }
}

#[test]
fn unreadable_settings_make_the_selection_unavailable_not_ambient() {
    // The settings file cannot be read, so the saved choice is unknown. An
    // exported key must not step into that gap.
    let entries = entries(&[]);
    let selection = select_in_context(context(&entries, Err(AuthFailure::CredentialParse), true));
    assert!(
        matches!(selection, Err(SelectionError::Unavailable { .. })),
        "{selection:?}"
    );
}

#[test]
fn unreadable_settings_do_not_let_a_sole_stored_account_stand_in() {
    let entries = entries(&[(ProviderIdentity::GithubCopilot, StoredEntry::Present)]);
    let selection = select_in_context(context(
        &entries,
        Err(AuthFailure::Io { kind: std::io::ErrorKind::PermissionDenied }),
        false,
    ));
    assert!(
        matches!(selection, Err(SelectionError::Unavailable { .. })),
        "{selection:?}"
    );
}

#[test]
fn an_unreadable_credential_beside_a_readable_one_is_not_a_sole_stored_account() {
    // Claude cannot be read; Copilot can. "The only credential is Copilot" is
    // exactly the conclusion that is not available here.
    let entries = entries(&[
        (ProviderIdentity::ClaudeAi, StoredEntry::Unreadable(AuthFailure::StoreUndecryptable)),
        (ProviderIdentity::GithubCopilot, StoredEntry::Present),
    ]);
    let selection = select_in_context(context(&entries, Ok(None), false));
    assert!(
        matches!(selection, Err(SelectionError::Unavailable { .. })),
        "{selection:?}"
    );
}

#[test]
fn an_explicit_request_for_an_unreadable_credential_is_not_a_missing_login() {
    let entries = entries(&[(
        ProviderIdentity::ClaudeAi,
        StoredEntry::Unreadable(AuthFailure::StoreUndecryptable),
    )]);
    let selection = select_in_context(SelectionContext {
        explicit: Some("claude"),
        ..context(&entries, Ok(None), true)
    });
    assert!(
        matches!(selection, Err(SelectionError::Unavailable { .. })),
        "telling this user to sign in would overwrite a credential that is still there: {selection:?}"
    );
}

#[test]
fn a_saved_choice_whose_credential_is_unreadable_is_not_a_missing_login() {
    let entries = entries(&[
        (ProviderIdentity::ClaudeAi, StoredEntry::Unreadable(AuthFailure::StoreUndecryptable)),
        (ProviderIdentity::GithubCopilot, StoredEntry::Present),
    ]);
    let selection = select_in_context(context(&entries, Ok(Some("claude-ai")), true));
    assert!(
        matches!(selection, Err(SelectionError::Unavailable { .. })),
        "{selection:?}"
    );
}

#[test]
fn a_named_readable_choice_still_resolves_beside_an_unreadable_stranger() {
    // The choice names one account, and that one reads fine: an unreadable
    // credential for a different provider cannot make it ambiguous.
    let entries = entries(&[
        (ProviderIdentity::ClaudeAi, StoredEntry::Unreadable(AuthFailure::StoreUndecryptable)),
        (ProviderIdentity::GithubCopilot, StoredEntry::Present),
    ]);
    let selection = select_in_context(context(&entries, Ok(Some("github-copilot")), false))
        .expect("a named, readable account is unambiguous");
    assert_eq!(selection.identity, ProviderIdentity::GithubCopilot);
    assert_eq!(selection.source, SelectionSource::SavedDefault);
}

#[test]
fn an_unknown_saved_choice_is_reported_not_treated_as_absent() {
    let entries = entries(&[(ProviderIdentity::GithubCopilot, StoredEntry::Present)]);
    let selection = select_in_context(context(&entries, Ok(Some("openai")), true));
    assert!(
        matches!(selection, Err(SelectionError::UnknownProvider { .. })),
        "{selection:?}"
    );
}

#[test]
fn the_pure_table_and_the_context_agree_when_everything_is_readable() {
    let entries = entries(&[(ProviderIdentity::ClaudeAi, StoredEntry::Present)]);
    let stored = [ProviderIdentity::ClaudeAi];
    assert_eq!(
        select_in_context(context(&entries, Ok(None), true)),
        select_provider(SelectionInputs {
            stored: &stored,
            ambient_api_key: true,
            ..SelectionInputs::default()
        })
    );
}

