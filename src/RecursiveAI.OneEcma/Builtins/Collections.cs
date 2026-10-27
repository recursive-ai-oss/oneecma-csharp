using System.Numerics;

namespace RecursiveAI.OneEcma;

/// <summary>SameValueZero as an equality comparer (ECMA-262 7.2.10), so Map and Set can key a dictionary on JsValue.</summary>
internal sealed class SameValueZeroComparer : IEqualityComparer<JsValue>
{
    internal static readonly SameValueZeroComparer Instance = new();

    public bool Equals(JsValue x, JsValue y) => Ops.SameValueZero(x, y);

    public int GetHashCode(JsValue v)
    {
        if (v.IsNumber)
        {
            // NaN is one key; +0 and -0 are one key.
            return double.IsNaN(v.Num) ? 0x7ff80000 : v.Num == 0 ? 0 : v.Num.GetHashCode();
        }

        return v.Ref switch
        {
            string s => s.GetHashCode(StringComparison.Ordinal),
            Rope r => r.Flatten().GetHashCode(StringComparison.Ordinal),
            BigInteger b => b.GetHashCode(),
            null => 1,
            _ => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v.Ref),
        };
    }
}

/// <summary>
/// The [[MapData]] / [[SetData]] list of ECMA-262 24.1 and 24.2: insertion ordered, keyed by SameValueZero.
/// Entries are nodes of a linked list. A removed node keeps its Prev link, so an iterator parked on it can walk back
/// to a live node and continue with that node's successor; deleting, clearing and re-adding during iteration behave
/// as the spec's "empty" list slots do, and a step never costs O(n).
/// </summary>
internal sealed class OrderedTable
{
    internal sealed class Node
    {
        internal JsValue Key;
        internal JsValue Value;
        internal Node? Next;
        internal Node? Prev;
        internal bool Removed;
    }

    private readonly Dictionary<JsValue, Node> _index = new(SameValueZeroComparer.Instance);
    private Node _tail;

    internal OrderedTable()
    {
        Head = new Node();
        _tail = Head;
    }

    /// <summary>The sentinel before the first entry; an iterator starts here.</summary>
    internal Node Head { get; }

    internal int Count => _index.Count;

    internal static JsValue Canonicalize(JsValue key) => key.IsNumber && key.Num == 0 ? JsValue.FromNumber(0) : key;

    internal Node? Find(JsValue key) => _index.TryGetValue(key, out Node? n) ? n : null;

    /// <summary>Adds or updates; the key must already be canonical.</summary>
    internal void Set(JsValue key, JsValue value)
    {
        if (_index.TryGetValue(key, out Node? n))
        {
            n.Value = value;
            return;
        }

        Agent.CurrentOrNull?.Alloc(80);
        n = new Node { Key = key, Value = value, Prev = _tail };
        _tail.Next = n;
        _tail = n;
        _index[key] = n;
    }

    internal bool Remove(JsValue key)
    {
        if (!_index.Remove(key, out Node? n))
        {
            return false;
        }

        Unlink(n);
        return true;
    }

    private void Unlink(Node n)
    {
        n.Removed = true;
        n.Prev!.Next = n.Next;
        if (n.Next is not null)
        {
            n.Next.Prev = n.Prev;
        }
        else
        {
            _tail = n.Prev;
        }

        n.Next = null;
    }

    internal void Clear()
    {
        for (Node? n = Head.Next; n is not null;)
        {
            Node? next = n.Next;
            n.Removed = true;
            n.Next = null;
            n = next;
        }

        // Removed nodes still point back to Head (possibly through other removed nodes), so parked iterators resume at the first new entry.
        Head.Next = null;
        _tail = Head;
        _index.Clear();
    }

    /// <summary>The first live entry after <paramref name="cursor"/>, which may be a node that has since been removed.</summary>
    internal static Node? Advance(Node cursor)
    {
        Node n = cursor;
        while (n.Removed)
        {
            n = n.Prev!;
        }

        return n.Next;
    }

    internal List<Node> Snapshot()
    {
        var l = new List<Node>(_index.Count);
        for (Node? n = Head.Next; n is not null; n = n.Next)
        {
            l.Add(n);
        }

        return l;
    }
}

internal sealed class JsMap : JsObject
{
    internal readonly OrderedTable Table = new();

    internal JsMap(JsObject? proto)
        : base(proto)
    {
    }

    internal override string ClassName => "Map";
}

internal sealed class JsSet : JsObject
{
    internal readonly OrderedTable Table = new();

    internal JsSet(JsObject? proto)
        : base(proto)
    {
    }

    internal override string ClassName => "Set";
}

/// <summary>A Map Iterator or Set Iterator (24.1.5, 24.2.6). <see cref="Kind"/>: 0 keys, 1 values, 2 entries.</summary>
internal sealed class CollectionIterator : JsObject
{
    internal OrderedTable? Table;
    internal OrderedTable.Node Cursor;
    internal readonly int Kind;
    internal readonly bool IsSet;

    internal CollectionIterator(JsObject? proto, OrderedTable table, int kind, bool isSet)
        : base(proto)
    {
        Table = table;
        Cursor = table.Head;
        Kind = kind;
        IsSet = isSet;
    }

    internal override string ClassName => IsSet ? "Set Iterator" : "Map Iterator";
}

/// <summary>One group produced by <see cref="Builtins.GroupBy"/>.</summary>
internal sealed class KeyedGroup
{
    internal JsValue Key;
    internal readonly List<JsValue> Elements = [];
}

internal static partial class Builtins
{
    private static JsException Incompat(string method, JsValue t) =>
        Errors.Type("Method " + method + " called on incompatible receiver " + (t.IsString ? t.AsString : Ops.DisplayValue(t)));

    private static JsMap ThisMap(JsValue t, string method) => t.Ref as JsMap ?? throw Incompat(method, t);

    private static JsSet ThisSet(JsValue t, string method) => t.Ref as JsSet ?? throw Incompat(method, t);

    private static string ValueText(JsValue v) => v.IsString ? v.AsString : Ops.DisplayValue(v);

    /// <summary>How V8 names a value in "x is not iterable" and "x is not a function" from built-ins: the type, then the value for primitives.</summary>
    private static string KindText(JsValue v) => Ops.KindText(v);

    private static JsException NotFunction(JsValue v) => Errors.Type(KindText(v) + " is not a function");

    /// <summary>GetIterator (7.4.3) with V8's wording for a value without Symbol.iterator.</summary>
    private static IteratorRecord GetIteratorFor(JsValue iterable)
    {
        return Ops.GetIterator(iterable);
    }

    /// <summary>
    /// GroupBy (7.3.35) shared by Map.groupBy and Object.groupBy: groups in first-seen key order.
    /// With <paramref name="propertyKeys"/> the callback result goes through ToPropertyKey, otherwise -0 becomes +0.
    /// </summary>
    internal static List<KeyedGroup> GroupBy(JsValue items, JsValue callback, bool propertyKeys, string who)
    {
        if (items.IsNullish)
        {
            throw Errors.Type(who + " called on null or undefined");
        }

        if (!Ops.IsCallable(callback))
        {
            throw Errors.Type(ValueText(callback) + " is not a function");
        }

        var groups = new List<KeyedGroup>();
        var byKey = new Dictionary<JsValue, KeyedGroup>(SameValueZeroComparer.Instance);
        IteratorRecord rec = GetIteratorFor(items);
        double k = 0;
        while (Ops.IteratorStepValue(rec, out JsValue value))
        {
            Agent.Current.Tick();
            JsValue key;
            try
            {
                key = Ops.Call(callback, default, [value, Ops.Num(k)]);
                key = propertyKeys ? Ops.ToPropertyKey(key).ToValue() : OrderedTable.Canonicalize(key);
            }
            catch (JsException)
            {
                Ops.IteratorCloseSilently(rec);
                throw;
            }

            if (!byKey.TryGetValue(key, out KeyedGroup? g))
            {
                g = new KeyedGroup { Key = key };
                byKey[key] = g;
                groups.Add(g);
            }

            g.Elements.Add(value);
            k++;
        }

        return groups;
    }

    /// <summary>
    /// The shared body of the Map, Set, WeakMap and WeakSet constructors: create from newTarget, then feed the
    /// iterable through the (looked-up) adder method. <paramref name="pairs"/> reads each item as a [key, value] entry.
    /// </summary>
    private static JsValue ConstructCollection(string name, JsObject? nt, JsValue iterable, string protoName, Func<JsObject, JsObject> create, string adderName, bool pairs)
    {
        if (nt is null)
        {
            throw Errors.Type("Constructor " + name + " requires 'new'");
        }

        JsObject obj = create(Ops.GetPrototypeFromConstructor(nt, x => x.Intrinsics[protoName]));
        if (iterable.IsNullish)
        {
            return JsValue.FromObject(obj);
        }

        JsValue adder = obj.Get(adderName);
        if (!Ops.IsCallable(adder))
        {
            throw Errors.Type("'" + ValueText(adder) + "' returned for property '" + adderName + "' of object '" + Ops.DisplayValue(JsValue.FromObject(obj)) + "' is not a function");
        }

        IteratorRecord rec = GetIteratorFor(iterable);
        var self = JsValue.FromObject(obj);
        while (Ops.IteratorStepValue(rec, out JsValue next))
        {
            Agent.Current.Tick();
            try
            {
                if (pairs)
                {
                    if (next.Ref is not JsObject item)
                    {
                        throw Errors.Type("Iterator value " + ValueText(next) + " is not an entry object");
                    }

                    JsValue k = item.Get("0");
                    JsValue v = item.Get("1");
                    Ops.Call(adder, self, [k, v]);
                }
                else
                {
                    Ops.Call(adder, self, [next]);
                }
            }
            catch (JsException)
            {
                Ops.IteratorCloseSilently(rec);
                throw;
            }
        }

        return self;
    }

    private static void SetupCollections(Realm r)
    {
        SetupMap(r);
        SetupSet(r);
        SetupCollectionIterators(r);
    }

    private static JsObject IterProto(Realm r, bool isSet) => r.Intrinsics[isSet ? "%SetIteratorPrototype%" : "%MapIteratorPrototype%"];

    private static void SetupMap(Realm r)
    {
        var proto = r.NewObject();
        r.Intrinsics["%Map.prototype%"] = proto;
        NativeFunction ctor = r.Ctor("Map", 0, (t, a, nt) =>
            ConstructCollection("Map", nt, A(a, 0), "%Map.prototype%", p => new JsMap(p), "set", true), proto);
        r.Getter(ctor, new PropertyKey(WellKnown.Species), "[Symbol.species]", (t, a, nt) => t);

        // 24.1.2.1 Map.groupBy ( items, callbackfn )
        r.Method(ctor, "groupBy", 2, (t, a, nt) =>
        {
            List<KeyedGroup> groups = GroupBy(A(a, 0), A(a, 1), false, "Map.groupBy");
            var map = new JsMap(r.Intrinsics["%Map.prototype%"]);
            foreach (KeyedGroup g in groups)
            {
                map.Table.Set(g.Key, JsValue.FromObject(Ops.CreateArray(g.Elements)));
            }

            return JsValue.FromObject(map);
        });

        r.Method(proto, "get", 1, (t, a, nt) => ThisMap(t, "Map.prototype.get").Table.Find(OrderedTable.Canonicalize(A(a, 0)))?.Value ?? default);
        r.Method(proto, "set", 2, (t, a, nt) =>
        {
            ThisMap(t, "Map.prototype.set").Table.Set(OrderedTable.Canonicalize(A(a, 0)), A(a, 1));
            return t;
        });
        r.Method(proto, "has", 1, (t, a, nt) => Ops.Bool(ThisMap(t, "Map.prototype.has").Table.Find(OrderedTable.Canonicalize(A(a, 0))) is not null));
        r.Method(proto, "delete", 1, (t, a, nt) => Ops.Bool(ThisMap(t, "Map.prototype.delete").Table.Remove(OrderedTable.Canonicalize(A(a, 0)))));
        r.Method(proto, "clear", 0, (t, a, nt) =>
        {
            ThisMap(t, "Map.prototype.clear").Table.Clear();
            return default;
        });
        r.Getter(proto, "size", "size", (t, a, nt) => Ops.Num(ThisMap(t, "get Map.prototype.size").Table.Count));

        // 24.1.3.5 Map.prototype.forEach: walks the live list, so entries added by the callback are visited and deleted ones are not.
        r.Method(proto, "forEach", 1, (t, a, nt) =>
        {
            JsMap m = ThisMap(t, "Map.prototype.forEach");
            if (!Ops.IsCallable(A(a, 0)))
            {
                throw NotFunction(A(a, 0));
            }

            OrderedTable.Node cursor = m.Table.Head;
            while (OrderedTable.Advance(cursor) is { } n)
            {
                cursor = n;
                Agent.Current.Tick();
                Ops.Call(A(a, 0), A(a, 1), [n.Value, n.Key, t]);
            }

            return default;
        });

        // 24.1.3.7 / 24.1.3.8 (ES2026 upsert)
        r.Method(proto, "getOrInsert", 2, (t, a, nt) =>
        {
            JsMap m = ThisMap(t, "Map.prototype.getOrInsert");
            JsValue key = OrderedTable.Canonicalize(A(a, 0));
            if (m.Table.Find(key) is { } n)
            {
                return n.Value;
            }

            m.Table.Set(key, A(a, 1));
            return A(a, 1);
        });
        r.Method(proto, "getOrInsertComputed", 2, (t, a, nt) =>
        {
            JsMap m = ThisMap(t, "Map.prototype.getOrInsertComputed");
            if (!Ops.IsCallable(A(a, 1)))
            {
                throw Errors.Type(ValueText(A(a, 1)) + " is not a function");
            }

            JsValue key = OrderedTable.Canonicalize(A(a, 0));
            if (m.Table.Find(key) is { } n)
            {
                return n.Value;
            }

            // The callback may have changed the map; the entry is searched again before appending.
            JsValue value = Ops.Call(A(a, 1), default, [key]);
            m.Table.Set(key, value);
            return value;
        });

        NativeFunction entries = r.Method(proto, "entries", 0, (t, a, nt) =>
            JsValue.FromObject(new CollectionIterator(IterProto(r, false), ThisMap(t, "Map.prototype.entries").Table, 2, false)));
        r.Method(proto, "keys", 0, (t, a, nt) =>
            JsValue.FromObject(new CollectionIterator(IterProto(r, false), ThisMap(t, "Map.prototype.keys").Table, 0, false)));
        r.Method(proto, "values", 0, (t, a, nt) =>
            JsValue.FromObject(new CollectionIterator(IterProto(r, false), ThisMap(t, "Map.prototype.values").Table, 1, false)));
        proto.DefineDirect(new PropertyKey(WellKnown.Iterator), JsValue.FromObject(entries), PropFlags.Hidden);
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Map"), PropFlags.Configurable);
    }

    private static void SetupCollectionIterators(Realm r)
    {
        foreach (bool isSet in new[] { false, true })
        {
            string kind = isSet ? "Set" : "Map";
            var ip = new JsObject(r.IteratorPrototype);
            r.Intrinsics["%" + kind + "IteratorPrototype%"] = ip;
            r.Method(ip, "next", 0, (t, a, nt) =>
            {
                if (t.Ref is not CollectionIterator it || it.IsSet != isSet)
                {
                    throw Incompat(kind + " Iterator.prototype.next", t);
                }

                if (it.Table is null)
                {
                    return JsValue.FromObject(Ops.CreateIterResult(default, true));
                }

                if (OrderedTable.Advance(it.Cursor) is not { } n)
                {
                    it.Table = null;
                    return JsValue.FromObject(Ops.CreateIterResult(default, true));
                }

                it.Cursor = n;
                Agent.Current.Tick();
                JsValue result = it.Kind switch
                {
                    0 => n.Key,
                    1 => isSet ? n.Key : n.Value,
                    _ => JsValue.FromObject(Ops.CreateArray([n.Key, isSet ? n.Key : n.Value])),
                };
                return JsValue.FromObject(Ops.CreateIterResult(result, false));
            });
            ip.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str(kind + " Iterator"), PropFlags.Configurable);
        }
    }

    // ---- Set ---------------------------------------------------------------------------------------------------------

    /// <summary>A Set Record (24.2.1.1).</summary>
    private readonly record struct SetRecord(JsObject Obj, double Size, JsValue Has, JsValue Keys);

    /// <summary>GetSetRecord (24.2.1.2): the observable order is size, has, keys.</summary>
    private static SetRecord GetSetRecord(JsValue other, string method)
    {
        if (other.Ref is not JsObject o)
        {
            throw Errors.Type(method + " argument must be an object");
        }

        double num = Ops.ToNumber(o.Get("size"));
        if (double.IsNaN(num))
        {
            throw Errors.Type("The .size property is NaN");
        }

        double size = Ops.ToIntegerOrInfinity(Ops.Num(num));
        if (size < 0)
        {
            throw Errors.Range("'" + Ops.NumberToStr(size) + "' is an invalid size");
        }

        JsValue has = o.Get("has");
        if (!Ops.IsCallable(has))
        {
            throw Errors.Type("string \"has\" is not a function");
        }

        JsValue keys = o.Get("keys");
        if (!Ops.IsCallable(keys))
        {
            throw Errors.Type("string \"keys\" is not a function");
        }

        return new SetRecord(o, size, has, keys);
    }

    private static bool CallHas(in SetRecord rec, JsValue e) =>
        Ops.ToBoolean(Ops.Call(rec.Has, JsValue.FromObject(rec.Obj), [e]));

    private static IteratorRecord KeysIterator(in SetRecord rec)
    {
        JsValue it = Ops.Call(rec.Keys, JsValue.FromObject(rec.Obj));
        if (it.Ref is not JsObject io)
        {
            throw Errors.Type("Result of the keys method is not an object");
        }

        return new IteratorRecord(io, io.Get("next"));
    }

    private static JsSet CopySet(Realm r, JsSet src)
    {
        var res = new JsSet(r.Intrinsics["%Set.prototype%"]);
        for (OrderedTable.Node? n = src.Table.Head.Next; n is not null; n = n.Next)
        {
            res.Table.Set(n.Key, default);
        }

        return res;
    }

    private static void SetupSet(Realm r)
    {
        var proto = r.NewObject();
        r.Intrinsics["%Set.prototype%"] = proto;
        NativeFunction ctor = r.Ctor("Set", 0, (t, a, nt) =>
            ConstructCollection("Set", nt, A(a, 0), "%Set.prototype%", p => new JsSet(p), "add", false), proto);
        r.Getter(ctor, new PropertyKey(WellKnown.Species), "[Symbol.species]", (t, a, nt) => t);

        r.Method(proto, "has", 1, (t, a, nt) => Ops.Bool(ThisSet(t, "Set.prototype.has").Table.Find(OrderedTable.Canonicalize(A(a, 0))) is not null));
        r.Method(proto, "add", 1, (t, a, nt) =>
        {
            JsSet s = ThisSet(t, "Set.prototype.add");
            JsValue k = OrderedTable.Canonicalize(A(a, 0));
            if (s.Table.Find(k) is null)
            {
                s.Table.Set(k, default);
            }

            return t;
        });
        r.Method(proto, "delete", 1, (t, a, nt) => Ops.Bool(ThisSet(t, "Set.prototype.delete").Table.Remove(OrderedTable.Canonicalize(A(a, 0)))));
        r.Method(proto, "clear", 0, (t, a, nt) =>
        {
            ThisSet(t, "Set.prototype.clear").Table.Clear();
            return default;
        });
        r.Getter(proto, "size", "size", (t, a, nt) => Ops.Num(ThisSet(t, "get Set.prototype.size").Table.Count));
        r.Method(proto, "forEach", 1, (t, a, nt) =>
        {
            JsSet s = ThisSet(t, "Set.prototype.forEach");
            if (!Ops.IsCallable(A(a, 0)))
            {
                throw NotFunction(A(a, 0));
            }

            OrderedTable.Node cursor = s.Table.Head;
            while (OrderedTable.Advance(cursor) is { } n)
            {
                cursor = n;
                Agent.Current.Tick();
                Ops.Call(A(a, 0), A(a, 1), [n.Key, n.Key, t]);
            }

            return default;
        });
        r.Method(proto, "entries", 0, (t, a, nt) =>
            JsValue.FromObject(new CollectionIterator(IterProto(r, true), ThisSet(t, "Set.prototype.entries").Table, 2, true)));
        NativeFunction values = r.Method(proto, "values", 0, (t, a, nt) =>
            JsValue.FromObject(new CollectionIterator(IterProto(r, true), ThisSet(t, "Set.prototype.values").Table, 1, true)));
        proto.DefineDirect("keys", JsValue.FromObject(values), PropFlags.Hidden);
        proto.DefineDirect(new PropertyKey(WellKnown.Iterator), JsValue.FromObject(values), PropFlags.Hidden);
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Set"), PropFlags.Configurable);

        // ES2025 set methods (24.2.4.x). Loops over this set's data walk the live list, as the spec's index loops do.
        r.Method(proto, "union", 1, (t, a, nt) =>
        {
            JsSet o = ThisSet(t, "Set.prototype.union");
            SetRecord other = GetSetRecord(A(a, 0), "Set.prototype.union");
            IteratorRecord keys = KeysIterator(other);
            JsSet result = CopySet(r, o);
            while (Ops.IteratorStepValue(keys, out JsValue next))
            {
                Agent.Current.Tick();
                JsValue k = OrderedTable.Canonicalize(next);
                if (result.Table.Find(k) is null)
                {
                    result.Table.Set(k, default);
                }
            }

            return JsValue.FromObject(result);
        });
        r.Method(proto, "intersection", 1, (t, a, nt) =>
        {
            JsSet o = ThisSet(t, "Set.prototype.intersection");
            SetRecord other = GetSetRecord(A(a, 0), "Set.prototype.intersection");
            var result = new JsSet(r.Intrinsics["%Set.prototype%"]);
            if (o.Table.Count <= other.Size)
            {
                OrderedTable.Node cursor = o.Table.Head;
                while (OrderedTable.Advance(cursor) is { } n)
                {
                    cursor = n;
                    Agent.Current.Tick();
                    JsValue e = n.Key;
                    if (CallHas(other, e) && result.Table.Find(e) is null)
                    {
                        result.Table.Set(e, default);
                    }
                }
            }
            else
            {
                IteratorRecord keys = KeysIterator(other);
                while (Ops.IteratorStepValue(keys, out JsValue next))
                {
                    Agent.Current.Tick();
                    JsValue k = OrderedTable.Canonicalize(next);
                    if (o.Table.Find(k) is not null && result.Table.Find(k) is null)
                    {
                        result.Table.Set(k, default);
                    }
                }
            }

            return JsValue.FromObject(result);
        });
        r.Method(proto, "difference", 1, (t, a, nt) =>
        {
            JsSet o = ThisSet(t, "Set.prototype.difference");
            SetRecord other = GetSetRecord(A(a, 0), "Set.prototype.difference");
            JsSet result = CopySet(r, o);
            if (o.Table.Count <= other.Size)
            {
                // The loop runs over the copy, so changes `has` makes to this set do not change what is visited.
                foreach (OrderedTable.Node n in result.Table.Snapshot())
                {
                    Agent.Current.Tick();
                    if (CallHas(other, n.Key))
                    {
                        result.Table.Remove(n.Key);
                    }
                }
            }
            else
            {
                IteratorRecord keys = KeysIterator(other);
                while (Ops.IteratorStepValue(keys, out JsValue next))
                {
                    Agent.Current.Tick();
                    result.Table.Remove(OrderedTable.Canonicalize(next));
                }
            }

            return JsValue.FromObject(result);
        });
        r.Method(proto, "symmetricDifference", 1, (t, a, nt) =>
        {
            JsSet o = ThisSet(t, "Set.prototype.symmetricDifference");
            SetRecord other = GetSetRecord(A(a, 0), "Set.prototype.symmetricDifference");
            IteratorRecord keys = KeysIterator(other);
            JsSet result = CopySet(r, o);
            while (Ops.IteratorStepValue(keys, out JsValue next))
            {
                Agent.Current.Tick();
                JsValue k = OrderedTable.Canonicalize(next);
                bool inResult = result.Table.Find(k) is not null;
                if (o.Table.Find(k) is not null)
                {
                    if (inResult)
                    {
                        result.Table.Remove(k);
                    }
                }
                else if (!inResult)
                {
                    result.Table.Set(k, default);
                }
            }

            return JsValue.FromObject(result);
        });
        r.Method(proto, "isSubsetOf", 1, (t, a, nt) =>
        {
            JsSet o = ThisSet(t, "Set.prototype.isSubsetOf");
            SetRecord other = GetSetRecord(A(a, 0), "Set.prototype.isSubsetOf");
            if (o.Table.Count > other.Size)
            {
                return JsValue.False;
            }

            OrderedTable.Node cursor = o.Table.Head;
            while (OrderedTable.Advance(cursor) is { } n)
            {
                cursor = n;
                Agent.Current.Tick();
                if (!CallHas(other, n.Key))
                {
                    return JsValue.False;
                }
            }

            return JsValue.True;
        });
        r.Method(proto, "isSupersetOf", 1, (t, a, nt) =>
        {
            JsSet o = ThisSet(t, "Set.prototype.isSupersetOf");
            SetRecord other = GetSetRecord(A(a, 0), "Set.prototype.isSupersetOf");
            if (o.Table.Count < other.Size)
            {
                return JsValue.False;
            }

            IteratorRecord keys = KeysIterator(other);
            while (Ops.IteratorStepValue(keys, out JsValue next))
            {
                Agent.Current.Tick();
                if (o.Table.Find(OrderedTable.Canonicalize(next)) is null)
                {
                    Ops.IteratorClose(keys);
                    return JsValue.False;
                }
            }

            return JsValue.True;
        });
        r.Method(proto, "isDisjointFrom", 1, (t, a, nt) =>
        {
            JsSet o = ThisSet(t, "Set.prototype.isDisjointFrom");
            SetRecord other = GetSetRecord(A(a, 0), "Set.prototype.isDisjointFrom");
            if (o.Table.Count <= other.Size)
            {
                OrderedTable.Node cursor = o.Table.Head;
                while (OrderedTable.Advance(cursor) is { } n)
                {
                    cursor = n;
                    Agent.Current.Tick();
                    if (CallHas(other, n.Key))
                    {
                        return JsValue.False;
                    }
                }
            }
            else
            {
                IteratorRecord keys = KeysIterator(other);
                while (Ops.IteratorStepValue(keys, out JsValue next))
                {
                    Agent.Current.Tick();
                    if (o.Table.Find(OrderedTable.Canonicalize(next)) is not null)
                    {
                        Ops.IteratorClose(keys);
                        return JsValue.False;
                    }
                }
            }

            return JsValue.True;
        });
    }
}
