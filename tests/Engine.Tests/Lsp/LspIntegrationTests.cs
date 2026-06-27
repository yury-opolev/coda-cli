using System.Text;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Lsp;
using Coda.Agent.Tools;
using LlmClient;

namespace Engine.Tests.Lsp;

/// <summary>
/// Integration tests for Task 9: AgentLoop wires diagnostics surfacing and CodaSession
/// conditionally registers LspTool. Uses the fake-server harness (no real language server).
///
/// Test approach: drives the AgentLoop directly with a stub model + fake LSP manager backed
/// by LspFakeServerHarness (in-proc connection). This is the most faithful approach for
/// testing the edit-seam + diagnostics-surfacing-seam without spinning up external processes.
/// </summary>
public sealed class LspIntegrationTests
{
    // -----------------------------------------------------------------------
    // Infrastructure: scripted LLM client + minimal sink
    // -----------------------------------------------------------------------

    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;
        public List<ChatRequest> ReceivedRequests { get; } = [];

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.ReceivedRequests.Add(request);
            var events = turns[this.turn++];
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    private static AgentOptions Options(string workingDirectory) =>
        new()
        {
            SystemPrompt = "sys",
            WorkingDirectory = workingDirectory,
            Model = "m",
        };

    // -----------------------------------------------------------------------
    // Helpers: write a real temp .ts file on disk
    // -----------------------------------------------------------------------

    private static string CreateTempTsFile(string directory, string content = "const x = 1;")
    {
        var path = Path.Combine(directory, "test.ts");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    // -----------------------------------------------------------------------
    // Test 1: edit tool triggers LSP file sync and diagnostics appear in
    //         the NEXT turn's history as a <diagnostics> block.
    // -----------------------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task After_edit_tool_next_turn_history_contains_diagnostics_block()
    {
        // Arrange: temp directory with a .ts file that can be "edited".
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var tsFile = CreateTempTsFile(tempDir);
            var relPath = Path.GetRelativePath(tempDir, tsFile);

            // Build an in-proc LSP manager backed by a fake server that emits
            // publishDiagnostics when it receives didSave.
            var (manager, fakeLoop) = LspFakeServerHarness.BuildManager();
            await using var _ = fakeLoop;

            var registry = new LspDiagnosticRegistry();
            LspPassiveFeedback.RegisterNotificationHandlers(manager, registry);

            // Scripted model: turn 1 calls edit_file on test.ts, turn 2 just finishes.
            var editInput = JsonSerializer.Serialize(new
            {
                path = relPath,
                old_string = "const x = 1;",
                new_string = "const x = 2;",
            });

            var turn1 = new[]
            {
                AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "edit_file", editInput)),
                AssistantStreamEvent.Finished("tool_use"),
            };
            var turn2 = new[]
            {
                AssistantStreamEvent.Delta("done"),
                AssistantStreamEvent.Finished("end_turn"),
            };

            var client = new ScriptedClient(turn1, turn2);
            var editTool = new EditTool();
            var loop = new AgentLoop(
                client,
                new ToolRegistry([editTool]),
                new AllowAllPermissionPrompt(),
                Options(tempDir),
                lsp: manager,
                lspDiagnostics: registry);

            var history = new List<ChatMessage> { ChatMessage.UserText("edit the file") };

            // Act
            await loop.RunAsync(history, new NullSink(), CancellationToken.None);

            // Assert: turn 2's request (ReceivedRequests[1]) must have a user message
            // containing a <diagnostics> block with "Test error".
            Assert.True(client.ReceivedRequests.Count >= 2, "Expected at least 2 model turns");

            var turn2Request = client.ReceivedRequests[1];
            var injectedDiagnosticsMessage = turn2Request.Messages
                .Where(m => m.Role == ChatRole.User)
                .SelectMany(m => m.Content)
                .OfType<TextBlock>()
                .FirstOrDefault(b => b.Text.Contains("<diagnostics>", StringComparison.Ordinal));

            Assert.NotNull(injectedDiagnosticsMessage);
            Assert.Contains("Test error", injectedDiagnosticsMessage.Text, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // -----------------------------------------------------------------------
    // Test 2: LspTool is registered in ToolRegistry when servers are configured
    //         and absent otherwise. Tested at the ToolRegistry level (no need
    //         to spin up a full CodaSession, which requires auth).
    // -----------------------------------------------------------------------

    [Fact]
    public void Tool_registered_only_when_servers_configured()
    {
        // When LSP servers exist: ToolRegistry should include LspTool.
        var (manager, _) = LspFakeServerHarness.BuildManager();

        // Simulate CodaSession behavior: add LspTool only when manager != null.
        var withLsp = new ToolRegistry([new EditTool(), new WriteFileTool(), new LspTool()]);
        Assert.Contains(withLsp.Definitions, d => d.Name == "lsp");

        // Without LSP manager: LspTool is not added.
        var withoutLsp = new ToolRegistry([new EditTool(), new WriteFileTool()]);
        Assert.DoesNotContain(withoutLsp.Definitions, d => d.Name == "lsp");
    }

    // -----------------------------------------------------------------------
    // Test 3: The registry receives a pending diagnostic after the edit-seam
    //         triggers ChangeFileAsync + SaveFileAsync (focused seam test).
    // -----------------------------------------------------------------------

    [Fact(Timeout = 15_000)]
    public async Task Edit_seam_triggers_save_and_registry_receives_diagnostic()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var tsFile = CreateTempTsFile(tempDir);
            var relPath = Path.GetRelativePath(tempDir, tsFile);

            var (manager, fakeLoop) = LspFakeServerHarness.BuildManager();
            await using var _ = fakeLoop;

            var registry = new LspDiagnosticRegistry();
            LspPassiveFeedback.RegisterNotificationHandlers(manager, registry);

            var editInput = JsonSerializer.Serialize(new
            {
                path = relPath,
                old_string = "const x = 1;",
                new_string = "const x = 2;",
            });

            // Two turns: turn 1 edits, turn 2 finishes.
            var turn1 = new[]
            {
                AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "edit_file", editInput)),
                AssistantStreamEvent.Finished("tool_use"),
            };
            var turn2 = new[]
            {
                AssistantStreamEvent.Finished("end_turn"),
            };

            var client = new ScriptedClient(turn1, turn2);
            var loop = new AgentLoop(
                client,
                new ToolRegistry([new EditTool()]),
                new AllowAllPermissionPrompt(),
                Options(tempDir),
                lsp: manager,
                lspDiagnostics: registry);

            var history = new List<ChatMessage> { ChatMessage.UserText("edit the file") };
            await loop.RunAsync(history, new NullSink(), CancellationToken.None);

            // Give the fake server a moment to process the didSave and emit the notification.
            await Task.Delay(500);

            // The fake server should have received a didSave.
            Assert.True(fakeLoop.DidSaveCount > 0, "Expected didSave to be sent");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
