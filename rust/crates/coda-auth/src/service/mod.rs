//! The shared authentication service: provider selection, login, logout and
//! status for the whole product.
//!
//! # Ownership
//!
//! Everything here is *host-local maintenance*. The service owns the
//! credential transaction and nothing else:
//!
//! * it never spawns, kills or repoints an engine — the host does that, in the
//!   order documented on [`AuthService::commit_login`];
//! * it never opens a browser, masks input or prints anything — that is the
//!   host's [`LoginUi`];
//! * it never refreshes a token merely to render status.
//!
//! # Modules
//!
//! * [`endpoint`] — where an Anthropic API-key request is sent.
//! * [`identity`] — the three account identities and the one alias table.
//! * [`selection`] — the one provider selector, shared with the engine.
//! * [`settings`] — the writable settings port the transaction commits through.
//! * [`transaction`] — the credential/settings commit, with rollback.
//! * [`login`] — the prepare/commit login types and the [`LoginUi`] port.
//! * [`status`] — stored-metadata status, with no network and no migration.
//! * [`verify`] — an uncached connection probe.

pub mod copilot_context;
pub mod endpoint;
pub mod environment;
pub mod identity;
pub mod login;
pub mod selection;
pub mod settings;
pub mod status;
pub mod transaction;
pub mod verify;

mod service;

pub use copilot_context::{
    copilot_connection, ContextBoundCopilotProvider, ContextScopedSource, CopilotConnection,
    CopilotContext, CopilotContextCell, PendingCopilotContext,
};
pub use endpoint::{
    AnthropicEndpoint, EndpointError, EndpointSource, ANTHROPIC_BASE_URL_ENV,
    DEFAULT_ANTHROPIC_BASE_URL,
};
pub use environment::{AuthEnvironment, MapEnvironment, ProcessEnvironment};
pub use identity::{canonical_engine_provider, ProviderIdentity};
pub use login::{
    ApiKeySource, AuthEvidence, LoginRequest, LoginUi, PrepareFailure, PreparedLogin,
    UnverifiedPolicy,
};
pub use selection::{
    select_in_context, select_provider, CredentialOrigin, Selection, SelectionContext,
    SelectionError, SelectionInputs, SelectionSource, StoredEntry,
};
pub use service::{AuthService, AuthServiceBuilder, CommitHandle, CLAUDE_AI_CLIENT_ID};
pub use settings::{AuthSettings, AuthSettingsPatch, AuthSettingsPort, InMemoryAuthSettings};
pub use status::{
    read_provider_states, read_stored_state, selection_entries, AuthStatus, CredentialSummary,
    LogoutFailure, LogoutReport, ProviderStatus, StoredState,
};
pub use transaction::{Baseline, CommitInterruption, CommitOutcome, CommitStep};
pub use verify::{
    verify_client, verify_client_with_source, UnverifiedReason, VerificationOutcome,
    VerificationReport,
};

