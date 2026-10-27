using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneEcma;

/// <summary>
/// The Atomics namespace (ECMA-262 25.4). The engine is a single agent, so every operation is trivially atomic. A
/// <c>wait</c> that the host allows to block can only time out (no other agent can notify it), and <c>waitAsync</c>
/// waiters run on virtual time: when the job queue drains, the waiter with the earliest deadline times out, in order,
/// so a run stays deterministic and never sleeps.
/// </summary>
internal static class AtomicsBuiltin
{
    private sealed class Waiter
    {
        internal JsArrayBuffer Buffer = null!;
        internal int ByteIndex;
        internal PromiseObject Promise = null!;
        internal double Deadline;
        internal long Seq;
    }

    private sealed class WaitState
    {
        internal readonly List<Waiter> Waiters = [];
        internal double VirtualNow;
        internal long NextSeq;
    }

    private static readonly ConditionalWeakTable<Agent, WaitState> States = new();

    private static JsValue A(ReadOnlySpan<JsValue> a, int i) => i < a.Length ? a[i] : default;

    private static JsValue Num(double d) => JsValue.FromNumber(d);

    /// <summary>ValidateIntegerTypedArray (25.4.3.1).</summary>
    private static JsTypedArray ValidateInteger(JsValue v, bool waitable)
    {
        if (v.Ref is not JsTypedArray ta)
        {
            throw Errors.Type(Ops.DisplayValue(v) + " is not an integer typed array.");
        }

        if (ta.IsOutOfBounds)
        {
            throw ArrayBuffers.Detached("Atomics operation");
        }

        if (waitable)
        {
            if (ta.Kind is not (ElementKind.Int32 or ElementKind.BigInt64))
            {
                throw Errors.Type("[object " + ta.ClassName + "] is not an int32 or BigInt64 typed array.");
            }
        }
        else if (ta.Kind is ElementKind.Float16 or ElementKind.Float32 or ElementKind.Float64 or ElementKind.Uint8Clamped)
        {
            throw Errors.Type("[object " + ta.ClassName + "] is not an integer typed array.");
        }

        return ta;
    }

    /// <summary>ValidateAtomicAccess (25.4.3.2): the element index, as the checked ToIndex of the argument.</summary>
    private static int AccessIndex(JsTypedArray ta, JsValue requestIndex)
    {
        int length = ta.Length;
        double n = Ops.ToIntegerOrInfinity(requestIndex);
        if (n < 0 || n > 9007199254740991d || n >= length)
        {
            throw Errors.Range("Invalid atomic access index");
        }

        return (int)n;
    }

    /// <summary>RevalidateAtomicAccess (25.4.3.4): after the coercions the buffer may have shrunk or been detached.</summary>
    private static void Revalidate(JsTypedArray ta, int index)
    {
        if (ta.IsOutOfBounds)
        {
            throw ArrayBuffers.Detached("Atomics operation");
        }

        if (index >= ta.Length)
        {
            throw Errors.Range("Invalid atomic access index");
        }
    }

    /// <summary>The operand after argument coercion: ToBigInt for BigInt arrays, ToIntegerOrInfinity (as a Number) otherwise.</summary>
    private static JsValue Operand(JsTypedArray ta, JsValue v)
    {
        if (ta.IsBigInt)
        {
            return JsValue.FromBigInt(Builtins.ToBigInt(v));
        }

        return Num(Ops.ToIntegerOrInfinity(v) + 0.0);
    }

    private static JsValue Wrapped(JsTypedArray ta, JsValue operand)
    {
        // The raw-bytes round trip of the spec: the operand wraps to the element type before it is combined.
        Span<byte> tmp = stackalloc byte[8];
        ElementIo.Write(ta.Kind, tmp, operand, true);
        return ElementIo.Read(ta.Kind, tmp, true);
    }

    private static void SetupOp(Realm r, JsObject atomics, string name, Func<ElementKind, JsValue, JsValue, JsValue> combine)
    {
        r.Method(atomics, name, 3, (t, a, nt) =>
        {
            JsTypedArray ta = ValidateInteger(A(a, 0), false);
            int idx = AccessIndex(ta, A(a, 1));
            JsValue v = Operand(ta, A(a, 2));
            Revalidate(ta, idx);
            JsValue old = ta.GetElement(idx);
            ta.SetElementRaw(idx, combine(ta.Kind, old, Wrapped(ta, v)));
            return old;
        });
    }

    private static JsValue Arith(ElementKind k, JsValue x, JsValue y, int op)
    {
        if (ElementIo.IsBig(k))
        {
            var bx = (BigInteger)x.Ref!;
            var by = (BigInteger)y.Ref!;
            return JsValue.FromBigInt(op switch
            {
                0 => bx + by,
                1 => bx - by,
                2 => bx & by,
                3 => bx | by,
                _ => bx ^ by,
            });
        }

        long lx = (long)x.Num;
        long ly = (long)y.Num;
        return Num(op switch
        {
            0 => lx + ly,
            1 => lx - ly,
            2 => lx & ly,
            3 => lx | ly,
            _ => lx ^ ly,
        });
    }

    internal static void Setup(Realm r)
    {
        JsObject atomics = r.NewObject();
        r.Global.DefineDirect("Atomics", JsValue.FromObject(atomics), PropFlags.Hidden);
        atomics.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Atomics"), PropFlags.Configurable);

        SetupOp(r, atomics, "add", (k, x, y) => Arith(k, x, y, 0));
        SetupOp(r, atomics, "sub", (k, x, y) => Arith(k, x, y, 1));
        SetupOp(r, atomics, "and", (k, x, y) => Arith(k, x, y, 2));
        SetupOp(r, atomics, "or", (k, x, y) => Arith(k, x, y, 3));
        SetupOp(r, atomics, "xor", (k, x, y) => Arith(k, x, y, 4));
        SetupOp(r, atomics, "exchange", (k, x, y) => y);

        // 25.4.6 Atomics.compareExchange ( typedArray, index, expectedValue, replacementValue )
        r.Method(atomics, "compareExchange", 4, (t, a, nt) =>
        {
            JsTypedArray ta = ValidateInteger(A(a, 0), false);
            int idx = AccessIndex(ta, A(a, 1));
            JsValue expected = Operand(ta, A(a, 2));
            JsValue replacement = Operand(ta, A(a, 3));
            Revalidate(ta, idx);
            JsValue old = ta.GetElement(idx);

            // Compare as raw element bytes: the expected value wraps to the element type first.
            if (Ops.SameValue(old, Wrapped(ta, expected)))
            {
                ta.SetElementRaw(idx, replacement);
            }

            return old;
        });

        // 25.4.9 Atomics.load ( typedArray, index )
        r.Method(atomics, "load", 2, (t, a, nt) =>
        {
            JsTypedArray ta = ValidateInteger(A(a, 0), false);
            int idx = AccessIndex(ta, A(a, 1));
            Revalidate(ta, idx);
            return ta.GetElement(idx);
        });

        // 25.4.11 Atomics.store ( typedArray, index, value )
        r.Method(atomics, "store", 3, (t, a, nt) =>
        {
            JsTypedArray ta = ValidateInteger(A(a, 0), false);
            int idx = AccessIndex(ta, A(a, 1));
            JsValue v = Operand(ta, A(a, 2));
            Revalidate(ta, idx);
            ta.SetElementRaw(idx, v);
            return v;
        });

        // 25.4.8 Atomics.isLockFree ( size )
        r.Method(atomics, "isLockFree", 1, (t, a, nt) =>
        {
            double n = Ops.ToIntegerOrInfinity(A(a, 0));
            return Ops.Bool(n is 1 or 2 or 4 or 8);
        });

        r.Method(atomics, "wait", 4, (t, a, nt) => DoWait(r, false, a));
        r.Method(atomics, "waitAsync", 4, (t, a, nt) => DoWait(r, true, a));

        // 25.4.15 Atomics.notify ( typedArray, index, count )
        r.Method(atomics, "notify", 3, (t, a, nt) =>
        {
            JsTypedArray ta = ValidateInteger(A(a, 0), true);
            int idx = AccessIndex(ta, A(a, 1));
            double c = double.PositiveInfinity;
            if (!A(a, 2).IsUndefined)
            {
                c = Math.Max(Ops.ToIntegerOrInfinity(A(a, 2)), 0);
            }

            JsArrayBuffer buffer = ta.Buffer;
            if (!buffer.Shared)
            {
                return Num(0);
            }

            int byteIndex = ta.ByteOffset + (idx * ta.ElementSize);
            WaitState st = States.GetOrCreateValue(r.Agent);
            int n = 0;
            for (int i = 0; i < st.Waiters.Count && n < c;)
            {
                Waiter w = st.Waiters[i];
                if (ReferenceEquals(w.Buffer, buffer) && w.ByteIndex == byteIndex)
                {
                    st.Waiters.RemoveAt(i);
                    n++;
                    PromiseObject p = w.Promise;
                    r.Agent.EnqueueJob(() => Promises.Fulfill(p, Ops.Str("ok")));
                }
                else
                {
                    i++;
                }
            }

            return Num(n);
        });
    }

    /// <summary>DoWait (25.4.3.14).</summary>
    private static JsValue DoWait(Realm r, bool isAsync, ReadOnlySpan<JsValue> a)
    {
        JsTypedArray ta = ValidateInteger(A(a, 0), true);
        JsArrayBuffer buffer = ta.Buffer;
        if (!buffer.Shared)
        {
            throw Errors.Type("[object " + ta.ClassName + "] is not a shared typed array.");
        }

        int idx = AccessIndex(ta, A(a, 1));
        long v = ta.Kind == ElementKind.BigInt64
            ? unchecked((long)ElementIo.ToUInt64(Builtins.ToBigInt(A(a, 2))))
            : Ops.ToInt32(A(a, 2));
        double q = Ops.ToNumber(A(a, 3));
        double timeout = double.IsNaN(q) || q == double.PositiveInfinity ? double.PositiveInfinity : q == double.NegativeInfinity ? 0 : Math.Max(q, 0);
        if (!isAsync && !r.Agent.Host.CanBlock)
        {
            throw Errors.Type("Atomics.wait cannot be called in this context");
        }

        JsValue current = ta.GetElement(idx);
        long w = ta.Kind == ElementKind.BigInt64 ? unchecked((long)ElementIo.ToUInt64((BigInteger)current.Ref!)) : (int)current.Num;
        if (v != w)
        {
            return isAsync ? Result(r, false, Ops.Str("not-equal")) : Ops.Str("not-equal");
        }

        if (isAsync && timeout == 0)
        {
            return Result(r, false, Ops.Str("timed-out"));
        }

        if (!isAsync)
        {
            if (double.IsPositiveInfinity(timeout))
            {
                throw Errors.Type("Atomics.wait would block forever: no other agent can notify it");
            }

            // No other agent exists to notify, so the wait runs out its timeout; virtual time passes at once.
            States.GetOrCreateValue(r.Agent).VirtualNow += timeout;
            return Ops.Str("timed-out");
        }

        WaitState st = States.GetOrCreateValue(r.Agent);
        PromiseObject promise = Promises.NewPromise(r);
        st.Waiters.Add(new Waiter
        {
            Buffer = buffer,
            ByteIndex = ta.ByteOffset + (idx * ta.ElementSize),
            Promise = promise,
            Deadline = st.VirtualNow + timeout,
            Seq = st.NextSeq++,
        });
        r.Agent.IdleHook = () => ExpireOne(r.Agent);
        return Result(r, true, JsValue.FromObject(promise));
    }

    private static JsValue Result(Realm r, bool isAsync, JsValue value)
    {
        JsObject o = r.NewObject();
        o.CreateDataPropertyOrThrow("async", Ops.Bool(isAsync));
        o.CreateDataPropertyOrThrow("value", value);
        return JsValue.FromObject(o);
    }

    /// <summary>Idle hook: times out the finite-deadline waiter that is due first; waiters with no timeout stay pending.</summary>
    private static bool ExpireOne(Agent agent)
    {
        WaitState st = States.GetOrCreateValue(agent);
        Waiter? next = null;
        foreach (Waiter w in st.Waiters)
        {
            if (!double.IsPositiveInfinity(w.Deadline) && (next is null || w.Deadline < next.Deadline || (w.Deadline == next.Deadline && w.Seq < next.Seq)))
            {
                next = w;
            }
        }

        if (next is null)
        {
            return false;
        }

        st.Waiters.Remove(next);
        st.VirtualNow = Math.Max(st.VirtualNow, next.Deadline);
        Promises.Fulfill(next.Promise, Ops.Str("timed-out"));
        return true;
    }
}
