using RecursiveAI.OneEcma.Diagnostics;
using System.Globalization;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

internal sealed partial class Parser
{
    private const BindingKind NoDeclare = (BindingKind)255;

    private sealed class ClassCtx
    {
        internal Dictionary<string, PrivateKind> Declared { get; } = new(StringComparer.Ordinal);

        internal List<(string Name, Span Span)> Refs { get; } = [];

        internal bool IsOuterEval { get; init; }
    }

    private readonly List<ClassCtx> _classes = [];
    private int _useStrictStart = -1;

    // ---- private names ---------------------------------------------------------------------

    private void RecordPrivateReference(string name, Span span)
    {
        if (_classes.Count == 0)
        {
            ReportSpan("undeclared-private", Messages.Format(Messages.UndeclaredPrivate, "#" + name), span);
            return;
        }

        _classes[^1].Refs.Add((name, span));
    }

    private void ResolvePrivateNamesAtEnd()
    {
        if (_classes.Count == 1 && _classes[0].IsOuterEval)
        {
            foreach (var (name, span) in _classes[0].Refs)
            {
                if (!_classes[0].Declared.ContainsKey(name))
                {
                    ReportSpan("undeclared-private", Messages.Format(Messages.UndeclaredPrivate, "#" + name), span);
                }
            }
        }
    }

    private void DeclarePrivate(ClassCtx ctx, string name, PrivateKind kind, Span span)
    {
        if (name == "constructor")
        {
            ReportSpan("private-constructor", "Classes may not have a private field named '#constructor'", span);
            return;
        }

        if (ctx.Declared.TryGetValue(name, out PrivateKind existing))
        {
            // 15.7.1: a getter and a setter of the same staticness may share a name.
            bool pair = (existing, kind) is (PrivateKind.Getter, PrivateKind.Setter) or (PrivateKind.Setter, PrivateKind.Getter)
                or (PrivateKind.StaticGetter, PrivateKind.StaticSetter) or (PrivateKind.StaticSetter, PrivateKind.StaticGetter);
            if (!pair)
            {
                ReportSpan("redeclaration", Messages.Format(Messages.DuplicateBinding, "#" + name), span);
            }
            else
            {
                ctx.Declared[name] = PrivateKind.Method; // both halves present: no further accessor may join
            }

            return;
        }

        ctx.Declared[name] = kind;
    }

    // ---- cover grammar support -------------------------------------------------------------

    private readonly struct CoverSnapshot
    {
        internal CoverSnapshot(FnState fn, Scope scope)
        {
            UsesThis = fn.UsesThis;
            UsesArguments = fn.UsesArguments;
            HasDirectEval = fn.HasDirectEval;
            YieldPos = fn.YieldPos;
            AwaitPos = fn.AwaitPos;
            AwaitIdentPos = fn.AwaitIdentPos;
            Scope = scope;
            ChildIndex = scope.Children.Count;
        }

        internal bool UsesThis { get; }

        internal bool UsesArguments { get; }

        internal bool HasDirectEval { get; }

        internal int YieldPos { get; }

        internal int AwaitPos { get; }

        internal int AwaitIdentPos { get; }

        internal Scope Scope { get; }

        internal int ChildIndex { get; }
    }

    private CoverSnapshot BeginCover()
    {
        var snap = new CoverSnapshot(_fn, _scope);
        _fn.UsesThis = false;
        _fn.UsesArguments = false;
        _fn.HasDirectEval = false;
        _fn.YieldPos = -1;
        _fn.AwaitPos = -1;
        _fn.AwaitIdentPos = -1;
        return snap;
    }

    /// <summary>Ends a cover section that turned out not to be arrow parameters: merge what it saw into the enclosing function.</summary>
    private void EndCoverAsExpression(in CoverSnapshot snap)
    {
        _fn.UsesThis |= snap.UsesThis;
        _fn.UsesArguments |= snap.UsesArguments;
        _fn.HasDirectEval |= snap.HasDirectEval;
        if (snap.YieldPos >= 0)
        {
            _fn.YieldPos = snap.YieldPos;
        }

        if (snap.AwaitPos >= 0)
        {
            _fn.AwaitPos = snap.AwaitPos;
        }

        if (snap.AwaitIdentPos >= 0)
        {
            _fn.AwaitIdentPos = snap.AwaitIdentPos;
        }
    }

    // ---- parenthesised expressions and arrow functions -------------------------------------

    private Expression ParseParenthesized(CoverErrors outer)
    {
        int start = _tok.Start;
        CoverSnapshot snap = BeginCover();
        var inner = new CoverErrors();
        List<Node> items = ParseCoverList(inner, isCall: false, out bool trailingComma, out int spreadNotLast, out int spreadPos);
        int rparenEnd = _prevEnd;
        if (Is(TokenType.Arrow))
        {
            return ParseArrowFromCover(start, items, isAsync: false, snap, inner, trailingComma, spreadNotLast);
        }

        EndCoverAsExpression(snap);
        if (items.Count == 0)
        {
            Report("unexpected-token", "Unexpected token ')'", rparenEnd - 1, rparenEnd);
            return new ErrorExpression(start, rparenEnd);
        }

        if (spreadPos >= 0)
        {
            Report("unexpected-token", "Unexpected token '...'", spreadPos, spreadPos + 3);
        }
        else if (trailingComma)
        {
            Report("unexpected-token", "Unexpected token ')'", rparenEnd - 1, rparenEnd);
        }

        CheckExpressionErrors(inner);
        Expression e;
        if (items.Count == 1 && items[0] is Expression single)
        {
            e = single;
        }
        else
        {
            var exprs = new List<Expression>(items.Count);
            foreach (Node n in items)
            {
                exprs.Add(n as Expression ?? new ErrorExpression(n.Start, n.End));
            }

            e = new SequenceExpression(exprs[0].Start, exprs[^1].End, exprs);
        }

        e.IsParenthesized = true;
        _ = outer;
        return e;
    }

    /// <summary>Parses <c>( item, item, ...rest )</c> where the opening parenthesis is the current token.</summary>
    private List<Node> ParseCoverList(CoverErrors inner, bool isCall, out bool trailingComma, out int spreadNotLast, out int spreadPos)
    {
        trailingComma = false;
        spreadNotLast = -1;
        spreadPos = -1;
        var items = new List<Node>();
        Advance();
        int lastSpread = -1;
        while (!Is(TokenType.RParen) && !Is(TokenType.EndOfInput))
        {
            if (lastSpread >= 0 && spreadNotLast < 0)
            {
                spreadNotLast = lastSpread;
            }

            int before = _tok.Start;
            if (Is(TokenType.Ellipsis))
            {
                int s = _tok.Start;
                Advance();
                Expression a = ParseAssignment(noIn: false, inner);
                items.Add(new SpreadElement(s, a.End, a));
                lastSpread = s;
                if (spreadPos < 0)
                {
                    spreadPos = s;
                }
            }
            else
            {
                items.Add(ParseAssignment(noIn: false, inner));
            }

            if (Is(TokenType.RParen))
            {
                break;
            }

            if (!Is(TokenType.Comma))
            {
                Diagnostic? d = isCall
                    ? Report("missing-paren-after-args", Messages.MissingParenAfterArgs, _tok.Start, Math.Max(_tok.End, _tok.Start))
                    : Unexpected();
                NoteInserted(d, _tok.Start, ")");
                if (_tok.Start == before || _tok.Type is TokenType.Semicolon or TokenType.RBrace or TokenType.RBracket)
                {
                    return items;
                }

                continue;
            }

            Advance();
            if (Is(TokenType.RParen))
            {
                trailingComma = true;
                if (lastSpread >= 0 && spreadNotLast < 0)
                {
                    spreadNotLast = lastSpread;
                }
            }
        }

        Expect(TokenType.RParen);
        return items;
    }

    private Expression ParseAsyncCallOrArrow(int start)
    {
        Token asyncTok = _tok;
        Advance(); // async
        CoverSnapshot snap = BeginCover();
        var inner = new CoverErrors();
        List<Node> items = ParseCoverList(inner, isCall: true, out bool trailingComma, out int spreadNotLast, out _);
        if (Is(TokenType.Arrow) && !_tok.NewlineBefore)
        {
            return ParseArrowFromCover(start, items, isAsync: true, snap, inner, trailingComma, spreadNotLast);
        }

        EndCoverAsExpression(snap);
        CheckExpressionErrors(inner);
        var callee = new Identifier(asyncTok.Start, asyncTok.End, "async");
        if (callee.Name == "async")
        {
            // `async(...)` is an ordinary call of a function named async.
        }

        return new CallExpression(start, _prevEnd, callee, items, false, false);
    }

    private FunctionNode ParseArrowFromIdentifier(int start)
    {
        Token t = _tok;
        CoverSnapshot snap = BeginCover();
        Advance();
        var id = new Identifier(t.Start, t.End, t.Text!);
        var inner = new CoverErrors();
        // `x => ...`: the identifier is checked as a binding name below.
        CheckBindingIdentifier(t);
        return ParseArrowFromCover(start, [id], isAsync: false, snap, inner, trailingComma: false, spreadNotLast: -1);
    }

    private Expression ParseAsyncArrowSingleParam(int start)
    {
        // `async` has been consumed; the current token is the parameter name.
        Token t = _tok;
        CoverSnapshot snap = BeginCover();
        Advance();
        var id = new Identifier(t.Start, t.End, t.Text!);
        if (!Is(TokenType.Arrow))
        {
            Unexpected();
            EndCoverAsExpression(snap);
            return id;
        }

        if (t.Text == "await")
        {
            Report("reserved-word", Messages.UnexpectedReserved, t.Start, t.End);
        }

        return ParseArrowFromCover(start, [id], isAsync: true, snap, new CoverErrors(), trailingComma: false, spreadNotLast: -1);
    }

    private FunctionNode ParseArrowFromCover(int start, List<Node> items, bool isAsync, in CoverSnapshot snap, CoverErrors inner, bool trailingComma, int spreadNotLast)
    {
        // The current token is `=>`.
        Token arrow = _tok;
        if (arrow.NewlineBefore)
        {
            Report("unexpected-token", "Unexpected token '=>'", arrow.Start, arrow.End);
        }

        if (spreadNotLast >= 0)
        {
            Report("rest-not-last", Messages.RestNotLastParameter, spreadNotLast, spreadNotLast + 3);
        }

        if (_fn.YieldPos >= 0)
        {
            int p = _fn.YieldPos;
            Report("yield-in-parameter", Messages.YieldInParameter, p, p + 5);
        }

        int awaitPos = _fn.AwaitPos;
        if (awaitPos >= 0)
        {
            Report("await-in-parameter", Messages.AwaitInParameter, awaitPos, awaitPos + 5);
        }

        if (isAsync && _fn.AwaitIdentPos >= 0)
        {
            Report("reserved-word", Messages.UnexpectedReserved, _fn.AwaitIdentPos, _fn.AwaitIdentPos + 5);
        }

        // Convert the cover list into parameters.
        var paramsList = new List<Node>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            Node n = items[i];
            if (n is SpreadElement sp)
            {
                Node arg = ToAssignable(sp.Argument, binding: true);
                if (arg is AssignmentPattern)
                {
                    Report("rest-initializer", Messages.RestParameterInit, arg.Start, arg.End);
                }

                paramsList.Add(new RestElement(sp.Start, sp.End, arg));
            }
            else
            {
                paramsList.Add(ToAssignable(n, binding: true));
            }
        }

        FnState outerFn = _fn;
        // Flags found while parsing the cover belong to the arrow (and still to the enclosing function).
        bool coverThis = outerFn.UsesThis;
        bool coverArgs = outerFn.UsesArguments;
        bool coverEval = outerFn.HasDirectEval;
        outerFn.UsesThis |= snap.UsesThis;
        outerFn.UsesArguments |= snap.UsesArguments;
        outerFn.HasDirectEval |= snap.HasDirectEval;
        outerFn.YieldPos = snap.YieldPos;
        outerFn.AwaitPos = snap.AwaitPos;
        if (snap.AwaitIdentPos >= 0)
        {
            outerFn.AwaitIdentPos = snap.AwaitIdentPos;
        }

        var fn = new FnState
        {
            Parent = outerFn,
            IsAsync = isAsync,
            IsArrow = true,
            CanReturn = true,
            AllowSuperProperty = outerFn.AllowSuperProperty,
            AllowSuperCall = outerFn.AllowSuperCall,
            AllowNewTarget = outerFn.AllowNewTarget,
            InClassFieldInit = outerFn.InClassFieldInit,
            UsesThis = coverThis,
            UsesArguments = coverArgs,
            HasDirectEval = coverEval,
        };

        Scope parentScope = _scope;
        Scope scope = PushScope(ScopeKind.Function, start);
        // Scopes created while parsing the cover (nested functions in defaults) belong inside the arrow.
        List<Scope> siblings = parentScope.Children;
        for (int i = snap.ChildIndex; i < siblings.Count - 1; i++)
        {
            Scope child = siblings[i];
            child.Parent = scope;
            scope.Children.Add(child);
        }

        if (siblings.Count - 1 > snap.ChildIndex)
        {
            siblings.RemoveRange(snap.ChildIndex, siblings.Count - 1 - snap.ChildIndex);
        }

        siblings.Remove(scope);
        siblings.Add(scope);
        if (snap.HasDirectEval || coverEval)
        {
            scope.ContainsDirectEval = true;
        }

        var bound = new List<Identifier>();
        foreach (Node p in paramsList)
        {
            CollectBoundIdentifiers(p, bound);
        }

        foreach (Identifier id in bound)
        {
            DeclareParameter(id.Name, id.Span);
        }

        bool simple = true;
        foreach (Node p in paramsList)
        {
            if (p is not Identifier)
            {
                simple = false;
            }
        }

        scope.HasParameterExpressions = !simple;
        Advance(); // =>
        _fn = fn;
        bool savedStrict = _strict;
        bool savedStrictFromDirective = _strictFromDirective;
        bool savedUseStrictFound = _useStrictFound;
        int savedUseStrictStart = _useStrictStart;
        _strictFromDirective = false;
        _useStrictFound = false;
        Node body;
        bool bodyIsBlock = Is(TokenType.LBrace);
        if (bodyIsBlock)
        {
            body = ParseFunctionBodyBlock(fn);
        }
        else
        {
            body = ParseAssignment(noIn: false);
        }

        bool viaDirective = _strictFromDirective;
        bool useStrict = _useStrictFound;
        bool strictNow = _strict;
        CheckParameterList(paramsList, bound, simple, allowDuplicates: false, viaDirective, useStrict, strictNow, null, _useStrictStart);
        _strict = savedStrict;
        _strictFromDirective = savedStrictFromDirective;
        _useStrictFound = savedUseStrictFound;
        _useStrictStart = savedUseStrictStart;
        scope.IsStrict = strictNow;
        PopScope();
        _fn = outerFn;
        outerFn.UsesThis |= fn.UsesThis;
        outerFn.UsesArguments |= fn.UsesArguments;
        outerFn.HasDirectEval |= fn.HasDirectEval;
        _ = trailingComma;
        _ = inner;
        _ = bodyIsBlock;
        return new FunctionNode(start, _prevEnd, null, paramsList, body, isAsync, false, true, FunctionFlavor.Arrow, strictNow, simple, scope, fn.UsesArguments, fn.UsesThis, fn.HasDirectEval);
    }

    // ---- functions -------------------------------------------------------------------------

    private FunctionDeclaration ParseFunctionDeclaration(int start, bool isAsync, bool allowAnonymous = false)
    {
        // The current token is `function`.
        Advance();
        bool isGen = Eat(TokenType.Star);
        Identifier? id = null;
        Token nameTok = _tok;
        if (Is(TokenType.Identifier))
        {
            CheckBindingIdentifier(nameTok);
            Advance();
            id = new Identifier(nameTok.Start, nameTok.End, nameTok.Text!);
        }
        else if (!allowAnonymous)
        {
            Report("function-name-required", Messages.FunctionNameRequired, _tok.Start, _tok.End);
        }

        FunctionNode fn = ParseFunctionRest(start, id, isAsync, isGen, FunctionFlavor.Normal, isExpression: false);
        var decl = new FunctionDeclaration(start, fn.End, fn, false);
        if (id is not null)
        {
            DeclareFunctionName(decl, id.Name, id.Span, !isAsync && !isGen);
        }

        return decl;
    }

    private FunctionNode ParseFunctionExpression(int start, bool isAsync)
    {
        Advance(); // function
        bool isGen = Eat(TokenType.Star);
        Identifier? id = null;
        if (Is(TokenType.Identifier))
        {
            Token nameTok = _tok;
            CheckFunctionExpressionName(nameTok, isGen, isAsync);

            Advance();
            id = new Identifier(nameTok.Start, nameTok.End, nameTok.Text!);
        }

        return ParseFunctionRest(start, id, isAsync, isGen, FunctionFlavor.Normal, isExpression: true);
    }

    // 15.2.1: the name of a function expression is bound inside the function, so the function's own
    // generator/async kind (not the enclosing code's) decides whether yield/await may be the name.
    private void CheckFunctionExpressionName(in Token t, bool isGen, bool isAsync)
    {
        string name = t.Text!;
        if (t.Escaped && Keywords.IsReserved(name))
        {
            Report("escaped-keyword", Messages.EscapedKeyword, t.Start, t.End);
        }
        else if (name == "yield" && (_strict || isGen))
        {
            Report(_strict ? "strict-reserved-word" : "reserved-word", _strict ? Messages.UnexpectedStrictReserved : "Unexpected identifier 'yield'", t.Start, t.End);
        }
        else if (name == "await" && (_module || isAsync))
        {
            Report("reserved-word", Messages.UnexpectedReserved, t.Start, t.End);
        }
        else if (_strict && name is "implements" or "interface" or "let" or "package" or "private" or "protected" or "public" or "static")
        {
            Report("strict-reserved-word", Messages.UnexpectedStrictReserved, t.Start, t.End);
        }
        else if (_strict && name is "eval" or "arguments")
        {
            Report("strict-eval-arguments", Messages.UnexpectedEvalOrArguments, t.Start, t.End);
        }
    }

    /// <summary>Parses <c>( params ) { body }</c> as a function (ECMA-262 15.2 - 15.8) with its own scope and state.</summary>
    private FunctionNode ParseFunctionRest(int start, Identifier? id, bool isAsync, bool isGen, FunctionFlavor flavor, bool isExpression)
    {
        Enter();
        FnState outer = _fn;
        bool savedStrict = _strict;
        bool savedFromDirective = _strictFromDirective;
        bool savedUseStrictFound = _useStrictFound;
        int savedUseStrictStart = _useStrictStart;
        _strictFromDirective = false;
        _useStrictFound = false;
        var fn = new FnState
        {
            Parent = outer,
            IsAsync = isAsync,
            IsGenerator = isGen,
            CanReturn = true,
            AllowNewTarget = true,
            AllowSuperProperty = flavor is not FunctionFlavor.Normal,
            AllowSuperCall = flavor == FunctionFlavor.DerivedConstructor,
        };
        _fn = fn;
        Scope scope = PushScope(ScopeKind.Function, start);
        var paramsList = new List<Node>();
        bool simple = true;
        fn.InParameters = true;
        if (Expect(TokenType.LParen))
        {
            ParseFormalParameters(paramsList, ref simple);
        }

        fn.InParameters = false;
        Expect(TokenType.RParen);
        scope.HasParameterExpressions = !simple;
        if (isGen && fn.YieldPos >= 0)
        {
            Report("yield-in-parameter", Messages.YieldInParameter, fn.YieldPos, fn.YieldPos + 5);
        }

        if (isAsync && fn.AwaitPos >= 0)
        {
            Report("await-in-parameter", Messages.AwaitInParameter, fn.AwaitPos, fn.AwaitPos + 5);
        }

        if (flavor == FunctionFlavor.Getter && paramsList.Count != 0)
        {
            Report("getter-params", Messages.GetterParams, paramsList[0].Start, paramsList[^1].End);
        }
        else if (flavor == FunctionFlavor.Setter)
        {
            if (paramsList.Count != 1)
            {
                Report("setter-params", Messages.SetterParams, paramsList.Count > 0 ? paramsList[0].Start : _prevEnd - 1, _prevEnd);
            }
            else if (paramsList[0] is RestElement)
            {
                Report("setter-rest", Messages.SetterRest, paramsList[0].Start, paramsList[0].End);
            }
        }

        FunctionNode result;
        if (!Is(TokenType.LBrace))
        {
            Unexpected();
            _strict = savedStrict;
            _strictFromDirective = savedFromDirective;
            _useStrictFound = savedUseStrictFound;
            _useStrictStart = savedUseStrictStart;
            PopScope();
            _fn = outer;
            Leave();
            return new FunctionNode(start, _prevEnd, id, paramsList, new BlockStatement(_tok.Start, _tok.Start, [], null), isAsync, isGen, false, flavor, _strict, simple, scope, false, false, false);
        }

        BlockStatement body = ParseFunctionBodyBlock(fn);
        bool viaDirective = _strictFromDirective;
        bool useStrict = _useStrictFound;
        bool strictNow = _strict;
        var bound = new List<Identifier>();
        foreach (Node p in paramsList)
        {
            CollectBoundIdentifiers(p, bound);
        }

        bool allowDup = !strictNow && simple && flavor == FunctionFlavor.Normal;
        CheckParameterList(paramsList, bound, simple, allowDup, viaDirective, useStrict, strictNow, id, _useStrictStart);
        scope.IsStrict = strictNow;
        _strict = savedStrict;
        _strictFromDirective = savedFromDirective;
        _useStrictFound = savedUseStrictFound;
        _useStrictStart = savedUseStrictStart;
        PopScope();
        _fn = outer;
        result = new FunctionNode(start, _prevEnd, id, paramsList, body, isAsync, isGen, false, flavor, strictNow, simple, scope, fn.UsesArguments, fn.UsesThis, fn.HasDirectEval);
        Leave();
        _ = isExpression;
        return result;
    }

    /// <summary>Parses <c>{ FunctionBody }</c> including its directive prologue; the caller has set up scope and state.</summary>
    private BlockStatement ParseFunctionBodyBlock(FnState fn)
    {
        int start = _tok.Start;
        Expect(TokenType.LBrace);
        var body = new List<Node>();
        FnState savedFn = _fn;
        _fn = fn;
        bool savedTop = _topLevel;
        _topLevel = false;
        ParseStatementsWithPrologue(body, TokenType.RBrace);
        _topLevel = savedTop;
        _fn = savedFn;
        int end = _tok.End;
        if (!Expect(TokenType.RBrace))
        {
            end = _prevEnd;
        }

        return new BlockStatement(start, end, body, null);
    }

    private void ParseFormalParameters(List<Node> paramsList, ref bool simple)
    {
        while (!Is(TokenType.RParen) && !Is(TokenType.EndOfInput))
        {
            int before = _tok.Start;
            if (Is(TokenType.Ellipsis))
            {
                int s = _tok.Start;
                Advance();
                Node target = ParseBindingTarget(BindingKind.Parameter, out _);
                if (Is(TokenType.Assign))
                {
                    Report("rest-initializer", Messages.RestParameterInit, _tok.Start, _tok.End);
                    Advance();
                    ParseAssignment(noIn: false);
                }

                paramsList.Add(new RestElement(s, _prevEnd, target));
                simple = false;
                if (Is(TokenType.Comma))
                {
                    Report("rest-not-last", Messages.RestNotLastParameter, s, _prevEnd);
                    while (Eat(TokenType.Comma))
                    {
                        if (Is(TokenType.RParen) || Is(TokenType.EndOfInput))
                        {
                            break;
                        }

                        ParseBindingElement(BindingKind.Parameter);
                    }
                }

                break;
            }

            Node el = ParseBindingElement(BindingKind.Parameter);
            paramsList.Add(el);
            if (el is not Identifier)
            {
                simple = false;
            }

            if (!Eat(TokenType.Comma))
            {
                if (!Is(TokenType.RParen))
                {
                    Diagnostic? d = Unexpected();
                    NoteInserted(d, _prevEnd, ",");
                    if (_tok.Start == before || _tok.Type is TokenType.LBrace or TokenType.Semicolon)
                    {
                        break;
                    }
                }
            }
        }
    }

    // 15.2.1 / 15.3.1 / 15.4.1 etc.: early errors that depend on the final strictness of the body.
    private void CheckParameterList(List<Node> paramsList, List<Identifier> bound, bool simple, bool allowDuplicates, bool viaDirective, bool useStrict, bool strictNow, Identifier? functionName, int useStrictStart)
    {
        if (useStrict && !simple)
        {
            int p = useStrictStart >= 0 ? useStrictStart : paramsList[0].Start;
            Report("use-strict-non-simple", Messages.UseStrictNonSimple, p, p + 12);
        }

        if (!allowDuplicates && bound.Count > 1)
        {
            if (bound.Count <= 8)
            {
                for (int i = 1; i < bound.Count; i++)
                {
                    for (int j = 0; j < i; j++)
                    {
                        if (bound[i].Name == bound[j].Name)
                        {
                            Report("duplicate-parameter", Messages.DuplicateParameter, bound[i].Start, bound[i].End);
                            j = i;
                        }
                    }
                }
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (Identifier id in bound)
                {
                    if (!seen.Add(id.Name))
                    {
                        Report("duplicate-parameter", Messages.DuplicateParameter, id.Start, id.End);
                    }
                }
            }
        }

        if (viaDirective && strictNow)
        {
            // The parameter names and function name were accepted while the code was still sloppy.
            foreach (Identifier id in bound)
            {
                CheckStrictBindingName(id);
            }

            if (functionName is not null)
            {
                CheckStrictBindingName(functionName);
            }
        }
    }

    private void CheckStrictBindingName(Identifier id)
    {
        if (id.Name is "eval" or "arguments")
        {
            Report("strict-eval-arguments", Messages.UnexpectedEvalOrArguments, id.Start, id.End);
        }
        else if (Keywords.IsStrictReserved(id.Name))
        {
            Report("strict-reserved-word", Messages.UnexpectedStrictReserved, id.Start, id.End);
        }
    }

    // ---- binding patterns ------------------------------------------------------------------

    private Node ParseBindingElement(BindingKind kind)
    {
        int start = _tok.Start;
        Node target = ParseBindingTarget(kind, out _);
        if (Is(TokenType.Assign))
        {
            Advance();
            Expression def = ParseAssignment(noIn: false);
            return new AssignmentPattern(start, def.End, target, def);
        }

        return target;
    }

    /// <summary>Parses a BindingIdentifier or binding pattern and declares its names according to <paramref name="kind"/> (<see cref="NoDeclare"/> to skip).</summary>
    private Node ParseBindingTarget(BindingKind kind, out int count)
    {
        count = 0;
        Enter();
        try
        {
            if (Is(TokenType.LBracket))
            {
                return ParseArrayBindingPattern(kind);
            }

            if (Is(TokenType.LBrace))
            {
                return ParseObjectBindingPattern(kind);
            }

            return ParseBindingIdentifier(kind);
        }
        finally
        {
            Leave();
        }
    }

    private Identifier ParseBindingIdentifier(BindingKind kind)
    {
        Token t = _tok;
        if (t.Type != TokenType.Identifier)
        {
            Unexpected();
            return new Identifier(t.Start, t.Start, "");
        }

        CheckBindingIdentifier(t);
        if (t.Text == "let" && kind is BindingKind.Let or BindingKind.Const or BindingKind.Class)
        {
            Report("let-binding", Messages.LetLexicallyBound, t.Start, t.End);
        }

        Advance();
        var id = new Identifier(t.Start, t.End, t.Text!);
        DeclareBinding(id, kind);
        return id;
    }

    private void DeclareBinding(Identifier id, BindingKind kind)
    {
        switch (kind)
        {
            case NoDeclare:
                break;
            case BindingKind.Var:
                DeclareVar(id.Name, id.Span);
                break;
            case BindingKind.Parameter:
                DeclareParameter(id.Name, id.Span);
                break;
            default:
                DeclareLexical(id.Name, kind, id.Span);
                break;
        }
    }

    private ArrayPattern ParseArrayBindingPattern(BindingKind kind)
    {
        int start = _tok.Start;
        Advance();
        var elements = new List<Node?>();
        while (!Is(TokenType.RBracket) && !Is(TokenType.EndOfInput))
        {
            if (Eat(TokenType.Comma))
            {
                elements.Add(null);
                continue;
            }

            int before = _tok.Start;
            if (Is(TokenType.Ellipsis))
            {
                int s = _tok.Start;
                Advance();
                Node target = ParseBindingTarget(kind, out _);
                elements.Add(new RestElement(s, _prevEnd, target));
                if (Is(TokenType.Comma))
                {
                    Report("rest-not-last", Messages.RestNotLastElement, _tok.Start, _tok.End);
                    Advance();
                    while (!Is(TokenType.RBracket) && !Is(TokenType.EndOfInput) && _tok.Start != before)
                    {
                        before = _tok.Start;
                        if (!Eat(TokenType.Comma))
                        {
                            ParseBindingElement(kind);
                        }
                    }
                }

                break;
            }

            elements.Add(ParseBindingElement(kind));
            if (!Is(TokenType.RBracket) && !Eat(TokenType.Comma))
            {
                Diagnostic? d = Unexpected();
                NoteInserted(d, _prevEnd, ",");
                if (_tok.Start == before || _tok.Type is TokenType.Semicolon or TokenType.RBrace or TokenType.RParen)
                {
                    break;
                }
            }
        }

        Expect(TokenType.RBracket);
        return new ArrayPattern(start, _prevEnd, elements);
    }

    private ObjectPattern ParseObjectBindingPattern(BindingKind kind)
    {
        int start = _tok.Start;
        Advance();
        var props = new List<Node>();
        while (!Is(TokenType.RBrace) && !Is(TokenType.EndOfInput))
        {
            int before = _tok.Start;
            if (Is(TokenType.Ellipsis))
            {
                int s = _tok.Start;
                Advance();
                Node target = ParseBindingIdentifier(kind);
                props.Add(new RestElement(s, _prevEnd, target));
                if (Is(TokenType.Comma))
                {
                    Report("rest-not-last", Messages.RestNotLastElement, _tok.Start, _tok.End);
                    Advance();
                }

                break;
            }

            int pstart = _tok.Start;
            Token keyTok = _tok;
            Node key = ParsePropertyKey(out bool computed);
            Node value;
            bool shorthand = false;
            if (Eat(TokenType.Colon))
            {
                value = ParseBindingElement(kind);
            }
            else
            {
                shorthand = true;
                if (computed || keyTok.Type != TokenType.Identifier)
                {
                    if (!(keyTok.Type >= TokenType.Break && !computed))
                    {
                        Unexpected(_tok);
                    }
                    else
                    {
                        Report("unexpected-token", UnexpectedMessage(keyTok), keyTok.Start, keyTok.End);
                    }

                    value = new Identifier(key.Start, key.End, "");
                }
                else
                {
                    CheckBindingIdentifier(keyTok);
                    if (keyTok.Text == "let" && kind is BindingKind.Let or BindingKind.Const or BindingKind.Class)
                    {
                        Report("let-binding", Messages.LetLexicallyBound, keyTok.Start, keyTok.End);
                    }

                    var id = new Identifier(keyTok.Start, keyTok.End, keyTok.Text!);
                    DeclareBinding(id, kind);
                    value = id;
                    if (Is(TokenType.Assign))
                    {
                        Advance();
                        Expression def = ParseAssignment(noIn: false);
                        value = new AssignmentPattern(id.Start, def.End, id, def);
                    }
                }
            }

            props.Add(new Property(pstart, _prevEnd, PropertyKind.Init, key, computed, value, shorthand));
            if (!Is(TokenType.RBrace) && !Eat(TokenType.Comma))
            {
                Diagnostic? d = Unexpected();
                NoteInserted(d, _prevEnd, ",");
                if (_tok.Start == before || _tok.Type is TokenType.Semicolon or TokenType.RParen or TokenType.RBracket)
                {
                    break;
                }
            }
        }

        Expect(TokenType.RBrace);
        return new ObjectPattern(start, _prevEnd, props);
    }

    /// <summary>Collects the identifiers a pattern binds (ECMA-262 8.2.1 BoundNames), in source order.</summary>
    private static void CollectBoundIdentifiers(Node n, List<Identifier> into)
    {
        switch (n)
        {
            case Identifier id:
                if (id.Name.Length > 0)
                {
                    into.Add(id);
                }

                break;
            case AssignmentPattern ap:
                CollectBoundIdentifiers(ap.Left, into);
                break;
            case RestElement re:
                CollectBoundIdentifiers(re.Argument, into);
                break;
            case ArrayPattern arr:
                foreach (Node? e in arr.Elements)
                {
                    if (e is not null)
                    {
                        CollectBoundIdentifiers(e, into);
                    }
                }

                break;
            case ObjectPattern obj:
                foreach (Node p in obj.Properties)
                {
                    if (p is Property prop)
                    {
                        CollectBoundIdentifiers(prop.Value, into);
                    }
                    else
                    {
                        CollectBoundIdentifiers(p, into);
                    }
                }

                break;
        }
    }

    // ---- reinterpreting expressions as patterns (cover grammar) -----------------------------

    private Node ToAssignableTarget(Expression e, CoverErrors ce, bool isForHead)
    {
        if (e is ArrayLiteral or ObjectLiteral && !e.IsParenthesized)
        {
            return ToAssignable(e, binding: false);
        }

        if (e is Identifier or MemberExpression || (e is CallExpression && !_strict))
        {
            CheckSimpleTarget(e, Messages.InvalidAssignTarget, e.Start, e.End);
            return e;
        }

        Report("invalid-assignment-target", isForHead ? Messages.InvalidForTarget : Messages.InvalidAssignTarget, e.Start, e.End);
        _ = ce;
        return e;
    }

    private Node ToAssignable(Node n, bool binding)
    {
        Enter();
        try
        {
            switch (n)
            {
                case Identifier id:
                    if (_strict && id.Name is "eval" or "arguments")
                    {
                        Report("strict-eval-arguments", Messages.UnexpectedEvalOrArguments, id.Start, id.End);
                    }

                    if (binding && id.IsParenthesized)
                    {
                        Report("invalid-destructuring-target", Messages.InvalidDestructuringTarget, id.Start, id.End);
                    }

                    return id;
                case MemberExpression m:
                    if (binding)
                    {
                        Report("invalid-destructuring-target", Messages.InvalidDestructuringTarget, m.Start, m.End);
                    }

                    return m;
                case ObjectLiteral o when !o.IsParenthesized:
                    return ObjectToPattern(o, binding);
                case ArrayLiteral a when !a.IsParenthesized:
                    return ArrayToPattern(a, binding);
                case AssignmentPattern ap:
                    ToAssignable(ap.Left, binding);
                    return ap;
                case AssignmentExpression { Operator: AssignOp.Assign, IsParenthesized: false } ae:
                    {
                        Node left = ae.Left;
                        if (binding && left is MemberExpression)
                        {
                            Report("invalid-destructuring-target", Messages.InvalidDestructuringTarget, left.Start, left.End);
                        }
                        else if (binding && left is Identifier lid)
                        {
                            ToAssignable(lid, binding: true);
                        }

                        return new AssignmentPattern(ae.Start, ae.End, left, ae.Right);
                    }

                default:
                    Report("invalid-destructuring-target", Messages.InvalidDestructuringTarget, n.Start, n.End);
                    return n;
            }
        }
        finally
        {
            Leave();
        }
    }

    private ArrayPattern ArrayToPattern(ArrayLiteral a, bool binding)
    {
        var elements = new List<Node?>(a.Elements.Count);
        for (int i = 0; i < a.Elements.Count; i++)
        {
            Node? el = a.Elements[i];
            if (el is null)
            {
                elements.Add(null);
            }
            else if (el is SpreadElement sp)
            {
                if (i != a.Elements.Count - 1 || _spreadWithTrailingComma.Contains(a))
                {
                    Report("rest-not-last", Messages.RestNotLastElement, sp.Start, sp.End);
                }

                Node arg = ToAssignable(sp.Argument, binding);
                if (arg is AssignmentPattern)
                {
                    Report("invalid-destructuring-target", Messages.InvalidDestructuringTarget, arg.Start, arg.End);
                }

                elements.Add(new RestElement(sp.Start, sp.End, arg));
            }
            else
            {
                elements.Add(ToAssignable(el, binding));
            }
        }

        return new ArrayPattern(a.Start, a.End, elements);
    }

    private ObjectPattern ObjectToPattern(ObjectLiteral o, bool binding)
    {
        var props = new List<Node>(o.Properties.Count);
        for (int i = 0; i < o.Properties.Count; i++)
        {
            Node p = o.Properties[i];
            if (p is SpreadElement sp)
            {
                if (i != o.Properties.Count - 1 || _spreadWithTrailingComma.Contains(o))
                {
                    Report("rest-not-last", Messages.RestNotLastElement, sp.Start, sp.End);
                }

                Node arg = sp.Argument;
                if (arg is not (Identifier or MemberExpression) || (binding && arg is not Identifier))
                {
                    Report("invalid-destructuring-target", Messages.InvalidDestructuringTarget, arg.Start, arg.End);
                }
                else
                {
                    arg = ToAssignable(arg, binding);
                }

                props.Add(new RestElement(sp.Start, sp.End, arg));
            }
            else if (p is Property prop)
            {
                if (prop.PropertyKind != PropertyKind.Init)
                {
                    Report("invalid-destructuring-target", Messages.InvalidDestructuringTarget, prop.Start, prop.End);
                    props.Add(prop);
                }
                else
                {
                    Node value = ToAssignable(prop.Value, binding);
                    props.Add(new Property(prop.Start, prop.End, PropertyKind.Init, prop.Key, prop.Computed, value, prop.Shorthand));
                }
            }
        }

        return new ObjectPattern(o.Start, o.End, props);
    }

    // ---- object literals -------------------------------------------------------------------

    private Node ParsePropertyKey(out bool computed)
    {
        computed = false;
        Token t = _tok;
        switch (t.Type)
        {
            case TokenType.String:
                Advance();
                CheckStringToken(t);
                return new StringLiteral(t.Start, t.End, t.Text!);
            case TokenType.Number:
                Advance();
                CheckNumberToken(t);
                return new NumberLiteral(t.Start, t.End, t.Number);
            case TokenType.BigInt:
                Advance();
                return new BigIntLiteral(t.Start, t.End, ParseBigInt(t.Text!));
            case TokenType.LBracket:
                {
                    Advance();
                    computed = true;
                    Expression e = ParseAssignment(noIn: false);
                    Expect(TokenType.RBracket);
                    return e;
                }

            default:
                if (IsIdentifierName(t))
                {
                    Advance();
                    return new Identifier(t.Start, t.End, NameOf(t));
                }

                Unexpected();
                return new Identifier(t.Start, t.Start, "");
        }
    }

    private bool IsModifierFollowedByName(bool newlineMatters)
    {
        Token next = PeekToken();
        if (next.Type is TokenType.Comma or TokenType.Colon or TokenType.LParen or TokenType.RBrace or TokenType.Assign or TokenType.Semicolon or TokenType.EndOfInput)
        {
            return false;
        }

        return !(newlineMatters && next.NewlineBefore);
    }

    private ObjectLiteral ParseObjectLiteral(CoverErrors ce)
    {
        int start = _tok.Start;
        Advance();
        var props = new List<Node>();
        bool seenProto = false;
        bool spreadTrailing = false;
        while (!Is(TokenType.RBrace) && !Is(TokenType.EndOfInput))
        {
            int before = _tok.Start;
            if (Is(TokenType.Ellipsis))
            {
                int s = _tok.Start;
                Advance();
                Expression a = ParseAssignment(noIn: false, ce);
                props.Add(new SpreadElement(s, a.End, a));
                if (Is(TokenType.Comma))
                {
                    spreadTrailing = true;
                }
            }
            else
            {
                props.Add(ParseObjectProperty(ce, ref seenProto));
            }

            if (Is(TokenType.RBrace))
            {
                break;
            }

            if (!Eat(TokenType.Comma))
            {
                Diagnostic? d = Unexpected();
                NoteInserted(d, _prevEnd, ",");
                if (_tok.Start == before || _tok.Type is TokenType.Semicolon or TokenType.RParen or TokenType.RBracket)
                {
                    break;
                }
            }
        }

        Expect(TokenType.RBrace);
        var lit = new ObjectLiteral(start, _prevEnd, props);
        if (spreadTrailing)
        {
            _spreadWithTrailingComma.Add(lit);
        }

        return lit;
    }

    private Property ParseObjectProperty(CoverErrors ce, ref bool seenProto)
    {
        int start = _tok.Start;
        bool isAsync = false;
        bool isGen = false;
        PropertyKind kind = PropertyKind.Init;
        Token first = _tok;
        if (first.Type == TokenType.Identifier && !first.Escaped)
        {
            if (first.Text is "get" or "set" && IsModifierFollowedByName(newlineMatters: false) && PeekToken().Type != TokenType.Star)
            {
                kind = first.Text == "get" ? PropertyKind.Get : PropertyKind.Set;
                Advance();
            }
            else if (first.Text == "async" && IsModifierFollowedByName(newlineMatters: true))
            {
                isAsync = true;
                Advance();
            }
        }

        if (kind == PropertyKind.Init && Is(TokenType.Star))
        {
            isGen = true;
            Advance();
        }

        Token keyTok = _tok;
        Node key = ParsePropertyKey(out bool computed);
        if (kind != PropertyKind.Init)
        {
            FunctionNode f = ParseFunctionRest(start, null, false, false, kind == PropertyKind.Get ? FunctionFlavor.Getter : FunctionFlavor.Setter, isExpression: true);
            return new Property(start, _prevEnd, kind, key, computed, f, false);
        }

        if (isAsync || isGen || Is(TokenType.LParen))
        {
            FunctionNode f = ParseFunctionRest(start, null, isAsync, isGen, FunctionFlavor.Method, isExpression: true);
            return new Property(start, _prevEnd, PropertyKind.Method, key, computed, f, false);
        }

        if (Eat(TokenType.Colon))
        {
            Expression value = ParseAssignment(noIn: false, ce);
            if (!computed && IsProtoKey(key))
            {
                if (seenProto && ce.DoubleProto < 0)
                {
                    ce.DoubleProto = key.Start;
                }

                seenProto = true;
            }

            return new Property(start, _prevEnd, PropertyKind.Init, key, computed, value, false);
        }

        // Shorthand `{ a }` or `{ a = 1 }` (CoverInitializedName).
        if (computed || keyTok.Type != TokenType.Identifier)
        {
            if (!computed && keyTok.Type >= TokenType.Break)
            {
                Report("unexpected-token", UnexpectedMessage(keyTok), keyTok.Start, keyTok.End);
            }
            else if (!Is(TokenType.RBrace) && !Is(TokenType.Comma))
            {
                Unexpected();
            }
            else if (keyTok.Type is TokenType.String or TokenType.Number or TokenType.BigInt || computed)
            {
                Report("unexpected-token", computed ? "Unexpected token '}'" : UnexpectedMessage(_tok), _tok.Start, _tok.End);
            }

            return new Property(start, _prevEnd, PropertyKind.Init, key, computed, new Identifier(key.Start, key.End, ""), true);
        }

        CheckIdentifierReference(keyTok);
        var id = new Identifier(keyTok.Start, keyTok.End, keyTok.Text!);
        if (keyTok.Text == "arguments")
        {
            NoteArgumentsReference(keyTok);
        }

        Node val = id;
        if (Is(TokenType.Assign))
        {
            if (ce.ShorthandAssign < 0)
            {
                ce.ShorthandAssign = _tok.Start;
                ce.ShorthandAssignEnd = _tok.End;
            }

            Advance();
            Expression def = ParseAssignment(noIn: false);
            val = new AssignmentPattern(id.Start, def.End, id, def);
        }

        return new Property(start, _prevEnd, PropertyKind.Init, key, false, val, true);
    }

    private static bool IsProtoKey(Node key) => key is Identifier { Name: "__proto__" } or StringLiteral { Value: "__proto__" };

    // ---- classes ---------------------------------------------------------------------------

    private ClassDeclaration ParseClassDeclaration()
    {
        int start = _tok.Start;
        ClassNode c = ParseClass(declaration: true, allowAnonymous: false);
        return new ClassDeclaration(start, c.End, c);
    }

    private ClassNode ParseClassExpression() => ParseClass(declaration: false, allowAnonymous: true);

    internal ClassNode ParseClass(bool declaration, bool allowAnonymous)
    {
        Enter();
        int start = _tok.Start;
        Advance(); // class
        bool savedStrict = _strict;
        _strict = true;
        Identifier? id = null;
        if (Is(TokenType.Identifier) && !(IsContextual("extends")))
        {
            Token nameTok = _tok;
            CheckBindingIdentifier(nameTok);
            if (nameTok.Text == "let")
            {
                Report("let-binding", Messages.LetLexicallyBound, nameTok.Start, nameTok.End);
            }

            Advance();
            id = new Identifier(nameTok.Start, nameTok.End, nameTok.Text!);
            if (declaration)
            {
                DeclareLexical(id.Name, BindingKind.Class, id.Span);
            }
        }
        else if (!allowAnonymous)
        {
            Unexpected();
        }

        Scope scope = PushScope(ScopeKind.Class, start);
        scope.IsStrict = true;
        if (id is not null)
        {
            scope.Add(id.Name, BindingKind.Class, id.Span);
        }

        Expression? superClass = null;
        if (Eat(TokenType.Extends))
        {
            superClass = ParseLeftHandSide(new CoverErrors());
            if (superClass is FunctionNode { IsArrow: true, IsParenthesized: false })
            {
                Report("unexpected-token", "Unexpected token '=>'", superClass.Start, superClass.Start + 1);
            }
        }

        var ctx = new ClassCtx();
        _classes.Add(ctx);
        scope._privateNames = ctx.Declared;
        var members = new List<Node>();
        bool seenConstructor = false;
        Expect(TokenType.LBrace);
        while (!Is(TokenType.RBrace) && !Is(TokenType.EndOfInput))
        {
            if (Eat(TokenType.Semicolon))
            {
                continue;
            }

            int before = _tok.Start;
            Node? m = ParseClassElement(ctx, superClass is not null, ref seenConstructor);
            if (m is not null)
            {
                members.Add(m);
            }

            if (_tok.Start == before)
            {
                SkipStuckToken(TokenType.RBrace);
            }
        }

        Expect(TokenType.RBrace);
        _classes.RemoveAt(_classes.Count - 1);
        foreach (var (name, span) in ctx.Refs)
        {
            if (ctx.Declared.ContainsKey(name))
            {
                continue;
            }

            if (_classes.Count > 0)
            {
                _classes[^1].Refs.Add((name, span));
            }
            else
            {
                ReportSpan("undeclared-private", Messages.Format(Messages.UndeclaredPrivate, "#" + name), span);
            }
        }

        PopScope();
        _strict = savedStrict;
        Leave();
        return new ClassNode(start, _prevEnd, id, superClass, members, scope);
    }

    private static string? StaticKeyName(Node key, bool computed)
    {
        if (computed)
        {
            return null;
        }

        return key switch { Identifier i => i.Name, StringLiteral s => s.Value, _ => null };
    }

    private Node? ParseClassElement(ClassCtx ctx, bool hasHeritage, ref bool seenConstructor)
    {
        int start = _tok.Start;
        bool isStatic = false;
        Token first = _tok;
        if (first.Type == TokenType.Identifier && !first.Escaped && first.Text == "static")
        {
            Token next = PeekToken();
            if (next.Type == TokenType.LBrace)
            {
                Advance();
                return ParseStaticBlock(start);
            }

            if (next.Type is not (TokenType.LParen or TokenType.Assign or TokenType.Semicolon or TokenType.RBrace or TokenType.EndOfInput))
            {
                isStatic = true;
                Advance();
            }
        }

        bool isAsync = false;
        bool isGen = false;
        ClassMemberKind kind = ClassMemberKind.Method;
        Token mod = _tok;
        if (mod.Type == TokenType.Identifier && !mod.Escaped)
        {
            if (mod.Text is "get" or "set" && IsModifierFollowedByName(newlineMatters: false) && PeekToken().Type != TokenType.Star)
            {
                kind = mod.Text == "get" ? ClassMemberKind.Get : ClassMemberKind.Set;
                Advance();
            }
            else if (mod.Text == "async" && IsModifierFollowedByName(newlineMatters: true))
            {
                isAsync = true;
                Advance();
            }
        }

        if (kind == ClassMemberKind.Method && Is(TokenType.Star))
        {
            isGen = true;
            Advance();
        }

        Token keyTok = _tok;
        Node key;
        bool computed = false;
        if (Is(TokenType.PrivateName))
        {
            Advance();
            key = new PrivateIdentifier(keyTok.Start, keyTok.End, keyTok.Text!);
        }
        else
        {
            key = ParsePropertyKey(out computed);
        }

        string? name = StaticKeyName(key, computed);
        bool isMethod = isAsync || isGen || kind != ClassMemberKind.Method || Is(TokenType.LParen);
        if (isMethod)
        {
            FunctionFlavor flavor = kind switch
            {
                ClassMemberKind.Get => FunctionFlavor.Getter,
                ClassMemberKind.Set => FunctionFlavor.Setter,
                _ => FunctionFlavor.Method,
            };
            bool isCtor = !isStatic && key is not PrivateIdentifier && name == "constructor";
            if (isCtor)
            {
                if (kind != ClassMemberKind.Method)
                {
                    Report("constructor-accessor", Messages.ConstructorAccessor, key.Start, key.End);
                }
                else if (isGen || isAsync)
                {
                    Report("constructor-special", Messages.Format(Messages.ConstructorSpecial, isGen ? " generator" : "n async method"), key.Start, key.End);
                }
                else
                {
                    if (seenConstructor)
                    {
                        Report("duplicate-constructor", Messages.DuplicateConstructor, key.Start, key.End);
                    }

                    seenConstructor = true;
                    kind = ClassMemberKind.Constructor;
                    flavor = hasHeritage ? FunctionFlavor.DerivedConstructor : FunctionFlavor.BaseConstructor;
                }
            }

            if (isStatic && name == "prototype")
            {
                Report("static-prototype", Messages.StaticPrototype, key.Start, key.End);
            }

            if (key is PrivateIdentifier pk)
            {
                PrivateKind pkind = (kind, isStatic) switch
                {
                    (ClassMemberKind.Get, false) => PrivateKind.Getter,
                    (ClassMemberKind.Set, false) => PrivateKind.Setter,
                    (ClassMemberKind.Get, true) => PrivateKind.StaticGetter,
                    (ClassMemberKind.Set, true) => PrivateKind.StaticSetter,
                    (_, false) => PrivateKind.Method,
                    _ => PrivateKind.StaticMethod,
                };
                DeclarePrivate(ctx, pk.Name, pkind, pk.Span);
            }

            FunctionNode f = ParseFunctionRest(start, null, isAsync, isGen, flavor, isExpression: true);
            return new MethodDefinition(start, _prevEnd, kind, key, computed, isStatic, f);
        }

        // A field.
        if (name == "constructor" && key is not PrivateIdentifier)
        {
            Report("field-constructor", Messages.FieldConstructor, key.Start, key.End);
        }
        else if (isStatic && name == "prototype")
        {
            Report("static-prototype", Messages.StaticPrototype, key.Start, key.End);
        }

        if (key is PrivateIdentifier fk)
        {
            DeclarePrivate(ctx, fk.Name, isStatic ? PrivateKind.StaticField : PrivateKind.Field, fk.Span);
        }

        Expression? init = null;
        Scope? initScope = null;
        if (Is(TokenType.Assign))
        {
            Advance();
            FnState outer = _fn;
            var fn = new FnState
            {
                Parent = outer,
                CanReturn = false,
                AllowNewTarget = true,
                AllowSuperProperty = true,
                InClassFieldInit = true,
            };
            _fn = fn;
            initScope = PushScope(ScopeKind.ClassFieldInitializer, _tok.Start);
            init = ParseAssignment(noIn: false);
            PopScope();
            _fn = outer;
        }

        ConsumeSemicolon();
        return new PropertyDefinition(start, _prevEnd, key, computed, isStatic, init, initScope);
    }

    private StaticBlock ParseStaticBlock(int start)
    {
        // The current token is `{`.
        FnState outer = _fn;
        var fn = new FnState
        {
            Parent = outer,
            CanReturn = false,
            AllowNewTarget = true,
            AllowSuperProperty = true,
            InClassFieldInit = true,
            InStaticBlock = true,
        };
        _fn = fn;
        Scope scope = PushScope(ScopeKind.ClassStaticBlock, start);
        Advance();
        var body = new List<Node>();
        ParseBlockBody(body);
        Expect(TokenType.RBrace);
        PopScope();
        _fn = outer;
        return new StaticBlock(start, _prevEnd, body, scope);
    }
}
