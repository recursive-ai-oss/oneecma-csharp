using System.Globalization;
using RecursiveAI.OneEcma.TextServices;

namespace RecursiveAI.OneEcma.Tests;

public class NormalizerTests
{
    private static readonly NormalizationForm[] Forms = [NormalizationForm.Nfc, NormalizationForm.Nfd, NormalizationForm.Nfkc, NormalizationForm.Nfkd];

    [Fact]
    public void OfficialConformanceFile()
    {
        string path = TextTestFiles.RequireOrSkip(TextTestFiles.Ucd("NormalizationTest.txt"));
        int part = 0;
        int lines = 0;
        var part1 = new HashSet<int>();
        var failures = new List<string>();
        foreach (string raw in File.ReadLines(path))
        {
            if (raw.StartsWith("@Part", StringComparison.Ordinal))
            {
                part = int.Parse(raw.AsSpan(5, 1), CultureInfo.InvariantCulture);
                continue;
            }

            int hash = raw.IndexOf('#', StringComparison.Ordinal);
            string line = hash >= 0 ? raw[..hash] : raw;
            if (line.Trim().Length == 0)
            {
                continue;
            }

            string[] f = line.Split(';');
            string c1 = TextTestFiles.FromHex(f[0]), c2 = TextTestFiles.FromHex(f[1]), c3 = TextTestFiles.FromHex(f[2]),
                c4 = TextTestFiles.FromHex(f[3]), c5 = TextTestFiles.FromHex(f[4]);
            lines++;
            if (part == 1)
            {
                part1.Add(char.ConvertToUtf32(c1, 0));
            }

            void Expect(string expected, string input, NormalizationForm form, string label)
            {
                string actual = Normalizer.Normalize(input, form);
                if (actual != expected)
                {
                    failures.Add($"{label} {form}: {raw}");
                }
            }

            // The conformance conditions of the file header.
            foreach (string x in new[] { c1, c2, c3 })
            {
                Expect(c2, x, NormalizationForm.Nfc, "c2");
                Expect(c3, x, NormalizationForm.Nfd, "c3");
            }

            foreach (string x in new[] { c4, c5 })
            {
                Expect(c4, x, NormalizationForm.Nfc, "c4");
                Expect(c5, x, NormalizationForm.Nfd, "c5");
            }

            foreach (string x in new[] { c1, c2, c3, c4, c5 })
            {
                Expect(c4, x, NormalizationForm.Nfkc, "c4k");
                Expect(c5, x, NormalizationForm.Nfkd, "c5k");
            }

            // IsNormalized agrees with comparing against the normalized text.
            foreach (string x in new[] { c1, c2, c3, c4, c5 })
            {
                foreach (NormalizationForm form in Forms)
                {
                    if (Normalizer.IsNormalized(x, form) != (Normalizer.Normalize(x.AsSpan(), form) == x))
                    {
                        failures.Add($"IsNormalized {form}: {raw}");
                    }
                }
            }
        }

        // Code points that are not in Part 1 are unchanged by every form.
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF || part1.Contains(cp))
            {
                continue;
            }

            string s = char.ConvertFromUtf32(cp);
            foreach (NormalizationForm form in Forms)
            {
                if (Normalizer.Normalize(s, form) != s)
                {
                    failures.Add($"U+{cp:X4} changed by {form}");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failures of {lines} lines; first: {string.Join(" | ", failures.Take(5))}");
        Assert.True(lines > 19000, $"only {lines} lines read");
    }

    [Fact]
    public void LoneSurrogatesAndOrderingAndHangul()
    {
        Assert.Equal("\uD800a", Normalizer.Normalize("\uD800a", NormalizationForm.Nfc));
        Assert.Equal("a\uDC00", Normalizer.Normalize("a\uDC00", NormalizationForm.Nfd));
        Assert.Equal("a\u0316\u0301", Normalizer.Normalize("a\u0301\u0316", NormalizationForm.Nfd));
        Assert.Equal("\uD55C", Normalizer.Normalize("\u1112\u1161\u11AB", NormalizationForm.Nfc));
        Assert.Equal("\u1112\u1161\u11AB", Normalizer.Normalize("\uD55C", NormalizationForm.Nfd));
        Assert.Equal("\u00E9", Normalizer.Normalize("e\u0301", NormalizationForm.Nfc));
        Assert.Equal("1/4", Normalizer.Normalize("\u00BC", NormalizationForm.Nfkc).Replace('\u2044', '/'));
    }

    [Fact]
    public void LongMarkRunIsSortedInLinearishTime()
    {
        // 200,000 marks of alternating class: the run is sorted once (n log n), never quadratically.
        string s = "a" + string.Concat(Enumerable.Repeat("\u0301\u0316", 100_000));
        string nfd = Normalizer.Normalize(s, NormalizationForm.Nfd);
        Assert.Equal(s.Length, nfd.Length);
        Assert.Equal('\u0316', nfd[1]);
        Assert.Equal('\u0301', nfd[^1]);
    }

    [Fact]
    public void AsciiIsReturnedAsTheSameInstance()
    {
        string s = "plain ascii text";
        Assert.Same(s, Normalizer.Normalize(s, NormalizationForm.Nfc));
        Assert.Same(s, Normalizer.Normalize(s, NormalizationForm.Nfkd));
    }

    [Fact]
    public void TryNormalizeReportsRequiredLength()
    {
        Span<char> small = stackalloc char[1];
        Assert.False(Normalizer.TryNormalize("\u00E9", small, NormalizationForm.Nfd, out int needed));
        Assert.Equal(2, needed);
        Span<char> ok = stackalloc char[2];
        Assert.True(Normalizer.TryNormalize("\u00E9", ok, NormalizationForm.Nfd, out int written));
        Assert.Equal(2, written);
        Assert.Equal("e\u0301", ok.ToString());
    }
}
