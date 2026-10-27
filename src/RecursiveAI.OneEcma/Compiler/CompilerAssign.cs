using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma;

internal sealed partial class Compiler
{
    private void Assign(AssignmentExpression a, bool discard)
    {
        Node left = a.Left;
        bool logical = a.Operator is AssignOp.AndAssign or AssignOp.OrAssign or AssignOp.CoalesceAssign;
        BinaryOp? compound = CompoundOp(a.Operator);
        switch (left)
        {
            case Identifier id:
                if (a.Operator == AssignOp.Assign)
                {
                    ExprNamed(a.Right, id.Name);
                }
                else if (logical)
                {
                    LoadIdent(id.Name);
                    JumpLabel end = NewLabel();
                    Jump(LogicalJump(a.Operator), end);
                    ExprNamed(a.Right, id.Name);
                    if (!discard)
                    {
                        Emit(Op.Dup);
                    }

                    StoreIdent(id.Name);
                    if (discard)
                    {
                        Jump(Op.Jmp, end);
                        Emit(Op.PushUndef);
                    }

                    Mark(end);
                    if (discard)
                    {
                        Emit(Op.Pop);
                    }

                    return;
                }
                else
                {
                    LoadIdent(id.Name);
                    Expr(a.Right);
                    SetSpan(a);
                    Emit(BinOp(compound!.Value));
                }

                if (!discard)
                {
                    Emit(Op.Dup);
                }

                StoreIdent(id.Name);
                return;

            case MemberExpression { Object: SuperExpression } sm:
                SuperAssign(a, sm, discard, logical, compound);
                return;

            case MemberExpression m:
                MemberAssign(a, m, discard, logical, compound);
                return;

            case CallExpression:
                Expr(left);
                Emit(Op.ThrowError, 1, NameConst("Invalid left-hand side in assignment"));
                if (discard)
                {
                    Emit(Op.Pop);
                }

                return;

            default:
                Expr(a.Right);
                if (!discard)
                {
                    Emit(Op.Dup);
                }

                DestructureText = SourceSlice(a.Right);
                BindTarget(left, BindMode.Assign);
                return;
        }
    }

    private static Op LogicalJump(AssignOp op) => op switch
    {
        AssignOp.AndAssign => Op.JmpIfFalseKeep,
        AssignOp.OrAssign => Op.JmpIfTrueKeep,
        _ => Op.JmpIfNotNullishKeep,
    };

    private void SuperAssign(AssignmentExpression a, MemberExpression sm, bool discard, bool logical, BinaryOp? compound)
    {
        bool computed = sm.Computed;
        string? name = computed ? null : ((Identifier)sm.Property).Name;
        if (computed)
        {
            Expr(sm.Property);
            Emit(Op.ToPropKey);
        }

        if (a.Operator == AssignOp.Assign)
        {
            Expr(a.Right);
        }
        else
        {
            if (computed)
            {
                Emit(Op.Dup);
                Emit(Op.GetSuperElem);
            }
            else
            {
                Emit(Op.GetSuper, NameConst(name!));
            }

            if (logical)
            {
                JumpLabel end = NewLabel();
                Jump(LogicalJump(a.Operator), end);
                Expr(a.Right);
                SuperStore(computed, name);
                Mark(end);
                if (computed)
                {
                    // [key, val] on the short-circuit path: drop the key.
                    JumpLabel done = NewLabel();
                    Jump(Op.Jmp, done);
                    Mark(done);
                }

                if (discard)
                {
                    Emit(Op.Pop);
                }

                return;
            }

            Expr(a.Right);
            Emit(BinOp(compound!.Value));
        }

        SuperStore(computed, name);
        if (discard)
        {
            Emit(Op.Pop);
        }
    }

    private void SuperStore(bool computed, string? name)
    {
        if (computed)
        {
            Emit(Op.SetSuperElem);
        }
        else
        {
            Emit(Op.SetSuper, NameConst(name!));
        }
    }

    private void MemberAssign(AssignmentExpression a, MemberExpression m, bool discard, bool logical, BinaryOp? compound)
    {
        bool isPrivate = m.Property is PrivateIdentifier;
        string? name = !m.Computed && !isPrivate ? ((Identifier)m.Property).Name : null;
        Expr(m.Object);
        if (isPrivate)
        {
            LoadPrivateName(((PrivateIdentifier)m.Property).Name);
        }
        else if (m.Computed)
        {
            Expr(m.Property);
            if (a.Operator != AssignOp.Assign)
            {
                Emit(Op.ToPropKey);
            }
        }

        int keyItems = isPrivate || m.Computed ? 1 : 0;
        SetSpan(m.Object);
        if (a.Operator == AssignOp.Assign)
        {
            if (name is not null)
            {
                ExprNamed(a.Right, string.Empty);
            }
            else
            {
                Expr(a.Right);
            }

            StoreMember(m, name, isPrivate);
        }
        else
        {
            Emit(keyItems == 1 ? Op.Dup2 : Op.Dup);
            LoadMember(m, name, isPrivate);
            if (logical)
            {
                JumpLabel shortL = NewLabel();
                JumpLabel end = NewLabel();
                Jump(LogicalJump(a.Operator), shortL);
                Expr(a.Right);
                SetSpan(m.Object);
                StoreMember(m, name, isPrivate);
                Jump(Op.Jmp, end);
                // Short-circuit: the stack holds [obj, (key), val]; keep only val.
                _f.Depth += 1;
                Mark(shortL);
                if (keyItems == 1)
                {
                    Emit(Op.Rot3);
                    Emit(Op.Pop);
                    Emit(Op.Pop);
                }
                else
                {
                    Emit(Op.Swap);
                    Emit(Op.Pop);
                }

                Mark(end);
            }
            else
            {
                Expr(a.Right);
                SetSpan(a);
                Emit(BinOp(compound!.Value));
                SetSpan(m.Object);
                StoreMember(m, name, isPrivate);
            }
        }

        if (discard)
        {
            Emit(Op.Pop);
        }
    }

    private void LoadMember(MemberExpression m, string? name, bool isPrivate)
    {
        if (isPrivate)
        {
            Emit(Op.PrivateGet);
        }
        else if (m.Computed)
        {
            Emit(Op.GetElem);
        }
        else
        {
            Emit(Op.GetProp, NameConst(name!), NewCache());
        }
    }

    private void StoreMember(MemberExpression m, string? name, bool isPrivate)
    {
        if (isPrivate)
        {
            Emit(Op.PrivateSet);
        }
        else if (m.Computed)
        {
            Emit(Op.SetElem);
        }
        else
        {
            Emit(Op.SetProp, NameConst(name!), NewCache());
        }
    }

    private void Update(UpdateExpression u, bool discard)
    {
        Op op = u.IsIncrement ? Op.Inc : Op.Dec;
        bool keepOld = !u.IsPrefix && !discard;
        switch (u.Argument)
        {
            case Identifier id:
                LoadIdent(id.Name);
                Emit(Op.ToNumeric);
                if (keepOld)
                {
                    Emit(Op.Dup);
                }

                Emit(op);
                if (u.IsPrefix && !discard)
                {
                    Emit(Op.Dup);
                }

                StoreIdent(id.Name);
                return;
            case MemberExpression { Object: SuperExpression } sm:
                {
                    bool computed = sm.Computed;
                    string? name = computed ? null : ((Identifier)sm.Property).Name;
                    if (computed)
                    {
                        Expr(sm.Property);
                        Emit(Op.ToPropKey);
                        Emit(Op.Dup);
                        Emit(Op.GetSuperElem);
                    }
                    else
                    {
                        Emit(Op.GetSuper, NameConst(name!));
                    }

                    Emit(Op.ToNumeric);
                    int t = NewTemp();
                    if (keepOld)
                    {
                        Emit(Op.Dup);
                        Emit(Op.StoreLocal, t);
                    }

                    Emit(op);
                    SuperStore(computed, name);
                    if (keepOld)
                    {
                        Emit(Op.Pop);
                        Emit(Op.LoadLocal, t);
                    }
                    else if (discard)
                    {
                        Emit(Op.Pop);
                    }

                    FreeTemp(t);
                    return;
                }

            case MemberExpression m:
                {
                    bool isPrivate = m.Property is PrivateIdentifier;
                    string? name = !m.Computed && !isPrivate ? ((Identifier)m.Property).Name : null;
                    Expr(m.Object);
                    if (isPrivate)
                    {
                        LoadPrivateName(((PrivateIdentifier)m.Property).Name);
                    }
                    else if (m.Computed)
                    {
                        Expr(m.Property);
                        Emit(Op.ToPropKey);
                    }

                    SetSpan(m.Object);
                    Emit(isPrivate || m.Computed ? Op.Dup2 : Op.Dup);
                    LoadMember(m, name, isPrivate);
                    Emit(Op.ToNumeric);
                    int t = NewTemp();
                    if (keepOld)
                    {
                        Emit(Op.Dup);
                        Emit(Op.StoreLocal, t);
                    }

                    Emit(op);
                    StoreMember(m, name, isPrivate);
                    if (keepOld)
                    {
                        Emit(Op.Pop);
                        Emit(Op.LoadLocal, t);
                    }
                    else if (discard)
                    {
                        Emit(Op.Pop);
                    }

                    FreeTemp(t);
                    return;
                }

            default:
                Expr(u.Argument);
                Emit(Op.ThrowError, 1, NameConst("Invalid left-hand side expression in " + (u.IsPrefix ? "prefix" : "postfix") + " operation"));
                if (discard)
                {
                    Emit(Op.Pop);
                }

                return;
        }
    }

    // ---- destructuring and binding --------------------------------------------------------------

    /// <summary>Binds or assigns the value on top of the stack to <paramref name="t"/> (consuming it).</summary>
    private void BindTarget(Node t, BindMode mode)
    {
        switch (t)
        {
            case Identifier id:
                if (mode == BindMode.Init)
                {
                    InitIdent(id.Name);
                }
                else
                {
                    StoreIdent(id.Name);
                }

                break;
            case MemberExpression { Object: SuperExpression } sm:
                if (sm.Computed)
                {
                    int tv = NewTemp();
                    Emit(Op.StoreLocal, tv);
                    Expr(sm.Property);
                    Emit(Op.LoadLocal, tv);
                    Emit(Op.SetSuperElem);
                    FreeTemp(tv);
                }
                else
                {
                    Emit(Op.SetSuper, NameConst(((Identifier)sm.Property).Name));
                }

                Emit(Op.Pop);
                break;
            case MemberExpression m:
                {
                    int tv = NewTemp();
                    Emit(Op.StoreLocal, tv);
                    Expr(m.Object);
                    if (m.Property is PrivateIdentifier pi)
                    {
                        LoadPrivateName(pi.Name);
                        Emit(Op.LoadLocal, tv);
                        Emit(Op.PrivateSet);
                    }
                    else if (m.Computed)
                    {
                        Expr(m.Property);
                        Emit(Op.LoadLocal, tv);
                        Emit(Op.SetElem);
                    }
                    else
                    {
                        Emit(Op.LoadLocal, tv);
                        Emit(Op.SetProp, NameConst(((Identifier)m.Property).Name), NewCache());
                    }

                    Emit(Op.Pop);
                    FreeTemp(tv);
                    break;
                }

            case AssignmentPattern ap:
                {
                    JumpLabel have = NewLabel();
                    Jump(Op.JmpIfNotUndefKeep, have);
                    if (ap.Left is Identifier lid)
                    {
                        ExprNamed(ap.Right, lid.Name);
                    }
                    else
                    {
                        Expr(ap.Right);
                    }

                    Mark(have);
                    BindTarget(ap.Left, mode);
                    break;
                }

            case ArrayPattern arr:
                ArrayPatternBind(arr, mode);
                break;
            case ObjectPattern obj:
                ObjectPatternBind(obj, mode);
                break;
            case CallExpression:
                Emit(Op.Pop);
                Emit(Op.ThrowError, 1, NameConst("Invalid left-hand side in assignment"));
                break;
            default:
                throw new NotSupportedException("binding target " + t.Kind);
        }
    }

    private void ArrayPatternBind(ArrayPattern arr, BindMode mode)
    {
        Emit(Op.GetIterator, 0, NameConst(DestructureText));
        int done = NewTemp();
        Emit(Op.PushFalse);
        Emit(Op.StoreLocal, done);
        JumpLabel handler = NewLabel();
        JumpLabel end = NewLabel();
        Jump(Op.TryEnter, handler);
        int depth = _f.Depth;
        var tc = new Control { Kind = Control.K.Try };
        _f.Controls.Add(tc);
        foreach (Node? el in arr.Elements)
        {
            if (el is null)
            {
                Emit(Op.IterStep, done);
                Emit(Op.Pop);
            }
            else if (el is RestElement re)
            {
                Emit(Op.IterRest, done);
                BindTarget(re.Argument, mode);
            }
            else
            {
                Emit(Op.IterStep, done);
                BindTarget(el, mode);
            }
        }

        PopControl(tc);
        Emit(Op.TryLeave);
        Emit(Op.IterCloseIfNotDone, done);
        Emit(Op.Pop);
        Emit(Op.Pop);
        Jump(Op.Jmp, end);
        _f.Depth = depth + 1;
        Mark(handler);
        Emit(Op.IterCloseSilent, done);
        Emit(Op.Throw);
        _f.Depth = depth - 2;
        Mark(end);
        FreeTemp(done);
    }

    private void ObjectPatternBind(ObjectPattern obj, BindMode mode)
    {
        string? first = null;
        bool hasRest = false;
        foreach (Node p in obj.Properties)
        {
            if (p is RestElement)
            {
                hasRest = true;
            }
            else if (first is null && p is Property pr)
            {
                first = StaticKeyName(pr.Key, pr.Computed);
            }
        }

        Emit(Op.CheckObjectCoercibleDestructure, first is null ? -1 : NameConst(first), NameConst(DestructureText));
        int keys = -1;
        if (hasRest)
        {
            keys = NewTemp();
            Emit(Op.NewArray);
            Emit(Op.StoreLocal, keys);
        }

        foreach (Node p in obj.Properties)
        {
            if (p is RestElement re)
            {
                Emit(Op.LoadLocal, keys);
                Emit(Op.ObjectRest);
                BindTarget(re.Argument, mode);
                FreeTemp(keys);
                return;
            }

            var prop = (Property)p;
            string? name = StaticKeyName(prop.Key, prop.Computed);
            Emit(Op.Dup);
            if (name is not null)
            {
                if (hasRest)
                {
                    Emit(Op.LoadLocal, keys);
                    PushString(name);
                    Emit(Op.ArrayPush);
                    Emit(Op.Pop);
                }

                Emit(Op.GetProp, NameConst(name), NewCache());
            }
            else
            {
                Expr(prop.Key);
                Emit(Op.ToPropKey);
                if (hasRest)
                {
                    Emit(Op.Dup);
                    Emit(Op.LoadLocal, keys);
                    Emit(Op.Swap);
                    Emit(Op.ArrayPush);
                    Emit(Op.Pop);
                }

                Emit(Op.GetElem);
            }

            BindTarget(prop.Value, mode);
        }

        Emit(Op.Pop);
    }
}
