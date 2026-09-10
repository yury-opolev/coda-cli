//! The ephemeral surfaces an authentication flow runs on.
//!
//! # Why these are the only place a challenge may appear
//!
//! An authorization URL is a live capability: whoever opens it finishes the
//! sign-in. A device code is the same for the length of its window, and an API
//! key is a secret outright. None of them may reach the transcript, the replay
//! buffer, the command history, the clipboard or the diagnostic log — all of
//! which outlive the login. They live here, on an [`Modality::Exclusive`]
//! surface that exists for exactly as long as the flow does, and are gone the
//! moment it ends.
//!
//! # Built from the toolkit, not from scratch
//!
//! Both surfaces are [`Form`]s. Masking, caret placement, focus and scrolling
//! are the toolkit's, so this module contributes the questions and nothing
//! else — in particular it contributes no key handling of its own beyond
//! "Escape cancels the flow", which an exclusive surface has to state itself
//! because the stack deliberately will not dismiss one.

use coda_auth::provider::copilot::CopilotDeploymentChoice;
use coda_auth::service::ProviderIdentity;
use coda_render::theme::Theme;
use crossterm::event::{KeyCode, KeyEvent};
use ratatui::layout::Rect;
use ratatui::text::Line;

use super::form::{form_cursor, render_form};
use super::{Modality, Placement, Surface, SurfaceAction, SurfaceOutcome};
use crate::widgets::{Form, FormOutcome, RadioGroup, StaticText, TextInput};

/// What the user chose, read back off the surface after it submits.
///
/// The key is carried here and nowhere else: it never becomes part of a
/// [`SurfaceAction`], which is `Debug` and `Clone` and travels through the
/// application's action log.
pub struct AuthChoice {
    pub identity: ProviderIdentity,
    /// An explicitly chosen Copilot deployment. `None` means "whatever is
    /// configured", which is what the engine does.
    pub deployment: Option<CopilotDeploymentChoice>,
    /// Use the exported `ANTHROPIC_API_KEY` and store no key.
    pub use_environment_key: bool,
    /// The key that was typed, when one was. Never logged, never displayed.
    pub api_key: Option<String>,
}

impl std::fmt::Debug for AuthChoice {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AuthChoice")
            .field("identity", &self.identity)
            .field("deployment", &self.deployment)
            .field("use_environment_key", &self.use_environment_key)
            .field("api_key", &self.api_key.as_ref().map(|_| "[REDACTED]"))
            .finish()
    }
}

// Control indices, named so the reader of `choice()` is not counting rows.
const ROW_ACCOUNT: usize = 1;
const ROW_DEPLOYMENT: usize = 3;
const ROW_DOMAIN: usize = 4;
const ROW_KEY_SOURCE: usize = 6;
const ROW_KEY: usize = 7;

const ACCOUNTS: [ProviderIdentity; 3] = ProviderIdentity::ALL;
const DEPLOYMENT_PUBLIC: usize = 0;
const DEPLOYMENT_ENTERPRISE: usize = 1;
const DEPLOYMENT_CONFIGURED: usize = 2;
const KEY_SOURCE_ENVIRONMENT: usize = 1;

/// Choose an account, disclose what it replaces, and collect a key.
pub struct AuthChoiceSurface {
    form: Form,
    title: String,
    /// The account this screen may connect, when the launch made it a
    /// condition rather than a suggestion.
    ///
    /// An explicit `--provider` is an instruction about *which account this
    /// session talks to*. Letting the wizard connect a different one and then
    /// launching anyway ended in the worst place available: the engine was
    /// started for the account that was just signed in to, and the launch's
    /// own verification then failed it for not being the one that was asked
    /// for — so a successful sign-in read as a broken start.
    required: Option<ProviderIdentity>,
    /// A refusal the form is showing, from the last submit that was rejected.
    ///
    /// Held here rather than rewritten into the form's text so the values the
    /// user typed survive the rejection: the point of refusing is to let them
    /// correct one field, not to make them start again.
    error: Option<String>,
}

/// Why an enterprise domain was refused, or `Ok` with the normalised host.
///
/// Applied **before** anything is sent. A malformed tenant is not a network
/// error to discover later: `resolve_copilot_config` would turn it into
/// endpoints, and a device authorization is a credential handed to whatever
/// answers there.
pub fn validate_enterprise_domain(raw: &str) -> Result<String, String> {
    let domain = raw.trim().trim_end_matches('.').to_ascii_lowercase();
    if domain.is_empty() {
        return Err(
            "Enter the GitHub Enterprise domain to sign in to (for example octocorp.ghe.com), \
             or choose a different deployment. Nothing was sent."
                .to_owned(),
        );
    }
    if domain.contains("://") || domain.contains('/') || domain.contains('@') {
        return Err(
            "Enter the domain only — no scheme, path, credentials or trailing slash (for \
             example octocorp.ghe.com). Nothing was sent."
                .to_owned(),
        );
    }
    if domain.contains(':') {
        return Err(
            "Enter the domain only — a port is not part of a GitHub Enterprise tenant name. \
             Nothing was sent."
                .to_owned(),
        );
    }
    if domain.len() > 253 {
        return Err("That domain is too long to be a host name. Nothing was sent.".to_owned());
    }
    let labelled = domain.split('.').collect::<Vec<_>>();
    let plausible = labelled.len() >= 2
        && labelled.iter().all(|label| {
            !label.is_empty()
                && label.len() <= 63
                && !label.starts_with('-')
                && !label.ends_with('-')
                && label
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || c == '-')
        });
    if !plausible {
        return Err(
            "That is not a host name Coda can send a device authorization to (expected \
             something like octocorp.ghe.com). Nothing was sent."
                .to_owned(),
        );
    }
    Ok(domain)
}

impl AuthChoiceSurface {
    /// `disclosure` is what the flow already knows: which accounts are stored,
    /// what this sign-in would remove, and where an Anthropic API key would be
    /// sent. It is shown **before** any key is entered or any probe is made.
    pub fn open(
        title: impl Into<String>,
        disclosure: Vec<String>,
        preselect: Option<ProviderIdentity>,
        saved_domain: Option<String>,
    ) -> Self {
        let selected = preselect
            .and_then(|identity| ACCOUNTS.iter().position(|candidate| *candidate == identity))
            .unwrap_or(0);
        let deployment = if saved_domain.is_some() {
            DEPLOYMENT_CONFIGURED
        } else {
            DEPLOYMENT_PUBLIC
        };
        let heading = if disclosure.is_empty() {
            "Choose the account to connect.".to_owned()
        } else {
            disclosure.join("\n")
        };

        let form = Form::new(vec![
            Box::new(StaticText::new(heading)),
            Box::new(
                RadioGroup::new(
                    "Account",
                    ACCOUNTS.iter().map(|identity| identity.label().to_owned()).collect(),
                )
                .with_selected(selected),
            ),
            Box::new(StaticText::new(
                "GitHub Copilot only — a public account token must never be sent to an \
                 enterprise tenant, so the deployment is chosen here rather than inherited.",
            )),
            Box::new(
                RadioGroup::new(
                    "Copilot deployment",
                    vec![
                        "Public github.com".to_owned(),
                        "GitHub Enterprise (domain below)".to_owned(),
                        "Whatever this profile has saved".to_owned(),
                    ],
                )
                .with_selected(deployment),
            ),
            Box::new(
                TextInput::new("Enterprise domain")
                    .with_placeholder("octocorp.ghe.com")
                    .with_value(saved_domain.unwrap_or_default()),
            ),
            Box::new(StaticText::new(
                "Anthropic API key only — an environment login stores no key at all.",
            )),
            Box::new(RadioGroup::new(
                "API key source",
                vec![
                    "Enter a key (hidden, stored for this profile)".to_owned(),
                    "Use ANTHROPIC_API_KEY from this environment (nothing is stored)".to_owned(),
                ],
            )),
            Box::new(TextInput::new("Anthropic API key").masked()),
        ]);

        // The caret starts where this account's next answer goes: naming
        // `api-key` and then having to tab past three questions that do not
        // apply to it is how a key ends up typed into the wrong box.
        let mut form = form;
        let focus = match preselect {
            Some(ProviderIdentity::AnthropicApiKey) => ROW_KEY,
            Some(ProviderIdentity::GithubCopilot) => ROW_DEPLOYMENT,
            _ => ROW_ACCOUNT,
        };
        while form.focused_index() != focus {
            let before = form.focused_index();
            form.focus_next();
            if form.focused_index() == before {
                break;
            }
        }

        Self { form, title: title.into(), required: None, error: None }
    }

    /// Makes `identity` a condition of this screen rather than its starting
    /// point.
    ///
    /// Used by the launch wizard when the launch itself named a provider: that
    /// is the account the session was told to use, so the choice is "connect
    /// it, or leave" — never "connect something else and be failed for it
    /// afterwards". A saved default that happens to be missing is *not* a
    /// condition: the user did not name it on this launch, so switching is a
    /// perfectly good answer.
    pub fn requiring(mut self, identity: Option<ProviderIdentity>) -> Self {
        self.required = identity;
        self
    }

    /// What the form says, read after it submitted.
    ///
    /// An `Err` is a refusal, never a quieter choice: an explicit "GitHub
    /// Enterprise" with a domain that is not a host name used to resolve to
    /// `None`, which the service reads as "whatever is configured" — so an
    /// explicit enterprise sign-in silently authorized against the saved
    /// tenant, or against public github.com.
    pub fn choice(&self) -> Result<AuthChoice, String> {
        let identity = self
            .radio(ROW_ACCOUNT)
            .and_then(|index| ACCOUNTS.get(index).copied())
            .unwrap_or(ProviderIdentity::ClaudeAi);

        // First, and before any field is even looked at: a key typed into a
        // form that cannot connect the account this launch named must not be
        // sent anywhere, and the refusal must name the only account that will
        // satisfy it.
        if let Some(required) = self.required {
            if identity != required {
                return Err(format!(
                    "This launch asked for {}, so that is the account it can connect. Choose \
                     {} above, or press Esc to leave without changing anything — then start \
                     Coda again without --provider to choose freely.",
                    required.label(),
                    required.label(),
                ));
            }
        }

        let deployment = match (identity, self.radio(ROW_DEPLOYMENT)) {
            (ProviderIdentity::GithubCopilot, Some(DEPLOYMENT_PUBLIC)) => {
                Some(CopilotDeploymentChoice::Public)
            }
            (ProviderIdentity::GithubCopilot, Some(DEPLOYMENT_ENTERPRISE)) => {
                let domain = validate_enterprise_domain(&self.text(ROW_DOMAIN).unwrap_or_default())?;
                Some(CopilotDeploymentChoice::Enterprise(domain))
            }
            _ => None,
        };

        let use_environment_key = identity == ProviderIdentity::AnthropicApiKey
            && self.radio(ROW_KEY_SOURCE) == Some(KEY_SOURCE_ENVIRONMENT);
        let api_key = (identity == ProviderIdentity::AnthropicApiKey && !use_environment_key)
            .then(|| self.text(ROW_KEY).unwrap_or_default());
        if identity == ProviderIdentity::AnthropicApiKey
            && !use_environment_key
            && api_key.as_deref().is_none_or(|key| key.trim().is_empty())
        {
            return Err(
                "Enter the Anthropic API key to store, or choose the environment key below. \
                 Nothing was sent."
                    .to_owned(),
            );
        }

        Ok(AuthChoice { identity, deployment, use_environment_key, api_key })
    }

    /// The refusal currently shown, if any.
    pub fn error(&self) -> Option<&str> {
        self.error.as_deref()
    }

    /// The account currently highlighted, so the host can keep a live
    /// disclosure (an endpoint, a replacement) in step with it.
    pub fn highlighted(&self) -> ProviderIdentity {
        self.radio(ROW_ACCOUNT)
            .and_then(|index| ACCOUNTS.get(index).copied())
            .unwrap_or(ProviderIdentity::ClaudeAi)
    }

    fn radio(&self, index: usize) -> Option<usize> {
        self.form
            .control(index)?
            .as_any()
            .downcast_ref::<RadioGroup>()
            .map(RadioGroup::selected_index)
    }

    fn text(&self, index: usize) -> Option<String> {
        self.form.control(index)?.as_any().downcast_ref::<TextInput>().map(TextInput::value)
    }
}

impl Surface for AuthChoiceSurface {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn title(&self) -> String {
        self.title.clone()
    }

    fn hints(&self) -> String {
        "Tab: next  ↑↓: choose  Enter: continue  Esc: cancel".to_owned()
    }

    fn placement(&self) -> Placement {
        Placement::FitContent { preferred_width: 78 }
    }

    fn modality(&self) -> Modality {
        // Nothing may open above a live sign-in: a browser or a settings form
        // over the key field would take the keystrokes with it.
        Modality::Exclusive
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        // Stated here because the stack will not dismiss an exclusive surface,
        // and an unanswerable form is worse than none.
        if key.code == KeyCode::Esc {
            return SurfaceOutcome::Emit(SurfaceAction::CancelAuth);
        }
        match self.form.handle_key(key) {
            FormOutcome::Consumed | FormOutcome::Ignored => {
                // Editing after a refusal clears it: leaving it up would make
                // a corrected field look as though it were still rejected.
                if matches!(
                    key.code,
                    KeyCode::Char(_) | KeyCode::Backspace | KeyCode::Delete
                ) {
                    self.error = None;
                }
                SurfaceOutcome::Handled
            }
            FormOutcome::Cancel => SurfaceOutcome::Emit(SurfaceAction::CancelAuth),
            FormOutcome::Submit => match self.choice() {
                Ok(_) => {
                    self.error = None;
                    SurfaceOutcome::Emit(SurfaceAction::SubmitAuthChoice)
                }
                // Refused here, on the surface, so nothing is sent and nothing
                // is written: the form stays exclusive and keeps every value
                // the user typed, including the key.
                Err(reason) => {
                    self.error = Some(reason);
                    SurfaceOutcome::Handled
                }
            },
        }
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        let mut lines = render_form(&self.form, area, theme);
        if let Some(error) = &self.error {
            // Appended rather than prepended: the caret is placed by the
            // form's own geometry, and a line above it would move the caret
            // off the field it belongs to.
            lines.push(Line::from(String::new()));
            for row in error.split('\n') {
                lines.push(Line::styled(
                    row.to_owned(),
                    theme.style(coda_render::theme::Role::Error),
                ));
            }
        }
        lines
    }

    fn cursor(&self, area: Rect, theme: &Theme) -> Option<(u16, u16)> {
        form_cursor(&self.form, area, theme)
    }
}

/// The live challenge: what the provider is asking for, for as long as it is
/// asking.
///
/// Rebuilt rather than mutated — the host replaces the top of the stack — so
/// there is exactly one copy of a device code in memory at a time and it goes
/// away with the surface.
pub struct AuthChallengeSurface {
    title: String,
    lines: Vec<String>,
    cancellable: bool,
}

impl AuthChallengeSurface {
    pub fn new(title: impl Into<String>, lines: Vec<String>, cancellable: bool) -> Self {
        Self { title: title.into(), lines, cancellable }
    }

    /// The lines currently shown, for a host keeping them in step.
    pub fn lines(&self) -> &[String] {
        &self.lines
    }
}

impl Surface for AuthChallengeSurface {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn title(&self) -> String {
        self.title.clone()
    }

    fn hints(&self) -> String {
        if self.cancellable {
            "Esc: cancel — nothing has been saved yet".to_owned()
        } else {
            // Said plainly: after the commit starts there is nothing to cancel
            // that would not leave the profile half-written.
            "Finishing — this step cannot be cancelled".to_owned()
        }
    }

    fn placement(&self) -> Placement {
        Placement::FitContent { preferred_width: 78 }
    }

    fn modality(&self) -> Modality {
        Modality::Exclusive
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        if self.cancellable && key.code == KeyCode::Esc {
            return SurfaceOutcome::Emit(SurfaceAction::CancelAuth);
        }
        SurfaceOutcome::Handled
    }

    fn render(&self, area: Rect, _theme: &Theme) -> Vec<Line<'static>> {
        self.lines
            .iter()
            .flat_map(|line| line.split('\n'))
            .take(area.height.max(1) as usize)
            .map(|line| Line::from(line.to_owned()))
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::KeyModifiers;

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn plain(lines: &[Line<'static>]) -> String {
        lines
            .iter()
            .flat_map(|line| line.spans.iter().map(|span| span.content.to_string()))
            .collect::<Vec<_>>()
            .join(" ")
    }

    /// Moves the account radio to `identity` from wherever the form opened.
    fn choose_account(form: &mut AuthChoiceSurface, identity: ProviderIdentity) {
        // Back to the account question, whichever answer the form opened on.
        while form.form.focused_index() != ROW_ACCOUNT {
            let before = form.form.focused_index();
            form.form.focus_previous();
            if form.form.focused_index() == before {
                break;
            }
        }
        let want = ACCOUNTS.iter().position(|c| *c == identity).expect("a known account");
        for _ in 0..ACCOUNTS.len() * 2 {
            match form.radio(ROW_ACCOUNT) {
                Some(at) if at == want => return,
                Some(at) if at > want => {
                    let _ = form.handle_key(key(KeyCode::Up));
                }
                _ => {
                    let _ = form.handle_key(key(KeyCode::Down));
                }
            }
        }
        panic!("the account could not be selected");
    }

    #[test]
    fn a_required_account_is_the_only_one_this_form_will_answer_with() {
        // A launch that named a provider is asking about *which account this
        // session talks to*. Answering with a different one and letting the
        // launch continue ended with the engine started for the account that
        // was just connected and the launch failing its own verification for
        // exactly that.
        let mut required = AuthChoiceSurface::open(
            "Connect an account",
            vec![],
            Some(ProviderIdentity::GithubCopilot),
            None,
        )
        .requiring(Some(ProviderIdentity::GithubCopilot));
        choose_account(&mut required, ProviderIdentity::ClaudeAi);

        let refusal = required.choice().expect_err("a different account was accepted");
        assert!(refusal.contains("GitHub Copilot"), "{refusal}");
        assert!(refusal.contains("--provider"), "the way out was not named: {refusal}");

        // And the account it asked for is answered normally.
        choose_account(&mut required, ProviderIdentity::GithubCopilot);
        assert_eq!(
            required.choice().expect("the required account was refused").identity,
            ProviderIdentity::GithubCopilot
        );

        // Without a condition — a first run, or a saved default the user did
        // not ask for on this launch — the same form is free to switch.
        let mut free = AuthChoiceSurface::open(
            "Connect an account",
            vec![],
            Some(ProviderIdentity::GithubCopilot),
            None,
        );
        choose_account(&mut free, ProviderIdentity::ClaudeAi);
        assert_eq!(
            free.choice().expect("a free choice was refused").identity,
            ProviderIdentity::ClaudeAi
        );
    }

    /// Submitting a refused account keeps the form and says why, on the
    /// surface, without emitting anything the host would act on.
    #[test]
    fn submitting_a_refused_account_keeps_the_form_and_shows_the_reason() {
        let mut form = AuthChoiceSurface::open(
            "Connect an account",
            vec![],
            Some(ProviderIdentity::GithubCopilot),
            None,
        )
        .requiring(Some(ProviderIdentity::GithubCopilot));
        choose_account(&mut form, ProviderIdentity::ClaudeAi);

        match form.handle_key(key(KeyCode::Enter)) {
            SurfaceOutcome::Handled => {}
            _ => panic!("a refused account was submitted to the host anyway"),
        }
        assert!(form.error().is_some_and(|error| error.contains("GitHub Copilot")));
    }

    #[test]
    fn a_login_surface_is_exclusive_so_nothing_can_open_over_a_live_challenge() {
        let mut stack = super::super::stack::SurfaceStack::default();
        assert!(stack.push(Box::new(AuthChoiceSurface::open("Connect", vec![], None, None))));
        let refused = stack.push(Box::new(AuthChallengeSurface::new("x", vec![], true)));
        assert!(!refused, "a surface opened over a live sign-in");
    }

    #[test]
    fn escape_cancels_the_flow_rather_than_being_swallowed_by_exclusivity() {
        let mut surface = AuthChoiceSurface::open("Connect", vec![], None, None);
        assert!(matches!(
            surface.handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Emit(SurfaceAction::CancelAuth)
        ));

        let mut challenge = AuthChallengeSurface::new("Waiting", vec!["…".into()], true);
        assert!(matches!(
            challenge.handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Emit(SurfaceAction::CancelAuth)
        ));
    }

    #[test]
    fn a_commit_in_progress_offers_no_cancel_at_all() {
        let mut challenge = AuthChallengeSurface::new("Saving", vec!["…".into()], false);
        assert!(matches!(challenge.handle_key(key(KeyCode::Esc)), SurfaceOutcome::Handled));
        assert!(!challenge.hints().contains("Esc"));
    }

    #[test]
    fn the_typed_key_is_masked_on_screen_and_never_in_the_action() {
        let mut surface = AuthChoiceSurface::open("Connect", vec![], Some(ProviderIdentity::AnthropicApiKey), None);
        // Focus the key field and type.
        while surface.form.focused_index() != ROW_KEY {
            surface.handle_key(key(KeyCode::Tab));
        }
        for ch in "sk-ant-secret".chars() {
            surface.handle_key(key(KeyCode::Char(ch)));
        }
        let drawn = plain(&surface.render(Rect::new(0, 0, 78, 40), &Theme::default()));
        assert!(!drawn.contains("sk-ant-secret"), "the key was drawn in clear text: {drawn}");

        let action = surface.handle_key(key(KeyCode::Enter));
        match action {
            SurfaceOutcome::Emit(action) => {
                assert!(!format!("{action:?}").contains("sk-ant"), "{action:?}");
            }
            _ => panic!("submitting did not emit"),
        }
        let choice = surface.choice().expect("a key was entered");
        assert_eq!(choice.api_key.as_deref(), Some("sk-ant-secret"));
        assert!(!format!("{choice:?}").contains("sk-ant"), "{choice:?}");
    }

    #[test]
    fn choosing_the_environment_key_carries_no_key_at_all() {
        let mut surface =
            AuthChoiceSurface::open("Connect", vec![], Some(ProviderIdentity::AnthropicApiKey), None);
        while surface.form.focused_index() != ROW_KEY {
            surface.handle_key(key(KeyCode::Tab));
        }
        for ch in "typed-then-abandoned".chars() {
            surface.handle_key(key(KeyCode::Char(ch)));
        }
        while surface.form.focused_index() != ROW_KEY_SOURCE {
            surface.handle_key(key(KeyCode::Tab));
        }
        surface.handle_key(key(KeyCode::Down));
        let choice = surface.choice().expect("an environment login needs no key");
        assert!(choice.use_environment_key);
        assert_eq!(choice.api_key, None, "an environment login must carry no key");
    }

    #[test]
    fn an_explicit_public_copilot_choice_is_carried_rather_than_inherited() {
        let mut surface = AuthChoiceSurface::open(
            "Connect",
            vec![],
            Some(ProviderIdentity::GithubCopilot),
            Some("octocorp.ghe.com".into()),
        );
        // The saved tenant preselects "whatever is saved" — no explicit choice.
        assert!(surface.choice().expect("configured is a valid answer").deployment.is_none());

        while surface.form.focused_index() != ROW_DEPLOYMENT {
            surface.handle_key(key(KeyCode::Tab));
        }
        surface.handle_key(key(KeyCode::Up));
        surface.handle_key(key(KeyCode::Up));
        assert!(matches!(
            surface.choice().expect("public is a valid answer").deployment,
            Some(CopilotDeploymentChoice::Public)
        ));
    }

    /// The bug this replaced: an explicit "GitHub Enterprise" with an empty
    /// box resolved to `None`, which the service reads as "whatever is
    /// configured" — so the sign-in went to the saved tenant, or to public
    /// github.com, without the user being told.
    #[test]
    fn an_enterprise_choice_with_an_empty_domain_is_refused_rather_than_quietly_routed_elsewhere() {
        let mut surface =
            AuthChoiceSurface::open("Connect", vec![], Some(ProviderIdentity::GithubCopilot), None);
        while surface.form.focused_index() != ROW_DEPLOYMENT {
            surface.handle_key(key(KeyCode::Tab));
        }
        surface.handle_key(key(KeyCode::Down));
        assert_eq!(surface.radio(ROW_DEPLOYMENT), Some(DEPLOYMENT_ENTERPRISE));
        assert!(surface.choice().is_err(), "an empty tenant became 'whatever is configured'");

        // Submitting keeps the surface open, sends nothing, and says why.
        match surface.handle_key(key(KeyCode::Enter)) {
            SurfaceOutcome::Handled => {}
            _ => panic!("an unusable enterprise choice was submitted anyway"),
        }
        let shown = surface.error().expect("the refusal is visible");
        assert!(shown.contains("octocorp.ghe.com"), "{shown}");
        assert!(shown.contains("Nothing was sent"), "{shown}");
        let drawn = plain(&surface.render(Rect::new(0, 0, 78, 40), &Theme::default()));
        assert!(drawn.contains("Nothing was sent"), "the refusal was not drawn: {drawn}");
        assert_eq!(surface.modality(), Modality::Exclusive);
    }

    #[test]
    fn a_rejected_submit_keeps_every_value_the_user_typed() {
        let mut surface = AuthChoiceSurface::open(
            "Connect",
            vec![],
            Some(ProviderIdentity::GithubCopilot),
            None,
        );
        while surface.form.focused_index() != ROW_DEPLOYMENT {
            surface.handle_key(key(KeyCode::Tab));
        }
        surface.handle_key(key(KeyCode::Down));
        while surface.form.focused_index() != ROW_DOMAIN {
            surface.handle_key(key(KeyCode::Tab));
        }
        for ch in "https://octocorp.ghe.com".chars() {
            surface.handle_key(key(KeyCode::Char(ch)));
        }
        surface.handle_key(key(KeyCode::Enter));
        assert!(surface.error().is_some(), "a URL was accepted as a tenant name");
        assert_eq!(surface.text(ROW_DOMAIN).as_deref(), Some("https://octocorp.ghe.com"));

        // Correcting it clears the refusal and submits.
        for _ in 0.."https://".len() {
            surface.handle_key(key(KeyCode::Backspace));
        }
        assert!(surface.error().is_none(), "the refusal outlived the correction");
    }

    #[test]
    fn a_malformed_tenant_is_refused_before_anything_could_be_sent() {
        for raw in ["", "   ", "octocorp", "octo corp.com", "octocorp.ghe.com/enterprises/x", "user@octocorp.ghe.com", "octocorp.ghe.com:8443", "-bad.example.com"] {
            assert!(
                validate_enterprise_domain(raw).is_err(),
                "'{raw}' was accepted as a GitHub Enterprise tenant"
            );
        }
        assert_eq!(
            validate_enterprise_domain("  OctoCorp.GHE.com. ").as_deref(),
            Ok("octocorp.ghe.com"),
            "a well-formed tenant was refused"
        );
    }

    #[test]
    fn an_api_key_login_with_an_empty_box_is_refused_rather_than_prepared() {
        let mut surface = AuthChoiceSurface::open(
            "Connect",
            vec![],
            Some(ProviderIdentity::AnthropicApiKey),
            None,
        );
        match surface.handle_key(key(KeyCode::Enter)) {
            SurfaceOutcome::Handled => {}
            _ => panic!("an empty key was submitted"),
        }
        assert!(surface.error().is_some());
    }

    #[test]
    fn a_disclosure_is_visible_before_anything_is_typed() {
        let surface = AuthChoiceSurface::open(
            "Connect",
            vec!["Signing in to GitHub Copilot removes the stored Claude.ai credential.".into()],
            None,
            None,
        );
        let drawn = plain(&surface.render(Rect::new(0, 0, 78, 40), &Theme::default()));
        assert!(drawn.contains("removes the stored Claude.ai credential"), "{drawn}");
    }

    #[test]
    fn a_challenge_never_renders_more_rows_than_it_was_given() {
        let challenge = AuthChallengeSurface::new(
            "Waiting",
            (0..40).map(|n| format!("line {n}")).collect(),
            true,
        );
        assert!(challenge.render(Rect::new(0, 0, 40, 5), &Theme::default()).len() <= 5);
    }
}
