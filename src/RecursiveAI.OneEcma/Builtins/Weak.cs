using System.Runtime.CompilerServices;

namespace RecursiveAI.OneEcma;

/// <summary>
/// Weak-reference bookkeeping for one engine (ECMA-262 9.10, 26.1, 26.2).
/// A WeakRef or FinalizationRegistry target is held strongly until the host asks for a collection point
/// (<see cref="Collect"/>, reached from <c>$262.gc()</c> or a host call). Only there are targets released to the
/// garbage collector and the unreachable ones declared dead, so what a script can observe never depends on when the
/// .NET collector happens to run. The set of ids that died at a collection point goes through the host, so a replaying
/// host can force the same outcome.
/// </summary>
internal sealed class WeakTracker
{
    private long _nextId = 1;
    private readonly List<WeakReference<WeakRefObject>> _refs = [];
    private readonly List<WeakReference<FinalizationRegistryObject>> _registries = [];

    /// <summary>[[KeptAlive]] (9.10.1): objects a WeakRef created or dereferenced in the current synchronous run.</summary>
    private readonly List<object> _kept = [];

    internal long NewId() => _nextId++;

    /// <summary>True once any WeakRef or FinalizationRegistry target has been made. It never goes back, so it does not depend on the garbage collector.</summary>
    internal bool HasTargets => _nextId > 1;

    internal void Track(WeakRefObject r) => _refs.Add(new WeakReference<WeakRefObject>(r));

    internal void Track(FinalizationRegistryObject r) => _registries.Add(new WeakReference<FinalizationRegistryObject>(r));

    /// <summary>AddToKeptObjects (9.10.2).</summary>
    internal void AddToKept(object target)
    {
        foreach (object o in _kept)
        {
            if (ReferenceEquals(o, target))
            {
                return;
            }
        }

        _kept.Add(target);
    }

    /// <summary>ClearKeptObjects (9.10.3): the host calls this when a synchronous run of script ends.</summary>
    internal void ClearKept() => _kept.Clear();

    /// <summary>
    /// A host-defined collection point. Releases every unkept target to the collector (or, when the host replays a
    /// recorded run, applies the recorded outcome), marks the dead ones, and queues cleanup jobs for registries
    /// that now hold dead cells.
    /// </summary>
    internal void Collect(Agent agent)
    {
        IReadOnlyList<long>? replay = agent.Host.ReplayWeakCollection();
        var live = new List<WeakRefObject>();
        foreach (WeakReference<WeakRefObject> w in _refs)
        {
            if (w.TryGetTarget(out WeakRefObject? o))
            {
                live.Add(o);
            }
        }

        var regs = new List<FinalizationRegistryObject>();
        foreach (WeakReference<FinalizationRegistryObject> w in _registries)
        {
            if (w.TryGetTarget(out FinalizationRegistryObject? o))
            {
                regs.Add(o);
            }
        }

        _refs.RemoveAll(static w => !w.TryGetTarget(out _));
        _registries.RemoveAll(static w => !w.TryGetTarget(out _));

        var dead = new List<long>();
        if (replay is null)
        {
            foreach (WeakRefObject o in live)
            {
                o.Slot.Release();
            }

            foreach (FinalizationRegistryObject reg in regs)
            {
                foreach (FinalizationCell c in reg.Cells)
                {
                    c.Slot.Release();
                }
            }

            ForceCollection();
            foreach (WeakRefObject o in live)
            {
                if (o.Slot.Reclaim())
                {
                    dead.Add(o.Slot.Id);
                }
            }

            foreach (FinalizationRegistryObject reg in regs)
            {
                foreach (FinalizationCell c in reg.Cells)
                {
                    if (c.Slot.Reclaim())
                    {
                        dead.Add(c.Slot.Id);
                    }
                }
            }

            dead.Sort();
            agent.Host.WeakTargetsCollected(dead);
        }
        else
        {
            var ids = new HashSet<long>(replay);
            foreach (WeakRefObject o in live)
            {
                if (ids.Contains(o.Slot.Id))
                {
                    o.Slot.Kill();
                }
            }

            foreach (FinalizationRegistryObject reg in regs)
            {
                foreach (FinalizationCell c in reg.Cells)
                {
                    if (ids.Contains(c.Slot.Id))
                    {
                        c.Slot.Kill();
                    }
                }
            }
        }

        foreach (FinalizationRegistryObject reg in regs)
        {
            reg.ScheduleCleanup(agent);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

/// <summary>A target held either strongly (between collection points) or weakly (during one), or dead.</summary>
internal sealed class WeakSlot
{
    private object? _strong;
    private WeakReference<object>? _weak;

    internal WeakSlot(long id, object target)
    {
        Id = id;
        _strong = target;
    }

    internal long Id { get; }

    internal bool IsDead { get; private set; }

    /// <summary>The target, or null once it has died.</summary>
    internal object? Target => IsDead ? null : _strong;

    internal void Release()
    {
        if (!IsDead && _strong is not null)
        {
            _weak = new WeakReference<object>(_strong);
            _strong = null;
        }
    }

    /// <summary>After a collection: pins a survivor again; returns true when the target was reclaimed.</summary>
    internal bool Reclaim()
    {
        if (IsDead || _weak is null)
        {
            return false;
        }

        if (_weak.TryGetTarget(out object? t))
        {
            _strong = t;
            _weak = null;
            return false;
        }

        IsDead = true;
        _weak = null;
        return true;
    }

    internal void Kill()
    {
        IsDead = true;
        _strong = null;
        _weak = null;
    }
}

/// <summary>[[WeakRefTarget]] holder (26.1).</summary>
internal sealed class WeakRefObject : JsObject
{
    internal readonly WeakSlot Slot;

    internal WeakRefObject(JsObject? proto, WeakSlot slot)
        : base(proto)
    {
        Slot = slot;
    }

    internal override string ClassName => "WeakRef";
}

/// <summary>A FinalizationRegistry cell (26.2): target, held value and unregister token.</summary>
internal sealed class FinalizationCell
{
    internal WeakSlot Slot = null!;
    internal JsValue HeldValue;

    /// <summary>The unregister token is only ever held weakly.</summary>
    internal WeakReference<object>? Token;
}

internal sealed class FinalizationRegistryObject : JsObject
{
    internal readonly JsObject Callback;
    internal readonly List<FinalizationCell> Cells = [];
    private bool _cleanupQueued;

    internal FinalizationRegistryObject(JsObject? proto, JsObject callback)
        : base(proto)
    {
        Callback = callback;
    }

    internal override string ClassName => "FinalizationRegistry";

    /// <summary>
    /// HostEnqueueFinalizationRegistryCleanupJob (9.13) plus CleanupFinalizationRegistry (26.2.4.1): one job per registry
    /// with dead cells, on the engine's job queue so its order is part of the recorded job order.
    /// </summary>
    internal void ScheduleCleanup(Agent agent)
    {
        if (_cleanupQueued || !Cells.Exists(static c => c.Slot.IsDead))
        {
            return;
        }

        _cleanupQueued = true;
        agent.EnqueueJob(() => RunCleanup(agent));
    }

    private void RunCleanup(Agent agent)
    {
        _cleanupQueued = false;
        try
        {
            while (true)
            {
                int i = Cells.FindIndex(static c => c.Slot.IsDead);
                if (i < 0)
                {
                    return;
                }

                FinalizationCell cell = Cells[i];
                Cells.RemoveAt(i);
                agent.Tick();
                Callback.Call(default, [cell.HeldValue]);
            }
        }
        finally
        {
            // A throwing callback leaves the remaining dead cells for a later job.
            ScheduleCleanup(agent);
        }
    }
}

internal static partial class Builtins
{
    /// <summary>CanBeHeldWeakly (9.12): objects and symbols that are not in the global registry.</summary>
    internal static bool CanBeHeldWeakly(JsValue v) => v.Ref is JsObject || (v.Ref is JsSymbol s && s.RegistryKey is null);

    private sealed class WeakValue
    {
        internal JsValue Value;
    }

    private sealed class JsWeakMap : JsObject
    {
        internal readonly ConditionalWeakTable<object, WeakValue> Table = new();

        internal JsWeakMap(JsObject? proto)
            : base(proto)
        {
        }

        internal override string ClassName => "WeakMap";
    }

    private sealed class JsWeakSet : JsObject
    {
        internal readonly ConditionalWeakTable<object, WeakValue> Table = new();

        internal JsWeakSet(JsObject? proto)
            : base(proto)
        {
        }

        internal override string ClassName => "WeakSet";
    }

    private static void SetupWeak(Realm r)
    {
        SetupWeakMap(r);
        SetupWeakSet(r);
        SetupWeakRef(r);
        SetupFinalizationRegistry(r);
    }

    private static void SetupWeakMap(Realm r)
    {
        var proto = r.NewObject();
        r.Intrinsics["%WeakMap.prototype%"] = proto;
        r.Ctor("WeakMap", 0, (t, a, nt) =>
            ConstructCollection("WeakMap", nt, A(a, 0), "%WeakMap.prototype%", p => new JsWeakMap(p), "set", true), proto);
        JsWeakMap This(JsValue t, string m) => t.Ref as JsWeakMap ?? throw Incompat(m, t);
        r.Method(proto, "delete", 1, (t, a, nt) =>
        {
            JsWeakMap m = This(t, "WeakMap.prototype.delete");
            return Ops.Bool(CanBeHeldWeakly(A(a, 0)) && m.Table.Remove(A(a, 0).Ref!));
        });
        r.Method(proto, "get", 1, (t, a, nt) =>
        {
            JsWeakMap m = This(t, "WeakMap.prototype.get");
            return CanBeHeldWeakly(A(a, 0)) && m.Table.TryGetValue(A(a, 0).Ref!, out WeakValue? v) ? v.Value : default;
        });
        r.Method(proto, "set", 2, (t, a, nt) =>
        {
            JsWeakMap m = This(t, "WeakMap.prototype.set");
            if (!CanBeHeldWeakly(A(a, 0)))
            {
                throw Errors.Type("Invalid value used as weak map key");
            }

            m.Table.AddOrUpdate(A(a, 0).Ref!, new WeakValue { Value = A(a, 1) });
            return t;
        });
        r.Method(proto, "has", 1, (t, a, nt) =>
        {
            JsWeakMap m = This(t, "WeakMap.prototype.has");
            return Ops.Bool(CanBeHeldWeakly(A(a, 0)) && m.Table.TryGetValue(A(a, 0).Ref!, out _));
        });

        // 24.3.3.4 / 24.3.3.5 (ES2026 upsert)
        r.Method(proto, "getOrInsert", 2, (t, a, nt) =>
        {
            JsWeakMap m = This(t, "WeakMap.prototype.getOrInsert");
            if (!CanBeHeldWeakly(A(a, 0)))
            {
                throw Errors.Type("Invalid value used as weak map key");
            }

            if (m.Table.TryGetValue(A(a, 0).Ref!, out WeakValue? v))
            {
                return v.Value;
            }

            m.Table.Add(A(a, 0).Ref!, new WeakValue { Value = A(a, 1) });
            return A(a, 1);
        });
        r.Method(proto, "getOrInsertComputed", 2, (t, a, nt) =>
        {
            JsWeakMap m = This(t, "WeakMap.prototype.getOrInsertComputed");
            if (!CanBeHeldWeakly(A(a, 0)))
            {
                throw Errors.Type("Invalid value used as weak map key");
            }

            if (!Ops.IsCallable(A(a, 1)))
            {
                throw Errors.Type(ValueText(A(a, 1)) + " is not a function");
            }

            if (m.Table.TryGetValue(A(a, 0).Ref!, out WeakValue? v))
            {
                return v.Value;
            }

            JsValue value = Ops.Call(A(a, 1), default, [A(a, 0)]);
            m.Table.AddOrUpdate(A(a, 0).Ref!, new WeakValue { Value = value });
            return value;
        });
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("WeakMap"), PropFlags.Configurable);
    }

    private static void SetupWeakSet(Realm r)
    {
        var proto = r.NewObject();
        r.Intrinsics["%WeakSet.prototype%"] = proto;
        r.Ctor("WeakSet", 0, (t, a, nt) =>
            ConstructCollection("WeakSet", nt, A(a, 0), "%WeakSet.prototype%", p => new JsWeakSet(p), "add", false), proto);
        JsWeakSet This(JsValue t, string m) => t.Ref as JsWeakSet ?? throw Incompat(m, t);
        var present = new WeakValue();
        r.Method(proto, "delete", 1, (t, a, nt) =>
        {
            JsWeakSet s = This(t, "WeakSet.prototype.delete");
            return Ops.Bool(CanBeHeldWeakly(A(a, 0)) && s.Table.Remove(A(a, 0).Ref!));
        });
        r.Method(proto, "has", 1, (t, a, nt) =>
        {
            JsWeakSet s = This(t, "WeakSet.prototype.has");
            return Ops.Bool(CanBeHeldWeakly(A(a, 0)) && s.Table.TryGetValue(A(a, 0).Ref!, out _));
        });
        r.Method(proto, "add", 1, (t, a, nt) =>
        {
            JsWeakSet s = This(t, "WeakSet.prototype.add");
            if (!CanBeHeldWeakly(A(a, 0)))
            {
                throw Errors.Type("Invalid value used in weak set");
            }

            s.Table.AddOrUpdate(A(a, 0).Ref!, present);
            return t;
        });
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("WeakSet"), PropFlags.Configurable);
    }

    private static void SetupWeakRef(Realm r)
    {
        var proto = r.NewObject();
        r.Intrinsics["%WeakRef.prototype%"] = proto;
        r.Ctor("WeakRef", 1, (t, a, nt) =>
        {
            if (nt is null)
            {
                throw Errors.Type("Constructor WeakRef requires 'new'");
            }

            if (!CanBeHeldWeakly(A(a, 0)))
            {
                throw Errors.Type("WeakRef: invalid target");
            }

            WeakTracker w = r.Agent.Weak;
            JsObject p = Ops.GetPrototypeFromConstructor(nt, x => x.Intrinsics["%WeakRef.prototype%"]);
            var wr = new WeakRefObject(p, new WeakSlot(w.NewId(), A(a, 0).Ref!));
            w.Track(wr);
            w.AddToKept(A(a, 0).Ref!);
            return JsValue.FromObject(wr);
        }, proto);
        r.Method(proto, "deref", 0, (t, a, nt) =>
        {
            if (t.Ref is not WeakRefObject wr)
            {
                throw Incompat("WeakRef.prototype.deref", t);
            }

            // 26.1.3.2 WeakRefDeref: a successful deref keeps the target alive until the current run ends.
            object? target = wr.Slot.Target;
            if (target is null)
            {
                return default;
            }

            r.Agent.Weak.AddToKept(target);
            return target is JsObject o ? JsValue.FromObject(o) : JsValue.FromSymbol((JsSymbol)target);
        });
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("WeakRef"), PropFlags.Configurable);
    }

    private static void SetupFinalizationRegistry(Realm r)
    {
        var proto = r.NewObject();
        r.Intrinsics["%FinalizationRegistry.prototype%"] = proto;
        r.Ctor("FinalizationRegistry", 1, (t, a, nt) =>
        {
            if (nt is null)
            {
                throw Errors.Type("Constructor FinalizationRegistry requires 'new'");
            }

            if (A(a, 0).Ref is not JsObject { IsCallable: true } cb)
            {
                throw Errors.Type("FinalizationRegistry: cleanup must be callable");
            }

            JsObject p = Ops.GetPrototypeFromConstructor(nt, x => x.Intrinsics["%FinalizationRegistry.prototype%"]);
            var reg = new FinalizationRegistryObject(p, cb);
            r.Agent.Weak.Track(reg);
            return JsValue.FromObject(reg);
        }, proto);
        FinalizationRegistryObject This(JsValue t, string m) => t.Ref as FinalizationRegistryObject ?? throw Incompat(m, t);

        r.Method(proto, "register", 2, (t, a, nt) =>
        {
            FinalizationRegistryObject reg = This(t, "FinalizationRegistry.prototype.register");
            JsValue target = A(a, 0), held = A(a, 1), token = A(a, 2);
            if (!CanBeHeldWeakly(target))
            {
                throw Errors.Type("FinalizationRegistry.prototype.register: invalid target");
            }

            if (Ops.SameValue(target, held))
            {
                throw Errors.Type("FinalizationRegistry.prototype.register: target and holdings must not be same");
            }

            if (!CanBeHeldWeakly(token) && !token.IsUndefined)
            {
                throw Errors.Type("Invalid unregisterToken ('" + ValueText(token) + "')");
            }

            WeakTracker w = r.Agent.Weak;
            reg.Cells.Add(new FinalizationCell
            {
                Slot = new WeakSlot(w.NewId(), target.Ref!),
                HeldValue = held,
                Token = CanBeHeldWeakly(token) ? new WeakReference<object>(token.Ref!) : null,
            });
            return default;
        });
        r.Method(proto, "unregister", 1, (t, a, nt) =>
        {
            FinalizationRegistryObject reg = This(t, "FinalizationRegistry.prototype.unregister");
            JsValue token = A(a, 0);
            if (!CanBeHeldWeakly(token))
            {
                throw Errors.Type("Invalid unregisterToken ('" + ValueText(token) + "')");
            }

            bool removed = reg.Cells.RemoveAll(c => c.Token is not null && c.Token.TryGetTarget(out object? o) && ReferenceEquals(o, token.Ref)) > 0;
            return Ops.Bool(removed);
        });
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("FinalizationRegistry"), PropFlags.Configurable);
    }
}
