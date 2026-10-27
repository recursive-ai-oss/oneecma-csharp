using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.ExecutorSpike.Bytecode;

/// <summary>Turns the syntax tree into register bytecode (executor shape B).</summary>
internal sealed class BytecodeCompiler
{
    private readonly string _src;
    private readonly Analysis _an;
    private Scope _cur;
    private FnState _fs = null!;
    private bool _top;

    internal BytecodeCompiler(string src, Syntax.Program program)
    {
        _src = src;
        _an = new Analysis(program);
        _cur = program.Scope;
        Program = program;
    }

    internal Syntax.Program Program { get; }

    private sealed class Label
    {
        internal int Pos = -1;
        internal List<int> Fixups = [];
    }

    private sealed class LoopCtx(Label brk, Label cont, int depth)
    {
        internal readonly Label Break = brk;
        internal readonly Label Continue = cont;
        internal readonly int Depth = depth;
    }

    private sealed class FnState
    {
        internal readonly List<Instr> Code = [];
        internal readonly List<int> SpanStart = [];
        internal readonly List<int> SpanEnd = [];
        internal readonly List<JsValue> Consts = [];
        internal readonly Dictionary<long, int> NumIdx = [];
        internal readonly Dictionary<string, int> StrIdx = new(StringComparer.Ordinal);
        internal readonly Dictionary<object, int> TagIdx = new(ReferenceEqualityComparer.Instance);
        internal readonly List<PropIc> Ics = [];
        internal readonly List<BcFunc> Funcs = [];
        internal readonly List<Handler> Handlers = [];
        internal readonly List<LoopCtx> Loops = [];
        internal int VarRegs;
        internal int TempTop;
        internal int MaxReg;
        internal int EnvDepth;
        internal int CompletionReg = -1;
        internal int LastStart;
        internal int LastEnd;
    }

    // ---- emission helpers -----------------------------------------------------------------

    private int Emit(Op op, int a, int b, int c, Node? at)
    {
        if (at is not null)
        {
            _fs.LastStart = at.Start;
            _fs.LastEnd = at.End;
        }

        _fs.Code.Add(new Instr { Op = op, A = a, B = b, C = c });
        _fs.SpanStart.Add(_fs.LastStart);
        _fs.SpanEnd.Add(_fs.LastEnd);
        return _fs.Code.Count - 1;
    }

    private int Temp()
    {
        int t = _fs.TempTop++;
        if (_fs.TempTop > _fs.MaxReg)
        {
            _fs.MaxReg = _fs.TempTop;
        }

        return t;
    }

    private int Konst(JsValue v)
    {
        FnState fs = _fs;
        int idx;
        if (v.IsNumber)
        {
            long key = BitConverter.DoubleToInt64Bits(v.Num);
            if (!fs.NumIdx.TryGetValue(key, out idx))
            {
                idx = fs.Consts.Count;
                fs.Consts.Add(v);
                fs.NumIdx[key] = idx;
            }
        }
        else if (v.Ref is string s)
        {
            if (!fs.StrIdx.TryGetValue(s, out idx))
            {
                idx = fs.Consts.Count;
                fs.Consts.Add(v);
                fs.StrIdx[s] = idx;
            }
        }
        else
        {
            object tag = v.Ref ?? this;
            if (!fs.TagIdx.TryGetValue(tag, out idx))
            {
                idx = fs.Consts.Count;
                fs.Consts.Add(v);
                fs.TagIdx[tag] = idx;
            }
        }

        return ~idx;
    }

    private int Undef() => Konst(default);

    private PropIc NewIc(string key, out int index)
    {
        var ic = new PropIc(key);
        _fs.Ics.Add(ic);
        index = _fs.Ics.Count - 1;
        return ic;
    }

    private void Place(Label l)
    {
        l.Pos = _fs.Code.Count;
        foreach (int at in l.Fixups)
        {
            Instr ins = _fs.Code[at];
            ins.A = l.Pos;
            _fs.Code[at] = ins;
        }

        l.Fixups.Clear();
    }

    private void Jump(Op op, Label l, int b, int c, Node? at)
    {
        int idx = Emit(op, l.Pos, b, c, at);
        if (l.Pos < 0)
        {
            l.Fixups.Add(idx);
        }
    }

    // ---- functions and scopes -------------------------------------------------------------

    internal BcFunc CompileProgram()
    {
        _an.LayoutOf(Program);
        var saved = _fs;
        _fs = new FnState();
        int vars = _an.LayoutOf(Program).NumRegs;
        _fs.VarRegs = vars + 1;
        _fs.CompletionReg = vars;
        _fs.TempTop = _fs.VarRegs;
        _fs.MaxReg = _fs.TempTop;
        _cur = Program.Scope;
        EnterScope(Program.Scope, CompileUtil.HoistedFunctions(Program.Body), null);
        _top = true;
        foreach (Node s in Program.Body)
        {
            CompileStmt(s);
        }

        _top = false;
        Emit(Op.Return, _fs.CompletionReg, 0, 0, null);
        BcFunc f = Finish("<program>", 0, false, false);
        _fs = saved;
        return f;
    }

    private BcFunc Finish(string name, int nparams, bool isArrow, bool isCtor)
    {
        FnState fs = _fs;
        return new BcFunc
        {
            Name = name,
            Code = [.. fs.Code],
            Consts = [.. fs.Consts],
            Ics = [.. fs.Ics],
            Funcs = [.. fs.Funcs],
            Handlers = [.. fs.Handlers],
            SpanStart = [.. fs.SpanStart],
            SpanEnd = [.. fs.SpanEnd],
            NParams = nparams,
            NumRegs = Math.Max(Math.Max(fs.MaxReg, fs.VarRegs), nparams) + 1,
            IsArrow = isArrow,
            IsCtor = isCtor,
            Source = _src,
        };
    }

    private int CompileFunction(FunctionNode fn)
    {
        _an.LayoutFunction(fn);
        FnState savedFs = _fs;
        Scope savedCur = _cur;
        bool savedTop = _top;
        _top = false;
        _fs = new FnState();
        _cur = fn.Scope;
        _fs.VarRegs = _an.LayoutOf(fn).NumRegs;
        _fs.TempTop = _fs.VarRegs;
        _fs.MaxReg = _fs.TempTop;
        if (fn.Body is Expression e)
        {
            EnterScope(fn.Scope, [], fn);
            int v = Expr(e);
            Emit(Op.Return, v, 0, 0, e);
        }
        else
        {
            var body = (BlockStatement)fn.Body;
            EnterScope(fn.Scope, CompileUtil.HoistedFunctions(body.Body), fn);
            foreach (Node s in body.Body)
            {
                CompileStmt(s);
            }

            Emit(Op.Return, Undef(), 0, 0, null);
        }

        BcFunc code = Finish(fn.Id?.Name ?? string.Empty, fn.Params.Count, fn.IsArrow, fn.Flavor == FunctionFlavor.Normal && !fn.IsArrow);
        _fs = savedFs;
        _cur = savedCur;
        _top = savedTop;
        _fs.Funcs.Add(code);
        return _fs.Funcs.Count - 1;
    }

    private void EnterScope(Scope s, List<FunctionDeclaration> hoists, FunctionNode? fn)
    {
        if (_an.HasEnv(s))
        {
            Emit(Op.PushEnv, _an.EnvSizeOf(s), 0, 0, null);
            _fs.EnvDepth++;
        }

        int tdz = -1;
        foreach (Binding b in s.Bindings)
        {
            Loc loc = _an.LocOf(b);
            if (!loc.Tdz)
            {
                continue;
            }

            if (tdz == -1)
            {
                tdz = Konst(JsValue.Tdz);
            }

            if (loc.InEnv)
            {
                Emit(Op.SetEnv, 0, loc.Slot, tdz, null);
            }
            else
            {
                Emit(Op.Move, loc.Slot, tdz, 0, null);
            }
        }

        if (fn is not null)
        {
            foreach ((int reg, Loc loc) in _an.CapturedParams(fn))
            {
                Emit(Op.SetEnv, 0, loc.Slot, reg, null);
            }
        }

        foreach (FunctionDeclaration fd in hoists)
        {
            Loc loc = _an.LocOf(s.Lookup(fd.Function.Id!.Name)!);
            int idx = CompileFunction(fd.Function);
            int t = Temp();
            Emit(Op.Closure, t, idx, 0, fd);
            if (loc.InEnv)
            {
                Emit(Op.SetEnv, 0, loc.Slot, t, null);
            }
            else
            {
                Emit(Op.Move, loc.Slot, t, 0, null);
            }

            _fs.TempTop--;
        }
    }

    private void ExitScope(Scope s)
    {
        if (_an.HasEnv(s))
        {
            Emit(Op.PopEnv, 0, 0, 0, null);
            _fs.EnvDepth--;
        }
    }

    // ---- variables ------------------------------------------------------------------------

    private readonly record struct VarInfo(bool IsGlobal, bool InEnv, int Slot, int Hops, bool Tdz, bool IsConst);

    private VarInfo Lookup(Identifier id)
    {
        Binding? b = Analysis.Resolve(_cur, id.Name);
        if (b is null)
        {
            return new VarInfo(true, false, 0, 0, false, false);
        }

        Loc loc = _an.LocOf(b);
        return new VarInfo(false, loc.InEnv, loc.Slot, loc.InEnv ? _an.Hops(_cur, b.Scope) : 0, loc.Tdz, loc.IsConst);
    }

    private int ReadVar(Identifier id, int dst)
    {
        VarInfo v = Lookup(id);
        if (v.IsGlobal)
        {
            if (id.Name == "undefined")
            {
                return Undef();
            }

            int t = dst >= 0 ? dst : Temp();
            NewIc(id.Name, out int ic);
            Emit(Op.GetGlobal, t, 0, ic, id);
            return t;
        }

        if (!v.InEnv)
        {
            if (v.Tdz)
            {
                Emit(Op.ChkTdz, v.Slot, 0, 0, id);
            }

            return v.Slot;
        }

        int r = dst >= 0 ? dst : Temp();
        Emit(v.Tdz ? Op.GetEnvChk : Op.GetEnv, r, v.Hops, v.Slot, id);
        return r;
    }

    private void WriteVar(Identifier id, VarInfo v, int val, bool init)
    {
        if (v.IsGlobal)
        {
            NewIc(id.Name, out int ic);
            Emit(Op.SetGlobal, val, 0, ic, id);
            return;
        }

        if (!init && v.IsConst)
        {
            Emit(Op.ThrowConst, 0, 0, 0, id);
            return;
        }

        if (v.InEnv)
        {
            Emit(!init && v.Tdz ? Op.SetEnvChk : Op.SetEnv, v.Hops, v.Slot, val, id);
        }
        else
        {
            if (!init && v.Tdz)
            {
                Emit(Op.ChkTdz, v.Slot, 0, 0, id);
            }

            Emit(Op.Move, v.Slot, val, 0, id);
        }
    }

    // ---- statements -----------------------------------------------------------------------

    private void CompileStmt(Node n)
    {
        int mark = _fs.TempTop;
        switch (n)
        {
            case EmptyStatement:
            case FunctionDeclaration:
                break;
            case ExpressionStatement es:
                if (_top)
                {
                    ExprTo(es.Expression, _fs.CompletionReg);
                }
                else
                {
                    Effect(es.Expression);
                }

                break;
            case VariableDeclaration vd:
                foreach (VariableDeclarator d in vd.Declarations)
                {
                    var id = (Identifier)d.Id;
                    if (d.Init is null && vd.DeclarationKind == VarKind.Var)
                    {
                        continue;
                    }

                    VarInfo v = Lookup(id);
                    if (!v.InEnv && !v.IsGlobal)
                    {
                        if (d.Init is null)
                        {
                            Emit(Op.Move, v.Slot, Undef(), 0, d);
                        }
                        else
                        {
                            ExprTo(d.Init, v.Slot);
                        }
                    }
                    else
                    {
                        int val = d.Init is null ? Undef() : Expr(d.Init);
                        WriteVar(id, v, val, true);
                    }

                    _fs.TempTop = mark;
                }

                break;
            case BlockStatement bs:
                CompileBlock(bs);
                break;
            case IfStatement ifs:
                {
                    var els = new Label();
                    var end = new Label();
                    EmitCond(ifs.Test, els, false, false);
                    CompileStmt(ifs.Consequent);
                    if (ifs.Alternate is not null)
                    {
                        Jump(Op.Jmp, end, 0, 0, null);
                        Place(els);
                        CompileStmt(ifs.Alternate);
                        Place(end);
                    }
                    else
                    {
                        Place(els);
                    }

                    break;
                }

            case ForStatement fs:
                CompileFor(fs);
                break;
            case WhileStatement ws:
                {
                    var body = new Label();
                    var test = new Label();
                    var brk = new Label();
                    _fs.Loops.Add(new LoopCtx(brk, test, _fs.EnvDepth));
                    Jump(Op.Jmp, test, 0, 0, null);
                    Place(body);
                    CompileStmt(ws.Body);
                    Place(test);
                    EmitCond(ws.Test, body, true, true);
                    Place(brk);
                    _fs.Loops.RemoveAt(_fs.Loops.Count - 1);
                    break;
                }

            case DoWhileStatement dw:
                {
                    var body = new Label();
                    var cont = new Label();
                    var brk = new Label();
                    _fs.Loops.Add(new LoopCtx(brk, cont, _fs.EnvDepth));
                    Place(body);
                    CompileStmt(dw.Body);
                    Place(cont);
                    EmitCond(dw.Test, body, true, true);
                    Place(brk);
                    _fs.Loops.RemoveAt(_fs.Loops.Count - 1);
                    break;
                }

            case ReturnStatement rs:
                Emit(Op.Return, rs.Argument is null ? Undef() : Expr(rs.Argument), 0, 0, rs);
                break;
            case BreakStatement bk:
                {
                    if (bk.Label is not null)
                    {
                        throw new SpikeUnsupportedException("labelled break");
                    }

                    LoopCtx ctx = _fs.Loops[^1];
                    for (int k = _fs.EnvDepth; k > ctx.Depth; k--)
                    {
                        Emit(Op.PopEnv, 0, 0, 0, bk);
                    }

                    Jump(Op.Jmp, ctx.Break, 0, 0, bk);
                    break;
                }

            case ContinueStatement ct:
                {
                    if (ct.Label is not null)
                    {
                        throw new SpikeUnsupportedException("labelled continue");
                    }

                    LoopCtx ctx = _fs.Loops[^1];
                    for (int k = _fs.EnvDepth; k > ctx.Depth; k--)
                    {
                        Emit(Op.PopEnv, 0, 0, 0, ct);
                    }

                    Jump(Op.Jmp, ctx.Continue, 0, 0, ct);
                    break;
                }

            case ThrowStatement ts:
                Emit(Op.Throw, Expr(ts.Argument), 0, 0, ts);
                break;
            case TryStatement tr:
                CompileTry(tr);
                break;
            default:
                throw new SpikeUnsupportedException($"{n.Kind} at {n.Start}");
        }

        _fs.TempTop = mark;
    }

    private void CompileBlock(BlockStatement bs)
    {
        Scope saved = _cur;
        bool savedTop = _top;
        _top = false;
        if (bs.Scope is not null)
        {
            _cur = bs.Scope;
            EnterScope(bs.Scope, CompileUtil.HoistedFunctions(bs.Body), null);
        }

        foreach (Node s in bs.Body)
        {
            CompileStmt(s);
        }

        if (bs.Scope is not null)
        {
            ExitScope(bs.Scope);
        }

        _cur = saved;
        _top = savedTop;
    }

    private void CompileFor(ForStatement fs)
    {
        Scope saved = _cur;
        bool hasEnv = false;
        if (fs.Scope is not null)
        {
            _cur = fs.Scope;
            hasEnv = _an.HasEnv(fs.Scope);
            EnterScope(fs.Scope, [], null);
        }

        if (fs.Init is VariableDeclaration)
        {
            CompileStmt(fs.Init);
        }
        else if (fs.Init is Expression ie)
        {
            Effect(ie);
        }

        var body = new Label();
        var cont = new Label();
        var test = new Label();
        var brk = new Label();
        _fs.Loops.Add(new LoopCtx(brk, cont, _fs.EnvDepth));
        if (fs.Test is not null)
        {
            Jump(Op.Jmp, test, 0, 0, null);
        }

        Place(body);
        CompileStmt(fs.Body);
        Place(cont);
        if (hasEnv)
        {
            Emit(Op.CopyEnv, 0, 0, 0, null);
        }

        if (fs.Update is not null)
        {
            Effect(fs.Update);
        }

        Place(test);
        if (fs.Test is not null)
        {
            EmitCond(fs.Test, body, true, true);
        }
        else
        {
            Jump(Op.JmpBack, body, 0, 0, null);
        }

        Place(brk);
        _fs.Loops.RemoveAt(_fs.Loops.Count - 1);
        if (fs.Scope is not null)
        {
            ExitScope(fs.Scope);
        }

        _cur = saved;
    }

    private void CompileTry(TryStatement tr)
    {
        CatchClause h = tr.Handler ?? throw new SpikeUnsupportedException("try without catch");
        int envReg = Temp();
        int catchReg = Temp();
        Emit(Op.SaveEnv, envReg, 0, 0, tr);
        int start = _fs.Code.Count;
        CompileBlock(tr.Block);
        int end = _fs.Code.Count;
        var done = new Label();
        Jump(Op.Jmp, done, 0, 0, null);
        _fs.Handlers.Add(new Handler(start, end, _fs.Code.Count, catchReg));
        Emit(Op.RestoreEnv, envReg, 0, 0, h);
        Scope saved = _cur;
        _cur = h.Scope;
        EnterScope(h.Scope, CompileUtil.HoistedFunctions(h.Body.Body), null);
        if (h.Param is Identifier pid)
        {
            WriteVar(pid, Lookup(pid), catchReg, true);
        }
        else if (h.Param is not null)
        {
            throw new SpikeUnsupportedException("catch pattern");
        }

        foreach (Node s in h.Body.Body)
        {
            CompileStmt(s);
        }

        ExitScope(h.Scope);
        _cur = saved;
        Place(done);
    }

    // ---- conditions -----------------------------------------------------------------------

    private static bool IsCompare(BinaryOp op) =>
        op is BinaryOp.LessThan or BinaryOp.LessThanOrEqual or BinaryOp.GreaterThan or BinaryOp.GreaterThanOrEqual
            or BinaryOp.StrictEqual or BinaryOp.StrictNotEqual;

    /// <summary>Jumps to <paramref name="target"/> when the test is <paramref name="jumpIfTrue"/>; <paramref name="back"/> marks a loop back-edge (fuel is charged).</summary>
    private void EmitCond(Expression test, Label target, bool jumpIfTrue, bool back)
    {
        int mark = _fs.TempTop;
        switch (test)
        {
            case BinaryExpression b when IsCompare(b.Operator) && (back == jumpIfTrue) && (back || !jumpIfTrue):
                {
                    int l = RegOrConst((Expression)b.Left, b.Right);
                    int r = Expr(b.Right);
                    Op op = b.Operator switch
                    {
                        BinaryOp.LessThan => back ? Op.JLt : Op.JNLt,
                        BinaryOp.LessThanOrEqual => back ? Op.JLe : Op.JNLe,
                        BinaryOp.GreaterThan => back ? Op.JGt : Op.JNGt,
                        BinaryOp.GreaterThanOrEqual => back ? Op.JGe : Op.JNGe,
                        BinaryOp.StrictEqual => back ? Op.JSEq : Op.JNSEq,
                        _ => back ? Op.JSNe : Op.JNSNe,
                    };
                    Jump(op, target, l, r, b);
                    break;
                }

            case UnaryExpression { Operator: UnaryOp.Not } u:
                EmitCond(u.Argument, target, !jumpIfTrue, back);
                break;
            case LogicalExpression { Operator: LogicalOp.And } and:
                if (!jumpIfTrue)
                {
                    EmitCond(and.Left, target, false, back);
                    EmitCond(and.Right, target, false, back);
                }
                else
                {
                    var skip = new Label();
                    EmitCond(and.Left, skip, false, false);
                    EmitCond(and.Right, target, true, back);
                    Place(skip);
                }

                break;
            case LogicalExpression { Operator: LogicalOp.Or } or:
                if (jumpIfTrue)
                {
                    EmitCond(or.Left, target, true, back);
                    EmitCond(or.Right, target, true, back);
                }
                else
                {
                    var skip = new Label();
                    EmitCond(or.Left, skip, true, false);
                    EmitCond(or.Right, target, false, back);
                    Place(skip);
                }

                break;
            default:
                {
                    int v = Expr(test);
                    if (jumpIfTrue)
                    {
                        Jump(back ? Op.JTrueBack : Op.JTrue, target, v, 0, test);
                    }
                    else if (!back)
                    {
                        Jump(Op.JFalse, target, v, 0, test);
                    }
                    else
                    {
                        // A back-edge taken when the test is false: skip over an unconditional (fuel-charging) jump.
                        var skip = new Label();
                        Jump(Op.JTrue, skip, v, 0, test);
                        Jump(Op.JmpBack, target, 0, 0, test);
                        Place(skip);
                    }

                    break;
                }
        }

        _fs.TempTop = mark;
    }

    /// <summary>Left operand of a binary operation; copied out of a variable register when the right side could change it.</summary>
    private int RegOrConst(Expression left, Expression right)
    {
        int l = Expr(left);
        if (l >= 0 && l < _fs.VarRegs && !IsPure(right))
        {
            int t = Temp();
            Emit(Op.Move, t, l, 0, left);
            return t;
        }

        return l;
    }

    private static bool IsPure(Expression e)
    {
        while (true)
        {
            switch (e)
            {
                case NumberLiteral or StringLiteral or BooleanLiteral or NullLiteral or Identifier or ThisExpression or FunctionNode:
                    return true;
                case MemberExpression m:
                    if (m.Computed && !IsPure((Expression)m.Property))
                    {
                        return false;
                    }

                    e = m.Object;
                    continue;
                case BinaryExpression b:
                    if (!IsPure(b.Right))
                    {
                        return false;
                    }

                    e = (Expression)b.Left;
                    continue;
                case LogicalExpression l:
                    if (!IsPure(l.Right))
                    {
                        return false;
                    }

                    e = l.Left;
                    continue;
                case UnaryExpression u when u.Operator != UnaryOp.Delete:
                    e = u.Argument;
                    continue;
                default:
                    return false;
            }
        }
    }

    // ---- expressions ----------------------------------------------------------------------

    /// <summary>Computes <paramref name="e"/> and returns the operand holding it: a variable's own register, a constant or a fresh temporary.</summary>
    private int Expr(Expression e)
    {
        switch (e)
        {
            case NumberLiteral or StringLiteral or BooleanLiteral or NullLiteral:
                return Konst(CompileUtil.ConstantOf(e));
            case Identifier id:
                return ReadVar(id, -1);
            default:
                {
                    int t = Temp();
                    ExprTo(e, t);
                    return t;
                }
        }
    }

    private int RegisterOf(Expression e, Expression? laterEvaluated)
    {
        int o = laterEvaluated is null ? Expr(e) : RegOrConst(e, laterEvaluated);
        if (o < 0)
        {
            int t = Temp();
            Emit(Op.Move, t, o, 0, e);
            return t;
        }

        return o;
    }

    private int Target(int dst) => dst < _fs.VarRegs ? Temp() : dst;

    private void Finalize(int dst, int t, Node at)
    {
        if (t != dst)
        {
            Emit(Op.Move, dst, t, 0, at);
        }
    }

    private int _depth;

    private void ExprTo(Expression e, int dst)
    {
        if (++_depth > CompileUtil.MaxNesting || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw CompileUtil.TooNested();
        }

        ExprToCore(e, dst);
        _depth--;
    }

    private void ExprToCore(Expression e, int dst)
    {
        int mark = _fs.TempTop;
        switch (e)
        {
            case NumberLiteral or StringLiteral or BooleanLiteral or NullLiteral:
                Emit(Op.Move, dst, Konst(CompileUtil.ConstantOf(e)), 0, e);
                break;
            case Identifier id:
                {
                    int r = ReadVar(id, dst);
                    Finalize(dst, r, id);
                    break;
                }

            case ThisExpression:
                Emit(Op.LoadThis, dst, 0, 0, e);
                break;
            case BinaryExpression or LogicalExpression:
                BinaryChainTo(e, dst);
                break;
            case UnaryExpression u:
                {
                    if (u.Operator == UnaryOp.Delete)
                    {
                        throw new SpikeUnsupportedException("delete");
                    }

                    if (u.Operator == UnaryOp.Void)
                    {
                        Effect(u.Argument);
                        Emit(Op.Move, dst, Undef(), 0, e);
                        break;
                    }

                    int a = Expr(u.Argument);
                    Op op = u.Operator switch
                    {
                        UnaryOp.Minus => Op.Neg,
                        UnaryOp.Plus => Op.ToNum,
                        UnaryOp.Not => Op.Not,
                        UnaryOp.BitwiseNot => Op.BitNot,
                        _ => Op.Typeof,
                    };
                    Emit(op, dst, a, 0, e);
                    break;
                }

            case ConditionalExpression c:
                {
                    int t = Target(dst);
                    var els = new Label();
                    var end = new Label();
                    EmitCond(c.Test, els, false, false);
                    ExprTo(c.Consequent, t);
                    Jump(Op.Jmp, end, 0, 0, null);
                    Place(els);
                    ExprTo(c.Alternate, t);
                    Place(end);
                    Finalize(dst, t, e);
                    break;
                }

            case SequenceExpression s:
                for (int i = 0; i < s.Expressions.Count - 1; i++)
                {
                    Effect(s.Expressions[i]);
                }

                ExprTo(s.Expressions[^1], dst);
                break;
            case AssignmentExpression a:
                {
                    int v = AssignTo(a, true);
                    Finalize(dst, v, e);
                    break;
                }

            case UpdateExpression up:
                {
                    int v = UpdateTo(up, true);
                    Finalize(dst, v, e);
                    break;
                }

            case MemberExpression m:
                CompileMember(m, dst);
                break;
            case CallExpression call:
                CompileCall(call.Callee, call.Arguments, call.IsOptional, false, dst, call);
                break;
            case NewExpression ne:
                CompileCall(ne.Callee, ne.Arguments, false, true, dst, ne);
                break;
            case FunctionNode fn:
                Emit(Op.Closure, dst, CompileFunction(fn), 0, e);
                break;
            case ArrayLiteral arr:
                {
                    int t = Target(dst);
                    Emit(Op.NewArray, t, arr.Elements.Count, 0, e);
                    foreach (Node? el in arr.Elements)
                    {
                        int keep = _fs.TempTop;
                        Emit(Op.ArrPush, t, Expr((Expression)el!), 0, el);
                        _fs.TempTop = keep;
                    }

                    Finalize(dst, t, e);
                    break;
                }

            case ObjectLiteral obj:
                {
                    int t = Target(dst);
                    Emit(Op.NewObject, t, obj.Properties.Count, 0, e);
                    foreach (Node p in obj.Properties)
                    {
                        var prop = (Property)p;
                        int keep = _fs.TempTop;
                        int v = Expr((Expression)prop.Value);
                        NewIc(CompileUtil.KeyName(prop.Key), out int ic);
                        Emit(Op.SetProp, t, v, ic, prop);
                        _fs.TempTop = keep;
                    }

                    Finalize(dst, t, e);
                    break;
                }

            default:
                throw new SpikeUnsupportedException($"{e.Kind} at {e.Start}");
        }

        _fs.TempTop = mark;
    }

    private void Effect(Expression e)
    {
        int mark = _fs.TempTop;
        switch (e)
        {
            case AssignmentExpression a:
                AssignTo(a, false);
                break;
            case UpdateExpression up:
                UpdateTo(up, false);
                break;
            default:
                ExprTo(e, Temp());
                break;
        }

        _fs.TempTop = mark;
    }

    private static Op OpFor(BinaryOp op) => op switch
    {
        BinaryOp.Add => Op.Add,
        BinaryOp.Subtract => Op.Sub,
        BinaryOp.Multiply => Op.Mul,
        BinaryOp.Remainder => Op.Rem,
        BinaryOp.LessThan => Op.Lt,
        BinaryOp.LessThanOrEqual => Op.Le,
        BinaryOp.GreaterThan => Op.Gt,
        BinaryOp.GreaterThanOrEqual => Op.Ge,
        BinaryOp.StrictEqual => Op.SEq,
        BinaryOp.StrictNotEqual => Op.SNe,
        _ => Op.Bin + (int)op,
    };

    private void BinaryChainTo(Expression top, int dst)
    {
        var spine = new List<Expression>();
        Node cur = top;
        bool anyLogical = false;
        while (cur is BinaryExpression or LogicalExpression)
        {
            spine.Add((Expression)cur);
            if (cur is BinaryExpression be)
            {
                cur = be.Left;
            }
            else
            {
                anyLogical = true;
                cur = ((LogicalExpression)cur).Left;
            }
        }

        var leaf = (Expression)cur;
        if (anyLogical)
        {
            int t = Target(dst);
            ExprTo(leaf, t);
            int baseTop = _fs.TempTop;
            for (int i = spine.Count - 1; i >= 0; i--)
            {
                if (spine[i] is LogicalExpression le)
                {
                    var end = new Label();
                    switch (le.Operator)
                    {
                        case LogicalOp.And:
                            Jump(Op.JFalse, end, t, 0, le);
                            break;
                        case LogicalOp.Or:
                            Jump(Op.JTrue, end, t, 0, le);
                            break;
                        default:
                            {
                                // a ?? b: skip b unless a is null or undefined.
                                int isNull = Temp();
                                Emit(Op.Bin + (int)BinaryOp.Equal, isNull, t, Konst(JsValue.Null), le);
                                Jump(Op.JFalse, end, isNull, 0, le);
                                break;
                            }
                    }

                    ExprTo(le.Right, t);
                    Place(end);
                }
                else
                {
                    var be2 = (BinaryExpression)spine[i];
                    int r = Expr(be2.Right);
                    Emit(OpFor(be2.Operator), t, t, r, be2);
                }

                _fs.TempTop = baseTop;
            }

            Finalize(dst, t, top);
            return;
        }

        int accTemp = spine.Count > 1 ? Temp() : -1;
        int floor = _fs.TempTop;
        int acc = Expr(leaf);
        for (int i = spine.Count - 1; i >= 0; i--)
        {
            var b = (BinaryExpression)spine[i];
            if (acc >= 0 && acc < _fs.VarRegs && !IsPure(b.Right))
            {
                int t = Temp();
                Emit(Op.Move, t, acc, 0, leaf);
                acc = t;
            }

            int r = Expr(b.Right);
            int target = i == 0 ? dst : accTemp;
            Emit(OpFor(b.Operator), target, acc, r, b);
            acc = target;
            _fs.TempTop = floor;
        }
    }

    private int AssignTo(AssignmentExpression a, bool needValue)
    {
        bool plain = a.Operator == AssignOp.Assign;
        BinaryOp bop = plain ? default : CompileUtil.ToBinary(a.Operator);
        switch (a.Left)
        {
            case Identifier id:
                {
                    VarInfo v = Lookup(id);
                    if (!v.IsGlobal && !v.InEnv && !v.Tdz && !v.IsConst)
                    {
                        if (plain)
                        {
                            ExprTo(a.Right, v.Slot);
                            return v.Slot;
                        }

                        int lhs = v.Slot;
                        if (!IsPure(a.Right))
                        {
                            lhs = Temp();
                            Emit(Op.Move, lhs, v.Slot, 0, id);
                        }

                        int r = Expr(a.Right);
                        Emit(OpFor(bop), v.Slot, lhs, r, a);
                        return v.Slot;
                    }

                    if (plain)
                    {
                        int val = Expr(a.Right);
                        WriteVar(id, v, val, false);
                        return val;
                    }

                    int cur = Temp();
                    int read = ReadVar(id, cur);
                    if (read != cur)
                    {
                        Emit(Op.Move, cur, read, 0, id);
                    }

                    int rr = Expr(a.Right);
                    Emit(OpFor(bop), cur, cur, rr, a);
                    WriteVar(id, v, cur, false);
                    return cur;
                }

            case MemberExpression m:
                {
                    if (m.IsOptional)
                    {
                        throw new SpikeUnsupportedException("optional chaining");
                    }

                    int o = RegisterOf(m.Object, a.Right);
                    if (m.Computed)
                    {
                        int idx = Expr((Expression)m.Property);
                        if (idx >= 0 && idx < _fs.VarRegs && !IsPure(a.Right))
                        {
                            int t = Temp();
                            Emit(Op.Move, t, idx, 0, m);
                            idx = t;
                        }

                        if (plain)
                        {
                            int val = Expr(a.Right);
                            Emit(Op.SetIndex, o, idx, val, a);
                            return val;
                        }

                        int cur = Temp();
                        Emit(Op.GetIndex, cur, o, idx, m);
                        int r = Expr(a.Right);
                        Emit(OpFor(bop), cur, cur, r, a);
                        Emit(Op.SetIndex, o, idx, cur, a);
                        return cur;
                    }

                    NewIc(CompileUtil.KeyName(m.Property), out int ic);
                    if (plain)
                    {
                        int val = Expr(a.Right);
                        Emit(Op.SetProp, o, val, ic, a);
                        return val;
                    }

                    int c2 = Temp();
                    Emit(Op.GetProp, c2, o, ic, m);
                    int r2 = Expr(a.Right);
                    Emit(OpFor(bop), c2, c2, r2, a);
                    Emit(Op.SetProp, o, c2, ic, a);
                    return c2;
                }

            default:
                throw new SpikeUnsupportedException("assignment target " + a.Left.Kind);
        }
    }

    private int UpdateTo(UpdateExpression up, bool needValue)
    {
        // x - (-1) is x + 1 with ToNumber applied, so no separate conversion is needed for the new value.
        int one = Konst(JsValue.FromNumber(up.IsIncrement ? -1 : 1));
        bool postfixValue = needValue && !up.IsPrefix;
        switch (up.Argument)
        {
            case Identifier id:
                {
                    VarInfo v = Lookup(id);
                    if (!v.IsGlobal && !v.InEnv && !v.Tdz && !v.IsConst)
                    {
                        if (postfixValue)
                        {
                            int old = Temp();
                            Emit(Op.ToNum, old, v.Slot, 0, up);
                            Emit(Op.Sub, v.Slot, old, one, up);
                            return old;
                        }

                        Emit(Op.Sub, v.Slot, v.Slot, one, up);
                        return v.Slot;
                    }

                    int cur = Temp();
                    int read = ReadVar(id, cur);
                    if (read != cur)
                    {
                        Emit(Op.Move, cur, read, 0, id);
                    }

                    int nv = Temp();
                    Emit(Op.ToNum, cur, cur, 0, up);
                    Emit(Op.Sub, nv, cur, one, up);
                    WriteVar(id, v, nv, false);
                    return postfixValue ? cur : nv;
                }

            case MemberExpression m:
                {
                    int o = RegisterOf(m.Object, null);
                    int cur = Temp();
                    int nv = Temp();
                    if (m.Computed)
                    {
                        int idx = Expr((Expression)m.Property);
                        Emit(Op.GetIndex, cur, o, idx, m);
                        Emit(Op.ToNum, cur, cur, 0, up);
                        Emit(Op.Sub, nv, cur, one, up);
                        Emit(Op.SetIndex, o, idx, nv, up);
                    }
                    else
                    {
                        NewIc(CompileUtil.KeyName(m.Property), out int ic);
                        Emit(Op.GetProp, cur, o, ic, m);
                        Emit(Op.ToNum, cur, cur, 0, up);
                        Emit(Op.Sub, nv, cur, one, up);
                        Emit(Op.SetProp, o, nv, ic, up);
                    }

                    return postfixValue ? cur : nv;
                }

            default:
                throw new SpikeUnsupportedException("update target");
        }
    }

    private void CompileMember(MemberExpression m, int dst)
    {
        if (m.IsOptional)
        {
            throw new SpikeUnsupportedException("optional chaining");
        }

        int o = RegisterOf(m.Object, m.Computed ? (Expression)m.Property : null);
        if (m.Computed)
        {
            int idx = Expr((Expression)m.Property);
            Emit(Op.GetIndex, dst, o, idx, m);
            return;
        }

        string key = CompileUtil.KeyName(m.Property);
        if (key == "length")
        {
            Emit(Op.GetLength, dst, o, 0, m);
            return;
        }

        NewIc(key, out int ic);
        Emit(Op.GetProp, dst, o, ic, m);
    }

    private void CompileCall(Expression callee, IReadOnlyList<Node> args, bool optional, bool isNew, int dst, Expression whole)
    {
        if (optional)
        {
            throw new SpikeUnsupportedException("optional call");
        }

        int b = Temp();
        int thisSlot = Temp();
        if (isNew)
        {
            ExprTo(callee, b);
        }
        else if (callee is MemberExpression m)
        {
            if (m.IsOptional)
            {
                throw new SpikeUnsupportedException("optional chaining");
            }

            ExprTo(m.Object, thisSlot);
            if (m.Computed)
            {
                int idx = Expr((Expression)m.Property);
                Emit(Op.GetIndex, b, thisSlot, idx, m);
            }
            else
            {
                NewIc(CompileUtil.KeyName(m.Property), out int ic);
                Emit(Op.GetProp, b, thisSlot, ic, m);
            }
        }
        else
        {
            ExprTo(callee, b);
            Emit(Op.Move, thisSlot, Undef(), 0, callee);
        }

        foreach (Node arg in args)
        {
            if (arg is not Expression ae)
            {
                throw new SpikeUnsupportedException("spread argument");
            }

            int slot = Temp();
            ExprTo(ae, slot);
        }

        // The error span of a failed call is the callee, so "x.y is not a function" can quote it.
        Emit(isNew ? Op.New : Op.Call, dst, b, args.Count, callee);
        _ = whole;
    }
}

/// <summary>Entry point of the bytecode executor.</summary>
public static class BytecodeEngine
{
    /// <summary>Parses, compiles and runs <paramref name="source"/>; returns the completion value as a host value.</summary>
    public static object? Evaluate(string source, SpikeLimits? limits = null)
    {
        ParseResult parsed = JsParser.Parse(source);
        return Run(parsed, source, limits);
    }

    /// <summary>Compiles and runs an already parsed program in a fresh realm.</summary>
    public static object? Run(ParseResult parsed, string source, SpikeLimits? limits = null) =>
        RunCore<NoTrace>(parsed, source, limits);

    /// <summary>Runs with the recording tracer and returns the (span, value) of every value-producing instruction executed.</summary>
    public static (object? Result, IReadOnlyList<(int Start, int End, string Value)> Trace) RunTraced(string source, SpikeLimits? limits = null)
    {
        ParseResult parsed = JsParser.Parse(source);
        var sink = new List<(int, int, string)>();
        RecordingTrace.Sink = sink;
        try
        {
            object? result = RunCore<RecordingTrace>(parsed, source, limits);
            return (result, sink);
        }
        finally
        {
            RecordingTrace.Sink = null;
        }
    }

    private static object? RunCore<TTrace>(ParseResult parsed, string source, SpikeLimits? limits)
        where TTrace : struct, ITracer
    {
        if (parsed.HasErrors)
        {
            throw new SpikeUnsupportedException("syntax error: " + parsed.Diagnostics[0].Headline);
        }

        limits ??= new SpikeLimits();
        var realm = new Realm(limits.Fuel, limits.MaxDepth);
        var compiler = new BytecodeCompiler(source, parsed.Program);
        BcFunc program = compiler.CompileProgram();
        var vm = new Vm(realm);
        try
        {
            return CompileUtil.ToHost(vm.RunProgram<TTrace>(program));
        }
        catch (JsThrow ex)
        {
            throw new SpikeUncaughtException(Conv.ToStr(ex.Value.Ref is JsObject o ? o.Get("name") : default) + ": " + Conv.ToStr(ex.Value.Ref is JsObject o2 ? o2.Get("message") : ex.Value));
        }
    }
}
