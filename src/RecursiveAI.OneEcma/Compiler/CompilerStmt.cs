using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma;

internal enum BindMode
{
    /// <summary>`var` declaration or plain assignment: PutValue through the resolved reference.</summary>
    Assign,

    /// <summary>let, const, class, parameter or catch parameter: InitializeReferencedBinding.</summary>
    Init,
}

internal sealed partial class Compiler
{
    // ---- statements ---------------------------------------------------------------------------

    private void Statements(IEnumerable<Node> list)
    {
        foreach (Node s in list)
        {
            Stmt(s);
        }
    }

    private void ResetCompletion()
    {
        if (_f.CompletionTemp >= 0)
        {
            Emit(Op.PushUndef);
            Emit(Op.StoreLocal, _f.CompletionTemp);
        }
    }

    private void Stmt(Node s, List<string>? labels = null)
    {
        EnsureStack();
        int ss = _f.CurS;
        int se = _f.CurE;
        int savedStmt = _f.StmtStart;
        _f.StmtStart = s.Start;
        SetSpan(s);
        StmtCore(s, labels);
        _f.StmtStart = savedStmt;
        SetSpan(ss, se);
    }

    private void StmtCore(Node s, List<string>? labels)
    {
        switch (s)
        {
            case ExpressionStatement es:
                if (_f.CompletionTemp >= 0)
                {
                    Expr(es.Expression);
                    Emit(Op.StoreLocal, _f.CompletionTemp);
                }
                else
                {
                    ExprDiscard(es.Expression);
                }

                break;
            case VariableDeclaration vd:
                VarDecl(vd);
                break;
            case FunctionDeclaration fd:
                if (fd.IsAnnexBHoisted)
                {
                    AnnexBCopy(fd.Function.Id!.Name);
                }

                break;
            case ClassDeclaration cd:
                ClassExpr(cd.Class);
                InitIdent(cd.Class.Id!.Name);
                break;
            case ReturnStatement rs:
                if (rs.Argument is null)
                {
                    Emit(Op.PushUndef);
                }
                else
                {
                    ExprTail(rs.Argument);
                    if (_f.IsAsync && _f.IsGenerator)
                    {
                        Emit(Op.Await);
                    }
                }

                EmitReturn();
                break;
            case IfStatement ifs:
                {
                    ResetCompletion();
                    JumpLabel els = NewLabel();
                    JumpLabel end = NewLabel();
                    Expr(ifs.Test);
                    Jump(Op.JmpIfFalse, els);
                    Stmt(ifs.Consequent);
                    if (ifs.Alternate is not null)
                    {
                        Jump(Op.Jmp, end);
                        Mark(els);
                        Stmt(ifs.Alternate);
                        Mark(end);
                    }
                    else
                    {
                        Mark(els);
                    }

                    break;
                }

            case BlockStatement b:
                Block(b, labels);
                break;
            case ForStatement fs:
                ForLoop(fs, labels);
                break;
            case ForInStatement fi:
                ForInLoop(fi, labels);
                break;
            case ForOfStatement fo:
                ForOfLoop(fo, labels);
                break;
            case WhileStatement ws:
                {
                    ResetCompletion();
                    Control c = PushLoop(labels);
                    // `continue` lands on the back edge (which charges fuel), not on the test, so a loop that only continues still ends.
                    JumpLabel top = NewLabel();
                    Mark(top);
                    Expr(ws.Test);
                    Jump(Op.JmpIfFalse, c.Break!);
                    Stmt(ws.Body);
                    Mark(c.Continue!);
                    Jump(Op.JmpBack, top);
                    PopControl(c);
                    Mark(c.Break!);
                    break;
                }

            case DoWhileStatement dw:
                {
                    ResetCompletion();
                    Control c = PushLoop(labels);
                    JumpLabel top = NewLabel();
                    Mark(top);
                    Stmt(dw.Body);
                    Mark(c.Continue!);
                    Expr(dw.Test);
                    Jump(Op.JmpIfFalse, c.Break!);
                    Jump(Op.JmpBack, top);
                    PopControl(c);
                    Mark(c.Break!);
                    break;
                }

            case BreakStatement br:
                Break(br.Label?.Name);
                break;
            case ContinueStatement co:
                Continue(co.Label?.Name);
                break;
            case ThrowStatement th:
                Expr(th.Argument);
                Emit(Op.Throw);
                break;
            case TryStatement ts:
                Try(ts);
                break;
            case SwitchStatement sw:
                Switch(sw, labels);
                break;
            case LabeledStatement ls:
                {
                    var set = labels is null ? [] : new List<string>(labels);
                    set.Add(ls.Label.Name);
                    if (ls.Body is ForStatement or ForInStatement or ForOfStatement or WhileStatement or DoWhileStatement or LabeledStatement or SwitchStatement)
                    {
                        Stmt(ls.Body, set);
                    }
                    else
                    {
                        Control c = new() { Kind = Control.K.Block, Labels = set, Break = NewLabel() };
                        _f.Controls.Add(c);
                        Stmt(ls.Body);
                        PopControl(c);
                        Mark(c.Break!);
                    }

                    break;
                }

            case WithStatement wi:
                {
                    ResetCompletion();
                    Expr(wi.Object);
                    Emit(Op.PushWith);
                    var cs = new CScope { Parent = _scope, IsWith = true };
                    _scope = cs;
                    var c = new Control { Kind = Control.K.With };
                    _f.Controls.Add(c);
                    Stmt(wi.Body);
                    PopControl(c);
                    _scope = cs.Parent;
                    Emit(Op.PopScope);
                    break;
                }

            case EmptyStatement:
            case DebuggerStatement:
                break;
            case ErrorStatement:
                throw new InvalidOperationException("tree has errors");
            default:
                throw new NotSupportedException("statement " + s.Kind);
        }
    }

    private void PopControl(Control c)
    {
        int idx = _f.Controls.LastIndexOf(c);
        _f.Controls.RemoveRange(idx, _f.Controls.Count - idx);
    }

    private Control PushLoop(List<string>? labels)
    {
        var c = new Control { Kind = Control.K.Loop, Labels = labels, Break = NewLabel(), Continue = NewLabel() };
        _f.Controls.Add(c);
        return c;
    }

    // ---- blocks and declarations --------------------------------------------------------------

    private void Block(BlockStatement b, List<string>? labels)
    {
        CScope? cs = EnterScope(b.Scope);
        Control? c = null;
        if (cs is not null)
        {
            c = new Control { Kind = Control.K.Scope };
            _f.Controls.Add(c);
        }

        HoistFunctions(b.Body);
        Statements(b.Body);
        if (c is not null)
        {
            PopControl(c);
        }

        LeaveScope(cs);
    }

    /// <summary>Instantiates the function declarations directly in a block, function body, or case clause list (BlockDeclarationInstantiation, 14.2.3).</summary>
    private void HoistFunctions(IEnumerable<Node> body)
    {
        foreach (Node st in body)
        {
            Node cur = st;
            while (cur is LabeledStatement l)
            {
                cur = l.Body;
            }

            if (cur is FunctionDeclaration fd)
            {
                // `export default function () {}` binds the hidden name *default* and is named "default".
                if (fd.Function.Id is null)
                {
                    _nameHint = "default";
                }

                CompileClosure(fd.Function);
                InitIdent(fd.Function.Id?.Name ?? "*default*");
            }
        }
    }

    /// <summary>B.3.3.1 step: when a block-level function declaration is evaluated, copy its value to the var-scoped binding.</summary>
    private void AnnexBCopy(string name)
    {
        // Find the enclosing var scope; the block binding is the innermost match.
        CScope? varScope = _scope;
        while (varScope is not null && !(varScope.Ast is not null && varScope.Ast.IsVarScope) && !varScope.IsFunctionTop)
        {
            varScope = varScope.Parent;
        }

        LoadIdent(name);
        Ref r = varScope is null
            ? new Ref { Kind = _evalUnit || _unknownParent ? RefKind.Dynamic : RefKind.Global, Name = name }
            : ResolveFromVarScope(name, varScope);
        switch (r.Kind)
        {
            case RefKind.Local:
                Emit(Op.StoreSlot, r.Hops, r.Slot);
                break;
            case RefKind.Global:
                Emit(Op.StoreGlobal, NameConst(name), 0);
                break;
            default:
                Emit(Op.StoreVarDyn, NameConst(name), 0);
                break;
        }
    }

    private Ref ResolveFromVarScope(string name, CScope varScope)
    {
        int hops = 0;
        for (CScope? cs = _scope; cs is not null; cs = cs.Parent)
        {
            if (ReferenceEquals(cs, varScope))
            {
                if (cs.Info is not null && cs.Slots!.TryGetValue(name, out int slot))
                {
                    return new Ref { Kind = RefKind.Local, Hops = hops, Slot = slot, BindingKind = cs.Info.Kinds[slot], Name = name };
                }

                break;
            }

            if (cs.Info is not null || cs.IsWith)
            {
                hops++;
            }
        }

        return new Ref { Kind = _evalUnit ? RefKind.Dynamic : RefKind.Global, Name = name };
    }

    private void VarDecl(VariableDeclaration vd)
    {
        foreach (VariableDeclarator d in vd.Declarations)
        {
            if (vd.DeclarationKind == VarKind.Var)
            {
                if (d.Init is null)
                {
                    continue;
                }

                if (d.Id is Identifier id)
                {
                    ExprNamed(d.Init, id.Name);
                    _f.ValS = d.Init.Start;
                    _f.ValE = d.Init.End;
                    StoreIdent(id.Name);
                    _f.ValS = -1;
                }
                else
                {
                    Expr(d.Init);
                    DestructureText = SourceSlice(d.Init);
                    _iterText = IterTextFor(d.Init);
                    _destructureSpan = (d.Init.Start, d.Init.End);
                    BindTarget(d.Id, BindMode.Assign);
                }
            }
            else
            {
                if (d.Id is Identifier id)
                {
                    if (d.Init is null)
                    {
                        Emit(Op.PushUndef);
                    }
                    else
                    {
                        ExprNamed(d.Init, id.Name);
                        _f.ValS = d.Init.Start;
                        _f.ValE = d.Init.End;
                    }

                    InitIdent(id.Name);
                    _f.ValS = -1;
                }
                else
                {
                    Expr(d.Init!);
                    DestructureText = SourceSlice(d.Init!);
                    _iterText = IterTextFor(d.Init!);
                    _destructureSpan = (d.Init!.Start, d.Init.End);
                    BindTarget(d.Id, BindMode.Init);
                }
            }
        }
    }

    private string DestructureText = "undefined";

    /// <summary>Where the value being destructured came from, so a failing pattern points at the source expression.</summary>
    private (int Start, int End)? _destructureSpan;

    /// <summary>V8 prints the initializer in "x is not iterable" when it is a name or a literal; any other expression is named by the value's type.</summary>
    private static string IterTextFor(Node init) => init is Identifier or NullLiteral or BooleanLiteral or NumberLiteral or ThisExpression or RegExpLiteral or ObjectLiteral
        ? CalleeText(init)
        : "\u0002";

    private string SourceSlice(Node n)
    {
        int len = Math.Min(n.End - n.Start, 60);
        return _source.Text.Substring(n.Start, len);
    }

    // ---- loops --------------------------------------------------------------------------------

    private static bool ScopeHasClosures(Scope? s)
    {
        if (s is null)
        {
            return false;
        }

        if (s.ContainsDirectEval)
        {
            return true;
        }

        var stack = new Stack<Scope>(s.Children);
        while (stack.Count > 0)
        {
            Scope c = stack.Pop();
            if (c.Kind is ScopeKind.Function or ScopeKind.Class or ScopeKind.ClassFieldInitializer or ScopeKind.ClassStaticBlock)
            {
                return true;
            }

            foreach (Scope cc in c.Children)
            {
                stack.Push(cc);
            }
        }

        return false;
    }

    private void ForLoop(ForStatement fs, List<string>? labels)
    {
        ResetCompletion();
        CScope? cs = EnterScope(fs.Scope);
        Control? scopeCtl = null;
        if (cs is not null)
        {
            scopeCtl = new Control { Kind = Control.K.Scope };
            _f.Controls.Add(scopeCtl);
        }

        bool perIteration = cs is not null && fs.Init is VariableDeclaration { DeclarationKind: not VarKind.Var } && ScopeHasClosures(fs.Scope);
        if (fs.Init is VariableDeclaration vd)
        {
            VarDecl(vd);
        }
        else if (fs.Init is not null)
        {
            ExprDiscard(fs.Init);
        }

        if (perIteration)
        {
            Emit(Op.CopyScope);
        }

        Control c = PushLoop(labels);
        JumpLabel top = NewLabel();
        Mark(top);
        if (fs.Test is not null)
        {
            Expr(fs.Test);
            Jump(Op.JmpIfFalse, c.Break!);
        }

        Stmt(fs.Body);
        Mark(c.Continue!);
        if (perIteration)
        {
            Emit(Op.CopyScope);
        }

        if (fs.Update is not null)
        {
            ExprDiscard(fs.Update);
        }

        Jump(Op.JmpBack, top);
        PopControl(c);
        Mark(c.Break!);
        if (scopeCtl is not null)
        {
            PopControl(scopeCtl);
        }

        LeaveScope(cs);
    }

    private static bool LoopHeadIsLexical(Node left, out VariableDeclaration? vd)
    {
        vd = left as VariableDeclaration;
        return vd is not null && vd.DeclarationKind != VarKind.Var;
    }

    private void ForInLoop(ForInStatement fi, List<string>? labels)
    {
        ResetCompletion();
        bool lexical = LoopHeadIsLexical(fi.Left, out VariableDeclaration? vd);
        if (vd is { DeclarationKind: VarKind.Var } vv && vv.Declarations[0].Init is not null && vv.Declarations[0].Id is Identifier vid)
        {
            // B.3.5: for (var x = init in obj)
            ExprNamed(vv.Declarations[0].Init!, vid.Name);
            StoreIdent(vid.Name);
        }

        if (lexical && fi.Scope is not null)
        {
            CScope? tdz = EnterScope(fi.Scope);
            Expr(fi.Right);
            LeaveScope(tdz);
        }
        else
        {
            Expr(fi.Right);
        }

        Emit(Op.ForInStart);
        Control c = new() { Kind = Control.K.ForIn, Labels = labels, Break = NewLabel(), Continue = NewLabel(), StackItems = 1 };
        _f.Controls.Add(c);
        JumpLabel top = NewLabel();
        JumpLabel exit = NewLabel();
        Mark(top);
        Jump(Op.ForInNext, exit);
        LoopBody(fi.Left, fi.Scope, lexical, fi.Body, c, false);
        Jump(Op.JmpBack, top);
        PopControl(c);
        _f.Depth--;
        Mark(exit);
        Emit(Op.Pop);
        Mark(c.Break!);
    }

    /// <summary>Binds the loop variable from the value on the stack top and compiles the body.</summary>
    private void LoopBody(Node left, Scope? scope, bool lexical, Node body, Control c, bool inTry)
    {
        CScope? cs = null;
        Control? sc = null;
        if (lexical)
        {
            cs = EnterScope(scope);
            if (cs is not null)
            {
                sc = new Control { Kind = Control.K.Scope };
                _f.Controls.Add(sc);
            }
        }

        if (left is VariableDeclaration vd)
        {
            BindTarget(vd.Declarations[0].Id, lexical ? BindMode.Init : BindMode.Assign);
        }
        else
        {
            BindTarget(left, BindMode.Assign);
        }

        Stmt(body);
        if (sc is not null)
        {
            PopControl(sc);
        }

        LeaveScope(cs);
        Mark(c.Continue!);
    }

    private void ForOfLoop(ForOfStatement fo, List<string>? labels)
    {
        ResetCompletion();
        bool lexical = LoopHeadIsLexical(fo.Left, out _);
        if (lexical && fo.Scope is not null)
        {
            CScope? tdz = EnterScope(fo.Scope);
            Expr(fo.Right);
            LeaveScope(tdz);
        }
        else
        {
            Expr(fo.Right);
        }

        bool isAwait = fo.IsAwait;
        if (isAwait)
        {
            _f.HasAwait = true;
        }

        SetSpan(fo.Right);
        Emit(Op.GetIterator, isAwait ? 1 : 0, TextConst(fo.Right));
        SetSpan(fo);
        Control c = new() { Kind = Control.K.IterLoop, Labels = labels, Break = NewLabel(), Continue = NewLabel(), StackItems = 2, Async = isAwait, RecordDepth = _f.Depth };
        _f.Controls.Add(c);
        JumpLabel top = NewLabel();
        JumpLabel exit = NewLabel();
        JumpLabel handler = NewLabel();
        Mark(top);
        if (isAwait)
        {
            Emit(Op.AsyncIterNextStart);
            Emit(Op.Await);
            Jump(Op.AsyncIterResult, exit);
        }
        else
        {
            Jump(Op.IterNext, exit);
        }

        Jump(Op.TryEnter, handler, 1);
        int depthInBody = _f.Depth;
        LoopBodyTry(fo, lexical, c);
        Emit(Op.TryLeave);
        Mark(c.Continue!);
        Jump(Op.JmpBack, top);
        PopControl(c);

        // Throw completion inside the body: close the iterator, ignoring errors from return(), and rethrow.
        _f.Depth = depthInBody - 1 + 1;
        Mark(handler);
        _f.Depth = depthInBody;
        if (isAwait)
        {
            EmitAsyncCloseSilent();
        }
        else
        {
            Emit(Op.IterCloseSilent, -1);
        }

        Emit(Op.Throw);
        _f.Depth = depthInBody - 1;
        Mark(exit);
        Emit(Op.Pop);
        Emit(Op.Pop);
        Mark(c.Break!);
    }

    private void LoopBodyTry(ForOfStatement fo, bool lexical, Control c)
    {
        CScope? cs = null;
        Control? sc = null;
        if (lexical)
        {
            cs = EnterScope(fo.Scope);
            if (cs is not null)
            {
                sc = new Control { Kind = Control.K.Scope };
                _f.Controls.Add(sc);
            }
        }

        var tryCtl = new Control { Kind = Control.K.Try };
        _f.Controls.Add(tryCtl);
        if (fo.Left is VariableDeclaration vd)
        {
            BindTarget(vd.Declarations[0].Id, lexical ? BindMode.Init : BindMode.Assign);
        }
        else
        {
            BindTarget(fo.Left, BindMode.Assign);
        }

        Stmt(fo.Body);
        PopControl(tryCtl);
        if (sc is not null)
        {
            PopControl(sc);
        }

        LeaveScope(cs);
    }

    private void EmitAsyncCloseSilent()
    {
        // [it, nx, exc]: call return() and await it, swallowing any error, then fall through to the rethrow.
        int t = NewTemp();
        Emit(Op.StoreLocal, t);
        JumpLabel skip = NewLabel();
        JumpLabel caught = NewLabel();
        JumpLabel done = NewLabel();
        Jump(Op.TryEnter, caught);
        Jump(Op.AsyncIterCloseStart, skip);
        Emit(Op.Await);
        Emit(Op.Pop);
        Mark(skip);
        Emit(Op.TryLeave);
        Jump(Op.Jmp, done);
        _f.Depth = Math.Max(0, _f.Depth);
        Mark(caught);
        Emit(Op.Pop);
        Mark(done);
        Emit(Op.LoadLocal, t);
        FreeTemp(t);
    }

    // ---- break, continue, return ---------------------------------------------------------------

    private void Break(string? label)
    {
        for (int i = _f.Controls.Count - 1; i >= 0; i--)
        {
            Control c = _f.Controls[i];
            bool match = label is null
                ? c.Kind is Control.K.Loop or Control.K.IterLoop or Control.K.ForIn or Control.K.Switch
                : c.Labels is not null && c.Labels.Contains(label) && c.Break is not null;
            if (match)
            {
                Unwind(i, c, isContinue: false);
                Jump(Op.Jmp, c.Break!);
                return;
            }
        }

        throw new InvalidOperationException("break target");
    }

    private void Continue(string? label)
    {
        for (int i = _f.Controls.Count - 1; i >= 0; i--)
        {
            Control c = _f.Controls[i];
            bool isLoop = c.Kind is Control.K.Loop or Control.K.IterLoop or Control.K.ForIn;
            if (isLoop && (label is null || (c.Labels is not null && c.Labels.Contains(label))))
            {
                Unwind(i, c, isContinue: true);
                Jump(Op.Jmp, c.Continue!);
                return;
            }
        }

        throw new InvalidOperationException("continue target");
    }

    /// <summary>Emits the cleanup for every control entry above <paramref name="targetIndex"/>, and the target's own for a break.</summary>
    private void Unwind(int targetIndex, Control target, bool isContinue)
    {
        List<Control> saved = _f.Controls;
        int savedDepth = _f.Depth;
        CScope? savedScope = _scope;
        try
        {
            for (int i = saved.Count - 1; i > targetIndex; i--)
            {
                _f.Controls = saved.GetRange(0, i);
                Cleanup(saved[i], false);
            }

            _f.Controls = saved.GetRange(0, targetIndex);
            if (!isContinue)
            {
                CleanupTarget(target);
            }
        }
        finally
        {
            _f.Controls = saved;
            _f.Depth = savedDepth;
            _scope = savedScope;
        }
    }

    private void CleanupTarget(Control c)
    {
        switch (c.Kind)
        {
            case Control.K.IterLoop:
                if (c.Async)
                {
                    EmitAsyncClose();
                }
                else
                {
                    Emit(Op.IterClose);
                }

                Emit(Op.Pop);
                Emit(Op.Pop);
                break;
            case Control.K.ForIn:
                Emit(Op.Pop);
                break;
        }
    }

    private void EmitAsyncClose()
    {
        JumpLabel skip = NewLabel();
        Jump(Op.AsyncIterCloseStart, skip);
        Emit(Op.Await);
        Emit(Op.ThrowIfNotObject);
        Mark(skip);
    }

    /// <summary>Undo one control entry while jumping out through it.</summary>
    private void Cleanup(Control c, bool forReturn)
    {
        switch (c.Kind)
        {
            case Control.K.Scope:
            case Control.K.With:
                if (!forReturn)
                {
                    // A finally block compiled inline after this point runs outside the popped scope, so name
                    // resolution must not count it (a hop too many reads a missing environment).
                    Emit(Op.PopScope);
                    _scope = _scope?.Parent;
                }

                break;
            case Control.K.Try:
                Emit(Op.TryLeave);
                if (c.DoneTemp >= 0)
                {
                    // Iterator of an array destructuring: its record sits StackItems deep; operands above it are still live.
                    Emit(Op.IterCloseIfNotDone, c.DoneTemp, _f.Depth - c.StackItems);
                }

                if (c.Finalizer is not null)
                {
                    FinallyBlock(c.Finalizer);
                }

                break;
            case Control.K.IterLoop:
                if (c.Async)
                {
                    EmitAsyncClose();
                }
                else
                {
                    // Operands above the record (an array destructuring in the loop head) are still on the stack.
                    Emit(Op.IterClose, 0, forReturn ? _f.Depth - c.RecordDepth : 0);
                }

                if (!forReturn)
                {
                    Emit(Op.Pop);
                    Emit(Op.Pop);
                }

                break;
            case Control.K.ForIn:
                if (!forReturn)
                {
                    Emit(Op.Pop);
                }

                break;
        }
    }

    /// <summary>Returns the value on top of the stack, running finally blocks and closing iterators on the way out.</summary>
    private void EmitReturn()
    {
        bool needsCleanup = false;
        foreach (Control c in _f.Controls)
        {
            if (c.Kind == Control.K.Try && (c.Finalizer is not null || c.DoneTemp >= 0) || c.Kind == Control.K.IterLoop)
            {
                needsCleanup = true;
            }
        }

        if (!needsCleanup)
        {
            Emit(Op.Return);
            return;
        }

        int t = NewTemp();
        Emit(Op.StoreLocal, t);
        List<Control> saved = _f.Controls;
        int savedDepth = _f.Depth;
        for (int i = saved.Count - 1; i >= 0; i--)
        {
            _f.Controls = saved.GetRange(0, i);
            Cleanup(saved[i], true);
        }

        _f.Controls = saved;
        _f.Depth = savedDepth;
        Emit(Op.LoadLocal, t);
        Emit(Op.Return);
        FreeTemp(t);
    }

    // ---- try ------------------------------------------------------------------------------------

    /// <summary>
    /// 14.15.3 TryStatement : try Block Finally: a finally block that completes normally leaves the completion value of the
    /// try or catch block in place, so statement values inside it must not leak into the completion register.
    /// </summary>
    private void FinallyBlock(BlockStatement fin)
    {
        if (_f.CompletionTemp < 0)
        {
            Stmt(fin);
            return;
        }

        int keep = NewTemp();
        Emit(Op.LoadLocal, _f.CompletionTemp);
        Emit(Op.StoreLocal, keep);
        ResetCompletion(); // an abrupt exit from the finally block (break, continue) carries its own value, empty meaning undefined
        Stmt(fin);
        Emit(Op.LoadLocal, keep);
        Emit(Op.StoreLocal, _f.CompletionTemp);
        FreeTemp(keep);
    }

    private void Try(TryStatement ts)
    {
        ResetCompletion();
        JumpLabel end = NewLabel();
        JumpLabel finHandler = NewLabel();
        bool hasFin = ts.Finalizer is not null;
        bool hasCatch = ts.Handler is not null;
        int baseDepth = _f.Depth;
        Control? finCtl = null;
        if (hasFin)
        {
            Jump(Op.TryEnter, finHandler);
            finCtl = new Control { Kind = Control.K.Try, Finalizer = ts.Finalizer };
            _f.Controls.Add(finCtl);
        }

        if (hasCatch)
        {
            JumpLabel catchL = NewLabel();
            JumpLabel afterCatch = NewLabel();
            Jump(Op.TryEnter, catchL);
            var tc = new Control { Kind = Control.K.Try };
            _f.Controls.Add(tc);
            Stmt(ts.Block);
            PopControl(tc);
            Emit(Op.TryLeave);
            Jump(Op.Jmp, afterCatch);
            _f.Depth = baseDepth + 1;
            Mark(catchL);
            ResetCompletion(); // 14.15.3 step: the catch block's value replaces the try block's (UpdateEmpty(C, undefined))
            CatchClause h = ts.Handler!;
            CScope? cs = EnterScope(h.Scope);
            Control? sc = null;
            if (cs is not null)
            {
                sc = new Control { Kind = Control.K.Scope };
                _f.Controls.Add(sc);
            }

            if (h.Param is null)
            {
                Emit(Op.Pop);
            }
            else
            {
                BindTarget(h.Param, BindMode.Init);
            }

            Stmt(h.Body);
            if (sc is not null)
            {
                PopControl(sc);
            }

            LeaveScope(cs);
            Mark(afterCatch);
        }
        else
        {
            Stmt(ts.Block);
        }

        if (hasFin)
        {
            PopControl(finCtl!);
            Emit(Op.TryLeave);
            FinallyBlock(ts.Finalizer!);
            Jump(Op.Jmp, end);
            _f.Depth = baseDepth + 1;
            Mark(finHandler);
            int t = NewTemp();
            Emit(Op.StoreLocal, t);
            FinallyBlock(ts.Finalizer!);
            Emit(Op.LoadLocal, t);
            Emit(Op.Throw);
            FreeTemp(t);
            _f.Depth = baseDepth;
        }

        Mark(end);
    }

    // ---- switch ------------------------------------------------------------------------------------

    private void Switch(SwitchStatement sw, List<string>? labels)
    {
        ResetCompletion();
        int t = NewTemp();
        Expr(sw.Discriminant);
        Emit(Op.StoreLocal, t);
        CScope? cs = EnterScope(sw.Scope);
        Control? sc = null;
        if (cs is not null)
        {
            sc = new Control { Kind = Control.K.Scope };
            _f.Controls.Add(sc);
        }

        var all = new List<Node>();
        foreach (SwitchCase k in sw.Cases)
        {
            all.AddRange(k.Consequent);
        }

        HoistFunctions(all);
        var c = new Control { Kind = Control.K.Switch, Labels = labels, Break = NewLabel() };
        _f.Controls.Add(c);
        var caseLabels = new JumpLabel[sw.Cases.Count];
        JumpLabel defaultL = NewLabel();
        bool hasDefault = false;
        for (int i = 0; i < sw.Cases.Count; i++)
        {
            caseLabels[i] = NewLabel();
            SwitchCase k = sw.Cases[i];
            if (k.Test is null)
            {
                hasDefault = true;
                continue;
            }

            Emit(Op.LoadLocal, t);
            Expr(k.Test);
            Jump(Op.JmpIfStrictEq, caseLabels[i]);
        }

        if (hasDefault)
        {
            for (int i = 0; i < sw.Cases.Count; i++)
            {
                if (sw.Cases[i].Test is null)
                {
                    Jump(Op.Jmp, caseLabels[i]);
                }
            }
        }
        else
        {
            Jump(Op.Jmp, c.Break!);
        }

        for (int i = 0; i < sw.Cases.Count; i++)
        {
            Mark(caseLabels[i]);
            Statements(sw.Cases[i].Consequent);
        }

        PopControl(c);
        Mark(c.Break!);
        if (sc is not null)
        {
            PopControl(sc);
        }

        LeaveScope(cs);
        FreeTemp(t);
    }
}
