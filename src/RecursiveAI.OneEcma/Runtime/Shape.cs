namespace RecursiveAI.OneEcma;

/// <summary>Attribute bits stored per property in a <see cref="Shape"/>.</summary>
[Flags]
internal enum PropFlags : byte
{
    None = 0,
    Writable = 1,
    Enumerable = 2,
    Configurable = 4,
    Accessor = 8,

    /// <summary>Writable, enumerable and configurable: what assignment creates.</summary>
    Default = 7,

    /// <summary>Writable and configurable, not enumerable: what built-in methods use.</summary>
    Hidden = 5,
}

/// <summary>A getter/setter pair stored in the slot of an accessor property.</summary>
internal sealed class AccessorPair
{
    internal JsObject? Getter;
    internal JsObject? Setter;

    internal AccessorPair(JsObject? g, JsObject? s)
    {
        Getter = g;
        Setter = s;
    }
}

/// <summary>
/// A hidden class: the prototype plus the ordered keys and attributes of an object. Objects with the same
/// history share a shape, so a property site can cache "shape S has key K in slot N". The prototype is part of
/// the shape, so a cached prototype hit needs no walk. A shape in dictionary mode belongs to one object, is
/// mutated in place, and is never cached by a site.
/// </summary>
internal sealed class Shape
{
    private static readonly Shape NullProtoRoot = new(null, default, 0, null, false);

    private Dictionary<PropertyKey, int>? _map;
    private Dictionary<(PropertyKey, byte), Shape>? _transitions;
    private List<DictEntry>? _entries;
    private Dictionary<PropertyKey, int>? _dictIndex;
    private int _live;

    private Shape(Shape? parent, PropertyKey key, PropFlags flags, JsObject? proto, bool dict)
    {
        Parent = parent;
        Key = key;
        Flags = flags;
        Proto = parent is null ? proto : parent.Proto;
        Count = parent is null ? 0 : parent.Count + 1;
        IsDictionary = dict;
    }

    private struct DictEntry
    {
        internal PropertyKey Key;
        internal PropFlags Flags;
        internal bool Deleted;
    }

    internal Shape? Parent { get; }

    internal PropertyKey Key { get; }

    internal PropFlags Flags { get; }

    internal JsObject? Proto { get; private set; }

    /// <summary>Number of slots in use (including tombstones in dictionary mode).</summary>
    internal int Count { get; private set; }

    internal bool IsDictionary { get; }

    internal int LiveCount => IsDictionary ? _live : Count;

    internal static Shape RootFor(JsObject? proto) => proto is null ? NullProtoRoot : proto.ChildRoot ??= new Shape(null, default, 0, proto, false);

    internal int Lookup(PropertyKey key, out PropFlags flags)
    {
        if (IsDictionary)
        {
            if (_dictIndex!.TryGetValue(key, out int i))
            {
                flags = _entries![i].Flags;
                return i;
            }

            flags = 0;
            return -1;
        }

        if (Count <= 8)
        {
            for (Shape? s = this; s is not null && s.Parent is not null; s = s.Parent)
            {
                if (s.Key.Equals(key))
                {
                    flags = s.Flags;
                    return s.Count - 1;
                }
            }

            flags = 0;
            return -1;
        }

        if (_map is null)
        {
            var m = new Dictionary<PropertyKey, int>(Count, PropertyKeyComparer.Instance);
            for (Shape? s = this; s is not null && s.Parent is not null; s = s.Parent)
            {
                m[s.Key] = ((s.Count - 1) << 4) | (int)s.Flags;
            }

            _map = m;
        }

        if (_map.TryGetValue(key, out int packed))
        {
            flags = (PropFlags)(packed & 15);
            return packed >> 4;
        }

        flags = 0;
        return -1;
    }

    private PropFlags FlagsAt(int slot)
    {
        Shape s = this;
        while (s.Count - 1 != slot)
        {
            s = s.Parent!;
        }

        return s.Flags;
    }

    /// <summary>The shape after adding <paramref name="key"/> (in place for a dictionary shape).</summary>
    internal Shape Add(PropertyKey key, PropFlags flags)
    {
        if (IsDictionary)
        {
            _dictIndex![key] = _entries!.Count;
            _entries.Add(new DictEntry { Key = key, Flags = flags });
            Count++;
            _live++;
            return this;
        }

        if (Count >= 128)
        {
            return ToDictionary().Add(key, flags);
        }

        _transitions ??= new Dictionary<(PropertyKey, byte), Shape>();
        if (_transitions.TryGetValue((key, (byte)flags), out Shape? next))
        {
            return next;
        }

        next = new Shape(this, key, flags, null, false);
        _transitions[(key, (byte)flags)] = next;
        return next;
    }

    /// <summary>Keys in creation order (live properties only).</summary>
    internal void CollectKeys(List<PropertyKey> into)
    {
        if (IsDictionary)
        {
            foreach (DictEntry e in _entries!)
            {
                if (!e.Deleted)
                {
                    into.Add(e.Key);
                }
            }

            return;
        }

        int start = into.Count;
        for (Shape? s = this; s is not null && s.Parent is not null; s = s.Parent)
        {
            into.Add(s.Key);
        }

        into.Reverse(start, into.Count - start);
    }

    internal PropertyKey KeyAt(int slot)
    {
        if (IsDictionary)
        {
            return _entries![slot].Key;
        }

        Shape s = this;
        while (s.Count - 1 != slot)
        {
            s = s.Parent!;
        }

        return s.Key;
    }

    internal PropFlags FlagsOfSlot(int slot) => IsDictionary ? _entries![slot].Flags : FlagsAt(slot);

    internal bool IsDeleted(int slot) => IsDictionary && _entries![slot].Deleted;

    /// <summary>A private dictionary-mode copy with the same keys, attributes and prototype.</summary>
    internal Shape ToDictionary()
    {
        var d = new Shape(null, default, 0, Proto, true)
        {
            _entries = new List<DictEntry>(Count + 4),
            _dictIndex = new Dictionary<PropertyKey, int>(Count + 4, PropertyKeyComparer.Instance),
        };
        var keys = new List<PropertyKey>(Count);
        var flags = new List<PropFlags>(Count);
        for (Shape? s = this; s is not null && s.Parent is not null; s = s.Parent)
        {
            keys.Add(s.Key);
            flags.Add(s.Flags);
        }

        for (int i = keys.Count - 1; i >= 0; i--)
        {
            d._dictIndex[keys[i]] = d._entries.Count;
            d._entries.Add(new DictEntry { Key = keys[i], Flags = flags[i] });
        }

        d.Count = d._entries.Count;
        d._live = d.Count;
        return d;
    }

    internal void SetFlags(int slot, PropFlags flags)
    {
        DictEntry e = _entries![slot];
        e.Flags = flags;
        _entries[slot] = e;
    }

    internal void Remove(int slot)
    {
        DictEntry e = _entries![slot];
        e.Deleted = true;
        _entries[slot] = e;
        _dictIndex!.Remove(e.Key);
        _live--;
    }

    internal bool NeedsCompaction => IsDictionary && _entries!.Count > 16 && _live * 2 < _entries.Count;

    /// <summary>Drops tombstones; returns the old slot index for each new slot.</summary>
    internal int[] Compact()
    {
        var map = new List<int>(_live);
        var ne = new List<DictEntry>(_live);
        _dictIndex!.Clear();
        for (int i = 0; i < _entries!.Count; i++)
        {
            if (!_entries[i].Deleted)
            {
                _dictIndex[_entries[i].Key] = ne.Count;
                ne.Add(_entries[i]);
                map.Add(i);
            }
        }

        _entries = ne;
        Count = ne.Count;
        return [.. map];
    }

    /// <summary>The shape with the same properties and another prototype.</summary>
    internal Shape WithProto(JsObject? proto)
    {
        if (IsDictionary)
        {
            Proto = proto;
            return this;
        }

        var keys = new List<PropertyKey>(Count);
        var flags = new List<PropFlags>(Count);
        for (Shape? s = this; s is not null && s.Parent is not null; s = s.Parent)
        {
            keys.Add(s.Key);
            flags.Add(s.Flags);
        }

        Shape r = RootFor(proto);
        for (int i = keys.Count - 1; i >= 0; i--)
        {
            r = r.Add(keys[i], flags[i]);
        }

        return r;
    }
}
