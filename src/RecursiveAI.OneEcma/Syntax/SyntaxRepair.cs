using System.Text;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

/// <summary>Limits for <see cref="SyntaxRepair.Repair(string, ParseOptions?, RepairOptions?, CancellationToken)"/>.</summary>
public sealed class RepairOptions
{
    /// <summary>The most repairs to apply to one source. Default 16. Hitting the cap leaves the source unrepaired and adds a <c>repair-limit</c> warning.</summary>
    public int MaxRepairs { get; set; } = 16;

    /// <summary>The most trial parses to spend on one source, counting every candidate fix that is tried. Default 400.</summary>
    public int MaxParses { get; set; } = 400;
}

/// <summary>
/// Opt-in, syntax-only repair of source that does not parse. Nothing here runs unless the host calls it. A fix is applied
/// only when it is the single plausible one for the first error and the result parses; fixes that could change meaning are
/// left as suggestions on the diagnostics. The rules and their guards are the repair table in <c>docs/repair-table.md</c>.
/// </summary>
public static class SyntaxRepair
{
    /// <summary>
    /// Repairs <paramref name="source"/> if it fails to parse. Source that already parses is returned untouched, with one
    /// exception: a program wrapped in markdown code fence lines parses (as template literals) but is not what the author
    /// meant, so the fence lines are removed when what remains parses.
    /// </summary>
    /// <remarks>
    /// Repairs are applied one at a time, each to the first remaining error, up to <see cref="RepairOptions.MaxRepairs"/>.
    /// Either every error is repaired and the final source parses with no errors, or nothing is changed: a partly repaired
    /// source is never returned. When nothing is changed the result carries the original diagnostics, with suggestions
    /// (<see cref="Diagnostic.Fixes"/>) for the mistakes that have a mechanical but meaning-changing fix.
    /// </remarks>
    /// <param name="source">The source text.</param>
    /// <param name="options">Parse options (goal, strictness, file name); <see langword="null"/> means a sloppy classic script.</param>
    /// <param name="repair">Limits; <see langword="null"/> means the defaults.</param>
    /// <param name="cancellationToken">Polled between trial parses; cancelling throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The repaired source, the repairs applied (in order, offsets into <paramref name="source"/>) and what is still wrong.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static RepairResult Repair(string source, ParseOptions? options = null, RepairOptions? repair = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Session(source, options ?? new ParseOptions(), repair ?? new RepairOptions(), cancellationToken).Run();
    }

    private sealed class Session
    {
        private readonly string _original;
        private readonly ParseOptions _options;
        private readonly RepairOptions _limits;
        private readonly CancellationToken _ct;
        private readonly SourceText _originalText;
        private int _parses;
        private string _text;
        private int[] _map;

        internal Session(string source, ParseOptions options, RepairOptions limits, CancellationToken ct)
        {
            _original = source;
            _options = options;
            _limits = limits;
            _ct = ct;
            _originalText = new SourceText(source, options.FileName);
            _text = source;
            _map = new int[source.Length + 1];
            for (int i = 0; i < _map.Length; i++)
            {
                _map[i] = i;
            }
        }

        private ParseResult Parse(string text)
        {
            _ct.ThrowIfCancellationRequested();
            _parses++;
            return JsParser.Parse(new SourceText(text, _options.FileName), _options, _ct);
        }

        private bool OutOfParses => _parses >= _limits.MaxParses;

        internal RepairResult Run()
        {
            ParseResult current = Parse(_original);
            var applied = new List<AppliedRepair>();

            // R003 runs before the loop because a fully fenced program parses: strip the fence when what remains parses
            // (or when the source did not parse to begin with and the rest of the rules may finish the job).
            List<TextEdit>? fence = FenceEdits(_text);
            if (fence is not null)
            {
                string stripped = ApplyEdits(_text, fence);
                ParseResult after = Parse(stripped);
                if (current.HasErrors || !after.HasErrors)
                {
                    Record(applied, new RepairCandidate("R003", "removed a markdown code fence", "removed this line", FenceSpan(_text, fence), fence));
                    _text = stripped;
                    current = after;
                }
            }

            string? stop = null;
            while (true)
            {
                if (!current.HasErrors)
                {
                    return Done(applied.Count == 0 ? _originalText : new SourceText(_text, _options.FileName), applied, current.Diagnostics);
                }

                if (applied.Count >= _limits.MaxRepairs)
                {
                    stop = "the limit of " + _limits.MaxRepairs.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " repairs was reached";
                    break;
                }

                RepairCandidate? chosen = Choose(current, out string? why);
                if (chosen is null)
                {
                    stop = why;
                    break;
                }

                Record(applied, chosen);
                _text = ApplyEdits(_text, chosen.Edits);
                current = Parse(_text);
            }

            // Nothing is returned half repaired: report the original errors, and the limit if that is what stopped the search.
            ParseResult original = JsParser.Parse(_originalText, _options, _ct);
            var diagnostics = new List<Diagnostic>(original.Diagnostics);
            if (stop is not null && stop.StartsWith("the limit", StringComparison.Ordinal))
            {
                var w = new Diagnostic("SyntaxError", "repair-limit", "Automatic repair stopped: " + stop, _originalText, new Span(0, 0), Severity.Warning);
                w.Notes.Add("the source was left unrepaired");
                diagnostics.Add(w);
            }

            return new RepairResult(_originalText, _originalText, [], diagnostics);
        }

        private RepairResult Done(SourceText repaired, List<AppliedRepair> applied, IReadOnlyList<Diagnostic> diagnostics) =>
            new(_originalText, repaired, applied, diagnostics);

        /// <summary>Tries every candidate for the first error; returns the one only if it is the single plausible fix.</summary>
        private RepairCandidate? Choose(ParseResult pr, out string? why)
        {
            var errors = pr.Diagnostics.Where(d => d.Severity == Severity.Error).OrderBy(d => d.ParseOffset).ToList();
            var ctx = new RepairContext(_text, pr.Source, errors);
            List<RepairCandidate> candidates = RepairRulebook.Generate(ctx);
            if (candidates.Count == 0)
            {
                why = "no repair rule applies to the first error";
                return null;
            }

            var plausible = new Dictionary<string, RepairCandidate>(StringComparer.Ordinal);
            foreach (RepairCandidate cand in candidates)
            {
                if (OutOfParses)
                {
                    why = "the limit of " + _limits.MaxParses.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " trial parses was reached";
                    return null;
                }

                string text = ApplyEdits(_text, cand.Edits);
                ParseResult r = Parse(text);
                if (!MadeProgress(text, r, cand.Edits))
                {
                    continue;
                }

                if (cand.Alternative)
                {
                    plausible.TryAdd(text, cand);
                }
                else
                {
                    plausible[text] = cand;
                }
            }

            RepairCandidate[] real = plausible.Values.Where(p => !p.Alternative).ToArray();
            if (plausible.Count == 1 && real.Length == 1)
            {
                why = null;
                return real[0];
            }

            why = plausible.Count == 0 ? "no candidate fix makes the source parse" : "more than one fix is plausible";
            return null;
        }

        /// <summary>
        /// The fix works when the source parses, or the first error lies beyond the first thing the fix touched and the
        /// token after it (an error right after the edit means the edit did not help).
        /// </summary>
        private static bool MadeProgress(string text, ParseResult r, List<TextEdit> edits)
        {
            int boundary = edits[0].Start + edits[0].Text.Length;
            while (boundary < text.Length && char.IsWhiteSpace(text[boundary]))
            {
                boundary++;
            }

            foreach (Diagnostic d in r.Diagnostics)
            {
                if (d.Severity == Severity.Error && d.ParseOffset <= boundary)
                {
                    return false;
                }
            }

            return true;
        }

        private void Record(List<AppliedRepair> applied, RepairCandidate c)
        {
            var edits = new List<TextEdit>(c.Edits.Count);
            foreach (TextEdit e in c.Edits)
            {
                Span s = Original(e.Start, e.Start + e.Length);
                edits.Add(new TextEdit(s.Start, s.Length, e.Text));
            }

            applied.Add(new AppliedRepair(c.Id, c.Title, c.Label, Original(c.Primary.Start, c.Primary.End), _originalText, edits));
            _map = Remap(_map, c.Edits, _text.Length);
        }

        /// <summary>The original offset of a current offset: the next character that survives from the original text.</summary>
        private int OriginalOffset(int current)
        {
            for (int i = current; i < _map.Length; i++)
            {
                if (_map[i] >= 0)
                {
                    return _map[i];
                }
            }

            return _original.Length;
        }

        private Span Original(int start, int end)
        {
            int s = OriginalOffset(start);
            int e = s;
            if (end > start)
            {
                for (int i = end - 1; i >= start; i--)
                {
                    if (_map[i] >= 0)
                    {
                        e = _map[i] + 1;
                        break;
                    }
                }
            }

            return new Span(s, Math.Max(s, e));
        }

        private static int[] Remap(int[] map, List<TextEdit> edits, int oldLength)
        {
            var next = new List<int>(map.Length);
            int pos = 0;
            foreach (TextEdit e in edits)
            {
                for (; pos < e.Start; pos++)
                {
                    next.Add(map[pos]);
                }

                pos = e.Start + e.Length;
                for (int k = 0; k < e.Text.Length; k++)
                {
                    next.Add(-1);
                }
            }

            for (; pos < oldLength; pos++)
            {
                next.Add(map[pos]);
            }

            next.Add(map[oldLength]);
            return [.. next];
        }
    }

    internal static string ApplyEdits(string text, List<TextEdit> edits)
    {
        var sb = new StringBuilder(text.Length + 16);
        int pos = 0;
        foreach (TextEdit e in edits)
        {
            sb.Append(text, pos, e.Start - pos).Append(e.Text);
            pos = e.Start + e.Length;
        }

        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    // ---- R003: markdown fences -----------------------------------------------------------------------

    private static bool IsFenceLine(string line)
    {
        ReadOnlySpan<char> t = line.AsSpan().Trim();
        if (t.Length < 3 || !t.StartsWith("```", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char ch in t[3..])
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '+' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Edits removing a fence line at the start and/or the end of the text, or <see langword="null"/> when there is none.</summary>
    private static List<TextEdit>? FenceEdits(string text)
    {
        var lines = new List<(int Start, int End, int Next)>();
        int pos = 0;
        while (pos < text.Length)
        {
            int e = pos;
            while (e < text.Length && !SourceText.IsLineTerminator(text[e]))
            {
                e++;
            }

            int next = e < text.Length ? (text[e] == '\r' && e + 1 < text.Length && text[e + 1] == '\n' ? e + 2 : e + 1) : e;
            lines.Add((pos, e, next));
            pos = next;
        }

        int firstLine = lines.FindIndex(l => text.AsSpan(l.Start, l.End - l.Start).Trim().Length > 0);
        if (firstLine < 0)
        {
            return null;
        }

        int lastLine = lines.FindLastIndex(l => text.AsSpan(l.Start, l.End - l.Start).Trim().Length > 0);
        var edits = new List<TextEdit>();
        (int fs, int fe, int fn) = lines[firstLine];
        if (IsFenceLine(text[fs..fe]))
        {
            edits.Add(new TextEdit(fs, fn - fs, ""));
        }

        if (lastLine != firstLine)
        {
            (int ls, int le, int ln) = lines[lastLine];
            if (IsFenceLine(text[ls..le]))
            {
                edits.Add(new TextEdit(ls, ln - ls, ""));
            }
        }

        return edits.Count == 0 ? null : edits;
    }

    private static Span FenceSpan(string text, List<TextEdit> fence)
    {
        TextEdit first = fence[0];
        int end = first.Start;
        while (end < text.Length && !SourceText.IsLineTerminator(text[end]))
        {
            end++;
        }

        int start = first.Start;
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        return new Span(start, end);
    }
}
