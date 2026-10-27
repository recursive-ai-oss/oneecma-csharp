using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.ExecutorSpike.Closure;

/// <summary>Turns the syntax tree into a tree of executable nodes (executor shape A).</summary>
internal sealed class ClosureCompiler
{
    private const int LongChain = 32;

    private readonly Realm _realm;
    private readonly string _src;
    private readonly Analysis _an;
    private Scope _cur;
    private bool _top;

    internal ClosureCompiler(Realm realm, string src, Syntax.Program program)
    {
        _realm = realm;
        _src = src;
        _an = new Analysis(program);
        _cur = program.Scope;
        Program = program;
    }

    internal Syntax.Program Program { get; }

    internal sealed class ProgramCode
    {
        internal ScopeEntry Entry = new();
        internal SNode[] Body = [];
        internal int NumRegs;
    }

    internal ProgramCode CompileProgram()
    {
        var pc = new ProgramCode { NumRegs = _an.LayoutOf(Program).NumRegs };
        _cur = Program.Scope;
        pc.Entry = MakeEntry(Program.Scope, CompileUtil.HoistedFunctions(Program.Body));
        _top = true;
        pc.Body = CompileList(Program.Body);
        _top = false;
        return pc;
    }

    private static XNode At(XNode n, Node a)
    {
        n.Start = a.Start;
        n.End = a.End;
        return n;
    }

    private static SNode AtS(SNode n, Node a)
    {
        n.Start = a.Start;
        n.End = a.End;
        return n;
    }

    // ---- scopes and functions -------------------------------------------------------------

    private ScopeEntry MakeEntry(Scope? s, List<FunctionDeclaration> hoists)
    {
        var e = new ScopeEntry();
        if (s is null)
        {
            return e;
        }

        e.EnvSize = _an.HasEnv(s) ? _an.EnvSizeOf(s) : 0;
        var tdzEnv = new List<int>();
        var tdzReg = new List<int>();
        foreach (Binding b in s.Bindings)
        {
            Loc loc = _an.LocOf(b);
            if (loc.Tdz)
            {
                (loc.InEnv ? tdzEnv : tdzReg).Add(loc.Slot);
            }
        }

        e.TdzEnvSlots = [.. tdzEnv];
        e.TdzRegs = [.. tdzReg];
        var hs = new List<HoistA>();
        foreach (FunctionDeclaration fd in hoists)
        {
            Binding b = s.Lookup(fd.Function.Id!.Name)!;
            Loc loc = _an.LocOf(b);
            hs.Add(new HoistA(loc.InEnv, loc.Slot, CompileFunction(fd.Function)));
        }

        e.Hoists = [.. hs];
        return e;
    }

    private FuncCodeA CompileFunction(FunctionNode fn)
    {
        _an.LayoutFunction(fn);
        Scope savedCur = _cur;
        bool savedTop = _top;
        _top = false;
        _cur = fn.Scope;
        var code = new FuncCodeA
        {
            Name = fn.Id?.Name ?? string.Empty,
            NParams = fn.Params.Count,
            NumRegs = _an.LayoutOf(fn).NumRegs,
            IsArrow = fn.IsArrow,
            IsCtor = fn.Flavor == FunctionFlavor.Normal && !fn.IsArrow,
            Source = _src,
        };
        if (fn.Body is Expression e)
        {
            code.Entry = MakeEntry(fn.Scope, []);
            code.ExprBody = CompileExpr(e);
        }
        else
        {
            var body = (BlockStatement)fn.Body;
            code.Entry = MakeEntry(fn.Scope, CompileUtil.HoistedFunctions(body.Body));
            code.Body = CompileList(body.Body);
        }

        var cp = new List<(int, int)>();
        foreach ((int reg, Loc loc) in _an.CapturedParams(fn))
        {
            cp.Add((reg, loc.Slot));
        }

        code.CapturedParams = [.. cp];
        _cur = savedCur;
        _top = savedTop;
        return code;
    }

    // ---- variables ------------------------------------------------------------------------

    private VarRef MakeRef(Identifier id)
    {
        Binding? b = Analysis.Resolve(_cur, id.Name);
        if (b is null)
        {
            return new GlobalRef(id.Name);
        }

        Loc loc = _an.LocOf(b);
        if (loc.InEnv)
        {
            return new EnvRef(_an.Hops(_cur, b.Scope), loc.Slot, loc.Tdz, loc.IsConst, id.Name);
        }

        return new RegRef(loc.Slot, loc.Tdz, loc.IsConst, id.Name);
    }

    private XNode CompileIdentifier(Identifier id)
    {
        Binding? b = Analysis.Resolve(_cur, id.Name);
        if (b is null)
        {
            return id.Name == "undefined" ? At(new ConstNode(default), id) : At(new GlobalGet(id.Name), id);
        }

        Loc loc = _an.LocOf(b);
        if (loc.InEnv)
        {
            return At(new EnvGet(_an.Hops(_cur, b.Scope), loc.Slot, loc.Tdz, id.Name), id);
        }

        return loc.Tdz ? At(new RegGetTdz(loc.Slot, id.Name), id) : At(new RegGet(loc.Slot), id);
    }

    // ---- statements -----------------------------------------------------------------------

    private SNode[] CompileList(IReadOnlyList<Node> list)
    {
        var res = new List<SNode>(list.Count);
        foreach (Node n in list)
        {
            SNode? s = CompileStmt(n);
            if (s is not null)
            {
                res.Add(s);
            }
        }

        return [.. res];
    }

    private static SNode Single(SNode[] list, Node at) => list.Length == 1 ? list[0] : AtS(new SeqStmt(list), at);

    private SNode? CompileStmt(Node n)
    {
        switch (n)
        {
            case EmptyStatement:
            case FunctionDeclaration:
                return null;
            case ExpressionStatement es:
                {
                    XNode x = CompileExpr(es.Expression);
                    return AtS(_top ? new CompletionStmt(x) : new ExprStmt(x), n);
                }

            case VariableDeclaration vd:
                {
                    var list = new List<SNode>();
                    foreach (VariableDeclarator d in vd.Declarations)
                    {
                        var id = (Identifier)d.Id;
                        if (d.Init is null && vd.DeclarationKind == VarKind.Var)
                        {
                            continue;
                        }

                        XNode? init = d.Init is null ? null : CompileExpr(d.Init);
                        VarRef r = MakeRef(id);
                        list.Add(AtS(r is RegRef rr && init is not null ? new RegInitStmt(rr.Reg, init) : new InitStmt(r, init), d));
                    }

                    return list.Count == 0 ? null : Single([.. list], n);
                }

            case BlockStatement bs:
                return CompileBlock(bs);
            case IfStatement ifs:
                return AtS(new IfStmt(CompileExpr(ifs.Test), CompileStmt(ifs.Consequent) ?? new EmptyStmt(), ifs.Alternate is null ? null : CompileStmt(ifs.Alternate) ?? new EmptyStmt()), n);
            case ForStatement fs:
                return CompileFor(fs);
            case WhileStatement ws:
                return AtS(new WhileStmt(CompileExpr(ws.Test), CompileStmt(ws.Body) ?? new EmptyStmt()), n);
            case DoWhileStatement dw:
                return AtS(new DoWhileStmt(CompileStmt(dw.Body) ?? new EmptyStmt(), CompileExpr(dw.Test)), n);
            case ReturnStatement rs:
                return AtS(new ReturnStmt(rs.Argument is null ? null : CompileExpr(rs.Argument)), n);
            case BreakStatement bk:
                return bk.Label is null ? AtS(new JumpStmt(Cmp.Break), n) : throw new SpikeUnsupportedException("labelled break");
            case ContinueStatement ct:
                return ct.Label is null ? AtS(new JumpStmt(Cmp.Continue), n) : throw new SpikeUnsupportedException("labelled continue");
            case ThrowStatement ts:
                return AtS(new ThrowStmt(CompileExpr(ts.Argument)), n);
            case TryStatement tr:
                return CompileTry(tr);
            default:
                throw new SpikeUnsupportedException($"{n.Kind} at {n.Start}");
        }
    }

    private SNode CompileBlock(BlockStatement bs)
    {
        Scope saved = _cur;
        bool savedTop = _top;
        _top = false;
        ScopeEntry? entry = null;
        if (bs.Scope is not null)
        {
            _cur = bs.Scope;
            entry = MakeEntry(bs.Scope, CompileUtil.HoistedFunctions(bs.Body));
            if (entry.IsEmpty)
            {
                entry = null;
            }
        }

        SNode[] body = CompileList(bs.Body);
        _cur = saved;
        _top = savedTop;
        return AtS(entry is null ? new SeqStmt(body) : new BlockStmt(entry, body), bs);
    }

    private ForStmt CompileFor(ForStatement fs)
    {
        Scope saved = _cur;
        ScopeEntry? entry = null;
        bool perIteration = false;
        if (fs.Scope is not null)
        {
            _cur = fs.Scope;
            entry = MakeEntry(fs.Scope, []);
            perIteration = entry.EnvSize > 0;
            if (entry.IsEmpty)
            {
                entry = null;
            }
        }

        SNode? init = fs.Init switch
        {
            null => null,
            VariableDeclaration => CompileStmt(fs.Init),
            Expression x => AtS(new ExprStmt(CompileExpr(x)), x),
            _ => throw new SpikeUnsupportedException("for init"),
        };
        XNode? test = fs.Test is null ? null : CompileExpr(fs.Test);
        XNode? update = fs.Update is null ? null : CompileExpr(fs.Update);
        SNode body = CompileStmt(fs.Body) ?? new EmptyStmt();
        _cur = saved;
        var loop = new ForStmt(entry, init, test, update, body, perIteration);
        AtS(loop, fs);
        return loop;
    }

    private TryStmt CompileTry(TryStatement tr)
    {
        SNode block = CompileBlock(tr.Block);
        CatchClause h = tr.Handler ?? throw new SpikeUnsupportedException("try without catch");
        Scope saved = _cur;
        _cur = h.Scope;
        ScopeEntry entry = MakeEntry(h.Scope, CompileUtil.HoistedFunctions(h.Body.Body));
        VarRef? param = h.Param is Identifier pid ? MakeRef(pid) : h.Param is null ? null : throw new SpikeUnsupportedException("catch pattern");
        SNode[] handler = CompileList(h.Body.Body);
        _cur = saved;
        var t = new TryStmt(block, entry, param, handler);
        AtS(t, tr);
        return t;
    }

    // ---- expressions ----------------------------------------------------------------------

    private XNode[] CompileArgs(IReadOnlyList<Node> args)
    {
        var res = new XNode[args.Count];
        for (int i = 0; i < res.Length; i++)
        {
            res[i] = args[i] is Expression e ? CompileExpr(e) : throw new SpikeUnsupportedException("spread argument");
        }

        return res;
    }

    private int _depth;

    private XNode CompileExpr(Expression e)
    {
        if (++_depth > CompileUtil.MaxNesting || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw CompileUtil.TooNested();
        }

        XNode r = CompileExprCore(e);
        _depth--;
        return r;
    }

    private XNode CompileExprCore(Expression e)
    {
        switch (e)
        {
            case NumberLiteral or StringLiteral or BooleanLiteral or NullLiteral:
                return At(new ConstNode(CompileUtil.ConstantOf(e)), e);
            case Identifier id:
                return CompileIdentifier(id);
            case ThisExpression:
                return At(new ThisNode(), e);
            case BinaryExpression or LogicalExpression:
                return CompileChain(e);
            case UnaryExpression u:
                if (u.Operator == UnaryOp.Delete)
                {
                    throw new SpikeUnsupportedException("delete");
                }

                return At(new UnaryNode(u.Operator, CompileExpr(u.Argument)), e);
            case ConditionalExpression c:
                return At(new CondNode(CompileExpr(c.Test), CompileExpr(c.Consequent), CompileExpr(c.Alternate)), e);
            case SequenceExpression s:
                return At(new SeqNode([.. s.Expressions.Select(CompileExpr)]), e);
            case AssignmentExpression a:
                return CompileAssign(a);
            case UpdateExpression up:
                return CompileUpdate(up);
            case MemberExpression m:
                return CompileMember(m);
            case CallExpression call:
                return CompileCall(call);
            case NewExpression ne:
                return At(new NewNode(CompileExpr(ne.Callee), CompileArgs(ne.Arguments), _src), e);
            case FunctionNode fn:
                return At(new FuncExprNode(CompileFunction(fn)), e);
            case ArrayLiteral arr:
                return At(new ArrayLitNode([.. arr.Elements.Select(x => CompileExpr((Expression)x!))]), e);
            case ObjectLiteral obj:
                return CompileObject(obj);
            default:
                throw new SpikeUnsupportedException($"{e.Kind} at {e.Start}");
        }
    }

    private ObjectLitNode CompileObject(ObjectLiteral obj)
    {
        var keys = new List<string>();
        var values = new List<XNode>();
        foreach (Node p in obj.Properties)
        {
            var prop = (Property)p;
            string key = CompileUtil.KeyName(prop.Key);
            if (keys.Contains(key))
            {
                throw new SpikeUnsupportedException("duplicate key in object literal");
            }

            keys.Add(key);
            values.Add(CompileExpr((Expression)prop.Value));
        }

        var lit = new ObjectLitNode([.. keys], [.. values]);
        At(lit, obj);
        return lit;
    }

    private XNode CompileChain(Expression top)
    {
        var spine = new List<Expression>();
        Node cur = top;
        while (cur is BinaryExpression or LogicalExpression)
        {
            spine.Add((Expression)cur);
            cur = cur is BinaryExpression be ? be.Left : ((LogicalExpression)cur).Left;
        }

        XNode acc = CompileExpr((Expression)cur);
        if (spine.Count > LongChain)
        {
            var steps = new List<(bool, BinaryOp, LogicalOp, XNode)>(spine.Count);
            for (int i = spine.Count - 1; i >= 0; i--)
            {
                steps.Add(spine[i] is BinaryExpression b
                    ? (false, b.Operator, default, CompileExpr(b.Right))
                    : (true, default, ((LogicalExpression)spine[i]).Operator, CompileExpr(((LogicalExpression)spine[i]).Right)));
            }

            return At(new ChainNode(acc, [.. steps]), top);
        }

        for (int i = spine.Count - 1; i >= 0; i--)
        {
            Expression s = spine[i];
            acc = s is BinaryExpression b
                ? MakeBinary(b, acc, CompileExpr(b.Right))
                : At(new LogicalNode(((LogicalExpression)s).Operator, acc, CompileExpr(((LogicalExpression)s).Right)), s);
        }

        return acc;
    }

    private static XNode MakeBinary(BinaryExpression b, XNode l, XNode r)
    {
        BinaryOp op = b.Operator;
        if (l is RegGet rg && r is ConstNode { Value.IsNumber: true } k)
        {
            switch (op)
            {
                case BinaryOp.Add: return At(new RegConstArith(rg.Reg, k.Value.Num, true), b);
                case BinaryOp.Subtract: return At(new RegConstArith(rg.Reg, k.Value.Num, false), b);
                case BinaryOp.LessThan: return At(new RegConstCompare(CmpKind.Lt, rg.Reg, k.Value.Num), b);
                case BinaryOp.LessThanOrEqual: return At(new RegConstCompare(CmpKind.Le, rg.Reg, k.Value.Num), b);
                case BinaryOp.GreaterThan: return At(new RegConstCompare(CmpKind.Gt, rg.Reg, k.Value.Num), b);
                case BinaryOp.GreaterThanOrEqual: return At(new RegConstCompare(CmpKind.Ge, rg.Reg, k.Value.Num), b);
                case BinaryOp.StrictEqual: return At(new RegConstCompare(CmpKind.StrictEq, rg.Reg, k.Value.Num), b);
                case BinaryOp.StrictNotEqual: return At(new RegConstCompare(CmpKind.StrictNe, rg.Reg, k.Value.Num), b);
            }
        }

        switch (op)
        {
            case BinaryOp.Add: return At(new AddNode(l, r), b);
            case BinaryOp.Subtract: return At(new SubNode(l, r), b);
            case BinaryOp.Multiply: return At(new MulNode(l, r), b);
            case BinaryOp.Remainder: return At(new RemNode(l, r), b);
            case BinaryOp.LessThan: return At(new CompareNode(CmpKind.Lt, l, r), b);
            case BinaryOp.LessThanOrEqual: return At(new CompareNode(CmpKind.Le, l, r), b);
            case BinaryOp.GreaterThan: return At(new CompareNode(CmpKind.Gt, l, r), b);
            case BinaryOp.GreaterThanOrEqual: return At(new CompareNode(CmpKind.Ge, l, r), b);
            case BinaryOp.StrictEqual: return At(new CompareNode(CmpKind.StrictEq, l, r), b);
            case BinaryOp.StrictNotEqual: return At(new CompareNode(CmpKind.StrictNe, l, r), b);
            default: return At(new BinaryNode(op, l, r), b);
        }
    }

    private XNode CompileAssign(AssignmentExpression a)
    {
        XNode rhs = CompileExpr(a.Right);
        switch (a.Left)
        {
            case Identifier id:
                {
                    VarRef r = MakeRef(id);
                    if (a.Operator == AssignOp.Assign)
                    {
                        return At(r is RegRef { Plain: true } rr ? new RegAssign(rr.Reg, rhs) : new VarAssign(r, rhs), a);
                    }

                    BinaryOp op = CompileUtil.ToBinary(a.Operator);
                    if (op == BinaryOp.Add && r is RegRef { Plain: true } add)
                    {
                        return At(new RegAddAssign(add.Reg, rhs), a);
                    }

                    return At(new VarCompound(r, op, rhs), a);
                }

            case MemberExpression m:
                {
                    XNode obj = CompileExpr(m.Object);
                    if (m.Computed)
                    {
                        XNode idx = CompileExpr((Expression)m.Property);
                        return At(a.Operator == AssignOp.Assign ? new IndexAssign(obj, idx, rhs) : new IndexCompound(obj, idx, CompileUtil.ToBinary(a.Operator), rhs), a);
                    }

                    string key = CompileUtil.KeyName(m.Property);
                    return At(a.Operator == AssignOp.Assign ? new MemberAssign(obj, key, rhs) : new MemberCompound(obj, key, CompileUtil.ToBinary(a.Operator), rhs), a);
                }

            default:
                throw new SpikeUnsupportedException("assignment target " + a.Left.Kind);
        }
    }

    private XNode CompileUpdate(UpdateExpression up)
    {
        double delta = up.IsIncrement ? 1 : -1;
        switch (up.Argument)
        {
            case Identifier id:
                {
                    VarRef r = MakeRef(id);
                    return At(r is RegRef { Plain: true } rr ? new RegUpdate(rr.Reg, delta, up.IsPrefix) : new VarUpdate(r, delta, up.IsPrefix), up);
                }

            case MemberExpression m:
                {
                    XNode obj = CompileExpr(m.Object);
                    if (m.Computed)
                    {
                        return At(new IndexUpdate(obj, CompileExpr((Expression)m.Property), delta, up.IsPrefix), up);
                    }

                    return At(new MemberUpdate(obj, CompileUtil.KeyName(m.Property), delta, up.IsPrefix), up);
                }

            default:
                throw new SpikeUnsupportedException("update target");
        }
    }

    private XNode CompileMember(MemberExpression m)
    {
        if (m.IsOptional)
        {
            throw new SpikeUnsupportedException("optional chaining");
        }

        XNode obj = CompileExpr(m.Object);
        if (m.Computed)
        {
            return At(new IndexGet(obj, CompileExpr((Expression)m.Property)), m);
        }

        string key = CompileUtil.KeyName(m.Property);
        return key == "length" ? At(new LengthGet(obj), m) : At(new MemberGet(obj, key), m);
    }

    private XNode CompileCall(CallExpression call)
    {
        if (call.IsOptional)
        {
            throw new SpikeUnsupportedException("optional call");
        }

        XNode[] args = CompileArgs(call.Arguments);
        if (call.Callee is MemberExpression m)
        {
            XNode obj = CompileExpr(m.Object);
            if (m.Computed)
            {
                XNode idx = CompileExpr((Expression)m.Property);
                return At(new IndexCallNode(obj, idx, At(new IndexGet(obj, idx), m), args, _src), call);
            }

            var getter = new MemberGet(obj, CompileUtil.KeyName(m.Property));
            At(getter, m);
            return At(new MethodCallNode(obj, getter, getter, args, _src), call);
        }

        return At(new CallNode(CompileExpr(call.Callee), args, _src), call);
    }
}

/// <summary>Entry point of the closure-compiled executor.</summary>
public static class ClosureEngine
{
    /// <summary>Parses, compiles and runs <paramref name="source"/>; returns the completion value as a host value.</summary>
    public static object? Evaluate(string source, SpikeLimits? limits = null)
    {
        ParseResult parsed = JsParser.Parse(source);
        return Run(parsed, source, limits);
    }

    /// <summary>Compiles and runs an already parsed program in a fresh realm.</summary>
    public static object? Run(ParseResult parsed, string source, SpikeLimits? limits = null)
    {
        if (parsed.HasErrors)
        {
            throw new SpikeUnsupportedException("syntax error: " + parsed.Diagnostics[0].Headline);
        }

        limits ??= new SpikeLimits();
        var realm = new Realm(limits.Fuel, limits.MaxDepth);
        var compiler = new ClosureCompiler(realm, source, parsed.Program);
        ClosureCompiler.ProgramCode code = compiler.CompileProgram();
        return CompileUtil.ToHost(Execute(realm, code));
    }

    private static JsValue Execute(Realm realm, ClosureCompiler.ProgramCode code)
    {
        var f = new Frame { Realm = realm, R = new JsValue[Math.Max(code.NumRegs, 1)] };
        realm.Frames[0] = f;
        try
        {
            code.Entry.Enter(f);
            foreach (SNode s in code.Body)
            {
                if (s.Exec(f) == Cmp.Return)
                {
                    break;
                }
            }

            return f.Completion;
        }
        catch (JsThrow ex)
        {
            throw new SpikeUncaughtException(Conv.ToStr(ex.Value.Ref is JsObject o ? o.Get("name") : default) + ": " + Conv.ToStr(ex.Value.Ref is JsObject o2 ? o2.Get("message") : ex.Value));
        }
    }
}
