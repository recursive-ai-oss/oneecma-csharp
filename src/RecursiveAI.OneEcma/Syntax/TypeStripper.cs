using System.Runtime.CompilerServices;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

/// <summary>
/// Removes erasable TypeScript syntax from a source text and leaves every other character where it was. It walks the token
/// stream with a small recursive-descent reader that knows just enough JavaScript grammar to tell where a type annotation,
/// type argument list, <c>as</c> clause or type-only declaration begins and ends; it builds no tree. Constructs that need code
/// generation (enums, namespaces with values, parameter properties, <c>import x = require()</c>, <c>&lt;T&gt;x</c> casts) are refused
/// with a report instead. See docs/typescript.md.
/// </summary>
internal sealed partial class TypeStripper
{
    private const int MaxDepth = 300;

    private enum EditKind : byte { Blank, BlankSemicolon, MoveArrow }

    private readonly record struct Edit(int Start, int End, EditKind Kind);

    private sealed class StripFail(int token, string? message = null) : Exception
    {
        internal int Token { get; } = token;

        internal string? Detail { get; } = message;
    }

    private readonly SourceText _src;
    private readonly string _text;
    private readonly List<Token> _t;
    private readonly int[] _match;
    private readonly CancellationToken _ct;
    private readonly List<Edit> _edits = [];
    private readonly List<Diagnostic> _diags = [];
    private int _p;
    private int _depth;
    private int _steps;
    private int _condTrue;
    private bool _heritage;
    private bool _noIn;
    private bool _syntaxFailed;

    internal TypeStripper(SourceText src, CancellationToken ct)
    {
        _src = src;
        _text = src.Text;
        _ct = ct;
        _t = SplitGreater(Scan(_text), _text);
        _match = MatchBrackets(_t);
    }

    internal bool HasRefusals { get; private set; }

    internal bool HasSyntaxFailure => _syntaxFailed;

    internal List<Diagnostic> Diagnostics => _diags;

    internal bool HasEdits => _edits.Count > 0;

    // Every '>' becomes its own token, so Array<Array<T>> closes two lists; the expression reader joins adjacent '>' and '=' again.
    private static List<Token> SplitGreater(List<Token> tokens, string text)
    {
        var result = new List<Token>(tokens.Count + 16);
        foreach (Token tok in tokens)
        {
            if (tok.Type is TokenType.Ge or TokenType.Shr or TokenType.UShr or TokenType.ShrAssign or TokenType.UShrAssign && text[tok.Start] == '>')
            {
                for (int i = tok.Start; i < tok.End; i++)
                {
                    result.Add(new Token(text[i] == '>' ? TokenType.Gt : TokenType.Assign, i, i + 1, i == tok.Start ? tok.Flags : TokenFlags.None));
                }
            }
            else
            {
                result.Add(tok);
            }
        }

        return result;
    }

    private static int[] MatchBrackets(List<Token> t)
    {
        var match = new int[t.Count];
        Array.Fill(match, -1);
        var stack = new Stack<int>();
        for (int i = 0; i < t.Count; i++)
        {
            switch (t[i].Type)
            {
                case TokenType.LBrace or TokenType.LParen or TokenType.LBracket:
                    stack.Push(i);
                    break;
                case TokenType.RBrace or TokenType.RParen or TokenType.RBracket:
                    if (stack.Count > 0)
                    {
                        int open = stack.Peek();
                        TokenType o = t[open].Type;
                        TokenType c = t[i].Type;
                        if ((o == TokenType.LBrace && c == TokenType.RBrace) || (o == TokenType.LParen && c == TokenType.RParen) || (o == TokenType.LBracket && c == TokenType.RBracket))
                        {
                            stack.Pop();
                            match[open] = i;
                            match[i] = open;
                        }
                    }

                    break;
            }
        }

        return match;
    }

    // ---- token access -------------------------------------------------------------------------------------------------

    private Token Tok(int i) => _t[Math.Clamp(i, 0, _t.Count - 1)];

    private Token Cur => _t[Math.Min(_p, _t.Count - 1)];

    private bool IsId(int i, string text)
    {
        Token t = Tok(i);
        return t.Type == TokenType.Identifier && !t.Escaped && t.Text == text;
    }

    // The lexer has no token for decorators's "@"; it comes through as an invalid token.
    private bool IsAt(Token t) => t.Type == TokenType.Invalid && _text[t.Start] == '@';

    // A template chunk that opens with a backtick starts a template; one that opens with "}" continues the template around a substitution.
    private bool IsTemplateStart(Token t) => _text[t.Start] == '`';

    private bool AtEnd => Cur.Type == TokenType.EndOfInput;

    private static bool IsNameLike(Token t) => t.Type is TokenType.Identifier or TokenType.PrivateName || t.Type >= TokenType.Break;

    private static bool IsKeywordName(Token t) => t.Type == TokenType.Identifier || t.Type >= TokenType.Break;

    private int Match(int i)
    {
        int m = i < _match.Length ? _match[i] : -1;
        if (m < 0)
        {
            throw new StripFail(i);
        }

        return m;
    }

    private static StripFail Fail(int tokenIndex) => new(tokenIndex);

    private void Enter()
    {
        if (++_depth > MaxDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new StripFail(_p, "depth");
        }

        if ((++_steps & 1023) == 0)
        {
            _ct.ThrowIfCancellationRequested();
        }
    }

    private void Expect(TokenType type)
    {
        if (Cur.Type != type)
        {
            throw Fail(_p);
        }

        _p++;
    }

    private void EatSemicolon()
    {
        if (Cur.Type == TokenType.Semicolon)
        {
            _p++;
        }
    }

    // ---- edits ---------------------------------------------------------------------------------------------------------

    private void Erase(int firstToken, int endToken) => _edits.Add(new Edit(Tok(firstToken).Start, Tok(endToken - 1).End, EditKind.Blank));

    // After an expression is cut short by erasing "as T", a following line that starts with ( [ or a template would join it,
    // because the type that used to end the expression is gone; a ';' in the erased span keeps the statement apart.
    private void EraseExpressionSuffix(int firstToken, int endToken)
    {
        Token next = Tok(endToken);
        bool hazard = next.NewlineBefore && (next.Type is TokenType.LParen or TokenType.LBracket || (next.Type == TokenType.Template && IsTemplateStart(next)));
        _edits.Add(new Edit(Tok(firstToken).Start, Tok(endToken - 1).End, hazard ? EditKind.BlankSemicolon : EditKind.Blank));
    }

    private bool EraseStatement(int firstToken, int endToken)
    {
        if (Tok(endToken).Type == TokenType.Semicolon)
        {
            endToken++;
        }

        Erase(firstToken, endToken);
        _p = endToken;
        return true;
    }

    internal string Apply()
    {
        var buf = _text.ToCharArray();
        foreach (Edit e in _edits)
        {
            if (e.Kind == EditKind.MoveArrow)
            {
                continue;
            }

            Blank(buf, e.Start, e.End);
            if (e.Kind == EditKind.BlankSemicolon)
            {
                for (int k = e.End - 1; k >= e.Start; k--)
                {
                    if (!SourceText.IsLineTerminator(_text[k]))
                    {
                        buf[k] = ';';
                        break;
                    }
                }
            }
        }

        // An arrow must stay on the line of its ')'. When an erased return type ran over a line break, '=>' moves up into the erased text.
        foreach (Edit e in _edits)
        {
            if (e.Kind == EditKind.MoveArrow)
            {
                buf[e.Start] = ' ';
                buf[e.Start + 1] = ' ';
            }
        }

        foreach (Edit e in _edits)
        {
            if (e.Kind == EditKind.MoveArrow)
            {
                buf[e.End] = '=';
                buf[e.End + 1] = '>';
            }
        }

        return new string(buf);
    }

    private static void Blank(char[] buf, int start, int end)
    {
        for (int k = start; k < end; k++)
        {
            if (!SourceText.IsLineTerminator(buf[k]))
            {
                buf[k] = ' ';
            }
        }
    }

    // ---- reports -------------------------------------------------------------------------------------------------------

    private void Refuse(string code, string headline, int start, int end, string label, params string[] help)
    {
        HasRefusals = true;
        var d = new Diagnostic("SyntaxError", code, headline, _src, new Span(start, Math.Max(end, start + 1)))
        {
            PrimaryLabel = label,
        };
        d.Notes.Add("StripTypes only erases types; it never generates code, so this construct cannot be run as written");
        d.Help.AddRange(help);
        _diags.Add(d);
    }

    private void ReportSyntax(StripFail f)
    {
        _syntaxFailed = true;
        Token t = Tok(f.Token);
        string headline = f.Detail == "depth"
            ? "Maximum nesting depth exceeded"
            : t.Type switch
            {
                TokenType.EndOfInput => "Unexpected end of input",
                TokenType.Identifier => "Unexpected identifier '" + _text[t.Start..t.End] + "'",
                TokenType.String => "Unexpected string",
                TokenType.Number or TokenType.BigInt => "Unexpected number",
                TokenType.Template => "Unexpected template string",
                _ => "Unexpected token '" + _text[t.Start..t.End] + "'",
            };
        _diags.Add(new Diagnostic("SyntaxError", f.Detail == "depth" ? "depth-limit" : "unexpected-token", headline, _src, new Span(t.Start, Math.Max(t.End, t.Start + 1))));
    }

    // ---- driver --------------------------------------------------------------------------------------------------------

    internal void Run()
    {
        while (!AtEnd && _diags.Count < 100)
        {
            int before = _p;
            try
            {
                ParseStatement();
            }
            catch (StripFail f)
            {
                ReportSyntax(f);
                Resynchronize(f.Token);
            }

            if (_p <= before)
            {
                _p = before + 1;
            }
        }
    }

    // After a syntax error, continue at the next line that is not inside a bracket group opened after the error.
    private void Resynchronize(int from)
    {
        _p = Math.Max(from, _p > from ? _p : from);
        int guard = 0;
        while (!AtEnd && guard++ < 1_000_000)
        {
            Token c = Cur;
            if (c.Type == TokenType.Semicolon)
            {
                _p++;
                return;
            }

            if (c.Type is TokenType.LBrace or TokenType.LParen or TokenType.LBracket && _match[_p] > _p)
            {
                _p = _match[_p] + 1;
            }
            else
            {
                _p++;
            }

            if (Cur.NewlineBefore)
            {
                return;
            }
        }
    }

    // ---- statements ----------------------------------------------------------------------------------------------------

    /// <summary>Parses one statement; returns true when it was erased as a whole (a type-only declaration or an overload signature).</summary>
    private bool ParseStatement(int startToken = -1)
    {
        Enter();
        try
        {
            return StatementCore(startToken < 0 ? _p : startToken);
        }
        finally
        {
            _depth--;
        }
    }

    private bool StatementCore(int st)
    {
        Token c = Cur;
        Token n = Tok(_p + 1);
        switch (c.Type)
        {
            case TokenType.Semicolon:
                _p++;
                return false;
            case TokenType.LBrace:
                ParseBlock();
                return false;
            case TokenType.Var:
                ParseVarDeclaration();
                return false;
            case TokenType.Const:
                if (n.Type == TokenType.Enum)
                {
                    return EnumDeclaration(st, ambient: false);
                }

                ParseVarDeclaration();
                return false;
            case TokenType.Enum:
                return EnumDeclaration(st, ambient: false);
            case TokenType.Function:
                return ParseFunction(st, declaration: true);
            case TokenType.Class:
                ParseClass(declaration: true);
                return false;
            case TokenType.Invalid when IsAt(c):
                while (IsAt(Cur))
                {
                    ParseDecorator();
                }

                return StatementCore(st);
            case TokenType.If:
                _p++;
                ParseParenthesized();
                ParseStatement();
                if (Cur.Type == TokenType.Else)
                {
                    _p++;
                    ParseStatement();
                }

                return false;
            case TokenType.For:
                ParseFor();
                return false;
            case TokenType.While:
                _p++;
                ParseParenthesized();
                ParseStatement();
                return false;
            case TokenType.Do:
                _p++;
                ParseStatement();
                Expect(TokenType.While);
                ParseParenthesized();
                EatSemicolon();
                return false;
            case TokenType.With:
                _p++;
                ParseParenthesized();
                ParseStatement();
                return false;
            case TokenType.Switch:
                ParseSwitch();
                return false;
            case TokenType.Try:
                ParseTry();
                return false;
            case TokenType.Throw:
                _p++;
                ParseExpression();
                EatSemicolon();
                return false;
            case TokenType.Return:
                _p++;
                if (!Cur.NewlineBefore && Cur.Type is not (TokenType.Semicolon or TokenType.RBrace or TokenType.EndOfInput))
                {
                    ParseExpression();
                }

                EatSemicolon();
                return false;
            case TokenType.Break or TokenType.Continue:
                _p++;
                if (!Cur.NewlineBefore && Cur.Type == TokenType.Identifier)
                {
                    _p++;
                }

                EatSemicolon();
                return false;
            case TokenType.Debugger:
                _p++;
                EatSemicolon();
                return false;
            case TokenType.Import when n.Type is not (TokenType.LParen or TokenType.Dot):
                return ParseImport(st);
            case TokenType.Export:
                return ParseExport();
            case TokenType.Identifier when !c.Escaped:
                return IdentifierStatement(st, c, n);
        }

        ParseExpression();
        EatSemicolon();
        return false;
    }

    private bool IdentifierStatement(int st, Token c, Token n)
    {
        bool sameLine = !n.NewlineBefore;
        switch (c.Text)
        {
            case "let" when n.Type is TokenType.Identifier or TokenType.LBracket or TokenType.LBrace:
                ParseVarDeclaration();
                return false;
            case "async" when n.Type == TokenType.Function && sameLine:
                return ParseFunction(st, declaration: true);
            case "type" when n.Type == TokenType.Identifier && sameLine && Tok(_p + 2).Type is TokenType.Assign or TokenType.Lt:
                return TypeAlias(st);
            case "interface" when n.Type == TokenType.Identifier && sameLine:
                return InterfaceDeclaration(st);
            case "declare" when sameLine && (n.Type is TokenType.Identifier or TokenType.Var or TokenType.Const or TokenType.Function or TokenType.Class or TokenType.Enum):
                return Declare(st);
            case "abstract" when n.Type == TokenType.Class && sameLine:
                Erase(_p, _p + 1);
                _p++;
                ParseClass(declaration: true);
                return false;
            case "namespace" when n.Type == TokenType.Identifier && sameLine:
            case "module" when n.Type is TokenType.Identifier or TokenType.String && sameLine:
                return Namespace(st);
        }

        if (n.Type == TokenType.Colon)
        {
            _p += 2;
            return ParseStatement();
        }

        ParseExpression();
        EatSemicolon();
        return false;
    }

    private void ParseBlock()
    {
        Expect(TokenType.LBrace);
        int saved = _condTrue;
        bool savedNoIn = _noIn;
        _condTrue = 0;
        _noIn = false;
        while (Cur.Type != TokenType.RBrace)
        {
            if (AtEnd)
            {
                throw Fail(_p);
            }

            ParseStatement();
        }

        _p++;
        _condTrue = saved;
        _noIn = savedNoIn;
    }

    private void ParseParenthesized()
    {
        Expect(TokenType.LParen);
        int saved = _condTrue;
        bool savedNoIn = _noIn;
        _condTrue = 0;
        _noIn = false;
        ParseExpression();
        _condTrue = saved;
        _noIn = savedNoIn;
        Expect(TokenType.RParen);
    }

    private void ParseVarDeclaration()
    {
        _p++;
        for (;;)
        {
            ParseBindingTarget();
            if (Cur.Type == TokenType.Bang && !Cur.NewlineBefore && Tok(_p + 1).Type == TokenType.Colon)
            {
                Erase(_p, _p + 1);
                _p++;
            }

            if (Cur.Type == TokenType.Colon)
            {
                _p = EraseAnnotation(_p);
            }

            if (Cur.Type == TokenType.Assign)
            {
                _p++;
                ParseAssign();
            }

            if (Cur.Type == TokenType.Comma)
            {
                _p++;
                continue;
            }

            break;
        }

        EatSemicolon();
    }

    private void ParseFor()
    {
        _p++;
        if (IsId(_p, "await"))
        {
            _p++;
        }

        Expect(TokenType.LParen);
        int saved = _condTrue;
        bool savedNoIn = _noIn;
        _condTrue = 0;
        _noIn = false;
        if (Cur.Type == TokenType.Semicolon)
        {
            // no init
        }
        else if (Cur.Type is TokenType.Var or TokenType.Const || (IsId(_p, "let") && Tok(_p + 1).Type is TokenType.Identifier or TokenType.LBracket or TokenType.LBrace))
        {
            ParseVarDeclarationNoSemicolon();
        }
        else
        {
            _noIn = true;
            ParseExpression();
            _noIn = false;
        }

        if (IsId(_p, "of") || Cur.Type == TokenType.In)
        {
            bool isOf = IsId(_p, "of");
            _p++;
            if (isOf)
            {
                ParseAssign();
            }
            else
            {
                ParseExpression();
            }
        }
        else
        {
            Expect(TokenType.Semicolon);
            if (Cur.Type != TokenType.Semicolon)
            {
                ParseExpression();
            }

            Expect(TokenType.Semicolon);
            if (Cur.Type != TokenType.RParen)
            {
                ParseExpression();
            }
        }

        _condTrue = saved;
        _noIn = savedNoIn;
        Expect(TokenType.RParen);
        ParseStatement();
    }

    // Like ParseVarDeclaration, but stops before "of"/"in" and leaves the ';' to the caller.
    private void ParseVarDeclarationNoSemicolon()
    {
        _p++;
        for (;;)
        {
            ParseBindingTarget();
            if (Cur.Type == TokenType.Colon)
            {
                _p = EraseAnnotation(_p);
            }

            if (Cur.Type == TokenType.Assign)
            {
                _p++;
                ParseAssign();
            }

            if (Cur.Type == TokenType.Comma)
            {
                _p++;
                continue;
            }

            break;
        }
    }

    private void ParseSwitch()
    {
        _p++;
        ParseParenthesized();
        Expect(TokenType.LBrace);
        while (Cur.Type != TokenType.RBrace)
        {
            if (AtEnd)
            {
                throw Fail(_p);
            }

            if (Cur.Type == TokenType.Case)
            {
                _p++;
                ParseExpression();
                Expect(TokenType.Colon);
            }
            else if (Cur.Type == TokenType.Default)
            {
                _p++;
                Expect(TokenType.Colon);
            }
            else
            {
                ParseStatement();
            }
        }

        _p++;
    }

    private void ParseTry()
    {
        _p++;
        ParseBlock();
        if (Cur.Type == TokenType.Catch)
        {
            _p++;
            if (Cur.Type == TokenType.LParen)
            {
                _p++;
                ParseBindingTarget();
                if (Cur.Type == TokenType.Colon)
                {
                    _p = EraseAnnotation(_p);
                }

                Expect(TokenType.RParen);
            }

            ParseBlock();
        }

        if (Cur.Type == TokenType.Finally)
        {
            _p++;
            ParseBlock();
        }
    }

    // ---- declarations that are erased whole ------------------------------------------------------------------------------

    private bool TypeAlias(int st)
    {
        int i = _p + 2;
        if (Tok(i).Type == TokenType.Lt)
        {
            i = TypeParams(i);
        }

        if (Tok(i).Type != TokenType.Assign)
        {
            throw Fail(i);
        }

        i = TypeFull(i + 1);
        return EraseStatement(st, i);
    }

    private bool InterfaceDeclaration(int st)
    {
        int i = _p + 2;
        if (Tok(i).Type == TokenType.Lt)
        {
            i = TypeParams(i);
        }

        if (Tok(i).Type == TokenType.Extends)
        {
            i++;
            for (;;)
            {
                i = TypeFull(i);
                if (Tok(i).Type == TokenType.Comma)
                {
                    i++;
                    continue;
                }

                break;
            }
        }

        if (Tok(i).Type != TokenType.LBrace)
        {
            throw Fail(i);
        }

        return EraseStatement(st, Match(i) + 1);
    }

    private bool Declare(int st)
    {
        _p++;
        Token c = Cur;
        if (c.Type == TokenType.Enum || (c.Type == TokenType.Const && Tok(_p + 1).Type == TokenType.Enum))
        {
            return EnumDeclaration(st, ambient: true);
        }

        if (IsId(_p, "global") && Tok(_p + 1).Type == TokenType.LBrace)
        {
            return EraseStatement(st, Match(_p + 1) + 1);
        }

        if (IsId(_p, "namespace") || IsId(_p, "module"))
        {
            int i = _p + 1;
            while (Tok(i).Type is TokenType.Identifier or TokenType.String or TokenType.Dot)
            {
                i++;
            }

            return EraseStatement(st, Tok(i).Type == TokenType.LBrace ? Match(i) + 1 : i);
        }

        // Ambient var/function/class/type/interface: read it like ordinary code to find where it ends, then erase it whole.
        int edits = _edits.Count;
        int diags = _diags.Count;
        bool refused = HasRefusals;
        StatementCore(_p);
        _edits.RemoveRange(edits, _edits.Count - edits);
        _diags.RemoveRange(diags, _diags.Count - diags);
        HasRefusals = refused;
        return EraseStatement(st, _p);
    }

    private bool EnumDeclaration(int st, bool ambient)
    {
        int first = _p;
        int i = _p;
        if (Tok(i).Type == TokenType.Const)
        {
            i++;
        }

        i++;
        int nameTok = i;
        if (Tok(i).Type == TokenType.Identifier)
        {
            i++;
        }

        if (Tok(i).Type != TokenType.LBrace)
        {
            throw Fail(i);
        }

        int end = Match(i) + 1;
        if (ambient)
        {
            return EraseStatement(st, end);
        }

        bool isConst = Tok(first).Type == TokenType.Const;
        Refuse(
            "ts-enum",
            isConst ? "TypeScript 'const enum' is not erasable syntax" : "TypeScript 'enum' is not erasable syntax",
            Tok(first).Start,
            Tok(nameTok).End,
            "an enum compiles to code that builds an object, which type erasure cannot produce",
            "use a `const` object instead: `const Color = { Red: 0, Green: 1 } as const;` and, if the type is needed, `type Color = (typeof Color)[keyof typeof Color];`");
        _p = end;
        return false;
    }

    private bool Namespace(int st)
    {
        int edits = _edits.Count;
        int diags = _diags.Count;
        bool refused = HasRefusals;
        int keyword = _p;
        _p++;
        while (Cur.Type is TokenType.Identifier or TokenType.String or TokenType.Dot)
        {
            _p++;
        }

        int headerEnd = _p;
        if (Cur.Type != TokenType.LBrace)
        {
            throw Fail(_p);
        }

        _p++;
        bool anyValue = false;
        while (Cur.Type != TokenType.RBrace)
        {
            if (AtEnd)
            {
                throw Fail(_p);
            }

            if (Cur.Type == TokenType.Semicolon)
            {
                _p++;
            }
            else if (!ParseStatement())
            {
                anyValue = true;
            }
        }

        _p++;
        _edits.RemoveRange(edits, _edits.Count - edits);
        _diags.RemoveRange(diags, _diags.Count - diags);
        HasRefusals = refused;
        if (!anyValue)
        {
            return EraseStatement(st, _p);
        }

        string word = _text[Tok(keyword).Start..Tok(keyword).End];
        Refuse(
            "ts-namespace",
            "TypeScript '" + word + "' blocks that contain values are not erasable syntax",
            Tok(keyword).Start,
            Tok(headerEnd - 1).End,
            "this block declares runtime values, so it compiles to code",
            "move the values into their own module and import them, or use a plain object: `export const Tools = { run() {} };` (a namespace with only types and interfaces is erased)");
        return false;
    }

    // ---- imports and exports -----------------------------------------------------------------------------------------------

    private void SkipImportAttributes()
    {
        if ((Cur.Type == TokenType.With || IsId(_p, "assert")) && Tok(_p + 1).Type == TokenType.LBrace)
        {
            _p = Match(_p + 1) + 1;
        }
    }

    private bool ParseImport(int st)
    {
        _p++;
        if (Cur.Type == TokenType.String)
        {
            _p++;
            SkipImportAttributes();
            EatSemicolon();
            return false;
        }

        if (IsId(_p, "type"))
        {
            Token n = Tok(_p + 1);
            bool typeImport = n.Type is TokenType.LBrace or TokenType.Star
                || (n.Type == TokenType.Identifier && !(n.Text == "from" && Tok(_p + 2).Type == TokenType.String));
            if (typeImport)
            {
                _p++;
                while (!AtEnd && Cur.Type != TokenType.String && Cur.Type != TokenType.Assign && Cur.Type != TokenType.Semicolon)
                {
                    _p = Cur.Type is TokenType.LBrace ? Match(_p) + 1 : _p + 1;
                }

                if (Cur.Type == TokenType.Assign)
                {
                    SkipToStatementEnd();
                }
                else
                {
                    Expect(TokenType.String);
                    SkipImportAttributes();
                }

                return EraseStatement(st, _p);
            }
        }

        if (Cur.Type == TokenType.Identifier && Tok(_p + 1).Type == TokenType.Assign)
        {
            int nameTok = _p;
            SkipToStatementEnd();
            ImportEquals(nameTok, st);
            return false;
        }

        if (Cur.Type == TokenType.Identifier)
        {
            _p++;
            if (Cur.Type == TokenType.Comma)
            {
                _p++;
            }
        }

        if (Cur.Type == TokenType.Star)
        {
            _p++;
            if (!IsId(_p, "as"))
            {
                throw Fail(_p);
            }

            _p += 2;
        }
        else if (Cur.Type == TokenType.LBrace)
        {
            SpecifierList();
        }

        if (!IsId(_p, "from"))
        {
            throw Fail(_p);
        }

        _p++;
        Expect(TokenType.String);
        SkipImportAttributes();
        EatSemicolon();
        return false;
    }

    private void ImportEquals(int nameTok, int st)
    {
        string name = _text[Tok(nameTok).Start..Tok(nameTok).End];
        bool isRequire = IsId(nameTok + 2, "require");
        Refuse(
            "ts-import-equals",
            isRequire ? "TypeScript 'import " + name + " = require()' is not erasable syntax" : "TypeScript 'import " + name + " = ...' is not erasable syntax",
            Tok(st).Start,
            Tok(_p - 1).End,
            "this is TypeScript's own import form, which compiles to code",
            isRequire ? "use a standard import: `import " + name + " from 'module';` (or `import * as " + name + " from 'module';`)" : "use a `const` binding for the value: `const " + name + " = Namespace.Member;`");
    }

    // Consumes tokens up to and including the ';' that ends the statement, or up to the first token on a later line.
    private void SkipToStatementEnd()
    {
        bool first = true;
        while (!AtEnd)
        {
            if (Cur.Type == TokenType.Semicolon)
            {
                _p++;
                return;
            }

            if (!first && Cur.NewlineBefore)
            {
                return;
            }

            first = false;
            _p = Cur.Type is TokenType.LBrace or TokenType.LParen or TokenType.LBracket && _match[_p] > _p ? _match[_p] + 1 : _p + 1;
        }
    }

    // { a, b as c, type D, type E as F }: removes the "type" specifiers (and one comma each); the braces stay, so
    // `import { type A } from 'm'` keeps its side effect as `import {  } from 'm'`.
    private void SpecifierList()
    {
        int open = _p;
        int close = Match(open);
        int i = open + 1;
        while (i < close)
        {
            int j = i;
            while (j < close && Tok(j).Type != TokenType.Comma)
            {
                j++;
            }

            int count = j - i;
            if (IsId(i, "type") && (count == 2 || count >= 4))
            {
                if (j < close)
                {
                    Erase(i, j + 1);
                }
                else if (i > open + 1)
                {
                    Erase(i - 1, j);
                }
                else
                {
                    Erase(i, j);
                }
            }

            i = j + 1;
        }

        _p = close + 1;
    }

    private bool ParseExport()
    {
        int st = _p;
        _p++;
        Token c = Cur;
        Token n = Tok(_p + 1);
        switch (c.Type)
        {
            case TokenType.Default:
                _p++;
                if (IsId(_p, "interface") && Tok(_p + 1).Type == TokenType.Identifier)
                {
                    return InterfaceDeclaration(st);
                }

                if (IsId(_p, "abstract") && Tok(_p + 1).Type == TokenType.Class)
                {
                    Erase(_p, _p + 1);
                    _p++;
                    ParseClass(declaration: true);
                    return false;
                }

                if (Cur.Type == TokenType.Function || (IsId(_p, "async") && Tok(_p + 1).Type == TokenType.Function))
                {
                    return ParseFunction(st, declaration: true);
                }

                if (IsAt(Cur))
                {
                    while (IsAt(Cur))
                    {
                        ParseDecorator();
                    }
                }

                if (Cur.Type == TokenType.Class)
                {
                    ParseClass(declaration: true);
                    return false;
                }

                ParseAssign();
                EatSemicolon();
                return false;
            case TokenType.Assign:
                _p++;
                ParseAssign();
                Refuse(
                    "ts-export-assignment",
                    "TypeScript 'export =' is not erasable syntax",
                    Tok(st).Start,
                    Tok(st + 1).End,
                    "this is TypeScript's CommonJS export form, which compiles to code",
                    "use `export default value;` or named exports instead");
                EatSemicolon();
                return false;
            case TokenType.LBrace:
                SpecifierList();
                ExportFrom();
                return false;
            case TokenType.Star:
                _p++;
                if (IsId(_p, "as"))
                {
                    _p += 2;
                }

                ExportFrom();
                return false;
            case TokenType.Enum:
                return EnumDeclaration(st, ambient: false);
            case TokenType.Const when n.Type == TokenType.Enum:
                return EnumDeclaration(st, ambient: false);
            case TokenType.Import:
                {
                    int nameTok = _p + 1;
                    SkipToStatementEnd();
                    ImportEquals(nameTok, st);
                    return false;
                }

            case TokenType.Invalid when IsAt(c):
                while (IsAt(Cur))
                {
                    ParseDecorator();
                }

                return StatementCore(st);
            case TokenType.Identifier when !c.Escaped:
                switch (c.Text)
                {
                    case "type" when n.Type is TokenType.LBrace or TokenType.Star:
                        _p++;
                        if (Cur.Type == TokenType.LBrace)
                        {
                            _p = Match(_p) + 1;
                        }
                        else
                        {
                            _p++;
                            if (IsId(_p, "as"))
                            {
                                _p += 2;
                            }
                        }

                        if (IsId(_p, "from"))
                        {
                            _p++;
                            Expect(TokenType.String);
                            SkipImportAttributes();
                        }

                        return EraseStatement(st, _p);
                    case "type" when n.Type == TokenType.Identifier && Tok(_p + 2).Type is TokenType.Assign or TokenType.Lt:
                        return TypeAlias(st);
                    case "interface" when n.Type == TokenType.Identifier:
                        return InterfaceDeclaration(st);
                    case "declare" when !n.NewlineBefore:
                        return Declare(st);
                    case "abstract" when n.Type == TokenType.Class:
                        Erase(_p, _p + 1);
                        _p++;
                        ParseClass(declaration: true);
                        return false;
                    case "namespace" when n.Type == TokenType.Identifier:
                    case "module" when n.Type is TokenType.Identifier or TokenType.String:
                        return Namespace(st);
                    case "as" when IsId(_p + 1, "namespace"):
                        _p += 3;
                        return EraseStatement(st, _p);
                }

                return ParseStatement(st);
            default:
                return ParseStatement(st);
        }
    }

    private void ExportFrom()
    {
        if (IsId(_p, "from"))
        {
            _p++;
            Expect(TokenType.String);
            SkipImportAttributes();
        }

        EatSemicolon();
    }

    // ---- decorators ----------------------------------------------------------------------------------------------------------

    // Decorators in the standard (stage 3) form pass through untouched; their expressions are read so any types in them are erased.
    private void ParseDecorator()
    {
        _p++;
        ParsePostfix();
    }
}
