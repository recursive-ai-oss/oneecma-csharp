using System.Runtime.CompilerServices;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.ExecutorSpike.Closure;

internal enum Cmp
{
    Normal,
    Break,
    Continue,
    Return,
}

/// <summary>One JavaScript activation. Frames are pooled per call depth: closures capture <see cref="Env"/>, never a frame.</summary>
internal sealed class Frame
{
    internal JsValue[] R = new JsValue[16];
    internal Env? Env;
    internal JsValue This;
    internal JsValue Ret;
    internal JsValue Completion;
    internal Realm Realm = null!;
}

internal abstract class XNode
{
    internal int Start;
    internal int End;

    internal abstract JsValue Eval(Frame f);

    internal virtual bool EvalBool(Frame f) => Conv.ToBoolean(Eval(f));
}

internal abstract class SNode
{
    internal int Start;
    internal int End;

    internal abstract Cmp Exec(Frame f);
}

internal readonly record struct HoistA(bool InEnv, int Slot, FuncCodeA Code);

/// <summary>What entering a scope does: allocate its environment, mark dead-zone bindings and create hoisted functions.</summary>
internal sealed class ScopeEntry
{
    internal int EnvSize;
    internal int[] TdzEnvSlots = [];
    internal int[] TdzRegs = [];
    internal HoistA[] Hoists = [];

    internal bool IsEmpty => EnvSize == 0 && TdzRegs.Length == 0 && Hoists.Length == 0;

    internal void Enter(Frame f)
    {
        if (EnvSize > 0)
        {
            var e = new Env(f.Env, EnvSize);
            foreach (int s in TdzEnvSlots)
            {
                e.Slots[s] = JsValue.Tdz;
            }

            f.Env = e;
        }

        foreach (int r in TdzRegs)
        {
            f.R[r] = JsValue.Tdz;
        }

        foreach (HoistA h in Hoists)
        {
            JsValue fn = JsValue.FromObject(new ScriptFunctionA(f.Realm, h.Code, f.Env, f.This));
            if (h.InEnv)
            {
                f.Env!.Slots[h.Slot] = fn;
            }
            else
            {
                f.R[h.Slot] = fn;
            }
        }
    }

    internal void Exit(Frame f)
    {
        if (EnvSize > 0)
        {
            f.Env = f.Env!.Parent;
        }
    }
}

internal sealed class FuncCodeA
{
    internal string Name = string.Empty;
    internal int NParams;
    internal int NumRegs;
    internal bool IsArrow;
    internal bool IsCtor;
    internal ScopeEntry Entry = new();
    internal (int Reg, int Slot)[] CapturedParams = [];
    internal SNode[] Body = [];
    internal XNode? ExprBody;
    internal string Source = string.Empty;
}

internal sealed class ScriptFunctionA : JsFunction
{
    internal readonly FuncCodeA Code;
    internal readonly Env? Env;
    internal readonly JsValue BoundThis;

    internal ScriptFunctionA(Realm realm, FuncCodeA code, Env? env, JsValue boundThis)
        : base(realm.FunctionProto, code.Name)
    {
        Code = code;
        Env = env;
        BoundThis = boundThis;
    }

    internal override bool IsConstructor => Code.IsCtor;
}

internal static class Calls
{
    internal static JsValue CallScript(Realm r, ScriptFunctionA fn, JsValue thisV, int argBase, int argc)
    {
        if (--r.Fuel < 0)
        {
            throw Realm.OutOfFuel();
        }

        int d = r.Depth + 1;
        if (d > r.MaxDepth || ((d & 7) == 0 && !RuntimeHelpers.TryEnsureSufficientExecutionStack()))
        {
            throw Realm.TooDeep();
        }

        r.Depth = d;
        FuncCodeA c = fn.Code;
        Frame nf = r.Frames[d] ?? NewFrame(r, d);
        JsValue[] regs = nf.R;
        if (regs.Length < c.NumRegs)
        {
            regs = nf.R = new JsValue[Math.Max(c.NumRegs, 16)];
        }
        else
        {
            Array.Clear(regs, 0, c.NumRegs);
        }

        int n = argc < c.NParams ? argc : c.NParams;
        JsValue[] args = r.ArgStack;
        for (int i = 0; i < n; i++)
        {
            regs[i] = args[argBase + i];
        }

        nf.This = c.IsArrow ? fn.BoundThis : thisV;
        nf.Env = fn.Env;
        nf.Ret = default;
        ScopeEntry entry = c.Entry;
        if (!entry.IsEmpty)
        {
            entry.Enter(nf);
            foreach ((int reg, int slot) in c.CapturedParams)
            {
                nf.Env!.Slots[slot] = regs[reg];
            }
        }

        JsValue result;
        if (c.ExprBody is { } eb)
        {
            result = eb.Eval(nf);
        }
        else
        {
            SNode[] body = c.Body;
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i].Exec(nf) == Cmp.Return)
                {
                    break;
                }
            }

            result = nf.Ret;
        }

        r.Depth = d - 1;
        return result;
    }

    private static Frame NewFrame(Realm r, int d)
    {
        var f = new Frame { Realm = r };
        r.Frames[d] = f;
        return f;
    }

    internal static JsValue Invoke(Realm r, JsValue fv, JsValue thisV, int argBase, int argc, XNode calleeNode, string source)
    {
        if (fv.Ref is ScriptFunctionA sf)
        {
            return CallScript(r, sf, thisV, argBase, argc);
        }

        if (fv.Ref is NativeFunction nf)
        {
            if (--r.Fuel < 0)
            {
                throw Realm.OutOfFuel();
            }

            return nf.Fn(thisV, new ReadOnlySpan<JsValue>(r.ArgStack, argBase, argc));
        }

        throw r.TypeError(string.Concat(source.AsSpan(calleeNode.Start, calleeNode.End - calleeNode.Start), " is not a function"));
    }

    internal static JsValue Construct(Realm r, JsValue fv, int argBase, int argc, XNode calleeNode, string source)
    {
        if (fv.Ref is ScriptFunctionA sf && sf.IsConstructor)
        {
            sf.EnsurePrototype(r);
            var obj = new JsObject(sf.PrototypeProperty, 4);
            JsValue res = CallScript(r, sf, JsValue.FromObject(obj), argBase, argc);
            return res.IsObject ? res : JsValue.FromObject(obj);
        }

        if (fv.Ref is NativeFunction nf)
        {
            JsValue res = nf.Fn(default, new ReadOnlySpan<JsValue>(r.ArgStack, argBase, argc));
            if (res.IsObject)
            {
                return res;
            }
        }

        throw r.TypeError(string.Concat(source.AsSpan(calleeNode.Start, calleeNode.End - calleeNode.Start), " is not a constructor"));
    }

    /// <summary>Evaluates arguments onto the shared argument stack; returns the base index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PushArgs(Frame f, XNode[] args)
    {
        Realm r = f.Realm;
        int b = r.Sp;
        if (b + args.Length + 1 > r.ArgStack.Length)
        {
            Array.Resize(ref r.ArgStack, Math.Max(r.ArgStack.Length * 2, b + args.Length + 1));
        }

        for (int i = 0; i < args.Length; i++)
        {
            JsValue v = args[i].Eval(f);
            r.ArgStack[b + i] = v;
            r.Sp = b + i + 1;
        }

        return b;
    }
}

// ---- expressions -------------------------------------------------------------------------

internal sealed class ConstNode(JsValue value) : XNode
{
    internal readonly JsValue Value = value;

    internal override JsValue Eval(Frame f) => Value;
}

internal sealed class ThisNode : XNode
{
    internal override JsValue Eval(Frame f) => f.This;
}

internal sealed class RegGet(int reg) : XNode
{
    internal int Reg => reg;

    internal override JsValue Eval(Frame f) => f.R[reg];
}

internal sealed class RegGetTdz(int reg, string name) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue v = f.R[reg];
        if (v.IsTdz)
        {
            throw f.Realm.ReferenceError($"Cannot access '{name}' before initialization");
        }

        return v;
    }
}

internal sealed class EnvGet(int hops, int slot, bool tdz, string name) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        Env e = f.Env!;
        for (int h = hops; h > 0; h--)
        {
            e = e.Parent!;
        }

        JsValue v = e.Slots[slot];
        if (tdz && v.IsTdz)
        {
            throw f.Realm.ReferenceError($"Cannot access '{name}' before initialization");
        }

        return v;
    }
}

internal sealed class GlobalGet(string name) : XNode
{
    private Shape? _shape;
    private int _slot;

    internal override JsValue Eval(Frame f)
    {
        JsObject g = f.Realm.Global;
        if (ReferenceEquals(g.Shape, _shape))
        {
            return g.Slots[_slot];
        }

        int slot = g.Shape.Lookup(name);
        if (slot < 0)
        {
            throw f.Realm.ReferenceError(name + " is not defined");
        }

        _shape = g.Shape;
        _slot = slot;
        return g.Slots[slot];
    }
}

/// <summary>Writes a variable: registers, environment slots and globals behind one shape.</summary>
internal abstract class VarRef
{
    internal abstract JsValue Get(Frame f);

    internal abstract void Set(Frame f, JsValue v);

    /// <summary>Initialisation by a declaration: no dead-zone or const check.</summary>
    internal abstract void Init(Frame f, JsValue v);
}

internal sealed class RegRef(int reg, bool tdz, bool isConst, string name) : VarRef
{
    internal int Reg => reg;

    internal bool Plain => !tdz && !isConst;

    internal override JsValue Get(Frame f)
    {
        JsValue v = f.R[reg];
        if (tdz && v.IsTdz)
        {
            throw f.Realm.ReferenceError($"Cannot access '{name}' before initialization");
        }

        return v;
    }

    internal override void Set(Frame f, JsValue v)
    {
        if (tdz && f.R[reg].IsTdz)
        {
            throw f.Realm.ReferenceError($"Cannot access '{name}' before initialization");
        }

        if (isConst)
        {
            throw f.Realm.TypeError("Assignment to constant variable.");
        }

        f.R[reg] = v;
    }

    internal override void Init(Frame f, JsValue v) => f.R[reg] = v;
}

internal sealed class EnvRef(int hops, int slot, bool tdz, bool isConst, string name) : VarRef
{
    private Env Target(Frame f)
    {
        Env e = f.Env!;
        for (int h = hops; h > 0; h--)
        {
            e = e.Parent!;
        }

        return e;
    }

    internal override JsValue Get(Frame f)
    {
        JsValue v = Target(f).Slots[slot];
        if (tdz && v.IsTdz)
        {
            throw f.Realm.ReferenceError($"Cannot access '{name}' before initialization");
        }

        return v;
    }

    internal override void Set(Frame f, JsValue v)
    {
        Env e = Target(f);
        if (tdz && e.Slots[slot].IsTdz)
        {
            throw f.Realm.ReferenceError($"Cannot access '{name}' before initialization");
        }

        if (isConst)
        {
            throw f.Realm.TypeError("Assignment to constant variable.");
        }

        e.Slots[slot] = v;
    }

    internal override void Init(Frame f, JsValue v) => Target(f).Slots[slot] = v;
}

internal sealed class GlobalRef(string name) : VarRef
{
    internal override JsValue Get(Frame f)
    {
        JsObject g = f.Realm.Global;
        int slot = g.Shape.Lookup(name);
        if (slot < 0)
        {
            throw f.Realm.ReferenceError(name + " is not defined");
        }

        return g.Slots[slot];
    }

    internal override void Set(Frame f, JsValue v) => f.Realm.Global.Set(name, v);

    internal override void Init(Frame f, JsValue v) => f.Realm.Global.Set(name, v);
}

internal sealed class VarGetNode(VarRef r) : XNode
{
    internal override JsValue Eval(Frame f) => r.Get(f);
}

internal sealed class RegAssign(int reg, XNode rhs) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue v = rhs.Eval(f);
        f.R[reg] = v;
        return v;
    }
}

internal sealed class VarAssign(VarRef target, XNode rhs) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue v = rhs.Eval(f);
        target.Set(f, v);
        return v;
    }
}

internal sealed class VarCompound(VarRef target, BinaryOp op, XNode rhs) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue old = target.Get(f);
        JsValue v = Ops.Binary(op, old, rhs.Eval(f));
        target.Set(f, v);
        return v;
    }
}

internal sealed class RegAddAssign(int reg, XNode rhs) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue b = rhs.Eval(f);
        JsValue a = f.R[reg];
        JsValue v = a.IsNumber && b.IsNumber ? JsValue.FromNumber(a.Num + b.Num) : Ops.Add(a, b);
        f.R[reg] = v;
        return v;
    }
}

internal sealed class RegUpdate(int reg, double delta, bool prefix) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue a = f.R[reg];
        double old = a.IsNumber ? a.Num : Conv.ToNumber(a);
        JsValue nv = JsValue.FromNumber(old + delta);
        f.R[reg] = nv;
        return prefix ? nv : JsValue.FromNumber(old);
    }
}

internal sealed class VarUpdate(VarRef target, double delta, bool prefix) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        double old = Conv.ToNumber(target.Get(f));
        JsValue nv = JsValue.FromNumber(old + delta);
        target.Set(f, nv);
        return prefix ? nv : JsValue.FromNumber(old);
    }
}

internal sealed class BinaryNode(BinaryOp op, XNode l, XNode r) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue a = l.Eval(f);
        return Ops.Binary(op, a, r.Eval(f));
    }
}

internal sealed class AddNode(XNode l, XNode r) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue a = l.Eval(f);
        JsValue b = r.Eval(f);
        return a.IsNumber && b.IsNumber ? JsValue.FromNumber(a.Num + b.Num) : Ops.Add(a, b);
    }
}

internal sealed class SubNode(XNode l, XNode r) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue a = l.Eval(f);
        JsValue b = r.Eval(f);
        return a.IsNumber && b.IsNumber ? JsValue.FromNumber(a.Num - b.Num) : JsValue.FromNumber(Conv.ToNumber(a) - Conv.ToNumber(b));
    }
}

internal sealed class MulNode(XNode l, XNode r) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue a = l.Eval(f);
        JsValue b = r.Eval(f);
        return a.IsNumber && b.IsNumber ? JsValue.FromNumber(a.Num * b.Num) : JsValue.FromNumber(Conv.ToNumber(a) * Conv.ToNumber(b));
    }
}

internal sealed class RemNode(XNode l, XNode r) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue a = l.Eval(f);
        JsValue b = r.Eval(f);
        return JsValue.FromNumber((a.IsNumber ? a.Num : Conv.ToNumber(a)) % (b.IsNumber ? b.Num : Conv.ToNumber(b)));
    }
}

/// <summary>Add/Sub with a register on the left and a constant on the right: the shape of <c>i + 1</c> and <c>n - 1</c>.</summary>
internal sealed class RegConstArith(int reg, double k, bool add) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue a = f.R[reg];
        double x = a.IsNumber ? a.Num : Conv.ToNumber(a);
        return JsValue.FromNumber(add ? x + k : x - k);
    }
}

internal enum CmpKind
{
    Lt,
    Le,
    Gt,
    Ge,
    StrictEq,
    StrictNe,
}

internal sealed class CompareNode(CmpKind kind, XNode l, XNode r) : XNode
{
    internal override JsValue Eval(Frame f) => JsValue.FromBool(EvalBool(f));

    internal override bool EvalBool(Frame f)
    {
        JsValue a = l.Eval(f);
        JsValue b = r.Eval(f);
        if (a.IsNumber && b.IsNumber)
        {
            double x = a.Num;
            double y = b.Num;
            switch (kind)
            {
                case CmpKind.Lt: return x < y;
                case CmpKind.Le: return x <= y;
                case CmpKind.Gt: return x > y;
                case CmpKind.Ge: return x >= y;
                case CmpKind.StrictEq: return x == y;
                default: return x != y;
            }
        }

        switch (kind)
        {
            case CmpKind.Lt: return Ops.LessThan(a, b);
            case CmpKind.Le: return Ops.LessEq(a, b);
            case CmpKind.Gt: return Ops.GreaterThan(a, b);
            case CmpKind.Ge: return Ops.GreaterEq(a, b);
            case CmpKind.StrictEq: return Ops.StrictEquals(a, b);
            default: return !Ops.StrictEquals(a, b);
        }
    }
}

/// <summary><c>reg &lt; constant</c> and friends, the loop-test shape.</summary>
internal sealed class RegConstCompare(CmpKind kind, int reg, double k) : XNode
{
    internal override JsValue Eval(Frame f) => JsValue.FromBool(EvalBool(f));

    internal override bool EvalBool(Frame f)
    {
        JsValue a = f.R[reg];
        double x = a.IsNumber ? a.Num : Conv.ToNumber(a);
        switch (kind)
        {
            case CmpKind.Lt: return x < k;
            case CmpKind.Le: return x <= k;
            case CmpKind.Gt: return x > k;
            case CmpKind.Ge: return x >= k;
            case CmpKind.StrictEq: return a.IsNumber && x == k;
            default: return !(a.IsNumber && x == k);
        }
    }
}

internal sealed class LogicalNode(LogicalOp op, XNode l, XNode r) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue a = l.Eval(f);
        switch (op)
        {
            case LogicalOp.And: return Conv.ToBoolean(a) ? r.Eval(f) : a;
            case LogicalOp.Or: return Conv.ToBoolean(a) ? a : r.Eval(f);
            default: return a.IsNullish ? r.Eval(f) : a;
        }
    }

    internal override bool EvalBool(Frame f)
    {
        switch (op)
        {
            case LogicalOp.And: return l.EvalBool(f) && r.EvalBool(f);
            case LogicalOp.Or: return l.EvalBool(f) || r.EvalBool(f);
            default: return Conv.ToBoolean(Eval(f));
        }
    }
}

/// <summary>A long left-leaning operator chain evaluated with a loop, so source length cannot become host stack depth.</summary>
internal sealed class ChainNode(XNode first, (bool Logical, BinaryOp B, LogicalOp L, XNode Rhs)[] steps) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue acc = first.Eval(f);
        foreach ((bool logical, BinaryOp b, LogicalOp l, XNode rhs) in steps)
        {
            if (!logical)
            {
                acc = Ops.Binary(b, acc, rhs.Eval(f));
                continue;
            }

            switch (l)
            {
                case LogicalOp.And:
                    if (Conv.ToBoolean(acc))
                    {
                        acc = rhs.Eval(f);
                    }

                    break;
                case LogicalOp.Or:
                    if (!Conv.ToBoolean(acc))
                    {
                        acc = rhs.Eval(f);
                    }

                    break;
                default:
                    if (acc.IsNullish)
                    {
                        acc = rhs.Eval(f);
                    }

                    break;
            }
        }

        return acc;
    }
}

internal sealed class UnaryNode(UnaryOp op, XNode arg) : XNode
{
    internal override JsValue Eval(Frame f) => Ops.Unary(op, arg.Eval(f));

    internal override bool EvalBool(Frame f) => op == UnaryOp.Not ? !arg.EvalBool(f) : Conv.ToBoolean(Eval(f));
}

internal sealed class CondNode(XNode test, XNode a, XNode b) : XNode
{
    internal override JsValue Eval(Frame f) => test.EvalBool(f) ? a.Eval(f) : b.Eval(f);
}

internal sealed class SeqNode(XNode[] items) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue v = default;
        foreach (XNode n in items)
        {
            v = n.Eval(f);
        }

        return v;
    }
}

internal sealed class FuncExprNode(FuncCodeA code) : XNode
{
    internal override JsValue Eval(Frame f) => JsValue.FromObject(new ScriptFunctionA(f.Realm, code, f.Env, f.This));
}

internal sealed class ArrayLitNode(XNode[] items) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsArray a = f.Realm.NewArray(items.Length);
        foreach (XNode n in items)
        {
            a.Push(n.Eval(f));
        }

        return JsValue.FromObject(a);
    }
}

internal sealed class ObjectLitNode(string[] keys, XNode[] values) : XNode
{
    private Shape? _shape;

    internal override JsValue Eval(Frame f)
    {
        JsObject o = f.Realm.NewObject();
        if (_shape is null)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                o.Set(keys[i], values[i].Eval(f));
            }

            _shape = o.Shape;
            return JsValue.FromObject(o);
        }

        var slots = new JsValue[keys.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = values[i].Eval(f);
        }

        o.Slots = slots;
        o.Shape = _shape;
        return JsValue.FromObject(o);
    }
}

/// <summary>Property read with an inline cache for own properties and one-level prototype hits.</summary>
internal sealed class MemberGet(XNode obj, string key) : XNode
{
    private Shape? _s0;
    private int _slot0;
    private Shape? _s1;
    private JsObject? _holder;
    private Shape? _holderShape;
    private int _slot1;

    internal override JsValue Eval(Frame f) => From(f.Realm, obj.Eval(f));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsValue From(Realm realm, JsValue o)
    {
        if (o.Ref is JsObject jo)
        {
            Shape s = jo.Shape;
            if (ReferenceEquals(s, _s0))
            {
                return jo.Slots[_slot0];
            }

            if (ReferenceEquals(s, _s1) && ReferenceEquals(_holder!.Shape, _holderShape))
            {
                return _holder.Slots[_slot1];
            }

            return Slow(realm, jo, o);
        }

        return Ops.GetMember(realm, o, key);
    }

    private JsValue Slow(Realm realm, JsObject jo, JsValue o)
    {
        if (jo is JsFunction fn && key == "prototype")
        {
            fn.EnsurePrototype(realm);
        }

        if (jo is JsArray && key == "length")
        {
            return Ops.GetMember(realm, o, key);
        }

        int slot = jo.Shape.Lookup(key);
        if (slot >= 0)
        {
            _s0 = jo.Shape;
            _slot0 = slot;
            return jo.Slots[slot];
        }

        JsObject? proto = jo.Shape.Proto;
        if (proto is not null && proto is not JsArray)
        {
            int ps = proto.Shape.Lookup(key);
            if (ps >= 0)
            {
                _s1 = jo.Shape;
                _holder = proto;
                _holderShape = proto.Shape;
                _slot1 = ps;
                return proto.Slots[ps];
            }
        }

        return Ops.GetMember(realm, o, key);
    }
}

internal sealed class LengthGet(XNode obj) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue o = obj.Eval(f);
        if (o.Ref is JsArray a)
        {
            return JsValue.FromNumber(a.Length);
        }

        if (o.Ref is string s)
        {
            return JsValue.FromNumber(s.Length);
        }

        return Ops.GetMember(f.Realm, o, "length");
    }
}

internal sealed class IndexGet(XNode obj, XNode index) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue o = obj.Eval(f);
        return Ops.GetIndex(f.Realm, o, index.Eval(f));
    }
}

/// <summary>Property write with inline caches for an existing own property and for the add-property transition.</summary>
internal sealed class MemberSetCore(string key)
{
    private Shape? _s0;
    private int _slot0;
    private Shape? _from;
    private Shape? _to;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Set(Realm realm, JsValue o, JsValue v)
    {
        if (o.Ref is JsObject jo)
        {
            Shape s = jo.Shape;
            if (ReferenceEquals(s, _s0))
            {
                jo.Slots[_slot0] = v;
                return;
            }

            if (ReferenceEquals(s, _from))
            {
                Shape to = _to!;
                int slot = to.Count - 1;
                if (slot >= jo.Slots.Length)
                {
                    Array.Resize(ref jo.Slots, Math.Max(4, jo.Slots.Length * 2));
                }

                jo.Slots[slot] = v;
                jo.Shape = to;
                return;
            }

            SlowSet(realm, jo, v);
            return;
        }

        Ops.SetMember(realm, o, key, v);
    }

    private void SlowSet(Realm realm, JsObject jo, JsValue v)
    {
        Shape before = jo.Shape;
        int slot = before.Lookup(key);
        jo.Set(key, v);
        if (jo is JsArray or JsFunction)
        {
            return;
        }

        if (slot >= 0)
        {
            _s0 = before;
            _slot0 = slot;
        }
        else
        {
            _from = before;
            _to = jo.Shape;
        }
    }
}

internal sealed class MemberAssign(XNode obj, string key, XNode rhs) : XNode
{
    private readonly MemberSetCore _core = new(key);

    internal override JsValue Eval(Frame f)
    {
        JsValue o = obj.Eval(f);
        JsValue v = rhs.Eval(f);
        _core.Set(f.Realm, o, v);
        return v;
    }
}

internal sealed class MemberCompound(XNode obj, string key, BinaryOp op, XNode rhs) : XNode
{
    private readonly MemberGet _get = new(new ConstNode(default), key);
    private readonly MemberSetCore _core = new(key);

    internal override JsValue Eval(Frame f)
    {
        JsValue o = obj.Eval(f);
        JsValue old = _get.From(f.Realm, o);
        JsValue v = Ops.Binary(op, old, rhs.Eval(f));
        _core.Set(f.Realm, o, v);
        return v;
    }
}

internal sealed class MemberUpdate(XNode obj, string key, double delta, bool prefix) : XNode
{
    private readonly MemberGet _get = new(new ConstNode(default), key);
    private readonly MemberSetCore _core = new(key);

    internal override JsValue Eval(Frame f)
    {
        JsValue o = obj.Eval(f);
        double old = Conv.ToNumber(_get.From(f.Realm, o));
        JsValue nv = JsValue.FromNumber(old + delta);
        _core.Set(f.Realm, o, nv);
        return prefix ? nv : JsValue.FromNumber(old);
    }
}

internal sealed class IndexAssign(XNode obj, XNode index, XNode rhs) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue o = obj.Eval(f);
        JsValue i = index.Eval(f);
        JsValue v = rhs.Eval(f);
        Ops.SetIndex(f.Realm, o, i, v);
        return v;
    }
}

internal sealed class IndexCompound(XNode obj, XNode index, BinaryOp op, XNode rhs) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue o = obj.Eval(f);
        JsValue i = index.Eval(f);
        JsValue v = Ops.Binary(op, Ops.GetIndex(f.Realm, o, i), rhs.Eval(f));
        Ops.SetIndex(f.Realm, o, i, v);
        return v;
    }
}

internal sealed class IndexUpdate(XNode obj, XNode index, double delta, bool prefix) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue o = obj.Eval(f);
        JsValue i = index.Eval(f);
        double old = Conv.ToNumber(Ops.GetIndex(f.Realm, o, i));
        JsValue nv = JsValue.FromNumber(old + delta);
        Ops.SetIndex(f.Realm, o, i, nv);
        return prefix ? nv : JsValue.FromNumber(old);
    }
}

internal sealed class CallNode(XNode callee, XNode[] args, string source) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue fv = callee.Eval(f);
        Realm r = f.Realm;
        int b = Calls.PushArgs(f, args);
        JsValue res = Calls.Invoke(r, fv, default, b, args.Length, callee, source);
        r.Sp = b;
        return res;
    }
}

internal sealed class MethodCallNode(XNode obj, MemberGet getter, XNode calleeText, XNode[] args, string source) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        Realm r = f.Realm;
        JsValue o = obj.Eval(f);
        JsValue fv = getter.From(r, o);
        int b = Calls.PushArgs(f, args);
        JsValue res = Calls.Invoke(r, fv, o, b, args.Length, calleeText, source);
        r.Sp = b;
        return res;
    }
}

internal sealed class IndexCallNode(XNode obj, XNode index, XNode calleeText, XNode[] args, string source) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        Realm r = f.Realm;
        JsValue o = obj.Eval(f);
        JsValue fv = Ops.GetIndex(r, o, index.Eval(f));
        int b = Calls.PushArgs(f, args);
        JsValue res = Calls.Invoke(r, fv, o, b, args.Length, calleeText, source);
        r.Sp = b;
        return res;
    }
}

internal sealed class NewNode(XNode callee, XNode[] args, string source) : XNode
{
    internal override JsValue Eval(Frame f)
    {
        JsValue fv = callee.Eval(f);
        Realm r = f.Realm;
        int b = Calls.PushArgs(f, args);
        JsValue res = Calls.Construct(r, fv, b, args.Length, callee, source);
        r.Sp = b;
        return res;
    }
}

// ---- statements ---------------------------------------------------------------------------

internal sealed class ExprStmt(XNode e) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        e.Eval(f);
        return Cmp.Normal;
    }
}

internal sealed class CompletionStmt(XNode e) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        f.Completion = e.Eval(f);
        return Cmp.Normal;
    }
}

internal sealed class InitStmt(VarRef target, XNode? init) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        target.Init(f, init is null ? default : init.Eval(f));
        return Cmp.Normal;
    }
}

internal sealed class RegInitStmt(int reg, XNode init) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        f.R[reg] = init.Eval(f);
        return Cmp.Normal;
    }
}

internal sealed class EmptyStmt : SNode
{
    internal override Cmp Exec(Frame f) => Cmp.Normal;
}

internal sealed class BlockStmt(ScopeEntry? entry, SNode[] body) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        entry?.Enter(f);
        Cmp c = Cmp.Normal;
        for (int i = 0; i < body.Length; i++)
        {
            c = body[i].Exec(f);
            if (c != Cmp.Normal)
            {
                break;
            }
        }

        entry?.Exit(f);
        return c;
    }
}

internal sealed class SeqStmt(SNode[] body) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        for (int i = 0; i < body.Length; i++)
        {
            Cmp c = body[i].Exec(f);
            if (c != Cmp.Normal)
            {
                return c;
            }
        }

        return Cmp.Normal;
    }
}

internal sealed class IfStmt(XNode test, SNode then, SNode? otherwise) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        if (test.EvalBool(f))
        {
            return then.Exec(f);
        }

        return otherwise is null ? Cmp.Normal : otherwise.Exec(f);
    }
}

internal sealed class ForStmt(ScopeEntry? entry, SNode? init, XNode? test, XNode? update, SNode body, bool perIteration) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        Realm r = f.Realm;
        entry?.Enter(f);
        init?.Exec(f);
        Cmp result = Cmp.Normal;
        while (true)
        {
            if (test is not null && !test.EvalBool(f))
            {
                break;
            }

            Cmp c = body.Exec(f);
            if (c == Cmp.Break)
            {
                break;
            }

            if (c == Cmp.Return)
            {
                result = c;
                break;
            }

            if (--r.Fuel < 0)
            {
                throw Realm.OutOfFuel();
            }

            if (perIteration)
            {
                Env old = f.Env!;
                var copy = new Env(old.Parent, old.Slots.Length);
                Array.Copy(old.Slots, copy.Slots, old.Slots.Length);
                f.Env = copy;
            }

            update?.Eval(f);
        }

        entry?.Exit(f);
        return result;
    }
}

internal sealed class WhileStmt(XNode test, SNode body) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        Realm r = f.Realm;
        while (test.EvalBool(f))
        {
            Cmp c = body.Exec(f);
            if (c == Cmp.Break)
            {
                break;
            }

            if (c == Cmp.Return)
            {
                return c;
            }

            if (--r.Fuel < 0)
            {
                throw Realm.OutOfFuel();
            }
        }

        return Cmp.Normal;
    }
}

internal sealed class DoWhileStmt(SNode body, XNode test) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        Realm r = f.Realm;
        do
        {
            Cmp c = body.Exec(f);
            if (c == Cmp.Break)
            {
                break;
            }

            if (c == Cmp.Return)
            {
                return c;
            }

            if (--r.Fuel < 0)
            {
                throw Realm.OutOfFuel();
            }
        }
        while (test.EvalBool(f));
        return Cmp.Normal;
    }
}

internal sealed class ReturnStmt(XNode? arg) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        f.Ret = arg is null ? default : arg.Eval(f);
        return Cmp.Return;
    }
}

internal sealed class JumpStmt(Cmp kind) : SNode
{
    internal override Cmp Exec(Frame f) => kind;
}

internal sealed class ThrowStmt(XNode arg) : SNode
{
    internal override Cmp Exec(Frame f) => throw new JsThrow(arg.Eval(f));
}

internal sealed class TryStmt(SNode block, ScopeEntry catchEntry, VarRef? param, SNode[] handler) : SNode
{
    internal override Cmp Exec(Frame f)
    {
        Realm r = f.Realm;
        int depth = r.Depth;
        int sp = r.Sp;
        Env? env = f.Env;
        try
        {
            return block.Exec(f);
        }
        catch (JsThrow ex)
        {
            r.Depth = depth;
            r.Sp = sp;
            f.Env = env;
            catchEntry.Enter(f);
            param?.Init(f, ex.Value);
            Cmp c = Cmp.Normal;
            for (int i = 0; i < handler.Length; i++)
            {
                c = handler[i].Exec(f);
                if (c != Cmp.Normal)
                {
                    break;
                }
            }

            f.Env = env;
            return c;
        }
    }
}
