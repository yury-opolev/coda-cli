using Coda.Agent;
using Coda.Tui.Agent;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using LlmClient;
using Spectre.Console;

namespace Coda.Tui;

/// <summary>The REPL host: renders the banner, reads input, and dispatches each line.</summary>
public sealed class TuiApp : IDisposable
{
    private readonly CommandContext context;
    private readonly AgentRunner agentRunner;
    private readonly bool ownsRunner;
    private readonly IShellExecutor shellExecutor;

    public TuiApp(
        CommandContext context,
        Func<IReadOnlyList<Coda.Agent.ITool>>? mcpToolsProvider = null,
        IShellExecutor? shellExecutor = null,
        AgentRunner? agentRunner = null)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));

        // A shared runner (e.g. one the host also hands to the controller for Ctrl-C / mode switches)
        // is owned by the caller; only a runner we create here is ours to dispose.
        this.ownsRunner = agentRunner is null;
        this.agentRunner = agentRunner ?? new AgentRunner(mcpToolsProvider);
        this.shellExecutor = shellExecutor ?? new ProcessShellExecutor();
    }

    /// <summary>The agent runner this host dispatches turns through. Exposed for host wiring/tests.</summary>
    internal AgentRunner Runner => this.agentRunner;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var connectedProvider = await this.context.Credentials.GetConnectedProviderIdAsync(cancellationToken).ConfigureAwait(false);
        Banner.Render(this.context.Console, this.context.Session, connectedProvider, this.context.Session.Model);

        var interactive = this.context.Console.Profile.Capabilities.Interactive;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Plain line read works both interactively and with piped/scripted input
            // (a Spectre text widget throws in non-interactive mode). Only show the
            // glyph when interactive so scripted/piped output stays clean.
            if (interactive)
            {
                if (!string.IsNullOrEmpty(this.context.Session.Goal))
                {
                    // Keep the indicator compact; the full goal text is shown by /goal.
                    var goal = this.context.Session.Goal;
                    var shown = goal.Length > 40 ? goal[..40] + "…" : goal;
                    this.context.Console.Markup(Theme.DimMarkup($"[goal: {shown}] "));
                }

                this.context.Console.Markup($"{Theme.PromptGlyph} ");
            }

            var input = interactive
                ? await new InteractiveLineEditor(this.context.Console, this.context.Commands)
                    .ReadLineAsync(cancellationToken).ConfigureAwait(false)
                : Console.ReadLine();
            if (input is null)
            {
                break; // EOF
            }

            CommandResult result;
            try
            {
                result = await this.DispatchAsync(CommandParser.Parse(input), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                this.context.Console.MarkupLine(Theme.DimMarkup("Canceled."));
                break;
            }
            catch (Exception ex)
            {
                // A single command must never tear down the whole REPL.
                this.context.Console.MarkupLine(Theme.ErrorMarkup($"Error: {ex.Message}"));
                continue;
            }

            if (result.ShouldExit)
            {
                break;
            }
        }
    }

    /// <summary>Dispatch a single parsed line. Exposed for testing.</summary>
    public async Task<CommandResult> DispatchAsync(ParsedInput parsed, CancellationToken cancellationToken = default)
    {
        switch (parsed.Kind)
        {
            case ParsedInputKind.Empty:
                return CommandResult.Continue;

            case ParsedInputKind.Slash:
                var name = parsed.Name;
                if (name.Length == 0)
                {
                    // Bare "/" -> interactive command menu through the host-neutral prompt surface.
                    name = await this.ShowCommandMenuAsync(cancellationToken).ConfigureAwait(false);
                    if (name is null)
                    {
                        return CommandResult.Continue;
                    }
                }

                var command = this.context.Commands.Resolve(name);
                if (command is null)
                {
                    this.context.Console.MarkupLine(
                        Theme.WarnMarkup($"Unknown command '/{name}'.") + " " + Theme.DimMarkup("Type /help for the list."));
                    return CommandResult.Continue;
                }

                if (parsed.Args.Count > 0 && HelpToken.IsHelpToken(parsed.Args[0]))
                {
                    Coda.Tui.Rendering.CommandHelpRenderer.Render(this.context.Console, command);
                    return CommandResult.Continue;
                }

                var commandResult = await command.ExecuteAsync(this.context, parsed.Args, cancellationToken).ConfigureAwait(false);
                if (!commandResult.ShouldExit && !string.IsNullOrEmpty(commandResult.PromptToRun))
                {
                    await this.agentRunner.RunAsync(this.context, commandResult.PromptToRun, cancellationToken).ConfigureAwait(false);
                }

                return commandResult;

            case ParsedInputKind.Bash:
                return await this.RunBashAsync(parsed.Text, cancellationToken).ConfigureAwait(false);

            case ParsedInputKind.Prompt:
                await this.agentRunner.RunAsync(this.context, parsed.Text, cancellationToken).ConfigureAwait(false);
                return CommandResult.Continue;

            default:
                return CommandResult.Continue;
        }
    }

    private async Task<CommandResult> RunBashAsync(string command, CancellationToken cancellationToken)
    {
        ShellResult result;
        try
        {
            result = await this.shellExecutor.RunAsync(
                command,
                this.context.Session.WorkingDirectory,
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new ShellResult(-1, string.Empty, $"Shell error: {ex.Message}", false);
        }

        // Surface a timeout in the stderr channel so it is both shown to the user
        // and recorded in the conversation (the model would otherwise be blind to it).
        var stderr = result.TimedOut && string.IsNullOrEmpty(result.Stderr)
            ? "Command timed out after 120s."
            : result.Stderr;

        if (!string.IsNullOrEmpty(result.Stdout))
        {
            this.context.Console.WriteLine(result.Stdout);
        }

        if (!string.IsNullOrEmpty(stderr))
        {
            this.context.Console.MarkupLine(Theme.WarnMarkup(Markup.Escape(stderr)));
        }

        this.context.Session.History.Add(
            new ChatMessage(ChatRole.User, [new TextBlock(BuildBashBlock(command, result.Stdout, stderr))]));

        return CommandResult.Continue;
    }

    private static string BuildBashBlock(string command, string stdout, string stderr) =>
        $"<bash-input>{XmlEscape(command)}</bash-input>\n" +
        $"<bash-stdout>{XmlEscape(stdout)}</bash-stdout>\n" +
        $"<bash-stderr>{XmlEscape(stderr)}</bash-stderr>";

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private async Task<string?> ShowCommandMenuAsync(CancellationToken cancellationToken)
    {
        // The interactive selection menu needs a prompt surface that a user can answer;
        // fall back to a hint otherwise (scripted/piped input) and never await a prompt.
        if (!this.context.Prompts.IsInteractive)
        {
            this.context.Console.MarkupLine(Theme.DimMarkup("Type /help to list commands."));
            return null;
        }

        var options = this.context.Commands.ListSorted()
            .Select(command => new UiPromptOption(command.Name, command.Name, command.Summary));

        var response = await this.context.Prompts.RequestAsync(
            UiPromptRequest.Select("Select a command", options),
            cancellationToken).ConfigureAwait(false);

        if (response.Cancelled || response.SelectedIds.Length == 0)
        {
            return null;
        }

        return response.SelectedIds[0];
    }

    public void Dispose()
    {
        if (this.ownsRunner)
        {
            this.agentRunner.Dispose();
        }
    }
}
