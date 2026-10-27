using System.Buffers;
using System.Collections.Concurrent;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.TextServices;

/// <summary>How many comparison levels count (UTS #10 section 5; ECMA-402 <c>sensitivity</c> maps onto these).</summary>
internal enum CollationStrength
{
    /// <summary>Base letters only: "a" = "A" = "á".</summary>
    Primary,

    /// <summary>Base letters and accents.</summary>
    Secondary,

    /// <summary>Base letters, accents and case or variants. The default.</summary>
    Tertiary,

    /// <summary>Adds the fourth level (only meaningful with <see cref="CollatorOptions.IgnorePunctuation"/>).</summary>
    Quaternary,

    /// <summary>Everything above, then the code points (UTS #10 S3.10): equal only for canonically equivalent text.</summary>
    Identical,
}

/// <summary>ECMA-402 <c>caseFirst</c>.</summary>
internal enum CaseFirst
{
    /// <summary>The table's own tertiary order (lowercase before uppercase).</summary>
    Off,

    /// <summary>Lowercase before uppercase, ahead of other tertiary differences.</summary>
    Lower,

    /// <summary>Uppercase before lowercase, ahead of other tertiary differences.</summary>
    Upper,
}

/// <summary>ECMA-402 <c>usage</c>.</summary>
internal enum CollationUsage
{
    /// <summary>Ordering.</summary>
    Sort,

    /// <summary>Matching: uses the CLDR search collation, which equates things users do not distinguish when searching.</summary>
    Search,
}

/// <summary>Settings of a <see cref="TextCollator"/>.</summary>
internal sealed record CollatorOptions
{
    /// <summary>Levels compared. Default <see cref="CollationStrength.Tertiary"/> (ECMA-402 <c>sensitivity: "variant"</c>).</summary>
    public CollationStrength Strength { get; init; } = CollationStrength.Tertiary;

    /// <summary>Compare case as its own level after accents (ECMA-402 <c>sensitivity: "case"</c> is primary strength with this on).</summary>
    public bool CaseLevel { get; init; }

    /// <summary>ECMA-402 <c>caseFirst</c>.</summary>
    public CaseFirst CaseFirst { get; init; }

    /// <summary>ECMA-402 <c>numeric</c> (<c>kn</c>): runs of decimal digits compare by value.</summary>
    public bool Numeric { get; init; }

    /// <summary>ECMA-402 <c>ignorePunctuation</c>: spaces and punctuation are ignored (UTS #10 "shifted").</summary>
    public bool IgnorePunctuation { get; init; }

    /// <summary>ECMA-402 <c>usage</c>.</summary>
    public CollationUsage Usage { get; init; }

    /// <summary>
    /// A BCP 47 collation type from a <c>-u-co-</c> extension, such as <c>phonebk</c> (German) or <c>trad</c>
    /// (Spanish); ignored when the locale has no such collation. Null for the locale's standard collation.
    /// </summary>
    public string? Collation { get; init; }

    /// <summary>Maps ECMA-402 <c>sensitivity</c> (<c>base</c>, <c>accent</c>, <c>case</c>, <c>variant</c>) to strength and case level.</summary>
    /// <param name="sensitivity">One of the four ECMA-402 values.</param>
    /// <returns>This record's strength and case-level settings.</returns>
    /// <exception cref="ArgumentException">The value is not one of the four.</exception>
    public CollatorOptions WithSensitivity(string sensitivity) => sensitivity switch
    {
        "base" => this with { Strength = CollationStrength.Primary, CaseLevel = false },
        "accent" => this with { Strength = CollationStrength.Secondary, CaseLevel = false },
        "case" => this with { Strength = CollationStrength.Primary, CaseLevel = true },
        "variant" => this with { Strength = CollationStrength.Tertiary, CaseLevel = false },
        _ => throw new ArgumentException($"Unknown sensitivity '{sensitivity}'.", nameof(sensitivity)),
    };
}

/// <summary>
/// A Unicode Collation Algorithm (UTS #10) collator over the CLDR root order of Unicode 17.0.0, with the
/// tailorings (UTS #35) of the supported locales. Text is decomposed to NFD, mapped to collation elements
/// (contractions, discontiguous contractions, implicit weights, numeric runs), and turned into a sort key of
/// per-level weights; <see cref="Compare(ReadOnlySpan{char}, ReadOnlySpan{char})"/> compares those keys.
/// </summary>
/// <remarks>
/// Both comparison and sort keys are provided. Keys are byte strings whose ordinary byte comparison equals
/// <c>Compare</c>, so a caller that sorts many strings, or compares one string many times, builds keys once.
/// Instances are immutable and thread-safe. Han ideographs sort by code point, not by radical and stroke.
/// Backwards secondary weights (French Canadian) and reordering of scripts are not supported.
/// </remarks>
internal sealed class TextCollator
{
    private static readonly ConcurrentDictionary<string, CollationTable> Tables = new(StringComparer.Ordinal);

    private readonly CollationTable _table;
    private readonly CollatorOptions _options;
    private readonly uint _zeroPrimary;

    private TextCollator(CollationTable table, CollatorOptions options)
    {
        _table = table;
        _options = options;
        _zeroPrimary = table.Root.TryGetSingle('0', out CollationElement[] zero) ? (uint)zero[0].Primary << Ce.PrimaryScale : 0;
    }

    /// <summary>Creates a collator for a locale.</summary>
    /// <param name="locale">A BCP 47 tag without extensions. A locale with no data falls back to the root order.</param>
    /// <param name="options">Settings; null means defaults.</param>
    internal static TextCollator Create(string locale, CollatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(locale);
        options ??= new CollatorOptions();
        string language = LanguageOf(locale);
        string type = options.Usage == CollationUsage.Search ? "search" : MapType(options.Collation);
        return new TextCollator(TableFor(language, type), options);
    }

    private static string LanguageOf(string locale)
    {
        if (!CldrData.TryResolveLocale(locale, out string id))
        {
            return "root";
        }

        int dash = id.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? id : id[..dash];
    }

    // BCP 47 collation types to the names CLDR uses in its collation files.
    private static string MapType(string? bcp47) => bcp47 switch
    {
        null or "" or "standard" => "standard",
        "phonebk" => "phonebook",
        "trad" => "traditional",
        _ => bcp47,
    };

    private static string? RulesFor(string language, string type)
    {
        DataTree sup = CldrData.Supplemental;
        return sup.GetString("collationTailoring", language, type);
    }

    private static CollationTable TableFor(string language, string type)
    {
        string? rules = RulesFor(language, type);
        if (rules is null && type == "search")
        {
            rules = RulesFor("root", "search");
            language = "root";
        }

        string key = rules is null ? "root|standard" : language + "|" + type;
        return Tables.GetOrAdd(key, _ =>
        {
            var table = new CollationTable(CldrData.Collation);
            if (rules is not null)
            {
                CollationRules.Apply(rules, table, ResolveImport);
            }

            return table;
        });
    }

    // [import de-u-co-phonebk] / [import und-u-co-search]
    private static string? ResolveImport(string target)
    {
        int marker = target.IndexOf("-u-co-", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        string lang = target[..marker];
        return RulesFor(lang == "und" ? "root" : lang, MapType(target[(marker + 6)..]));
    }

    /// <summary>The settings this collator was created with.</summary>
    internal CollatorOptions Options => _options;

    // ---- Collation elements ------------------------------------------------------------------------------

    private void Elements(ReadOnlySpan<char> text, CeBuffer output)
    {
        using var cps = new CodePointBuffer(text.Length + 4);
        Normalizer.Decompose(text, false, cps);
        Span<int> all = cps.Span;
        if (!_options.Numeric)
        {
            _table.Append(all, output);
            return;
        }

        CollationData root = _table.Root;
        int i = 0;
        int segment = 0;
        while (i < all.Length)
        {
            if (root.DecimalDigitValue(all[i]) < 0)
            {
                i++;
                continue;
            }

            _table.Append(all[segment..i], output);
            int end = i;
            while (end < all.Length && root.DecimalDigitValue(all[end]) >= 0)
            {
                end++;
            }

            AppendNumber(root, all[i..end], output);
            i = end;
            segment = end;
        }

        _table.Append(all[segment..], output);
    }

    // A run of digits becomes: a marker element just below the primary of "0" (numbers sort before any
    // non-decimal use of a digit, such as the "0" inside a fraction character), the digit count, then base-100 pairs
    // from the most significant end. Leading zeros do not count, so 007 = 7 (ECMA-402 numeric).
    private void AppendNumber(CollationData root, ReadOnlySpan<int> digits, CeBuffer output)
    {
        int first = 0;
        while (first < digits.Length - 1 && root.DecimalDigitValue(digits[first]) == 0)
        {
            first++;
        }

        ReadOnlySpan<int> significant = digits[first..];
        output.Add(new Ce(_zeroPrimary - 1, Ce.CommonSecondary, Ce.CommonTertiary, Ce.CommonTertiary, 0));
        output.Add(new Ce((uint)Math.Min(significant.Length, 0x0FFFFFFF), 0, 0, 0, 0));
        int i = 0;
        if ((significant.Length & 1) == 1)
        {
            output.Add(new Ce((uint)root.DecimalDigitValue(significant[0]) + 1, 0, 0, 0, 0));
            i = 1;
        }

        for (; i < significant.Length; i += 2)
        {
            int pair = (root.DecimalDigitValue(significant[i]) * 10) + root.DecimalDigitValue(significant[i + 1]);
            output.Add(new Ce((uint)pair + 1, 0, 0, 0, 0));
        }
    }

    // ---- Sort keys ---------------------------------------------------------------------------------------

    /// <summary>Builds the sort key of a string (UTS #10 section 7 / S3).</summary>
    internal byte[] GetSortKey(ReadOnlySpan<char> text)
    {
        using var key = new KeyBuffer();
        BuildKey(text, key);
        return key.Span.ToArray();
    }

    /// <summary>Writes the sort key into a caller's buffer.</summary>
    /// <returns>False when the buffer is too small; <paramref name="written"/> then holds the required length.</returns>
    internal bool TryGetSortKey(ReadOnlySpan<char> text, Span<byte> destination, out int written)
    {
        using var key = new KeyBuffer();
        BuildKey(text, key);
        written = key.Count;
        if (key.Count > destination.Length)
        {
            return false;
        }

        key.Span.CopyTo(destination);
        return true;
    }

    /// <summary>Compares two strings; negative, zero or positive as <paramref name="x"/> sorts before, equal to or after <paramref name="y"/>.</summary>
    internal int Compare(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        if (x.SequenceEqual(y))
        {
            return 0;
        }

        using var a = new KeyBuffer();
        using var b = new KeyBuffer();
        BuildKey(x, a);
        BuildKey(y, b);
        int c = a.Span.SequenceCompareTo(b.Span);
        return c < 0 ? -1 : c > 0 ? 1 : 0;
    }

    // Rank of a case class: lowercase, mixed, uppercase (reversed for caseFirst "upper").
    private static int CaseOrder(Ce ce, CaseFirst caseFirst) => caseFirst == CaseFirst.Upper ? 2 - ce.CaseClass : ce.CaseClass;

    private void BuildKey(ReadOnlySpan<char> text, KeyBuffer key)
    {
        using var ces = new CeBuffer();
        Elements(text, ces);
        ReadOnlySpan<Ce> all = ces.Span;
        CollationStrength strength = _options.Strength;
        bool shifted = _options.IgnorePunctuation;
        CaseFirst caseFirst = _options.CaseFirst;
        bool splitCase = _options.CaseLevel || caseFirst != CaseFirst.Off;

        // Variable weighting (UTS #10 section 4, "shifted"): punctuation drops out of levels 1 to 3 and is
        // kept at level 4; ignorables right after it drop out entirely.
        Span<uint> quaternary = default;
        uint[]? quaternaryRented = null;
        Span<bool> dropped = default;
        bool[]? droppedRented = null;
        if (shifted)
        {
            quaternaryRented = ArrayPool<uint>.Shared.Rent(all.Length);
            droppedRented = ArrayPool<bool>.Shared.Rent(all.Length);
            quaternary = quaternaryRented.AsSpan(0, all.Length);
            dropped = droppedRented.AsSpan(0, all.Length);
            bool afterVariable = false;
            for (int i = 0; i < all.Length; i++)
            {
                Ce ce = all[i];
                dropped[i] = false;
                if (ce.IsVariable)
                {
                    dropped[i] = true;
                    quaternary[i] = ce.Primary;
                    afterVariable = true;
                }
                else if (ce.Primary == 0)
                {
                    if (afterVariable)
                    {
                        dropped[i] = true;
                        quaternary[i] = 0;
                    }
                    else
                    {
                        quaternary[i] = ce.Secondary == 0 && ce.Tertiary == 0 ? 0 : uint.MaxValue;
                    }
                }
                else
                {
                    afterVariable = false;
                    quaternary[i] = uint.MaxValue;
                }
            }
        }

        try
        {
            // Level 1
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Primary != 0 && !(shifted && dropped[i]))
                {
                    key.AddUInt32(all[i].Primary + 0x10000000u);
                }
            }

            if (strength >= CollationStrength.Secondary)
            {
                key.Add(0);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].Secondary != 0 && !(shifted && dropped[i]))
                    {
                        key.AddUInt24(all[i].Secondary + 0x800000u);
                    }
                }
            }

            if (_options.CaseLevel)
            {
                // One value per element with a primary weight; lowercase first unless caseFirst is "upper".
                key.Add(0);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].Primary != 0 && !(shifted && dropped[i]))
                    {
                        key.Add((byte)(1 + CaseOrder(all[i], caseFirst)));
                    }
                }
            }

            if (strength >= CollationStrength.Tertiary)
            {
                key.Add(0);
                for (int i = 0; i < all.Length; i++)
                {
                    Ce ce = all[i];
                    if (shifted && dropped[i])
                    {
                        continue;
                    }

                    if (ce.Tertiary == 0)
                    {
                        continue;
                    }

                    uint t = splitCase ? ce.PlainTertiary : ce.Tertiary;
                    if (caseFirst != CaseFirst.Off && !_options.CaseLevel)
                    {
                        // Case orders ahead of the rest of the tertiary weight (ECMA-402 caseFirst).
                        t |= (uint)CaseOrder(ce, caseFirst) << 13;
                    }

                    key.AddUInt16((ushort)(t + 0x8000u));
                }
            }

            if (strength >= CollationStrength.Quaternary && shifted)
            {
                key.Add(0);
                for (int i = 0; i < all.Length; i++)
                {
                    uint q = quaternary[i];
                    if (q == 0)
                    {
                        continue;
                    }

                    key.AddUInt32(q == uint.MaxValue ? uint.MaxValue : q + 0x10000000u);
                }
            }

            if (strength == CollationStrength.Identical)
            {
                // S3.10: code points of the NFD form.
                key.Add(0);
                using var cps = new CodePointBuffer(text.Length + 4);
                Normalizer.Decompose(text, false, cps);
                foreach (int cp in cps.Span)
                {
                    key.AddUInt24((uint)cp + 0x800000u);
                }
            }
        }
        finally
        {
            if (quaternaryRented is not null)
            {
                ArrayPool<uint>.Shared.Return(quaternaryRented);
            }

            if (droppedRented is not null)
            {
                ArrayPool<bool>.Shared.Return(droppedRented);
            }
        }
    }
}

/// <summary>A growable byte buffer backed by pooled arrays.</summary>
internal sealed class KeyBuffer : IDisposable
{
    private byte[] _bytes = ArrayPool<byte>.Shared.Rent(128);

    internal int Count { get; private set; }

    internal ReadOnlySpan<byte> Span => _bytes.AsSpan(0, Count);

    internal void Add(byte b)
    {
        if (Count == _bytes.Length)
        {
            Grow();
        }

        _bytes[Count++] = b;
    }

    internal void AddUInt16(ushort v)
    {
        Add((byte)(v >> 8));
        Add((byte)v);
    }

    internal void AddUInt24(uint v)
    {
        Add((byte)(v >> 16));
        Add((byte)(v >> 8));
        Add((byte)v);
    }

    internal void AddUInt32(uint v)
    {
        Add((byte)(v >> 24));
        Add((byte)(v >> 16));
        Add((byte)(v >> 8));
        Add((byte)v);
    }

    private void Grow()
    {
        byte[] bigger = ArrayPool<byte>.Shared.Rent(_bytes.Length * 2);
        Array.Copy(_bytes, bigger, Count);
        ArrayPool<byte>.Shared.Return(_bytes);
        _bytes = bigger;
    }

    public void Dispose()
    {
        byte[] b = _bytes;
        _bytes = [];
        if (b.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }
}
