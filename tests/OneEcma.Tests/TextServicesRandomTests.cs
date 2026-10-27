using RecursiveAI.OneEcma.TextServices;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>Seeded random text through every text service: invariants that must hold for any UTF-16, including lone surrogates.</summary>
public class TextServicesRandomTests
{
    private static readonly int[] Pool =
    [
        'a', 'A', 'e', 'n', 'N', 'z', 'Z', ' ', '-', '.', ',', '\'', '_', '0', '1', '9', '\r', '\n', '\t',
        0xC0, 0xE9, 0xF1, 0xDF, 0xC5, 0x212B, 0x1E9E, 0x132, 0x133, 0xFB01, 0x3392, 0xBD, 0x2189,
        0x300, 0x301, 0x303, 0x308, 0x323, 0x316, 0x334, 0x345, 0x5B0, 0x64B, 0x93C, 0x94D, 0xE31, 0xF71, 0xF72, 0xF74,
        0x915, 0x937, 0x93F, 0xE01, 0xE40, 0x1100, 0x1161, 0x11A8, 0xAC00, 0xAC01, 0xD7A3,
        0x200D, 0x200C, 0xFE0F, 0x20E3, 0x1F1E6, 0x1F1FA, 0x1F600, 0x1F468, 0x1F3FD, 0x2764,
        0x3041, 0x3042, 0x30A2, 0xFF71, 0x4E00, 0x4E8C, 0x660, 0x663, 0x966, 0xFF11, 0x1D7CE,
        0x5D0, 0x5D1, 0x22, 0x627, 0x628, 0x3B1, 0x3A3, 0x3C2, 0x2E, 0x3002, 0x2018, 0x201D, 0x2028, 0x85, 0xAD,
        0xD800, 0xDC00, 0xDBFF, 0xDFFF, 0xFFFE, 0xFFFF, 0x10FFFF, 0x0, 0x1,
    ];

    private static string Random(System.Random rng)
    {
        var sb = new System.Text.StringBuilder();
        int n = rng.Next(0, 24);
        for (int i = 0; i < n; i++)
        {
            int cp = Pool[rng.Next(Pool.Length)];
            if (cp >= 0x10000)
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
            else
            {
                sb.Append((char)cp);
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void NormalizationAndSegmentationInvariants()
    {
        var rng = new System.Random(20260101);
        for (int i = 0; i < 4000; i++)
        {
            string s = Random(rng);
            foreach (NormalizationForm f in Enum.GetValues<NormalizationForm>())
            {
                string n = Normalizer.Normalize(s, f);
                Assert.Equal(n, Normalizer.Normalize(n, f));
                Assert.True(Normalizer.IsNormalized(n, f), $"{f} of {Escape(s)}");
            }

            Assert.Equal(Normalizer.Normalize(s, NormalizationForm.Nfc), Normalizer.Normalize(Normalizer.Normalize(s, NormalizationForm.Nfd), NormalizationForm.Nfc));
            Assert.Equal(Normalizer.Normalize(s, NormalizationForm.Nfkc), Normalizer.Normalize(Normalizer.Normalize(s, NormalizationForm.Nfkd), NormalizationForm.Nfkc));

            foreach (SegmentGranularity g in Enum.GetValues<SegmentGranularity>())
            {
                int pos = 0;
                foreach (var seg in TextSegmenter.Enumerate(s, g))
                {
                    Assert.Equal(pos, seg.Start);
                    Assert.True(seg.End > seg.Start);
                    pos = seg.End;
                }

                Assert.Equal(s.Length, pos);
            }
        }
    }

    [Fact]
    public void CollationIsAConsistentOrder()
    {
        var rng = new System.Random(7);
        var items = Enumerable.Range(0, 1500).Select(_ => Random(rng)).ToArray();
        var optionSets = new[]
        {
            new CollatorOptions(),
            new CollatorOptions { Numeric = true, IgnorePunctuation = true },
            new CollatorOptions { CaseFirst = CaseFirst.Upper, Strength = CollationStrength.Identical },
            new CollatorOptions { CaseLevel = true, Strength = CollationStrength.Primary },
        };
        foreach (CollatorOptions o in optionSets)
        {
            TextCollator c = TextCollator.Create("es", o);

            // Array.Sort throws when the comparer is inconsistent; sorting also exercises transitivity.
            var sorted = (string[])items.Clone();
            Array.Sort(sorted, (a, b) => c.Compare(a, b));
            for (int i = 0; i + 1 < sorted.Length; i++)
            {
                Assert.True(c.Compare(sorted[i], sorted[i + 1]) <= 0);
                Assert.Equal(-Math.Sign(c.Compare(sorted[i + 1], sorted[i])), Math.Sign(c.Compare(sorted[i], sorted[i + 1])));
            }

            foreach (string s in items.Take(200))
            {
                Assert.Equal(0, c.Compare(s, s));
                Assert.Equal(0, c.Compare(s, Normalizer.Normalize(s, NormalizationForm.Nfd)));
            }
        }
    }

    private static string Escape(string s) => string.Concat(s.Select(ch => $"\\u{(int)ch:X4}"));
}
