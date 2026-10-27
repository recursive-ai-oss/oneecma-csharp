namespace RecursiveAI.OneEcma;

/// <summary>A Proxy exotic object (ECMA-262 10.5). Every internal method looks up its trap on the handler, enforces the invariants of 6.1.7.3, and otherwise forwards to the target.</summary>
internal sealed class ProxyObject : JsObject
{
    /// <summary>How many proxy operations may be nested on the host stack before a RangeError, so proxy chains and proxy prototype chains cannot overflow it.</summary>
    private const int MaxNesting = 512;

    [ThreadStatic]
    private static int t_depth;

    private JsObject? _target;
    private JsObject? _handler;
    private readonly bool _callable;
    private readonly bool _constructor;

    internal ProxyObject(JsObject target, JsObject handler)
        : base((JsObject?)null)
    {
        _target = target;
        _handler = handler;
        _callable = target.IsCallable;
        _constructor = target.IsConstructor;
        OverridesGet = true;
        OverridesSet = true;
    }

    internal override bool IsCallable => _callable;

    internal override bool IsConstructor => _constructor;

    internal override bool IsPlainStorage => false;

    internal void Revoke()
    {
        _target = null;
        _handler = null;
    }

    internal override bool ProxyTarget(out JsObject? target, out bool revoked)
    {
        target = _target;
        revoked = _target is null;
        return true;
    }

    /// <summary>IsArray (7.2.2) looks through proxies; a revoked one throws. Iterative so a long chain cannot overflow.</summary>
    internal override bool IsArrayExotic()
    {
        JsObject? o = this;
        while (o is ProxyObject p)
        {
            if (p._target is null)
            {
                throw Errors.Type("Cannot perform 'IsArray' on a proxy that has been revoked");
            }

            o = p._target;
        }

        return Ops.IsArray(JsValue.FromObject(o!));
    }

    private readonly struct Nesting : IDisposable
    {
        public Nesting()
        {
            if (++t_depth > MaxNesting)
            {
                t_depth--;
                throw Errors.Range("Maximum call stack size exceeded");
            }
        }

        public void Dispose() => t_depth--;
    }

    private static JsException Revoked(string op) => Errors.Type("Cannot perform '" + op + "' on a proxy that has been revoked");

    /// <summary>The TypeError a caller throws when an operation that must succeed (strict assignment, delete, Object.defineProperty) got false from a trap.</summary>
    internal static JsException Falsish(string trap, string? key) =>
        Violation(trap, "trap returned falsish" + (key is null ? string.Empty : " for property '" + key + "'"));

    private static JsException Violation(string trap, string detail) => Errors.Type("'" + trap + "' on proxy: " + detail);

    /// <summary>GetMethod (7.3.11) on the handler. Returns the trap and sets target/handler, or throws for a revoked proxy.</summary>
    private JsValue Trap(string name, out JsObject target, out JsObject handler)
    {
        if (_handler is null || _target is null)
        {
            throw Revoked(name);
        }

        target = _target;
        handler = _handler;
        JsValue f = handler.Get(name);
        if (f.IsNullish)
        {
            return default;
        }

        if (!Ops.IsCallable(f))
        {
            // V8 reports a bad apply or construct trap as a failed call of the proxy itself.
            if (name is "apply" or "construct")
            {
                throw Errors.Type("(intermediate value) is not a function");
            }

            throw Errors.Type("'" + (f.IsString ? f.AsString : Ops.DisplayValue(f)) + "' returned for property '" + name + "' of object '" + Ops.DisplayValue(JsValue.FromObject(handler)) + "' is not a function");
        }

        return f;
    }

    private static JsValue KeyValue(PropertyKey k) => k.ToValue();

    private static string KeyName(PropertyKey k) => Ops.KeyText(k);

    // 10.5.1
    internal override JsObject? GetPrototypeOf()
    {
        using var _ = new Nesting();
        JsValue trap = Trap("getPrototypeOf", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.GetPrototypeOf();
        }

        JsValue hp = Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target)]);
        if (!hp.IsObject && !hp.IsNull)
        {
            throw Violation("getPrototypeOf", "trap returned neither object nor null");
        }

        if (target.IsExtensibleOp())
        {
            return hp.AsObject;
        }

        if (!ReferenceEquals(target.GetPrototypeOf(), hp.AsObject))
        {
            throw Violation("getPrototypeOf", "proxy target is non-extensible but the trap did not return its actual prototype");
        }

        return hp.AsObject;
    }

    // 10.5.2
    internal override bool SetPrototypeOf(JsObject? p)
    {
        using var _ = new Nesting();
        JsValue trap = Trap("setPrototypeOf", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.SetPrototypeOf(p);
        }

        JsValue pv = p is null ? JsValue.Null : JsValue.FromObject(p);
        if (!Ops.ToBoolean(Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target), pv])))
        {
            return false;
        }

        if (target.IsExtensibleOp())
        {
            return true;
        }

        if (!ReferenceEquals(target.GetPrototypeOf(), p))
        {
            throw Violation("setPrototypeOf", "trap returned truish for setting a new prototype on the non-extensible proxy target");
        }

        return true;
    }

    // 10.5.3
    internal override bool IsExtensibleOp()
    {
        using var _ = new Nesting();
        JsValue trap = Trap("isExtensible", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.IsExtensibleOp();
        }

        bool result = Ops.ToBoolean(Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target)]));
        bool targetResult = target.IsExtensibleOp();
        if (result != targetResult)
        {
            throw Violation("isExtensible", "trap result does not reflect extensibility of proxy target (which is '" + (targetResult ? "true" : "false") + "')");
        }

        return result;
    }

    // 10.5.4
    internal override bool PreventExtensions()
    {
        using var _ = new Nesting();
        JsValue trap = Trap("preventExtensions", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.PreventExtensions();
        }

        bool result = Ops.ToBoolean(Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target)]));
        if (result && target.IsExtensibleOp())
        {
            throw Violation("preventExtensions", "trap returned truish but the proxy target is extensible");
        }

        return result;
    }

    // 10.5.5
    internal override bool GetOwnProperty(PropertyKey key, out PropertyDescriptor desc)
    {
        using var _ = new Nesting();
        JsValue trap = Trap("getOwnPropertyDescriptor", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.GetOwnProperty(key, out desc);
        }

        JsValue trapResult = Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target), KeyValue(key)]);
        if (!trapResult.IsObject && !trapResult.IsUndefined)
        {
            throw Violation("getOwnPropertyDescriptor", "trap returned neither object nor undefined for property '" + KeyName(key) + "'");
        }

        bool hasTarget = target.GetOwnProperty(key, out PropertyDescriptor targetDesc);
        if (trapResult.IsUndefined)
        {
            desc = default;
            if (!hasTarget)
            {
                return false;
            }

            if (!targetDesc.Configurable)
            {
                throw Violation("getOwnPropertyDescriptor", "trap returned undefined for property '" + KeyName(key) + "' which is non-configurable in the proxy target");
            }

            if (!target.IsExtensibleOp())
            {
                throw Violation("getOwnPropertyDescriptor", "trap returned undefined for property '" + KeyName(key) + "' which exists in the non-extensible proxy target");
            }

            return false;
        }

        bool extensibleTarget = target.IsExtensibleOp();
        PropertyDescriptor result = Builtins.ToPropertyDescriptor(trapResult);
        Complete(ref result);
        if (!IsCompatible(extensibleTarget, result, hasTarget, targetDesc))
        {
            throw Violation("getOwnPropertyDescriptor", "trap returned descriptor for property '" + KeyName(key) + "' that is incompatible with the existing property in the proxy target");
        }

        if (!result.Configurable)
        {
            if (!hasTarget || targetDesc.Configurable)
            {
                throw Violation("getOwnPropertyDescriptor", "trap reported non-configurability for property '" + KeyName(key) + "' which is either non-existent or configurable in the proxy target");
            }

            if (result.HasWritable && !result.Writable && targetDesc.Writable)
            {
                throw Violation("getOwnPropertyDescriptor", "trap reported non-configurable and non-writable for property '" + KeyName(key) + "' which is non-configurable, writable in the proxy target");
            }
        }

        desc = result;
        return true;
    }

    // 10.5.6
    internal override bool DefineOwnProperty(PropertyKey key, in PropertyDescriptor desc)
    {
        using var _ = new Nesting();
        JsValue trap = Trap("defineProperty", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.DefineOwnProperty(key, desc);
        }

        JsValue descObj = Builtins.FromPropertyDescriptor(Ops.CurrentRealm, desc);
        if (!Ops.ToBoolean(Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target), KeyValue(key), descObj])))
        {
            return false;
        }

        bool hasTarget = target.GetOwnProperty(key, out PropertyDescriptor targetDesc);
        bool extensibleTarget = target.IsExtensibleOp();
        bool settingConfigFalse = desc.HasConfigurable && !desc.Configurable;
        if (!hasTarget)
        {
            if (!extensibleTarget)
            {
                throw Violation("defineProperty", "trap returned truish for adding property '" + KeyName(key) + "'  to the non-extensible proxy target");
            }

            if (settingConfigFalse)
            {
                throw Violation("defineProperty", "trap returned truish for defining non-configurable property '" + KeyName(key) + "' which is either non-existent or configurable in the proxy target");
            }
        }
        else
        {
            if (!IsCompatible(extensibleTarget, desc, true, targetDesc))
            {
                throw Violation("defineProperty", "trap returned truish for adding property '" + KeyName(key) + "'  that is incompatible with the existing property in the proxy target");
            }

            if (settingConfigFalse && targetDesc.Configurable)
            {
                throw Violation("defineProperty", "trap returned truish for defining non-configurable property '" + KeyName(key) + "' which is either non-existent or configurable in the proxy target");
            }

            if (targetDesc.IsData && !targetDesc.Configurable && targetDesc.Writable && desc.HasWritable && !desc.Writable)
            {
                throw Violation("defineProperty", "trap returned truish for defining non-configurable property '" + KeyName(key) + "' which cannot be non-writable, unless there exists a corresponding non-configurable, non-writable own property of the target object.");
            }
        }

        return true;
    }

    // 10.5.7
    internal override bool HasProperty(PropertyKey key)
    {
        using var _ = new Nesting();
        JsValue trap = Trap("has", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.HasProperty(key);
        }

        bool result = Ops.ToBoolean(Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target), KeyValue(key)]));
        if (!result && target.GetOwnProperty(key, out PropertyDescriptor td))
        {
            if (!td.Configurable)
            {
                throw Violation("has", "trap returned falsish for property '" + KeyName(key) + "' which exists in the proxy target as non-configurable");
            }

            if (!target.IsExtensibleOp())
            {
                throw Violation("has", "trap returned falsish for property '" + KeyName(key) + "' but the proxy target is not extensible");
            }
        }

        return result;
    }

    // 10.5.8
    internal override JsValue Get(PropertyKey key, JsValue receiver)
    {
        using var _ = new Nesting();
        JsValue trap = Trap("get", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.Get(key, receiver);
        }

        JsValue v = Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target), KeyValue(key), receiver]);
        if (target.GetOwnProperty(key, out PropertyDescriptor td) && !td.Configurable)
        {
            if (td.IsData && !td.Writable && !Ops.SameValue(v, td.Value))
            {
                throw Violation("get", "property '" + KeyName(key) + "' is a read-only and non-configurable data property on the proxy target but the proxy did not return its actual value (expected '" + Shown(td.Value) + "' but got '" + Shown(v) + "')");
            }

            if (td.IsAccessor && td.Get.IsUndefined && !v.IsUndefined)
            {
                throw Violation("get", "property '" + KeyName(key) + "' is a non-configurable accessor property on the proxy target and does not have a getter function, but the trap did not return 'undefined' (got '" + Shown(v) + "')");
            }
        }

        return v;
    }

    private static string Shown(JsValue v) => v.IsString ? v.AsString : v.Ref is JsObject ? Ops.DisplayValue(v) : Ops.DebugString(v);

    // 10.5.9
    internal override bool Set(PropertyKey key, JsValue value, JsValue receiver)
    {
        using var _ = new Nesting();
        JsValue trap = Trap("set", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.Set(key, value, receiver);
        }

        if (!Ops.ToBoolean(Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target), KeyValue(key), value, receiver])))
        {
            return false;
        }

        if (target.GetOwnProperty(key, out PropertyDescriptor td) && !td.Configurable)
        {
            if (td.IsData && !td.Writable && !Ops.SameValue(value, td.Value))
            {
                throw Violation("set", "trap returned truish for property '" + KeyName(key) + "' which exists in the proxy target as a non-configurable and non-writable data property with a different value");
            }

            if (td.IsAccessor && td.Set.IsUndefined)
            {
                throw Violation("set", "trap returned truish for property '" + KeyName(key) + "' which exists in the proxy target as a non-configurable and non-writable accessor property without a setter");
            }
        }

        return true;
    }

    // 10.5.10
    internal override bool Delete(PropertyKey key)
    {
        using var _ = new Nesting();
        JsValue trap = Trap("deleteProperty", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.Delete(key);
        }

        if (!Ops.ToBoolean(Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target), KeyValue(key)])))
        {
            return false;
        }

        if (!target.GetOwnProperty(key, out PropertyDescriptor td))
        {
            return true;
        }

        if (!td.Configurable)
        {
            throw Violation("deleteProperty", "trap returned truish for property '" + KeyName(key) + "' which is non-configurable in the proxy target");
        }

        if (!target.IsExtensibleOp())
        {
            throw Violation("deleteProperty", "trap returned truish for property '" + KeyName(key) + "' but the proxy target is non-extensible");
        }

        return true;
    }

    // 10.5.11
    internal override void OwnKeys(List<PropertyKey> keys)
    {
        using var _ = new Nesting();
        JsValue trap = Trap("ownKeys", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            target.OwnKeys(keys);
            return;
        }

        JsValue arr = Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target)]);
        if (arr.Ref is not JsObject ao)
        {
            throw Errors.Type("CreateListFromArrayLike called on non-object");
        }

        // CreateListFromArrayLike (7.3.18) with element types « String, Symbol ».
        long n = Ops.LengthOfArrayLike(ao);
        Agent.Current.Alloc(n * 24);
        var trapKeys = new List<PropertyKey>();
        var seen = new HashSet<PropertyKey>();
        for (long i = 0; i < n; i++)
        {
            Agent.Current.Tick();
            JsValue e = ao.Get(Ops.NumberToStr(i));
            if (!e.IsString && !e.IsSymbol)
            {
                throw Errors.Type(Shown(e) + " is not a valid property name");
            }

            PropertyKey k = e.IsSymbol ? new PropertyKey((JsSymbol)e.Ref!) : new PropertyKey(e.AsString);
            if (!seen.Add(k))
            {
                throw Violation("ownKeys", "trap returned duplicate entries");
            }

            trapKeys.Add(k);
        }

        bool extensibleTarget = target.IsExtensibleOp();
        var targetKeys = new List<PropertyKey>();
        target.OwnKeys(targetKeys);
        var configurable = new List<PropertyKey>();
        var nonconfigurable = new List<PropertyKey>();
        foreach (PropertyKey k in targetKeys)
        {
            Agent.Current.Tick();
            if (target.GetOwnProperty(k, out PropertyDescriptor d) && !d.Configurable)
            {
                nonconfigurable.Add(k);
            }
            else
            {
                configurable.Add(k);
            }
        }

        if (!extensibleTarget || nonconfigurable.Count > 0)
        {
            var unchecked_ = new HashSet<PropertyKey>(trapKeys);
            foreach (PropertyKey k in nonconfigurable)
            {
                if (!unchecked_.Remove(k))
                {
                    throw Violation("ownKeys", "trap result did not include '" + KeyName(k) + "'");
                }
            }

            if (!extensibleTarget)
            {
                foreach (PropertyKey k in configurable)
                {
                    if (!unchecked_.Remove(k))
                    {
                        throw Violation("ownKeys", "trap result did not include '" + KeyName(k) + "'");
                    }
                }

                if (unchecked_.Count > 0)
                {
                    throw Violation("ownKeys", "trap returned extra keys but proxy target is non-extensible");
                }
            }
        }

        keys.AddRange(trapKeys);
    }

    // 10.5.12
    internal override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        using var _ = new Nesting();
        JsValue trap = Trap("apply", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.Call(thisValue, args);
        }

        JsArray argArray = Ops.CreateArray(args.ToArray());
        return Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target), thisValue, JsValue.FromObject(argArray)]);
    }

    // 10.5.13
    internal override JsValue Construct(ReadOnlySpan<JsValue> args, JsObject newTarget)
    {
        using var _ = new Nesting();
        JsValue trap = Trap("construct", out JsObject target, out JsObject handler);
        if (trap.IsUndefined)
        {
            return target.Construct(args, newTarget);
        }

        JsArray argArray = Ops.CreateArray(args.ToArray());
        JsValue result = Ops.Call(trap, JsValue.FromObject(handler), [JsValue.FromObject(target), JsValue.FromObject(argArray), JsValue.FromObject(newTarget)]);
        if (!result.IsObject)
        {
            throw Violation("construct", "trap returned non-object ('" + Shown(result) + "')");
        }

        return result;
    }

    /// <summary>CompletePropertyDescriptor (6.2.6.6).</summary>
    private static void Complete(ref PropertyDescriptor d)
    {
        if (d.IsGeneric || d.IsData)
        {
            if (!d.HasValue)
            {
                d.HasValue = true;
            }

            d.HasWritable = true;
        }
        else
        {
            d.HasGet = true;
            d.HasSet = true;
        }

        d.HasEnumerable = true;
        d.HasConfigurable = true;
    }

    /// <summary>
    /// IsCompatiblePropertyDescriptor (10.1.6.2): ValidateAndApplyPropertyDescriptor with O undefined, so it only decides
    /// and never changes anything.
    /// </summary>
    internal static bool IsCompatible(bool extensible, in PropertyDescriptor desc, bool has, in PropertyDescriptor current)
    {
        if (!has)
        {
            return extensible;
        }

        if (!current.Configurable)
        {
            if (desc.HasConfigurable && desc.Configurable)
            {
                return false;
            }

            if (desc.HasEnumerable && desc.Enumerable != current.Enumerable)
            {
                return false;
            }

            if (!desc.IsGeneric && desc.IsAccessor != current.IsAccessor)
            {
                return false;
            }

            if (current.IsAccessor)
            {
                if ((desc.HasGet && !Ops.SameValue(desc.Get, current.Get)) || (desc.HasSet && !Ops.SameValue(desc.Set, current.Set)))
                {
                    return false;
                }
            }
            else if (!current.Writable)
            {
                if (desc.HasWritable && desc.Writable)
                {
                    return false;
                }

                if (desc.HasValue && !Ops.SameValue(desc.Value, current.Value))
                {
                    return false;
                }
            }
        }

        return true;
    }
}

internal static partial class Builtins
{
    private static void SetupProxy(Realm r)
    {
        // 28.2.1.1 Proxy ( target, handler ): a constructor without a "prototype" property.
        var ctor = new NativeFunction(r, "Proxy", 2, (t, a, nt) =>
        {
            if (nt is null)
            {
                throw Errors.Type("Constructor Proxy requires 'new'");
            }

            return JsValue.FromObject(ProxyCreate(A(a, 0), A(a, 1)));
        }, true);
        r.Global.DefineDirect("Proxy", JsValue.FromObject(ctor), PropFlags.Hidden);
        r.Intrinsics["%Proxy%"] = ctor;

        // 28.2.2.1 Proxy.revocable ( target, handler )
        r.Method(ctor, "revocable", 2, (t, a, nt) =>
        {
            ProxyObject proxy = ProxyCreate(A(a, 0), A(a, 1));
            ProxyObject? revocable = proxy;
            NativeFunction revoke = r.Fn(string.Empty, 0, (t2, a2, nt2) =>
            {
                revocable?.Revoke();
                revocable = null;
                return default;
            });
            var result = r.NewObject();
            result.CreateDataProperty("proxy", JsValue.FromObject(proxy));
            result.CreateDataProperty("revoke", JsValue.FromObject(revoke));
            return JsValue.FromObject(result);
        });
    }

    /// <summary>ProxyCreate (10.5.14).</summary>
    private static ProxyObject ProxyCreate(JsValue target, JsValue handler)
    {
        if (target.Ref is not JsObject t || handler.Ref is not JsObject h)
        {
            throw Errors.Type("Cannot create proxy with a non-object as target or handler");
        }

        return new ProxyObject(t, h);
    }
}
