using System.Globalization;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneEcma.ExecutorSpike;

/// <summary>Marker objects that stand for the non-reference value kinds inside <see cref="JsValue"/>.</summary>
internal sealed class Tag
{
    internal Tag(string name) => Name = name;

    internal string Name { get; }
}

/// <summary>
/// The shared value representation: a 16-byte struct holding one reference and one double. A number is
/// <c>(NumberTag, d)</c>, so arithmetic never allocates; every other kind lives in the reference (a tag for
/// undefined/null/booleans, a <see cref="string"/> or <see cref="Rope"/>, or a <see cref="JsObject"/>).
/// <c>default</c> is undefined, which makes freshly zeroed register files and slot arrays correct.
/// </summary>
internal readonly struct JsValue
{
    internal static readonly Tag NumberTag = new("number");
    internal static readonly Tag NullTag = new("null");
    internal static readonly Tag TrueTag = new("true");
    internal static readonly Tag FalseTag = new("false");
    internal static readonly Tag TdzTag = new("tdz");

    internal readonly object? Ref;
    internal readonly double Num;

    internal JsValue(object? r, double d)
    {
        Ref = r;
        Num = d;
    }

    internal static JsValue Undefined => default;

    internal static JsValue Null => new(NullTag, 0);

    internal static JsValue True => new(TrueTag, 0);

    internal static JsValue False => new(FalseTag, 0);

    internal static JsValue Tdz => new(TdzTag, 0);

    internal bool IsUndefined => Ref is null;

    internal bool IsNull => ReferenceEquals(Ref, NullTag);

    internal bool IsNullish => Ref is null || ReferenceEquals(Ref, NullTag);

    internal bool IsNumber => ReferenceEquals(Ref, NumberTag);

    internal bool IsBool => ReferenceEquals(Ref, TrueTag) || ReferenceEquals(Ref, FalseTag);

    internal bool IsTdz => ReferenceEquals(Ref, TdzTag);

    internal bool IsString => Ref is string or Rope;

    internal bool IsObject => Ref is JsObject;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JsValue FromNumber(double d) => new(NumberTag, d);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JsValue FromBool(bool b) => new(b ? TrueTag : FalseTag, 0);

    internal static JsValue FromString(string s) => new(s, 0);

    internal static JsValue FromObject(JsObject o) => new(o, 0);

    public override string ToString() => Conv.ToStr(this);
}

/// <summary>A lazily flattened concatenation, so <c>s = s + x</c> in a loop is linear instead of quadratic.</summary>
internal sealed class Rope
{
    private object _left;
    private object? _right;
    private string? _flat;

    internal Rope(object left, object right, int length)
    {
        _left = left;
        _right = right;
        Length = length;
    }

    internal int Length { get; }

    /// <summary>Flattens with an explicit stack: ropes built in a loop are as deep as the loop ran.</summary>
    internal string Flatten()
    {
        if (_flat is not null)
        {
            return _flat;
        }

        string result = string.Create(Length, this, static (span, root) =>
        {
            var stack = new Stack<object>();
            stack.Push(root);
            int pos = 0;
            while (stack.Count > 0)
            {
                object n = stack.Pop();
                if (n is string s)
                {
                    s.CopyTo(span[pos..]);
                    pos += s.Length;
                }
                else
                {
                    var r = (Rope)n;
                    if (r._flat is not null)
                    {
                        r._flat.CopyTo(span[pos..]);
                        pos += r._flat.Length;
                    }
                    else
                    {
                        stack.Push(r._right!);
                        stack.Push(r._left);
                    }
                }
            }
        });
        _flat = result;
        _left = result;
        _right = null;
        return result;
    }
}

/// <summary>
/// A hidden class: the prototype plus the ordered property keys of an object. Objects with the same history
/// share a shape, so a property site can cache "shape S has key K in slot N" and skip the lookup. The
/// prototype is part of the shape, so a cached prototype hit needs no walk.
/// </summary>
internal sealed class Shape
{
    private static readonly Shape NullProtoRoot = new(null, null, null);

    private Dictionary<string, int>? _map;
    private Dictionary<string, Shape>? _transitions;

    private Shape(Shape? parent, string? key, JsObject? proto)
    {
        Parent = parent;
        Key = key;
        Proto = parent is null ? proto : parent.Proto;
        Count = parent is null ? 0 : parent.Count + 1;
    }

    internal Shape? Parent { get; }

    internal string? Key { get; }

    internal JsObject? Proto { get; }

    /// <summary>The number of properties; the newest one lives in slot <c>Count - 1</c>.</summary>
    internal int Count { get; }

    internal static Shape RootFor(JsObject? proto)
    {
        if (proto is null)
        {
            return NullProtoRoot;
        }

        return proto.ChildRoot ??= new Shape(null, null, proto);
    }

    internal int Lookup(string key)
    {
        if (Count <= 8)
        {
            for (Shape? s = this; s is not null && s.Key is not null; s = s.Parent)
            {
                if ((object)s.Key == key || s.Key == key)
                {
                    return s.Count - 1;
                }
            }

            return -1;
        }

        if (_map is null)
        {
            var m = new Dictionary<string, int>(Count, StringComparer.Ordinal);
            for (Shape? s = this; s is not null && s.Key is not null; s = s.Parent)
            {
                m[s.Key] = s.Count - 1;
            }

            _map = m;
        }

        return _map.GetValueOrDefault(key, -1);
    }

    internal Shape With(string key)
    {
        _transitions ??= new Dictionary<string, Shape>(StringComparer.Ordinal);
        if (!_transitions.TryGetValue(key, out Shape? next))
        {
            next = new Shape(this, key, null);
            _transitions[key] = next;
        }

        return next;
    }

    internal IEnumerable<string> Keys()
    {
        var keys = new List<string>(Count);
        for (Shape? s = this; s is not null && s.Key is not null; s = s.Parent)
        {
            keys.Add(s.Key);
        }

        keys.Reverse();
        return keys;
    }
}

/// <summary>An ordinary object: a shape and a slot array.</summary>
internal class JsObject
{
    internal Shape Shape;
    internal JsValue[] Slots;
    internal Shape? ChildRoot;

    internal JsObject(JsObject? proto, int capacity = 0)
    {
        Shape = Shape.RootFor(proto);
        Slots = capacity == 0 ? [] : new JsValue[capacity];
    }

    internal JsObject? Proto => Shape.Proto;

    internal virtual JsValue Get(string key)
    {
        for (JsObject? o = this; o is not null; o = o.Proto)
        {
            if (o.TryGetOwn(key, out JsValue v))
            {
                return v;
            }
        }

        return default;
    }

    internal virtual bool TryGetOwn(string key, out JsValue value)
    {
        int slot = Shape.Lookup(key);
        if (slot >= 0)
        {
            value = Slots[slot];
            return true;
        }

        value = default;
        return false;
    }

    internal virtual void Set(string key, JsValue value)
    {
        int slot = Shape.Lookup(key);
        if (slot >= 0)
        {
            Slots[slot] = value;
            return;
        }

        AddProperty(key, value);
    }

    internal void AddProperty(string key, JsValue value)
    {
        Shape next = Shape.With(key);
        int slot = next.Count - 1;
        if (slot >= Slots.Length)
        {
            Array.Resize(ref Slots, Math.Max(4, Slots.Length * 2));
        }

        Slots[slot] = value;
        Shape = next;
    }

    internal virtual IEnumerable<string> OwnKeys() => Shape.Keys();
}

/// <summary>An array with a dense element store; holes are not modelled in the spike.</summary>
internal sealed class JsArray : JsObject
{
    internal JsValue[] Items;
    internal int Length;

    internal JsArray(JsObject? proto, int capacity = 0)
        : base(proto)
    {
        Items = capacity == 0 ? [] : new JsValue[capacity];
    }

    internal void Push(JsValue v)
    {
        if (Length == Items.Length)
        {
            Array.Resize(ref Items, Math.Max(4, Items.Length * 2));
        }

        Items[Length++] = v;
    }

    internal void SetLength(int n)
    {
        if (n > Items.Length)
        {
            Array.Resize(ref Items, n);
        }
        else if (n < Length)
        {
            Array.Clear(Items, n, Length - n);
        }

        Length = n;
    }

    internal override bool TryGetOwn(string key, out JsValue value)
    {
        if (key == "length")
        {
            value = JsValue.FromNumber(Length);
            return true;
        }

        return base.TryGetOwn(key, out value);
    }

    internal override void Set(string key, JsValue value)
    {
        if (key == "length")
        {
            SetLength((int)Conv.ToNumber(value));
            return;
        }

        base.Set(key, value);
    }

    internal override IEnumerable<string> OwnKeys()
    {
        for (int i = 0; i < Length; i++)
        {
            yield return i.ToString(CultureInfo.InvariantCulture);
        }

        foreach (string k in base.OwnKeys())
        {
            yield return k;
        }
    }
}

internal delegate JsValue NativeFn(JsValue thisValue, ReadOnlySpan<JsValue> args);

/// <summary>Base of everything callable. The executors derive their own script-function classes.</summary>
internal abstract class JsFunction : JsObject
{
    internal JsFunction(JsObject? proto, string name)
        : base(proto)
    {
        Name = name;
    }

    internal string Name { get; }

    internal virtual bool IsConstructor => false;

    internal JsObject? PrototypeProperty { get; private set; }

    /// <summary>Creates the <c>prototype</c> property on first use, as an ordinary property afterwards.</summary>
    internal void EnsurePrototype(Realm realm)
    {
        if (PrototypeProperty is null && IsConstructor)
        {
            var p = new JsObject(realm.ObjectProto);
            PrototypeProperty = p;
            AddProperty("prototype", JsValue.FromObject(p));
        }
    }
}

internal sealed class NativeFunction : JsFunction
{
    internal NativeFunction(Realm realm, string name, NativeFn fn)
        : base(realm.FunctionProto, name)
    {
        Fn = fn;
    }

    internal NativeFn Fn { get; }
}

/// <summary>A heap environment record: the captured variables of one scope.</summary>
internal sealed class Env
{
    internal readonly Env? Parent;
    internal readonly JsValue[] Slots;

    internal Env(Env? parent, int size)
    {
        Parent = parent;
        Slots = new JsValue[size];
    }
}

/// <summary>A JavaScript exception in flight.</summary>
internal sealed class JsThrow : Exception
{
    internal JsThrow(JsValue value)
        : base("JavaScript exception")
    {
        Value = value;
    }

    internal JsValue Value { get; }

    public override string Message => "Uncaught " + Conv.ToStr(Value);
}

/// <summary>A resource limit was hit (fuel or call depth).</summary>
public sealed class SpikeLimitException : Exception
{
    public SpikeLimitException(string message)
        : base(message)
    {
    }
}

/// <summary>A construct outside the spike's subset.</summary>
public sealed class SpikeUnsupportedException : Exception
{
    public SpikeUnsupportedException(string message)
        : base(message)
    {
    }
}

internal static class Conv
{
    internal static bool ToBoolean(JsValue v)
    {
        object? r = v.Ref;
        if (r is null || ReferenceEquals(r, JsValue.NullTag) || ReferenceEquals(r, JsValue.FalseTag))
        {
            return false;
        }

        if (ReferenceEquals(r, JsValue.NumberTag))
        {
            return v.Num != 0 && !double.IsNaN(v.Num);
        }

        if (r is string s)
        {
            return s.Length != 0;
        }

        if (r is Rope rope)
        {
            return rope.Length != 0;
        }

        return true;
    }

    internal static double ToNumber(JsValue v)
    {
        object? r = v.Ref;
        if (ReferenceEquals(r, JsValue.NumberTag))
        {
            return v.Num;
        }

        if (r is null)
        {
            return double.NaN;
        }

        if (ReferenceEquals(r, JsValue.NullTag) || ReferenceEquals(r, JsValue.FalseTag))
        {
            return 0;
        }

        if (ReferenceEquals(r, JsValue.TrueTag))
        {
            return 1;
        }

        if (r is string or Rope)
        {
            string s = (r is string str ? str : ((Rope)r).Flatten()).Trim();
            if (s.Length == 0)
            {
                return 0;
            }

            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;
        }

        return double.NaN;
    }

    internal static string ToStr(JsValue v)
    {
        object? r = v.Ref;
        if (r is string s)
        {
            return s;
        }

        if (r is null)
        {
            return "undefined";
        }

        if (ReferenceEquals(r, JsValue.NumberTag))
        {
            return NumberToString(v.Num);
        }

        if (ReferenceEquals(r, JsValue.NullTag))
        {
            return "null";
        }

        if (ReferenceEquals(r, JsValue.TrueTag))
        {
            return "true";
        }

        if (ReferenceEquals(r, JsValue.FalseTag))
        {
            return "false";
        }

        if (r is Rope rope)
        {
            return rope.Flatten();
        }

        if (r is JsArray a)
        {
            var parts = new string[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                parts[i] = a.Items[i].IsNullish ? string.Empty : ToStr(a.Items[i]);
            }

            return string.Join(",", parts);
        }

        return r is JsFunction ? "function" : "[object Object]";
    }

    internal static string NumberToString(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "Infinity" : "-Infinity";
        }

        if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
        {
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        }

        // The spike approximates Number::toString; the real engine follows the shortest round-trip rules.
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>ECMA-262 7.1.6 ToInt32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ToInt32(double d)
    {
        int i = (int)d;
        if (i == d)
        {
            return i;
        }

        return ToInt32Slow(d);
    }

    private static int ToInt32Slow(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            return 0;
        }

        double t = Math.Truncate(d);
        double m = t % 4294967296.0;
        if (m < 0)
        {
            m += 4294967296.0;
        }

        return unchecked((int)(uint)m);
    }

    internal static string TypeOf(JsValue v)
    {
        object? r = v.Ref;
        if (r is null)
        {
            return "undefined";
        }

        if (ReferenceEquals(r, JsValue.NumberTag))
        {
            return "number";
        }

        if (ReferenceEquals(r, JsValue.NullTag))
        {
            return "object";
        }

        if (v.IsBool)
        {
            return "boolean";
        }

        if (r is string or Rope)
        {
            return "string";
        }

        return r is JsFunction ? "function" : "object";
    }
}
