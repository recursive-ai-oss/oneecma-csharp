using System.Numerics;
using RecursiveAI.OneEcma.Intl;
using RecursiveAI.OneEcma.Intl.Data;
using RecursiveAI.OneEcma.TextServices;

namespace RecursiveAI.OneEcma;

internal static partial class IntlJs
{
    // ---- ToIntlMathematicalValue (ECMA-402 16.5.?) ------------------------------------------------------------------

    /// <summary>ToIntlMathematicalValue: numbers by their shortest decimal, BigInts and numeric strings exactly.</summary>
    internal static DecimalNumber ToIntlMathematicalValue(JsValue v)
    {
        JsValue p = Ops.ToPrimitive(v, "number");
        if (p.IsBigInt)
        {
            return DecimalNumber.FromBigInteger((BigInteger)p.Ref!);
        }

        if (p.IsString)
        {
            string s = p.AsString;
            return Svc(() => DecimalNumber.ParseNumericString(s));
        }

        return DecimalNumber.FromDouble(Ops.ToNumber(p));
    }

    // ---- Intl.Collator (10) -----------------------------------------------------------------------------------------

    private sealed record CollatorState(TextCollator Collator, string Locale, string Usage, string Sensitivity, bool IgnorePunctuation, string Collation, bool Numeric, string CaseFirst);

    private static void SetupCollator(Realm r, JsObject intl)
    {
        MakeCtor(r, intl, "Collator", 0, true, (t, a, nt, called) =>
        {
            JsIntl c = Allocate(r, nt, "Collator");
            c.Data = CreateCollator(Locales(A(a, 0)), A(a, 1));
            return JsValue.FromObject(c);
        }, out JsObject proto);

        // 10.3.3 get compare: the bound function is cached on the instance.
        r.Getter(proto, "compare", "compare", (t, a, nt) =>
        {
            JsIntl c = This(t, "Collator", "compare");
            c.Bound ??= new NativeFunction(r, string.Empty, 2, (t2, a2, nt2) =>
            {
                string x = Ops.ToStr(A(a2, 0));
                string y = Ops.ToStr(A(a2, 1));
                return Ops.Num(Math.Sign(((CollatorState)c.Data!).Collator.Compare(x, y)));
            });
            return JsValue.FromObject(c.Bound);
        });
        r.Method(proto, "resolvedOptions", 0, (t, a, nt) =>
        {
            var s = (CollatorState)This(t, "Collator", "resolvedOptions").Data!;
            var o = r.NewObject();
            Put(o, "locale", s.Locale);
            Put(o, "usage", s.Usage);
            Put(o, "sensitivity", s.Sensitivity);
            Put(o, "ignorePunctuation", s.IgnorePunctuation);
            Put(o, "collation", s.Collation);
            Put(o, "numeric", s.Numeric);
            Put(o, "caseFirst", s.CaseFirst);
            return JsValue.FromObject(o);
        });
    }

    private static CollatorState CreateCollator(List<string> requested, JsValue optionsArg)
    {
        var o = new Opts(CoerceOptions(optionsArg), "Collator");
        string usage = o.Str("usage", "sort", "search") ?? "sort";
        string? matcher = o.Str("localeMatcher", "lookup", "best fit");
        _ = matcher;
        string? collation = TypeOption(o, "collation");
        bool? numeric = o.Bool("numeric");
        string? caseFirst = o.Str("caseFirst", "upper", "lower", "false");

        var keyOptions = new Dictionary<string, string?>
        {
            ["co"] = collation,
            ["kn"] = numeric is null ? null : numeric.Value ? "true" : "false",
            ["kf"] = caseFirst,
        };
        ResolvedLocale rl = Svc(() => LocaleResolver.Resolve(requested, ["co", "kf", "kn"], keyOptions, (locale, key) => key switch
        {
            "co" => [string.Empty, .. CollationTypes(locale)],
            "kn" => ["false", "true"],
            _ => ["false", "lower", "upper"],
        }, DefaultLocale()));

        string co = rl.Keys["co"];
        string defaultSensitivity = "variant";
        string sensitivity = o.Str("sensitivity", "base", "accent", "case", "variant") ?? defaultSensitivity;
        bool ignorePunctuation = o.Bool("ignorePunctuation") ?? false;
        bool kn = rl.Keys["kn"] == "true";
        string kf = rl.Keys["kf"];

        var options = new CollatorOptions
        {
            Usage = usage == "search" ? CollationUsage.Search : CollationUsage.Sort,
            Collation = co.Length == 0 ? null : co,
            Numeric = kn,
            CaseFirst = kf switch { "upper" => CaseFirst.Upper, "lower" => CaseFirst.Lower, _ => CaseFirst.Off },
            IgnorePunctuation = ignorePunctuation,
        }.WithSensitivity(sensitivity);
        TextCollator collator = TextCollator.Create(rl.DataLocale, options);
        return new CollatorState(collator, rl.Locale, usage, sensitivity, ignorePunctuation, co.Length == 0 ? "default" : co, kn, kf);
    }

    private static readonly Dictionary<string, SortedSet<string>> CollationCache = new(StringComparer.Ordinal);

    /// <summary>The collation types a locale's language has usable tailorings for, as BCP 47 type names (standard and search excluded).</summary>
    private static SortedSet<string> CollationTypes(string locale)
    {
        string lang = locale.Split('-')[0];
        lock (CollationCache)
        {
            if (CollationCache.TryGetValue(lang, out SortedSet<string>? cached))
            {
                return cached;
            }

            var list = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string type in CldrData.Supplemental.GetKeys("collationTailoring", lang))
            {
                if (type is "standard" or "search")
                {
                    continue;
                }

                string bcp = type switch { "phonebook" => "phonebk", "traditional" => "trad", _ => type };
                try
                {
                    _ = TextCollator.Create(lang, new CollatorOptions { Collation = bcp });
                    list.Add(bcp);
                }
                catch (FormatException)
                {
                    // A tailoring whose rules import data the engine does not carry is not offered.
                }
            }

            CollationCache[lang] = list;
            return list;
        }
    }

    // ---- Intl.NumberFormat (16) -------------------------------------------------------------------------------------

    private static readonly int[] AllowedIncrements = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000];

    private static int ConvNum(JsValue v, string prop, int min, int max, int fallback)
    {
        if (v.IsUndefined)
        {
            return fallback;
        }

        double d = Ops.ToNumber(v);
        if (double.IsNaN(d) || d < min || d > max)
        {
            throw Errors.Range($"{prop} value is out of range.");
        }

        return (int)Math.Floor(d);
    }

    /// <summary>SetNumberFormatDigitOptions (ECMA-402 16.1.?): reads the digit and rounding options in the observable order.</summary>
    internal static DigitOptionInputs ReadDigitOptions(Opts o, string notation)
    {
        int? mnid = o.Num("minimumIntegerDigits", 1, 21);
        JsValue mnfdV = o.Raw("minimumFractionDigits");
        JsValue mxfdV = o.Raw("maximumFractionDigits");
        JsValue mnsdV = o.Raw("minimumSignificantDigits");
        JsValue mxsdV = o.Raw("maximumSignificantDigits");
        int? increment = o.Num("roundingIncrement", 1, 5000);
        if (increment is { } inc && Array.IndexOf(AllowedIncrements, inc) < 0)
        {
            throw Errors.Range("roundingIncrement value is out of range.");
        }

        string? mode = o.Str("roundingMode", DigitOptions.RoundingModeNames);
        string? priority = o.Str("roundingPriority", "auto", "morePrecision", "lessPrecision");
        string? tzd = o.Str("trailingZeroDisplay", "auto", "stripIfInteger");

        bool hasSd = !mnsdV.IsUndefined || !mxsdV.IsUndefined;
        bool hasFd = !mnfdV.IsUndefined || !mxfdV.IsUndefined;
        bool needSd = true, needFd = true;
        if ((priority ?? "auto") == "auto")
        {
            needSd = hasSd;
            if (needSd || (!hasFd && notation == "compact"))
            {
                needFd = false;
            }
        }

        int? mnsd = null, mxsd = null, mnfd = null, mxfd = null;
        if (needSd && hasSd)
        {
            int lo = ConvNum(mnsdV, "minimumSignificantDigits", 1, 21, 1);
            mnsd = lo;
            mxsd = ConvNum(mxsdV, "maximumSignificantDigits", lo, 21, 21);
        }

        if (needFd && hasFd)
        {
            int lo = ConvNum(mnfdV, "minimumFractionDigits", 0, 100, -1);
            int hi = ConvNum(mxfdV, "maximumFractionDigits", 0, 100, -1);
            mnfd = lo < 0 ? null : lo;
            mxfd = hi < 0 ? null : hi;
        }

        return new DigitOptionInputs
        {
            MinimumIntegerDigits = mnid,
            MinimumFractionDigits = mnfd,
            MaximumFractionDigits = mxfd,
            MinimumSignificantDigits = mnsd,
            MaximumSignificantDigits = mxsd,
            RoundingIncrement = increment,
            RoundingMode = mode,
            RoundingPriority = priority,
            TrailingZeroDisplay = tzd,
        };
    }

    private static NumberFormatService CreateNumberFormat(List<string> requested, JsValue optionsArg)
    {
        var o = new Opts(CoerceOptions(optionsArg), "NumberFormat");
        string? matcher = o.Str("localeMatcher", "lookup", "best fit");
        string? nu = o.Str("numberingSystem");
        if (nu is not null)
        {
            Svc(() => LocaleResolver.ValidateNumberingSystem(nu, "NumberFormat"));
        }

        // SetNumberFormatUnitOptions
        string? style = o.Str("style", "decimal", "percent", "currency", "unit");
        string? currency = o.Str("currency");
        if (currency is null)
        {
            if (style == "currency")
            {
                throw Errors.Type("Currency code is required with currency style.");
            }
        }
        else if (currency.Length != 3 || !currency.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'))
        {
            throw Errors.Range($"Invalid currency code : {currency}");
        }

        string? currencyDisplay = o.Str("currencyDisplay", "code", "symbol", "narrowSymbol", "name");
        string? currencySign = o.Str("currencySign", "standard", "accounting");
        string? unit = o.Str("unit");
        if (unit is null)
        {
            if (style == "unit")
            {
                throw Errors.Type("Unit is required with unit style.");
            }
        }
        else if (!NumberFormatService.IsWellFormedUnit(unit))
        {
            throw Errors.Range($"Invalid unit argument for Intl.NumberFormat() '{unit}'");
        }

        string? unitDisplay = o.Str("unitDisplay", "short", "narrow", "long");
        string? notation = o.Str("notation", "standard", "scientific", "engineering", "compact");
        DigitOptionInputs digits = ReadDigitOptions(o, notation ?? "standard");
        string? compactDisplay = o.Str("compactDisplay", "short", "long");

        // GetBooleanOrStringNumberFormatOption (9.2.12)
        string? useGrouping = null;
        bool? useGroupingFlag = null;
        JsValue ug = o.Raw("useGrouping");
        if (!ug.IsUndefined)
        {
            if (ug.IsTrue)
            {
                useGroupingFlag = true;
            }
            else if (!Ops.ToBoolean(ug))
            {
                useGroupingFlag = false;
            }
            else
            {
                string s = Ops.ToStr(ug);
                if (Array.IndexOf(["min2", "auto", "always", "true", "false"], s) < 0)
                {
                    throw Errors.Range($"Value {s} out of range for Intl.NumberFormat options property useGrouping");
                }

                useGrouping = s;
            }
        }

        string? signDisplay = o.Str("signDisplay", "auto", "never", "always", "exceptZero", "negative");

        var options = new NumberFormatOptions
        {
            LocaleMatcher = matcher,
            NumberingSystem = nu,
            Style = style,
            Currency = currency,
            CurrencyDisplay = currencyDisplay,
            CurrencySign = currencySign,
            Unit = unit,
            UnitDisplay = unitDisplay,
            Notation = notation,
            CompactDisplay = compactDisplay,
            UseGrouping = useGrouping,
            UseGroupingFlag = useGroupingFlag,
            SignDisplay = signDisplay,
            Digits = digits,
        };
        return Svc(() => NumberFormatService.Create(requested, options, DefaultLocale()));
    }

    /// <summary>Adds the digit-option rows of a resolvedOptions table (Table 28 / 32 order: integer, fraction, significant digits).</summary>
    private static void PutDigits(JsObject o, DigitOptions d)
    {
        Put(o, "minimumIntegerDigits", d.MinimumIntegerDigits);
        if (d.UsesFractionDigits)
        {
            Put(o, "minimumFractionDigits", d.MinimumFractionDigits);
            Put(o, "maximumFractionDigits", d.MaximumFractionDigits);
        }

        if (d.UsesSignificantDigits)
        {
            Put(o, "minimumSignificantDigits", d.MinimumSignificantDigits);
            Put(o, "maximumSignificantDigits", d.MaximumSignificantDigits);
        }
    }

    private static void PutRounding(JsObject o, DigitOptions d)
    {
        Put(o, "roundingIncrement", d.RoundingIncrement);
        Put(o, "roundingMode", d.RoundingModeName);
        Put(o, "roundingPriority", d.RoundingPriority);
        Put(o, "trailingZeroDisplay", d.TrailingZeroDisplay);
    }

    /// <summary>UnwrapNumberFormat / UnwrapDateTimeFormat (ECMA-402 16.5.9): the legacy constructor stores the real instance on the plain object.</summary>
    private static JsIntl Unwrap(Realm r, JsValue t, string kind, string method)
    {
        if (t.Ref is JsIntl o && o.Kind == kind)
        {
            return o;
        }

        if (t.Ref is JsObject obj && Ops.OrdinaryHasInstance(JsValue.FromObject(r.Intrinsics["%Intl." + kind + "%"]), t))
        {
            JsValue inner = obj.Get(new PropertyKey(FallbackSymbol(r)));
            if (inner.Ref is JsIntl o2 && o2.Kind == kind)
            {
                return o2;
            }
        }

        throw Incompat("Intl." + kind + ".prototype." + method, t);
    }

    /// <summary>ChainNumberFormat / the DateTimeFormat equivalent: a plain call on an instance of the constructor hides the new object on it.</summary>
    private static JsValue Chain(Realm r, JsIntl created, string kind, JsValue thisValue, bool called)
    {
        if (called && Ops.OrdinaryHasInstance(JsValue.FromObject(r.Intrinsics["%Intl." + kind + "%"]), thisValue))
        {
            var thisObj = (JsObject)thisValue.Ref!;
            thisObj.DefinePropertyOrThrow(new PropertyKey(FallbackSymbol(r)), PropertyDescriptor.Data(JsValue.FromObject(created), PropFlags.None));
            return thisValue;
        }

        return JsValue.FromObject(created);
    }

    private static JsValue PartsArray(Realm r, IEnumerable<NumberPart> parts, bool withSource)
    {
        var list = new List<JsValue>();
        foreach (NumberPart p in parts)
        {
            list.Add(JsValue.FromObject(withSource ? Part(r, p.Type, p.Value, ("source", p.Source)) : Part(r, p.Type, p.Value)));
        }

        return JsValue.FromObject(Ops.CreateArray(list));
    }

    private static void SetupNumberFormat(Realm r, JsObject intl)
    {
        MakeCtor(r, intl, "NumberFormat", 0, true, (t, a, nt, called) =>
        {
            JsIntl nf = Allocate(r, nt, "NumberFormat");
            nf.Data = CreateNumberFormat(Locales(A(a, 0)), A(a, 1));
            return Chain(r, nf, "NumberFormat", t, called);
        }, out JsObject proto);

        r.Getter(proto, "format", "format", (t, a, nt) =>
        {
            JsIntl nf = Unwrap(r, t, "NumberFormat", "format");
            nf.Bound ??= new NativeFunction(r, string.Empty, 1, (t2, a2, nt2) =>
            {
                DecimalNumber x = ToIntlMathematicalValue(A(a2, 0));
                return Ops.Str(Svc(() => ((NumberFormatService)nf.Data!).Format(x)));
            });
            return JsValue.FromObject(nf.Bound);
        });
        r.Method(proto, "formatToParts", 1, (t, a, nt) =>
        {
            var nf = (NumberFormatService)This(t, "NumberFormat", "formatToParts").Data!;
            DecimalNumber x = ToIntlMathematicalValue(A(a, 0));
            return PartsArray(r, Svc(() => nf.FormatToParts(x)), false);
        });
        r.Method(proto, "formatRange", 2, (t, a, nt) =>
        {
            var nf = (NumberFormatService)This(t, "NumberFormat", "formatRange").Data!;
            (DecimalNumber x, DecimalNumber y) = RangeArgs(A(a, 0), A(a, 1));
            return Ops.Str(Svc(() => nf.FormatRange(x, y)));
        });
        r.Method(proto, "formatRangeToParts", 2, (t, a, nt) =>
        {
            var nf = (NumberFormatService)This(t, "NumberFormat", "formatRangeToParts").Data!;
            (DecimalNumber x, DecimalNumber y) = RangeArgs(A(a, 0), A(a, 1));
            return PartsArray(r, Svc(() => nf.FormatRangeToParts(x, y)), true);
        });
        r.Method(proto, "resolvedOptions", 0, (t, a, nt) =>
        {
            var nf = (NumberFormatService)Unwrap(r, t, "NumberFormat", "resolvedOptions").Data!;
            var o = r.NewObject();
            Put(o, "locale", nf.Locale);
            Put(o, "numberingSystem", nf.NumberingSystem);
            Put(o, "style", nf.Style);
            Put(o, "currency", nf.Style == "currency" ? nf.Currency : null);
            Put(o, "currencyDisplay", nf.Style == "currency" ? nf.CurrencyDisplay : null);
            Put(o, "currencySign", nf.Style == "currency" ? nf.CurrencySign : null);
            Put(o, "unit", nf.Style == "unit" ? nf.Unit : null);
            Put(o, "unitDisplay", nf.Style == "unit" ? nf.UnitDisplay : null);
            PutDigits(o, nf.Digits);
            if (nf.UseGrouping == "false")
            {
                Put(o, "useGrouping", JsValue.False);
            }
            else
            {
                Put(o, "useGrouping", nf.UseGrouping);
            }

            Put(o, "notation", nf.Notation);
            Put(o, "compactDisplay", nf.Notation == "compact" ? nf.CompactDisplay : null);
            Put(o, "signDisplay", nf.SignDisplay);
            PutRounding(o, nf.Digits);
            return JsValue.FromObject(o);
        });
    }

    private static (DecimalNumber, DecimalNumber) RangeArgs(JsValue start, JsValue end)
    {
        if (start.IsUndefined || end.IsUndefined)
        {
            throw Errors.Type("Start or end of range is undefined");
        }

        DecimalNumber x = ToIntlMathematicalValue(start);
        DecimalNumber y = ToIntlMathematicalValue(end);
        if (x.IsNaN || y.IsNaN)
        {
            throw Errors.Range("Invalid number value: NaN");
        }

        return (x, y);
    }

    // ---- Intl.PluralRules (17) --------------------------------------------------------------------------------------

    private static void SetupPluralRules(Realm r, JsObject intl)
    {
        MakeCtor(r, intl, "PluralRules", 0, false, (t, a, nt, called) =>
        {
            JsIntl pr = Allocate(r, nt, "PluralRules");
            List<string> requested = Locales(A(a, 0));
            var o = new Opts(CoerceOptions(A(a, 1)), "PluralRules");
            string? matcher = o.Str("localeMatcher", "lookup", "best fit");
            string? type = o.Str("type", "cardinal", "ordinal");
            string? notation = o.Str("notation", "standard", "scientific", "engineering", "compact");
            string? compactDisplay = o.Str("compactDisplay", "short", "long");
            DigitOptionInputs digits = ReadDigitOptions(o, notation ?? "standard");
            var options = new PluralRulesOptions { LocaleMatcher = matcher, Type = type, Notation = notation, CompactDisplay = compactDisplay, Digits = digits };
            pr.Data = Svc(() => PluralRulesService.Create(requested, options, DefaultLocale()));
            return JsValue.FromObject(pr);
        }, out JsObject proto);

        r.Method(proto, "select", 1, (t, a, nt) =>
        {
            var pr = (PluralRulesService)This(t, "PluralRules", "select").Data!;
            DecimalNumber n = ToIntlMathematicalValue(A(a, 0));
            return Ops.Str(Svc(() => pr.Select(n)));
        });
        r.Method(proto, "selectRange", 2, (t, a, nt) =>
        {
            var pr = (PluralRulesService)This(t, "PluralRules", "selectRange").Data!;
            (DecimalNumber x, DecimalNumber y) = RangeArgs(A(a, 0), A(a, 1));
            return Ops.Str(Svc(() => pr.SelectRange(x, y)));
        });
        r.Method(proto, "resolvedOptions", 0, (t, a, nt) =>
        {
            var pr = (PluralRulesService)This(t, "PluralRules", "resolvedOptions").Data!;
            var o = r.NewObject();
            Put(o, "locale", pr.Locale);
            Put(o, "type", pr.Type);
            Put(o, "notation", pr.Notation);
            Put(o, "compactDisplay", pr.CompactDisplay);
            PutDigits(o, pr.Digits);
            var cats = new List<JsValue>();
            foreach (string c in pr.PluralCategories)
            {
                cats.Add(Ops.Str(c));
            }

            Put(o, "pluralCategories", JsValue.FromObject(Ops.CreateArray(cats)));
            PutRounding(o, pr.Digits);
            return JsValue.FromObject(o);
        });
    }
}
