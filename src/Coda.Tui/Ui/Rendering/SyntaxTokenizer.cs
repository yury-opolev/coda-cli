using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Coda.Tui.Ui.Rendering;

/// <summary>Semantic token kinds the transcript can colour.</summary>
public enum SyntaxTokenKind : byte
{
    /// <summary>No highlight; caller keeps the row's own colour.</summary>
    Plain,
    /// <summary>A language keyword such as <c>if</c>, <c>return</c>, or <c>class</c>.</summary>
    Keyword,
    /// <summary>A built-in or well-known type name such as <c>int</c> or <c>string</c>.</summary>
    Type,
    /// <summary>A string literal, including its delimiters and any prefix character.</summary>
    String,
    /// <summary>A numeric literal in decimal, hex, binary, or floating-point form.</summary>
    Number,
    /// <summary>A line or block comment, from its opening delimiter to the end of the spanned text.</summary>
    Comment,
}

/// <summary>
/// A tokenised range on one logical source line, in char offsets (not terminal cells).
/// <see cref="EndChar"/> is exclusive, matching the .NET slice convention.
/// </summary>
/// <param name="StartChar">Inclusive start of the token.</param>
/// <param name="EndChar">Exclusive end of the token.</param>
/// <param name="Kind">The semantic role of the token.</param>
internal readonly record struct SyntaxCharSpan(int StartChar, int EndChar, SyntaxTokenKind Kind);

/// <summary>The languages the tokenizer understands.</summary>
internal enum SyntaxLanguage
{
    /// <summary>Unknown language; the tokenizer emits no spans.</summary>
    None,
    /// <summary>C# source code.</summary>
    CSharp,
    /// <summary>TypeScript source code.</summary>
    TypeScript,
    /// <summary>JavaScript source code.</summary>
    JavaScript,
    /// <summary>Python source code.</summary>
    Python,
    /// <summary>JSON (or JSONC) data.</summary>
    Json,
    /// <summary>POSIX shell or Bash script.</summary>
    Shell,
    /// <summary>PowerShell script.</summary>
    PowerShell,
}

/// <summary>
/// A purely declarative description of one language's lexical structure. The scanner consults this
/// record without any per-language branching — all specialisation lives in the data, not the algorithm.
/// </summary>
internal sealed record SyntaxLanguageDefinition(
    FrozenSet<string> Keywords,
    FrozenSet<string> Types,
    string? LineComment,
    (string Open, string Close)? BlockComment,
    IReadOnlyList<(string Open, string Close, bool IsMultiLine)> StringDelimiters
);

/// <summary>
/// Tokenises source lines into semantic spans suitable for syntax-coloured rendering.
/// Only non-Plain spans are emitted; runs of plain text produce no span. The class is intentionally
/// host-neutral so tests need no terminal setup.
/// </summary>
internal static class SyntaxTokenizer
{
    private enum CarryKind : byte { None, BlockComment, MultiLineString }
    private readonly record struct CarryState(CarryKind Kind, string? CloseDelimiter);

    private static readonly FrozenDictionary<SyntaxLanguage, SyntaxLanguageDefinition> Definitions =
        new Dictionary<SyntaxLanguage, SyntaxLanguageDefinition>
        {
            [SyntaxLanguage.CSharp] = new(
                Keywords: new[]
                {
                    "public", "private", "protected", "internal", "class", "struct", "interface",
                    "enum", "record", "var", "if", "else", "for", "foreach", "while", "return",
                    "new", "using", "namespace", "async", "await", "static", "readonly", "const",
                    "override", "virtual", "abstract", "sealed", "try", "catch", "finally", "throw",
                    "switch", "case", "default", "break", "continue", "this", "base", "null", "true",
                    "false", "is", "as", "in", "out", "ref", "params", "get", "set", "yield", "lock",
                    "typeof", "nameof", "when", "where", "select", "from",
                }.ToFrozenSet(StringComparer.Ordinal),
                Types: new[]
                {
                    "void", "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int",
                    "uint", "long", "ulong", "short", "ushort", "object", "string", "dynamic",
                    "nint", "nuint", "Task", "List", "Dictionary", "IEnumerable", "IReadOnlyList",
                    "Span", "ReadOnlySpan",
                }.ToFrozenSet(StringComparer.Ordinal),
                LineComment: "//",
                BlockComment: ("/*", "*/"),
                StringDelimiters:
                [
                    // Longer prefixes must precede shorter ones so the first-match rule is correct.
                    ("$@\"", "\"", false),
                    ("@$\"", "\"", false),
                    ("@\"",  "\"", false),
                    ("$\"",  "\"", false),
                    ("\"",   "\"", false),
                    ("'",    "'",  false),
                ]
            ),
            [SyntaxLanguage.TypeScript] = new(
                Keywords: new[]
                {
                    "abstract", "as", "async", "await", "break", "case", "catch", "class", "const",
                    "continue", "debugger", "declare", "default", "delete", "do", "else", "enum",
                    "export", "extends", "false", "finally", "for", "from", "function", "if",
                    "implements", "import", "in", "instanceof", "interface", "let", "namespace",
                    "new", "null", "of", "override", "package", "private", "protected", "public",
                    "readonly", "return", "static", "super", "switch", "this", "throw", "true",
                    "try", "type", "typeof", "undefined", "var", "void", "while", "yield",
                }.ToFrozenSet(StringComparer.Ordinal),
                Types: new[]
                {
                    "string", "number", "boolean", "object", "symbol", "bigint", "any", "unknown",
                    "never", "void", "null", "undefined", "Array", "Map", "Set", "Promise",
                    "Function", "Date", "RegExp", "Error",
                }.ToFrozenSet(StringComparer.Ordinal),
                LineComment: "//",
                BlockComment: ("/*", "*/"),
                StringDelimiters:
                [
                    ("\"", "\"", false),
                    ("'",  "'",  false),
                    ("`",  "`",  false),
                ]
            ),
            [SyntaxLanguage.JavaScript] = new(
                Keywords: new[]
                {
                    "abstract", "async", "await", "break", "case", "catch", "class", "const",
                    "continue", "debugger", "default", "delete", "do", "else", "enum", "export",
                    "extends", "false", "finally", "for", "from", "function", "if", "import",
                    "in", "instanceof", "let", "new", "null", "of", "package", "private",
                    "protected", "public", "return", "static", "super", "switch", "this", "throw",
                    "true", "try", "typeof", "undefined", "var", "void", "while", "yield",
                }.ToFrozenSet(StringComparer.Ordinal),
                Types: new[]
                {
                    "Array", "Map", "Set", "Promise", "Function", "Date", "RegExp", "Error",
                    "Object", "Number", "String", "Boolean", "Symbol", "BigInt",
                }.ToFrozenSet(StringComparer.Ordinal),
                LineComment: "//",
                BlockComment: ("/*", "*/"),
                StringDelimiters:
                [
                    ("\"", "\"", false),
                    ("'",  "'",  false),
                    ("`",  "`",  false),
                ]
            ),
            [SyntaxLanguage.Python] = new(
                Keywords: new[]
                {
                    "False", "None", "True", "and", "as", "assert", "async", "await", "break",
                    "class", "continue", "def", "del", "elif", "else", "except", "finally", "for",
                    "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not",
                    "or", "pass", "raise", "return", "try", "while", "with", "yield",
                }.ToFrozenSet(StringComparer.Ordinal),
                Types: new[]
                {
                    "int", "float", "str", "bool", "bytes", "list", "tuple", "dict", "set",
                    "frozenset", "type", "object", "complex", "bytearray", "range",
                }.ToFrozenSet(StringComparer.Ordinal),
                LineComment: "#",
                BlockComment: null,
                StringDelimiters:
                [
                    // Triple-quoted forms must precede their single-char equivalents.
                    ("\"\"\"", "\"\"\"", true),
                    ("'''",    "'''",    true),
                    ("\"",     "\"",     false),
                    ("'",      "'",      false),
                ]
            ),
            [SyntaxLanguage.Json] = new(
                Keywords: new[] { "true", "false", "null" }
                    .ToFrozenSet(StringComparer.Ordinal),
                Types: FrozenSet<string>.Empty,
                LineComment: null,
                BlockComment: null,
                StringDelimiters:
                [
                    ("\"", "\"", false),
                ]
            ),
            [SyntaxLanguage.Shell] = new(
                Keywords: new[]
                {
                    "if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case",
                    "esac", "in", "function", "return", "local", "export", "echo", "exit",
                    "break", "continue",
                }.ToFrozenSet(StringComparer.Ordinal),
                Types: FrozenSet<string>.Empty,
                LineComment: "#",
                BlockComment: null,
                StringDelimiters:
                [
                    ("\"", "\"", false),
                    ("'",  "'",  false),
                ]
            ),
            [SyntaxLanguage.PowerShell] = new(
                Keywords: new[]
                {
                    "if", "else", "elseif", "while", "do", "for", "foreach", "switch", "break",
                    "continue", "return", "function", "filter", "class", "enum", "trap", "throw",
                    "try", "catch", "finally", "begin", "process", "end", "param", "using", "exit",
                }.ToFrozenSet(StringComparer.Ordinal),
                Types: new[]
                {
                    "string", "int", "bool", "char", "byte", "decimal", "double", "float",
                    "long", "short", "object", "array", "hashtable",
                }.ToFrozenSet(StringComparer.Ordinal),
                LineComment: "#",
                BlockComment: ("<#", "#>"),
                StringDelimiters:
                [
                    ("\"", "\"", false),
                    ("'",  "'",  false),
                ]
            ),
        }.ToFrozenDictionary();

    /// <summary>
    /// Tokenises a run of contiguous source lines, carrying multi-line state (block comments,
    /// triple-quoted strings) from one line to the next. Returns one span list per input line,
    /// always the same length as <paramref name="lines"/>. Callers that render non-contiguous
    /// source must call this once per contiguous run so unterminated constructs cannot bleed across gaps.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<SyntaxCharSpan>> Tokenize(
        IReadOnlyList<string> lines, SyntaxLanguage language)
    {
        if (lines.Count == 0)
            return [];

        if (!Definitions.TryGetValue(language, out var def))
        {
            // None or any unknown language — return empty span lists without scanning.
            var empty = new IReadOnlyList<SyntaxCharSpan>[lines.Count];
            for (var i = 0; i < lines.Count; i++)
                empty[i] = [];
            return empty;
        }

        var result = new IReadOnlyList<SyntaxCharSpan>[lines.Count];
        var carry = new CarryState(CarryKind.None, null);
        for (var i = 0; i < lines.Count; i++)
            result[i] = ScanLine(lines[i], def, ref carry);
        return result;
    }

    /// <summary>
    /// Scans one line given the incoming carry state (which may indicate we are inside a block
    /// comment or multi-line string from a previous line) and updates the carry for the next line.
    /// </summary>
    private static IReadOnlyList<SyntaxCharSpan> ScanLine(
        string line,
        SyntaxLanguageDefinition def,
        ref CarryState carry)
    {
        var spans = new List<SyntaxCharSpan>();
        var pos = 0;

        // Resume a block comment that started on a previous line.
        if (carry.Kind == CarryKind.BlockComment)
        {
            var closeIdx = line.IndexOf(carry.CloseDelimiter!, StringComparison.Ordinal);
            if (closeIdx >= 0)
            {
                var end = closeIdx + carry.CloseDelimiter!.Length;
                spans.Add(new SyntaxCharSpan(0, end, SyntaxTokenKind.Comment));
                carry = default;
                pos = end;
            }
            else
            {
                if (line.Length > 0)
                    spans.Add(new SyntaxCharSpan(0, line.Length, SyntaxTokenKind.Comment));
                return spans;
            }
        }
        // Resume a multi-line string (e.g. Python triple-quote) from a previous line.
        else if (carry.Kind == CarryKind.MultiLineString)
        {
            var closeIdx = FindUnescapedClose(line, carry.CloseDelimiter!, 0);
            if (closeIdx >= 0)
            {
                var end = closeIdx + carry.CloseDelimiter!.Length;
                spans.Add(new SyntaxCharSpan(0, end, SyntaxTokenKind.String));
                carry = default;
                pos = end;
            }
            else
            {
                if (line.Length > 0)
                    spans.Add(new SyntaxCharSpan(0, line.Length, SyntaxTokenKind.String));
                return spans;
            }
        }

        while (pos < line.Length)
        {
            var ch = line[pos];

            // Rule 1: Whitespace — advance without emitting.
            if (char.IsWhiteSpace(ch))
            {
                pos++;
                continue;
            }

            // Rule 2: Line comment — consume the rest of the line as one Comment span.
            if (def.LineComment is not null
                && line.AsSpan(pos).StartsWith(def.LineComment.AsSpan(), StringComparison.Ordinal))
            {
                spans.Add(new SyntaxCharSpan(pos, line.Length, SyntaxTokenKind.Comment));
                pos = line.Length;
                continue;
            }

            // Rule 3: Block comment open — consume to the close or end of line, setting carry if needed.
            if (def.BlockComment is { } bc
                && line.AsSpan(pos).StartsWith(bc.Open.AsSpan(), StringComparison.Ordinal))
            {
                var searchFrom = pos + bc.Open.Length;
                var closeIdx = line.IndexOf(bc.Close, searchFrom, StringComparison.Ordinal);
                if (closeIdx >= 0)
                {
                    var end = closeIdx + bc.Close.Length;
                    spans.Add(new SyntaxCharSpan(pos, end, SyntaxTokenKind.Comment));
                    pos = end;
                }
                else
                {
                    spans.Add(new SyntaxCharSpan(pos, line.Length, SyntaxTokenKind.Comment));
                    carry = new CarryState(CarryKind.BlockComment, bc.Close);
                    return spans;
                }
                continue;
            }

            // Rule 4: String delimiter — first match in the definition's ordered list wins.
            // Longer prefixes (e.g. `"""`) appear before shorter ones (`"`) to ensure greedy matching.
            var matchedString = false;
            foreach (var (open, close, isMultiLine) in def.StringDelimiters)
            {
                if (!line.AsSpan(pos).StartsWith(open.AsSpan(), StringComparison.Ordinal))
                    continue;

                var searchFrom = pos + open.Length;
                var closeIdx = FindUnescapedClose(line, close, searchFrom);
                if (closeIdx >= 0)
                {
                    var end = closeIdx + close.Length;
                    spans.Add(new SyntaxCharSpan(pos, end, SyntaxTokenKind.String));
                    pos = end;
                }
                else
                {
                    // Unterminated: span reaches end of line; only multi-line forms set carry.
                    spans.Add(new SyntaxCharSpan(pos, line.Length, SyntaxTokenKind.String));
                    pos = line.Length;
                    if (isMultiLine)
                        carry = new CarryState(CarryKind.MultiLineString, close);
                }
                matchedString = true;
                break;
            }
            if (matchedString)
                continue;

            // Rule 5: Number — leading digit, or '.' followed by a digit.
            if (char.IsAsciiDigit(ch)
                || (ch == '.' && pos + 1 < line.Length && char.IsAsciiDigit(line[pos + 1])))
            {
                var start = pos;
                pos = ScanNumber(line, pos);
                spans.Add(new SyntaxCharSpan(start, pos, SyntaxTokenKind.Number));
                continue;
            }

            // Rule 6: Identifier — emit Keyword or Type if recognised; otherwise Plain (no span).
            if (IsIdentStart(ch))
            {
                var start = pos;
                pos = ScanIdentifier(line, pos);
                var word = line[start..pos];
                if (def.Keywords.Contains(word))
                    spans.Add(new SyntaxCharSpan(start, pos, SyntaxTokenKind.Keyword));
                else if (def.Types.Contains(word))
                    spans.Add(new SyntaxCharSpan(start, pos, SyntaxTokenKind.Type));
                continue;
            }

            // Rule 7: Anything else — advance one char without emitting.
            pos++;
        }

        return spans;
    }

    /// <summary>
    /// Scans forward from <paramref name="from"/> for the first occurrence of
    /// <paramref name="closeDelimiter"/> that is not preceded by an odd number of backslashes,
    /// returning the index of the first char of the delimiter or -1 if not found.
    /// </summary>
    private static int FindUnescapedClose(string line, string closeDelimiter, int from)
    {
        var pos = from;
        while (pos < line.Length)
        {
            if (line[pos] == '\\')
            {
                pos += 2; // skip the escaped char
                continue;
            }
            if (line.AsSpan(pos).StartsWith(closeDelimiter.AsSpan(), StringComparison.Ordinal))
                return pos;
            pos++;
        }
        return -1;
    }

    /// <summary>
    /// Scans a numeric literal from <paramref name="pos"/>, returning the exclusive end position.
    /// Handles <c>0x</c>/<c>0b</c> prefixes, digit separators (<c>_</c>), decimal points, and
    /// trailing exponents (<c>e</c>/<c>E</c> with optional sign).
    /// </summary>
    private static int ScanNumber(string line, int pos)
    {
        // 0x hex or 0b binary prefix.
        if (line[pos] == '0' && pos + 1 < line.Length)
        {
            var next = line[pos + 1];
            if (next is 'x' or 'X')
            {
                pos += 2;
                while (pos < line.Length && (IsHexDigit(line[pos]) || line[pos] == '_'))
                    pos++;
                return pos;
            }
            if (next is 'b' or 'B')
            {
                pos += 2;
                while (pos < line.Length && (line[pos] is '0' or '1' || line[pos] == '_'))
                    pos++;
                return pos;
            }
        }

        var hasDot = line[pos] == '.';
        pos++;

        // Integer digits with optional separators.
        while (pos < line.Length && (char.IsAsciiDigit(line[pos]) || line[pos] == '_'))
            pos++;

        // Optional decimal point (only for numbers that started with a digit, not '.').
        if (!hasDot && pos < line.Length && line[pos] == '.')
        {
            pos++;
            while (pos < line.Length && (char.IsAsciiDigit(line[pos]) || line[pos] == '_'))
                pos++;
        }

        // Optional exponent.
        if (pos < line.Length && line[pos] is 'e' or 'E')
        {
            pos++;
            if (pos < line.Length && line[pos] is '+' or '-')
                pos++;
            while (pos < line.Length && char.IsAsciiDigit(line[pos]))
                pos++;
        }

        return pos;
    }

    private static bool IsHexDigit(char c) =>
        char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '$';

    /// <summary>
    /// Advances past an identifier beginning at <paramref name="pos"/> (which must already be a
    /// valid identifier-start character) and returns the exclusive end position.
    /// </summary>
    private static int ScanIdentifier(string line, int pos)
    {
        pos++;
        while (pos < line.Length
               && (char.IsLetterOrDigit(line[pos]) || line[pos] == '_' || line[pos] == '$'))
            pos++;
        return pos;
    }
}

/// <summary>
/// Maps human-readable language names and file extensions to <see cref="SyntaxLanguage"/> values.
/// Both methods tolerate null/empty/whitespace input and return <see cref="SyntaxLanguage.None"/>
/// for anything unrecognised.
/// </summary>
internal static class SyntaxLanguageDetector
{
    private static readonly FrozenDictionary<string, SyntaxLanguage> InfoStringMap =
        new Dictionary<string, SyntaxLanguage>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"]     = SyntaxLanguage.CSharp,
            ["cs"]         = SyntaxLanguage.CSharp,
            ["c#"]         = SyntaxLanguage.CSharp,
            ["typescript"] = SyntaxLanguage.TypeScript,
            ["ts"]         = SyntaxLanguage.TypeScript,
            ["tsx"]        = SyntaxLanguage.TypeScript,
            ["javascript"] = SyntaxLanguage.JavaScript,
            ["js"]         = SyntaxLanguage.JavaScript,
            ["jsx"]        = SyntaxLanguage.JavaScript,
            ["mjs"]        = SyntaxLanguage.JavaScript,
            ["python"]     = SyntaxLanguage.Python,
            ["py"]         = SyntaxLanguage.Python,
            ["json"]       = SyntaxLanguage.Json,
            ["jsonc"]      = SyntaxLanguage.Json,
            ["bash"]       = SyntaxLanguage.Shell,
            ["sh"]         = SyntaxLanguage.Shell,
            ["shell"]      = SyntaxLanguage.Shell,
            ["zsh"]        = SyntaxLanguage.Shell,
            ["powershell"] = SyntaxLanguage.PowerShell,
            ["pwsh"]       = SyntaxLanguage.PowerShell,
            ["ps1"]        = SyntaxLanguage.PowerShell,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, SyntaxLanguage> ExtensionMap =
        new Dictionary<string, SyntaxLanguage>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"]   = SyntaxLanguage.CSharp,
            [".csx"]  = SyntaxLanguage.CSharp,
            [".ts"]   = SyntaxLanguage.TypeScript,
            [".tsx"]  = SyntaxLanguage.TypeScript,
            [".mts"]  = SyntaxLanguage.TypeScript,
            [".js"]   = SyntaxLanguage.JavaScript,
            [".jsx"]  = SyntaxLanguage.JavaScript,
            [".mjs"]  = SyntaxLanguage.JavaScript,
            [".cjs"]  = SyntaxLanguage.JavaScript,
            [".py"]   = SyntaxLanguage.Python,
            [".pyi"]  = SyntaxLanguage.Python,
            [".json"] = SyntaxLanguage.Json,
            [".sh"]   = SyntaxLanguage.Shell,
            [".bash"] = SyntaxLanguage.Shell,
            [".zsh"]  = SyntaxLanguage.Shell,
            [".ps1"]  = SyntaxLanguage.PowerShell,
            [".psm1"] = SyntaxLanguage.PowerShell,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps a fenced-code-block info string ("csharp", "ts", "python 3") to a language. Only the
    /// first whitespace-separated token is examined, so "python 3" resolves as "python".
    /// </summary>
    public static SyntaxLanguage FromInfoString(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
            return SyntaxLanguage.None;

        var trimmed = info.Trim();
        var spaceIdx = trimmed.IndexOfAny([' ', '\t', '\n', '\r']);
        var token = spaceIdx >= 0 ? trimmed[..spaceIdx] : trimmed;

        return InfoStringMap.TryGetValue(token, out var lang) ? lang : SyntaxLanguage.None;
    }

    /// <summary>
    /// Maps a file path or bare extension ("src/App.tsx", ".cs", "cs") to a language. Both slash
    /// directions are accepted; a bare token with no dot is treated as an extension and matched
    /// case-insensitively.
    /// </summary>
    public static SyntaxLanguage FromFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return SyntaxLanguage.None;

        path = path.Trim().Replace('\\', '/');

        // Isolate the filename from any directory components.
        var lastSlash = path.LastIndexOf('/');
        var filename = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;

        if (string.IsNullOrEmpty(filename))
            return SyntaxLanguage.None;

        // Extract extension: if no dot is present, treat the whole token as a bare extension.
        var lastDot = filename.LastIndexOf('.');
        var ext = lastDot >= 0 ? filename[lastDot..] : "." + filename;

        return ExtensionMap.TryGetValue(ext, out var lang) ? lang : SyntaxLanguage.None;
    }
}
