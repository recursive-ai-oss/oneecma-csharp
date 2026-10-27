using System.Numerics;
using System.Text;
using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma;

internal static partial class IntlJs
{
    /// <summary>
    /// The locale-sensitive functions of ECMA-262 as ECMA-402 section 20 redefines them (String, Number, BigInt, Date).
    /// They replace whatever root-locale versions the core installed; the Date ones are installed when a Date constructor exists.
    /// </summary>
    private static void SetupLocaleMethods(Realm r)
    {
        // 20.1.1 String.prototype.localeCompare ( that [ , locales [ , options ] ] )
        r.Method(r.StringPrototype, "localeCompare", 1, (t, a, nt) =>
        {
            if (t.IsNullish)
            {
                throw Errors.Type("String.prototype.localeCompare called on null or undefined");
            }

            string s = Ops.ToStr(t);
            string that = Ops.ToStr(A(a, 0));
            CollatorState c = CreateCollator(Locales(A(a, 1)), A(a, 2));
            return Ops.Num(Math.Sign(c.Collator.Compare(s, that)));
        });

        JsValue lower = r.StringPrototype.Get("toLowerCase");
        JsValue upper = r.StringPrototype.Get("toUpperCase");
        if (Ops.IsCallable(lower) && Ops.IsCallable(upper))
        {
            r.Method(r.StringPrototype, "toLocaleLowerCase", 0, (t, a, nt) => TransformCase(t, A(a, 0), lower, "toLocaleLowerCase", true));
            r.Method(r.StringPrototype, "toLocaleUpperCase", 0, (t, a, nt) => TransformCase(t, A(a, 0), upper, "toLocaleUpperCase", false));
        }

        // 20.2 / 20.3: Number.prototype.toLocaleString and BigInt.prototype.toLocaleString format through a NumberFormat.
        r.Method(r.NumberPrototype, "toLocaleString", 0, (t, a, nt) =>
        {
            double x = t.IsNumber ? t.Num : t.Ref is JsPrimitiveObject { ClassName: "Number" } p ? p.Value.Num
                : throw Errors.Type("Number.prototype.toLocaleString requires that 'this' be a Number");
            NumberFormatService nf = CreateNumberFormat(Locales(A(a, 0)), A(a, 1));
            return Ops.Str(Svc(() => nf.Format(DecimalNumber.FromDouble(x))));
        });
        r.Method(r.BigIntPrototype, "toLocaleString", 0, (t, a, nt) =>
        {
            BigInteger x = t.IsBigInt ? (BigInteger)t.Ref! : t.Ref is JsPrimitiveObject { ClassName: "BigInt" } p && p.Value.IsBigInt ? (BigInteger)p.Value.Ref!
                : throw Errors.Type("BigInt.prototype.toLocaleString requires that 'this' be a BigInt");
            NumberFormatService nf = CreateNumberFormat(Locales(A(a, 0)), A(a, 1));
            return Ops.Str(Svc(() => nf.Format(DecimalNumber.FromBigInteger(x))));
        });

        // 21.4 Date.prototype.toLocale{,Date,Time}String: the same DateTimeFormat path with different required/defaults.
        if (r.Global.Get("Date").Ref is JsObject dateCtor && dateCtor.Get("prototype").Ref is JsObject dateProto && dateProto.Get("valueOf") is { } valueOf && Ops.IsCallable(valueOf))
        {
            foreach ((string name, string required, string defaults) in new[] { ("toLocaleString", "any", "all"), ("toLocaleDateString", "date", "date"), ("toLocaleTimeString", "time", "time") })
            {
                string method = name;
                string req = required, def = defaults;
                r.Method(dateProto, method, 0, (t, a, nt) =>
                {
                    double x = Ops.ToNumber(Ops.Call(valueOf, t));
                    if (double.IsNaN(x))
                    {
                        return Ops.Str("Invalid Date");
                    }

                    DateTimeFormatService dtf = CreateDateTimeFormat(Locales(A(a, 0)), A(a, 1), req, def);
                    return Ops.Str(Svc(() => dtf.Format(x)));
                });
            }
        }
    }

    /// <summary>
    /// TransformCase (ECMA-402 20.1.2.1). Locales with language-sensitive mappings in SpecialCasing.txt (Turkish, Azeri, Lithuanian) get their
    /// context rules applied first, in a form the default mapping leaves alone; everything else uses the default Unicode case mapping.
    /// </summary>
    private static JsValue TransformCase(JsValue thisValue, JsValue locales, JsValue defaultMapping, string method, bool lower)
    {
        if (thisValue.IsNullish)
        {
            throw Errors.Type($"String.prototype.{method} called on null or undefined");
        }

        string s = Ops.ToStr(thisValue);
        List<string> requested = Locales(locales);
        string requestedLocale = requested.Count > 0 ? requested[0] : DefaultLocale();
        string language = requestedLocale.Split('-')[0];
        if (language is "tr" or "az" or "lt")
        {
            s = lower ? LowerTailoring(s, language) : UpperTailoring(s, language);
        }

        return Ops.Call(defaultMapping, Ops.Str(s));
    }

    private static bool IsAbove(string s, int index)
    {
        // Combining class 230 ("Above") after the character at index, skipping combining marks of other classes (SpecialCasing "More_Above").
        for (int i = index + 1; i < s.Length;)
        {
            int cp = char.ConvertToUtf32(s, i);
            int step = cp > 0xFFFF ? 2 : 1;
            if (IsClass230(cp))
            {
                return true;
            }

            if (!IsCombiningMark(cp))
            {
                return false;
            }

            i += step;
        }

        return false;
    }

    private static bool IsClass230(int cp) =>
        cp is >= 0x0300 and <= 0x0314 or 0x033D or 0x033E or 0x033F or 0x0346 or 0x034A or 0x034B or 0x034C or 0x0350 or 0x0351 or 0x0352
            or 0x0357 or 0x035B or >= 0x0363 and <= 0x036F or 0x1D185 or 0x1D186 or 0x1D187 or 0x1D188 or 0x1D189;

    private static bool IsCombiningMark(int cp) =>
        cp is >= 0x0300 and <= 0x036F or >= 0x1D165 and <= 0x1D169 or >= 0x1D16D and <= 0x1D172 or >= 0x1D17B and <= 0x1D182 or >= 0x1D185 and <= 0x1D18B or >= 0x1D1AA and <= 0x1D1AD
            && !IsClass230(cp);

    private static string LowerTailoring(string s, string language)
    {
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (language is "tr" or "az")
            {
                if (c == '\u0130')
                {
                    sb.Append('i');
                }
                else if (c == 'I')
                {
                    // Before_Dot: "I" followed by U+0307 (possibly through marks that are not above or of class 0) is a dotted i.
                    int j = i + 1;
                    while (j < s.Length && s[j] != '\u0307' && IsCombiningMark(s[j]) && !IsClass230(s[j]))
                    {
                        j++;
                    }

                    if (j < s.Length && s[j] == '\u0307')
                    {
                        sb.Append('i');
                        sb.Append(s, i + 1, j - i - 1);
                        i = j;
                    }
                    else
                    {
                        sb.Append('\u0131');
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                // Lithuanian: keep the dot of i, j and i-ogonek when an accent above follows; accented capital I forms decompose.
                switch (c)
                {
                    case 'I' or 'J' or '\u012E' when IsAbove(s, i):
                        sb.Append(c == 'I' ? 'i' : c == 'J' ? 'j' : '\u012F').Append('\u0307');
                        break;
                    case '\u00CC':
                        sb.Append("i\u0307\u0300");
                        break;
                    case '\u00CD':
                        sb.Append("i\u0307\u0301");
                        break;
                    case '\u0128':
                        sb.Append("i\u0307\u0303");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
        }

        return sb.ToString();
    }

    private static bool IsSoftDotted(char c) =>
        c is 'i' or 'j' or '\u012F' or '\u0249' or '\u0268' or '\u029D' or '\u02B2' or '\u03F3' or '\u0456' or '\u0458' or '\u1D62' or '\u1D96'
            or '\u1DA4' or '\u1DA8' or '\u1E2D' or '\u1ECB' or '\u2071' or '\u2148' or '\u2149' or '\u2C7C';

    private static string UpperTailoring(string s, string language)
    {
        var sb = new StringBuilder(s.Length + 2);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (language is "tr" or "az")
            {
                sb.Append(c == 'i' ? '\u0130' : c);
            }
            else if (c == '\u0307' && i > 0 && PrecededBySoftDotted(s, i))
            {
                // After_Soft_Dotted: U+0307 is dropped when uppercasing (the dot is part of the capital).
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static bool PrecededBySoftDotted(string s, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (IsSoftDotted(s[i]))
            {
                return true;
            }

            if (!IsCombiningMark(s[i]) || IsClass230(s[i]))
            {
                return false;
            }
        }

        return false;
    }
}
