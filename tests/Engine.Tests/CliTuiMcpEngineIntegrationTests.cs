using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.JsonRpc;
using Coda.Sdk;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Messages;
using Engine.Tests.TestSupport;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;

namespace Engine.Tests;

public sealed class CliTuiMcpEngineIntegrationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("exact override")]
    public async Task Transcript_round_trips_prompt_metadata_and_tool_correlation_together(
        string systemPromptOverride)
    {
        var root = Directory.CreateTempSubdirectory("coda_cli_tui_mcp_").FullName;
        try
        {
            var store = new SessionTranscriptStore(root);
            await store.SaveAsync("session1", CorrelatedHistory(),
                new SessionMetadata { SystemPromptOverride = systemPromptOverride });

            var loaded = await store.LoadSessionAsync("session1");

            Assert.NotNull(loaded);
            Assert.Equal(systemPromptOverride, loaded.Metadata.SystemPromptOverride);
            var use = Assert.IsType<ToolUseBlock>(loaded.Messages[1].Content.Single());
            var result = Assert.IsType<ToolResultBlock>(loaded.Messages[2].Content.Single());
            Assert.Equal("call-1", use.Id);
            Assert.Equal("root-1", use.RootTurnId);
            Assert.Equal("activity-1", use.ActivityId);
            Assert.Equal("root:root-1", use.SourceId);
            Assert.Equal("call-1", result.ToolUseId);
            Assert.False(result.IsError);
            Assert.Equal("root-1", result.RootTurnId);
            Assert.Equal("activity-1", result.ActivityId);
            Assert.Equal("root:root-1", result.SourceId);
            Assert.Equal(nameof(ToolCallStatus.Succeeded), result.ToolStatus);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Compatibility_wrappers_preserve_metadata_and_correlated_messages()
    {
        var root = Directory.CreateTempSubdirectory("coda_cli_tui_mcp_").FullName;
        try
        {
            var store = new SessionTranscriptStore(root);
            await store.SaveAsync("session1", CorrelatedHistory(),
                new SessionMetadata { SystemPromptOverride = "persisted" });
            await store.SaveAsync("session1", CorrelatedHistory());

            var messages = await store.LoadAsync("session1");
            var stored = await store.LoadSessionAsync("session1");

            Assert.NotNull(messages);
            Assert.Equal("persisted", stored!.Metadata.SystemPromptOverride);
            Assert.Equal("activity-1",
                Assert.IsType<ToolUseBlock>(messages[1].Content.Single()).ActivityId);
            Assert.Equal(nameof(ToolCallStatus.Succeeded),
                Assert.IsType<ToolResultBlock>(messages[2].Content.Single()).ToolStatus);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("override")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Bundle_round_trip_preserves_override_activity_and_audited_prompt(
        string? systemPromptOverride)
    {
        var sourceRoot = Directory.CreateTempSubdirectory("coda_cli_tui_mcp_").FullName;
        var destinationRoot = Directory.CreateTempSubdirectory("coda_cli_tui_mcp_").FullName;
        try
        {
            await new SessionTranscriptStore(sourceRoot).SaveAsync(
                "session1",
                CorrelatedHistory(),
                new SessionMetadata { SystemPromptOverride = systemPromptOverride });
            await new SessionAuditStore(sourceRoot).AppendTurnAsync("session1", new SessionAuditTurn
            {
                TurnIndex = 0,
                TsUtc = DateTime.UtcNow,
                Provider = "test-provider",
                Model = "test-model",
                InputTokens = 1,
                OutputTokens = 1,
                SystemPrompt = "audited system prompt",
            });

            var source = new SessionBundleService(sourceRoot, "test");
            var bundle = await source.ExportAsync("session1", DateTime.UtcNow);
            var path = await source.WriteAsync(
                bundle!,
                Path.Combine(sourceRoot, "bundle.json"),
                pretty: false);

            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Assert.Equal("coda.session/1", json["schema"]!.GetValue<string>());
            AssertOptionalString(json, "systemPromptOverride", systemPromptOverride);
            Assert.Equal("audited system prompt", json["systemPrompt"]!.GetValue<string>());
            Assert.False(json.ContainsKey("rootTurnId"));
            Assert.False(json.ContainsKey("activityId"));
            Assert.False(json.ContainsKey("sourceId"));
            var useJson = json["turns"]!.AsArray()[1]!["blocks"]!.AsArray()[0]!.AsObject();
            var resultJson = json["turns"]!.AsArray()[2]!["blocks"]!.AsArray()[0]!.AsObject();
            AssertCorrelated(useJson, resultJson);

            var importedId = await new SessionBundleService(destinationRoot, "test")
                .ImportAsync(path);
            var imported = await new SessionTranscriptStore(destinationRoot)
                .LoadSessionAsync(importedId);
            var importedAudit = await new SessionAuditStore(destinationRoot).LoadAsync(importedId);

            Assert.Equal(systemPromptOverride, imported!.Metadata.SystemPromptOverride);
            Assert.Equal("audited system prompt", Assert.Single(importedAudit).SystemPrompt);
            AssertCorrelated(
                Assert.IsType<ToolUseBlock>(imported.Messages[1].Content.Single()),
                Assert.IsType<ToolResultBlock>(imported.Messages[2].Content.Single()));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(destinationRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_optional_transcript_fields_remain_backward_compatible()
    {
        var root = Directory.CreateTempSubdirectory("coda_cli_tui_mcp_").FullName;
        try
        {
            var path = Path.Combine(root, ".coda", "sessions", "legacy.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path,
                """{"id":"legacy","createdUtc":"2026-07-24T00:00:00.0000000Z","messages":[{"role":"assistant","blocks":[{"type":"tool_use","id":"call-1","name":"read_file","input":"{}"},{"type":"tool_result","toolUseId":"call-1","content":"ok"}]}]}""");

            var loaded = await new SessionTranscriptStore(root).LoadSessionAsync("legacy");

            Assert.NotNull(loaded);
            Assert.Null(loaded.Metadata.SystemPromptOverride);
            var use = Assert.IsType<ToolUseBlock>(loaded.Messages[0].Content[0]);
            var result = Assert.IsType<ToolResultBlock>(loaded.Messages[0].Content[1]);
            Assert.Null(use.RootTurnId);
            Assert.Null(use.ActivityId);
            Assert.Null(use.SourceId);
            Assert.Null(result.RootTurnId);
            Assert.Null(result.ActivityId);
            Assert.Null(result.SourceId);
            Assert.Null(result.ToolStatus);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, "persisted")]
    [InlineData("", "")]
    [InlineData("startup", "startup")]
    public async Task Resume_precedence_resolves_every_root_prompt_and_keeps_each_root_activity_correlated(
        string? startupOverride,
        string expectedOverride)
    {
        var root = Directory.CreateTempSubdirectory("coda_cli_tui_mcp_").FullName;
        try
        {
            var factory = new CapturingActivityLoopFactory();
            using var http = new HttpClient(new SseTestHandler(SseTestHandler.MessageStopOnly));
            using var session = new CodaSession(
                CredentialFixtures.SignedInClaude(),
                new SessionOptions
                {
                    ProviderId = ClaudeAiProvider.Id,
                    Model = "claude-sonnet-4-6",
                    WorkingDirectory = root,
                    PermissionMode = PermissionMode.BypassPermissions,
                    SystemPromptOverride = startupOverride,
                },
                httpClient: http,
                agentLoopFactory: factory);
            session.Resume(
                "resumed",
                [ChatMessage.UserText("history")],
                new SessionMetadata { SystemPromptOverride = "persisted" });

            var sink = new CorrelationSink();
            var first = await session.RunAsync("one", sink);
            var second = await session.RunAsync("two", sink);

            Assert.Equal(expectedOverride, session.Options.SystemPromptOverride);
            Assert.Equal(2, factory.Specs.Count);
            Assert.All(factory.Specs, spec => Assert.Equal(expectedOverride, spec.Options.SystemPrompt));
            Assert.NotEqual(first.RootTurnId, second.RootTurnId);
            Assert.Equal(2, sink.Completions.Count);

            foreach (var rootIdentity in new[] { first.RootTurnId, second.RootTurnId })
            {
                var callbacks = sink.Identities
                    .Where(identity => identity.RootTurnId == rootIdentity)
                    .ToArray();
                var identity = Assert.Single(callbacks.Distinct());
                var summary = Assert.Single(sink.Completions, item => item.RootTurnId == rootIdentity);
                Assert.Equal(identity.ActivityId, summary.ActivityId);
                Assert.Equal($"root:{rootIdentity}", identity.SourceId);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Serve_resume_applies_metadata_before_initialization_and_emits_run_result_correlation()
    {
        await using var fixture = await ServeIntegrationFixture.CreateAsync(
            startupOverride: null,
            persistedOverride: "persisted");

        var initialize = await fixture.InitializeAsync("session1");
        var prompt = await fixture.PromptAsync("go");
        var completed = fixture.Notifications
            .Where(item => item.Method == ServeMethods.EventTurnComplete)
            .Select(item => ServeJson.FromNode<TurnCompleteEvent>(item.Params))
            .Last();
        var identity = Assert.Single(fixture.LoopFactory.Identities);

        Assert.Equal("persisted", fixture.OverrideObservedAtInitialize);
        Assert.Equal(ServeMethods.ProtocolVersion, initialize.ProtocolVersion);
        Assert.Equal("session1", initialize.SessionId);
        Assert.True(prompt.Ok);
        Assert.NotNull(completed);
        Assert.Equal(identity.RootTurnId, completed!.RootTurnId);
        Assert.Equal(identity.ActivityId, completed.ActivityId);
    }

    private sealed class CapturingActivityLoopFactory : IAgentLoopFactory
    {
        public List<AgentLoopSpec> Specs { get; } = [];

        public List<ToolCallIdentity> Identities { get; } = [];

        public IAgentLoop Create(AgentLoopSpec spec)
        {
            this.Specs.Add(spec);
            return new CapturingActivityLoop(
                spec.ToolActivity ?? throw new InvalidOperationException("Expected root activity."),
                this.Identities);
        }
    }

    private sealed class CapturingActivityLoop(
        ToolActivityContext root,
        List<ToolCallIdentity> identities) : IAgentLoop
    {
        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(
            List<ChatMessage> history,
            IAgentSink sink,
            CancellationToken cancellationToken = default)
        {
            var identity = root.EnsureActivity().ForCall("call-1");
            identities.Add(identity);
            sink.OnToolQueued(identity, "read_file", """{"path":"a.txt"}""");
            sink.OnToolCall(identity, "read_file", """{"path":"a.txt"}""");
            sink.OnToolStatus(identity, "read_file", ToolCallStatus.Running);
            sink.OnToolResult(
                identity,
                "read_file",
                new ToolResult("content"),
                ToolCallStatus.Succeeded);
            return Task.CompletedTask;
        }
    }

    private sealed class CorrelationSink : IAgentSink
    {
        public List<ToolCallIdentity> Identities { get; } = [];

        public List<ToolActivitySummary> Completions { get; } = [];

        public void OnAssistantText(string delta) { }

        public void OnAssistantTextComplete() { }

        public void OnToolCall(string toolName, string inputPreview) { }

        public void OnToolResult(string toolName, ToolResult result) { }

        public void OnError(string message) { }

        public void OnToolQueued(ToolCallIdentity identity, string toolName, string inputJson) =>
            this.Identities.Add(identity);

        public void OnToolCall(ToolCallIdentity identity, string toolName, string inputJson) =>
            this.Identities.Add(identity);

        public void OnToolStatus(ToolCallIdentity identity, string toolName, ToolCallStatus status) =>
            this.Identities.Add(identity);

        public void OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status) =>
            this.Identities.Add(identity);

        public void OnToolActivityCompleted(ToolActivitySummary summary) =>
            this.Completions.Add(summary);
    }

    private sealed record RecordedNotification(string Method, JsonNode? Params);

    private sealed class ServeIntegrationFixture : IAsyncDisposable
    {
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

        private readonly string root;
        private readonly DuplexStreamPair pair;
        private readonly CancellationTokenSource cancellation;
        private readonly ServeHost host;
        private readonly JsonRpcConnection orchestrator;
        private readonly Task hostTask;
        private readonly TaskCompletionSource turnComplete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object notificationsGate = new();
        private readonly List<RecordedNotification> notifications = [];

        private ServeIntegrationFixture(
            string root,
            DuplexStreamPair pair,
            CancellationTokenSource cancellation,
            ServeHost host,
            JsonRpcConnection orchestrator,
            Task hostTask,
            CapturingActivityLoopFactory loopFactory)
        {
            this.root = root;
            this.pair = pair;
            this.cancellation = cancellation;
            this.host = host;
            this.orchestrator = orchestrator;
            this.hostTask = hostTask;
            this.LoopFactory = loopFactory;
        }

        public CapturingActivityLoopFactory LoopFactory { get; }

        public string? OverrideObservedAtInitialize { get; private set; }

        public IReadOnlyList<RecordedNotification> Notifications
        {
            get
            {
                lock (this.notificationsGate)
                {
                    return [.. this.notifications];
                }
            }
        }

        public static async Task<ServeIntegrationFixture> CreateAsync(
            string? startupOverride,
            string? persistedOverride)
        {
            var root = Directory.CreateTempSubdirectory("coda_cli_tui_mcp_").FullName;
            await new SessionTranscriptStore(root).SaveAsync(
                "session1",
                [ChatMessage.UserText("history")],
                new SessionMetadata { SystemPromptOverride = persistedOverride });

            var pair = new DuplexStreamPair();
            var cancellation = new CancellationTokenSource();
            var factory = new CapturingActivityLoopFactory();
            ServeIntegrationFixture? fixture = null;
            var host = new ServeHost(
                pair.ServerReads,
                pair.ServerWrites,
                (permission, question, plan) => new CodaSession(
                    CredentialFixtures.SignedInClaude(),
                    new SessionOptions
                    {
                        ProviderId = ClaudeAiProvider.Id,
                        Model = "claude-sonnet-4-6",
                        WorkingDirectory = root,
                        PermissionMode = PermissionMode.BypassPermissions,
                        SystemPromptOverride = startupOverride,
                        InteractivePrompt = permission,
                        UserQuestionPrompt = question,
                        PlanApprover = plan,
                    },
                    httpClient: new HttpClient(new SseTestHandler(SseTestHandler.MessageStopOnly)),
                    agentLoopFactory: factory),
                expectedApiKey: null,
                initializeSession: (session, cancellationToken) =>
                {
                    fixture!.OverrideObservedAtInitialize = session.Options.SystemPromptOverride;
                    return session.InitializeAsync(cancellationToken);
                });
            var hostTask = host.RunAsync(cancellation.Token);
            var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
            fixture = new ServeIntegrationFixture(root, pair, cancellation, host, orchestrator, hostTask, factory);
            orchestrator.OnNotification(ServeMethods.EventTurnComplete, node =>
            {
                lock (fixture.notificationsGate)
                {
                    fixture.notifications.Add(new RecordedNotification(ServeMethods.EventTurnComplete, node));
                }

                fixture.turnComplete.TrySetResult();
            });

            return fixture;
        }

        public async Task<InitializeResult> InitializeAsync(string sessionId)
        {
            var node = await this.orchestrator.SendRequestAsync(
                ServeMethods.Initialize,
                ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null, SessionId: sessionId)),
                CancellationToken.None).WaitAsync(WaitTimeout);
            return Assert.IsType<InitializeResult>(ServeJson.FromNode<InitializeResult>(node));
        }

        public async Task<PromptResult> PromptAsync(string text)
        {
            var node = await this.orchestrator.SendRequestAsync(
                ServeMethods.Prompt,
                ServeJson.ToNode(new PromptParams { Text = text }),
                CancellationToken.None).WaitAsync(WaitTimeout);
            await this.turnComplete.Task.WaitAsync(WaitTimeout);
            return Assert.IsType<PromptResult>(ServeJson.FromNode<PromptResult>(node));
        }

        public async ValueTask DisposeAsync()
        {
            this.cancellation.Cancel();
            try
            {
                await this.hostTask.WaitAsync(WaitTimeout);
            }
            catch
            {
                // Shutdown closes the in-memory transport while pending reads unwind.
            }

            await this.orchestrator.DisposeAsync();
            await this.host.DisposeAsync();
            this.cancellation.Dispose();
            this.pair.Dispose();
            try { Directory.Delete(this.root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static IReadOnlyList<ChatMessage> CorrelatedHistory() =>
    [
        ChatMessage.UserText("go"),
        new ChatMessage(
            ChatRole.Assistant,
            [
                new ToolUseBlock("call-1", "read_file", """{"path":"a.txt"}""")
                {
                    RootTurnId = "root-1",
                    ActivityId = "activity-1",
                    SourceId = "root:root-1",
                },
            ]),
        new ChatMessage(
            ChatRole.User,
            [
                new ToolResultBlock("call-1", "content")
                {
                    RootTurnId = "root-1",
                    ActivityId = "activity-1",
                    SourceId = "root:root-1",
                    ToolStatus = nameof(ToolCallStatus.Succeeded),
                },
            ]),
    ];

    private static void AssertOptionalString(JsonObject json, string name, string? expected)
    {
        if (expected is null)
        {
            Assert.False(json.ContainsKey(name));
            return;
        }

        Assert.Equal(expected, json[name]!.GetValue<string>());
    }

    private static void AssertCorrelated(JsonObject use, JsonObject result)
    {
        Assert.Equal("call-1", use["id"]!.GetValue<string>());
        Assert.Equal("root-1", use["rootTurnId"]!.GetValue<string>());
        Assert.Equal("activity-1", use["activityId"]!.GetValue<string>());
        Assert.Equal("root:root-1", use["sourceId"]!.GetValue<string>());
        Assert.Equal("call-1", result["toolUseId"]!.GetValue<string>());
        Assert.Equal("root-1", result["rootTurnId"]!.GetValue<string>());
        Assert.Equal("activity-1", result["activityId"]!.GetValue<string>());
        Assert.Equal("root:root-1", result["sourceId"]!.GetValue<string>());
        Assert.Equal(nameof(ToolCallStatus.Succeeded), result["toolStatus"]!.GetValue<string>());
    }

    private static void AssertCorrelated(ToolUseBlock use, ToolResultBlock result)
    {
        Assert.Equal("call-1", use.Id);
        Assert.Equal("root-1", use.RootTurnId);
        Assert.Equal("activity-1", use.ActivityId);
        Assert.Equal("root:root-1", use.SourceId);
        Assert.Equal("call-1", result.ToolUseId);
        Assert.Equal("root-1", result.RootTurnId);
        Assert.Equal("activity-1", result.ActivityId);
        Assert.Equal("root:root-1", result.SourceId);
        Assert.Equal(nameof(ToolCallStatus.Succeeded), result.ToolStatus);
    }
}
