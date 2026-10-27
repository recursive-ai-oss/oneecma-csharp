using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneEcma;

/// <summary>Marker objects that stand for the non-reference value kinds inside <see cref="JsValue"/>.</summary>
internal sealed class Tag
{
    internal Tag(string name) => Name = name;

    internal string Name { get; }
}

/// <summary>A Symbol value (ECMA-262 6.1.5.1). Identity is the reference.</summary>
public sealed class JsSymbol
{
    internal JsSymbol(string? description) => Description = description;

    /// <summary>The description, or null when the symbol was created without one.</summary>
    public string? Description { get; }

    /// <summary>The key a global registry (<c>Symbol.for</c>) used, or null.</summary>
    internal string? RegistryKey { get; set; }

    /// <inheritdoc/>
    public override string ToString() => "Symbol(" + Description + ")";
}

/// <summary>
/// An ECMAScript value: a 16-byte struct holding one reference and one double. A number is
/// <c>(NumberTag, d)</c>, so arithmetic never allocates. <c>default</c> is <c>undefined</c>, which makes
/// zeroed stacks and slot arrays correct. Kinds: undefined (null reference), null, boolean, number,
/// string (<see cref="string"/> or a lazily flattened <see cref="Rope"/>), symbol, bigint, object.
/// </summary>
public readonly partial struct JsValue : IEquatable<JsValue>
{
    internal static readonly Tag NumberTag = new("number");
    internal static readonly Tag NullTag = new("null");
    internal static readonly Tag TrueTag = new("true");
    internal static readonly Tag FalseTag = new("false");
    internal static readonly Tag TdzTag = new("tdz");
    internal static readonly Tag HoleTag = new("hole");

    internal readonly object? Ref;
    internal readonly double Num;

    internal JsValue(object? r, double d)
    {
        Ref = r;
        Num = d;
    }

    /// <summary>The undefined value.</summary>
    public static JsValue Undefined => default;

    /// <summary>The null value.</summary>
    public static JsValue Null => new(NullTag, 0);

    /// <summary>The value true.</summary>
    public static JsValue True => new(TrueTag, 0);

    /// <summary>The value false.</summary>
    public static JsValue False => new(FalseTag, 0);

    internal static JsValue Tdz => new(TdzTag, 0);

    internal static JsValue Hole => new(HoleTag, 0);

    /// <summary>True for undefined.</summary>
    public bool IsUndefined => Ref is null;

    /// <summary>True for null.</summary>
    public bool IsNull => ReferenceEquals(Ref, NullTag);

    /// <summary>True for undefined or null.</summary>
    public bool IsNullish => Ref is null || ReferenceEquals(Ref, NullTag);

    /// <summary>True for numbers.</summary>
    public bool IsNumber => ReferenceEquals(Ref, NumberTag);

    /// <summary>True for booleans.</summary>
    public bool IsBoolean => ReferenceEquals(Ref, TrueTag) || ReferenceEquals(Ref, FalseTag);

    /// <summary>True for strings.</summary>
    public bool IsString => Ref is string or Rope;

    /// <summary>True for symbols.</summary>
    public bool IsSymbol => Ref is JsSymbol;

    /// <summary>True for bigints.</summary>
    public bool IsBigInt => Ref is BigInteger;

    /// <summary>True for objects (including functions).</summary>
    public bool IsObject => Ref is JsObject;

    internal bool IsTdz => ReferenceEquals(Ref, TdzTag);

    internal bool IsHole => ReferenceEquals(Ref, HoleTag);

    internal bool IsTrue => ReferenceEquals(Ref, TrueTag);

    /// <summary>The object, or null when this is not an object.</summary>
    public JsObject? AsObject => Ref as JsObject;

    /// <summary>The number value. Only meaningful when <see cref="IsNumber"/>.</summary>
    public double AsNumber => Num;

    /// <summary>Creates a number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JsValue FromNumber(double d) => new(NumberTag, d);

    /// <summary>Creates a boolean.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JsValue FromBoolean(bool b) => new(b ? TrueTag : FalseTag, 0);

    /// <summary>Creates a string value.</summary>
    public static JsValue FromString(string s) => new(s, 0);

    /// <summary>Creates a bigint value.</summary>
    public static JsValue FromBigInt(BigInteger b) => new(b, 0);

    /// <summary>Wraps an object.</summary>
    public static JsValue FromObject(JsObject o) => new(o, 0);

    /// <summary>Wraps a symbol (create one with <see cref="JsRealm.NewSymbol"/>).</summary>
    public static JsValue FromSymbol(JsSymbol s) => new(s, 0);

    internal static JsValue FromRope(Rope r) => new(r, 0);

    /// <summary>The string content. Only meaningful when <see cref="IsString"/>; any other value throws <see cref="InvalidCastException"/>.</summary>
    public string AsString => Ref as string ?? ((Rope)Ref!).Flatten();

    /// <summary>The ECMAScript <c>typeof</c> result.</summary>
    public string TypeOf => Ref switch
    {
        null => "undefined",
        string or Rope => "string",
        JsObject o => o.TypeOfName,
        JsSymbol => "symbol",
        BigInteger => "bigint",
        Tag t => ReferenceEquals(t, NumberTag) ? "number" : ReferenceEquals(t, NullTag) ? "object" : "boolean",
        _ => "undefined",
    };

    /// <summary>A debugging rendering; for the language-level conversion use the abstract operations.</summary>
    public override string ToString() => Ops.DebugString(this);

    /// <inheritdoc/>
    public bool Equals(JsValue other) => Ops.SameValue(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JsValue v && Equals(v);

    /// <inheritdoc/>
    public override int GetHashCode() => Ref is Tag ? Num.GetHashCode() : Ref is string s ? s.GetHashCode(StringComparison.Ordinal) : Ref?.GetHashCode() ?? 0;

    /// <summary>SameValue.</summary>
    public static bool operator ==(JsValue a, JsValue b) => a.Equals(b);

    /// <summary>Negated SameValue.</summary>
    public static bool operator !=(JsValue a, JsValue b) => !a.Equals(b);
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

        Agent.CurrentOrNull?.Alloc((long)Length * 2);
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

/// <summary>A property key: a string or a symbol (ECMA-262 6.1.7).</summary>
internal readonly struct PropertyKey : IEquatable<PropertyKey>
{
    internal readonly object Ref;

    internal PropertyKey(string s) => Ref = s;

    internal PropertyKey(JsSymbol s) => Ref = s;

    internal bool IsSymbol => Ref is JsSymbol;

    internal string? String => Ref as string;

    internal JsSymbol? Symbol => Ref as JsSymbol;

    public static implicit operator PropertyKey(string s) => new(s);

    public bool Equals(PropertyKey other) => ReferenceEquals(Ref, other.Ref) || (Ref is string a && other.Ref is string b && a == b);

    public override bool Equals(object? obj) => obj is PropertyKey k && Equals(k);

    public override int GetHashCode() => Ref is string s ? s.GetHashCode(StringComparison.Ordinal) : RuntimeHelpers.GetHashCode(Ref);

    internal JsValue ToValue() => Ref is string s ? JsValue.FromString(s) : JsValue.FromSymbol((JsSymbol)Ref);

    /// <summary>The array index (ECMA-262 6.1.7) this key denotes, if any.</summary>
    internal bool TryGetIndex(out uint index)
    {
        if (Ref is string s)
        {
            return TryParseIndex(s, out index);
        }

        index = 0;
        return false;
    }

    internal static bool TryParseIndex(string s, out uint index)
    {
        index = 0;
        int n = s.Length;
        if (n == 0 || n > 10 || s[0] > '9' || s[0] < '0' || (s[0] == '0' && n > 1))
        {
            return false;
        }

        ulong v = 0;
        for (int i = 0; i < n; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
            {
                return false;
            }

            v = (v * 10) + (ulong)(c - '0');
        }

        if (v >= uint.MaxValue)
        {
            return false;
        }

        index = (uint)v;
        return true;
    }

    public override string ToString() => Ref is string s ? s : Ref.ToString()!;
}

internal sealed class PropertyKeyComparer : IEqualityComparer<PropertyKey>
{
    internal static readonly PropertyKeyComparer Instance = new();

    public bool Equals(PropertyKey x, PropertyKey y) => x.Equals(y);

    public int GetHashCode(PropertyKey k) => k.GetHashCode();
}
