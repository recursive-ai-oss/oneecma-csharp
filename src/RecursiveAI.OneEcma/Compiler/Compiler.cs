using RecursiveAI.OneEcma.Syntax;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma;

/// <summary>A compile-time mirror of one runtime environment (or of a scope that needs none).</summary>
internal sealed class CScope
{
    internal CScope? Parent;
    internal Scope? Ast;
    internal ScopeInfo? Info;
    internal Dictionary<string, int>? Slots;
    internal bool IsWith;

    /// <summary>Names can appear at run time (sloppy direct eval declares vars here).</summary>
    internal bool DynamicNames;
    internal bool IsFunctionTop;
    internal bool IsStrict;
    internal ScopeKind Kind;
}

internal enum RefKind
{
    Local,
    Global,
    Dynamic,
}

internal struct Ref
{
    internal RefKind Kind;
    internal int Hops;
    internal int Slot;
    internal byte BindingKind;
    internal string Name;
}

internal sealed class JumpLabel
{
    internal int Pc = -1;
    internal List<int>? Fixups;
}

/// <summary>An entry of the compile-time control stack: what a jump out of the current position must undo first.</summary>
internal sealed class Control
{
    internal enum K
    {
        Loop,
        Block,
        Scope,
        Try,
        Finally,
        IterLoop,
        ForIn,
        Switch,
        With,
        IterRest,
    }

    internal K Kind;
    internal List<string>? Labels;
    internal JumpLabel? Break;
    internal JumpLabel? Continue;
    internal BlockStatement? Finalizer;
    internal bool Async;
    internal int StackItems;

    /// <summary>Compile-time stack depth right after the iterator record of a for-of loop was pushed.</summary>
    internal int RecordDepth;
    internal int DoneTemp = -1;
}

/// <summary>Compiles the syntax tree to bytecode (ECMA-262 semantics are cited where the code encodes a rule that is not obvious).</summary>
internal sealed partial class Compiler
{
    private readonly SourceText _source;
    private FnState _f = null!;
    private CScope? _scope;
    private readonly bool _evalUnit;

    private Compiler(SourceText source, bool evalUnit)
    {
        _source = source;
        _evalUnit = evalUnit;
    }

    /// <summary>Per-function emission state.</summary>
    private sealed class FnState
    {
        internal List<Instr> Ops = new(64);
        internal List<int> SpanS = new(64);
        internal List<int> SpanE = new(64);
        internal List<int> PosL = new(64);
        internal int CurP = -1;
        internal int ValS = -1;
        internal int ValE;
        internal Dictionary<int, (int, int)>? ValSpans;
        internal int StmtStart = -1;
        internal List<JsValue> Consts = [];
        internal Dictionary<object, int> ConstIndex = new();
        internal List<string> Names = [];
        internal Dictionary<string, int> NameIndex = new(StringComparer.Ordinal);
        internal List<FunctionCode> Funcs = [];
        internal List<ScopeInfo> Scopes = [];
        internal List<object> Tables = [];
        internal int CacheCount;
        internal int NumLocals;
        internal Stack<int> FreeTemps = new();
        internal int Depth;
        internal int MaxDepth;
        internal List<Control> Controls = [];
        internal FunctionKind Kind;
        internal bool Strict;
        internal bool IsAsync;
        internal bool IsGenerator;
        internal FnState? Outer;
        internal int CompletionTemp = -1;
        internal int CurS;
        internal int CurE;
        internal bool HasAwait;
        internal CScope? FunctionTopScope;
        internal bool InClassFieldInit;
    }

    // ---- entry points ------------------------------------------------------------------------

    /// <summary>Compiles a script (ECMA-262 16.1.7 GlobalDeclarationInstantiation is performed by the DeclareGlobals instruction).</summary>
    internal static FunctionCode CompileScript(Syntax.Program program, SourceText source)
    {
        var c = new Compiler(source, false);
        return c.RunScript(program);
    }

    /// <summary>
    /// Compiles the result of parsing with <see cref="Syntax.ParseGoal.AsyncFunctionBody"/> as a script whose value is the async function
    /// that runs the body; the caller calls it.
    /// </summary>
    internal static FunctionCode CompileAsyncBody(Syntax.ParseResult parsed)
    {
        Syntax.FunctionNode fn = parsed.AsyncBody ?? throw new InvalidOperationException("The parse result is not an async function body.");
        Syntax.Program p = parsed.Program;
        var wrapper = new Syntax.Program(p.Start, p.End, [new Syntax.ExpressionStatement(fn.Start, fn.End, fn, null)], false, false, p.Scope);
        var c = new Compiler(parsed.Source, false);
        return c.RunScript(wrapper);
    }

    /// <summary>Compiles eval code; <paramref name="direct"/> is true for a direct eval call.</summary>
    internal static FunctionCode CompileEval(Syntax.Program program, SourceText source, bool direct)
    {
        var c = new Compiler(source, true);
        return c.RunEval(program, direct);
    }

    internal static FunctionCode CompileModule(Syntax.Program program, SourceText source, string moduleId)
    {
        var c = new Compiler(source, false);
        return c.RunModule(program, moduleId);
    }

    // ---- emission helpers --------------------------------------------------------------------

    private int Emit(Op op, int a = 0, int b = 0)
    {
        FnState f = _f;
        f.Ops.Add(new Instr { Op = op, A = a, B = b });
        f.SpanS.Add(f.CurS);
        f.SpanE.Add(f.CurE);
        f.PosL.Add(f.CurP >= 0 ? f.CurP : f.CurS);
        if (f.ValS >= 0 && op is Op.InitSlot or Op.StoreSlot or Op.StoreSlotChecked or Op.InitGlobalLexical or Op.StoreGlobal or Op.DefineField or Op.SetProp or Op.SetElem)
        {
            // Where the stored value was written, so a traced replay can say where a binding or property got its value.
            (f.ValSpans ??= new())[f.Ops.Count - 1] = (f.ValS, f.ValE);
        }
        AdjustDepth(op, a, b);
        return f.Ops.Count - 1;
    }

    private void AdjustDepth(Op op, int a, int b)
    {
        int d = Effect(op, a, b);
        _f.Depth += d;
        if (_f.Depth > _f.MaxDepth)
        {
            _f.MaxDepth = _f.Depth;
        }

        if (_f.Depth < 0)
        {
            _f.Depth = 0;
        }
    }

    private static int Effect(Op op, int a, int b)
    {
        switch (op)
        {
            case Op.PushUndef: case Op.PushNull: case Op.PushTrue: case Op.PushFalse: case Op.PushInt: case Op.PushConst:
            case Op.Dup: case Op.DupUnder: case Op.LoadLocal: case Op.LoadSlot: case Op.LoadSlotChecked: case Op.LoadImport:
            case Op.LoadGlobal: case Op.TypeofGlobal: case Op.LoadDyn: case Op.TypeofDyn: case Op.DeleteDyn:
            case Op.LoadThis: case Op.LoadThisChecked: case Op.LoadNewTarget: case Op.LoadFunction: case Op.LoadArg:
            case Op.NewObject: case Op.NewArray: case Op.NewRegExp: case Op.TemplateObject: case Op.MakeClosure: case Op.ImportMeta:
            case Op.CreateArguments: case Op.CollectRest: case Op.GetMethod:
            case Op.GetIterator: case Op.GetAsyncIterator: case Op.IterNext: case Op.IterStep: case Op.IterRest: case Op.ForInNext:
            case Op.AsyncIterNextStart: case Op.AsyncIterCloseStart: case Op.GetSuper: case Op.ResolveDynRef:
                return 1;
            case Op.Dup2: case Op.LoadDynCall:
                return 2;
            case Op.Pop: case Op.StoreLocal: case Op.StoreSlot: case Op.StoreSlotChecked: case Op.InitSlot: case Op.StoreGlobal:
            case Op.InitGlobalLexical: case Op.DefineGlobalFunction: case Op.StoreDyn: case Op.StoreVarDyn: case Op.InitDyn: case Op.PushWith:
            case Op.JmpIfFalse: case Op.JmpIfTrue: case Op.Throw: case Op.Return: case Op.ThrowIfNotObject:
            case Op.ArrayPush: case Op.ArraySpread: case Op.SetProtoLiteral: case Op.CopyDataProps: case Op.DefineField:
            case Op.Add: case Op.Sub: case Op.Mul: case Op.Div: case Op.Mod: case Op.Exp: case Op.BitAnd: case Op.BitOr: case Op.BitXor:
            case Op.Shl: case Op.Shr: case Op.UShr: case Op.Eq: case Op.Ne: case Op.StrictEq: case Op.StrictNe: case Op.Lt: case Op.Le: case Op.Gt: case Op.Ge:
            case Op.In: case Op.InstanceOf: case Op.GetElem: case Op.DeleteElem: case Op.TemplateConcat: case Op.SetProp: case Op.PrivateGet: case Op.PrivateIn:
            case Op.SetSuperElem: case Op.JmpIfFalseKeep: case Op.JmpIfTrueKeep: case Op.JmpIfNotNullishKeep: case Op.JmpIfNotUndefKeep:
            case Op.NewSpread: case Op.ObjectRest: case Op.RunStaticBlock:
                return -1;
            case Op.SetElem: case Op.DefineFieldComputed: case Op.CallSpread: case Op.CallEvalSpread: case Op.PrivateSet: case Op.JmpIfStrictEq:
                return -2;
            case Op.DefineClassField: case Op.AddPrivateMethod:
                return -3;
            case Op.DefineMethodOp: return a < 0 ? -2 : -1;
            case Op.Call: case Op.TailCall: case Op.TailCallEval: case Op.CallEval: return -(a + 1);
            case Op.New: return -a;
            case Op.SuperCall: return a < 0 ? 1 : 1 - a;
            case Op.NewClass: return (b & 1) != 0 ? 1 : 2;
            case Op.YieldStar: return -2;
            case Op.PutDynRef: return (b & 2) != 0 ? -1 : -2;
            case Op.ImportCallOp: return a == 1 ? -1 : 0;
            default: return 0;
        }
    }

    private static JumpLabel NewLabel() => new();

    private void Mark(JumpLabel l)
    {
        l.Pc = _f.Ops.Count;
        if (l.Fixups is not null)
        {
            foreach (int at in l.Fixups)
            {
                Instr i = _f.Ops[at];
                i.A = l.Pc;
                _f.Ops[at] = i;
            }

            l.Fixups = null;
        }
    }

    private void Jump(Op op, JumpLabel l, int b = 0)
    {
        if (l.Pc >= 0)
        {
            Emit(op, l.Pc, b);
            return;
        }

        int at = Emit(op, 0, b);
        (l.Fixups ??= []).Add(at);
    }

    private int AddConst(JsValue v)
    {
        object key = v.IsNumber ? v.Num : v.Ref is string s ? "s:" + s : v.IsBigInt ? v.Ref! : new object();
        if (v.IsNumber && v.Num == 0 && double.IsNegative(v.Num))
        {
            key = "negzero";
        }

        if (_f.ConstIndex.TryGetValue(key, out int idx))
        {
            return idx;
        }

        _f.Consts.Add(v);
        idx = _f.Consts.Count - 1;
        _f.ConstIndex[key] = idx;
        return idx;
    }

    private int NameConst(string name)
    {
        if (_f.NameIndex.TryGetValue(name, out int i))
        {
            return i;
        }

        _f.Names.Add(name);
        _f.NameIndex[name] = _f.Names.Count - 1;
        return _f.Names.Count - 1;
    }

    private int NewCache() => _f.CacheCount++;

    private int AddTable(object o)
    {
        _f.Tables.Add(o);
        return _f.Tables.Count - 1;
    }

    private int NewTemp()
    {
        if (_f.FreeTemps.Count > 0)
        {
            return _f.FreeTemps.Pop();
        }

        return _f.NumLocals++;
    }

    private void FreeTemp(int t) => _f.FreeTemps.Push(t);

    private void SetSpan(Node n)
    {
        _f.CurS = n.Start;
        _f.CurE = n.End;
        _f.CurP = -1;
    }

    private void SetSpan(int s, int e)
    {
        _f.CurS = s;
        _f.CurE = e;
        _f.CurP = -1;
    }

    /// <summary>
    /// The offset of the operator that follows an operand ending at <paramref name="from"/>: stack traces point at the operator of a
    /// binary operation or an assignment, not at its first operand.
    /// </summary>
    private int OperatorPos(int from)
    {
        string t = _source.Text;
        int i = from;
        while (i < t.Length)
        {
            char c = t[i];
            if (c is ' ' or '\t' or '\r' or '\n' or ')' or '\u00A0' or '\u2028' or '\u2029')
            {
                i++;
            }
            else if (c == '/' && i + 1 < t.Length && t[i + 1] == '/')
            {
                while (i < t.Length && t[i] is not ('\n' or '\r'))
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < t.Length && t[i + 1] == '*')
            {
                int end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? t.Length : end + 2;
            }
            else
            {
                return i;
            }
        }

        return from;
    }

    private void PushNumber(double d)
    {
        if (d == Math.Floor(d) && Math.Abs(d) < 1_000_000_000 && !(d == 0 && double.IsNegative(d)))
        {
            Emit(Op.PushInt, (int)d);
        }
        else
        {
            Emit(Op.PushConst, AddConst(JsValue.FromNumber(d)));
        }
    }

    private void PushString(string s) => Emit(Op.PushConst, AddConst(JsValue.FromString(s)));

    // ---- scopes -------------------------------------------------------------------------------

    private static byte KindOf(Binding b, ScopeKind scopeKind) => b.Kind switch
    {
        BindingKind.Let => ScopeInfo.KLet,
        BindingKind.Class => scopeKind == ScopeKind.Class ? ScopeInfo.KConst : ScopeInfo.KLet,
        BindingKind.Const => ScopeInfo.KConst,
        BindingKind.Import => ScopeInfo.KImport,
        _ => ScopeInfo.KVar,
    };

    /// <summary>Builds the runtime layout for an AST scope; returns null when the scope needs no environment.</summary>
    private CScope? EnterScope(Scope? ast, bool functionTop = false, IEnumerable<string>? extraNames = null, bool forceMaterialize = false, bool paramsTdz = false, Predicate<Binding>? filter = null)
    {
        if (ast is null && extraNames is null && !forceMaterialize)
        {
            return null;
        }

        var names = new List<string>();
        var kinds = new List<byte>();
        var tdz = new List<bool>();
        if (ast is not null)
        {
            foreach (Binding b in ast.Bindings)
            {
                if (filter is not null && !filter(b))
                {
                    continue;
                }

                if (names.Contains(b.Name))
                {
                    continue;
                }

                names.Add(b.Name);
                byte k = KindOf(b, ast.Kind);
                if (b.Kind == BindingKind.Parameter && paramsTdz)
                {
                    // Parameters are in their temporal dead zone until initialized, so default-value code reads them checked (10.2.11 step 21).
                    k = ScopeInfo.KLet;
                }

                kinds.Add(k);
                tdz.Add(k is ScopeInfo.KLet or ScopeInfo.KConst || (b.Kind == BindingKind.Parameter && paramsTdz));
            }
        }

        if (extraNames is not null)
        {
            foreach (string n in extraNames)
            {
                if (!names.Contains(n))
                {
                    names.Add(n);
                    bool hiddenDefault = n == "*default*"; // the binding of `export default <expression>` starts uninitialized
                    kinds.Add(n.StartsWith('#') ? ScopeInfo.KConst : hiddenDefault ? ScopeInfo.KLet : ScopeInfo.KVar);
                    tdz.Add(hiddenDefault);
                }
            }
        }

        bool dynamic = ast is not null && ast.ContainsDirectEval && !ast.IsStrict && ast.IsVarScope;
        if (names.Count == 0 && !forceMaterialize && !(dynamic && functionTop))
        {
            // No bindings: nothing to materialize, but the compile-time chain still needs to know about
            // `with`-free transparent scopes only through their parent.
            return null;
        }

        var info = new ScopeInfo
        {
            Names = [.. names],
            Kinds = [.. kinds],
            IsFunctionVarScope = ast is not null && ast.IsVarScope,
            IsStrict = ast?.IsStrict ?? false,
        };
        info.Template = new JsValue[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            if (tdz[i])
            {
                info.Template[i] = JsValue.Tdz;
            }
        }

        var cs = new CScope
        {
            Parent = _scope, Ast = ast, Info = info, Slots = new Dictionary<string, int>(names.Count, StringComparer.Ordinal),
            IsFunctionTop = functionTop, DynamicNames = dynamic, IsStrict = ast?.IsStrict ?? false, Kind = ast?.Kind ?? ScopeKind.Block,
        };
        for (int i = 0; i < names.Count; i++)
        {
            cs.Slots[names[i]] = i;
        }

        _f.Scopes.Add(info);
        _scope = cs;
        Emit(Op.PushScope, _f.Scopes.Count - 1);
        return cs;
    }

    private void LeaveScope(CScope? cs)
    {
        if (cs is null)
        {
            return;
        }

        _scope = cs.Parent;
        Emit(Op.PopScope);
    }

    private Ref Resolve(string name, CScope? start = null, CScope? skipUntil = null)
    {
        bool crossedDynamic = false;
        bool skipping = skipUntil is not null;
        int hops = 0;
        for (CScope? cs = start ?? _scope; cs is not null; cs = cs.Parent)
        {
            if (skipping && ReferenceEquals(cs, skipUntil))
            {
                skipping = false;
            }

            if (cs.IsWith)
            {
                if (!skipping)
                {
                    crossedDynamic = true;
                }

                hops++;
                continue;
            }

            if (cs.Info is not null)
            {
                if (!skipping && cs.Slots!.TryGetValue(name, out int slot))
                {
                    if (crossedDynamic)
                    {
                        return new Ref { Kind = RefKind.Dynamic, Name = name };
                    }

                    return new Ref { Kind = RefKind.Local, Hops = hops, Slot = slot, BindingKind = cs.Info.Kinds[slot], Name = name };
                }

                hops++;
            }

            if (cs.DynamicNames && !skipping)
            {
                crossedDynamic = true;
            }
        }

        if (_evalUnit || crossedDynamic || _unknownParent)
        {
            return new Ref { Kind = RefKind.Dynamic, Name = name };
        }

        return new Ref { Kind = RefKind.Global, Name = name };
    }

    private bool _unknownParent;

    private FunctionCode Finish(FnState f, string name, int length, int srcStart, int srcEnd)
    {
        var code = new FunctionCode
        {
            Ops = [.. f.Ops],
            SpanStart = [.. f.SpanS],
            SpanEnd = [.. f.SpanE],
            Pos = [.. f.PosL],
            ValueSpans = f.ValSpans,
            Consts = [.. f.Consts],
            Names = [.. f.Names],
            Functions = [.. f.Funcs],
            Scopes = [.. f.Scopes],
            Tables = [.. f.Tables],
            Caches = new PropCache[f.CacheCount],
            Name = name,
            Length = length,
            Strict = f.Strict,
            IsAsync = f.IsAsync,
            IsGenerator = f.IsGenerator,
            Kind = f.Kind,
            NumLocals = f.NumLocals,
            StackSize = f.MaxDepth + 8,
            Source = _source,
            SrcStart = srcStart,
            SrcEnd = srcEnd,
        };
        for (int i = 0; i < code.Caches.Length; i++)
        {
            code.Caches[i] = new PropCache();
        }

        return code;
    }
}
