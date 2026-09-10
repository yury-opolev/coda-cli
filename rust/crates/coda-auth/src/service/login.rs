//! Preparing a login: the request, the host callbacks, and the prepared result.
//!
//! # The two phases, and why
//!
//! Authenticating is slow and interactive; committing is fast and destructive.
//! Keeping them apart is what lets a host stop and await its engine *between*
//! them, and what makes cancellation harmless:
//!
//! ```text
//! prepare_login   network + browser/device/prompt   writes nothing
//!      ↓
//! (host stops and awaits the engine it owns)
//!      ↓
//! commit_login    one credential + settings section  one terminal outcome
//!      ↓
//! (host starts a fresh engine and reads the real provider/model back)
//! ```
//!
//! A [`PreparedLogin`] holds live token material — except the one that
//! deliberately holds none: an [`ApiKeySource::Environment`] login carries the
//! *intent* to select the exported key, never a copy of it. It is deliberately
//! not `Clone`, its `Debug` is redacted, and dropping it before the commit
//! writes nothing at all: the loopback listener and the device poller live
//! inside `prepare_login`'s future, so cancelling that future closes them.
//!
//! # What the host owns
//!
//! [`LoginUi`] is the whole UI contract. The service never prints, masks,
//! launches a browser or keeps a device code: it hands them to the host for
//! the lifetime of one callback. Authorization URLs and device codes belong in
//! that ephemeral surface only — never in a transcript, a replay buffer, a
//! command history or a diagnostic log.

use async_trait::async_trait;

use crate::credential::Credential;
use crate::error::AuthError;
use crate::failure::AuthFailure;
use crate::provider::copilot::CopilotDeploymentChoice;
use crate::provider::DeviceCodePrompt;
use crate::secret::Secret;
use crate::service::identity::ProviderIdentity;
use crate::service::settings::AuthSettingsPatch;
use crate::service::transaction::Baseline;

/// Where an API key comes from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ApiKeySource {
    /// Ask the host for it ([`LoginUi::api_key`]), and store what is entered.
    Prompt,
    /// Use the `ANTHROPIC_API_KEY` already exported in this process, and store
    /// **nothing**.
    ///
    /// What the commit records is the selection: `defaultProvider` becomes the
    /// console key, the stored key it replaces is removed, and the value keeps
    /// living in the environment that exported it. A later process without the
    /// variable therefore has a chosen provider and no credential, which fails
    /// closed rather than falling back to something else.
    Environment,
}

/// What to do when a credential cannot be *checked* (as opposed to being
/// rejected).
///
/// The default refuses: silently storing a key that could not be validated,
/// after deleting the account that was working, is the failure mode this
/// exists to prevent.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum UnverifiedPolicy {
    /// Refuse to prepare a login whose credential could not be checked.
    #[default]
    Refuse,
    /// The host asked the user and they chose to proceed anyway.
    AllowedByHost,
}

/// What kind of login to prepare.
#[derive(Debug, Clone)]
pub struct LoginRequest {
    pub(crate) identity: ProviderIdentity,
    pub(crate) api_key_source: ApiKeySource,
    pub(crate) deployment: Option<CopilotDeploymentChoice>,
    pub(crate) validate: bool,
    pub(crate) unverified: UnverifiedPolicy,
}

impl LoginRequest {
    /// Sign in to a Claude.ai subscription through the browser.
    pub fn claude_ai() -> Self {
        Self {
            identity: ProviderIdentity::ClaudeAi,
            api_key_source: ApiKeySource::Prompt,
            deployment: None,
            validate: false,
            unverified: UnverifiedPolicy::default(),
        }
    }

    /// Sign in with an Anthropic console API key.
    ///
    /// Validation is on by default: unlike an OAuth exchange, entering a key
    /// proves nothing, and this login may delete the account that currently
    /// works.
    pub fn api_key(source: ApiKeySource) -> Self {
        Self {
            identity: ProviderIdentity::AnthropicApiKey,
            api_key_source: source,
            deployment: None,
            validate: true,
            unverified: UnverifiedPolicy::default(),
        }
    }

    /// Sign in to GitHub Copilot with the device-code flow.
    pub fn copilot(deployment: Option<CopilotDeploymentChoice>) -> Self {
        Self {
            identity: ProviderIdentity::GithubCopilot,
            api_key_source: ApiKeySource::Prompt,
            deployment,
            validate: false,
            unverified: UnverifiedPolicy::default(),
        }
    }

    /// The identity this request signs in to.
    pub fn identity(&self) -> ProviderIdentity {
        self.identity
    }

    /// Skip the pre-commit credential check.
    ///
    /// A convenience for a **first** sign-in, which displaces nothing: if this
    /// login would remove an account that currently works, the check runs
    /// anyway. Trading a working connection for an unchecked one is not
    /// something a convenience flag may decide — see
    /// [`UnverifiedPolicy::AllowedByHost`] for the decision that can.
    pub fn without_validation(mut self) -> Self {
        self.validate = false;
        self
    }

    /// What to do when validation cannot be performed.
    pub fn with_unverified_policy(mut self, policy: UnverifiedPolicy) -> Self {
        self.unverified = policy;
        self
    }
}

/// The host's side of an interactive login.
///
/// Every method may return [`AuthError::LoginCancelled`], which aborts the
/// login without touching anything. The defaults do exactly that, so a host
/// implements only the flows it offers.
#[async_trait]
pub trait LoginUi: Send + Sync {
    /// Collect an API key. The host owns masking; the service never sees a
    /// terminal, and receives the value as a [`Secret`], not a literal.
    async fn api_key(&self) -> Result<Secret<String>, AuthError> {
        Err(AuthError::LoginCancelled("this host cannot collect an API key".into()))
    }

    /// Show (and usually open) the authorization URL.
    ///
    /// The URL is live for one login only. It must not be written to a
    /// transcript, a log or the clipboard history.
    async fn authorization_url(&self, _url: &str) -> Result<(), AuthError> {
        Err(AuthError::LoginCancelled("this host cannot open a browser login".into()))
    }

    /// Show a device code and its verification URL, for as long as the login
    /// runs and no longer.
    async fn device_code(&self, _prompt: DeviceCodePrompt) -> Result<(), AuthError> {
        Err(AuthError::LoginCancelled("this host cannot show a device code".into()))
    }

    /// Choose the Copilot deployment when the request did not.
    ///
    /// `Ok(None)` means "use the configured default", which is what the engine
    /// does; a host that asks the user returns their answer.
    async fn copilot_deployment(
        &self,
        _saved_domain: Option<&str>,
    ) -> Result<Option<CopilotDeploymentChoice>, AuthError> {
        Ok(None)
    }
}

/// Why a login could not be prepared.
///
/// All of these leave the profile exactly as it was.
#[derive(Debug)]
pub enum PrepareFailure {
    /// The user cancelled, denied, or let the login expire.
    Cancelled,
    /// Environment-only authentication was requested without an exported key.
    EnvironmentKeyMissing,
    /// The credential was refused: a blank key, a rejected exchange, a
    /// credential the provider says is invalid.
    Rejected(AuthFailure),
    /// The Anthropic endpoint this process is configured with was refused.
    ///
    /// Raised **before** any key is read, entered or sent: a login that cannot
    /// establish where the key would go must not send it anywhere, and must
    /// never quietly fall back to Anthropic's own host for a user who pointed
    /// Coda at a gateway.
    EndpointRejected { reason: crate::service::endpoint::EndpointError },
    /// The credential could not be *checked* — the check itself was
    /// unavailable. Not the same as a rejection, and not something to proceed
    /// through silently.
    ///
    /// `displaces_a_working_account` says whether accepting it anyway would
    /// also delete an account that currently works, which is the case where a
    /// host must ask rather than decide.
    ValidationUnavailable {
        reason: AuthFailure,
        displaces_a_working_account: bool,
    },
    /// Anything else (store, configuration, transport).
    Failed(AuthFailure),
}

impl PrepareFailure {
    pub(crate) fn from_error(error: &AuthError) -> Self {
        let failure = AuthFailure::classify(error);
        match failure {
            AuthFailure::Cancelled => Self::Cancelled,
            AuthFailure::InvalidInput
            | AuthFailure::StateMismatch
            | AuthFailure::OAuthRejected { .. } => Self::Rejected(failure),
            _ => Self::Failed(failure),
        }
    }

    /// The safe classification behind this failure, if any.
    pub fn failure(&self) -> Option<AuthFailure> {
        match self {
            Self::Cancelled => Some(AuthFailure::Cancelled),
            Self::EnvironmentKeyMissing => Some(AuthFailure::NoCredential),
            Self::EndpointRejected { .. } => Some(AuthFailure::InvalidEndpoint),
            Self::Rejected(failure) | Self::Failed(failure) => Some(*failure),
            Self::ValidationUnavailable { reason, .. } => Some(*reason),
        }
    }
}

impl std::fmt::Display for PrepareFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Cancelled => f.write_str("sign-in was cancelled; nothing was changed"),
            Self::EnvironmentKeyMissing => f.write_str(
                "ANTHROPIC_API_KEY is not set in this process; export it before using \
                 --use-env, or sign in with a securely entered API key. Nothing was changed",
            ),
            Self::Rejected(failure) => write!(f, "sign-in was refused: {failure}"),
            Self::EndpointRejected { reason } => write!(
                f,
                "the Anthropic endpoint configured by {} was refused ({reason}); nothing was \
                 changed, and no API key was read or sent. Correct that variable, or unset it to \
                 use Anthropic's own host",
                crate::service::endpoint::ANTHROPIC_BASE_URL_ENV,
            ),
            Self::ValidationUnavailable { reason, displaces_a_working_account } => {
                write!(f, "the credential could not be checked ({reason}); ")?;
                if *displaces_a_working_account {
                    f.write_str(
                        "the account you are signed in to was left connected. Retry, or \
                         explicitly choose to replace it without checking",
                    )
                } else {
                    f.write_str(
                        "nothing was changed. Retry, or explicitly choose to continue without \
                         checking",
                    )
                }
            }
            Self::Failed(failure) => write!(f, "sign-in failed: {failure}"),
        }
    }
}

impl std::error::Error for PrepareFailure {}

/// Evidence that the credential in a [`PreparedLogin`] actually authenticates.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AuthEvidence {
    /// The provider exchanged a token for it. This *is* proof of
    /// authentication; a later model-listing failure does not retract it.
    TokenExchange,
    /// A live, uncached probe with this credential succeeded.
    ProbeSucceeded,
    /// Nothing has checked it yet — a well-formed key is not a working one.
    None,
}

/// An authenticated credential that has not been stored yet.
///
/// Not `Clone`: there is exactly one of these per login, and committing
/// consumes it. Dropping it before the commit writes nothing.
pub struct PreparedLogin {
    pub(crate) identity: ProviderIdentity,
    pub(crate) credential: PreparedCredential,
    pub(crate) settings: AuthSettingsPatch,
    pub(crate) baseline: Baseline,
    pub(crate) evidence: AuthEvidence,
    pub(crate) replaces: Vec<ProviderIdentity>,
    pub(crate) deployment: Option<String>,
    /// The Copilot endpoints this login actually authenticated against.
    ///
    /// Carried to the commit so the resolved configuration — not a fresh
    /// resolution that an exported domain could override — becomes the context
    /// every later refresh of this credential uses.
    pub(crate) copilot: Option<crate::provider::copilot::ResolvedCopilotConfig>,
}

/// What a prepared login will persist.
///
/// The two cases are not interchangeable, and the difference is the whole
/// point of the type: one login has a credential to save, the other has an
/// *environment variable to select*, and reporting the second as the first is
/// how a user is told a secret was stored that never was.
pub(crate) enum PreparedCredential {
    /// A credential to write into the profile.
    Stored(Credential),
    /// Nothing is written. `ANTHROPIC_API_KEY` stays in the process that
    /// exported it; the commit records the choice and removes the stored key
    /// it replaces.
    ///
    /// The value read during preparation lives only long enough to be checked
    /// and is dropped there: it is deliberately not carried here, so there is
    /// no copy for a commit, a log line or a `Debug` to reach.
    Environment,
}

impl PreparedCredential {
    /// A safe description that never claims a key was kept.
    fn describe(&self) -> &'static str {
        match self {
            Self::Stored(_) => "[REDACTED]",
            Self::Environment => "none (ANTHROPIC_API_KEY is used in place)",
        }
    }
}

impl std::fmt::Debug for PreparedLogin {
    /// Never prints token material, an authorization URL or a device code.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PreparedLogin")
            .field("identity", &self.identity)
            .field("credential", &self.credential.describe())
            .field("evidence", &self.evidence)
            .field("replaces", &self.replaces)
            .field("deployment", &self.deployment)
            .finish()
    }
}

impl PreparedLogin {
    /// The identity that will be signed in.
    pub fn identity(&self) -> ProviderIdentity {
        self.identity
    }

    /// Whether committing this login stores **no key**, using the exported
    /// `ANTHROPIC_API_KEY` instead.
    ///
    /// A host must consult this before it tells the user what was saved, and
    /// before it decides what to check the connection with: there will be no
    /// stored credential to read afterwards.
    pub fn uses_environment_key(&self) -> bool {
        matches!(self.credential, PreparedCredential::Environment)
    }

    /// The stored accounts this login will remove. The host must disclose
    /// these before committing.
    ///
    /// For an environment login this can include the identity being signed in
    /// to: choosing the exported key deletes the *stored* console key as well,
    /// and a disclosure that left it out would understate what is lost.
    pub fn replaces(&self) -> &[ProviderIdentity] {
        &self.replaces
    }

    /// The Copilot deployment this login authenticated against, when it is not
    /// the public default. Safe to display: a host name, never a secret.
    pub fn deployment(&self) -> Option<&str> {
        self.deployment.as_deref()
    }

    /// Whether the credential has been proven to work.
    ///
    /// An OAuth token exchange counts: the provider issued the token. An API
    /// key counts only after a live probe.
    pub fn is_verified(&self) -> bool {
        !matches!(self.evidence, AuthEvidence::None)
    }

    /// How the credential was proven.
    pub fn evidence(&self) -> AuthEvidence {
        self.evidence
    }
}
