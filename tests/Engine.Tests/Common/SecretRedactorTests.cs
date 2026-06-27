using Coda.Common;

namespace Engine.Tests.Common;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("x-api-key")]
    [InlineData("Cookie")]
    [InlineData("set-cookie")]
    [InlineData("proxy-authorization")]
    public void IsSecretHeader_true_for_known_secret_headers(string name)
    {
        Assert.True(SecretRedactor.IsSecretHeader(name));
    }

    [Theory]
    [InlineData("anthropic-version")]
    [InlineData("content-type")]
    [InlineData("user-agent")]
    [InlineData("anthropic-beta")]
    public void IsSecretHeader_false_for_safe_headers(string name)
    {
        Assert.False(SecretRedactor.IsSecretHeader(name));
    }

    [Fact]
    public void RedactJson_replaces_known_secret_key_values()
    {
        var json = """{"model":"opus","api_key":"sk-ant-secret","nested":{"access_token":"tok"}}""";

        var redacted = SecretRedactor.RedactJson(json);

        Assert.Contains("\"model\"", redacted);
        Assert.Contains("opus", redacted);
        Assert.DoesNotContain("sk-ant-secret", redacted);
        Assert.DoesNotContain("\"tok\"", redacted);
        Assert.Contains(SecretRedactor.Placeholder, redacted);
    }

    [Fact]
    public void RedactJson_on_invalid_json_falls_back_to_text_redaction()
    {
        var text = "Bearer sk-ant-abc123 not json";

        var redacted = SecretRedactor.RedactJson(text);

        Assert.DoesNotContain("sk-ant-abc123", redacted);
    }

    [Fact]
    public void Redact_masks_bearer_and_sk_tokens()
    {
        var text = "header Authorization: Bearer abcdef1234567890abcdef and key sk-ant-api03-XYZ987";

        var redacted = SecretRedactor.Redact(text);

        Assert.DoesNotContain("abcdef1234567890abcdef", redacted);
        Assert.DoesNotContain("sk-ant-api03-XYZ987", redacted);
    }

    [Fact]
    public void RedactHeaderValue_masks_secret_keeps_safe()
    {
        Assert.Equal(SecretRedactor.Placeholder, SecretRedactor.RedactHeaderValue("Authorization", "Bearer abc"));
        Assert.Equal("application/json", SecretRedactor.RedactHeaderValue("content-type", "application/json"));
    }

    [Fact]
    public void Redact_leaves_ordinary_prose_untouched()
    {
        var text = "Use a bearer token for authentication";
        Assert.Equal(text, SecretRedactor.Redact(text));
    }

    [Fact]
    public void RedactJson_preserves_error_code_field()
    {
        var json = """{"error":{"type":"authentication_error","code":"invalid_api_key","message":"bad key"}}""";
        var redacted = SecretRedactor.RedactJson(json);
        Assert.Contains("invalid_api_key", redacted);
    }

    // --- Extended coverage: all secret JSON keys ---

    [Theory]
    [InlineData("api_key")]
    [InlineData("apikey")]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("id_token")]
    [InlineData("client_secret")]
    [InlineData("verifier")]
    [InlineData("password")]
    [InlineData("secret")]
    public void RedactJson_redacts_each_secret_key(string key)
    {
        var json = $"{{\"{key}\":\"supersecret-value\"}}";

        var redacted = SecretRedactor.RedactJson(json);

        Assert.DoesNotContain("supersecret-value", redacted);
        Assert.Contains(SecretRedactor.Placeholder, redacted);
    }

    [Fact]
    public void RedactJson_redacts_values_inside_json_array()
    {
        // Array recursion: items inside an array are also walked.
        var json = """[{"api_key":"hidden"},{"model":"safe"}]""";

        var redacted = SecretRedactor.RedactJson(json);

        Assert.DoesNotContain("hidden", redacted);
        Assert.Contains("safe", redacted);
    }

    [Fact]
    public void RedactJson_redacts_deeply_nested_objects()
    {
        var json = """{"outer":{"inner":{"password":"deep-secret"}}}""";

        var redacted = SecretRedactor.RedactJson(json);

        Assert.DoesNotContain("deep-secret", redacted);
        Assert.Contains(SecretRedactor.Placeholder, redacted);
    }

    [Fact]
    public void RedactJson_handles_null_node_result()
    {
        // A JSON literal "null" parses to a null JsonNode; RedactJson must fall back gracefully.
        var redacted = SecretRedactor.RedactJson("null");

        // Should not throw and should return the text-redacted form of "null" (unchanged).
        Assert.Equal("null", redacted);
    }

    [Fact]
    public void RedactJson_empty_input_returns_empty()
    {
        Assert.Equal(string.Empty, SecretRedactor.RedactJson(string.Empty));
    }

    [Fact]
    public void Redact_null_returns_empty()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
    }

    [Fact]
    public void Redact_empty_returns_empty()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(string.Empty));
    }

    [Fact]
    public void Redact_masks_sk_key_without_bearer()
    {
        // sk- token appearing standalone (not inside a Bearer header).
        var text = "key=sk-abcdefghijklmno";

        var redacted = SecretRedactor.Redact(text);

        Assert.DoesNotContain("sk-abcdefghijklmno", redacted);
        Assert.Contains(SecretRedactor.Placeholder, redacted);
    }

    [Fact]
    public void RedactJson_non_secret_key_in_object_is_not_redacted()
    {
        var json = """{"model":"claude-opus","temperature":0.7}""";

        var redacted = SecretRedactor.RedactJson(json);

        Assert.Contains("claude-opus", redacted);
    }

    [Fact]
    public void RedactJson_null_child_array_item_is_skipped()
    {
        // An array that contains a JSON null element must not cause a NullReferenceException.
        var json = """{"tokens":["abc", null, "def"]}""";

        var redacted = SecretRedactor.RedactJson(json);

        // Non-secret key — values are passed through unchanged.
        Assert.Contains("abc", redacted);
    }
}
