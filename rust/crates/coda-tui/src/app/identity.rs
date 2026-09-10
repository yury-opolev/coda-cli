//! What this session says it is: the startup banner and the exit summary.
//!
//! Both answer one question — *which account and model is this session
//! spending?* — and both get it wrong in the same way if they ask the wrong
//! machine. Kept together, and kept out of `app/mod.rs`, because "who owns
//! the settings the engine reads" is its own responsibility rather than more
//! of the event loop's.

use super::App;
use crate::branding::SettingsHost;
use crate::config::Settings;

/// What the startup banner needs before the UI takes over the terminal.
#[derive(Debug, Clone, Default)]
pub struct SessionSnapshot {
    pub provider: Option<String>,
    pub model: Option<String>,
    /// Whose settings decided them, which is what makes an unknown provider
    /// readable as "not signed in here" or "not reported by the engine".
    pub settings_host: SettingsHost,
}

impl App {
    /// What the startup banner needs to know before the UI takes the terminal.
    ///
    /// Where the answer comes from depends on who owns the settings the engine
    /// actually reads:
    ///
    /// - [`AccessMode::TrustedLocal`]: this process started its own core on
    ///   this machine, so this machine's settings *are* the engine's settings
    ///   and reading them names the account the first turn will spend.
    /// - [`AccessMode::ApiOnly`]: the engine is somebody else's process. Its
    ///   provider, model and credentials live on its host, and probing this
    ///   machine's `settings.json` would name an account this session is not
    ///   using — or announce "not signed in" while the engine is perfectly
    ///   well authenticated against its own credential. Only what the API
    ///   reported is used, and what has not been reported stays unknown
    ///   rather than being filled in from here.
    ///
    /// [`AccessMode::TrustedLocal`]: crate::local::AccessMode::TrustedLocal
    /// [`AccessMode::ApiOnly`]: crate::local::AccessMode::ApiOnly
    pub fn session_snapshot(&self) -> SessionSnapshot {
        if !self.access_mode.allows_local_maintenance() {
            return SessionSnapshot {
                provider: self.connected_provider.clone(),
                model: self.state.model.clone(),
                settings_host: SettingsHost::Engine,
            };
        }
        let settings = Settings::load(&self.paths).ok();
        // The engine's own answer wins even locally: it connects with the
        // credential it actually found, which is not always the one the
        // settings nominate.
        let provider = self
            .connected_provider
            .clone()
            .or_else(|| settings.as_ref().and_then(|s| s.default_provider().map(str::to_owned)));
        let model = self.state.model.clone().or_else(|| {
            let settings = settings.as_ref()?;
            let provider = provider.as_deref()?;
            settings.model_for(provider).map(str::to_owned)
        });
        SessionSnapshot { provider, model, settings_host: SettingsHost::Local }
    }

    /// Seeds the transcript with the startup banner.
    ///
    /// The banner belongs in the transcript, not on the raw console: written
    /// before the alternate screen it is hidden the moment the screen is
    /// entered, so the user never sees it at all. In the transcript it scrolls
    /// and can be selected like any other content.
    pub fn push_banner(&mut self, working_directory: &str) {
        let session = self.session_snapshot();
        self.state.transcript.push(crate::transcript::Block::Banner {
            wordmark: crate::branding::wordmark_lines(),
            details: crate::branding::startup_detail_lines(
                working_directory,
                session.provider.as_deref(),
                session.model.as_deref(),
                session.settings_host,
            ),
        });
        self.dirty = true;
    }

    /// Builds the exit summary from the session's final state.
    pub fn exit_summary(&self, duration: std::time::Duration) -> crate::branding::ExitSummary {
        let snapshot = self.session_snapshot();
        crate::branding::ExitSummary {
            duration,
            message_count: self.state.transcript.blocks().len(),
            provider_id: snapshot.provider.unwrap_or_else(|| "—".into()),
            model: snapshot.model.unwrap_or_else(|| "—".into()),
            effort: self.session_effort.clone(),
            input_tokens: self.state.usage.input_tokens.max(0) as u64,
            output_tokens: self.state.usage.output_tokens.max(0) as u64,
            session_id: self.state.session_id.clone(),
            working_directory: self.paths.project_root.to_string_lossy().into_owned(),
            settings_host: snapshot.settings_host,
        }
    }
}
