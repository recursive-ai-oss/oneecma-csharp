using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma;

/// <summary>
/// Date.parse (21.4.3.2): the Date Time String Format of 21.4.1.32 exactly, then the legacy forms real code relies on
/// (see docs/date.md). The result is a time value before TimeClip, NaN when the string is not recognised.
/// </summary>
internal static class DateParser
{
    /// <param name="s">The string to parse.</param>
    /// <param name="host">The host, for local-time interpretation.</param>
    internal static double Parse(string s, IJsHost host)
    {
        if (TryParseIso(s, host, out double iso))
        {
            return iso;
        }

        return ParseLegacy(s, host);
    }

    private static bool Digits(string s, ref int i, int count, out int value)
    {
        value = 0;
        if (i + count > s.Length)
        {
            return false;
        }

        for (int k = 0; k < count; k++)
        {
            char c = s[i + k];
            if (c is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        i += count;
        return true;
    }

    // ---- 21.4.1.32 Date Time String Format -------------------------------------------------------------

    private static bool TryParseIso(string s, IJsHost host, out double result)
    {
        result = double.NaN;
        int i = 0;
        int year;
        if (s.Length > 0 && s[0] is '+' or '-')
        {
            bool neg = s[0] == '-';
            i = 1;
            if (!Digits(s, ref i, 6, out year) || (neg && year == 0))
            {
                // -000000 is not a valid expanded year.
                return false;
            }

            if (neg)
            {
                year = -year;
            }
        }
        else if (!Digits(s, ref i, 4, out year))
        {
            return false;
        }

        int month = 1, day = 1;
        if (i < s.Length && s[i] == '-')
        {
            i++;
            if (!Digits(s, ref i, 2, out month))
            {
                return false;
            }

            if (i < s.Length && s[i] == '-')
            {
                i++;
                if (!Digits(s, ref i, 2, out day))
                {
                    return false;
                }
            }
        }

        // Day overflow within 31 days rolls into the next month, as the first engines to ship the format did.
        if (month is < 1 or > 12 || day is < 1 or > 31)
        {
            return false;
        }

        int hour = 0, minute = 0, second = 0;
        double ms = 0;
        bool hasTime = false;
        double? offset = null;
        if (i < s.Length && s[i] is 'T' or 't')
        {
            hasTime = true;
            i++;
            if (!Digits(s, ref i, 2, out hour) || i >= s.Length || s[i] != ':')
            {
                return false;
            }

            i++;
            if (!Digits(s, ref i, 2, out minute))
            {
                return false;
            }

            if (i < s.Length && s[i] == ':')
            {
                i++;
                if (!Digits(s, ref i, 2, out second))
                {
                    return false;
                }

                if (i < s.Length && s[i] == '.')
                {
                    i++;
                    int start = i;
                    double scale = 100;
                    while (i < s.Length && s[i] is >= '0' and <= '9')
                    {
                        ms += (s[i] - '0') * scale;
                        scale /= 10;
                        i++;
                    }

                    if (i == start)
                    {
                        return false;
                    }

                    ms = Math.Floor(ms);
                }
            }

            if (hour > 24 || minute > 59 || second > 59 || (hour == 24 && (minute != 0 || second != 0 || ms != 0)))
            {
                return false;
            }

            if (i < s.Length && s[i] is 'Z' or 'z')
            {
                offset = 0;
                i++;
            }
            else if (i < s.Length && s[i] is '+' or '-')
            {
                bool neg = s[i] == '-';
                i++;
                if (!Digits(s, ref i, 2, out int oh))
                {
                    return false;
                }

                if (i < s.Length && s[i] == ':')
                {
                    i++;
                }

                if (!Digits(s, ref i, 2, out int om) || oh > 23 || om > 59)
                {
                    return false;
                }

                offset = (neg ? -1 : 1) * ((oh * 60) + om) * 60000.0;
            }
        }
        else if (i < s.Length && s[i] is 'Z' or 'z')
        {
            // A date-only form with a trailing Z is still UTC.
            i++;
            offset = 0;
        }

        if (i != s.Length)
        {
            return false;
        }

        double dayNum = DateMath.MakeDay(year, month - 1, day);
        double time = DateMath.MakeTime(hour, minute, second, ms);
        double t = DateMath.MakeDate(dayNum, time);
        if (offset is { } o)
        {
            result = t - o;
        }
        else if (hasTime)
        {
            // Date-time forms without an offset are local time (21.4.1.32).
            result = DateMath.UtcFromLocal(host, t);
        }
        else
        {
            result = t;
        }

        return true;
    }

    // ---- legacy forms ----------------------------------------------------------------------------------

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private static int ZoneAbbreviation(string w) => w switch
    {
        "est" => -5,
        "edt" => -4,
        "cst" => -6,
        "cdt" => -5,
        "mst" => -7,
        "mdt" => -6,
        "pst" => -8,
        "pdt" => -7,
        _ => 99,
    };

    private readonly record struct Number(long Value, int Digits);

    private static double ParseLegacy(string s, IJsHost host)
    {
        var nums = new List<Number>(3);
        int month = 0; // 1 to 12 when a month name was seen
        bool seenNumber = false, hasTime = false, utcSeen = false;
        int hour = 0, minute = 0, second = 0, ms = 0;
        int ampm = 0; // 1 am, 2 pm
        double? offset = null;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f' or ',' or '/' or '.')
            {
                i++;
            }
            else if (c == '(')
            {
                // Parenthesised text is a comment; an unclosed one runs to the end.
                int depth = 0;
                while (i < s.Length)
                {
                    if (s[i] == '(')
                    {
                        depth++;
                    }
                    else if (s[i] == ')' && --depth == 0)
                    {
                        i++;
                        break;
                    }

                    i++;
                }
            }
            else if (c is >= '0' and <= '9')
            {
                int start = i;
                long value = 0;
                while (i < s.Length && s[i] is >= '0' and <= '9')
                {
                    if (value < 100000000000)
                    {
                        value = (value * 10) + (s[i] - '0');
                    }

                    i++;
                }

                int digits = i - start;
                seenNumber = true;
                if (i < s.Length && s[i] == ':')
                {
                    if (hasTime || value > 1000)
                    {
                        return double.NaN;
                    }

                    hasTime = true;
                    hour = (int)value;
                    i++;
                    minute = ReadSmall(s, ref i);
                    if (i < s.Length && s[i] == ':')
                    {
                        i++;
                        second = ReadSmall(s, ref i);
                        if (i < s.Length && s[i] == '.')
                        {
                            if (i + 1 >= s.Length || s[i + 1] is < '0' or > '9')
                            {
                                return double.NaN;
                            }

                            i++;
                            double scale = 100;
                            double frac = 0;
                            while (i < s.Length && s[i] is >= '0' and <= '9')
                            {
                                frac += (s[i] - '0') * scale;
                                scale /= 10;
                                i++;
                            }

                            ms = (int)Math.Floor(frac);
                        }

                        // Further colon-separated fields are accepted and ignored.
                        while (i < s.Length && s[i] == ':')
                        {
                            i++;
                            ReadSmall(s, ref i);
                        }
                    }
                }
                else
                {
                    if (nums.Count == 3)
                    {
                        return double.NaN;
                    }

                    nums.Add(new Number(value, digits));
                }

                if (i < s.Length && char.IsAsciiLetter(s[i]) && !(s[i] is 'Z' or 'z' && (i + 1 >= s.Length || !char.IsAsciiLetter(s[i + 1]))))
                {
                    // A word glued to a number ("4th", "10:00pm") is not a form we know.
                    return double.NaN;
                }
            }
            else if (c is '+' or '-')
            {
                i++;
                if (!hasTime && !utcSeen && offset is null)
                {
                    // A date separator. After a number it must touch its neighbours ("1-1-1", "Jul-04-2024"); "1 -5" is not a date.
                    if (seenNumber && i >= 2 && !char.IsAsciiLetterOrDigit(s[i - 2]))
                    {
                        return double.NaN;
                    }

                    continue;
                }

                int start = i;
                long v = 0;
                while (i < s.Length && s[i] is >= '0' and <= '9')
                {
                    v = (v * 10) + (s[i] - '0');
                    i++;
                }

                int len = i - start;
                if (len == 0 || len > 4)
                {
                    return double.NaN;
                }

                long hh, mm;
                if (i < s.Length && s[i] == ':' && len <= 2)
                {
                    i++;
                    int ms0 = i;
                    long m2 = 0;
                    while (i < s.Length && s[i] is >= '0' and <= '9')
                    {
                        m2 = (m2 * 10) + (s[i] - '0');
                        i++;
                    }

                    if (i == ms0 || i - ms0 > 2)
                    {
                        return double.NaN;
                    }

                    hh = v;
                    mm = m2;
                }
                else if (len <= 2)
                {
                    hh = v;
                    mm = 0;
                }
                else
                {
                    hh = v / 100;
                    mm = v % 100;
                }

                offset = (c == '-' ? -1 : 1) * (((hh * 60) + mm) * 60000.0);
            }
            else if (char.IsAsciiLetter(c))
            {
                int start = i;
                while (i < s.Length && char.IsAsciiLetter(s[i]))
                {
                    i++;
                }

                string w = s.Substring(start, i - start).ToLowerInvariantAscii();
                if (w is "am" or "pm")
                {
                    if (!hasTime || ampm != 0)
                    {
                        return double.NaN;
                    }

                    ampm = w == "am" ? 1 : 2;
                }
                else if (w is "z" or "ut" or "utc" or "gmt")
                {
                    utcSeen = true;
                    offset = 0;
                }
                else if (ZoneAbbreviation(w) is var zh and not 99)
                {
                    utcSeen = true;
                    offset = zh * 3600000.0;
                }
                else if (w.Length >= 3 && Array.IndexOf(Months, w[..3]) is var mi and >= 0)
                {
                    if (month != 0)
                    {
                        return double.NaN;
                    }

                    month = mi + 1;
                }
                else if (w.Length >= 3 && Array.IndexOf(DayNames, w[..3]) >= 0)
                {
                }
                else if (seenNumber)
                {
                    return double.NaN;
                }
            }
            else
            {
                return double.NaN;
            }
        }

        // Compose the date from the numbers and the optional month name.
        long year, mon, day;
        bool YearLike(Number n) => n.Value > 31 || n.Digits >= 3 || n.Value == 0;
        long FullYear(Number n) => n.Digits >= 4 || n.Value >= 100 ? n.Value : n.Value < 50 ? 2000 + n.Value : 1900 + n.Value;
        if (month != 0)
        {
            mon = month;
            switch (nums.Count)
            {
                case 1:
                    if (YearLike(nums[0]))
                    {
                        year = FullYear(nums[0]);
                        day = 1;
                    }
                    else
                    {
                        year = 2001;
                        day = nums[0].Value;
                    }

                    break;
                case 2:
                    if (YearLike(nums[0]))
                    {
                        year = FullYear(nums[0]);
                        day = nums[1].Value;
                    }
                    else
                    {
                        day = nums[0].Value;
                        year = FullYear(nums[1]);
                    }

                    break;
                default:
                    return double.NaN;
            }
        }
        else
        {
            switch (nums.Count)
            {
                case 1:
                    if (YearLike(nums[0]))
                    {
                        year = FullYear(nums[0]);
                        mon = 1;
                    }
                    else
                    {
                        year = 2001;
                        mon = nums[0].Value;
                    }

                    day = 1;
                    break;
                case 2:
                    if (YearLike(nums[0]))
                    {
                        year = FullYear(nums[0]);
                        mon = nums[1].Value;
                        day = 1;
                    }
                    else
                    {
                        year = 2001;
                        mon = nums[0].Value;
                        day = nums[1].Value;
                    }

                    break;
                case 3:
                    if (YearLike(nums[0]))
                    {
                        year = FullYear(nums[0]);
                        mon = nums[1].Value;
                        day = nums[2].Value;
                    }
                    else
                    {
                        mon = nums[0].Value;
                        day = nums[1].Value;
                        year = FullYear(nums[2]);
                    }

                    break;
                default:
                    return double.NaN;
            }
        }

        if (mon is < 1 or > 12 || day is < 1 or > 31)
        {
            return double.NaN;
        }

        if (ampm != 0)
        {
            if (hour > 12)
            {
                return double.NaN;
            }

            hour = (hour % 12) + (ampm == 2 ? 12 : 0);
        }

        if (hour > 24 || minute > 59 || second > 59 || (hour == 24 && (minute != 0 || second != 0 || ms != 0)))
        {
            return double.NaN;
        }

        double t = DateMath.MakeDate(DateMath.MakeDay(year, mon - 1, day), DateMath.MakeTime(hour, minute, second, ms));
        return offset is { } o ? t - o : DateMath.UtcFromLocal(host, t);
    }

    private static int ReadSmall(string s, ref int i)
    {
        int v = 0;
        int n = 0;
        while (i < s.Length && s[i] is >= '0' and <= '9' && n < 9)
        {
            v = (v * 10) + (s[i] - '0');
            i++;
            n++;
        }

        return v;
    }
}
