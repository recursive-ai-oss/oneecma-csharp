using System.Diagnostics;
using System.Globalization;
using System.Text;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Fuzz;

/// <summary>
/// Mutational parser fuzzer. Seeds are .js files found under the given directories; every input is
/// parsed in script, module and strict mode and its diagnostics are rendered. Any exception, any
/// diagnostic outside the source, or an input that takes longer than the timeout is a finding and is
/// written to the output directory.
/// usage: fuzz --out DIR [--minutes N] [--seed N] [--timeout SECONDS] SEEDDIR...
/// </summary>
internal static class Program
{
    private static readonly string[] Tokens =
    [
        "(", ")", "[", "]", "{", "}", "`", "${", "'", "\"", "/", "/*", "*/", "//", "\\", "\\u{", "=>", "...", "?.", "??", "**",
        "async ", "await ", "yield ", "function ", "class ", "extends ", "static ", "get ", "set ", "let ", "const ", "var ",
        "import ", "export ", "default ", "from ", "as ", "of ", "in ", "new.target", "import.meta", "super", "#x", "0x", "1e",
        "1_", "08", "0n", "<!--", "-->", "\u2028", "\u2029", "\uD800", "\uFEFF", "'use strict'", "\n", ";", ",", ":", "?", "label:",
        "/(?<a>", "/[", "/u", "with(a)", "eval", "arguments", "delete x", "if(", "for(", "for await(", "try{", "catch(", "switch(",
    ];

    private static string? _outDir;
    private static long _started;
    private static string _current = "";
    private static int _findings;

    private static int Main(string[] args)
    {
        // regexp <seconds> [seed] [corpus-file]: seeded pattern/input fuzzing of the RegExp parser and matcher.
        // regexp-file <path>: one byte-encoded case (pattern length, flags selector, pattern, input) for replaying a finding.
        // interp <seconds> <outdir> [seed] [seeddir...]: seeded mutation fuzzing of the interpreter under tight limits.
        // interp-file <path>: replay one input in script and module mode.
        if (args.Length >= 3 && args[0] == "interp")
        {
            return InterpFuzz.RunRandom(int.Parse(args[1], CultureInfo.InvariantCulture), args[2], args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1, args.Skip(4));
        }

        if (args.Length == 2 && args[0] == "interp-file")
        {
            string? failure = InterpFuzz.RunOne(File.ReadAllText(args[1]), 30);
            Console.WriteLine(failure ?? "ok");
            return failure is null ? 0 : 1;
        }

        // replay <seconds> <outdir> [seed] [seeddir...]: record each mutant, replay the log with tracing, compare outcomes.
        // replay-file <path>: check one input.
        if (args.Length >= 3 && args[0] == "replay")
        {
            return ReplayFuzz.RunRandom(int.Parse(args[1], CultureInfo.InvariantCulture), args[2], args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1, args.Skip(4));
        }

        if (args.Length == 2 && args[0] == "replay-file")
        {
            string? failure = ReplayFuzz.RunOne(args[1].EndsWith(".json", StringComparison.Ordinal) ? System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(args[1]))! : File.ReadAllText(args[1]), 60);
            Console.WriteLine(failure ?? "ok");
            return failure is null ? 0 : 1;
        }

        // json <seconds> [seed] [outdir]: seeded generation and mutation of JSON text through parse, reviver and stringify.
        // json-file <path>: one text, for replaying a finding.
        if (args.Length >= 2 && args[0] == "json")
        {
            return JsonFuzz.RunRandom(int.Parse(args[1], CultureInfo.InvariantCulture), args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 1, args.Length > 3 ? args[3] : null);
        }

        if (args.Length == 2 && args[0] == "json-file")
        {
            string? failure = JsonFuzz.Check(File.ReadAllText(args[1]));
            Console.WriteLine(failure ?? "ok");
            return failure is null ? 0 : 1;
        }

        if (args.Length >= 2 && args[0] == "regexp")
        {
            return RegExpFuzz.RunRandom(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1, args.Length > 3 ? args[3] : null) == 0 ? 0 : 1;
        }

        // sourcemap <seconds> [seed] [dir-of-.map-seeds]: seeded generation and mutation of source map text.
        // sourcemap-file <path>: one finding, for replaying.
        if (args.Length >= 2 && args[0] == "sourcemap")
        {
            return SourceMapFuzz.RunRandom(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1, args.Length > 3 ? args[3] : null) == 0 ? 0 : 1;
        }

        if (args.Length == 2 && args[0] == "sourcemap-file")
        {
            string? failure = SourceMapFuzz.Check(File.ReadAllText(args[1]));
            Console.WriteLine(failure ?? "ok");
            return failure is null ? 0 : 1;
        }

        if (args.Length == 1 && args[0] == "regexp-throughput")
        {
            RegExpFuzz.Throughput();
            return 0;
        }

        if (args.Length == 2 && args[0] == "regexp-file")
        {
            string? failure = RegExpFuzz.RunBytes(File.ReadAllBytes(args[1]));
            Console.WriteLine(failure ?? "ok");
            return failure is null ? 0 : 1;
        }

        double minutes = 1;
        int seed = 12345;
        double timeout = 5;
        var seedDirs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": _outDir = args[++i]; break;
                case "--minutes": minutes = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--timeout": timeout = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: seedDirs.Add(args[i]); break;
            }
        }

        if (args.Length == 2 && args[0] == "--time")
        {
            // Reproduction helper: time parsing and rendering of one file separately.
            string text = File.ReadAllText(args[1]);
            long a = Stopwatch.GetTimestamp();
            var r = JsParser.Parse(text);
            long b = Stopwatch.GetTimestamp();
            _ = DiagnosticRenderer.RenderAll(r.Diagnostics);
            long c = Stopwatch.GetTimestamp();
            Console.WriteLine($"parse {(b - a) * 1000.0 / Stopwatch.Frequency:F1} ms, render {(c - b) * 1000.0 / Stopwatch.Frequency:F1} ms, {r.Diagnostics.Count} diagnostics");
            return 0;
        }

        if (args.Length == 2 && args[0] == "--reduce")
        {
            // Shrinks a failing input by removing chunks while it still fails; prints the result as JSON.
            string cur = File.ReadAllText(args[1]);
            for (int chunk = Math.Max(1, cur.Length / 2); chunk >= 1; chunk /= 2)
            {
                for (int i = 0; i < cur.Length;)
                {
                    string cand = cur.Remove(i, Math.Min(chunk, cur.Length - i));
                    if (Fails(cand))
                    {
                        cur = cand;
                    }
                    else
                    {
                        i += chunk;
                    }
                }
            }

            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(cur));
            return 0;
        }

        if (_outDir is null)
        {
            Console.Error.WriteLine("usage: fuzz --out DIR [--minutes N] [--seed N] [--timeout S] SEEDDIR...");
            return 2;
        }

        Directory.CreateDirectory(_outDir);
        var seeds = new List<string>();
        foreach (string d in seedDirs)
        {
            foreach (string f in Directory.EnumerateFiles(d, "*.js", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(f);
                if (fi.Length < 64 * 1024)
                {
                    seeds.Add(File.ReadAllText(f));
                }
            }
        }

        if (seeds.Count == 0)
        {
            seeds.Add("let a = 1;");
        }

        Console.WriteLine($"seeds: {seeds.Count}");
        var rng = new Random(seed);
        long timeoutTicks = (long)(timeout * Stopwatch.Frequency);
        var worker = new Thread(() => Loop(seeds, rng, minutes), 1024 * 1024);
        worker.Start();
        // Watchdog: a parse that does not return within the timeout is a hang.
        while (!worker.Join(500))
        {
            long s = Interlocked.Read(ref _started);
            if (s != 0 && Stopwatch.GetTimestamp() - s > timeoutTicks)
            {
                Save("hang", _current, "no result within " + timeout.ToString(CultureInfo.InvariantCulture) + "s");
                Console.WriteLine("HANG");
                Environment.Exit(3);
            }
        }

        return _findings == 0 ? 0 : 1;
    }

    private static void Loop(List<string> seeds, Random rng, double minutes)
    {
        long end = Stopwatch.GetTimestamp() + (long)(minutes * 60 * Stopwatch.Frequency);
        long total = 0;
        long t0 = Stopwatch.GetTimestamp();
        long nextReport = t0 + 30 * Stopwatch.Frequency;
        // First every seed unmodified, then mutations.
        foreach (string s in seeds)
        {
            RunOne(s);
            total++;
        }

        while (Stopwatch.GetTimestamp() < end)
        {
            string input = Mutate(seeds, rng);
            RunOne(input);
            total++;
            if (Stopwatch.GetTimestamp() > nextReport)
            {
                nextReport += 30 * Stopwatch.Frequency;
                Console.WriteLine($"{total} inputs, {total / Elapsed(t0):F0}/s, findings {_findings}");
            }
        }

        Console.WriteLine($"DONE inputs={total} seconds={Elapsed(t0):F0} inputs/s={total / Elapsed(t0):F0} findings={_findings}");
    }

    private static double Elapsed(long t0) => (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;

    private static void RunOne(string input)
    {
        _current = input;
        Interlocked.Exchange(ref _started, Stopwatch.GetTimestamp());
        try
        {
            for (int mode = 0; mode < 3; mode++)
            {
                var options = new ParseOptions { Goal = mode == 1 ? ParseGoal.Module : ParseGoal.Script, Strict = mode == 2, MaxDepth = 256 };
                var r = JsParser.Parse(input, options);
                foreach (var d in r.Diagnostics)
                {
                    if (d.PrimarySpan.Start < 0 || d.PrimarySpan.End > input.Length || d.PrimarySpan.Start > d.PrimarySpan.End)
                    {
                        throw new InvalidOperationException($"span out of range: {d.PrimarySpan.Start}..{d.PrimarySpan.End} of {input.Length}");
                    }
                }

                if (mode == 0)
                {
                    _ = DiagnosticRenderer.RenderAll(r.Diagnostics);
                    CheckRepair(input, options, r.HasErrors);
                }
            }
        }
        catch (Exception e)
        {
            Save("crash", input, e.ToString());
        }
        finally
        {
            Interlocked.Exchange(ref _started, 0);
        }
    }

    /// <summary>
    /// Repair mode: whatever it returns must parse, must be reproducible, must not touch source that parses (a fence aside),
    /// must be a fixed point, and its edits (original offsets) must rebuild the repaired text.
    /// </summary>
    private static void CheckRepair(string input, ParseOptions options, bool hadErrors)
    {
        var limits = new RepairOptions { MaxParses = 60, MaxRepairs = 8 };
        RepairResult rr = SyntaxRepair.Repair(input, options, limits);
        RepairResult again = SyntaxRepair.Repair(input, options, limits);
        if (rr.RepairedSource != again.RepairedSource || rr.Repairs.Count != again.Repairs.Count)
        {
            throw new InvalidOperationException("repair is not deterministic");
        }

        if (!rr.Changed)
        {
            if (rr.RepairedSource != input)
            {
                throw new InvalidOperationException("unchanged result carries different text");
            }

            return;
        }

        if (JsParser.Parse(rr.RepairedSource, options).HasErrors)
        {
            throw new InvalidOperationException("repaired source does not parse");
        }

        if (!hadErrors && rr.Repairs.Any(x => x.Id != "R003"))
        {
            throw new InvalidOperationException("source that parses was repaired by a rule other than the fence rule");
        }

        var edits = rr.Repairs.SelectMany(x => x.Edits).OrderBy(e => e.Start).ToList();
        bool disjoint = true;
        for (int i = 0; i < edits.Count; i++)
        {
            if (edits[i].Start < 0 || edits[i].Start + edits[i].Length > input.Length || (i > 0 && edits[i].Start < edits[i - 1].Start + edits[i - 1].Length))
            {
                disjoint = false;
            }
        }

        if (disjoint)
        {
            var sb = new StringBuilder();
            int pos = 0;
            foreach (TextEdit e in edits)
            {
                sb.Append(input, pos, e.Start - pos).Append(e.Text);
                pos = e.Start + e.Length;
            }

            sb.Append(input, pos, input.Length - pos);
            if (sb.ToString() != rr.RepairedSource)
            {
                throw new InvalidOperationException("edits do not rebuild the repaired source");
            }
        }
        else if (rr.Repairs.Any(x => x.Span.Start < 0 || x.Span.End > input.Length))
        {
            throw new InvalidOperationException("repair span outside the source");
        }

        if (!rr.RepairedSource.Contains("```", StringComparison.Ordinal) && SyntaxRepair.Repair(rr.RepairedSource, options, limits).Changed)
        {
            throw new InvalidOperationException("repair is not a fixed point");
        }

        _ = DiagnosticRenderer.RenderRepairResult(rr);
    }

    private static bool Fails(string input)
    {
        try
        {
            for (int mode = 0; mode < 3; mode++)
            {
                var r = JsParser.Parse(input, new ParseOptions { Goal = mode == 1 ? ParseGoal.Module : ParseGoal.Script, Strict = mode == 2, MaxDepth = 256 });
                if (r.Diagnostics.Any(d => d.PrimarySpan.Start < 0 || d.PrimarySpan.End > input.Length))
                {
                    return true;
                }

                _ = DiagnosticRenderer.RenderAll(r.Diagnostics);
            }

            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static void Save(string kind, string input, string detail)
    {
        int n = Interlocked.Increment(ref _findings);
        File.WriteAllText(Path.Combine(_outDir!, $"{kind}-{n}.js"), input, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_outDir!, $"{kind}-{n}.txt"), detail);
        Console.WriteLine($"{kind.ToUpperInvariant()} {n}: {detail.Split('\n')[0]}");
    }

    private static string Mutate(List<string> seeds, Random rng)
    {
        string s = seeds[rng.Next(seeds.Count)];
        int rounds = 1 + rng.Next(4);
        for (int i = 0; i < rounds; i++)
        {
            s = MutateOnce(s, seeds, rng);
            if (s.Length > 200_000)
            {
                s = s[..200_000];
            }
        }

        return s;
    }

    private static string MutateOnce(string s, List<string> seeds, Random rng)
    {
        int pos = s.Length == 0 ? 0 : rng.Next(s.Length + 1);
        switch (rng.Next(9))
        {
            case 0: // flip a character
                if (s.Length == 0) return s;
                pos = Math.Min(pos, s.Length - 1);
                return s[..pos] + (char)(s[pos] ^ (1 << rng.Next(8))) + s[(pos + 1)..];
            case 1: // delete a range
            {
                int len = Math.Min(s.Length - pos, 1 + rng.Next(20));
                return s.Remove(pos, len);
            }
            case 2: // duplicate a range
            {
                int len = Math.Min(s.Length - pos, 1 + rng.Next(40));
                return s.Insert(pos, s.Substring(pos, len));
            }
            case 3: // splice from another seed
            {
                string o = seeds[rng.Next(seeds.Count)];
                int op = o.Length == 0 ? 0 : rng.Next(o.Length);
                int len = Math.Min(o.Length - op, 1 + rng.Next(80));
                return s.Insert(pos, o.Substring(op, len));
            }
            case 4: // insert a token
                return s.Insert(pos, Tokens[rng.Next(Tokens.Length)]);
            case 5: // truncate
                return s[..pos];
            case 6: // repeat a bracket or token many times
            {
                string t = Tokens[rng.Next(Tokens.Length)];
                int n = 1 + rng.Next(rng.Next(4) == 0 ? 20_000 : 200);
                return s.Insert(pos, string.Concat(Enumerable.Repeat(t, n)));
            }
            case 7: // replace a token
            {
                int len = Math.Min(s.Length - pos, rng.Next(6));
                return s.Remove(pos, len).Insert(pos, Tokens[rng.Next(Tokens.Length)]);
            }
            default: // random BMP / astral character
            {
                int cp = rng.Next(4) switch { 0 => rng.Next(0x80), 1 => rng.Next(0x800), 2 => rng.Next(0xD800), _ => 0x10000 + rng.Next(0x100000) };
                return s.Insert(pos, char.ConvertFromUtf32(cp));
            }
        }
    }
}
