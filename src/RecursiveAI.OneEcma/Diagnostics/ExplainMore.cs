using System.Globalization;
using System.Numerics;
using System.Text;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Diagnostics;

// Explanations for errors a built-in throws on bad input: an empty reduce, BigInt mixing, destructuring a missing value,
// an Invalid Date and JSON text. Like the rest of the explainer they read live values and never run script.
internal static partial class Explain
{
    /// <summary>The members (own and inherited, enumerable or not) a value would answer to, nearest first; accessors are skipped.</summary>
    private static List<(string Name, JsValue Value)> Members(JsValue v, Realm realm)
    {
        var result = new List<(string, JsValue)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsObject? start = v.Ref switch
        {
            JsObject obj => obj,
            string or Rope => realm.StringPrototype,
            _ => v.IsNumber ? realm.NumberPrototype : v.IsBoolean ? realm.BooleanPrototype : null,
        };
        int guard = 0;
        for (JsObject? o = start; o is not null && guard++ < 16 && o is not ProxyObject; o = o.Proto)
        {
            if (o is JsTypedArray || o is JsArray { Length: > 10_000 })
            {
                continue;
            }

            var keys = new List<PropertyKey>();
            o.OwnKeys(keys);
            foreach (PropertyKey k in keys)
            {
                if (k.String is { } s && !k.TryGetIndex(out _) && seen.Add(s) && o.GetOwnProperty(k, out PropertyDescriptor d) && !d.IsAccessor)
                {
                    result.Add((s, d.Value));
                    if (result.Count >= 600)
                    {
                        return result;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Where the value at the end of <paramref name="p"/> was written: the binding for a bare name, else the property store.</summary>
    private static Origin? OriginOf(Path p, Eval ev, TraceState trace) =>
        p.Keys.Count == 0
            ? ev.RootOrigin
            : ev.OwnerObject is { } oo && ev.LastKey is { } lk && !ev.Absent ? trace.PropertyOrigin(oo, lk) : null;

    private static void AddOriginOf(Diagnostic d, Path p, Eval ev, TraceState trace, bool valueIsTheProblem)
    {
        if (OriginOf(p, ev, trace) is { } o)
        {
            AddOrigin(d, Pretty(p, p.Keys.Count), o, ev.Value, valueIsTheProblem);
        }
    }

    private static string SourceOf(Diagnostic d, Origin o) => o.End <= d.Source.Text.Length && o.Start >= 0 && o.End >= o.Start ? d.Source.Text[o.Start..o.End] : string.Empty;

    // ---- reduce of an empty array ----------------------------------------------------------------

    private static void ReduceOfEmpty(Diagnostic d, StackFrameInfo frame, TraceState trace, string expr, Span primary)
    {
        // The primary span is the callee; the argument list follows it in the source.
        string t = d.Source.Text;
        int calleeLen = expr.IndexOf('(', StringComparison.Ordinal) is var q and >= 0 ? q : expr.Length;
        if (ParsePath(expr[..calleeLen]) is not { } callee || callee.Keys.Count == 0 || callee.Keys[^1] is not ("reduce" or "reduceRight"))
        {
            return;
        }

        int open = primary.Start + calleeLen;
        while (open < t.Length && char.IsWhiteSpace(t[open]))
        {
            open++;
        }

        var owner = new Path { Root = callee.Root, Keys = callee.Keys.GetRange(0, callee.Keys.Count - 1), Bracket = callee.Bracket.GetRange(0, callee.Bracket.Count - 1) };
        if (Evaluate(owner, frame, trace) is not { } ev)
        {
            return;
        }

        string method = callee.Keys[^1];
        string ownerText = Pretty(owner, owner.Keys.Count);
        d.PrimaryLabel = "`" + ownerText + "` is empty, so `" + method + "` has no first value to start from";
        AddOriginOf(d, owner, ev, trace, false);
        int close = open < t.Length && t[open] == '(' ? FindClose(t, open) : -1;
        string args = close > open ? t[(open + 1)..close].Trim() : string.Empty;
        string shown = args.Length is > 0 and <= 60 && !args.Contains('\n', StringComparison.Ordinal) ? args : "callback";
        d.Help.Add("pass an initial value as the second argument: `" + ownerText + "." + method + "(" + shown + ", 0)`");
        d.Help.Add("or handle the empty case first: `if (" + ownerText + ".length === 0) { ... }`");
    }

    // ---- BigInt mixed with another type ----------------------------------------------------------

    private static readonly string[] BinaryOperators =
    [
        ">>>=", "**=", "<<=", ">>=", ">>>", "&&=", "||=", "??=", "**", "<<", ">>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "+", "-", "*", "/", "%", "&", "|", "^",
    ];

    /// <summary>Splits <c>a op b</c> at its only top-level binary operator; false when there is none or more than one.</summary>
    private static bool SplitBinary(string s, out string left, out string op, out string right)
    {
        left = op = right = string.Empty;
        int depth = 0;
        int found = -1;
        string foundOp = string.Empty;
        bool afterOperand = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '"' or '\'' or '`')
            {
                int j = i + 1;
                while (j < s.Length && s[j] != c)
                {
                    j += s[j] == '\\' ? 2 : 1;
                }

                i = j;
                afterOperand = true;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
                afterOperand = false;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                afterOperand = true;
            }
            else if (depth == 0 && afterOperand && !char.IsWhiteSpace(c) && !IsIdentPart(c) && c != '.')
            {
                string? match = null;
                foreach (string candidate in BinaryOperators)
                {
                    if (string.CompareOrdinal(s, i, candidate, 0, candidate.Length) == 0)
                    {
                        match = candidate;
                        break;
                    }
                }

                if (match is null || found >= 0)
                {
                    return false;
                }

                found = i;
                foundOp = match;
                i += match.Length - 1;
                afterOperand = false;
            }
            else if (!char.IsWhiteSpace(c))
            {
                afterOperand = true;
            }
        }

        if (found < 0)
        {
            return false;
        }

        left = s[..found];
        op = foundOp;
        right = s[(found + foundOp.Length)..];
        return left.Trim().Length > 0 && right.Trim().Length > 0;
    }

    private sealed class Operand
    {
        internal string Text = "";
        internal JsValue Value;
        internal Path? Path;
        internal Eval? Eval;
    }

    private static Operand? ReadOperand(string text, StackFrameInfo frame, TraceState trace)
    {
        text = text.Trim();
        if (text.Length > 1 && text[^1] == 'n' && text[..^1].All(char.IsAsciiDigit))
        {
            return new Operand { Text = text, Value = JsValue.FromBigInt(BigInteger.Parse(text[..^1], NumberStyles.None, NumberFormatInfo.InvariantInfo)) };
        }

        if (text.Length > 0 && text.All(c => char.IsAsciiDigit(c) || c == '.') && double.TryParse(text, NumberStyles.Float, NumberFormatInfo.InvariantInfo, out double num))
        {
            return new Operand { Text = text, Value = JsValue.FromNumber(num) };
        }

        if (ParsePath(text) is { } p && Evaluate(p, frame, trace) is { } ev)
        {
            return new Operand { Text = text, Value = ev.Value, Path = p, Eval = ev };
        }

        return null;
    }

    private static void BigIntMix(Diagnostic d, StackFrameInfo frame, TraceState trace, string expr)
    {
        if (!SplitBinary(expr, out string l, out string op, out string r) || ReadOperand(l, frame, trace) is not { } a || ReadOperand(r, frame, trace) is not { } b)
        {
            return;
        }

        if (a.Value.Ref is BigInteger == b.Value.Ref is BigInteger)
        {
            return;
        }

        Operand big = a.Value.Ref is BigInteger ? a : b;
        Operand other = ReferenceEquals(big, a) ? b : a;
        string otherType = other.Value.IsNumber ? "a Number" : TypeWord(other.Value);
        d.PrimaryLabel = "`" + big.Text + "` is a BigInt but `" + other.Text + "` is " + otherType + "; arithmetic needs both to be the same type";
        foreach (Operand o in new[] { a, b })
        {
            if (o.Path is { Keys.Count: 0 } && o.Eval!.RootOrigin is not null)
            {
                AddOrigin(d, o.Path.Root, o.Eval.RootOrigin, o.Eval.Root, false);
            }
        }

        string Rebuild(string replaceBig, string replaceOther)
        {
            string lt = l.Trim();
            string rt = r.Trim();
            string bigNew = replaceBig.Length > 0 ? replaceBig : big.Text;
            string otherNew = replaceOther.Length > 0 ? replaceOther : other.Text;
            return ReferenceEquals(big, a) ? bigNew + " " + op + " " + otherNew : otherNew + " " + op + " " + bigNew;
        }

        if (other.Value.IsNumber)
        {
            double n = other.Value.Num;
            if (!double.IsNaN(n) && !double.IsInfinity(n) && Math.Floor(n) == n)
            {
                d.Help.Add("make both BigInt: `" + Rebuild(string.Empty, "BigInt(" + other.Text + ")") + "`");
            }
            else
            {
                d.Help.Add("`" + other.Text + "` is " + Ops.NumberToStr(n) + ", which is not a whole number, so `BigInt(" + other.Text + ")` would throw");
            }

            d.Help.Add("or make both Number (precision is lost above 2^53): `" + Rebuild("Number(" + big.Text + ")", string.Empty) + "`");
        }
        else
        {
            d.Help.Add("convert `" + other.Text + "` to a BigInt or Number first, so both operands have the same type");
        }
    }

    // ---- destructuring a missing value -----------------------------------------------------------

    private static void Destructure(Diagnostic d, StackFrameInfo frame, TraceState trace, string headline, string expr, Span primary)
    {
        // "Cannot destructure property 'host' of 'config.db' as it is undefined." / "Cannot destructure 'x' as it is null."
        const string AsIt = "' as it is ";
        int asIt = headline.LastIndexOf(AsIt, StringComparison.Ordinal);
        if (asIt < 0)
        {
            return;
        }

        string word = headline[(asIt + AsIt.Length)..].TrimEnd('.');
        string head = headline[..asIt];
        string? property = null;
        string sourceText;
        const string PropOf = "Cannot destructure property '";
        if (head.StartsWith(PropOf, StringComparison.Ordinal))
        {
            string rest = head[PropOf.Length..];
            int of = rest.IndexOf("' of '", StringComparison.Ordinal);
            if (of < 0)
            {
                return;
            }

            property = rest[..of];
            sourceText = rest[(of + "' of '".Length)..];
        }
        else if (head.StartsWith("Cannot destructure '", StringComparison.Ordinal))
        {
            sourceText = head["Cannot destructure '".Length..];
        }
        else
        {
            return;
        }

        if (sourceText is "undefined" or "null" && expr.StartsWith('{') && expr.EndsWith('}'))
        {
            DestructuredParameter(d, frame, word, expr, primary);
            return;
        }

        if (ParsePath(sourceText) is not { } p || Evaluate(p, frame, trace) is not { } ev || !ev.Value.IsNullish)
        {
            return;
        }

        string full = Pretty(p, p.Keys.Count);
        d.PrimaryLabel = "`" + full + "` is " + word + (property is null ? string.Empty : ", so `" + property + "` cannot be taken from it");
        if (p.Keys.Count > 0)
        {
            if (OriginOf(p, ev, trace) is { } po)
            {
                string msg = "`" + p.Keys[^1] + "` is set to " + word + " here";
                if (LabelFor(d, po, msg) is { } pl)
                {
                    d.SecondaryLabels.Add(pl);
                    d.Provenance.Add(new ProvenanceStep(pl.Span, msg));
                }
            }

            AddOrigin(d, p.Root, ev.RootOrigin, ev.Root, false);
        }
        else
        {
            AddOrigin(d, p.Root, ev.RootOrigin, ev.Root, true);
        }

        if (p.Keys.Count > 0 && ev.OwnerObject is { } owner && ev.LastKey is { } wanted && ev.Absent)
        {
            var cands = new List<(string, JsValue)>();
            foreach (string k in OwnKeys(owner))
            {
                if (TryData(owner, k, out JsValue kv, out _))
                {
                    cands.Add((k, kv));
                }
            }

            string parent = Pretty(p, p.Keys.Count - 1);
            if (frame.ScopeRealm is { } realm && Closest(wanted, cands, null, realm) is { } best)
            {
                Suggest(d, parent + "." + best.Name, Short(best.Value));
            }
            else if (cands.Count > 0)
            {
                d.Help.Add("`" + parent + "` has " + string.Join(", ", cands.Take(MaxKeys).Select(c => "`" + c.Item1 + "`")) + (cands.Count > MaxKeys ? ", …" : string.Empty) + " but no `" + wanted + "`");
            }
            else
            {
                d.Help.Add("`" + parent + "` has no own properties, so `" + wanted + "` was never set");
            }
        }

        d.Help.Add("fall back to an empty object when it can be missing: `" + full + " ?? {}`");
    }

    private static void DestructuredParameter(Diagnostic d, StackFrameInfo frame, string word, string pattern, Span primary)
    {
        string fn = d.Stack[0].Function;
        var names = new List<string>();
        if (SplitTopLevel(pattern[1..^1]) is { } parts)
        {
            foreach (string part in parts)
            {
                string t = part.Trim();
                if (t.Length == 0 || t.StartsWith("...", StringComparison.Ordinal))
                {
                    continue;
                }

                int cut = t.IndexOfAny([':', '=']);
                string name = (cut < 0 ? t : t[..cut]).Trim();
                if (name.Length > 0 && name.All(IsIdentPart))
                {
                    names.Add(name);
                }
            }
        }

        d.PrimaryLabel = "this parameter expects an object, but " + (fn.Length > 0 ? "`" + fn + "`" : "the function") + " received " + word;
        if (d.Stack.Count > 1 && d.Stack[1] is { IsHost: false, IsNative: false } caller && caller.Span.End <= d.Source.Text.Length && ReferenceEquals(caller.Source, d.Source))
        {
            // The frame's span may stop at the callee; take the argument list that follows it.
            string text = d.Source.Text;
            int end = caller.Span.End;
            int open = end;
            while (open < text.Length && char.IsWhiteSpace(text[open]))
            {
                open++;
            }

            bool noArgs = false;
            if (open < text.Length && text[open] == '(' && FindClose(text, open) is var close and > 0)
            {
                noArgs = text[(open + 1)..close].Trim().Length == 0;
                end = close + 1;
            }

            d.SecondaryLabels.Add(new Label(new Span(caller.Span.Start, end), noArgs ? "called here without an argument" : "called here"));
        }

        d.Help.Add("give the parameter a default so a missing argument is allowed: `" + pattern + " = {}`");
        if (fn.Length > 0 && names.Count > 0)
        {
            d.Help.Add("or pass an object with the properties it reads: `" + fn + "({ " + string.Join(", ", names.Select(n => n + ": …")) + " })`");
        }
    }

    // ---- Invalid Date -----------------------------------------------------------------------------

    private static void InvalidDate(Diagnostic d, StackFrameInfo frame, TraceState trace, string expr, Span primary)
    {
        int open = expr.IndexOf('(', StringComparison.Ordinal) is var q and >= 0 ? q : expr.Length;
        if (ParsePath(expr[..open]) is not { Keys.Count: > 0 } callee)
        {
            return;
        }

        var owner = new Path { Root = callee.Root, Keys = callee.Keys.GetRange(0, callee.Keys.Count - 1), Bracket = callee.Bracket.GetRange(0, callee.Bracket.Count - 1) };
        if (Evaluate(owner, frame, trace) is not { } ev || ev.Value.Ref is not JsDate { Tv: double tv } || !double.IsNaN(tv))
        {
            return;
        }

        string name = Pretty(owner, owner.Keys.Count);
        string method = callee.Keys[^1];
        d.PrimaryLabel = "`" + name + "` is an Invalid Date, so `" + method + "` throws";
        string? input = null;
        if (OriginOf(owner, ev, trace) is { } origin)
        {
            string made = SourceOf(d, origin);
            string message = "`" + name + "` is made here";
            if (made.StartsWith("new Date(", StringComparison.Ordinal) && made.EndsWith(')') && SplitTopLevel(made["new Date(".Length..^1]) is { Count: 1 } one && one[0].Trim().Length > 0)
            {
                string arg = one[0].Trim();
                if (arg.Length >= 2 && arg[0] is '"' or '\'' && arg[^1] == arg[0])
                {
                    input = arg[1..^1];
                    message += " from the text " + arg;
                }
                else if (ParsePath(arg) is { } ap && Evaluate(ap, frame, trace) is { } ae)
                {
                    message += " from `" + arg + "`, which is " + Preview(ae.Value);
                    input = ae.Value.IsString ? ae.Value.AsString : null;
                }
            }

            if (LabelFor(d, origin, message) is { } l)
            {
                d.SecondaryLabels.Add(l);
                d.Provenance.Add(new ProvenanceStep(l.Span, message));
            }
        }

        if (input is not null && IsoParts(input) is { } iso)
        {
            if (iso.Month is < 1 or > 12)
            {
                d.Help.Add("month " + iso.Month.ToString(NumberFormatInfo.InvariantInfo) + " does not exist; in `YYYY-MM-DD` the month is 01 to 12");
            }
            else if (iso.Day is < 1 or > 31)
            {
                d.Help.Add("day " + iso.Day.ToString(NumberFormatInfo.InvariantInfo) + " does not exist; in `YYYY-MM-DD` the day is 01 to 31");
            }
        }
        else if (input is not null)
        {
            d.Help.Add("`new Date(text)` only reliably reads ISO 8601 text such as `2024-01-31` or `2024-01-31T10:00:00Z`");
        }

        d.Help.Add("check it before formatting: `if (Number.isNaN(" + name + ".getTime())) { ... }`");
    }

    private static (int Month, int Day)? IsoParts(string s)
    {
        // YYYY-MM-DD at the start, optionally followed by a time part.
        if (s.Length >= 10 && s[4] == '-' && s[7] == '-' && s.Take(4).All(char.IsAsciiDigit) && char.IsAsciiDigit(s[5]) && char.IsAsciiDigit(s[6]) && char.IsAsciiDigit(s[8]) && char.IsAsciiDigit(s[9]))
        {
            return (((s[5] - '0') * 10) + (s[6] - '0'), ((s[8] - '0') * 10) + (s[9] - '0'));
        }

        return null;
    }

    // ---- JSON text --------------------------------------------------------------------------------

    /// <summary>The offset in <paramref name="json"/> where JSON.parse gives up, found by parsing it again; -1 when it parses.</summary>
    private static int JsonErrorPosition(string json)
    {
        var parser = new JsonParser(json, false);
        try
        {
            parser.Parse(out _);
        }
        catch (JsException)
        {
            return parser.Position;
        }
        catch (JsLimitException)
        {
            // The explanation must not turn into a second limit error; the plain report stands.
            return -1;
        }

        return -1;
    }

    /// <summary>The string a source literal holds, with the source offset of each character; null when the text is not a plain string literal.</summary>
    private static (string Value, List<int> Offsets)? ReadStringLiteral(string src, int start, int end)
    {
        if (end - start < 2 || src[start] is not ('"' or '\'') || src[end - 1] != src[start])
        {
            return null;
        }

        char q = src[start];
        var sb = new StringBuilder();
        var map = new List<int>();
        for (int i = start + 1; i < end - 1; i++)
        {
            char c = src[i];
            if (c == q)
            {
                return null;
            }

            if (c != '\\')
            {
                sb.Append(c);
                map.Add(i);
                continue;
            }

            if (i + 1 >= end - 1)
            {
                return null;
            }

            char e = src[i + 1];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '0' when !(i + 2 < end - 1 && char.IsAsciiDigit(src[i + 2])): sb.Append('\0'); break;
                case '\\' or '"' or '\'' or '/': sb.Append(e); break;
                case 'u' when i + 5 < end - 1 && int.TryParse(src.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out int u):
                    sb.Append((char)u);
                    map.Add(i);
                    i += 5;
                    continue;
                default:
                    return null;
            }

            map.Add(i);
            i++;
        }

        return (sb.ToString(), map);
    }

    private static string Describe(char c) => c switch
    {
        '\n' => "a newline",
        '\t' => "a tab",
        ' ' => "a space",
        _ => "`" + c + "`",
    };

    private static void ExplainJsonText(Diagnostic d, StackFrameInfo frame, TraceState trace, string argText, Span argSpan, Path? p, Eval? ev, string text)
    {
        int pos = JsonErrorPosition(text);
        if (pos < 0)
        {
            return;
        }

        bool atEnd = pos >= text.Length;
        char at = atEnd ? '\0' : text[pos];

        // Find the string literal in the source that holds this text, either the argument itself or the value a binding was set from.
        string src = d.Source.Text;
        (string Value, List<int> Offsets)? lit = ReadStringLiteral(src, argSpan.Start, argSpan.End);
        bool literalIsArgument = lit is not null;
        Origin? litOrigin = null;
        if (lit is null && p is { Keys.Count: 0 } && ev?.RootOrigin is { } ro && ReferenceEquals(ro.Source, d.Source))
        {
            lit = ReadStringLiteral(src, ro.Start, ro.End);
            litOrigin = ro;
        }

        string what = atEnd ? "the text ends here, but JSON needs more" : "JSON.parse stops at " + Describe(at) + " here";
        if (lit is { } l && l.Value == text)
        {
            int off = atEnd ? (litOrigin is not null ? litOrigin.End - 1 : argSpan.End - 1) : l.Offsets[pos];
            var charSpan = new Span(off, off + 1);
            if (literalIsArgument)
            {
                d.MovePrimary(charSpan);
                d.PrimaryLabel = what;
            }
            else
            {
                // Keep the argument as the primary and mark the exact character inside the text it came from.
                d.PrimaryLabel = "`" + argText + "` is not valid JSON";
                d.SecondaryLabels.RemoveAll(x => litOrigin is not null && x.Span == new Span(litOrigin.Start, litOrigin.End));
                d.Provenance.RemoveAll(x => litOrigin is not null && x.Span == new Span(litOrigin.Start, litOrigin.End));
                d.SecondaryLabels.Add(new Label(charSpan, "`" + argText + "` is set from this text; " + what));
                d.Provenance.Add(new ProvenanceStep(charSpan, what));
            }
        }
        else
        {
            int from = Math.Max(0, pos - 20);
            string before = text[from..Math.Min(pos, text.Length)].Replace("\n", "\\n", StringComparison.Ordinal);
            string line = (from > 0 ? "…" : string.Empty) + before;
            d.Notes.Add(atEnd
                ? "the text ends after `" + line + "`, but JSON needs more (position " + pos.ToString(NumberFormatInfo.InvariantInfo) + ")"
                : "JSON.parse stops at position " + pos.ToString(NumberFormatInfo.InvariantInfo) + ", at " + Describe(at) + ", right after `" + line + "`");
        }

        if (atEnd)
        {
            d.Help.Add("the text is cut off; check for a missing closing `}`, `]` or `\"`");
        }
        else if (at is '}' or ']' && PreviousSignificant(text, pos) == ',')
        {
            d.Help.Add("JSON does not allow a trailing comma; remove the `,` before `" + at + "`");
        }
        else if (at == '\'')
        {
            d.Help.Add("JSON strings and keys use double quotes `\"`, not single quotes");
        }
        else if (char.IsLetter(at) && PreviousSignificant(text, pos) is '{' or ',')
        {
            d.Help.Add("JSON property names must be in double quotes: `\"" + text[pos..Math.Min(text.Length, pos + 8)].TakeWhile(IsIdentPart).Aggregate(string.Empty, (a, c) => a + c) + "\"`");
        }
        else if (at == '<')
        {
            d.Help.Add("this looks like HTML or XML rather than JSON; check what the request returned");
        }
    }

    private static char PreviousSignificant(string s, int pos)
    {
        for (int i = pos - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(s[i]))
            {
                return s[i];
            }
        }

        return '\0';
    }

    // ---- callable members for "is not a function" --------------------------------------------------

    /// <summary>The closest member the receiver really has: a callable one when possible, else a data property the author called by mistake.</summary>
    private static bool SuggestMember(Diagnostic d, string ownerText, string member, JsValue receiver, Realm realm)
    {
        List<(string Name, JsValue Value)> all = Members(receiver, realm);
        var callable = all.Where(m => m.Value.Ref is JsFunction && m.Name != "constructor").ToList();
        if (Closest(member, callable, null, realm) is { } best)
        {
            Suggest(d, ownerText + "." + best.Name, Short(best.Value));
            return true;
        }

        // A property is only offered for a near-certain typo: `lenght` for `length`, not `a` for `map`.
        if (Closest(member, all.Where(m => m.Value.Ref is not JsFunction && Closeness(member, m.Name) <= 2), null, realm) is { } prop)
        {
            Suggest(d, ownerText + "." + prop.Name, Short(prop.Value) + "; it is a property, so read it without `()`");
            return true;
        }

        return false;
    }
}
