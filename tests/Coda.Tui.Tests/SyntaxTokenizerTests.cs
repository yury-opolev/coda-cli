using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

/// <summary>
/// Pure unit tests for <see cref="SyntaxTokenizer"/> and <see cref="SyntaxLanguageDetector"/>.
/// No terminal setup required — all types are host-neutral and exercised in full isolation.
/// </summary>
public sealed class SyntaxTokenizerTests
{
    // Helper: tokenize a single line, returns that line's span list.
    private static IReadOnlyList<SyntaxCharSpan> TokenizeLine(
        string line, SyntaxLanguage lang = SyntaxLanguage.CSharp)
    {
        var result = SyntaxTokenizer.Tokenize([line], lang);
        return result[0];
    }

    // Guard: all spans must be sorted by StartChar and must never overlap.
    private static void AssertAscendingNonOverlapping(IReadOnlyList<SyntaxCharSpan> spans)
    {
        for (var i = 1; i < spans.Count; i++)
        {
            Assert.True(
                spans[i].StartChar >= spans[i - 1].EndChar,
                $"Span[{i}] start={spans[i].StartChar} overlaps or precedes span[{i - 1}] end={spans[i - 1].EndChar}");
        }
    }

    // -----------------------------------------------------------------------
    // Contract: result length, None language, empty input
    // -----------------------------------------------------------------------

    [Fact]
    public void Empty_input_returns_empty_list()
    {
        var result = SyntaxTokenizer.Tokenize([], SyntaxLanguage.CSharp);
        Assert.Empty(result);
    }

    [Fact]
    public void None_language_returns_one_empty_span_list_per_line()
    {
        var result = SyntaxTokenizer.Tokenize(["line one", "line two", "line three"], SyntaxLanguage.None);
        Assert.Equal(3, result.Count);
        Assert.All(result, spans => Assert.Empty(spans));
    }

    [Fact]
    public void None_language_with_empty_input_returns_empty_list()
    {
        var result = SyntaxTokenizer.Tokenize([], SyntaxLanguage.None);
        Assert.Empty(result);
    }

    [Fact]
    public void Result_length_always_equals_input_line_count()
    {
        var lines = new[] { "a", "b", "c", "d", "e" };
        var result = SyntaxTokenizer.Tokenize(lines, SyntaxLanguage.CSharp);
        Assert.Equal(lines.Length, result.Count);
    }

    // -----------------------------------------------------------------------
    // Whitespace
    // -----------------------------------------------------------------------

    [Fact]
    public void Whitespace_only_line_produces_no_spans()
    {
        Assert.Empty(TokenizeLine("   \t  "));
    }

    [Fact]
    public void Empty_line_produces_no_spans()
    {
        Assert.Empty(TokenizeLine(string.Empty));
    }

    // -----------------------------------------------------------------------
    // CSharp — keywords and types
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("if")]
    [InlineData("return")]
    [InlineData("class")]
    [InlineData("var")]
    [InlineData("async")]
    [InlineData("await")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    public void CSharp_keyword_is_highlighted_as_Keyword(string keyword)
    {
        var spans = TokenizeLine(keyword);
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(keyword.Length, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Keyword, span.Kind);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("string")]
    [InlineData("bool")]
    [InlineData("void")]
    [InlineData("Task")]
    [InlineData("List")]
    public void CSharp_type_name_is_highlighted_as_Type(string typeName)
    {
        var spans = TokenizeLine(typeName);
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(typeName.Length, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Type, span.Kind);
    }

    [Fact]
    public void Plain_identifier_produces_no_span()
    {
        // "x" is not a keyword or known type in any language.
        Assert.Empty(TokenizeLine("x"));
    }

    // -----------------------------------------------------------------------
    // CSharp — line comments
    // -----------------------------------------------------------------------

    [Fact]
    public void CSharp_line_comment_spans_to_end_of_line()
    {
        // "// comment" → one Comment span covering the full line.
        var spans = TokenizeLine("// comment");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(10, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Comment, span.Kind);
    }

    [Fact]
    public void CSharp_line_comment_after_code_starts_at_the_delimiter()
    {
        // "x; // note" — "x" is plain, so only the comment span is emitted.
        var spans = TokenizeLine("x; // note");
        var span = Assert.Single(spans);
        Assert.Equal(3, span.StartChar); // "// note" begins at char 3
        Assert.Equal(10, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Comment, span.Kind);
    }

    // -----------------------------------------------------------------------
    // CSharp — block comments
    // -----------------------------------------------------------------------

    [Fact]
    public void CSharp_inline_block_comment_is_a_Comment_span()
    {
        // "/* hi */" → one Comment span covering the whole thing.
        var spans = TokenizeLine("/* hi */");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(8, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Comment, span.Kind);
    }

    [Fact]
    public void CSharp_block_comment_spanning_three_lines_middle_line_is_entirely_Comment()
    {
        // Line 0: "/* start"   → Comment [0, 8)
        // Line 1: "middle"     → Comment [0, 6)  ← the mandatory assertion
        // Line 2: "end */"     → Comment [0, 6)
        var result = SyntaxTokenizer.Tokenize(["/* start", "middle", "end */"], SyntaxLanguage.CSharp);

        Assert.Equal(3, result.Count);

        var l0 = Assert.Single(result[0]);
        Assert.Equal(0, l0.StartChar);
        Assert.Equal(8, l0.EndChar);
        Assert.Equal(SyntaxTokenKind.Comment, l0.Kind);

        var l1 = Assert.Single(result[1]);
        Assert.Equal(0, l1.StartChar);
        Assert.Equal(6, l1.EndChar);
        Assert.Equal(SyntaxTokenKind.Comment, l1.Kind);

        var l2 = Assert.Single(result[2]);
        Assert.Equal(0, l2.StartChar);
        Assert.Equal(6, l2.EndChar);
        Assert.Equal(SyntaxTokenKind.Comment, l2.Kind);
    }

    [Fact]
    public void CSharp_after_block_comment_closes_scanning_continues_on_same_line()
    {
        // "/* c */ int" — Comment span then Type span for "int".
        var spans = TokenizeLine("/* c */ int");
        Assert.Equal(2, spans.Count);
        Assert.Equal(SyntaxTokenKind.Comment, spans[0].Kind);
        Assert.Equal(0, spans[0].StartChar);
        Assert.Equal(7, spans[0].EndChar);
        Assert.Equal(SyntaxTokenKind.Type, spans[1].Kind);
        Assert.Equal(8, spans[1].StartChar);
        Assert.Equal(11, spans[1].EndChar);
    }

    // -----------------------------------------------------------------------
    // CSharp — string literals
    // -----------------------------------------------------------------------

    [Fact]
    public void CSharp_double_quoted_string_produces_String_span()
    {
        // `"hello"` → String [0, 7)
        var spans = TokenizeLine("\"hello\"");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(7, span.EndChar);
        Assert.Equal(SyntaxTokenKind.String, span.Kind);
    }

    [Fact]
    public void CSharp_char_literal_produces_String_span()
    {
        // `'x'` → String [0, 3)
        var spans = TokenizeLine("'x'");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(3, span.EndChar);
        Assert.Equal(SyntaxTokenKind.String, span.Kind);
    }

    [Fact]
    public void CSharp_escaped_quote_inside_string_does_not_terminate_the_span()
    {
        // `"say \"hi\""` — the backslash-escaped inner quotes must not close the string early.
        // Outer quotes at 0 and 11; total length = 12.
        var line = "\"say \\\"hi\\\"\"";
        var spans = TokenizeLine(line);
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(line.Length, span.EndChar);
        Assert.Equal(SyntaxTokenKind.String, span.Kind);
    }

    [Fact]
    public void CSharp_unterminated_string_ends_at_end_of_line()
    {
        // `"hello` — no closing quote; the span reaches end of line, no carry.
        var spans = TokenizeLine("\"hello");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(6, span.EndChar);
        Assert.Equal(SyntaxTokenKind.String, span.Kind);
    }

    [Fact]
    public void CSharp_unterminated_string_does_not_bleed_onto_next_line()
    {
        // An unterminated regular string must NOT carry state to the next line.
        var result = SyntaxTokenizer.Tokenize(["\"open", "next line"], SyntaxLanguage.CSharp);
        // Line 1 should not be treated as a string continuation.
        var l1 = result[1];
        Assert.DoesNotContain(l1, s => s.Kind == SyntaxTokenKind.String);
    }

    [Fact]
    public void CSharp_at_prefixed_string_span_includes_the_at_sign()
    {
        // `@"hello"` → String [0, 8)  (@ is char 0, closing " is char 7)
        var spans = TokenizeLine("@\"hello\"");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(8, span.EndChar);
        Assert.Equal(SyntaxTokenKind.String, span.Kind);
    }

    [Fact]
    public void CSharp_dollar_prefixed_string_span_includes_the_dollar_sign()
    {
        // `$"hello"` → String [0, 8)
        var spans = TokenizeLine("$\"hello\"");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(8, span.EndChar);
        Assert.Equal(SyntaxTokenKind.String, span.Kind);
    }

    // -----------------------------------------------------------------------
    // Numbers
    // -----------------------------------------------------------------------

    [Fact]
    public void Number_plain_integer_is_highlighted()
    {
        var spans = TokenizeLine("42");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(2, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Number, span.Kind);
    }

    [Fact]
    public void Number_hex_prefix_0xFF_is_highlighted()
    {
        var spans = TokenizeLine("0xFF");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(4, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Number, span.Kind);
    }

    [Fact]
    public void Number_binary_prefix_0b1010_is_highlighted()
    {
        var spans = TokenizeLine("0b1010");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(6, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Number, span.Kind);
    }

    [Fact]
    public void Number_with_digit_separator_1_000_is_highlighted()
    {
        var spans = TokenizeLine("1_000");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(5, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Number, span.Kind);
    }

    [Fact]
    public void Number_floating_point_3_14_is_highlighted()
    {
        var spans = TokenizeLine("3.14");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(4, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Number, span.Kind);
    }

    [Fact]
    public void Number_with_exponent_1e10_is_highlighted()
    {
        var spans = TokenizeLine("1e10");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(4, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Number, span.Kind);
    }

    [Fact]
    public void Number_starting_with_dot_dot5_is_highlighted()
    {
        var spans = TokenizeLine(".5");
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(2, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Number, span.Kind);
    }

    // -----------------------------------------------------------------------
    // Mixed line — exact order, offsets, and kinds
    // -----------------------------------------------------------------------

    [Fact]
    public void Mixed_line_var_x_equals_string_comment_has_correct_spans_in_order()
    {
        // `var x = "hi"; // note`
        //  012345678901234567890
        //  var [0,3)  "hi" [8,12)  // note [14,21)
        var spans = TokenizeLine("var x = \"hi\"; // note");

        Assert.Equal(3, spans.Count);

        Assert.Equal(new SyntaxCharSpan(0, 3, SyntaxTokenKind.Keyword), spans[0]);   // var
        Assert.Equal(new SyntaxCharSpan(8, 12, SyntaxTokenKind.String), spans[1]);   // "hi"
        Assert.Equal(new SyntaxCharSpan(14, 21, SyntaxTokenKind.Comment), spans[2]); // // note

        AssertAscendingNonOverlapping(spans);
    }

    [Fact]
    public void Gnarly_mixed_line_spans_are_ascending_and_non_overlapping()
    {
        // `int x = 0xFF; string s = "hello";`
        //  int[0,3)  0xFF[8,12)  string[14,20)  "hello"[25,32)
        var line = "int x = 0xFF; string s = \"hello\";";
        var spans = TokenizeLine(line);

        // Verify by kind and position rather than hard-coding count (future keyword additions
        // must not break this test — only the ordering invariant matters here).
        AssertAscendingNonOverlapping(spans);

        // Spot-check that the types and number are present at the expected positions.
        Assert.Contains(spans, s => s.Kind == SyntaxTokenKind.Type && s.StartChar == 0 && s.EndChar == 3);
        Assert.Contains(spans, s => s.Kind == SyntaxTokenKind.Number && s.StartChar == 8 && s.EndChar == 12);
        Assert.Contains(spans, s => s.Kind == SyntaxTokenKind.Type && s.StartChar == 14 && s.EndChar == 20);
        Assert.Contains(spans, s => s.Kind == SyntaxTokenKind.String && s.StartChar == 25 && s.EndChar == 32);
    }

    // -----------------------------------------------------------------------
    // Python — triple-quoted strings carry across lines
    // -----------------------------------------------------------------------

    [Fact]
    public void Python_triple_quoted_string_carries_across_lines()
    {
        // Line 0: `x = """`    → String [4, 7), carry set
        // Line 1: `middle`     → String [0, 6), still in carry
        // Line 2: `"""`        → String [0, 3), carry cleared
        var result = SyntaxTokenizer.Tokenize(
            ["x = \"\"\"", "middle", "\"\"\""],
            SyntaxLanguage.Python);

        Assert.Equal(3, result.Count);

        var l0 = Assert.Single(result[0]);
        Assert.Equal(4, l0.StartChar);
        Assert.Equal(7, l0.EndChar);
        Assert.Equal(SyntaxTokenKind.String, l0.Kind);

        var l1 = Assert.Single(result[1]);
        Assert.Equal(0, l1.StartChar);
        Assert.Equal(6, l1.EndChar);
        Assert.Equal(SyntaxTokenKind.String, l1.Kind);

        var l2 = Assert.Single(result[2]);
        Assert.Equal(0, l2.StartChar);
        Assert.Equal(3, l2.EndChar);
        Assert.Equal(SyntaxTokenKind.String, l2.Kind);
    }

    [Fact]
    public void Python_single_quoted_triple_string_carries_across_lines()
    {
        // `'''` triple-single-quote variant.
        var result = SyntaxTokenizer.Tokenize(
            ["'''open", "body", "close'''"],
            SyntaxLanguage.Python);

        Assert.Equal(3, result.Count);

        // Line 0: String from 0 to 7 ("'''open" length = 7)
        var l0 = Assert.Single(result[0]);
        Assert.Equal(0, l0.StartChar);
        Assert.Equal(7, l0.EndChar);
        Assert.Equal(SyntaxTokenKind.String, l0.Kind);

        // Line 1: entirely String
        var l1 = Assert.Single(result[1]);
        Assert.Equal(0, l1.StartChar);
        Assert.Equal(4, l1.EndChar);
        Assert.Equal(SyntaxTokenKind.String, l1.Kind);

        // Line 2: String [0, 8) — "close'''" length 8
        var l2 = Assert.Single(result[2]);
        Assert.Equal(0, l2.StartChar);
        Assert.Equal(8, l2.EndChar);
        Assert.Equal(SyntaxTokenKind.String, l2.Kind);
    }

    // -----------------------------------------------------------------------
    // JSON — true / false / null as keywords
    // -----------------------------------------------------------------------

    [Fact]
    public void JSON_true_is_highlighted_as_Keyword()
    {
        var spans = TokenizeLine("true", SyntaxLanguage.Json);
        var span = Assert.Single(spans);
        Assert.Equal(new SyntaxCharSpan(0, 4, SyntaxTokenKind.Keyword), span);
    }

    [Fact]
    public void JSON_false_is_highlighted_as_Keyword()
    {
        var spans = TokenizeLine("false", SyntaxLanguage.Json);
        var span = Assert.Single(spans);
        Assert.Equal(new SyntaxCharSpan(0, 5, SyntaxTokenKind.Keyword), span);
    }

    [Fact]
    public void JSON_null_is_highlighted_as_Keyword()
    {
        var spans = TokenizeLine("null", SyntaxLanguage.Json);
        var span = Assert.Single(spans);
        Assert.Equal(new SyntaxCharSpan(0, 4, SyntaxTokenKind.Keyword), span);
    }

    [Fact]
    public void JSON_string_value_is_highlighted()
    {
        // `"hello"` in JSON context.
        var spans = TokenizeLine("\"hello\"", SyntaxLanguage.Json);
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(7, span.EndChar);
        Assert.Equal(SyntaxTokenKind.String, span.Kind);
    }

    // -----------------------------------------------------------------------
    // PowerShell — block comment <# #>
    // -----------------------------------------------------------------------

    [Fact]
    public void PowerShell_inline_block_comment_is_a_Comment_span()
    {
        // `<# note #>` → Comment [0, 10)
        var spans = TokenizeLine("<# note #>", SyntaxLanguage.PowerShell);
        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartChar);
        Assert.Equal(10, span.EndChar);
        Assert.Equal(SyntaxTokenKind.Comment, span.Kind);
    }

    [Fact]
    public void PowerShell_block_comment_spans_across_lines()
    {
        var result = SyntaxTokenizer.Tokenize(
            ["<# start", "body", "end #>"],
            SyntaxLanguage.PowerShell);

        Assert.Equal(3, result.Count);
        Assert.Equal(SyntaxTokenKind.Comment, Assert.Single(result[0]).Kind);
        Assert.Equal(SyntaxTokenKind.Comment, Assert.Single(result[1]).Kind);
        Assert.Equal(SyntaxTokenKind.Comment, Assert.Single(result[2]).Kind);
    }

    // -----------------------------------------------------------------------
    // SyntaxLanguageDetector.FromInfoString
    // -----------------------------------------------------------------------

    // SyntaxLanguage is internal; [Theory] method parameters must be at least as accessible as the
    // method itself, so we pass the numeric ordinal and cast inside the body.
    [Theory]
    [InlineData("csharp",     1)]  // CSharp
    [InlineData("cs",         1)]
    [InlineData("c#",         1)]
    [InlineData("typescript", 2)]  // TypeScript
    [InlineData("ts",         2)]
    [InlineData("tsx",        2)]
    [InlineData("javascript", 3)]  // JavaScript
    [InlineData("js",         3)]
    [InlineData("jsx",        3)]
    [InlineData("mjs",        3)]
    [InlineData("python",     4)]  // Python
    [InlineData("py",         4)]
    [InlineData("json",       5)]  // Json
    [InlineData("jsonc",      5)]
    [InlineData("bash",       6)]  // Shell
    [InlineData("sh",         6)]
    [InlineData("shell",      6)]
    [InlineData("zsh",        6)]
    [InlineData("powershell", 7)]  // PowerShell
    [InlineData("pwsh",       7)]
    [InlineData("ps1",        7)]
    public void FromInfoString_maps_all_aliases(string alias, int expectedOrdinal)
    {
        Assert.Equal((SyntaxLanguage)expectedOrdinal, SyntaxLanguageDetector.FromInfoString(alias));
    }

    [Fact]
    public void FromInfoString_is_case_insensitive()
    {
        Assert.Equal(SyntaxLanguage.CSharp, SyntaxLanguageDetector.FromInfoString("CSHARP"));
        Assert.Equal(SyntaxLanguage.Python, SyntaxLanguageDetector.FromInfoString("PYTHON"));
    }

    [Fact]
    public void FromInfoString_takes_only_the_first_token_so_python_3_maps_to_Python()
    {
        Assert.Equal(SyntaxLanguage.Python, SyntaxLanguageDetector.FromInfoString("python 3"));
    }

    [Fact]
    public void FromInfoString_trims_whitespace_before_splitting()
    {
        Assert.Equal(SyntaxLanguage.TypeScript, SyntaxLanguageDetector.FromInfoString("  ts  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown-lang")]
    public void FromInfoString_returns_None_for_null_empty_whitespace_and_unknown(string? info)
    {
        Assert.Equal(SyntaxLanguage.None, SyntaxLanguageDetector.FromInfoString(info));
    }

    // -----------------------------------------------------------------------
    // SyntaxLanguageDetector.FromFilePath
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(".cs",          1)]  // CSharp
    [InlineData(".csx",         1)]
    [InlineData(".ts",          2)]  // TypeScript
    [InlineData(".tsx",         2)]
    [InlineData(".mts",         2)]
    [InlineData(".js",          3)]  // JavaScript
    [InlineData(".jsx",         3)]
    [InlineData(".mjs",         3)]
    [InlineData(".cjs",         3)]
    [InlineData(".py",          4)]  // Python
    [InlineData(".pyi",         4)]
    [InlineData(".json",        5)]  // Json
    [InlineData(".sh",          6)]  // Shell
    [InlineData(".bash",        6)]
    [InlineData(".zsh",         6)]
    [InlineData(".ps1",         7)]  // PowerShell
    [InlineData(".psm1",        7)]
    public void FromFilePath_maps_extension_with_leading_dot(string ext, int expectedOrdinal)
    {
        Assert.Equal((SyntaxLanguage)expectedOrdinal, SyntaxLanguageDetector.FromFilePath(ext));
    }

    [Theory]
    [InlineData("cs",   1)]  // CSharp
    [InlineData("ts",   2)]  // TypeScript
    [InlineData("py",   4)]  // Python
    [InlineData("json", 5)]  // Json
    [InlineData("sh",   6)]  // Shell
    [InlineData("ps1",  7)]  // PowerShell
    public void FromFilePath_maps_bare_extension_without_leading_dot(string ext, int expectedOrdinal)
    {
        Assert.Equal((SyntaxLanguage)expectedOrdinal, SyntaxLanguageDetector.FromFilePath(ext));
    }

    [Theory]
    [InlineData("src/App.tsx",           2)]  // TypeScript
    [InlineData("src/main.py",           4)]  // Python
    [InlineData("scripts/build.ps1",     7)]  // PowerShell
    [InlineData("lib/util.js",           3)]  // JavaScript
    [InlineData("config.json",           5)]  // Json
    public void FromFilePath_maps_forward_slash_paths(string path, int expectedOrdinal)
    {
        Assert.Equal((SyntaxLanguage)expectedOrdinal, SyntaxLanguageDetector.FromFilePath(path));
    }

    [Fact]
    public void FromFilePath_handles_windows_backslash_paths()
    {
        Assert.Equal(SyntaxLanguage.TypeScript, SyntaxLanguageDetector.FromFilePath(@"src\components\App.tsx"));
        Assert.Equal(SyntaxLanguage.CSharp, SyntaxLanguageDetector.FromFilePath(@"src\Foo\Bar.cs"));
    }

    [Fact]
    public void FromFilePath_is_case_insensitive()
    {
        Assert.Equal(SyntaxLanguage.CSharp, SyntaxLanguageDetector.FromFilePath(".CS"));
        Assert.Equal(SyntaxLanguage.Python, SyntaxLanguageDetector.FromFilePath("PY"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file.unknown")]
    [InlineData("noextension")]
    public void FromFilePath_returns_None_for_null_empty_whitespace_and_unknown(string? path)
    {
        Assert.Equal(SyntaxLanguage.None, SyntaxLanguageDetector.FromFilePath(path));
    }
}
