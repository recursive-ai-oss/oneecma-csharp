using RecursiveAI.OneEcma.Diagnostics;
using System.Globalization;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

internal sealed partial class Parser
{
    private sealed class LabelInfo
    {
        internal required string Name { get; init; }

        internal bool IsLoop { get; set; }

        internal int LabelStart { get; init; }

        internal int BodyStart { get; init; }
    }

    /// <summary>Per-function parse state: what the enclosing function allows and what its body used.</summary>
    private sealed class FnState
    {
        internal FnState? Parent;
        internal bool IsAsync;
        internal bool IsGenerator;
        internal bool IsArrow;
        internal bool CanReturn;
        internal bool AllowSuperProperty;
        internal bool AllowSuperCall;
        internal bool AllowNewTarget;
        internal bool InClassFieldInit;
        internal bool InStaticBlock;
        internal bool UsesThis;
        internal bool UsesArguments;
        internal bool HasDirectEval;
        internal List<LabelInfo> Labels = [];
        internal int LoopDepth;
        internal int BreakDepth;
        internal bool InParameters;

        // First offsets of `yield` / `await` expressions and of `await` used as an identifier, for the
        // arrow-parameter cover grammar (ECMA-262 15.3.1 Early Errors).
        internal int YieldPos = -1;
        internal int AwaitPos = -1;
        internal int AwaitIdentPos = -1;
    }

    private enum StmtCtx
    {
        /// <summary>A StatementListItem: declarations allowed.</summary>
        List,

        /// <summary>The body of if/else: a sloppy plain function declaration is allowed.</summary>
        IfBody,

        /// <summary>Any other single-statement position.</summary>
        Single,

        /// <summary>A labelled statement directly in a list.</summary>
        LabelledInList,

        /// <summary>A labelled statement in a single-statement position.</summary>
        LabelledInSingle,
    }

    private bool _inPrologue;
    private bool _topLevel;

    private Program ParseProgram()
    {
        _strict = _module || _opt.Strict;
        _fn = new FnState
        {
            IsAsync = _module,
            CanReturn = _opt.AllowReturnOutsideFunction,
            AllowNewTarget = _opt.AllowNewTarget,
            AllowSuperProperty = _opt.AllowSuperProperty,
            AllowSuperCall = _opt.AllowSuperCall,
            InClassFieldInit = _opt.InClassFieldInitializer,
        };
        if (_opt.PrivateNames is { Count: > 0 } pn)
        {
            _classes.Add(new ClassCtx { IsOuterEval = true });
            foreach (string n in pn)
            {
                _classes[0].Declared[n] = PrivateKind.Field;
            }
        }

        ScopeKind kind = _module ? ScopeKind.Module : _opt.Goal == ParseGoal.Eval ? ScopeKind.Eval : ScopeKind.Global;
        Scope top = PushScope(kind, 0);
        Advance();
        var body = new List<Node>();
        _topLevel = true;
        ParseStatementsWithPrologue(body, TokenType.EndOfInput);
        Program program = new(0, _s.Length, body, _module, _strict, top);
        if (_module)
        {
            CheckExports();
        }

        top.IsStrict = _strict;
        PopScope();
        ResolvePrivateNamesAtEnd();
        return program;
    }

    /// <summary>Parses statements until <paramref name="end"/>, treating leading string statements as the directive prologue (ECMA-262 11.2.1).</summary>
    private void ParseStatementsWithPrologue(List<Node> body, TokenType end)
    {
        bool inPrologue = true;
        int octalPos = -1;
        while (!Is(TokenType.EndOfInput) && !Is(end))
        {
            int before = _tok.Start;
            Token first = _tok;
            _inPrologue = inPrologue && first.Type == TokenType.String;
            Node st = ParseStatementListItem();
            body.Add(st);
            if (inPrologue)
            {
                if (st is ExpressionStatement { Directive: { } directive })
                {
                    if (first.AuxStart >= 0 && octalPos < 0)
                    {
                        octalPos = first.AuxStart;
                    }

                    if (directive == "use strict")
                    {
                        _useStrictFound = true;
                        _useStrictStart = first.Start;
                        if (!_strict)
                        {
                            _strict = true;
                            _scope.IsStrict = true;
                            _strictFromDirective = true;
                            if (octalPos >= 0)
                            {
                                Report("octal-escape", Messages.OctalEscapeStrict, octalPos, octalPos + 1);
                            }
                        }
                    }
                }
                else
                {
                    inPrologue = false;
                }
            }

            _inPrologue = false;
            if (_tok.Start == before && !Is(TokenType.EndOfInput))
            {
                SkipStuckToken(end);
            }
        }
    }

    private bool _strictFromDirective;
    private bool _useStrictFound;

    /// <summary>Guarantees progress after a statement that consumed nothing: the stray token is reported (if not already) and dropped.</summary>
    private void SkipStuckToken(TokenType end)
    {
        if (Is(end))
        {
            return;
        }

        Diagnostic? d = Unexpected();
        NoteSkipped(d, _tok.Start, _tok.End);
        Advance();
    }

    private Node ParseStatementListItem() => ParseStatement(StmtCtx.List);

    private Node ParseStatement(StmtCtx ctx)
    {
        Enter();
        try
        {
            return ParseStatementCore(ctx);
        }
        finally
        {
            Leave();
        }
    }

    private Node ParseStatementCore(StmtCtx ctx)
    {
        int start = _tok.Start;
        bool single = ctx != StmtCtx.List;
        switch (_tok.Type)
        {
            case TokenType.LBrace:
                return ParseBlock(newScope: true);
            case TokenType.Semicolon:
                Advance();
                return new EmptyStatement(start, _prevEnd);
            case TokenType.Var:
                {
                    Advance();
                    VariableDeclaration d = ParseVariableDeclarations(VarKind.Var, start, noIn: false, forHead: false);
                    ConsumeSemicolon();
                    return d;
                }

            case TokenType.Const:
                if (single)
                {
                    return UnexpectedStatement();
                }

                {
                    Advance();
                    VariableDeclaration d = ParseVariableDeclarations(VarKind.Const, start, noIn: false, forHead: false);
                    ConsumeSemicolon();
                    return d;
                }

            case TokenType.Function:
                return ParseFunctionDeclarationStatement(ctx, start, isAsync: false);
            case TokenType.Class:
                if (single)
                {
                    return UnexpectedStatement();
                }

                return ParseClassDeclaration();
            case TokenType.If:
                return ParseIf();
            case TokenType.For:
                return ParseFor();
            case TokenType.While:
                return ParseWhile();
            case TokenType.Do:
                return ParseDoWhile();
            case TokenType.Continue:
                return ParseBreakContinue(isBreak: false);
            case TokenType.Break:
                return ParseBreakContinue(isBreak: true);
            case TokenType.Return:
                return ParseReturn();
            case TokenType.With:
                return ParseWith();
            case TokenType.Switch:
                return ParseSwitch();
            case TokenType.Throw:
                return ParseThrow();
            case TokenType.Try:
                return ParseTry();
            case TokenType.Debugger:
                Advance();
                ConsumeSemicolon();
                return new DebuggerStatement(start, _prevEnd);
            case TokenType.Import:
                {
                    Token next = PeekToken();
                    if (next.Type is TokenType.LParen or TokenType.Dot)
                    {
                        return ParseExpressionStatement();
                    }

                    return ParseImportDeclarationStatement(ctx);
                }

            case TokenType.Export:
                return ParseExportDeclarationStatement(ctx);
            case TokenType.Identifier:
                return ParseIdentifierStatement(ctx, single);
            default:
                return ParseExpressionStatement();
        }
    }

    private ErrorStatement UnexpectedStatement()
    {
        int start = _tok.Start;
        Diagnostic? d = Unexpected();
        NoteSkipped(d, _tok.Start, _tok.End);
        Advance();
        // Carry on with the declaration as if it were allowed, so its own errors still surface.
        return new ErrorStatement(start, _prevEnd);
    }

    private Node ParseIdentifierStatement(StmtCtx ctx, bool single)
    {
        int start = _tok.Start;
        Token t = _tok;
        if (!t.Escaped)
        {
            if (t.Text == "let")
            {
                Token next = PeekToken();
                bool declaration = next.Type is TokenType.LBracket or TokenType.LBrace or TokenType.Identifier;
                if (declaration)
                {
                    if (ctx == StmtCtx.List)
                    {
                        Advance();
                        VariableDeclaration d = ParseVariableDeclarations(VarKind.Let, start, noIn: false, forHead: false);
                        ConsumeSemicolon();
                        return d;
                    }

                    // 14.5: ExpressionStatement may not start with `let [`; otherwise `let` followed by a
                    // line break is an identifier and ASI applies.
                    if (next.Type != TokenType.LBracket && next.NewlineBefore && !_strict)
                    {
                        return ParseExpressionStatement();
                    }

                    Report("lexical-in-single-statement", Messages.SingleStatementLexical, start, t.End);
                    Advance();
                    VariableDeclaration junk = ParseVariableDeclarations(VarKind.Let, start, noIn: false, forHead: false);
                    ConsumeSemicolon();
                    return junk;
                }
            }
            else if (t.Text == "async")
            {
                Token next = PeekToken();
                if (next.Type == TokenType.Function && !next.NewlineBefore)
                {
                    Advance();
                    return ParseFunctionDeclarationStatement(ctx, start, isAsync: true);
                }
            }
        }

        // Labelled statement: Identifier ':'
        if (PeekToken().Type == TokenType.Colon)
        {
            return ParseLabeled(ctx);
        }

        return ParseExpressionStatement();
    }

    private ExpressionStatement ParseExpressionStatement()
    {
        int start = _tok.Start;
        Token first = _tok;
        bool mayBeDirective = _inPrologue && first.Type == TokenType.String;
        _inPrologue = false;
        Expression e = ParseExpression(noIn: false);
        string? directive = null;
        if (mayBeDirective && e is StringLiteral && !e.IsParenthesized && e.Start == first.Start && e.End == first.End)
        {
            directive = _s.Substring(first.Start + 1, first.End - first.Start - 2);
        }

        ConsumeSemicolon();
        return new ExpressionStatement(start, Math.Max(_prevEnd, e.End), e, directive);
    }

    private BlockStatement ParseBlock(bool newScope)
    {
        int start = _tok.Start;
        Scope? scope = null;
        if (newScope)
        {
            scope = PushScope(ScopeKind.Block, start);
        }

        Expect(TokenType.LBrace);
        var body = new List<Node>();
        ParseBlockBody(body);
        int end = _tok.End;
        if (!Expect(TokenType.RBrace))
        {
            end = _prevEnd;
        }

        if (newScope)
        {
            PopScope();
        }

        return new BlockStatement(start, end, body, scope);
    }

    private void ParseBlockBody(List<Node> body)
    {
        while (!Is(TokenType.RBrace) && !Is(TokenType.EndOfInput))
        {
            int before = _tok.Start;
            body.Add(ParseStatementListItem());
            if (_tok.Start == before)
            {
                SkipStuckToken(TokenType.RBrace);
            }
        }
    }

    // ---- declarations ----------------------------------------------------------------------

    private VariableDeclaration ParseVariableDeclarations(VarKind kind, int start, bool noIn, bool forHead)
    {
        var list = new List<VariableDeclarator>();
        do
        {
            int dstart = _tok.Start;
            Node id = ParseBindingTarget(kind == VarKind.Var ? BindingKind.Var : kind == VarKind.Let ? BindingKind.Let : BindingKind.Const, out _);
            Expression? init = null;
            if (Eat(TokenType.Assign))
            {
                init = ParseAssignment(noIn);
                if (init is FunctionNode or ClassNode && id is Identifier idn)
                {
                    // name inference is the executor's job
                    _ = idn;
                }
            }
            else if (!forHead || !(Is(TokenType.In) || IsContextual("of")))
            {
                if (kind == VarKind.Const)
                {
                    Report("missing-initializer", Messages.MissingConstInit, id.Start, id.End);
                }
                else if (id is not Identifier)
                {
                    Report("missing-initializer", Messages.MissingDestructuringInit, id.Start, id.End);
                }
            }

            list.Add(new VariableDeclarator(dstart, Math.Max(_prevEnd, id.End), id, init));
        }
        while (Eat(TokenType.Comma));
        return new VariableDeclaration(start, _prevEnd, kind, list);
    }

    private Node ParseFunctionDeclarationStatement(StmtCtx ctx, int start, bool isAsync)
    {
        // ctx decides where a declaration is allowed (ECMA-262 14.6, 14.13, B.3.2).
        bool inList = ctx is StmtCtx.List or StmtCtx.LabelledInList;
        Token functionTok = _tok;
        Token after = PeekToken();
        bool isGenerator = after.Type == TokenType.Star;
        if (!inList)
        {
            string? message = null;
            if (isAsync)
            {
                message = Messages.AsyncFunctionPosition;
            }
            else if (isGenerator)
            {
                message = Messages.GeneratorPosition;
            }
            else if (_strict)
            {
                message = Messages.StrictFunctionPosition;
            }
            else if (ctx != StmtCtx.IfBody)
            {
                message = Messages.SloppyFunctionPosition;
            }

            if (message is not null)
            {
                Report("function-position", message, functionTok.Start, functionTok.End);
            }
        }
        else if (ctx == StmtCtx.LabelledInList && (isAsync || isGenerator))
        {
            Report("function-position", isAsync ? Messages.AsyncFunctionPosition : Messages.GeneratorPosition, functionTok.Start, functionTok.End);
        }
        else if (ctx == StmtCtx.LabelledInList && _strict)
        {
            Report("function-position", Messages.StrictFunctionPosition, functionTok.Start, functionTok.End);
        }

        if (!inList && ctx == StmtCtx.IfBody)
        {
            // B.3.3: `if (x) function f(){}` behaves as if the declaration were in a block.
            Scope blockScope = PushScope(ScopeKind.Block, start);
            FunctionDeclaration d = ParseFunctionDeclaration(start, isAsync);
            PopScope();
            return new BlockStatement(start, d.End, [d], blockScope);
        }

        return ParseFunctionDeclaration(start, isAsync);
    }

    // ---- control flow ----------------------------------------------------------------------

    private Expression ParseParenthesizedCondition()
    {
        Expect(TokenType.LParen);
        Expression e = ParseExpression(noIn: false);
        Expect(TokenType.RParen);
        return e;
    }

    private IfStatement ParseIf()
    {
        int start = _tok.Start;
        Advance();
        Expression test = ParseParenthesizedCondition();
        Node cons = ParseStatement(StmtCtx.IfBody);
        Node? alt = null;
        if (Eat(TokenType.Else))
        {
            alt = ParseStatement(StmtCtx.IfBody);
        }

        return new IfStatement(start, _prevEnd, test, cons, alt);
    }

    private void MarkLoopLabels(int statementStart)
    {
        List<LabelInfo> labels = _fn.Labels;
        int target = statementStart;
        for (int i = labels.Count - 1; i >= 0; i--)
        {
            if (labels[i].BodyStart == target)
            {
                labels[i].IsLoop = true;
                target = labels[i].LabelStart;
            }
        }
    }

    private Node ParseLoopBody()
    {
        _fn.LoopDepth++;
        _fn.BreakDepth++;
        Node body = ParseStatement(StmtCtx.Single);
        _fn.LoopDepth--;
        _fn.BreakDepth--;
        return body;
    }

    private WhileStatement ParseWhile()
    {
        int start = _tok.Start;
        MarkLoopLabels(start);
        Advance();
        Expression test = ParseParenthesizedCondition();
        Node body = ParseLoopBody();
        return new WhileStatement(start, _prevEnd, test, body);
    }

    private DoWhileStatement ParseDoWhile()
    {
        int start = _tok.Start;
        MarkLoopLabels(start);
        Advance();
        Node body = ParseLoopBody();
        Expect(TokenType.While);
        Expression test = ParseParenthesizedCondition();
        // 12.10.1: a semicolon after do-while is inserted if missing, even without a line break.
        Eat(TokenType.Semicolon);
        return new DoWhileStatement(start, _prevEnd, body, test);
    }

    private Node ParseFor()
    {
        int start = _tok.Start;
        MarkLoopLabels(start);
        Advance();
        bool isAwait = false;
        if (IsContextual("await"))
        {
            if (_fn.IsAsync)
            {
                isAwait = true;
            }
            else
            {
                Report("unexpected-token", Messages.UnexpectedReserved, _tok.Start, _tok.End);
                isAwait = true;
            }

            Advance();
        }

        Expect(TokenType.LParen);
        Scope forScope = PushScope(ScopeKind.For, start);
        try
        {
            Node? init = null;
            int initStart = _tok.Start;
            bool lexicalHead = false;
            if (Is(TokenType.Semicolon))
            {
                // no init
            }
            else if (Is(TokenType.Var))
            {
                Advance();
                init = ParseVariableDeclarations(VarKind.Var, initStart, noIn: true, forHead: true);
            }
            else if (Is(TokenType.Const))
            {
                Advance();
                init = ParseVariableDeclarations(VarKind.Const, initStart, noIn: true, forHead: true);
                lexicalHead = true;
            }
            else if (IsContextual("let") && IsLetDeclarationStartInFor())
            {
                Advance();
                init = ParseVariableDeclarations(VarKind.Let, initStart, noIn: true, forHead: true);
                lexicalHead = true;
            }
            else
            {
                bool startsWithLet = IsContextual("let");
                bool startsWithAsync = IsContextual("async") && !_tok.Escaped;
                var cover = new CoverErrors();
                Expression e;
                if (startsWithAsync && PeekToken() is { Type: TokenType.Identifier, Text: "of", Escaped: false })
                {
                    // `for (async of ...)` / `for (async of => x;;)`: `async` is a plain identifier unless an arrow follows `of`.
                    Token asyncTok = _tok;
                    Advance();
                    if (PeekToken().Type == TokenType.Arrow)
                    {
                        e = ParseAsyncArrowSingleParam(initStart);
                    }
                    else
                    {
                        e = new Identifier(asyncTok.Start, asyncTok.End, "async");
                    }
                }
                else
                {
                    e = ParseExpression(noIn: true, cover);
                }

                if (Is(TokenType.In) || IsContextual("of"))
                {
                    bool isOf = !Is(TokenType.In);
                    if (isOf && startsWithLet)
                    {
                        Report("for-of-let", Messages.ForOfLet, initStart, e.End);
                    }

                    if (isOf && startsWithAsync && !isAwait && e is Identifier { Name: "async" } && !e.IsParenthesized)
                    {
                        Report("for-of-async", Messages.ForOfAsync, initStart, e.End);
                    }

                    Node target = ToAssignableTarget(e, cover, isForHead: true);
                    return ParseForInOfRest(start, target, isOf, isAwait, forScope, lexicalHead: false);
                }

                CheckExpressionErrors(cover);
                init = e;
            }

            if (init is VariableDeclaration vd && (Is(TokenType.In) || IsContextual("of")))
            {
                bool isOf = !Is(TokenType.In);
                if (vd.Declarations.Count != 1)
                {
                    Report("for-in-of-bindings", Messages.InvalidLhsInFor, vd.Start, vd.End);
                }
                else if (vd.Declarations[0].Init is { } declInit)
                {
                    string loop = isOf ? "for-of" : "for-in";
                    bool allowed = !isOf && !_strict && vd.DeclarationKind == VarKind.Var && vd.Declarations[0].Id is Identifier;
                    if (!allowed)
                    {
                        Report("for-in-of-initializer", Messages.Format(Messages.ForInOfInit, loop), vd.Declarations[0].Id.Start, declInit.End);
                    }
                }

                return ParseForInOfRest(start, vd, isOf, isAwait, forScope, lexicalHead);
            }

            if (isAwait)
            {
                Unexpected();
            }

            Expect(TokenType.Semicolon);
            Expression? test = Is(TokenType.Semicolon) ? null : ParseExpression(noIn: false);
            Expect(TokenType.Semicolon);
            Expression? update = Is(TokenType.RParen) ? null : ParseExpression(noIn: false);
            Expect(TokenType.RParen);
            Node body = ParseLoopBody();
            return new ForStatement(start, _prevEnd, init, test, update, body, lexicalHead ? forScope : null);
        }
        finally
        {
            PopScope();
        }
    }

    private bool IsLetDeclarationStartInFor()
    {
        Token next = PeekToken();
        if (next.Type is TokenType.LBracket or TokenType.LBrace)
        {
            return true;
        }

        if (next.Type == TokenType.Identifier)
        {
            // `for (let in x)` and `for (let of x)` ... `let` is an identifier only before `in`; `of` binds as a name.
            return true;
        }

        return false;
    }

    private Node ParseForInOfRest(int start, Node left, bool isOf, bool isAwait, Scope forScope, bool lexicalHead)
    {
        if (isAwait && !isOf)
        {
            Unexpected();
        }

        Advance(); // `in` or `of`
        Expression right = isOf ? ParseAssignment(noIn: false) : ParseExpression(noIn: false);
        Expect(TokenType.RParen);
        Node body = ParseLoopBody();
        Scope? scope = lexicalHead ? forScope : null;
        if (isOf)
        {
            return new ForOfStatement(start, _prevEnd, left, right, body, isAwait, scope);
        }

        return new ForInStatement(start, _prevEnd, left, right, body, scope);
    }

    private Node ParseBreakContinue(bool isBreak)
    {
        int start = _tok.Start;
        Token kw = _tok;
        Advance();
        Identifier? label = null;
        if (Is(TokenType.Identifier) && !_tok.NewlineBefore)
        {
            label = ParseLabelReference();
        }

        ConsumeSemicolon();
        if (label is not null)
        {
            LabelInfo? found = null;
            foreach (LabelInfo l in _fn.Labels)
            {
                if (l.Name == label.Name)
                {
                    found = l;
                }
            }

            if (found is null)
            {
                Report("undefined-label", Messages.Format(Messages.UndefinedLabel, label.Name), label.Start, label.End);
            }
            else if (!isBreak && !found.IsLoop)
            {
                Report("illegal-continue", Messages.Format(Messages.IllegalContinueTarget, label.Name), label.Start, label.End);
            }
        }
        else if (isBreak ? _fn.BreakDepth == 0 : _fn.LoopDepth == 0)
        {
            Report(isBreak ? "illegal-break" : "illegal-continue", isBreak ? Messages.IllegalBreak : Messages.IllegalContinue, kw.Start, kw.End);
        }

        return isBreak
            ? new BreakStatement(start, _prevEnd, label)
            : new ContinueStatement(start, _prevEnd, label);
    }

    private Identifier ParseLabelReference()
    {
        Token t = _tok;
        CheckIdentifierReference(t);
        Advance();
        return new Identifier(t.Start, t.End, t.Text!);
    }

    private ReturnStatement ParseReturn()
    {
        int start = _tok.Start;
        Token kw = _tok;
        if (!_fn.CanReturn)
        {
            Report("illegal-return", Messages.IllegalReturn, kw.Start, kw.End);
        }

        Advance();
        Expression? arg = null;
        if (!Is(TokenType.Semicolon) && !Is(TokenType.RBrace) && !Is(TokenType.EndOfInput) && !_tok.NewlineBefore)
        {
            arg = ParseExpression(noIn: false);
        }

        ConsumeSemicolon();
        return new ReturnStatement(start, _prevEnd, arg);
    }

    private WithStatement ParseWith()
    {
        int start = _tok.Start;
        Token kw = _tok;
        if (_strict)
        {
            Report("strict-with", Messages.StrictWith, kw.Start, kw.End);
        }

        Advance();
        Expect(TokenType.LParen);
        Expression obj = ParseExpression(noIn: false);
        Expect(TokenType.RParen);
        for (Scope? s = _scope; s is not null; s = s.Parent)
        {
            s.ContainsWith = true;
        }

        Node body = ParseStatement(StmtCtx.Single);
        return new WithStatement(start, _prevEnd, obj, body);
    }

    private ThrowStatement ParseThrow()
    {
        int start = _tok.Start;
        Advance();
        if (_tok.NewlineBefore)
        {
            Report("newline-after-throw", Messages.NewlineAfterThrow, _tok.Start, _tok.End);
        }

        Expression arg = ParseExpression(noIn: false);
        ConsumeSemicolon();
        return new ThrowStatement(start, _prevEnd, arg);
    }

    private SwitchStatement ParseSwitch()
    {
        int start = _tok.Start;
        Advance();
        Expression disc = ParseParenthesizedCondition();
        Scope scope = PushScope(ScopeKind.Switch, _tok.Start);
        var cases = new List<SwitchCase>();
        Expect(TokenType.LBrace);
        _fn.BreakDepth++;
        bool seenDefault = false;
        while (!Is(TokenType.RBrace) && !Is(TokenType.EndOfInput))
        {
            int cstart = _tok.Start;
            Expression? test = null;
            if (Is(TokenType.Case))
            {
                Advance();
                test = ParseExpression(noIn: false);
            }
            else if (Is(TokenType.Default))
            {
                if (seenDefault)
                {
                    Report("multiple-defaults", Messages.MultipleDefaults, _tok.Start, _tok.End);
                }

                seenDefault = true;
                Advance();
            }
            else
            {
                Diagnostic? d = Unexpected();
                NoteSkipped(d, _tok.Start, _tok.End);
                Advance();
                continue;
            }

            Expect(TokenType.Colon);
            var body = new List<Node>();
            while (!Is(TokenType.Case) && !Is(TokenType.Default) && !Is(TokenType.RBrace) && !Is(TokenType.EndOfInput))
            {
                int before = _tok.Start;
                body.Add(ParseStatementListItem());
                if (_tok.Start == before)
                {
                    SkipStuckToken(TokenType.RBrace);
                }
            }

            cases.Add(new SwitchCase(cstart, _prevEnd, test, body));
        }

        _fn.BreakDepth--;
        Expect(TokenType.RBrace);
        PopScope();
        return new SwitchStatement(start, _prevEnd, disc, cases, scope);
    }

    private TryStatement ParseTry()
    {
        int start = _tok.Start;
        Token kw = _tok;
        Advance();
        BlockStatement block = ParseBlock(newScope: true);
        CatchClause? handler = null;
        BlockStatement? finalizer = null;
        if (Is(TokenType.Catch))
        {
            int cstart = _tok.Start;
            Advance();
            Scope scope = PushScope(ScopeKind.Catch, cstart);
            Node? param = null;
            if (Eat(TokenType.LParen))
            {
                param = ParseBindingTarget(BindingKind.CatchParameter, out _);
                scope.SimpleCatchParameter = param is Identifier;
                Expect(TokenType.RParen);
            }

            // The block's declarations live in the catch scope so they clash with the parameter (14.15.1).
            int bstart = _tok.Start;
            Expect(TokenType.LBrace);
            var body = new List<Node>();
            ParseBlockBody(body);
            int bend = _tok.End;
            if (!Expect(TokenType.RBrace))
            {
                bend = _prevEnd;
            }

            PopScope();
            handler = new CatchClause(cstart, _prevEnd, param, new BlockStatement(bstart, bend, body, null), scope);
        }

        if (Is(TokenType.Finally))
        {
            Advance();
            finalizer = ParseBlock(newScope: true);
        }

        if (handler is null && finalizer is null)
        {
            // V8 points at the end of the try block.
            Diagnostic? d = Report("missing-catch-or-finally", Messages.MissingCatchOrFinally, _prevStart, _prevEnd);
            d?.SecondaryLabels.Add(new Label(new Span(kw.Start, kw.End), "this `try` needs one of them"));
        }

        _ = kw;
        return new TryStatement(start, _prevEnd, block, handler, finalizer);
    }

    private LabeledStatement ParseLabeled(StmtCtx ctx)
    {
        int start = _tok.Start;
        Token t = _tok;
        // The label is an IdentifierReference-like name (yield/await rules apply).
        CheckIdentifierReference(t);
        Advance();
        Advance(); // ':'
        var labelNode = new Identifier(t.Start, t.End, t.Text!);
        foreach (LabelInfo l in _fn.Labels)
        {
            if (l.Name == t.Text)
            {
                Report("duplicate-label", Messages.Format(Messages.DuplicateLabel, t.Text), t.Start, t.End);
            }
        }

        var info = new LabelInfo { Name = t.Text!, LabelStart = start, BodyStart = _tok.Start };
        _fn.Labels.Add(info);
        StmtCtx inner = ctx is StmtCtx.List or StmtCtx.LabelledInList ? StmtCtx.LabelledInList : StmtCtx.LabelledInSingle;
        Node body;
        if (Is(TokenType.Function))
        {
            body = ParseFunctionDeclarationStatement(inner, _tok.Start, isAsync: false);
        }
        else
        {
            body = ParseStatement(inner);
        }

        _fn.Labels.Remove(info);
        return new LabeledStatement(start, _prevEnd, labelNode, body);
    }
}
