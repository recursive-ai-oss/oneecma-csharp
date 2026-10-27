using RecursiveAI.OneEcma.Intl;
using RecursiveAI.OneEcma.TextServices;

namespace RecursiveAI.OneEcma;

internal static partial class IntlJs
{
    // ---- Intl.ListFormat (14) ---------------------------------------------------------------------------------------

    private static void SetupListFormat(Realm r, JsObject intl)
    {
        MakeCtor(r, intl, "ListFormat", 0, false, (t, a, nt, called) =>
        {
            JsIntl lf = Allocate(r, nt, "ListFormat");
            List<string> requested = Locales(A(a, 0));
            var o = new Opts(GetOptionsObject(A(a, 1)), "ListFormat");
            string? matcher = o.Str("localeMatcher", "lookup", "best fit");
            string? type = o.Str("type", "conjunction", "disjunction", "unit");
            string? style = o.Str("style", "long", "short", "narrow");
            lf.Data = Svc(() => ListFormatService.Create(requested, type, style, DefaultLocale(), matcher));
            return JsValue.FromObject(lf);
        }, out JsObject proto);

        r.Method(proto, "format", 1, (t, a, nt) =>
        {
            var lf = (ListFormatService)This(t, "ListFormat", "format").Data!;
            List<string> list = StringListFromIterable(A(a, 0));
            return Ops.Str(Svc(() => lf.Format(list)));
        });
        r.Method(proto, "formatToParts", 1, (t, a, nt) =>
        {
            var lf = (ListFormatService)This(t, "ListFormat", "formatToParts").Data!;
            List<string> list = StringListFromIterable(A(a, 0));
            var parts = new List<JsValue>();
            foreach (ListPart p in Svc(() => lf.FormatToParts(list)))
            {
                parts.Add(JsValue.FromObject(Part(r, p.Type, p.Value)));
            }

            return JsValue.FromObject(Ops.CreateArray(parts));
        });
        r.Method(proto, "resolvedOptions", 0, (t, a, nt) =>
        {
            var lf = (ListFormatService)This(t, "ListFormat", "resolvedOptions").Data!;
            var o = r.NewObject();
            Put(o, "locale", lf.Locale);
            Put(o, "type", lf.Type);
            Put(o, "style", lf.Style);
            return JsValue.FromObject(o);
        });
    }

    /// <summary>StringListFromIterable (ECMA-402 14.5.?): every element must be a String.</summary>
    private static List<string> StringListFromIterable(JsValue iterable)
    {
        var list = new List<string>();
        if (iterable.IsUndefined)
        {
            return list;
        }

        IteratorRecord rec = Ops.GetIterator(iterable);
        while (Ops.IteratorStepValue(rec, out JsValue v))
        {
            Agent.Current.Tick();
            if (!v.IsString)
            {
                Ops.IteratorCloseSilently(rec);
                throw Errors.Type($"Iterable yielded {Ops.DisplayValue(v)} which is not a string");
            }

            list.Add(v.AsString);
        }

        return list;
    }

    // ---- Intl.RelativeTimeFormat (18) -------------------------------------------------------------------------------

    private static void SetupRelativeTimeFormat(Realm r, JsObject intl)
    {
        MakeCtor(r, intl, "RelativeTimeFormat", 0, false, (t, a, nt, called) =>
        {
            JsIntl rtf = Allocate(r, nt, "RelativeTimeFormat");
            List<string> requested = Locales(A(a, 0));
            var o = new Opts(CoerceOptions(A(a, 1)), "RelativeTimeFormat");
            string? matcher = o.Str("localeMatcher", "lookup", "best fit");
            string? nu = o.Str("numberingSystem");
            if (nu is not null)
            {
                Svc(() => LocaleResolver.ValidateNumberingSystem(nu, "RelativeTimeFormat"));
            }

            string? style = o.Str("style", "long", "short", "narrow");
            string? numeric = o.Str("numeric", "always", "auto");
            rtf.Data = Svc(() => RelativeTimeFormatService.Create(requested, style, numeric, DefaultLocale(), matcher, nu));
            return JsValue.FromObject(rtf);
        }, out JsObject proto);

        r.Method(proto, "format", 2, (t, a, nt) =>
        {
            var rtf = (RelativeTimeFormatService)This(t, "RelativeTimeFormat", "format").Data!;
            double value = Ops.ToNumber(A(a, 0));
            string unit = Ops.ToStr(A(a, 1));
            return Ops.Str(Svc(() => rtf.Format(DecimalNumber.FromDouble(value), unit)));
        });
        r.Method(proto, "formatToParts", 2, (t, a, nt) =>
        {
            var rtf = (RelativeTimeFormatService)This(t, "RelativeTimeFormat", "formatToParts").Data!;
            double value = Ops.ToNumber(A(a, 0));
            string unit = Ops.ToStr(A(a, 1));
            var parts = new List<JsValue>();
            foreach (RelativeTimePart p in Svc(() => rtf.FormatToParts(DecimalNumber.FromDouble(value), unit)))
            {
                parts.Add(JsValue.FromObject(Part(r, p.Type, p.Value, ("unit", p.Unit))));
            }

            return JsValue.FromObject(Ops.CreateArray(parts));
        });
        r.Method(proto, "resolvedOptions", 0, (t, a, nt) =>
        {
            var rtf = (RelativeTimeFormatService)This(t, "RelativeTimeFormat", "resolvedOptions").Data!;
            var o = r.NewObject();
            Put(o, "locale", rtf.Locale);
            Put(o, "style", rtf.Style);
            Put(o, "numeric", rtf.Numeric);
            Put(o, "numberingSystem", RelativeTimeFormatService.NumberingSystem);
            return JsValue.FromObject(o);
        });
    }

    // ---- Intl.DisplayNames (12) -------------------------------------------------------------------------------------

    private static void SetupDisplayNames(Realm r, JsObject intl)
    {
        MakeCtor(r, intl, "DisplayNames", 2, false, (t, a, nt, called) =>
        {
            JsIntl dn = Allocate(r, nt, "DisplayNames");
            List<string> requested = Locales(A(a, 0));
            if (A(a, 1).IsUndefined)
            {
                throw Errors.Type("Required option \"type\" is missing");
            }

            var o = new Opts(GetOptionsObject(A(a, 1)), "DisplayNames");
            string? matcher = o.Str("localeMatcher", "lookup", "best fit");
            string? style = o.Str("style", "narrow", "short", "long");
            string? type = o.Str("type", "language", "region", "script", "currency", "calendar", "dateTimeField");
            if (type is null)
            {
                throw Errors.Type("Required option \"type\" is missing");
            }

            string? fallback = o.Str("fallback", "code", "none");
            string? languageDisplay = o.Str("languageDisplay", "dialect", "standard");
            var options = new DisplayNamesOptions { LocaleMatcher = matcher, Style = style, Type = type, Fallback = fallback, LanguageDisplay = languageDisplay };
            dn.Data = Svc(() => DisplayNamesService.Create(requested, options, DefaultLocale()));
            return JsValue.FromObject(dn);
        }, out JsObject proto);

        r.Method(proto, "of", 1, (t, a, nt) =>
        {
            var dn = (DisplayNamesService)This(t, "DisplayNames", "of").Data!;
            string code = Ops.ToStr(A(a, 0));
            string? name = Svc(() => dn.Of(code));
            return name is null ? default : Ops.Str(name);
        });
        r.Method(proto, "resolvedOptions", 0, (t, a, nt) =>
        {
            var dn = (DisplayNamesService)This(t, "DisplayNames", "resolvedOptions").Data!;
            var o = r.NewObject();
            Put(o, "locale", dn.Locale);
            Put(o, "style", dn.Style);
            Put(o, "type", dn.Type);
            Put(o, "fallback", dn.Fallback);
            Put(o, "languageDisplay", dn.LanguageDisplay);
            return JsValue.FromObject(o);
        });
    }

    // ---- Intl.Segmenter (19) ----------------------------------------------------------------------------------------

    private sealed record SegmentsState(string Input, SegmentGranularity Granularity);

    private sealed class SegmentIteratorState(string input, SegmentGranularity granularity)
    {
        internal string Input = input;
        internal SegmentGranularity Granularity = granularity;
        internal int Position;
    }

    private static JsValue SegmentData(Realm r, string input, SegmentGranularity g, int start, int end, bool wordLike)
    {
        var o = r.NewObject();
        Put(o, "segment", input.Substring(start, end - start));
        Put(o, "index", Ops.Num(start));
        Put(o, "input", input);
        if (g == SegmentGranularity.Word)
        {
            Put(o, "isWordLike", Ops.Bool(wordLike));
        }

        return JsValue.FromObject(o);
    }

    private static void SetupSegmenter(Realm r, JsObject intl)
    {
        MakeCtor(r, intl, "Segmenter", 0, false, (t, a, nt, called) =>
        {
            JsIntl sg = Allocate(r, nt, "Segmenter");
            List<string> requested = Locales(A(a, 0));
            var o = new Opts(GetOptionsObject(A(a, 1)), "Segmenter");
            string? matcher = o.Str("localeMatcher", "lookup", "best fit");
            _ = matcher;
            string granularity = o.Str("granularity", "grapheme", "word", "sentence") ?? "grapheme";
            ResolvedLocale rl = Svc(() => LocaleResolver.Resolve(requested, [], new Dictionary<string, string?>(), (_, _) => [], DefaultLocale()));
            sg.Data = rl.Locale;
            sg.Extra = granularity;
            return JsValue.FromObject(sg);
        }, out JsObject proto);

        var segmentsProto = r.NewObject();
        var iterProto = new JsObject(r.IteratorPrototype);
        r.Intrinsics["%SegmentsPrototype%"] = segmentsProto;
        r.Intrinsics["%SegmentIteratorPrototype%"] = iterProto;

        r.Method(proto, "resolvedOptions", 0, (t, a, nt) =>
        {
            JsIntl sg = This(t, "Segmenter", "resolvedOptions");
            var o = r.NewObject();
            Put(o, "locale", (string)sg.Data!);
            Put(o, "granularity", (string)sg.Extra!);
            return JsValue.FromObject(o);
        });
        r.Method(proto, "segment", 1, (t, a, nt) =>
        {
            JsIntl sg = This(t, "Segmenter", "segment");
            string s = Ops.ToStr(A(a, 0));
            var segs = new JsIntl(segmentsProto, "Segments") { Data = new SegmentsState(s, GranularityOf((string)sg.Extra!)) };
            return JsValue.FromObject(segs);
        });

        r.Method(segmentsProto, "containing", 1, (t, a, nt) =>
        {
            var st = (SegmentsState)This(t, "Segments", "containing").Data!;
            double n = Ops.ToIntegerOrInfinity(A(a, 0));
            if (n < 0 || n >= st.Input.Length)
            {
                return default;
            }

            if (!TextSegmenter.TryFindSegment(st.Input, (int)n, st.Granularity, out int start, out int end, out bool wordLike))
            {
                return default;
            }

            return SegmentData(r, st.Input, st.Granularity, start, end, wordLike);
        });
        r.Method(segmentsProto, WellKnown.Iterator, "[Symbol.iterator]", 0, (t, a, nt) =>
        {
            var st = (SegmentsState)This(t, "Segments", "[Symbol.iterator]").Data!;
            var it = new JsIntl(iterProto, "SegmentIterator") { Data = new SegmentIteratorState(st.Input, st.Granularity) };
            return JsValue.FromObject(it);
        });

        r.Method(iterProto, "next", 0, (t, a, nt) =>
        {
            var st = (SegmentIteratorState)This(t, "SegmentIterator", "next").Data!;
            if (st.Position >= st.Input.Length)
            {
                return JsValue.FromObject(Ops.CreateIterResult(default, true));
            }

            Agent.Current.Tick();
            int start = st.Position;
            int end = TextSegmenter.NextBoundary(st.Input, start, st.Granularity, out bool wordLike);
            st.Position = end;
            return JsValue.FromObject(Ops.CreateIterResult(SegmentData(r, st.Input, st.Granularity, start, end, wordLike), false));
        });
        iterProto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Segmenter String Iterator"), PropFlags.Configurable);
    }

    private static SegmentGranularity GranularityOf(string g) => g switch
    {
        "word" => SegmentGranularity.Word,
        "sentence" => SegmentGranularity.Sentence,
        _ => SegmentGranularity.Grapheme,
    };
}
