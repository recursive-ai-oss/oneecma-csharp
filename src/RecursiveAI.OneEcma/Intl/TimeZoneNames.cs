using System.Globalization;
using System.Text;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>The styles of the <c>timeZoneName</c> option (ECMA-402 11.1.2).</summary>
internal enum ZoneNameStyle
{
    Short,
    Long,
    ShortOffset,
    LongOffset,
    ShortGeneric,
    LongGeneric,
}

/// <summary>
/// Time zone display names from CLDR (UTS #35 Part 4, "Time Zone Names"): zone and metazone names, the localized GMT
/// offset format, generic location names, with the fallbacks the standard defines.
/// </summary>
internal static class TimeZoneNames
{
    // Generic names check this far either side of the instant for daylight time (184 days, about half a year).
    private const double DaylightCheckMs = 184 * 86400000.0;

    /// <summary>Formats the name of a zone for one instant.</summary>
    /// <param name="data">The locale data.</param>
    /// <param name="zoneId">The canonical zone id (an IANA name or an offset id such as <c>+05:30</c>).</param>
    /// <param name="epochMs">The instant, for metazone periods.</param>
    /// <param name="offset">The offset at that instant.</param>
    /// <param name="style">The requested style.</param>
    /// <param name="offsetAt">The zone's offset at another instant, for the daylight check of generic names; null when unknown.</param>
    internal static string Format(DataTree data, string zoneId, double epochMs, TimeZoneOffset offset, ZoneNameStyle style, Func<double, TimeZoneOffset?> offsetAt)
    {
        switch (style)
        {
            case ZoneNameStyle.ShortOffset:
                return LocalizedGmt(data, offset.OffsetMs, false);
            case ZoneNameStyle.LongOffset:
                return LocalizedGmt(data, offset.OffsetMs, true);
        }

        bool isLong = style is ZoneNameStyle.Long or ZoneNameStyle.LongGeneric;
        if (TimeZoneData.IsOffsetId(zoneId))
        {
            return LocalizedGmt(data, offset.OffsetMs, isLong);
        }

        string cldrId = zoneId == "UTC" ? "Etc/UTC" : zoneId;
        string? mz = MetaZoneAt(cldrId, epochMs);
        string width = isLong ? "long" : "short";
        if (style is ZoneNameStyle.Short or ZoneNameStyle.Long)
        {
            string kind = offset.IsDaylight ? "daylight" : "standard";
            string? name = ZoneLevel(data, cldrId, width, kind) ?? (mz is null ? null : MetaLevel(data, mz, width, kind));
            return name ?? LocalizedGmt(data, offset.OffsetMs, isLong);
        }

        // Generic non-location name (UTS #35, Time Zone Names): the zone's own, else the metazone's; a zone that does not observe
        // daylight time near this instant uses the metazone's standard name when that differs from its generic name.
        string? generic = ZoneLevel(data, cldrId, width, "generic");
        if (generic is not null)
        {
            return generic;
        }

        if (mz is not null)
        {
            string? mzGeneric = MetaLevel(data, mz, width, "generic");
            bool useStandard = !offset.IsDaylight
                && offsetAt(epochMs - DaylightCheckMs) is { IsDaylight: false }
                && offsetAt(epochMs + DaylightCheckMs) is { IsDaylight: false };
            if (useStandard && (ZoneLevel(data, cldrId, width, "standard") ?? MetaLevel(data, mz, width, "standard")) is { } std
                && !string.Equals(std, mzGeneric, StringComparison.OrdinalIgnoreCase))
            {
                return std;
            }

            if (mzGeneric is not null)
            {
                string? region = CldrData.Supplemental.GetString("zoneRegions", cldrId);
                string? golden = region is null ? null : GoldenZone(mz, region);
                return golden is null || golden == cldrId ? mzGeneric : PartialLocation(data, cldrId, region!, mzGeneric);
            }
        }

        return LocationName(data, cldrId, offset, isLong);
    }

    private static Dictionary<string, string>? s_golden;

    /// <summary>The reference zone of a metazone in a region, else the metazone's zone for the world (<c>001</c>).</summary>
    private static string? GoldenZone(string mz, string region)
    {
        lock (Gate)
        {
            if (s_golden is null)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                DataTree sup = CldrData.Supplemental;
                int n = sup.ArrayLength("metaZoneMap");
                for (int i = 0; i < n; i++)
                {
                    string at = i.ToString(NumberFormatInfo.InvariantInfo);
                    if (sup.GetString("metaZoneMap", at, "mapZone", "_other") is { } other
                        && sup.GetString("metaZoneMap", at, "mapZone", "_territory") is { } territory
                        && sup.GetString("metaZoneMap", at, "mapZone", "_type") is { } type)
                    {
                        map[other + "|" + territory] = type;
                    }
                }

                s_golden = map;
            }

            string? id = s_golden.TryGetValue(mz + "|" + region, out string? z) ? z : s_golden.GetValueOrDefault(mz + "|001");
            return id is null ? null : TimeZoneData.Canonicalize(id);
        }
    }

    private static readonly object Gate = new();

    /// <summary>A metazone name for a zone that is not the metazone's reference zone: <c>Pacific Time (Vancouver)</c>.</summary>
    private static string PartialLocation(DataTree data, string zoneId, string region, string mzName)
    {
        string? country = data.GetString("territories", region);
        string location = country is not null && IsPrimaryZone(CldrData.Supplemental, zoneId, region) ? country : ZoneLevelRaw(data, zoneId, "exemplarCity") ?? CityOf(zoneId);
        string fallback = data.GetString("timeZoneNames", "fallbackFormat") ?? "{1} ({0})";
        return fallback.Replace("{1}", mzName, StringComparison.Ordinal).Replace("{0}", location, StringComparison.Ordinal);
    }

    /// <summary>
    /// The generic location name (UTS #35, Time Zone Names, generic location format): the region format around the country name when
    /// the zone is its country's only or primary zone, else around the exemplar city. Zones without a country (such as <c>Etc/GMT+5</c>)
    /// have no location and use the localized GMT format.
    /// </summary>
    private static string LocationName(DataTree data, string zoneId, TimeZoneOffset offset, bool isLong)
    {
        DataTree sup = CldrData.Supplemental;
        string? region = sup.GetString("zoneRegions", zoneId);
        if (region is null)
        {
            return LocalizedGmt(data, offset.OffsetMs, isLong);
        }

        string format = data.GetString("timeZoneNames", "regionFormat") ?? "{0}";
        string? country = data.GetString("territories", region);
        if (country is not null && IsPrimaryZone(sup, zoneId, region))
        {
            return format.Replace("{0}", country, StringComparison.Ordinal);
        }

        string city = ZoneLevelRaw(data, zoneId, "exemplarCity") ?? CityOf(zoneId);
        return format.Replace("{0}", city, StringComparison.Ordinal);
    }

    private static readonly Dictionary<string, int> ZonesPerRegion = new(StringComparer.Ordinal);

    private static bool IsPrimaryZone(DataTree sup, string zoneId, string region)
    {
        if (sup.GetString("primaryZones", region) is { } primary && TimeZoneData.Canonicalize(primary) == zoneId)
        {
            return true;
        }

        lock (ZonesPerRegion)
        {
            if (ZonesPerRegion.Count == 0)
            {
                foreach (string id in sup.GetKeys("zoneRegions"))
                {
                    string r = sup.GetString("zoneRegions", id)!;
                    ZonesPerRegion[r] = ZonesPerRegion.GetValueOrDefault(r) + 1;
                }
            }

            return ZonesPerRegion.GetValueOrDefault(region) == 1;
        }
    }

    private static string CityOf(string zoneId)
    {
        string last = zoneId[(zoneId.LastIndexOf('/') + 1)..];
        return last.Replace('_', ' ');
    }

    private static string? ZoneLevelRaw(DataTree data, string zoneId, string leaf)
    {
        string[] parts = zoneId.Split('/');
        var path = new List<string> { "timeZoneNames", "zone" };
        path.AddRange(parts);
        path.Add(leaf);
        return Clean(data.GetString([.. path]));
    }

    private static string? ZoneLevel(DataTree data, string zoneId, string width, string kind)
    {
        string[] parts = zoneId.Split('/');
        var path = new List<string> { "timeZoneNames", "zone" };
        path.AddRange(parts);
        path.Add(width);
        path.Add(kind);
        return Clean(data.GetString([.. path]));
    }

    private static string? MetaLevel(DataTree data, string mz, string width, string kind) =>
        Clean(data.GetString("timeZoneNames", "metazone", mz, width, kind));

    private static string? Clean(string? s) => s is null || s == DataTree.EmptyMarker ? null : s;

    /// <summary>The metazone a zone belongs to at an instant (supplemental metazone periods, boundaries in UTC).</summary>
    // The reference engine stops using metazone names in the last hours of year 9999 (253402297200000 is 9999-12-31T23:00Z).
    private const double MetaZoneEnd = 253402297200000;

    private static string? MetaZoneAt(string zoneId, double epochMs)
    {
        DataTree sup = CldrData.Supplemental;
        string[] parts = zoneId.Split('/');
        var path = new List<string> { "metaZoneInfo" };
        path.AddRange(parts);
        int n = sup.ArrayLength([.. path]);
        for (int i = 0; i < n; i++)
        {
            string at = i.ToString(NumberFormatInfo.InvariantInfo);
            string[] p = [.. path, at, "usesMetazone"];
            string? mz = sup.GetString([.. p, "_mzone"]);
            if (mz is null)
            {
                continue;
            }

            // A period without a start begins at 1970-01-01, the earliest date the names are used for.
            double start = sup.GetString([.. p, "_from"]) is { } from && ParseUtc(from) is { } f ? f : 0;
            if (epochMs < start || epochMs > MetaZoneEnd)
            {
                continue;
            }

            if (sup.GetString([.. p, "_to"]) is { } to && ParseUtc(to) is { } t && epochMs >= t)
            {
                continue;
            }

            return mz;
        }

        return null;
    }

    private static double? ParseUtc(string s)
    {
        // "yyyy-MM-dd HH:mm" in UTC.
        if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm", NumberFormatInfo.InvariantInfo, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime dt))
        {
            return (dt - DateTime.UnixEpoch).TotalMilliseconds;
        }

        return null;
    }

    /// <summary>
    /// The localized GMT format (UTS #35, "Time Zone Names", localized GMT format): <c>GMT-8</c> or <c>GMT-08:00</c>, built from
    /// the locale's <c>gmtFormat</c> and <c>hourFormat</c>; zero is <c>gmtZeroFormat</c>.
    /// </summary>
    internal static string LocalizedGmt(DataTree data, int offsetMs, bool isLong)
    {
        string hourFormat = data.GetString("timeZoneNames", "hourFormat") ?? "+HH:mm;-HH:mm";
        int semi = hourFormat.IndexOf(';', StringComparison.Ordinal);
        // Zero is written with the positive pattern (GMT+0), as ECMA-402 engines do, not with the locale's zero-offset text.
        string pattern = offsetMs >= 0 ? hourFormat[..semi] : hourFormat[(semi + 1)..];
        int total = Math.Abs(offsetMs) / 1000;
        int h = total / 3600, m = total / 60 % 60, s = total % 60;
        var sb = new StringBuilder();
        foreach (PatternToken t in PatternToken.Parse(pattern))
        {
            if (!t.IsField)
            {
                sb.Append(t.Literal);
            }
            else if (t.Letter == 'H')
            {
                sb.Append(h.ToString(isLong ? "00" : "0", NumberFormatInfo.InvariantInfo));
            }
            else if (t.Letter == 'm')
            {
                if (isLong || m != 0 || s != 0)
                {
                    sb.Append(m.ToString("00", NumberFormatInfo.InvariantInfo));
                }
                else
                {
                    // Short form without minutes: drop the separator already written.
                    while (sb.Length > 0 && !char.IsAsciiDigit(sb[^1]))
                    {
                        sb.Length--;
                    }
                }
            }
        }

        if (s != 0)
        {
            char sep = pattern.Contains('.', StringComparison.Ordinal) ? '.' : ':';
            sb.Append(sep).Append(s.ToString("00", NumberFormatInfo.InvariantInfo));
        }

        string gmt = data.GetString("timeZoneNames", "gmtFormat") ?? "GMT{0}";
        return gmt.Replace("{0}", sb.ToString(), StringComparison.Ordinal);
    }
}
