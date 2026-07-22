using Coda.Agent;
using Coda.JsonRpc;
using Coda.Sdk;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Messages;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests.Serve;

/// <summary>
/// Integration test for resuming a persisted session via <c>initialize { sessionId }</c>.
/// Mirrors the <see cref="ServeHostTests"/> harness (DuplexStreamPair + an JsonRpcConnection
/// orchestrator), driving requests with CancellationToken.None and bounding each await with
/// WaitAsync(5s) so the test is robust under the full parallel suite.
/// </summary>
public sealed class ServeHostResumeTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly string workDir = Directory.CreateTempSubdirectory("serve_resume_").FullName;

    private static CredentialManager SignedInClaude()
    {
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(store, [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        creds.StoreAsync(ClaudeAiProvider.Id, new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
        }).GetAwaiter().GetResult();
        return creds;
    }

    private Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> MakeFactory(
        string? systemPromptOverride = null)
    {
        return (perm, question, plan) =>
        {
            var options = new SessionOptions
            {
                ProviderId = ClaudeAiProvider.Id,
                Model = "claude-sonnet-4-6",
                WorkingDirectory = this.workDir,
                PermissionMode = PermissionMode.BypassPermissions,
                InteractivePrompt = perm,
                UserQuestionPrompt = question,
                PlanApprover = plan,
                SystemPromptOverride = systemPromptOverride,
            };
            return new CodaSession(SignedInClaude(), options, httpClient: new HttpClient());
        };
    }

    [Fact]
    public async Task Initialize_with_sessionId_resumes_persisted_transcript()
    {
        // Persist a transcript that a fresh session should resume.
        var store = new SessionTranscriptStore(this.workDir);
        var saved = new List<ChatMessage>
        {
            new(ChatRole.User, new ContentBlock[] { new TextBlock("first question") }),
            new(ChatRole.Assistant, new ContentBlock[] { new TextBlock("first answer") }),
        };
        await store.SaveAsync("resume-me", saved);

        using var pair = new DuplexStreamPair();
        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, this.MakeFactory());
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var initNode = await orchestrator
            .SendRequestAsync(
                ServeMethods.Initialize,
                ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null, SessionId: "resume-me")),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        var init = ServeJson.FromNode<InitializeResult>(initNode);
        Assert.NotNull(init);
        Assert.Equal("resume-me", init!.SessionId);

        var historyNode = await orchestrator
            .SendRequestAsync(ServeMethods.History, null, CancellationToken.None)
            .WaitAsync(WaitTimeout);

        var history = ServeJson.FromNode<HistoryResult>(historyNode);
        Assert.NotNull(history);
        Assert.Equal(2, history!.Messages.Count);
        Assert.Contains(history.Messages, m => m.Role == "user" && m.Content.Contains("first question"));

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Initialize_with_sessionId_applies_metadata_before_initialization_delegate()
    {
        var store = new SessionTranscriptStore(this.workDir);
        await store.SaveAsync(
            "resume-me",
            [new ChatMessage(ChatRole.User, [new TextBlock("first question")])],
            new SessionMetadata { SystemPromptOverride = "persisted prompt" });

        var initializationCalls = 0;
        string? capturedSessionId = null;
        string? capturedPrompt = null;

        using var pair = new DuplexStreamPair();
        await using var host = new ServeHost(
            pair.ServerReads,
            pair.ServerWrites,
            this.MakeFactory(),
            expectedApiKey: null,
            initializeSession: (session, _) =>
            {
                Interlocked.Increment(ref initializationCalls);
                capturedSessionId = session.SessionId;
                capturedPrompt = session.Options.SystemPromptOverride;
                return Task.CompletedTask;
            });
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        var initNode = await orchestrator
            .SendRequestAsync(
                ServeMethods.Initialize,
                ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null, SessionId: "resume-me")),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        var init = ServeJson.FromNode<InitializeResult>(initNode);
        Assert.NotNull(init);
        Assert.Equal("resume-me", capturedSessionId);
        Assert.Equal("persisted prompt", capturedPrompt);
        Assert.Equal(1, Volatile.Read(ref initializationCalls));

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Concurrent_initialize_with_sessionId_and_prompt_applies_metadata_before_initialization()
    {
        var store = new SessionTranscriptStore(this.workDir);
        await store.SaveAsync(
            "resume-me",
            [new ChatMessage(ChatRole.User, [new TextBlock("first question")])],
            new SessionMetadata { SystemPromptOverride = "persisted prompt" });

        var initializationCalls = 0;
        string? capturedSessionId = null;
        string? capturedPrompt = null;
        var initializationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var pair = new DuplexStreamPair();
        await using var host = new ServeHost(
            pair.ServerReads,
            pair.ServerWrites,
            this.MakeFactory(),
            expectedApiKey: null,
            initializeSession: (session, _) =>
            {
                Interlocked.Increment(ref initializationCalls);
                capturedSessionId = session.SessionId;
                capturedPrompt = session.Options.SystemPromptOverride;
                initializationEntered.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, _);
            });
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        var initializeTask = orchestrator.SendRequestAsync(
            ServeMethods.Initialize,
            ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null, SessionId: "resume-me")),
            CancellationToken.None);
        var promptTask = orchestrator.SendRequestAsync(
            ServeMethods.Prompt,
            ServeJson.ToNode(new PromptParams { Text = "follow up" }),
            CancellationToken.None);

        await initializationEntered.Task.WaitAsync(WaitTimeout);

        Assert.Equal("resume-me", capturedSessionId);
        Assert.Equal("persisted prompt", capturedPrompt);
        Assert.Equal(1, Volatile.Read(ref initializationCalls));

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
        await Assert.ThrowsAnyAsync<Exception>(() => initializeTask.WaitAsync(WaitTimeout));
        await Assert.ThrowsAnyAsync<Exception>(() => promptTask.WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task Initialize_with_unknown_sessionId_returns_session_not_found()
    {
        using var pair = new DuplexStreamPair();
        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, this.MakeFactory());
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(() => orchestrator
            .SendRequestAsync(
                ServeMethods.Initialize,
                ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null, SessionId: "does-not-exist")),
                CancellationToken.None)
            .WaitAsync(WaitTimeout));

        Assert.Equal(-32002, ex.Code);
        Assert.Contains("session not found", ex.Message);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    public void Dispose()
    {
        try { Directory.Delete(this.workDir, recursive: true); } catch { /* ignore */ }
    }
}
