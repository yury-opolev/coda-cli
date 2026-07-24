using Coda.Mcp;
using Coda.Sdk;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Setup;
using Coda.Tui.Ui.Prompts;
using LlmAuth;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class PromptDrivenCommandTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_prompt_").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Bare_slash_uses_prompt_service_command_picker()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["help"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        await built.App.DispatchAsync(CommandParser.Parse("/"));

        Assert.Equal("Select a command", Assert.Single(prompts.Requests).Title);
        Assert.Contains("Commands", built.Console.Output);
    }

    [Fact]
    public async Task Setup_uses_prompt_service_provider_picker()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["anthropic-api-key"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        var provider = await SetupWizard.ChooseProviderAsync(built.Context);

        Assert.Equal("Choose a provider", Assert.Single(prompts.Requests).Title);
        Assert.Equal("anthropic-api-key", provider?.Id);
    }

    [Fact]
    public async Task Provider_without_id_uses_picker()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["anthropic-api-key"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        await new ProviderCommand().ExecuteAsync(built.Context, []);

        Assert.Equal("Choose a provider", Assert.Single(prompts.Requests).Title);
    }

    [Fact]
    public async Task Model_without_id_uses_cached_models_picker()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["model-b"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);
        var models = new ModelListResult(
            built.Context.ActiveProvider.Id,
            ModelSource.Catalog,
            [new ModelListEntry("model-a", "A", 200_000), new ModelListEntry("model-b", "B", 200_000)]);

        var selected = await ModelCommand.ChooseModelAsync(built.Context, models);

        Assert.Equal("model-b", selected);
        Assert.Equal("Choose a model", Assert.Single(prompts.Requests).Title);
    }

    [Fact]
    public async Task Model_picker_marks_and_defaults_to_current_model()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["model-b"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);
        built.Context.Session.Model = "MODEL-B"; // differently-cased session string
        var models = new ModelListResult(
            built.Context.ActiveProvider.Id,
            ModelSource.Catalog,
            [new ModelListEntry("model-a", "A", 200_000), new ModelListEntry("model-b", "B", 200_000)]);

        await ModelCommand.ChooseModelAsync(built.Context, models);

        var request = Assert.Single(prompts.Requests);
        Assert.Equal("model-b", request.DefaultValue); // canonical list spelling, usable as ordinal default
        var optionA = request.Options.Single(option => option.Id == "model-a");
        var optionB = request.Options.Single(option => option.Id == "model-b");
        Assert.True(optionB.IsCurrent);
        Assert.False(optionA.IsCurrent);
    }

    [Fact]
    public async Task Resume_without_id_prompts_with_recent_session_ids()
    {
        await new SessionTranscriptStore(this.tempDir).SaveAsync(
            "session-a",
            [new ChatMessage(ChatRole.User, [new TextBlock("hello")])]);
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["session-a"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);

        await new ResumeCommand().ExecuteAsync(built.Context, []);

        Assert.Equal("session-a", built.Context.Session.SessionId);
        Assert.Single(built.Context.Session.History);
        Assert.Equal("Choose a session", Assert.Single(prompts.Requests).Title);
    }

    [Fact]
    public async Task Login_copilot_prompts_for_deployment_and_enterprise_domain()
    {
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ["enterprise"], null),
            new UiPromptResponse(false, [], "octocorp.ghe.com"));
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        var selection = await LoginCommand.PromptCopilotDeploymentAsync(
            built.Context,
            currentEnterpriseDomain: null,
            CancellationToken.None);

        Assert.False(selection.Cancelled);
        Assert.Equal("octocorp.ghe.com", selection.EnterpriseDomain);
        Assert.Equal(["Which GitHub Copilot deployment", "GitHub Enterprise domain"], prompts.Requests.Select(request => request.Title));
    }

    [Fact]
    public async Task Login_copilot_public_deployment_clears_enterprise_domain()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["public"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        var selection = await LoginCommand.PromptCopilotDeploymentAsync(
            built.Context,
            currentEnterpriseDomain: "octocorp.ghe.com",
            CancellationToken.None);

        Assert.False(selection.Cancelled);
        Assert.Null(selection.EnterpriseDomain);
        Assert.Equal("Which GitHub Copilot deployment", Assert.Single(prompts.Requests).Title);
    }

    [Fact]
    public async Task Mcp_wizard_prompts_for_http_secret_storage()
    {
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ["http"], null),
            new UiPromptResponse(false, [], "https://example.com/mcp"),
            new UiPromptResponse(false, [], string.Empty),
            new UiPromptResponse(false, ["bearer"], null),
            new UiPromptResponse(false, [], "super-secret"),
            new UiPromptResponse(false, ["yes"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);
        built.Context.CredentialStore = new InMemoryTokenStore();

        var config = Assert.IsType<McpHttpServerConfig>(
            await McpCommand.RunWizardAsync(built.Context, "demo", CancellationToken.None));

        Assert.Equal(new Uri("https://example.com/mcp"), config.Url);
        Assert.Equal("super-secret", config.Auth.BearerToken);
    }

    [Fact]
    public async Task Marketplace_missing_action_is_collected_through_prompt_service()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["list"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);

        await new MarketplaceCommand(Path.Combine(this.tempDir, "plugins")).ExecuteAsync(built.Context, []);

        Assert.Equal("Marketplace action", Assert.Single(prompts.Requests).Title);
        Assert.Contains("No marketplaces added", built.Console.Output);
    }

    [Fact]
    public async Task Plugin_missing_action_is_collected_through_prompt_service()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["list"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);

        await new PluginCommand(Path.Combine(this.tempDir, "plugins")).ExecuteAsync(built.Context, []);

        Assert.Equal("Plugin action", Assert.Single(prompts.Requests).Title);
        Assert.Contains("No plugins installed", built.Console.Output);
    }

    // ── Cancellation: a dismissed prompt returns null/Continue and mutates nothing ──

    [Fact]
    public async Task Provider_picker_cancelled_makes_no_change()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(true, [], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);
        var before = built.Context.Session.ActiveProviderId;

        var provider = await SetupWizard.ChooseProviderAsync(built.Context);

        Assert.Null(provider);
        Assert.Equal(before, built.Context.Session.ActiveProviderId);
    }

    [Fact]
    public async Task Model_picker_cancelled_leaves_model_unchanged()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(true, [], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);
        var before = built.Context.Session.Model;
        var models = new ModelListResult(
            built.Context.ActiveProvider.Id,
            ModelSource.Catalog,
            [new ModelListEntry("model-a", "A", 200_000)]);

        var selected = await ModelCommand.ChooseModelAsync(built.Context, models);

        Assert.Null(selected);
        Assert.Equal(before, built.Context.Session.Model);
    }

    [Fact]
    public async Task Resume_picker_cancelled_leaves_session_unchanged()
    {
        await new SessionTranscriptStore(this.tempDir).SaveAsync(
            "session-a",
            [new ChatMessage(ChatRole.User, [new TextBlock("hello")])]);
        var prompts = new RecordingPromptService(new UiPromptResponse(true, [], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);

        await new ResumeCommand().ExecuteAsync(built.Context, []);

        Assert.Null(built.Context.Session.SessionId);
        Assert.Empty(built.Context.Session.History);
    }

    [Fact]
    public async Task Login_copilot_deployment_cancelled_reports_cancelled()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(true, [], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        var selection = await LoginCommand.PromptCopilotDeploymentAsync(
            built.Context,
            currentEnterpriseDomain: "octocorp.ghe.com",
            CancellationToken.None);

        Assert.True(selection.Cancelled);
        Assert.Null(selection.EnterpriseDomain);
    }

    [Fact]
    public async Task Mcp_wizard_cancelled_transport_writes_no_config()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(true, [], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);
        built.Context.CredentialStore = new InMemoryTokenStore();

        var config = await McpCommand.RunWizardAsync(built.Context, "demo", CancellationToken.None);

        Assert.Null(config);
    }

    // ── Non-interactive: retains list/usage behavior and never awaits a prompt ──

    [Fact]
    public async Task Bare_slash_non_interactive_never_awaits_a_prompt()
    {
        var prompts = new NonInteractivePromptService();
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        await built.App.DispatchAsync(CommandParser.Parse("/"));

        Assert.Equal(0, prompts.RequestCount);
    }

    [Fact]
    public async Task Setup_uses_prompt_interactivity_instead_of_console_capability()
    {
        var prompts = new NonInteractivePromptService();
        var built = TestAppBuilder.BuildApp(prompts: prompts);
        built.Console.Profile.Capabilities.Interactive = true;

        await new SetupWizard().RunAsync(built.Context);

        Assert.Equal(0, prompts.RequestCount);
        Assert.Contains("Non-interactive", built.Console.Output);
    }

    [Fact]
    public async Task Provider_no_id_non_interactive_lists_without_prompting()
    {
        var prompts = new NonInteractivePromptService();
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        await new ProviderCommand().ExecuteAsync(built.Context, []);

        Assert.Equal(0, prompts.RequestCount);
        Assert.Contains("Available", built.Console.Output);
    }

    [Fact]
    public async Task Resume_no_id_non_interactive_lists_without_prompting()
    {
        await new SessionTranscriptStore(this.tempDir).SaveAsync(
            "session-a",
            [new ChatMessage(ChatRole.User, [new TextBlock("hello")])]);
        var prompts = new NonInteractivePromptService();
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);

        await new ResumeCommand().ExecuteAsync(built.Context, []);

        Assert.Equal(0, prompts.RequestCount);
        Assert.Contains("session-a", built.Console.Output);
    }

    [Fact]
    public async Task Marketplace_no_action_non_interactive_lists_without_prompting()
    {
        var prompts = new NonInteractivePromptService();
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);

        await new MarketplaceCommand(Path.Combine(this.tempDir, "plugins")).ExecuteAsync(built.Context, []);

        Assert.Equal(0, prompts.RequestCount);
        Assert.Contains("No marketplaces added", built.Console.Output);
    }

    [Fact]
    public async Task Plugin_no_action_non_interactive_lists_without_prompting()
    {
        var prompts = new NonInteractivePromptService();
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);

        await new PluginCommand(Path.Combine(this.tempDir, "plugins")).ExecuteAsync(built.Context, []);

        Assert.Equal(0, prompts.RequestCount);
        Assert.Contains("No plugins installed", built.Console.Output);
    }

    /// <summary>
    /// A non-interactive surface that fails loudly if any code path awaits a prompt, so
    /// tests can prove plain/headless dispatch never blocks on user input.
    /// </summary>
    private sealed class NonInteractivePromptService : IUiPromptService
    {
        public bool IsInteractive => false;

        public int RequestCount { get; private set; }

        public Task<UiPromptResponse> RequestAsync(UiPromptRequest request, CancellationToken cancellationToken = default)
        {
            this.RequestCount++;
            throw new InvalidOperationException("Non-interactive prompt surface must not be awaited.");
        }
    }
}
