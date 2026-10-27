using System.Text;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>
/// A parsed Unicode BCP 47 locale identifier (UTS #35 Part 1, "Unicode Language and Locale Identifiers";
/// ECMA-402 6.2.1 IsStructurallyValidLanguageTag). Instances are mutable while being built by the
/// parser and the option overrides; <see cref="Canonicalize"/> brings them to the canonical form of
/// UTS #35 Annex C and ECMA-402 6.2.3 CanonicalizeUnicodeLocaleId.
/// </summary>
internal sealed class LocaleId
{
    /// <summary>The language subtag (2-3 or 5-8 letters), lowercase. <c>und</c> when unknown.</summary>
    internal string Language { get; set; } = "und";

    /// <summary>The four-letter script subtag in title case, or null.</summary>
    internal string? Script { get; set; }

    /// <summary>The region subtag (two letters uppercase or three digits), or null.</summary>
    internal string? Region { get; set; }

    /// <summary>Variant subtags, lowercase; sorted by <see cref="Canonicalize"/>.</summary>
    internal List<string> Variants { get; } = [];

    /// <summary>Unicode extension attributes (<c>-u-attr</c>), lowercase.</summary>
    internal List<string> Attributes { get; } = [];

    /// <summary>Unicode extension keywords in source order; an empty value means <c>true</c>.</summary>
    internal List<KeyValuePair<string, string>> Keywords { get; } = [];

    /// <summary>The <c>-t-</c> extension's source language, or null.</summary>
    internal string? TransformLanguage { get; set; }

    /// <summary>The <c>-t-</c> extension's fields (key such as <c>m0</c>, value).</summary>
    internal List<KeyValuePair<string, string>> TransformFields { get; } = [];

    /// <summary>Other single-letter extensions, keyed by singleton, value is the hyphen-joined subtags.</summary>
    internal SortedDictionary<char, string> OtherExtensions { get; } = [];

    /// <summary>The private-use subtags after <c>-x-</c> (hyphen-joined), or null.</summary>
    internal string? PrivateUse { get; set; }

    /// <summary>Whether a <c>-u-</c> extension exists (even with no keywords).</summary>
    internal bool HasUnicodeExtension { get; set; }

    /// <summary>Whether a <c>-t-</c> extension exists.</summary>
    internal bool HasTransformExtension { get; set; }

    /// <summary>The value of a Unicode extension keyword, or null; <c>true</c> keywords give an empty string.</summary>
    internal string? GetKeyword(string key)
    {
        foreach (var kv in Keywords)
        {
            if (kv.Key == key)
            {
                return kv.Value;
            }
        }

        return null;
    }

    /// <summary>Sets (or replaces) a Unicode extension keyword, keeping its position when it exists.</summary>
    internal void SetKeyword(string key, string value)
    {
        HasUnicodeExtension = true;
        for (int i = 0; i < Keywords.Count; i++)
        {
            if (Keywords[i].Key == key)
            {
                Keywords[i] = new(key, value);
                return;
            }
        }

        Keywords.Add(new(key, value));
    }

    /// <summary>Removes a Unicode extension keyword.</summary>
    internal void RemoveKeyword(string key) => Keywords.RemoveAll(k => k.Key == key);

    /// <summary>A deep copy.</summary>
    internal LocaleId Clone()
    {
        var c = new LocaleId
        {
            Language = Language,
            Script = Script,
            Region = Region,
            TransformLanguage = TransformLanguage,
            PrivateUse = PrivateUse,
            HasUnicodeExtension = HasUnicodeExtension,
            HasTransformExtension = HasTransformExtension,
        };
        c.Variants.AddRange(Variants);
        c.Attributes.AddRange(Attributes);
        c.Keywords.AddRange(Keywords);
        c.TransformFields.AddRange(TransformFields);
        foreach (var kv in OtherExtensions)
        {
            c.OtherExtensions[kv.Key] = kv.Value;
        }

        return c;
    }

    // ---- parsing ----

    /// <summary>Whether <paramref name="s"/> is 2-3 or 5-8 ASCII letters (unicode_language_subtag).</summary>
    internal static bool IsLanguageSubtag(string s) => (s.Length is 2 or 3 or (>= 5 and <= 8)) && AllAlpha(s);

    /// <summary>Whether <paramref name="s"/> is 4 ASCII letters (unicode_script_subtag).</summary>
    internal static bool IsScriptSubtag(string s) => s.Length == 4 && AllAlpha(s);

    /// <summary>Whether <paramref name="s"/> is 2 letters or 3 digits (unicode_region_subtag).</summary>
    internal static bool IsRegionSubtag(string s) => (s.Length == 2 && AllAlpha(s)) || (s.Length == 3 && AllDigit(s));

    /// <summary>Whether <paramref name="s"/> is 5-8 alphanumerics or a digit plus 3 alphanumerics (unicode_variant_subtag).</summary>
    internal static bool IsVariantSubtag(string s) =>
        ((s.Length is >= 5 and <= 8) && AllAlnum(s)) || (s.Length == 4 && IsDigit(s[0]) && AllAlnum(s));

    private static bool IsAlpha(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';

    private static bool IsDigit(char c) => (uint)(c - '0') <= 9;

    private static bool AllAlpha(string s) => s.All(IsAlpha);

    private static bool AllDigit(string s) => s.All(IsDigit);

    private static bool AllAlnum(string s) => s.All(c => IsAlpha(c) || IsDigit(c));

    private static string Lower(string s)
    {
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                char c = src[i];
                span[i] = (uint)(c - 'A') <= 'Z' - 'A' ? (char)(c | 0x20) : c;
            }
        });
    }

    private static string Upper(string s)
    {
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                char c = src[i];
                span[i] = (uint)(c - 'a') <= 'z' - 'a' ? (char)(c & ~0x20) : c;
            }
        });
    }

    private static string Title(string s) => Upper(s[..1]) + Lower(s[1..]);

    /// <summary>
    /// Parses a unicode_locale_id (ECMA-402 6.2.1). Accepts any letter case; the result is case-normalized
    /// but not otherwise canonicalized. Returns null when the tag is not structurally valid.
    /// </summary>
    internal static LocaleId? Parse(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length == 0 || tag.Length > 4096)
        {
            return null;
        }

        string[] subtags = tag.Split('-');
        foreach (string st in subtags)
        {
            if (st.Length is 0 or > 8 || !AllAlnum(st))
            {
                return null;
            }
        }

        var id = new LocaleId();
        int pos = 0;
        string first = subtags[pos];
        if (!IsLanguageSubtag(first))
        {
            return null;
        }

        id.Language = Lower(first);
        pos++;
        if (!ParseLanguageTail(id, subtags, ref pos, allowExtensionStart: true))
        {
            return null;
        }

        var seen = new HashSet<char>();
        while (pos < subtags.Length)
        {
            string st = subtags[pos];
            if (st.Length != 1)
            {
                return null;
            }

            char singleton = (char)(st[0] | 0x20);
            if (!seen.Add(singleton))
            {
                return null;
            }

            pos++;
            if (singleton == 'x')
            {
                if (pos >= subtags.Length)
                {
                    return null;
                }

                id.PrivateUse = Lower(string.Join('-', subtags[pos..]));
                pos = subtags.Length;
                break;
            }

            int start = pos;
            while (pos < subtags.Length && subtags[pos].Length > 1)
            {
                pos++;
            }

            if (pos == start)
            {
                return null;
            }

            string[] body = [.. subtags[start..pos].Select(Lower)];
            switch (singleton)
            {
                case 'u':
                    if (!ParseUnicodeExtension(id, body))
                    {
                        return null;
                    }

                    break;
                case 't':
                    if (!ParseTransformExtension(id, body))
                    {
                        return null;
                    }

                    break;
                default:
                    if (body.Any(b => b.Length < 2))
                    {
                        return null;
                    }

                    id.OtherExtensions[singleton] = string.Join('-', body);
                    break;
            }
        }

        return id;
    }

    // Parses [script] [region] *variant starting at pos. Stops at a singleton.
    private static bool ParseLanguageTail(LocaleId id, string[] st, ref int pos, bool allowExtensionStart)
    {
        if (pos < st.Length && IsScriptSubtag(st[pos]))
        {
            id.Script = Title(st[pos]);
            pos++;
        }

        if (pos < st.Length && IsRegionSubtag(st[pos]))
        {
            id.Region = Upper(st[pos]);
            pos++;
        }

        while (pos < st.Length && IsVariantSubtag(st[pos]))
        {
            string v = Lower(st[pos]);
            if (id.Variants.Contains(v))
            {
                return false;
            }

            id.Variants.Add(v);
            pos++;
        }

        _ = allowExtensionStart;
        return true;
    }

    private static bool ParseUnicodeExtension(LocaleId id, string[] body)
    {
        id.HasUnicodeExtension = true;
        int i = 0;
        while (i < body.Length && body[i].Length >= 3)
        {
            id.Attributes.Add(body[i]);
            i++;
        }

        while (i < body.Length)
        {
            string key = body[i];
            if (key.Length != 2 || !IsAlpha(key[1]))
            {
                return false;
            }

            i++;
            int s = i;
            while (i < body.Length && body[i].Length >= 3)
            {
                i++;
            }

            string value = string.Join('-', body[s..i]);
            if (id.GetKeyword(key) is null)
            {
                id.Keywords.Add(new(key, value));
            }
        }

        return true;
    }

    private static bool ParseTransformExtension(LocaleId id, string[] body)
    {
        id.HasTransformExtension = true;
        int i = 0;
        if (IsLanguageSubtag(body[0]))
        {
            // tlang: language [script] [region] *variant, ending where the first tfield key (alpha digit) begins.
            var sub = new LocaleId { Language = body[0] };
            i = 1;
            int p = 1;
            if (!ParseLanguageTail(sub, body, ref p, false))
            {
                return false;
            }

            i = p;
            id.TransformLanguage = sub.ToString();
        }

        while (i < body.Length)
        {
            string key = body[i];
            if (key.Length != 2 || !IsAlpha(key[0]) || !IsDigit(key[1]))
            {
                return false;
            }

            i++;
            int s = i;
            while (i < body.Length && body[i].Length >= 3)
            {
                i++;
            }

            if (i == s)
            {
                return false;
            }

            id.TransformFields.Add(new(key, string.Join('-', body[s..i])));
        }

        return id.TransformLanguage is not null || id.TransformFields.Count > 0;
    }

    // ---- output ----

    /// <summary>The language, script, region and variants only (the "language identifier"), as a tag.</summary>
    internal string LanguageTag()
    {
        var sb = new StringBuilder(Language);
        if (Script is not null)
        {
            sb.Append('-').Append(Script);
        }

        if (Region is not null)
        {
            sb.Append('-').Append(Region);
        }

        foreach (string v in Variants)
        {
            sb.Append('-').Append(v);
        }

        return sb.ToString();
    }

    /// <summary>The tag with extensions in singleton order (<c>t</c>, <c>u</c>, others, then private use).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(LanguageTag());
        var ext = new SortedDictionary<char, string>(OtherExtensions);
        if (HasUnicodeExtension && (Attributes.Count > 0 || Keywords.Count > 0))
        {
            var u = new StringBuilder();
            foreach (string a in Attributes)
            {
                u.Append('-').Append(a);
            }

            foreach (var kv in Keywords)
            {
                u.Append('-').Append(kv.Key);
                if (kv.Value.Length > 0)
                {
                    u.Append('-').Append(kv.Value);
                }
            }

            ext['u'] = u.ToString(1, u.Length - 1);
        }

        if (HasTransformExtension && (TransformLanguage is not null || TransformFields.Count > 0))
        {
            var t = new StringBuilder();
            if (TransformLanguage is not null)
            {
                t.Append('-').Append(TransformLanguage);
            }

            foreach (var kv in TransformFields)
            {
                t.Append('-').Append(kv.Key).Append('-').Append(kv.Value);
            }

            ext['t'] = t.ToString(1, t.Length - 1);
        }

        foreach (var kv in ext)
        {
            sb.Append('-').Append(kv.Key).Append('-').Append(kv.Value);
        }

        if (PrivateUse is not null)
        {
            sb.Append("-x-").Append(PrivateUse);
        }

        return sb.ToString();
    }

    // ---- canonicalization (UTS #35 Annex C; ECMA-402 6.2.3) ----

    /// <summary>
    /// Canonicalizes in place: alias replacement for language, script, region and variants, variant and
    /// extension ordering, keyword type aliases, and removal of <c>true</c> values.
    /// </summary>
    internal LocaleId Canonicalize()
    {
        Aliases.ReplaceLanguageAliases(this);
        if (Script is not null && Aliases.Script.TryGetValue(Script.ToLowerInvariantAscii(), out string? rs))
        {
            Script = Title(rs);
        }

        if (Region is not null && Aliases.Territory.TryGetValue(Region.ToLowerInvariantAscii(), out string? rr))
        {
            Region = Aliases.PickRegion(this, rr);
        }

        for (int i = 0; i < Variants.Count; i++)
        {
            if (Aliases.Variant.TryGetValue(Variants[i], out string? rv))
            {
                Variants[i] = Lower(rv);
            }
        }

        Variants.Sort(StringComparer.Ordinal);
        for (int i = Variants.Count - 1; i > 0; i--)
        {
            if (Variants[i] == Variants[i - 1])
            {
                Variants.RemoveAt(i);
            }
        }

        Attributes.Sort(StringComparer.Ordinal);
        List<string> distinct = [.. Attributes.Distinct()];
        Attributes.Clear();
        Attributes.AddRange(distinct);
        var kws = new List<KeyValuePair<string, string>>();
        foreach (var kv in Keywords.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string v = KeywordAliases.CanonicalType(kv.Key, kv.Value);
            if (v == "true")
            {
                v = "";
            }

            kws.Add(new(kv.Key, v));
        }

        Keywords.Clear();
        Keywords.AddRange(kws);
        if (Keywords.Count == 0 && Attributes.Count == 0)
        {
            HasUnicodeExtension = false;
        }

        if (HasTransformExtension)
        {
            if (TransformLanguage is not null)
            {
                LocaleId? tl = Parse(TransformLanguage);
                if (tl is not null)
                {
                    TransformLanguage = Lower(tl.Canonicalize().ToString());
                }
            }

            var sorted = TransformFields.OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
            TransformFields.Clear();
            foreach (var kv in sorted)
            {
                TransformFields.Add(kv.Key == "m0" ? new(kv.Key, KeywordAliases.CanonicalTransformValue(kv.Value)) : kv);
            }
        }

        return this;
    }

    // ---- likely subtags (UTS #35 "Likely Subtags") ----

    /// <summary>Add Likely Subtags: fills in script and region. Returns this.</summary>
    internal LocaleId Maximize()
    {
        // UTS #35: the unknown script (Zzzz) and region (ZZ) count as absent when looking up, and are replaced.
        string? script = Script == "Zzzz" ? null : Script;
        string? region = Region == "ZZ" ? null : Region;
        string? likely = LikelyFor(Language, script, region);
        if (likely is null)
        {
            return this;
        }

        LocaleId l = Parse(likely)!;
        if (Language == "und")
        {
            Language = l.Language;
        }

        Script = script ?? l.Script;
        Region = region ?? l.Region;
        return this;
    }

    /// <summary>Remove Likely Subtags, favoring script over region as UTS #35 does. Returns this.</summary>
    internal LocaleId Minimize()
    {
        LocaleId max = Clone().Maximize();
        string target = max.LanguageTagNoVariants();
        foreach (var (s, r) in new[] { ((string?)null, (string?)null), (null, max.Region), (max.Script, null) })
        {
            var trial = new LocaleId { Language = max.Language, Script = s, Region = r };
            if (trial.Maximize().LanguageTagNoVariants() == target)
            {
                Language = max.Language;
                Script = s;
                Region = r;
                return this;
            }
        }

        Script = max.Script;
        Region = max.Region;
        Language = max.Language;
        return this;
    }

    private string LanguageTagNoVariants() => Language + (Script is null ? "" : "-" + Script) + (Region is null ? "" : "-" + Region);

    private static string? LikelyFor(string lang, string? script, string? region)
    {
        DataTree sup = CldrData.Supplemental;
        string?[] keys =
        [
            script is not null && region is not null ? $"{lang}-{script}-{region}" : null,
            region is not null ? $"{lang}-{region}" : null,
            script is not null ? $"{lang}-{script}" : null,
            lang,
            script is not null ? $"und-{script}" : null,
        ];
        foreach (string? k in keys)
        {
            if (k is not null && sup.GetString("likelySubtags", k) is { } v)
            {
                return v;
            }
        }

        // Only the undetermined language falls back to the "und" entry; an unknown language is left as it is.
        return lang == "und" ? sup.GetString("likelySubtags", "und") : null;
    }
}

internal static class AsciiExtensions
{
    /// <summary>ASCII-only lowercase (culture-free).</summary>
    internal static string ToLowerInvariantAscii(this string s)
    {
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                char c = src[i];
                span[i] = (uint)(c - 'A') <= 'Z' - 'A' ? (char)(c | 0x20) : c;
            }
        });
    }
}
