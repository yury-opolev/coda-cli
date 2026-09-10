use std::sync::Arc;

use coda_llm::anthropic::{AnthropicConfig, Auth};
use coda_llm::{CopilotConfig, CredentialSource, LlmError};

const SECRET: &str = "CONFIG_SECRET_SENTINEL";

struct SensitiveSource;

impl std::fmt::Debug for SensitiveSource {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(SECRET)
    }
}

#[async_trait::async_trait]
impl CredentialSource for SensitiveSource {
    async fn auth_headers(&self) -> Result<Option<Vec<(String, String)>>, LlmError> {
        panic!("formatting a configuration must not read credentials")
    }
}

fn assert_redacted(value: &impl std::fmt::Debug) {
    for rendered in [format!("{value:?}"), format!("{value:#?}")] {
        assert!(!rendered.contains(SECRET), "configuration Debug leaked a secret");
        assert!(rendered.contains("REDACTED"));
    }
}

#[test]
fn credentials_urls_headers_and_dynamic_sources_are_redacted() {
    assert_redacted(&Auth::ApiKey(SECRET.into()));
    assert_redacted(&Auth::Bearer(SECRET.into()));

    let mut anthropic = AnthropicConfig::api_key(SECRET)
        .with_base_url(format!("https://example.invalid/{SECRET}?key={SECRET}"))
        .with_credential_source(Arc::new(SensitiveSource));
    anthropic.extra_headers.push(("authorization".into(), SECRET.into()));
    assert_redacted(&anthropic);
    anthropic.auth = Auth::Bearer(SECRET.into());
    assert_redacted(&anthropic);

    let copilot = CopilotConfig::with_token(SECRET)
        .with_base_url(format!("https://example.invalid/{SECRET}"))
        .with_header("authorization", SECRET)
        .with_credential_source(Arc::new(SensitiveSource));
    assert_redacted(&copilot);
}
