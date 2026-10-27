namespace RecursiveAI.OneEcma.Intl;

/// <summary>One piece of a formatted value (ECMA-402 <c>formatToParts</c> element).</summary>
/// <param name="Type">The part type: <c>integer</c>, <c>group</c>, <c>decimal</c>, <c>fraction</c>, <c>literal</c>, <c>currency</c>, ...</param>
/// <param name="Value">The text.</param>
/// <param name="Source">For range formatting <c>startRange</c>, <c>endRange</c> or <c>shared</c>; otherwise null.</param>
internal readonly record struct NumberPart(string Type, string Value, string? Source = null);

/// <summary>Inputs for <see cref="NumberFormatService"/> (the Intl.NumberFormat constructor options). Null means undefined.</summary>
internal sealed record NumberFormatOptions
{
    /// <summary><c>localeMatcher</c>: <c>lookup</c> or <c>best fit</c>; both use the lookup matcher.</summary>
    internal string? LocaleMatcher { get; init; }

    /// <summary><c>numberingSystem</c>; only <c>latn</c> is supported, others fall back to it.</summary>
    internal string? NumberingSystem { get; init; }

    /// <summary><c>style</c>: <c>decimal</c> (default), <c>percent</c>, <c>currency</c>, <c>unit</c>.</summary>
    internal string? Style { get; init; }

    /// <summary><c>currency</c> code, required for the currency style; any case.</summary>
    internal string? Currency { get; init; }

    /// <summary><c>currencyDisplay</c>: <c>symbol</c> (default), <c>narrowSymbol</c>, <c>code</c>, <c>name</c>.</summary>
    internal string? CurrencyDisplay { get; init; }

    /// <summary><c>currencySign</c>: <c>standard</c> (default) or <c>accounting</c>.</summary>
    internal string? CurrencySign { get; init; }

    /// <summary><c>unit</c>: a sanctioned simple unit or <c>X-per-Y</c>; required for the unit style.</summary>
    internal string? Unit { get; init; }

    /// <summary><c>unitDisplay</c>: <c>short</c> (default), <c>narrow</c>, <c>long</c>.</summary>
    internal string? UnitDisplay { get; init; }

    /// <summary><c>notation</c>: <c>standard</c> (default), <c>scientific</c>, <c>engineering</c>, <c>compact</c>.</summary>
    internal string? Notation { get; init; }

    /// <summary><c>compactDisplay</c>: <c>short</c> (default) or <c>long</c>.</summary>
    internal string? CompactDisplay { get; init; }

    /// <summary>
    /// <c>useGrouping</c> as a string: <c>min2</c>, <c>auto</c>, <c>always</c>; the strings "true" and "false"
    /// select the default (ECMA-402 15.1.2). Booleans go through <see cref="UseGroupingFlag"/>.
    /// </summary>
    internal string? UseGrouping { get; init; }

    /// <summary>A boolean <c>useGrouping</c>: true means <c>always</c>, false turns grouping off. Wins over <see cref="UseGrouping"/>.</summary>
    internal bool? UseGroupingFlag { get; init; }

    /// <summary><c>signDisplay</c>: <c>auto</c> (default), <c>never</c>, <c>always</c>, <c>exceptZero</c>, <c>negative</c>.</summary>
    internal string? SignDisplay { get; init; }

    /// <summary>Digit and rounding options.</summary>
    internal DigitOptionInputs Digits { get; init; } = new();
}
