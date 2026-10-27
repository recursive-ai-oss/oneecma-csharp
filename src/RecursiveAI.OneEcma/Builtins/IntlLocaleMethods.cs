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

    private static int Ccc(int cp) => Intl.Data.CldrData.Normalization.CombiningClass(cp);

    private static int[] CodePoints(string s)
    {
        var l = new List<int>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                l.Add(char.ConvertToUtf32(s[i], s[i + 1]));
                i++;
            }
            else
            {
                l.Add(s[i]);
            }
        }

        return [.. l];
    }

    private static void Append(StringBuilder sb, int cp)
    {
        if (cp > 0xFFFF)
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }
        else
        {
            sb.Append((char)cp);
        }
    }

    /// <summary>SpecialCasing "More_Above": a class 230 mark follows, with only marks of other non-zero classes between.</summary>
    private static bool MoreAbove(int[] cps, int index)
    {
        for (int i = index + 1; i < cps.Length; i++)
        {
            int c = Ccc(cps[i]);
            if (c == 230)
            {
                return true;
            }

            if (c == 0)
            {
                return false;
            }
        }

        return false;
    }

    private static string LowerTailoring(string s, string language)
    {
        int[] cps = CodePoints(s);
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < cps.Length; i++)
        {
            int c = cps[i];
            if (language is "tr" or "az")
            {
                if (c == 0x130)
                {
                    sb.Append('i');
                }
                else if (c == 'I')
                {
                    // Before_Dot: U+0307 follows, with only marks of other non-zero classes between; the dot is absorbed into the i.
                    int dot = -1;
                    for (int j = i + 1; j < cps.Length; j++)
                    {
                        if (cps[j] == 0x307)
                        {
                            dot = j;
                            break;
                        }

                        int cc = Ccc(cps[j]);
                        if (cc is 0 or 230)
                        {
                            break;
                        }
                    }

                    if (dot < 0)
                    {
                        sb.Append('\u0131');
                    }
                    else
                    {
                        sb.Append('i');
                        for (int j = i + 1; j < dot; j++)
                        {
                            Append(sb, cps[j]);
                        }

                        i = dot;
                    }
                }
                else
                {
                    Append(sb, c);
                }
            }
            else
            {
                // Lithuanian: i, j and i-ogonek keep their dot when an accent above follows; the accented capitals I decompose.
                switch (c)
                {
                    case 'I' or 'J' or 0x12E when MoreAbove(cps, i):
                        sb.Append(c == 'I' ? 'i' : c == 'J' ? 'j' : '\u012F').Append('\u0307');
                        break;
                    case 0xCC:
                        sb.Append("i\u0307\u0300");
                        break;
                    case 0xCD:
                        sb.Append("i\u0307\u0301");
                        break;
                    case 0x128:
                        sb.Append("i\u0307\u0303");
                        break;
                    default:
                        Append(sb, c);
                        break;
                }
            }
        }

        return sb.ToString();
    }

    // Soft_Dotted (PropList.txt, Unicode 17).
    private static bool IsSoftDotted(int c) =>
        c is 'i' or 'j' or 0x12F or 0x249 or 0x268 or 0x29D or 0x2B2 or 0x3F3 or 0x456 or 0x458 or 0x1D62 or 0x1D96 or 0x1DA4 or 0x1DA8 or 0x1E2D or 0x1ECB or 0x2071
            or 0x2148 or 0x2149 or 0x2C7C or 0x1D422 or 0x1D423 or 0x1D456 or 0x1D457 or 0x1D48A or 0x1D48B or 0x1D4BE or 0x1D4BF or 0x1D4F2 or 0x1D4F3
            or 0x1D526 or 0x1D527 or 0x1D55A or 0x1D55B or 0x1D58E or 0x1D58F or 0x1D5C2 or 0x1D5C3 or 0x1D5F6 or 0x1D5F7 or 0x1D62A or 0x1D62B
            or 0x1D65E or 0x1D65F or 0x1D692 or 0x1D693 or 0x1DF1A or 0x1E04C or 0x1E04D or 0x1E068;

    private static string UpperTailoring(string s, string language)
    {
        int[] cps = CodePoints(s);
        var sb = new StringBuilder(s.Length + 2);
        for (int i = 0; i < cps.Length; i++)
        {
            int c = cps[i];
            if (language is "tr" or "az")
            {
                Append(sb, c == 'i' ? 0x130 : c);
            }
            else if (c == 0x307 && AfterSoftDotted(cps, i))
            {
                // After_Soft_Dotted: the dot above is part of the capital letter's form and is dropped.
            }
            else
            {
                Append(sb, c);
            }
        }

        return sb.ToString();
    }

    private static bool AfterSoftDotted(int[] cps, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (IsSoftDotted(cps[i]))
            {
                return true;
            }

            if (Ccc(cps[i]) is 0 or 230)
            {
                return false;
            }
        }

        return false;
    }
}
