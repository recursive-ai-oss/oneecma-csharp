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
                // Currencies some region uses today (a region entry with no end date).
                var codes = new SortedSet<string>(StringComparer.Ordinal);
                DataTree sup = CldrData.Supplemental;
                foreach (string region in sup.GetKeys("currencyData", "region"))
                {
                    int n = sup.ArrayLength("currencyData", "region", region);
                    for (int i = 0; i < n; i++)
                    {
                        foreach (string code in sup.GetKeys("currencyData", "region", region, i.ToString(NumberFormatInfo.InvariantInfo)))
                        {
                            string at = i.ToString(NumberFormatInfo.InvariantInfo);
                            if (sup.GetString("currencyData", "region", region, at, code, "_to") is null
                                && sup.GetString("currencyData", "region", region, at, code, "_tender") != "false")
                            {
                                codes.Add(code);
                            }
                        }
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
                        if (!canonical.StartsWith("Etc/", StringComparison.Ordinal))
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
}
