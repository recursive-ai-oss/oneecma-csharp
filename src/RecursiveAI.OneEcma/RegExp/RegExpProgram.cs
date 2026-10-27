using System.Text;

namespace RecursiveAI.OneEcma.RegExp;

/// <summary>A named capture group and the numbers of every group that carries the name (duplicates are allowed in different alternatives).</summary>
/// <param name="Name">The group name with escapes resolved.</param>
/// <param name="Groups">Capture group numbers, starting at 1, in pattern order.</param>
public sealed record RegExpGroupName(string Name, IReadOnlyList<int> Groups);

/// <summary>The result of <see cref="RegExpProgram.Compile"/>: a program, or the reasons there is none.</summary>
public sealed class RegExpCompileResult
{
    internal RegExpCompileResult(RegExpProgram? program, IReadOnlyList<RegExpDiagnostic> diagnostics)
    {
        Program = program;
        Diagnostics = diagnostics;
    }

    /// <summary>The compiled program, or <see langword="null"/> when <see cref="Diagnostics"/> is not empty.</summary>
    public RegExpProgram? Program { get; }

    /// <summary>Errors in the pattern or flags; empty on success.</summary>
    public IReadOnlyList<RegExpDiagnostic> Diagnostics { get; }
}

/// <summary>The capture ranges of one successful match, in UTF-16 code unit indices.</summary>
public sealed class RegExpMatch
{
    private readonly int[] _indices;

    internal RegExpMatch(int[] indices)
    {
        _indices = indices;
    }

    /// <summary>Index of the first code unit of the match.</summary>
    public int Start => _indices[0];

    /// <summary>Index after the last code unit of the match.</summary>
    public int End => _indices[1];

    /// <summary>Number of capture groups, not counting the whole match (group 0).</summary>
    public int GroupCount => (_indices.Length / 2) - 1;

    /// <summary>True if group <paramref name="group"/> took part in the match. Group 0 always did.</summary>
    /// <param name="group">0 for the whole match, then 1..<see cref="GroupCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="group"/> is out of range.</exception>
    public bool IsGroupMatched(int group) => _indices[2 * Check(group)] >= 0;

    /// <summary>Start index of a group, or -1 if it did not take part.</summary>
    /// <param name="group">0 for the whole match, then 1..<see cref="GroupCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="group"/> is out of range.</exception>
    public int GetGroupStart(int group) => _indices[2 * Check(group)];

    /// <summary>End index of a group, or -1 if it did not take part.</summary>
    /// <param name="group">0 for the whole match, then 1..<see cref="GroupCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="group"/> is out of range.</exception>
    public int GetGroupEnd(int group) => _indices[(2 * Check(group)) + 1];

    private int Check(int group)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(group);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(group, GroupCount);
        return group;
    }
}

/// <summary>The outcome of <see cref="RegExpProgram.Exec"/>.</summary>
public sealed class RegExpExecResult
{
    internal RegExpExecResult(RegExpOutcome outcome, RegExpLimitKind limit, RegExpMatch? match)
    {
        Outcome = outcome;
        Limit = limit;
        Match = match;
    }

    /// <summary>Whether a match was found, none exists, or a limit stopped the search.</summary>
    public RegExpOutcome Outcome { get; }

    /// <summary>The limit that stopped the search when <see cref="Outcome"/> is <see cref="RegExpOutcome.LimitExceeded"/>.</summary>
    public RegExpLimitKind Limit { get; }

    /// <summary>The match when <see cref="Outcome"/> is <see cref="RegExpOutcome.Match"/>, else <see langword="null"/>.</summary>
    public RegExpMatch? Match { get; }
}

/// <summary>
/// A compiled regular expression: ECMA-262 22.2 pattern semantics with the Annex B.1.2 legacy grammar, run by our own
/// backtracking matcher. Programs are immutable and safe to share between threads.
/// </summary>
public sealed class RegExpProgram
{
    private readonly CompiledProgram _code;
    private CompiledProgram.Scratch? _scratch;

    private RegExpProgram(string pattern, string flags, CompiledProgram code, RegExpGroupName[] names)
    {
        Pattern = pattern;
        Flags = flags;
        _code = code;
        GroupNames = names;
    }

    /// <summary>The pattern text as given.</summary>
    public string Pattern { get; }

    /// <summary>The flags as given.</summary>
    public string Flags { get; }

    /// <summary>Number of capture groups, not counting the whole match.</summary>
    public int CaptureGroupCount => _code.GroupCount;

    /// <summary>Named groups in order of first appearance.</summary>
    public IReadOnlyList<RegExpGroupName> GroupNames { get; }

    /// <summary>True if the <c>u</c> or <c>v</c> flag is present, so the matcher works on code points.</summary>
    public bool IsUnicode => _code.Unicode;

    /// <summary>
    /// Parses and compiles a pattern. Pattern and flag errors come back as diagnostics, never as exceptions.
    /// </summary>
    /// <param name="pattern">The pattern text (the body between the slashes of a literal).</param>
    /// <param name="flags">Flag letters; each of <c>dgimsuvy</c> at most once, and not both <c>u</c> and <c>v</c>.</param>
    /// <returns>The program, or the diagnostics.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static RegExpCompileResult Compile(string pattern, string flags)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(flags);
        var diagnostics = new List<RegExpDiagnostic>();
        if (!ParseFlags(pattern, flags, out ModeFlags mode, out bool unicode, out bool unicodeSets, diagnostics))
        {
            return new RegExpCompileResult(null, diagnostics);
        }

        ParseResult parsed = RegExpParser.Parse(pattern, unicode, unicodeSets);
        if (parsed.HasError)
        {
            diagnostics.Add(MakeDiagnostic(pattern, flags, parsed.ErrorStart, parsed.ErrorLength, parsed.ErrorReason));
            return new RegExpCompileResult(null, diagnostics);
        }

        CompiledProgram code = RegExpCompiler.Compile(parsed, mode, unicode, unicodeSets);
        var names = new RegExpGroupName[parsed.Names.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = new RegExpGroupName(parsed.Names[i].Name, parsed.Names[i].Groups.ToArray());
        }

        return new RegExpCompileResult(new RegExpProgram(pattern, flags, code, names), diagnostics);
    }

    /// <summary>
    /// Looks for a match at or after <paramref name="startIndex"/>, or only exactly at it when <paramref name="sticky"/>.
    /// </summary>
    /// <param name="input">The string to search.</param>
    /// <param name="startIndex">Where to start, 0 to <c>input.Length</c>. With u or v, an index inside a surrogate pair moves back to the pair's start.</param>
    /// <param name="sticky">True to try only <paramref name="startIndex"/> (the <c>y</c> flag).</param>
    /// <param name="fuel">Work budget; null means unlimited.</param>
    /// <param name="limits">Memory limits; null means <see cref="RegExpLimits.Default"/>.</param>
    /// <returns>A match, no match, or a limit result; never throws for any pattern or input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startIndex"/> is outside the string.</exception>
    public RegExpExecResult Exec(string input, int startIndex, bool sticky, FuelMeter? fuel = null, RegExpLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startIndex, input.Length);
        fuel ??= FuelMeter.Unlimited;
        limits ??= RegExpLimits.Default;
        CompiledProgram.Scratch scratch = Interlocked.Exchange(ref _scratch, null) ?? new CompiledProgram.Scratch();
        try
        {
            var m = new Matcher(_code, input, fuel, limits, scratch);
            return m.Search(startIndex, sticky);
        }
        finally
        {
            _scratch = scratch;
        }
    }

    // The messages mimic V8's: 'Invalid regular expression: /<pattern>/<flags>: <reason>'.
    // V8 prints the flags in its fixed order (dgimsuvy), whatever order they were written in.
    internal static RegExpDiagnostic MakeDiagnostic(string pattern, string flags, int start, int length, string reason)
    {
        var sb = new StringBuilder(flags.Length);
        foreach (char f in "dgimsuvy")
        {
            if (flags.Contains(f, StringComparison.Ordinal))
            {
                sb.Append(f);
            }
        }

        return new RegExpDiagnostic(start, length, reason, $"Invalid regular expression: /{pattern}/{sb}: {reason}");
    }

    internal static bool ParseFlags(string pattern, string flags, out ModeFlags mode, out bool unicode, out bool unicodeSets, List<RegExpDiagnostic> diagnostics)
    {
        mode = ModeFlags.None;
        unicode = false;
        unicodeSets = false;
        int seen = 0;
        for (int i = 0; i < flags.Length; i++)
        {
            int bit = flags[i] switch
            {
                'd' => 1,
                'g' => 2,
                'i' => 4,
                'm' => 8,
                's' => 16,
                'u' => 32,
                'v' => 64,
                'y' => 128,
                _ => 0,
            };
            if (bit == 0 || (seen & bit) != 0)
            {
                diagnostics.Add(new RegExpDiagnostic(i, 1, "Invalid flags", $"Invalid flags supplied to RegExp constructor '{flags}'", InFlags: true));
                return false;
            }

            seen |= bit;
        }

        if ((seen & 32) != 0 && (seen & 64) != 0)
        {
            diagnostics.Add(new RegExpDiagnostic(Math.Max(flags.IndexOf('v', StringComparison.Ordinal), 0), 1, "Invalid flags", $"Invalid flags supplied to RegExp constructor '{flags}'", InFlags: true));
            return false;
        }

        _ = pattern;
        unicode = (seen & 32) != 0;
        unicodeSets = (seen & 64) != 0;
        mode = ((seen & 4) != 0 ? ModeFlags.IgnoreCase : 0) | ((seen & 8) != 0 ? ModeFlags.Multiline : 0) | ((seen & 16) != 0 ? ModeFlags.DotAll : 0);
        return true;
    }
}

/// <summary>
/// Static entry point for validating a regular expression literal during parsing: pattern errors are early errors.
/// </summary>
public static class RegExpValidator
{
    /// <summary>
    /// Checks a regex literal's body and flags without compiling it.
    /// </summary>
    /// <param name="body">The text between the slashes.</param>
    /// <param name="flags">The flag letters after the closing slash.</param>
    /// <returns>Diagnostics with spans inside <paramref name="body"/> (or <paramref name="flags"/> when <see cref="RegExpDiagnostic.InFlags"/>); empty when the literal is valid. Flag errors read "Invalid regular expression flags", as for a literal.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<RegExpDiagnostic> Validate(string body, string flags)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(flags);
        var diagnostics = new List<RegExpDiagnostic>();
        if (!RegExpProgram.ParseFlags(body, flags, out _, out bool unicode, out bool unicodeSets, diagnostics))
        {
            var d = diagnostics[0];
            return [new RegExpDiagnostic(d.Start, d.Length, "Invalid regular expression flags", "Invalid regular expression flags", InFlags: true)];
        }

        ParseResult parsed = RegExpParser.Parse(body, unicode, unicodeSets);
        if (parsed.HasError)
        {
            diagnostics.Add(RegExpProgram.MakeDiagnostic(body, flags, parsed.ErrorStart, parsed.ErrorLength, parsed.ErrorReason));
        }

        return diagnostics;
    }

    /// <summary>The shape the parser front end calls for a regex literal: body, flags, and the literal's span.</summary>
    /// <param name="body">The text between the slashes.</param>
    /// <param name="flags">The flag letters.</param>
    /// <param name="literalStart">Source offset of the first character of <paramref name="body"/>.</param>
    /// <returns>Diagnostics whose spans are already shifted to source offsets (flag spans start after the body and its closing slash).</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<RegExpDiagnostic> ValidateLiteral(string body, string flags, int literalStart)
    {
        var list = Validate(body, flags);
        if (list.Count == 0)
        {
            return list;
        }

        var shifted = new RegExpDiagnostic[list.Count];
        for (int i = 0; i < shifted.Length; i++)
        {
            var d = list[i];
            int baseOffset = d.InFlags ? literalStart + body.Length + 1 : literalStart;
            shifted[i] = d with { Start = baseOffset + d.Start };
        }

        return shifted;
    }
}
