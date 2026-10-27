using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.ExecutorSpike;

/// <summary>Where a binding lives at run time.</summary>
internal readonly record struct Loc(bool InEnv, int Slot, bool Tdz, bool IsConst);

/// <summary>The register and environment layout of one function (or the program).</summary>
internal sealed class FuncLayout
{
    internal int NumRegs;
}

/// <summary>
/// The pass both executors share. It resolves every identifier reference to a binding, finds the bindings a
/// nested function captures (those live in heap environments; everything else lives in registers) and the
/// ones that can be read before initialisation (those get a temporal-dead-zone check). Spine-shaped
/// operator chains are walked with a loop, so a 100,000-term sum cannot overflow the host stack.
/// </summary>
internal sealed class Analysis
{
    private readonly HashSet<Binding> _captured = new(ReferenceEqualityComparer.Instance as IEqualityComparer<Binding>);
    private readonly HashSet<Binding> _needsTdz = new(ReferenceEqualityComparer.Instance as IEqualityComparer<Binding>);
    private readonly HashSet<Binding> _initializing = new(ReferenceEqualityComparer.Instance as IEqualityComparer<Binding>);
    private readonly Dictionary<Node, FuncLayout> _layouts = new(ReferenceEqualityComparer.Instance as IEqualityComparer<Node>);
    private readonly Dictionary<Binding, Loc> _locs = new(ReferenceEqualityComparer.Instance as IEqualityComparer<Binding>);
    private readonly Dictionary<Scope, int> _envSize = new(ReferenceEqualityComparer.Instance as IEqualityComparer<Scope>);
    private Scope _cur;
    private Scope _curFunc;

    internal Analysis(Syntax.Program program)
    {
        _cur = program.Scope;
        _curFunc = program.Scope;
        foreach (Node s in program.Body)
        {
            Visit(s);
        }

        _layouts[program] = BuildLayout(program.Scope, []);
    }

    internal FuncLayout LayoutOf(Node functionOrProgram) => _layouts[functionOrProgram];

    // ---- scope helpers --------------------------------------------------------------------

    internal static Binding? Resolve(Scope from, string name)
    {
        for (Scope? s = from; s is not null; s = s.Parent)
        {
            Binding? b = s.Lookup(name);
            if (b is not null)
            {
                return b;
            }
        }

        return null;
    }

    private static Scope FuncScopeOf(Scope s)
    {
        Scope cur = s;
        while (cur.Kind is not (ScopeKind.Function or ScopeKind.Global))
        {
            cur = cur.Parent ?? throw new SpikeUnsupportedException("scope without a function ancestor");
        }

        return cur;
    }

    internal bool HasEnv(Scope s) => _envSize.ContainsKey(s);

    internal int EnvSizeOf(Scope s) => _envSize[s];

    internal Loc LocOf(Binding b) =>
        _locs.TryGetValue(b, out Loc loc) ? loc : throw new SpikeUnsupportedException("binding without a location: " + b.Name);

    /// <summary>The number of environments between the code in <paramref name="from"/> and the scope holding <paramref name="target"/>.</summary>
    internal int Hops(Scope from, Scope target)
    {
        int hops = 0;
        for (Scope? s = from; s is not null && !ReferenceEquals(s, target); s = s.Parent)
        {
            if (_envSize.ContainsKey(s))
            {
                hops++;
            }
        }

        return hops;
    }

    // ---- layout ---------------------------------------------------------------------------

    internal void LayoutFunction(FunctionNode fn)
    {
        if (_layouts.ContainsKey(fn))
        {
            return;
        }

        var paramNames = new List<string>();
        foreach (Node p in fn.Params)
        {
            if (p is Identifier id)
            {
                paramNames.Add(id.Name);
            }
            else
            {
                throw new SpikeUnsupportedException("non-simple parameter");
            }
        }

        _layouts[fn] = BuildLayout(fn.Scope, paramNames);
    }

    private FuncLayout BuildLayout(Scope fnScope, List<string> paramNames)
    {
        var layout = new FuncLayout();
        int reg = 0;
        var paramBindings = new HashSet<Binding>(ReferenceEqualityComparer.Instance as IEqualityComparer<Binding>);
        var paramRegs = new Dictionary<Binding, int>(ReferenceEqualityComparer.Instance as IEqualityComparer<Binding>);
        foreach (string name in paramNames)
        {
            Binding b = fnScope.Lookup(name)!;
            if (paramBindings.Add(b))
            {
                paramRegs[b] = reg;
            }

            reg++;
        }

        // A duplicate parameter name still takes a register position so arguments line up.
        var stack = new Stack<Scope>();
        stack.Push(fnScope);
        while (stack.Count > 0)
        {
            Scope s = stack.Pop();
            int envSlots = 0;
            foreach (Binding b in s.Bindings)
            {
                bool captured = _captured.Contains(b);
                bool isConst = b.Kind == BindingKind.Const;
                bool lexical = b.IsLexical;
                bool tdz = lexical && (captured || _needsTdz.Contains(b)) && b.Kind != BindingKind.LexicalFunction;
                if (captured)
                {
                    _locs[b] = new Loc(true, envSlots++, tdz, isConst);
                }
                else if (paramRegs.TryGetValue(b, out int pr))
                {
                    _locs[b] = new Loc(false, pr, false, false);
                }
                else
                {
                    _locs[b] = new Loc(false, reg++, tdz, isConst);
                }
            }

            if (envSlots > 0)
            {
                _envSize[s] = envSlots;
            }

            foreach (Scope c in s.Children)
            {
                if (c.Kind is not (ScopeKind.Function or ScopeKind.Global))
                {
                    stack.Push(c);
                }
            }
        }

        layout.NumRegs = reg;
        return layout;
    }

    /// <summary>Parameter bindings that were captured: the prologue copies the argument register into the environment.</summary>
    internal IEnumerable<(int Reg, Loc Loc)> CapturedParams(FunctionNode fn)
    {
                for (int i = 0; i < fn.Params.Count; i++)
        {
            var id = (Identifier)fn.Params[i];
            Binding b = fn.Scope.Lookup(id.Name)!;
            Loc loc = _locs[b];
            if (loc.InEnv)
            {
                yield return (i, loc);
            }
        }
    }

    // ---- the walk -------------------------------------------------------------------------

    private void Reference(Identifier id)
    {
        Binding? b = Resolve(_cur, id.Name);
        if (b is null)
        {
            return;
        }

        if (!ReferenceEquals(FuncScopeOf(b.Scope), _curFunc))
        {
            _captured.Add(b);
        }

        if (b.IsLexical && (id.Start < b.Span.Start || _initializing.Contains(b)))
        {
            _needsTdz.Add(b);
        }
    }

    private void VisitScoped(Scope? scope, Action visit)
    {
        Scope saved = _cur;
        if (scope is not null)
        {
            _cur = scope;
        }

        visit();
        _cur = saved;
    }

    private void VisitFunction(FunctionNode fn)
    {
        if (fn.IsAsync || fn.IsGenerator)
        {
            throw new SpikeUnsupportedException("async/generator function in the spike executors");
        }

        Scope savedCur = _cur;
        Scope savedFunc = _curFunc;
        _cur = fn.Scope;
        _curFunc = fn.Scope;
        if (fn.Body is Expression e)
        {
            Visit(e);
        }
        else if (fn.Body is BlockStatement b)
        {
            foreach (Node s in b.Body)
            {
                Visit(s);
            }
        }

        _cur = savedCur;
        _curFunc = savedFunc;
    }

    private int _depth;

    private void Visit(Node? n)
    {
        if (++_depth > CompileUtil.MaxNesting || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw CompileUtil.TooNested();
        }

        VisitCore(n);
        _depth--;
    }

    private void VisitCore(Node? n)
    {
        switch (n)
        {
            case null:
            case NumberLiteral:
            case StringLiteral:
            case BooleanLiteral:
            case NullLiteral:
            case ThisExpression:
            case EmptyStatement:
            case BreakStatement:
            case ContinueStatement:
                return;
            case Identifier id:
                Reference(id);
                return;
            case BinaryExpression or LogicalExpression:
                {
                    var spine = new List<Expression>();
                    Node cur = n;
                    while (true)
                    {
                        if (cur is BinaryExpression be)
                        {
                            spine.Add(be.Right);
                            cur = be.Left;
                        }
                        else if (cur is LogicalExpression le)
                        {
                            spine.Add(le.Right);
                            cur = le.Left;
                        }
                        else
                        {
                            break;
                        }
                    }

                    Visit(cur);
                    for (int i = spine.Count - 1; i >= 0; i--)
                    {
                        Visit(spine[i]);
                    }

                    return;
                }

            case UnaryExpression u:
                Visit(u.Argument);
                return;
            case UpdateExpression up:
                Visit(up.Argument);
                return;
            case AssignmentExpression a:
                Visit(a.Left);
                Visit(a.Right);
                return;
            case ConditionalExpression c:
                Visit(c.Test);
                Visit(c.Consequent);
                Visit(c.Alternate);
                return;
            case CallExpression call:
                Visit(call.Callee);
                foreach (Node arg in call.Arguments)
                {
                    Visit(arg);
                }

                return;
            case NewExpression ne:
                Visit(ne.Callee);
                foreach (Node arg in ne.Arguments)
                {
                    Visit(arg);
                }

                return;
            case MemberExpression m:
                Visit(m.Object);
                if (m.Computed)
                {
                    Visit(m.Property);
                }

                return;
            case SequenceExpression seq:
                foreach (Expression e in seq.Expressions)
                {
                    Visit(e);
                }

                return;
            case ArrayLiteral arr:
                foreach (Node? e in arr.Elements)
                {
                    if (e is null or SpreadElement)
                    {
                        throw new SpikeUnsupportedException("array hole or spread");
                    }

                    Visit(e);
                }

                return;
            case ObjectLiteral obj:
                foreach (Node p in obj.Properties)
                {
                    if (p is not Property prop || prop.Computed || prop.PropertyKind is PropertyKind.Get or PropertyKind.Set)
                    {
                        throw new SpikeUnsupportedException("object literal member kind");
                    }

                    Visit(prop.Value);
                }

                return;
            case FunctionNode fn:
                VisitFunction(fn);
                return;
            case FunctionDeclaration fd:
                VisitFunction(fd.Function);
                return;
            case VariableDeclaration vd:
                foreach (VariableDeclarator d in vd.Declarations)
                {
                    if (d.Id is not Identifier did)
                    {
                        throw new SpikeUnsupportedException("destructuring declaration");
                    }

                    Binding? b = Resolve(_cur, did.Name);
                    bool lex = vd.DeclarationKind != VarKind.Var && b is not null;
                    if (lex)
                    {
                        _initializing.Add(b!);
                    }

                    Visit(d.Init);
                    if (lex)
                    {
                        _initializing.Remove(b!);
                    }
                }

                return;
            case ExpressionStatement es:
                Visit(es.Expression);
                return;
            case BlockStatement bs:
                VisitScoped(bs.Scope, () =>
                {
                    foreach (Node s in bs.Body)
                    {
                        Visit(s);
                    }
                });
                return;
            case IfStatement ifs:
                Visit(ifs.Test);
                Visit(ifs.Consequent);
                Visit(ifs.Alternate);
                return;
            case ForStatement fs:
                VisitScoped(fs.Scope, () =>
                {
                    Visit(fs.Init);
                    Visit(fs.Test);
                    Visit(fs.Update);
                    Visit(fs.Body);
                });
                return;
            case WhileStatement ws:
                Visit(ws.Test);
                Visit(ws.Body);
                return;
            case DoWhileStatement dw:
                Visit(dw.Body);
                Visit(dw.Test);
                return;
            case ReturnStatement rs:
                Visit(rs.Argument);
                return;
            case ThrowStatement ts:
                Visit(ts.Argument);
                return;
            case TryStatement tr:
                if (tr.Finalizer is not null)
                {
                    throw new SpikeUnsupportedException("finally");
                }

                Visit(tr.Block);
                if (tr.Handler is { } h)
                {
                    VisitScoped(h.Scope, () =>
                    {
                        foreach (Node s in h.Body.Body)
                        {
                            Visit(s);
                        }
                    });
                }

                return;
            default:
                throw new SpikeUnsupportedException($"{n.Kind} at {n.Start}");
        }
    }
}
