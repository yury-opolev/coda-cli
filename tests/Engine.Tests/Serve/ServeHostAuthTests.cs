using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.JsonRpc;
using Coda.Sdk;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Messages;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using System.Net;
using System.Text;

namespace Engine.Tests.Serve;

public sealed class ServeHostAuthTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"; // 64 hex

    private readonly string workDir = Directory.CreateTempSubdirectory("serve_auth_").FullName;

    private sealed class TextHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string body =
                "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
                "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n" +
                "data: {\"type\":\"message_stop\"}\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private static CredentialManager SignedInClaude()
    {
        var creds = new CredentialManager(new InMemoryTokenStore(), [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        creds.StoreAsync(ClaudeAiProvider.Id, new Credential { ProviderId = ClaudeAiProvider.Id, Kind = CredentialKind.OAuth, AccessToken = "AT" }).GetAwaiter().GetResult();
        return creds;
    }

    private Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> Factory() =>
        (perm, question, plan) => new CodaSession(
            SignedInClaude(),
            new SessionOptions
            {
                ProviderId = ClaudeAiProvider.Id,
                Model = "claude-sonnet-4-6",
                WorkingDirectory = this.workDir,
                PermissionMode = PermissionMode.BypassPermissions,
                InteractivePrompt = perm,
                UserQuestionPrompt = question,
                PlanApprover = plan,
            },
            httpClient: new HttpClient(new TextHandler()));

    private static JsonNode InitNode(string? apiKey) =>
        ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null, apiKey));

    [Fact]
    public async Task Correct_key_authenticates_and_prompt_works()
    {
        using var pair = new DuplexStreamPair();
        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, this.Factory(), Key);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);
        await using var client = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var init = await client.SendRequestAsync(ServeMethods.Initialize, InitNode(Key), CancellationToken.None).WaitAsync(WaitTimeout);
        Assert.NotNull(ServeJson.FromNode<InitializeResult>(init));

        var prompt = await client.SendRequestAsync(ServeMethods.Prompt, ServeJson.ToNode(new PromptParams { Text = "hello" }), CancellationToken.None).WaitAsync(WaitTimeout);
        Assert.True(ServeJson.FromNode<PromptResult>(prompt)!.Ok);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { }
    }

    [Fact]
    public async Task Wrong_key_is_rejected_and_session_stays_locked()
    {
        using var pair = new DuplexStreamPair();
        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, this.Factory(), Key);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);
        await using var client = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var bad = await Assert.ThrowsAsync<JsonRpcResponseException>(
            () => client.SendRequestAsync(ServeMethods.Initialize, InitNode("wrong-but-long-enough-to-be-a-key-0123456789abcdef0123456789"), CancellationToken.None).WaitAsync(WaitTimeout));
        Assert.Equal(-32001, bad.Code);

        var stillBad = await Assert.ThrowsAsync<JsonRpcResponseException>(
            () => client.SendRequestAsync(ServeMethods.History, null, CancellationToken.None).WaitAsync(WaitTimeout));
        Assert.Equal(-32001, stillBad.Code);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { }
    }

    [Fact]
    public async Task Missing_key_is_rejected()
    {
        using var pair = new DuplexStreamPair();
        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, this.Factory(), Key);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);
        await using var client = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
            () => client.SendRequestAsync(ServeMethods.Initialize, InitNode(null), CancellationToken.None).WaitAsync(WaitTimeout));
        Assert.Equal(-32001, ex.Code);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { }
    }

    [Fact]
    public async Task Non_initialize_first_request_is_rejected()
    {
        using var pair = new DuplexStreamPair();
        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, this.Factory(), Key);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);
        await using var client = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
            () => client.SendRequestAsync(ServeMethods.History, null, CancellationToken.None).WaitAsync(WaitTimeout));
        Assert.Equal(-32001, ex.Code);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { }
    }

    [Fact]
    public async Task Null_expected_key_skips_auth_regression()
    {
        using var pair = new DuplexStreamPair();
        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, this.Factory()); // no key
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);
        await using var client = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var init = await client.SendRequestAsync(ServeMethods.Initialize, InitNode(null), CancellationToken.None).WaitAsync(WaitTimeout);
        Assert.NotNull(ServeJson.FromNode<InitializeResult>(init));

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { }
    }

    public void Dispose()
    {
        try { Directory.Delete(this.workDir, recursive: true); } catch { }
    }
}
