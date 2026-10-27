using System.Globalization;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Locates the official Unicode conformance files (NormalizationTest.txt, the break tests, the CLDR
/// collation tests). They are fetched by <c>tools/locale-data/fetch.sh</c> and never committed; the root
/// is <c>$ONEECMA_LOCALE_DATA</c> or <c>~/oneecma/locale-data</c>.
/// </summary>
internal static class TextTestFiles
{
    /// <summary>Directory the locale-data fetch script fills.</summary>
    internal static string Root
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("ONEECMA_LOCALE_DATA");
            return !string.IsNullOrEmpty(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "oneecma", "locale-data");
        }
    }

    /// <summary>Full path of a UCD file under <c>ucd/17.0.0</c>.</summary>
    internal static string Ucd(string name) => Path.Combine(Root, "ucd", "17.0.0", name);

    /// <summary>Full path of a file of the CLDR common archive under <c>cldr/48.0/common/common</c>.</summary>
    internal static string Cldr(params string[] parts) => Path.Combine([Root, "cldr", "48.0", "common", "common", .. parts]);

    /// <summary>Skips the calling test with a message that says how to get the file.</summary>
    internal static string RequireOrSkip(string path)
    {
        if (!File.Exists(path))
        {
            Assert.Skip($"{path} is missing; run tools/locale-data/fetch.sh (or set ONEECMA_LOCALE_DATA) to fetch it.");
        }

        return path;
    }

    /// <summary>Parses space-separated hexadecimal code points into a string.</summary>
    internal static string FromHex(string field)
    {
        var sb = new System.Text.StringBuilder();
        foreach (string h in field.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append(char.ConvertFromUtf32(int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }
}
