using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma;

internal static partial class Vm
{
    // ---- resume ---------------------------------------------------------------------------------

    private static bool ResumeSetup(Frame f, ref int sp, ref int pc, out JsValue thrown)
    {
        thrown = default;
        FrameState s = f.State;
        f.State = FrameState.Running;
        JsValue sent = f.Sent;
        JsValue[] st = f.Stack;
        Instr[] code = f.Code.Ops;
        switch (s)
        {
            case FrameState.SuspendedStart:
                return true;
            case FrameState.Awaiting:
                if (code[pc - 1].Op == Op.YieldStar)
                {
                    pc--;
                    f.Mode = f.Mode == ResumeMode.Throw ? ResumeMode.Throw : ResumeMode.Next;
                    st[sp - 1] = sent;
                    f.AwaitedInStar = true;
                    return true;
                }

                if (f.Mode == ResumeMode.Throw)
                {
                    thrown = sent;
                    return false;
                }

                st[sp++] = sent;
                return true;
            default:
                {
                    Instr op = code[pc - 1];
                    if (op.Op == Op.YieldStar)
                    {
                        pc--;
                        st[sp - 1] = sent;
                        return true;
                    }

                    switch (f.Mode)
                    {
                        case ResumeMode.Throw:
                            thrown = sent;
                            return false;
                        case ResumeMode.Return:
                            st[sp++] = sent;
                            pc = op.A;
                            return true;
                        default:
                            st[sp++] = sent;
                            return true;
                    }
                }
        }
    }

    // ---- globals ----------------------------------------------------------------------------------

    private static JsValue LoadGlobal(Frame f, string name, PropCache c)
    {
        Realm r = f.Realm;
        if (c.Cell is { } cell)
        {
            if (cell.Value.IsTdz)
            {
                throw Errors.Reference("Cannot access '" + name + "' before initialization");
            }

            return cell.Value;
        }

        if (r.GlobalLexicals.Count > 0 && r.GlobalLexicals.TryGetValue(name, out Cell? lc))
        {
            c.Cell = lc;
            if (lc.Value.IsTdz)
            {
                throw Errors.Reference("Cannot access '" + name + "' before initialization");
            }

            return lc.Value;
        }

        JsObject g = r.Global;
        var key = new PropertyKey(name);
        int slot = g.Shape.Lookup(key, out PropFlags fl);
        if (slot >= 0 && (fl & PropFlags.Accessor) == 0)
        {
            return g.Slots[slot];
        }

        if (g.HasProperty(key))
        {
            return g.Get(key);
        }

        throw Errors.Reference(name + " is not defined");
    }

    private static void StoreGlobal(Frame f, string name, JsValue v, bool strict)
    {
        Realm r = f.Realm;
        if (r.GlobalLexicals.TryGetValue(name, out Cell? lc))
        {
            if (lc.Value.IsTdz)
            {
                throw Errors.Reference("Cannot access '" + name + "' before initialization");
            }

            if (lc.IsConst)
            {
                throw Errors.Type("Assignment to constant variable.");
            }

            lc.Value = v;
            return;
        }

        JsObject g = r.Global;
        var key = new PropertyKey(name);
        if (strict && !g.HasProperty(key))
        {
            throw Errors.Reference(name + " is not defined");
        }

        if (!g.Set(key, v, JsValue.FromObject(g)) && strict)
        {
            throw Errors.Type(Ops.SetFailureMessage(g, key));
        }
    }

    private static string TypeofGlobal(Frame f, string name)
    {
        Realm r = f.Realm;
        if (r.GlobalLexicals.TryGetValue(name, out Cell? lc))
        {
            if (lc.Value.IsTdz)
            {
                throw Errors.Reference("Cannot access '" + name + "' before initialization");
            }

            return lc.Value.TypeOf;
        }

        var key = new PropertyKey(name);
        return r.Global.HasProperty(key) ? r.Global.Get(key).TypeOf : "undefined";
    }

    /// <summary>GlobalDeclarationInstantiation (16.1.7).</summary>
    private static void DeclareGlobals(Realm r, GlobalDeclsView d)
    {
        JsObject g = r.Global;
        foreach ((string name, bool _) in d.Lexicals)
        {
            if (r.GlobalLexicals.ContainsKey(name))
            {
                throw Errors.Syntax("Identifier '" + name + "' has already been declared");
            }

            if (g.GetOwnProperty(name, out PropertyDescriptor pd) && !pd.Configurable)
            {
                throw Errors.Syntax("Identifier '" + name + "' has already been declared");
            }
        }

        foreach (string name in d.Vars.Concat(d.Functions))
        {
            if (r.GlobalLexicals.ContainsKey(name))
            {
                throw Errors.Syntax("Identifier '" + name + "' has already been declared");
            }
        }

        foreach (string name in d.Functions)
        {
            if (g.GetOwnProperty(name, out PropertyDescriptor pd))
            {
                if (!pd.Configurable && !(pd.IsData && pd.Writable && pd.Enumerable))
                {
                    throw Errors.Type("Cannot redefine property: " + name);
                }
            }
            else if (!g.IsExtensibleOp())
            {
                throw Errors.Type("Cannot define global function '" + name + "': object is not extensible");
            }
        }

        foreach (string name in d.Vars)
        {
            if (!g.HasOwnProperty(name) && !g.IsExtensibleOp())
            {
                throw Errors.Type("Cannot define global variable '" + name + "': object is not extensible");
            }
        }

        foreach (string name in d.Functions.Concat(d.AnnexB.Where(n => !r.GlobalLexicals.ContainsKey(n) && (g.HasOwnProperty(n) || g.IsExtensibleOp()))))
        {
            if (!g.HasOwnProperty(name))
            {
                g.DefinePropertyOrThrow(name, PropertyDescriptor.Data(default, PropFlags.Writable | PropFlags.Enumerable));
            }
        }

        foreach (string name in d.Vars)
        {
            if (!g.HasOwnProperty(name))
            {
                g.DefinePropertyOrThrow(name, PropertyDescriptor.Data(default, PropFlags.Writable | PropFlags.Enumerable));
            }
        }

        foreach ((string name, bool isConst) in d.Lexicals)
        {
            r.GlobalLexicals[name] = new Cell { Value = JsValue.Tdz, IsConst = isConst };
        }
    }

    private static void DefineGlobalFunction(Realm r, string name, JsValue fn)
    {
        JsObject g = r.Global;
        PropertyDescriptor d = g.GetOwnProperty(name, out PropertyDescriptor ex) && !ex.Configurable
            ? new PropertyDescriptor { Value = fn, HasValue = true }
            : PropertyDescriptor.Data(fn, PropFlags.Writable | PropFlags.Enumerable);
        g.DefinePropertyOrThrow(name, d);
        g.SetOrThrow(name, fn, true);
    }

    // ---- dynamic (by-name) lookup: with, direct eval, eval code -----------------------------------

    private enum DynKind
    {
        None,
        Slot,
        Cell,
        With,
        GlobalLex,
        GlobalObj,
    }

    private struct DynRef
    {
        internal DynKind Kind;
        internal Env? Env;
        internal int Slot;
        internal Cell? Cell;
        internal JsObject? Obj;
    }

    private static bool BlockedByUnscopables(JsObject o, PropertyKey key)
    {
        JsValue u = o.Get(new PropertyKey(WellKnown.Unscopables));
        return u.Ref is JsObject uo && Ops.ToBoolean(uo.Get(key));
    }

    private static DynRef ResolveDynamic(Frame f, string name, bool fromVarEnv = false)
    {
        var key = new PropertyKey(name);
        for (Env? e = f.Env; e is not null; e = e.Parent)
        {
            if (fromVarEnv && e.Info is not { IsFunctionVarScope: true })
            {
                continue;
            }

            fromVarEnv = false;
            if (e.WithObject is { } w)
            {
                if (w.HasProperty(key) && !BlockedByUnscopables(w, key))
                {
                    return new DynRef { Kind = DynKind.With, Obj = w };
                }

                continue;
            }

            if (e.Info is not null)
            {
                int i = e.Info.IndexOf(name);
                if (i >= 0)
                {
                    return new DynRef { Kind = DynKind.Slot, Env = e, Slot = i };
                }
            }

            if (e.Extra is not null && e.Extra.TryGetValue(name, out Cell? c))
            {
                return new DynRef { Kind = DynKind.Cell, Cell = c };
            }
        }

        Realm r = f.Realm;
        if (r.GlobalLexicals.TryGetValue(name, out Cell? lc))
        {
            return new DynRef { Kind = DynKind.GlobalLex, Cell = lc };
        }

        if (r.Global.HasProperty(key))
        {
            return new DynRef { Kind = DynKind.GlobalObj, Obj = r.Global };
        }

        return default;
    }

    private static JsValue LoadDynamic(Frame f, string name, out JsValue thisV, bool forCall)
    {
        DynRef d = ResolveDynamic(f, name);
        thisV = default;
        switch (d.Kind)
        {
            case DynKind.Slot:
                {
                    JsValue v = d.Env!.Slots[d.Slot];
                    if (v.IsTdz)
                    {
                        throw Errors.Reference("Cannot access '" + name + "' before initialization");
                    }

                    return ModuleLoader.ReadImport(v, name);
                }

            case DynKind.With:
                {
                    var key = new PropertyKey(name);
                    if (forCall)
                    {
                        thisV = JsValue.FromObject(d.Obj!);
                    }

                    if (!d.Obj!.HasProperty(key))
                    {
                        return default; // 9.1.1.2.6: the binding vanished during the lookup (sloppy mode)
                    }

                    return d.Obj.Get(key);
                }

            case DynKind.Cell:
            case DynKind.GlobalLex:
                if (d.Cell!.Value.IsTdz)
                {
                    throw Errors.Reference("Cannot access '" + name + "' before initialization");
                }

                return d.Cell.Value;
            case DynKind.GlobalObj:
                return d.Obj!.Get(new PropertyKey(name));
            default:
                throw Errors.Reference(name + " is not defined");
        }
    }

    private static void StoreDynamic(Frame f, string name, JsValue v, bool strict, bool init, bool fromVarEnv = false)
    {
        DynRef d = ResolveDynamic(f, name, fromVarEnv);
        switch (d.Kind)
        {
            case DynKind.Slot:
                {
                    byte kind = d.Env!.Info!.Kinds[d.Slot];
                    if (!init)
                    {
                        if (d.Env.Slots[d.Slot].IsTdz)
                        {
                            throw Errors.Reference("Cannot access '" + name + "' before initialization");
                        }

                        if (kind is ScopeInfo.KConst or ScopeInfo.KImport || (kind == ScopeInfo.KFuncName && strict))
                        {
                            throw Errors.Type("Assignment to constant variable.");
                        }

                        if (kind == ScopeInfo.KFuncName)
                        {
                            return;
                        }
                    }

                    d.Env.Slots[d.Slot] = v;
                    return;
                }

            case DynKind.With:
                {
                    var key = new PropertyKey(name);
                    if (strict && !d.Obj!.HasProperty(key))
                    {
                        throw Errors.Reference(name + " is not defined");
                    }

                    if (!d.Obj!.Set(key, v, JsValue.FromObject(d.Obj)) && strict)
                    {
                        throw Errors.Type(Ops.SetFailureMessage(d.Obj, key));
                    }

                    return;
                }

            case DynKind.Cell:
            case DynKind.GlobalLex:
                if (!init && d.Cell!.Value.IsTdz)
                {
                    throw Errors.Reference("Cannot access '" + name + "' before initialization");
                }

                if (!init && d.Cell!.IsConst)
                {
                    throw Errors.Type("Assignment to constant variable.");
                }

                d.Cell!.Value = v;
                return;
            case DynKind.GlobalObj:
                if (!d.Obj!.Set(new PropertyKey(name), v, JsValue.FromObject(d.Obj)) && strict)
                {
                    throw Errors.Type(Ops.SetFailureMessage(d.Obj, new PropertyKey(name)));
                }

                return;
            default:
                if (strict)
                {
                    throw Errors.Reference(name + " is not defined");
                }

                f.Realm.Global.Set(new PropertyKey(name), v, JsValue.FromObject(f.Realm.Global));
                return;
        }
    }

    private static string TypeofDynamic(Frame f, string name)
    {
        DynRef d = ResolveDynamic(f, name);
        if (d.Kind == DynKind.None)
        {
            return "undefined";
        }

        return LoadDynamic(f, name, out _, false).TypeOf;
    }

    private static bool DeleteDynamic(Frame f, string name)
    {
        DynRef d = ResolveDynamic(f, name);
        switch (d.Kind)
        {
            case DynKind.None:
                return true;
            case DynKind.With:
            case DynKind.GlobalObj:
                return d.Obj!.Delete(new PropertyKey(name));
            case DynKind.Cell:
                for (Env? e = f.Env; e is not null; e = e.Parent)
                {
                    if (e.Extra is not null && e.Extra.TryGetValue(name, out Cell? c) && ReferenceEquals(c, d.Cell))
                    {
                        if (!c.Deletable)
                        {
                            return false;
                        }

                        e.Extra.Remove(name);
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>EvalDeclarationInstantiation (19.2.1.3) for sloppy direct and indirect eval.</summary>
    private static void DeclareEvalVars(Frame f, GlobalDeclsView d)
    {
        var names = d.Vars.Concat(d.Functions).Concat(d.AnnexB).ToList();
        Env? varEnv = null;
        // Our own lexical environment (if any) is the first on the chain; the variable environment is the nearest function/module scope.
        for (Env? e = f.Env; e is not null; e = e.Parent)
        {
            if (e.Info is { IsFunctionVarScope: true })
            {
                varEnv = e;
                break;
            }
        }

        // 3.d: a var name may not collide with a lexical binding between the eval and its variable environment.
        for (Env? e = f.Env; e is not null && !ReferenceEquals(e, varEnv); e = e.Parent)
        {
            if (e.Info is null)
            {
                continue;
            }

            foreach (string n in names)
            {
                int i = e.Info.IndexOf(n);
                if (i >= 0 && e.Info.Kinds[i] is ScopeInfo.KLet or ScopeInfo.KConst && !(f.Env == e))
                {
                    throw Errors.Syntax("Identifier '" + n + "' has already been declared");
                }
            }
        }

        if (varEnv is null)
        {
            Realm r = f.Realm;
            foreach (string n in d.Vars.Concat(d.Functions))
            {
                if (r.GlobalLexicals.ContainsKey(n))
                {
                    throw Errors.Syntax("Identifier '" + n + "' has already been declared");
                }
            }

            foreach (string n in d.Functions)
            {
                if (r.Global.GetOwnProperty(n, out PropertyDescriptor pd))
                {
                    if (!pd.Configurable && !(pd.IsData && pd.Writable && pd.Enumerable))
                    {
                        throw Errors.Type("Cannot redefine property: " + n);
                    }
                }
                else if (!r.Global.IsExtensibleOp())
                {
                    throw Errors.Type("Cannot define global function '" + n + "'");
                }
            }

            foreach (string n in names)
            {
                if (!r.Global.HasOwnProperty(n) && r.Global.IsExtensibleOp() && !(d.AnnexB.Contains(n) && r.GlobalLexicals.ContainsKey(n)))
                {
                    r.Global.DefinePropertyOrThrow(n, PropertyDescriptor.Data(default, PropFlags.Default));
                }
            }

            return;
        }

        foreach (string n in names)
        {
            if (varEnv.Info!.IndexOf(n) >= 0)
            {
                continue;
            }

            varEnv.Extra ??= new Dictionary<string, Cell>(StringComparer.Ordinal);
            if (!varEnv.Extra.ContainsKey(n))
            {
                varEnv.Extra[n] = new Cell { Deletable = true };
            }
        }
    }

    // ---- arguments object -----------------------------------------------------------------------------

    private static JsArguments CreateArguments(Frame f, int[]? map)
    {
        Realm r = f.Realm;
        int n = f.ArgCount;
        JsArguments a = new(r.ObjectPrototype);
        if (map is not null)
        {
            a.MapEnv = f.Env;
            a.Map = new int[n];
            for (int i = 0; i < n; i++)
            {
                a.Map[i] = i < map.Length ? map[i] : -1;
            }
        }

        a.AddProperty("length", JsValue.FromNumber(n), PropFlags.Hidden);
        for (int i = 0; i < n; i++)
        {
            a.AddProperty(new PropertyKey(Ops.NumberToStr(i)), f.ArgArr![f.ArgOff + i], PropFlags.Default);
        }

        a.AddProperty(new PropertyKey(WellKnown.Iterator), r.ArrayProtoValues, PropFlags.Hidden);
        if (f.Code.Strict || map is null)
        {
            a.DefineAccessorDirect("callee", r.ThrowTypeError, r.ThrowTypeError, PropFlags.None);
        }
        else
        {
            a.AddProperty("callee", JsValue.FromObject(f.Func!), PropFlags.Hidden);
        }

        return a;
    }

    // ---- property access slow paths --------------------------------------------------------------------

    private static JsValue GetPropSlow(JsObject obj, JsValue receiver, string name, PropCache c)
    {
        var key = new PropertyKey(name);
        if (!obj.OverridesGet)
        {
            int slot = obj.Shape.Lookup(key, out PropFlags fl);
            if (slot >= 0)
            {
                if ((fl & PropFlags.Accessor) == 0 && !obj.Shape.IsDictionary)
                {
                    c.Shape = obj.Shape;
                    c.Slot = slot;
                    c.Holder = null;
                    return obj.Slots[slot];
                }
            }
            else if (obj.Shape.Proto is { } p && !p.OverridesGet && !obj.Shape.IsDictionary && (obj.IsPlainStorage || (obj is JsArray && name != "length")) && !p.Shape.IsDictionary)
            {
                int ps = p.Shape.Lookup(key, out PropFlags pf);
                if (ps >= 0 && (pf & PropFlags.Accessor) == 0)
                {
                    c.Shape = obj.Shape;
                    c.Holder = p;
                    c.HolderShape = p.Shape;
                    c.Slot = ps;
                    return p.Slots[ps];
                }
            }
        }

        return obj.Get(key, receiver);
    }

    private static void SetPropSlow(JsObject obj, string name, JsValue v, PropCache c, bool strict)
    {
        var key = new PropertyKey(name);
        int slot = obj.Shape.Lookup(key, out PropFlags fl);
        if (slot >= 0)
        {
            if ((fl & (PropFlags.Accessor | PropFlags.Writable)) == PropFlags.Writable && !obj.Shape.IsDictionary)
            {
                c.Shape = obj.Shape;
                c.Slot = slot;
                c.Holder = null;
                obj.Slots[slot] = v;
                return;
            }
        }
        else if (obj.Extensible && obj.IsPlainStorage)
        {
            bool clear = true;
            for (JsObject? p = obj.Shape.Proto; p is not null; p = p.Shape.Proto)
            {
                if (p.OverridesSet || (!p.IsPlainStorage && !(p is JsArray && name != "length")) || p.Shape.Lookup(key, out _) >= 0)
                {
                    clear = false;
                    break;
                }
            }

            if (clear)
            {
                obj.AddProperty(key, v, PropFlags.Default);
                return;
            }
        }

        if (!obj.Set(key, v, JsValue.FromObject(obj)) && strict)
        {
            throw Errors.Type(Ops.SetFailureMessage(obj, key));
        }
    }

    internal static JsValue GetElem(JsValue o, JsValue k)
    {
        if (k.IsNumber)
        {
            double d = k.Num;
            if (o.Ref is JsArray arr)
            {
                uint i = (uint)d;
                if (i == d && arr.Dense is not null && i < (uint)arr.Count)
                {
                    JsValue v = arr.Dense[i];
                    if (!v.IsHole)
                    {
                        return v;
                    }
                }
            }
            else if (o.Ref is string s)
            {
                uint i = (uint)d;
                if (i == d && i < (uint)s.Length)
                {
                    return JsValue.FromString(s[(int)i].ToString());
                }
            }
        }

        if (o.Ref is JsObject obj)
        {
            return obj.Get(Ops.ToPropertyKey(k), o);
        }

        if (o.IsNullish)
        {
            throw Errors.Type("Cannot read properties of " + (o.IsNull ? "null" : "undefined") + " (reading '" + (k.IsSymbol ? k.ToString() : Ops.ToStr(k)) + "')");
        }

        return Ops.GetV(o, Ops.ToPropertyKey(k));
    }

    internal static void SetElem(JsValue o, JsValue k, JsValue v, bool strict)
    {
        if (o.Ref is JsArray arr && k.IsNumber && arr.Dense is not null)
        {
            double d = k.Num;
            uint i = (uint)d;
            if (i == d)
            {
                if (i < (uint)arr.Count && !arr.Dense[i].IsHole)
                {
                    arr.Dense[i] = v;
                    return;
                }

                if (i == (uint)arr.Count && i == arr.Length && arr.Extensible && arr.LengthWritable)
                {
                    arr.Push(v);
                    return;
                }
            }
        }

        if (o.IsNullish)
        {
            throw Errors.Type("Cannot set properties of " + (o.IsNull ? "null" : "undefined") + " (setting '" + (k.IsSymbol ? k.ToString() : Ops.ToStr(k)) + "')");
        }

        Ops.SetV(o, Ops.ToPropertyKey(k), v, strict);
    }

    private static bool DeleteMember(JsValue o, PropertyKey key, bool strict)
    {
        if (o.IsNullish)
        {
            throw Errors.Type("Cannot convert undefined or null to object");
        }

        JsObject obj = Ops.ToObject(o);
        bool ok = obj.Delete(key);
        if (!ok && strict)
        {
            throw Errors.Type("Cannot delete property '" + Ops.KeyText(key) + "' of " + Ops.DisplayValue(JsValue.FromObject(obj)));
        }

        return ok;
    }

    private static JsObject? SuperBase(Frame f, out JsValue thisV)
    {
        Frame tf = f.ThisFrame;
        thisV = tf.This;
        if (thisV.IsTdz)
        {
            throw Errors.Reference("Must call super constructor in derived class before accessing 'this' or returning from derived constructor");
        }

        JsObject home = tf.Func?.HomeObject ?? throw Errors.Syntax("'super' keyword unexpected here");
        return home.GetPrototypeOf();
    }

    private static JsValue SuperGet(Frame f, PropertyKey key)
    {
        JsObject? p = SuperBase(f, out JsValue thisV);
        if (p is null)
        {
            throw Errors.Type("Cannot read properties of null (reading '" + Ops.KeyText(key) + "')");
        }

        return p.Get(key, thisV);
    }

    private static void SuperSet(Frame f, PropertyKey key, JsValue v, bool strict)
    {
        JsObject? p = SuperBase(f, out JsValue thisV);
        if (p is null)
        {
            throw Errors.Type("Cannot set properties of null (setting '" + Ops.KeyText(key) + "')");
        }

        if (!p.Set(key, v, thisV) && strict)
        {
            throw Errors.Type("Cannot assign to read only property '" + Ops.KeyText(key) + "' of object");
        }
    }

    private static JsValue PrivateGet(JsValue o, PrivateName pn)
    {
        if (o.Ref is JsObject obj && obj.Privates is { } d && d.TryGetValue(pn, out PrivateElement? e))
        {
            if (e.Kind == 0 || e.Kind == 1)
            {
                return e.Value;
            }

            if (e.Getter is null)
            {
                throw Errors.Type("'" + pn.Description + "' was defined without a getter");
            }

            return e.Getter.Call(o, default);
        }

        throw Errors.Type("Cannot read private member " + pn.Description + " from an object whose class did not declare it");
    }

    private static void PrivateSet(JsValue o, PrivateName pn, JsValue v)
    {
        if (o.Ref is JsObject obj && obj.Privates is { } d && d.TryGetValue(pn, out PrivateElement? e))
        {
            switch (e.Kind)
            {
                case 0:
                    e.Value = v;
                    return;
                case 1:
                    throw Errors.Type("Private method '" + pn.Description + "' is not writable");
                default:
                    if (e.Setter is null)
                    {
                        throw Errors.Type("'" + pn.Description + "' was defined without a setter");
                    }

                    e.Setter.Call(o, [v]);
                    return;
            }
        }

        throw Errors.Type("Cannot write private member " + pn.Description + " to an object whose class did not declare it");
    }

    // ---- literals and classes ----------------------------------------------------------------------------

    private static void SpreadInto(JsArray target, JsValue iterable)
    {
        IteratorRecord rec = Ops.GetIterator(iterable);
        Agent ag = Agent.Current;
        while (Ops.IteratorStepValue(rec, out JsValue v))
        {
            ag.Tick();
            target.Push(v);
        }
    }

    internal static void CopyDataProperties(JsObject target, JsValue source, List<PropertyKey>? excluded)
    {
        if (source.IsNullish)
        {
            return;
        }

        JsObject from = Ops.ToObject(source);
        var keys = new List<PropertyKey>();
        from.OwnKeys(keys);
        foreach (PropertyKey k in keys)
        {
            if (excluded is not null && excluded.Contains(k))
            {
                continue;
            }

            if (from.GetOwnProperty(k, out PropertyDescriptor d) && d.Enumerable)
            {
                target.CreateDataPropertyOrThrow(k, from.Get(k, JsValue.FromObject(from)));
            }
        }
    }

    private static void DefineMethod(JsObject target, PropertyKey key, ScriptFunction fn, int flags)
    {
        fn.HomeObject = target;
        bool enumerable = (flags & 1) != 0;
        PropFlags pf = PropFlags.Configurable | (enumerable ? PropFlags.Enumerable : 0);
        PropertyDescriptor d;
        if ((flags & 2) != 0)
        {
            d = new PropertyDescriptor { Get = JsValue.FromObject(fn), HasGet = true, HasEnumerable = true, HasConfigurable = true, Enumerable = enumerable, Configurable = true };
        }
        else if ((flags & 4) != 0)
        {
            d = new PropertyDescriptor { Set = JsValue.FromObject(fn), HasSet = true, HasEnumerable = true, HasConfigurable = true, Enumerable = enumerable, Configurable = true };
        }
        else
        {
            d = PropertyDescriptor.Data(JsValue.FromObject(fn), pf | PropFlags.Writable);
        }

        target.DefinePropertyOrThrow(key, d);
    }

    private static void SetFunctionNameFromKey(JsValue fn, JsValue key, int prefixKind)
    {
        if (fn.Ref is not JsObject f)
        {
            return;
        }

        string name = key.Ref is JsSymbol s ? (s.Description is null ? string.Empty : "[" + s.Description + "]") : Ops.ToStr(key);
        if (prefixKind == 1)
        {
            name = "get " + name;
        }
        else if (prefixKind == 2)
        {
            name = "set " + name;
        }

        if (f.GetOwnProperty("name", out PropertyDescriptor d) && d.Value.IsString && d.Value.AsString.Length > 0 && prefixKind == 0 && f is ScriptFunction { Code.IsClassMember: true })
        {
            return;
        }

        f.DefineDirect("name", JsValue.FromString(name), PropFlags.Configurable);
    }

    private static void NewClass(Frame f, FunctionCode code, bool hasHeritage, JsValue heritage, out JsObject ctor, out JsObject proto)
    {
        Realm r = f.Realm;
        JsObject? protoParent = r.ObjectPrototype;
        JsObject ctorParent = r.FunctionPrototype;
        if (hasHeritage)
        {
            if (heritage.IsNull)
            {
                protoParent = null;
            }
            else if (!Ops.IsConstructor(heritage))
            {
                throw Errors.Type("Class extends value " + Ops.DisplayValue(heritage) + " is not a constructor or null");
            }
            else
            {
                var h = (JsObject)heritage.Ref!;
                JsValue pp = h.Get("prototype");
                if (!pp.IsObject && !pp.IsNull)
                {
                    throw Errors.Type("Class extends value does not have valid prototype property " + Ops.DisplayValue(pp));
                }

                protoParent = pp.AsObject;
                ctorParent = h;
            }
        }

        proto = new JsObject(protoParent);
        var fn = new ScriptFunction(r, code, f.Env, ctorParent, true, readOnlyPrototype: true)
        {
            IsClassConstructor = true,
            IsDerived = code.Kind == FunctionKind.DerivedConstructor,
            HomeObject = proto,
            Module = f.Func?.Module ?? f.Owner as ModuleRecord,
        };
        fn.Slots[2] = JsValue.FromObject(proto);
        proto.DefineDirect("constructor", JsValue.FromObject(fn), PropFlags.Hidden);
        ctor = fn;
    }

    private static void DefineClassField(ScriptFunction ctor, JsValue key, JsValue init, int flags)
    {
        bool isStatic = (flags & 1) != 0;
        bool isPrivate = (flags & 2) != 0;
        var initFn = init.Ref as ScriptFunction;
        if (initFn is not null)
        {
            initFn.HomeObject = isStatic ? ctor : (JsObject)ctor.Slots[2].Ref!;
        }

        if (isStatic)
        {
            JsValue v = initFn is null ? default : initFn.Call(JsValue.FromObject(ctor), default);
            if (isPrivate)
            {
                ctor.PrivateFieldAdd((PrivateName)key.Ref!, v);
            }
            else
            {
                ctor.CreateDataPropertyOrThrow(Ops.ToPropertyKey(key), v);
            }

            return;
        }

        ctor.Fields ??= [];
        ctor.Fields.Add(isPrivate
            ? new ClassFieldDef { Private = (PrivateName)key.Ref!, Init = initFn }
            : new ClassFieldDef { Key = Ops.ToPropertyKey(key), Init = initFn });
    }

    private static void AddPrivateMethod(ScriptFunction ctor, PrivateName pn, ScriptFunction fn, int flags)
    {
        bool isStatic = (flags & 1) != 0;
        fn.HomeObject = isStatic ? ctor : (JsObject)ctor.Slots[2].Ref!;
        if (isStatic)
        {
            ctor.PrivateAddMethod(new PrivateMethodDef { Name = pn, Method = (flags & 6) == 0 ? fn : null, Getter = (flags & 2) != 0 ? fn : null, Setter = (flags & 4) != 0 ? fn : null });
            return;
        }

        ctor.PrivateMethods ??= [];
        PrivateMethodDef? def = ctor.PrivateMethods.Find(m => ReferenceEquals(m.Name, pn));
        if (def is null)
        {
            def = new PrivateMethodDef { Name = pn };
            ctor.PrivateMethods.Add(def);
        }

        if ((flags & 2) != 0)
        {
            def.Getter = fn;
        }
        else if ((flags & 4) != 0)
        {
            def.Setter = fn;
        }
        else
        {
            def.Method = fn;
        }
    }

    private static JsValue ConstructResult(Frame f, JsValue rv)
    {
        if (rv.IsObject)
        {
            return rv;
        }

        if (f.Func!.IsDerived)
        {
            if (!rv.IsUndefined)
            {
                throw Errors.Type("Derived constructors may only return object or undefined");
            }

            if (f.This.IsTdz)
            {
                throw Errors.Reference("Must call super constructor in derived class before accessing 'this' or returning from derived constructor");
            }
        }

        return f.This;
    }

    private static JsValue[] ArgsFromArray(JsValue arr)
    {
        var a = (JsArray)arr.Ref!;
        var r = new JsValue[a.Count];
        for (int i = 0; i < r.Length; i++)
        {
            JsValue v = a.Dense![i];
            r[i] = v.IsHole ? default : v;
        }

        return r;
    }

    private static JsValue CallValue(JsValue fv, JsValue thisV, JsValue[] args, string text)
    {
        if (fv.Ref is JsObject o && o.IsCallable)
        {
            Agent.Current.Tick();
            return o.Call(thisV, args);
        }

        throw Errors.Type(text + " is not a function");
    }

    private static JsValue SuperCall(Frame f, JsValue[] args)
    {
        Frame tf = f.ThisFrame;
        ScriptFunction func = tf.Func!;
        JsObject? sc = func.GetPrototypeOf();
        if (sc is null || !sc.IsConstructor)
        {
            throw Errors.Type("Super constructor " + (sc is null ? "null" : Ops.DisplayValue(JsValue.FromObject(sc))) + " of anonymous class is not a constructor");
        }

        JsValue result = sc.Construct(args, tf.NewTarget!);
        if (!tf.This.IsTdz)
        {
            throw Errors.Reference("Super constructor may only be called once");
        }

        tf.This = result;
        InitializeInstanceElements((JsObject)result.Ref!, func);
        return result;
    }

    private static ScriptFunction MakeClosure(Frame f, FunctionCode code)
    {
        Realm r = f.Realm;
        JsObject proto = code.IsGenerator ? (code.IsAsync ? r.AsyncGeneratorFunctionPrototype : r.GeneratorFunctionPrototype) : code.IsAsync ? r.AsyncFunctionPrototype : r.FunctionPrototype;
        bool hasProto = code.IsGenerator || (code.Kind == FunctionKind.Normal && !code.IsAsync);
        var fn = new ScriptFunction(r, code, f.Env, proto, hasProto);
        if (code.Kind == FunctionKind.Arrow)
        {
            fn.OuterFrame = f;
        }

        fn.Module = f.Func?.Module ?? f.Owner as ModuleRecord;
        if (code.IsGenerator)
        {
            fn.Slots[2] = JsValue.FromObject(new JsObject(code.IsAsync ? r.AsyncGeneratorPrototype : r.GeneratorPrototype));
        }
        else if (hasProto)
        {
            var p = new JsObject(r.ObjectPrototype);
            p.AddProperty("constructor", JsValue.FromObject(fn), PropFlags.Hidden);
            fn.Slots[2] = JsValue.FromObject(p);
        }

        return fn;
    }

    private static string IterErrorText(JsValue v, string text) =>
        v.IsNullish ? Ops.DisplayValue(v) + " is not iterable (cannot read property Symbol(Symbol.iterator))" : text + " is not iterable";

    private static string DestructureError(JsValue v, string? firstKey, string text)
    {
        string what = v.IsNull ? "null" : "undefined";
        return firstKey is null
            ? "Cannot destructure '" + text + "' as it is " + what + "."
            : "Cannot destructure property '" + firstKey + "' of '" + text + "' as it is " + what + ".";
    }

    private static int EvalFlagsFor(Frame f) => f.Code.Strict ? 1 : 0;

    private static Frame PrepareDirectEval(Agent ag, Frame f, string src, int flags)
    {
        var names = new List<string>();
        for (Env? e = f.Env; e is not null; e = e.Parent)
        {
            if (e.Info is not null)
            {
                foreach (string n in e.Info.Names)
                {
                    if (n.StartsWith('#'))
                    {
                        names.Add(n[1..]);
                    }
                }
            }
        }

        var opts = new ParseOptions
        {
            Goal = ParseGoal.Eval,
            Strict = (flags & 1) != 0,
            AllowNewTarget = (flags & 2) != 0 || f.Code.AllowsNewTarget,
            AllowSuperProperty = (flags & 4) != 0 || f.Code.AllowsSuperProperty,
            AllowSuperCall = (flags & 8) != 0 || f.Code.AllowsSuperCall,
            InClassFieldInitializer = (flags & 16) != 0 || f.Code.InFieldInit,
            PrivateNames = names,
            FileName = "<eval>",
        };
        ParseResult pr = JsParser.Parse(src, opts);
        if (pr.HasErrors)
        {
            throw Errors.Syntax(pr.Diagnostics[0].Headline);
        }

        FunctionCode code = Compiler.CompileEval(pr.Program, pr.Source, true);
        code.AllowsNewTarget = opts.AllowNewTarget;
        code.AllowsSuperProperty = opts.AllowSuperProperty;
        code.AllowsSuperCall = opts.AllowSuperCall;
        code.InFieldInit = opts.InClassFieldInitializer;
        return new Frame(code, f.Realm)
        {
            Env = f.Env,
            ThisFrame = f.ThisFrame,
            Func = f.Func,
            ArgArr = null,
        };
    }
}
