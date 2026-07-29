using System.Text.Json.Nodes;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Tests for <see cref="CacheMinimumPrefix"/>, <see cref="PromptCachePlanner"/>, and the
/// cache-breakpoint integration in <see cref="AnthropicMessagesClient.BuildBody"/>.
/// </summary>
public sealed class PromptCachePlannerTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    // 5 000 chars ≈ 1 250 tokens — above the 1 024-token minimum for sonnet/opus-4.x/unknown.
    private static string LargeSystem() => new string('x', 5000);

    // 20 000 chars ≈ 5 000 tokens — above the 4 096-token minimum for haiku-4-5.
    private static string HugeSystem() => new string('x', 20000);

    private static ToolDefinition MakeTool(string name) =>
        new(name, "a description", "{}");

    private static ChatMessage UserMsg(string text) =>
        ChatMessage.UserText(text);

    private static ChatMessage AssistantMsg(string text) =>
        new(ChatRole.Assistant, [new TextBlock(text)]);

    // Count occurrences of the "cache_control" key in the serialized JSON.
    private static int CountCacheControlMarkers(string json)
    {
        var count = 0;
        var pos = 0;
        while ((pos = json.IndexOf("\"cache_control\"", pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos++;
        }

        return count;
    }

    // ── CacheMinimumPrefix — tier lookup ──────────────────────────────────────

    [Theory]
    [InlineData("claude-opus-5", 512)]
    [InlineData("claude-opus-5-20260101", 512)]
    [InlineData("claude-fable-5", 512)]
    [InlineData("claude-mythos-5", 512)]
    [InlineData("claude-opus-4-6", 4096)]
    [InlineData("claude-opus-4-5", 4096)]
    [InlineData("claude-haiku-4-5", 4096)]
    // Dotted spellings — catalog ships both forms; normalisation must make them equivalent.
    [InlineData("claude-haiku-4.5", 4096)]
    [InlineData("claude-opus-4.5", 4096)]
    [InlineData("claude-opus-4-7", 2048)]
    [InlineData("claude-opus-4.7", 2048)]
    [InlineData("claude-haiku-3-5", 2048)]
    [InlineData("claude-mythos-preview", 2048)]
    [InlineData("claude-sonnet-4-6", 1024)]
    [InlineData("claude-sonnet-4-5", 1024)]
    [InlineData("claude-opus-4-8", 1024)]
    [InlineData("claude-opus-4-1", 1024)]
    [InlineData("unknown-model-xyz", 1024)]
    [InlineData("", 1024)]
    [InlineData(null, 1024)]
    public void CacheMinimumPrefix_returns_correct_tier_for_model(string? model, int expected)
    {
        Assert.Equal(expected, CacheMinimumPrefix.For(model));
    }

    // ── PromptCachePlanner — below-minimum guard ───────────────────────────────

    [Fact]
    public void Plan_returns_None_when_prefix_is_below_model_minimum()
    {
        // "claude-sonnet-4-6" needs 1 024 tokens; "hi" + 1 short message ≈ 0 tokens.
        var plan = PromptCachePlanner.Plan(
            model: "claude-sonnet-4-6",
            system: "hi",
            tools: [MakeTool("t1"), MakeTool("t2")],
            messages: [UserMsg("hello")],
            toolsVolatile: false);

        Assert.Equal(CachePlan.None, plan);
        Assert.False(plan.ToolsBreakpoint);
        Assert.Equal(-1, plan.AnchorMessageIndex);
        Assert.Equal(-1, plan.RollingMessageIndex);
    }

    // ── PromptCachePlanner — above-minimum, full plan ─────────────────────────

    [Fact]
    public void Plan_places_tools_and_two_message_breakpoints_when_above_minimum()
    {
        IReadOnlyList<ChatMessage> messages =
        [
            UserMsg("msg-0"),      // index 0 — second-to-last user → anchor
            AssistantMsg("asst"),  // index 1
            UserMsg("msg-2"),      // index 2 — last user → rolling write
        ];

        var plan = PromptCachePlanner.Plan(
            model: "claude-sonnet-4-6",
            system: LargeSystem(),
            tools: [MakeTool("t1")],
            messages: messages,
            toolsVolatile: false);

        Assert.True(plan.ToolsBreakpoint);
        Assert.Equal(0, plan.AnchorMessageIndex);
        Assert.Equal(2, plan.RollingMessageIndex);

        // Including the unconditional system breakpoint the total is ≤ 4.
        var totalBreakpoints =
            1 // system (always)
            + (plan.ToolsBreakpoint ? 1 : 0)
            + (plan.AnchorMessageIndex >= 0 ? 1 : 0)
            + (plan.RollingMessageIndex >= 0 ? 1 : 0);
        Assert.True(totalBreakpoints <= 4, $"Total breakpoints {totalBreakpoints} exceeds 4");
    }

    [Fact]
    public void Plan_skips_tools_breakpoint_when_tools_volatile()
    {
        IReadOnlyList<ChatMessage> messages =
        [
            UserMsg("msg-0"),
            AssistantMsg("asst"),
            UserMsg("msg-2"),
        ];

        var plan = PromptCachePlanner.Plan(
            model: "claude-sonnet-4-6",
            system: LargeSystem(),
            tools: [MakeTool("t1")],
            messages: messages,
            toolsVolatile: true);

        Assert.False(plan.ToolsBreakpoint);
        // Message breakpoints are still placed; total remains ≤ 4.
        Assert.Equal(0, plan.AnchorMessageIndex);
        Assert.Equal(2, plan.RollingMessageIndex);
    }

    [Fact]
    public void Plan_skips_tools_breakpoint_when_tools_list_is_empty()
    {
        IReadOnlyList<ChatMessage> messages =
        [
            UserMsg("msg-0"),
            AssistantMsg("asst"),
            UserMsg("msg-2"),
        ];

        var plan = PromptCachePlanner.Plan(
            model: "claude-sonnet-4-6",
            system: LargeSystem(),
            tools: [],
            messages: messages,
            toolsVolatile: false);

        Assert.False(plan.ToolsBreakpoint);
    }

    // ── Anchor / rolling-write slot indices ───────────────────────────────────

    [Fact]
    public void Plan_anchor_is_second_to_last_user_message_and_rolling_is_last()
    {
        // User messages at indices 0, 2, 4 → anchor = 2, rolling = 4.
        IReadOnlyList<ChatMessage> messages =
        [
            UserMsg("u0"),       // index 0
            AssistantMsg("a1"),  // index 1
            UserMsg("u2"),       // index 2 — anchor
            AssistantMsg("a3"),  // index 3
            UserMsg("u4"),       // index 4 — rolling write
        ];

        var plan = PromptCachePlanner.Plan(
            model: "claude-sonnet-4-6",
            system: LargeSystem(),
            tools: [],
            messages: messages,
            toolsVolatile: false);

        Assert.Equal(2, plan.AnchorMessageIndex);
        Assert.Equal(4, plan.RollingMessageIndex);
    }

    // ── Fewer than two user messages ──────────────────────────────────────────

    [Fact]
    public void Plan_with_single_user_message_places_only_rolling_write_no_anchor()
    {
        var plan = PromptCachePlanner.Plan(
            model: "claude-sonnet-4-6",
            system: LargeSystem(),
            tools: [],
            messages: [UserMsg("only user")],
            toolsVolatile: false);

        Assert.Equal(-1, plan.AnchorMessageIndex);
        Assert.Equal(0, plan.RollingMessageIndex);
    }

    [Fact]
    public void Plan_with_no_user_messages_places_no_message_breakpoints_but_tools_breakpoint_remains()
    {
        var plan = PromptCachePlanner.Plan(
            model: "claude-sonnet-4-6",
            system: LargeSystem(),
            tools: [MakeTool("t1")],
            messages: [AssistantMsg("only an assistant message")],
            toolsVolatile: false);

        Assert.Equal(-1, plan.AnchorMessageIndex);
        Assert.Equal(-1, plan.RollingMessageIndex);
        Assert.True(plan.ToolsBreakpoint);
    }

    [Fact]
    public void Plan_with_empty_message_list_does_not_throw()
    {
        var plan = PromptCachePlanner.Plan(
            model: "claude-sonnet-4-6",
            system: LargeSystem(),
            tools: [],
            messages: [],
            toolsVolatile: false);

        Assert.Equal(-1, plan.AnchorMessageIndex);
        Assert.Equal(-1, plan.RollingMessageIndex);
    }

    // ── BuildBody integration ─────────────────────────────────────────────────

    [Fact]
    public void BuildBody_emits_at_most_4_cache_control_markers()
    {
        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            System = LargeSystem(),
            Messages =
            [
                UserMsg("user-0"),
                AssistantMsg("asst-1"),
                UserMsg("user-2"),
            ],
            Tools = [MakeTool("t1"), MakeTool("t2")],
        };

        var body = AnthropicMessagesClient.BuildBody(request);
        var json = body.ToJsonString();
        var count = CountCacheControlMarkers(json);

        Assert.True(count <= 4, $"Expected ≤ 4 cache_control markers, found {count}");
    }

    [Fact]
    public void BuildBody_places_cache_control_on_correct_blocks()
    {
        // Two tools → cache_control only on the last one.
        // Three messages (user/asst/user) → anchor on messages[0], rolling on messages[2].
        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            System = LargeSystem(),
            Messages =
            [
                UserMsg("user-0"),   // anchor
                AssistantMsg("a1"),
                UserMsg("user-2"),   // rolling write
            ],
            Tools = [MakeTool("first-tool"), MakeTool("last-tool")],
        };

        var body = AnthropicMessagesClient.BuildBody(request);

        // System block always has cache_control.
        var sysBlock = body["system"]!.AsArray()[0]!.AsObject();
        Assert.Equal("ephemeral", (string?)sysBlock["cache_control"]!["type"]);

        // Last tool has cache_control; first tool does not.
        var toolsArr = body["tools"]!.AsArray();
        Assert.Null(toolsArr[0]!["cache_control"]);
        Assert.Equal("ephemeral", (string?)toolsArr[^1]!["cache_control"]!["type"]);

        // Anchor: last content block of messages[0] (user-0) has cache_control.
        var msgArr = body["messages"]!.AsArray();
        var anchorContent = msgArr[0]!["content"]!.AsArray();
        Assert.Equal("ephemeral", (string?)anchorContent[^1]!["cache_control"]!["type"]);

        // Rolling write: last content block of messages[2] (user-2) has cache_control.
        var rollingContent = msgArr[2]!["content"]!.AsArray();
        Assert.Equal("ephemeral", (string?)rollingContent[^1]!["cache_control"]!["type"]);

        // Assistant message (index 1) must have no cache_control on any block.
        var asstContent = msgArr[1]!["content"]!.AsArray();
        foreach (var block in asstContent)
        {
            Assert.Null(block!["cache_control"]);
        }

        // Exactly 4 markers total: system + last-tool + anchor + rolling.
        var json = body.ToJsonString();
        Assert.Equal(4, CountCacheControlMarkers(json));
    }

    [Fact]
    public void BuildBody_with_volatile_tools_omits_tools_cache_control()
    {
        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            System = LargeSystem(),
            Messages =
            [
                UserMsg("user-0"),
                AssistantMsg("asst"),
                UserMsg("user-2"),
            ],
            Tools = [MakeTool("tool1")],
            ToolsVolatile = true,
        };

        var body = AnthropicMessagesClient.BuildBody(request);

        // No cache_control on any tool.
        var toolsArr = body["tools"]!.AsArray();
        foreach (var tool in toolsArr)
        {
            Assert.Null(tool!["cache_control"]);
        }

        // Total markers: system(1) + anchor(1) + rolling(1) = 3.
        var json = body.ToJsonString();
        Assert.Equal(3, CountCacheControlMarkers(json));
    }

    [Fact]
    public void BuildBody_regression_small_request_only_system_has_cache_control()
    {
        // A small request is below any model minimum — the output must be byte-identical
        // to the pre-Phase-1 shape: only the system block has cache_control.
        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            System = "system prompt",
            Messages = [UserMsg("hello")],
        };

        var body = AnthropicMessagesClient.BuildBody(request);
        var json = body.ToJsonString();

        // System keeps its cache_control.
        Assert.Equal("ephemeral", (string?)body["system"]![0]!["cache_control"]!["type"]);

        // Exactly 1 marker total — messages and (absent) tools are plain.
        Assert.Equal(1, CountCacheControlMarkers(json));

        // Message content is a block array without cache_control.
        var msgContent = body["messages"]!.AsArray()[0]!["content"]!.AsArray();
        Assert.Null(msgContent[0]!["cache_control"]);
    }

    [Fact]
    public void BuildBody_haiku_4_5_requires_large_prefix_before_caching()
    {
        // haiku-4-5 needs 4 096 tokens minimum.
        // A 5 000-char system (~1 250 tokens) is below that, so plan = None.
        var requestSmall = new ChatRequest
        {
            Model = "claude-haiku-4-5",
            System = LargeSystem(),   // ~1 250 tokens — below haiku-4-5 minimum (4 096)
            Messages = [UserMsg("user-0"), AssistantMsg("asst"), UserMsg("user-2")],
            Tools = [MakeTool("t1")],
        };
        var jsonSmall = AnthropicMessagesClient.BuildBody(requestSmall).ToJsonString();
        Assert.Equal(1, CountCacheControlMarkers(jsonSmall)); // system only

        // A 20 000-char system (~5 000 tokens) exceeds the minimum → full plan.
        var requestLarge = new ChatRequest
        {
            Model = "claude-haiku-4-5",
            System = HugeSystem(),    // ~5 000 tokens — above haiku-4-5 minimum (4 096)
            Messages = [UserMsg("user-0"), AssistantMsg("asst"), UserMsg("user-2")],
            Tools = [MakeTool("t1")],
        };
        var jsonLarge = AnthropicMessagesClient.BuildBody(requestLarge).ToJsonString();
        Assert.Equal(4, CountCacheControlMarkers(jsonLarge)); // system + tool + anchor + rolling
    }

    // ── Multi-block message placement ─────────────────────────────────────────

    [Fact]
    public void BuildBody_places_cache_control_on_last_block_of_multiblock_anchor_message()
    {
        // Anchor message has three blocks: two ToolResultBlocks and a trailing TextBlock.
        // cache_control must land on content[^1] (the TextBlock) and be absent on the two
        // earlier blocks. A naive content[0] implementation makes this test fail.
        var anchor = new ChatMessage(ChatRole.User, [
            new ToolResultBlock("tool-id-1", "result 1"),
            new ToolResultBlock("tool-id-2", "result 2"),
            new TextBlock("follow-up text"),
        ]);

        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            System = LargeSystem(),
            Messages = [anchor, AssistantMsg("a"), UserMsg("rolling")],
        };

        var body = AnthropicMessagesClient.BuildBody(request);
        var msgArr = body["messages"]!.AsArray();
        var anchorContent = msgArr[0]!["content"]!.AsArray();

        Assert.Equal(3, anchorContent.Count);

        // Earlier blocks must NOT carry the marker.
        Assert.Null(anchorContent[0]!["cache_control"]);
        Assert.Null(anchorContent[1]!["cache_control"]);

        // Last block must carry the marker.
        Assert.Equal("ephemeral", (string?)anchorContent[^1]!["cache_control"]!["type"]);
    }

    [Fact]
    public void BuildBody_places_cache_control_on_last_block_of_multiblock_rolling_write_message()
    {
        // Rolling-write message has three blocks: two ToolResultBlocks and a trailing TextBlock.
        // cache_control must land on content[^1] (the TextBlock) and be absent on the two
        // earlier blocks. A naive content[0] implementation makes this test fail.
        var rolling = new ChatMessage(ChatRole.User, [
            new ToolResultBlock("tool-id-1", "result 1"),
            new ToolResultBlock("tool-id-2", "result 2"),
            new TextBlock("follow-up text"),
        ]);

        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            System = LargeSystem(),
            Messages = [UserMsg("anchor"), AssistantMsg("a"), rolling],
        };

        var body = AnthropicMessagesClient.BuildBody(request);
        var msgArr = body["messages"]!.AsArray();
        var rollingContent = msgArr[2]!["content"]!.AsArray();

        Assert.Equal(3, rollingContent.Count);

        // Earlier blocks must NOT carry the marker.
        Assert.Null(rollingContent[0]!["cache_control"]);
        Assert.Null(rollingContent[1]!["cache_control"]);

        // Last block must carry the marker.
        Assert.Equal("ephemeral", (string?)rollingContent[^1]!["cache_control"]!["type"]);
    }
}
