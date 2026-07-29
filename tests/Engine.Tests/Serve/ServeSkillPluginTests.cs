using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Agent.Settings;
using Coda.JsonRpc;
using Coda.Sdk;
using Coda.Sdk.Serve;
using Coda.Tui;
using Coda.Tui.Plugins;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Engine.Tests.Serve;

/// <summary>
/// Integration tests for the <c>skills/list</c>, <c>plugins/list</c>, and <c>skills/trust</c>
/// JSON-RPC methods over in-memory duplex streams (Phase 8 — serve parity).
/// </summary>
public sealed class ServeSkillPluginTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private readonly string workDir = Directory.CreateTempSubdirectory("serve_skill_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.workDir, recursive: true); } catch { /* best-effort */ }
    }

    private sealed class NullHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
            });
    }

    private static CredentialManager SignedInClaude()
    {
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(
            store,
            [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        creds.StoreAsync(ClaudeAiProvider.Id, new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
        }).GetAwaiter().GetResult();
        return creds;
    }

    private Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> MakeFactory()
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
            };
            return new CodaSession(SignedInClaude(), options, httpClient: new HttpClient(new NullHandler()));
        };
    }

    private async Task RunWithHostAsync(Func<JsonRpcConnection, Task> test)
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory();

        await using var host = new ServeHost(
            pair.ServerReads,
            pair.ServerWrites,
            factory,
            skillsProvider: ServeSkillPluginProviders.BuildSkillsProvider(),
            pluginsProvider: ServeSkillPluginProviders.BuildPluginsProvider());
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        await test(orchestrator);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    private void WriteProjectSkill(string name, string description)
    {
        var dir = Path.Combine(this.workDir, ".coda", "skills", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\nbody\n");
    }

    private void WriteForeignSkill(string name, string description)
    {
        var dir = Path.Combine(this.workDir, ".agents", "skills", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\nbody\n");
    }

    private void WriteProjectPlugin(string name, string version)
    {
        var dir = Path.Combine(this.workDir, ".coda", "plugins", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "plugin.json"),
            $"{{\n  \"name\": \"{name}\",\n  \"version\": \"{version}\",\n  \"description\": \"a plugin\"\n}}\n");
    }

    // ── skills/list ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SkillList_returns_skills_with_enabled_and_origin()
    {
        this.WriteProjectSkill("alpha", "first skill");

        await RunWithHostAsync(async orchestrator =>
        {
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.SkillList, null, CancellationToken.None)
                .WaitAsync(WaitTimeout);

            var obj = result as JsonObject;
            Assert.NotNull(obj);
            var skills = obj!["skills"] as JsonArray;
            Assert.NotNull(skills);
            var alpha = skills!.Select(s => s as JsonObject)
                .FirstOrDefault(s => s?["name"]?.GetValue<string>() == "alpha");
            Assert.NotNull(alpha);
            Assert.Equal("project", alpha!["origin"]?.GetValue<string>());
            Assert.True(alpha["enabled"]?.GetValue<bool>());
        });
    }

    [Fact]
    public async Task SkillList_includes_external_foreign_skill()
    {
        this.WriteForeignSkill("foreign-one", "from agents dir");

        await RunWithHostAsync(async orchestrator =>
        {
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.SkillList, null, CancellationToken.None)
                .WaitAsync(WaitTimeout);

            var obj = result as JsonObject;
            var skills = obj!["skills"] as JsonArray;
            var foreign = skills!.Select(s => s as JsonObject)
                .FirstOrDefault(s => s?["name"]?.GetValue<string>() == "foreign-one");
            Assert.NotNull(foreign);
            Assert.Equal("foreign", foreign!["origin"]?.GetValue<string>());
        });
    }

    // ── plugins/list ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PluginList_returns_plugins_with_enabled_and_trusted()
    {
        this.WriteProjectPlugin("alpha", "1.0.0");

        await RunWithHostAsync(async orchestrator =>
        {
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.PluginList, null, CancellationToken.None)
                .WaitAsync(WaitTimeout);

            var obj = result as JsonObject;
            Assert.NotNull(obj);
            var plugins = obj!["plugins"] as JsonArray;
            Assert.NotNull(plugins);
            var alpha = plugins!.Select(p => p as JsonObject)
                .FirstOrDefault(p => p?["name"]?.GetValue<string>() == "alpha");
            Assert.NotNull(alpha);
            Assert.Equal("1.0.0", alpha!["version"]?.GetValue<string>());
            Assert.NotNull(alpha["enabled"]);
            Assert.NotNull(alpha["trusted"]);
        });
    }

    // ── skills/trust ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SkillTrust_is_refused_in_serve_mode()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            await Assert.ThrowsAsync<JsonRpcResponseException>(async () =>
            {
                await orchestrator
                    .SendRequestAsync(ServeMethods.SkillTrust, new JsonObject { ["skillName"] = "x" }, CancellationToken.None)
                    .WaitAsync(WaitTimeout);
            });
        });
    }

    // ── L1 — skills/list must return workspace-relative paths ─────────────────

    /// <summary>
    /// L1: sourcePath for a skill inside the workspace must be returned as a relative path, not
    /// an absolute one that exposes the user's home-directory layout.
    /// Before the fix: sourcePath is the absolute path to SKILL.md.
    /// After the fix:  sourcePath is relative to the workspace root.
    /// </summary>
    [Fact]
    public async Task SkillList_sourcePath_is_workspace_relative_for_project_skill()
    {
        this.WriteProjectSkill("beta", "second skill");

        await RunWithHostAsync(async orchestrator =>
        {
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.SkillList, null, CancellationToken.None)
                .WaitAsync(WaitTimeout);

            var obj = result as JsonObject;
            var skills = obj!["skills"] as JsonArray;
            var beta = skills!.Select(s => s as JsonObject)
                .FirstOrDefault(s => s?["name"]?.GetValue<string>() == "beta");
            Assert.NotNull(beta);

            var sourcePath = beta!["sourcePath"]?.GetValue<string>();
            Assert.NotNull(sourcePath);
            Assert.False(Path.IsPathRooted(sourcePath),
                $"Expected a relative sourcePath but got absolute: {sourcePath}");
            Assert.Contains("beta", sourcePath, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ── L2 — plugins/list trusted flag must use the full-surface hash ──────────

    /// <summary>
    /// L2: Confirm that PluginContentHash.Compute(name, version) differs from
    /// PluginContentHash.Compute(PluginInfo) for a plugin with a manifest that has hooks,
    /// and that after the fix the serve provider uses the full hash so an approved plugin
    /// reports trusted:true.
    /// </summary>
    [Fact]
    public async Task PluginList_trusted_is_true_for_plugin_approved_with_full_hash()
    {
        // Arrange: plugin with a hook so the two hash variants differ.
        var pluginDir = Path.Combine(this.workDir, ".coda", "plugins", "trustable");
        Directory.CreateDirectory(pluginDir);
        var hookFile = Path.Combine(pluginDir, "hook.json");
        File.WriteAllText(hookFile, """{"PreToolUse":[{"command":"./check.sh"}]}""");
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name":"trustable","version":"1.0.0","description":"test","hooks":["hook.json"]}""");

        var manifest = PluginManifestParser.Parse(
            File.ReadAllText(Path.Combine(pluginDir, "plugin.json")), pluginDir);
        var info = new PluginInfo("trustable", "1.0.0", "test", pluginDir) { Manifest = manifest };
        var fullHash = PluginContentHash.Compute(info);
        var shortHash = PluginContentHash.Compute("trustable", "1.0.0");

        // Pre-condition: the two hashes must differ to prove the bug exists.
        Assert.NotEqual(shortHash, fullHash);

        // Record approval with the full hash.
        var trustDir = Path.Combine(this.workDir, "_trust_l2");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);
        trustStore.SetApprovedClasses(fullHash, [PluginComponentClass.Hook]);

        // Inject the trust store path via CODA_SETTINGS_DIR so BuildPluginsProvider picks it up.
        var priorSettingsDir = Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CODA_SETTINGS_DIR", trustDir);

            await RunWithHostAsync(async orchestrator =>
            {
                var result = await orchestrator
                    .SendRequestAsync(ServeMethods.PluginList, null, CancellationToken.None)
                    .WaitAsync(WaitTimeout);

                var obj = result as JsonObject;
                var plugins = obj!["plugins"] as JsonArray;
                var trustable = plugins!.Select(p => p as JsonObject)
                    .FirstOrDefault(p => p?["name"]?.GetValue<string>() == "trustable");
                Assert.NotNull(trustable);
                Assert.True(trustable!["trusted"]?.GetValue<bool>(),
                    "Plugin approved with full hash must report trusted:true after fix");
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODA_SETTINGS_DIR", priorSettingsDir);
        }
    }
}
