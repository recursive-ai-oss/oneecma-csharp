using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>
/// Tracks one conversion of a script value to a .NET value and, on failure, builds the precise TypeError: the .NET member, the argument
/// path (<c>order.lineItems[2].price</c>), what was expected and what was found. Generated binding code uses it; it has no use outside that.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct JsConv
{
    private readonly JsRealm _realm;
    private readonly string _member;
    private readonly string _signature;
    private string _root;
    private bool _isResult;
    private List<string>? _segments;
    private string? _expected;
    private JsValue _actual;
    private string? _unknownProperty;
    private IReadOnlyList<string>? _known;
    private bool _missing;
    private string? _hint;

    /// <summary>Starts a conversion context for one host call.</summary>
    /// <param name="realm">The realm the call runs in.</param>
    /// <param name="member">The .NET member, such as <c>Shop.OrderApi.AddOrder</c>.</param>
    /// <param name="signature">The member with its parameters, such as <c>Shop.OrderApi.AddOrder(Order order)</c>.</param>
    public JsConv(JsRealm realm, string member, string signature)
    {
        _realm = realm;
        _member = member;
        _signature = signature;
        _root = string.Empty;
    }

    /// <summary>The realm the call runs in.</summary>
    public readonly JsRealm Realm => _realm;

    /// <summary>The .NET member being called.</summary>
    public readonly string Member => _member;

    /// <summary>The member with its parameters.</summary>
    public readonly string Signature => _signature;

    /// <summary>Begins converting the argument named <paramref name="name"/>.</summary>
    public void Arg(string name)
    {
        _root = name;
        _isResult = false;
        _segments = null;
    }

    /// <summary>Begins converting the value a callback returned to .NET.</summary>
    public void Result(string callbackName)
    {
        _root = callbackName;
        _isResult = true;
        _segments = null;
    }

    /// <summary>Records that the value is not what the .NET type needs. Always returns false.</summary>
    /// <param name="expected">What was expected, as in <c>a number (System.Decimal)</c>.</param>
    /// <param name="actual">The script value found.</param>
    /// <param name="hint">An extra suggestion for the help line, or null.</param>
    public bool Fail(string expected, JsValue actual, string? hint = null)
    {
        _hint = hint;
        _expected = expected;
        _actual = actual;
        _missing = false;
        return false;
    }

    /// <summary>Records that a required value is absent. Always returns false.</summary>
    public bool Missing(string expected)
    {
        _expected = expected;
        _actual = default;
        _missing = true;
        return false;
    }

    /// <summary>Records an unknown property of an object that rejects them. Always returns false.</summary>
    public bool Unknown(string property, IReadOnlyList<string> known)
    {
        _unknownProperty = property;
        _known = known;
        return false;
    }

    /// <summary>Notes that the failure is inside property <paramref name="name"/> of the value being converted. Always returns false.</summary>
    public bool InProperty(string name)
    {
        (_segments ??= []).Add(name);
        return false;
    }

    /// <summary>Notes that the failure is inside element <paramref name="index"/>. Always returns false.</summary>
    public bool InIndex(int index)
    {
        (_segments ??= []).Add("[" + index.ToString(CultureInfo.InvariantCulture) + "]");
        return false;
    }

    /// <summary>The path to the failing value, such as <c>order.lineItems[2].price</c>.</summary>
    public readonly string Path
    {
        get
        {
            var sb = new StringBuilder(_root);
            if (_segments is not null)
            {
                for (int i = _segments.Count - 1; i >= 0; i--)
                {
                    if (!_segments[i].StartsWith('['))
                    {
                        sb.Append('.');
                    }

                    sb.Append(_segments[i]);
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>Builds the TypeError for the recorded failure. Throw the result.</summary>
    public readonly JsException ToException()
    {
        string path = Path;
        string what = _isResult
            ? "the value returned by callback '" + _root + "'" + (_segments is { Count: > 0 } ? " (at " + path.Replace(_root, "result", StringComparison.Ordinal) + ")" : string.Empty)
            : "argument '" + path + "'";
        string message;
        string label;
        var help = new List<string>();
        if (_unknownProperty is not null)
        {
            message = _member + ": " + what + " has no property '" + _unknownProperty + "'";
            label = "unknown property '" + _unknownProperty + "' in `" + path + "`";
            string? near = JsMarshal.Nearest(_unknownProperty, _known!);
            if (near is not null)
            {
                help.Add("did you mean '" + near + "'?");
            }

            if (_known!.Count > 0)
            {
                help.Add("it accepts: " + string.Join(", ", _known));
            }
        }
        else if (_missing)
        {
            message = _member + ": " + what + " is missing; expected " + _expected;
            label = "`" + path + "` is missing";
            help.Add("pass " + _expected + (_segments is null ? " for '" + _root + "'" : " as '" + path + "'"));
        }
        else
        {
            string actual = JsMarshal.Describe(_actual);
            message = _member + ": " + what + " must be " + _expected + ", got " + actual;
            label = (_isResult ? "callback '" + _root + "' returned " : "`" + path + "` is ") + actual + ", expected " + _expected;
            string? fix = JsMarshal.HelpFor(_expected!, _actual);
            if (_hint is not null)
            {
                help.Add(_hint);
            }

            if (fix is not null)
            {
                help.Add(fix);
            }
        }

        JsException ex = Errors.Make("TypeError", message);
        var info = new HostErrorInfo { PrimaryLabel = label };
        info.Help.AddRange(help);
        info.Notes.Add("host function: " + _signature);
        info.HostFrames.Add(new StackFrameInfo(_member, new Text.SourceText(string.Empty, _member), new Text.Span(0, 0)) { IsHost = true });
        HostErrors.Attach((JsObject)ex.Value.Ref!, info);
        return ex;
    }
}

/// <summary>Converts a conversion callback result: tries to turn a script value into a .NET value, recording the failure in <paramref name="conv"/>.</summary>
/// <typeparam name="T">The .NET type.</typeparam>
/// <param name="value">The script value.</param>
/// <param name="conv">The conversion context.</param>
/// <param name="result">The converted value when the call returns true.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public delegate bool JsTryConvert<T>(JsValue value, ref JsConv conv, out T result);

/// <summary>
/// Conversions between script values and .NET values for generated host bindings. Conversions are strict: a number is not accepted where
/// a string is expected, so a wrong argument is an error that says so instead of a silent coercion. The members are public so generated
/// code in other assemblies can call them; they are not meant to be called by hand.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class JsMarshal
{
    private const double MaxSafe = 9007199254740991d;

    // ---- describing values ---------------------------------------------------------------------

    /// <summary>A short description of a value for messages.</summary>
    public static string Describe(JsValue v)
    {
        if (v.IsUndefined)
        {
            return "undefined";
        }

        if (v.IsNull)
        {
            return "null";
        }

        if (v.IsNumber)
        {
            return "number " + Ops.NumberToStr(v.AsNumber);
        }

        if (v.IsString)
        {
            string s = v.AsString;
            return "string \"" + (s.Length > 40 ? s[..40] + "…" : s) + "\"";
        }

        if (v.IsBoolean)
        {
            return "boolean " + (v.AsBoolean ? "true" : "false");
        }

        if (v.IsBigInt)
        {
            return "bigint " + v.AsBigInt.ToString(CultureInfo.InvariantCulture) + "n";
        }

        if (v.IsSymbol)
        {
            return "a symbol";
        }

        if (v.Ref is JsHostObject h)
        {
            return "a " + h.Target.GetType().Name + " object";
        }

        if (v.Ref is JsObject o)
        {
            if (o.IsCallable)
            {
                return "a function";
            }

            if (o is JsArray arr)
            {
                return "an array of " + arr.Length.ToString(CultureInfo.InvariantCulture) + (arr.Length == 1 ? " item" : " items");
            }

            var keys = new List<PropertyKey>();
            o.OwnKeys(keys);
            var names = new List<string>();
            foreach (PropertyKey k in keys)
            {
                if (!k.IsSymbol && o.GetOwnProperty(k, out PropertyDescriptor d) && d.Enumerable)
                {
                    names.Add(k.ToString());
                }
            }

            if (names.Count == 0)
            {
                return "an empty object";
            }

            return "an object with " + (names.Count == 1 ? "key " : "keys ") + string.Join(", ", names.Take(5)) + (names.Count > 5 ? ", …" : string.Empty);
        }

        return "an unknown value";
    }

    /// <summary>A one-line suggestion for converting <paramref name="actual"/> into what was <paramref name="expected"/>, or null.</summary>
    public static string? HelpFor(string expected, JsValue actual)
    {
        bool wantNumber = expected.StartsWith("a number", StringComparison.Ordinal) || expected.StartsWith("a finite number", StringComparison.Ordinal) || expected.StartsWith("an integer", StringComparison.Ordinal);
        if (wantNumber && actual.IsString)
        {
            return "convert the string first: Number(" + Quote(actual.AsString) + ")";
        }

        if (expected.StartsWith("a string", StringComparison.Ordinal) && (actual.IsNumber || actual.IsBoolean))
        {
            return "convert it first: String(value)";
        }

        if (expected.StartsWith("an array", StringComparison.Ordinal) && actual.Ref is JsObject { IsCallable: false } && actual.Ref is not JsArray)
        {
            return "pass an array, for example [value], or Object.values(obj) for the values of an object";
        }

        if (expected.StartsWith("an integer", StringComparison.Ordinal) && actual.IsNumber)
        {
            return "round it first: Math.round(value)";
        }

        if (expected.StartsWith("a boolean", StringComparison.Ordinal) && !actual.IsNullish)
        {
            return "pass true or false; Boolean(value) converts";
        }

        if (expected.StartsWith("a function", StringComparison.Ordinal))
        {
            return "pass a function, for example (x) => x";
        }

        return null;
    }

    /// <summary>"a Name" or "an Name".</summary>
    public static string An(string noun) => (noun.Length > 0 && "AEIOUaeiou".Contains(noun[0], StringComparison.Ordinal) ? "an " : "a ") + noun;

    private static string Quote(string s) => "\"" + (s.Length > 20 ? s[..20] + "…" : s) + "\"";

    /// <summary>The candidate closest to <paramref name="name"/> by edit distance when it is close enough to be a typo; else null.</summary>
    public static string? Nearest(string name, IReadOnlyList<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string c in candidates)
        {
            int d = Distance(name, c);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }

        return best is not null && bestDistance <= Math.Max(1, Math.Min(name.Length, best.Length) / 3) ? best : null;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    // ---- script -> .NET -------------------------------------------------------------------------------

    /// <summary>Converts to a boolean; only <c>true</c> and <c>false</c> are accepted.</summary>
    public static bool TryBool(JsValue v, ref JsConv c, out bool result)
    {
        result = v.IsBoolean && v.AsBoolean;
        return v.IsBoolean || c.Fail("a boolean", v);
    }

    /// <summary>Converts to a string; only strings are accepted.</summary>
    public static bool TryString(JsValue v, ref JsConv c, out string result)
    {
        result = v.IsString ? v.AsString : string.Empty;
        return v.IsString || c.Fail("a string", v);
    }

    /// <summary>Converts to a single UTF-16 code unit from a one-character string.</summary>
    public static bool TryChar(JsValue v, ref JsConv c, out char result)
    {
        result = default;
        if (!v.IsString)
        {
            return c.Fail("a one-character string (System.Char)", v);
        }

        string s = v.AsString;
        if (s.Length != 1)
        {
            return c.Fail("a one-character string (System.Char)", v);
        }

        result = s[0];
        return true;
    }

    /// <summary>Converts to a double; only numbers are accepted (NaN and the infinities included).</summary>
    public static bool TryDouble(JsValue v, ref JsConv c, string clr, out double result)
    {
        result = v.IsNumber ? v.AsNumber : 0;
        return v.IsNumber || c.Fail("a number (" + clr + ")", v);
    }

    /// <summary>Converts to a signed integer within <paramref name="min"/>..<paramref name="max"/>; a BigInt is accepted when it fits.</summary>
    public static bool TryInteger(JsValue v, ref JsConv c, long min, long max, string clr, out long result)
    {
        result = 0;
        if (v.IsNumber)
        {
            double d = v.AsNumber;
            if (d == Math.Floor(d) && d >= min && d <= max && Math.Abs(d) <= MaxSafe)
            {
                result = (long)d;
                return true;
            }

            return c.Fail(d == Math.Floor(d) && !double.IsInfinity(d) ? "an integer in the range of " + clr + " (" + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture) + ")" : "an integer (" + clr + ")", v);
        }

        if (v.IsBigInt && v.AsBigInt >= min && v.AsBigInt <= max)
        {
            result = (long)v.AsBigInt;
            return true;
        }

        return c.Fail("an integer (" + clr + ")", v);
    }

    /// <summary>Converts to an unsigned integer up to <paramref name="max"/>; a BigInt is accepted when it fits.</summary>
    public static bool TryUnsigned(JsValue v, ref JsConv c, ulong max, string clr, out ulong result)
    {
        result = 0;
        if (v.IsNumber)
        {
            double d = v.AsNumber;
            if (d == Math.Floor(d) && d >= 0 && d <= MaxSafe && d <= max)
            {
                result = (ulong)d;
                return true;
            }

            return c.Fail(d == Math.Floor(d) && !double.IsInfinity(d) ? "a non-negative integer in the range of " + clr + " (0 to " + max.ToString(CultureInfo.InvariantCulture) + ")" : "a non-negative integer (" + clr + ")", v);
        }

        if (v.IsBigInt && v.AsBigInt >= 0 && v.AsBigInt <= max)
        {
            result = (ulong)v.AsBigInt;
            return true;
        }

        return c.Fail("a non-negative integer (" + clr + ")", v);
    }

    /// <summary>Converts to a BigInteger; only BigInts are accepted.</summary>
    public static bool TryBigInt(JsValue v, ref JsConv c, out BigInteger result)
    {
        result = v.IsBigInt ? v.AsBigInt : default;
        return v.IsBigInt || c.Fail("a BigInt (System.Numerics.BigInteger)", v);
    }

    /// <summary>Converts to a decimal from a finite number.</summary>
    public static bool TryDecimal(JsValue v, ref JsConv c, out decimal result)
    {
        result = 0;
        if (!v.IsNumber || double.IsNaN(v.AsNumber) || Math.Abs(v.AsNumber) > 7.9e28)
        {
            return c.Fail("a finite number (System.Decimal)", v);
        }

        result = (decimal)v.AsNumber;
        return true;
    }

    /// <summary>Converts to an enum member: the name (case-insensitive) or the underlying number.</summary>
    public static bool TryEnum(JsValue v, ref JsConv c, string[] names, long[] values, string clr, out long result)
    {
        result = 0;
        if (v.IsString)
        {
            string s = v.AsString;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], s, StringComparison.OrdinalIgnoreCase))
                {
                    result = values[i];
                    return true;
                }
            }
        }
        else if (v.IsNumber)
        {
            foreach (long val in values)
            {
                if (v.AsNumber == val)
                {
                    result = val;
                    return true;
                }
            }
        }

        string list = string.Join(", ", names.Select(n => "'" + n + "'"));
        if (v.IsString)
        {
            string? near = Nearest(v.AsString, names);
            string expected = "one of " + list + " (" + clr + ")";
            return c.Fail(expected, v, near is null ? null : "did you mean '" + near + "'?");
        }

        return c.Fail("one of " + list + " (" + clr + ")", v);
    }

    /// <summary>Converts a script function to a callable object.</summary>
    public static bool TryFunction(JsValue v, ref JsConv c, out JsObject result)
    {
        if (v.Ref is JsObject { IsCallable: true } f)
        {
            result = f;
            return true;
        }

        result = null!;
        return c.Fail("a function", v);
    }

    /// <summary>Converts to any object.</summary>
    public static bool TryObject(JsValue v, ref JsConv c, out JsObject result)
    {
        if (v.Ref is JsObject o)
        {
            result = o;
            return true;
        }

        result = null!;
        return c.Fail("an object", v);
    }

    /// <summary>Builds the TypeError for a call that matches none of a member's overloads.</summary>
    public static JsException NoOverload(JsRealm realm, string member, ReadOnlySpan<JsValue> args, string[] candidates)
    {
        _ = realm;
        var kinds = new List<string>();
        foreach (JsValue v in args)
        {
            kinds.Add(KindName(v));
        }

        string got = "(" + string.Join(", ", kinds) + ")";
        JsException ex = Errors.Make("TypeError", member + ": no overload accepts " + got);
        var info = new HostErrorInfo { PrimaryLabel = "arguments " + got + " match no overload" };
        foreach (string c in candidates)
        {
            info.Notes.Add("overload: " + c);
        }

        info.HostFrames.Add(new StackFrameInfo(member, new Text.SourceText(string.Empty, member), new Text.Span(0, 0)) { IsHost = true });
        HostErrors.Attach((JsObject)ex.Value.Ref!, info);
        return ex;
    }

    private static string KindName(JsValue v)
    {
        if (v.IsUndefined)
        {
            return "undefined";
        }

        if (v.IsNull)
        {
            return "null";
        }

        if (v.IsNumber)
        {
            return "number";
        }

        if (v.IsString)
        {
            return "string";
        }

        if (v.IsBoolean)
        {
            return "boolean";
        }

        if (v.IsBigInt)
        {
            return "bigint";
        }

        if (v.IsSymbol)
        {
            return "symbol";
        }

        return v.Ref is JsArray ? "array" : v.Ref is JsObject { IsCallable: true } ? "function" : "object";
    }

    /// <summary>Converts a script array to a list, converting each element.</summary>
    public static bool TryList<T>(JsValue v, ref JsConv c, JsTryConvert<T> element, string expected, out List<T> result)
    {
        result = [];
        if (v.Ref is not JsArray arr)
        {
            return c.Fail(expected, v);
        }

        uint n = arr.Length;
        result.Capacity = (int)Math.Min(n, 1 << 20);
        for (uint i = 0; i < n; i++)
        {
            JsValue item = arr.Get(i.ToString(CultureInfo.InvariantCulture));
            if (!element(item, ref c, out T? r))
            {
                return c.InIndex((int)i);
            }

            result.Add(r);
        }

        return true;
    }

    /// <summary>Converts a plain script object to a string-keyed dictionary, converting each value.</summary>
    public static bool TryDictionary<T>(JsValue v, ref JsConv c, JsTryConvert<T> element, string expected, out Dictionary<string, T> result)
    {
        result = new Dictionary<string, T>(StringComparer.Ordinal);
        if (v.Ref is not JsObject o || o.IsCallable || o is JsArray)
        {
            return c.Fail(expected, v);
        }

        var keys = new List<PropertyKey>();
        o.OwnKeys(keys);
        foreach (PropertyKey k in keys)
        {
            if (k.IsSymbol || !o.GetOwnProperty(k, out PropertyDescriptor d) || !d.Enumerable)
            {
                continue;
            }

            if (!element(o.Get(k), ref c, out T? r))
            {
                return c.InProperty(k.ToString());
            }

            result[k.ToString()] = r;
        }

        return true;
    }

    /// <summary>Reads a property of an object being converted to a .NET value type. Returns false (with the failure recorded) only when the property is required and absent.</summary>
    public static bool TryMember(JsObject o, string name, bool required, string expected, ref JsConv c, out JsValue value)
    {
        value = o.Get(name);
        if (value.IsUndefined && required)
        {
            return c.InProperty(name) | c.Missing(expected);
        }

        return true;
    }

    /// <summary>Checks that an object being converted to a .NET value type has no property outside <paramref name="known"/>.</summary>
    public static bool CheckKnown(JsObject o, IReadOnlyList<string> known, ref JsConv c)
    {
        var keys = new List<PropertyKey>();
        o.OwnKeys(keys);
        foreach (PropertyKey k in keys)
        {
            if (k.IsSymbol || !o.GetOwnProperty(k, out PropertyDescriptor d) || !d.Enumerable)
            {
                continue;
            }

            string name = k.ToString();
            bool ok = false;
            foreach (string kn in known)
            {
                if (kn == name)
                {
                    ok = true;
                    break;
                }
            }

            if (!ok)
            {
                return c.Unknown(name, known);
            }
        }

        return true;
    }

    /// <summary>Whether a value can be a .NET value type: a plain object (not an array, function or host object).</summary>
    public static bool IsPlainObject(JsValue v) => v.Ref is JsObject o && !o.IsCallable && o is not JsArray && o is not JsHostObject;

    /// <summary>Converts to the target of a host object.</summary>
    public static bool TryHost<T>(JsValue v, ref JsConv c, string expected, out T result)
    {
        if (v.Ref is JsHostObject h && h.Target is T t)
        {
            result = t;
            return true;
        }

        result = default!;
        return c.Fail(expected, v);
    }

    /// <summary>Reads the <c>this</c> of an instance member. Throws a TypeError when it is not a host object of the right type.</summary>
    public static T This<T>(JsValue thisValue, string member)
    {
        if (thisValue.Ref is JsHostObject h && h.Target is T t)
        {
            return t;
        }

        JsException ex = Errors.Make("TypeError", member + ": called on an incompatible receiver (this is " + Describe(thisValue) + "); expected " + An(typeof(T).Name) + " object");
        var info = new HostErrorInfo { PrimaryLabel = "`this` is " + Describe(thisValue) + " here, expected " + An(typeof(T).Name) + " object" };
        info.Help.Add("call the method on the object, as in obj.method(...), or bind it first: obj.method.bind(obj)");
        info.Notes.Add("host function: " + member);
        info.HostFrames.Add(new StackFrameInfo(member, new Text.SourceText(string.Empty, member), new Text.Span(0, 0)) { IsHost = true });
        HostErrors.Attach((JsObject)ex.Value.Ref!, info);
        throw ex;
    }

    // ---- kind tests for overload resolution --------------------------------------------------------

    /// <summary>Whether the value is a number with an integral value.</summary>
    public static bool IsInteger(JsValue v) => v.IsNumber && v.AsNumber == Math.Floor(v.AsNumber) && !double.IsInfinity(v.AsNumber);

    /// <summary>Whether the value is a script array.</summary>
    public static bool IsArrayValue(JsValue v) => v.Ref is JsArray;

    /// <summary>Whether the value is a callable object.</summary>
    public static bool IsFunctionValue(JsValue v) => v.Ref is JsObject { IsCallable: true };

    /// <summary>Whether the value is a host object whose target is a <typeparamref name="T"/>.</summary>
    public static bool IsHost<T>(JsValue v) => v.Ref is JsHostObject h && h.Target is T;

    // ---- .NET -> script -----------------------------------------------------------------------------

    /// <summary>Converts a 64-bit integer: a number when it is exactly representable, else a BigInt.</summary>
    public static JsValue FromInt64(long v) => Math.Abs((double)v) <= MaxSafe ? JsValue.FromNumber(v) : JsValue.FromBigInt(v);

    /// <summary>Converts an unsigned 64-bit integer: a number when it is exactly representable, else a BigInt.</summary>
    public static JsValue FromUInt64(ulong v) => v <= MaxSafe ? JsValue.FromNumber(v) : JsValue.FromBigInt(v);

    /// <summary>Converts a string; null becomes <c>null</c>.</summary>
    public static JsValue FromString(string? s) => s is null ? JsValue.Null : JsValue.FromString(s);

    /// <summary>Converts a sequence to a script array using <paramref name="element"/> for each item.</summary>
    public static JsValue FromSequence<T>(JsRealm realm, IEnumerable<T>? items, Func<JsRealm, T, JsValue> element)
    {
        if (items is null)
        {
            return JsValue.Null;
        }

        var list = new List<JsValue>();
        foreach (T item in items)
        {
            list.Add(element(realm, item));
        }

        return JsValue.FromObject(realm.NewArray(list));
    }

    /// <summary>Converts a string-keyed dictionary to a plain script object (keys in enumeration order).</summary>
    public static JsValue FromDictionary<T>(JsRealm realm, IEnumerable<KeyValuePair<string, T>>? items, Func<JsRealm, T, JsValue> element)
    {
        if (items is null)
        {
            return JsValue.Null;
        }

        JsObject o = realm.NewObject();
        foreach (KeyValuePair<string, T> kv in items)
        {
            realm.DefineData(o, kv.Key, element(realm, kv.Value));
        }

        return JsValue.FromObject(o);
    }

    // ---- calling back into script ---------------------------------------------------------------------

    /// <summary>Calls a script callback from .NET. Must happen on the engine's thread.</summary>
    public static JsValue CallBack(JsRealm realm, JsObject function, params ReadOnlySpan<JsValue> args)
    {
        if (Agent.CurrentOrNull is null)
        {
            throw new InvalidOperationException("A script callback was called from a thread that is not running the engine. Call callbacks before the host method returns, or after an await inside an async host method, which resumes on the engine's thread.");
        }

        return realm.Call(JsValue.FromObject(function), default, args);
    }

    // ---- tasks and iterators ----------------------------------------------------------------------------

    /// <summary>Starts a task-returning host method and returns a promise for its result. A synchronous throw rejects the promise.</summary>
    public static JsValue FromTask<T>(JsRealm realm, string member, Func<Task<T>> start, Func<JsRealm, T, JsValue> toJs)
    {
        return Bridge(realm, member, start, (t, p) =>
        {
            T value = ((Task<T>)t).Result;
            p.Resolve(toJs(realm, value));
        });
    }

    /// <summary>Starts a task-returning host method with no result; the promise fulfils with undefined.</summary>
    public static JsValue FromTask(JsRealm realm, string member, Func<Task> start)
    {
        return Bridge(realm, member, start, (_, p) => p.Resolve(default));
    }

    private static JsValue Bridge(JsRealm realm, string member, Func<Task> start, Action<Task, JsPromise> succeed)
    {
        ScriptEngine engine = realm.Engine;
        JsPromise promise = realm.NewPromise();
        List<StackFrameInfo> callSite = Agent.Current.CaptureFrames(10);
        Task task;
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(engine.HostContext);
        try
        {
            task = start();
        }
        catch (Exception e) when (e is not (JsLimitException or OperationCanceledException))
        {
            promise.Reject(e is JsException je ? je.Value : HostErrors.FromException(e, member, callSite).Value);
            return promise.Value;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        void Settle()
        {
            if (task.IsCompletedSuccessfully)
            {
                try
                {
                    succeed(task, promise);
                }
                catch (JsException je)
                {
                    promise.Reject(je.Value);
                }
            }
            else
            {
                Exception e = task.Exception is { InnerExceptions.Count: > 0 } agg ? agg.InnerExceptions[0] : new TaskCanceledException(task);
                promise.Reject(e is JsException je ? je.Value : HostErrors.FromException(e, member, callSite).Value);
            }
        }

        if (task.IsCompleted)
        {
            Settle();
        }
        else
        {
            engine.BeginHostOperation();
            _ = task.ContinueWith(_ => engine.Post(() =>
            {
                engine.EndHostOperation();
                Settle();
            }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return promise.Value;
    }

    /// <summary>Wraps a sequence as a script iterator (an object with <c>next</c>, <c>return</c> and <c>[Symbol.iterator]</c>); items are produced as the script asks.</summary>
    public static JsValue FromEnumerable<T>(JsRealm realm, string member, IEnumerable<T>? items, Func<JsRealm, T, JsValue> element)
    {
        if (items is null)
        {
            return JsValue.Null;
        }

        IEnumerator<T> e = items.GetEnumerator();
        bool done = false;
        JsObject it = realm.NewObject(realm.Inner.IteratorPrototype);
        realm.DefineData(it, "next", JsValue.FromObject(realm.NewFunction("next", 0, (_, _) =>
        {
            if (!done && e.MoveNext())
            {
                return JsValue.FromObject(Ops.CreateIterResult(element(realm, e.Current), false));
            }

            if (!done)
            {
                done = true;
                e.Dispose();
            }

            return JsValue.FromObject(Ops.CreateIterResult(default, true));
        })), JsAttributes.Method);
        realm.DefineData(it, "return", JsValue.FromObject(realm.NewFunction("return", 0, (_, a) =>
        {
            if (!done)
            {
                done = true;
                e.Dispose();
            }

            return JsValue.FromObject(Ops.CreateIterResult(a.Length > 0 ? a[0] : default, true));
        })), JsAttributes.Method);
        _ = member;
        return JsValue.FromObject(it);
    }

    /// <summary>Wraps an async sequence as a script async iterator whose <c>next()</c> returns promises.</summary>
    public static JsValue FromAsyncEnumerable<T>(JsRealm realm, string member, IAsyncEnumerable<T>? items, Func<JsRealm, T, JsValue> element)
    {
        if (items is null)
        {
            return JsValue.Null;
        }

        IAsyncEnumerator<T> e = items.GetAsyncEnumerator();
        bool done = false;
        JsObject it = realm.NewObject(realm.Inner.AsyncIteratorPrototype);
        realm.DefineData(it, "next", JsValue.FromObject(realm.NewFunction("next", 0, (_, _) =>
            FromTask(realm, member, () => MoveNext(e), (_, moved) =>
            {
                if (moved)
                {
                    return JsValue.FromObject(Ops.CreateIterResult(element(realm, e.Current), false));
                }

                done = true;
                return JsValue.FromObject(Ops.CreateIterResult(default, true));
            }))), JsAttributes.Method);
        realm.DefineData(it, "return", JsValue.FromObject(realm.NewFunction("return", 0, (_, a) =>
        {
            JsValue arg = a.Length > 0 ? a[0] : default;
            if (done)
            {
                return FromTask(realm, member, () => Task.FromResult(0), (_, _) => JsValue.FromObject(Ops.CreateIterResult(arg, true)));
            }

            done = true;
            return FromTask(realm, member, async () =>
            {
                await e.DisposeAsync().ConfigureAwait(false);
                return 0;
            }, (_, _) => JsValue.FromObject(Ops.CreateIterResult(arg, true)));
        })), JsAttributes.Method);
        return JsValue.FromObject(it);
    }

    /// <summary>Turns the value a script callback returned into a task: a promise is followed, anything else completes at once.</summary>
    public static Task AwaitPromise(JsRealm realm, JsValue result)
    {
        JsPromise? p = realm.AsPromise(result);
        if (p is null)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.OnSettled((state, v) =>
        {
            if (state == JsPromiseState.Fulfilled)
            {
                tcs.TrySetResult();
            }
            else
            {
                tcs.TrySetException(new JsException(v));
            }
        });
        return tcs.Task;
    }

    /// <summary>Like <see cref="AwaitPromise(JsRealm, JsValue)"/>, converting the fulfilment value with <paramref name="convert"/>.</summary>
    public static Task<T> AwaitPromise<T>(JsRealm realm, JsValue result, JsConv conv, JsTryConvert<T> convert)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finish(JsValue v)
        {
            if (convert(v, ref conv, out T? r))
            {
                tcs.TrySetResult(r);
            }
            else
            {
                tcs.TrySetException(conv.ToException());
            }
        }

        JsPromise? p = realm.AsPromise(result);
        if (p is null)
        {
            Finish(result);
        }
        else
        {
            p.OnSettled((state, v) =>
            {
                if (state == JsPromiseState.Fulfilled)
                {
                    Finish(v);
                }
                else
                {
                    tcs.TrySetException(new JsException(v));
                }
            });
        }

        return tcs.Task;
    }

    private static async Task<bool> MoveNext<T>(IAsyncEnumerator<T> e) => await e.MoveNextAsync().ConfigureAwait(false);
}
