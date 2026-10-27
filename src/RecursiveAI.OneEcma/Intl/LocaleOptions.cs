namespace RecursiveAI.OneEcma.Intl;

/// <summary>Inputs for <see cref="LocaleOptions.Apply"/> (the Intl.Locale constructor options). Null means undefined.</summary>
internal sealed record LocaleOptionsInput
{
    internal string? Language { get; init; }
    internal string? Script { get; init; }
    internal string? Region { get; init; }
    internal string? Calendar { get; init; }
    internal string? Collation { get; init; }
    internal string? HourCycle { get; init; }
    internal string? CaseFirst { get; init; }

    /// <summary><c>numeric</c> after ToBoolean.</summary>
    internal bool? Numeric { get; init; }
    internal string? NumberingSystem { get; init; }
}

/// <summary>The Intl.Locale constructor's option handling (ECMA-402 section 14.1.1).</summary>
internal static class LocaleOptions
{
    /// <summary>
    /// ApplyOptionsToTag and the keyword options: returns the canonical tag of <paramref name="tag"/> with the
    /// language, script, region and Unicode extension keywords of <paramref name="o"/> applied.
    /// </summary>
    /// <exception cref="IntlException">The tag or an option value is not well formed (RangeError).</exception>
    internal static string Apply(string tag, LocaleOptionsInput o)
    {
        LocaleId id = LocaleId.Parse(tag) ?? throw IntlException.Range($"Incorrect locale information provided");
        id.Canonicalize();
        if (o.Language is not null)
        {
            if (!LocaleId.IsLanguageSubtag(o.Language) || o.Language.Length == 4)
            {
                throw IntlException.Range("Incorrect locale information provided");
            }

            id.Language = o.Language.ToLowerInvariantAscii();
        }

        if (o.Script is not null)
        {
            if (!LocaleId.IsScriptSubtag(o.Script))
            {
                throw IntlException.Range("Incorrect locale information provided");
            }

            id.Script = char.ToUpperInvariant(o.Script[0]) + o.Script[1..].ToLowerInvariantAscii();
        }

        if (o.Region is not null)
        {
            if (!LocaleId.IsRegionSubtag(o.Region))
            {
                throw IntlException.Range("Incorrect locale information provided");
            }

            id.Region = o.Region.ToUpperInvariantAscii();
        }

        id.Canonicalize();
        SetType(id, "ca", o.Calendar);
        SetType(id, "co", o.Collation);
        if (o.HourCycle is not null)
        {
            if (o.HourCycle is not ("h11" or "h12" or "h23" or "h24"))
            {
                throw IntlException.Range($"Value {o.HourCycle} out of range for Intl.Locale options property hourCycle");
            }

            id.SetKeyword("hc", o.HourCycle);
        }

        if (o.CaseFirst is not null)
        {
            if (o.CaseFirst is not ("upper" or "lower" or "false"))
            {
                throw IntlException.Range($"Value {o.CaseFirst} out of range for Intl.Locale options property caseFirst");
            }

            id.SetKeyword("kf", o.CaseFirst);
        }

        if (o.Numeric is { } numeric)
        {
            id.SetKeyword("kn", numeric ? "" : "false");
        }

        SetType(id, "nu", o.NumberingSystem);
        return id.Canonicalize().ToString();
    }

    private static void SetType(LocaleId id, string key, string? value)
    {
        if (value is null)
        {
            return;
        }

        LocaleResolver.ValidateNumberingSystem(value, "Locale"); // the same `type` production applies
        id.SetKeyword(key, value.ToLowerInvariantAscii());
    }
}
