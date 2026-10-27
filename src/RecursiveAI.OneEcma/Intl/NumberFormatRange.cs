using System.Text;

namespace RecursiveAI.OneEcma.Intl;

internal sealed partial class NumberFormatService
{
    /// <summary>FormatNumericRange (15.5.?): the range <paramref name="x"/> to <paramref name="y"/> as a string.</summary>
    /// <exception cref="IntlException">Either value is NaN (RangeError).</exception>
    internal string FormatRange(DecimalNumber x, DecimalNumber y) => Concat(FormatRangeToParts(x, y));

    /// <summary>FormatNumericRangeToParts: parts carry the source <c>startRange</c>, <c>endRange</c> or <c>shared</c>.</summary>
    /// <exception cref="IntlException">Either value is NaN (RangeError).</exception>
    internal List<NumberPart> FormatRangeToParts(DecimalNumber x, DecimalNumber y)
    {
        if (x.IsNaN || y.IsNaN)
        {
            throw IntlException.Range("Invalid start : NaN");
        }

        Pieces px = Build(x), py = Build(y);
        var parts = new List<NumberPart>(16);

        // Equal after rounding: "~3" (FormatApproximately).
        if (SameText(px, py))
        {
            // The sign comes from the locale's symbols (the "approximately" pattern text can differ, as in French).
            parts.Add(new NumberPart("approximatelySign", _symbols.Approximately, "shared"));
            px.AppendTo(parts, "shared");
            return parts;
        }

        string rangePattern = _data.GetString("numbers", "miscPatterns" + _numberNs, "range") ?? "{0}-{1}";
        int i0 = rangePattern.IndexOf("{0}", StringComparison.Ordinal);
        int i1 = rangePattern.IndexOf("{1}", StringComparison.Ordinal);
        string separator = rangePattern.Substring(i0 + 3, i1 - i0 - 3);

        // Collapsing follows the number range formatter's "auto" heuristic: shared unit or currency-name
        // text collapses whenever both sides match; the sign, pattern and compact affixes collapse only
        // when they match and are longer than one code point ("$" stays on both sides, "km" does not).
        bool collapseOuter = Same(px.OuterPrefix, py.OuterPrefix) && Same(px.OuterSuffix, py.OuterSuffix);
        bool collapseMiddle = collapseOuter && Same(px.MiddlePrefix, py.MiddlePrefix) && Same(px.MiddleSuffix, py.MiddleSuffix)
            && CodePoints(px.MiddlePrefix) + CodePoints(px.MiddleSuffix) > 1;

        // Spaces go around the separator when the first value keeps any affix of its own.
        bool spaced = Notation is "scientific" or "engineering" || (!collapseOuter && (px.OuterPrefix.Count + px.OuterSuffix.Count) > 0)
            || (!collapseMiddle && (px.MiddlePrefix.Count + px.MiddleSuffix.Count) > 0);
        if (spaced)
        {
            separator = " " + separator.Trim() + " ";
        }

        if (collapseOuter)
        {
            Append(parts, px.OuterPrefix, "shared");
        }
        else
        {
            Append(parts, px.OuterPrefix, "startRange");
        }

        if (collapseMiddle)
        {
            Append(parts, px.MiddlePrefix, "shared");
        }
        else
        {
            Append(parts, px.MiddlePrefix, "startRange");
        }

        Append(parts, px.Number, "startRange");
        if (!collapseMiddle)
        {
            Append(parts, px.MiddleSuffix, "startRange");
        }

        if (!collapseOuter)
        {
            Append(parts, px.OuterSuffix, "startRange");
        }

        // A literal that ends the first value joins the separator ("($5.00) – $5.00"), and the range counts it as shared.
        if (parts.Count > 0 && parts[^1].Type == "literal")
        {
            separator = parts[^1].Value + separator;
            parts.RemoveAt(parts.Count - 1);
        }

        parts.Add(new NumberPart("literal", separator, "shared"));

        if (!collapseOuter)
        {
            Append(parts, py.OuterPrefix, "endRange");
        }

        if (!collapseMiddle)
        {
            Append(parts, py.MiddlePrefix, "endRange");
        }

        Append(parts, py.Number, "endRange");
        if (collapseMiddle)
        {
            Append(parts, py.MiddleSuffix, "shared");
        }
        else
        {
            Append(parts, py.MiddleSuffix, "endRange");
        }

        if (collapseOuter)
        {
            Append(parts, py.OuterSuffix, "shared");
        }
        else
        {
            Append(parts, py.OuterSuffix, "endRange");
        }

        return parts;
    }

    private static void Append(List<NumberPart> parts, List<NumberPart> from, string source)
    {
        foreach (NumberPart p in from)
        {
            AddPart(parts, p, source);
        }
    }

    private static bool Same(List<NumberPart> a, List<NumberPart> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Type != b[i].Type || a[i].Value != b[i].Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameText(Pieces a, Pieces b)
    {
        var pa = new List<NumberPart>();
        var pb = new List<NumberPart>();
        a.AppendTo(pa, null);
        b.AppendTo(pb, null);
        return Concat(pa) == Concat(pb);
    }

    private static int CodePoints(List<NumberPart> parts)
    {
        int n = 0;
        foreach (NumberPart p in parts)
        {
            foreach (Rune _ in p.Value.EnumerateRunes())
            {
                n++;
            }
        }

        return n;
    }
}
