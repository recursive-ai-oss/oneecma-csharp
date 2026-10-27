using System.Runtime.CompilerServices;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

/// <summary>
/// Splits a whole source text into tokens without a grammar, for the hint and repair passes that need to look
/// at broken code. A <c>/</c> is a regular expression unless the previous token ends a value (ECMA-262 12.1: the
/// parser normally decides; this guess is right for every sensible program and a wrong guess only costs a hint),
/// and a <c>}</c> closing a template substitution continues the template.
/// </summary>
internal static class TokenScan
{
    private static readonly ConditionalWeakTable<SourceText, List<Token>> Cache = [];
    private static readonly ConditionalWeakTable<SourceText, int[]> MatchCache = [];

    /// <summary>The tokens of <paramref name="source"/> (script goal), ending with the end-of-input token; cached per source.</summary>
    internal static List<Token> Of(SourceText source) => Cache.GetValue(source, s => Scan(s.Text));

    /// <summary>Bracket pairing for <see cref="Of"/> of the same source, cached with it.</summary>
    internal static int[] MatchOf(SourceText source) => MatchCache.GetValue(source, s => MatchBrackets(Of(s)));

    /// <summary>Scans <paramref name="text"/>; stops after 4 million tokens so a pathological input costs bounded time.</summary>
    internal static List<Token> Scan(string text)
    {
        var tokens = new List<Token>();
        var lexer = new Lexer(text, module: false, (_, _, _, _) => { });
        var substitutions = new List<int>();
        TokenType prev = TokenType.EndOfInput;
        while (tokens.Count < 4_000_000)
        {
            Token t = lexer.Next();
            switch (t.Type)
            {
                case TokenType.Slash or TokenType.SlashAssign when RegExpAllowedAfter(prev):
                    t = lexer.RescanRegExp(t);
                    break;
                case TokenType.Template when (t.Flags & TokenFlags.TemplateTail) == 0:
                    substitutions.Add(0);
                    break;
                case TokenType.LBrace when substitutions.Count > 0:
                    substitutions[^1]++;
                    break;
                case TokenType.RBrace when substitutions.Count > 0:
                    if (substitutions[^1] == 0)
                    {
                        t = lexer.RescanTemplateContinuation(t);
                        if ((t.Flags & TokenFlags.TemplateTail) != 0)
                        {
                            substitutions.RemoveAt(substitutions.Count - 1);
                        }
                    }
                    else
                    {
                        substitutions[^1]--;
                    }

                    break;
            }

            tokens.Add(t);
            if (t.Type == TokenType.EndOfInput)
            {
                return tokens;
            }

            prev = t.Type;
        }

        tokens.Add(new Token(TokenType.EndOfInput, text.Length, text.Length, TokenFlags.None));
        return tokens;
    }

    private static bool RegExpAllowedAfter(TokenType prev) => prev is not (
        TokenType.Identifier or TokenType.PrivateName or TokenType.Number or TokenType.BigInt or TokenType.String
        or TokenType.Template or TokenType.RegExp or TokenType.RParen or TokenType.RBracket
        or TokenType.This or TokenType.Null or TokenType.True or TokenType.False or TokenType.Super
        or TokenType.PlusPlus or TokenType.MinusMinus);

    /// <summary>Index of the token that starts at or after <paramref name="offset"/> (the end-of-input token if none).</summary>
    internal static int IndexAtOrAfter(List<Token> tokens, int offset)
    {
        int lo = 0;
        int hi = tokens.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (tokens[mid].Start < offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>
    /// For each token index, the index of its matching opener (or -1): brackets and braces pair up by nesting, and an
    /// unmatched closer maps to -1. Template pieces are skipped because the scan has already joined them.
    /// </summary>
    internal static int[] MatchBrackets(List<Token> tokens)
    {
        var match = new int[tokens.Count];
        Array.Fill(match, -1);
        var stack = new List<int>();
        for (int i = 0; i < tokens.Count; i++)
        {
            switch (tokens[i].Type)
            {
                case TokenType.LParen or TokenType.LBracket or TokenType.LBrace:
                    stack.Add(i);
                    break;
                case TokenType.RParen or TokenType.RBracket or TokenType.RBrace:
                    {
                        TokenType open = tokens[i].Type switch { TokenType.RParen => TokenType.LParen, TokenType.RBracket => TokenType.LBracket, _ => TokenType.LBrace };
                        if (stack.Count > 0 && tokens[stack[^1]].Type == open)
                        {
                            match[i] = stack[^1];
                            match[stack[^1]] = i;
                            stack.RemoveAt(stack.Count - 1);
                        }

                        break;
                    }
            }
        }

        return match;
    }
}
