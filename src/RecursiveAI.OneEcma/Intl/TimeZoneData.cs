using System.Globalization;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>
/// The offset of a time zone at one instant: milliseconds east of UTC (zones before standard time was
/// introduced have offsets with seconds) and whether daylight saving time is in effect.
/// </summary>
internal readonly record struct TimeZoneOffset(int OffsetMs, bool IsDaylight);

/// <summary>
/// Where the formatting services get zone offsets from. The services never read the operating system:
/// the host supplies the rules (a tzdata reader, a recorded table in tests, or <see cref="BclTimeZoneSource"/>).
/// </summary>
internal interface ITimeZoneSource
{
    /// <summary>The offset of the IANA zone <paramref name="zoneId"/> at <paramref name="epochMs"/> (UTC milliseconds); false when the zone is unknown to the source.</summary>
    bool TryGetOffset(string zoneId, double epochMs, out TimeZoneOffset offset);
}

/// <summary>
/// Time zone identifiers (ECMA-402 section 6.5): validation, case-insensitive lookup, canonicalization
/// and the offset-zone syntax. Names come from the CLDR BCP 47 time zone data, whose first alias is the canonical id
/// (so <c>Asia/Kolkata</c> canonicalizes to <c>Asia/Calcutta</c>, as ICU-based engines do).
/// </summary>
internal static class TimeZoneData
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? s_canonical;   // lower-case id -> canonical id
    private static string[]? s_supported;

    /// <summary>
    /// Validates and canonicalizes a <c>timeZone</c> option (ECMA-402 11.1.2 steps for timeZone):
    /// an offset time zone (<c>+01</c>, <c>+0100</c>, <c>+01:00</c>) becomes <c>+HH:MM</c>; an IANA name is matched
    /// ignoring case and returned in its canonical spelling.
    /// </summary>
    /// <returns>The canonical identifier, or null when the string is not a valid time zone.</returns>
    internal static string? Canonicalize(string id)
    {
        if (id.Length > 0 && id[0] is '+' or '-' or '\u2212')
        {
            return ParseOffset(id) is { } ms ? FormatOffsetId(ms) : null;
        }

        Dictionary<string, string> map = Map();
        return map.TryGetValue(id.ToLowerInvariantAscii(), out string? canonical) ? canonical : null;
    }

    /// <summary>True when the identifier is an offset time zone such as <c>+05:30</c>.</summary>
    internal static bool IsOffsetId(string id) => id.Length > 0 && id[0] is '+' or '-';

    /// <summary>The offset in milliseconds of a canonical offset identifier (<c>+05:30</c>).</summary>
    internal static int OffsetOf(string offsetId) => ParseOffset(offsetId) ?? 0;

    /// <summary>
    /// The value of <c>Intl.supportedValuesOf("timeZone")</c> (ECMA-402 8.3.2): the canonical identifiers, without the
    /// <c>Etc/</c> and fixed-offset aliases, ascending in code unit order.
    /// </summary>
    internal static string[] Supported()
    {
        lock (Gate)
        {
            if (s_supported is null)
            {
                var set = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string canonical in Map().Values)
                {
                    if (!canonical.StartsWith("Etc/", StringComparison.Ordinal) && canonical != "UTC")
                    {
                        set.Add(canonical);
                    }
                }

                s_supported = [.. set];
            }

            return s_supported;
        }
    }

    private static Dictionary<string, string> Map()
    {
        lock (Gate)
        {
            if (s_canonical is not null)
            {
                return s_canonical;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            DataTree sup = CldrData.Supplemental;
            foreach (string key in sup.GetKeys("bcp47", "timezone", "tz"))
            {
                if (key.StartsWith('_') || key == "unk" || sup.GetString("bcp47", "timezone", "tz", key, "_alias") is not { } aliasList)
                {
                    continue;
                }

                string[] aliases = aliasList.Split(' ');
                // Engines name the UTC family "UTC" (ECMA-402 6.5.2: Etc/UTC and Etc/GMT are not primary identifiers).
                string canonical = aliases[0] is "Etc/UTC" or "Etc/GMT" ? "UTC" : aliases[0];
                foreach (string alias in aliases)
                {
                    // The four legacy US rule zones stay zones of their own in ICU-based engines, though tzdata now links them.
                    map[alias.ToLowerInvariantAscii()] = alias is "EST5EDT" or "CST6CDT" or "MST7MDT" or "PST8PDT" ? alias : canonical;
                }
            }

            return s_canonical = map;
        }
    }

    /// <summary>Parses <c>[+-]HH</c>, <c>[+-]HHMM</c> or <c>[+-]HH:MM</c> (ECMA-402 11.1.? ParseTimeZoneIdentifier) into milliseconds.</summary>
    private static int? ParseOffset(string s)
    {
        if (s.Length is not (3 or 5 or 6) || s[0] is not ('+' or '-'))
        {
            return null;
        }

        int Two(int at) => s[at] is >= '0' and <= '9' && s[at + 1] is >= '0' and <= '9' ? ((s[at] - '0') * 10) + (s[at + 1] - '0') : -1;
        int h = Two(1);
        int m = 0;
        if (s.Length == 5)
        {
            m = Two(3);
        }
        else if (s.Length == 6)
        {
            if (s[3] != ':')
            {
                return null;
            }

            m = Two(4);
        }

        if (h is < 0 or > 23 || m is < 0 or > 59)
        {
            return null;
        }

        int ms = ((h * 60) + m) * 60000;
        return s[0] == '-' ? -ms : ms;
    }

    private static string FormatOffsetId(int ms)
    {
        int abs = Math.Abs(ms) / 60000;
        return string.Create(CultureInfo.InvariantCulture, $"{(ms < 0 ? '-' : '+')}{abs / 60:00}:{abs % 60:00}");
    }
}

/// <summary>
/// An <see cref="ITimeZoneSource"/> over the BCL <c>TimeZoneInfo</c> rules of the machine (for tests and host wiring; the formatting
/// services never construct it). <c>TimeZoneInfo</c> only covers years 1 to 9999, so instants outside that range use the offset at the nearest end.
/// </summary>
internal sealed class BclTimeZoneSource : ITimeZoneSource
{
    private readonly Dictionary<string, TimeZoneInfo?> _zones = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public bool TryGetOffset(string zoneId, double epochMs, out TimeZoneOffset offset)
    {
        offset = default;
        TimeZoneInfo? tz;
        lock (_zones)
        {
            if (!_zones.TryGetValue(zoneId, out tz))
            {
                try
                {
                    tz = zoneId == "UTC" ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                }
                catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    tz = null;
                }

                _zones[zoneId] = tz;
            }
        }

        if (tz is null)
        {
            return false;
        }

        const double Min = -62135596800000.0 + 86400000.0, Max = 253402300799000.0 - 86400000.0;
        DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Clamp(Math.Floor(epochMs), Min, Max));
        TimeSpan span = tz.GetUtcOffset(at);
        offset = new TimeZoneOffset((int)span.TotalMilliseconds, tz.IsDaylightSavingTime(at));
        return true;
    }
}
