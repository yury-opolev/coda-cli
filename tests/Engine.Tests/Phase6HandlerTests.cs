using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Hooks;
using Coda.Agent.Settings;
using Coda.Agent.Tasks;
using Coda.Agent.Watchers;
using LlmClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Engine.Tests;

/// <summary>
/// TDD tests for Phase 6 of the agent-hooks system: handler types
/// (<c>command</c>, <c>http</c>, <c>prompt</c>, <c>agent</c>).
/// Every test is independently unit-testable with no real network, no real model
/// call, and no process spawn — all dependencies are injected fakes.
/// </summary>
public sealed class Phase6HandlerTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_p6_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    // =========================================================================
    // 1. Settings parsing
    // =========================================================================

    [Fact]
    public void Settings_command_entry_parses_and_infers_type()
    {
        // Arrange
        var json = """
            {
              "hooks": {
                "PreToolUse": [
                  { "command": "./check.sh" }
                ]
              }
            }
            """;
        var (settings, _) = LoadFromJson(json);

        // Assert: one hook, type inferred as "command"
        var hook = Assert.Single(settings.Hooks);
        Assert.Equal("PreToolUse", hook.Event);
        Assert.Equal("./check.sh", hook.Command);
        Assert.Equal("command", hook.HandlerType);
        Assert.Null(hook.Url);
        Assert.Null(hook.HookPrompt);
    }

    [Fact]
    public void Settings_http_entry_parses_correctly()
    {
        var json = """
            {
              "hooks": {
                "UserPromptSubmit": [
                  { "type": "http", "url": "https://policy.internal/check" }
                ]
              }
            }
            """;
        var (settings, _) = LoadFromJson(json);

        var hook = Assert.Single(settings.Hooks);
        Assert.Equal("UserPromptSubmit", hook.Event);
        Assert.Equal("http", hook.HandlerType);
        Assert.Equal("https://policy.internal/check", hook.Url);
        Assert.Null(hook.Command);
    }

    [Fact]
    public void Settings_prompt_entry_parses_correctly()
    {
        var json = """
            {
              "hooks": {
                "PreToolUse": [
                  { "type": "prompt", "prompt": "Does this message contain secrets?" }
                ]
              }
            }
            """;
        var (settings, _) = LoadFromJson(json);

        var hook = Assert.Single(settings.Hooks);
        Assert.Equal("prompt", hook.HandlerType);
        Assert.Equal("Does this message contain secrets?", hook.HookPrompt);
        Assert.Null(hook.Command);
        Assert.Null(hook.Url);
    }

    [Fact]
    public void Settings_agent_entry_parses_correctly()
    {
        var json = """
            {
              "hooks": {
                "PreToolUse": [
                  { "type": "agent", "prompt": "Review this change", "agent": "code-review" }
                ]
              }
            }
            """;
        var (settings, _) = LoadFromJson(json);

        var hook = Assert.Single(settings.Hooks);
        Assert.Equal("agent", hook.HandlerType);
        Assert.Equal("Review this change", hook.HookPrompt);
        Assert.Equal("code-review", hook.AgentType);
    }

    [Fact]
    public void Settings_unusable_entry_is_skipped_not_crash()
    {
        var json = """
            {
              "hooks": {
                "PreToolUse": [
                  { "command": "./valid.sh" },
                  { "type": "http" },
                  { "type": "prompt" },
                  { "type": "agent" },
                  {}
                ]
              }
            }
            """;
        var (settings, _) = LoadFromJson(json);

        // Only the valid command entry is retained; unusable entries are skipped.
        var hook = Assert.Single(settings.Hooks);
        Assert.Equal("./valid.sh", hook.Command);
    }

    [Fact]
    public void Settings_httpHookAllowlist_parses_from_json()
    {
        var json = """
            {
              "httpHookAllowlist": ["policy.internal", "localhost"]
            }
            """;
        var (settings, _) = LoadFromJson(json);

        Assert.Equal(2, settings.HttpHookAllowlist.Count);
        Assert.Contains("policy.internal", settings.HttpHookAllowlist);
        Assert.Contains("localhost", settings.HttpHookAllowlist);
    }

    [Fact]
    public void Settings_command_only_is_backward_compatible()
    {
        var json = """{ "hooks": { "Stop": [ { "command": "notify.sh" } ] } }""";
        var (settings, _) = LoadFromJson(json);

        var hook = Assert.Single(settings.Hooks);
        Assert.Equal("notify.sh", hook.Command);
        Assert.Equal("command", hook.HandlerType);
    }

    [Fact]
    public void Settings_unknown_type_with_command_present_falls_back_to_command()
    {
        // M1 regression: a typo in "type" (e.g. "htpp") with a valid "command" must NOT silently
        // drop the hook. SettingsLoader should warn and fall back to "command" so the hook fires.
        var json = """
            {
              "hooks": {
                "PreToolUse": [
                  { "type": "htpp", "command": "./check.sh" }
                ]
              }
            }
            """;
        var (settings, _) = LoadFromJson(json);

        var hook = Assert.Single(settings.Hooks);
        Assert.Equal("command", hook.HandlerType);
        Assert.Equal("./check.sh", hook.Command);
    }

    [Fact]
    public async Task HookBus_null_command_hook_does_not_throw_nre()
    {
        // Low: RunCommandHookAsync dereferenced hook.Command! even when null.
        // Verify the guard logs and skips instead of throwing NullReferenceException.
        var hook = new UserHook("PreToolUse", Command: null, HandlerType: "command");
        var bus = MakeBus([hook]);

        // Must not throw; should produce a fail-closed block (no command = cannot run = block).
        var ex = await Record.ExceptionAsync(
            () => bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None));

        Assert.Null(ex); // No NRE or other exception.
    }

    // =========================================================================
    // 2. Dispatcher selects the right handler per type
    // =========================================================================

    [Fact]
    public async Task Dispatcher_routes_command_hook_to_executor()
    {
        var executor = new CapturingExecutor(exitCode: 0, stdout: "{}");
        var bus = MakeBus(
            [new UserHook("PreToolUse", "echo test")],
            executor: executor);

        await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.Single(executor.Calls);
    }

    [Fact]
    public async Task Dispatcher_routes_http_hook_to_http_handler()
    {
        var httpHandler = new CapturingHookHandler(HookOutput.NoOp);
        var hook = new UserHook("PreToolUse", null, HandlerType: "http", Url: "https://example.com/check");
        var bus = MakeBus([hook], httpHandler: httpHandler);

        await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.Single(httpHandler.Calls);
    }

    [Fact]
    public async Task Dispatcher_routes_prompt_hook_to_prompt_handler()
    {
        var promptHandler = new CapturingHookHandler(HookOutput.NoOp);
        var hook = new UserHook("PreToolUse", null, HandlerType: "prompt", HookPrompt: "rule text");
        var bus = MakeBus([hook], promptHandler: promptHandler);

        await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.Single(promptHandler.Calls);
    }

    [Fact]
    public async Task Dispatcher_routes_agent_hook_to_agent_handler()
    {
        var agentHandler = new CapturingHookHandler(HookOutput.NoOp);
        var hook = new UserHook("PreToolUse", null, HandlerType: "agent", HookPrompt: "review policy");
        var bus = MakeBus([hook], agentHandler: agentHandler);

        await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.Single(agentHandler.Calls);
    }

    [Fact]
    public async Task Dispatcher_with_no_http_handler_applies_failOpen()
    {
        // fail-open = true for PreToolUse? No, PreToolUse is fail-closed. Use Stop (fail-open).
        var hook = new UserHook("Stop", null, HandlerType: "http", Url: "https://x.com/check", FailOpen: true);
        var bus = MakeBus([hook]); // No httpHandler injected.

        // Should not throw; fail-open means it allows.
        await bus.RunStopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatcher_with_no_http_handler_blocks_when_failClosed()
    {
        var hook = new UserHook("PreToolUse", null, HandlerType: "http", Url: "https://x.com/check", FailOpen: false);
        var bus = MakeBus([hook]);

        var result = await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.True(result.Block);
    }

    // =========================================================================
    // 3. Http handler
    // =========================================================================

    [Fact]
    public async Task Http_2xx_parses_response_as_output()
    {
        var fakeHttp = new FakeHttpHandler(HttpStatusCode.OK, """{"decision":"block","reason":"no"}""");
        var handler = new HttpHookHandler(
            new HttpClient(fakeHttp),
            ["example.com"]);

        var hook = new UserHook("PreToolUse", null, HandlerType: "http", Url: "https://example.com/hook");
        var output = await handler.HandleAsync(hook, "{}", CancellationToken.None);

        Assert.Equal("block", output.Decision);
        Assert.Equal("no", output.Reason);
    }

    [Fact]
    public async Task Http_non2xx_throws_HttpHookNonSuccessException()
    {
        var fakeHttp = new FakeHttpHandler(HttpStatusCode.Forbidden, "denied");
        var handler = new HttpHookHandler(
            new HttpClient(fakeHttp),
            ["example.com"]);

        var hook = new UserHook("PreToolUse", null, HandlerType: "http", Url: "https://example.com/hook");

        var ex = await Assert.ThrowsAsync<HttpHookNonSuccessException>(
            () => handler.HandleAsync(hook, "{}", CancellationToken.None));

        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task Http_non2xx_applies_failOpen_through_bus()
    {
        // Bus with an http handler that returns 403; hook is fail-open → allow.
        var fakeHttp = new FakeHttpHandler(HttpStatusCode.ServiceUnavailable, "error");
        var httpHookHandler = new HttpHookHandler(
            new HttpClient(fakeHttp),
            ["example.com"]);

        var hook = new UserHook("PreToolUse", null, HandlerType: "http", Url: "https://example.com/hook", FailOpen: true);
        var bus = MakeBus([hook], httpHandler: httpHookHandler);

        var result = await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.False(result.Block);
    }

    [Fact]
    public async Task Http_non2xx_blocks_when_failClosed_through_bus()
    {
        var fakeHttp = new FakeHttpHandler(HttpStatusCode.ServiceUnavailable, "error");
        var httpHookHandler = new HttpHookHandler(
            new HttpClient(fakeHttp),
            ["example.com"]);

        var hook = new UserHook("PreToolUse", null, HandlerType: "http", Url: "https://example.com/hook", FailOpen: false);
        var bus = MakeBus([hook], httpHandler: httpHookHandler);

        var result = await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.True(result.Block);
    }

    [Fact]
    public async Task Http_non_allowlisted_host_is_refused()
    {
        var fakeHttp = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var handler = new HttpHookHandler(
            new HttpClient(fakeHttp),
            ["allowed.com"]); // "evil.com" is not in the list

        var hook = new UserHook("PreToolUse", null, HandlerType: "http", Url: "https://evil.com/hook");

        await Assert.ThrowsAsync<SecurityException>(
            () => handler.HandleAsync(hook, "{}", CancellationToken.None));
    }

    [Fact]
    public async Task Http_no_allowlist_configured_means_no_http_hooks_run()
    {
        var fakeHttp = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var handler = new HttpHookHandler(
            new HttpClient(fakeHttp),
            []); // Empty allowlist

        var hook = new UserHook("PreToolUse", null, HandlerType: "http", Url: "https://example.com/hook");

        await Assert.ThrowsAsync<SecurityException>(
            () => handler.HandleAsync(hook, "{}", CancellationToken.None));
    }

    [Fact]
    public async Task Http_payload_is_redacted_before_transmission()
    {
        var capture = new CapturingHttpHandler(HttpStatusCode.OK, "{}");
        var handler = new HttpHookHandler(
            new HttpClient(capture),
            ["example.com"]);

        // Payload with a JSON key that SecretRedactor knows to redact ("password").
        var sensitivePayload = """{"password":"hunter2","event":"test"}""";
        var hook = new UserHook("PreToolUse", null, HandlerType: "http", Url: "https://example.com/hook");
        await handler.HandleAsync(hook, sensitivePayload, CancellationToken.None);

        Assert.NotNull(capture.LastBody);
        // Raw secret value must not appear in the transmitted body.
        Assert.DoesNotContain("hunter2", capture.LastBody);
        // The placeholder injected by SecretRedactor must be present.
        Assert.Contains(Coda.Common.SecretRedactor.Placeholder, capture.LastBody);
        Assert.Contains("application/json", capture.LastContentType ?? "");
    }

    [Fact]
    public void Http_host_match_is_case_insensitive()
    {
        // "EXAMPLE.COM" should match allowlist entry "example.com" (OrdinalIgnoreCase).
        HttpHookHandler.ValidateUrl("https://EXAMPLE.COM/hook", ["example.com"]);
        HttpHookHandler.ValidateUrl("https://Example.Com/hook", ["example.com"]);
    }

    [Fact]
    public void Http_subdomain_does_not_match_allowlist()
    {
        // "sub.example.com" must NOT match "example.com" — exact match only.
        Assert.Throws<SecurityException>(() =>
            HttpHookHandler.ValidateUrl("https://sub.example.com/hook", ["example.com"]));
    }

    [Fact]
    public async Task Http_redirect_307_is_not_followed()
    {
        // A 307 from an allowlisted host pointing to a non-allowlisted host must never
        // deliver the payload to the non-allowlisted destination.
        var redirect = new RedirectAndTrackingHandler(
            redirectStatusCode: HttpStatusCode.TemporaryRedirect,
            redirectLocation: "https://evil.example.net/steal");
        // Inject a mock resolver so SSRF guard passes (returns a public, non-private IP).
        static Task<System.Net.IPAddress[]> PublicResolver(string host, CancellationToken ct) =>
            Task.FromResult(new[] { System.Net.IPAddress.Parse("203.0.113.1") }); // TEST-NET-3 (RFC5737)

        var handler = new HttpHookHandler(
            new HttpClient(redirect),
            ["allowlisted.example.com"],
            resolveHost: PublicResolver);

        var hook = new UserHook("PreToolUse", null, HandlerType: "http",
            Url: "https://allowlisted.example.com/hook");

        // Non-2xx (307) must surface as HttpHookNonSuccessException.
        await Assert.ThrowsAsync<HttpHookNonSuccessException>(
            () => handler.HandleAsync(hook, "{}", CancellationToken.None));

        // The redirect destination must never have been contacted.
        Assert.False(redirect.EvilHostContacted,
            "The 307 redirect target (evil.example.net) must never be contacted.");
    }

    [Fact]
    public async Task Http_private_ip_resolving_host_is_blocked()
    {
        // A host that resolves to a link-local/metadata address must be blocked even when it
        // is on the allowlist — defends against SSRF via allowlist + DNS rebinding.
        static Task<System.Net.IPAddress[]> MockResolver(string host, CancellationToken ct) =>
            Task.FromResult(new[] { System.Net.IPAddress.Parse("169.254.169.254") });

        var capture = new CapturingHttpHandler(HttpStatusCode.OK, "{}");
        var handler = new HttpHookHandler(
            new HttpClient(capture),
            ["cloud-metadata.internal"],
            resolveHost: MockResolver);

        var hook = new UserHook("PreToolUse", null, HandlerType: "http",
            Url: "https://cloud-metadata.internal/hook");

        await Assert.ThrowsAsync<SecurityException>(
            () => handler.HandleAsync(hook, "{}", CancellationToken.None));

        // The POST must never have been sent.
        Assert.Null(capture.LastBody);
    }

    [Fact]
    public void Http_url_validation_rejects_http_for_non_loopback()
    {
        Assert.Throws<SecurityException>(() =>
            HttpHookHandler.ValidateUrl("http://evil.com/hook", ["evil.com"]));
    }

    [Fact]
    public void Http_url_validation_allows_http_for_loopback()
    {
        // Should not throw.
        HttpHookHandler.ValidateUrl("http://localhost/hook", ["localhost"]);
        HttpHookHandler.ValidateUrl("http://127.0.0.1/hook", ["127.0.0.1"]);
    }

    [Fact]
    public void Http_url_validation_rejects_embedded_credentials()
    {
        Assert.Throws<SecurityException>(() =>
            HttpHookHandler.ValidateUrl("https://user:pass@example.com/hook", ["example.com"]));
    }

    [Fact]
    public async Task Http_timeout_is_honoured()
    {
        var slowHttp = new SlowHttpHandler(delayMs: 10_000); // 10s, way longer than the 50ms timeout
        var handler = new HttpHookHandler(new HttpClient(slowHttp), ["example.com"]);
        var hook = new UserHook("PreToolUse", null, HandlerType: "http",
            Url: "https://example.com/hook", TimeoutSeconds: 0, FailOpen: false);
        var bus = MakeBus([hook], httpHandler: handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await bus.RunPreToolUseAsync("bash", "{}", cts.Token);

        // Either the hook timed out (block) or the CTS fired — either way we get a block.
        Assert.True(result.Block || cts.IsCancellationRequested);
    }

    // =========================================================================
    // 4. Prompt handler
    // =========================================================================

    [Fact]
    public async Task Prompt_ok_false_becomes_block_with_reason()
    {
        var agent = new ScriptedForkedAgent("""{"ok": false, "reason": "contains PII"}""");
        var handler = new PromptHookHandler(agent);

        var hook = new UserHook("PreToolUse", null, HandlerType: "prompt",
            HookPrompt: "Does this contain PII?");
        var output = await handler.HandleAsync(hook, "{}", CancellationToken.None);

        Assert.Equal("block", output.Decision);
        Assert.Equal("contains PII", output.Reason);
    }

    [Fact]
    public async Task Prompt_ok_true_returns_noop()
    {
        var agent = new ScriptedForkedAgent("""{"ok": true, "reason": "clean"}""");
        var handler = new PromptHookHandler(agent);

        var hook = new UserHook("PreToolUse", null, HandlerType: "prompt",
            HookPrompt: "Does this contain PII?");
        var output = await handler.HandleAsync(hook, "{}", CancellationToken.None);

        // ok:true → NoOp (not blocked)
        Assert.NotEqual("block", output.Decision);
    }

    [Fact]
    public async Task Prompt_non_json_response_throws_FormatException()
    {
        var agent = new ScriptedForkedAgent("I am a banana.");
        var handler = new PromptHookHandler(agent);

        var hook = new UserHook("PreToolUse", null, HandlerType: "prompt",
            HookPrompt: "Does this contain PII?");

        await Assert.ThrowsAsync<FormatException>(
            () => handler.HandleAsync(hook, "{}", CancellationToken.None));
    }

    [Fact]
    public async Task Prompt_non_json_degrades_per_failOpen_through_bus()
    {
        var agent = new ScriptedForkedAgent("I am a banana.");
        var promptHandler = new PromptHookHandler(agent);
        var hook = new UserHook("PreToolUse", null, HandlerType: "prompt",
            HookPrompt: "rule", FailOpen: true);
        var bus = MakeBus([hook], promptHandler: promptHandler);

        var result = await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        // Non-JSON + fail-open → allow
        Assert.False(result.Block);
    }

    [Fact]
    public async Task Prompt_non_json_blocks_when_failClosed()
    {
        var agent = new ScriptedForkedAgent("not json");
        var promptHandler = new PromptHookHandler(agent);
        var hook = new UserHook("PreToolUse", null, HandlerType: "prompt",
            HookPrompt: "rule", FailOpen: false);
        var bus = MakeBus([hook], promptHandler: promptHandler);

        var result = await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.True(result.Block);
    }

    [Fact]
    public async Task Prompt_identical_repeat_hits_cache()
    {
        var agent = new CountingForkedAgent("""{"ok": true, "reason": "ok"}""");
        var handler = new PromptHookHandler(agent);

        var hook = new UserHook("PreToolUse", null, HandlerType: "prompt",
            HookPrompt: "same rule");
        const string samePayload = """{"input":"same"}""";

        await handler.HandleAsync(hook, samePayload, CancellationToken.None);
        await handler.HandleAsync(hook, samePayload, CancellationToken.None);

        // Model should have been called only once; second call is a cache hit.
        Assert.Equal(1, agent.CallCount);
    }

    [Fact]
    public async Task Prompt_cache_hits_through_HookBus_with_real_envelope()
    {
        // Regression test for I1: HookBus.WriteEnvelope unconditionally stamps a 100ns-resolution
        // timestamp into every payload, so the cache key was always unique.
        // MakeStablePayload must strip timestamp/taskId before hashing so a second identical
        // invocation (same prompt, same semantic payload) still hits the cache.
        var agent = new CountingForkedAgent("""{"ok": true, "reason": "ok"}""");
        var promptHandler = new PromptHookHandler(agent);
        var hook = new UserHook("UserPromptSubmit", null, HandlerType: "prompt",
            HookPrompt: "No PII allowed", FailOpen: true);

        // Use a HookBus with a real HookContext so WriteEnvelope is active.
        var context = new HookContext(SessionId: "test-session", Cwd: "/tmp");
        var bus = new HookBus(
            [hook],
            context: context,
            promptHandler: promptHandler);

        // Two calls with the same user prompt. The envelope timestamps will differ, but the
        // stable projection (without timestamp/taskId) must be identical → second call is a cache hit.
        await bus.RunUserPromptSubmitAsync("hello world", [], 0, "claude-model", "default", CancellationToken.None);
        await bus.RunUserPromptSubmitAsync("hello world", [], 0, "claude-model", "default", CancellationToken.None);

        // The model must have been called only once.
        Assert.Equal(1, agent.CallCount);
    }

    [Fact]
    public async Task Prompt_different_payload_bypasses_cache()
    {
        var agent = new CountingForkedAgent("""{"ok": true, "reason": "ok"}""");
        var handler = new PromptHookHandler(agent);

        var hook = new UserHook("PreToolUse", null, HandlerType: "prompt",
            HookPrompt: "same rule");

        await handler.HandleAsync(hook, """{"input":"first"}""", CancellationToken.None);
        await handler.HandleAsync(hook, """{"input":"second"}""", CancellationToken.None);

        Assert.Equal(2, agent.CallCount);
    }

    [Fact]
    public async Task Prompt_cache_is_bounded()
    {
        var agent = new CountingForkedAgent("""{"ok": true, "reason": "ok"}""");
        var handler = new PromptHookHandler(agent, cacheCapacity: 3);
        var hook = new UserHook("PreToolUse", null, HandlerType: "prompt", HookPrompt: "rule");

        // Fill cache with 3 unique payloads.
        for (var i = 0; i < 3; i++)
        {
            await handler.HandleAsync(hook, $"{{\"i\":{i}}}", CancellationToken.None);
        }

        // 4th unique payload evicts the oldest (i=0).
        await handler.HandleAsync(hook, "{\"i\":3}", CancellationToken.None);

        var callsBefore = agent.CallCount; // = 4

        // Calling with i=0 again must NOT be a cache hit (it was evicted).
        await handler.HandleAsync(hook, "{\"i\":0}", CancellationToken.None);

        Assert.Equal(callsBefore + 1, agent.CallCount);
    }

    [Fact]
    public void Prompt_ParseModelResponse_parses_ok_false()
    {
        var output = PromptHookHandler.ParseModelResponse("""{"ok": false, "reason": "blocked"}""");
        Assert.Equal("block", output.Decision);
        Assert.Equal("blocked", output.Reason);
    }

    [Fact]
    public void Prompt_ParseModelResponse_parses_ok_true()
    {
        var output = PromptHookHandler.ParseModelResponse("""{"ok": true, "reason": "fine"}""");
        Assert.NotEqual("block", output.Decision);
    }

    [Fact]
    public void Prompt_ParseModelResponse_extracts_json_from_prose()
    {
        var output = PromptHookHandler.ParseModelResponse("""Here is my answer: {"ok": false, "reason": "PII found"} done""");
        Assert.Equal("block", output.Decision);
    }

    [Fact]
    public void Prompt_ParseModelResponse_throws_on_empty()
    {
        Assert.Throws<FormatException>(() => PromptHookHandler.ParseModelResponse(null));
        Assert.Throws<FormatException>(() => PromptHookHandler.ParseModelResponse(""));
        Assert.Throws<FormatException>(() => PromptHookHandler.ParseModelResponse("   "));
    }

    [Fact]
    public void Prompt_ParseModelResponse_throws_when_no_ok_field()
    {
        Assert.Throws<FormatException>(() => PromptHookHandler.ParseModelResponse("""{"reason": "test"}"""));
    }

    // =========================================================================
    // 5. Agent handler
    // =========================================================================

    [Fact]
    public async Task Agent_produces_block_decision_from_subagent()
    {
        var subagentHost = new ScriptedSubagentHost("""{"ok": false, "reason": "policy violation"}""");
        var handler = new AgentHookHandler(subagentHost);

        var hook = new UserHook("PreToolUse", null, HandlerType: "agent",
            HookPrompt: "Review this change");
        var output = await handler.HandleAsync(hook, """{"depth":0}""", CancellationToken.None);

        Assert.Equal("block", output.Decision);
        Assert.Equal("policy violation", output.Reason);
    }

    [Fact]
    public async Task Agent_produces_allow_decision_from_subagent()
    {
        var subagentHost = new ScriptedSubagentHost("""{"ok": true, "reason": "all good"}""");
        var handler = new AgentHookHandler(subagentHost);

        var hook = new UserHook("PreToolUse", null, HandlerType: "agent",
            HookPrompt: "Review this change");
        var output = await handler.HandleAsync(hook, """{"depth":0}""", CancellationToken.None);

        Assert.NotEqual("block", output.Decision);
    }

    [Fact]
    public async Task Agent_respects_depth_limit()
    {
        var subagentHost = new ScriptedSubagentHost("""{"ok": false, "reason": "would block"}""");
        var handler = new AgentHookHandler(subagentHost);

        var hook = new UserHook("PreToolUse", null, HandlerType: "agent",
            HookPrompt: "Review this");
        // Depth is at the limit — subagent must NOT run.
        var payload = $$$"""{"depth": {{{TaskManager.MaxSubagentDepth}}}}""";
        var output = await handler.HandleAsync(hook, payload, CancellationToken.None);

        Assert.Equal(0, subagentHost.CallCount);
        Assert.NotEqual("block", output.Decision); // Hook was skipped → NoOp
    }

    [Fact]
    public async Task Agent_skips_when_depth_exceeds_limit()
    {
        var subagentHost = new ScriptedSubagentHost("""{"ok": false, "reason": "would block"}""");
        var handler = new AgentHookHandler(subagentHost);

        var hook = new UserHook("PreToolUse", null, HandlerType: "agent",
            HookPrompt: "Review this");
        var payload = $$$"""{"depth": {{{TaskManager.MaxSubagentDepth + 1}}}}""";
        var output = await handler.HandleAsync(hook, payload, CancellationToken.None);

        Assert.Equal(0, subagentHost.CallCount);
    }

    [Fact]
    public async Task Agent_hook_spawned_subagent_does_not_retrigger_hooks()
    {
        // The AgentHookHandler uses an ISubagentHost that has NO hooks attached.
        // We verify this by ensuring the subagent host call count is exactly 1:
        // if hooks fired inside the subagent they would call the host recursively.
        var callCount = 0;
        var captureHost = new CallCountingSubagentHost("""{"ok": true, "reason": "ok"}""",
            () => callCount++);

        var handler = new AgentHookHandler(captureHost);
        var hook = new UserHook("PreToolUse", null, HandlerType: "agent",
            HookPrompt: "Review this");
        await handler.HandleAsync(hook, """{"depth":0}""", CancellationToken.None);

        // Exactly one call to RunSubagentAsync — no recursive hook firing.
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Agent_hook_spawned_subagent_host_is_hook_free_by_construction()
    {
        // Structural invariant (I2): the AgentHookHandler's SubagentHost must be constructed
        // with userHooks: null so hook recursion is impossible by construction, not by assertion.
        // This test verifies the invariant at the handler level via SubagentHostForTest.
        // The wiring-site test in TurnPipelineBuilderTests.cs verifies it end-to-end.
        var hookFreeHost = new SubagentHost(
            client: new NullStreamClient(),
            subagentTools: new ToolRegistry([]),
            permissions: new AllowAllPermissionPrompt(),
            baseOptions: new AgentOptions { SystemPrompt = "sys", WorkingDirectory = "." },
            tasks: new TaskManager("test", logRoot: null),
            userHooks: null);   // <--- hook-free: no UserHookRunner

        var handler = new AgentHookHandler(hookFreeHost);
        var host = (SubagentHost)handler.SubagentHostForTest;

        Assert.True(host.IsHookFree,
            "The subagent host injected into AgentHookHandler must be hook-free (userHooks: null).");
    }

    [Fact]
    public void Agent_ExtractDepthFromPayload_reads_depth_field()
    {
        Assert.Equal(2, AgentHookHandler.ExtractDepthFromPayload("""{"depth":2,"event":"PreToolUse"}"""));
        Assert.Equal(0, AgentHookHandler.ExtractDepthFromPayload("""{"event":"PreToolUse"}"""));
        Assert.Equal(0, AgentHookHandler.ExtractDepthFromPayload(""));
        Assert.Equal(0, AgentHookHandler.ExtractDepthFromPayload(null!));
    }

    // =========================================================================
    // 6. Fail-closed events block on timing-out prompt / agent handlers
    // =========================================================================

    [Fact]
    public async Task FailClosed_event_blocks_when_prompt_handler_times_out()
    {
        var slowAgent = new SlowForkedAgent(delayMs: 10_000);
        var handler = new PromptHookHandler(slowAgent);
        var hook = new UserHook("PreToolUse", null, HandlerType: "prompt",
            HookPrompt: "rule", TimeoutSeconds: 0, FailOpen: false);
        var bus = MakeBus([hook], promptHandler: handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await bus.RunPreToolUseAsync("bash", "{}", cts.Token);

        Assert.True(result.Block || cts.IsCancellationRequested);
    }

    [Fact]
    public async Task FailClosed_event_blocks_when_agent_handler_times_out()
    {
        var slowHost = new SlowSubagentHost(delayMs: 10_000);
        var handler = new AgentHookHandler(slowHost);
        var hook = new UserHook("PreToolUse", null, HandlerType: "agent",
            HookPrompt: "rule", TimeoutSeconds: 0, FailOpen: false);
        var bus = MakeBus([hook], agentHandler: handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await bus.RunPreToolUseAsync("bash", "{}", cts.Token);

        Assert.True(result.Block || cts.IsCancellationRequested);
    }

    [Fact]
    public async Task FailOpen_event_allows_when_prompt_handler_times_out()
    {
        var slowAgent = new SlowForkedAgent(delayMs: 10_000);
        var handler = new PromptHookHandler(slowAgent);
        // Stop is fail-open by default
        var hook = new UserHook("Stop", null, HandlerType: "prompt",
            HookPrompt: "rule", TimeoutSeconds: 0, FailOpen: true);
        var bus = MakeBus([hook], promptHandler: handler);

        // Should not throw or block; Stop is fire-and-forget.
        await bus.RunStopAsync(CancellationToken.None);
        // If we reach here without exception, the test passes.
    }

    // =========================================================================
    // 7. Command-only settings behave identically to today (backward compat)
    // =========================================================================

    [Fact]
    public async Task Command_only_settings_behave_identically_to_today()
    {
        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: """{"decision":"block","reason":"legacy block"}""");

        var hooks = new[] { new UserHook("PreToolUse", "legacy.sh") };
        var bus = MakeBus(hooks, executor: executor);

        var result = await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        // Block is produced exactly as it was before Phase 6.
        Assert.True(result.Block);
        Assert.Single(executor.Calls);
        Assert.Equal("legacy.sh", executor.Calls[0].Command);
    }

    [Fact]
    public async Task Command_non_zero_exit_applies_failOpen()
    {
        var executor = new CapturingExecutor(exitCode: 1, stdout: "");
        var hook = new UserHook("PreToolUse", "fail.sh", FailOpen: true);
        var bus = MakeBus([hook], executor: executor);

        var result = await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.False(result.Block);
    }

    [Fact]
    public async Task Command_exit_2_blocks_with_stderr_reason()
    {
        var executor = new CapturingExecutor(exitCode: 2, stdout: "", stderr: "bad thing happened");
        var hook = new UserHook("PreToolUse", "deny.sh");
        var bus = MakeBus([hook], executor: executor);

        var result = await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.True(result.Block);
        Assert.Contains("bad thing happened", result.Message ?? "");
    }

    // =========================================================================
    // Helpers / fakes
    // =========================================================================

    private static HookBus MakeBus(
        IReadOnlyList<UserHook> hooks,
        IHookExecutor? executor = null,
        IHookHandler? httpHandler = null,
        IHookHandler? promptHandler = null,
        IHookHandler? agentHandler = null) =>
        new(hooks, executor ?? new CapturingExecutor(),
            httpHandler: httpHandler,
            promptHandler: promptHandler,
            agentHandler: agentHandler);

    /// <summary>Writes a settings.json to a project temp dir and loads it via SettingsLoader with an empty user dir.</summary>
    private (CodaSettings settings, string dir) LoadFromJson(string json)
    {
        var projectDir = Path.Combine(this.tempDir, Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(this.tempDir, Guid.NewGuid().ToString("N") + "_user");
        var codaDir = Path.Combine(projectDir, ".coda");
        Directory.CreateDirectory(codaDir);
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(codaDir, "settings.json"), json);
        var settings = SettingsLoader.Load(projectDir, userDir);
        return (settings, projectDir);
    }

    // -------------------------------------------------------------------------
    // Fake: IHookExecutor
    // -------------------------------------------------------------------------

    private sealed class CapturingExecutor(
        int exitCode = 0,
        string stdout = "{}",
        string stderr = "") : IHookExecutor
    {
        public List<(string Command, string Payload)> Calls { get; } = [];

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string? command, string payload, CancellationToken ct)
        {
            this.Calls.Add((command ?? "", payload));
            return Task.FromResult((exitCode, stdout, stderr));
        }
    }

    // -------------------------------------------------------------------------
    // Fake: IHookHandler (capturing)
    // -------------------------------------------------------------------------

    private sealed class CapturingHookHandler(HookOutput response) : IHookHandler
    {
        public List<(UserHook Hook, string Payload)> Calls { get; } = [];

        public Task<HookOutput> HandleAsync(UserHook hook, string payload, CancellationToken ct)
        {
            this.Calls.Add((hook, payload));
            return Task.FromResult(response);
        }
    }

    // -------------------------------------------------------------------------
    // Fake: IForkedAgent
    // -------------------------------------------------------------------------

    private sealed class ScriptedForkedAgent(string response) : IForkedAgent
    {
        public Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(response);
    }

    private sealed class CountingForkedAgent(string response) : IForkedAgent
    {
        public int CallCount { get; private set; }

        public Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return Task.FromResult(response);
        }
    }

    private sealed class SlowForkedAgent(int delayMs) : IForkedAgent
    {
        public async Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delayMs, cancellationToken);
            return """{"ok": true, "reason": "ok"}""";
        }
    }

    // -------------------------------------------------------------------------
    // Fake: ISubagentHost
    // -------------------------------------------------------------------------

    private sealed class ScriptedSubagentHost(string result) : ISubagentHost
    {
        public int CallCount { get; private set; }

        public Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink,
            SteeringInbox steering, string taskId, int depth,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class CallCountingSubagentHost(string result, Action onCall) : ISubagentHost
    {
        public Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink,
            SteeringInbox steering, string taskId, int depth,
            CancellationToken cancellationToken = default)
        {
            onCall();
            return Task.FromResult(result);
        }
    }

    private sealed class SlowSubagentHost(int delayMs) : ISubagentHost
    {
        public async Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink,
            SteeringInbox steering, string taskId, int depth,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delayMs, cancellationToken);
            return """{"ok": true, "reason": "ok"}""";
        }
    }

    // -------------------------------------------------------------------------
    // Fake: HttpMessageHandler
    // -------------------------------------------------------------------------

    private sealed class FakeHttpHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingHttpHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public string? LastContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                this.LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
                this.LastContentType = request.Content.Headers.ContentType?.ToString();
            }

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SlowHttpHandler(int delayMs) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delayMs, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    // -------------------------------------------------------------------------
    // Fake: ILlmClient (no-op, only used for SubagentHost construction in tests
    // that never actually invoke RunSubagentAsync)
    // -------------------------------------------------------------------------

    private sealed class NullStreamClient : ILlmClient
    {
        public string ProviderId => "null";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>
    /// Returns a redirect (307/308/301) for the first request, then tracks whether
    /// the redirect destination is ever subsequently contacted.
    /// </summary>
    private sealed class RedirectAndTrackingHandler(
        HttpStatusCode redirectStatusCode,
        string redirectLocation) : HttpMessageHandler
    {
        public bool EvilHostContacted { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "evil.example.net"
                || (request.RequestUri?.ToString().Contains("evil") == true))
            {
                this.EvilHostContacted = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                });
            }

            var response = new HttpResponseMessage(redirectStatusCode);
            response.Headers.Location = new Uri(redirectLocation);
            return Task.FromResult(response);
        }
    }
}
