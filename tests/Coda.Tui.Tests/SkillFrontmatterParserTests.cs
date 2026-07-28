using Coda.Tui.Skills;

namespace Coda.Tui.Tests;

public sealed class SkillFrontmatterParserTests
{
    // ── No-content / no-frontmatter cases ─────────────────────────────────

    [Fact]
    public void Empty_file_returns_no_frontmatter_and_empty_body()
    {
        var fm = SkillFrontmatterParser.Parse(string.Empty);

        Assert.False(fm.HasFrontmatter);
        Assert.Null(fm.Name);
        Assert.Null(fm.Description);
        Assert.Equal(string.Empty, fm.Body);
    }

    [Fact]
    public void File_with_no_frontmatter_returns_whole_content_as_body()
    {
        var fm = SkillFrontmatterParser.Parse("Just a plain skill body.");

        Assert.False(fm.HasFrontmatter);
        Assert.Equal("Just a plain skill body.", fm.Body);
    }

    [Fact]
    public void Unterminated_frontmatter_returns_no_frontmatter_and_whole_file_as_body()
    {
        const string content = "---\nname: foo\ndescription: bar\n";

        var fm = SkillFrontmatterParser.Parse(content);

        Assert.False(fm.HasFrontmatter);
        Assert.Equal(content.Trim(), fm.Body);
    }

    // ── Scalar fields ──────────────────────────────────────────────────────

    [Fact]
    public void Scalars_name_and_description_are_parsed()
    {
        var fm = Parse("name: my-skill\ndescription: Does something useful");

        Assert.Equal("my-skill", fm.Name);
        Assert.Equal("Does something useful", fm.Description);
    }

    [Fact]
    public void Double_quoted_value_strips_quotes()
    {
        var fm = Parse("name: \"quoted name\"");

        Assert.Equal("quoted name", fm.Name);
    }

    [Fact]
    public void Single_quoted_value_strips_quotes()
    {
        var fm = Parse("description: 'single quoted'");

        Assert.Equal("single quoted", fm.Description);
    }

    [Fact]
    public void When_to_use_field_is_parsed()
    {
        var fm = Parse("when_to_use: Use this when reviewing code");

        Assert.Equal("Use this when reviewing code", fm.WhenToUse);
    }

    [Fact]
    public void Argument_hint_field_is_parsed()
    {
        var fm = Parse("argument-hint: <filename> [<options>]");

        Assert.Equal("<filename> [<options>]", fm.ArgumentHint);
    }

    // ── Block lists ────────────────────────────────────────────────────────

    [Fact]
    public void Block_list_arguments_collected()
    {
        var fm = Parse("arguments:\n  - filename\n  - query");

        Assert.Equal(["filename", "query"], fm.Arguments);
    }

    [Fact]
    public void Block_list_items_with_trailing_comment_stripped()
    {
        var fm = Parse("arguments:\n  - file  # the target file\n  - mode");

        Assert.Equal(["file", "mode"], fm.Arguments);
    }

    [Fact]
    public void Block_list_unknown_field_retained()
    {
        var fm = Parse("allowed-tools:\n  - read_file\n  - grep");

        Assert.True(fm.UnknownFields.ContainsKey("allowed-tools"),
            "allowed-tools should appear in UnknownFields");
        var stored = fm.UnknownFields["allowed-tools"];
        Assert.Contains("read_file", stored);
        Assert.Contains("grep", stored);
    }

    // ── Flow lists ─────────────────────────────────────────────────────────

    [Fact]
    public void Flow_list_arguments_collected()
    {
        var fm = Parse("arguments: [filename, query]");

        Assert.Equal(["filename", "query"], fm.Arguments);
    }

    [Fact]
    public void Flow_list_with_spaces_and_quotes()
    {
        var fm = Parse("arguments: [ file , \"my query\" ]");

        Assert.Equal(["file", "my query"], fm.Arguments);
    }

    [Fact]
    public void Empty_flow_list_produces_empty_list()
    {
        var fm = Parse("arguments: []");

        Assert.Empty(fm.Arguments);
    }

    // ── Boolean and integer values (stored as raw strings) ─────────────────

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("FALSE")]
    [InlineData("YES")]
    public void Boolean_spellings_stored_as_raw_string_in_unknown_fields(string boolValue)
    {
        var fm = Parse($"user-invocable: {boolValue}");

        Assert.True(fm.UnknownFields.ContainsKey("user-invocable"));
        Assert.Equal(boolValue, fm.UnknownFields["user-invocable"]);
    }

    [Fact]
    public void Integer_value_stored_as_raw_string_in_unknown_fields()
    {
        var fm = Parse("max-tokens: 1024");

        Assert.True(fm.UnknownFields.ContainsKey("max-tokens"));
        Assert.Equal("1024", fm.UnknownFields["max-tokens"]);
    }

    // ── Comments ───────────────────────────────────────────────────────────

    [Fact]
    public void Full_line_comment_is_skipped()
    {
        var fm = Parse("# This is a comment\nname: foo");

        Assert.Equal("foo", fm.Name);
        Assert.False(fm.UnknownFields.ContainsKey("#"));
    }

    [Fact]
    public void Inline_comment_after_whitespace_is_stripped()
    {
        var fm = Parse("name: my-skill # this is the name");

        Assert.Equal("my-skill", fm.Name);
    }

    [Fact]
    public void Hash_inside_quoted_value_is_not_treated_as_comment()
    {
        var fm = Parse("name: \"has # in it\"");

        Assert.Equal("has # in it", fm.Name);
    }

    // ── Case-insensitive and hyphen/underscore-equivalent keys ─────────────

    [Fact]
    public void Keys_are_matched_case_insensitively()
    {
        var fm = Parse("NAME: upper-case-name\nDESCRIPTION: upper-case-description");

        Assert.Equal("upper-case-name", fm.Name);
        Assert.Equal("upper-case-description", fm.Description);
    }

    [Fact]
    public void Hyphen_and_underscore_are_equivalent_in_keys()
    {
        var fm = Parse("when_to_use: use for X\nargument_hint: <name>");

        Assert.Equal("use for X", fm.WhenToUse);
        Assert.Equal("<name>", fm.ArgumentHint);
    }

    [Fact]
    public void Mixed_case_hyphen_underscore_key_normalises_correctly()
    {
        var fm = Parse("WHEN-TO_USE: testing");

        Assert.Equal("testing", fm.WhenToUse);
    }

    // ── Unknown keys retained ─────────────────────────────────────────────

    [Fact]
    public void Unknown_scalar_key_is_retained_in_unknown_fields()
    {
        var fm = Parse("name: foo\nmodel: claude-opus-4\ndescription: bar");

        Assert.True(fm.UnknownFields.ContainsKey("model"),
            "model is an unknown field and should appear in UnknownFields");
        Assert.Equal("claude-opus-4", fm.UnknownFields["model"]);
    }

    [Fact]
    public void Unknown_key_with_bracketed_value_is_retained_verbatim_with_brackets()
    {
        // model: [gpt-4] must NOT be parsed as a flow list — brackets preserved literally.
        var fm = Parse("name: s\nmodel: [gpt-4]");

        Assert.True(fm.UnknownFields.ContainsKey("model"),
            "model should appear in UnknownFields");
        Assert.Equal("[gpt-4]", fm.UnknownFields["model"]);
    }

    [Fact]
    public void Multiple_unknown_keys_all_retained()
    {
        var fm = Parse("name: s\nmodel: x\neffort: high\ncontext: fork");

        Assert.Equal(3, fm.UnknownFields.Count);
        Assert.Equal("x", fm.UnknownFields["model"]);
        Assert.Equal("high", fm.UnknownFields["effort"]);
        Assert.Equal("fork", fm.UnknownFields["context"]);
    }

    [Fact]
    public void Known_keys_do_not_appear_in_unknown_fields()
    {
        var fm = Parse("name: n\ndescription: d\nwhen-to-use: w\nargument-hint: a");

        Assert.Empty(fm.UnknownFields);
    }

    // ── Body extraction ───────────────────────────────────────────────────

    [Fact]
    public void Body_is_content_after_closing_delimiter_trimmed()
    {
        var content = "---\nname: foo\n---\n\nBody text here.\n";
        var fm = SkillFrontmatterParser.Parse(content);

        Assert.Equal("Body text here.", fm.Body);
    }

    [Fact]
    public void Has_frontmatter_is_true_when_well_formed()
    {
        var fm = Parse("name: foo");

        Assert.True(fm.HasFrontmatter);
    }

    // ── Triple-dash in a quoted value ─────────────────────────────────────

    [Fact]
    public void Triple_dash_inside_value_does_not_terminate_frontmatter()
    {
        var content = "---\nname: \"value with --- inside\"\ndescription: fine\n---\nbody\n";
        var fm = SkillFrontmatterParser.Parse(content);

        Assert.True(fm.HasFrontmatter);
        Assert.Equal("value with --- inside", fm.Name);
        Assert.Equal("fine", fm.Description);
        Assert.Equal("body", fm.Body);
    }

    // ── Block/folded scalar markers ───────────────────────────────────────

    [Fact]
    public void Block_scalar_marker_pipe_does_not_crash_and_value_is_empty()
    {
        var fm = Parse("description: |\nname: foo");

        // Parser stores the block scalar marker line's value as empty.
        Assert.Equal(string.Empty, fm.Description);
        Assert.Equal("foo", fm.Name);
    }

    [Fact]
    public void Folded_scalar_marker_gt_does_not_crash_and_value_is_empty()
    {
        var fm = Parse("description: >\nname: foo");

        Assert.Equal(string.Empty, fm.Description);
        Assert.Equal("foo", fm.Name);
    }

    [Fact]
    public void Block_scalar_indented_content_skipped_without_crash()
    {
        var content = "---\ndescription: |\n  Line one.\n  Line two.\nname: after\n---\nbody\n";
        var fm = SkillFrontmatterParser.Parse(content);

        Assert.True(fm.HasFrontmatter);
        Assert.Equal(string.Empty, fm.Description); // marker yields empty
        Assert.Equal("after", fm.Name);
    }

    // ── Helper ────────────────────────────────────────────────────────────

    /// <summary>Wraps <paramref name="frontmatterBody"/> in triple-dash delimiters and parses.</summary>
    private static SkillFrontmatter Parse(string frontmatterBody) =>
        SkillFrontmatterParser.Parse($"---\n{frontmatterBody}\n---\n");
}
