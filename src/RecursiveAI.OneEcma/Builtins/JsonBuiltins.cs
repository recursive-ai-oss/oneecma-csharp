using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>An object created by JSON.rawJSON (the [[IsRawJSON]] internal slot, ECMA-262 25.5.4).</summary>
internal sealed class JsRawJsonObject : JsObject
{
    internal JsRawJsonObject()
        : base((JsObject?)null)
    {
    }
}

/// <summary>
/// ParseJSON (ECMA-262 25.5.2.1) over a string, without recursion: arrays and objects nest on an explicit stack, so a 100000-deep
/// text parses like a flat one. Error messages follow V8's wording. When <c>record</c> is set it also builds the JSON Parse Record tree
/// (25.5.2.2) that JSON.parse's reviver uses to expose source text.
/// </summary>
internal sealed class JsonParser
{
    private readonly string _s;
    private readonly bool _record;
    private int _pos;

    internal JsonParser(string text, bool record)
    {
        _s = text;
        _record = record;
    }

    /// <summary>A JSON Parse Record: the value, the source span of a primitive, and the records of the children.</summary>
    internal sealed class ParseRecord
    {
        internal JsValue Value;
        internal int Start;
        internal int End;
        internal List<ParseRecord>? Elements;
        internal Dictionary<string, ParseRecord>? Entries;
    }

    private sealed class Frame
    {
        internal JsObject Container = null!;
        internal bool IsArray;
        internal string? Key;
        internal ParseRecord? Record;
        internal int Start;
    }

    internal string Source => _s;

    private static JsException Error(string message) => Errors.Syntax(message);

    private static JsException UnexpectedEnd() => Error("Unexpected end of JSON input");

    private JsException At(string what) => Error(what + (what.EndsWith("JSON", StringComparison.Ordinal) ? string.Empty : " in JSON") + " at position " + _pos.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " (line " + Line(_pos) + " column " + Column(_pos) + ")");

    private string Line(int pos)
    {
        int line = 1;
        for (int i = 0; i < pos && i < _s.Length; i++)
        {
            if (_s[i] == '\n')
            {
                line++;
            }
        }

        return line.ToString(System.Globalization.NumberFormatInfo.InvariantInfo);
    }

    private string Column(int pos)
    {
        int col = 1;
        for (int i = 0; i < pos && i < _s.Length; i++)
        {
            col = _s[i] == '\n' ? 1 : col + 1;
        }

        return col.ToString(System.Globalization.NumberFormatInfo.InvariantInfo);
    }

    /// <summary>The generic "unexpected token" error for the character at the current position (end of input reads as "Unexpected end").</summary>
    private JsException Unexpected()
    {
        if (_pos >= _s.Length)
        {
            return UnexpectedEnd();
        }

        if (_pos == 0 && _s is "undefined" or "NaN" or "Infinity")
        {
            return Error("\"" + _s + "\" is not valid JSON");
        }

        const int Context = 10;
        string snippet;
        if (_s.Length < (2 * Context) + 1)
        {
            snippet = "\"" + _s + "\"";
        }
        else
        {
            int start = Math.Max(0, _pos - Context);
            int end = Math.Min(_s.Length, _pos + Context);
            snippet = string.Concat(start > 0 ? "...\"" : "\"", _s.AsSpan(start, end - start), end < _s.Length ? "\"..." : "\"");
        }

        return Error("Unexpected token '" + _s[_pos] + "', " + snippet + " is not valid JSON");
    }

    private void SkipWhite()
    {
        while (_pos < _s.Length && _s[_pos] is ' ' or '\t' or '\n' or '\r')
        {
            _pos++;
        }
    }

    /// <summary>Parses the whole text; <paramref name="record"/> receives the root parse record when recording.</summary>
    internal JsValue Parse(out ParseRecord? record)
    {
        Realm realm = Agent.Current.Realm;
        Agent agent = Agent.Current;
        var stack = new List<Frame>();
        _pos = 0;
        SkipWhite();
        JsValue value;
        ParseRecord? rec;
        while (true)
        {
            // A value is expected at _pos.
            agent.Tick();
            if (_pos >= _s.Length)
            {
                throw UnexpectedEnd();
            }

            int start = _pos;
            char c = _s[_pos];
            bool complete = true;
            value = default;
            rec = null;
            switch (c)
            {
                case '{':
                    _pos++;
                    SkipWhite();
                    if (_pos < _s.Length && _s[_pos] == '}')
                    {
                        _pos++;
                        agent.Alloc(64);
                        value = JsValue.FromObject(realm.NewObject());
                        rec = _record ? new ParseRecord { Value = value, Start = start, End = _pos, Entries = [] } : null;
                    }
                    else
                    {
                        if (_pos >= _s.Length || _s[_pos] != '"')
                        {
                            throw At("Expected property name or '}'");
                        }

                        agent.Alloc(64);
                        var obj = realm.NewObject();
                        var f = new Frame { Container = obj, Start = start, Record = _record ? new ParseRecord { Value = JsValue.FromObject(obj), Start = start, Entries = [] } : null };
                        stack.Add(f);
                        ReadMemberKey(f);
                        complete = false;
                    }

                    break;
                case '[':
                    _pos++;
                    SkipWhite();
                    agent.Alloc(64);
                    var arr = new JsArray(realm.ArrayPrototype);
                    if (_pos < _s.Length && _s[_pos] == ']')
                    {
                        _pos++;
                        value = JsValue.FromObject(arr);
                        rec = _record ? new ParseRecord { Value = value, Start = start, End = _pos, Elements = [] } : null;
                    }
                    else
                    {
                        stack.Add(new Frame { Container = arr, IsArray = true, Start = start, Record = _record ? new ParseRecord { Value = JsValue.FromObject(arr), Start = start, Elements = [] } : null });
                        complete = false;
                    }

                    break;
                case '"':
                    value = JsValue.FromString(ReadString());
                    rec = Prim(value, start);
                    break;
                case 't':
                    ExpectLiteral("true");
                    value = JsValue.True;
                    rec = Prim(value, start);
                    break;
                case 'f':
                    ExpectLiteral("false");
                    value = JsValue.False;
                    rec = Prim(value, start);
                    break;
                case 'n':
                    ExpectLiteral("null");
                    value = JsValue.Null;
                    rec = Prim(value, start);
                    break;
                default:
                    if (c == '-' || c is >= '0' and <= '9')
                    {
                        value = JsValue.FromNumber(ReadNumber());
                        rec = Prim(value, start);
                        break;
                    }

                    throw Unexpected();
            }

            if (!complete)
            {
                continue;
            }

            // A value is complete: attach it to its container, then continue with the next sibling or close containers.
            while (true)
            {
                if (stack.Count == 0)
                {
                    SkipWhite();
                    if (_pos < _s.Length)
                    {
                        throw At("Unexpected non-whitespace character after JSON");
                    }

                    record = rec;
                    return value;
                }

                Frame top = stack[^1];
                agent.Alloc(16);
                if (top.IsArray)
                {
                    ((JsArray)top.Container).Push(value);
                    top.Record?.Elements!.Add(rec!);
                }
                else
                {
                    top.Container.CreateDataProperty(top.Key!, value);
                    if (top.Record is not null)
                    {
                        top.Record.Entries![top.Key!] = rec!;
                    }
                }

                SkipWhite();
                char n = _pos < _s.Length ? _s[_pos] : '\0';
                bool atEnd = _pos >= _s.Length;
                if (top.IsArray)
                {
                    if (!atEnd && n == ',')
                    {
                        _pos++;
                        SkipWhite();
                        break; // next element: a value is expected
                    }

                    if (!atEnd && n == ']')
                    {
                        _pos++;
                        stack.RemoveAt(stack.Count - 1);
                        value = JsValue.FromObject(top.Container);
                        rec = top.Record;
                        if (rec is not null)
                        {
                            rec.End = _pos;
                        }

                        continue;
                    }

                    throw At("Expected ',' or ']' after array element");
                }

                if (!atEnd && n == ',')
                {
                    _pos++;
                    SkipWhite();
                    if (_pos >= _s.Length || _s[_pos] != '"')
                    {
                        throw At("Expected double-quoted property name");
                    }

                    ReadMemberKey(top);
                    break;
                }

                if (!atEnd && n == '}')
                {
                    _pos++;
                    stack.RemoveAt(stack.Count - 1);
                    value = JsValue.FromObject(top.Container);
                    rec = top.Record;
                    if (rec is not null)
                    {
                        rec.End = _pos;
                    }

                    continue;
                }

                throw At("Expected ',' or '}' after property value");
            }
        }
    }

    private ParseRecord? Prim(JsValue v, int start) => _record ? new ParseRecord { Value = v, Start = start, End = _pos } : null;

    /// <summary>Reads <c>"key" :</c> at the current position (which holds the opening quote) and leaves the position at the value.</summary>
    private void ReadMemberKey(Frame f)
    {
        f.Key = ReadString();
        SkipWhite();
        if (_pos >= _s.Length || _s[_pos] != ':')
        {
            throw At("Expected ':' after property name");
        }

        _pos++;
        SkipWhite();
    }

    private void ExpectLiteral(string word)
    {
        for (int i = 0; i < word.Length; i++)
        {
            if (_pos >= _s.Length || _s[_pos] != word[i])
            {
                throw Unexpected();
            }

            _pos++;
        }
    }

    private double ReadNumber()
    {
        int start = _pos;
        if (_s[_pos] == '-')
        {
            _pos++;
            if (_pos >= _s.Length || _s[_pos] is < '0' or > '9')
            {
                throw At("No number after minus sign");
            }
        }

        if (_s[_pos] == '0')
        {
            _pos++;
            if (_pos < _s.Length && _s[_pos] is >= '0' and <= '9')
            {
                throw At("Unexpected number");
            }
        }
        else
        {
            while (_pos < _s.Length && _s[_pos] is >= '0' and <= '9')
            {
                _pos++;
            }
        }

        if (_pos < _s.Length && _s[_pos] == '.')
        {
            _pos++;
            if (_pos >= _s.Length || _s[_pos] is < '0' or > '9')
            {
                throw At("Unterminated fractional number");
            }

            while (_pos < _s.Length && _s[_pos] is >= '0' and <= '9')
            {
                _pos++;
            }
        }

        if (_pos < _s.Length && _s[_pos] is 'e' or 'E')
        {
            _pos++;
            if (_pos < _s.Length && _s[_pos] is '+' or '-')
            {
                _pos++;
            }

            if (_pos >= _s.Length || _s[_pos] is < '0' or > '9')
            {
                throw At("Exponent part is missing a number");
            }

            while (_pos < _s.Length && _s[_pos] is >= '0' and <= '9')
            {
                _pos++;
            }
        }

        return double.Parse(_s.AsSpan(start, _pos - start), System.Globalization.NumberStyles.Float, System.Globalization.NumberFormatInfo.InvariantInfo);
    }

    /// <summary>Reads a string literal; the position is at the opening quote on entry and after the closing quote on exit.</summary>
    private string ReadString()
    {
        _pos++;
        int start = _pos;
        // Fast path: no escapes.
        while (_pos < _s.Length)
        {
            char c = _s[_pos];
            if (c == '"')
            {
                string res = _s.Substring(start, _pos - start);
                _pos++;
                Agent.Current.Alloc((res.Length * 2L) + 24);
                return res;
            }

            if (c == '\\')
            {
                break;
            }

            if (c < 0x20)
            {
                throw At("Bad control character in string literal");
            }

            _pos++;
        }

        var sb = new StringBuilder();
        sb.Append(_s, start, _pos - start);
        while (true)
        {
            if (_pos >= _s.Length)
            {
                throw At("Unterminated string");
            }

            char c = _s[_pos];
            if (c == '"')
            {
                _pos++;
                Agent.Current.Charge(sb.Length >> 6);
                Agent.Current.Alloc((sb.Length * 2L) + 24);
                return sb.ToString();
            }

            if (c < 0x20)
            {
                throw At("Bad control character in string literal");
            }

            if (c != '\\')
            {
                sb.Append(c);
                _pos++;
                continue;
            }

            _pos++;
            if (_pos >= _s.Length)
            {
                throw UnexpectedEnd();
            }

            char e = _s[_pos];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    int code = 0;
                    for (int k = 1; k <= 4; k++)
                    {
                        _pos++;
                        int d = _pos < _s.Length ? NumberConv.DigitValue(_s[_pos]) : -1;
                        if (d < 0 || d > 15)
                        {
                            throw At("Bad Unicode escape");
                        }

                        code = (code << 4) | d;
                    }

                    sb.Append((char)code);
                    break;
                default:
                    throw At("Bad escaped character");
            }

            _pos++;
        }
    }
}

/// <summary>JSON (ECMA-262 25.5), including JSON.rawJSON, JSON.isRawJSON and the reviver's source text access.</summary>
internal static partial class Builtins
{
    private static void SetupMathJson(Realm r)
    {
        SetupMath(r);
        SetupJson(r);
    }

    private static void SetupJson(Realm r)
    {
        var json = r.NewObject();
        r.Global.DefineDirect("JSON", JsValue.FromObject(json), PropFlags.Hidden);
        json.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("JSON"), PropFlags.Configurable);
        r.Method(json, "parse", 2, (t, a, nt) =>
        {
            string text = Ops.ToStr(A(a, 0));
            JsValue reviver = A(a, 1);
            bool revive = Ops.IsCallable(reviver);
            Cost(text.Length);
            var parser = new JsonParser(text, revive);
            JsValue unfiltered = parser.Parse(out JsonParser.ParseRecord? snapshot);
            if (!revive)
            {
                return unfiltered;
            }

            JsObject root = r.NewObject();
            root.CreateDataPropertyOrThrow(string.Empty, unfiltered);
            return Internalize(r, root, reviver, parser.Source, snapshot);
        });
        r.Method(json, "stringify", 3, (t, a, nt) => Stringify(r, A(a, 0), A(a, 1), A(a, 2)));
        r.Method(json, "rawJSON", 1, (t, a, nt) =>
        {
            string s = Ops.ToStr(A(a, 0));
            if (s.Length == 0)
            {
                throw Errors.Syntax("Invalid value for JSON.rawJSON");
            }

            static bool Edge(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '"';
            if (!(Edge(s[0]) || s[0] == '-'))
            {
                throw Errors.Syntax("Unexpected token '" + s[0] + "', " + (s.Length < 21 ? "\"" + s + "\"" : "\"" + s[..10] + "\"...") + " is not valid JSON");
            }

            if (!Edge(s[^1]))
            {
                throw Errors.Syntax("Unexpected token '" + s[^1] + "', " + (s.Length < 21 ? "\"" + s + "\"" : "...\"" + s[^10..] + "\"") + " is not valid JSON");
            }

            new JsonParser(s, false).Parse(out _);
            var o = new JsRawJsonObject();
            o.CreateDataPropertyOrThrow("rawJSON", Ops.Str(s));
            SetIntegrity(JsValue.FromObject(o), true);
            return JsValue.FromObject(o);
        });
        r.Method(json, "isRawJSON", 1, (t, a, nt) => Ops.Bool(A(a, 0).Ref is JsRawJsonObject));
    }

    /// <summary>One suspended InternalizeJSONProperty (25.5.2.4) call.</summary>
    private sealed class ReviveFrame
    {
        internal JsObject Holder = null!;
        internal string Name = null!;
        internal JsValue Val;
        internal JsObject Context = null!;
        internal JsonParser.ParseRecord? Record;
        internal bool IsArray;
        internal long Len;
        internal long Index;
        internal List<string>? Keys;
        internal string? Pending;
    }

    /// <summary>InternalizeJSONProperty without recursion: the walk over the parsed value keeps its own stack.</summary>
    private static JsValue Internalize(Realm r, JsObject root, JsValue reviver, string source, JsonParser.ParseRecord? snapshot)
    {
        var stack = new List<ReviveFrame>();
        ReviveFrame Enter(JsObject holder, string name, JsonParser.ParseRecord? rec)
        {
            Agent.Current.Tick();
            var f = new ReviveFrame { Holder = holder, Name = name, Record = rec };
            f.Val = holder.Get(name);
            f.Context = r.NewObject();
            if (rec is not null && Ops.SameValue(rec.Value, f.Val))
            {
                if (!f.Val.IsObject)
                {
                    f.Context.CreateDataPropertyOrThrow("source", Ops.Str(source.Substring(rec.Start, rec.End - rec.Start)));
                }
            }
            else
            {
                f.Record = null;
            }

            if (f.Val.Ref is JsObject vo)
            {
                f.IsArray = Ops.IsArray(f.Val);
                if (f.IsArray)
                {
                    f.Len = Ops.LengthOfArrayLike(vo);
                }
                else
                {
                    f.Keys = EnumerableOwnStringKeys(vo);
                }
            }

            return f;
        }

        stack.Add(Enter(root, string.Empty, snapshot));
        JsValue returned = default;
        bool haveReturn = false;
        while (true)
        {
            ReviveFrame f = stack[^1];
            if (haveReturn)
            {
                // The child call for f.Pending finished with `returned`.
                JsObject vo = (JsObject)f.Val.Ref!;
                if (returned.IsUndefined)
                {
                    vo.Delete(new PropertyKey(f.Pending!));
                }
                else
                {
                    vo.CreateDataProperty(new PropertyKey(f.Pending!), returned);
                }

                haveReturn = false;
            }

            if (f.Val.Ref is JsObject)
            {
                string? next = null;
                JsonParser.ParseRecord? childRec = null;
                if (f.IsArray)
                {
                    if (f.Index < f.Len)
                    {
                        next = Ops.NumberToStr(f.Index);
                        List<JsonParser.ParseRecord>? els = f.Record?.Elements;
                        childRec = els is not null && f.Index < els.Count ? els[(int)f.Index] : null;
                        f.Index++;
                    }
                }
                else if (f.Index < f.Keys!.Count)
                {
                    next = f.Keys[(int)f.Index++];
                    JsonParser.ParseRecord? found = null;
                    f.Record?.Entries?.TryGetValue(next, out found);
                    childRec = found;
                }

                if (next is not null)
                {
                    f.Pending = next;
                    stack.Add(Enter((JsObject)f.Val.Ref!, next, childRec));
                    continue;
                }
            }

            // All children done: call the reviver.
            JsValue result = Ops.Call(reviver, JsValue.FromObject(f.Holder), [Ops.Str(f.Name), f.Val, JsValue.FromObject(f.Context)]);
            stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0)
            {
                return result;
            }

            returned = result;
            haveReturn = true;
        }
    }

    /// <summary>EnumerableOwnProperties (7.3.23) with kind key, restricted to string keys.</summary>
    private static List<string> EnumerableOwnStringKeys(JsObject o)
    {
        var keys = new List<PropertyKey>();
        o.OwnKeys(keys);
        var res = new List<string>(keys.Count);
        foreach (PropertyKey k in keys)
        {
            if (k.Ref is string s && o.GetOwnProperty(k, out PropertyDescriptor d) && d.Enumerable)
            {
                res.Add(s);
            }
        }

        return res;
    }

    // ---- JSON.stringify ----------------------------------------------------------------------------------------------

    private sealed class StringifyFrame
    {
        internal JsObject Obj = null!;
        internal bool IsArray;
        internal List<string>? Keys;
        internal long Len;
        internal long I;
        internal bool Any;
        internal string Stepback = null!;
        internal string Indent = null!;
        internal string CurKey = string.Empty;
    }

    private static JsValue Stringify(Realm r, JsValue value, JsValue replacer, JsValue space)
    {
        JsValue replacerFn = default;
        List<string>? propertyList = null;
        if (replacer.Ref is JsObject ro)
        {
            if (ro.IsCallable)
            {
                replacerFn = replacer;
            }
            else if (Ops.IsArray(replacer))
            {
                propertyList = [];
                long len = Ops.LengthOfArrayLike(ro);
                for (long k = 0; k < len; k++)
                {
                    JsValue v = ro.Get(StrIdx(k));
                    string? item = null;
                    if (v.IsString)
                    {
                        item = v.AsString;
                    }
                    else if (v.IsNumber)
                    {
                        item = Ops.NumberToStr(v.Num);
                    }
                    else if (v.Ref is JsStringObject or JsPrimitiveObject { ClassName: "Number" })
                    {
                        item = Ops.ToStr(v);
                    }

                    if (item is not null && !propertyList.Contains(item))
                    {
                        propertyList.Add(item);
                    }
                }
            }
        }

        if (space.Ref is JsPrimitiveObject { ClassName: "Number" })
        {
            space = Ops.Num(Ops.ToNumber(space));
        }
        else if (space.Ref is JsStringObject)
        {
            space = Ops.Str(Ops.ToStr(space));
        }

        string gap = string.Empty;
        if (space.IsNumber)
        {
            double n = Math.Min(10, Ops.ToIntegerOrInfinity(space));
            gap = n < 1 ? string.Empty : new string(' ', (int)n);
        }
        else if (space.IsString)
        {
            string sp = space.AsString;
            gap = sp.Length <= 10 ? sp : sp[..10];
        }

        JsObject wrapper = r.NewObject();
        wrapper.CreateDataPropertyOrThrow(string.Empty, value);
        var sb = new StringBuilder();
        var stack = new List<StringifyFrame>();
        var inStack = new HashSet<JsObject>(ReferenceEqualityComparer.Instance);
        Agent agent = Agent.Current;
        int limit = agent.Limits.MaxStringLength;

        // Steps 1-4 and the type dispatch of SerializeJSONProperty (25.5.4.2). Returns false for "undefined".
        bool Resolve(JsObject holder, string key, out JsValue v)
        {
            agent.Tick();
            v = holder.Get(key);
            if (v.Ref is JsObject or System.Numerics.BigInteger)
            {
                JsValue toJson = Ops.GetV(v, "toJSON");
                if (Ops.IsCallable(toJson))
                {
                    v = Ops.Call(toJson, v, [Ops.Str(key)]);
                }
            }

            if (!replacerFn.IsUndefined)
            {
                v = Ops.Call(replacerFn, JsValue.FromObject(holder), [Ops.Str(key), v]);
            }

            if (v.Ref is JsObject vo)
            {
                if (vo is JsRawJsonObject)
                {
                    return true;
                }

                if (vo is JsPrimitiveObject { ClassName: "Number" })
                {
                    v = Ops.Num(Ops.ToNumber(v));
                }
                else if (vo is JsStringObject)
                {
                    v = Ops.Str(Ops.ToStr(v));
                }
                else if (vo is JsPrimitiveObject { ClassName: "Boolean" or "BigInt" } pw)
                {
                    v = pw.Value;
                }
            }

            if (v.Ref is JsSymbol || v.IsUndefined)
            {
                return false;
            }

            return !(v.Ref is JsObject fo && fo.IsCallable);
        }

        // Writes a resolved value; containers push a frame.
        void Emit(JsValue v, string indent)
        {
            switch (v.Ref)
            {
                case string or Rope:
                    Quote(sb, v.AsString);
                    return;
                case System.Numerics.BigInteger:
                    throw Errors.Type("Do not know how to serialize a BigInt");
                case JsRawJsonObject raw:
                    sb.Append(raw.Get("rawJSON").AsString);
                    return;
                case JsObject obj:
                    if (!inStack.Add(obj))
                    {
                        throw CircularError(stack, obj);
                    }

                    var f = new StringifyFrame { Obj = obj, Stepback = indent, Indent = indent + gap };
                    if (Ops.IsArray(v))
                    {
                        f.IsArray = true;
                        f.Len = Ops.LengthOfArrayLike(obj);
                        sb.Append('[');
                    }
                    else
                    {
                        f.Keys = propertyList ?? EnumerableOwnStringKeys(obj);
                        sb.Append('{');
                    }

                    agent.Alloc(96);
                    stack.Add(f);
                    return;
                default:
                    if (v.IsNumber)
                    {
                        sb.Append(double.IsFinite(v.Num) ? Ops.NumberToStr(v.Num) : "null");
                    }
                    else
                    {
                        sb.Append(v.IsNull ? "null" : v.IsTrue ? "true" : "false");
                    }

                    return;
            }
        }

        if (!Resolve(wrapper, string.Empty, out JsValue rootValue))
        {
            return default;
        }

        Emit(rootValue, string.Empty);
        while (stack.Count > 0)
        {
            StringifyFrame f = stack[^1];
            if (sb.Length > limit)
            {
                throw Errors.Range("Invalid string length");
            }

            if (f.IsArray)
            {
                if (f.I < f.Len)
                {
                    if (f.I > 0)
                    {
                        sb.Append(',');
                    }

                    if (gap.Length > 0)
                    {
                        sb.Append('\n').Append(f.Indent);
                    }

                    f.CurKey = Ops.NumberToStr(f.I);
                    f.I++;
                    if (Resolve(f.Obj, f.CurKey, out JsValue ev))
                    {
                        Emit(ev, f.Indent);
                    }
                    else
                    {
                        sb.Append("null");
                    }

                    continue;
                }

                if (f.Len > 0 && gap.Length > 0)
                {
                    sb.Append('\n').Append(f.Stepback);
                }

                sb.Append(']');
            }
            else
            {
                if (f.I < f.Keys!.Count)
                {
                    string key = f.Keys[(int)f.I++];
                    f.CurKey = key;
                    if (Resolve(f.Obj, key, out JsValue ev))
                    {
                        if (f.Any)
                        {
                            sb.Append(',');
                        }

                        f.Any = true;
                        if (gap.Length > 0)
                        {
                            sb.Append('\n').Append(f.Indent);
                        }

                        Quote(sb, key);
                        sb.Append(':');
                        if (gap.Length > 0)
                        {
                            sb.Append(' ');
                        }

                        Emit(ev, f.Indent);
                    }

                    continue;
                }

                if (f.Any && gap.Length > 0)
                {
                    sb.Append('\n').Append(f.Stepback);
                }

                sb.Append('}');
            }

            inStack.Remove(f.Obj);
            stack.RemoveAt(stack.Count - 1);
        }

        if (sb.Length > limit)
        {
            throw Errors.Range("Invalid string length");
        }

        agent.Alloc(sb.Length * 2L);
        return Ops.Str(sb.ToString());
    }

    /// <summary>QuoteJSONString (25.5.4.3): lone surrogates are escaped so the output is well-formed.</summary>
    private static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        int run = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool plain = c >= 0x20 && c != '"' && c != '\\' && !char.IsSurrogate(c);
            if (plain)
            {
                run++;
                continue;
            }

            sb.Append(s, i - run, run);
            run = 0;
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb.Append(c).Append(s[i + 1]);
                        i++;
                    }
                    else
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                    }

                    break;
            }
        }

        sb.Append(s, s.Length - run, run).Append('"');
    }

    /// <summary>The TypeError for a cycle, with the path from the first object of the cycle to the repeated one.</summary>
    private static JsException CircularError(List<StringifyFrame> stack, JsObject repeated)
    {
        int start = stack.FindIndex(f => ReferenceEquals(f.Obj, repeated));
        var sb = new StringBuilder("Converting circular structure to JSON\n    --> starting at object with constructor '").Append(ConstructorLabel(repeated)).Append('\'');
        int last = stack.Count - 1;
        // Steps from stack[start] down to stack[last]: each names the key in frame i that leads to frame i+1.
        var lines = new List<string>();
        for (int i = start; i < last; i++)
        {
            lines.Add("    |     " + KeyLabel(stack[i]) + " -> object with constructor '" + ConstructorLabel(stack[i + 1].Obj) + "'");
        }

        if (lines.Count > 3)
        {
            lines = [lines[0], lines[1], "    |     ...", lines[^1]];
        }

        foreach (string l in lines)
        {
            sb.Append('\n').Append(l);
        }

        sb.Append("\n    --- ").Append(KeyLabel(stack[last])).Append(" closes the circle");
        return Errors.Type(sb.ToString());
    }

    private static string KeyLabel(StringifyFrame f) => f.IsArray ? "index " + f.CurKey : "property '" + f.CurKey + "'";

    /// <summary>The constructor name shown in circular-structure errors, found without running user code.</summary>
    private static string ConstructorLabel(JsObject o)
    {
        for (JsObject? p = o; p is not null; p = p.GetPrototypeOf())
        {
            if (p.GetOwnProperty("constructor", out PropertyDescriptor d))
            {
                if (d.IsData && d.Value.Ref is JsObject c && c.GetOwnProperty("name", out PropertyDescriptor nd) && nd.IsData && nd.Value.IsString && nd.Value.AsString.Length > 0)
                {
                    return nd.Value.AsString;
                }

                break;
            }
        }

        return "Object";
    }
}
