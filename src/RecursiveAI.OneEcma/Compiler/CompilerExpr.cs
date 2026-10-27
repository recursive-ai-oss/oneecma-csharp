using RecursiveAI.OneEcma.Syntax;
using System.Text;

namespace RecursiveAI.OneEcma;

internal sealed partial class Compiler
{
    private string? _nameHint;
    private JumpLabel? _chainEnd;

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

    /// <summary>13.15.2 step 1.c: the anonymous function takes the target's name only when the target is an IdentifierRef, which a parenthesized identifier is not.</summary>
    private void ExprNamedUnlessParenthesized(Node e, Identifier target)
    {
        if (target.IsParenthesized)
        {
            Expr(e);
        }
        else
        {
            ExprNamed(e, target.Name);
        }
    }

    /// <summary>13.3.7.1 SuperProperty : super [ Expression ]: the this binding is read (and may throw) before the key expression runs.</summary>
    private void SuperKeyExpr(MemberExpression sm)
    {
        Emit(Op.LoadThis);
        Emit(Op.Pop);
        Expr(sm.Property);
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
        EnsureStack();
        int s = _f.CurS;
        int e = _f.CurE;
        SetSpan(n);
        ExprCore(n);
        SetSpan(s, e);
    }

    /// <summary>
    /// Compiling recurses over the syntax tree. Source can nest far deeper than the host stack allows, so stop with the same
    /// catchable RangeError a script gets for runaway recursion instead of overflowing the host stack.
    /// </summary>
    private static void EnsureStack()
    {
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw Errors.Range("Maximum call stack size exceeded");
        }
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
                    JumpLabel alt = NewLabel();
                    JumpLabel end = NewLabel();
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
                    JumpLabel saved = _chainEnd!;
                    JumpLabel end = NewLabel();
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
        // V8 reports a failed variable lookup (not defined, before initialization) at the start of the statement.
        if (_f.StmtStart >= 0)
        {
            int keep = _f.CurP;
            _f.CurP = _f.StmtStart;
            LoadIdentCore(name);
            _f.CurP = keep;
            return;
        }

        LoadIdentCore(name);
    }

    private void LoadIdentCore(string name)
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
            _f.CurP = OperatorPos(x.Left.End);
            Emit(BinOp(x.Operator));
            SetSpan(s, e);
        }
    }

    private void Logical(LogicalExpression l, bool tail = false)
    {
        // Left-nested chains (a || b || c ...) can be 100,000 deep: walk the spine with a loop.
        var spine = new Stack<LogicalExpression>();
        Node cur = l;
        while (cur is LogicalExpression lx)
        {
            spine.Push(lx);
            cur = lx.Left;
        }

        Expr(cur);
        while (spine.Count > 0)
        {
            LogicalExpression x = spine.Pop();
            JumpLabel end = NewLabel();
            switch (x.Operator)
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

            if (tail && spine.Count == 0)
            {
                ExprTail(x.Right);
            }
            else
            {
                Expr(x.Right);
            }

            Mark(end);
        }
    }

    /// <summary>
    /// ECMA-262 15.10 tail position calls: in strict code a call in tail position reuses the caller's frame. Only
    /// plain functions qualify (not constructors, generators or async functions) and only outside try blocks and
    /// iterator loops, where the frame still has cleanup to do.
    /// </summary>
    private bool CanTailCall()
    {
        if (!Agent.CurrentOrNull?.ProperTailCalls ?? true)
        {
            return false;
        }

        if (!_f.Strict || _f.IsAsync || _f.IsGenerator || _f.Kind is not (FunctionKind.Normal or FunctionKind.Arrow or FunctionKind.Method or FunctionKind.Getter or FunctionKind.Setter))
        {
            return false;
        }

        foreach (Control c in _f.Controls)
        {
            if (c.Kind is Control.K.Try or Control.K.IterLoop)
            {
                return false;
            }
        }

        return true;
    }

    private bool _tailCall;

    /// <summary>Compiles an expression whose value is returned directly, turning calls in tail position into tail calls.</summary>
    private void ExprTail(Node n)
    {
        if (!CanTailCall())
        {
            Expr(n);
            return;
        }

        switch (n)
        {
            case CallExpression call when !call.IsOptional && call.Callee is not (SuperExpression or ChainExpression) && !HasSpread(call.Arguments):
                {
                    int s = _f.CurS;
                    int e = _f.CurE;
                    SetSpan(n);
                    _tailCall = true;
                    CallExpr(call);
                    _tailCall = false;
                    SetSpan(s, e);
                    break;
                }

            case TaggedTemplate:
                {
                    int s = _f.CurS;
                    int e = _f.CurE;
                    SetSpan(n);
                    _tailCall = true;
                    Expr(n);
                    _tailCall = false;
                    SetSpan(s, e);
                    break;
                }

            case ConditionalExpression c:
                {
                    JumpLabel alt = NewLabel();
                    JumpLabel end = NewLabel();
                    Expr(c.Test);
                    Jump(Op.JmpIfFalse, alt);
                    ExprTail(c.Consequent);
                    Jump(Op.Jmp, end);
                    _f.Depth--;
                    Mark(alt);
                    ExprTail(c.Alternate);
                    Mark(end);
                    break;
                }

            case SequenceExpression sq:
                for (int i = 0; i < sq.Expressions.Count - 1; i++)
                {
                    ExprDiscard(sq.Expressions[i]);
                }

                ExprTail(sq.Expressions[^1]);
                break;
            case LogicalExpression l:
                Logical(l, true);
                break;
            default:
                Expr(n);
                break;
        }
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
                    Emit(Op.DeleteElem, 0, _f.Strict ? 1 : 0);
                }
                else
                {
                    Emit(Op.DeleteProp, NameConst(((Identifier)m.Property).Name), _f.Strict ? 1 : 0);
                }

                return;
            case ChainExpression ch:
                {
                    JumpLabel saved = _chainEnd!;
                    JumpLabel end = NewLabel();
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
                SuperKeyExpr(m);
                Emit(Op.GetSuperElem);
            }
            else
            {
                Emit(Op.GetSuper, NameConst(((Identifier)m.Property).Name));
            }

            return;
        }

        // A chain like a.b.c.d... can be 100,000 links long: walk its spine with a loop instead of recursing.
        var spine = new Stack<MemberExpression>();
        Node cur = m;
        while (cur is MemberExpression mx && mx.Object is not SuperExpression)
        {
            spine.Push(mx);
            cur = mx.Object;
        }

        Expr(cur);
        while (spine.Count > 0)
        {
            MemberExpression x = spine.Pop();
            if (x.IsOptional)
            {
                Emit(Op.JmpNullishUndef);
                PatchChain();
            }

            int s = _f.CurS;
            int e = _f.CurE;
            SetSpan(x.Object);
            _f.CurP = x.Property.Start;
            if (x.Property is PrivateIdentifier pi)
            {
                LoadPrivateName(pi.Name);
                Emit(Op.PrivateGet);
            }
            else if (x.Computed)
            {
                Expr(x.Property);
                SetSpan(x.Object);
                _f.CurP = x.Property.Start;
                Emit(Op.GetElem);
            }
            else
            {
                Emit(Op.GetProp, NameConst(((Identifier)x.Property).Name), NewCache());
            }

            SetSpan(s, e);
        }
    }

    private void LoadPrivateName(string name)
    {
        Ref r = Resolve("#" + name);
        if (r.Kind == RefKind.Dynamic)
        {
            // Direct eval code finds the enclosing class's private names by name at run time.
            Emit(Op.LoadDyn, NameConst("#" + name));
            return;
        }

        Emit(Op.LoadSlot, r.Hops, r.Slot);
    }

    /// <summary>The text V8 prints for a callee in "x is not a function".</summary>
    private static string CalleeText(Node n, int depth = 0)
    {
        if (depth > 16)
        {
            // Messages stay short and the walk stays shallow however long the chain is.
            return "(intermediate value)";
        }

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
                    string o = CalleeText(m.Object, depth + 1);
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
                return CalleeText(c.Callee, depth + 1) + "(...)";
            case ChainExpression ch:
                return CalleeText(ch.Expression, depth + 1);
            case NullLiteral:
                return "null";
            case BooleanLiteral bl:
                return bl.Value ? "true" : "false";
            case RegExpLiteral rl:
                return "/" + rl.Pattern + "/" + rl.Flags;
            case ObjectLiteral ol:
                return ol.Properties.Count == 0 ? "{}" : "{(intermediate value)}";
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
        bool tailRequested = _tailCall;
        _tailCall = false;
        Expression callee = call.Callee;
        int textIdx = TextConst(callee);
        bool hasSpread = HasSpread(call.Arguments);
        bool direct = call.IsDirectEvalCandidate && callee is Identifier { Name: "eval" };

        if (callee is SuperExpression)
        {
            // ECMA-262 13.3.7.1 SuperCall: evaluate the arguments, then construct through the parent constructor.
            if (hasSpread)
            {
                ArgsArray(call.Arguments);
                SetSpan(call);
                Emit(Op.SuperCallSpread);
            }
            else
            {
                foreach (Node a in call.Arguments)
                {
                    Expr(a);
                }

                SetSpan(call);
                Emit(Op.SuperCall, call.Arguments.Count);
            }

            return;
        }

        // Push [function, this].
        switch (callee)
        {
            case MemberExpression { Object: SuperExpression } sm:
                if (sm.Computed)
                {
                    SuperKeyExpr(sm);
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
                _f.CurP = m.Property.Start;
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
                    _f.CurP = m.Property.Start;
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
                    JumpLabel saved = _chainEnd!;
                    JumpLabel end = NewLabel();
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

                    JumpLabel done = NewLabel();
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
            ArgsArray(call.Arguments, CallPos(callee));
            SetSpan(callSpanS, callSpanE);
            _f.CurP = CallPos(callee);
            Emit(direct ? Op.CallEvalSpread : Op.CallSpread, 0, textIdx);
        }
        else
        {
            foreach (Node a in call.Arguments)
            {
                Expr(a);
            }

            SetSpan(callSpanS, callSpanE);
            _f.CurP = CallPos(callee);
            bool tail = tailRequested && !call.IsOptional;
            Emit(direct ? (tail ? Op.TailCallEval : Op.CallEval) : tail ? Op.TailCall : Op.Call, call.Arguments.Count, direct ? EvalFlags() : textIdx);
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

    private void ArgsArray(IReadOnlyList<Node> args, int pos = -1)
    {
        Emit(Op.NewArray);
        foreach (Node a in args)
        {
            if (a is SpreadElement sp)
            {
                Expr(sp.Argument);
                SetSpan(sp.Argument);
                if (pos >= 0)
                {
                    _f.CurP = pos;
                }

                Emit(Op.ArraySpread, 1, TextConst(sp.Argument));
            }
            else
            {
                Expr(a);
                Emit(Op.ArrayPush);
            }
        }
    }

    /// <summary>Where a stack trace points for a call: the property name of a method call, else the callee.</summary>
    private int CallPos(Expression callee) => callee switch
    {
        MemberExpression { Computed: false } mm => mm.Property.Start,
        Identifier or MemberExpression or SuperExpression => callee.Start,
        _ => OperatorPos(callee.End),
    };

    private void NewExpr(NewExpression ne)
    {
        int textIdx = TextConst(ne.Callee);
        Expr(ne.Callee);
        if (HasSpread(ne.Arguments))
        {
            ArgsArray(ne.Arguments, ne.Start);
            SetSpan(ne.Callee);
            _f.CurP = ne.Start;
            Emit(Op.NewSpread, 0, textIdx);
        }
        else
        {
            foreach (Node a in ne.Arguments)
            {
                Expr(a);
            }

            SetSpan(ne.Callee);
            _f.CurP = ne.Start;
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
        bool tailRequested = _tailCall;
        _tailCall = false;
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
        _f.CurP = tt.Quasi.Start;
        Emit(tailRequested ? Op.TailCall : Op.Call, tt.Quasi.Expressions.Count + 1, TextConst(tt.Tag));
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
                SetSpan(sp.Argument);
                Emit(Op.ArraySpread, 0, TextConst(sp.Argument));
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
                            _f.ValS = prop.Start;
                            _f.ValE = prop.End;
                            Emit(Op.DefineField, NameConst(name));
                            _f.ValS = -1;
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
            Emit(Op.GetIterator, asyncGen ? 1 : 0, NameConst("\u0002"));
            Emit(Op.PushUndef);
            JumpLabel ret = NewLabel();
            JumpLabel end = NewLabel();
            _f.CurP = y.Argument!.Start;
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

        JumpLabel retL = NewLabel();
        JumpLabel cont = NewLabel();
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
