namespace RecursiveAI.OneEcma.Intl;

/// <summary>
/// The digit-related options NumberFormat and PluralRules read (ECMA-402 15.1.? SetNumberFormatDigitOptions
/// input): <c>null</c> means the option was undefined. Values are already numbers; the JS layer does the
/// ToNumber and floor of GetNumberOption.
/// </summary>
internal sealed record DigitOptionInputs
{
    /// <summary><c>minimumIntegerDigits</c>, 1 to 21.</summary>
    internal int? MinimumIntegerDigits { get; init; }

    /// <summary><c>minimumFractionDigits</c>, 0 to 100.</summary>
    internal int? MinimumFractionDigits { get; init; }

    /// <summary><c>maximumFractionDigits</c>, 0 to 100.</summary>
    internal int? MaximumFractionDigits { get; init; }

    /// <summary><c>minimumSignificantDigits</c>, 1 to 21.</summary>
    internal int? MinimumSignificantDigits { get; init; }

    /// <summary><c>maximumSignificantDigits</c>, 1 to 21.</summary>
    internal int? MaximumSignificantDigits { get; init; }

    /// <summary><c>roundingIncrement</c>, one of 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000.</summary>
    internal int? RoundingIncrement { get; init; }

    /// <summary><c>roundingMode</c>, default <c>halfExpand</c>.</summary>
    internal string? RoundingMode { get; init; }

    /// <summary><c>roundingPriority</c>: <c>auto</c> (default), <c>morePrecision</c>, <c>lessPrecision</c>.</summary>
    internal string? RoundingPriority { get; init; }

    /// <summary><c>trailingZeroDisplay</c>: <c>auto</c> (default) or <c>stripIfInteger</c>.</summary>
    internal string? TrailingZeroDisplay { get; init; }
}

/// <summary>
/// The resolved digit options (ECMA-402 SetNumberFormatDigitOptions output) and the rounding they define
/// (FormatNumericToString). Shared by NumberFormat, PluralRules and, later, DurationFormat.
/// </summary>
internal sealed class DigitOptions
{
    private static readonly int[] AllowedIncrements = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000];

    internal static readonly string[] RoundingModeNames =
        ["ceil", "floor", "expand", "trunc", "halfCeil", "halfFloor", "halfExpand", "halfTrunc", "halfEven"];

    /// <summary>The resolved <c>minimumIntegerDigits</c>.</summary>
    internal int MinimumIntegerDigits { get; private set; }

    /// <summary>The resolved <c>minimumFractionDigits</c> (meaningful when <see cref="UsesFractionDigits"/>).</summary>
    internal int MinimumFractionDigits { get; private set; }

    /// <summary>The resolved <c>maximumFractionDigits</c>.</summary>
    internal int MaximumFractionDigits { get; private set; }

    /// <summary>The resolved <c>minimumSignificantDigits</c> (meaningful when <see cref="UsesSignificantDigits"/>).</summary>
    internal int MinimumSignificantDigits { get; private set; }

    /// <summary>The resolved <c>maximumSignificantDigits</c>.</summary>
    internal int MaximumSignificantDigits { get; private set; }

    /// <summary>The resolved <c>roundingIncrement</c>.</summary>
    internal int RoundingIncrement { get; private set; }

    /// <summary>The resolved <c>roundingMode</c>.</summary>
    internal RoundingMode RoundingMode { get; private set; }

    /// <summary>The resolved <c>roundingMode</c> name.</summary>
    internal string RoundingModeName => RoundingModeNames[(int)RoundingMode];

    /// <summary>The resolved <c>roundingPriority</c> (as reported by resolvedOptions).</summary>
    internal string RoundingPriority { get; private set; } = "auto";

    /// <summary>The resolved <c>trailingZeroDisplay</c>.</summary>
    internal string TrailingZeroDisplay { get; private set; } = "auto";

    /// <summary>[[RoundingType]]: <c>significantDigits</c>, <c>fractionDigits</c>, <c>morePrecision</c> or <c>lessPrecision</c>.</summary>
    internal string RoundingType { get; private set; } = "fractionDigits";

    /// <summary>Whether resolvedOptions reports fraction digit bounds.</summary>
    internal bool UsesFractionDigits => RoundingType != "significantDigits";

    /// <summary>Whether resolvedOptions reports significant digit bounds.</summary>
    internal bool UsesSignificantDigits => RoundingType != "fractionDigits";

    private static int Number(int? v, string name, int min, int max, int fallback)
    {
        if (v is null)
        {
            return fallback;
        }

        if (v < min || v > max)
        {
            throw IntlException.Range($"{name} value is out of range.");
        }

        return v.Value;
    }

    internal static RoundingMode ParseRoundingMode(string? name)
    {
        if (name is null)
        {
            return RoundingMode.HalfExpand;
        }

        int i = Array.IndexOf(RoundingModeNames, name);
        return i < 0 ? throw IntlException.Range($"Value {name} out of range for Intl.NumberFormat options property roundingMode") : (RoundingMode)i;
    }

    /// <summary>SetNumberFormatDigitOptions (ECMA-402 15.1.3).</summary>
    /// <param name="o">The digit options as given.</param>
    /// <param name="mnfdDefault">The default minimum fraction digits for the style.</param>
    /// <param name="mxfdDefault">The default maximum fraction digits for the style.</param>
    /// <param name="compactNotation">Whether the notation is <c>compact</c>.</param>
    internal static DigitOptions Resolve(DigitOptionInputs o, int mnfdDefault, int mxfdDefault, bool compactNotation)
    {
        var r = new DigitOptions();
        r.MinimumIntegerDigits = Number(o.MinimumIntegerDigits, "minimumIntegerDigits", 1, 21, 1);
        int? mnfd = o.MinimumFractionDigits, mxfd = o.MaximumFractionDigits, mnsd = o.MinimumSignificantDigits, mxsd = o.MaximumSignificantDigits;
        int increment = Number(o.RoundingIncrement, "roundingIncrement", 1, 5000, 1);
        if (Array.IndexOf(AllowedIncrements, increment) < 0)
        {
            throw IntlException.Range("roundingIncrement value is out of range.");
        }

        r.RoundingMode = ParseRoundingMode(o.RoundingMode);
        string priority = o.RoundingPriority ?? "auto";
        if (priority is not ("auto" or "morePrecision" or "lessPrecision"))
        {
            throw IntlException.Range($"Value {priority} out of range for Intl.NumberFormat options property roundingPriority");
        }

        string tzd = o.TrailingZeroDisplay ?? "auto";
        if (tzd is not ("auto" or "stripIfInteger"))
        {
            throw IntlException.Range($"Value {tzd} out of range for Intl.NumberFormat options property trailingZeroDisplay");
        }

        if (increment != 1)
        {
            mxfdDefault = mnfdDefault;
        }

        r.RoundingIncrement = increment;
        r.TrailingZeroDisplay = tzd;
        bool hasSd = mnsd is not null || mxsd is not null;
        bool hasFd = mnfd is not null || mxfd is not null;
        bool needSd = true, needFd = true;
        if (priority == "auto")
        {
            needSd = hasSd;
            if (needSd || (!hasFd && compactNotation))
            {
                needFd = false;
            }
        }

        if (needSd)
        {
            if (hasSd)
            {
                r.MinimumSignificantDigits = Number(mnsd, "minimumSignificantDigits", 1, 21, 1);
                r.MaximumSignificantDigits = Number(mxsd, "maximumSignificantDigits", r.MinimumSignificantDigits, 21, 21);
            }
            else
            {
                r.MinimumSignificantDigits = 1;
                r.MaximumSignificantDigits = 21;
            }
        }

        if (needFd)
        {
            if (hasFd)
            {
                int? a = mnfd is null ? null : Number(mnfd, "minimumFractionDigits", 0, 100, 0);
                int? b = mxfd is null ? null : Number(mxfd, "maximumFractionDigits", 0, 100, 0);
                if (a is null)
                {
                    a = Math.Min(mnfdDefault, b!.Value);
                }
                else if (b is null)
                {
                    b = Math.Max(mxfdDefault, a.Value);
                }
                else if (a > b)
                {
                    throw IntlException.Range("maximumFractionDigits value is out of range.");
                }

                r.MinimumFractionDigits = a.Value;
                r.MaximumFractionDigits = b!.Value;
            }
            else
            {
                r.MinimumFractionDigits = mnfdDefault;
                r.MaximumFractionDigits = mxfdDefault;
            }
        }

        if (!needSd && !needFd)
        {
            r.MinimumFractionDigits = 0;
            r.MaximumFractionDigits = 0;
            r.MinimumSignificantDigits = 1;
            r.MaximumSignificantDigits = 2;
            r.RoundingType = "morePrecision";
            r.RoundingPriority = "morePrecision";
        }
        else if (priority is "morePrecision" or "lessPrecision")
        {
            r.RoundingType = priority;
            r.RoundingPriority = priority;
        }
        else if (hasSd)
        {
            r.RoundingType = "significantDigits";
            r.RoundingPriority = "auto";
        }
        else
        {
            r.RoundingType = "fractionDigits";
            r.RoundingPriority = "auto";
        }

        if (increment != 1)
        {
            if (r.RoundingType != "fractionDigits")
            {
                throw IntlException.Type("roundingIncrement requires fraction digit rounding");
            }

            if (r.MaximumFractionDigits != r.MinimumFractionDigits)
            {
                throw IntlException.Range("maximumFractionDigits value is out of range.");
            }
        }

        return r;
    }

    /// <summary>
    /// FormatNumericToString (15.5.3) for a finite absolute value: rounds by the resolved type and applies
    /// <c>trailingZeroDisplay</c>. The integer part is not padded to <see cref="MinimumIntegerDigits"/> here.
    /// </summary>
    internal RoundedDigits Round(in DecimalNumber absolute, bool negative)
    {
        RoundedDigits result;
        switch (RoundingType)
        {
            case "significantDigits":
                result = DigitRounding.ToRawPrecision(absolute, MinimumSignificantDigits, MaximumSignificantDigits, RoundingMode, negative);
                break;
            case "fractionDigits":
                result = DigitRounding.ToRawFixed(absolute, MinimumFractionDigits, MaximumFractionDigits, RoundingIncrement, RoundingMode, negative);
                break;
            default:
                RoundedDigits s = DigitRounding.ToRawPrecision(absolute, MinimumSignificantDigits, MaximumSignificantDigits, RoundingMode, negative);
                RoundedDigits f = DigitRounding.ToRawFixed(absolute, MinimumFractionDigits, MaximumFractionDigits, RoundingIncrement, RoundingMode, negative);
                bool pickS = RoundingType == "morePrecision"
                    ? s.RoundingMagnitude <= f.RoundingMagnitude
                    : s.RoundingMagnitude > f.RoundingMagnitude;
                result = pickS ? s : f;
                break;
        }

        if (TrailingZeroDisplay == "stripIfInteger" && result.Fraction.AsSpan().Trim('0').Length == 0)
        {
            result = new RoundedDigits(result.Integer, "", result.RoundingMagnitude);
        }

        return result;
    }
}
