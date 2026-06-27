using LlmClient;

namespace Engine.Tests;

public sealed class LlmClientExceptionTests
{
    [Fact]
    public void Message_includes_parsed_error_message()
    {
        var body = """{"type":"error","error":{"type":"invalid_request_error","message":"messages.0: bad role"}}""";

        var ex = new LlmClientException(400, body);

        Assert.Contains("HTTP 400", ex.Message);
        Assert.Contains("messages.0: bad role", ex.Message);
        Assert.Equal(body, ex.ResponseBody);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void Message_falls_back_to_bare_status_on_unparseable_body()
    {
        var ex = new LlmClientException(500, "<html>gateway error</html>");

        Assert.Equal("Model API request failed (HTTP 500).", ex.Message);
    }

    [Fact]
    public void Message_redacts_secrets_in_error_detail()
    {
        var body = """{"error":{"message":"bad token sk-ant-api03-LEAKED9999"}}""";

        var ex = new LlmClientException(401, body);

        Assert.DoesNotContain("sk-ant-api03-LEAKED9999", ex.Message);
    }
}
