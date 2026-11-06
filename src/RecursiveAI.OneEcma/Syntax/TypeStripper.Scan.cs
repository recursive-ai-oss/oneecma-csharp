namespace RecursiveAI.OneEcma.Syntax;

internal sealed partial class TypeStripper
{
    // Splits the whole text into tokens before any grammar is read, so the slash between "regular expression" and "division" is
    // guessed from the tokens before it (ECMA-262 12.1 leaves that to the grammar). The guess looks at what a '}' closes (a block
    // or an object literal) and what a ')' closes (an if/for/while/with header or something else); a wrong guess in odd code only
    // costs a stumble, and then the source is passed through if the parser accepts it as it stands.
    private static List<Token> Scan(string text)
    {
        var tokens = new List<Token>();
        var lexer = new Lexer(text, module: false, (_, _, _, _) => { });
        var templates = new List<int>();
        var braces = new Stack<bool>();   // true: block
        var parens = new Stack<bool>();   // true: control-statement header
        Token prevToken = default;
        TokenType prev = TokenType.EndOfInput;
        TokenType prev2 = TokenType.EndOfInput;
        bool prevIsAwait = false;
        bool lastCloserAllowsRegExp = false;
        int classParens = -1;
        while (tokens.Count < 4_000_000)
        {
            Token t = lexer.Next();
            switch (t.Type)
            {
                case TokenType.Slash or TokenType.SlashAssign when RegExpAllowed(prevToken, lastCloserAllowsRegExp):
                    t = lexer.RescanRegExp(t);
                    break;
                case TokenType.Template when (t.Flags & TokenFlags.TemplateTail) == 0:
                    templates.Add(0);
                    break;
                case TokenType.LBrace:
                    if (templates.Count > 0)
                    {
                        templates[^1]++;
                    }

                    bool classBody = classParens == parens.Count;
                    classParens = classBody ? -1 : classParens;
                    braces.Push(classBody || prev is TokenType.EndOfInput or TokenType.Semicolon or TokenType.LBrace or TokenType.RBrace or TokenType.RParen
                        or TokenType.Arrow or TokenType.Else or TokenType.Do or TokenType.Try or TokenType.Finally);
                    break;
                case TokenType.RBrace:
                    if (templates.Count > 0)
                    {
                        if (templates[^1] == 0)
                        {
                            t = lexer.RescanTemplateContinuation(t);
                            if ((t.Flags & TokenFlags.TemplateTail) != 0)
                            {
                                templates.RemoveAt(templates.Count - 1);
                            }

                            break;
                        }

                        templates[^1]--;
                    }

                    lastCloserAllowsRegExp = braces.Count > 0 && braces.Pop();
                    break;
                case TokenType.Class:
                    classParens = parens.Count;
                    break;
                case TokenType.LParen:
                    parens.Push(prev is TokenType.If or TokenType.While or TokenType.For or TokenType.With || (prevIsAwait && prev2 == TokenType.For));
                    break;
                case TokenType.RParen:
                    lastCloserAllowsRegExp = parens.Count > 0 && parens.Pop();
                    break;
            }

            tokens.Add(t);
            if (t.Type == TokenType.EndOfInput)
            {
                return tokens;
            }

            prevToken = t;
            prev2 = prev;
            prev = t.Type;
            prevIsAwait = t.Type == TokenType.Identifier && t.Text == "await";
        }

        tokens.Add(new Token(TokenType.EndOfInput, text.Length, text.Length, TokenFlags.None));
        return tokens;
    }

    private static bool RegExpAllowed(Token prev, bool closerAllowsRegExp) => prev.Type switch
    {
        TokenType.RBrace or TokenType.RParen => closerAllowsRegExp,
        TokenType.Identifier when prev.Text is "await" or "yield" or "of" => true,
        TokenType.Identifier or TokenType.PrivateName or TokenType.Number or TokenType.BigInt or TokenType.String or TokenType.Template
            or TokenType.RegExp or TokenType.RBracket or TokenType.This or TokenType.Null or TokenType.True or TokenType.False
            or TokenType.Super or TokenType.PlusPlus or TokenType.MinusMinus => false,
        _ => true,
    };
}
