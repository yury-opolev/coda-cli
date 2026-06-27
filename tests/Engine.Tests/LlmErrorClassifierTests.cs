using System.Net.Http;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Pure classification of LLM call failures into retry buckets. Drives both the
/// retry policy (retry transient/rate-limited) and fail-fast (permanent 4xx — e.g.
/// the dash/dot model id rejected by Copilot with HTTP 400 model_not_supported).
/// </summary>
public sealed class LlmErrorClassifierTests
{
    [Theory]
    [InlineData(400, LlmFailureKind.Permanent)]   // model_not_supported (the dash/dot bug)
    [InlineData(401, LlmFailureKind.Permanent)]
    [InlineData(403, LlmFailureKind.Permanent)]
    [InlineData(404, LlmFailureKind.Permanent)]
    [InlineData(429, LlmFailureKind.RateLimited)]
    [InlineData(500, LlmFailureKind.Transient)]
    [InlineData(502, LlmFailureKind.Transient)]
    [InlineData(503, LlmFailureKind.Transient)]
    public void ClassifyStatus_maps_http_codes(int status, LlmFailureKind expected)
    {
        Assert.Equal(expected, LlmErrorClassifier.ClassifyStatus(status));
    }

    [Fact]
    public void Classify_http_timeout_is_transient()
    {
        Assert.Equal(
            LlmFailureKind.Transient,
            LlmErrorClassifier.Classify(LlmHttpTimeoutException.StreamIdle(TimeSpan.FromSeconds(60))));
    }

    [Fact]
    public void Classify_client_exception_uses_status()
    {
        Assert.Equal(
            LlmFailureKind.Permanent,
            LlmErrorClassifier.Classify(new LlmClientException(400, "{\"error\":{\"message\":\"The requested model is not supported.\"}}")));
    }

    [Fact]
    public void Classify_connection_error_is_transient()
    {
        Assert.Equal(LlmFailureKind.Transient, LlmErrorClassifier.Classify(new HttpRequestException("connection reset")));
    }
}
