using System.Diagnostics;

namespace Coda.Client.Process;

/// <summary>
/// How to launch an engine process. The defaults describe the usual
/// <c>coda serve</c> invocation; the fluent methods build up a command without
/// mutating a shared instance, so a caller can derive variants safely.
/// </summary>
public sealed record EngineCommand
{
    /// <summary>The executable to run. Defaults to <c>coda</c> resolved from
    /// <c>PATH</c>.</summary>
    public string Program { get; init; } = "coda";

    /// <summary>The arguments. Defaults to <c>serve</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = new[] { "serve" };

    /// <summary>The working directory for the session, or null to inherit the
    /// current one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment variables to set on the child.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Inherited environment variables to remove from the child. Setting a
    /// variable to empty cannot express "make sure this inherited secret is
    /// gone" — an empty value is still a present variable — so removal is the only
    /// way to isolate a child from an inherited credential such as
    /// <c>ANTHROPIC_API_KEY</c> or a <c>CODA_SERVE_*</c> setting.
    /// </summary>
    public IReadOnlyList<string> EnvironmentToRemove { get; init; } = Array.Empty<string>();

    /// <summary>Starts from a bare program with no preset arguments.</summary>
    public static EngineCommand For(string program) => new()
    {
        Program = program,
        Arguments = Array.Empty<string>(),
    };

    public EngineCommand WithArguments(params string[] arguments) =>
        this with { Arguments = arguments };

    public EngineCommand WithArgument(string argument) =>
        this with { Arguments = this.Arguments.Append(argument).ToArray() };

    public EngineCommand WithWorkingDirectory(string workingDirectory) =>
        this with { WorkingDirectory = workingDirectory };

    public EngineCommand WithEnvironment(string key, string value)
    {
        var environment = new Dictionary<string, string>(this.Environment) { [key] = value };
        return this with { Environment = environment };
    }

    public EngineCommand WithoutEnvironment(string key) =>
        this with { EnvironmentToRemove = this.EnvironmentToRemove.Append(key).ToArray() };

    internal ProcessStartInfo ToStartInfo()
    {
        var info = new ProcessStartInfo
        {
            FileName = this.Program,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in this.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (this.WorkingDirectory is not null)
        {
            info.WorkingDirectory = this.WorkingDirectory;
        }

        foreach ((string key, string value) in this.Environment)
        {
            info.Environment[key] = value;
        }

        foreach (string key in this.EnvironmentToRemove)
        {
            info.Environment.Remove(key);
        }

        return info;
    }
}
