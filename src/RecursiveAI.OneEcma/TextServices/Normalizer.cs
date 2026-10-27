using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.TextServices;

/// <summary>The four normalization forms of UAX #15.</summary>
internal enum NormalizationForm
{
    /// <summary>Canonical decomposition, then canonical composition.</summary>
    Nfc,

    /// <summary>Canonical decomposition.</summary>
    Nfd,

    /// <summary>Compatibility decomposition, then canonical composition.</summary>
    Nfkc,

    /// <summary>Compatibility decomposition.</summary>
    Nfkd,
}

/// <summary>
/// Unicode normalization (UAX #15) over UTF-16 text, using the Unicode 17.0.0 data. Lone surrogates are
/// passed through unchanged, as ECMA-262 <c>String.prototype.normalize</c> treats them. Time is linear in
/// the input except that a run of more than 32 combining marks is sorted (n log n in that run).
/// Output of the span overloads never needs more than 18 times the input length for decomposing forms
/// (the longest Unicode expansion); <see cref="TryNormalize"/> reports when the destination is too small.
/// </summary>
internal static class Normalizer
{
    private static NormalizationData Data => Intl.Data.CldrData.Normalization;

    /// <summary>Normalizes a string; returns the same instance when it is already normalized.</summary>
    internal static string Normalize(string text, NormalizationForm form)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsNormalized(text, form))
        {
            return text;
        }

        return NormalizeCore(text, form);
    }

    /// <summary>Normalizes text into a new string.</summary>
    internal static string Normalize(ReadOnlySpan<char> text, NormalizationForm form) =>
        IsNormalized(text, form) ? text.ToString() : NormalizeCore(text, form);

    /// <summary>Normalizes into a caller-provided buffer.</summary>
    /// <param name="source">The text.</param>
    /// <param name="destination">Receives the result.</param>
    /// <param name="form">The target form.</param>
    /// <param name="written">Length of the result; when the method returns false it is the length required.</param>
    /// <returns>False when <paramref name="destination"/> is too short.</returns>
    internal static bool TryNormalize(ReadOnlySpan<char> source, Span<char> destination, NormalizationForm form, out int written)
    {
        string result = Normalize(source, form);
        written = result.Length;
        if (result.Length > destination.Length)
        {
            return false;
        }

        result.CopyTo(destination);
        return true;
    }

    /// <summary>
    /// Decomposes text into code points in canonical order, the form UTS #10 collation consumes.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="compatibility">True for NFKD, false for NFD.</param>
    /// <param name="output">Receives the code points; cleared first.</param>
    internal static void Decompose(ReadOnlySpan<char> text, bool compatibility, CodePointBuffer output)
    {
        NormalizationData data = Data;
        output.Clear();
        int runStart = -1;
        for (int i = 0; i < text.Length;)
        {
            int cp = Utf16.CodePointAt(text, i, out int len);
            i += len;
            AppendDecomposed(data, cp, compatibility, output);
        }

        // Canonical ordering (UAX #15 section 3.11) over each run of non-starters.
        Span<int> all = output.Span;
        for (int k = 0; k <= all.Length; k++)
        {
            bool mark = k < all.Length && data.CombiningClass(all[k]) != 0;
            if (mark)
            {
                if (runStart < 0)
                {
                    runStart = k;
                }
            }
            else if (runStart >= 0)
            {
                if (k - runStart > 1)
                {
                    SortRun(data, all[runStart..k]);
                }

                runStart = -1;
            }
        }
    }

    private static void AppendDecomposed(NormalizationData data, int cp, bool compatibility, CodePointBuffer output)
    {
        if (cp < (compatibility ? 0xA0 : 0xC0))
        {
            output.Add(cp);
            return;
        }

        int s = cp - NormalizationData.HangulSBase;
        if ((uint)s < NormalizationData.HangulSCount)
        {
            // Unicode 3.12 Hangul syllable decomposition.
            output.Add(NormalizationData.HangulLBase + (s / NormalizationData.HangulNCount));
            output.Add(NormalizationData.HangulVBase + (s % NormalizationData.HangulNCount / NormalizationData.HangulTCount));
            int t = s % NormalizationData.HangulTCount;
            if (t != 0)
            {
                output.Add(NormalizationData.HangulTBase + t);
            }

            return;
        }

        ReadOnlySpan<int> d = data.Decomposition(cp, compatibility);
        if (d.IsEmpty)
        {
            output.Add(cp);
        }
        else
        {
            output.AddRange(d);
        }
    }

    // Stable sort by combining class. Short runs use insertion sort; long ones sort (class, index) keys.
    private static void SortRun(NormalizationData data, Span<int> run)
    {
        if (run.Length <= 32)
        {
            for (int i = 1; i < run.Length; i++)
            {
                int cp = run[i];
                int cc = data.CombiningClass(cp);
                int j = i - 1;
                while (j >= 0 && data.CombiningClass(run[j]) > cc)
                {
                    run[j + 1] = run[j];
                    j--;
                }

                run[j + 1] = cp;
            }

            return;
        }

        var keys = new long[run.Length];
        var copy = run.ToArray();
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = ((long)data.CombiningClass(copy[i]) << 32) | (uint)i;
        }

        Array.Sort(keys);
        for (int i = 0; i < keys.Length; i++)
        {
            run[i] = copy[(int)(keys[i] & 0xFFFFFFFF)];
        }
    }

    private static string NormalizeCore(ReadOnlySpan<char> text, NormalizationForm form)
    {
        bool compat = form is NormalizationForm.Nfkc or NormalizationForm.Nfkd;
        bool compose = form is NormalizationForm.Nfc or NormalizationForm.Nfkc;
        using var buffer = new CodePointBuffer(text.Length + 8);
        Decompose(text, compat, buffer);
        int count = buffer.Count;
        Span<int> cps = buffer.Span;
        if (compose)
        {
            count = Compose(Data, cps);
        }

        int units = 0;
        for (int i = 0; i < count; i++)
        {
            units += Utf16.Length(cps[i]);
        }

        return string.Create(units, (buffer, count), static (dest, state) =>
        {
            Span<int> items = state.buffer.Span;
            int pos = 0;
            for (int i = 0; i < state.count; i++)
            {
                pos += Utf16.Write(dest[pos..], items[i]);
            }
        });
    }

    // UAX #15 section 3.11 canonical composition, in place; returns the new length.
    private static int Compose(NormalizationData data, Span<int> cps)
    {
        if (cps.Length == 0)
        {
            return 0;
        }

        int starter = 0;
        int lastClass = data.CombiningClass(cps[0]);
        if (lastClass != 0)
        {
            lastClass = 256; // A leading non-starter has nothing before it to combine with.
        }

        int write = 1;
        for (int read = 1; read < cps.Length; read++)
        {
            int ch = cps[read];
            int cc = data.CombiningClass(ch);

            // A character is blocked from the starter when something between them has a class of 0 or >= its own.
            if (lastClass < cc || lastClass == 0)
            {
                int composite = data.CombiningClass(cps[starter]) == 0 ? data.Compose(cps[starter], ch) : -1;
                if (composite >= 0)
                {
                    cps[starter] = composite;
                    continue;
                }
            }

            if (cc == 0)
            {
                starter = write;
            }

            lastClass = cc;
            cps[write++] = ch;
        }

        return write;
    }

    /// <summary>
    /// Whether text is already in a normalization form. Uses the quick-check properties (UAX #15 section 9)
    /// and normalizes to compare only when a character is "maybe".
    /// </summary>
    internal static bool IsNormalized(ReadOnlySpan<char> text, NormalizationForm form)
    {
        NormalizationData data = Data;
        bool compat = form is NormalizationForm.Nfkc or NormalizationForm.Nfkd;
        bool compose = form is NormalizationForm.Nfc or NormalizationForm.Nfkc;
        int lastClass = 0;
        bool maybe = false;
        for (int i = 0; i < text.Length;)
        {
            int cp = Utf16.CodePointAt(text, i, out int len);
            i += len;
            if (cp < 0xC0 && !(compat && cp >= 0xA0))
            {
                lastClass = 0;
                continue;
            }

            int cc = data.CombiningClass(cp);
            if (cc != 0 && lastClass > cc)
            {
                return false;
            }

            lastClass = cc;
            if ((uint)(cp - NormalizationData.HangulSBase) < NormalizationData.HangulSCount)
            {
                if (!compose)
                {
                    return false;
                }

                continue;
            }

            ReadOnlySpan<int> canonicalMap = data.Decomposition(cp, false);
            ReadOnlySpan<int> compatMap = compat ? data.Decomposition(cp, true) : canonicalMap;
            bool hasCanonical = !canonicalMap.IsEmpty;
            bool hasAny = !compatMap.IsEmpty;
            bool compatOnly = hasAny && !compatMap.SequenceEqual(canonicalMap);
            if (!compose)
            {
                if (hasAny)
                {
                    return false;
                }
            }
            else if ((hasCanonical && data.IsCompositionExcluded(cp)) || (compat && compatOnly))
            {
                // NFC_Quick_Check=No: composition-excluded, or (NFKC) a compatibility mapping.
                return false;
            }

            // UAX #15 section 9.2: a composite whose decomposition starts with a character that composes
            // backward (Kirat Rai, Tulu-Tigalari) can change after a preceding starter, so it is "maybe" too.
            if (compose && (data.CanComposeBackward(cp) || (hasAny && data.CanComposeBackward(compatMap[0]))))
            {
                maybe = true;
            }
        }

        return !maybe || NormalizeCore(text, form).AsSpan().SequenceEqual(text);
    }
}
