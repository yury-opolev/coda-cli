using Coda.Agent.Settings;
using Coda.Sdk.Providers;

namespace Coda.Tui.Tests;

/// <summary>
/// Parity tests proving <c>coda serve</c> and the TUI/<see cref="SettingsLoader"/>
/// read from a SINGLE source of settings defaults. For the same settings dir, the
/// (provider, model) serve resolves must equal what the loader resolves with the
/// same precedence chain: explicit flag → connected credential (provider; NOT
/// settings.DefaultProvider — <c>Parse</c>/<c>ApplyDefaults</c> never has a
/// connected credential to consult, so provider resolves from the flag alone),
/// explicit flag → settings default (model). Also locks the intended alignment:
/// serve now honors PROJECT settings.json defaults for the model (previously it
/// read USER-only).
/// </summary>
public sealed class ServeDefaultsParityTests
{
    /// <summary>
    /// Mirrors the precedence the loader-backed resolution applies, so the test
    /// asserts against an independent expectation rather than the code under test.
    /// </summary>
    private static (string? ProviderId, string? Model) ResolveFromLoader(
        string? providerFlag,
        string? modelFlag,
        string workingDir,
        string userSettingsDir)
    {
        var settings = SettingsLoader.Load(workingDir, userSettingsDir);
        var providerId = string.IsNullOrWhiteSpace(providerFlag) ? null : ProviderAliases.Resolve(providerFlag);
        var model = modelFlag ?? settings.DefaultModel;
        return (providerId, model);
    }

    [Fact]
    public void Serve_resolved_defaults_equal_loader_resolution_user_only()
    {
        using var env = new TempEnv("""
        {
            "defaultProvider": "github-copilot",
            "defaultModel": "user-model"
        }
        """, projectJson: null);

        var serve = ServeRunner.Parse(["--cwd", env.WorkingDir], env.UserHome);
        var expected = ResolveFromLoader(null, null, env.WorkingDir, env.UserHome);

        // settings.DefaultProvider is no longer a provider selector: with no flag and no
        // connected credential in scope, provider resolves to null (Require fails fast later).
        Assert.Null(serve.ProviderId);
        Assert.Equal("user-model", serve.Model);
        Assert.Equal(expected.ProviderId, serve.ProviderId);
        Assert.Equal(expected.Model, serve.Model);
    }

    [Fact]
    public void Serve_honors_project_settings_defaults_matching_loader()
    {
        // INTENDED ALIGNMENT: project settings.json now influences serve's MODEL default (it
        // used to be USER-only). Provider is untouched by either settings file (see above).
        using var env = new TempEnv(
            userJson: """
            {
                "defaultProvider": "github-copilot",
                "defaultModel": "user-model"
            }
            """,
            projectJson: """
            {
                "defaultModel": "project-model"
            }
            """);

        var serve = ServeRunner.Parse(["--cwd", env.WorkingDir], env.UserHome);
        var expected = ResolveFromLoader(null, null, env.WorkingDir, env.UserHome);

        // Project model wins; provider stays null (no flag, no connected credential). Serve == loader.
        Assert.Null(serve.ProviderId);
        Assert.Equal("project-model", serve.Model);
        Assert.Equal(expected.ProviderId, serve.ProviderId);
        Assert.Equal(expected.Model, serve.Model);
    }

    [Fact]
    public void Serve_explicit_flags_win_and_match_loader()
    {
        using var env = new TempEnv("""
        {
            "defaultProvider": "github-copilot",
            "defaultModel": "user-model"
        }
        """, projectJson: null);

        var serve = ServeRunner.Parse(["--provider", "claude", "--model", "flag-model", "--cwd", env.WorkingDir], env.UserHome);
        var expected = ResolveFromLoader("claude", "flag-model", env.WorkingDir, env.UserHome);

        Assert.Equal(expected.ProviderId, serve.ProviderId);
        Assert.Equal(expected.Model, serve.Model);
        Assert.Equal("flag-model", serve.Model);
    }

    [Fact]
    public void Serve_no_settings_resolves_to_null_no_builtin_default()
    {
        // No flags, no settings → nothing configured. Serve resolves provider/model to
        // null (no built-in fallback); RunAsync then fails fast before spawning.
        using var env = new TempEnv(userJson: null, projectJson: null);

        var serve = ServeRunner.Parse(["--cwd", env.WorkingDir], env.UserHome);
        var expected = ResolveFromLoader(null, null, env.WorkingDir, env.UserHome);

        Assert.Null(serve.ProviderId);
        Assert.Null(serve.Model);
        Assert.Equal(expected.ProviderId, serve.ProviderId);
        Assert.Equal(expected.Model, serve.Model);
    }

    /// <summary>
    /// A throwaway user-home + project working dir, each with an optional
    /// <c>.coda/settings.json</c>, so parity tests never touch the real machine.
    /// </summary>
    private sealed class TempEnv : IDisposable
    {
        public string UserHome { get; }
        public string WorkingDir { get; }

        public TempEnv(string? userJson, string? projectJson)
        {
            this.UserHome = Path.Combine(Path.GetTempPath(), "coda-parity-user-" + Guid.NewGuid().ToString("N"));
            this.WorkingDir = Path.Combine(Path.GetTempPath(), "coda-parity-proj-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(this.UserHome, ".coda"));
            Directory.CreateDirectory(Path.Combine(this.WorkingDir, ".coda"));
            if (userJson is not null)
            {
                File.WriteAllText(Path.Combine(this.UserHome, ".coda", "settings.json"), userJson);
            }

            if (projectJson is not null)
            {
                File.WriteAllText(Path.Combine(this.WorkingDir, ".coda", "settings.json"), projectJson);
            }
        }

        public void Dispose()
        {
            TryDelete(this.UserHome);
            TryDelete(this.WorkingDir);
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
