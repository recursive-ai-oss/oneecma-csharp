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

    /// <summary>Binding strength of a binary operator, higher binds tighter.</summary>
    private static int Precedence(string op) => op switch
    {
        "**" => 12,
        "*" or "/" or "%" => 11,
        "+" or "-" => 10,
        "<<" or ">>" or ">>>" => 9,
        "&" => 7,
        "^" => 6,
        "|" => 5,
        _ => 1,
    };

    /// <summary>
    /// Splits <c>a op b</c> at the operator that runs last (the root of the expression): the lowest-precedence top-level operator,
    /// the rightmost of equals (the leftmost for <c>**</c>). False when the text holds something else at the top level, such as a
    /// comparison, a conditional or a spread.
    /// </summary>
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

                if (match is null)
                {
                    return false;
                }

                int rank = Precedence(match);
                int foundRank = found < 0 ? int.MaxValue : Precedence(foundOp);
                if (found < 0 || rank < foundRank || (rank == foundRank && match != "**"))
                {
                    found = i;
                    foundOp = match;
                }

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
        if (!SplitBinary(expr, out string l, out string op, out string r))
        {
            return;
        }

        // An operand that is a literal or a plain path is read from the scope; any other (a call, a nested operation) takes the value
        // the operator was about to use, which the traced replay noted just before it threw.
        Operand? a = ReadOperand(l, frame, trace);
        Operand? b = ReadOperand(r, frame, trace);
        if ((a is null || b is null) && !trace.HasLastBinary)
        {
            return;
        }

        a ??= new Operand { Text = l.Trim(), Value = trace.LastLeft };
        b ??= new Operand { Text = r.Trim(), Value = trace.LastRight };

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

    // ---- an undefined that came from somewhere else ------------------------------------------------

    private static string Slice(Text.SourceText src, int start, int end) =>
        start >= 0 && end <= src.Text.Length && end >= start ? src.Text[start..end] : string.Empty;

    /// <summary>The end of a call whose recorded span stops at the callee: past the argument list that follows it.</summary>
    private static int ExtendCall(string text, int end)
    {
        int open = end;
        while (open < text.Length && char.IsWhiteSpace(text[open]))
        {
            open++;
        }

        return open < text.Length && text[open] == '(' && FindClose(text, open) is > 0 and var close ? close + 1 : end;
    }

    /// <summary>The path from <paramref name="start"/> to an object that has its own <paramref name="key"/>, breadth first through plain nested objects.</summary>
    private static List<string>? FindNested(JsObject start, string key, out JsValue found)
    {
        found = default;
        var queue = new Queue<(JsObject Obj, List<string> Path)>();
        queue.Enqueue((start, []));
        int visited = 0;
        while (queue.Count > 0 && visited++ < 200)
        {
            (JsObject obj, List<string> path) = queue.Dequeue();
            foreach (string k in OwnKeys(obj))
            {
                if (!TryData(obj, k, out JsValue v, out _) || v.Ref is not JsObject inner || inner is JsFunction or JsArray or ProxyObject || path.Count >= 3)
                {
                    continue;
                }

                var next = new List<string>(path) { k };
                if (TryData(inner, key, out JsValue hit, out bool inherited) && !inherited && !hit.IsNullish)
                {
                    found = hit;
                    return next;
                }

                queue.Enqueue((inner, next));
            }
        }

        return null;
    }

    /// <summary>
    /// When the undefined being read was made somewhere else (a property read that found nothing, a call that returned nothing, a
    /// <c>find</c> that matched nothing), labels that place and says how to fix it there. Returns false when the value is untagged or
    /// was made by the expression that failed, which the caller explains itself. With <paramref name="advise"/> false only the
    /// label and notes are added.
    /// </summary>
    private static bool ExplainUndefinedOrigin(Diagnostic d, TraceState trace, string name, JsValue value, string member, bool setting, Span primary, bool advise)
    {
        if (trace.UndefinedOriginOf(value) is not { } uo || (!ReferenceEquals(uo.Source, d.Source) && uo.Source.Name != d.Source.Name))
        {
            return false;
        }

        if (uo.Start >= primary.Start && uo.End <= primary.End)
        {
            return false;
        }

        string text = d.Source.Text;
        int end = uo.Kind is UndefinedKind.CallReturned or UndefinedKind.FindNothing ? ExtendCall(text, uo.End) : uo.End;
        string produced = Slice(d.Source, uo.Start, end);
        string producedHead = Slice(d.Source, uo.Start, uo.End);
        string ownerText = producedHead.EndsWith("." + uo.Key, StringComparison.Ordinal) ? producedHead[..^(uo.Key.Length + 1)].TrimEnd('?') : producedHead;
        string guard = member.Length == 0 ? string.Empty : "`" + name + (member.All(char.IsAsciiDigit) ? "?.[" + member + "]" : "?." + member) + "`";
        string callText = Clip(produced.Replace("\n", " ", StringComparison.Ordinal), 60);

        string message = uo.Kind switch
        {
            UndefinedKind.MissingProperty when uo.IsElement => "undefined starts here: `" + ownerText + "` has nothing at " + uo.Key,
            UndefinedKind.MissingProperty => "undefined starts here: `" + ownerText + "` has no `" + uo.Key + "`",
            UndefinedKind.PropertyHoldsUndefined => "undefined starts here: `" + producedHead + "` holds undefined",
            UndefinedKind.CallReturned => "undefined starts here: `" + callText + "` returned without a value",
            _ => "undefined starts here: `" + callText + "` matched nothing",
        };
        var span = new Span(uo.Start, end);
        // The generic "set to undefined here" label of the binding says less than this one about the same place.
        d.SecondaryLabels.RemoveAll(x => x.Span == span);
        d.Provenance.RemoveAll(x => x.Span == span);
        d.SecondaryLabels.Add(new Label(span, message));
        d.Provenance.Add(new ProvenanceStep(span, message));

        if (uo.Kind == UndefinedKind.FindNothing && uo.Receiver is { } recv)
        {
            string recvText = producedHead.EndsWith("." + uo.Key, StringComparison.Ordinal) ? ownerText : "the array";
            if (trace.FilterOf(recv) is { } fr)
            {
                string filterText = Clip(Slice(fr.Source, fr.Start, ExtendCall(fr.Source.Text, fr.End)).Replace("\n", " ", StringComparison.Ordinal), 60);
                d.Notes.Add("`" + recvText + "` came from `" + filterText + "` which kept " + fr.KeptLength.ToString(NumberFormatInfo.InvariantInfo) + " of " + Items((int)Math.Min(fr.InputLength, int.MaxValue)));
            }
            else if (recv.Length == 0)
            {
                d.Notes.Add("`" + recvText + "` is empty");
            }
            else
            {
                d.Notes.Add("none of the " + Items((int)Math.Min(recv.Length, int.MaxValue)) + " in `" + recvText + "` matched");
            }
        }

        if (!advise)
        {
            return true;
        }

        switch (uo.Kind)
        {
            case UndefinedKind.MissingProperty when uo.Owner is { } owner:
                {
                    if (!uo.IsElement && FindNested(owner, uo.Key, out JsValue hit) is { } path)
                    {
                        Suggest(d, ownerText + "." + string.Join(".", path) + "." + uo.Key, Short(hit));
                        break;
                    }

                    var cands = new List<(string, JsValue)>();
                    foreach (string k in OwnKeys(owner))
                    {
                        if (TryData(owner, k, out JsValue kv, out _))
                        {
                            cands.Add((k, kv));
                        }
                    }

                    if (!uo.IsElement && Agent.CurrentOrNull?.Realm is { } realm && Closest(uo.Key, cands, null, realm) is { } best)
                    {
                        Suggest(d, ownerText + "." + best.Name, Short(best.Value));
                    }
                    else if (cands.Count > 0)
                    {
                        d.Help.Add("`" + ownerText + "` has " + string.Join(", ", cands.Take(MaxKeys).Select(c => "`" + c.Item1 + "`")) + (cands.Count > MaxKeys ? ", …" : string.Empty) + " but no `" + uo.Key + "`");
                    }
                    else
                    {
                        d.Help.Add("`" + ownerText + "` has no own properties, so `" + uo.Key + "` was never set");
                    }

                    break;
                }

            case UndefinedKind.PropertyHoldsUndefined:
                d.Help.Add("`" + producedHead + "` is present but set to undefined; find where it is assigned" + (guard.Length > 0 && !setting ? ", or if it can be missing, guard it: " + guard : string.Empty));
                break;
            case UndefinedKind.CallReturned:
                d.Help.Add("`" + (uo.Key.Length > 0 ? uo.Key : "the function") + "` returns undefined on this path; make it return the value" + (guard.Length > 0 && !setting ? ", or if it can be missing, guard it: " + guard : string.Empty));
                break;
            default:
                d.Help.Add(guard.Length > 0 && !setting ? "if it can be missing, guard it: " + guard : "if it can be missing, check it before use");
                break;
        }

        return true;
    }

    // ---- strings past the length limit -------------------------------------------------------------

    /// <summary>A number the source spells as a literal, a plain path or a small arithmetic expression of those; false when it needs script to run.</summary>
    private static bool TryNumber(string text, StackFrameInfo frame, TraceState trace, int depth, out double value)
    {
        value = 0;
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '(' && FindClose(text, 0) == text.Length - 1)
        {
            return depth < 8 && TryNumber(text[1..^1], frame, trace, depth + 1, out value);
        }

        if (text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] == '.') && double.TryParse(text.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.Float, NumberFormatInfo.InvariantInfo, out value))
        {
            return true;
        }

        if (depth < 8 && SplitBinary(text, out string l, out string op, out string r) && op is "**" or "*" or "+" or "-" or "/"
            && TryNumber(l, frame, trace, depth + 1, out double a) && TryNumber(r, frame, trace, depth + 1, out double b))
        {
            value = op switch { "**" => Math.Pow(a, b), "*" => a * b, "+" => a + b, "-" => a - b, _ => a / b };
            return true;
        }

        if (ParsePath(text) is { } p && Evaluate(p, frame, trace) is { } ev && ev.Value.IsNumber)
        {
            value = ev.Value.Num;
            return true;
        }

        return false;
    }

    private static void StringLength(Diagnostic d, StackFrameInfo frame, TraceState trace, string expr, Span primary)
    {
        long limit = Agent.CurrentOrNull?.Limits.MaxStringLength ?? 536_870_888;
        string limitText = limit.ToString(NumberFormatInfo.InvariantInfo);

        // A concatenation: the operator noted its operands just before it threw.
        if (trace.HasLastBinary && trace.LastLeft.IsString && trace.LastRight.IsString && SplitBinary(expr, out string cl, out string cop, out string cr) && cop is "+" or "+=")
        {
            int left = trace.LastLeft.AsString.Length;
            int right = trace.LastRight.AsString.Length;
            d.PrimaryLabel = "`" + cl.Trim() + "` has " + Digits(left) + " characters and `" + cr.Trim() + "` adds " + Digits(right) + "; the result would pass the limit of " + limitText;
            d.Help.Add("a string this long is usually a loop that keeps appending to the same string; collect the pieces in an array and write them out in parts");
            return;
        }

        int dot = expr.LastIndexOf('.');
        if (dot <= 0)
        {
            return;
        }

        string method = expr[(dot + 1)..].Trim();
        string receiver = expr[..dot].Trim();
        var args = ArgumentsAfter(d.Source.Text, primary.End).ToList();
        string? receiverString = null;
        if (ReadStringLiteral(receiver, 0, receiver.Length) is { } lit)
        {
            receiverString = lit.Value;
        }
        else if (ParsePath(receiver) is { } rp && Evaluate(rp, frame, trace) is { } rev && rev.Value.IsString)
        {
            receiverString = rev.Value.AsString;
        }

        switch (method)
        {
            case "repeat" when args.Count == 1 && receiverString is not null && TryNumber(args[0].Text, frame, trace, 0, out double count):
                {
                    double total = receiverString.Length * count;
                    d.PrimaryLabel = "`" + receiver + ".repeat` would build a string of " + Digits(total) + " characters, past the limit of " + limitText;
                    d.SecondaryLabels.Add(new Label(args[0].Span, "the count is " + Digits(count)));
                    if (receiverString.Length > 0)
                    {
                        d.Help.Add("repeat it at most " + Digits(Math.Floor(limit / (double)receiverString.Length)) + " times (`" + receiver + ".repeat(n)` with `n` up to that), and in practice far less");
                    }

                    d.Help.Add("if the count comes from data, check it first: `Math.min(count, 1000)`");
                    return;
                }

            case "padStart" or "padEnd" when args.Count >= 1 && TryNumber(args[0].Text, frame, trace, 0, out double target):
                {
                    d.PrimaryLabel = "`" + method + "` would pad to " + Digits(target) + " characters, past the limit of " + limitText;
                    d.SecondaryLabels.Add(new Label(args[0].Span, "the target length is " + Digits(target)));
                    d.Help.Add("pass the width you mean; the target length is the length of the whole result, not the number of padding characters");
                    return;
                }

            case "repeat" or "padStart" or "padEnd" or "join" or "concat":
                d.PrimaryLabel = "`" + method + "` would build a string longer than the limit of " + limitText + " characters";
                d.Help.Add(method == "join"
                    ? "an array this large cannot be joined into one string; write it out in parts"
                    : "check the numbers passed to `" + method + "`: the result is longer than any string can be");
                return;
        }
    }

    private static string Digits(double n) => n.ToString("0", NumberFormatInfo.InvariantInfo);

    // ---- Invalid Date -----------------------------------------------------------------------------

    private static void InvalidDate(Diagnostic d, StackFrameInfo frame, TraceState trace, string expr, Span primary)
    {
        int open = expr.IndexOf('(', StringComparison.Ordinal) is var q and >= 0 ? q : expr.Length;
        if (ParsePath(expr[..open]) is not { Keys.Count: > 0 } callee)
        {
            return;
        }

        string method = callee.Keys[^1];
        var owner = new Path { Root = callee.Root, Keys = callee.Keys.GetRange(0, callee.Keys.Count - 1), Bracket = callee.Bracket.GetRange(0, callee.Bracket.Count - 1) };
        string calleeText = Pretty(callee, callee.Keys.Count);

        // The Date is the receiver (`when.toISOString()`) or an argument (`formatter.format(when)`).
        Path? datePath = null;
        Eval? dateEval = null;
        Span? argSpan = null;
        string? inlineText = null;
        if (Evaluate(owner, frame, trace) is { } oev && oev.Value.Ref is JsDate { Tv: double otv } && double.IsNaN(otv))
        {
            datePath = owner;
            dateEval = oev;
        }
        else
        {
            foreach ((string text, Span span) in ArgumentsAfter(d.Source.Text, primary.Start + open))
            {
                if (ParsePath(text) is { } ap && Evaluate(ap, frame, trace) is { } ae && ae.Value.Ref is JsDate { Tv: double atv } && double.IsNaN(atv))
                {
                    datePath = ap;
                    dateEval = ae;
                    argSpan = span;
                    break;
                }

                if (text.StartsWith("new Date(", StringComparison.Ordinal) && text.EndsWith(')'))
                {
                    inlineText = text;
                    argSpan = span;
                    break;
                }
            }
        }

        if (argSpan is { } moved)
        {
            d.MovePrimary(moved);
        }

        string? input = null;
        if (datePath is not null && dateEval is not null)
        {
            string name = Pretty(datePath, datePath.Keys.Count);
            d.PrimaryLabel = argSpan is null
                ? "`" + name + "` is an Invalid Date, so `" + method + "` throws"
                : "`" + name + "` is an Invalid Date, so `" + calleeText + "` cannot format it";
            input = AddDateOrigin(d, frame, trace, name, datePath, dateEval);
            if (OriginOf(datePath, dateEval, trace) is null && datePath.Keys.Count == 0)
            {
                input = FindDateInCallers(d, trace, (JsDate)dateEval.Value.Ref!, name);
            }

            if (!ExplainDateFromUndefined(d, trace, (JsDate)dateEval.Value.Ref!, primary))
            {
                d.Help.Add("check it before " + (argSpan is null ? "formatting" : "passing it on") + ": `if (Number.isNaN(" + name + ".getTime())) { ... }`");
            }
        }
        else if (inlineText is not null)
        {
            d.PrimaryLabel = "`" + inlineText + "` is an Invalid Date, so `" + calleeText + "` cannot format it";
            input = InlineDateText(inlineText, frame, trace);
        }
        else
        {
            return;
        }

        if (input is not null && IsoParts(input) is { } iso)
        {
            if (iso.Month is < 1 or > 12)
            {
                d.Help.Insert(0, "month " + iso.Month.ToString(NumberFormatInfo.InvariantInfo) + " does not exist; in `YYYY-MM-DD` the month is 01 to 12");
            }
            else if (iso.Day is < 1 or > 31)
            {
                d.Help.Insert(0, "day " + iso.Day.ToString(NumberFormatInfo.InvariantInfo) + " does not exist; in `YYYY-MM-DD` the day is 01 to 31");
            }
        }
        else if (input is not null)
        {
            d.Help.Insert(0, "`new Date(text)` only reliably reads ISO 8601 text such as `2024-01-31` or `2024-01-31T10:00:00Z`");
        }
    }

    /// <summary>
    /// When the Invalid Date was made by `new Date(x)` and `x` was an undefined from a property read, says so at the `new Date`
    /// and gives the usual help for a missing property (did you mean, including `created_at` for `createdAt`).
    /// </summary>
    private static bool ExplainDateFromUndefined(Diagnostic d, TraceState trace, JsDate date, Span primary)
    {
        if (trace.MadeDateOf(date) is not { } made || trace.UndefinedOriginOf(made.Argument) is not { } uo
            || (!ReferenceEquals(made.Source, d.Source) && made.Source.Name != d.Source.Name))
        {
            return false;
        }

        string argText = Slice(d.Source, uo.Start, uo.End);
        // The recorded span is the constructor name; the label wants `new Date(...)` whole.
        string all = d.Source.Text;
        int madeStart = made.Start;
        int back = madeStart;
        while (back > 0 && char.IsWhiteSpace(all[back - 1]))
        {
            back--;
        }

        if (back >= 3 && string.CompareOrdinal(all, back - 3, "new", 0, 3) == 0 && (back == 3 || !IsIdentPart(all[back - 4])))
        {
            madeStart = back - 3;
        }

        int madeEnd = ExtendCall(all, made.End);
        string madeText = Clip(Slice(d.Source, madeStart, madeEnd).Replace("\n", " ", StringComparison.Ordinal), 60);
        var madeSpan = new Span(madeStart, madeEnd);
        if (!ExplainUndefinedOrigin(d, trace, argText, made.Argument, string.Empty, false, primary, advise: true))
        {
            return false;
        }

        // One label says it all: the Date call and the undefined that went into it.
        string message = "`" + madeText + "`: `" + argText + "` is undefined, so the Date is invalid";
        d.SecondaryLabels.RemoveAll(x => x.Span == madeSpan);
        d.Provenance.RemoveAll(x => x.Span == madeSpan);
        var undefinedSpan = new Span(uo.Start, uo.End);
        d.SecondaryLabels.RemoveAll(x => x.Span == undefinedSpan);
        d.Provenance.RemoveAll(x => x.Span == undefinedSpan);
        d.SecondaryLabels.Add(new Label(madeSpan, message));
        d.Provenance.Add(new ProvenanceStep(madeSpan, message));
        return true;
    }

    /// <summary>The text of <c>new Date("...")</c> or of the string a path holds, or null when it is neither.</summary>
    private static string? InlineDateText(string made, StackFrameInfo frame, TraceState trace)
    {
        if (SplitTopLevel(made["new Date(".Length..^1]) is not { Count: 1 } one || one[0].Trim().Length == 0)
        {
            return null;
        }

        string arg = one[0].Trim();
        if (arg.Length >= 2 && arg[0] is '"' or '\'' && arg[^1] == arg[0])
        {
            return arg[1..^1];
        }

        return ParsePath(arg) is { } ap && Evaluate(ap, frame, trace) is { } ae && ae.Value.IsString ? ae.Value.AsString : null;
    }

    /// <summary>Labels where an Invalid Date was made (`new Date("text")`) and returns the text it was made from, when the source shows it.</summary>
    private static string? AddDateOrigin(Diagnostic d, StackFrameInfo frame, TraceState trace, string name, Path path, Eval ev)
    {
        string? input = null;
        if (OriginOf(path, ev, trace) is { } origin)
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

        return input;
    }

    /// <summary>
    /// A parameter has no store of its own, so its Date is looked for in the callers: the call whose argument is the same object
    /// shows where it was passed in and, through that argument's own origin, where it was made.
    /// </summary>
    private static string? FindDateInCallers(Diagnostic d, TraceState trace, JsDate date, string name)
    {
        for (int i = 1; i < d.Stack.Count && i <= 4; i++)
        {
            StackFrameInfo caller = d.Stack[i];
            if (caller.IsHost || caller.IsNative || caller.ScopeRealm is null || !ReferenceEquals(caller.Source, d.Source))
            {
                continue;
            }

            string text = d.Source.Text;
            int end = caller.Span.End;
            int open = end;
            while (open < text.Length && char.IsWhiteSpace(text[open]))
            {
                open++;
            }

            if (open >= text.Length || text[open] != '(')
            {
                continue;
            }

            foreach ((string argText, Span span) in ArgumentsAfter(text, open))
            {
                if (ParsePath(argText) is { } ap && Evaluate(ap, caller, trace) is { } ae && ReferenceEquals(ae.Value.Ref, date))
                {
                    string callee = d.Stack[i - 1].Function;
                    string message = "`" + argText + "` is passed to " + (callee.Length > 0 ? "`" + callee + "`" : "the function") + " here";
                    d.SecondaryLabels.Add(new Label(span, message));
                    d.Provenance.Add(new ProvenanceStep(span, message));
                    return AddDateOrigin(d, caller, trace, Pretty(ap, ap.Keys.Count), ap, ae);
                }
            }
        }

        return null;
    }

    /// <summary>The arguments of the call whose argument list opens at or after <paramref name="from"/>, each with its trimmed text and span.</summary>
    private static IEnumerable<(string Text, Span Span)> ArgumentsAfter(string text, int from)
    {
        int open = from;
        while (open < text.Length && char.IsWhiteSpace(text[open]))
        {
            open++;
        }

        if (open >= text.Length || text[open] != '(' || FindClose(text, open) is not (> 0 and var close) || SplitTopLevel(text[(open + 1)..close]) is not { } parts)
        {
            yield break;
        }

        int at = open + 1;
        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                int start = at + (part.Length - part.TrimStart().Length);
                yield return (trimmed, new Span(start, start + trimmed.Length));
            }

            at += part.Length + 1;
        }
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
