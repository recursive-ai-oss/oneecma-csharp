using RecursiveAI.OneEcma.Syntax;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma;

internal sealed partial class Compiler
{
    // ---- functions ----------------------------------------------------------------------------

    private void FunctionExpr(FunctionNode fn)
    {
        if (fn.Id is not null && !fn.IsArrow && fn.Flavor == FunctionFlavor.Normal)
        {
            // A named function expression sees its own name in an extra scope (15.2.5 InstantiateOrdinaryFunctionExpression).
            var info = new ScopeInfo { Names = [fn.Id.Name], Kinds = [ScopeInfo.KFuncName], Template = new JsValue[1] };
            _f.Scopes.Add(info);
            var cs = new CScope { Parent = _scope, Info = info, Slots = new Dictionary<string, int> { [fn.Id.Name] = 0 } };
            _scope = cs;
            Emit(Op.PushScope, _f.Scopes.Count - 1);
            CompileClosure(fn);
            Emit(Op.Dup);
            Emit(Op.InitSlot, 0, 0);
            _scope = cs.Parent;
            Emit(Op.PopScope);
            return;
        }

        CompileClosure(fn);
    }

    private void CompileClosure(FunctionNode fn)
    {
        string name = _nameHint ?? fn.Id?.Name ?? "";
        _nameHint = null;
        FunctionCode code = CompileFunctionBody(fn, name);
        _f.Funcs.Add(code);
        Emit(Op.MakeClosure, _f.Funcs.Count - 1);
    }

    private static int ExpectedArgs(IReadOnlyList<Node> ps)
    {
        int n = 0;
        foreach (Node p in ps)
        {
            if (p is AssignmentPattern or RestElement)
            {
                break;
            }

            n++;
        }

        return n;
    }

    private FunctionCode CompileFunctionBody(FunctionNode fn, string name, int srcStart = -1, int srcEnd = -1)
    {
        FnState outerF = _f;
        CScope? outerScope = _scope;
        var f = new FnState
        {
            Outer = outerF,
            Strict = fn.IsStrict,
            IsAsync = fn.IsAsync,
            IsGenerator = fn.IsGenerator,
            Kind = fn.IsArrow ? FunctionKind.Arrow : fn.Flavor switch
            {
                FunctionFlavor.Getter => FunctionKind.Getter,
                FunctionFlavor.Setter => FunctionKind.Setter,
                FunctionFlavor.Method => FunctionKind.Method,
                FunctionFlavor.BaseConstructor => FunctionKind.ClassConstructor,
                FunctionFlavor.DerivedConstructor => FunctionKind.DerivedConstructor,
                _ => FunctionKind.Normal,
            },
        };
        _f = f;
        SetSpan(fn.Start, fn.End);

        bool paramsTdz = fn.Scope.HasParameterExpressions;
        bool needsArguments = !fn.IsArrow && (fn.UsesArguments || fn.HasDirectEval);
        if (needsArguments)
        {
            Binding? existing = fn.Scope.Lookup("arguments");
            if (existing is not null && existing.Kind != BindingKind.Var && existing.Kind != BindingKind.AnnexBFunctionVar)
            {
                // A parameter, lexical declaration or function named "arguments" shadows the object (10.2.11 step 22).
                needsArguments = false;
            }
        }

        CScope? top = EnterScope(fn.Scope, true, needsArguments ? ["arguments"] : null, fn.HasDirectEval, paramsTdz);
        _f.FunctionTopScope = top;

        // Function parameters (10.2.11 FunctionDeclarationInstantiation).
        if (needsArguments)
        {
            bool mapped = !fn.IsStrict && fn.HasSimpleParameterList;
            int table = -1;
            if (mapped)
            {
                var map = new int[fn.Params.Count];
                var seen = new HashSet<string>();
                for (int i = fn.Params.Count - 1; i >= 0; i--)
                {
                    string pn = ((Identifier)fn.Params[i]).Name;
                    map[i] = seen.Add(pn) ? top!.Slots![pn] : -1;
                }

                table = AddTable(map);
            }

            Emit(Op.CreateArguments, table, mapped ? 1 : 0);
            Emit(Op.InitSlot, 0, top!.Slots!["arguments"]);
        }

        for (int i = 0; i < fn.Params.Count; i++)
        {
            Node p = fn.Params[i];
            switch (p)
            {
                case Identifier id:
                    Emit(Op.LoadArg, i);
                    InitIdent(id.Name);
                    break;
                case RestElement re:
                    Emit(Op.CollectRest, i);
                    DestructureText = "undefined";
                    BindTarget(re.Argument, BindMode.Init);
                    break;
                default:
                    Emit(Op.LoadArg, i);
                    DestructureText = "undefined";
                    BindTarget(p, BindMode.Init);
                    break;
            }
        }

        if (fn.Body is BlockStatement body)
        {
            HoistFunctions(body.Body);
            if (fn.IsGenerator)
            {
                Emit(Op.GenStart);
            }

            Statements(body.Body);
            Emit(Op.ReturnUndef);
        }
        else
        {
            if (fn.IsGenerator)
            {
                Emit(Op.GenStart);
            }

            ExprTail(fn.Body);
            Emit(Op.Return);
        }

        FunctionCode code = Finish(f, name, ExpectedArgs(fn.Params), srcStart >= 0 ? srcStart : fn.Start, srcEnd >= 0 ? srcEnd : fn.End);
        code.HasParamExpressions = paramsTdz;
        _f = outerF;
        _scope = outerScope;
        return code;
    }

    // ---- classes -------------------------------------------------------------------------------

    private void ClassExpr(ClassNode cn)
    {
        string className = _nameHint ?? cn.Id?.Name ?? "";
        _nameHint = null;
        var priv = new List<string>();
        foreach (string pn in cn.Scope.PrivateNames)
        {
            priv.Add("#" + pn.TrimStart('#'));
        }

        CScope? cs = EnterScope(cn.Scope, false, priv);
        var scopeCtl = new Control { Kind = Control.K.Scope };
        if (cs is not null)
        {
            _f.Controls.Add(scopeCtl);
        }

        foreach (string pn in priv)
        {
            Emit(Op.NewPrivateName, NameConst(pn));
            Ref r = Resolve(pn);
            Emit(Op.InitSlot, r.Hops, r.Slot);
        }

        bool hasHeritage = cn.SuperClass is not null;
        if (hasHeritage)
        {
            Expr(cn.SuperClass!);
        }

        MethodDefinition? ctorDef = null;
        foreach (Node m in cn.Members)
        {
            if (m is MethodDefinition md && md.MemberKind == ClassMemberKind.Constructor)
            {
                ctorDef = md;
            }
        }

        FunctionCode ctorCode;
        if (ctorDef is not null)
        {
            ctorCode = CompileFunctionBody(ctorDef.Value, className, cn.Start, cn.End);
        }
        else
        {
            ctorCode = SyntheticConstructor(className, hasHeritage, cn);
        }

        ctorCode.IsClassMember = true;
        _f.Funcs.Add(ctorCode);
        SetSpan(cn);
        Emit(Op.NewClass, _f.Funcs.Count - 1, hasHeritage ? 1 : 0);
        int tProto = NewTemp();
        int tCtor = NewTemp();
        Emit(Op.StoreLocal, tProto);
        Emit(Op.StoreLocal, tCtor);

        var staticTasks = new List<(bool IsBlock, int KeyTemp, int InitTemp, int Flags)>();
        foreach (Node m in cn.Members)
        {
            switch (m)
            {
                case MethodDefinition md when md.MemberKind != ClassMemberKind.Constructor:
                    {
                        int kind = md.MemberKind == ClassMemberKind.Get ? 2 : md.MemberKind == ClassMemberKind.Set ? 4 : 0;
                        if (md.Key is PrivateIdentifier pi)
                        {
                            Emit(Op.LoadLocal, tCtor);
                            LoadPrivateName(pi.Name);
                            _nameHint = (kind == 2 ? "get " : kind == 4 ? "set " : "") + "#" + pi.Name;
                            CompileClosure(md.Value);
                            Emit(Op.AddPrivateMethod, kind | (md.IsStatic ? 1 : 0));
                        }
                        else
                        {
                            Emit(Op.LoadLocal, md.IsStatic ? tCtor : tProto);
                            string? name = StaticKeyName(md.Key, md.Computed);
                            MethodCommon(md.Key, md.Computed, name, md.Value, kind);
                            Emit(Op.Pop);
                        }

                        break;
                    }

                case PropertyDefinition pd:
                    {
                        int flags = (pd.IsStatic ? 1 : 0) | (pd.Key is PrivateIdentifier ? 2 : 0) | (pd.Value is not null ? 4 : 0);
                        int tKey = pd.IsStatic ? NewTemp() : -1;
                        int tInit = pd.IsStatic ? NewTemp() : -1;
                        if (!pd.IsStatic)
                        {
                            Emit(Op.LoadLocal, tCtor);
                        }

                        string? fname = null;
                        if (pd.Key is PrivateIdentifier pk)
                        {
                            LoadPrivateName(pk.Name);
                            fname = "#" + pk.Name;
                        }
                        else
                        {
                            fname = StaticKeyName(pd.Key, pd.Computed);
                            if (fname is not null)
                            {
                                PushString(fname);
                            }
                            else
                            {
                                Expr(pd.Key);
                                Emit(Op.ToPropKey);
                            }
                        }

                        if (pd.IsStatic)
                        {
                            Emit(Op.StoreLocal, tKey);
                        }

                        if (pd.Value is not null)
                        {
                            FunctionCode init = CompileFieldInit(pd, fname);
                            _f.Funcs.Add(init);
                            Emit(Op.MakeClosure, _f.Funcs.Count - 1);
                        }
                        else
                        {
                            Emit(Op.PushUndef);
                        }

                        if (pd.IsStatic)
                        {
                            Emit(Op.StoreLocal, tInit);
                            staticTasks.Add((false, tKey, tInit, flags));
                        }
                        else
                        {
                            Emit(Op.DefineClassField, flags);
                        }

                        break;
                    }

                case StaticBlock sb:
                    {
                        FunctionCode blk = CompileStaticBlock(sb);
                        _f.Funcs.Add(blk);
                        Emit(Op.MakeClosure, _f.Funcs.Count - 1);
                        int tFn = NewTemp();
                        Emit(Op.StoreLocal, tFn);
                        staticTasks.Add((true, -1, tFn, 0));
                        break;
                    }
            }
        }

        if (cn.Id is not null)
        {
            Emit(Op.LoadLocal, tCtor);
            InitIdent(cn.Id.Name);
        }

        foreach ((bool isBlock, int keyTemp, int initTemp, int flags) in staticTasks)
        {
            Emit(Op.LoadLocal, tCtor);
            if (isBlock)
            {
                Emit(Op.LoadLocal, initTemp);
                Emit(Op.RunStaticBlock);
            }
            else
            {
                Emit(Op.LoadLocal, keyTemp);
                Emit(Op.LoadLocal, initTemp);
                Emit(Op.DefineClassField, flags);
                FreeTemp(keyTemp);
            }

            FreeTemp(initTemp);
        }

        Emit(Op.LoadLocal, tCtor);
        FreeTemp(tProto);
        FreeTemp(tCtor);
        if (cs is not null)
        {
            PopControl(scopeCtl);
        }

        LeaveScope(cs);
    }

    private FunctionCode SyntheticConstructor(string name, bool derived, ClassNode cn)
    {
        FnState outerF = _f;
        var f = new FnState { Outer = outerF, Strict = true, Kind = derived ? FunctionKind.DerivedConstructor : FunctionKind.ClassConstructor };
        _f = f;
        SetSpan(cn.Start, cn.End);
        if (derived)
        {
            Emit(Op.SuperCall, -1);
            Emit(Op.Pop);
        }

        Emit(Op.ReturnUndef);
        FunctionCode code = Finish(f, name, 0, cn.Start, cn.End);
        _f = outerF;
        return code;
    }

    private FunctionCode CompileFieldInit(PropertyDefinition pd, string? name)
    {
        FnState outerF = _f;
        CScope? outerScope = _scope;
        var f = new FnState { Outer = outerF, Strict = true, Kind = FunctionKind.FieldInit, InClassFieldInit = true };
        _f = f;
        SetSpan(pd.Value!);
        CScope? cs = EnterScope(pd.Scope, true, null, pd.Scope?.ContainsDirectEval ?? false);
        if (name is not null)
        {
            ExprNamed(pd.Value!, name);
        }
        else
        {
            Expr(pd.Value!);
        }

        Emit(Op.Return);
        FunctionCode code = Finish(f, name ?? "", 0, pd.Value!.Start, pd.Value.End);
        _f = outerF;
        _scope = outerScope;
        _ = cs;
        return code;
    }

    private FunctionCode CompileStaticBlock(StaticBlock sb)
    {
        FnState outerF = _f;
        CScope? outerScope = _scope;
        var f = new FnState { Outer = outerF, Strict = true, Kind = FunctionKind.StaticBlock };
        _f = f;
        SetSpan(sb);
        EnterScope(sb.Scope, true, null, sb.Scope.ContainsDirectEval);
        var body = sb.Body;
        HoistFunctions(body);
        Statements(body);
        Emit(Op.ReturnUndef);
        FunctionCode code = Finish(f, "", 0, sb.Start, sb.End);
        _f = outerF;
        _scope = outerScope;
        return code;
    }

    // ---- scripts, eval, modules -----------------------------------------------------------------

    private static GlobalDeclsView CollectDecls(Scope scope)
    {
        var d = new GlobalDeclsView();
        foreach (Binding b in scope.Bindings)
        {
            switch (b.Kind)
            {
                case BindingKind.Var:
                    d.Vars.Add(b.Name);
                    break;
                case BindingKind.Function:
                    d.Functions.Add(b.Name);
                    break;
                case BindingKind.AnnexBFunctionVar:
                    d.AnnexB.Add(b.Name);
                    break;
                case BindingKind.Let:
                case BindingKind.Class:
                    d.Lexicals.Add((b.Name, false));
                    break;
                case BindingKind.Const:
                    d.Lexicals.Add((b.Name, true));
                    break;
            }
        }

        return d;
    }

    private static IEnumerable<FunctionDeclaration> TopLevelFunctions(IEnumerable<Node> body)
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
                yield return fd;
            }
        }
    }

    private FunctionCode RunScript(Syntax.Program program)
    {
        var f = new FnState { Strict = program.IsStrict, Kind = FunctionKind.Script, CompletionTemp = 0, NumLocals = 1 };
        _f = f;
        _scope = null;
        SetSpan(program.Start, Math.Max(program.Start, Math.Min(program.End, program.Start)));
        Emit(Op.PushUndef);
        Emit(Op.StoreLocal, 0);
        GlobalDeclsView d = CollectDecls(program.Scope);
        Emit(Op.DeclareGlobals, AddTable(d));
        foreach (FunctionDeclaration fd in TopLevelFunctions(program.Body))
        {
            CompileClosure(fd.Function);
            Emit(Op.DefineGlobalFunction, NameConst(fd.Function.Id!.Name));
        }

        Statements(program.Body);
        Emit(Op.LoadLocal, 0);
        Emit(Op.Return);
        FunctionCode code = Finish(f, "", 0, program.Start, program.End);
        return code;
    }

    private FunctionCode RunEval(Syntax.Program program, bool direct)
    {
        var f = new FnState { Strict = program.IsStrict, Kind = FunctionKind.Eval, CompletionTemp = 0, NumLocals = 1 };
        _f = f;
        _scope = null;
        _unknownParent = true;
        SetSpan(program.Start, program.Start);
        Emit(Op.PushUndef);
        Emit(Op.StoreLocal, 0);
        GlobalDeclsView d = CollectDecls(program.Scope);
        CScope? cs;
        if (program.IsStrict)
        {
            cs = EnterScope(program.Scope, true, null, true);
        }
        else
        {
            // Sloppy eval: var and function declarations go to the caller's variable environment (19.2.1.3 EvalDeclarationInstantiation).
            cs = EnterEvalLexicalScope(program.Scope);
            Emit(Op.DeclareEvalVar, AddTable(d));
        }

        foreach (FunctionDeclaration fd in TopLevelFunctions(program.Body))
        {
            CompileClosure(fd.Function);
            if (program.IsStrict)
            {
                InitIdent(fd.Function.Id!.Name);
            }
            else
            {
                Emit(Op.StoreDyn, NameConst(fd.Function.Id!.Name), 0);
            }
        }

        Statements(program.Body);
        Emit(Op.LoadLocal, 0);
        Emit(Op.Return);
        _ = cs;
        return Finish(f, "", 0, program.Start, program.End);
    }

    /// <summary>The environment for the lexical declarations of sloppy eval code.</summary>
    private CScope? EnterEvalLexicalScope(Scope scope)
    {
        var names = new List<string>();
        var kinds = new List<byte>();
        foreach (Binding b in scope.Bindings)
        {
            if (b.Kind is BindingKind.Let or BindingKind.Const or BindingKind.Class)
            {
                names.Add(b.Name);
                kinds.Add(b.Kind == BindingKind.Const ? ScopeInfo.KConst : ScopeInfo.KLet);
            }
        }

        if (names.Count == 0)
        {
            return null;
        }

        var info = new ScopeInfo { Names = [.. names], Kinds = [.. kinds], Template = new JsValue[names.Count] };
        for (int i = 0; i < names.Count; i++)
        {
            info.Template[i] = JsValue.Tdz;
        }

        var cs = new CScope { Parent = null, Info = info, Slots = new Dictionary<string, int>() };
        for (int i = 0; i < names.Count; i++)
        {
            cs.Slots[names[i]] = i;
        }

        _f.Scopes.Add(info);
        _scope = cs;
        Emit(Op.PushScope, _f.Scopes.Count - 1);
        return cs;
    }

    private FunctionCode RunModule(Syntax.Program program, string moduleId)
    {
        var f = new FnState { Strict = true, Kind = FunctionKind.Module };
        _f = f;
        _scope = null;
        SetSpan(program.Start, program.Start);
        var extra = new List<string>();
        foreach (Node st in program.Body)
        {
            if (st is ExportDefaultDeclaration)
            {
                extra.Add("*default*");
                break;
            }
        }

        CScope? top = EnterScope(program.Scope, true, extra, true);
        HoistFunctions(ModuleDeclarations(program.Body));
        Emit(Op.GenStart);
        foreach (Node st in program.Body)
        {
            ModuleItem(st);
        }

        Emit(Op.ReturnUndef);
        f.IsAsync = f.HasAwait;
        FunctionCode code = Finish(f, "", 0, program.Start, program.End);
        code.ModuleId = moduleId;
        code.FuncScope = top!.Info;
        return code;
    }

    private static IEnumerable<Node> ModuleDeclarations(IEnumerable<Node> body)
    {
        foreach (Node st in body)
        {
            switch (st)
            {
                case ExportNamedDeclaration { Declaration: FunctionDeclaration fd }:
                    yield return fd;
                    break;
                case ExportDefaultDeclaration { Declaration: FunctionDeclaration fd2 }:
                    yield return fd2;
                    break;
                default:
                    yield return st;
                    break;
            }
        }
    }

    private void ModuleItem(Node st)
    {
        switch (st)
        {
            case ImportDeclaration:
            case ExportAllDeclaration:
                break;
            case ExportNamedDeclaration en:
                if (en.Declaration is not null && en.Declaration is not FunctionDeclaration)
                {
                    Stmt(en.Declaration);
                }

                break;
            case ExportDefaultDeclaration ed:
                switch (ed.Declaration)
                {
                    case FunctionDeclaration:
                        break;
                    case ClassDeclaration cd:
                        ClassExpr(cd.Class);
                        InitIdent(cd.Class.Id?.Name ?? "*default*");
                        if (cd.Class.Id is null)
                        {
                            // anonymous default class: bound as "default"
                        }

                        break;
                    default:
                        ExprNamed(ed.Declaration, "default");
                        InitIdent("*default*");
                        break;
                }

                break;
            default:
                Stmt(st);
                break;
        }
    }
}

/// <summary>The global declarations of a script or eval body, as the declaration-instantiation ops consume them.</summary>
internal sealed class GlobalDeclsView
{
    internal List<string> Vars = [];
    internal List<string> AnnexB = [];
    internal List<string> Functions = [];
    internal List<(string Name, bool IsConst)> Lexicals = [];
}
