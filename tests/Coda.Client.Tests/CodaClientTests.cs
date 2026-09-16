using System.Text.Json.Nodes;

using Coda.Client;
using Coda.Client.Protocol;
using Coda.Client.Tests.Harness;
using Coda.Client.Transport;

namespace Coda.Client.Tests;

/// <summary>
/// End-to-end behaviour of the typed client against the fake engine: the
/// reference initialize → getState flow, a prompt that stays pending while
/// events and a reverse request flow past it, the honest reading of an
/// <c>ok:false</c> turn, the goal-budget wire mapping through the typed helper,
/// the raw escape hatch, and the survival of unknown methods and fields.
/// </summary>
public sealed class CodaClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static CodaClient DeclineClient(FakeEngine engine) =>
        CodaClient.Attach(engine.ClientRead, engine.ClientWrite,
            new CodaClientOptions { ReverseRequestHandler = ReverseRequestHandlers.Decline });

    private static async Task<CodaEvent> NextEventAsync(CodaClient client)
    {
        using var cts = new CancellationTokenSource(Timeout);
        return await client.Events.ReadAsync(cts.Token);
    }

    [Fact]
    public async Task Initialize_negotiates_and_exposes_capabilities_as_the_runtime_authority()
    {
        await using var engine = new FakeEngine();
        await using var client = DeclineClient(engine);

        Task<InitializeResult> initTask = client.InitializeAsync(new InitializeParams
        {
            ClientInfo = "test-client",
            ClientCapabilities = new ClientCapabilities { StateEvents = true },
        });

        JsonObject request = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Equal("initialize", (string?)request["method"]);
        Assert.Equal("1", (string?)request["params"]!["protocolVersion"]);
        Assert.True((bool)request["params"]!["clientCapabilities"]!["stateEvents"]!);

        await engine.RespondSuccessAsync(request["id"]!, new JsonObject
        {
            ["protocolVersion"] = "1",
            ["sessionId"] = "s1",
            ["serverInfo"] = "coda",
            ["contractVersion"] = "2026-09-1",
            ["engineInstanceId"] = "e1",
            ["eventCursor"] = 0,
            ["capabilities"] = new JsonObject
            {
                ["schedules.bounds"] = new JsonObject { ["supported"] = true },
                ["events.payloadLimitNegotiation"] = new JsonObject { ["supported"] = false, ["reason"] = "unsupported" },
            },
        });

        InitializeResult init = await initTask.WaitAsync(Timeout);
        Assert.Equal("s1", init.SessionId);
        Assert.Equal("e1", init.EngineInstanceId);
        Assert.True(init.IsCapabilitySupported("schedules.bounds"));
        Assert.False(init.IsCapabilitySupported("events.payloadLimitNegotiation"));
        Assert.False(init.IsCapabilitySupported("a.capability.never.advertised"));
        Assert.Same(init, client.LastInitializeResult);
    }

    [Fact]
    public async Task Get_state_sends_no_sections_and_exposes_the_snapshot_raw()
    {
        await using var engine = new FakeEngine();
        await using var client = DeclineClient(engine);

        Task<StateSnapshot> stateTask = client.GetStateAsync();
        JsonObject request = await engine.ReadMessageAsync().WaitAsync(Timeout);

        Assert.Equal("session/getState", (string?)request["method"]);
        // `sections` filtering is unsupported, so the client sends no params at all.
        Assert.False(request.ContainsKey("params") && request["params"] is not null);

        await engine.RespondSuccessAsync(request["id"]!, new JsonObject
        {
            ["engineInstanceId"] = "e1",
            ["sessionId"] = "s1",
            ["cursor"] = 5,
            ["lifecycle"] = "ready",
            ["someNewSection"] = new JsonObject { ["x"] = 1 },
        });

        StateSnapshot state = await stateTask.WaitAsync(Timeout);
        Assert.Equal("e1", state.EngineInstanceId);
        Assert.Equal(5, state.Cursor);
        Assert.Equal("ready", state.Lifecycle);
        // Nothing is narrowed away: an unmodelled section is still reachable.
        Assert.Equal(1, (int)state.Raw["someNewSection"]!["x"]!);
    }

    [Fact]
    public async Task A_prompt_stays_pending_while_events_and_a_reverse_request_flow_past_it()
    {
        await using var engine = new FakeEngine();
        await using var client = DeclineClient(engine);

        Task<PromptResult> promptTask = client.PromptAsync("do the thing");
        JsonObject promptMessage = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Equal("session/prompt", (string?)promptMessage["method"]);
        Assert.Equal("do the thing", (string?)promptMessage["params"]!["text"]);
        Assert.False(promptTask.IsCompleted);

        // A streamed event and a permission request arrive mid-turn.
        await engine.SendNotificationAsync("event/assistantText", new JsonObject { ["delta"] = "partial", ["seq"] = 1 });
        await engine.SendServerRequestAsync(200, "request/permission",
            new JsonObject { ["toolName"] = "write_file", ["inputPreview"] = "x" });

        // The decline policy answers the reverse request without granting.
        JsonObject decline = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Equal(200, (int)decline["id"]!);
        Assert.Equal(RpcErrorCodes.RequestCancelled, (int)decline["error"]!["code"]!);

        // The event is delivered on the stream, all while the prompt is unfinished.
        CodaEvent streamed = await NextEventAsync(client);
        Assert.Equal("event/assistantText", streamed.Method);
        Assert.Equal("partial", (string?)streamed.Params!["delta"]);
        Assert.False(promptTask.IsCompleted);

        await engine.RespondSuccessAsync(promptMessage["id"]!, new JsonObject { ["ok"] = true, ["interrupted"] = false });
        PromptResult result = await promptTask.WaitAsync(Timeout);
        Assert.True(result.Ok);
        Assert.False(result.Interrupted);
    }

    [Fact]
    public async Task A_successful_round_trip_that_reports_ok_false_is_surfaced_as_a_failed_turn()
    {
        await using var engine = new FakeEngine();
        await using var client = DeclineClient(engine);

        Task<PromptResult> promptTask = client.PromptAsync("x");
        JsonObject message = await engine.ReadMessageAsync().WaitAsync(Timeout);
        await engine.RespondSuccessAsync(message["id"]!, new JsonObject
        {
            ["ok"] = false,
            ["interrupted"] = false,
            ["error"] = "no provider configured",
        });

        PromptResult result = await promptTask.WaitAsync(Timeout);
        Assert.False(result.Ok);
        Assert.Equal("no provider configured", result.Error);
    }

    [Fact]
    public async Task A_goal_status_on_the_prompt_result_is_parsed_and_an_unknown_outcome_survives()
    {
        await using var engine = new FakeEngine();
        await using var client = DeclineClient(engine);

        Task<PromptResult> promptTask = client.PromptAsync("x");
        JsonObject message = await engine.ReadMessageAsync().WaitAsync(Timeout);
        await engine.RespondSuccessAsync(message["id"]!, new JsonObject
        {
            ["ok"] = true,
            ["interrupted"] = false,
            ["goalStatus"] = new JsonObject
            {
                ["outcome"] = "GenuinelyBlocked",
                ["remaining"] = "needs a credential",
                ["continuations"] = 5,
                ["elapsedSeconds"] = 1.5,
            },
        });

        PromptResult result = await promptTask.WaitAsync(Timeout);
        Assert.NotNull(result.GoalStatus);
        Assert.Equal("GenuinelyBlocked", result.GoalStatus!.Outcome);
        Assert.Equal("needs a credential", result.GoalStatus.Remaining);
        Assert.Equal(5, result.GoalStatus.Continuations);
    }

    [Fact]
    public async Task Interrupt_sends_the_route_and_returns_the_ack()
    {
        await using var engine = new FakeEngine();
        await using var client = DeclineClient(engine);

        Task<OkResult> interruptTask = client.InterruptAsync();
        JsonObject message = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Equal("session/interrupt", (string?)message["method"]);
        await engine.RespondSuccessAsync(message["id"]!, new JsonObject { ["ok"] = true });

        OkResult result = await interruptTask.WaitAsync(Timeout);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Set_goal_maps_the_typed_budgets_onto_the_wire_and_replaces_the_whole_config()
    {
        await using var engine = new FakeEngine();
        await using var client = DeclineClient(engine);

        Task<SetGoalResult> goalTask = client.SetGoalAsync(
            "all tests pass",
            ContinuationBudget.Limited(200),
            DurationBudget.Limited(TimeSpan.FromMinutes(30)));

        JsonObject message = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Equal("session/setGoal", (string?)message["method"]);
        JsonObject sent = (JsonObject)message["params"]!;
        Assert.Equal("all tests pass", (string?)sent["goal"]);
        Assert.Equal(200, (int)sent["maxContinuations"]!);
        Assert.Equal("30m", (string?)sent["maxDuration"]);

        await engine.RespondSuccessAsync(message["id"]!, new JsonObject
        {
            ["ok"] = true,
            ["goal"] = "all tests pass",
            ["maxDuration"] = "30m",
            ["maxContinuations"] = 200,
        });
        SetGoalResult result = await goalTask.WaitAsync(Timeout);
        Assert.True(result.Ok);

        // A default-budget call omits the fields, so the engine restores defaults.
        Task<SetGoalResult> defaultsTask = client.SetGoalAsync("ship");
        JsonObject defaultsMessage = await engine.ReadMessageAsync().WaitAsync(Timeout);
        JsonObject defaultsSent = (JsonObject)defaultsMessage["params"]!;
        Assert.Equal("ship", (string?)defaultsSent["goal"]);
        Assert.False(defaultsSent.ContainsKey("maxContinuations"));
        Assert.False(defaultsSent.ContainsKey("maxDuration"));
        await engine.RespondSuccessAsync(defaultsMessage["id"]!, new JsonObject { ["ok"] = true, ["goal"] = "ship" });
        await defaultsTask.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Clearing_a_goal_sends_setgoal_with_no_fields_and_is_not_an_interrupt()
    {
        await using var engine = new FakeEngine();
        await using var client = DeclineClient(engine);

        Task<SetGoalResult> clearTask = client.ClearGoalAsync();
        JsonObject message = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Equal("session/setGoal", (string?)message["method"]);
        Assert.False(message["params"]!.AsObject().ContainsKey("goal"));

        await engine.RespondSuccessAsync(message["id"]!, new JsonObject { ["ok"] = true });
        SetGoalResult result = await clearTask.WaitAsync(Timeout);
        Assert.True(result.Ok);
        Assert.Null(result.Goal);
    }

    [Fact]
    public async Task The_raw_escape_hatch_calls_a_method_with_no_typed_helper()
    {
        await using var engine = new FakeEngine();
        await using var client = DeclineClient(engine);

        Task<JsonNode?> rawTask = client.SendRequestAsync("session/listSessions", new JsonObject { ["limit"] = 10 });
        JsonObject message = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Equal("session/listSessions", (string?)message["method"]);
        Assert.Equal(10, (int)message["params"]!["limit"]!);

        await engine.RespondSuccessAsync(message["id"]!, new JsonObject { ["sessions"] = new JsonArray() });
        JsonNode? result = await rawTask.WaitAsync(Timeout);
        Assert.NotNull(result!["sessions"]);
    }

    [Fact]
    public async Task An_unknown_event_method_and_its_unknown_fields_survive_the_round_trip()
    {
        await using var engine = new FakeEngine();
        await using var client = DeclineClient(engine);

        await engine.SendNotificationAsync("event/futureThing",
            new JsonObject { ["brandNewField"] = 42, ["seq"] = 7, ["engineInstanceId"] = "e1" });

        CodaEvent streamed = await NextEventAsync(client);
        Assert.Equal("event/futureThing", streamed.Method);
        Assert.Equal(42, (int)streamed.Params!["brandNewField"]!);
        Assert.Equal(7, streamed.Seq);
        Assert.Equal("e1", streamed.EngineInstanceId);
    }

    [Fact]
    public async Task Unknown_top_level_fields_on_a_typed_result_are_preserved_not_dropped()
    {
        await using var engine = new FakeEngine();
        await using var client = DeclineClient(engine);

        Task<GetEventsResult> eventsTask = client.GetEventsAsync(new GetEventsParams { EngineInstanceId = "e1", AfterCursor = 0 });
        JsonObject message = await engine.ReadMessageAsync().WaitAsync(Timeout);
        await engine.RespondSuccessAsync(message["id"]!, new JsonObject
        {
            ["engineInstanceId"] = "e1",
            ["events"] = new JsonArray(new JsonObject
            {
                ["seq"] = 1,
                ["engineInstanceId"] = "e1",
                ["method"] = "event/futureThing",
                ["params"] = new JsonObject { ["x"] = 1 },
            }),
            ["nextCursor"] = 1,
            ["truncated"] = false,
            ["oldestAvailableCursor"] = 0,
            ["aBrandNewTopLevelField"] = "surprise",
        });

        GetEventsResult result = await eventsTask.WaitAsync(Timeout);
        EventEnvelope envelope = Assert.Single(result.Events);
        Assert.Equal("event/futureThing", envelope.Method);
        Assert.Equal(1, envelope.Seq);
        Assert.NotNull(result.AdditionalData);
        Assert.True(result.AdditionalData!.ContainsKey("aBrandNewTopLevelField"));
    }
}
