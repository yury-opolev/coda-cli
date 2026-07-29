using Coda.Tui.Skills;

namespace Coda.Tui.Tests;

public sealed class SkillArgumentBinderTests
{
    // ── No placeholders ───────────────────────────────────────────────────

    [Fact]
    public void Body_with_no_placeholders_is_returned_unchanged()
    {
        var result = SkillArgumentBinder.Bind("Just a plain body.", [], []);

        Assert.Equal("Just a plain body.", result);
    }

    [Fact]
    public void Empty_body_is_returned_unchanged()
    {
        var result = SkillArgumentBinder.Bind(string.Empty, [], []);

        Assert.Equal(string.Empty, result);
    }

    // ── $ARGUMENTS ────────────────────────────────────────────────────────

    [Fact]
    public void Dollar_ARGUMENTS_joins_all_values_with_space()
    {
        var result = SkillArgumentBinder.Bind("Do this: $ARGUMENTS", [], ["arg1", "arg2", "arg3"]);

        Assert.Equal("Do this: arg1 arg2 arg3", result);
    }

    [Fact]
    public void Dollar_ARGUMENTS_with_no_values_renders_empty()
    {
        var result = SkillArgumentBinder.Bind("Args: $ARGUMENTS.", [], []);

        Assert.Equal("Args: .", result);
    }

    [Fact]
    public void Dollar_ARGUMENTS_single_value()
    {
        var result = SkillArgumentBinder.Bind("$ARGUMENTS", [], ["hello"]);

        Assert.Equal("hello", result);
    }

    [Fact]
    public void Dollar_ARGUMENTS_is_case_sensitive_lowercase_not_special()
    {
        // $arguments is NOT the special $ARGUMENTS token; treated as named arg.
        var result = SkillArgumentBinder.Bind("$arguments", [], ["ignored"]);

        // "arguments" is not in argumentNames → rendered as empty
        Assert.Equal(string.Empty, result);
    }

    // ── Positional ($N) ───────────────────────────────────────────────────

    [Fact]
    public void Positional_one_substitutes_first_value()
    {
        var result = SkillArgumentBinder.Bind("Hello $1!", [], ["World"]);

        Assert.Equal("Hello World!", result);
    }

    [Fact]
    public void Positional_multiple_substituted_independently()
    {
        var result = SkillArgumentBinder.Bind("$1 and $2", [], ["foo", "bar"]);

        Assert.Equal("foo and bar", result);
    }

    [Fact]
    public void Positional_out_of_range_renders_empty()
    {
        var result = SkillArgumentBinder.Bind("$3", [], ["a", "b"]);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Positional_zero_renders_empty()
    {
        // $0 is not a valid 1-based index.
        var result = SkillArgumentBinder.Bind("$0", [], ["a"]);

        Assert.Equal(string.Empty, result);
    }

    // ── Named ($name) ─────────────────────────────────────────────────────

    [Fact]
    public void Named_argument_substituted_by_position_in_list()
    {
        var result = SkillArgumentBinder.Bind(
            "Review $filename for $lang.",
            ["filename", "lang"],
            ["main.cs", "C#"]);

        Assert.Equal("Review main.cs for C#.", result);
    }

    [Fact]
    public void Named_argument_missing_value_renders_empty()
    {
        var result = SkillArgumentBinder.Bind(
            "File: $filename",
            ["filename"],
            []);

        Assert.Equal("File: ", result);
    }

    [Fact]
    public void Unknown_named_identifier_renders_empty()
    {
        // $nonexistent is not in argumentNames → empty
        var result = SkillArgumentBinder.Bind("$nonexistent", ["other"], ["val"]);

        Assert.Equal(string.Empty, result);
    }

    // ── $$ escape ─────────────────────────────────────────────────────────

    [Fact]
    public void Double_dollar_produces_literal_dollar()
    {
        var result = SkillArgumentBinder.Bind("Cost: $$10", [], []);

        Assert.Equal("Cost: $10", result);
    }

    [Fact]
    public void Double_dollar_not_reinterpreted()
    {
        // $$ should produce a single $, not attempt to expand what follows.
        var result = SkillArgumentBinder.Bind("$$1", [], ["ignored"]);

        Assert.Equal("$1", result);
    }

    // ── No re-expansion ───────────────────────────────────────────────────

    [Fact]
    public void Substituted_value_containing_placeholder_is_not_re_expanded()
    {
        // Value "$2" should appear literally — not trigger a second substitution pass.
        var result = SkillArgumentBinder.Bind("$1", ["name"], ["$2"]);

        Assert.Equal("$2", result);
    }

    [Fact]
    public void Substituted_value_containing_ARGUMENTS_is_not_re_expanded()
    {
        var result = SkillArgumentBinder.Bind("$1", [], ["$ARGUMENTS"]);

        Assert.Equal("$ARGUMENTS", result);
    }

    // ── Longest-name-wins ─────────────────────────────────────────────────

    [Fact]
    public void Longest_name_wins_when_prefix_overlap()
    {
        // "file" is a prefix of "filename"; $filename should match "filename".
        var result = SkillArgumentBinder.Bind(
            "$filename",
            ["file", "filename"],
            ["short", "long"]);

        Assert.Equal("long", result);
    }

    [Fact]
    public void Shorter_name_matches_when_longer_does_not_start_at_position()
    {
        // $file should NOT match "filename" — it matches "file".
        var result = SkillArgumentBinder.Bind(
            "$file is here",
            ["file", "filename"],
            ["short", "long"]);

        Assert.Equal("short is here", result);
    }

    // ── Word boundary ─────────────────────────────────────────────────────

    [Fact]
    public void Named_arg_does_not_match_within_longer_identifier()
    {
        // $files: "file" is a declared arg, but "files" is not.
        // With word-boundary: "file" matches only if followed by non-ident char.
        // "$files" → "file" followed by "s" (ident char) → no match → empty.
        var result = SkillArgumentBinder.Bind("$files", ["file"], ["value"]);

        Assert.Equal(string.Empty, result);
    }

    // ── Mixed substitutions ───────────────────────────────────────────────

    [Fact]
    public void Multiple_kinds_of_substitution_in_one_body()
    {
        var result = SkillArgumentBinder.Bind(
            "All: $ARGUMENTS. First: $1. Named: $target.",
            ["target"],
            ["readme.md"]);

        Assert.Equal("All: readme.md. First: readme.md. Named: readme.md.", result);
    }
}
