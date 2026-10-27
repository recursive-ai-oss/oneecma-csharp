using System.Diagnostics;
using System.Text.Json;
using RecursiveAI.OneEcma.TextServices;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>Informal throughput of the text services. Skipped unless ONEECMA_TEXT_THROUGHPUT is set; it prints, it asserts nothing.</summary>
public class TextServicesThroughput
{
    [Fact]
    public void Report()
    {
        if (Environment.GetEnvironmentVariable("ONEECMA_TEXT_THROUGHPUT") is null)
        {
            Assert.Skip("Set ONEECMA_TEXT_THROUGHPUT=1 to print throughput numbers.");
        }

        string dir = Path.Combine(RepoPaths.Root, "testdata", "text");
        string[] prose = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(dir, "segmentation-corpus.json")))!
            .Where(s => !s.Contains('\uD800', StringComparison.Ordinal)).ToArray();
        string[] words = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(dir, "collation-corpus.json")))!;
        var sb = new System.Text.StringBuilder();
        while (sb.Length < 4_000_000)
        {
            foreach (string p in prose)
            {
                sb.Append(p).Append(' ');
            }
        }

        string text = sb.ToString();
        string ascii = new string('x', 1000) + " " + string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 100_000));
        var lines = new List<string>
        {
            $"input: mixed text {text.Length / 1e6:F1} M chars; ASCII {ascii.Length / 1e6:F1} M chars; {words.Length} short strings",
        };

        double Time(Action a, int reps)
        {
            a();
            var w = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++)
            {
                a();
            }

            return w.Elapsed.TotalSeconds / reps;
        }

        foreach (NormalizationForm f in Enum.GetValues<NormalizationForm>())
        {
            string mixed = text;
            double t = Time(() => Normalizer.Normalize(mixed.AsSpan(), f), 3);
            lines.Add($"normalize {f,-4} mixed: {mixed.Length / t / 1e6:F0} M chars/s");
        }

        lines.Add($"normalize NFC ASCII (quick check): {ascii.Length / Time(() => Normalizer.Normalize(ascii, NormalizationForm.Nfc), 10) / 1e6:F0} M chars/s");
        lines.Add($"normalize NFD ASCII: {ascii.Length / Time(() => Normalizer.Normalize(ascii.AsSpan(), NormalizationForm.Nfd), 5) / 1e6:F0} M chars/s");

        foreach (SegmentGranularity g in Enum.GetValues<SegmentGranularity>())
        {
            int count = 0;
            double t = Time(() =>
            {
                count = 0;
                foreach (var s in TextSegmenter.Enumerate(text, g))
                {
                    count++;
                }
            }, 3);
            lines.Add($"segment {g,-8} mixed: {text.Length / t / 1e6:F1} M chars/s ({count / t / 1e6:F2} M segments/s)");
        }

        TextCollator c = TextCollator.Create("en-US");
        TextCollator cn = TextCollator.Create("en-US", new CollatorOptions { Numeric = true, IgnorePunctuation = true });
        int n = words.Length;
        double compares = n * (double)n;
        double tc = Time(() =>
        {
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    c.Compare(words[i], words[j]);
                }
            }
        }, 1);
        lines.Add($"collate compare (default): {compares / tc / 1e6:F2} M compares/s");
        double tn = Time(() =>
        {
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    cn.Compare(words[i], words[j]);
                }
            }
        }, 1);
        lines.Add($"collate compare (numeric+shifted): {compares / tn / 1e6:F2} M compares/s");
        double tk = Time(() =>
        {
            foreach (string w in words)
            {
                c.GetSortKey(w);
            }
        }, 200);
        lines.Add($"collate sort keys: {n / tk / 1e6:F2} M keys/s");
        var keys = words.Select(w => c.GetSortKey(w)).ToArray();
        lines.Add($"sort {n} strings by cached keys: {Time(() => Array.Sort((byte[][])keys.Clone(), (a, b) => a.AsSpan().SequenceCompareTo(b)), 200) * 1e6:F0} us; by Compare: {Time(() => Array.Sort((string[])words.Clone(), (a, b) => c.Compare(a, b)), 20) * 1e6:F0} us");

        File.WriteAllLines(Path.Combine(Environment.GetEnvironmentVariable("ONEECMA_ARTIFACTS") ?? Path.GetTempPath(), "text-throughput.txt"), lines);
    }
}
