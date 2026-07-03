using System.Text.Json;
using Coda.Mcp;

namespace Engine.Tests;

public sealed class McpServerInfoTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Parse_extracts_name_version_and_instructions()
    {
        var el = Json("""
        {
          "protocolVersion": "2025-06-18",
          "serverInfo": { "name": "github", "version": "1.2.3" },
          "instructions": "Use this to manage GitHub."
        }
        """);

        var info = McpServerInfo.Parse(el);

        Assert.Equal("github", info.Name);
        Assert.Equal("1.2.3", info.Version);
        Assert.Equal("Use this to manage GitHub.", info.Instructions);
    }

    [Fact]
    public void Parse_missing_serverInfo_and_instructions_are_null()
    {
        var el = Json("""{ "protocolVersion": "2025-06-18" }""");

        var info = McpServerInfo.Parse(el);

        Assert.Null(info.Name);
        Assert.Null(info.Version);
        Assert.Null(info.Instructions);
    }

    [Fact]
    public void Parse_partial_serverInfo_keeps_present_fields()
    {
        // name present, version absent, instructions absent → only name is populated.
        var el = Json("""{ "serverInfo": { "name": "fs" } }""");

        var info = McpServerInfo.Parse(el);

        Assert.Equal("fs", info.Name);
        Assert.Null(info.Version);
        Assert.Null(info.Instructions);
    }

    [Fact]
    public void Parse_non_object_is_all_null()
    {
        var info = McpServerInfo.Parse(Json("\"not-an-object\""));

        Assert.Null(info.Name);
        Assert.Null(info.Version);
        Assert.Null(info.Instructions);
    }
}
