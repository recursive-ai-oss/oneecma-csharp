using System.Text;
using RecursiveAI.OneEcma.TextServices;
using NormalizationForm = RecursiveAI.OneEcma.TextServices.NormalizationForm;
using RecursiveAI.OneEcma.Unicode;

namespace RecursiveAI.OneEcma;

/// <summary>String constructor, String.prototype (ECMA-262 22.1, Annex B.2.2) and %StringIteratorPrototype% (22.1.5).</summary>
internal static partial class Builtins
{
    [ThreadStatic]
    private static TextCollator? t_rootCollator;

    /// <summary>Charges fuel in proportion to the work a built-in does over <paramref name="units"/> characters.</summary>
    private static void Cost(long units)
    {
        if (units >= 64)
        {
            Agent.Current.Charge(units >> 5);
        }
    }

    /// <summary>The string a String.prototype method works on: RequireObjectCoercible(this), then ToString (V8 wording on failure).</summary>
    private static string ThisStr(JsValue t, string method)
    {
        if (t.IsString)
        {
            return t.AsString;
        }

        if (t.IsNullish)
        {
            throw Errors.Type("String.prototype" + (method[0] == '[' ? string.Empty : ".") + method + " called on null or undefined");
        }

        return Ops.ToStr(t);
    }

    /// <summary>Checks a result length against the string limit before it is built (V8: "Invalid string length").</summary>
    private static void CheckStringLength(long length)
    {
        if (length > Agent.Current.Limits.MaxStringLength)
        {
            throw Errors.Range("Invalid string length");
        }
    }

    /// <summary>Accounts for a string of <paramref name="length"/> code units about to be built.</summary>
    private static void ReserveString(long length)
    {
        CheckStringLength(length);
        Agent ag = Agent.Current;
        ag.Alloc(length * 2);
        Cost(length);
    }

    private static long StrRel(JsValue v, long len, long dflt)
    {
        if (v.IsUndefined)
        {
            return dflt;
        }

        double n = Ops.ToIntegerOrInfinity(v);
        return n < 0 ? (long)Math.Max(len + n, 0) : (long)Math.Min(n, len);
    }

    private static string StrIdx(long i)
    {
        Agent.Current.Tick();
        return Ops.NumberToStr(i);
    }

    private static readonly string[] OneChar = BuildOneChar();

    private static string[] BuildOneChar()
    {
        var t = new string[128];
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = ((char)i).ToString();
        }

        return t;
    }

    private static string CharStr(char c) => c < 128 ? OneChar[c] : c.ToString();

    /// <summary>IsRegExp (7.2.8).</summary>
    internal static bool IsRegExp(JsValue v)
    {
        if (v.Ref is not JsObject o)
        {
            return false;
        }

        JsValue m = o.Get(new PropertyKey(WellKnown.Match));
        if (!m.IsUndefined)
        {
            return Ops.ToBoolean(m);
        }

        return o.ClassName == "RegExp";
    }

    private static bool IsTrimWhite(char c) => CharClass.IsWhiteSpace(c) || c is '\n' or '\r' or '\u2028' or '\u2029';

    /// <summary>TrimString (22.1.3.32.1): where=0 both, 1 start, 2 end.</summary>
    private static string TrimString(string s, int where)
    {
        int a = 0;
        int b = s.Length;
        if (where != 2)
        {
            while (a < b && IsTrimWhite(s[a]))
            {
                a++;
            }
        }

        if (where != 1)
        {
            while (b > a && IsTrimWhite(s[b - 1]))
            {
                b--;
            }
        }

        Cost(s.Length);
        return a == 0 && b == s.Length ? s : s.Substring(a, b - a);
    }

    /// <summary>StringPad (22.1.3.17.2).</summary>
    private static JsValue StringPad(JsValue t, ReadOnlySpan<JsValue> a, bool atStart, string name)
    {
        string s = ThisStr(t, name);
        long maxLength = Ops.ToLength(A(a, 0));
        if (maxLength <= s.Length)
        {
            return Ops.Str(s);
        }

        string filler = A(a, 1).IsUndefined ? " " : Ops.ToStr(A(a, 1));
        if (filler.Length == 0)
        {
            return Ops.Str(s);
        }

        ReserveString(maxLength);
        int fillLen = (int)maxLength - s.Length;
        var sb = new StringBuilder((int)maxLength);
        if (!atStart)
        {
            sb.Append(s);
        }

        int remaining = fillLen;
        while (remaining > 0)
        {
            int n = Math.Min(remaining, filler.Length);
            sb.Append(filler, 0, n);
            remaining -= n;
        }

        if (atStart)
        {
            sb.Append(s);
        }

        return Ops.Str(sb.ToString());
    }

    private static JsValue CreateHtml(JsValue t, string method, string tag, string? attribute, JsValue value)
    {
        string s = ThisStr(t, method);
        var sb = new StringBuilder("<").Append(tag);
        if (attribute is not null)
        {
            string v = Ops.ToStr(value);
            sb.Append(' ').Append(attribute).Append("=\"").Append(v.Replace("\"", "&quot;", StringComparison.Ordinal)).Append('"');
        }

        sb.Append('>');
        CheckStringLength((long)sb.Length + s.Length + tag.Length + 3);
        return Ops.Str(sb.Append(s).Append("</").Append(tag).Append('>').ToString());
    }

    private static void SetupString(Realm r)
    {
        var proto = new JsStringObject(r.ObjectPrototype, string.Empty);
        r.StringPrototype = proto;
        NativeFunction ctor = r.Ctor("String", 1, (t, a, nt) =>
        {
            string s = a.Length == 0 ? string.Empty : nt is null && a[0].Ref is JsSymbol sy ? sy.ToString() : Ops.ToStr(a[0]);
            if (nt is null)
            {
                return Ops.Str(s);
            }

            return JsValue.FromObject(new JsStringObject(Ops.GetPrototypeFromConstructor(nt, x => x.StringPrototype), s));
        }, proto);

        // ---- constructor properties ----
        r.Method(ctor, "fromCharCode", 1, (t, a, nt) =>
        {
            if (a.Length == 1)
            {
                return Ops.Str(CharStr((char)(ushort)Ops.ToUint32(a[0])));
            }

            Cost(a.Length);
            var sb = new StringBuilder(a.Length);
            foreach (JsValue v in a)
            {
                sb.Append((char)(ushort)Ops.ToUint32(v));
            }

            return Ops.Str(sb.ToString());
        });
        r.Method(ctor, "fromCodePoint", 1, (t, a, nt) =>
        {
            Cost(a.Length);
            var sb = new StringBuilder(a.Length);
            foreach (JsValue v in a)
            {
                double n = Ops.ToNumber(v);
                if (n != Math.Truncate(n) || double.IsNaN(n) || n < 0 || n > 0x10FFFF)
                {
                    throw Errors.Range("Invalid code point " + Ops.NumberToStr(n));
                }

                int cp = (int)n;
                if (cp < 0x10000)
                {
                    sb.Append((char)cp);
                }
                else
                {
                    cp -= 0x10000;
                    sb.Append((char)(0xD800 + (cp >> 10))).Append((char)(0xDC00 + (cp & 0x3FF)));
                }
            }

            return Ops.Str(sb.ToString());
        });
        r.Method(ctor, "raw", 1, (t, a, nt) =>
        {
            JsObject cooked = Ops.ToObject(A(a, 0));
            JsObject literals = Ops.ToObject(cooked.Get("raw"));
            long literalCount = Ops.LengthOfArrayLike(literals);
            if (literalCount <= 0)
            {
                return Ops.Str(string.Empty);
            }

            var sb = new StringBuilder();
            for (long i = 0; ; i++)
            {
                sb.Append(Ops.ToStr(literals.Get(StrIdx(i))));
                CheckStringLength(sb.Length);
                if (i + 1 == literalCount)
                {
                    return Ops.Str(sb.ToString());
                }

                if (i + 1 < a.Length)
                {
                    sb.Append(Ops.ToStr(a[(int)i + 1]));
                    CheckStringLength(sb.Length);
                }
            }
        });

        // ---- prototype ----
        string ThisS(JsValue t, string name) => t.IsString ? t.AsString : t.Ref is JsStringObject so ? so.Value : throw Errors.Type("String.prototype." + name + " requires that 'this' be a String");
        r.Method(proto, "toString", 0, (t, a, nt) => Ops.Str(ThisS(t, "toString")));
        r.Method(proto, "valueOf", 0, (t, a, nt) => Ops.Str(ThisS(t, "valueOf")));
        r.Method(proto, "at", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "at");
            double rel = Ops.ToIntegerOrInfinity(A(a, 0));
            double k = rel >= 0 ? rel : s.Length + rel;
            return k < 0 || k >= s.Length ? default : Ops.Str(CharStr(s[(int)k]));
        });
        r.Method(proto, "charAt", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "charAt");
            double p = Ops.ToIntegerOrInfinity(A(a, 0));
            return Ops.Str(p >= 0 && p < s.Length ? CharStr(s[(int)p]) : string.Empty);
        });
        r.Method(proto, "charCodeAt", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "charCodeAt");
            double p = Ops.ToIntegerOrInfinity(A(a, 0));
            return Ops.Num(p >= 0 && p < s.Length ? s[(int)p] : double.NaN);
        });
        r.Method(proto, "codePointAt", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "codePointAt");
            double p = Ops.ToIntegerOrInfinity(A(a, 0));
            if (p < 0 || p >= s.Length)
            {
                return default;
            }

            int i = (int)p;
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                return Ops.Num(char.ConvertToUtf32(c, s[i + 1]));
            }

            return Ops.Num(c);
        });
        r.Method(proto, "concat", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "concat");
            if (a.Length == 1)
            {
                string o = Ops.ToStr(a[0]);
                CheckStringLength((long)s.Length + o.Length);
                return Ops.Str(s + o);
            }

            var sb = new StringBuilder(s);
            foreach (JsValue v in a)
            {
                sb.Append(Ops.ToStr(v));
                CheckStringLength(sb.Length);
                Agent.Current.Tick();
            }

            return Ops.Str(sb.ToString());
        });
        r.Method(proto, "endsWith", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "endsWith");
            if (IsRegExp(A(a, 0)))
            {
                throw Errors.Type("First argument to String.prototype.endsWith must not be a regular expression");
            }

            string search = Ops.ToStr(A(a, 0));
            double end = A(a, 1).IsUndefined ? s.Length : Math.Clamp(Ops.ToIntegerOrInfinity(A(a, 1)), 0, s.Length);
            double start = end - search.Length;
            Cost(search.Length);
            return Ops.Bool(start >= 0 && string.CompareOrdinal(s, (int)start, search, 0, search.Length) == 0);
        });
        r.Method(proto, "includes", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "includes");
            if (IsRegExp(A(a, 0)))
            {
                throw Errors.Type("First argument to String.prototype.includes must not be a regular expression");
            }

            string search = Ops.ToStr(A(a, 0));
            int pos = (int)Math.Clamp(Ops.ToIntegerOrInfinity(A(a, 1)), 0, s.Length);
            Cost(s.Length);
            return Ops.Bool(s.AsSpan(pos).IndexOf(search.AsSpan(), StringComparison.Ordinal) >= 0);
        });
        r.Method(proto, "indexOf", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "indexOf");
            string search = Ops.ToStr(A(a, 0));
            int pos = (int)Math.Clamp(Ops.ToIntegerOrInfinity(A(a, 1)), 0, s.Length);
            Cost(s.Length);
            int i = s.AsSpan(pos).IndexOf(search.AsSpan(), StringComparison.Ordinal);
            return Ops.Num(i < 0 ? -1 : i + pos);
        });
        r.Method(proto, "isWellFormed", 0, (t, a, nt) =>
        {
            string s = ThisStr(t, "isWellFormed");
            Cost(s.Length);
            return Ops.Bool(FirstLoneSurrogate(s) < 0);
        });
        r.Method(proto, "lastIndexOf", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "lastIndexOf");
            string search = Ops.ToStr(A(a, 0));
            double numPos = Ops.ToNumber(A(a, 1));
            double pos = double.IsNaN(numPos) ? double.PositiveInfinity : Math.Truncate(numPos);
            int start = (int)Math.Clamp(pos, 0, s.Length);
            int limit = Math.Min(s.Length, start + search.Length);
            Cost(s.Length);
            return Ops.Num(s.AsSpan(0, limit).LastIndexOf(search.AsSpan(), StringComparison.Ordinal));
        });
        r.Method(proto, "localeCompare", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "localeCompare");
            string that = Ops.ToStr(A(a, 0));
            Cost((long)s.Length + that.Length);
            t_rootCollator ??= TextCollator.Create("und");
            int c = t_rootCollator.Compare(s, that);
            return Ops.Num(Math.Sign(c));
        });
        r.Method(proto, "normalize", 0, (t, a, nt) =>
        {
            string s = ThisStr(t, "normalize");
            string f = A(a, 0).IsUndefined ? "NFC" : Ops.ToStr(A(a, 0));
            NormalizationForm form = f switch
            {
                "NFC" => NormalizationForm.Nfc,
                "NFD" => NormalizationForm.Nfd,
                "NFKC" => NormalizationForm.Nfkc,
                "NFKD" => NormalizationForm.Nfkd,
                _ => throw Errors.Range("The normalization form should be one of NFC, NFD, NFKC, NFKD."),
            };
            Cost(s.Length * 4L);
            if (s.Length > 0 && (long)s.Length * 18 > Agent.Current.Limits.MaxStringLength)
            {
                // The worst-case expansion could pass the string limit; normalize first, then check the real length.
                string big = Normalizer.Normalize(s, form);
                CheckStringLength(big.Length);
                return Ops.Str(big);
            }

            return Ops.Str(Normalizer.Normalize(s, form));
        });
        r.Method(proto, "padEnd", 1, (t, a, nt) => StringPad(t, a, false, "padEnd"));
        r.Method(proto, "padStart", 1, (t, a, nt) => StringPad(t, a, true, "padStart"));
        r.Method(proto, "repeat", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "repeat");
            double n = Ops.ToNumber(A(a, 0));
            double count = double.IsNaN(n) ? 0 : Math.Truncate(n);
            if (count < 0 || double.IsInfinity(count))
            {
                throw Errors.Range("Invalid count value: " + Ops.NumberToStr(n));
            }

            if (count == 0 || s.Length == 0)
            {
                return Ops.Str(string.Empty);
            }

            if (count * s.Length > Agent.Current.Limits.MaxStringLength)
            {
                throw Errors.Range("Invalid string length");
            }

            int total = (int)count * s.Length;
            ReserveString(total);
            if (s.Length == 1)
            {
                return Ops.Str(new string(s[0], total));
            }

            return Ops.Str(string.Create(total, s, static (span, src) =>
            {
                src.CopyTo(span);
                int filled = src.Length;
                while (filled < span.Length)
                {
                    int n = Math.Min(filled, span.Length - filled);
                    span[..n].CopyTo(span[filled..]);
                    filled += n;
                }
            }));
        });
        r.Method(proto, "slice", 2, (t, a, nt) =>
        {
            string s = ThisStr(t, "slice");
            long from = StrRel(A(a, 0), s.Length, 0);
            long to = StrRel(A(a, 1), s.Length, s.Length);
            return Ops.Str(from < to ? s.Substring((int)from, (int)(to - from)) : string.Empty);
        });
        r.Method(proto, "startsWith", 1, (t, a, nt) =>
        {
            string s = ThisStr(t, "startsWith");
            if (IsRegExp(A(a, 0)))
            {
                throw Errors.Type("First argument to String.prototype.startsWith must not be a regular expression");
            }

            string search = Ops.ToStr(A(a, 0));
            int start = (int)Math.Clamp(Ops.ToIntegerOrInfinity(A(a, 1)), 0, s.Length);
            Cost(search.Length);
            return Ops.Bool(start + search.Length <= s.Length && string.CompareOrdinal(s, start, search, 0, search.Length) == 0);
        });
        r.Method(proto, "substring", 2, (t, a, nt) =>
        {
            string s = ThisStr(t, "substring");
            int from = (int)Math.Clamp(Ops.ToIntegerOrInfinity(A(a, 0)), 0, s.Length);
            int to = A(a, 1).IsUndefined ? s.Length : (int)Math.Clamp(Ops.ToIntegerOrInfinity(A(a, 1)), 0, s.Length);
            return Ops.Str(s.Substring(Math.Min(from, to), Math.Abs(to - from)));
        });
        r.Method(proto, "substr", 2, (t, a, nt) =>
        {
            string s = ThisStr(t, "substr");
            double start = Ops.ToIntegerOrInfinity(A(a, 0));
            start = double.IsNegativeInfinity(start) ? 0 : start < 0 ? Math.Max(s.Length + start, 0) : Math.Min(start, s.Length);
            double len = A(a, 1).IsUndefined ? s.Length : Ops.ToIntegerOrInfinity(A(a, 1));
            len = Math.Clamp(len, 0, s.Length);
            double end = Math.Min(start + len, s.Length);
            return Ops.Str(start >= end ? string.Empty : s.Substring((int)start, (int)(end - start)));
        });
        r.Method(proto, "toLocaleLowerCase", 0, (t, a, nt) =>
        {
            string s = ThisStr(t, "toLocaleLowerCase");
            Cost(s.Length);
            return Ops.Str(CaseMapping.ToLower(s));
        });
        r.Method(proto, "toLocaleUpperCase", 0, (t, a, nt) =>
        {
            string s = ThisStr(t, "toLocaleUpperCase");
            Cost(s.Length);
            string u = CaseMapping.ToUpper(s);
            CheckStringLength(u.Length);
            return Ops.Str(u);
        });
        r.Method(proto, "toLowerCase", 0, (t, a, nt) =>
        {
            string s = ThisStr(t, "toLowerCase");
            Cost(s.Length);
            return Ops.Str(CaseMapping.ToLower(s));
        });
        r.Method(proto, "toUpperCase", 0, (t, a, nt) =>
        {
            string s = ThisStr(t, "toUpperCase");
            Cost(s.Length);
            string u = CaseMapping.ToUpper(s);
            CheckStringLength(u.Length);
            return Ops.Str(u);
        });
        r.Method(proto, "toWellFormed", 0, (t, a, nt) =>
        {
            string s = ThisStr(t, "toWellFormed");
            Cost(s.Length);
            int first = FirstLoneSurrogate(s);
            if (first < 0)
            {
                return Ops.Str(s);
            }

            var chars = s.ToCharArray();
            for (int i = first; i < chars.Length; i++)
            {
                if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
                {
                    i++;
                }
                else if (char.IsSurrogate(chars[i]))
                {
                    chars[i] = '\uFFFD';
                }
            }

            return Ops.Str(new string(chars));
        });
        r.Method(proto, "trim", 0, (t, a, nt) => Ops.Str(TrimString(ThisStr(t, "trim"), 0)));
        NativeFunction trimStart = r.Method(proto, "trimStart", 0, (t, a, nt) => Ops.Str(TrimString(ThisStr(t, "trimStart"), 1)));
        NativeFunction trimEnd = r.Method(proto, "trimEnd", 0, (t, a, nt) => Ops.Str(TrimString(ThisStr(t, "trimEnd"), 2)));
        // B.2.2.15 and B.2.2.16: the aliases are the same function objects.
        proto.DefineDirect("trimLeft", JsValue.FromObject(trimStart), PropFlags.Hidden);
        proto.DefineDirect("trimRight", JsValue.FromObject(trimEnd), PropFlags.Hidden);

        // B.2.2.2 - B.2.2.14: HTML methods.
        foreach ((string name, string tag, string? attr) in new[]
        {
            ("anchor", "a", "name"), ("big", "big", null), ("blink", "blink", null), ("bold", "b", null), ("fixed", "tt", null),
            ("fontcolor", "font", "color"), ("fontsize", "font", "size"), ("italics", "i", null), ("link", "a", "href"),
            ("small", "small", null), ("strike", "strike", null), ("sub", "sub", null), ("sup", "sup", null),
        })
        {
            r.Method(proto, name, attr is null ? 0 : 1, (t, a, nt) => CreateHtml(t, name, tag, attr, A(a, 0)));
        }

        SetupStringRegexMethods(r, proto);

        // 22.1.5 %StringIteratorPrototype%
        var iterProto = new JsObject(r.IteratorPrototype);
        r.Method(iterProto, "next", 0, (t, a, nt) =>
        {
            if (t.Ref is not StringIteratorObject it)
            {
                throw Errors.Type("Method String Iterator.prototype.next called on incompatible receiver " + Ops.DisplayValue(t));
            }

            Agent.Current.Tick();
            if (it.Text is null || it.Pos >= it.Text.Length)
            {
                it.Text = null;
                return JsValue.FromObject(Ops.CreateIterResult(default, true));
            }

            int n = char.IsHighSurrogate(it.Text[it.Pos]) && it.Pos + 1 < it.Text.Length && char.IsLowSurrogate(it.Text[it.Pos + 1]) ? 2 : 1;
            string cp = n == 1 ? CharStr(it.Text[it.Pos]) : it.Text.Substring(it.Pos, n);
            it.Pos += n;
            return JsValue.FromObject(Ops.CreateIterResult(Ops.Str(cp), false));
        });
        iterProto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("String Iterator"), PropFlags.Configurable);
        r.Method(proto, WellKnown.Iterator, "[Symbol.iterator]", 0, (t, a, nt) => JsValue.FromObject(new StringIteratorObject(iterProto) { Text = ThisStr(t, "[Symbol.iterator]") }));
    }

    /// <summary>Index of the first lone surrogate in <paramref name="s"/>, or -1 when it is well-formed (7.1.? IsStringWellFormedUnicode).</summary>
    private static int FirstLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return i;
            }
        }

        return -1;
    }
}
