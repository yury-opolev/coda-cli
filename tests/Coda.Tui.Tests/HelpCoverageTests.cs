using Coda.Tui.Repl;

namespace Coda.Tui.Tests;

public sealed class HelpCoverageTests
{
    public static IEnumerable<object[]> AllCommands() =>
        SlashCommandCatalog.CreateAll().Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Every_command_has_usage(ISlashCommand command)
    {
        Assert.False(string.IsNullOrWhiteSpace(command.Help.Usage),
            $"/{command.Name} is missing Help.Usage");
        Assert.StartsWith("/", command.Help.Usage);
    }

    // Commands the spec marks "complex" must carry a description AND at least one example.
    // When you add a new command that takes arguments/subcommands, add its Name here so
    // its help is held to the richer (description + examples) standard.
    private static readonly HashSet<string> Complex = new(StringComparer.Ordinal)
    {
        "goal", "team", "log", "effort", "model", "provider", "plugin", "plugins",
        "marketplace", "skill", "skills", "output-style", "permissions", "rewind",
        "resume", "context", "memory", "image",
    };

    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Complex_commands_have_description_and_examples(ISlashCommand command)
    {
        if (!Complex.Contains(command.Name))
        {
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(command.Help.Description),
            $"/{command.Name} (complex) is missing Help.Description");
        Assert.True(command.Help.Examples is { Count: > 0 },
            $"/{command.Name} (complex) is missing Help.Examples");
    }
}
