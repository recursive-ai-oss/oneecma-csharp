using System.Numerics;

namespace RecursiveAI.OneEcma.Intl.Data;

/// <summary>CLDR plural categories (UTS #35 Part 3), in the order the rules are stored.</summary>
internal enum PluralCategory
{
    /// <summary>The "zero" category.</summary>
    Zero,

    /// <summary>The "one" category.</summary>
    One,

    /// <summary>The "two" category.</summary>
    Two,

    /// <summary>The "few" category.</summary>
    Few,

    /// <summary>The "many" category.</summary>
    Many,

    /// <summary>The "other" category, the fallback every language has.</summary>
    Other,
}

/// <summary>Whether a rule set selects cardinal ("1 day") or ordinal ("1st day") forms.</summary>
internal enum PluralType
{
    /// <summary>Counting: "1 item", "2 items".</summary>
    Cardinal,

    /// <summary>Ranking: "1st", "2nd".</summary>
    Ordinal,
}

/// <summary>
/// The operands of a plural rule (UTS #35 Part 3, "Plural Operand Meanings"), computed from the decimal
/// text of a number exactly as it is displayed, so <c>1.0</c> and <c>1</c> differ in <c>V</c>.
/// </summary>
internal readonly struct PluralOperands
{
    private PluralOperands(BigInteger integer, bool hasFraction, int v, int w, long f, long t, int exponent)
    {
        IntegerPart = integer;
        HasNonZeroFraction = hasFraction;
        V = v;
        W = w;
        F = f;
        T = t;
        Exponent = exponent;
    }

    /// <summary>Operand <c>i</c>: the integer digits of the absolute value (after the exponent shift).</summary>
    internal BigInteger IntegerPart { get; }

    /// <summary>False when <c>n</c> is a whole number (so <c>n = 1</c> matches 1.0 and 1.00).</summary>
    internal bool HasNonZeroFraction { get; }

    /// <summary>Operand <c>v</c>: visible fraction digits, with trailing zeros.</summary>
    internal int V { get; }

    /// <summary>Operand <c>w</c>: visible fraction digits, without trailing zeros.</summary>
    internal int W { get; }

    /// <summary>Operand <c>f</c>: the visible fraction digits as an integer, with trailing zeros.</summary>
    internal long F { get; }

    /// <summary>Operand <c>t</c>: the visible fraction digits as an integer, without trailing zeros.</summary>
    internal long T { get; }

    /// <summary>Operands <c>c</c> and <c>e</c>: the compact decimal exponent.</summary>
    internal int Exponent { get; }

    /// <summary>
    /// Reads a decimal such as <c>-1.50</c> or <c>1.2c6</c> (<c>c</c> or <c>e</c> introduces the compact
    /// decimal exponent, which shifts the decimal point as UTS #35 specifies).
    /// </summary>
    /// <exception cref="FormatException">The text is not a decimal number.</exception>
    internal static PluralOperands Parse(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        if (text.Length > 0 && (text[0] == '-' || text[0] == '+'))
        {
            text = text[1..];
        }

        int exponent = 0;
        int expAt = text.IndexOfAny('c', 'e');
        if (expAt >= 0)
        {
            ReadOnlySpan<char> expText = text[(expAt + 1)..];
            bool expNegative = expText.Length > 0 && expText[0] == '-';
            if (expText.Length > 0 && (expText[0] == '-' || expText[0] == '+'))
            {
                expText = expText[1..];
            }

            exponent = (int)DigitsToNumber(expText.ToString(), 9);
            exponent = expNegative ? -exponent : exponent;
            text = text[..expAt];
        }

        int dot = text.IndexOf('.');
        string intDigits = (dot < 0 ? text : text[..dot]).ToString();
        string fracDigits = dot < 0 ? "" : text[(dot + 1)..].ToString();
        if (intDigits.Length == 0 && fracDigits.Length == 0)
        {
            throw new FormatException("Not a decimal number.");
        }

        foreach (char ch in intDigits.Concat(fracDigits))
        {
            if (ch is < '0' or > '9')
            {
                throw new FormatException("Not a decimal number.");
            }
        }

        return FromDigits(intDigits, fracDigits, exponent);
    }

    /// <summary>
    /// Operands of the decimal <c>intDigits.fracDigits</c> with compact exponent <paramref name="exponent"/>.
    /// Fraction digits past the 18th do not enter <c>f</c> and <c>t</c> (no CLDR rule compares them).
    /// </summary>
    internal static PluralOperands FromDigits(string intDigits, string fracDigits, int exponent, bool shift = true)
    {
        // Shift the decimal point right by the exponent (UTS #35: operands are computed after the shift).
        if (shift && exponent > 0)
        {
            int take = Math.Min(exponent, fracDigits.Length);
            intDigits += fracDigits[..take] + new string('0', exponent - take);
            fracDigits = fracDigits[take..];
        }

        BigInteger integer = BigInteger.Zero;
        foreach (char ch in intDigits)
        {
            integer = (integer * 10) + (ch - '0');
        }

        string trimmed = fracDigits.TrimEnd('0');
        long f = DigitsToNumber(fracDigits.Length > 18 ? fracDigits[..18] : fracDigits, 18);
        long t = DigitsToNumber(trimmed.Length > 18 ? trimmed[..18] : trimmed, 18);
        return new PluralOperands(integer, trimmed.Length > 0, fracDigits.Length, trimmed.Length, f, t, exponent);
    }

    // Digits to a number; operands beyond maxDigits digits are outside what any CLDR rule inspects, so they throw.
    private static long DigitsToNumber(string digits, int maxDigits)
    {
        if (digits.Length > maxDigits)
        {
            throw new FormatException("Too many digits for a plural operand.");
        }

        long value = 0;
        foreach (char ch in digits)
        {
            value = (value * 10) + (ch - '0');
        }

        return value;
    }

    /// <summary>Operands of a non-negative integer.</summary>
    internal static PluralOperands FromInteger(BigInteger value) =>
        new(BigInteger.Abs(value), false, 0, 0, 0, 0, 0);

    /// <summary>The integer value of operand <paramref name="operand"/> (0..7 = n i v w f t c e) when it has one.</summary>
    internal bool TryGetValue(int operand, out BigInteger value)
    {
        value = operand switch
        {
            0 or 1 => IntegerPart,
            2 => V,
            3 => W,
            4 => F,
            5 => T,
            _ => Exponent,
        };
        return operand != 0 || !HasNonZeroFraction;
    }
}

/// <summary>
/// Plural rules parsed from the embedded AST. Rules are stored per language (and a few language-region
/// pairs such as <c>pt-PT</c>); a category without a rule is "other".
/// </summary>
internal sealed class PluralData
{
    private readonly Dictionary<string, Rule[]>[] _byType;

    private PluralData(Dictionary<string, Rule[]>[] byType)
    {
        _byType = byType;
    }

    private sealed class Rule(PluralCategory category, Relation[][] anyOfAllOf)
    {
        public PluralCategory Category { get; } = category;

        public bool Matches(in PluralOperands ops)
        {
            foreach (Relation[] all in anyOfAllOf)
            {
                bool ok = true;
                foreach (Relation rel in all)
                {
                    if (!rel.Matches(in ops))
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class Relation(int operand, int modulus, bool negated, (uint Lo, uint Hi)[] ranges)
    {
        public bool Matches(in PluralOperands ops)
        {
            bool hit = false;
            if (ops.TryGetValue(operand, out BigInteger value))
            {
                if (modulus != 0)
                {
                    value = BigInteger.Remainder(value, modulus);
                }

                foreach (var (lo, hi) in ranges)
                {
                    if (value >= lo && value <= hi)
                    {
                        hit = true;
                        break;
                    }
                }
            }

            // A non-integral n never equals an integer range, so its negation (!=) holds (UTS #35 "Relations").
            return hit != negated;
        }
    }

    /// <summary>Parses a plurals blob.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a plurals blob.</exception>
    internal static PluralData Open(byte[] data)
    {
        BlobReader r = BlobReader.Open(data, BlobReader.KindPlurals);
        var byType = new Dictionary<string, Rule[]>[2];
        for (int type = 0; type < 2; type++)
        {
            int langs = r.Varint();
            var map = new Dictionary<string, Rule[]>(langs, StringComparer.Ordinal);
            for (int l = 0; l < langs; l++)
            {
                string lang = r.String();
                var rules = new Rule[r.Byte()];
                for (int i = 0; i < rules.Length; i++)
                {
                    var cat = (PluralCategory)r.Byte();
                    r.Varint(); // AST byte length, for skipping without parsing
                    int ors = r.Varint();
                    var anyOf = new Relation[ors][];
                    for (int o = 0; o < ors; o++)
                    {
                        var all = new Relation[r.Varint()];
                        for (int a = 0; a < all.Length; a++)
                        {
                            int operand = r.Byte();
                            int modulus = r.Varint();
                            bool negated = r.Byte() != 0;
                            var ranges = new (uint, uint)[r.Varint()];
                            for (int k = 0; k < ranges.Length; k++)
                            {
                                ranges[k] = ((uint)r.Varint(), (uint)r.Varint());
                            }

                            all[a] = new Relation(operand, modulus, negated, ranges);
                        }

                        anyOf[o] = all;
                    }

                    rules[i] = new Rule(cat, anyOf);
                }

                map[lang] = rules;
            }

            byType[type] = map;
        }

        return new PluralData(byType);
    }

    /// <summary>Whether CLDR has plural rules for the language of <paramref name="locale"/> (after truncation).</summary>
    internal bool HasRules(string locale, PluralType type) => Find(locale, type) is not null;

    /// <summary>
    /// The category the first matching rule gives for <paramref name="operands"/>, or Other. The locale is
    /// looked up as given, then with trailing subtags dropped; unknown languages select Other.
    /// </summary>
    internal PluralCategory Select(string locale, PluralType type, in PluralOperands operands)
    {
        Rule[]? rules = Find(locale, type);
        if (rules is not null)
        {
            foreach (Rule rule in rules)
            {
                if (rule.Matches(in operands))
                {
                    return rule.Category;
                }
            }
        }

        return PluralCategory.Other;
    }

    /// <summary>The categories the language distinguishes, in canonical order, always ending with Other.</summary>
    internal PluralCategory[] Categories(string locale, PluralType type)
    {
        var result = new List<PluralCategory>();
        Rule[]? rules = Find(locale, type);
        if (rules is not null)
        {
            result.AddRange(rules.Select(r => r.Category));
        }

        result.Add(PluralCategory.Other);
        return [.. result];
    }

    private Rule[]? Find(string locale, PluralType type)
    {
        Dictionary<string, Rule[]> map = _byType[(int)type];
        string candidate = locale.Replace('_', '-');
        while (candidate.Length > 0)
        {
            if (map.TryGetValue(candidate, out Rule[]? rules))
            {
                return rules;
            }

            int dash = candidate.LastIndexOf('-');
            if (dash < 0)
            {
                break;
            }

            candidate = candidate[..dash];
        }

        return null;
    }
}
