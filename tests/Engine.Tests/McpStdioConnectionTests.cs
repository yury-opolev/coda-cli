using System.Diagnostics;
using System.Text;
using Coda.Common;
using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// Exercises <see cref="McpStdioClient"/> over a real child process and stdio pipes using the
/// <c>McpStdioTestServer</c> helper. Covers startup failures (process exit, transport loss while
/// the child stays alive), the happy path, and UTF-8/BOM guarantees on both directions of the
/// pipe. These are deliberately end-to-end: the unit-level RPC harness cannot observe process
/// lifetime, stderr draining, or stdin byte encoding.
/// </summary>
public sealed class McpStdioConnectionTests
{
    [Fact]
    public async Task Initialize_phase_process_exit_reports_exit_code_and_redacted_stderr()
    {
        await using var client = new McpStdioClient("github", ServerConfig("exit-on-initialize"));

        var ex = await Assert.ThrowsAsync<McpConnectionException>(
            () => client.InitializeAndListToolsAsync());

        Assert.Equal("initialize", ex.Phase);
        Assert.Contains("exit code 3", ex.Message);
        Assert.Contains(SecretRedactor.Placeholder, ex.Message);
        Assert.DoesNotContain("sk-ABCDEF1234567890", ex.Message);
    }

    [Fact]
    public async Task Stdout_closing_before_process_exit_is_reported_as_process_exit()
    {
        await using var client = new McpStdioClient("github", ServerConfig("close-stdout-exit"));

        var ex = await Assert.ThrowsAsync<McpConnectionException>(
            () => client.InitializeAndListToolsAsync());

        Assert.Equal("initialize", ex.Phase);
        Assert.Contains("exit code 7", ex.Message);
    }

    [Fact]
    public async Task Transport_closed_while_process_alive_preserves_connection_error()
    {
        await using var client = new McpStdioClient("github", ServerConfig("close-stdout-alive"));

        // The child closes stdout but stays alive; after the grace period the original transport
        // McpException must be preserved rather than converted to a ProcessExited failure.
        var ex = await Assert.ThrowsAsync<McpException>(
            () => client.InitializeAndListToolsAsync());

        Assert.IsType<McpException>(ex);
        Assert.DoesNotContain("exit code", ex.Message);
    }

    [Fact]
    public async Task Successful_initialize_and_tools_list_returns_tools()
    {
        await using var client = new McpStdioClient("github", ServerConfig("serve"));

        var tools = await client.InitializeAndListToolsAsync();

        Assert.Contains(tools, t => t.Name == "echo");
        Assert.NotNull(client.ServerInfo);
        Assert.Equal("1.0", client.ServerInfo!.Version);
    }

    [Fact]
    public async Task First_stdin_bytes_have_no_utf8_bom()
    {
        await using var client = new McpStdioClient("github", ServerConfig("serve"));

        await client.InitializeAndListToolsAsync();

        // The helper reports "nobom" only when the first bytes it read on stdin were not EF BB BF.
        Assert.Equal("nobom", client.ServerInfo!.Name);
    }

    [Fact]
    public async Task Helper_responses_are_bom_free()
    {
        var psi = new ProcessStartInfo
        {
            FileName = ServerCommand,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in ServerPrefixArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add("serve");

        using var process = Process.Start(psi)!;
        try
        {
            var request = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n");
            await process.StandardInput.BaseStream.WriteAsync(request);
            await process.StandardInput.BaseStream.FlushAsync();

            var buffer = new byte[8];
            var got = 0;
            while (got < 3)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(got, buffer.Length - got));
                if (read == 0)
                {
                    break;
                }

                got += read;
            }

            Assert.True(got >= 1, "helper produced no stdout");
            Assert.False(
                got >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF,
                "helper response started with a UTF-8 BOM");
            Assert.Equal((byte)'{', buffer[0]);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task Process_less_client_preserves_original_transport_exception()
    {
        // The test-only constructor owns no process, so a transport McpException must surface
        // unchanged rather than being probed as a process exit.
        var rpc = new McpRpcConnection(new StringWriter());
        var client = new ScriptedMcpStdioClient("test-server", rpc);

        var task = client.InitializeAndListToolsAsync();
        rpc.DispatchLine("""{"jsonrpc":"2.0","id":1,"error":{"code":-1,"message":"transport boom"}}""");

        var ex = await Assert.ThrowsAsync<McpException>(() => task);
        Assert.IsType<McpException>(ex);
        Assert.Equal("transport boom", ex.Message);
    }

    [Theory]
    [InlineData(20)]
    public async Task Repeated_initialize_exit_has_no_race_regression(int iterations)
    {
        for (var i = 0; i < iterations; i++)
        {
            await using var client = new McpStdioClient("github", ServerConfig("exit-on-initialize"));

            var ex = await Assert.ThrowsAsync<McpConnectionException>(
                () => client.InitializeAndListToolsAsync());

            Assert.Equal("initialize", ex.Phase);
            Assert.Contains("exit code 3", ex.Message);
        }
    }

    // --- Test server launch helpers ------------------------------------------

    private static readonly (string Command, IReadOnlyList<string> PrefixArgs) Server = ResolveServer();

    private static string ServerCommand => Server.Command;

    private static IReadOnlyList<string> ServerPrefixArgs => Server.PrefixArgs;

    private static McpStdioServerConfig ServerConfig(string mode)
    {
        var args = new List<string>(Server.PrefixArgs) { mode };
        return new McpStdioServerConfig(Server.Command, args, new Dictionary<string, string>());
    }

    private static (string Command, IReadOnlyList<string> PrefixArgs) ResolveServer()
    {
        // AppContext.BaseDirectory: .../tests/Engine.Tests/bin/<config>/<tfm>/
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = baseDir.Name;
        var config = baseDir.Parent!.Name;
        var testsDir = baseDir.Parent!.Parent!.Parent!.Parent!;
        var serverDir = Path.Combine(testsDir.FullName, "McpStdioTestServer", "bin", config, tfm);

        var exe = Path.Combine(serverDir, "McpStdioTestServer.exe");
        if (File.Exists(exe))
        {
            return (exe, []);
        }

        var dll = Path.Combine(serverDir, "McpStdioTestServer.dll");
        return ("dotnet", new[] { dll });
    }
}
