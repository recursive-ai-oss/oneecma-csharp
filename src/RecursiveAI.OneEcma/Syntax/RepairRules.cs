using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

/// <summary>A proposed repair for the first error, in the coordinates of the text being repaired.</summary>
internal sealed class RepairCandidate
{
    internal RepairCandidate(string id, string title, string label, Span primary, List<TextEdit> edits, bool alternative = false)
    {
        Id = id;
        Title = title;
        Label = label;
        Primary = primary;
        Edits = edits;
        Alternative = alternative;
    }

    internal string Id { get; }

    internal string Title { get; }

    internal string Label { get; }

    internal Span Primary { get; }

    /// <summary>Ascending and not overlapping.</summary>
    internal List<TextEdit> Edits { get; }

    /// <summary>
    /// A rival fix that is never applied; it exists so that a rule can tell when its own fix is not the only plausible one
    /// (the table's uniqueness guard).
    /// </summary>
    internal bool Alternative { get; }
}

/// <summary>What a rule may look at: the text, its tokens, bracket pairing and the first error.</summary>
internal sealed class RepairContext
{
    internal RepairContext(string text, SourceText source, List<Diagnostic> errors)
    {
        Text = text;
        Source = source;
        Tokens = TokenScan.Of(source);
        Match = TokenScan.MatchBrackets(Tokens);
        Errors = errors;
        First = errors[0];
        FirstIndex = TokenScan.IndexAtOrAfter(Tokens, First.ParseOffset);
        int line = source.GetPosition(First.ParseOffset).Line;
        WindowStart = source.GetLineStart(Math.Max(1, line - 1));
        WindowEnd = source.GetLineStart(line) + source.GetLineText(line).Length;
    }

    internal string Text { get; }

    internal SourceText Source { get; }

    internal List<Token> Tokens { get; }

    internal int[] Match { get; }

    internal List<Diagnostic> Errors { get; }

    internal Diagnostic First { get; }

    /// <summary>Index of the token the parser stopped at.</summary>
    internal int FirstIndex { get; }

    /// <summary>The previous and the current line of the error: where a rule may look for its site.</summary>
    internal int WindowStart { get; }

    internal int WindowEnd { get; }

    internal bool InWindow(int offset) => offset >= WindowStart && offset <= WindowEnd;

    internal Token At(int i) => Tokens[Math.Clamp(i, 0, Tokens.Count - 1)];

    /// <summary>Index of the innermost opener that is still open just before token <paramref name="i"/>, or -1.</summary>
    internal int Enclosing(int i)
    {
        int depth = 0;
        for (int j = i - 1; j >= 0; j--)
        {
            switch (Tokens[j].Type)
            {
                case TokenType.RParen or TokenType.RBracket or TokenType.RBrace:
                    depth++;
                    break;
                case TokenType.LParen or TokenType.LBracket or TokenType.LBrace:
                    if (depth == 0)
                    {
                        return j;
                    }

                    depth--;
                    break;
            }
        }

        return -1;
    }
}

/// <summary>Text-level facts the rules share: which characters are code and where the repeated mistakes sit.</summary>
internal static class RepairSites
{
    /// <summary>True when <paramref name="offset"/> lies in a token that is not a string, template or regular expression (so not in a comment either).</summary>
    internal static bool InCode(List<Token> toks, int offset)
    {
        int i = TokenScan.IndexAtOrAfter(toks, offset + 1) - 1;
        if (i < 0)
        {
            return false;
        }

        Token t = toks[i];
        return offset >= t.Start && offset < t.End && t.Type is not (TokenType.String or TokenType.Template or TokenType.RegExp);
    }

    /// <summary>Offsets of every backslash directly before a quote from <paramref name="from"/> to the end of the line.</summary>
    internal static List<int> EscapedQuotes(string text, int from, int lineEnd)
    {
        var at = new List<int>();
        for (int i = from; i + 1 < lineEnd; i++)
        {
            if (text[i] == '\\' && text[i + 1] is '"' or '\'')
            {
                at.Add(i);
                i++;
            }
        }

        return at;
    }

    /// <summary>Offsets of every <c>\n</c> (backslash, n) that sits in code rather than in a string, template, regular expression or comment.</summary>
    internal static List<int> LiteralNewlines(string text, List<Token> toks)
    {
        var at = new List<int>();
        int from = 0;
        while ((from = text.IndexOf("\\n", from, StringComparison.Ordinal)) >= 0)
        {
            if (InCode(toks, from))
            {
                at.Add(from);
            }

            from += 2;
        }

        return at;
    }

    /// <summary>Offsets and decoded text of every HTML entity (<c>&amp;amp; &amp;lt; &amp;gt; &amp;quot;</c>) written in code.</summary>
    internal static List<(int At, int Length, string Decoded)> Entities(string text, List<Token> toks)
    {
        var found = new List<(int, int, string)>();
        int from = 0;
        while ((from = text.IndexOf('&', from)) >= 0)
        {
            (int len, string dec) = EntityAt(text, from);
            if (len > 0 && InCode(toks, from))
            {
                found.Add((from, len, dec));
                from += len;
            }
            else
            {
                from++;
            }
        }

        return found;
    }

    private static (int, string) EntityAt(string text, int i)
    {
        foreach ((string raw, string dec) in new[] { ("&amp;", "&"), ("&lt;", "<"), ("&gt;", ">"), ("&quot;", "\"") })
        {
            if (string.CompareOrdinal(text, i, raw, 0, raw.Length) == 0)
            {
                return (raw.Length, dec);
            }
        }

        return (0, "");
    }

    internal static string IndentOfLine(SourceText src, int offset)
    {
        ReadOnlySpan<char> line = src.GetLineText(src.GetPosition(offset).Line);
        int n = 0;
        while (n < line.Length && line[n] is ' ' or '\t')
        {
            n++;
        }

        return line[..n].ToString();
    }
}
