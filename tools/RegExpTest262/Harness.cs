using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RecursiveAI.OneEcma.RegExp;

namespace RecursiveAI.OneEcma.RegExpTest262;

/// <summary>One test file's verdict.</summary>
public sealed record FileResult(string Path, bool Passed, string Detail);

/// <summary>
/// Runs the parts of test262 that exercise only the pattern grammar and matching, without an interpreter.
/// Two translators: (1) the generated property-escape and set-notation tests, whose arguments are literal data
/// (code point lists, string lists, regex literals), are evaluated against the engine the way the harness
/// functions testPropertyEscapes / testPropertyOfStrings evaluate them; (2) every regex literal of a test file is
/// extracted and validated, expecting an early error exactly when the file's metadata says it is a parse-phase negative.
/// </summary>
public static partial class Harness
{
    /// <summary>Runs the generated tests that build code point strings and test them with regex literals.</summary>
    public static IEnumerable<FileResult> RunGenerated(string test262Root, string relativeDirectory, string? filter = null)
    {
        string dir = Path.Combine(test262Root, "test", relativeDirectory);
        foreach (string file in Directory.EnumerateFiles(dir, "*.js", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (filter is not null && !file.Contains(filter, StringComparison.Ordinal))
            {
                continue;
            }

            string rel = Path.GetRelativePath(test262Root, file);
            string src = File.ReadAllText(file);
            FileResult r;
            try
            {
                r = IsParseNegative(src) ? CheckNegative(rel, src) : RunPositive(rel, src);
            }
            catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
                r = new FileResult(rel, false, "harness error: " + e.Message);
            }

            yield return r;
        }
    }

    /// <summary>Validates every regex literal of each file in the directory (recursively).</summary>
    public static IEnumerable<FileResult> RunLiterals(string test262Root, string relativeDirectory, string? filter = null)
    {
        string dir = Path.Combine(test262Root, "test", relativeDirectory);
        foreach (string file in Directory.EnumerateFiles(dir, "*.js", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (filter is not null && !file.Contains(filter, StringComparison.Ordinal))
            {
                continue;
            }

            string rel = Path.GetRelativePath(test262Root, file);
            string src = File.ReadAllText(file);
            yield return IsParseNegative(src) ? CheckNegative(rel, src) : CheckAllValid(rel, src);
        }
    }

    private static bool IsParseNegative(string src)
    {
        int end = src.IndexOf("---*/", StringComparison.Ordinal);
        string head = end < 0 ? "" : src[..end];
        return head.Contains("negative:", StringComparison.Ordinal) && head.Contains("phase: parse", StringComparison.Ordinal);
    }

    private static FileResult CheckNegative(string rel, string src)
    {
        var literals = ExtractLiterals(src);
        foreach (var (body, flags) in literals)
        {
            if (RegExpValidator.Validate(body, flags).Count > 0)
            {
                return new FileResult(rel, true, "");
            }
        }

        return new FileResult(rel, false, $"no regex literal was rejected ({literals.Count} literal(s))");
    }

    private static FileResult CheckAllValid(string rel, string src)
    {
        foreach (var (body, flags) in ExtractLiterals(src))
        {
            var d = RegExpValidator.Validate(body, flags);
            if (d.Count > 0)
            {
                return new FileResult(rel, false, $"/{body}/{flags}: {d[0].Reason}");
            }
        }

        return new FileResult(rel, true, "");
    }

    // ---- generated positive tests -------------------------------------------------------------------------

    private static FileResult RunPositive(string rel, string src)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in BuildStringRegex().Matches(src))
        {
            strings[m.Groups[1].Value] = BuildString(m.Groups[2].Value);
        }

        int checks = 0;
        if (src.Contains("const regexes = [standard,unicode,vflag]", StringComparison.Ordinal) && strings.TryGetValue("str", out string? str))
        {
            // CharacterClassEscapes tests: the same string against the plain, u and v forms; positive files expect a
            // match, negative files expect none.
            bool expectMatch = rel.Contains("positive", StringComparison.Ordinal);
            foreach (Match m in ClassEscapeLiteralRegex().Matches(src))
            {
                var (body, flags) = ReadLiteralAt(m.Groups[2].Value);
                var prog = RegExpProgram.Compile(body, flags).Program ?? throw new InvalidOperationException($"/{body}/{flags} does not compile");
                bool matched = prog.Exec(str, 0, sticky: false).Outcome == RegExpOutcome.Match;
                if (matched != expectMatch)
                {
                    return new FileResult(rel, false, $"/{body}/{flags}: expected {(expectMatch ? "a match" : "no match")}");
                }

                checks++;
            }
        }

        foreach (Match m in TestPropertyEscapesRegex().Matches(src))
        {
            var (body, flags) = ReadLiteralAt(m.Groups[1].Value);
            string input = strings[m.Groups[2].Value];
            var prog = RegExpProgram.Compile(body, flags).Program ?? throw new InvalidOperationException($"/{body}/{flags} does not compile");
            var r = prog.Exec(input, 0, sticky: false);
            if (r.Outcome != RegExpOutcome.Match)
            {
                return new FileResult(rel, false, $"/{body}/{flags} did not match {m.Groups[2].Value} ({r.Outcome}); first miss: {FirstMiss(prog, input)}");
            }

            checks++;
        }

        foreach (Match m in StringsCallRegex().Matches(src))
        {
            int open = m.Index + m.Length - 1;
            int close = FindMatchingBrace(src, open);
            if (!CheckStringsCall(src[open..(close + 1)], out string detail))
            {
                return new FileResult(rel, false, detail);
            }

            checks++;
        }

        return checks == 0 ? new FileResult(rel, false, "nothing to check (unrecognised test shape)") : new FileResult(rel, true, "");
    }

    private static bool CheckStringsCall(string call, out string detail)
    {
        detail = "";
        int re = call.IndexOf("regExp:", StringComparison.Ordinal);
        var (body, flags) = ReadLiteralAt(call[(re + 7)..].TrimStart());
        var prog = RegExpProgram.Compile(body, flags).Program;
        if (prog is null)
        {
            detail = $"/{body}/{flags} does not compile";
            return false;
        }

        List<string> match = ReadStringList(call, "matchStrings");
        List<string>? nonMatch = call.Contains("nonMatchStrings:", StringComparison.Ordinal) ? ReadStringList(call, "nonMatchStrings") : null;

        // Mirrors testPropertyOfStrings in harness/regExpUtils.js.
        bool Test(string s) => prog.Exec(s, 0, sticky: false).Outcome == RegExpOutcome.Match;
        if (!Test(string.Concat(match)))
        {
            foreach (string s in match)
            {
                if (!Test(s))
                {
                    detail = $"/{body}/{flags} should match U+{string.Join(" U+", s.EnumerateRunes().Select(r => r.Value.ToString("X6", CultureInfo.InvariantCulture)))}";
                    return false;
                }
            }
        }

        if (nonMatch is not null && Test(string.Concat(nonMatch)))
        {
            foreach (string s in nonMatch)
            {
                if (Test(s))
                {
                    detail = $"/{body}/{flags} should not match U+{string.Join(" U+", s.EnumerateRunes().Select(r => r.Value.ToString("X6", CultureInfo.InvariantCulture)))}";
                    return false;
                }
            }
        }

        return true;
    }

    private static string FirstMiss(RegExpProgram prog, string input)
    {
        // Same fallback as testPropertyEscapes: find the first symbol that does not match on its own.
        for (int i = 0; i < input.Length; i += char.IsHighSurrogate(input[i]) && i + 1 < input.Length ? 2 : 1)
        {
            string sym = char.IsHighSurrogate(input[i]) && i + 1 < input.Length ? input.Substring(i, 2) : input.Substring(i, 1);
            if (prog.Exec(sym, 0, sticky: false).Outcome != RegExpOutcome.Match)
            {
                return "U+" + char.ConvertToUtf32(sym, 0).ToString("X6", CultureInfo.InvariantCulture);
            }
        }

        return "none individually";
    }

    private static List<string> ReadStringList(string call, string name)
    {
        int key = call.IndexOf(name + ":", StringComparison.Ordinal);
        int open = call.IndexOf('[', key);
        var list = new List<string>();
        int i = open + 1;
        while (true)
        {
            while (call[i] is ' ' or '\n' or '\r' or ',' or '\t')
            {
                i++;
            }

            if (call[i] == ']')
            {
                return list;
            }

            var sb = new StringBuilder();
            char quote = call[i++];
            while (call[i] != quote)
            {
                if (call[i] == '\\')
                {
                    i++;
                    switch (call[i])
                    {
                        case 'u' when call[i + 1] == '{':
                        {
                            int end = call.IndexOf('}', i);
                            sb.Append(char.ConvertFromUtf32(int.Parse(call.AsSpan(i + 2, end - i - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                            i = end + 1;
                            continue;
                        }

                        case 'x':
                            sb.Append((char)int.Parse(call.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 3;
                            continue;
                        case 'u':
                            sb.Append((char)int.Parse(call.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 5;
                            continue;
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        default:
                            sb.Append(call[i]);
                            break;
                    }

                    i++;
                }
                else
                {
                    sb.Append(call[i++]);
                }
            }

            i++;
            list.Add(sb.ToString());
        }
    }

    private static int FindMatchingBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '"' or '\'')
            {
                i++;
                while (s[i] != c)
                {
                    if (s[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }
            }
            else if (c == '/' && i + 1 < s.Length && s[i - 1] is ' ' or ':' or '(' && s[i + 1] != '/' && s[i + 1] != '*')
            {
                var (_, _, next) = ReadLiteral(s, i);
                i = next - 1;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i;
            }
        }

        throw new FormatException("unbalanced braces");
    }

    private static string BuildString(string objectText)
    {
        var sb = new StringBuilder();
        int lone = objectText.IndexOf("loneCodePoints:", StringComparison.Ordinal);
        int ranges = objectText.IndexOf("ranges:", StringComparison.Ordinal);
        int loneEnd = objectText.IndexOf(']', lone);
        foreach (Match m in HexOrDecimalRegex().Matches(objectText[lone..loneEnd]))
        {
            sb.Append(char.ConvertFromUtf32(ParseNumber(m.Value)));
        }

        foreach (Match m in RangePairRegex().Matches(objectText[ranges..]))
        {
            int a = ParseNumber(m.Groups[1].Value);
            int b = ParseNumber(m.Groups[2].Value);
            for (int cp = a; cp <= b; cp++)
            {
                if (cp > 0xFFFF)
                {
                    sb.Append(char.ConvertFromUtf32(cp));
                }
                else
                {
                    // buildString uses String.fromCodePoint, which also yields lone surrogates.
                    sb.Append((char)cp);
                }
            }
        }

        return sb.ToString();
    }

    private static int ParseNumber(string s) => s.StartsWith("0x", StringComparison.Ordinal) ? int.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(s, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"const (\w+) = buildString\(\s*(\{.*?\})\s*\);", RegexOptions.Singleline)]
    private static partial Regex BuildStringRegex();

    [GeneratedRegex(@"testPropertyEscapes\(\s*(/.*?)\s*,\s*(\w+)\s*,\s*""", RegexOptions.Singleline)]
    private static partial Regex TestPropertyEscapesRegex();

    [GeneratedRegex(@"test(?:PropertyOfStrings|ExtendedCharacterClass)\(\{")]
    private static partial Regex StringsCallRegex();

    [GeneratedRegex(@"const (standard|unicode|vflag) = (/.*);")]
    private static partial Regex ClassEscapeLiteralRegex();

    [GeneratedRegex(@"0x[0-9A-Fa-f]+|\d+")]
    private static partial Regex HexOrDecimalRegex();

    [GeneratedRegex(@"\[\s*(0x[0-9A-Fa-f]+|\d+)\s*,\s*(0x[0-9A-Fa-f]+|\d+)\s*\]")]
    private static partial Regex RangePairRegex();

    // ---- regex literal extraction -------------------------------------------------------------------------

    private static (string Body, string Flags) ReadLiteralAt(string text)
    {
        var (body, flags, _) = ReadLiteral(text, 0);
        return (body, flags);
    }

    // Reads /body/flags starting at the slash; the body may contain '/' inside a class.
    private static (string Body, string Flags, int Next) ReadLiteral(string s, int start)
    {
        int i = start + 1;
        bool inClass = false;
        while (i < s.Length && s[i] != '\n')
        {
            char c = s[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '[')
            {
                inClass = true;
            }
            else if (c == ']')
            {
                inClass = false;
            }
            else if (c == '/' && !inClass)
            {
                break;
            }

            i++;
        }

        if (i >= s.Length || s[i] != '/')
        {
            throw new FormatException("unterminated regex literal");
        }

        string body = s.Substring(start + 1, i - start - 1);
        int f = i + 1;
        while (f < s.Length && (char.IsAsciiLetterOrDigit(s[f]) || s[f] == '_' || s[f] == '$'))
        {
            f++;
        }

        return (body, s.Substring(i + 1, f - i - 1), f);
    }

    /// <summary>Finds regex literals in a test file: skips comments, strings and templates, and decides regex-or-division from the previous token.</summary>
    internal static List<(string Body, string Flags)> ExtractLiterals(string src)
    {
        var result = new List<(string, string)>();
        int i = src.IndexOf("---*/", StringComparison.Ordinal);
        i = i < 0 ? 0 : i + 5;
        char prev = ';';
        string prevWord = "";
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? src.Length : end + 2;
                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                i++;
                while (i < src.Length && src[i] != c)
                {
                    i += src[i] == '\\' ? 2 : 1;
                }

                i++;
                prev = 'a';
                prevWord = "";
                continue;
            }

            if (c == '/')
            {
                bool regexAllowed = prev is ';' or '(' or ',' or '=' or ':' or '[' or '!' or '&' or '|' or '?' or '{' or '}' or '+' or '-' or '*' or '%' or '<' or '>' or '~' or '^'
                    || prevWord is "return" or "typeof" or "in" or "of" or "case" or "void" or "delete" or "throw" or "new";
                if (regexAllowed)
                {
                    try
                    {
                        var (body, flags, next) = ReadLiteral(src, i);
                        result.Add((body, flags));
                        i = next;
                    }
                    catch (FormatException)
                    {
                        // An unterminated literal is itself a syntax error; the invalid flag text makes the validator reject it.
                        result.Add((string.Empty, "!unterminated"));
                        return result;
                    }

                    prev = 'a';
                    prevWord = "";
                    continue;
                }
            }

            if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$')
            {
                int s = i;
                while (i < src.Length && (char.IsAsciiLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '$'))
                {
                    i++;
                }

                prevWord = src[s..i];
                prev = 'a';
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                prev = c;
                prevWord = "";
            }

            i++;
        }

        return result;
    }
}
