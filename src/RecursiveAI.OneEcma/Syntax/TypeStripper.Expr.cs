namespace RecursiveAI.OneEcma.Syntax;

internal sealed partial class TypeStripper
{
    // ---- expressions ---------------------------------------------------------------------------------------------------------

    private void ParseExpression()
    {
        ParseAssign();
        while (Cur.Type == TokenType.Comma)
        {
            _p++;
            ParseAssign();
        }
    }

    // One flat loop covers binary, assignment and conditional operators: the reader only has to find where an expression
    // ends, and every operator continues it. Precedence never changes which text is a type.
    private void ParseAssign()
    {
        Enter();
        try
        {
            ParseOperand();
            for (;;)
            {
                Token c = Cur;
                if (c.Type == TokenType.Question)
                {
                    _p++;
                    _condTrue++;
                    bool noIn = _noIn;
                    _noIn = false;
                    try
                    {
                        ParseAssign();
                    }
                    finally
                    {
                        _condTrue--;
                        _noIn = noIn;
                    }

                    Expect(TokenType.Colon);
                    ParseAssign();
                    continue;
                }

                if (c.Type == TokenType.Identifier && !c.Escaped && !c.NewlineBefore && c.Text is "as" or "satisfies")
                {
                    int first = _p;
                    int end = c.Text == "as" && Tok(_p + 1).Type == TokenType.Const ? _p + 2 : TypeFull(_p + 1);
                    EraseExpressionSuffix(first, end);
                    _p = end;
                    continue;
                }

                if (c.Type == TokenType.Gt)
                {
                    _p++;
                    while (Cur.Type is TokenType.Gt or TokenType.Assign && Cur.Start == Tok(_p - 1).End)
                    {
                        _p++;
                    }

                    ParseOperand();
                    continue;
                }

                if (IsBinaryOrAssignment(c.Type) || (c.Type == TokenType.In && !_noIn))
                {
                    _p++;
                    ParseOperand();
                    continue;
                }

                return;
            }
        }
        finally
        {
            _depth--;
        }
    }

    private static bool IsBinaryOrAssignment(TokenType t) => t is
        TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.Percent or TokenType.StarStar
        or TokenType.Shl or TokenType.Amp or TokenType.Pipe or TokenType.Caret or TokenType.AmpAmp or TokenType.PipePipe
        or TokenType.QuestionQuestion or TokenType.EqEq or TokenType.NotEq or TokenType.EqEqEq or TokenType.NotEqEq
        or TokenType.Lt or TokenType.Le or TokenType.Instanceof
        or TokenType.Assign or TokenType.PlusAssign or TokenType.MinusAssign or TokenType.StarAssign or TokenType.SlashAssign
        or TokenType.PercentAssign or TokenType.StarStarAssign or TokenType.ShlAssign or TokenType.AmpAssign or TokenType.PipeAssign
        or TokenType.CaretAssign or TokenType.AmpAmpAssign or TokenType.PipePipeAssign or TokenType.QuestionQuestionAssign;

    private bool StartsExpression(Token t) => (t.Type == TokenType.Template && IsTemplateStart(t)) || t.Type is
        TokenType.Identifier or TokenType.PrivateName or TokenType.Number or TokenType.BigInt or TokenType.String
        or TokenType.RegExp or TokenType.LParen or TokenType.LBracket or TokenType.LBrace or TokenType.Function or TokenType.Class
        or TokenType.New or TokenType.This or TokenType.Super or TokenType.Null or TokenType.True or TokenType.False
        or TokenType.Typeof or TokenType.Void or TokenType.Delete or TokenType.Bang or TokenType.Tilde or TokenType.Import
        or TokenType.Slash or TokenType.SlashAssign;

    private void ParseOperand()
    {
        Enter();
        try
        {
            Token c = Cur;
            switch (c.Type)
            {
                case TokenType.Plus or TokenType.Minus or TokenType.Bang or TokenType.Tilde or TokenType.PlusPlus or TokenType.MinusMinus
                    or TokenType.Typeof or TokenType.Void or TokenType.Delete:
                    _p++;
                    ParseOperand();
                    return;
                case TokenType.Lt:
                    ParseAngle();
                    return;
                case TokenType.Identifier when !c.Escaped && c.Text is "await" or "yield":
                    {
                        Token n = Tok(_p + 1);
                        bool operand = StartsExpression(n) && n.Type is not (TokenType.Slash or TokenType.SlashAssign) && (c.Text == "await" || !n.NewlineBefore);
                        if (c.Text == "yield" && n.Type == TokenType.Star && !n.NewlineBefore)
                        {
                            _p += 2;
                            ParseAssign();
                            return;
                        }

                        if (operand)
                        {
                            _p++;
                            ParseOperand();
                            return;
                        }

                        break;
                    }
            }

            ParsePostfix();
        }
        finally
        {
            _depth--;
        }
    }

    // `<T>(x) => x` is a generic arrow; `<T>x` is the legacy cast, which is a type assertion with no erasable form.
    private void ParseAngle()
    {
        int paramsEnd = -1;
        try
        {
            paramsEnd = TypeParams(_p);
        }
        catch (StripFail)
        {
        }

        if (paramsEnd >= 0 && Tok(paramsEnd).Type == TokenType.LParen && IsArrowParen(paramsEnd, out _))
        {
            Erase(_p, paramsEnd);
            _p = paramsEnd;
            ParseArrowFromParen();
            return;
        }

        int start = _p;
        int end;
        try
        {
            end = TypeArgs(_p);
        }
        catch (StripFail)
        {
            throw Fail(_p);
        }

        Refuse(
            "ts-angle-cast",
            "TypeScript '<T>expr' type assertions are not erasable syntax",
            Tok(start).Start,
            Tok(end - 1).End,
            "this angle-bracket cast has no erasable form here",
            "write the assertion as `expr as T` instead");
        _p = end;
        ParseOperand();
    }

    private bool IsArrowParen(int open, out bool hasReturnType)
    {
        hasReturnType = false;
        int m = open < _match.Length ? _match[open] : -1;
        if (m < 0)
        {
            return false;
        }

        Token n = Tok(m + 1);
        if (n.Type == TokenType.Arrow)
        {
            return !n.NewlineBefore;
        }

        if (n.Type == TokenType.Colon)
        {
            try
            {
                int e = ReturnType(m + 2);
                if (Tok(e).Type == TokenType.Arrow)
                {
                    hasReturnType = true;
                    return true;
                }
            }
            catch (StripFail)
            {
            }
        }

        return false;
    }

    // At '(' of an arrow function's parameters.
    private void ParseArrowFromParen()
    {
        ParseParams();
        if (Cur.Type == TokenType.Colon)
        {
            int colon = _p;
            int closeEnd = Tok(colon - 1).End;
            int end = ReturnType(_p + 1);
            Erase(colon, end);
            Token arrow = Tok(end);
            int colonStart = Tok(colon).Start;
            int lineBreak = -1;
            for (int k = colonStart; k < arrow.Start; k++)
            {
                if (Text.SourceText.IsLineTerminator(_text[k]))
                {
                    lineBreak = k;
                    break;
                }
            }

            if (arrow.Type == TokenType.Arrow && lineBreak >= 0)
            {
                // The arrow must stay on the line of its ')'. It moves into the erased text, or into blanks just before the colon.
                int target = colonStart;
                while (target > closeEnd && _text[target - 1] is ' ' or '\t')
                {
                    target--;
                }

                if (lineBreak - target >= 2)
                {
                    _edits.Add(new Edit(arrow.Start, target, EditKind.MoveArrow));
                }
                else
                {
                    Refuse(
                        "ts-arrow-return-type-layout",
                        "The return type of this arrow function cannot be erased in place",
                        colonStart,
                        Tok(end - 1).End,
                        "the line break right after ':' would leave '=>' on a later line than ')', which JavaScript forbids",
                        "start the return type on the same line as the colon: `(a): Promise<T> => ...`");
                }
            }

            _p = end;
        }

        Expect(TokenType.Arrow);
        ParseArrowBody();
    }

    private void ParseArrowBody()
    {
        if (Cur.Type == TokenType.LBrace)
        {
            ParseBlock();
        }
        else
        {
            ParseAssign();
        }
    }

    private void ParsePostfix()
    {
        if (ParsePrimary())
        {
            return;
        }

        for (;;)
        {
            Token c = Cur;
            switch (c.Type)
            {
                case TokenType.Dot:
                    _p++;
                    NameAfterDot();
                    continue;
                case TokenType.QuestionDot:
                    _p++;
                    if (Cur.Type == TokenType.LParen)
                    {
                        ParseArguments();
                    }
                    else if (Cur.Type == TokenType.LBracket)
                    {
                        ParseIndex();
                    }
                    else
                    {
                        NameAfterDot();
                    }

                    continue;
                case TokenType.LBracket:
                    ParseIndex();
                    continue;
                case TokenType.LParen:
                    ParseArguments();
                    continue;
                case TokenType.Template when IsTemplateStart(c):
                    ParseTemplate();
                    continue;
                case TokenType.Bang when !c.NewlineBefore:
                    Erase(_p, _p + 1);
                    _p++;
                    continue;
                case TokenType.Lt:
                    if (TryTypeArguments())
                    {
                        continue;
                    }

                    return;
                case TokenType.PlusPlus or TokenType.MinusMinus when !c.NewlineBefore:
                    _p++;
                    return;
                default:
                    return;
            }
        }
    }

    private void NameAfterDot()
    {
        if (!IsNameLike(Cur))
        {
            throw Fail(_p);
        }

        _p++;
    }

    private void ParseIndex()
    {
        _p++;
        int saved = _condTrue;
        bool savedNoIn = _noIn;
        _condTrue = 0;
        _noIn = false;
        ParseExpression();
        _condTrue = saved;
        _noIn = savedNoIn;
        Expect(TokenType.RBracket);
    }

    private void ParseArguments()
    {
        _p++;
        int saved = _condTrue;
        bool savedNoIn = _noIn;
        _condTrue = 0;
        _noIn = false;
        while (Cur.Type != TokenType.RParen)
        {
            if (AtEnd)
            {
                throw Fail(_p);
            }

            if (Cur.Type == TokenType.Ellipsis)
            {
                _p++;
            }

            ParseAssign();
            if (Cur.Type == TokenType.Comma)
            {
                _p++;
            }
            else
            {
                break;
            }
        }

        _condTrue = saved;
        _noIn = savedNoIn;
        Expect(TokenType.RParen);
    }

    private void ParseTemplate()
    {
        int saved = _condTrue;
        bool savedNoIn = _noIn;
        _condTrue = 0;
        _noIn = false;
        for (;;)
        {
            Token t = Cur;
            if (t.Type != TokenType.Template)
            {
                throw Fail(_p);
            }

            _p++;
            if ((t.Flags & TokenFlags.TemplateTail) != 0)
            {
                break;
            }

            ParseExpression();
        }

        _condTrue = saved;
        _noIn = savedNoIn;
    }

    // f<T>(x), new Map<string, number>(), f<T>`x`: type arguments are taken only when what follows cannot continue a comparison.
    private bool TryTypeArguments()
    {
        int end;
        try
        {
            end = TypeArgs(_p);
        }
        catch (StripFail)
        {
            return false;
        }

        if (!CanFollowTypeArguments(end))
        {
            return false;
        }

        // "f<T>" newline "[0]": TypeScript ends the statement after the instantiation, but blanked, the '[' would index f.
        Token after = Tok(end);
        _edits.Add(new Edit(Tok(_p).Start, Tok(end - 1).End, after.NewlineBefore && after.Type == TokenType.LBracket ? EditKind.BlankSemicolon : EditKind.Blank));
        _p = end;
        return true;
    }

    private bool CanFollowTypeArguments(int end)
    {
        Token n = Tok(end);
        if (n.Type == TokenType.LParen || (n.Type == TokenType.Template && IsTemplateStart(n)))
        {
            return true;
        }

        if (n.Type is TokenType.Lt or TokenType.Gt or TokenType.Plus or TokenType.Minus)
        {
            return false;
        }

        // "a < b >= c": the '=' is the second half of ">=".
        if (n.Type == TokenType.Assign && n.Start == Tok(end - 1).End)
        {
            return false;
        }

        if (_heritage && (n.Type == TokenType.LBrace || IsId(end, "implements")))
        {
            return true;
        }

        return n.NewlineBefore || !StartsExpression(n);
    }

    /// <summary>Returns true when the primary was an arrow function, which takes no postfix operators.</summary>
    private bool ParsePrimary()
    {
        Token c = Cur;
        Token n = Tok(_p + 1);
        switch (c.Type)
        {
            case TokenType.Number or TokenType.BigInt or TokenType.String or TokenType.RegExp or TokenType.True or TokenType.False
                or TokenType.Null or TokenType.This or TokenType.Super or TokenType.PrivateName:
                _p++;
                return false;
            case TokenType.Import:
                _p++;
                return false;
            case TokenType.Template when IsTemplateStart(c):
                ParseTemplate();
                return false;
            case TokenType.LBracket:
                ParseArrayLiteral();
                return false;
            case TokenType.LBrace:
                ParseObjectLiteral();
                return false;
            case TokenType.Function:
                ParseFunction(_p, declaration: false);
                return false;
            case TokenType.Class:
                ParseClass(declaration: false);
                return false;
            case TokenType.Invalid when IsAt(c):
                while (IsAt(Cur))
                {
                    ParseDecorator();
                }

                ParseClass(declaration: false);
                return false;
            case TokenType.New:
                ParseNew();
                return false;
            case TokenType.LParen:
                return ParseParenOrArrow();
            case TokenType.Identifier:
                if (!c.Escaped && c.Text == "async" && !n.NewlineBefore)
                {
                    if (n.Type == TokenType.Function)
                    {
                        ParseFunction(_p, declaration: false);
                        return false;
                    }

                    if (n.Type == TokenType.Identifier && Tok(_p + 2).Type == TokenType.Arrow)
                    {
                        _p += 3;
                        ParseArrowBody();
                        return true;
                    }

                    if (n.Type == TokenType.LParen && IsArrowParen(_p + 1, out _))
                    {
                        _p++;
                        ParseArrowFromParen();
                        return true;
                    }

                    if (n.Type == TokenType.Lt)
                    {
                        int pe = -1;
                        try
                        {
                            pe = TypeParams(_p + 1);
                        }
                        catch (StripFail)
                        {
                        }

                        if (pe >= 0 && Tok(pe).Type == TokenType.LParen && IsArrowParen(pe, out _))
                        {
                            Erase(_p + 1, pe);
                            _p = pe;
                            ParseArrowFromParen();
                            return true;
                        }
                    }
                }

                if (n.Type == TokenType.Arrow && !n.NewlineBefore)
                {
                    _p += 2;
                    ParseArrowBody();
                    return true;
                }

                _p++;
                return false;
            default:
                throw Fail(_p);
        }
    }

    private bool ParseParenOrArrow()
    {
        if (IsArrowParen(_p, out bool hasReturnType))
        {
            if (!(hasReturnType && _condTrue > 0))
            {
                ParseArrowFromParen();
                return true;
            }

            // In "a ? (b) : c => d" the ':' may belong to the conditional; the arrow reading holds only if a ':' follows the arrow.
            int start = _p;
            int edits = _edits.Count;
            int diags = _diags.Count;
            ParseArrowFromParen();
            if (Cur.Type == TokenType.Colon)
            {
                return true;
            }

            _p = start;
            _edits.RemoveRange(edits, _edits.Count - edits);
            _diags.RemoveRange(diags, _diags.Count - diags);
        }

        _p++;
        int saved = _condTrue;
        bool savedNoIn = _noIn;
        _condTrue = 0;
        _noIn = false;
        ParseExpression();
        _condTrue = saved;
        _noIn = savedNoIn;
        Expect(TokenType.RParen);
        return false;
    }

    private void ParseNew()
    {
        _p++;
        if (Cur.Type == TokenType.Dot)
        {
            _p += 2;
            return;
        }

        ParsePrimary();
        for (;;)
        {
            Token c = Cur;
            if (c.Type == TokenType.Dot)
            {
                _p++;
                NameAfterDot();
            }
            else if (c.Type == TokenType.LBracket)
            {
                ParseIndex();
            }
            else if (c.Type == TokenType.Template && IsTemplateStart(c))
            {
                ParseTemplate();
            }
            else if (c.Type == TokenType.Bang && !c.NewlineBefore)
            {
                Erase(_p, _p + 1);
                _p++;
            }
            else if (c.Type == TokenType.Lt && TryTypeArguments())
            {
                // erased; continue
            }
            else
            {
                break;
            }
        }

        if (Cur.Type == TokenType.LParen)
        {
            ParseArguments();
        }
    }

    private void ParseArrayLiteral()
    {
        _p++;
        int saved = _condTrue;
        bool savedNoIn = _noIn;
        _condTrue = 0;
        _noIn = false;
        while (Cur.Type != TokenType.RBracket)
        {
            if (AtEnd)
            {
                throw Fail(_p);
            }

            if (Cur.Type == TokenType.Comma)
            {
                _p++;
                continue;
            }

            if (Cur.Type == TokenType.Ellipsis)
            {
                _p++;
            }

            ParseAssign();
            if (Cur.Type == TokenType.Comma)
            {
                _p++;
            }
            else
            {
                break;
            }
        }

        _condTrue = saved;
        _noIn = savedNoIn;
        Expect(TokenType.RBracket);
    }

    private void ParseObjectLiteral()
    {
        _p++;
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

            if (Cur.Type == TokenType.Ellipsis)
            {
                _p++;
                ParseAssign();
            }
            else
            {
                ParseObjectMember();
            }

            if (Cur.Type == TokenType.Comma)
            {
                _p++;
            }
            else
            {
                break;
            }
        }

        _condTrue = saved;
        _noIn = savedNoIn;
        Expect(TokenType.RBrace);
    }

    private void ParseObjectMember()
    {
        for (;;)
        {
            Token c = Cur;
            if (c.Type == TokenType.Identifier && !c.Escaped && c.Text is "async" or "get" or "set"
                && Tok(_p + 1).Type is not (TokenType.Comma or TokenType.Colon or TokenType.LParen or TokenType.RBrace or TokenType.Assign or TokenType.Lt))
            {
                _p++;
                continue;
            }

            break;
        }

        if (Cur.Type == TokenType.Star)
        {
            _p++;
        }

        ParsePropertyName();
        switch (Cur.Type)
        {
            case TokenType.Colon:
                _p++;
                ParseAssign();
                break;
            case TokenType.Lt or TokenType.LParen:
                ParseMethodRest();
                break;
            case TokenType.Assign:
                _p++;
                ParseAssign();
                break;
        }
    }

    private void ParsePropertyName()
    {
        if (Cur.Type == TokenType.LBracket)
        {
            _p++;
            ParseAssign();
            Expect(TokenType.RBracket);
        }
        else if (IsNameLike(Cur) || Cur.Type is TokenType.String or TokenType.Number or TokenType.BigInt)
        {
            _p++;
        }
        else
        {
            throw Fail(_p);
        }
    }

    // At '<' or '(' of a method: type parameters, parameters, return type, body.
    private void ParseMethodRest()
    {
        if (Cur.Type == TokenType.Lt)
        {
            int e = TypeParams(_p);
            Erase(_p, e);
            _p = e;
        }

        ParseParams();
        if (Cur.Type == TokenType.Colon)
        {
            _p = EraseAnnotation(_p, returnType: true);
        }

        ParseBlock();
    }

    // ---- functions and parameters ---------------------------------------------------------------------------------------------

    private bool ParseFunction(int st, bool declaration)
    {
        if (IsId(_p, "async"))
        {
            _p++;
        }

        Expect(TokenType.Function);
        if (Cur.Type == TokenType.Star)
        {
            _p++;
        }

        if (Cur.Type != TokenType.LParen && Cur.Type != TokenType.Lt)
        {
            if (!IsKeywordName(Cur))
            {
                throw Fail(_p);
            }

            _p++;
        }

        if (Cur.Type == TokenType.Lt)
        {
            int e = TypeParams(_p);
            Erase(_p, e);
            _p = e;
        }

        ParseParams();
        if (Cur.Type == TokenType.Colon)
        {
            _p = EraseAnnotation(_p, returnType: true);
        }

        if (Cur.Type == TokenType.LBrace)
        {
            ParseBlock();
            return false;
        }

        if (!declaration)
        {
            throw Fail(_p);
        }

        // An overload signature has no body and exists only for the type checker.
        return EraseStatement(st, _p);
    }

    private void ParseParams()
    {
        if (Cur.Type != TokenType.LParen)
        {
            throw Fail(_p);
        }

        _p++;
        int saved = _condTrue;
        bool savedNoIn = _noIn;
        _condTrue = 0;
        _noIn = false;
        bool first = true;
        while (Cur.Type != TokenType.RParen)
        {
            if (AtEnd)
            {
                throw Fail(_p);
            }

            while (IsAt(Cur))
            {
                int at = _p;
                ParseDecorator();
                Refuse(
                    "ts-parameter-decorator",
                    "TypeScript parameter decorators are not erasable syntax",
                    Tok(at).Start,
                    Tok(_p - 1).End,
                    "decorators on parameters exist only in TypeScript's experimentalDecorators mode",
                    "pass the dependency or metadata explicitly, or decorate the method instead (standard decorators do not apply to parameters)");
            }

            int paramStart = _p;
            if (first && Cur.Type == TokenType.This && Tok(_p + 1).Type == TokenType.Colon)
            {
                int e = TypeFull(_p + 2);
                if (Tok(e).Type == TokenType.Comma)
                {
                    e++;
                }

                Erase(paramStart, e);
                _p = e;
                first = false;
                continue;
            }

            int propertyStart = -1;
            while (Cur.Type == TokenType.Identifier && !Cur.Escaped && Cur.Text is "public" or "private" or "protected" or "readonly" or "override"
                && Tok(_p + 1).Type is TokenType.Identifier or TokenType.LBrace or TokenType.LBracket or TokenType.Ellipsis)
            {
                propertyStart = propertyStart < 0 ? Cur.Start : propertyStart;
                _p++;
            }

            if (Cur.Type == TokenType.Ellipsis)
            {
                _p++;
            }

            ParseBindingTarget();
            if (propertyStart >= 0)
            {
                Refuse(
                    "ts-parameter-property",
                    "TypeScript parameter properties are not erasable syntax",
                    propertyStart,
                    Tok(_p - 1).End,
                    "this modifier makes the parameter also a class field, which compiles to an assignment",
                    "declare the field in the class body and assign it in the constructor: `x; constructor(x) { this.x = x; }`");
            }

            if (Cur.Type == TokenType.Question)
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

            first = false;
            if (Cur.Type == TokenType.Comma)
            {
                _p++;
            }
            else
            {
                break;
            }
        }

        _condTrue = saved;
        _noIn = savedNoIn;
        Expect(TokenType.RParen);
    }

    private void ParseBindingTarget()
    {
        Enter();
        try
        {
            Token c = Cur;
            if (c.Type == TokenType.Identifier)
            {
                _p++;
            }
            else if (c.Type == TokenType.LBracket)
            {
                _p++;
                while (Cur.Type != TokenType.RBracket)
                {
                    if (AtEnd)
                    {
                        throw Fail(_p);
                    }

                    if (Cur.Type == TokenType.Comma)
                    {
                        _p++;
                        continue;
                    }

                    if (Cur.Type == TokenType.Ellipsis)
                    {
                        _p++;
                    }

                    ParseBindingTarget();
                    if (Cur.Type == TokenType.Assign)
                    {
                        _p++;
                        ParseAssign();
                    }

                    if (Cur.Type == TokenType.Comma)
                    {
                        _p++;
                    }
                    else
                    {
                        break;
                    }
                }

                Expect(TokenType.RBracket);
            }
            else if (c.Type == TokenType.LBrace)
            {
                _p++;
                while (Cur.Type != TokenType.RBrace)
                {
                    if (AtEnd)
                    {
                        throw Fail(_p);
                    }

                    if (Cur.Type == TokenType.Ellipsis)
                    {
                        _p++;
                        ParseBindingTarget();
                    }
                    else
                    {
                        ParsePropertyName();
                        if (Cur.Type == TokenType.Colon)
                        {
                            _p++;
                            ParseBindingTarget();
                        }

                        if (Cur.Type == TokenType.Assign)
                        {
                            _p++;
                            ParseAssign();
                        }
                    }

                    if (Cur.Type == TokenType.Comma)
                    {
                        _p++;
                    }
                    else
                    {
                        break;
                    }
                }

                Expect(TokenType.RBrace);
            }
            else
            {
                throw Fail(_p);
            }
        }
        finally
        {
            _depth--;
        }
    }

    // ': T' at a colon: erases it and returns the index after the type.
    private int EraseAnnotation(int colon, bool returnType = false)
    {
        int end = returnType ? ReturnType(colon + 1) : TypeFull(colon + 1);
        Erase(colon, end);
        return end;
    }
}
