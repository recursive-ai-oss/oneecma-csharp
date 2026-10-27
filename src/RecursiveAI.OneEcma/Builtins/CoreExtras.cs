using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>Object.hasOwn and Object.groupBy, Error.isError, Promise.try and the URI handling functions of the global object.</summary>
internal static partial class Builtins
{
    private static void SetupCoreExtras(Realm r)
    {
        JsObject objectCtor = r.Intrinsics["%Object%"];

        // ECMA-262 20.1.2.13 Object.hasOwn ( O, P )
        r.Method(objectCtor, "hasOwn", 2, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(A(a, 0));
            PropertyKey key = Ops.ToPropertyKey(A(a, 1));
            return Ops.Bool(o.HasOwnProperty(key));
        });

        // ECMA-262 20.1.2.11 Object.groupBy ( items, callbackfn ) and GroupBy (7.3.35)
        r.Method(objectCtor, "groupBy", 2, (t, a, nt) =>
        {
            JsValue items = A(a, 0);
            JsValue cb = A(a, 1);
            if (items.IsNullish)
            {
                throw Errors.Type("Object.groupBy called on null or undefined");
            }

            if (!Ops.IsCallable(cb))
            {
                throw Errors.Type(Ops.DisplayValue(cb) + " is not a function");
            }

            IteratorRecord rec = Ops.GetIterator(items);
            var keys = new List<PropertyKey>();
            var groups = new Dictionary<PropertyKey, List<JsValue>>(PropertyKeyComparer.Instance);
            double k = 0;
            while (Ops.IteratorStepValue(rec, out JsValue value))
            {
                Agent.Current.Tick();
                PropertyKey key;
                try
                {
                    key = Ops.ToPropertyKey(Ops.Call(cb, default, [value, JsValue.FromNumber(k)]));
                }
                catch (JsException)
                {
                    Ops.IteratorCloseSilently(rec);
                    throw;
                }

                if (!groups.TryGetValue(key, out List<JsValue>? list))
                {
                    list = [];
                    groups[key] = list;
                    keys.Add(key);
                }

                list.Add(value);
                k++;
            }

            var result = new JsObject((JsObject?)null);
            foreach (PropertyKey key in keys)
            {
                result.CreateDataPropertyOrThrow(key, JsValue.FromObject(Ops.CreateArray(groups[key])));
            }

            return JsValue.FromObject(result);
        });

        // ECMA-262 20.5.2.1 Error.isError ( arg ): true for objects with an [[ErrorData]] internal slot.
        r.Method(r.Intrinsics["%Error%"], "isError", 1, (t, a, nt) => Ops.Bool(A(a, 0).Ref is JsError));

        // ECMA-262 27.2.4.8 Promise.try ( callbackfn, ...args )
        r.Method(r.PromiseConstructor, "try", 1, (t, a, nt) =>
        {
            if (!t.IsObject)
            {
                throw Errors.Type("Promise.try called on non-object");
            }

            PromiseCapability cap = Promises.NewCapability(t);
            JsValue status;
            try
            {
                JsValue[] rest = a.Length > 1 ? a[1..].ToArray() : [];
                status = Ops.Call(A(a, 0), default, rest);
            }
            catch (JsException e)
            {
                Ops.Call(cap.Reject, default, [e.Value]);
                return JsValue.FromObject(cap.Promise);
            }

            Ops.Call(cap.Resolve, default, [status]);
            return JsValue.FromObject(cap.Promise);
        });

        SetupUriFunctions(r);
    }

    private const string UriReserved = ";/?:@&=+$,";
    private const string UriUnescapedExtra = "-_.!~*'()";

    private static bool IsAsciiAlnum(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static void SetupUriFunctions(Realm r)
    {
        r.Method(r.Global, "encodeURI", 1, (t, a, nt) => JsValue.FromString(Encode(Ops.ToStr(A(a, 0)), UriReserved + "#")));
        r.Method(r.Global, "encodeURIComponent", 1, (t, a, nt) => JsValue.FromString(Encode(Ops.ToStr(A(a, 0)), string.Empty)));
        r.Method(r.Global, "decodeURI", 1, (t, a, nt) => JsValue.FromString(Decode(Ops.ToStr(A(a, 0)), UriReserved + "#")));
        r.Method(r.Global, "decodeURIComponent", 1, (t, a, nt) => JsValue.FromString(Decode(Ops.ToStr(A(a, 0)), string.Empty)));

        // ECMA-262 B.2.1.1 escape ( string )
        r.Method(r.Global, "escape", 1, (t, a, nt) =>
        {
            string s = Ops.ToStr(A(a, 0));
            Agent.Current.Alloc((long)s.Length * 6);
            var sb = new StringBuilder(s.Length);
            const string Hex = "0123456789ABCDEF";
            foreach (char c in s)
            {
                Agent.Current.Tick();
                if (IsAsciiAlnum(c) || "@*_+-./".Contains(c, StringComparison.Ordinal))
                {
                    sb.Append(c);
                }
                else if (c < 256)
                {
                    sb.Append('%').Append(Hex[c >> 4]).Append(Hex[c & 15]);
                }
                else
                {
                    sb.Append("%u").Append(Hex[c >> 12]).Append(Hex[(c >> 8) & 15]).Append(Hex[(c >> 4) & 15]).Append(Hex[c & 15]);
                }
            }

            return JsValue.FromString(sb.ToString());
        });

        // ECMA-262 B.2.1.2 unescape ( string )
        r.Method(r.Global, "unescape", 1, (t, a, nt) =>
        {
            string s = Ops.ToStr(A(a, 0));
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                Agent.Current.Tick();
                char c = s[i];
                if (c == '%')
                {
                    if (i + 6 <= s.Length && s[i + 1] == 'u' && TryHex(s, i + 2, 4, out int u))
                    {
                        sb.Append((char)u);
                        i += 5;
                        continue;
                    }

                    if (i + 3 <= s.Length && TryHex(s, i + 1, 2, out int b))
                    {
                        sb.Append((char)b);
                        i += 2;
                        continue;
                    }
                }

                sb.Append(c);
            }

            return JsValue.FromString(sb.ToString());
        });
    }

    private static bool TryHex(string s, int start, int count, out int value)
    {
        value = 0;
        for (int i = 0; i < count; i++)
        {
            char c = s[start + i];
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
            if (d < 0)
            {
                return false;
            }

            value = value * 16 + d;
        }

        return true;
    }

    /// <summary>Encode (19.2.6.5): UTF-8 percent-encoding of every code point not in the unescaped set.</summary>
    private static string Encode(string s, string extraUnescaped)
    {
        Agent.Current.Alloc((long)s.Length * 9);
        var sb = new StringBuilder(s.Length);
        const string Hex = "0123456789ABCDEF";
        Span<byte> utf8 = stackalloc byte[4];
        for (int k = 0; k < s.Length; k++)
        {
            Agent.Current.Tick();
            char c = s[k];
            if (IsAsciiAlnum(c) || UriUnescapedExtra.Contains(c, StringComparison.Ordinal) || extraUnescaped.Contains(c, StringComparison.Ordinal))
            {
                sb.Append(c);
                continue;
            }

            int cp = c;
            if (char.IsLowSurrogate(c))
            {
                throw Errors.Make("URIError", "URI malformed");
            }

            if (char.IsHighSurrogate(c))
            {
                if (k + 1 >= s.Length || !char.IsLowSurrogate(s[k + 1]))
                {
                    throw Errors.Make("URIError", "URI malformed");
                }

                cp = char.ConvertToUtf32(c, s[++k]);
            }

            Span<byte> buf = utf8;
            int n = new Rune(cp).EncodeToUtf8(buf);
            for (int i = 0; i < n; i++)
            {
                sb.Append('%').Append(Hex[buf[i] >> 4]).Append(Hex[buf[i] & 15]);
            }
        }

        return sb.ToString();
    }

    /// <summary>Decode (19.2.6.6): percent-escapes of UTF-8 sequences, leaving escapes of characters in <paramref name="preserve"/> as written.</summary>
    private static string Decode(string s, string preserve)
    {
        var sb = new StringBuilder(s.Length);
        for (int k = 0; k < s.Length; k++)
        {
            Agent.Current.Tick();
            char c = s[k];
            if (c != '%')
            {
                sb.Append(c);
                continue;
            }

            int start = k;
            if (k + 3 > s.Length || !TryHex(s, k + 1, 2, out int b))
            {
                throw Errors.Make("URIError", "URI malformed");
            }

            k += 2;
            if (b < 0x80)
            {
                char ch = (char)b;
                if (preserve.Contains(ch, StringComparison.Ordinal))
                {
                    sb.Append(s, start, 3);
                }
                else
                {
                    sb.Append(ch);
                }

                continue;
            }

            int n = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 0;
            if (n == 0)
            {
                throw Errors.Make("URIError", "URI malformed");
            }

            var octets = new byte[n];
            octets[0] = (byte)b;
            if (k + 3 * (n - 1) >= s.Length)
            {
                throw Errors.Make("URIError", "URI malformed");
            }

            for (int j = 1; j < n; j++)
            {
                k++;
                if (s[k] != '%' || !TryHex(s, k + 1, 2, out int cont) || (cont & 0xC0) != 0x80)
                {
                    throw Errors.Make("URIError", "URI malformed");
                }

                k += 2;
                octets[j] = (byte)cont;
            }

            // Strict UTF-8: no overlong forms, surrogates or values above U+10FFFF.
            if (Rune.DecodeFromUtf8(octets, out Rune rune, out int used) != System.Buffers.OperationStatus.Done || used != n)
            {
                throw Errors.Make("URIError", "URI malformed");
            }

            sb.Append(rune.ToString());
        }

        return sb.ToString();
    }
}
