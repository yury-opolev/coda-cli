//! Shared argument contract for `coda auth` and `coda-engine auth`.

use clap::{Args, Subcommand};
use coda_auth::service::ProviderIdentity;

#[derive(Debug, Args)]
pub struct AuthArgs {
    #[command(subcommand)]
    pub command: AuthCommand,
}

#[derive(Debug, Subcommand)]
pub enum AuthCommand {
    /// Show stored authentication and provider-selection status.
    Status,
    /// Connect a provider; omit PROVIDER for an interactive choice.
    Login(AuthLoginArgs),
    /// Remove stored authentication without revoking other processes or shell variables.
    Logout(AuthLogoutArgs),
}

#[derive(Args)]
pub struct AuthLoginArgs {
    /// claude, copilot, api-key, or a canonical provider identity.
    #[arg(value_name = "PROVIDER", value_parser = parse_provider)]
    pub provider: Option<ProviderIdentity>,
    /// Sign in to public github.com rather than a saved enterprise deployment.
    #[arg(long, requires = "provider", conflicts_with = "enterprise_domain")]
    pub public: bool,
    /// GitHub Enterprise deployment to authorize.
    #[arg(long, value_name = "HOST", requires = "provider", conflicts_with = "public")]
    pub enterprise_domain: Option<String>,
    /// Read one API-key line from stdin instead of a masked terminal prompt.
    #[arg(long, requires = "provider", conflicts_with = "use_env")]
    pub api_key_stdin: bool,
    /// Select the exported ANTHROPIC_API_KEY as this profile's provider. No
    /// key is stored: a shell without the variable is not signed in.
    ///
    /// Like a stored key, it is checked against ANTHROPIC_BASE_URL (or
    /// Anthropic's own host when that is unset) before anything is changed.
    #[arg(long, requires = "provider", conflicts_with = "api_key_stdin")]
    pub use_env: bool,
}

impl std::fmt::Debug for AuthLoginArgs {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AuthLoginArgs")
            .field("provider", &self.provider)
            .field("public", &self.public)
            .field("enterprise_domain", &self.enterprise_domain.as_ref().map(|_| "[REDACTED]"))
            .field("api_key_stdin", &self.api_key_stdin)
            .field("use_env", &self.use_env)
            .finish()
    }
}

#[derive(Debug, Args)]
pub struct AuthLogoutArgs {
    /// Provider to disconnect; otherwise use the selected/stored provider.
    #[arg(value_name = "PROVIDER", value_parser = parse_provider)]
    pub provider: Option<ProviderIdentity>,
}

#[derive(Debug, thiserror::Error)]
pub enum AuthArgumentError {
    #[error("--public and --enterprise-domain require the Copilot provider")]
    DeploymentProvider,
    #[error("--api-key-stdin and --use-env require the API-key provider")]
    ApiKeyProvider,
    #[error("the enterprise deployment must be a valid bare host without embedded credentials")]
    Deployment,
}

impl AuthArgs {
    /// Validate provider-specific options before opening settings or credentials.
    pub fn validate(&self) -> Result<(), AuthArgumentError> {
        let AuthCommand::Login(login) = &self.command else { return Ok(()); };
        if (login.public || login.enterprise_domain.is_some())
            && login.provider != Some(ProviderIdentity::GithubCopilot)
        {
            return Err(AuthArgumentError::DeploymentProvider);
        }
        if (login.api_key_stdin || login.use_env)
            && login.provider != Some(ProviderIdentity::AnthropicApiKey)
        {
            return Err(AuthArgumentError::ApiKeyProvider);
        }
        if let Some(domain) = &login.enterprise_domain {
            coda_auth::provider::copilot::CopilotConfig::for_enterprise(domain)
                .map_err(|_| AuthArgumentError::Deployment)?;
        }
        Ok(())
    }
}

fn parse_provider(value: &str) -> Result<ProviderIdentity, String> {
    ProviderIdentity::parse(value)
        .ok_or_else(|| "unknown provider; use claude, copilot, or api-key".to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;
    use clap::Parser;

    #[derive(Parser)]
    struct Cli {
        #[command(flatten)]
        auth: AuthArgs,
    }

    #[test]
    fn provider_aliases_use_the_shared_identity_table() {
        for (alias, expected) in [
            ("claude", ProviderIdentity::ClaudeAi),
            ("copilot", ProviderIdentity::GithubCopilot),
            ("api-key", ProviderIdentity::AnthropicApiKey),
            ("anthropic", ProviderIdentity::AnthropicApiKey),
        ] {
            let parsed = Cli::try_parse_from(["auth", "login", alias]).unwrap();
            let AuthCommand::Login(login) = parsed.auth.command else { panic!("login"); };
            assert_eq!(login.provider, Some(expected));
        }
    }

    #[test]
    fn rejects_conflicting_and_provider_inapplicable_options() {
        for args in [
            vec!["auth", "login", "copilot", "--public", "--enterprise-domain", "tenant.ghe.com"],
            vec!["auth", "login", "api-key", "--use-env", "--api-key-stdin"],
            vec!["auth", "login", "--use-env"],
        ] {
            assert!(Cli::try_parse_from(args).is_err());
        }
        for args in [
            vec!["auth", "login", "claude", "--public"],
            vec!["auth", "login", "copilot", "--api-key-stdin"],
            vec!["auth", "login", "api-key", "--enterprise-domain", "tenant.ghe.com"],
        ] {
            assert!(Cli::try_parse_from(args).unwrap().auth.validate().is_err());
        }
    }

    #[test]
    fn rejects_bad_deployment_before_any_service_is_created() {
        let parsed = Cli::try_parse_from([
            "auth", "login", "copilot", "--enterprise-domain", "user:password@tenant.ghe.com",
        ]).unwrap();
        assert!(parsed.auth.validate().is_err());
        let debug = format!("{:?}", parsed.auth);
        assert!(!debug.contains("password"));
        assert!(!debug.contains("tenant.ghe.com"));
    }

    #[test]
    fn exposes_status_logout_and_interactive_login_without_a_literal_key_option() {
        assert!(matches!(Cli::try_parse_from(["auth", "status"]).unwrap().auth.command, AuthCommand::Status));
        assert!(matches!(Cli::try_parse_from(["auth", "logout"]).unwrap().auth.command, AuthCommand::Logout(_)));
        let parsed = Cli::try_parse_from(["auth", "login"]).unwrap();
        let AuthCommand::Login(login) = parsed.auth.command else { panic!("login"); };
        assert!(login.provider.is_none());
        assert!(Cli::try_parse_from(["auth", "login", "api-key", "--api-key", "literal-key"]).is_err());
    }
}
