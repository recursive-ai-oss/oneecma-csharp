namespace RecursiveAI.OneEcma.Syntax;

internal sealed partial class TypeStripper
{
    // ---- classes ----------------------------------------------------------------------------------------------------------------

    private void ParseClass(bool declaration)
    {
        Expect(TokenType.Class);
        if (Cur.Type == TokenType.Identifier && !IsId(_p, "implements"))
        {
            _p++;
        }
        else if (declaration && Cur.Type != TokenType.LBrace && Cur.Type != TokenType.Extends && !IsId(_p, "implements") && Cur.Type != TokenType.Lt)
        {
            throw Fail(_p);
        }

        if (Cur.Type == TokenType.Lt)
        {
            int e = TypeParams(_p);
            Erase(_p, e);
            _p = e;
        }

        if (Cur.Type == TokenType.Extends)
        {
            _p++;
            bool saved = _heritage;
            _heritage = true;
            ParsePostfix();
            _heritage = saved;
        }

        if (IsId(_p, "implements"))
        {
            int first = _p;
            int i = _p + 1;
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

            Erase(first, i);
            _p = i;
        }

        Expect(TokenType.LBrace);
        int condSaved = _condTrue;
        bool noInSaved = _noIn;
        _condTrue = 0;
        _noIn = false;
        while (Cur.Type != TokenType.RBrace)
        {
            if (AtEnd)
            {
                throw Fail(_p);
            }

            ParseMember();
        }

        _p++;
        _condTrue = condSaved;
        _noIn = noInSaved;
    }

    private static bool EndsModifierRun(Token n) =>
        n.Type is TokenType.LParen or TokenType.Assign or TokenType.Semicolon or TokenType.RBrace or TokenType.Colon
            or TokenType.Question or TokenType.Bang or TokenType.Lt or TokenType.EndOfInput;

    private void ParseMember()
    {
        if (Cur.Type == TokenType.Semicolon)
        {
            _p++;
            return;
        }

        while (IsAt(Cur))
        {
            ParseDecorator();
        }

        int st = _p;
        var erasable = new List<int>();
        bool whole = false;
        bool isStatic = false;
        for (;;)
        {
            Token c = Cur;
            if (c.Type != TokenType.Identifier || c.Escaped || c.Text is not ("declare" or "public" or "private" or "protected" or "abstract" or "override"
                or "readonly" or "static" or "accessor" or "async" or "get" or "set"))
            {
                break;
            }

            Token n = Tok(_p + 1);
            if (EndsModifierRun(n) || (c.Text == "async" && n.NewlineBefore))
            {
                break;
            }

            switch (c.Text)
            {
                case "declare" or "abstract":
                    whole = true;
                    erasable.Add(_p);
                    break;
                case "public" or "private" or "protected" or "override" or "readonly":
                    erasable.Add(_p);
                    break;
                case "static":
                    isStatic = true;
                    break;
            }

            _p++;
        }

        if (isStatic && Cur.Type == TokenType.LBrace)
        {
            ParseBlock();
            return;
        }

        foreach (int m in erasable)
        {
            Erase(m, m + 1);
        }

        // [key: string]: T;
        if (Cur.Type == TokenType.LBracket && Tok(_p + 1).Type == TokenType.Identifier && Tok(_p + 2).Type == TokenType.Colon)
        {
            int close = Match(_p);
            if (Tok(close + 1).Type == TokenType.Colon)
            {
                int end = TypeFull(close + 2);
                if (Tok(end).Type is TokenType.Semicolon or TokenType.Comma)
                {
                    end++;
                }

                Erase(st, end);
                _p = end;
                return;
            }
        }

        if (Cur.Type == TokenType.Star)
        {
            _p++;
        }

        ParsePropertyName();
        if (Cur.Type is TokenType.Question or TokenType.Bang)
        {
            Erase(_p, _p + 1);
            _p++;
        }

        if (Cur.Type is TokenType.Lt or TokenType.LParen)
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

            if (Cur.Type == TokenType.LBrace)
            {
                ParseBlock();
            }
            else
            {
                // A method signature without a body: an overload or an abstract method.
                int end = _p;
                if (Tok(end).Type is TokenType.Semicolon or TokenType.Comma)
                {
                    end++;
                }

                Erase(st, end);
                _p = end;
            }

            return;
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

        EatSemicolon();
        if (whole)
        {
            Erase(st, _p);
        }
    }

    // ---- types ----------------------------------------------------------------------------------------------------------------------
    // The functions below only find where a type ends: each takes the index of its first token and returns the index after its last.
    // They change nothing, so a failed guess costs nothing.

    private int TypeFull(int i)
    {
        Enter();
        try
        {
            Token c = Tok(i);
            if (c.Type == TokenType.LParen)
            {
                int m = Match(i);
                if (Tok(m + 1).Type == TokenType.Arrow)
                {
                    return ReturnType(m + 2);
                }
            }
            else if (c.Type == TokenType.Lt)
            {
                int j = TypeParams(i);
                if (Tok(j).Type == TokenType.LParen && Tok(Match(j) + 1).Type == TokenType.Arrow)
                {
                    return ReturnType(Match(j) + 2);
                }

                throw Fail(i);
            }
            else if (c.Type == TokenType.New || (IsId(i, "abstract") && Tok(i + 1).Type == TokenType.New))
            {
                int j = c.Type == TokenType.New ? i + 1 : i + 2;
                if (Tok(j).Type == TokenType.Lt)
                {
                    j = TypeParams(j);
                }

                if (Tok(j).Type != TokenType.LParen)
                {
                    throw Fail(j);
                }

                int m = Match(j);
                if (Tok(m + 1).Type != TokenType.Arrow)
                {
                    throw Fail(m + 1);
                }

                return ReturnType(m + 2);
            }

            int k = TypeUnion(i);
            if (Tok(k).Type == TokenType.Extends && !Tok(k).NewlineBefore)
            {
                int e = TypeUnion(k + 1);
                if (Tok(e).Type == TokenType.Question)
                {
                    int a = TypeFull(e + 1);
                    if (Tok(a).Type != TokenType.Colon)
                    {
                        throw Fail(a);
                    }

                    return TypeFull(a + 1);
                }
            }

            return k;
        }
        finally
        {
            _depth--;
        }
    }

    private int TypeUnion(int i)
    {
        if (Tok(i).Type is TokenType.Pipe or TokenType.Amp)
        {
            i++;
        }

        i = TypeOperand(i);
        while (Tok(i).Type is TokenType.Pipe or TokenType.Amp)
        {
            i = TypeOperand(i + 1);
        }

        return i;
    }

    private static bool StartsType(Token t) => t.Type is
        TokenType.Identifier or TokenType.String or TokenType.Number or TokenType.BigInt or TokenType.Template or TokenType.LBrace
        or TokenType.LBracket or TokenType.LParen or TokenType.Minus or TokenType.Typeof or TokenType.Void or TokenType.Null
        or TokenType.True or TokenType.False or TokenType.This or TokenType.Import or TokenType.New or TokenType.Lt;

    private int TypeOperand(int i)
    {
        Enter();
        try
        {
            Token c = Tok(i);
            if (c.Type == TokenType.Identifier && !c.Escaped)
            {
                switch (c.Text)
                {
                    case "keyof" or "unique" or "readonly" when StartsType(Tok(i + 1)):
                        return TypeOperand(i + 1);
                    case "infer" when Tok(i + 1).Type == TokenType.Identifier:
                        {
                            int j = i + 2;
                            if (Tok(j).Type == TokenType.Extends)
                            {
                                try
                                {
                                    int e = TypeUnion(j + 1);
                                    if (Tok(e).Type != TokenType.Question)
                                    {
                                        j = e;
                                    }
                                }
                                catch (StripFail)
                                {
                                }
                            }

                            return j;
                        }
                }
            }

            if (c.Type == TokenType.Typeof)
            {
                return TypeSuffix(TypeofQuery(i));
            }

            return TypeSuffix(TypePrimary(i));
        }
        finally
        {
            _depth--;
        }
    }

    private int TypeSuffix(int i)
    {
        for (;;)
        {
            Token c = Tok(i);
            if (c.Type == TokenType.LBracket && !c.NewlineBefore)
            {
                i = Tok(i + 1).Type == TokenType.RBracket ? i + 2 : Match(i) + 1;
                continue;
            }

            return i;
        }
    }

    private int TypePrimary(int i)
    {
        Token c = Tok(i);
        switch (c.Type)
        {
            case TokenType.Identifier or TokenType.This or TokenType.Void or TokenType.Null or TokenType.True or TokenType.False:
                return TypeNameRest(i + 1);
            case TokenType.String or TokenType.Number or TokenType.BigInt:
                return i + 1;
            case TokenType.Minus when Tok(i + 1).Type is TokenType.Number or TokenType.BigInt:
                return i + 2;
            case TokenType.Template:
                for (;;)
                {
                    Token tt = Tok(i);
                    if (tt.Type != TokenType.Template)
                    {
                        throw Fail(i);
                    }

                    if ((tt.Flags & TokenFlags.TemplateTail) != 0)
                    {
                        return i + 1;
                    }

                    i = TypeFull(i + 1);
                }

            case TokenType.LBrace or TokenType.LBracket or TokenType.LParen:
                return Match(i) + 1;
            case TokenType.Import:
                return ImportType(i);
            default:
                throw Fail(i);
        }
    }

    // After the first name of a type reference: .Name chains and type arguments.
    private int TypeNameRest(int i)
    {
        while (Tok(i).Type == TokenType.Dot && IsNameLike(Tok(i + 1)))
        {
            i += 2;
        }

        if (Tok(i).Type == TokenType.Lt && !Tok(i).NewlineBefore)
        {
            i = TypeArgs(i);
        }

        return i;
    }

    private int TypeofQuery(int i)
    {
        i++;
        if (Tok(i).Type == TokenType.Import)
        {
            return ImportType(i);
        }

        if (Tok(i).Type is not (TokenType.Identifier or TokenType.This))
        {
            throw Fail(i);
        }

        return TypeNameRest(i + 1);
    }

    private int ImportType(int i)
    {
        if (Tok(i + 1).Type != TokenType.LParen)
        {
            throw Fail(i + 1);
        }

        return TypeNameRest(Match(i + 1) + 1);
    }

    private int TypeArgs(int i)
    {
        if (Tok(i).Type != TokenType.Lt)
        {
            throw Fail(i);
        }

        i++;
        for (;;)
        {
            i = TypeFull(i);
            if (Tok(i).Type == TokenType.Comma)
            {
                i++;
                if (Tok(i).Type == TokenType.Gt)
                {
                    break;
                }

                continue;
            }

            break;
        }

        if (Tok(i).Type != TokenType.Gt)
        {
            throw Fail(i);
        }

        return i + 1;
    }

    private int TypeParams(int i)
    {
        if (Tok(i).Type != TokenType.Lt)
        {
            throw Fail(i);
        }

        i++;
        for (;;)
        {
            while ((Tok(i).Type is TokenType.Const or TokenType.In || IsId(i, "out")) && Tok(i + 1).Type == TokenType.Identifier)
            {
                i++;
            }

            if (Tok(i).Type != TokenType.Identifier)
            {
                throw Fail(i);
            }

            i++;
            if (Tok(i).Type == TokenType.Extends)
            {
                i = TypeFull(i + 1);
            }

            if (Tok(i).Type == TokenType.Assign)
            {
                i = TypeFull(i + 1);
            }

            if (Tok(i).Type == TokenType.Comma)
            {
                i++;
                if (Tok(i).Type == TokenType.Gt)
                {
                    break;
                }

                continue;
            }

            break;
        }

        if (Tok(i).Type != TokenType.Gt)
        {
            throw Fail(i);
        }

        return i + 1;
    }

    // After the ':' of a return type: "x is T" and "asserts x [is T]" are allowed there.
    private int ReturnType(int i)
    {
        Token c = Tok(i);
        if (IsId(i, "asserts") && !Tok(i + 1).NewlineBefore && Tok(i + 1).Type is TokenType.Identifier or TokenType.This)
        {
            i += 2;
            return IsId(i, "is") && !Tok(i).NewlineBefore ? TypeFull(i + 1) : i;
        }

        if (c.Type is TokenType.Identifier or TokenType.This && IsId(i + 1, "is") && !Tok(i + 1).NewlineBefore)
        {
            return TypeFull(i + 2);
        }

        return TypeFull(i);
    }
}
