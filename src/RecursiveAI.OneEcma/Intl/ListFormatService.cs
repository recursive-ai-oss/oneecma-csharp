using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>One element of a formatted list (<c>element</c> or <c>literal</c>).</summary>
internal readonly record struct ListPart(string Type, string Value);

/// <summary>Intl.ListFormat (ECMA-402 section 13) over CLDR list patterns.</summary>
internal sealed class ListFormatService
{
    private readonly string _two, _start, _middle, _end;
    private readonly bool _spanish;

    private ListFormatService(string locale, string dataLocale, string type, string style, string[] patterns)
    {
        _spanish = dataLocale == "es" || dataLocale.StartsWith("es-", StringComparison.Ordinal);
        Locale = locale;
        Type = type;
        Style = style;
        _two = patterns[0];
        _start = patterns[1];
        _middle = patterns[2];
        _end = patterns[3];
    }

    /// <summary>The resolved locale tag.</summary>
    internal string Locale { get; }

    /// <summary>The resolved type: <c>conjunction</c>, <c>disjunction</c> or <c>unit</c>.</summary>
    internal string Type { get; }

    /// <summary>The resolved style: <c>long</c>, <c>short</c> or <c>narrow</c>.</summary>
    internal string Style { get; }

    /// <summary>Creates the service (13.1.1).</summary>
    /// <param name="locales">Requested locales, already canonicalized.</param>
    /// <param name="type"><c>type</c> option or null (conjunction).</param>
    /// <param name="style"><c>style</c> option or null (long).</param>
    /// <param name="defaultLocale">The host's default locale.</param>
    /// <param name="localeMatcher"><c>localeMatcher</c> option or null.</param>
    /// <exception cref="IntlException">An option is out of range.</exception>
    internal static ListFormatService Create(IReadOnlyList<string> locales, string? type, string? style, string defaultLocale, string? localeMatcher = null)
    {
        LocaleResolver.ValidateLocaleMatcher(localeMatcher, "ListFormat");
        type ??= "conjunction";
        style ??= "long";
        if (type is not ("conjunction" or "disjunction" or "unit"))
        {
            throw IntlException.Range($"Value {type} out of range for Intl.ListFormat options property type");
        }

        if (style is not ("long" or "short" or "narrow"))
        {
            throw IntlException.Range($"Value {style} out of range for Intl.ListFormat options property style");
        }

        ResolvedLocale r = LocaleResolver.Resolve(locales, [], new Dictionary<string, string?>(), (_, _) => [], defaultLocale);
        DataTree data = LocaleResolver.DataFor(r.DataLocale);
        string baseKey = type switch { "conjunction" => "listPattern-type-standard", "disjunction" => "listPattern-type-or", _ => "listPattern-type-unit" };
        string key = style == "long" ? baseKey : baseKey + "-" + style;
        // Narrow falls back to short, short to long, as CLDR inheritance defines.
        string[] fallbacks = style == "narrow" ? [key, baseKey + "-short", baseKey] : style == "short" ? [key, baseKey] : [baseKey];
        string Get(string field)
        {
            foreach (string k in fallbacks)
            {
                string? v = data.GetString("listPatterns", k, field);
                if (v is not null)
                {
                    return v;
                }
            }

            return "{0}, {1}";
        }

        return new ListFormatService(r.Locale, r.DataLocale, type, style, [Get("2"), Get("start"), Get("middle"), Get("end")]);
    }

    /// <summary>FormatList (13.5.?): the list as one string.</summary>
    internal string Format(IReadOnlyList<string> list) => string.Concat(FormatToParts(list).Select(p => p.Value));

    /// <summary>CreatePartsFromList: element and literal parts.</summary>
    internal List<ListPart> FormatToParts(IReadOnlyList<string> list)
    {
        var parts = new List<ListPart>();
        int n = list.Count;
        if (n == 0)
        {
            return parts;
        }

        if (n == 1)
        {
            parts.Add(new ListPart("element", list[0]));
            return parts;
        }

        if (n == 2)
        {
            Apply(Spanish(_two, list[1]), list[0], list[1], parts, null);
            return Merge(parts);
        }

        // Built from the right: the end pattern joins the last two, middle patterns add elements, start the first.
        var tail = new List<ListPart>();
        Apply(Spanish(_end, list[n - 1]), list[n - 2], list[n - 1], tail, null);
        for (int i = n - 3; i >= 1; i--)
        {
            var next = new List<ListPart>();
            Apply(_middle, list[i], null, next, tail);
            tail = next;
        }

        Apply(_start, list[0], null, parts, tail);
        return Merge(parts);
    }

    // Spanish writes "e" for "y" before words starting with i or hi (not hia, hie), and "u" for "o" before words
    // starting with o, ho, 8 or a lone 11. The reference formatter applies the same rewrite, so this does too.
    private string Spanish(string pattern, string next)
    {
        if (!_spanish)
        {
            return pattern;
        }

        if (pattern.Contains(" y ", StringComparison.Ordinal) && NeedsE(next))
        {
            return pattern.Replace(" y ", " e ", StringComparison.Ordinal);
        }

        if (pattern.Contains(" o ", StringComparison.Ordinal) && NeedsU(next))
        {
            return pattern.Replace(" o ", " u ", StringComparison.Ordinal);
        }

        return pattern;
    }

    private static bool Starts(string s, string prefix) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool NeedsE(string w) =>
        (Starts(w, "i") || (Starts(w, "hi") && !Starts(w, "hia") && !Starts(w, "hie")));

    private static bool NeedsU(string w) =>
        Starts(w, "o") || Starts(w, "ho") || Starts(w, "8")
        || (w.StartsWith("11", StringComparison.Ordinal) && (w.Length == 2 || !char.IsAsciiDigit(w[2])));

    // Expands a pattern: {0} is the first operand and {1} the second (or an already built tail).
    private static void Apply(string pattern, string first, string? second, List<ListPart> into, List<ListPart>? tail)
    {
        int i = 0;
        while (i < pattern.Length)
        {
            int open = pattern.IndexOf('{', i);
            if (open < 0 || open + 2 >= pattern.Length)
            {
                into.Add(new ListPart("literal", pattern[i..]));
                break;
            }

            if (open > i)
            {
                into.Add(new ListPart("literal", pattern[i..open]));
            }

            if (pattern[open + 1] == '0')
            {
                into.Add(new ListPart("element", first));
            }
            else if (tail is not null)
            {
                into.AddRange(tail);
            }
            else
            {
                into.Add(new ListPart("element", second!));
            }

            i = open + 3;
        }
    }

    private static List<ListPart> Merge(List<ListPart> parts)
    {
        var result = new List<ListPart>(parts.Count);
        foreach (ListPart p in parts)
        {
            if (p.Type == "literal" && result.Count > 0 && result[^1].Type == "literal")
            {
                result[^1] = new ListPart("literal", result[^1].Value + p.Value);
            }
            else if (p.Value.Length > 0)
            {
                result.Add(p);
            }
        }

        return result;
    }
}
