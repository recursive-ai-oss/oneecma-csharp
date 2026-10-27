using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>One lexical element of a CLDR date pattern (UTS #35, Date Format Patterns): a run of one field letter or a literal.</summary>
internal readonly record struct PatternToken(char Letter, int Count, string Literal)
{
    internal bool IsField => Letter != '\0';

    /// <summary>
    /// Splits a pattern into field runs and literals. Single quotes quote text and <c>''</c> is an apostrophe
    /// (UTS #35 Part 4, quoting rules). Linear in the pattern length.
    /// </summary>
    internal static List<PatternToken> Parse(string pattern)
    {
        var tokens = new List<PatternToken>();
        var lit = new StringBuilder();
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c == '\'')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
                {
                    lit.Append('\'');
                    i += 2;
                    continue;
                }

                i++;
                while (i < pattern.Length)
                {
                    if (pattern[i] == '\'')
                    {
                        if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
                        {
                            lit.Append('\'');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    lit.Append(pattern[i++]);
                }

                continue;
            }

            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                if (lit.Length > 0)
                {
                    tokens.Add(new PatternToken('\0', 0, lit.ToString()));
                    lit.Clear();
                }

                int j = i;
                while (j < pattern.Length && pattern[j] == c)
                {
                    j++;
                }

                tokens.Add(new PatternToken(c, j - i, ""));
                i = j;
                continue;
            }

            lit.Append(c);
            i++;
        }

        if (lit.Length > 0)
        {
            tokens.Add(new PatternToken('\0', 0, lit.ToString()));
        }

        return tokens;
    }

    /// <summary>Writes a literal back with the quoting a pattern needs.</summary>
    internal static void AppendLiteral(StringBuilder sb, string text)
    {
        bool open = false;
        foreach (char c in text)
        {
            if (c == '\'')
            {
                sb.Append("''");
                continue;
            }

            bool letter = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
            if (letter && !open)
            {
                sb.Append('\'');
                open = true;
            }
            else if (!letter && open)
            {
                sb.Append('\'');
                open = false;
            }

            sb.Append(c);
        }

        if (open)
        {
            sb.Append('\'');
        }
    }
}

/// <summary>
/// A date-time skeleton: the fields a pattern shows, one letter and width per field (UTS #35, Date Format Patterns and
/// "Matching Skeletons"). The field indexes follow the order of the fields in a skeleton.
/// </summary>
internal sealed class DateSkeleton
{
    internal const int Era = 0, Year = 1, Quarter = 2, Month = 3, WeekOfYear = 4, WeekOfMonth = 5, Weekday = 6, DayOfYear = 7, WeekdayInMonth = 8, Day = 9, DayPeriod = 10, Hour = 11, Minute = 12, Second = 13, Fraction = 14, Zone = 15, FieldCount = 16;

    /// <summary>Fields before the day period belong to the date part; the day period and later fields to the time part.</summary>
    internal const int DateMask = (1 << DayPeriod) - 1, TimeMask = ((1 << FieldCount) - 1) & ~DateMask;

    // Distance weights: a type is "numeric" (positive) or textual (negative); a short text is -0x103 and so on.
    private const int Numeric = 0x100, Delta = 0x10, Narrow = -0x101, Shorter = -0x102, Short = -0x103, Long = -0x104;

    // (letter, field, base type, min width, max width)
    private static readonly (char L, int F, int T, int Min, int Max)[] Rows =
    [
        ('G', Era, Short, 1, 3), ('G', Era, Long, 4, 4), ('G', Era, Narrow, 5, 5),
        ('y', Year, Numeric, 1, 20), ('Y', Year, Numeric + Delta, 1, 20), ('u', Year, Numeric + (2 * Delta), 1, 20), ('r', Year, Numeric + (3 * Delta), 1, 20),
        ('U', Year, Short, 1, 3), ('U', Year, Long, 4, 4), ('U', Year, Narrow, 5, 5),
        ('Q', Quarter, Numeric, 1, 2), ('Q', Quarter, Short, 3, 3), ('Q', Quarter, Long, 4, 4), ('Q', Quarter, Narrow, 5, 5),
        ('q', Quarter, Numeric + Delta, 1, 2), ('q', Quarter, Short - Delta, 3, 3), ('q', Quarter, Long - Delta, 4, 4), ('q', Quarter, Narrow - Delta, 5, 5),
        ('M', Month, Numeric, 1, 2), ('M', Month, Short, 3, 3), ('M', Month, Long, 4, 4), ('M', Month, Narrow, 5, 5),
        ('L', Month, Numeric + Delta, 1, 2), ('L', Month, Short - Delta, 3, 3), ('L', Month, Long - Delta, 4, 4), ('L', Month, Narrow - Delta, 5, 5),
        ('w', WeekOfYear, Numeric, 1, 2), ('W', WeekOfMonth, Numeric + Delta, 1, 1),
        ('E', Weekday, Short, 1, 3), ('E', Weekday, Long, 4, 4), ('E', Weekday, Narrow, 5, 5), ('E', Weekday, Shorter, 6, 6),
        ('c', Weekday, Numeric + (2 * Delta), 1, 2), ('c', Weekday, Short - (2 * Delta), 3, 3), ('c', Weekday, Long - (2 * Delta), 4, 4), ('c', Weekday, Narrow - (2 * Delta), 5, 5), ('c', Weekday, Shorter - (2 * Delta), 6, 6),
        ('e', Weekday, Numeric + Delta, 1, 2), ('e', Weekday, Short - Delta, 3, 3), ('e', Weekday, Long - Delta, 4, 4), ('e', Weekday, Narrow - Delta, 5, 5), ('e', Weekday, Shorter - Delta, 6, 6),
        ('D', DayOfYear, Numeric, 1, 3), ('F', WeekdayInMonth, Numeric, 1, 1),
        ('d', Day, Numeric, 1, 2), ('g', Day, Numeric + Delta, 1, 20),
        ('a', DayPeriod, Short, 1, 3), ('a', DayPeriod, Long, 4, 4), ('a', DayPeriod, Narrow, 5, 5),
        ('b', DayPeriod, Short - Delta, 1, 3), ('b', DayPeriod, Long - Delta, 4, 4), ('b', DayPeriod, Narrow - Delta, 5, 5),
        ('B', DayPeriod, Short - (2 * Delta), 1, 3), ('B', DayPeriod, Long - (2 * Delta), 4, 4), ('B', DayPeriod, Narrow - (2 * Delta), 5, 5),
        ('H', Hour, Numeric + (10 * Delta), 1, 2), ('k', Hour, Numeric + (11 * Delta), 1, 2), ('h', Hour, Numeric, 1, 2), ('K', Hour, Numeric + Delta, 1, 2),
        ('m', Minute, Numeric, 1, 2), ('s', Second, Numeric, 1, 2), ('A', Second, Numeric + (2 * Delta), 1, 1000), ('S', Fraction, Numeric, 1, 1000),
        ('v', Zone, Short - (2 * Delta), 1, 1), ('v', Zone, Long - (2 * Delta), 4, 4),
        ('z', Zone, Short, 1, 3), ('z', Zone, Long, 4, 4),
        ('Z', Zone, Narrow - Delta, 1, 3), ('Z', Zone, Long - Delta, 4, 4), ('Z', Zone, Short - Delta, 5, 5),
        ('O', Zone, Short - Delta, 1, 1), ('O', Zone, Long - Delta, 4, 4),
        ('V', Zone, Short - Delta, 1, 1), ('V', Zone, Long - Delta, 2, 4),
        ('X', Zone, Narrow - Delta, 1, 1), ('X', Zone, Short - Delta, 2, 2), ('X', Zone, Long - Delta, 4, 4),
        ('x', Zone, Narrow - Delta, 1, 1), ('x', Zone, Short - Delta, 2, 2), ('x', Zone, Long - Delta, 4, 4),
    ];

    internal readonly char[] Chars = new char[FieldCount];
    internal readonly int[] Lengths = new int[FieldCount];
    internal readonly int[] Types = new int[FieldCount];

    /// <summary>The field index of a pattern letter, or -1 for letters the matcher ignores.</summary>
    internal static int FieldOf(char letter)
    {
        foreach (var r in Rows)
        {
            if (r.L == letter)
            {
                return r.F;
            }
        }

        return -1;
    }

    /// <summary>The base type of a field letter at a width (positive for numeric text), or 0 when the combination is unknown.</summary>
    internal static int BaseType(char letter, int count)
    {
        foreach (var r in Rows)
        {
            if (r.L == letter && count >= r.Min && count <= r.Max)
            {
                return r.T;
            }
        }

        return 0;
    }

    /// <summary>Reads a skeleton or a pattern: every field letter sets its field; literals are ignored.</summary>
    internal static DateSkeleton FromTokens(IEnumerable<PatternToken> tokens)
    {
        var sk = new DateSkeleton();
        foreach (PatternToken t in tokens)
        {
            if (!t.IsField)
            {
                continue;
            }

            int f = FieldOf(t.Letter);
            if (f < 0)
            {
                continue;
            }

            int count = t.Letter == 'E' && t.Count < 3 ? 3 : t.Count;
            int bt = BaseType(t.Letter, count);
            if (bt == 0)
            {
                continue;
            }

            sk.Chars[f] = t.Letter;
            sk.Lengths[f] = count;
            sk.Types[f] = bt > 0 ? bt + count : bt;
        }

        return sk;
    }

    internal static DateSkeleton Parse(string text) => FromTokens(PatternToken.Parse(text));

    /// <summary>The bit set of fields present.</summary>
    internal int FieldMask
    {
        get
        {
            int m = 0;
            for (int i = 0; i < FieldCount; i++)
            {
                if (Types[i] != 0)
                {
                    m |= 1 << i;
                }
            }

            return m;
        }
    }

    /// <summary>A canonical text for the skeleton (fields in index order), used as identity.</summary>
    internal string Key()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < FieldCount; i++)
        {
            if (Types[i] != 0)
            {
                sb.Append(Chars[i], Lengths[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The distance of this pattern skeleton from a request (UTS #35, Matching Skeletons): a field the pattern lacks costs 0x1000,
    /// a field it adds costs 0x10000, and a field in both costs the difference of their types.
    /// </summary>
    internal int DistanceFrom(DateSkeleton request, int includeMask, out int missing, out int extra)
    {
        int distance = 0;
        missing = 0;
        extra = 0;
        for (int i = 0; i < FieldCount; i++)
        {
            int mine = (includeMask & (1 << i)) == 0 ? 0 : request.Types[i];
            int other = Types[i];
            if (mine == other)
            {
                continue;
            }

            if (mine == 0)
            {
                distance += 0x10000;
                extra |= 1 << i;
            }
            else if (other == 0)
            {
                distance += 0x1000;
                missing |= 1 << i;
            }
            else if (i == Hour && (Chars[i] is 'h' or 'K') != (request.Chars[i] is 'h' or 'K'))
            {
                // A 12-hour pattern is no stand-in for a 24-hour request (or the reverse): its day period would come along.
                distance += 0x11000;
                missing |= 1 << i;
                extra |= 1 << i;
            }
            else
            {
                distance += Math.Abs(mine - other);
            }
        }

        return distance;
    }
}

/// <summary>
/// Chooses a date-time pattern for a skeleton from a locale's CLDR formats: the best-fit matching of UTS #35
/// (Part 9, "Matching Skeletons" and "Missing Skeleton Fields"). The available formats, the standard date and
/// time formats and the single-field canonical patterns are candidates; a pattern with the wrong widths is adjusted, a request the
/// candidates cannot satisfy together is split into a date part and a time part, and missing fields are appended with the
/// <c>appendItems</c> patterns.
/// </summary>
internal sealed class DateTimePatternGenerator
{
    private static readonly ConcurrentDictionary<string, DateTimePatternGenerator> Cache = new(StringComparer.Ordinal);

    private readonly List<(DateSkeleton Skeleton, string Pattern)> _entries = [];
    private readonly string[] _appendItems = new string[DateSkeleton.FieldCount];
    private readonly string[] _dateTimeGlue = new string[4];   // full, long, medium, short
    private readonly string[] _rangeGlue = new string[4];      // the same without the "at" wording, used between a date and a time interval
    private readonly string _decimal;
    private readonly string[] _fieldNames = new string[DateSkeleton.FieldCount];

    private DateTimePatternGenerator(DataTree data)
    {
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string skeletonText, string pattern, bool overwrite)
        {
            DateSkeleton sk = DateSkeleton.Parse(skeletonText);
            string key = sk.Key();
            if (byKey.TryGetValue(key, out int at))
            {
                if (overwrite)
                {
                    _entries[at] = (sk, pattern);
                }

                return;
            }

            byKey[key] = _entries.Count;
            _entries.Add((sk, pattern));
        }

        // The canonical single-field patterns come first, then the standard formats, then the CLDR available formats,
        // which replace an equal skeleton.
        foreach (string canonical in new[] { "G", "y", "Q", "M", "w", "W", "E", "D", "F", "d", "a", "H", "m", "s", "S", "v" })
        {
            Add(canonical, canonical, false);
        }

        foreach (string style in new[] { "full", "long", "medium", "short" })
        {
            foreach (string kind in new[] { "dateFormats", "timeFormats" })
            {
                if (data.GetString("gregorian", kind, style) is { } p)
                {
                    Add(DateSkeleton.FromTokens(PatternToken.Parse(p)).Key(), p, false);
                }
            }
        }

        foreach (string key in data.GetKeys("gregorian", "dateTimeFormats", "availableFormats"))
        {
            // "-alt-*" variants are alternative spellings and "-count-*" entries belong to week-based patterns, none of which an option set asks for.
            if (key.Contains("-alt-", StringComparison.Ordinal) || key.Contains("-count-", StringComparison.Ordinal))
            {
                continue;
            }

            if (data.GetString("gregorian", "dateTimeFormats", "availableFormats", key) is { } p)
            {
                Add(key, p, true);
            }
        }

        // Candidates are visited in the order of their skeleton's first letter (upper case before lower case), then in insertion order.
        var sorted = new List<(DateSkeleton, string)>(_entries.Count);
        foreach (var group in _entries.Select((e, i) => (e, i)).OrderBy(x => FirstChar(x.e.Skeleton), Comparer<char>.Default).ThenBy(x => x.i))
        {
            sorted.Add(group.e);
        }

        _entries = sorted;
        string[] styles = ["full", "long", "medium", "short"];
        for (int i = 0; i < 4; i++)
        {
            // The "at time" patterns join a date and a time into one phrase ("July 4, 2024 at 3:00 PM").
            _dateTimeGlue[i] = data.GetString("gregorian", "dateTimeFormats-atTime", "standard", styles[i]) ?? data.GetString("gregorian", "dateTimeFormats", styles[i]) ?? "{1} {0}";
            _rangeGlue[i] = data.GetString("gregorian", "dateTimeFormats", styles[i]) ?? "{1} {0}";
        }

        // Fields CLDR has no append pattern for (day period, fractional seconds, ...) use the root pattern.
        string?[] names = ["Era", "Year", "Quarter", "Month", "Week", null, "Day-Of-Week", null, null, "Day", null, "Hour", "Minute", "Second", null, "Timezone"];
        for (int i = 0; i < _appendItems.Length; i++)
        {
            _appendItems[i] = (names[i] is { } n ? data.GetString("gregorian", "dateTimeFormats", "appendItems", n) : null) ?? "{0} ├{2}: {1}┤";
        }

        for (int i = 0; i < _fieldNames.Length; i++)
        {
            _fieldNames[i] = (FieldKeys[i] is { } key ? data.GetString("dateFields", key, "displayName") : null) ?? "F" + (i).ToString(NumberFormatInfo.InvariantInfo);
        }

        _decimal = data.GetString("numbers", "symbols-numberSystem-latn", "decimal") ?? ".";
    }

    private static char FirstChar(DateSkeleton s)
    {
        for (int i = 0; i < DateSkeleton.FieldCount; i++)
        {
            if (s.Types[i] != 0)
            {
                return s.Chars[i];
            }
        }

        return '\0';
    }

    internal static DateTimePatternGenerator For(string dataLocale) =>
        Cache.GetOrAdd(dataLocale, static l => new DateTimePatternGenerator(CldrData.GetLocale(l)));

    /// <summary>
    /// The best pattern for a skeleton (for example <c>yMMMdhm</c>). Hour widths follow the request.
    /// </summary>
    internal string GetBestPattern(string skeletonText) => GetBestPattern(skeletonText, out _, out _, out _);

    /// <summary>
    /// The best pattern for a skeleton; when it had to be assembled from a date part and a time part, those parts and the joining
    /// pattern are returned too (null otherwise).
    /// </summary>
    internal string GetBestPattern(string skeletonText, out string? datePart, out string? timePart, out string? glue)
    {
        datePart = timePart = glue = null;
        DateSkeleton request = DateSkeleton.Parse(skeletonText);
        string best = BestRaw(request, -1, out int missing, out int extra, out DateSkeleton specified, out string pattern) ? pattern : "";
        if (missing == 0 && extra == 0)
        {
            return Adjust(best, request, specified, false);
        }

        int needed = request.FieldMask;
        string date = BestAppending(request, needed & DateSkeleton.DateMask);
        string time = BestAppending(request, needed & DateSkeleton.TimeMask);
        if (date.Length == 0)
        {
            return time;
        }

        if (time.Length == 0)
        {
            return date;
        }

        int style = GlueStyle(request);
        datePart = date;
        timePart = time;
        glue = _dateTimeGlue[style];
        return Glue(glue, time, date);
    }

    /// <summary>The joining style for a skeleton, by how much of the month is spelled out (UTS #35, "Missing Skeleton Fields"): 0 full ... 3 short.</summary>
    internal static int GlueStyle(DateSkeleton request)
    {
        if (request.Lengths[DateSkeleton.Month] == 4)
        {
            return request.Lengths[DateSkeleton.Weekday] > 0 ? 0 : 1;
        }

        return request.Lengths[DateSkeleton.Month] == 3 ? 2 : 3;
    }

    /// <summary>The plain date-time joining pattern of one style, as used between a date and a time range.</summary>
    internal string RangeGlueFor(int style) => _rangeGlue[style];

    /// <summary>The date-time joining pattern of one style (0 full, 1 long, 2 medium, 3 short).</summary>
    internal string GlueFor(int style) => _dateTimeGlue[style];

    /// <summary>Substitutes a time (<c>{0}</c>) and a date (<c>{1}</c>) into a joining pattern.</summary>
    internal static string Glue(string glue, string time, string date) => glue.Replace("{0}", time, StringComparison.Ordinal).Replace("{1}", date, StringComparison.Ordinal);

    private bool BestRaw(DateSkeleton request, int includeMask, out int missingMask, out int extraMask, out DateSkeleton specified, out string pattern)
    {
        int bestDistance = int.MaxValue;
        int bestMissing = -1;
        missingMask = 0;
        extraMask = 0;
        specified = request;
        pattern = "";
        bool found = false;
        foreach (var (sk, p) in _entries)
        {
            int d = sk.DistanceFrom(request, includeMask, out int miss, out int ext);
            // Among equal distances the earlier candidate wins unless the later one lacks a higher-numbered field (so "y" beats "d"
            // for a year and a day); candidates are in a fixed order, so the result does not depend on hash order.
            if (d < bestDistance || (d == bestDistance && bestMissing < miss))
            {
                bestDistance = d;
                bestMissing = miss;
                missingMask = miss;
                extraMask = ext;
                specified = sk;
                pattern = p;
                found = true;
                if (d == 0)
                {
                    // An exact match cannot be beaten.
                    break;
                }
            }
        }

        return found;
    }

    private string BestAppending(DateSkeleton request, int fields)
    {
        if (fields == 0)
        {
            return "";
        }

        BestRaw(request, fields, out int missing, out _, out DateSkeleton specified, out string raw);
        string result = Adjust(raw, request, specified, false);
        if (missing == 0)
        {
            return result;
        }

        int last = 0;
        while (missing != 0)
        {
            if (last == missing)
            {
                break;
            }

            const int SecondAndFraction = (1 << DateSkeleton.Second) | (1 << DateSkeleton.Fraction);
            if ((missing & SecondAndFraction) == (1 << DateSkeleton.Fraction) && (fields & SecondAndFraction) == SecondAndFraction)
            {
                result = Adjust(result, request, specified, true);
                missing &= ~(1 << DateSkeleton.Fraction);
                continue;
            }

            int starting = missing;
            BestRaw(request, missing, out int nextMissing, out _, out specified, out string temp);
            missing = nextMissing;
            temp = Adjust(temp, request, specified, false);
            int found = starting & ~missing;
            int top = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)found);
            string append = _appendItems[top];
            result = append.Replace("{0}", result, StringComparison.Ordinal).Replace("{1}", temp, StringComparison.Ordinal).Replace("{2}", QuotedFieldName(top), StringComparison.Ordinal);
            last = missing;
        }

        return result;
    }

    private static readonly string?[] FieldKeys = ["era", "year", "quarter", "month", "week", "weekOfMonth", "weekday", "dayOfYear", "weekdayOfMonth", "day", "dayperiod", "hour", "minute", "second", null, "zone"];

    /// <summary>The localized field name from <c>dateFields</c> as a quoted literal for use inside a pattern.</summary>
    private string QuotedFieldName(int field)
    {
        var sb = new StringBuilder();
        PatternToken.AppendLiteral(sb, _fieldNames[field]);
        return sb.ToString();
    }

    /// <summary>
    /// Changes the widths and letters of a candidate pattern to those requested (UTS #35, "Matching Skeletons", step on field widths).
    /// A field keeps the width the CLDR pattern chose when the candidate's own skeleton already had the requested width, or
    /// when exactly one of the two is numeric; the hour field takes the requested width.
    /// </summary>
    private string Adjust(string pattern, DateSkeleton request, DateSkeleton specified, bool fixFraction)
    {
        var sb = new StringBuilder();
        foreach (PatternToken t in PatternToken.Parse(pattern))
        {
            if (!t.IsField)
            {
                PatternToken.AppendLiteral(sb, t.Literal);
                continue;
            }

            int f = DateSkeleton.FieldOf(t.Letter);
            if (f < 0 || request.Lengths[f] == 0)
            {
                sb.Append(t.Letter, t.Count);
                continue;
            }

            int reqLen = request.Lengths[f];
            char reqChar = request.Chars[f];
            if (fixFraction && f == DateSkeleton.Second)
            {
                sb.Append(t.Letter, t.Count);
                PatternToken.AppendLiteral(sb, _decimal);
                sb.Append(request.Chars[DateSkeleton.Fraction], request.Lengths[DateSkeleton.Fraction]);
                continue;
            }

            // Month, weekday, hour and (unless 'Y' was asked) year keep the pattern's letter; the others take the requested one.
            char c = f != DateSkeleton.Hour && f != DateSkeleton.Month && f != DateSkeleton.Weekday && (f != DateSkeleton.Year || reqChar == 'Y') ? reqChar : t.Letter;
            if (c == 'E' && reqLen < 3)
            {
                reqLen = 3;
            }

            // Minutes and seconds keep the pattern's width; other fields take the requested width unless the candidate's own
            // skeleton already had that width or numeric and text forms differ.
            int width = reqLen;
            // The era always takes the requested width (CLDR patterns often spell it narrow where a short era was asked for).
            if (f == DateSkeleton.Era)
            {
                width = reqLen;
            }
            else if (f == DateSkeleton.Minute || f == DateSkeleton.Second || (reqChar != 'c' && reqChar != 'e' && KeepPatternWidth(t, f, specified, reqLen)))
            {
                width = t.Count;
            }

            sb.Append(c, width);
        }

        return sb.ToString();
    }

    private static bool KeepPatternWidth(PatternToken t, int field, DateSkeleton specified, int reqLen)
    {
        int skelLen = specified.Lengths[field];
        bool patNumeric = DateSkeleton.BaseType(t.Letter, t.Count) > 0;
        bool skelNumeric = specified.Types[field] > 0;
        return skelLen == reqLen || (patNumeric && !skelNumeric) || (skelNumeric && !patNumeric);
    }

    /// <summary>The skeleton of a pattern (UTS #35 "skeleton from pattern"): its field letters in field order, with widths.</summary>
    internal static string SkeletonOf(string pattern) => DateSkeleton.FromTokens(PatternToken.Parse(pattern)).Key();
}
