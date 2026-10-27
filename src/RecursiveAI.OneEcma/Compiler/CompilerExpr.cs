using RecursiveAI.OneEcma.Syntax;
using System.Text;

namespace RecursiveAI.OneEcma;

internal sealed partial class Compiler
{
    private string? _nameHint;
    private Label? _chainEnd;

    private static bool IsAnonymousFunctionDefinition(Node n) => n switch
    {
        FunctionNode f => f.Id is null,
        ClassNode c => c.Id is null,
        _ => false,
    };

    /// <summary>NamedEvaluation (ECMA-262 8.4.5): an anonymous function or class gets the name of the binding or property it is assigned to.</summary>
    private void ExprNamed(Node e, string name)
    {
        if (IsAnonymousFunctionDefinition(e))
        {
            _nameHint = name;
        }

        Expr(e);
        _nameHint = null;
    }

    private void ExprDiscard(Node n)
    {
        int s = _f.CurS;
        int e = _f.CurE;
        SetSpan(n);
        switch (n)
        {
            case AssignmentExpression a:
                Assign(a, true);
                break;
            case UpdateExpression u:
                Update(u, true);
                break;
            case SequenceExpression sq:
                foreach (Expression x in sq.Expressions)
                {
                    ExprDiscard(x);
                }

                break;
            default:
                Expr(n);
                Emit(Op.Pop);
                break;
        }

        SetSpan(s, e);
    }

    /// <summary>Compiles an expression, leaving exactly one value on the operand stack.</summary>
    private void Expr(Node n)
    {
        int s = _f.CurS;
        int e = _f.CurE;
        SetSpan(n);
        ExprCore(n);
        SetSpan(s, e);
    }

    private void ExprCore(Node n)
    {
        switch (n)
        {
            case NumberLiteral nl:
                PushNumber(nl.Value);
                break;
            case StringLiteral sl:
                PushString(sl.Value);
                break;
            case BooleanLiteral bl:
                Emit(bl.Value ? Op.PushTrue : Op.PushFalse);
                break;
            case NullLiteral:
                Emit(Op.PushNull);
                break;
            case BigIntLiteral bi:
                Emit(Op.PushConst, AddConst(JsValue.FromBigInt(bi.Value)));
                break;
            case RegExpLiteral re:
                Emit(Op.NewRegExp, NameConst(re.Pattern), NameConst(re.Flags));
                break;
            case Identifier id:
                LoadIdent(id.Name);
                break;
            case ThisExpression:
                Emit(Op.LoadThis);
                break;
            case TemplateLiteral tl:
                Template(tl);
                break;
            case TaggedTemplate tt:
                TaggedTemplateExpr(tt);
                break;
            case ArrayLiteral al:
                ArrayLit(al);
                break;
            case ObjectLiteral ol:
                ObjectLit(ol);
                break;
            case FunctionNode fn:
                FunctionExpr(fn);
                break;
            case ClassNode cn:
                ClassExpr(cn);
                break;
            case UnaryExpression u:
                Unary(u);
                break;
            case UpdateExpression up:
                Update(up, false);
                break;
            case BinaryExpression b:
                Binary(b);
                break;
            case LogicalExpression l:
                Logical(l);
                break;
            case AssignmentExpression a:
                Assign(a, false);
                break;
            case ConditionalExpression c:
                {
                    Label alt = NewLabel();
                    Label end = NewLabel();
                    Expr(c.Test);
                    Jump(Op.JmpIfFalse, alt);
                    Expr(c.Consequent);
                    Jump(Op.Jmp, end);
                    _f.Depth--;
                    Mark(alt);
                    Expr(c.Alternate);
                    Mark(end);
                    break;
                }

            case CallExpression call:
                CallExpr(call);
                break;
            case NewExpression ne:
                NewExpr(ne);
                break;
            case MemberExpression m:
                Member(m);
                break;
            case ChainExpression ch:
                {
                    Label saved = _chainEnd!;
                    Label end = NewLabel();
                    _chainEnd = end;
                    Expr(ch.Expression);
                    Mark(end);
                    _chainEnd = saved;
                    break;
                }

            case SequenceExpression sq:
                for (int i = 0; i < sq.Expressions.Count; i++)
                {
                    if (i < sq.Expressions.Count - 1)
                    {
                        ExprDiscard(sq.Expressions[i]);
                    }
                    else
                    {
                        Expr(sq.Expressions[i]);
                    }
                }

                break;
            case YieldExpression y:
                YieldExpr(y);
                break;
            case AwaitExpression aw:
                Expr(aw.Argument);
                Emit(Op.Await);
                _f.HasAwait = true;
                break;
            case MetaProperty mp:
                if (mp.Meta == "new")
                {
                    Emit(Op.LoadNewTarget);
                }
                else
                {
                    Emit(Op.ImportMeta);
                }

                break;
            case ImportCall ic:
                Expr(ic.Source);
                if (ic.Options is not null)
                {
                    Expr(ic.Options);
                }

                Emit(Op.ImportCallOp, ic.Options is null ? 0 : 1);
                break;
            case SuperExpression:
                throw new InvalidOperationException("bare super");
            case SpreadElement:
                throw new InvalidOperationException("spread outside list");
            default:
                throw new NotSupportedException("expression " + n.Kind);
        }
    }

    // ---- identifiers --------------------------------------------------------------------------

    private void LoadIdent(string name)
    {
        Ref r = Resolve(name);
        switch (r.Kind)
        {
            case RefKind.Local:
                if (r.BindingKind == ScopeInfo.KImport)
                {
                    Emit(Op.LoadImport, r.Hops, r.Slot);
                }
                else
                {
                    Emit(r.BindingKind is ScopeInfo.KLet or ScopeInfo.KConst ? Op.LoadSlotChecked : Op.LoadSlot, r.Hops, r.Slot);
                }

                break;
            case RefKind.Global:
                if (name == "undefined")
                {
                    Emit(Op.PushUndef);
                }
                else
                {
                    Emit(Op.LoadGlobal, NameConst(name), NewCache());
                }

                break;
            default:
                Emit(Op.LoadDyn, NameConst(name));
                break;
        }
    }

    /// <summary>Stores the value on top of the stack (popping it) into the binding named <paramref name="name"/> (PutValue / SetMutableBinding).</summary>
    private void StoreIdent(string name)
    {
        Ref r = Resolve(name);
        switch (r.Kind)
        {
            case RefKind.Local:
                switch (r.BindingKind)
                {
                    case ScopeInfo.KConst:
                    case ScopeInfo.KImport:
                        Emit(Op.ThrowConstAssign, r.Hops, r.Slot);
                        Emit(Op.Pop);
                        break;
                    case ScopeInfo.KFuncName:
                        if (_f.Strict)
                        {
                            Emit(Op.ThrowConstAssign, r.Hops, r.Slot);
                        }

                        Emit(Op.Pop);
                        break;
                    case ScopeInfo.KLet:
                        Emit(Op.StoreSlotChecked, r.Hops, r.Slot);
                        break;
                    default:
                        Emit(Op.StoreSlot, r.Hops, r.Slot);
                        break;
                }

                break;
            case RefKind.Global:
                Emit(Op.StoreGlobal, NameConst(name), _f.Strict ? 1 : 0);
                break;
            default:
                Emit(Op.StoreDyn, NameConst(name), _f.Strict ? 1 : 0);
                break;
        }
    }

    /// <summary>InitializeReferencedBinding for let/const/class/parameter bindings (no TDZ or const check).</summary>
    private void InitIdent(string name)
    {
        Ref r = Resolve(name);
        switch (r.Kind)
        {
            case RefKind.Local:
                Emit(Op.InitSlot, r.Hops, r.Slot);
                break;
            case RefKind.Global:
                Emit(Op.InitGlobalLexical, NameConst(name));
                break;
            default:
                Emit(Op.InitDyn, NameConst(name));
                break;
        }
    }

    // ---- operators ----------------------------------------------------------------------------

    private static Op BinOp(BinaryOp op) => op switch
    {
        BinaryOp.Add => Op.Add,
        BinaryOp.Subtract => Op.Sub,
        BinaryOp.Multiply => Op.Mul,
        BinaryOp.Divide => Op.Div,
        BinaryOp.Remainder => Op.Mod,
        BinaryOp.Exponent => Op.Exp,
        BinaryOp.Equal => Op.Eq,
        BinaryOp.NotEqual => Op.Ne,
        BinaryOp.StrictEqual => Op.StrictEq,
        BinaryOp.StrictNotEqual => Op.StrictNe,
        BinaryOp.LessThan => Op.Lt,
        BinaryOp.LessThanOrEqual => Op.Le,
        BinaryOp.GreaterThan => Op.Gt,
        BinaryOp.GreaterThanOrEqual => Op.Ge,
        BinaryOp.ShiftLeft => Op.Shl,
        BinaryOp.ShiftRight => Op.Shr,
        BinaryOp.UnsignedShiftRight => Op.UShr,
        BinaryOp.BitwiseAnd => Op.BitAnd,
        BinaryOp.BitwiseOr => Op.BitOr,
        BinaryOp.BitwiseXor => Op.BitXor,
        BinaryOp.In => Op.In,
        _ => Op.InstanceOf,
    };

    private static BinaryOp? CompoundOp(AssignOp op) => op switch
    {
        AssignOp.AddAssign => BinaryOp.Add,
        AssignOp.SubtractAssign => BinaryOp.Subtract,
        AssignOp.MultiplyAssign => BinaryOp.Multiply,
        AssignOp.DivideAssign => BinaryOp.Divide,
        AssignOp.RemainderAssign => BinaryOp.Remainder,
        AssignOp.ExponentAssign => BinaryOp.Exponent,
        AssignOp.ShiftLeftAssign => BinaryOp.ShiftLeft,
        AssignOp.ShiftRightAssign => BinaryOp.ShiftRight,
        AssignOp.UnsignedShiftRightAssign => BinaryOp.UnsignedShiftRight,
        AssignOp.BitwiseAndAssign => BinaryOp.BitwiseAnd,
        AssignOp.BitwiseOrAssign => BinaryOp.BitwiseOr,
        AssignOp.BitwiseXorAssign => BinaryOp.BitwiseXor,
        _ => null,
    };

    private void Binary(BinaryExpression b)
    {
        if (b.Operator == BinaryOp.In && b.Left is PrivateIdentifier pi)
        {
            LoadPrivateName(pi.Name);
            Expr(b.Right);
            Emit(Op.PrivateIn);
            return;
        }

        // Left-nested chains (a+b+c+...) can be 100,000 deep: walk the spine with a loop.
        var spine = new Stack<BinaryExpression>();
        Node cur = b;
        while (cur is BinaryExpression bx && !(bx.Operator == BinaryOp.In && bx.Left is PrivateIdentifier))
        {
            spine.Push(bx);
            cur = bx.Left;
        }

        Expr(cur);
        while (spine.Count > 0)
        {
            BinaryExpression x = spine.Pop();
            Expr(x.Right);
            int s = _f.CurS;
            int e = _f.CurE;
            SetSpan(x);
            Emit(BinOp(x.Operator));
            SetSpan(s, e);
        }
    }

    private void Logical(LogicalExpression l)
    {
        Label end = NewLabel();
        Expr(l.Left);
        switch (l.Operator)
        {
            case LogicalOp.And:
                Jump(Op.JmpIfFalseKeep, end);
                break;
            case LogicalOp.Or:
                Jump(Op.JmpIfTrueKeep, end);
                break;
            default:
                Jump(Op.JmpIfNotNullishKeep, end);
                break;
        }

        Expr(l.Right);
        Mark(end);
    }

    private void Unary(UnaryExpression u)
    {
        switch (u.Operator)
        {
            case UnaryOp.Typeof:
                if (u.Argument is Identifier id)
                {
                    Ref r = Resolve(id.Name);
                    if (r.Kind == RefKind.Global)
                    {
                        Emit(Op.TypeofGlobal, NameConst(id.Name));
                        return;
                    }

                    if (r.Kind == RefKind.Dynamic)
                    {
                        Emit(Op.TypeofDyn, NameConst(id.Name));
                        return;
                    }
                }

                Expr(u.Argument);
                Emit(Op.Typeof);
                return;
            case UnaryOp.Void:
                ExprDiscard(u.Argument);
                Emit(Op.PushUndef);
                return;
            case UnaryOp.Delete:
                Delete(u.Argument);
                return;
        }

        Expr(u.Argument);
        Emit(u.Operator switch { UnaryOp.Minus => Op.Neg, UnaryOp.Plus => Op.Plus, UnaryOp.Not => Op.Not, _ => Op.BitNot });
    }

    private void Delete(Expression arg)
    {
        switch (arg)
        {
            case Identifier id:
                {
                    Ref r = Resolve(id.Name);
                    if (r.Kind == RefKind.Local)
                    {
                        Emit(Op.PushFalse);
                    }
                    else
                    {
                        Emit(Op.DeleteDyn, NameConst(id.Name), r.Kind == RefKind.Global ? 1 : 0);
                    }

                    return;
                }

            case MemberExpression { Object: SuperExpression } sm:
                if (sm.Computed)
                {
                    ExprDiscard(sm.Property);
                }

                Emit(Op.LoadThis);
                Emit(Op.Pop);
                Emit(Op.ThrowError, 1, NameConst("Unsupported reference to 'super'"));
                Emit(Op.PushTrue);
                return;
            case MemberExpression m:
                Expr(m.Object);
                if (m.IsOptional)
                {
                    Emit(Op.JmpNullishUndef, 0);
                    PatchChain();
                }

                if (m.Computed)
                {
                    Expr(m.Property);
                    Emit(Op.DeleteElem, _f.Strict ? 1 : 0);
                }
                else
                {
                    Emit(Op.DeleteProp, NameConst(((Identifier)m.Property).Name), _f.Strict ? 1 : 0);
                }

                return;
            case ChainExpression ch:
                {
                    Label saved = _chainEnd!;
                    Label end = NewLabel();
                    _chainEnd = end;
                    Delete(ch.Expression);
                    Mark(end);
                    _chainEnd = saved;
                    return;
                }

            default:
                ExprDiscard(arg);
                Emit(Op.PushTrue);
                return;
        }
    }

    /// <summary>Redirects the most recent optional-chain jump to the end of the chain.</summary>
    private void PatchChain()
    {
        int at = _f.Ops.Count - 1;
        (_chainEnd!.Fixups ??= []).Add(at);
        if (_chainEnd.Pc >= 0)
        {
            Instr i = _f.Ops[at];
            i.A = _chainEnd.Pc;
            _f.Ops[at] = i;
        }
    }

    // ---- members and calls -------------------------------------------------------------------

    private void Member(MemberExpression m)
    {
        if (m.Object is SuperExpression)
        {
            if (m.Computed)
            {
                Expr(m.Property);
                Emit(Op.GetSuperElem);
            }
            else
            {
                Emit(Op.GetSuper, NameConst(((Identifier)m.Property).Name));
            }

            return;
        }

        Expr(m.Object);
        if (m.IsOptional)
        {
            Emit(Op.JmpNullishUndef);
            PatchChain();
        }

        SetSpan(m.Object);
        if (m.Property is PrivateIdentifier pi)
        {
            LoadPrivateName(pi.Name);
            Emit(Op.PrivateGet);
        }
        else if (m.Computed)
        {
            Expr(m.Property);
            SetSpan(m.Object);
            Emit(Op.GetElem);
        }
        else
        {
            Emit(Op.GetProp, NameConst(((Identifier)m.Property).Name), NewCache());
        }
    }

    private void LoadPrivateName(string name)
    {
        Ref r = Resolve("#" + name);
        Emit(Op.LoadSlot, r.Hops, r.Slot);
    }

    /// <summary>The text V8 prints for a callee in "x is not a function".</summary>
    private string CalleeText(Node n)
    {
        switch (n)
        {
            case Identifier id:
                return id.Name;
            case ThisExpression:
                return "this";
            case SuperExpression:
                return "super";
            case MemberExpression m:
                {
                    string o = CalleeText(m.Object);
                    if (m.Property is PrivateIdentifier pi)
                    {
                        return o + ".#" + pi.Name;
                    }

                    if (!m.Computed)
                    {
                        return o + "." + ((Identifier)m.Property).Name;
                    }

                    return m.Property switch
                    {
                        StringLiteral sl => o + "." + sl.Value,
                        NumberLiteral nl => o + "[" + Ops.NumberToStr(nl.Value) + "]",
                        Identifier pid => o + "[" + pid.Name + "]",
                        _ => o + "[...]",
                    };
                }

            case CallExpression c:
                return CalleeText(c.Callee) + "(...)";
            case ChainExpression ch:
                return CalleeText(ch.Expression);
            case StringLiteral sl:
                return "\"" + sl.Value + "\"";
            case NumberLiteral nl:
                return Ops.NumberToStr(nl.Value);
            default:
                return "(intermediate value)";
        }
    }

    private int TextConst(Node n) => NameConst(CalleeText(n));

    private void CallExpr(CallExpression call)
    {
        Expression callee = call.Callee;
        int textIdx = TextConst(callee);
        bool hasSpread = HasSpread(call.Arguments);
        bool direct = call.IsDirectEvalCandidate && callee is Identifier { Name: "eval" };

        // Push [function, this].
        switch (callee)
        {
            case MemberExpression { Object: SuperExpression } sm:
                if (sm.Computed)
                {
                    Expr(sm.Property);
                    Emit(Op.GetSuperElem);
                }
                else
                {
                    Emit(Op.GetSuper, NameConst(((Identifier)sm.Property).Name));
                }

                Emit(Op.LoadThis);
                break;
            case MemberExpression m:
                Expr(m.Object);
                if (m.IsOptional)
                {
                    Emit(Op.JmpNullishUndef);
                    PatchChain();
                }

                SetSpan(m.Object);
                if (m.Property is PrivateIdentifier pi)
                {
                    Emit(Op.Dup);
                    LoadPrivateName(pi.Name);
                    Emit(Op.PrivateGet);
                    Emit(Op.Swap);
                }
                else if (m.Computed)
                {
                    Expr(m.Property);
                    SetSpan(m.Object);
                    Emit(Op.GetElemMethod);
                }
                else
                {
                    Emit(Op.GetMethod, NameConst(((Identifier)m.Property).Name), NewCache());
                }

                break;
            case Identifier id:
                {
                    Ref r = Resolve(id.Name);
                    if (r.Kind == RefKind.Dynamic)
                    {
                        Emit(Op.LoadDynCall, NameConst(id.Name));
                    }
                    else
                    {
                        LoadIdent(id.Name);
                        Emit(Op.PushUndef);
                    }

                    break;
                }

            case ChainExpression ch when ch.Expression is MemberExpression:
                {
                    // (a?.b)() keeps `a` as the this value.
                    Label saved = _chainEnd!;
                    Label end = NewLabel();
                    _chainEnd = end;
                    var inner = (MemberExpression)ch.Expression;
                    Expr(inner.Object);
                    if (inner.IsOptional)
                    {
                        Emit(Op.JmpNullishUndef);
                        PatchChain();
                    }

                    if (inner.Computed)
                    {
                        Expr(inner.Property);
                        Emit(Op.GetElemMethod);
                    }
                    else
                    {
                        Emit(Op.GetMethod, NameConst(((Identifier)inner.Property).Name), NewCache());
                    }

                    Label done = NewLabel();
                    Jump(Op.Jmp, done);
                    Mark(end);
                    Emit(Op.PushUndef);
                    _f.Depth--;
                    Mark(done);
                    _chainEnd = saved;
                    break;
                }

            default:
                Expr(callee);
                Emit(Op.PushUndef);
                break;
        }

        if (call.IsOptional)
        {
            Emit(Op.JmpNullishUndef2);
            PatchChain();
        }

        int callSpanS = callee.Start;
        int callSpanE = callee.End;
        if (hasSpread)
        {
            ArgsArray(call.Arguments);
            SetSpan(callSpanS, callSpanE);
            Emit(direct ? Op.CallEvalSpread : Op.CallSpread, 0, textIdx);
        }
        else
        {
            foreach (Node a in call.Arguments)
            {
                Expr(a);
            }

            SetSpan(callSpanS, callSpanE);
            Emit(direct ? Op.CallEval : Op.Call, call.Arguments.Count, direct ? EvalFlags() : textIdx);
        }

        SetSpan(call);
    }

    private int EvalFlags()
    {
        // bit0 strict, bit1 new.target allowed, bit2 super property allowed, bit3 super call allowed, bit4 in field initializer
        int f = _f.Strict ? 1 : 0;
        FnState? fs = _f;
        while (fs is not null && fs.Kind == FunctionKind.Arrow)
        {
            fs = fs.Outer;
        }

        FunctionKind k = fs?.Kind ?? FunctionKind.Script;
        if (k is FunctionKind.Normal or FunctionKind.Method or FunctionKind.Getter or FunctionKind.Setter or FunctionKind.ClassConstructor or FunctionKind.DerivedConstructor or FunctionKind.FieldInit or FunctionKind.StaticBlock)
        {
            f |= 2;
        }

        if (k is FunctionKind.Method or FunctionKind.Getter or FunctionKind.Setter or FunctionKind.ClassConstructor or FunctionKind.DerivedConstructor or FunctionKind.FieldInit or FunctionKind.StaticBlock)
        {
            f |= 4;
        }

        if (k == FunctionKind.DerivedConstructor)
        {
            f |= 8;
        }

        if (k == FunctionKind.FieldInit)
        {
            f |= 16;
        }

        if (k is FunctionKind.Eval)
        {
            f |= 0; // inherits through the dynamic flags passed by the caller frame
        }

        return f;
    }

    private static bool HasSpread(IReadOnlyList<Node> args)
    {
        foreach (Node a in args)
        {
            if (a is SpreadElement)
            {
                return true;
            }
        }

        return false;
    }

    private void ArgsArray(IReadOnlyList<Node> args)
    {
        Emit(Op.NewArray);
        foreach (Node a in args)
        {
            if (a is SpreadElement sp)
            {
                Expr(sp.Argument);
                Emit(Op.ArraySpread);
            }
            else
            {
                Expr(a);
                Emit(Op.ArrayPush);
            }
        }
    }

    private void NewExpr(NewExpression ne)
    {
        int textIdx = TextConst(ne.Callee);
        Expr(ne.Callee);
        if (HasSpread(ne.Arguments))
        {
            ArgsArray(ne.Arguments);
            SetSpan(ne.Callee);
            Emit(Op.NewSpread, 0, textIdx);
        }
        else
        {
            foreach (Node a in ne.Arguments)
            {
                Expr(a);
            }

            SetSpan(ne.Callee);
            Emit(Op.New, ne.Arguments.Count, textIdx);
        }
    }

    // ---- literals ------------------------------------------------------------------------------

    private void Template(TemplateLiteral tl)
    {
        PushString(tl.Quasis[0].Cooked ?? "");
        for (int i = 0; i < tl.Expressions.Count; i++)
        {
            Expr(tl.Expressions[i]);
            Emit(Op.ToStringOp);
            Emit(Op.TemplateConcat);
            string next = tl.Quasis[i + 1].Cooked ?? "";
            if (next.Length > 0)
            {
                PushString(next);
                Emit(Op.TemplateConcat);
            }
        }
    }

    private void TaggedTemplateExpr(TaggedTemplate tt)
    {
        // The tag is evaluated as a call target, so member tags keep their this value.
        switch (tt.Tag)
        {
            case MemberExpression { Object: not SuperExpression } m when m.Property is not PrivateIdentifier:
                Expr(m.Object);
                if (m.Computed)
                {
                    Expr(m.Property);
                    Emit(Op.GetElemMethod);
                }
                else
                {
                    Emit(Op.GetMethod, NameConst(((Identifier)m.Property).Name), NewCache());
                }

                break;
            default:
                Expr(tt.Tag);
                Emit(Op.PushUndef);
                break;
        }

        var cooked = new List<string?>();
        var raw = new List<string>();
        foreach (TemplateElement q in tt.Quasi.Quasis)
        {
            cooked.Add(q.Cooked);
            raw.Add(q.Raw);
        }

        Emit(Op.TemplateObject, AddTable(new TemplateSite(cooked, raw)));
        foreach (Expression x in tt.Quasi.Expressions)
        {
            Expr(x);
        }

        SetSpan(tt.Tag);
        Emit(Op.Call, tt.Quasi.Expressions.Count + 1, TextConst(tt.Tag));
    }

    private void ArrayLit(ArrayLiteral al)
    {
        Emit(Op.NewArray);
        foreach (Node? el in al.Elements)
        {
            if (el is null)
            {
                Emit(Op.ArrayPushHole);
            }
            else if (el is SpreadElement sp)
            {
                Expr(sp.Argument);
                Emit(Op.ArraySpread);
            }
            else
            {
                Expr(el);
                Emit(Op.ArrayPush);
            }
        }
    }

    private static string? StaticKeyName(Node key, bool computed)
    {
        if (computed)
        {
            return null;
        }

        return key switch
        {
            Identifier id => id.Name,
            StringLiteral s => s.Value,
            NumberLiteral n => Ops.NumberToStr(n.Value),
            BigIntLiteral b => b.Value.ToString(),
            _ => null,
        };
    }

    private void ObjectLit(ObjectLiteral ol)
    {
        Emit(Op.NewObject);
        foreach (Node p in ol.Properties)
        {
            if (p is SpreadElement sp)
            {
                Expr(sp.Argument);
                Emit(Op.CopyDataProps);
                continue;
            }

            var prop = (Property)p;
            string? name = StaticKeyName(prop.Key, prop.Computed);
            switch (prop.PropertyKind)
            {
                case PropertyKind.Init:
                    if (name is not null)
                    {
                        if (name == "__proto__" && !prop.Shorthand && !prop.Computed)
                        {
                            Expr(prop.Value);
                            Emit(Op.SetProtoLiteral);
                        }
                        else
                        {
                            ExprNamed(prop.Value, name);
                            Emit(Op.DefineField, NameConst(name));
                        }
                    }
                    else
                    {
                        Expr(prop.Key);
                        Emit(Op.ToPropKey);
                        bool anon = IsAnonymousFunctionDefinition(prop.Value);
                        Expr(prop.Value);
                        if (anon)
                        {
                            Emit(Op.SetFnNameKey, 0);
                        }

                        Emit(Op.DefineFieldComputed);
                    }

                    break;
                default:
                    MethodProperty(prop, name, 1);
                    break;
            }
        }
    }

    /// <summary>Defines a method, getter or setter on the object under the top of the stack (object literal or class prototype/constructor).</summary>
    private void MethodProperty(Property prop, string? name, int enumerableFlag)
    {
        var fn = (FunctionNode)prop.Value;
        int kindFlag = prop.PropertyKind == PropertyKind.Get ? 2 : prop.PropertyKind == PropertyKind.Set ? 4 : 0;
        MethodCommon(prop.Key, prop.Computed, name, fn, kindFlag | enumerableFlag);
    }

    private void MethodCommon(Node key, bool computed, string? name, FunctionNode fn, int flags)
    {
        string prefix = (flags & 2) != 0 ? "get " : (flags & 4) != 0 ? "set " : "";
        if (name is not null)
        {
            _nameHint = prefix + name;
            CompileClosure(fn);
            Emit(Op.DefineMethodOp, NameConst(name), flags);
        }
        else
        {
            Expr(key);
            Emit(Op.ToPropKey);
            _nameHint = "";
            CompileClosure(fn);
            Emit(Op.SetFnNameKey, (flags & 2) != 0 ? 1 : (flags & 4) != 0 ? 2 : 0);
            Emit(Op.DefineMethodOp, -1, flags);
        }
    }

    private void YieldExpr(YieldExpression y)
    {
        bool asyncGen = _f.IsAsync;
        if (y.IsDelegate)
        {
            Expr(y.Argument!);
            Emit(Op.GetIterator, asyncGen ? 1 : 0, NameConst("yield*"));
            Emit(Op.PushUndef);
            Label ret = NewLabel();
            Label end = NewLabel();
            Jump(Op.YieldStar, ret, asyncGen ? 1 : 0);
            Jump(Op.Jmp, end);
            Mark(ret);
            EmitReturnFromTop();
            Mark(end);
            return;
        }

        if (y.Argument is null)
        {
            Emit(Op.PushUndef);
        }
        else
        {
            Expr(y.Argument);
        }

        if (asyncGen)
        {
            Emit(Op.Await);
        }

        Label retL = NewLabel();
        Label cont = NewLabel();
        Jump(Op.Yield, retL, asyncGen ? 1 : 0);
        Jump(Op.Jmp, cont);
        Mark(retL);
        EmitReturnFromTop();
        Mark(cont);
    }

    /// <summary>The generator was resumed with return(value): run finally blocks and return the value on top of the stack.</summary>
    private void EmitReturnFromTop()
    {
        int saved = _f.Depth;
        if (_f.IsAsync)
        {
            Emit(Op.Await);
        }

        EmitReturn();
        _f.Depth = saved;
    }
}

/// <summary>A tagged-template call site: the strings that build the cached template object (13.2.8.4).</summary>
internal sealed class TemplateSite
{
    internal TemplateSite(List<string?> cooked, List<string> raw)
    {
        Cooked = cooked;
        Raw = raw;
    }

    internal List<string?> Cooked { get; }

    internal List<string> Raw { get; }
}
