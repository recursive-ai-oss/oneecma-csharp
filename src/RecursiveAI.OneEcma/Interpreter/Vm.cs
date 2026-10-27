using System.Numerics;
using System.Runtime.CompilerServices;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma;

/// <summary>Hooks the interpreter calls around every operation; the lean run uses the empty implementation, so the call vanishes.</summary>
internal interface ITracer
{
    void OnOp(Frame frame, Instr ins, int pc, JsValue[] st, int sp);
}

internal struct NoTrace : ITracer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void OnOp(Frame frame, Instr ins, int pc, JsValue[] st, int sp)
    {
    }
}

/// <summary>The bytecode interpreter: an explicit frame stack, so script call depth is a counter and never host stack.</summary>
internal static partial class Vm
{
    // ---- entry from native code ---------------------------------------------------------------

    /// <summary>[[Call]] / [[Construct]] of a script function from native code.</summary>
    internal static JsValue CallFunction(ScriptFunction fn, JsValue thisValue, ReadOnlySpan<JsValue> args, JsObject? newTarget)
    {
        Agent ag = Agent.Current;
        if (ag.NativeDepth >= ag.Limits.MaxNativeDepth)
        {
            throw ag.Limit(LimitKind.NativeDepth, "Native code re-entered the interpreter more than " + ag.Limits.MaxNativeDepth + " levels deep");
        }

        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw Errors.Range("Maximum call stack size exceeded");
        }

        Frame? savedFrame = ag.Frame;
        int savedDepth = ag.CallDepth;
        Realm savedRealm = ag.Realm;
        NativeFunction? savedNative = ag.CurrentNative;
        JsValue savedNativeThis = ag.CurrentNativeThis;
        bool savedNativeNew = ag.CurrentNativeNew;
        ag.NativeDepth++;
        try
        {
            JsValue[] arr = args.ToArray();
            Frame f = PrepareFrame(ag, fn, thisValue, arr, 0, arr.Length, newTarget);
            f.IsEntry = true;
            f.Caller = savedFrame;
            f.NativeCaller = savedNative;
            f.NativeCallerThis = savedNativeThis;
            f.NativeCallerNew = savedNativeNew;
            ag.CurrentNative = null;
            return Run(ag, f);
        }
        finally
        {
            ag.CurrentNative = savedNative;
            ag.CurrentNativeThis = savedNativeThis;
            ag.CurrentNativeNew = savedNativeNew;
            ag.NativeDepth--;
            ag.Frame = savedFrame;
            ag.CallDepth = savedDepth;
            ag.Realm = savedRealm;
        }
    }

    /// <summary>Runs a prepared frame (script, module, eval) to completion from native code.</summary>
    internal static JsValue RunEntry(Agent ag, Frame f)
    {
        if (ag.NativeDepth >= ag.Limits.MaxNativeDepth)
        {
            throw ag.Limit(LimitKind.NativeDepth, "Native code re-entered the interpreter more than " + ag.Limits.MaxNativeDepth + " levels deep");
        }

        Frame? savedFrame = ag.Frame;
        int savedDepth = ag.CallDepth;
        Realm savedRealm = ag.Realm;
        NativeFunction? savedNative = ag.CurrentNative;
        JsValue savedNativeThis = ag.CurrentNativeThis;
        bool savedNativeNew = ag.CurrentNativeNew;
        ag.NativeDepth++;
        try
        {
            f.IsEntry = true;
            f.Caller = savedFrame;
            f.NativeCaller = savedNative;
            f.NativeCallerThis = savedNativeThis;
            f.NativeCallerNew = savedNativeNew;
            ag.CurrentNative = null;
            return Run(ag, f);
        }
        finally
        {
            ag.CurrentNative = savedNative;
            ag.CurrentNativeThis = savedNativeThis;
            ag.CurrentNativeNew = savedNativeNew;
            ag.NativeDepth--;
            ag.Frame = savedFrame;
            ag.CallDepth = savedDepth;
            ag.Realm = savedRealm;
        }
    }

    /// <summary>Creates the frame for a call (10.2.1 / 10.2.2): this-binding, class-constructor check, arguments view.</summary>
    internal static Frame PrepareFrame(Agent ag, ScriptFunction fn, JsValue thisValue, JsValue[] args, int off, int argc, JsObject? newTarget)
    {
        FunctionCode code = fn.Code;
        if (newTarget is null && fn.IsClassConstructor)
        {
            throw Errors.Type("Class constructor " + code.Name + " cannot be invoked without 'new'");
        }

        var f = Frame.Obtain(code, fn.Realm);
        f.Func = fn;
        f.Env = fn.Env;
        f.NewTarget = newTarget;
        f.ArgArr = args;
        f.ArgOff = off;
        f.ArgCount = argc;
        f.IsConstruct = newTarget is not null;
        if (code.Kind == FunctionKind.Arrow)
        {
            f.ThisFrame = fn.OuterFrame!.ThisFrame;
            f.This = default;
            f.CallReceiver = thisValue;
        }
        else if (newTarget is not null)
        {
            if (fn.IsDerived)
            {
                f.This = JsValue.Tdz;
            }
            else
            {
                JsObject proto = Ops.GetPrototypeFromConstructor(newTarget, static r => r.ObjectPrototype);
                var obj = new JsObject(proto);
                f.This = JsValue.FromObject(obj);
                if (fn.Fields is not null || fn.PrivateMethods is not null)
                {
                    InitializeInstanceElements(obj, fn);
                }
            }
        }
        else if (code.Strict)
        {
            f.This = thisValue;
        }
        else if (thisValue.IsNullish)
        {
            f.This = JsValue.FromObject(fn.Realm.Global);
        }
        else if (!thisValue.IsObject)
        {
            f.This = JsValue.FromObject(Ops.ToObject(thisValue));
        }
        else
        {
            f.This = thisValue;
        }

        if (code.IsAsync && !code.IsGenerator)
        {
            f.Owner = Promises.NewPromise(fn.Realm);
        }

        return f;
    }

    /// <summary>InitializeInstanceElements (7.3.34): private methods first, then fields in order.</summary>
    internal static void InitializeInstanceElements(JsObject o, ScriptFunction ctor)
    {
        if (ctor.PrivateMethods is not null)
        {
            foreach (PrivateMethodDef m in ctor.PrivateMethods)
            {
                o.PrivateAddMethod(m);
            }
        }

        if (ctor.Fields is not null)
        {
            foreach (ClassFieldDef fd in ctor.Fields)
            {
                JsValue v = fd.Init is null ? default : fd.Init.Call(JsValue.FromObject(o), default);
                if (fd.Private is not null)
                {
                    o.PrivateFieldAdd(fd.Private, v);
                }
                else
                {
                    o.CreateDataPropertyOrThrow(fd.Key, v);
                }
            }
        }
    }

    // ---- the dispatch loop ----------------------------------------------------------------------

    internal static JsValue Run(Agent ag, Frame entry) => ag.Trace is null ? Run<NoTrace>(ag, entry) : Run<RecordingTrace>(ag, entry);

    private static JsException TypeErr(string m) => Errors.Type(m);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static JsValue Run<TTrace>(Agent ag, Frame entry)
        where TTrace : struct, ITracer
    {
        TTrace tracer = default;
        Frame f = entry;
        ag.Frame = f;
        ag.Realm = f.Realm;
        if (++ag.CallDepth > ag.Limits.MaxCallDepth)
        {
            ag.CallDepth--;
            throw Errors.Range("Maximum call stack size exceeded");
        }

        JsValue[] st = f.Stack;
        int sp = f.Sp;
        int pc = f.Pc;
        Instr[] code = f.Code.Ops;
        FunctionCode fc = f.Code;
        JsValue thrown = default;
        bool hasThrown = false;

        if (f.State != FrameState.Running)
        {
            // Resuming a suspended generator, async function or module (the sent value was stored by the resumer).
            if (!ResumeSetup(f, ref sp, ref pc, out thrown))
            {
                hasThrown = true;
            }
        }

        while (true)
        {
            if (hasThrown)
            {
                hasThrown = false;
                // Unwind to the nearest handler (ECMA-262 14.15 try statement), leaving frames that have none.
                while (true)
                {
                    if (f.Handlers is { Count: > 0 } hs)
                    {
                        Handler h = hs[^1];
                        hs.RemoveAt(hs.Count - 1);
                        f.Env = h.Env;
                        sp = h.Sp;
                        st[sp++] = thrown;
                        pc = h.Pc;
                        break;
                    }

                    ag.CallDepth--;
                    f.State = FrameState.Completed;
                    if (f.Owner is PromiseObject asyncPromise && f.Code.IsAsync && !f.Code.IsGenerator && f.Code.Kind != FunctionKind.Module)
                    {
                        Promises.Reject(asyncPromise, thrown);
                        if (f.IsEntry)
                        {
                            // A call from native code gets the rejected promise back, as a call from script does.
                            ag.Frame = f.Caller;
                            return JsValue.FromObject(asyncPromise);
                        }

                        f = f.Caller!;
                        ag.Frame = f;
                        ag.Realm = f.Realm;
                        st = f.Stack;
                        sp = f.Sp;
                        pc = f.Pc + 1;
                        code = f.Code.Ops;
                        fc = f.Code;
                        st[sp++] = JsValue.FromObject(asyncPromise);
                        break;
                    }

                    if (f.IsEntry)
                    {
                        ag.Frame = f.Caller;
                        throw new JsException(thrown);
                    }

                    f = f.Caller!;
                    ag.Frame = f;
                    ag.Realm = f.Realm;
                    st = f.Stack;
                    sp = f.Sp;
                    pc = f.Pc;
                    code = f.Code.Ops;
                    fc = f.Code;
                }
            }

            try
            {
                while (true)
                {
                    Instr ins = code[pc];
                    f.Pc = pc;
                    pc++;
                    tracer.OnOp(f, ins, pc - 1, st, sp);
                    if (typeof(TTrace) == typeof(NoTrace) && ins.Fused != Fusion.None)
                    {
                        // A marked sequence over a number binding runs in one step; anything else falls through to the first
                        // instruction's own case and the rest run one by one. A traced run never takes this path.
                        Env fe = f.Env!;
                        for (int h = ins.A; h > 0; h--)
                        {
                            fe = fe.Parent!;
                        }

                        ref JsValue fv = ref fe.Slots[ins.B];
                        if (fv.IsNumber)
                        {
                            switch (ins.Fused)
                            {
                                case Fusion.CompareSlotInt:
                                    {
                                        double x = fv.Num;
                                        double y = code[pc].A;
                                        bool r = code[pc + 1].Op switch
                                        {
                                            Op.Lt => x < y,
                                            Op.Le => x <= y,
                                            Op.Gt => x > y,
                                            _ => x >= y,
                                        };
                                        pc = r ? pc + 3 : code[pc + 2].A;
                                        continue;
                                    }

                                case Fusion.IncrementSlot:
                                    fv = JsValue.FromNumber(fv.Num + 1);
                                    pc += 3;
                                    continue;
                                case Fusion.AddIntToSlot:
                                    fv = JsValue.FromNumber(fv.Num + code[pc].A);
                                    pc += 3;
                                    continue;
                            }
                        }
                    }

                    switch (ins.Op)
                    {
                        case Op.Nop:
                            break;
                        case Op.PushUndef:
                            st[sp++] = default;
                            break;
                        case Op.PushNull:
                            st[sp++] = JsValue.Null;
                            break;
                        case Op.PushTrue:
                            st[sp++] = JsValue.True;
                            break;
                        case Op.PushFalse:
                            st[sp++] = JsValue.False;
                            break;
                        case Op.PushInt:
                            st[sp++] = JsValue.FromNumber(ins.A);
                            break;
                        case Op.PushConst:
                            st[sp++] = fc.Consts[ins.A];
                            break;
                        case Op.Pop:
                            sp--;
                            break;
                        case Op.Dup:
                            st[sp] = st[sp - 1];
                            sp++;
                            break;
                        case Op.Dup2:
                            st[sp] = st[sp - 2];
                            st[sp + 1] = st[sp - 1];
                            sp += 2;
                            break;
                        case Op.DupUnder:
                            st[sp] = st[sp - 2];
                            sp++;
                            break;
                        case Op.Swap:
                            (st[sp - 1], st[sp - 2]) = (st[sp - 2], st[sp - 1]);
                            break;
                        case Op.Rot3:
                            {
                                JsValue a = st[sp - 3];
                                st[sp - 3] = st[sp - 2];
                                st[sp - 2] = st[sp - 1];
                                st[sp - 1] = a;
                                break;
                            }

                        case Op.Rot4:
                            {
                                JsValue a = st[sp - 4];
                                st[sp - 4] = st[sp - 3];
                                st[sp - 3] = st[sp - 2];
                                st[sp - 2] = st[sp - 1];
                                st[sp - 1] = a;
                                break;
                            }

                        case Op.LoadLocal:
                            st[sp++] = st[ins.A];
                            break;
                        case Op.StoreLocal:
                            st[ins.A] = st[--sp];
                            break;
                        case Op.SetLocal:
                            st[ins.A] = st[sp - 1];
                            break;

                        // ---- bindings ----
                        case Op.LoadSlot:
                            {
                                Env e = f.Env!;
                                for (int h = ins.A; h > 0; h--)
                                {
                                    e = e.Parent!;
                                }

                                st[sp++] = e.Slots[ins.B];
                                break;
                            }

                        case Op.LoadSlotChecked:
                            {
                                Env e = f.Env!;
                                for (int h = ins.A; h > 0; h--)
                                {
                                    e = e.Parent!;
                                }

                                JsValue v = e.Slots[ins.B];
                                if (v.IsTdz)
                                {
                                    throw Errors.Reference("Cannot access '" + e.Info!.Names[ins.B] + "' before initialization");
                                }

                                st[sp++] = v;
                                break;
                            }

                        case Op.StoreSlot:
                            {
                                Env e = f.Env!;
                                for (int h = ins.A; h > 0; h--)
                                {
                                    e = e.Parent!;
                                }

                                e.Slots[ins.B] = st[--sp];
                                break;
                            }

                        case Op.StoreSlotChecked:
                            {
                                Env e = f.Env!;
                                for (int h = ins.A; h > 0; h--)
                                {
                                    e = e.Parent!;
                                }

                                if (e.Slots[ins.B].IsTdz)
                                {
                                    throw Errors.Reference("Cannot access '" + e.Info!.Names[ins.B] + "' before initialization");
                                }

                                e.Slots[ins.B] = st[--sp];
                                break;
                            }

                        case Op.InitSlot:
                            {
                                Env e = f.Env!;
                                for (int h = ins.A; h > 0; h--)
                                {
                                    e = e.Parent!;
                                }

                                e.Slots[ins.B] = st[--sp];
                                break;
                            }

                        case Op.ThrowConstAssign:
                            {
                                Env e = f.Env!;
                                for (int h = ins.A; h > 0; h--)
                                {
                                    e = e.Parent!;
                                }

                                if (e.Slots[ins.B].IsTdz)
                                {
                                    throw Errors.Reference("Cannot access '" + e.Info!.Names[ins.B] + "' before initialization");
                                }

                                throw Errors.Type("Assignment to constant variable.");
                            }

                        case Op.LoadImport:
                            {
                                Env e = f.Env!;
                                for (int h = ins.A; h > 0; h--)
                                {
                                    e = e.Parent!;
                                }

                                st[sp++] = ModuleLoader.ReadImport(e.Slots[ins.B], e.Info!.Names[ins.B]);
                                break;
                            }

                        case Op.LoadGlobal:
                            st[sp++] = LoadGlobal(f, fc.Names[ins.A], fc.Caches[ins.B]);
                            break;
                        case Op.StoreGlobal:
                            StoreGlobal(f, fc.Names[ins.A], st[--sp], ins.B != 0);
                            break;
                        case Op.TypeofGlobal:
                            st[sp++] = JsValue.FromString(TypeofGlobal(f, fc.Names[ins.A]));
                            break;
                        case Op.DeclareGlobals:
                            DeclareGlobals(f.Realm, (GlobalDeclsView)fc.Tables[ins.A]);
                            break;
                        case Op.InitGlobalLexical:
                            f.Realm.GlobalLexicals[fc.Names[ins.A]].Value = st[--sp];
                            break;
                        case Op.DefineGlobalFunction:
                            DefineGlobalFunction(f.Realm, fc.Names[ins.A], st[--sp]);
                            break;
                        case Op.LoadDyn:
                            st[sp++] = LoadDynamic(f, fc.Names[ins.A], out _, false);
                            break;
                        case Op.LoadDynCall:
                            {
                                JsValue v = LoadDynamic(f, fc.Names[ins.A], out JsValue thisV, true);
                                st[sp++] = v;
                                st[sp++] = thisV;
                                break;
                            }

                        case Op.StoreDyn:
                            StoreDynamic(f, fc.Names[ins.A], st[--sp], ins.B != 0, false);
                            break;
                        case Op.ResolveDynRef:
                            st[sp++] = new JsValue(new DynRefBox { R = ResolveDynamic(f, fc.Names[ins.A]) }, 0);
                            break;
                        case Op.GetDynRef:
                            st[sp - 1] = LoadFromDyn(((DynRefBox)st[sp - 1].Ref!).R, fc.Names[ins.A], out _, false);
                            break;
                        case Op.PutDynRef:
                            {
                                JsValue pv = st[--sp];
                                var box = (DynRefBox)st[--sp].Ref!;
                                StoreToDyn(f, box.R, fc.Names[ins.A], pv, (ins.B & 1) != 0, false);
                                if ((ins.B & 2) != 0)
                                {
                                    st[sp++] = pv;
                                }

                                break;
                            }

                        case Op.StoreVarDyn:
                            // B.3.3.3: the copy of a block function goes to the variable environment, past block and with scopes.
                            StoreDynamic(f, fc.Names[ins.A], st[--sp], false, false, true);
                            break;
                        case Op.InitDyn:
                            StoreDynamic(f, fc.Names[ins.A], st[--sp], false, true);
                            break;
                        case Op.TypeofDyn:
                            st[sp++] = JsValue.FromString(TypeofDynamic(f, fc.Names[ins.A]));
                            break;
                        case Op.DeleteDyn:
                            st[sp++] = JsValue.FromBoolean(DeleteDynamic(f, fc.Names[ins.A]));
                            break;
                        case Op.DeclareEvalVar:
                            DeclareEvalVars(f, (GlobalDeclsView)fc.Tables[ins.A]);
                            break;
                        case Op.PushScope:
                            f.Env = Env.Obtain(f.Env, fc.Scopes[ins.A]);
                            break;
                        case Op.PopScope:
                            f.Env = f.Env!.Parent;
                            break;
                        case Op.CopyScope:
                            f.Env = f.Env!.Copy();
                            break;
                        case Op.PushWith:
                            f.Env = new Env(f.Env, Ops.ToObject(st[--sp]));
                            break;
                        case Op.LoadArg:
                            st[sp++] = ins.A < f.ArgCount ? f.ArgArr![f.ArgOff + ins.A] : default;
                            break;
                        case Op.CreateArguments:
                            st[sp++] = JsValue.FromObject(CreateArguments(f, ins.A < 0 ? null : (int[])fc.Tables[ins.A]));
                            break;
                        case Op.CollectRest:
                            {
                                var arr = new JsArray(f.Realm.ArrayPrototype);
                                for (int i = ins.A; i < f.ArgCount; i++)
                                {
                                    arr.Push(f.ArgArr![f.ArgOff + i]);
                                }

                                st[sp++] = JsValue.FromObject(arr);
                                break;
                            }

                        case Op.LoadThis:
                            {
                                JsValue t = f.ThisFrame.This;
                                if (t.IsTdz)
                                {
                                    throw Errors.Reference("Must call super constructor in derived class before accessing 'this' or returning from derived constructor");
                                }

                                st[sp++] = t;
                                break;
                            }

                        case Op.LoadNewTarget:
                            st[sp++] = f.ThisFrame.NewTarget is { } nt ? JsValue.FromObject(nt) : default;
                            break;
                        case Op.LoadFunction:
                            st[sp++] = f.ThisFrame.Func is { } thisFn ? JsValue.FromObject(thisFn) : default;
                            break;

                        // ---- properties ----
                        case Op.GetProp:
                            {
                                JsValue o = st[sp - 1];
                                if (o.Ref is JsObject obj)
                                {
                                    PropCache c = fc.Caches[ins.B];
                                    if (ReferenceEquals(obj.Shape, c.Shape))
                                    {
                                        if (c.Holder is null)
                                        {
                                            st[sp - 1] = obj.Slots[c.Slot];
                                            break;
                                        }

                                        if (ReferenceEquals(c.Holder.Shape, c.HolderShape))
                                        {
                                            st[sp - 1] = c.Holder.Slots[c.Slot];
                                            break;
                                        }
                                    }

                                    st[sp - 1] = GetPropSlow(obj, o, fc.Names[ins.A], c);
                                }
                                else if (o.Ref is string or Rope)
                                {
                                    st[sp - 1] = GetStringProp(ag.Realm, o, fc.Names[ins.A], fc.Caches[ins.B]);
                                }
                                else
                                {
                                    st[sp - 1] = Ops.GetV(o, new PropertyKey(fc.Names[ins.A]));
                                }

                                break;
                            }

                        case Op.GetMethod:
                            {
                                JsValue o = st[sp - 1];
                                JsValue fnv;
                                if (o.Ref is JsObject obj)
                                {
                                    PropCache c = fc.Caches[ins.B];
                                    if (ReferenceEquals(obj.Shape, c.Shape) && (c.Holder is null ? true : ReferenceEquals(c.Holder.Shape, c.HolderShape)))
                                    {
                                        fnv = c.Holder is null ? obj.Slots[c.Slot] : c.Holder.Slots[c.Slot];
                                    }
                                    else
                                    {
                                        fnv = GetPropSlow(obj, o, fc.Names[ins.A], c);
                                    }
                                }
                                else if (o.Ref is string or Rope)
                                {
                                    fnv = GetStringProp(ag.Realm, o, fc.Names[ins.A], fc.Caches[ins.B]);
                                }
                                else
                                {
                                    fnv = Ops.GetV(o, new PropertyKey(fc.Names[ins.A]));
                                }

                                st[sp - 1] = fnv;
                                st[sp++] = o;
                                break;
                            }

                        case Op.SetProp:
                            {
                                JsValue v = st[--sp];
                                JsValue o = st[sp - 1];
                                if (o.Ref is JsObject obj && !obj.OverridesSet)
                                {
                                    PropCache c = fc.Caches[ins.B];
                                    if (ReferenceEquals(obj.Shape, c.Shape) && c.Holder is null)
                                    {
                                        obj.Slots[c.Slot] = v;
                                        st[sp - 1] = v;
                                        break;
                                    }

                                    SetPropSlow(obj, fc.Names[ins.A], v, c, fc.Strict);
                                }
                                else
                                {
                                    Ops.SetV(o, new PropertyKey(fc.Names[ins.A]), v, fc.Strict);
                                }

                                st[sp - 1] = v;
                                break;
                            }

                        case Op.GetElem:
                            {
                                JsValue k = st[--sp];
                                JsValue o = st[sp - 1];
                                st[sp - 1] = GetElem(o, k);
                                break;
                            }

                        case Op.GetElemMethod:
                            {
                                JsValue k = st[sp - 1];
                                JsValue o = st[sp - 2];
                                st[sp - 2] = GetElem(o, k);
                                st[sp - 1] = o;
                                break;
                            }

                        case Op.SetElem:
                            {
                                JsValue v = st[--sp];
                                JsValue k = st[--sp];
                                JsValue o = st[sp - 1];
                                SetElem(o, k, v, fc.Strict);
                                st[sp - 1] = v;
                                break;
                            }

                        case Op.DeleteProp:
                            {
                                JsValue o = st[sp - 1];
                                st[sp - 1] = JsValue.FromBoolean(DeleteMember(o, new PropertyKey(fc.Names[ins.A]), ins.B != 0));
                                break;
                            }

                        case Op.DeleteElem:
                            {
                                JsValue k = st[--sp];
                                JsValue o = st[sp - 1];
                                if (o.IsNullish)
                                {
                                    throw Errors.Type("Cannot convert undefined or null to object");
                                }

                                st[sp - 1] = JsValue.FromBoolean(DeleteMember(o, Ops.ToPropertyKey(k), ins.B != 0));
                                break;
                            }

                        case Op.GetSuper:
                            st[sp++] = SuperGet(f, new PropertyKey(fc.Names[ins.A]));
                            break;
                        case Op.GetSuperElem:
                            st[sp - 1] = SuperGet(f, Ops.ToPropertyKey(st[sp - 1]));
                            break;
                        case Op.SetSuper:
                            SuperSet(f, new PropertyKey(fc.Names[ins.A]), st[sp - 1], fc.Strict);
                            break;
                        case Op.SetSuperElem:
                            {
                                JsValue v = st[--sp];
                                SuperSet(f, Ops.ToPropertyKey(st[sp - 1]), v, fc.Strict);
                                st[sp - 1] = v;
                                break;
                            }

                        case Op.ToPropKey:
                            {
                                JsValue k = st[sp - 1];
                                if (ins.B == 1 && st[sp - 2].IsNullish)
                                {
                                    // The base is checked before the key is converted (ECMA-262 13.15.2 / 6.2.5.5 GetValue order).
                                    throw Errors.Type("Cannot read properties of " + (st[sp - 2].IsNull ? "null" : "undefined") + KeySuffix("reading", k));
                                }

                                if (!k.IsString && !k.IsNumber && !k.IsSymbol)
                                {
                                    st[sp - 1] = Ops.ToPropertyKey(k).ToValue();
                                }

                                break;
                            }

                        case Op.PrivateGet:
                            {
                                JsValue pn = st[--sp];
                                st[sp - 1] = PrivateGet(st[sp - 1], (PrivateName)pn.Ref!);
                                break;
                            }

                        case Op.PrivateSet:
                            {
                                JsValue v = st[--sp];
                                JsValue pn = st[--sp];
                                PrivateSet(st[sp - 1], (PrivateName)pn.Ref!, v);
                                st[sp - 1] = v;
                                break;
                            }

                        case Op.PrivateIn:
                            {
                                JsValue o = st[--sp];
                                JsValue pn = st[sp - 1];
                                if (o.Ref is not JsObject po)
                                {
                                    throw Errors.Type("Cannot use 'in' operator to search for '#" + ((PrivateName)pn.Ref!).Description.TrimStart('#') + "' in " + Ops.DisplayValue(o));
                                }

                                st[sp - 1] = JsValue.FromBoolean(po.PrivateHas((PrivateName)pn.Ref!));
                                break;
                            }

                        case Op.In:
                            {
                                JsValue o = st[--sp];
                                JsValue k = st[sp - 1];
                                if (o.Ref is not JsObject io)
                                {
                                    throw Errors.Type("Cannot use 'in' operator to search for '" + (k.IsSymbol ? k.ToString() : Ops.ToStr(k)) + "' in " + (o.IsString ? o.AsString : Ops.DisplayValue(o)));
                                }

                                st[sp - 1] = JsValue.FromBoolean(io.HasProperty(Ops.ToPropertyKey(k)));
                                break;
                            }

                        case Op.InstanceOf:
                            {
                                JsValue c = st[--sp];
                                st[sp - 1] = JsValue.FromBoolean(Ops.InstanceOf(st[sp - 1], c));
                                break;
                            }

                        // ---- literals ----
                        case Op.NewObject:
                            ag.Alloc(64);
                            st[sp++] = JsValue.FromObject(new JsObject(f.Realm.ObjectPrototype));
                            break;
                        case Op.NewArray:
                            ag.Alloc(48);
                            st[sp++] = JsValue.FromObject(new JsArray(f.Realm.ArrayPrototype));
                            break;
                        case Op.ArrayPush:
                            {
                                JsValue v = st[--sp];
                                ((JsArray)st[sp - 1].Ref!).Push(v);
                                break;
                            }

                        case Op.ArrayPushHole:
                            {
                                var arr = (JsArray)st[sp - 1].Ref!;
                                if (arr.Dense is not null && arr.Count == arr.Length)
                                {
                                    if (arr.Count == arr.Dense.Length)
                                    {
                                        Array.Resize(ref arr.Dense, Math.Max(4, arr.Count * 2));
                                    }

                                    arr.Dense[arr.Count++] = JsValue.Hole;
                                    arr.Length++;
                                }
                                else
                                {
                                    arr.Length++;
                                }

                                break;
                            }

                        case Op.ArraySpread:
                            {
                                JsValue it = st[--sp];
                                SpreadInto((JsArray)st[sp - 1].Ref!, it, ins.A == 1, fc.Names[ins.B]);
                                break;
                            }

                        case Op.DefineField:
                            {
                                JsValue v = st[--sp];
                                ((JsObject)st[sp - 1].Ref!).DefineLiteralProperty(new PropertyKey(fc.Names[ins.A]), v);
                                break;
                            }

                        case Op.DefineFieldComputed:
                            {
                                JsValue v = st[--sp];
                                JsValue k = st[--sp];
                                ((JsObject)st[sp - 1].Ref!).DefineLiteralProperty(Ops.ToPropertyKey(k), v);
                                break;
                            }

                        case Op.SetProtoLiteral:
                            {
                                JsValue v = st[--sp];
                                if (v.IsObject || v.IsNull)
                                {
                                    ((JsObject)st[sp - 1].Ref!).SetPrototypeOf(v.AsObject);
                                }

                                break;
                            }

                        case Op.DefineMethodOp:
                            {
                                JsValue fnv = st[--sp];
                                PropertyKey key;
                                if (ins.A >= 0)
                                {
                                    key = new PropertyKey(fc.Names[ins.A]);
                                }
                                else
                                {
                                    key = Ops.ToPropertyKey(st[--sp]);
                                }

                                DefineMethod((JsObject)st[sp - 1].Ref!, key, (ScriptFunction)fnv.Ref!, ins.B);
                                break;
                            }

                        case Op.CopyDataProps:
                            {
                                JsValue src = st[--sp];
                                CopyDataProperties((JsObject)st[sp - 1].Ref!, src, null);
                                break;
                            }

                        case Op.ObjectRest:
                            {
                                JsValue keys = st[--sp];
                                var rest = new JsObject(f.Realm.ObjectPrototype);
                                var ex = new List<PropertyKey>();
                                foreach (JsValue kv in ((JsArray)keys.Ref!).Dense!.AsSpan(0, ((JsArray)keys.Ref!).Count))
                                {
                                    ex.Add(Ops.ToPropertyKey(kv));
                                }

                                CopyDataProperties(rest, st[sp - 1], ex);
                                st[sp - 1] = JsValue.FromObject(rest);
                                break;
                            }

                        case Op.NewRegExp:
                            st[sp++] = RegExpHook.Create(f.Realm, fc.Names[ins.A], fc.Names[ins.B]);
                            break;
                        case Op.TemplateObject:
                            st[sp++] = JsValue.FromObject(f.Realm.GetTemplateObject(fc, ins.A, (TemplateSite)fc.Tables[ins.A]));
                            break;
                        case Op.SetFnNameKey:
                            SetFunctionNameFromKey(st[sp - 1], st[sp - 2], ins.A);
                            break;
                        case Op.NewPrivateName:
                            st[sp++] = new JsValue(new PrivateName(fc.Names[ins.A]), 0);
                            break;
                        case Op.NewClass:
                            {
                                JsValue heritage = default;
                                if ((ins.B & 1) != 0)
                                {
                                    heritage = st[--sp];
                                }

                                NewClass(f, fc.Functions[ins.A], (ins.B & 1) != 0, heritage, out JsObject ctor, out JsObject proto);
                                st[sp++] = JsValue.FromObject(ctor);
                                st[sp++] = JsValue.FromObject(proto);
                                break;
                            }

                        case Op.DefineClassField:
                            {
                                JsValue init = st[--sp];
                                JsValue key = st[--sp];
                                var ctor = (ScriptFunction)st[--sp].Ref!;
                                DefineClassField(ctor, key, init, ins.A);
                                break;
                            }

                        case Op.AddPrivateMethod:
                            {
                                JsValue fnv = st[--sp];
                                JsValue pn = st[--sp];
                                var ctor = (ScriptFunction)st[--sp].Ref!;
                                AddPrivateMethod(ctor, (PrivateName)pn.Ref!, (ScriptFunction)fnv.Ref!, ins.A);
                                break;
                            }

                        case Op.RunStaticBlock:
                            {
                                JsValue fnv = st[--sp];
                                JsValue ctor = st[--sp];
                                var blk = (ScriptFunction)fnv.Ref!;
                                blk.HomeObject = (JsObject)ctor.Ref!;
                                blk.Call(ctor, default);
                                break;
                            }

                        // ---- operators ----
                        case Op.Add:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = a.IsNumber && b.IsNumber ? JsValue.FromNumber(a.Num + b.Num) : Ops.Add(a, b);
                                break;
                            }

                        case Op.Sub:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = a.IsNumber && b.IsNumber ? JsValue.FromNumber(a.Num - b.Num) : Ops.Arith(BinaryOp.Subtract, a, b);
                                break;
                            }

                        case Op.Mul:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = a.IsNumber && b.IsNumber ? JsValue.FromNumber(a.Num * b.Num) : Ops.Arith(BinaryOp.Multiply, a, b);
                                break;
                            }

                        case Op.Div:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = a.IsNumber && b.IsNumber ? JsValue.FromNumber(a.Num / b.Num) : Ops.Arith(BinaryOp.Divide, a, b);
                                break;
                            }

                        case Op.Mod:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = a.IsNumber && b.IsNumber ? JsValue.FromNumber(Ops.Remainder(a.Num, b.Num)) : Ops.Arith(BinaryOp.Remainder, a, b);
                                break;
                            }

                        case Op.Exp:
                            {
                                JsValue b = st[--sp];
                                st[sp - 1] = Ops.Arith(BinaryOp.Exponent, st[sp - 1], b);
                                break;
                            }

                        case Op.BitAnd:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = a.IsNumber && b.IsNumber ? JsValue.FromNumber(Ops.ToInt32(a.Num) & Ops.ToInt32(b.Num)) : Ops.Arith(BinaryOp.BitwiseAnd, a, b);
                                break;
                            }

                        case Op.BitOr:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = a.IsNumber && b.IsNumber ? JsValue.FromNumber(Ops.ToInt32(a.Num) | Ops.ToInt32(b.Num)) : Ops.Arith(BinaryOp.BitwiseOr, a, b);
                                break;
                            }

                        case Op.BitXor:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = a.IsNumber && b.IsNumber ? JsValue.FromNumber(Ops.ToInt32(a.Num) ^ Ops.ToInt32(b.Num)) : Ops.Arith(BinaryOp.BitwiseXor, a, b);
                                break;
                            }

                        case Op.Shl:
                            {
                                JsValue b = st[--sp];
                                st[sp - 1] = Ops.Arith(BinaryOp.ShiftLeft, st[sp - 1], b);
                                break;
                            }

                        case Op.Shr:
                            {
                                JsValue b = st[--sp];
                                st[sp - 1] = Ops.Arith(BinaryOp.ShiftRight, st[sp - 1], b);
                                break;
                            }

                        case Op.UShr:
                            {
                                JsValue b = st[--sp];
                                st[sp - 1] = Ops.Arith(BinaryOp.UnsignedShiftRight, st[sp - 1], b);
                                break;
                            }

                        case Op.Eq:
                            {
                                JsValue b = st[--sp];
                                st[sp - 1] = JsValue.FromBoolean(Ops.LooseEquals(st[sp - 1], b));
                                break;
                            }

                        case Op.Ne:
                            {
                                JsValue b = st[--sp];
                                st[sp - 1] = JsValue.FromBoolean(!Ops.LooseEquals(st[sp - 1], b));
                                break;
                            }

                        case Op.StrictEq:
                            {
                                JsValue b = st[--sp];
                                st[sp - 1] = JsValue.FromBoolean(Ops.StrictEquals(st[sp - 1], b));
                                break;
                            }

                        case Op.StrictNe:
                            {
                                JsValue b = st[--sp];
                                st[sp - 1] = JsValue.FromBoolean(!Ops.StrictEquals(st[sp - 1], b));
                                break;
                            }

                        case Op.Lt:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = JsValue.FromBoolean(a.IsNumber && b.IsNumber ? a.Num < b.Num : Ops.LessThan(a, b, true) == 1);
                                break;
                            }

                        case Op.Gt:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = JsValue.FromBoolean(a.IsNumber && b.IsNumber ? a.Num > b.Num : Ops.LessThan(b, a, false) == 1);
                                break;
                            }

                        case Op.Le:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = JsValue.FromBoolean(a.IsNumber && b.IsNumber ? a.Num <= b.Num : Ops.LessThan(b, a, false) == 0);
                                break;
                            }

                        case Op.Ge:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[sp - 1];
                                st[sp - 1] = JsValue.FromBoolean(a.IsNumber && b.IsNumber ? a.Num >= b.Num : Ops.LessThan(a, b, true) == 0);
                                break;
                            }

                        case Op.Neg:
                            {
                                JsValue a = st[sp - 1];
                                if (a.IsNumber)
                                {
                                    st[sp - 1] = JsValue.FromNumber(-a.Num);
                                }
                                else
                                {
                                    JsValue n = Ops.ToNumeric(a);
                                    st[sp - 1] = n.IsNumber ? JsValue.FromNumber(-n.Num) : JsValue.FromBigInt(-(BigInteger)n.Ref!);
                                }

                                break;
                            }

                        case Op.Plus:
                            st[sp - 1] = JsValue.FromNumber(Ops.ToNumber(st[sp - 1]));
                            break;
                        case Op.Not:
                            st[sp - 1] = JsValue.FromBoolean(!Ops.ToBoolean(st[sp - 1]));
                            break;
                        case Op.BitNot:
                            {
                                JsValue n = Ops.ToNumeric(st[sp - 1]);
                                st[sp - 1] = n.IsNumber ? JsValue.FromNumber(~Ops.ToInt32(n.Num)) : JsValue.FromBigInt(-(BigInteger)n.Ref! - 1);
                                break;
                            }

                        case Op.Typeof:
                            st[sp - 1] = JsValue.FromString(st[sp - 1].TypeOf);
                            break;
                        case Op.ToNumeric:
                            if (!st[sp - 1].IsNumber)
                            {
                                st[sp - 1] = Ops.ToNumeric(st[sp - 1]);
                            }

                            break;
                        case Op.Inc:
                            {
                                JsValue a = st[sp - 1];
                                st[sp - 1] = a.IsNumber ? JsValue.FromNumber(a.Num + 1) : JsValue.FromBigInt((BigInteger)a.Ref! + 1);
                                break;
                            }

                        case Op.Dec:
                            {
                                JsValue a = st[sp - 1];
                                st[sp - 1] = a.IsNumber ? JsValue.FromNumber(a.Num - 1) : JsValue.FromBigInt((BigInteger)a.Ref! - 1);
                                break;
                            }

                        case Op.ToStringOp:
                            if (!st[sp - 1].IsString)
                            {
                                st[sp - 1] = JsValue.FromString(Ops.ToStr(st[sp - 1]));
                            }

                            break;
                        case Op.RequireObjectCoercible:
                            Ops.RequireObjectCoercible(st[sp - 1]);
                            break;
                        case Op.TemplateConcat:
                            {
                                JsValue b = st[--sp];
                                st[sp - 1] = Ops.Concat(st[sp - 1], b);
                                break;
                            }

                        // ---- control ----
                        case Op.Jmp:
                            pc = ins.A;
                            break;
                        case Op.JmpBack:
                            if (--ag.Fuel < 0)
                            {
                                ag.Tick();
                            }

                            pc = ins.A;
                            break;
                        case Op.JmpIfFalse:
                            {
                                JsValue c = st[--sp];
                                if (c.Ref is Tag t0 ? (ReferenceEquals(t0, JsValue.FalseTag) || ReferenceEquals(t0, JsValue.NullTag) || (ReferenceEquals(t0, JsValue.NumberTag) && (c.Num == 0 || double.IsNaN(c.Num)))) : !Ops.ToBoolean(c))
                                {
                                    pc = ins.A;
                                }

                                break;
                            }

                        case Op.JmpIfTrue:
                            if (Ops.ToBoolean(st[--sp]))
                            {
                                pc = ins.A;
                            }

                            break;
                        case Op.JmpIfFalseKeep:
                            if (!Ops.ToBoolean(st[sp - 1]))
                            {
                                pc = ins.A;
                            }
                            else
                            {
                                sp--;
                            }

                            break;
                        case Op.JmpIfTrueKeep:
                            if (Ops.ToBoolean(st[sp - 1]))
                            {
                                pc = ins.A;
                            }
                            else
                            {
                                sp--;
                            }

                            break;
                        case Op.JmpIfNotNullishKeep:
                            if (!st[sp - 1].IsNullish)
                            {
                                pc = ins.A;
                            }
                            else
                            {
                                sp--;
                            }

                            break;
                        case Op.JmpIfNotUndefKeep:
                            if (!st[sp - 1].IsUndefined)
                            {
                                pc = ins.A;
                            }
                            else
                            {
                                sp--;
                            }

                            break;
                        case Op.JmpNullishUndef:
                            if (st[sp - 1].IsNullish)
                            {
                                st[sp - 1] = default;
                                pc = ins.A;
                            }

                            break;
                        case Op.JmpNullishUndef2:
                            if (st[sp - 2].IsNullish)
                            {
                                sp -= 1;
                                st[sp - 1] = default;
                                pc = ins.A;
                            }

                            break;
                        case Op.JmpIfStrictEq:
                            {
                                JsValue b = st[--sp];
                                JsValue a = st[--sp];
                                if (Ops.StrictEquals(a, b))
                                {
                                    pc = ins.A;
                                }

                                break;
                            }

                        case Op.TryEnter:
                            (f.Handlers ??= new List<Handler>(2)).Add(new Handler { Pc = ins.A, Sp = sp - ins.B, Env = f.Env });
                            break;
                        case Op.TryLeave:
                            f.Handlers!.RemoveAt(f.Handlers.Count - 1);
                            break;
                        case Op.Throw:
                            thrown = st[--sp];
                            hasThrown = true;
                            goto handle;
                        case Op.ThrowError:
                            throw ins.A switch
                            {
                                1 => Errors.Reference(fc.Names[ins.B]),
                                2 => Errors.Syntax(fc.Names[ins.B]),
                                3 => Errors.Range(fc.Names[ins.B]),
                                _ => Errors.Type(fc.Names[ins.B]),
                            };
                        case Op.ReturnUndef:
                            st[sp++] = default;
                            goto case Op.Return;
                        case Op.Return:
                            {
                                JsValue rv = st[--sp];
                                if (f.IsConstruct)
                                {
                                    // 10.2.2 [[Construct]] steps 10-13 run after the body has finished, so a try block around the
                                    // return statement cannot catch the TypeError or ReferenceError raised here.
                                    f.Handlers?.Clear();
                                    rv = ConstructResult(f, rv);
                                }

                                ag.CallDepth--;
                                f.State = FrameState.Completed;
                                if (f.Code.IsAsync && f.Owner is PromiseObject ap && !f.Code.IsGenerator && f.Code.Kind != FunctionKind.Module)
                                {
                                    Promises.ResolveWith(ap, rv);
                                    rv = JsValue.FromObject(ap);
                                }

                                if (f.IsEntry)
                                {
                                    ag.Frame = f.Caller;
                                    f.Sp = sp;
                                    return rv;
                                }

                                Frame done = f;
                                f = f.Caller!;
                                ag.Frame = f;
                                ag.Realm = f.Realm;
                                st = f.Stack;
                                sp = f.Sp;
                                pc = f.Pc + 1;
                                code = f.Code.Ops;
                                fc = f.Code;
                                st[sp++] = rv;
                                if (typeof(TTrace) == typeof(NoTrace))
                                {
                                    Frame.Recycle(done);
                                }

                                break;
                            }

                        // ---- calls ----
                        case Op.Call:
                            {
                                int argc = ins.A;
                                int bs = sp - argc - 2;
                                JsValue fv = st[bs];
                                if (fv.Ref is ScriptFunction sf)
                                {
                                    if (--ag.Fuel < 0)
                                    {
                                        ag.Tick();
                                    }

                                    if (ag.CallDepth >= ag.Limits.MaxCallDepth)
                                    {
                                        throw Errors.Range("Maximum call stack size exceeded");
                                    }

                                    Frame nf = PrepareFrame(ag, sf, st[bs + 1], st, bs + 2, argc, null);
                                    f.Sp = bs;
                                    nf.Caller = f;
                                    f = nf;
                                    ag.CallDepth++;
                                    ag.Frame = f;
                                    ag.Realm = f.Realm;
                                    st = f.Stack;
                                    sp = f.Sp;
                                    pc = 0;
                                    code = f.Code.Ops;
                                    fc = f.Code;
                                    break;
                                }

                                if (fv.Ref is JsObject fo && fo.IsCallable)
                                {
                                    if (--ag.Fuel < 0)
                                    {
                                        ag.Tick();
                                    }

                                    JsValue r = fo.Call(st[bs + 1], new ReadOnlySpan<JsValue>(st, bs + 2, argc));
                                    sp = bs;
                                    st[sp++] = r;
                                    break;
                                }

                                throw Errors.Type(fc.Names[ins.B] + " is not a function");
                            }

                        case Op.TailCall:
                        case Op.TailCallEval:
                            {
                                int argc = ins.A;
                                int bs = sp - argc - 2;
                                if (st[bs].Ref is ScriptFunction sf && !f.IsConstruct)
                                {
                                    // ECMA-262 PrepareForTailCall (7.3.?): the callee takes this frame's place, so depth does not grow.
                                    if (--ag.Fuel < 0)
                                    {
                                        ag.Tick();
                                    }

                                    Frame nf = PrepareFrame(ag, sf, st[bs + 1], st, bs + 2, argc, null);
                                    f.Sp = bs;
                                    f.State = FrameState.Completed;
                                    nf.Caller = f.Caller;
                                    nf.IsEntry = f.IsEntry;
                                    f = nf;
                                    ag.Frame = f;
                                    ag.Realm = f.Realm;
                                    st = f.Stack;
                                    sp = f.Sp;
                                    pc = 0;
                                    code = f.Code.Ops;
                                    fc = f.Code;
                                    break;
                                }

                                if (ins.Op == Op.TailCallEval)
                                {
                                    goto case Op.CallEval;
                                }

                                goto case Op.Call;
                            }

                        case Op.CallSpread:
                            {
                                JsValue[] args = ArgsFromArray(st[--sp]);
                                int bs = sp - 2;
                                JsValue fv = st[bs];
                                JsValue r = CallValue(fv, st[bs + 1], args, fc.Names[ins.B]);
                                sp = bs;
                                st[sp++] = r;
                                break;
                            }

                        case Op.New:
                            {
                                int argc = ins.A;
                                int bs = sp - argc - 1;
                                JsValue cv = st[bs];
                                if (cv.Ref is ScriptFunction sf && sf.IsConstructor)
                                {
                                    if (--ag.Fuel < 0)
                                    {
                                        ag.Tick();
                                    }

                                    if (ag.CallDepth >= ag.Limits.MaxCallDepth)
                                    {
                                        throw Errors.Range("Maximum call stack size exceeded");
                                    }

                                    Frame nf = PrepareFrame(ag, sf, default, st, bs + 1, argc, sf);
                                    f.Sp = bs;
                                    nf.Caller = f;
                                    f = nf;
                                    ag.CallDepth++;
                                    ag.Frame = f;
                                    ag.Realm = f.Realm;
                                    st = f.Stack;
                                    sp = f.Sp;
                                    pc = 0;
                                    code = f.Code.Ops;
                                    fc = f.Code;
                                    break;
                                }

                                if (cv.Ref is JsObject co && co.IsConstructor)
                                {
                                    JsValue r = co.Construct(new ReadOnlySpan<JsValue>(st, bs + 1, argc), co);
                                    sp = bs;
                                    st[sp++] = r;
                                    break;
                                }

                                throw Errors.Type(fc.Names[ins.B] + " is not a constructor");
                            }

                        case Op.NewSpread:
                            {
                                JsValue[] args = ArgsFromArray(st[--sp]);
                                int bs = sp - 1;
                                JsValue cv = st[bs];
                                if (cv.Ref is not JsObject co || !co.IsConstructor)
                                {
                                    throw Errors.Type(fc.Names[ins.B] + " is not a constructor");
                                }

                                JsValue r = co.Construct(args, co);
                                sp = bs;
                                st[sp++] = r;
                                break;
                            }

                        case Op.SuperCall:
                            {
                                JsValue[] args;
                                if (ins.A < 0)
                                {
                                    Frame tf = f.ThisFrame;
                                    args = new JsValue[tf.ArgCount];
                                    Array.Copy(tf.ArgArr!, tf.ArgOff, args, 0, tf.ArgCount);
                                }
                                else
                                {
                                    args = new JsValue[ins.A];
                                    Array.Copy(st, sp - ins.A, args, 0, ins.A);
                                    sp -= ins.A;
                                }

                                st[sp++] = SuperCall(f, args);
                                break;
                            }

                        case Op.SuperCallSpread:
                            st[sp - 1] = SuperCall(f, ArgsFromArray(st[sp - 1]));
                            break;
                        case Op.CallEval:
                        case Op.CallEvalSpread:
                            {
                                JsValue[] args;
                                int bs;
                                if (ins.Op != Op.CallEvalSpread)
                                {
                                    args = new JsValue[ins.A];
                                    Array.Copy(st, sp - ins.A, args, 0, ins.A);
                                    bs = sp - ins.A - 2;
                                }
                                else
                                {
                                    args = ArgsFromArray(st[--sp]);
                                    bs = sp - 2;
                                }

                                JsValue fv = st[bs];
                                if (fv.Ref is JsObject eo && ReferenceEquals(eo, f.Realm.EvalFunction))
                                {
                                    sp = bs;
                                    f.Sp = sp;
                                    if (args.Length == 0 || !args[0].IsString)
                                    {
                                        st[sp++] = args.Length == 0 ? default : args[0];
                                        break;
                                    }

                                    Frame ef = PrepareDirectEval(ag, f, args[0].AsString, ins.Op != Op.CallEvalSpread ? ins.B : EvalFlagsFor(f));
                                    ef.Caller = f;
                                    f = ef;
                                    ag.CallDepth++;
                                    ag.Frame = f;
                                    st = f.Stack;
                                    sp = f.Sp;
                                    pc = 0;
                                    code = f.Code.Ops;
                                    fc = f.Code;
                                    break;
                                }

                                JsValue r = CallValue(fv, st[bs + 1], args, ins.Op != Op.CallEvalSpread ? "eval" : fc.Names[ins.B]);
                                sp = bs;
                                st[sp++] = r;
                                break;
                            }

                        case Op.ImportCallOp:
                            {
                                JsValue opts = ins.A == 1 ? st[--sp] : default;
                                st[sp - 1] = ModuleLoader.DynamicImport(f, st[sp - 1], opts);
                                break;
                            }

                        case Op.ImportMeta:
                            st[sp++] = JsValue.FromObject(ModuleLoader.ImportMeta(f));
                            break;
                        case Op.MakeClosure:
                            st[sp++] = JsValue.FromObject(MakeClosure(f, fc.Functions[ins.A]));
                            break;

                        // ---- iteration ----
                        case Op.GetIterator:
                            {
                                JsValue iterable = st[sp - 1];
                                IteratorRecord rec;
                                if (ins.A == 1)
                                {
                                    rec = Ops.GetIterator(iterable, true);
                                }
                                else
                                {
                                    JsValue m = iterable.IsNullish ? default : Ops.GetV(iterable, new PropertyKey(WellKnown.Iterator));
                                    if (!Ops.IsCallable(m))
                                    {
                                        throw Errors.Type(IterErrorText(iterable, fc.Names[ins.B]));
                                    }

                                    rec = Ops.GetIteratorFromMethod(iterable, m);
                                }

                                st[sp - 1] = JsValue.FromObject(rec.Iterator);
                                st[sp++] = rec.Next;
                                break;
                            }

                        case Op.IterNext:
                            {
                                if (--ag.Fuel < 0)
                                {
                                    ag.Tick();
                                }

                                var it = (JsObject)st[sp - 2].Ref!;
                                JsValue r = Ops.Call(st[sp - 1], JsValue.FromObject(it));
                                if (r.Ref is not JsObject ro)
                                {
                                    throw Errors.Type("Iterator result " + Ops.DisplayValue(r) + " is not an object");
                                }

                                if (Ops.ToBoolean(ro.Get("done")))
                                {
                                    pc = ins.A;
                                }
                                else
                                {
                                    st[sp++] = ro.Get("value");
                                }

                                break;
                            }

                        case Op.IterStep:
                            {
                                if (st[ins.A].IsTrue)
                                {
                                    st[sp++] = default;
                                    break;
                                }

                                var it = (JsObject)st[sp - 2].Ref!;
                                JsValue r;
                                try
                                {
                                    r = Ops.Call(st[sp - 1], JsValue.FromObject(it));
                                    if (r.Ref is not JsObject ro)
                                    {
                                        throw Errors.Type("Iterator result " + Ops.DisplayValue(r) + " is not an object");
                                    }

                                    if (Ops.ToBoolean(ro.Get("done")))
                                    {
                                        st[ins.A] = JsValue.True;
                                        st[sp++] = default;
                                    }
                                    else
                                    {
                                        st[sp++] = ro.Get("value");
                                    }
                                }
                                catch (JsException)
                                {
                                    st[ins.A] = JsValue.True;
                                    throw;
                                }

                                break;
                            }

                        case Op.IterRest:
                            {
                                var arr = new JsArray(f.Realm.ArrayPrototype);
                                if (!st[ins.A].IsTrue)
                                {
                                    var rec = new IteratorRecord((JsObject)st[sp - 2].Ref!, st[sp - 1]);
                                    try
                                    {
                                        while (Ops.IteratorStepValue(rec, out JsValue v))
                                        {
                                            ag.Tick();
                                            arr.Push(v);
                                        }
                                    }
                                    finally
                                    {
                                        st[ins.A] = JsValue.True;
                                    }
                                }

                                st[sp++] = JsValue.FromObject(arr);
                                break;
                            }

                        case Op.IterClose:
                            Ops.IteratorClose(new IteratorRecord((JsObject)st[sp - 2 - ins.B].Ref!, st[sp - 1 - ins.B]));
                            break;
                        case Op.IterCloseIfNotDone:
                            if (!st[ins.A].IsTrue)
                            {
                                Ops.IteratorClose(new IteratorRecord((JsObject)st[sp - 2 - ins.B].Ref!, st[sp - 1 - ins.B]));
                            }

                            break;
                        case Op.IterCloseSilent:
                            if (ins.A < 0 || !st[ins.A].IsTrue)
                            {
                                Ops.IteratorCloseSilently(new IteratorRecord((JsObject)st[sp - 3].Ref!, st[sp - 2]));
                            }

                            break;
                        case Op.ForInStart:
                            st[sp - 1] = new JsValue(new ForInState(st[sp - 1]), 0);
                            break;
                        case Op.ForInNext:
                            {
                                if (--ag.Fuel < 0)
                                {
                                    ag.Tick();
                                }

                                var fi = (ForInState)st[sp - 1].Ref!;
                                if (fi.Next(out string? key))
                                {
                                    st[sp++] = JsValue.FromString(key!);
                                }
                                else
                                {
                                    pc = ins.A;
                                }

                                break;
                            }

                        case Op.AsyncIterNextStart:
                            {
                                var it = (JsObject)st[sp - 2].Ref!;
                                st[sp++] = Ops.Call(st[sp - 2], JsValue.FromObject(it));
                                break;
                            }

                        case Op.AsyncIterResult:
                            {
                                JsValue r = st[sp - 1];
                                if (r.Ref is not JsObject ro)
                                {
                                    throw Errors.Type("Iterator result " + Ops.DisplayValue(r) + " is not an object");
                                }

                                if (Ops.ToBoolean(ro.Get("done")))
                                {
                                    sp--;
                                    pc = ins.A;
                                }
                                else
                                {
                                    st[sp - 1] = ro.Get("value");
                                }

                                break;
                            }

                        case Op.AsyncIterCloseStart:
                            {
                                var it = st[sp - 2];
                                JsValue ret = Ops.GetMethod(it, "return");
                                if (ret.IsUndefined)
                                {
                                    pc = ins.A;
                                }
                                else
                                {
                                    st[sp++] = ((JsObject)ret.Ref!).Call(it, default);
                                }

                                break;
                            }

                        case Op.ThrowIfNotObject:
                            if (!st[--sp].IsObject)
                            {
                                throw Errors.Type("Iterator result " + Ops.DisplayValue(st[sp]) + " is not an object");
                            }

                            break;
                        case Op.CheckObjectCoercibleDestructure:
                            if (st[sp - 1].IsNullish)
                            {
                                throw Errors.Type(DestructureError(st[sp - 1], ins.A < 0 ? null : fc.Names[ins.A], fc.Names[ins.B]));
                            }

                            break;

                        // ---- generators and async ----
                        case Op.GenStart:
                            f.Pc = pc;
                            f.Sp = sp;
                            f.ArgArr = null;
                            if (f.Code.Kind == FunctionKind.Module)
                            {
                                f.State = FrameState.SuspendedStart;
                                ag.CallDepth--;
                                ag.Frame = f.Caller;
                                return default;
                            }
                            else
                            {
                                JsValue gen = Generators.Create(f);
                                ag.CallDepth--;
                                f.State = FrameState.SuspendedStart;
                                f.Handlers = null;
                                if (f.IsEntry)
                                {
                                    ag.Frame = f.Caller;
                                    return gen;
                                }

                                f = f.Caller!;
                                ag.Frame = f;
                                ag.Realm = f.Realm;
                                st = f.Stack;
                                sp = f.Sp;
                                pc = f.Pc + 1;
                                code = f.Code.Ops;
                                fc = f.Code;
                                st[sp++] = gen;
                                break;
                            }

                        case Op.Yield:
                            {
                                JsValue v = st[--sp];
                                f.Sp = sp;
                                f.Pc = pc;
                                f.State = FrameState.SuspendedYield;
                                ag.CallDepth--;
                                ag.Frame = f.Caller;
                                return v;
                            }

                        case Op.YieldStar:
                            {
                                // [iterator, next, received] -> either suspends (re-executed on resume) or leaves [result].
                                YieldStarResult yr = Generators.YieldStarStep(f, st, ref sp, ins.B == 1);
                                if (yr.Kind == YieldStarKind.Yield)
                                {
                                    f.Sp = sp;
                                    f.Pc = pc;
                                    f.State = FrameState.SuspendedYield;
                                    ag.CallDepth--;
                                    ag.Frame = f.Caller;
                                    return yr.Value;
                                }

                                if (yr.Kind == YieldStarKind.Await)
                                {
                                    f.Sp = sp;
                                    f.Pc = pc;
                                    f.State = FrameState.Awaiting;
                                    ag.CallDepth--;
                                    Generators.AwaitInFrame(f, yr.Value);
                                    ag.Frame = f.Caller;
                                    return default;
                                }

                                if (yr.Kind == YieldStarKind.Return)
                                {
                                    pc = ins.A;
                                }

                                break;
                            }

                        case Op.Await:
                            {
                                JsValue v = st[--sp];
                                f.Sp = sp;
                                f.Pc = pc;
                                f.State = FrameState.Awaiting;
                                ag.CallDepth--;
                                Generators.AwaitInFrame(f, v);
                                if (f.IsEntry)
                                {
                                    ag.Frame = f.Caller;

                                    // Native code that called an async function (Array.prototype.map, a host call, ScriptEngine) gets its promise at the
                                    // first suspension, as a call from script does. When the job queue resumes the frame the value is ignored.
                                    return f.Owner is PromiseObject own && f.Code.IsAsync && !f.Code.IsGenerator && f.Code.Kind != FunctionKind.Module
                                        ? JsValue.FromObject(own)
                                        : default;
                                }

                                // First suspension of an async function called from script: hand the promise to the caller.
                                f.IsEntry = true;
                                Frame callee = f;
                                f = f.Caller!;
                                callee.Caller = null;
                                ag.Frame = f;
                                ag.Realm = f.Realm;
                                st = f.Stack;
                                sp = f.Sp;
                                pc = f.Pc + 1;
                                code = f.Code.Ops;
                                fc = f.Code;
                                st[sp++] = JsValue.FromObject((JsObject)callee.Owner!);
                                break;
                            }

                        case Op.Debugger:
                            break;
                        default:
                            throw new InvalidOperationException("opcode " + ins.Op);
                    }

                    continue;
                handle:
                    break;
                }
            }
            catch (JsException ex)
            {
                ag.CurrentNative = null;
                thrown = ex.Value;
                hasThrown = true;
            }
            catch (InvalidCastException ice)
            {
                throw new InvalidOperationException("internal error in " + fc.Name + " at op " + code[Math.Max(0, pc - 1)].Op + ": " + ice.Message, ice);
            }
            catch (IndexOutOfRangeException ioe)
            {
                throw new InvalidOperationException("internal error in " + fc.Name + " at op " + code[Math.Max(0, pc - 1)].Op + ": " + ioe.Message, ioe);
            }
            catch (NullReferenceException nre)
            {
                throw new InvalidOperationException("internal error in " + fc.Name + " at op " + code[Math.Max(0, pc - 1)].Op + ": " + nre.Message, nre);
            }
        }
    }
}
