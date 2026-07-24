using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Sdk;
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
