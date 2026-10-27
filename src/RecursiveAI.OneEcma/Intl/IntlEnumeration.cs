using System.Globalization;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>Intl.supportedValuesOf (ECMA-402 8.3.2): the values each key supports, sorted.</summary>
internal static class IntlEnumeration
{
    /// <summary>The keys <see cref="SupportedValuesOf"/> accepts.</summary>
    internal static readonly string[] Keys = ["calendar", "collation", "currency", "numberingSystem", "timeZone", "unit"];

    /// <summary>Values of one key, in ascending code unit order, without duplicates.</summary>
    /// <exception cref="IntlException">The key is not one of <see cref="Keys"/> (RangeError).</exception>
    internal static string[] SupportedValuesOf(string key)
    {
        switch (key)
        {
            case "calendar":
                return ["gregory"];
            case "numberingSystem":
                return ["latn"];
            case "unit":
                return NumberFormatService.SanctionedUnits;
            case "collation":
            {
                // Types some supported language has tailoring rules for, as BCP 47 type names; standard and search are excluded.
                var types = new SortedSet<string>(StringComparer.Ordinal);
                DataTree sup = CldrData.Supplemental;
                foreach (string language in sup.GetKeys("collationTailoring"))
                {
                    foreach (string type in sup.GetKeys("collationTailoring", language))
                    {
                        string bcp = KeywordAliases.CanonicalType("co", type);
                        if (bcp is not ("standard" or "search"))
                        {
                            types.Add(bcp);
                        }
                    }
                }

                return [.. types];
            }

            case "currency":
            {
                // Every currency the English display names know: DisplayNames accepts exactly these (and any three letters as a fallback code).
                DataTree en = CldrData.GetLocale("en");
                var codes = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string code in en.GetKeys("currencies"))
                {
                    if (code.Length == 3 && en.GetString("currencies", code, "displayName") is not null)
                    {
                        codes.Add(code);
                    }
                }

                return [.. codes];
            }

            case "timeZone":
            {
                var zones = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string k in CldrData.Supplemental.GetKeys("bcp47", "timezone", "tz"))
                {
                    if (!k.StartsWith('_') && CldrData.Supplemental.GetString("bcp47", "timezone", "tz", k, "_alias") is { } alias)
                    {
                        string canonical = alias.Split(' ')[0];
                        if (canonical == "Etc/UTC")
                        {
                            zones.Add("UTC");
                        }
                        else if (!canonical.StartsWith("Etc/", StringComparison.Ordinal) || IsFixedOffsetZone(canonical))
                        {
                            zones.Add(canonical);
                        }
                    }
                }

                return [.. zones];
            }

            default:
                throw IntlException.Range($"Invalid key : {key}");
        }
    }

    // Etc/GMT+1 ... Etc/GMT-14: zones that belong to no continent but are primary identifiers all the same (ECMA-402 6.5.3).
    private static bool IsFixedOffsetZone(string id) =>
        id.StartsWith("Etc/GMT", StringComparison.Ordinal) && id.Length > 8 && id[7] is '+' or '-' && id.AsSpan(8).IndexOfAnyExceptInRange('0', '9') < 0;
}
