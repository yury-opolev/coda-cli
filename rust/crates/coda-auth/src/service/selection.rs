//! The one provider selector.
//!
//! Selecting a provider selects *an account*. That is why this function is
//! pure, total, and shared: the engine at startup and the auth service (status,
//! login, logout) must give the same answer, and neither may resolve a choice
//! by iterating a map.
//!
//! The rules, in order:
//!
//! 1. **An explicit provider wins and fails closed.** `--provider claude-ai`
//!    with no Claude credential is [`SelectionError::NeedsLogin`], never a
//!    different account that happens to be available.
//! 2. **A saved `defaultProvider` is also a choice**, and fails closed the same
//!    way. A user who chose an account must not be quietly signed in to
//!    another one because their credential went missing.
//! 3. **Only an unconfigured start may choose for itself**, and then only
//!    unambiguously: the sole stored credential, or — when nothing at all is
//!    stored — an ambient `ANTHROPIC_API_KEY`. A deliberate login outranks an
//!    exported variable.
//! 4. **Several stored credentials are ambiguous**, reported rather than
//!    resolved.
//! 5. **Nothing stored and no ambient key is not an authenticated fallback**:
//!    it is [`SelectionError::NoCredentials`], which the engine starts up on as
//!    a client-less discovery state.
//!
//! The environment can only ever answer for the identity it belongs to. An
//! exported `ANTHROPIC_API_KEY` is a console key: it is not a Claude.ai
//! subscription and must never stand in for one.

use crate::service::identity::ProviderIdentity;

/// Why a provider was chosen.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SelectionSource {
    /// Named by the caller (`--provider`, a slash command, a CLI argument).
    Explicit,
    /// The saved `defaultProvider` in settings.
    SavedDefault,
    /// Nothing was configured and exactly one credential is stored.
    SoleStored,
    /// Nothing was configured, nothing is stored, and `ANTHROPIC_API_KEY` is
    /// exported.
    AmbientEnvKey,
}

/// Where the credential for the selected identity comes from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CredentialOrigin {
    /// The profile's credential store.
    Stored,
    /// The process environment (`ANTHROPIC_API_KEY`).
    Environment,
}

/// A resolved selection: which identity, why, and on the strength of which
/// credential.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Selection {
    pub identity: ProviderIdentity,
    pub source: SelectionSource,
    pub origin: CredentialOrigin,
}

/// Why no provider could be selected.
///
/// Every variant is a distinct thing to tell the user. In particular
/// `NeedsLogin` (a chosen account with no credential) is not `NoCredentials`
/// (nothing configured at all): the first must not be answered by signing in
/// to something else, and the second is a first-run state, not a failure.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SelectionError {
    /// The chosen provider has no usable credential.
    NeedsLogin { identity: ProviderIdentity, source: SelectionSource },
    /// The name is not one of this product's providers.
    UnknownProvider { requested: String },
    /// More than one credential is stored and nothing chose between them.
    Ambiguous { stored: Vec<ProviderIdentity> },
    /// Nothing is configured and nothing is available.
    NoCredentials,
    /// The profile could not be read at all.
    ///
    /// Never collapse this into `NoCredentials`: a locked or unreadable store
    /// may be holding the credential the user is signed in with, and telling
    /// them to sign in again invites overwriting it.
    Unavailable { failure: crate::failure::AuthFailure },
}

/// The facts a selection is made from.
///
/// Gathering them is the caller's job (the store for `stored`, settings for
/// `saved_default`, the environment for `ambient_api_key`) so this function
/// stays pure and both hosts can be tested against the same table.
#[derive(Debug, Clone, Copy, Default)]
pub struct SelectionInputs<'a> {
    /// A provider named by this invocation.
    pub explicit: Option<&'a str>,
    /// The saved `defaultProvider`.
    pub saved_default: Option<&'a str>,
    /// Identities with a stored credential.
    pub stored: &'a [ProviderIdentity],
    /// Whether `ANTHROPIC_API_KEY` is set in this process' environment.
    pub ambient_api_key: bool,
}

/// What one identity's slot in the profile reads as.
///
/// The third case is the reason this type exists. "There is no credential" and
/// "there is a credential I cannot read" look the same to a caller that only
/// collects the ids that parsed, and the difference decides whether the user
/// is told to sign in — which overwrites what is still there — or told that
/// their store cannot be read.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StoredEntry {
    /// Nothing is stored for this identity.
    Absent,
    /// A credential is stored and readable.
    Present,
    /// Something is stored but could not be read.
    Unreadable(crate::failure::AuthFailure),
}

impl StoredEntry {
    fn is_present(&self) -> bool {
        matches!(self, Self::Present)
    }

    fn unreadable(&self) -> Option<crate::failure::AuthFailure> {
        match self {
            Self::Unreadable(failure) => Some(*failure),
            _ => None,
        }
    }
}

/// Everything a selection is made from, including what could not be read.
///
/// `saved_default` is a `Result` on purpose: a settings file that cannot be
/// read is not a profile with no saved choice. Passing `Ok(None)` for an
/// unreadable file is precisely the fail-open bug this type prevents.
#[derive(Debug, Clone, Copy)]
pub struct SelectionContext<'a> {
    /// A provider named by this invocation.
    pub explicit: Option<&'a str>,
    /// The saved `defaultProvider`, or why it could not be read.
    pub saved_default: Result<Option<&'a str>, crate::failure::AuthFailure>,
    /// Every identity's slot, readable or not.
    pub entries: &'a [(ProviderIdentity, StoredEntry)],
    /// Whether `ANTHROPIC_API_KEY` is set in this process' environment.
    pub ambient_api_key: bool,
}

impl<'a> SelectionContext<'a> {
    fn entry(&self, identity: ProviderIdentity) -> &StoredEntry {
        self.entries
            .iter()
            .find(|(candidate, _)| *candidate == identity)
            .map(|(_, state)| state)
            .unwrap_or(&StoredEntry::Absent)
    }

    fn present(&self) -> Vec<ProviderIdentity> {
        let mut present: Vec<ProviderIdentity> = self
            .entries
            .iter()
            .filter(|(_, state)| state.is_present())
            .map(|(identity, _)| *identity)
            .collect();
        present.sort();
        present.dedup();
        present
    }

    fn first_unreadable(&self) -> Option<crate::failure::AuthFailure> {
        self.entries.iter().find_map(|(_, state)| state.unreadable())
    }
}

/// Resolve a provider from a context that knows what it could not read.
///
/// The rules of [`select_provider`] apply, with three refusals in front of
/// them:
///
/// * the settings could not be read → [`SelectionError::Unavailable`]; the
///   saved choice is unknown, and neither an ambient key nor the one
///   credential that happens to parse may stand in for it;
/// * a *named* choice whose own credential is unreadable → `Unavailable`, not
///   `NeedsLogin`: sending that user through a login overwrites a credential
///   that is still recoverable;
/// * *nothing named* and any credential unreadable → `Unavailable`, because
///   "this is the only stored account" is exactly the conclusion an unreadable
///   slot makes unavailable.
///
/// A named choice that reads fine still resolves even when some *other*
/// identity is unreadable: the name settles which account is meant, so nothing
/// is being guessed.
pub fn select_in_context(context: SelectionContext<'_>) -> Result<Selection, SelectionError> {
    let saved_default = match context.saved_default {
        Ok(saved) => saved,
        Err(failure) => return Err(SelectionError::Unavailable { failure }),
    };

    let chosen = named(context.explicit)
        .map(|name| (name, SelectionSource::Explicit))
        .or_else(|| named(saved_default).map(|name| (name, SelectionSource::SavedDefault)));

    if let Some((name, source)) = chosen {
        let Some(identity) = ProviderIdentity::parse(name) else {
            return Err(SelectionError::UnknownProvider { requested: name.trim().to_owned() });
        };
        let entry = context.entry(identity);
        if let Some(failure) = entry.unreadable() {
            return Err(SelectionError::Unavailable { failure });
        }
        if entry.is_present() {
            return Ok(Selection { identity, source, origin: CredentialOrigin::Stored });
        }
        // The environment only ever carries the console API key, and it stands
        // for that identity alone.
        if context.ambient_api_key && identity == ProviderIdentity::AnthropicApiKey {
            return Ok(Selection { identity, source, origin: CredentialOrigin::Environment });
        }
        return Err(SelectionError::NeedsLogin { identity, source });
    }

    // Nothing was named, so the answer would have to be inferred from what is
    // stored — and an unreadable slot means we do not know what is stored.
    if let Some(failure) = context.first_unreadable() {
        return Err(SelectionError::Unavailable { failure });
    }

    match context.present().as_slice() {
        [] => {
            if context.ambient_api_key {
                Ok(Selection {
                    identity: ProviderIdentity::AnthropicApiKey,
                    source: SelectionSource::AmbientEnvKey,
                    origin: CredentialOrigin::Environment,
                })
            } else {
                Err(SelectionError::NoCredentials)
            }
        }
        // A deliberate login outranks an exported variable.
        [only] => Ok(Selection {
            identity: *only,
            source: SelectionSource::SoleStored,
            origin: CredentialOrigin::Stored,
        }),
        several => Err(SelectionError::Ambiguous { stored: several.to_vec() }),
    }
}

/// Resolve a provider from the given facts. See the module docs for the rules.
///
/// This is the readable-inputs form: every identity not in `stored` is taken
/// to be absent, and the saved choice is taken to have been read
/// successfully. A caller that gathers those facts from a real profile must
/// use [`select_in_context`], which can say "I could not read this".
pub fn select_provider(inputs: SelectionInputs<'_>) -> Result<Selection, SelectionError> {
    let entries: Vec<(ProviderIdentity, StoredEntry)> = ProviderIdentity::ALL
        .into_iter()
        .map(|identity| {
            let state = if inputs.stored.contains(&identity) {
                StoredEntry::Present
            } else {
                StoredEntry::Absent
            };
            (identity, state)
        })
        .collect();

    select_in_context(SelectionContext {
        explicit: inputs.explicit,
        saved_default: Ok(inputs.saved_default),
        entries: &entries,
        ambient_api_key: inputs.ambient_api_key,
    })
}

/// A blank string is not a name: it is the absence of a choice.
fn named(value: Option<&str>) -> Option<&str> {
    value.filter(|name| !name.trim().is_empty())
}

impl std::fmt::Display for SelectionError {
    /// Safe, closed wording: no store keys, no server text, no secrets.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::NeedsLogin { identity, source } => {
                let chosen = match source {
                    SelectionSource::Explicit => "requested",
                    _ => "saved as the default",
                };
                match identity {
                    // The console key can come from three places, and two of
                    // them are not a login: say so, or a user holding a key is
                    // told to sign in for no reason.
                    ProviderIdentity::AnthropicApiKey => write!(
                        f,
                        "the provider {chosen} (anthropic) has no API key available \
                         (pass --api-key, set ANTHROPIC_API_KEY, or sign in first)",
                    ),
                    _ => write!(
                        f,
                        "the provider {chosen} ({}) has no stored credential; sign in first",
                        identity.engine_id()
                    ),
                }
            }
            Self::UnknownProvider { requested } => write!(
                f,
                "unknown provider '{}' (expected anthropic, claude-ai, or github-copilot)",
                crate::error::sanitize_key(requested)
            ),
            Self::Ambiguous { stored } => {
                let names: Vec<&str> = stored.iter().map(|i| i.engine_id()).collect();
                write!(
                    f,
                    "more than one provider has a stored credential ({}); sign out and sign in again",
                    names.join(", ")
                )
            }
            Self::NoCredentials => f.write_str("no provider is configured and no credential is available"),
            Self::Unavailable { failure } => write!(
                f,
                "the saved credentials could not be read ({failure}); they were left untouched",
            ),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_duplicated_stored_entry_is_still_one_account() {
        let stored = [ProviderIdentity::ClaudeAi, ProviderIdentity::ClaudeAi];
        let selection = select_provider(SelectionInputs { stored: &stored, ..Default::default() })
            .expect("the same identity twice is not an ambiguity");
        assert_eq!(selection.identity, ProviderIdentity::ClaudeAi);
    }

    #[test]
    fn the_messages_never_carry_untrusted_text_verbatim() {
        let error = SelectionError::UnknownProvider { requested: "a\u{0}b".repeat(80) };
        let rendered = error.to_string();
        assert!(!rendered.contains('\u{0}'));
        assert!(rendered.len() < 200);
    }
}
