using System.Globalization;
using System.Text;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

internal sealed partial class DateTimeFormatService
{
    /// <summary>Formats a range of two instants (ECMA-402 11.5.8 FormatDateTimeRange).</summary>
    /// <exception cref="IntlException">A value is not a valid time value (RangeError).</exception>
    internal string FormatRange(double start, double end) => Join(FormatRangeToParts(start, end));

    /// <summary>
    /// Formats a range as typed parts, each tagged <c>startRange</c>, <c>endRange</c> or <c>shared</c> (ECMA-402 11.5.10
    /// PartitionDateTimeRangePattern). The two dates are compared field by field from the era down; the CLDR interval format for the
    /// largest differing field is used when the locale has one, else the two dates are formatted in full around the interval fallback
    /// pattern (UTS #35, Date Interval Formats).
    /// </summary>
    /// <exception cref="IntlException">A value is not a valid time value (RangeError).</exception>
    internal List<DateTimePart> FormatRangeToParts(double start, double end)
    {
        double a = CheckTime(start), b = CheckTime(end);
        LocalFields fa = Fields(a), fb = Fields(b);
        bool is12 = HourCycle is "h11" or "h12";

        // The largest differing calendar field, ranked era 0 ... millisecond 8, and its CLDR interval key letter.
        int rank;
        char letter;
        if (fa.IsAd != fb.IsAd) { rank = 0; letter = 'G'; }
        else if (fa.Year != fb.Year) { rank = 1; letter = 'y'; }
        else if (fa.Month != fb.Month) { rank = 2; letter = 'M'; }
        else if (fa.Day != fb.Day) { rank = 3; letter = 'd'; }
        else if (is12 && (fa.Hour >= 12) != (fb.Hour >= 12)) { rank = 4; letter = 'a'; }
        else if (fa.Hour != fb.Hour) { rank = 5; letter = is12 ? 'h' : 'H'; }
        else if (fa.Minute != fb.Minute) { rank = 6; letter = 'm'; }
        else if (fa.Second != fb.Second) { rank = 7; letter = 's'; }
        else if (fa.Millisecond != fb.Millisecond) { rank = 8; letter = 'S'; }
        else { rank = 99; letter = ' '; }

        int smallest = SmallestRank();
        if (rank > smallest)
        {
            // The dates look the same in this format: one date, all shared.
            return FormatWith(_tokens, a, fa, "shared");
        }

        bool hasDate = _tokens.Any(t => t.Letter is 'G' or 'y' or 'Y' or 'u' or 'M' or 'L' or 'd' or 'E' or 'c' or 'e');
        bool hasTime = _tokens.Any(t => t.Letter is 'h' or 'H' or 'K' or 'k' or 'm' or 's' or 'S' or 'a' or 'b' or 'B');
        string fallback = _data.GetString("gregorian", "dateTimeFormats", "intervalFormats", "intervalFormatFallback") ?? "{0} \u2013 {1}";
        bool dayLevel = rank <= 3;

        if (!hasDate && hasTime && dayLevel)
        {
            // A time-only format across days gets a short date in front of each time.
            DateTimePatternGenerator gen = DateTimePatternGenerator.For(_dataLocale);
            string combined = DateTimePatternGenerator.Glue(gen.GlueFor(3), Pattern, gen.GetBestPattern("yMd"));
            return FallbackParts(PatternToken.Parse(combined), a, b, fa, fb, fallback);
        }

        if (hasDate && hasTime && !dayLevel && _datePart is not null && _timePart is not null)
        {
            // Same day: the date once, the time as an interval.
            List<PatternToken> dateTokens = PatternToken.Parse(_datePart), timeTokens = PatternToken.Parse(_timePart);
            List<DateTimePart> time = IntervalParts(timeTokens, a, b, fa, fb, letter, fallback);
            List<DateTimePart> date = FormatWith(dateTokens, a, fa, "shared");
            return Combine(_glue!, time, date);
        }

        if (hasDate && hasTime)
        {
            return FallbackParts(_tokens, a, b, fa, fb, fallback);
        }

        return IntervalParts(_tokens, a, b, fa, fb, letter, fallback);
    }

    private static List<DateTimePart> Combine(string glue, List<DateTimePart> time, List<DateTimePart> date)
    {
        var result = new List<DateTimePart>();
        int t = glue.IndexOf("{0}", StringComparison.Ordinal), d = glue.IndexOf("{1}", StringComparison.Ordinal);
        void Lit(string s)
        {
            if (s.Length > 0)
            {
                result.Add(new DateTimePart("literal", s, "shared"));
            }
        }

        if (d < t)
        {
            Lit(glue[..d]);
            result.AddRange(date);
            Lit(glue[(d + 3)..t]);
            result.AddRange(time);
            Lit(glue[(t + 3)..]);
        }
        else
        {
            Lit(glue[..t]);
            result.AddRange(time);
            Lit(glue[(t + 3)..d]);
            result.AddRange(date);
            Lit(glue[(d + 3)..]);
        }

        return result;
    }

    private int SmallestRank()
    {
        int rank = -1;
        foreach (PatternToken t in _tokens)
        {
            int r = t.Letter switch
            {
                'G' => 0,
                'y' or 'Y' or 'u' or 'r' => 1,
                'M' or 'L' => 2,
                'd' or 'E' or 'c' or 'e' => 3,
                'a' or 'b' or 'B' => 4,
                'h' or 'H' or 'K' or 'k' => 5,
                'm' => 6,
                's' => 7,
                'S' => 8,
                _ => -1,
            };
            rank = Math.Max(rank, r);
        }

        return rank;
    }

    /// <summary>Both dates formatted in full around the fallback pattern (<c>{0} – {1}</c>).</summary>
    private List<DateTimePart> FallbackParts(List<PatternToken> tokens, double a, double b, LocalFields fa, LocalFields fb, string fallback)
    {
        var result = new List<DateTimePart>();
        int i0 = fallback.IndexOf("{0}", StringComparison.Ordinal), i1 = fallback.IndexOf("{1}", StringComparison.Ordinal);
        List<DateTimePart> first = FormatWith(tokens, a, fa, "startRange"), second = FormatWith(tokens, b, fb, "endRange");
        void Lit(string s)
        {
            if (s.Length > 0)
            {
                result.Add(new DateTimePart("literal", s, "shared"));
            }
        }

        if (i0 < i1)
        {
            Lit(fallback[..i0]);
            result.AddRange(first);
            Lit(fallback[(i0 + 3)..i1]);
            result.AddRange(second);
            Lit(fallback[(i1 + 3)..]);
        }
        else
        {
            Lit(fallback[..i1]);
            result.AddRange(second);
            Lit(fallback[(i1 + 3)..i0]);
            result.AddRange(first);
            Lit(fallback[(i0 + 3)..]);
        }

        return result;
    }

    /// <summary>
    /// The interval pattern for the skeleton and the differing field, applied to both dates, or the fallback when the locale has none.
    /// Fields that occur in both halves of the interval pattern form the range; the rest of the text is shared.
    /// </summary>
    private List<DateTimePart> IntervalParts(List<PatternToken> tokens, double a, double b, LocalFields fa, LocalFields fb, char letter, string fallback)
    {
        string? interval = FindIntervalPattern(tokens, letter);
        if (interval is null)
        {
            return FallbackParts(tokens, a, b, fa, fb, fallback);
        }

        interval = AdjustInterval(interval, tokens);
        List<PatternToken> all = PatternToken.Parse(interval);

        // The second half starts at the first field letter that already occurred (UTS #35: the pattern is split at the first repeated field).
        var seen = new HashSet<char>();
        int split = -1;
        for (int i = 0; i < all.Count; i++)
        {
            if (!all[i].IsField)
            {
                continue;
            }

            if (!seen.Add(Canon(all[i].Letter)))
            {
                split = i;
                break;
            }
        }

        if (split < 0)
        {
            return FallbackParts(tokens, a, b, fa, fb, fallback);
        }

        List<PatternToken> first = all[..split], second = all[split..];
        var secondLetters = new HashSet<char>(second.Where(t => t.IsField).Select(t => Canon(t.Letter)));
        var firstLetters = new HashSet<char>(first.Where(t => t.IsField).Select(t => Canon(t.Letter)));
        var common = new HashSet<char>(firstLetters.Intersect(secondLetters));

        // Trailing literal of the first half is shared with the separator.
        var result = new List<DateTimePart>();
        void Emit(List<PatternToken> part, double t, LocalFields f, string source, bool isFirst)
        {
            int firstCommon = part.FindIndex(x => x.IsField && common.Contains(Canon(x.Letter)));
            int lastCommon = part.FindLastIndex(x => x.IsField && common.Contains(Canon(x.Letter)));
            for (int i = 0; i < part.Count; i++)
            {
                PatternToken tok = part[i];
                string src = i >= firstCommon && i <= lastCommon ? source : "shared";
                if (!tok.IsField)
                {
                    if (tok.Literal.Length > 0)
                    {
                        result.Add(new DateTimePart("literal", tok.Literal, src));
                    }

                    continue;
                }

                (string type, string value) = Field(tok, f);
                if (value.Length > 0)
                {
                    result.Add(new DateTimePart(type, value, src));
                }
            }
        }

        Emit(first, a, fa, "startRange", true);
        Emit(second, b, fb, "endRange", false);
        return Merge(result);
    }

    private static List<DateTimePart> Merge(List<DateTimePart> parts)
    {
        var result = new List<DateTimePart>(parts.Count);
        foreach (DateTimePart p in parts)
        {
            if (result.Count > 0 && p.Type == "literal" && result[^1].Type == "literal" && result[^1].Source == p.Source)
            {
                result[^1] = result[^1] with { Value = result[^1].Value + p.Value };
            }
            else
            {
                result.Add(p);
            }
        }

        return result;
    }

    private static char Canon(char letter) => letter switch { 'L' => 'M', 'c' or 'e' => 'E', 'K' => 'h', 'k' => 'H', 'b' or 'B' => 'a', _ => letter };

    /// <summary>The CLDR skeleton of a pattern for interval lookup (canonical letters, month and weekday widths kept, other widths dropped).</summary>
    private static string IntervalSkeleton(List<PatternToken> tokens)
    {
        var sb = new StringBuilder();
        var present = new Dictionary<char, int>();
        foreach (PatternToken t in tokens)
        {
            if (!t.IsField)
            {
                continue;
            }

            char c = Canon(t.Letter);
            if (c is 'G' or 'y' or 'Y' or 'u' or 'r' or 'M' or 'd' or 'E' or 'h' or 'H' or 'm' or 's' or 'z' or 'v' or 'O' or 'a')
            {
                present[c] = t.Count;
            }
        }

        foreach (char c in "GyMEdhHmsvzOa")
        {
            if (!present.TryGetValue(c, out int n))
            {
                continue;
            }

            switch (c)
            {
                case 'M':
                    sb.Append('M', n >= 3 ? n : 1);
                    break;
                case 'E':
                    sb.Append('E');
                    break;
                case 'a':
                    break;
                case 'z' or 'O':
                    sb.Append('v');
                    break;
                case 'Y' or 'u' or 'r':
                    sb.Append('y');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    private string? FindIntervalPattern(List<PatternToken> tokens, char letter)
    {
        string skeleton = IntervalSkeleton(tokens);
        string key = letter.ToString();
        if (_data.GetString("gregorian", "dateTimeFormats", "intervalFormats", skeleton, key) is { } exact)
        {
            return exact;
        }

        // Without the weekday, then closest widths of the month (MMM for MMMM and the like).
        foreach (string alt in Alternatives(skeleton))
        {
            if (_data.GetString("gregorian", "dateTimeFormats", "intervalFormats", alt, key) is { } p)
            {
                return p;
            }
        }

        return null;
    }

    private static IEnumerable<string> Alternatives(string skeleton)
    {
        if (skeleton.Contains("MMMM", StringComparison.Ordinal))
        {
            yield return skeleton.Replace("MMMM", "MMM", StringComparison.Ordinal);
        }
        else if (skeleton.Contains("MMM", StringComparison.Ordinal))
        {
            yield return skeleton.Replace("MMM", "MMMM", StringComparison.Ordinal);
        }
    }

    /// <summary>Gives the interval pattern the field widths and hour letter of the format's own pattern.</summary>
    private static string AdjustInterval(string interval, List<PatternToken> own)
    {
        var widths = new Dictionary<char, (char Letter, int Count)>();
        foreach (PatternToken t in own)
        {
            if (t.IsField)
            {
                widths[Canon(t.Letter)] = (t.Letter, t.Count);
            }
        }

        var sb = new StringBuilder();
        foreach (PatternToken t in PatternToken.Parse(interval))
        {
            if (!t.IsField)
            {
                PatternToken.AppendLiteral(sb, t.Literal);
                continue;
            }

            char c = Canon(t.Letter);
            if (widths.TryGetValue(c, out var w))
            {
                bool textual = c is 'E' or 'G' || (c == 'M' && (t.Count >= 3 || w.Count >= 3));
                if (c is 'h' or 'H')
                {
                    sb.Append(w.Letter, w.Count);
                }
                else if (c == 'M' && textual)
                {
                    sb.Append(t.Letter, Math.Max(t.Count, w.Count >= 3 ? w.Count : t.Count));
                }
                else
                {
                    sb.Append(t.Letter, textual ? t.Count : w.Count);
                }
            }
            else
            {
                sb.Append(t.Letter, t.Count);
            }
        }

        return sb.ToString();
    }
}
