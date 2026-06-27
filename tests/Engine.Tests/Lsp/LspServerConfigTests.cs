using System.Text.Json.Nodes;
using Coda.Agent.Lsp;
using Coda.Agent.Settings;

namespace Engine.Tests.Lsp;

/// <summary>
/// TDD tests for Task 4 — LspServerConfig record + settings loading.
/// </summary>
public sealed class LspServerConfigTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static (string projectDir, string userDir) CreateTempDirs()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var userDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(projectDir, ".coda"));
        Directory.CreateDirectory(userDir);
        return (projectDir, userDir);
    }

    private static void WriteProjectSettings(string projectDir, string json)
    {
        File.WriteAllText(Path.Combine(projectDir, ".coda", "settings.json"), json);
    }

    private static void WriteUserSettings(string userDir, string json)
    {
        Directory.CreateDirectory(Path.Combine(userDir, ".coda"));
        File.WriteAllText(Path.Combine(userDir, ".coda", "settings.json"), json);
    }

    private static void CleanUp(string projectDir, string userDir)
    {
        Directory.Delete(projectDir, recursive: true);
        Directory.Delete(userDir, recursive: true);
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Parses_two_servers_with_extension_maps()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            WriteProjectSettings(projectDir, """
                {
                  "lspServers": {
                    "typescript": {
                      "command": "typescript-language-server",
                      "args": ["--stdio"],
                      "extensionToLanguage": { ".ts": "typescript", ".tsx": "typescriptreact" }
                    },
                    "python": {
                      "command": "pyright-langserver",
                      "args": ["--stdio"],
                      "extensionToLanguage": { ".py": "python" }
                    }
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Equal(2, settings.LspServers.Count);
            Assert.True(settings.LspServers.ContainsKey("typescript"));
            Assert.True(settings.LspServers.ContainsKey("python"));

            var ts = settings.LspServers["typescript"];
            Assert.Equal("typescript-language-server", ts.Command);
            Assert.Equal(["--stdio"], ts.Args);
            Assert.Equal(2, ts.ExtensionToLanguage.Count);
            Assert.Equal("typescript", ts.ExtensionToLanguage[".ts"]);
            Assert.Equal("typescriptreact", ts.ExtensionToLanguage[".tsx"]);

            var py = settings.LspServers["python"];
            Assert.Equal("python", py.ExtensionToLanguage[".py"]);
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void Server_missing_extensionToLanguage_is_skipped_others_kept()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            WriteProjectSettings(projectDir, """
                {
                  "lspServers": {
                    "bad": {
                      "command": "some-server",
                      "args": []
                    },
                    "good": {
                      "command": "good-server",
                      "args": [],
                      "extensionToLanguage": { ".go": "go" }
                    }
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Single(settings.LspServers);
            Assert.True(settings.LspServers.ContainsKey("good"));
            Assert.False(settings.LspServers.ContainsKey("bad"));
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void Command_with_space_non_absolute_is_skipped()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            WriteProjectSettings(projectDir, """
                {
                  "lspServers": {
                    "bad": {
                      "command": "node server.js",
                      "args": [],
                      "extensionToLanguage": { ".js": "javascript" }
                    },
                    "good": {
                      "command": "node",
                      "args": ["server.js"],
                      "extensionToLanguage": { ".js": "javascript" }
                    }
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Single(settings.LspServers);
            Assert.True(settings.LspServers.ContainsKey("good"));
            Assert.False(settings.LspServers.ContainsKey("bad"));
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void Socket_transport_is_skipped()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            WriteProjectSettings(projectDir, """
                {
                  "lspServers": {
                    "socket-server": {
                      "command": "some-lsp",
                      "args": [],
                      "extensionToLanguage": { ".rs": "rust" },
                      "transport": "socket"
                    },
                    "stdio-server": {
                      "command": "other-lsp",
                      "args": [],
                      "extensionToLanguage": { ".rs": "rust" },
                      "transport": "stdio"
                    }
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Single(settings.LspServers);
            Assert.True(settings.LspServers.ContainsKey("stdio-server"));
            Assert.False(settings.LspServers.ContainsKey("socket-server"));
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void Extensions_normalized_to_lowercase_with_dot()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            WriteProjectSettings(projectDir, """
                {
                  "lspServers": {
                    "typescript": {
                      "command": "typescript-language-server",
                      "args": [],
                      "extensionToLanguage": {
                        "TS": "typescript",
                        "tsx": "typescriptreact"
                      }
                    }
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Single(settings.LspServers);
            var ts = settings.LspServers["typescript"];
            Assert.True(ts.ExtensionToLanguage.ContainsKey(".ts"), "uppercase TS should become .ts");
            Assert.True(ts.ExtensionToLanguage.ContainsKey(".tsx"), "tsx without dot should become .tsx");
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void Project_server_overrides_user_server_same_name()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            WriteUserSettings(userDir, """
                {
                  "lspServers": {
                    "typescript": {
                      "command": "user-ts-server",
                      "args": ["--user-arg"],
                      "extensionToLanguage": { ".ts": "typescript" }
                    }
                  }
                }
                """);

            WriteProjectSettings(projectDir, """
                {
                  "lspServers": {
                    "typescript": {
                      "command": "project-ts-server",
                      "args": ["--project-arg"],
                      "extensionToLanguage": { ".ts": "typescript", ".tsx": "typescriptreact" }
                    }
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Single(settings.LspServers);
            var ts = settings.LspServers["typescript"];
            Assert.Equal("project-ts-server", ts.Command);
            Assert.Equal(["--project-arg"], ts.Args);
            Assert.Equal(2, ts.ExtensionToLanguage.Count);
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void No_lspServers_key_yields_empty_map_and_existing_settings_still_parse()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            WriteProjectSettings(projectDir, """
                {
                  "permissions": {
                    "allow": ["read_file"],
                    "deny": ["run_command"]
                  },
                  "hooks": {
                    "Stop": [{ "command": "notify-done" }]
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            // lspServers absent => empty map
            Assert.Empty(settings.LspServers);

            // Permissions and hooks still parsed correctly
            Assert.Contains("read_file", settings.Allow);
            Assert.Contains("run_command", settings.Deny);
            Assert.Single(settings.Hooks);
            Assert.Equal("notify-done", settings.Hooks[0].Command);
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void Env_and_initializationOptions_and_startupTimeout_parsed()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            WriteProjectSettings(projectDir, """
                {
                  "lspServers": {
                    "typescript": {
                      "command": "typescript-language-server",
                      "args": ["--stdio"],
                      "extensionToLanguage": { ".ts": "typescript" },
                      "env": { "NODE_ENV": "production", "DEBUG": "ts-server" },
                      "initializationOptions": { "preferences": { "includeInlayParameterNameHints": "all" } },
                      "startupTimeoutMs": 15000
                    }
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Single(settings.LspServers);
            var ts = settings.LspServers["typescript"];
            Assert.NotNull(ts.Env);
            Assert.Equal("production", ts.Env!["NODE_ENV"]);
            Assert.Equal("ts-server", ts.Env["DEBUG"]);
            Assert.NotNull(ts.InitializationOptions);
            Assert.Equal(15000, ts.StartupTimeoutMs);
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void Absolute_path_command_with_space_is_accepted()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            // An absolute Windows path with a space should be accepted
            WriteProjectSettings(projectDir, """
                {
                  "lspServers": {
                    "ts": {
                      "command": "C:\\Program Files\\Node\\node.exe",
                      "args": [],
                      "extensionToLanguage": { ".ts": "typescript" }
                    }
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Single(settings.LspServers);
            Assert.Equal("C:\\Program Files\\Node\\node.exe", settings.LspServers["ts"].Command);
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void Empty_extensionToLanguage_is_skipped()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            WriteProjectSettings(projectDir, """
                {
                  "lspServers": {
                    "bad": {
                      "command": "some-server",
                      "args": [],
                      "extensionToLanguage": {}
                    }
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Empty(settings.LspServers);
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void Missing_command_is_skipped()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            WriteProjectSettings(projectDir, """
                {
                  "lspServers": {
                    "bad": {
                      "args": [],
                      "extensionToLanguage": { ".ts": "typescript" }
                    }
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Empty(settings.LspServers);
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void Absent_transport_defaults_to_stdio_and_is_accepted()
    {
        var (projectDir, userDir) = CreateTempDirs();
        try
        {
            WriteProjectSettings(projectDir, """
                {
                  "lspServers": {
                    "go": {
                      "command": "gopls",
                      "args": [],
                      "extensionToLanguage": { ".go": "go" }
                    }
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Single(settings.LspServers);
        }
        finally
        {
            CleanUp(projectDir, userDir);
        }
    }

    [Fact]
    public void CodaSettings_Empty_has_empty_LspServers()
    {
        Assert.Empty(CodaSettings.Empty.LspServers);
    }
}
