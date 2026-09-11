//! Reading the engine's model catalogue and turning it into what the status
//! bar, the picker and the effort UI show.
//!
//! One entry point — [`App::ingest_models_result`] — so startup, a model
//! switch, `/model` and the browser's own (re)load all learn the same
//! provider-scoped display names the same way. A second read that skipped
//! this would leave one of those surfaces trusting a name the others had
//! already replaced.

use coda_proto::messages::{self, method};
use super::App;
use crate::state::UiEvent;

impl App {
    /// Records what a `session/models` read said, in the one place every
    /// caller shares.
    ///
    /// Three things happen, always together: the provider a model switch
    /// must be saved under is remembered (the engine's own credential, not
    /// whatever settings nominate); the friendly names the picker, `/model`
    /// and a later resync all read are cached, scoped to that provider and
    /// wholly replaced rather than merged, so a name that changed or a model
    /// that vanished cannot leave a stale label behind; and, when the result
    /// names an active model, the status bar is told about it right away
    /// rather than waiting for whatever next touches `state.model`.
    pub(super) fn ingest_models_result(&mut self, result: &messages::ModelsResult) {
        if let Some(provider) = result.provider_id.clone() {
            self.connected_provider = Some(provider);
        }
        self.state
            .model_labels
            .replace(result.provider_id.clone(), &result.models);
        if let Some(id) = result.model.as_deref() {
            let label = self.state.model_labels.resolve(result.provider_id.as_deref(), id).to_owned();
            let context_limit = result.active_context_limit();
            let price = result.active_price();
            self.apply(UiEvent::ModelChanged { id: label, context_limit });
            // Carried on the state so the renderer can show a running cost
            // without reaching for a catalogue of its own.
            self.state.usage.price_per_million = price;
        }
    }

    /// Fetches the model list so the status bar can name the active model.
    ///
    /// The engine reports which model is active; the list is only how it is
    /// labelled. Taking the first entry instead named whatever the provider
    /// happened to return first, so the status bar could disagree with the
    /// engine and switching a model looked as though it had not been saved.
    pub(super) async fn load_models(&mut self) {
        if !self.engine_connected {
            return;
        }
        // Bounded like every other read the loop awaits: this one runs at
        // startup and after a model switch, and an unbounded await here would
        // freeze the UI before it had drawn a single frame.
        let Ok(value) = self
            .bounded(
                self.connection
                    .request(method::MODELS, Some(serde_json::json!({ "refresh": false }))),
            )
            .await
        else {
            return;
        };
        let Ok(result) = serde_json::from_value::<messages::ModelsResult>(value) else {
            return;
        };
        self.ingest_models_result(&result);
        self.refresh_effort().await;
    }
}
