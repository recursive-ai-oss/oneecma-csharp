using System.Globalization;
using System.Text;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>What a pattern token stands for after parsing (UTS #35 Part 3, "Number Format Patterns").</summary>
internal enum TokKind
{
    /// <summary>Literal text (possibly a compact suffix or unit name, see the formatter).</summary>
    Literal,

    /// <summary>The percent sign.</summary>
    Percent,

    /// <summary>The currency placeholder; <c>Text</c> holds the repeat count ("1", "2" or "3").</summary>
    Currency,

    /// <summary>The minus sign placeholder.</summary>
    Minus,

    /// <summary>Literal text that is a unit name (formatToParts type <c>unit</c>).</summary>
    Unit,

    /// <summary>Literal text that is a compact suffix (formatToParts type <c>compact</c>).</summary>
    Compact,

    /// <summary>The plus sign placeholder.</summary>
    Plus,
}

/// <summary>A prefix or suffix token of a number pattern.</summary>
internal readonly record struct Tok(TokKind Kind, string Text);

/// <summary>
/// A parsed decimal number pattern such as <c>#,##0.00</c> or <c>¤#,##0.00;(¤#,##0.00)</c>: affixes around
/// the number, grouping sizes and the minimum integer digits. Affix syntax follows UTS #35 Part 3.
/// </summary>
internal sealed class NumberPattern
{
    private static readonly Tok[] NoTokens = [];

    /// <summary>Tokens before the number in the positive form.</summary>
    internal Tok[] Prefix { get; private init; } = NoTokens;

    /// <summary>Tokens after the number in the positive form.</summary>
    internal Tok[] Suffix { get; private init; } = NoTokens;

    /// <summary>Prefix of the explicit negative subpattern, or null when the pattern has none.</summary>
    internal Tok[]? NegativePrefix { get; private init; }

    /// <summary>Suffix of the explicit negative subpattern.</summary>
    internal Tok[]? NegativeSuffix { get; private init; }

    /// <summary>Digits in the primary group (0 when the pattern has no grouping separator).</summary>
    internal int PrimaryGroup { get; private init; }

    /// <summary>Digits in the secondary groups.</summary>
    internal int SecondaryGroup { get; private init; }

    /// <summary>Count of <c>0</c> placeholders left of the decimal point.</summary>
    internal int ZeroCount { get; private init; }

    /// <summary>Whether the pattern is exactly the number placeholder with no affix (a compact "do not abbreviate" entry).</summary>
    internal bool IsBare => Prefix.Length == 0 && Suffix.Length == 0;

    /// <summary>Parses a pattern; <paramref name="scientific"/> lets <c>E0</c> count as part of the number.</summary>
    internal static NumberPattern Parse(string pattern, bool scientific = false)
    {
        // Split at the first unquoted ';'.
        int semi = -1;
        bool quoted = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (pattern[i] == ';' && !quoted)
            {
                semi = i;
                break;
            }
        }

        (Tok[] pre, Tok[] suf, int primary, int secondary, int zeros) = ParseOne(semi < 0 ? pattern : pattern[..semi], scientific);
        Tok[]? np = null, ns = null;
        if (semi >= 0)
        {
            (np, ns, _, _, _) = ParseOne(pattern[(semi + 1)..], scientific);
        }

        return new NumberPattern
        {
            Prefix = pre,
            Suffix = suf,
            NegativePrefix = np,
            NegativeSuffix = ns,
            PrimaryGroup = primary,
            SecondaryGroup = secondary,
            ZeroCount = zeros,
        };
    }

    private static (Tok[] Pre, Tok[] Suf, int Primary, int Secondary, int Zeros) ParseOne(string p, bool scientific)
    {
        int start = -1, end = -1;
        bool quoted = false;
        for (int i = 0; i < p.Length; i++)
        {
            char c = p[i];
            if (c == '\'')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
            {
                continue;
            }

            bool numeric = (start < 0 ? c is '#' or '0' : c is '#' or '0' or ',' or '.') || (scientific && start >= 0 && (c == 'E' || (c == '+' && p[i - 1] == 'E')));
            if (numeric)
            {
                if (start < 0)
                {
                    start = i;
                }

                end = i + 1;
            }
            else if (start >= 0)
            {
                break;
            }
        }

        if (start < 0)
        {
            return (Tokenize(p), NoTokens, 0, 0, 0);
        }

        string number = p[start..end];
        string intPart = number;
        int cut = intPart.IndexOfAny(['.', 'E']);
        if (cut >= 0)
        {
            intPart = intPart[..cut];
        }

        int zeros = intPart.Count(ch => ch == '0');
        int primary = 0, secondary = 0;
        int lastComma = intPart.LastIndexOf(',');
        if (lastComma >= 0)
        {
            primary = intPart.Length - lastComma - 1;
            int prev = lastComma > 0 ? intPart.LastIndexOf(',', lastComma - 1) : -1;
            secondary = prev >= 0 ? lastComma - prev - 1 : primary;
        }

        return (Tokenize(p[..start]), Tokenize(p[end..]), primary, secondary, zeros);
    }

    /// <summary>Tokenizes affix text that has no number placeholder.</summary>
    internal static Tok[] TokenizeAffix(string affix) => Tokenize(affix);

    private static Tok[] Tokenize(string affix)
    {
        if (affix.Length == 0)
        {
            return NoTokens;
        }

        var result = new List<Tok>();
        var lit = new StringBuilder();
        for (int i = 0; i < affix.Length; i++)
        {
            char c = affix[i];
            switch (c)
            {
                case '\'':
                    if (i + 1 < affix.Length && affix[i + 1] == '\'')
                    {
                        lit.Append('\'');
                        i++;
                    }
                    else
                    {
                        int close = affix.IndexOf('\'', i + 1);
                        if (close < 0)
                        {
                            close = affix.Length;
                        }

                        lit.Append(affix, i + 1, close - i - 1);
                        i = close;
                    }

                    break;
                case '%':
                    Flush(result, lit);
                    result.Add(new Tok(TokKind.Percent, "%"));
                    break;
                case '\u00A4':
                    Flush(result, lit);
                    int n = 1;
                    while (i + 1 < affix.Length && affix[i + 1] == '\u00A4')
                    {
                        n++;
                        i++;
                    }

                    result.Add(new Tok(TokKind.Currency, n.ToString(NumberFormatInfo.InvariantInfo)));
                    break;
                case '-':
                    Flush(result, lit);
                    result.Add(new Tok(TokKind.Minus, "-"));
                    break;
                case '+':
                    Flush(result, lit);
                    result.Add(new Tok(TokKind.Plus, "+"));
                    break;
                default:
                    lit.Append(c);
                    break;
            }
        }

        Flush(result, lit);
        return [.. result];
    }

    private static void Flush(List<Tok> result, StringBuilder lit)
    {
        if (lit.Length > 0)
        {
            result.Add(new Tok(TokKind.Literal, lit.ToString()));
            lit.Clear();
        }
    }
}
