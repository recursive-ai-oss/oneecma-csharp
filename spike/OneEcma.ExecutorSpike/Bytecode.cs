using System.Runtime.CompilerServices;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.ExecutorSpike.Bytecode;

internal enum Op
{
    Move, LoadThis,
    Add, Sub, Mul, Rem, Lt, Le, Gt, Ge, SEq, SNe,
    Neg, Not, ToNum, BitNot, Typeof,
    Jmp, JmpBack, JTrue, JFalse, JTrueBack,
    JNLt, JNLe, JNGt, JNGe, JNSEq, JNSNe,
    JLt, JLe, JGt, JGe, JSEq, JSNe,
    GetProp, SetProp, GetLength, GetIndex, SetIndex,
    GetGlobal, SetGlobal,
    GetEnv, GetEnvChk, SetEnv, SetEnvChk, ChkTdz, ThrowConst,
    PushEnv, PopEnv, CopyEnv, SaveEnv, RestoreEnv,
    Closure, NewObject, NewArray, ArrPush,
    Call, New, Return, Throw,

    /// <summary>Generic binary operator: <c>Bin + (int)BinaryOp</c>.</summary>
    Bin = 100,
}

/// <summary>One instruction: opcode and three operands. A non-negative operand is a register, a negative one <c>~k</c> a constant.</summary>
internal struct Instr
{
    internal Op Op;
    internal int A;
    internal int B;
    internal int C;
}

internal readonly record struct Handler(int Start, int End, int HandlerPc, int CatchReg);

/// <summary>Inline cache for one property site (read or write) or global read.</summary>
internal sealed class PropIc(string key)
{
    internal readonly string Key = key;
    internal Shape? S0;
    internal int Slot0;
    internal Shape? S1;
    internal JsObject? Holder;
    internal Shape? HolderShape;
    internal int Slot1;
    internal Shape? From;
    internal Shape? To;

    internal JsValue GetSlow(Realm realm, JsObject jo, JsValue o)
    {
        if (jo is JsFunction fn && Key == "prototype")
        {
            fn.EnsurePrototype(realm);
        }

        if (jo is JsArray && Key == "length")
        {
            return Ops.GetMember(realm, o, Key);
        }

        int slot = jo.Shape.Lookup(Key);
        if (slot >= 0)
        {
            S0 = jo.Shape;
            Slot0 = slot;
            return jo.Slots[slot];
        }

        JsObject? proto = jo.Shape.Proto;
        if (proto is not null && proto is not JsArray)
        {
            int ps = proto.Shape.Lookup(Key);
            if (ps >= 0)
            {
                S1 = jo.Shape;
                Holder = proto;
                HolderShape = proto.Shape;
                Slot1 = ps;
                return proto.Slots[ps];
            }
        }

        return Ops.GetMember(realm, o, Key);
    }

    internal void SetSlow(JsObject jo, JsValue v)
    {
        Shape before = jo.Shape;
        int slot = before.Lookup(Key);
        jo.Set(Key, v);
        if (jo is JsArray or JsFunction)
        {
            return;
        }

        if (slot >= 0)
        {
            S0 = before;
            Slot0 = slot;
        }
        else
        {
            From = before;
            To = jo.Shape;
        }
    }
}

internal sealed class BcFunc
{
    internal string Name = string.Empty;
    internal Instr[] Code = [];
    internal JsValue[] Consts = [];
    internal PropIc[] Ics = [];
    internal BcFunc[] Funcs = [];
    internal Handler[] Handlers = [];
    internal int[] SpanStart = [];
    internal int[] SpanEnd = [];
    internal int NParams;
    internal int NumRegs;
    internal bool IsArrow;
    internal bool IsCtor;
    internal string Source = string.Empty;
}

internal sealed class ScriptFunctionB : JsFunction
{
    internal readonly BcFunc Code;
    internal readonly Env? Env;
    internal readonly JsValue BoundThis;

    internal ScriptFunctionB(Realm realm, BcFunc code, Env? env, JsValue boundThis)
        : base(realm.FunctionProto, code.Name)
    {
        Code = code;
        Env = env;
        BoundThis = boundThis;
    }

    internal override bool IsConstructor => Code.IsCtor;
}

/// <summary>An activation record. Records are pooled by depth; the registers live in the shared stack.</summary>
internal sealed class Fr
{
    internal BcFunc Fn = null!;
    internal int Pc;
    internal int Base;
    internal Env? Env;
    internal JsValue This;
    internal int RetDst;
    internal bool IsConstruct;
}

/// <summary>
/// Observation hooks of the traced run. The lean run instantiates <see cref="NoTrace"/>; its empty, inlined
/// bodies cost nothing, so the traced and lean runs share one dispatch loop and cannot drift apart.
/// </summary>
internal interface ITracer
{
    /// <summary>Called after an instruction that wrote register <paramref name="a"/> has finished.</summary>
    void After(BcFunc fn, int pc, Op op, JsValue[] stack, int bas, int a);
}

internal struct NoTrace : ITracer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void After(BcFunc fn, int pc, Op op, JsValue[] stack, int bas, int a)
    {
    }
}

/// <summary>Records, for every value-producing instruction, its span and the value it produced: the raw material of value provenance.</summary>
internal struct RecordingTrace : ITracer
{
    internal static List<(int Start, int End, string Value)>? Sink;

    public readonly void After(BcFunc fn, int pc, Op op, JsValue[] stack, int bas, int a)
    {
        if (Sink is null || !ProducesValue(op))
        {
            return;
        }

        Sink.Add((fn.SpanStart[pc], fn.SpanEnd[pc], Conv.ToStr(stack[bas + a])));
    }

    private static bool ProducesValue(Op op) =>
        op < Op.Jmp || (op >= Op.GetProp && op <= Op.GetGlobal && op is not (Op.SetProp or Op.SetIndex))
        || op is Op.GetEnv or Op.GetEnvChk or Op.Closure or Op.NewObject or Op.NewArray or Op.Call or Op.New or >= Op.Bin;
}

internal sealed class Vm
{
    private readonly Realm _realm;
    private JsValue[] _stack = new JsValue[4096];
    private readonly Fr[] _frames;
    private int _fp;

    internal Vm(Realm realm)
    {
        _realm = realm;
        _frames = new Fr[realm.MaxDepth + 2];
    }

    internal JsValue RunProgram<TTrace>(BcFunc program)
        where TTrace : struct, ITracer
    {
        Fr f = _frames[0] ??= new Fr();
        f.Fn = program;
        f.Pc = 0;
        f.Base = 0;
        f.Env = null;
        f.RetDst = 0;
        _fp = 0;
        EnsureStack(program.NumRegs + 8);
        return Run<TTrace>(0);
    }

    private void EnsureStack(int need)
    {
        if (need > _stack.Length)
        {
            if (need > 64 * 1024 * 1024)
            {
                throw new SpikeLimitException("register stack exhausted");
            }

            Array.Resize(ref _stack, Math.Max(need, _stack.Length * 2));
        }
    }

    private bool Unwind(JsValue thrown, int entryFp, int topPc)
    {
        for (int k = _fp; k >= entryFp; k--)
        {
            Fr f = _frames[k];
            int at = (k == _fp ? topPc : f.Pc) - 1;
            foreach (Handler h in f.Fn.Handlers)
            {
                if (at >= h.Start && at < h.End)
                {
                    _fp = k;
                    f.Pc = h.HandlerPc;
                    _stack[f.Base + h.CatchReg] = thrown;
                    return true;
                }
            }
        }

        _fp = entryFp - 1;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref JsValue V(JsValue[] st, int bas, JsValue[] k, int x) => ref (x >= 0 ? ref st[bas + x] : ref k[~x]);

    private JsValue Run<TTrace>(int entryFp)
        where TTrace : struct, ITracer
    {
        TTrace tracer = default;
        Realm r = _realm;
        JsValue thrown = default;
        int pc = 0;
        while (true)
        {
            // (Re)load the state of the current frame: after a call, a return or an unwind.
            Fr fr = _frames[_fp];
            BcFunc code = fr.Fn;
            Instr[] ins = code.Code;
            JsValue[] consts = code.Consts;
            PropIc[] ics = code.Ics;
            JsValue[] stack = _stack;
            int bas = fr.Base;
            Env? env = fr.Env;
            pc = fr.Pc;
            try
            {
                while (true)
                {
                    ref Instr i = ref ins[pc++];
                    switch (i.Op)
                    {
                        case Op.Move:
                            stack[bas + i.A] = V(stack, bas, consts, i.B);
                            break;
                        case Op.LoadThis:
                            stack[bas + i.A] = fr.This;
                            break;
                        case Op.Add:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                stack[bas + i.A] = a.IsNumber && b.IsNumber ? JsValue.FromNumber(a.Num + b.Num) : Ops.Add(a, b);
                                break;
                            }

                        case Op.Sub:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                stack[bas + i.A] = a.IsNumber && b.IsNumber ? JsValue.FromNumber(a.Num - b.Num) : JsValue.FromNumber(Conv.ToNumber(a) - Conv.ToNumber(b));
                                break;
                            }

                        case Op.Mul:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                stack[bas + i.A] = a.IsNumber && b.IsNumber ? JsValue.FromNumber(a.Num * b.Num) : JsValue.FromNumber(Conv.ToNumber(a) * Conv.ToNumber(b));
                                break;
                            }

                        case Op.Rem:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                stack[bas + i.A] = JsValue.FromNumber((a.IsNumber ? a.Num : Conv.ToNumber(a)) % (b.IsNumber ? b.Num : Conv.ToNumber(b)));
                                break;
                            }

                        case Op.Lt:
                            stack[bas + i.A] = JsValue.FromBool(Ops.LessThan(V(stack, bas, consts, i.B), V(stack, bas, consts, i.C)));
                            break;
                        case Op.Le:
                            stack[bas + i.A] = JsValue.FromBool(Ops.LessEq(V(stack, bas, consts, i.B), V(stack, bas, consts, i.C)));
                            break;
                        case Op.Gt:
                            stack[bas + i.A] = JsValue.FromBool(Ops.GreaterThan(V(stack, bas, consts, i.B), V(stack, bas, consts, i.C)));
                            break;
                        case Op.Ge:
                            stack[bas + i.A] = JsValue.FromBool(Ops.GreaterEq(V(stack, bas, consts, i.B), V(stack, bas, consts, i.C)));
                            break;
                        case Op.SEq:
                            stack[bas + i.A] = JsValue.FromBool(Ops.StrictEquals(V(stack, bas, consts, i.B), V(stack, bas, consts, i.C)));
                            break;
                        case Op.SNe:
                            stack[bas + i.A] = JsValue.FromBool(!Ops.StrictEquals(V(stack, bas, consts, i.B), V(stack, bas, consts, i.C)));
                            break;
                        case Op.Neg:
                            stack[bas + i.A] = JsValue.FromNumber(-Conv.ToNumber(V(stack, bas, consts, i.B)));
                            break;
                        case Op.Not:
                            stack[bas + i.A] = JsValue.FromBool(!Conv.ToBoolean(V(stack, bas, consts, i.B)));
                            break;
                        case Op.ToNum:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                stack[bas + i.A] = a.IsNumber ? a : JsValue.FromNumber(Conv.ToNumber(a));
                                break;
                            }

                        case Op.BitNot:
                            stack[bas + i.A] = JsValue.FromNumber(~Conv.ToInt32(Conv.ToNumber(V(stack, bas, consts, i.B))));
                            break;
                        case Op.Typeof:
                            stack[bas + i.A] = JsValue.FromString(Conv.TypeOf(V(stack, bas, consts, i.B)));
                            break;
                        case Op.Jmp:
                            pc = i.A;
                            break;
                        case Op.JmpBack:
                            if (--r.Fuel < 0)
                            {
                                throw Realm.OutOfFuel();
                            }

                            pc = i.A;
                            break;
                        case Op.JTrue:
                            if (Conv.ToBoolean(V(stack, bas, consts, i.B)))
                            {
                                pc = i.A;
                            }

                            break;
                        case Op.JFalse:
                            if (!Conv.ToBoolean(V(stack, bas, consts, i.B)))
                            {
                                pc = i.A;
                            }

                            break;
                        case Op.JTrueBack:
                            if (Conv.ToBoolean(V(stack, bas, consts, i.B)))
                            {
                                if (--r.Fuel < 0)
                                {
                                    throw Realm.OutOfFuel();
                                }

                                pc = i.A;
                            }

                            break;
                        case Op.JNLt:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (!(a.IsNumber && b.IsNumber ? a.Num < b.Num : Ops.LessThan(a, b)))
                                {
                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.JNLe:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (!(a.IsNumber && b.IsNumber ? a.Num <= b.Num : Ops.LessEq(a, b)))
                                {
                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.JNGt:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (!(a.IsNumber && b.IsNumber ? a.Num > b.Num : Ops.GreaterThan(a, b)))
                                {
                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.JNGe:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (!(a.IsNumber && b.IsNumber ? a.Num >= b.Num : Ops.GreaterEq(a, b)))
                                {
                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.JNSEq:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (!(a.IsNumber && b.IsNumber ? a.Num == b.Num : Ops.StrictEquals(a, b)))
                                {
                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.JNSNe:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (a.IsNumber && b.IsNumber ? a.Num == b.Num : Ops.StrictEquals(a, b))
                                {
                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.JLt:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (a.IsNumber && b.IsNumber ? a.Num < b.Num : Ops.LessThan(a, b))
                                {
                                    if (--r.Fuel < 0)
                                    {
                                        throw Realm.OutOfFuel();
                                    }

                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.JLe:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (a.IsNumber && b.IsNumber ? a.Num <= b.Num : Ops.LessEq(a, b))
                                {
                                    if (--r.Fuel < 0)
                                    {
                                        throw Realm.OutOfFuel();
                                    }

                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.JGt:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (a.IsNumber && b.IsNumber ? a.Num > b.Num : Ops.GreaterThan(a, b))
                                {
                                    if (--r.Fuel < 0)
                                    {
                                        throw Realm.OutOfFuel();
                                    }

                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.JGe:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (a.IsNumber && b.IsNumber ? a.Num >= b.Num : Ops.GreaterEq(a, b))
                                {
                                    if (--r.Fuel < 0)
                                    {
                                        throw Realm.OutOfFuel();
                                    }

                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.JSEq:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (a.IsNumber && b.IsNumber ? a.Num == b.Num : Ops.StrictEquals(a, b))
                                {
                                    if (--r.Fuel < 0)
                                    {
                                        throw Realm.OutOfFuel();
                                    }

                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.JSNe:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                if (!(a.IsNumber && b.IsNumber ? a.Num == b.Num : Ops.StrictEquals(a, b)))
                                {
                                    if (--r.Fuel < 0)
                                    {
                                        throw Realm.OutOfFuel();
                                    }

                                    pc = i.A;
                                }

                                break;
                            }

                        case Op.GetProp:
                            {
                                JsValue o = stack[bas + i.B];
                                PropIc ic = ics[i.C];
                                if (o.Ref is JsObject jo)
                                {
                                    Shape s = jo.Shape;
                                    if (ReferenceEquals(s, ic.S0))
                                    {
                                        stack[bas + i.A] = jo.Slots[ic.Slot0];
                                    }
                                    else if (ReferenceEquals(s, ic.S1) && ReferenceEquals(ic.Holder!.Shape, ic.HolderShape))
                                    {
                                        stack[bas + i.A] = ic.Holder.Slots[ic.Slot1];
                                    }
                                    else
                                    {
                                        stack[bas + i.A] = ic.GetSlow(r, jo, o);
                                    }
                                }
                                else
                                {
                                    stack[bas + i.A] = Ops.GetMember(r, o, ic.Key);
                                }

                                break;
                            }

                        case Op.SetProp:
                            {
                                JsValue o = stack[bas + i.A];
                                JsValue v = V(stack, bas, consts, i.B);
                                PropIc ic = ics[i.C];
                                if (o.Ref is JsObject jo)
                                {
                                    Shape s = jo.Shape;
                                    if (ReferenceEquals(s, ic.S0))
                                    {
                                        jo.Slots[ic.Slot0] = v;
                                    }
                                    else if (ReferenceEquals(s, ic.From))
                                    {
                                        Shape to = ic.To!;
                                        int slot = to.Count - 1;
                                        if (slot >= jo.Slots.Length)
                                        {
                                            Array.Resize(ref jo.Slots, Math.Max(4, jo.Slots.Length * 2));
                                        }

                                        jo.Slots[slot] = v;
                                        jo.Shape = to;
                                    }
                                    else
                                    {
                                        ic.SetSlow(jo, v);
                                    }
                                }
                                else
                                {
                                    Ops.SetMember(r, o, ic.Key, v);
                                }

                                break;
                            }

                        case Op.GetLength:
                            {
                                JsValue o = stack[bas + i.B];
                                if (o.Ref is JsArray a)
                                {
                                    stack[bas + i.A] = JsValue.FromNumber(a.Length);
                                }
                                else if (o.Ref is string s)
                                {
                                    stack[bas + i.A] = JsValue.FromNumber(s.Length);
                                }
                                else
                                {
                                    stack[bas + i.A] = Ops.GetMember(r, o, "length");
                                }

                                break;
                            }

                        case Op.GetIndex:
                            stack[bas + i.A] = Ops.GetIndex(r, stack[bas + i.B], V(stack, bas, consts, i.C));
                            break;
                        case Op.SetIndex:
                            Ops.SetIndex(r, stack[bas + i.A], V(stack, bas, consts, i.B), V(stack, bas, consts, i.C));
                            break;
                        case Op.GetGlobal:
                            {
                                PropIc ic = ics[i.C];
                                JsObject g = r.Global;
                                if (ReferenceEquals(g.Shape, ic.S0))
                                {
                                    stack[bas + i.A] = g.Slots[ic.Slot0];
                                    break;
                                }

                                int slot = g.Shape.Lookup(ic.Key);
                                if (slot < 0)
                                {
                                    throw r.ReferenceError(ic.Key + " is not defined");
                                }

                                ic.S0 = g.Shape;
                                ic.Slot0 = slot;
                                stack[bas + i.A] = g.Slots[slot];
                                break;
                            }

                        case Op.SetGlobal:
                            r.Global.Set(ics[i.C].Key, V(stack, bas, consts, i.A));
                            break;
                        case Op.GetEnv:
                        case Op.GetEnvChk:
                            {
                                Env e = env!;
                                for (int h = i.B; h > 0; h--)
                                {
                                    e = e.Parent!;
                                }

                                JsValue v = e.Slots[i.C];
                                if (i.Op == Op.GetEnvChk && v.IsTdz)
                                {
                                    throw TdzError(r, code, pc - 1);
                                }

                                stack[bas + i.A] = v;
                                break;
                            }

                        case Op.SetEnv:
                        case Op.SetEnvChk:
                            {
                                Env e = env!;
                                for (int h = i.A; h > 0; h--)
                                {
                                    e = e.Parent!;
                                }

                                if (i.Op == Op.SetEnvChk && e.Slots[i.B].IsTdz)
                                {
                                    throw TdzError(r, code, pc - 1);
                                }

                                e.Slots[i.B] = V(stack, bas, consts, i.C);
                                break;
                            }

                        case Op.ChkTdz:
                            if (stack[bas + i.A].IsTdz)
                            {
                                throw TdzError(r, code, pc - 1);
                            }

                            break;
                        case Op.ThrowConst:
                            throw r.TypeError("Assignment to constant variable.");
                        case Op.PushEnv:
                            env = new Env(env, i.A);
                            break;
                        case Op.PopEnv:
                            env = env!.Parent;
                            break;
                        case Op.CopyEnv:
                            {
                                Env old = env!;
                                var copy = new Env(old.Parent, old.Slots.Length);
                                Array.Copy(old.Slots, copy.Slots, old.Slots.Length);
                                env = copy;
                                break;
                            }

                        case Op.SaveEnv:
                            stack[bas + i.A] = new JsValue(env, 0);
                            break;
                        case Op.RestoreEnv:
                            env = (Env?)stack[bas + i.A].Ref;
                            break;
                        case Op.Closure:
                            stack[bas + i.A] = JsValue.FromObject(new ScriptFunctionB(r, code.Funcs[i.B], env, fr.This));
                            break;
                        case Op.NewObject:
                            stack[bas + i.A] = JsValue.FromObject(new JsObject(r.ObjectProto, i.B));
                            break;
                        case Op.NewArray:
                            stack[bas + i.A] = JsValue.FromObject(r.NewArray(i.B));
                            break;
                        case Op.ArrPush:
                            ((JsArray)stack[bas + i.A].Ref!).Push(V(stack, bas, consts, i.B));
                            break;
                        case Op.Call:
                        case Op.New:
                            {
                                int b = bas + i.B;
                                int argc = i.C;
                                bool isNew = i.Op == Op.New;
                                object? callee = stack[b].Ref;
                                if (callee is ScriptFunctionB sf && (!isNew || sf.IsConstructor))
                                {
                                    if (--r.Fuel < 0)
                                    {
                                        throw Realm.OutOfFuel();
                                    }

                                    if (_fp >= r.MaxDepth)
                                    {
                                        throw Realm.TooDeep();
                                    }

                                    BcFunc c = sf.Code;
                                    int nb = b + 2;
                                    int end = nb + c.NumRegs;
                                    if (end + 8 > stack.Length)
                                    {
                                        EnsureStack(end + 8);
                                        stack = _stack;
                                    }

                                    int from = nb + (argc < c.NParams ? argc : c.NParams);
                                    Array.Clear(stack, from, end - from);
                                    fr.Pc = pc;
                                    fr.Env = env;
                                    Fr nf = _frames[++_fp] ??= new Fr();
                                    nf.Fn = c;
                                    nf.Pc = 0;
                                    nf.Base = nb;
                                    nf.Env = sf.Env;
                                    nf.RetDst = bas + i.A;
                                    nf.IsConstruct = isNew;
                                    if (isNew)
                                    {
                                        sf.EnsurePrototype(r);
                                        var obj = new JsObject(sf.PrototypeProperty, 4);
                                        nf.This = JsValue.FromObject(obj);
                                    }
                                    else
                                    {
                                        nf.This = c.IsArrow ? sf.BoundThis : stack[b + 1];
                                    }

                                    goto reload;
                                }

                                if (callee is NativeFunction nat)
                                {
                                    if (--r.Fuel < 0)
                                    {
                                        throw Realm.OutOfFuel();
                                    }

                                    JsValue res = nat.Fn(isNew ? default : stack[b + 1], new ReadOnlySpan<JsValue>(stack, b + 2, argc));
                                    if (isNew && !res.IsObject)
                                    {
                                        throw r.TypeError("not a constructor");
                                    }

                                    stack[bas + i.A] = res;
                                    break;
                                }

                                throw r.TypeError(string.Concat(
                                    code.Source.AsSpan(code.SpanStart[pc - 1], code.SpanEnd[pc - 1] - code.SpanStart[pc - 1]),
                                    isNew ? " is not a constructor" : " is not a function"));
                            }

                        case Op.Return:
                            {
                                JsValue v = V(stack, bas, consts, i.A);
                                if (fr.IsConstruct && !v.IsObject)
                                {
                                    v = fr.This;
                                }

                                int dst = fr.RetDst;
                                if (_fp == entryFp)
                                {
                                    _fp--;
                                    return v;
                                }

                                _fp--;
                                stack[dst] = v;
                                goto reload;
                            }

                        case Op.Throw:
                            thrown = V(stack, bas, consts, i.A);
                            fr.Pc = pc;
                            goto handle;
                        default:
                            {
                                ref JsValue a = ref V(stack, bas, consts, i.B);
                                ref JsValue b = ref V(stack, bas, consts, i.C);
                                stack[bas + i.A] = Ops.Binary((BinaryOp)(i.Op - Op.Bin), a, b);
                                break;
                            }
                    }

                    tracer.After(code, pc - 1, i.Op, stack, bas, i.A);
                }
            }
            catch (JsThrow ex)
            {
                thrown = ex.Value;
                fr.Pc = pc;
            }

        handle:
            if (!Unwind(thrown, entryFp, fr.Pc))
            {
                throw new JsThrow(thrown);
            }

            continue;
        reload:
            continue;
        }
    }

    private static JsThrow TdzError(Realm r, BcFunc code, int pc)
    {
        string name = code.Source.Substring(code.SpanStart[pc], code.SpanEnd[pc] - code.SpanStart[pc]);
        return r.ReferenceError($"Cannot access '{name}' before initialization");
    }
}
