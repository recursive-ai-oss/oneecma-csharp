namespace RecursiveAI.OneEcma;

/// <summary>A property descriptor with presence bits (ECMA-262 6.2.6).</summary>
internal struct PropertyDescriptor
{
    internal JsValue Value;
    internal JsValue Get;
    internal JsValue Set;
    internal bool HasValue;
    internal bool HasGet;
    internal bool HasSet;
    internal bool HasWritable;
    internal bool HasEnumerable;
    internal bool HasConfigurable;
    internal bool Writable;
    internal bool Enumerable;
    internal bool Configurable;

    internal readonly bool IsAccessor => HasGet || HasSet;

    internal readonly bool IsData => HasValue || HasWritable;

    internal readonly bool IsGeneric => !IsAccessor && !IsData;

    internal static PropertyDescriptor Data(JsValue v, PropFlags f) => new()
    {
        Value = v, HasValue = true, HasWritable = true, HasEnumerable = true, HasConfigurable = true,
        Writable = (f & PropFlags.Writable) != 0, Enumerable = (f & PropFlags.Enumerable) != 0, Configurable = (f & PropFlags.Configurable) != 0,
    };

    internal static PropertyDescriptor Accessor(JsValue g, JsValue s, PropFlags f) => new()
    {
        Get = g, Set = s, HasGet = true, HasSet = true, HasEnumerable = true, HasConfigurable = true,
        Enumerable = (f & PropFlags.Enumerable) != 0, Configurable = (f & PropFlags.Configurable) != 0,
    };

    internal readonly PropFlags ToFlags()
    {
        PropFlags f = 0;
        if (Writable)
        {
            f |= PropFlags.Writable;
        }

        if (Enumerable)
        {
            f |= PropFlags.Enumerable;
        }

        if (Configurable)
        {
            f |= PropFlags.Configurable;
        }

        if (IsAccessor)
        {
            f = (f & ~PropFlags.Writable) | PropFlags.Accessor;
        }

        return f;
    }
}

/// <summary>
/// An ECMAScript object with the ordinary internal methods of ECMA-262 10.1. Exotic objects derive and override
/// the virtual internal methods; fast paths in the interpreter check for the common ordinary case first.
/// </summary>
public class JsObject
{
    internal Shape Shape;
    internal JsValue[] Slots;
    internal bool Extensible = true;
    internal Shape? ChildRoot;

    /// <summary>Set by exotic subclasses that override <see cref="Get"/> (Proxy, module namespaces, typed arrays).</summary>
    internal bool OverridesGet;

    /// <summary>Set by exotic subclasses that override <see cref="Set"/>.</summary>
    internal bool OverridesSet;

    internal JsObject(Shape shape)
    {
        Shape = shape;
        Slots = shape.Count == 0 ? [] : new JsValue[Math.Max(shape.Count, 4)];
    }

    internal JsObject(JsObject? proto)
        : this(Shape.RootFor(proto))
    {
    }

    /// <summary>The builtinTag used by Object.prototype.toString and class checks.</summary>
    internal virtual string ClassName => "Object";

    /// <summary>The <c>typeof</c> result.</summary>
    internal virtual string TypeOfName => IsCallable ? "function" : "object";

    internal virtual bool IsCallable => false;

    internal virtual bool IsConstructor => false;

    internal JsObject? Proto => Shape.Proto;

    /// <summary>[[IsHTMLDDA]] (B.3.6): only the host test object has it.</summary>
    internal virtual bool IsHtmlDda => false;

    internal virtual bool IsArrayExotic() => false;

    internal virtual bool ProxyTarget(out JsObject? target, out bool revoked)
    {
        target = null;
        revoked = false;
        return false;
    }

    // ---- private elements (7.3.26 and following) ---------------------------------------------

    internal Dictionary<PrivateName, PrivateElement>? Privates;

    internal bool PrivateHas(PrivateName n) => Privates is not null && Privates.ContainsKey(n);

    internal void PrivateFieldAdd(PrivateName n, JsValue v)
    {
        Privates ??= new Dictionary<PrivateName, PrivateElement>();
        if (!Privates.TryAdd(n, new PrivateElement { Kind = 0, Value = v }))
        {
            throw Errors.Type("Cannot initialize " + n.Description + " twice on the same object");
        }
    }

    internal void PrivateAddMethod(PrivateMethodDef m)
    {
        Privates ??= new Dictionary<PrivateName, PrivateElement>();
        if (Privates.TryGetValue(m.Name, out PrivateElement? ex) && ex.Kind == 2 && m.Method is null)
        {
            ex.Getter ??= m.Getter;
            ex.Setter ??= m.Setter;
            return;
        }

        PrivateElement e = m.Method is not null ? new PrivateElement { Kind = 1, Value = JsValue.FromObject(m.Method) } : new PrivateElement { Kind = 2, Getter = m.Getter, Setter = m.Setter };
        if (!Privates.TryAdd(m.Name, e))
        {
            throw Errors.Type("Cannot initialize private methods of class twice on the same object");
        }
    }

    /// <summary>CreateDataPropertyOrThrow for object literal members, with a fast path for new keys on plain objects.</summary>
    internal void DefineLiteralProperty(PropertyKey key, JsValue v)
    {
        if (IsPlainStorage && Extensible && Shape.Lookup(key, out _) < 0)
        {
            AddProperty(key, v, PropFlags.Default);
            return;
        }

        CreateDataPropertyOrThrow(key, v);
    }

    // ---- calls (functions and callable proxies override) -----------------------------------

    internal virtual JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args) => throw Errors.Type("object is not a function");

    internal virtual JsValue Construct(ReadOnlySpan<JsValue> args, JsObject newTarget) => throw Errors.Type("object is not a constructor");

    // ---- internal methods (ECMA-262 10.1) --------------------------------------------------

    internal virtual JsObject? GetPrototypeOf() => Shape.Proto;

    /// <summary>OrdinarySetPrototypeOf (10.1.2.1).</summary>
    internal virtual bool SetPrototypeOf(JsObject? p)
    {
        JsObject? current = GetPrototypeOf();
        if (ReferenceEquals(p, current))
        {
            return true;
        }

        if (!Extensible || ImmutablePrototype)
        {
            return false;
        }

        // Step 8: refuse cycles; stop at a proxy because its [[GetPrototypeOf]] is not ordinary.
        for (JsObject? q = p; q is not null; q = q.OverridesGet ? null : q.Shape.Proto)
        {
            if (ReferenceEquals(q, this))
            {
                return false;
            }
        }

        Shape = Shape.WithProto(p);
        return true;
    }

    /// <summary>True for objects with an immutable prototype (Object.prototype, 10.4.7).</summary>
    internal virtual bool ImmutablePrototype => false;

    internal virtual bool IsExtensibleOp() => Extensible;

    internal virtual bool PreventExtensions()
    {
        Extensible = false;
        return true;
    }

    /// <summary>OrdinaryGetOwnProperty (10.1.5.1).</summary>
    internal virtual bool GetOwnProperty(PropertyKey key, out PropertyDescriptor desc)
    {
        int slot = Shape.Lookup(key, out PropFlags f);
        if (slot < 0)
        {
            desc = default;
            return false;
        }

        desc = DescriptorFor(slot, f);
        return true;
    }

    internal PropertyDescriptor DescriptorFor(int slot, PropFlags f)
    {
        if ((f & PropFlags.Accessor) != 0)
        {
            var pair = (AccessorPair)Slots[slot].Ref!;
            return PropertyDescriptor.Accessor(pair.Getter is null ? default : JsValue.FromObject(pair.Getter), pair.Setter is null ? default : JsValue.FromObject(pair.Setter), f);
        }

        return PropertyDescriptor.Data(Slots[slot], f);
    }

    /// <summary>OrdinaryDefineOwnProperty (10.1.6.1).</summary>
    internal virtual bool DefineOwnProperty(PropertyKey key, in PropertyDescriptor desc)
    {
        bool has = GetOwnProperty(key, out PropertyDescriptor current);
        return ValidateAndApply(key, IsExtensibleOp(), desc, has, current);
    }

    /// <summary>ValidateAndApplyPropertyDescriptor (10.1.6.3).</summary>
    internal bool ValidateAndApply(PropertyKey key, bool extensible, in PropertyDescriptor desc, bool has, in PropertyDescriptor current)
    {
        if (!has)
        {
            if (!extensible)
            {
                return false;
            }

            PropFlags f = desc.ToFlags();
            if (desc.IsAccessor)
            {
                AddProperty(key, new JsValue(new AccessorPair(desc.Get.AsObject, desc.Set.AsObject), 0), f | PropFlags.Accessor);
            }
            else
            {
                AddProperty(key, desc.HasValue ? desc.Value : default, f);
            }

            return true;
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

        bool enumerable = desc.HasEnumerable ? desc.Enumerable : current.Enumerable;
        bool configurable = desc.HasConfigurable ? desc.Configurable : current.Configurable;
        PropertyDescriptor result;
        if (desc.IsGeneric)
        {
            result = current;
            result.Enumerable = enumerable;
            result.Configurable = configurable;
        }
        else if (desc.IsAccessor)
        {
            result = PropertyDescriptor.Accessor(
                desc.HasGet ? desc.Get : (current.IsAccessor ? current.Get : default),
                desc.HasSet ? desc.Set : (current.IsAccessor ? current.Set : default),
                0);
            result.Enumerable = enumerable;
            result.Configurable = configurable;
        }
        else
        {
            result = PropertyDescriptor.Data(
                desc.HasValue ? desc.Value : (current.IsAccessor ? default : current.Value),
                0);
            result.Writable = desc.HasWritable ? desc.Writable : (!current.IsAccessor && current.Writable);
            result.Enumerable = enumerable;
            result.Configurable = configurable;
        }

        ReplaceProperty(key, result);
        return true;
    }

    // ---- storage primitives ----------------------------------------------------------------

    /// <summary>Adds a property known to be absent.</summary>
    internal void AddProperty(PropertyKey key, JsValue value, PropFlags flags)
    {
        int slot = Shape.Count;
        Shape = Shape.Add(key, flags);
        if (slot >= Slots.Length)
        {
            Array.Resize(ref Slots, Math.Max(4, Slots.Length * 2));
        }

        Slots[slot] = value;
    }

    /// <summary>Overwrites an existing own property with the (complete) descriptor, changing attributes if needed.</summary>
    internal void ReplaceProperty(PropertyKey key, in PropertyDescriptor d)
    {
        int slot = Shape.Lookup(key, out PropFlags old);
        PropFlags f = d.ToFlags();
        if (f != old)
        {
            if (!Shape.IsDictionary)
            {
                Shape = Shape.ToDictionary();
            }

            Shape.SetFlags(slot, f);
        }

        Slots[slot] = d.IsAccessor ? new JsValue(new AccessorPair(d.Get.AsObject, d.Set.AsObject), 0) : d.Value;
    }

    /// <summary>Creates or overwrites an own data property without any checks (used while building built-ins).</summary>
    internal void DefineDirect(PropertyKey key, JsValue value, PropFlags flags = PropFlags.Default)
    {
        int slot = Shape.Lookup(key, out PropFlags old);
        if (slot < 0)
        {
            AddProperty(key, value, flags);
            return;
        }

        if (old != flags)
        {
            if (!Shape.IsDictionary)
            {
                Shape = Shape.ToDictionary();
            }

            Shape.SetFlags(slot, flags);
        }

        Slots[slot] = value;
    }

    internal void DefineAccessorDirect(PropertyKey key, JsObject? getter, JsObject? setter, PropFlags flags)
    {
        int slot = Shape.Lookup(key, out PropFlags old);
        flags = (flags & ~PropFlags.Writable) | PropFlags.Accessor;
        var pair = new JsValue(new AccessorPair(getter, setter), 0);
        if (slot < 0)
        {
            AddProperty(key, pair, flags);
            return;
        }

        if (old != flags)
        {
            if (!Shape.IsDictionary)
            {
                Shape = Shape.ToDictionary();
            }

            Shape.SetFlags(slot, flags);
        }

        Slots[slot] = pair;
    }

    // ---- [[HasProperty]], [[Get]], [[Set]], [[Delete]], [[OwnPropertyKeys]] -------------------

    /// <summary>OrdinaryHasProperty (10.1.7.1).</summary>
    internal virtual bool HasProperty(PropertyKey key)
    {
        JsObject? o = this;
        while (o is not null)
        {
            if (o.OverridesGet && !ReferenceEquals(o, this))
            {
                return o.HasProperty(key);
            }

            if (o.GetOwnProperty(key, out _))
            {
                return true;
            }

            o = o.GetPrototypeOf();
        }

        return false;
    }

    /// <summary>Own-property lookup that applies getters; returns false when absent. The ordinary version of the [[Get]] step.</summary>
    internal virtual bool TryGetOwn(PropertyKey key, JsValue receiver, out JsValue value)
    {
        int slot = Shape.Lookup(key, out PropFlags f);
        if (slot < 0)
        {
            value = default;
            return false;
        }

        if ((f & PropFlags.Accessor) == 0)
        {
            value = Slots[slot];
            return true;
        }

        JsObject? getter = ((AccessorPair)Slots[slot].Ref!).Getter;
        value = getter is null ? default : getter.Call(receiver, default);
        return true;
    }

    /// <summary>OrdinaryGet (10.1.8.1), iterative along the prototype chain.</summary>
    internal virtual JsValue Get(PropertyKey key, JsValue receiver)
    {
        JsObject? o = this;
        while (o is not null)
        {
            if (o.OverridesGet && !ReferenceEquals(o, this))
            {
                return o.Get(key, receiver);
            }

            if (o.TryGetOwn(key, receiver, out JsValue v))
            {
                return v;
            }

            o = o.Shape.Proto;
        }

        return default;
    }

    internal JsValue Get(PropertyKey key) => Get(key, JsValue.FromObject(this));

    /// <summary>OrdinarySet (10.1.9.1) with OrdinarySetWithOwnDescriptor.</summary>
    internal virtual bool Set(PropertyKey key, JsValue value, JsValue receiver)
    {
        JsObject? o = this;
        PropertyDescriptor own = default;
        bool found = false;
        while (o is not null)
        {
            if (o.OverridesSet && !ReferenceEquals(o, this))
            {
                return o.Set(key, value, receiver);
            }

            if (o.GetOwnProperty(key, out own))
            {
                found = true;
                break;
            }

            o = o.GetPrototypeOf();
        }

        if (!found)
        {
            own = PropertyDescriptor.Data(default, PropFlags.Default);
        }

        if (own.IsAccessor)
        {
            if (own.Set.AsObject is not JsObject setter)
            {
                return false;
            }

            setter.Call(receiver, [value]);
            return true;
        }

        if (!own.Writable)
        {
            return false;
        }

        if (receiver.Ref is not JsObject r)
        {
            return false;
        }

        if (ReferenceEquals(r, this) && found && ReferenceEquals(o, this) && !r.OverridesSet && r.IsPlainStorage)
        {
            int slot = Shape.Lookup(key, out _);
            if (slot >= 0)
            {
                Slots[slot] = value;
                return true;
            }
        }

        if (r.GetOwnProperty(key, out PropertyDescriptor existing))
        {
            if (existing.IsAccessor || !existing.Writable)
            {
                return false;
            }

            var vd = new PropertyDescriptor { Value = value, HasValue = true };
            return r.DefineOwnProperty(key, vd);
        }

        return r.DefineOwnProperty(key, PropertyDescriptor.Data(value, PropFlags.Default));
    }

    /// <summary>True when own properties live only in the shape and slots (no exotic [[GetOwnProperty]]).</summary>
    internal virtual bool IsPlainStorage => true;

    /// <summary>OrdinaryDelete (10.1.10.1).</summary>
    internal virtual bool Delete(PropertyKey key)
    {
        int slot = Shape.Lookup(key, out PropFlags f);
        if (slot < 0)
        {
            return true;
        }

        if ((f & PropFlags.Configurable) == 0)
        {
            return false;
        }

        RemoveSlot(slot);
        return true;
    }

    internal void RemoveSlot(int slot)
    {
        if (!Shape.IsDictionary)
        {
            Shape = Shape.ToDictionary();
        }

        Shape.Remove(slot);
        Slots[slot] = default;
        if (Shape.NeedsCompaction)
        {
            int[] map = Shape.Compact();
            var ns = new JsValue[Math.Max(4, map.Length * 2)];
            for (int i = 0; i < map.Length; i++)
            {
                ns[i] = Slots[map[i]];
            }

            Slots = ns;
        }
    }

    /// <summary>OrdinaryOwnPropertyKeys (10.1.11.1): integer indices ascending, then strings, then symbols, each in creation order.</summary>
    internal virtual void OwnKeys(List<PropertyKey> keys)
    {
        var all = new List<PropertyKey>(Shape.LiveCount);
        Shape.CollectKeys(all);
        AppendOrdered(all, keys);
    }

    internal static void AppendOrdered(List<PropertyKey> all, List<PropertyKey> keys)
    {
        List<(uint, PropertyKey)>? idx = null;
        foreach (PropertyKey k in all)
        {
            if (k.TryGetIndex(out uint i))
            {
                (idx ??= []).Add((i, k));
            }
        }

        if (idx is not null)
        {
            idx.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
            foreach ((uint, PropertyKey) p in idx)
            {
                keys.Add(p.Item2);
            }
        }

        foreach (PropertyKey k in all)
        {
            if (k.Ref is string && (idx is null || !k.TryGetIndex(out _)))
            {
                keys.Add(k);
            }
        }

        foreach (PropertyKey k in all)
        {
            if (k.IsSymbol)
            {
                keys.Add(k);
            }
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    internal bool CreateDataProperty(PropertyKey key, JsValue v) => DefineOwnProperty(key, PropertyDescriptor.Data(v, PropFlags.Default));

    internal void CreateDataPropertyOrThrow(PropertyKey key, JsValue v)
    {
        if (!CreateDataProperty(key, v))
        {
            throw Errors.Type("Cannot add property " + Ops.KeyText(key) + ", object is not extensible");
        }
    }

    internal void DefinePropertyOrThrow(PropertyKey key, in PropertyDescriptor d)
    {
        if (!DefineOwnProperty(key, d))
        {
            throw Errors.Type("Cannot redefine property: " + Ops.KeyText(key));
        }
    }

    internal bool HasOwnProperty(PropertyKey key) => GetOwnProperty(key, out _);

    /// <summary>The Set abstract operation (7.3.4).</summary>
    internal void SetOrThrow(PropertyKey key, JsValue v, bool strict = true)
    {
        if (!Set(key, v, JsValue.FromObject(this)) && strict)
        {
            throw Errors.Type("Cannot assign to read only property '" + Ops.KeyText(key) + "' of object");
        }
    }
}

/// <summary>A private field, method or accessor on one object. Kind 0 field, 1 method, 2 accessor.</summary>
internal sealed class PrivateElement
{
    internal int Kind;
    internal JsValue Value;
    internal JsObject? Getter;
    internal JsObject? Setter;
}
