using System.Reflection;

namespace RecursiveAI.OneEcma.Intl.Data;

/// <summary>
/// Entry point to the embedded locale data: CLDR 48.0 (locale and supplemental data, plural rules, root
/// collation) and Unicode 17.0.0 break properties. Everything loads on first use and is cached; the data
/// is immutable, so all members are thread-safe.
/// </summary>
internal static class CldrData
{
    /// <summary>The CLDR release the data was generated from; equals the reference Node's <c>process.versions.cldr</c>.</summary>
    internal const string CldrVersion = "48.0";

    /// <summary>The Unicode Character Database version of the break and collation tables.</summary>
    internal const string UnicodeVersion = "17.0.0";

    /// <summary>
    /// The locales Intl supports, as canonical BCP 47 tags. <c>en-US</c> and <c>pt-BR</c> are CLDR default
    /// content of <c>en</c> and <c>pt</c>, so their data is that of the language locale.
    /// </summary>
    internal static readonly string[] SupportedLocales = ["en-US", "en-GB", "es", "es-US", "fr", "de", "pt-BR"];

    private static readonly object Gate = new();
    private static readonly Dictionary<string, DataTree> Trees = new(StringComparer.Ordinal);
    private static PluralData? s_plurals;
    private static CollationData? s_collation;
    private static BreakData? s_breaks;
    private static NormalizationData? s_normalization;

    /// <summary>Supplemental data: likely subtags, aliases, parent locales, currency, week and time data, and more.</summary>
    internal static DataTree Supplemental => GetTree("supplemental", "supplemental.bin", null);

    /// <summary>Cardinal and ordinal plural rules for every language CLDR has rules for.</summary>
    internal static PluralData Plurals
    {
        get
        {
            lock (Gate)
            {
                return s_plurals ??= PluralData.Open(Load("plurals.bin"));
            }
        }
    }

    /// <summary>The root collation table and implicit-weight rules.</summary>
    internal static CollationData Collation
    {
        get
        {
            lock (Gate)
            {
                return s_collation ??= CollationData.Open(Load("collation.bin"));
            }
        }
    }

    /// <summary>The UAX #15 normalization tables (combining classes, decompositions, composites).</summary>
    internal static NormalizationData Normalization
    {
        get
        {
            lock (Gate)
            {
                return s_normalization ??= NormalizationData.Open(Load("normalization.bin"));
            }
        }
    }

    /// <summary>The UAX #29 break property tables.</summary>
    internal static BreakData Breaks
    {
        get
        {
            lock (Gate)
            {
                return s_breaks ??= BreakData.Open(Load("breaks.bin"));
            }
        }
    }

    /// <summary>
    /// Maps a canonical locale tag to the stored locale whose data serves it, by dropping trailing subtags
    /// (ECMA-402 BestAvailableLocale): <c>en-US</c> gives <c>en</c>, <c>es-US</c> stays <c>es-US</c>.
    /// </summary>
    /// <param name="tag">A canonical-case tag without extensions, such as <c>pt-BR</c> or <c>zh-Hans-CN</c>.</param>
    /// <param name="id">The stored locale id when the result is true.</param>
    /// <returns>False when no stored locale other than the root matches the language.</returns>
    internal static bool TryResolveLocale(string tag, out string id)
    {
        ArgumentNullException.ThrowIfNull(tag);
        DataTree sup = Supplemental;
        string candidate = tag;
        while (candidate.Length > 0)
        {
            if (candidate != "und" && sup.KindOf("locales", candidate) == TreeKind.String)
            {
                id = candidate;
                return true;
            }

            int dash = candidate.LastIndexOf('-');
            if (dash < 0)
            {
                break;
            }

            candidate = candidate[..dash];
        }

        id = "";
        return false;
    }

    /// <summary>
    /// The data of a locale with its inheritance chain applied. Accepts any tag <see cref="TryResolveLocale"/>
    /// resolves, and <c>und</c> / <c>root</c> for the root locale.
    /// </summary>
    /// <exception cref="ArgumentException">No stored locale serves the tag.</exception>
    internal static DataTree GetLocale(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        string id = tag is "und" or "root" ? "und" : TryResolveLocale(tag, out string r) ? r : throw new ArgumentException($"No locale data for '{tag}'.", nameof(tag));
        return GetTree(id, null, id);
    }

    private static DataTree GetTree(string key, string? resource, string? localeId)
    {
        lock (Gate)
        {
            if (Trees.TryGetValue(key, out DataTree? cached))
            {
                return cached;
            }

            var chain = new List<TreeBlob>();
            if (localeId is null)
            {
                chain.Add(TreeBlob.Open(Load(resource!)));
            }
            else
            {
                string? id = localeId;
                while (!string.IsNullOrEmpty(id))
                {
                    TreeBlob blob = TreeBlob.Open(Load($"locale-{id}.bin"));
                    chain.Add(blob);
                    id = blob.TryGetChild(blob.Root, "@parent", out int p) ? blob.StringOf(p) : null;
                }
            }

            var tree = new DataTree(key, [.. chain]);
            Trees[key] = tree;
            return tree;
        }
    }

    private static byte[] Load(string name)
    {
        Assembly asm = typeof(CldrData).Assembly;
        using Stream s = asm.GetManifestResourceStream("oneecma-data/" + name)
            ?? throw new InvalidDataException($"Embedded locale data '{name}' is missing.");
        var bytes = new byte[(int)s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }
}
