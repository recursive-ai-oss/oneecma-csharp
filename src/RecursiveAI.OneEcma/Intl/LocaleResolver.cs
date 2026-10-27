using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>Which JavaScript error class an <see cref="IntlException"/> maps to.</summary>
internal enum IntlErrorKind
{
    /// <summary>A <c>RangeError</c>: an option value or locale tag is outside its allowed set.</summary>
    Range,

    /// <summary>A <c>TypeError</c>: a missing or wrongly typed argument.</summary>
    Type,
}

/// <summary>
/// An ECMA-402 abstract operation threw. The JS layer turns it into the named error class with the same
/// message.
/// </summary>
internal sealed class IntlException(IntlErrorKind kind, string message) : Exception(message)
{
    /// <summary>The error class to throw in JavaScript.</summary>
    internal IntlErrorKind Kind { get; } = kind;

    internal static IntlException Range(string message) => new(IntlErrorKind.Range, message);

    internal static IntlException Type(string message) => new(IntlErrorKind.Type, message);
}

/// <summary>The outcome of ECMA-402 ResolveLocale (section 9.2.7).</summary>
internal sealed class ResolvedLocale
{
    /// <summary>The locale tag, with the supported Unicode extension keywords inserted.</summary>
    internal required string Locale { get; init; }

    /// <summary>The available locale whose data serves the request (no extension).</summary>
    internal required string DataLocale { get; init; }

    /// <summary>Resolved value per relevant extension key.</summary>
    internal required Dictionary<string, string> Keys { get; init; }
}

/// <summary>
/// Locale negotiation (ECMA-402 section 9): CanonicalizeLocaleList, LookupMatcher, BestFitMatcher (the same
/// algorithm, which the spec allows), ResolveLocale, SupportedLocales and GetOption helpers.
/// The set of available locales is the six supported ones plus their language parents; there is no ambient
/// default, the host passes the default locale in.
/// </summary>
internal static class LocaleResolver
{
    /// <summary>The available locales: a bare language is available when a supported locale has it, a regional tag only when it is supported itself.</summary>
    internal static readonly string[] AvailableLocales = ["de", "en", "en-GB", "en-US", "es", "es-US", "fr", "pt", "pt-BR"];

    private static readonly HashSet<string> AvailableSet = new(AvailableLocales, StringComparer.Ordinal);

    /// <summary>CanonicalizeLocaleList (9.2.1) for a list of tags: validates, canonicalizes and removes duplicates, keeping order.</summary>
    /// <exception cref="IntlException">A tag is not structurally valid (RangeError).</exception>
    internal static List<string> CanonicalizeLocaleList(IEnumerable<string> tags)
    {
        var seen = new List<string>();
        foreach (string tag in tags)
        {
            seen.Add(CanonicalizeTag(tag));
        }

        return [.. seen.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>CanonicalizeUnicodeLocaleId of one tag.</summary>
    /// <exception cref="IntlException">The tag is not structurally valid (RangeError).</exception>
    internal static string CanonicalizeTag(string tag)
    {
        LocaleId id = LocaleId.Parse(tag) ?? throw IntlException.Range($"Incorrect locale information provided");
        return id.Canonicalize().ToString();
    }

    /// <summary>BestAvailableLocale (9.2.2): truncates the tag until an available locale matches.</summary>
    internal static string? BestAvailableLocale(string locale)
    {
        string candidate = locale;
        while (true)
        {
            if (AvailableSet.Contains(candidate))
            {
                return candidate;
            }

            int pos = candidate.LastIndexOf('-');
            if (pos < 0)
            {
                return null;
            }

            if (pos >= 2 && candidate[pos - 2] == '-')
            {
                pos -= 2;
            }

            candidate = candidate[..pos];
        }
    }

    /// <summary>
    /// LookupMatcher (9.2.3) and BestFitMatcher (9.2.4, equal to lookup here) for one requested list.
    /// </summary>
    /// <returns>The matched available locale and the Unicode extension of the request that matched (or empty); null locale when none.</returns>
    internal static (string? Locale, string Extension) Match(IReadOnlyList<string> requested)
    {
        foreach (string locale in requested)
        {
            (string noExt, string ext) = SplitUnicodeExtension(locale);
            string? available = BestAvailableLocale(noExt);
            if (available is not null)
            {
                return (available, ext);
            }
        }

        return (null, "");
    }

    /// <summary>Separates the <c>-u-</c> extension (as <c>-u-...</c>) from a canonical tag.</summary>
    internal static (string WithoutExtension, string Extension) SplitUnicodeExtension(string locale)
    {
        // Subtags after the private-use singleton never count.
        string[] parts = locale.Split('-');
        int end = Array.IndexOf(parts, "x");
        if (end < 0)
        {
            end = parts.Length;
        }

        int start = -1;
        for (int i = 1; i < end; i++)
        {
            if (parts[i] == "u")
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return (locale, "");
        }

        int stop = start + 1;
        while (stop < end && parts[stop].Length > 1)
        {
            stop++;
        }

        string ext = "-" + string.Join('-', parts[start..stop]);
        string rest = string.Join('-', parts[..start].Concat(parts[stop..]));
        return (rest, ext);
    }

    /// <summary>UnicodeExtensionValue (9.2.?): the value of <paramref name="key"/> in a <c>-u-...</c> string; null when absent, empty for a bare key.</summary>
    internal static string? UnicodeExtensionValue(string extension, string key)
    {
        string[] parts = extension.Split('-', StringSplitOptions.RemoveEmptyEntries);
        // parts[0] is "u". Attributes come first, then key/value runs.
        int i = 1;
        while (i < parts.Length && parts[i].Length != 2)
        {
            i++;
        }

        for (; i < parts.Length; i++)
        {
            if (parts[i].Length == 2)
            {
                int j = i + 1;
                while (j < parts.Length && parts[j].Length > 2)
                {
                    j++;
                }

                if (parts[i] == key)
                {
                    return string.Join('-', parts[(i + 1)..j]);
                }

                i = j - 1;
            }
        }

        return null;
    }

    /// <summary>
    /// ResolveLocale (9.2.7). <paramref name="keyData"/> returns, for a data locale and key, the supported
    /// values with the default first. <paramref name="options"/> holds option-supplied keyword values.
    /// </summary>
    /// <param name="requested">Canonicalized requested locales (CanonicalizeLocaleList output).</param>
    /// <param name="relevantKeys">The extension keys the service uses (for example <c>nu</c>).</param>
    /// <param name="options">Values for those keys given as options (null entries mean undefined).</param>
    /// <param name="keyData">Supported values of a key for a data locale, default first.</param>
    /// <param name="defaultLocale">The host's default locale, used when nothing matches.</param>
    internal static ResolvedLocale Resolve(
        IReadOnlyList<string> requested,
        string[] relevantKeys,
        IReadOnlyDictionary<string, string?> options,
        Func<string, string, string[]> keyData,
        string defaultLocale)
    {
        (string? found, string extension) = Match(requested);
        found ??= BestAvailableLocale(defaultLocale) ?? "en-US";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var supported = new List<KeyValuePair<string, string>>();
        foreach (string key in relevantKeys)
        {
            string[] data = keyData(found, key);
            string value = data.Length > 0 ? data[0] : "";
            string? addition = null;
            if (extension.Length > 0 && UnicodeExtensionValue(extension, key) is { } requestedValue)
            {
                if (requestedValue.Length > 0)
                {
                    if (Array.IndexOf(data, requestedValue) >= 0)
                    {
                        value = requestedValue;
                        addition = requestedValue;
                    }
                }
                else if (Array.IndexOf(data, "true") >= 0)
                {
                    value = "true";
                    addition = "";
                }
            }

            if (options.TryGetValue(key, out string? optionValue) && optionValue is not null
                && Array.IndexOf(data, optionValue) >= 0 && optionValue != value)
            {
                value = optionValue;
                addition = null;
            }

            result[key] = value;
            if (addition is not null)
            {
                supported.Add(new(key, addition == "true" ? "" : addition));
            }
        }

        string locale = found;
        if (supported.Count > 0)
        {
            LocaleId id = LocaleId.Parse(found)!;
            id.HasUnicodeExtension = true;
            foreach (var kv in supported)
            {
                id.SetKeyword(kv.Key, kv.Value);
            }

            locale = id.Canonicalize().ToString();
        }

        return new ResolvedLocale { Locale = locale, DataLocale = found, Keys = result };
    }

    /// <summary>
    /// GetOption for <c>localeMatcher</c> (ECMA-402 9.2.?): only <c>lookup</c> and <c>best fit</c> are allowed;
    /// both resolve with the lookup matcher here.
    /// </summary>
    /// <exception cref="IntlException">Any other value (RangeError).</exception>
    internal static void ValidateLocaleMatcher(string? value, string service)
    {
        if (value is not null and not ("lookup" or "best fit"))
        {
            throw IntlException.Range($"Value {value} out of range for Intl.{service} options property localeMatcher");
        }
    }

    /// <summary>
    /// The <c>numberingSystem</c> option check (ECMA-402 15.1.2 step: the value must match the Unicode locale
    /// <c>type</c> production, one or more groups of 3 to 8 alphanumerics joined by hyphens).
    /// </summary>
    /// <exception cref="IntlException">The value is not well formed (RangeError).</exception>
    internal static void ValidateNumberingSystem(string? value, string service)
    {
        if (value is null)
        {
            return;
        }

        int start = 0;
        while (true)
        {
            int end = value.IndexOf('-', start);
            if (end < 0)
            {
                end = value.Length;
            }

            int len = end - start;
            if (len is < 3 or > 8)
            {
                throw IntlException.Range($"Invalid numberingSystem : {value}");
            }

            for (int i = start; i < end; i++)
            {
                char c = value[i];
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'))
                {
                    throw IntlException.Range($"Invalid numberingSystem : {value}");
                }
            }

            if (end == value.Length)
            {
                return;
            }

            start = end + 1;
        }
    }

    /// <summary>
    /// SupportedLocales (9.2.10) with the lookup matcher: the requested locales an available locale serves.
    /// </summary>
    internal static List<string> SupportedLocales(IReadOnlyList<string> requested)
    {
        var result = new List<string>();
        foreach (string locale in requested)
        {
            if (BestAvailableLocale(SplitUnicodeExtension(locale).WithoutExtension) is not null)
            {
                result.Add(locale);
            }
        }

        return result;
    }

    /// <summary>
    /// The CLDR data tree serving a resolved data locale (for example <c>pt-BR</c> gives the Portuguese data).
    /// </summary>
    internal static DataTree DataFor(string dataLocale) => CldrData.GetLocale(dataLocale);
}
