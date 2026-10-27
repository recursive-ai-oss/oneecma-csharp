using System.Diagnostics;
using System.Text;
using RecursiveAI.OneEcma.RegExp;

namespace RecursiveAI.OneEcma.Fuzz;

/// <summary>
/// Pattern-parser plus matcher fuzzing. <see cref="RunBytes"/> is the entry point for a coverage-guided driver
/// (bytes in, invariants checked); <see cref="RunRandom"/> is the seeded grammar-and-mutation driver used when no
/// coverage instrumentation is available. Both check the same properties: no exception escapes, Compile and
/// Validate agree, matches stay inside the input, and a call under a small fuel budget returns quickly.
/// </summary>
internal static class RegExpFuzz
{
    private static readonly string[] Lits = ["a", "b", "c", "A", "1", "_", " ", "-", "\n", "k", "s", "\u017f", "\u212a", "\u01c6", "\u03c3", "\u03c2", "\u00df", "\U0001F600", "\U00010400", "\ud83d", "\ude00", ".", "/", "{", "}", "]"];
    private static readonly string[] Esc = [@"\d", @"\D", @"\w", @"\W", @"\s", @"\S", @"\b", @"\B", @"\n", @"\x61", @"\u0062", @"\u{1F600}", @"\0", @"\cA", @"\.", @"\1", @"\2", @"\k<n>", @"\p{L}", @"\P{Lu}", @"\p{Script=Greek}", @"\p{RGI_Emoji}", @"\8", @"\q", @"\u", @"\c"];
    private static readonly string[] ClassAtoms = ["a", "z", "0", "-", @"\d", @"\w", @"\W", @"\b", @"\-", @"\]", "\u00e9", "\U0001F600", "\u017f", @"\p{L}", @"\u{61}", @"\x41", "^", "[", "(", "&", "&&", "--", @"\q{ab|c}", @"\q{}", "[a-c]"];
    private static readonly string[] Quants = ["*", "+", "?", "*?", "+?", "??", "{2}", "{1,2}", "{0,}", "{2,3}?", "{1,}", "{0,100000}", "{99999999999}"];
    private static readonly string[] FlagSets = ["", "i", "u", "v", "iu", "iv", "gimsy", "d", "m", "s", "y", "uv", "ii", "x"];
    private static readonly string[] InputPieces = ["a", "b", "ab", "aaaa", "A", "1", " ", "\n", "k", "\u212a", "\u017f", "\u03c3", "\u03c2", "\U0001F600", "\ud83d", "\ude00", "\U00010400", "\U00010428", "__", "a\u0301"];

    /// <summary>Decodes a byte string into pattern, flags and input and runs the checks; returns the failure text or null.</summary>
    internal static string? RunBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return null;
        }

        string flags = FlagSets[data[0] % FlagSets.Length];
        int split = Math.Min(data[1], data.Length - 2);
        string pattern = Encoding.UTF8.GetString(data.Slice(2, split));
        string input = Encoding.UTF8.GetString(data[(2 + split)..]);
        return Check(pattern, flags, input);
    }

    internal static string? Check(string pattern, string flags, string input)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            RegExpCompileResult compiled = RegExpProgram.Compile(pattern, flags);
            var diagnostics = RegExpValidator.Validate(pattern, flags);
            if ((compiled.Program is null) != (diagnostics.Count > 0))
            {
                return "Compile and Validate disagree";
            }

            if (compiled.Program is null)
            {
                if (compiled.Diagnostics.Count == 0 || compiled.Diagnostics[0].Message.Length == 0)
                {
                    return "failed compile without a diagnostic";
                }

                return sw.ElapsedMilliseconds > 2000 ? "slow compile" : null;
            }

            RegExpProgram p = compiled.Program;
            foreach (int start in new[] { 0, input.Length / 2, input.Length, input.Length + 1, -1 })
            {
                foreach (bool sticky in new[] { false, true })
                {
                    if (start < 0 || start > input.Length)
                    {
                        // Out-of-range starts are caller errors; they may throw only argument exceptions.
                        try
                        {
                            p.Exec(input, start, sticky, new FuelMeter(1000));
                        }
                        catch (ArgumentException)
                        {
                        }

                        continue;
                    }

                    var r = p.Exec(input, start, sticky, new FuelMeter(200_000), new RegExpLimits { MaxBacktrackBytes = 1 << 20 });
                    if (r.Outcome == RegExpOutcome.Match)
                    {
                        RegExpMatch m = r.Match!;
                        if (m.Start < 0 || m.End < m.Start || m.End > input.Length || (sticky && m.Start != start && !p.IsUnicode))
                        {
                            return $"bad match range {m.Start}-{m.End} (start {start}, sticky {sticky})";
                        }

                        for (int g = 1; g <= m.GroupCount; g++)
                        {
                            if (m.IsGroupMatched(g) && (m.GetGroupStart(g) < 0 || m.GetGroupEnd(g) < m.GetGroupStart(g) || m.GetGroupEnd(g) > input.Length))
                            {
                                return $"bad group {g} range";
                            }
                        }
                    }
                    else if (r.Outcome == RegExpOutcome.NoMatch && r.Match is not null)
                    {
                        return "no-match with a match object";
                    }
                }
            }

            return sw.ElapsedMilliseconds > 5000 ? $"slow exec {sw.ElapsedMilliseconds} ms" : null;
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>Runs for <paramref name="seconds"/> and returns the number of findings (also printed with their inputs).</summary>
    internal static int RunRandom(int seconds, int seed, string? corpusFile)
    {
        var rng = new Random(seed);
        var corpus = new List<string>();
        if (corpusFile is not null && File.Exists(corpusFile))
        {
            if (corpusFile.EndsWith(".json", StringComparison.Ordinal))
            {
                // The recorded differential file: its first line is a header, then one JSON case per line.
                foreach (string line in File.ReadLines(corpusFile).Skip(1))
                {
                    string t = line.TrimEnd(',');
                    if (t.StartsWith('{'))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(t);
                        corpus.Add(doc.RootElement.GetProperty("p").GetString() ?? string.Empty);
                    }
                }
            }
            else
            {
                corpus.AddRange(File.ReadAllLines(corpusFile).Where(l => l.Length > 0));
            }
        }

        var sw = Stopwatch.StartNew();
        long execs = 0;
        int findings = 0;
        var seen = new HashSet<string>();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            string pattern;
            if (corpus.Count > 0 && rng.Next(3) == 0)
            {
                pattern = Mutate(rng, corpus[rng.Next(corpus.Count)]);
            }
            else
            {
                pattern = Disjunction(rng, 3, rng.Next(4) == 0);
                if (rng.Next(6) == 0)
                {
                    pattern = Mutate(rng, pattern);
                }
            }

            string flags = FlagSets[rng.Next(FlagSets.Length)];
            var input = new StringBuilder();
            int n = rng.Next(12);
            for (int i = 0; i < n; i++)
            {
                input.Append(InputPieces[rng.Next(InputPieces.Length)]);
            }

            if (rng.Next(20) == 0)
            {
                input.Append('a', rng.Next(200, 3000));
            }

            string? failure = Check(pattern, flags, input.ToString());
            execs++;
            if (failure is not null && seen.Add(failure + "|" + pattern))
            {
                findings++;
                Console.WriteLine($"FINDING {failure}\n  pattern={Escape(pattern)} flags={flags} input={Escape(input.ToString())}");
            }
        }

        Console.WriteLine($"regexp fuzz: seed {seed}, {execs} executions in {sw.Elapsed.TotalSeconds:F0} s, {findings} findings");
        return findings;
    }

    /// <summary>Informal matcher speed on a 10 MB string; for orientation, not a benchmark.</summary>
    internal static void Throughput()
    {
        var sb = new StringBuilder();
        var rng = new Random(5);
        while (sb.Length < 10_000_000)
        {
            sb.Append("lorem ipsum ").Append(rng.Next(100000)).Append(" dolor sit amet user").Append((char)(97 + rng.Next(26))).Append("@example.com, ");
        }

        string text = sb.ToString();
        foreach ((string pattern, string flags) in new[] { (@"\d+", ""), (@"[a-z]+@[a-z]+\.com", ""), (@"(?:lorem|dolor) \w+", "i"), (@"\b\p{L}{5}\b", "u") })
        {
            RegExpProgram p = RegExpProgram.Compile(pattern, flags).Program!;
            var sw = Stopwatch.StartNew();
            int pos = 0;
            int matches = 0;
            while (pos <= text.Length)
            {
                var r = p.Exec(text, pos, false);
                if (r.Match is null)
                {
                    break;
                }

                matches++;
                pos = r.Match.End == r.Match.Start ? r.Match.End + 1 : r.Match.End;
            }

            Console.WriteLine($"/{pattern}/{flags}: {matches} matches over {text.Length / 1_000_000.0:F0} MB in {sw.ElapsedMilliseconds} ms ({text.Length / 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0} MB/s)");
        }
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c is < ' ' or > '~')
            {
                sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string Mutate(Random rng, string s)
    {
        var sb = new StringBuilder(s);
        int edits = 1 + rng.Next(3);
        for (int i = 0; i < edits; i++)
        {
            int at = sb.Length == 0 ? 0 : rng.Next(sb.Length + 1);
            switch (rng.Next(4))
            {
                case 0 when at < sb.Length:
                    sb.Remove(at, 1);
                    break;
                case 1:
                    const string Punct = "()[]{}|\\?*+^$.-:<>=!&";
                    sb.Insert(at, Punct[rng.Next(Punct.Length)]);
                    break;
                case 2:
                    sb.Insert(at, Lits[rng.Next(Lits.Length)]);
                    break;
                default:
                    sb.Length = at;
                    break;
            }
        }

        return sb.ToString();
    }

    private static string Disjunction(Random rng, int depth, bool v)
    {
        var sb = new StringBuilder(Alternative(rng, depth, v));
        while (rng.Next(4) == 0)
        {
            sb.Append('|').Append(Alternative(rng, depth, v));
        }

        return sb.ToString();
    }

    private static string Alternative(Random rng, int depth, bool v)
    {
        var sb = new StringBuilder();
        int n = rng.Next(5);
        for (int i = 0; i < n; i++)
        {
            sb.Append(Atom(rng, depth, v));
            if (rng.Next(3) == 0)
            {
                sb.Append(Quants[rng.Next(Quants.Length)]);
            }
        }

        return sb.ToString();
    }

    private static string Atom(Random rng, int depth, bool v)
    {
        int k = rng.Next(100);
        if (k < 30)
        {
            return Lits[rng.Next(Lits.Length)];
        }

        if (k < 38)
        {
            return ".";
        }

        if (k < 52)
        {
            return Esc[rng.Next(Esc.Length)];
        }

        if (k < 66)
        {
            var sb = new StringBuilder("[");
            if (rng.Next(4) == 0)
            {
                sb.Append('^');
            }

            int n = rng.Next(5);
            for (int i = 0; i < n; i++)
            {
                sb.Append(ClassAtoms[rng.Next(v ? ClassAtoms.Length : 16)]);
                if (rng.Next(5) == 0)
                {
                    sb.Append('-').Append(ClassAtoms[rng.Next(16)]);
                }
            }

            return sb.Append(']').ToString();
        }

        if (depth <= 0)
        {
            return Lits[rng.Next(Lits.Length)];
        }

        string inner = Disjunction(rng, depth - 1, v);
        return rng.Next(12) switch
        {
            0 or 1 or 2 => "(" + inner + ")",
            3 or 4 => "(?:" + inner + ")",
            5 => "(?<n>" + inner + ")",
            6 => "(?=" + inner + ")",
            7 => "(?!" + inner + ")",
            8 => "(?<=" + inner + ")",
            9 => "(?<!" + inner + ")",
            10 => "(?i:" + inner + ")",
            _ => "(?-s:" + inner + ")",
        };
    }
}
