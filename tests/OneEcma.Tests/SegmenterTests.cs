using System.Globalization;
using RecursiveAI.OneEcma.TextServices;

namespace RecursiveAI.OneEcma.Tests;

public class SegmenterTests
{
    // Runs one of the official UAX #29 break test files: "÷ 0041 × 0308 ÷" lines, where ÷ is a boundary and × is none.
    private static (int Lines, List<string> Failures) RunFile(string name, SegmentGranularity g)
    {
        string path = TextTestFiles.RequireOrSkip(TextTestFiles.Ucd(name));
        int lines = 0;
        var failures = new List<string>();
        foreach (string raw in File.ReadLines(path))
        {
            int hash = raw.IndexOf('#', StringComparison.Ordinal);
            string line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var sb = new System.Text.StringBuilder();
            var expected = new List<int>();
            foreach (string tok in line.Split(' ', '\t').Where(t => t.Length > 0))
            {
                if (tok == "\u00F7")
                {
                    expected.Add(sb.Length);
                }
                else if (tok != "\u00D7")
                {
                    sb.Append(char.ConvertFromUtf32(int.Parse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                }
            }

            string text = sb.ToString();
            var actual = new List<int> { 0 };
            foreach (var seg in TextSegmenter.Enumerate(text, g))
            {
                actual.Add(seg.End);
            }

            lines++;
            if (!actual.SequenceEqual(expected))
            {
                failures.Add($"{line} => got [{string.Join(",", actual)}] want [{string.Join(",", expected)}]");
            }
        }

        return (lines, failures);
    }

    [Theory]
    [InlineData("GraphemeBreakTest.txt", 0, 700)]
    [InlineData("WordBreakTest.txt", 1, 1800)]
    [InlineData("SentenceBreakTest.txt", 2, 500)]
    public void OfficialBreakTests(string file, int granularity, int minLines)
    {
        var (lines, failures) = RunFile(file, (SegmentGranularity)granularity);
        Assert.True(failures.Count == 0, $"{failures.Count} of {lines} failed; first: {string.Join(" | ", failures.Take(3))}");
        Assert.True(lines >= minLines, $"only {lines} test lines");
    }

    [Fact]
    public void WordLikeAndContaining()
    {
        var words = new List<(string, bool)>();
        const string sample = "foo_bar 3.14 _ \uD83D\uDE00";
        foreach (var s in TextSegmenter.Enumerate(sample, SegmentGranularity.Word))
        {
            words.Add((sample[s.Start..s.End], s.IsWordLike));
        }

        Assert.Equal([("foo_bar", true), (" ", false), ("3.14", true), (" ", false), ("_", false), (" ", false), ("\uD83D\uDE00", false)], words);
        Assert.True(TextSegmenter.TryFindSegment("ab cd", 3, SegmentGranularity.Word, out int start, out int end, out bool like));
        Assert.Equal((3, 5, true), (start, end, like));
        Assert.False(TextSegmenter.TryFindSegment("ab", 2, SegmentGranularity.Word, out _, out _, out _));
    }

    [Fact]
    public void PathologicalInputsAreLinear()
    {
        // Each of these is quadratic with a naive rescan: long regional-indicator runs, ATerm lookahead, mark runs.
        string ri = string.Concat(Enumerable.Repeat("\uD83C\uDDE6", 200_000));
        string aterm = string.Concat(Enumerable.Repeat("1. ", 200_000));
        string marks = "a" + new string('\u0301', 400_000);
        foreach (var (text, g) in new[] { (ri, SegmentGranularity.Grapheme), (ri, SegmentGranularity.Word), (aterm, SegmentGranularity.Sentence), (marks, SegmentGranularity.Word), (marks, SegmentGranularity.Grapheme), (marks, SegmentGranularity.Sentence) })
        {
            int count = 0;
            foreach (var s in TextSegmenter.Enumerate(text, g))
            {
                count++;
            }

            Assert.True(count > 0);
        }
    }
}

/// <summary>Differences between our segmentation and the reference Node's on the committed corpus.</summary>
public class SegmenterNodeDifferentialTests
{
    // Index of the corpus string and granularity -> reason. Every entry is a place where the default UAX #29
    // rules differ from ICU, which adds dictionary segmentation for Han, Thai and similar scripts.
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["word:25"] = "Thai: ICU segments with a dictionary; UAX #29 gives Thai letters no word class, so each cluster stands alone",
        ["word:41"] = "Han: ICU dictionary words (世界); UAX #29 breaks between ideographs",
        ["word:42"] = "Japanese: ICU dictionary words; UAX #29 breaks between ideographs and hiragana",
    };

    [Fact]
    public void MatchesNodeOnTheCorpusExceptDocumentedDifferences()
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "text");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "segmentation-expected.json")));
        System.Text.Json.JsonElement root = doc.RootElement;
        var strings = root.GetProperty("strings").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var mismatches = new List<string>();
        foreach (var (name, g) in new[] { ("grapheme", SegmentGranularity.Grapheme), ("word", SegmentGranularity.Word), ("sentence", SegmentGranularity.Sentence) })
        {
            System.Text.Json.JsonElement expected = root.GetProperty(name);
            for (int i = 0; i < strings.Length; i++)
            {
                string text = strings[i];
                var want = expected[i].EnumerateArray().Select(e => e.GetString()!).ToArray();
                var got = new List<string>();
                var like = new System.Text.StringBuilder();
                foreach (var s in TextSegmenter.Enumerate(text, g))
                {
                    got.Add(text[s.Start..s.End]);
                    like.Append(s.IsWordLike ? '1' : '0');
                }

                bool ok = got.SequenceEqual(want);
                if (ok && g == SegmentGranularity.Word)
                {
                    ok = like.ToString() == root.GetProperty("wordLike")[i].GetString();
                }

                if (!ok && !Known.ContainsKey($"{name}:{i}"))
                {
                    mismatches.Add($"{name}:{i} {System.Text.Json.JsonSerializer.Serialize(text)} want {System.Text.Json.JsonSerializer.Serialize(want)} got {System.Text.Json.JsonSerializer.Serialize(got)} like {like}");
                }
                else if (ok && Known.ContainsKey($"{name}:{i}"))
                {
                    mismatches.Add($"{name}:{i} is listed as a known difference but now matches");
                }
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} differences:\n{string.Join("\n", mismatches)}");
    }
}
