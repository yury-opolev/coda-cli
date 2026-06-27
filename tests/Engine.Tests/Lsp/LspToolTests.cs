using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Agent.Lsp;
using Coda.JsonRpc;

namespace Engine.Tests.Lsp;

/// <summary>
/// Tests for LspTool using an in-memory fake LSP server (no real language server).
/// </summary>
public sealed class LspToolTests : IAsyncDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    // -------------------------------------------------------------------------
    // Canned fake server that returns configured results per method.
    // -------------------------------------------------------------------------

    private sealed class CannedFakeServerLoop : IAsyncDisposable
    {
        private readonly LspFakeTransport transport;
        private readonly CancellationTokenSource cts;
        private readonly Task loopTask;

        /// <summary>Canned results: method → result JsonNode to return.</summary>
        private readonly Dictionary<string, Func<JsonNode?, JsonNode?>> cannedResults = [];

        /// <summary>Captures the params received for specific methods (for assertion).</summary>
        public readonly Dictionary<string, JsonNode?> ReceivedParams = [];

        public CannedFakeServerLoop(LspFakeTransport transport)
        {
            this.transport = transport;
            this.cts = new CancellationTokenSource();
            this.loopTask = Task.Run(this.RunAsync);
        }

        public void SetCannedResult(string method, JsonNode? result)
        {
            this.cannedResults[method] = _ => result;
        }

        public void SetCannedResultFactory(string method, Func<JsonNode?, JsonNode?> factory)
        {
            this.cannedResults[method] = factory;
        }

        private async Task RunAsync()
        {
            try
            {
                while (!this.cts.IsCancellationRequested)
                {
                    var msg = await JsonRpcMessageCodec
                        .ReadMessageAsync(this.transport.ServerReads, this.cts.Token)
                        .ConfigureAwait(false);

                    if (msg is null)
                    {
                        return;
                    }

                    var method = msg["method"]?.GetValue<string>();
                    var hasId = msg["id"] is not null;

                    if (method == "initialize" && hasId)
                    {
                        await this.RespondAsync(msg, new JsonObject { ["capabilities"] = new JsonObject() }).ConfigureAwait(false);
                        continue;
                    }

                    if (method == "initialized" || method == "textDocument/didOpen" ||
                        method == "textDocument/didChange" || method == "textDocument/didClose")
                    {
                        continue;
                    }

                    if (method == "shutdown" && hasId)
                    {
                        await this.RespondAsync(msg, null).ConfigureAwait(false);
                        continue;
                    }

                    if (method == "exit")
                    {
                        return;
                    }

                    if (hasId && method is not null)
                    {
                        // Capture received params for assertion.
                        this.ReceivedParams[method] = msg["params"]?.DeepClone();

                        if (this.cannedResults.TryGetValue(method, out var factory))
                        {
                            var result = factory(msg["params"]);
                            await this.RespondAsync(msg, result).ConfigureAwait(false);
                        }
                        else
                        {
                            await this.RespondAsync(msg, new JsonObject { ["echo"] = method }).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation.
            }
            catch
            {
                // Swallow — fake server terminating.
            }
        }

        private Task RespondAsync(JsonNode request, JsonNode? result)
        {
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"]!.DeepClone(),
                ["result"] = result,
            };
            return JsonRpcMessageCodec.WriteMessageAsync(this.transport.ServerWrites, response, this.cts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await this.cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await this.loopTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // Bounded wait.
            }

            this.cts.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // Fixture state
    // -------------------------------------------------------------------------

    private readonly LspServerManager manager;
    private readonly CannedFakeServerLoop fakeLoop;
    private readonly Coda.Agent.Tools.LspTool tool;
    private readonly string tempFilePath;

    public LspToolTests()
    {
        this.tool = new Coda.Agent.Tools.LspTool();

        // Create a real temp file on disk so file-existence checks pass.
        this.tempFilePath = Path.Combine(Path.GetTempPath(), $"lsp-test-{Guid.NewGuid()}.ts");
        File.WriteAllText(this.tempFilePath, "// test file\nfunction foo() {}\n");

        CannedFakeServerLoop? capturedLoop = null;

        var config = new LspServerConfig(
            Command: "fake-lsp",
            Args: [],
            ExtensionToLanguage: new Dictionary<string, string> { [".ts"] = "typescript" },
            Env: null,
            InitializationOptions: null,
            StartupTimeoutMs: 5000);
        var configs = new Dictionary<string, LspServerConfig> { ["ts-server"] = config };

        LspServerInstance Factory(string name, LspServerConfig cfg)
        {
            var pair = new DuplexStreamPair();
            var transport = new LspFakeTransport(pair);
            capturedLoop = new CannedFakeServerLoop(transport);
            var client = new LspClient(name, _ => Task.FromResult<ILspTransport>(transport));
            return new LspServerInstance(name, cfg, client);
        }

        this.manager = new LspServerManager(configs, Factory);
        this.manager.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        this.fakeLoop = capturedLoop!;
    }

    public async ValueTask DisposeAsync()
    {
        await this.manager.DisposeAsync().ConfigureAwait(false);
        await this.fakeLoop.DisposeAsync().ConfigureAwait(false);
        if (File.Exists(this.tempFilePath))
        {
            File.Delete(this.tempFilePath);
        }
    }

    private ToolContext MakeContext(bool withLsp = true)
    {
        return new ToolContext(Path.GetTempPath())
        {
            Lsp = withLsp ? this.manager : null,
        };
    }

    private static JsonElement ParseInput(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Disabled_message_when_Lsp_null()
    {
        var context = MakeContext(withLsp: false);
        var input = ParseInput($$"""{"operation":"goToDefinition","filePath":"{{this.tempFilePath.Replace("\\", "\\\\")}}","line":1,"character":1}""");

        var result = await this.tool.ExecuteAsync(input, context).WaitAsync(TestTimeout);

        Assert.True(result.IsError);
        Assert.Contains("lspServers", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validation_fails_for_missing_file()
    {
        var context = MakeContext();
        var nonExistent = Path.Combine(Path.GetTempPath(), "definitely-does-not-exist-xyz.ts");
        var input = ParseInput($$"""{"operation":"goToDefinition","filePath":"{{nonExistent.Replace("\\", "\\\\")}}","line":1,"character":1}""");

        var result = await this.tool.ExecuteAsync(input, context).WaitAsync(TestTimeout);

        Assert.True(result.IsError);
        Assert.Contains("does not exist", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validation_fails_for_bad_operation()
    {
        var context = MakeContext();
        var input = ParseInput($$"""{"operation":"notARealOp","filePath":"{{this.tempFilePath.Replace("\\", "\\\\")}}","line":1,"character":1}""");

        var result = await this.tool.ExecuteAsync(input, context).WaitAsync(TestTimeout);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Validation_fails_for_line_less_than_one()
    {
        var context = MakeContext();
        var input = ParseInput($$"""{"operation":"goToDefinition","filePath":"{{this.tempFilePath.Replace("\\", "\\\\")}}","line":0,"character":1}""");

        var result = await this.tool.ExecuteAsync(input, context).WaitAsync(TestTimeout);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GoToDefinition_formats_location_list()
    {
        // Canned: [{ uri: "file:///x.ts", range: {start:{line:9,character:4}, end:{line:9,character:7}} }]
        this.fakeLoop.SetCannedResult("textDocument/definition", new JsonArray
        {
            new JsonObject
            {
                ["uri"] = "file:///x.ts",
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 9, ["character"] = 4 },
                    ["end"] = new JsonObject { ["line"] = 9, ["character"] = 7 },
                }
            }
        });

        var context = MakeContext();
        var input = ParseInput($$"""{"operation":"goToDefinition","filePath":"{{this.tempFilePath.Replace("\\", "\\\\")}}","line":1,"character":1}""");

        var result = await this.tool.ExecuteAsync(input, context).WaitAsync(TestTimeout);

        Assert.False(result.IsError);
        // 0-based line 9 → display line 10, character 4 → display 5
        Assert.Contains("x.ts", result.Content);
        Assert.Contains("10", result.Content);
        Assert.Contains("5", result.Content);
    }

    [Fact]
    public async Task FindReferences_counts_results()
    {
        this.fakeLoop.SetCannedResult("textDocument/references", new JsonArray
        {
            new JsonObject
            {
                ["uri"] = "file:///a.ts",
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                    ["end"] = new JsonObject { ["line"] = 0, ["character"] = 3 },
                }
            },
            new JsonObject
            {
                ["uri"] = "file:///b.ts",
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 5, ["character"] = 2 },
                    ["end"] = new JsonObject { ["line"] = 5, ["character"] = 5 },
                }
            }
        });

        var context = MakeContext();
        var input = ParseInput($$"""{"operation":"findReferences","filePath":"{{this.tempFilePath.Replace("\\", "\\\\")}}","line":1,"character":1}""");

        var result = await this.tool.ExecuteAsync(input, context).WaitAsync(TestTimeout);

        Assert.False(result.IsError);
        Assert.Contains("2", result.Content);
    }

    [Fact]
    public async Task Hover_renders_contents()
    {
        this.fakeLoop.SetCannedResult("textDocument/hover", new JsonObject
        {
            ["contents"] = new JsonObject
            {
                ["kind"] = "markdown",
                ["value"] = "**Foo** is a function"
            }
        });

        var context = MakeContext();
        var input = ParseInput($$"""{"operation":"hover","filePath":"{{this.tempFilePath.Replace("\\", "\\\\")}}","line":2,"character":10}""");

        var result = await this.tool.ExecuteAsync(input, context).WaitAsync(TestTimeout);

        Assert.False(result.IsError);
        Assert.Contains("Foo", result.Content);
    }

    [Fact]
    public async Task DocumentSymbol_renders_symbols()
    {
        this.fakeLoop.SetCannedResult("textDocument/documentSymbol", new JsonArray
        {
            new JsonObject
            {
                ["name"] = "MyClass",
                ["kind"] = 5, // Class
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                    ["end"] = new JsonObject { ["line"] = 20, ["character"] = 1 },
                },
                ["selectionRange"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 0, ["character"] = 6 },
                    ["end"] = new JsonObject { ["line"] = 0, ["character"] = 13 },
                },
                ["children"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "myMethod",
                        ["kind"] = 6, // Method
                        ["range"] = new JsonObject
                        {
                            ["start"] = new JsonObject { ["line"] = 2, ["character"] = 2 },
                            ["end"] = new JsonObject { ["line"] = 5, ["character"] = 3 },
                        },
                        ["selectionRange"] = new JsonObject
                        {
                            ["start"] = new JsonObject { ["line"] = 2, ["character"] = 2 },
                            ["end"] = new JsonObject { ["line"] = 2, ["character"] = 10 },
                        },
                    }
                }
            }
        });

        var context = MakeContext();
        var input = ParseInput($$"""{"operation":"documentSymbol","filePath":"{{this.tempFilePath.Replace("\\", "\\\\")}}","line":1,"character":1}""");

        var result = await this.tool.ExecuteAsync(input, context).WaitAsync(TestTimeout);

        Assert.False(result.IsError);
        Assert.Contains("MyClass", result.Content);
        Assert.Contains("myMethod", result.Content);
    }

    [Fact]
    public async Task Converts_1based_to_0based_in_wire_params()
    {
        // Capture the received params to verify 1→0 conversion.
        this.fakeLoop.SetCannedResult("textDocument/definition", new JsonArray());

        var context = MakeContext();
        // Line=3, character=7 (1-based) → wire should be line=2, character=6 (0-based).
        var input = ParseInput($$"""{"operation":"goToDefinition","filePath":"{{this.tempFilePath.Replace("\\", "\\\\")}}","line":3,"character":7}""");

        await this.tool.ExecuteAsync(input, context).WaitAsync(TestTimeout);

        Assert.True(this.fakeLoop.ReceivedParams.ContainsKey("textDocument/definition"));
        var @params = this.fakeLoop.ReceivedParams["textDocument/definition"]!;
        var positionNode = @params["position"]!;
        Assert.Equal(2, positionNode["line"]!.GetValue<int>());
        Assert.Equal(6, positionNode["character"]!.GetValue<int>());
    }

    [Fact]
    public async Task CallHierarchy_incoming_two_step()
    {
        // Step 1: prepareCallHierarchy returns an item.
        this.fakeLoop.SetCannedResult("textDocument/prepareCallHierarchy", new JsonArray
        {
            new JsonObject
            {
                ["name"] = "myFunc",
                ["kind"] = 12, // Function
                ["uri"] = "file:///my.ts",
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 10, ["character"] = 0 },
                    ["end"] = new JsonObject { ["line"] = 15, ["character"] = 1 },
                },
                ["selectionRange"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 10, ["character"] = 9 },
                    ["end"] = new JsonObject { ["line"] = 10, ["character"] = 15 },
                }
            }
        });

        // Step 2: incomingCalls returns a caller.
        this.fakeLoop.SetCannedResult("callHierarchy/incomingCalls", new JsonArray
        {
            new JsonObject
            {
                ["from"] = new JsonObject
                {
                    ["name"] = "callerFunc",
                    ["kind"] = 12,
                    ["uri"] = "file:///caller.ts",
                    ["range"] = new JsonObject
                    {
                        ["start"] = new JsonObject { ["line"] = 3, ["character"] = 0 },
                        ["end"] = new JsonObject { ["line"] = 8, ["character"] = 1 },
                    },
                    ["selectionRange"] = new JsonObject
                    {
                        ["start"] = new JsonObject { ["line"] = 3, ["character"] = 9 },
                        ["end"] = new JsonObject { ["line"] = 3, ["character"] = 19 },
                    }
                },
                ["fromRanges"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["start"] = new JsonObject { ["line"] = 5, ["character"] = 4 },
                        ["end"] = new JsonObject { ["line"] = 5, ["character"] = 10 },
                    }
                }
            }
        });

        var context = MakeContext();
        var input = ParseInput($$"""{"operation":"incomingCalls","filePath":"{{this.tempFilePath.Replace("\\", "\\\\")}}","line":11,"character":10}""");

        var result = await this.tool.ExecuteAsync(input, context).WaitAsync(TestTimeout);

        Assert.False(result.IsError);
        Assert.Contains("callerFunc", result.Content);
    }
}
