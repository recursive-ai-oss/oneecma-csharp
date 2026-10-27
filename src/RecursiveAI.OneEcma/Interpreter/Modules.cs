using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma;

internal enum ModuleStatus : byte
{
    Loaded,
    Linked,
    Evaluating,
    Evaluated,
    Errored,
}

internal sealed class ImportEntry
{
    internal string Request = "";
    /// <summary>The exported name imported, or null for a namespace import.</summary>
    internal string? ImportName;
    internal string Local = "";
}

internal sealed class IndirectExport
{
    internal string ExportName = "";
    internal string Request = "";
    /// <summary>The imported name, or null for <c>export * as ns</c>.</summary>
    internal string? ImportName;
}

/// <summary>A Source Text Module Record (16.2.1.6).</summary>
internal sealed class ModuleRecord
{
    internal string Id = "";
    internal Realm Realm = null!;
    internal FunctionCode Code = null!;
    internal Frame? Frame;
    internal Env? Env;
    internal ModuleStatus Status;
    internal JsValue Error;
    internal readonly Dictionary<string, ModuleRecord> Requested = new(StringComparer.Ordinal);
    internal readonly List<string> RequestOrder = [];
    internal readonly List<ImportEntry> Imports = [];
    internal readonly Dictionary<string, string> LocalExports = new(StringComparer.Ordinal);
    internal readonly List<IndirectExport> IndirectExports = [];
    internal readonly List<string> StarExports = [];
    internal ModuleNamespace? Namespace;
    internal JsObject? Meta;
    internal PromiseObject? EvalPromise;
    internal Action? OnDone;
    internal readonly List<Action> Waiters = [];
    internal bool Instantiated;
}

/// <summary>The module namespace exotic object (10.4.6).</summary>
internal sealed class ModuleNamespace : JsObject
{
    private readonly ModuleRecord _module;
    private readonly string[] _names;
    private readonly Dictionary<string, ImportRef> _refs;

    internal ModuleNamespace(ModuleRecord module, string[] names, Dictionary<string, ImportRef> refs)
        : base((JsObject?)null)
    {
        _module = module;
        _names = names;
        _refs = refs;
        OverridesGet = true;
        OverridesSet = true;
        Extensible = false;
        DefineDirect(new PropertyKey(WellKnown.ToStringTag), JsValue.FromString("Module"), PropFlags.None);
    }

    internal override string ClassName => "Module";

    internal override bool IsPlainStorage => false;

    internal override bool SetPrototypeOf(JsObject? p) => p is null;

    internal override bool IsExtensibleOp() => false;

    internal override bool PreventExtensions() => true;

    private bool TryRead(string name, out JsValue v)
    {
        if (!_refs.TryGetValue(name, out ImportRef? r))
        {
            v = default;
            return false;
        }

        v = ModuleLoader.ReadRef(r, name);
        return true;
    }

    internal override bool GetOwnProperty(PropertyKey key, out PropertyDescriptor desc)
    {
        if (key.IsSymbol)
        {
            return base.GetOwnProperty(key, out desc);
        }

        if (!TryRead(key.String!, out JsValue v))
        {
            desc = default;
            return false;
        }

        desc = PropertyDescriptor.Data(v, PropFlags.Writable | PropFlags.Enumerable);
        return true;
    }

    internal override bool DefineOwnProperty(PropertyKey key, in PropertyDescriptor desc)
    {
        if (key.IsSymbol)
        {
            return base.DefineOwnProperty(key, desc);
        }

        if (!TryRead(key.String!, out JsValue v))
        {
            return false;
        }

        if ((desc.HasConfigurable && desc.Configurable) || (desc.HasEnumerable && !desc.Enumerable) || desc.IsAccessor || (desc.HasWritable && !desc.Writable))
        {
            return false;
        }

        return !desc.HasValue || Ops.SameValue(desc.Value, v);
    }

    internal override bool HasProperty(PropertyKey key) => key.IsSymbol ? base.HasProperty(key) : _refs.ContainsKey(key.String!);

    internal override bool TryGetOwn(PropertyKey key, JsValue receiver, out JsValue value)
    {
        if (key.IsSymbol)
        {
            return base.TryGetOwn(key, receiver, out value);
        }

        return TryRead(key.String!, out value);
    }

    internal override JsValue Get(PropertyKey key, JsValue receiver)
    {
        if (key.IsSymbol)
        {
            return base.Get(key, receiver);
        }

        return TryRead(key.String!, out JsValue v) ? v : default;
    }

    internal override bool Set(PropertyKey key, JsValue value, JsValue receiver) => false;

    internal override bool Delete(PropertyKey key)
    {
        if (key.IsSymbol)
        {
            return base.Delete(key);
        }

        return !_refs.ContainsKey(key.String!);
    }

    internal override void OwnKeys(List<PropertyKey> keys)
    {
        foreach (string n in _names)
        {
            keys.Add(new PropertyKey(n));
        }

        base.OwnKeys(keys);
    }
}

/// <summary>Loading, linking and evaluating modules (16.2.1.5).</summary>
internal static class ModuleLoader
{
    // ---- loading ------------------------------------------------------------------------------------

    /// <summary>ParseModule + HostLoadImportedModule for the whole static graph. Throws a SyntaxError for bad source.</summary>
    internal static ModuleRecord Load(Realm realm, HostModule hm)
    {
        if (realm.Modules.TryGetValue(hm.Id, out ModuleRecord? existing))
        {
            return existing;
        }

        ParseResult pr = JsParser.Parse(hm.Source, new ParseOptions { Goal = ParseGoal.Module, FileName = hm.Id });
        if (pr.HasErrors)
        {
            JsException pe = Errors.Syntax(pr.Diagnostics[0].Headline);
            pe.Report = pr.Diagnostics[0];
            throw pe;
        }

        var m = new ModuleRecord { Id = hm.Id, Realm = realm };
        m.Code = Compiler.CompileModule(pr.Program, pr.Source, hm.Id);
        realm.Modules[hm.Id] = m;
        try
        {
            Collect(m, pr.Program);
            foreach (string req in m.RequestOrder.ToArray())
            {
                string spec = req;
                HostModule? dep = realm.Agent.Host.ResolveModule(spec, hm.Id, EmptyAttrs);
                if (dep is null)
                {
                    throw Errors.Make("Error", "Cannot find module '" + spec + "' imported from " + hm.Id);
                }

                m.Requested[req] = Load(realm, dep);
            }
        }
        catch
        {
            realm.Modules.Remove(hm.Id);
            throw;
        }

        return m;
    }

    private static readonly Dictionary<string, string> EmptyAttrs = new();

    private static string NameOf(Node n) => n is Identifier id ? id.Name : ((StringLiteral)n).Value;

    private static void Request(ModuleRecord m, string spec)
    {
        if (!m.Requested.ContainsKey(spec) && !m.RequestOrder.Contains(spec))
        {
            m.RequestOrder.Add(spec);
        }
    }

    private static void Collect(ModuleRecord m, Syntax.Program program)
    {
        // Local bindings that are imports: re-exporting one is an indirect export (16.2.1.6.2 ParseModule step).
        var importLocals = new Dictionary<string, ImportEntry>(StringComparer.Ordinal);
        foreach (Node st in program.Body)
        {
            if (st is ImportDeclaration imp)
            {
                string spec = imp.Source.Value;
                Request(m, spec);
                foreach (ImportSpecifier s in imp.Specifiers)
                {
                    var e = new ImportEntry
                    {
                        Request = spec,
                        Local = s.Local.Name,
                        ImportName = s.SpecifierKind switch
                        {
                            ImportSpecifierKind.Namespace => null,
                            ImportSpecifierKind.Default => "default",
                            _ => NameOf(s.Imported!),
                        },
                    };
                    m.Imports.Add(e);
                    importLocals[e.Local] = e;
                }
            }
        }

        foreach (Node st in program.Body)
        {
            switch (st)
            {
                case ExportAllDeclaration ea:
                    Request(m, ea.Source.Value);
                    if (ea.Exported is null)
                    {
                        m.StarExports.Add(ea.Source.Value);
                    }
                    else
                    {
                        m.IndirectExports.Add(new IndirectExport { ExportName = NameOf(ea.Exported), Request = ea.Source.Value, ImportName = null });
                    }

                    break;
                case ExportNamedDeclaration en:
                    if (en.Source is not null)
                    {
                        Request(m, en.Source.Value);
                        foreach (ExportSpecifier sp in en.Specifiers)
                        {
                            m.IndirectExports.Add(new IndirectExport { ExportName = NameOf(sp.Exported), Request = en.Source.Value, ImportName = NameOf(sp.Local) });
                        }
                    }
                    else if (en.Declaration is not null)
                    {
                        foreach (string n in DeclaredNames(en.Declaration))
                        {
                            m.LocalExports[n] = n;
                        }
                    }
                    else
                    {
                        foreach (ExportSpecifier sp in en.Specifiers)
                        {
                            string local = NameOf(sp.Local);
                            string exported = NameOf(sp.Exported);
                            if (importLocals.TryGetValue(local, out ImportEntry? ie))
                            {
                                m.IndirectExports.Add(new IndirectExport { ExportName = exported, Request = ie.Request, ImportName = ie.ImportName });
                            }
                            else
                            {
                                m.LocalExports[exported] = local;
                            }
                        }
                    }

                    break;
                case ExportDefaultDeclaration ed:
                    switch (ed.Declaration)
                    {
                        case FunctionDeclaration { Function.Id: { } fid }:
                            m.LocalExports["default"] = fid.Name;
                            break;
                        case ClassDeclaration { Class.Id: { } cid }:
                            m.LocalExports["default"] = cid.Name;
                            break;
                        default:
                            m.LocalExports["default"] = "*default*";
                            break;
                    }

                    break;
            }
        }
    }

    private static IEnumerable<string> DeclaredNames(Node decl)
    {
        switch (decl)
        {
            case FunctionDeclaration fd:
                yield return fd.Function.Id!.Name;
                break;
            case ClassDeclaration cd:
                yield return cd.Class.Id!.Name;
                break;
            case VariableDeclaration vd:
                foreach (VariableDeclarator d in vd.Declarations)
                {
                    foreach (string n in PatternNames(d.Id))
                    {
                        yield return n;
                    }
                }

                break;
        }
    }

    private static IEnumerable<string> PatternNames(Node p)
    {
        switch (p)
        {
            case Identifier id:
                yield return id.Name;
                break;
            case AssignmentPattern ap:
                foreach (string n in PatternNames(ap.Left))
                {
                    yield return n;
                }

                break;
            case RestElement re:
                foreach (string n in PatternNames(re.Argument))
                {
                    yield return n;
                }

                break;
            case ArrayPattern arr:
                foreach (Node? e in arr.Elements)
                {
                    if (e is not null)
                    {
                        foreach (string n in PatternNames(e))
                        {
                            yield return n;
                        }
                    }
                }

                break;
            case ObjectPattern op:
                foreach (Node prop in op.Properties)
                {
                    Node target = prop is Property pn ? pn.Value : prop;
                    foreach (string n in PatternNames(target))
                    {
                        yield return n;
                    }
                }

                break;
        }
    }

    // ---- linking ------------------------------------------------------------------------------------

    /// <summary>Link (16.2.1.5.1): instantiate every environment in the graph, then bind imports.</summary>
    internal static void Link(ModuleRecord root)
    {
        var order = new List<ModuleRecord>();
        Visit(root, order, new HashSet<ModuleRecord>());
        foreach (ModuleRecord m in order)
        {
            if (!m.Instantiated)
            {
                Instantiate(m);
            }
        }

        foreach (ModuleRecord m in order)
        {
            if (m.Status == ModuleStatus.Loaded)
            {
                BindImports(m);
                m.Status = ModuleStatus.Linked;
            }
        }
    }

    private static void Visit(ModuleRecord m, List<ModuleRecord> order, HashSet<ModuleRecord> seen)
    {
        // Post-order over the static dependency graph, iterative: module graphs can be deep.
        var stack = new Stack<(ModuleRecord M, int Next)>();
        if (!seen.Add(m))
        {
            return;
        }

        stack.Push((m, 0));
        while (stack.Count > 0)
        {
            (ModuleRecord cur, int i) = stack.Pop();
            if (i < cur.RequestOrder.Count)
            {
                stack.Push((cur, i + 1));
                ModuleRecord dep = cur.Requested[cur.RequestOrder[i]];
                if (seen.Add(dep))
                {
                    stack.Push((dep, 0));
                }
            }
            else
            {
                order.Add(cur);
            }
        }
    }

    private static void Instantiate(ModuleRecord m)
    {
        // InitializeEnvironment's first half: run the prologue (scope creation and function hoisting) up to GenStart.
        Agent ag = m.Realm.Agent;
        var f = new Frame(m.Code, m.Realm) { Env = null, This = default, Owner = m };
        m.Frame = f;
        Vm.RunEntry(ag, f);
        m.Env = f.Env;
        m.Instantiated = true;
    }

    private static int SlotOf(ModuleRecord m, string name)
    {
        string[] names = m.Code.FuncScope!.Names;
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i] == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static void BindImports(ModuleRecord m)
    {
        foreach (ImportEntry ie in m.Imports)
        {
            ModuleRecord target = m.Requested[ie.Request];
            ImportRef r;
            if (ie.ImportName is null)
            {
                r = new ImportRef { Namespace = GetNamespace(target) };
            }
            else
            {
                ImportRef? res = ResolveExport(target, ie.ImportName, []);
                if (res is null)
                {
                    throw LinkError("The requested module '" + ie.Request + "' does not provide an export named '" + ie.ImportName + "'");
                }

                if (ReferenceEquals(res, Ambiguous))
                {
                    throw LinkError("The requested module '" + ie.Request + "' contains conflicting star exports for name '" + ie.ImportName + "'");
                }

                r = res;
            }

            int slot = SlotOf(m, ie.Local);
            m.Env!.Slots[slot] = new JsValue(r, 0);
        }
    }

    private static readonly ImportRef Ambiguous = new();

    private static JsException LinkError(string message)
    {
        JsException e = Errors.Syntax(message);
        e.Report = Diagnostics.ErrorReport.ForException(e, "link");
        return e;
    }

    /// <summary>ResolveExport (16.2.1.6.3). Returns null when not found and <see cref="Ambiguous"/> when conflicting.</summary>
    internal static ImportRef? ResolveExport(ModuleRecord m, string name, List<(ModuleRecord, string)> resolveSet)
    {
        foreach ((ModuleRecord rm, string rn) in resolveSet)
        {
            if (ReferenceEquals(rm, m) && rn == name)
            {
                return null; // circular import request
            }
        }

        resolveSet.Add((m, name));
        if (m.LocalExports.TryGetValue(name, out string? local))
        {
            return new ImportRef { Env = m.Env, Slot = SlotOf(m, local), Name = local };
        }

        foreach (IndirectExport ie in m.IndirectExports)
        {
            if (ie.ExportName == name)
            {
                ModuleRecord imported = m.Requested[ie.Request];
                if (ie.ImportName is null)
                {
                    return new ImportRef { Namespace = GetNamespace(imported) };
                }

                return ResolveExport(imported, ie.ImportName, resolveSet);
            }
        }

        if (name == "default")
        {
            return null;
        }

        ImportRef? star = null;
        foreach (string req in m.StarExports)
        {
            ImportRef? res = ResolveExport(m.Requested[req], name, resolveSet);
            if (ReferenceEquals(res, Ambiguous))
            {
                return res;
            }

            if (res is not null)
            {
                if (star is null)
                {
                    star = res;
                }
                else if (!SameBinding(star, res))
                {
                    return Ambiguous;
                }
            }
        }

        return star;
    }

    private static bool SameBinding(ImportRef a, ImportRef b) =>
        a.Namespace is not null || b.Namespace is not null ? ReferenceEquals(a.Namespace, b.Namespace) : ReferenceEquals(a.Env, b.Env) && a.Slot == b.Slot;

    private static void GetExportedNames(ModuleRecord m, HashSet<string> names, HashSet<ModuleRecord> visited)
    {
        if (!visited.Add(m))
        {
            return;
        }

        foreach (string n in m.LocalExports.Keys)
        {
            names.Add(n);
        }

        foreach (IndirectExport ie in m.IndirectExports)
        {
            names.Add(ie.ExportName);
        }

        foreach (string req in m.StarExports)
        {
            var sub = new HashSet<string>(StringComparer.Ordinal);
            GetExportedNames(m.Requested[req], sub, visited);
            foreach (string n in sub)
            {
                if (n != "default")
                {
                    names.Add(n);
                }
            }
        }
    }

    internal static ModuleNamespace GetNamespace(ModuleRecord m)
    {
        if (m.Namespace is not null)
        {
            return m.Namespace;
        }

        var all = new HashSet<string>(StringComparer.Ordinal);
        GetExportedNames(m, all, []);
        var refs = new Dictionary<string, ImportRef>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (string n in all)
        {
            ImportRef? r = ResolveExport(m, n, []);
            if (r is not null && !ReferenceEquals(r, Ambiguous))
            {
                refs[n] = r;
                names.Add(n);
            }
        }

        names.Sort(StringComparer.Ordinal);
        m.Namespace = new ModuleNamespace(m, [.. names], refs);
        return m.Namespace;
    }

    // ---- reading imports ------------------------------------------------------------------------------

    internal static JsValue ReadImport(JsValue slot, string name)
    {
        if (slot.Ref is ImportRef r)
        {
            return ReadRef(r, name);
        }

        return slot;
    }

    internal static JsValue ReadRef(ImportRef r, string name)
    {
        if (r.Namespace is not null)
        {
            return JsValue.FromObject(r.Namespace);
        }

        JsValue v = r.Env!.Slots[r.Slot];
        if (v.IsTdz)
        {
            throw Errors.Reference("Cannot access '" + (r.Name.Length > 0 ? r.Name : name) + "' before initialization");
        }

        return v.Ref is ImportRef inner ? ReadRef(inner, name) : v;
    }

    // ---- evaluation -----------------------------------------------------------------------------------

    /// <summary>Evaluate (16.2.1.5.3): runs the graph in dependency order, waiting on top-level await; returns a promise.</summary>
    internal static PromiseObject Evaluate(ModuleRecord root)
    {
        if (root.EvalPromise is not null)
        {
            return root.EvalPromise;
        }

        Realm realm = root.Realm;
        var promise = Promises.NewPromise(realm);
        root.EvalPromise = promise;
        var order = new List<ModuleRecord>();
        Visit(root, order, new HashSet<ModuleRecord>());
        RunFrom(order, 0, promise);
        return promise;
    }

    private static void RunFrom(List<ModuleRecord> order, int i, PromiseObject promise)
    {
        Agent ag = Agent.Current;
        for (; i < order.Count; i++)
        {
            ModuleRecord m = order[i];
            if (m.Status == ModuleStatus.Errored)
            {
                Promises.Reject(promise, m.Error);
                return;
            }

            if (m.Status == ModuleStatus.Evaluated)
            {
                continue;
            }

            if (m.Status == ModuleStatus.Evaluating)
            {
                // Another evaluation (a cycle member awaiting, or a concurrent import()) is running it: wait for it.
                int next = i;
                m.Waiters.Add(() => RunFrom(order, next, promise));
                return;
            }

            m.Status = ModuleStatus.Evaluating;
            int resume = i + 1;
            m.OnDone = () => RunFrom(order, resume, promise);
            m.Frame!.State = FrameState.SuspendedStart;
            m.Frame.Mode = ResumeMode.Next;
            if (!Step(ag, m))
            {
                return;
            }
        }

        Promises.ResolveWith(promise, default);
    }

    /// <summary>Runs or resumes the module's frame; true when it finished (so the caller continues), false when it awaits or failed.</summary>
    private static bool Step(Agent ag, ModuleRecord m)
    {
        try
        {
            Vm.RunEntry(ag, m.Frame!);
        }
        catch (JsException e)
        {
            Fail(m, e.Value);
            return false;
        }

        if (m.Frame!.State == FrameState.Completed)
        {
            m.Status = ModuleStatus.Evaluated;
            Action[] ws = [.. m.Waiters];
            m.Waiters.Clear();
            m.OnDone = null;
            foreach (Action w in ws)
            {
                ag.EnqueueJob(w);
            }

            return true;
        }

        return false;
    }

    private static void Fail(ModuleRecord m, JsValue err)
    {
        m.Status = ModuleStatus.Errored;
        m.Error = err;
        Action? done = m.OnDone;
        m.OnDone = null;
        Action[] ws = [.. m.Waiters];
        m.Waiters.Clear();
        // Waiters and the driver re-run and observe the Errored status.
        done?.Invoke();
        foreach (Action w in ws)
        {
            w();
        }
    }

    /// <summary>Called when an awaiting module frame is resumed by a job.</summary>
    internal static void ResumeModule(ModuleRecord m)
    {
        Agent ag = Agent.Current;
        Action? done = m.OnDone;
        if (Step(ag, m))
        {
            done?.Invoke();
        }
    }

    // ---- import(), import.meta -----------------------------------------------------------------------

    internal static JsObject ImportMeta(Frame f)
    {
        ModuleRecord m = f.Func?.Module ?? (ModuleRecord)f.Owner!;
        if (m.Meta is null)
        {
            m.Meta = new JsObject((JsObject?)null);
            m.Realm.Agent.Host.InitializeImportMeta(m.Id, m.Meta);
        }

        return m.Meta;
    }

    /// <summary>EvaluateImportCall (13.3.10.1): returns a promise for the namespace.</summary>
    internal static JsValue DynamicImport(Frame f, JsValue specifier, JsValue options)
    {
        Realm realm = f.Realm;
        var promise = Promises.NewPromise(realm);
        try
        {
            string spec = Ops.ToStr(specifier);
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!options.IsUndefined)
            {
                if (options.Ref is not JsObject oo)
                {
                    throw Errors.Type("The second argument of import() must be an object");
                }

                JsValue with = oo.Get("with");
                if (!with.IsUndefined)
                {
                    if (with.Ref is not JsObject wo)
                    {
                        throw Errors.Type("The 'with' option of import() must be an object");
                    }

                    var keys = new List<PropertyKey>();
                    wo.OwnKeys(keys);
                    foreach (PropertyKey k in keys)
                    {
                        if (k.IsSymbol || !wo.GetOwnProperty(k, out PropertyDescriptor d) || !d.Enumerable)
                        {
                            continue;
                        }

                        JsValue v = wo.Get(k);
                        if (!v.IsString)
                        {
                            throw Errors.Type("Import attribute value must be a string");
                        }

                        attrs[k.String!] = v.AsString;
                    }
                }
            }

            ModuleRecord? referrer = f.Func?.Module ?? f.Owner as ModuleRecord;
            HostModule? hm = realm.Agent.Host.ResolveModule(spec, referrer?.Id, attrs);
            if (hm is null)
            {
                throw Errors.Make("Error", "Cannot find module '" + spec + "'" + (referrer is null ? "" : " imported from " + referrer.Id));
            }

            ModuleRecord m = Load(realm, hm);
            Link(m);
            PromiseObject ev = Evaluate(m);
            Promises.ThenNative(ev, _ => Promises.ResolveWith(promise, JsValue.FromObject(GetNamespace(m))), err => Promises.Reject(promise, err));
        }
        catch (JsException e)
        {
            Promises.Reject(promise, e.Value);
        }

        return JsValue.FromObject(promise);
    }
}

/// <summary>Runs classic scripts in a realm (16.1.6 ScriptEvaluation).</summary>
internal static class ScriptRunner
{
    internal static JsValue RunScript(Realm r, string source, string name)
    {
        ParseResult pr = JsParser.Parse(source, new ParseOptions { FileName = name });
        if (pr.HasErrors)
        {
            throw Errors.Syntax(pr.Diagnostics[0].Headline);
        }

        FunctionCode code = Compiler.CompileScript(pr.Program, pr.Source);
        var f = new Frame(code, r) { Env = null, This = JsValue.FromObject(r.Global) };
        return Vm.RunEntry(r.Agent, f);
    }
}
