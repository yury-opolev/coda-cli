using System.Text.Json;
using Coda.Agent;
using Coda.Agent.ToolSearch;

namespace Engine.Tests.ToolSearch;

public sealed class ToolSearchEngineTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class StubTool : ITool
    {
        private readonly bool shouldDefer;
        private readonly string? searchHint;

        public StubTool(string name, string description = "stub", bool shouldDefer = false, string? searchHint = null)
        {
            this.Name = name;
            this.Description = description;
            this.shouldDefer = shouldDefer;
            this.searchHint = searchHint;
        }

        public string Name { get; }
        public string Description { get; }
        public string InputSchemaJson => "{}";
        public bool IsReadOnly => true;
        public bool ShouldDefer => this.shouldDefer;
        public string? SearchHint => this.searchHint;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private static IReadOnlyList<ITool> Tools(params ITool[] tools) => tools;

    // ── select: path ─────────────────────────────────────────────────────────

    [Fact]
    public void Select_returns_named_tools()
    {
        var read = new StubTool("Read");
        var edit = new StubTool("Edit");
        var grep = new StubTool("Grep");
        var deferred = Tools(read, edit, grep);
        var all = Tools(read, edit, grep);

        var result = ToolSearchEngine.Search("select:Read,Grep", deferred, all, 10);

        Assert.Equal(["Read", "Grep"], result);
    }

    [Fact]
    public void Select_missing_returns_only_found()
    {
        var read = new StubTool("Read");
        var deferred = Tools(read);
        var all = Tools(read);

        var result = ToolSearchEngine.Search("select:Read,Nope", deferred, all, 10);

        Assert.Equal(["Read"], result);
    }

    [Fact]
    public void Select_none_found_returns_empty()
    {
        var deferred = Tools();
        var all = Tools();

        var result = ToolSearchEngine.Search("select:Nope", deferred, all, 10);

        Assert.Empty(result);
    }

    [Fact]
    public void Select_case_insensitive_prefix()
    {
        var read = new StubTool("Read");
        var deferred = Tools(read);
        var all = Tools(read);

        var result = ToolSearchEngine.Search("SELECT:Read", deferred, all, 10);

        Assert.Equal(["Read"], result);
    }

    [Fact]
    public void Select_deduplicates_repeated_names()
    {
        var read = new StubTool("Read");
        var deferred = Tools(read);
        var all = Tools(read);

        var result = ToolSearchEngine.Search("select:Read,Read", deferred, all, 10);

        Assert.Equal(["Read"], result);
    }

    // ── exact-name fast path ─────────────────────────────────────────────────

    [Fact]
    public void Exact_name_match()
    {
        var tool = new StubTool("edit_file");
        var deferred = Tools(tool);
        var all = Tools(tool);

        var result = ToolSearchEngine.Search("edit_file", deferred, all, 10);

        Assert.Equal(["edit_file"], result);
    }

    [Fact]
    public void Exact_name_match_falls_back_to_all_tools()
    {
        var tool = new StubTool("Read");
        var deferred = Tools();
        var all = Tools(tool);

        var result = ToolSearchEngine.Search("read", deferred, all, 10);

        Assert.Equal(["Read"], result);
    }

    // ── mcp__ prefix fast path ───────────────────────────────────────────────

    [Fact]
    public void Mcp_prefix_match()
    {
        var slackSend = new StubTool("mcp__slack__send");
        var slackList = new StubTool("mcp__slack__list");
        var githubPr = new StubTool("mcp__github__pr");
        var deferred = Tools(slackSend, slackList, githubPr);
        var all = deferred;

        var result = ToolSearchEngine.Search("mcp__slack", deferred, all, 10);

        Assert.Equal(["mcp__slack__send", "mcp__slack__list"], result);
    }

    [Fact]
    public void Mcp_prefix_match_respects_maxResults()
    {
        var tools = Enumerable.Range(1, 5)
            .Select(i => (ITool)new StubTool($"mcp__slack__action{i}"))
            .ToList();
        var deferred = tools;
        var all = tools;

        var result = ToolSearchEngine.Search("mcp__slack", deferred, all, 3);

        Assert.Equal(3, result.Count);
    }

    // ── keyword search — required terms (+) ──────────────────────────────────

    [Fact]
    public void Required_term_filters()
    {
        var slackSend = new StubTool("mcp__slack__send", "send a message");
        var githubPr = new StubTool("mcp__github__pr", "create a PR");
        var deferred = Tools(slackSend, githubPr);
        var all = deferred;

        var result = ToolSearchEngine.Search("+slack send", deferred, all, 10);

        Assert.Equal(["mcp__slack__send"], result);
    }

    // ── keyword scoring ───────────────────────────────────────────────────────

    [Fact]
    public void Keyword_name_part_beats_description_only()
    {
        // Tool A: name has "notebook" part → weight 10 (non-mcp exact part)
        var toolA = new StubTool("notebook_edit", "some description");
        // Tool B: description has "edit a notebook" → weight 2 (description word-boundary)
        var toolB = new StubTool("writer", "edit a notebook");
        var deferred = Tools(toolA, toolB);
        var all = deferred;

        var result = ToolSearchEngine.Search("notebook", deferred, all, 10);

        Assert.Equal(2, result.Count);
        Assert.Equal("notebook_edit", result[0]); // higher score first
        Assert.Equal("writer", result[1]);
    }

    [Fact]
    public void Word_boundary_avoids_substring_false_positive()
    {
        // "listen" contains "list" as substring but NOT at a word boundary
        var listenTool = new StubTool("audio", "listen to the stream");
        // "list items" contains "list" at a word boundary
        var listTool = new StubTool("items", "list items in a directory");
        var deferred = Tools(listenTool, listTool);
        var all = deferred;

        var result = ToolSearchEngine.Search("list", deferred, all, 10);

        Assert.DoesNotContain("audio", result);
        Assert.Contains("items", result);
    }

    // ── maxResults cap ────────────────────────────────────────────────────────

    [Fact]
    public void MaxResults_caps()
    {
        // Many tools whose name contains "read"
        var tools = Enumerable.Range(1, 10)
            .Select(i => (ITool)new StubTool($"read_file_{i}", $"reads file {i}"))
            .ToList();
        var deferred = tools;
        var all = tools;

        var result = ToolSearchEngine.Search("read", deferred, all, 2);

        Assert.Equal(2, result.Count);
    }

    // ── edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void No_match_returns_empty()
    {
        var tool = new StubTool("ReadFile", "reads a file");
        var deferred = Tools(tool);
        var all = deferred;

        var result = ToolSearchEngine.Search("zzznomatch", deferred, all, 10);

        Assert.Empty(result);
    }

    [Fact]
    public void Empty_query_returns_empty()
    {
        var tool = new StubTool("ReadFile", "reads a file");
        var deferred = Tools(tool);
        var all = deferred;

        var result = ToolSearchEngine.Search("", deferred, all, 10);

        Assert.Empty(result);
    }

    [Fact]
    public void Whitespace_only_query_returns_empty()
    {
        var tool = new StubTool("ReadFile", "reads a file");
        var deferred = Tools(tool);
        var all = deferred;

        var result = ToolSearchEngine.Search("   ", deferred, all, 10);

        Assert.Empty(result);
    }

    // ── SearchHint boost ──────────────────────────────────────────────────────

    [Fact]
    public void SearchHint_boosts()
    {
        // This tool's name/desc do not mention "slack" but its SearchHint does
        var hintTool = new StubTool("mcp__messaging__send", "send messages", searchHint: "send slack message");
        var noHintTool = new StubTool("mcp__unrelated__tool", "unrelated thing");
        var deferred = Tools(hintTool, noHintTool);
        var all = deferred;

        var result = ToolSearchEngine.Search("slack", deferred, all, 10);

        Assert.Contains("mcp__messaging__send", result);
        Assert.DoesNotContain("mcp__unrelated__tool", result);
    }
}
