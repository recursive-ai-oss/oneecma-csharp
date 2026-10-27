using System.Globalization;
using System.Text;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Diagnostics;

/// <summary>Options for <see cref="DiagnosticRenderer"/>.</summary>
public sealed class RenderOptions
{
    /// <summary>Colour the output with ANSI escape sequences. Off by default; hosts turn it on only for a terminal and never when <c>NO_COLOR</c> is set.</summary>
    public bool Ansi { get; set; }

    /// <summary>Source lines longer than this many display columns are cut to a window around the first label, so a minified file does not flood a report. Default 140.</summary>
    public int MaxLineWidth { get; set; } = 140;

    /// <summary>Add one <c>= locals:</c> line per frame that has them (only reports explained by a traced replay carry locals). Default false.</summary>
    public bool ShowLocals { get; set; }

    /// <summary>
    /// The note under every repair block. The default says the repaired source parses; a host that goes on to run the
    /// repaired source and has seen it finish may say so here.
    /// </summary>
    public string RepairOutcome { get; set; } = "the repaired source parses and is returned with this report";
}

/// <summary>
/// Renders diagnostics as text in the layout of the reference report: <c>error[Kind]: headline</c>,
/// a <c>--&gt;</c> location line, source lines with <c>^^^</c> under the primary span and <c>---</c> under
/// secondary spans, then <c>= help:</c> and <c>= note:</c> lines.
/// </summary>
public static class DiagnosticRenderer
{
    private const int TabWidth = 4;
    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string Red = "\u001b[1;31m";
    private const string Blue = "\u001b[1;34m";
    private const string Green = "\u001b[1;32m";

    /// <summary>Renders one diagnostic.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    /// <param name="options">Rendering options; <see langword="null"/> means plain text with defaults.</param>
    /// <returns>The report, ending with a newline.</returns>
    public static string Render(Diagnostic diagnostic, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var sb = new StringBuilder();
        Append(sb, diagnostic, options ?? new RenderOptions());
        return sb.ToString();
    }

    /// <summary>
    /// Renders diagnostics one after another separated by blank lines. With more than one error a
    /// closing <c>error: aborting due to N errors</c> line is added.
    /// </summary>
    public static string RenderAll(IReadOnlyList<Diagnostic> diagnostics, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        options ??= new RenderOptions();
        var sb = new StringBuilder();
        int errors = 0;
        bool allSyntax = true;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            Append(sb, diagnostics[i], options);
            if (diagnostics[i].Severity == Severity.Error)
            {
                errors++;
                allSyntax &= diagnostics[i].Kind == "SyntaxError";
            }
        }

        if (errors > 1)
        {
            sb.Append('\n');
            string text = "aborting due to " + errors.ToString(NumberFormatInfo.InvariantInfo) + (allSyntax ? " syntax errors" : " errors");
            sb.Append(Paint(options, Red, "error")).Append(Paint(options, Bold, ": " + text)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Renders one applied repair: <c>repair[Rxxx]: title</c>, the first edited place in the original source, and a note.</summary>
    /// <param name="repair">The repair.</param>
    /// <param name="options">Rendering options; <see langword="null"/> means plain text with defaults.</param>
    /// <returns>The block, ending with a newline.</returns>
    public static string RenderRepair(AppliedRepair repair, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(repair);
        var sb = new StringBuilder();
        AppendRepair(sb, repair, options ?? new RenderOptions());
        return sb.ToString();
    }

    /// <summary>
    /// Renders a repair result as the repairs applied first, then the diagnostics that remain (for a result that was
    /// not repaired these are the original errors with their suggested fixes).
    /// </summary>
    /// <param name="result">The result of <see cref="Syntax.SyntaxRepair.Repair(string, Syntax.ParseOptions?, Syntax.RepairOptions?, CancellationToken)"/>.</param>
    /// <param name="options">Rendering options; <see langword="null"/> means plain text with defaults.</param>
    /// <returns>The report, or an empty string when the source was fine and nothing was reported.</returns>
    public static string RenderRepairResult(RepairResult result, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return RenderRepairs(result.Repairs, result.Diagnostics, options);
    }

    /// <summary>Renders applied repairs first, then diagnostics; the parts of <see cref="RenderRepairResult"/> for hosts that changed the diagnostics (for example by remapping them through a source map).</summary>
    /// <param name="repairs">Applied repairs, first to last; may be empty.</param>
    /// <param name="diagnostics">The diagnostics that remain; may be empty.</param>
    /// <param name="options">Rendering options; <see langword="null"/> means plain text with defaults.</param>
    /// <returns>The report, or an empty string when there is nothing to report.</returns>
    public static string RenderRepairs(IReadOnlyList<AppliedRepair> repairs, IReadOnlyList<Diagnostic> diagnostics, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(repairs);
        ArgumentNullException.ThrowIfNull(diagnostics);
        options ??= new RenderOptions();
        var sb = new StringBuilder();
        foreach (AppliedRepair r in repairs)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            AppendRepair(sb, r, options);
        }

        if (diagnostics.Count > 0)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(RenderAll(diagnostics, options));
        }

        return sb.ToString();
    }

    private static void AppendRepair(StringBuilder sb, AppliedRepair r, RenderOptions o)
    {
        sb.Append(Paint(o, Green, "repair[" + r.Id + "]")).Append(Paint(o, Bold, ": " + r.Title)).Append('\n');
        var labels = new List<Label> { new Label(r.Span, r.Label, true) };
        AppendBody(sb, o, r.Source, labels, [], [o.RepairOutcome]);
    }

    private static void Append(StringBuilder sb, Diagnostic d, RenderOptions o)
    {
        string level = d.Severity == Severity.Error ? "error" : "warning";
        sb.Append(Paint(o, d.Severity == Severity.Error ? Red : "\u001b[1;33m", level + "[" + d.Kind + "]"))
          .Append(Paint(o, Bold, ": " + d.Headline)).Append('\n');

        var labels = new List<Label>(d.SecondaryLabels.Count + 1)
        {
            new Label(d.PrimarySpan, d.PrimaryLabel, true),
        };
        labels.AddRange(d.SecondaryLabels);
        int gutter = AppendBody(sb, o, d.Source, labels, d.Help, d.Notes, d.Stack.Count > 0);
        if (d.Stack.Count > 0)
        {
            sb.Append(new string(' ', gutter)).Append(" = stack: ");
            for (int i = 0; i < d.Stack.Count; i++)
            {
                StackFrameInfo fr = d.Stack[i];
                if (i > 0)
                {
                    sb.Append(" ← ");
                }

                // Identical frames in a row (recursion) collapse to one with a count.
                int run = 1;
                while (i + run < d.Stack.Count && SameFrame(fr, d.Stack[i + run]))
                {
                    run++;
                }

                if (fr.IsHost)
                {
                    sb.Append(fr.Function).Append(fr.HostLine > 0 ? " (" + fr.Source.Name + ":" + fr.HostLine.ToString(NumberFormatInfo.InvariantInfo) + ")" : " [.NET]");
                    continue;
                }

                sb.Append(fr.IsAsync ? "async " : "").Append(fr.IsTopLevel ? "<top-level>" : fr.Function.Length == 0 ? "<anonymous>" : fr.Function).Append(" (").Append(fr.Source.Name).Append(':').Append(fr.Position.Line).Append(')');
                if (run > 1)
                {
                    sb.Append(" ×").Append(run.ToString(NumberFormatInfo.InvariantInfo));
                    i += run - 1;
                }
            }

            sb.Append('\n');
            if (o.ShowLocals)
            {
                foreach (StackFrameInfo fr in d.Stack)
                {
                    if (fr.Locals is { Count: > 0 } locals)
                    {
                        sb.Append(new string(' ', gutter)).Append(" = locals in ").Append(fr.IsTopLevel ? "<top-level>" : fr.Function.Length == 0 ? "<anonymous>" : fr.Function).Append(": ")
                          .AppendJoin(", ", locals.Select(l => l.Name + " = " + l.Value)).Append('\n');
                    }
                }
            }
        }
    }

    private static bool SameFrame(StackFrameInfo a, StackFrameInfo b) =>
        !a.IsHost && !b.IsHost && a.Function == b.Function && a.IsAsync == b.IsAsync && a.IsTopLevel == b.IsTopLevel
        && ReferenceEquals(a.Source, b.Source) && a.Position.Line == b.Position.Line;

    /// <summary>Everything under the headline: the location, the labelled source lines, then help and notes. The first label is the primary one.</summary>
    private static int AppendBody(StringBuilder sb, RenderOptions o, SourceText src, List<Label> labels, List<string> help, List<string> notes, bool stackFollows = false)
    {
        var lineLabels = new SortedDictionary<int, List<(Label Lbl, int StartCol, int EndCol)>>();
        foreach (Label l in labels)
        {
            LinePosition p = src.GetPosition(l.Span.Start);
            int lineLen = src.GetLineText(p.Line).Length;
            int startCol = p.Column - 1;
            int endCol;
            LinePosition e = src.GetPosition(Math.Max(l.Span.End, l.Span.Start));
            endCol = e.Line == p.Line ? e.Column - 1 : lineLen;
            if (endCol <= startCol)
            {
                endCol = startCol + 1;
            }

            if (!lineLabels.TryGetValue(p.Line, out var list))
            {
                list = [];
                lineLabels[p.Line] = list;
            }

            list.Add((l, startCol, endCol));
        }

        var shown = new List<int>(lineLabels.Keys);
        int width = shown[^1].ToString(NumberFormatInfo.InvariantInfo).Length;
        string pad = new(' ', width);
        LinePosition loc = src.GetPosition(labels[0].Span.Start);
        sb.Append(pad).Append(Paint(o, Blue, "-->")).Append(' ').Append(src.Name).Append(':')
          .Append(loc.Line.ToString(NumberFormatInfo.InvariantInfo)).Append(':')
          .Append(loc.Column.ToString(NumberFormatInfo.InvariantInfo)).Append('\n');
        string bar = Paint(o, Blue, "|");
        sb.Append(pad).Append(' ').Append(bar).Append('\n');

        int prev = -1;
        foreach (int line in shown)
        {
            if (prev >= 0 && line - prev >= 2)
            {
                if (line - prev == 2)
                {
                    AppendSourceLine(sb, o, src, prev + 1, width, null);
                }
                else
                {
                    sb.Append(Paint(o, Blue, "...")).Append('\n');
                }
            }

            AppendSourceLine(sb, o, src, line, width, lineLabels[line]);
            prev = line;
        }

        if (help.Count + notes.Count > 0 || stackFollows)
        {
            sb.Append(pad).Append(' ').Append(bar).Append('\n');
            foreach (string h in help)
            {
                sb.Append(pad).Append(' ').Append(Paint(o, Blue, "=")).Append(' ').Append(Paint(o, Green, "help")).Append(": ").Append(h).Append('\n');
            }

            foreach (string n in notes)
            {
                sb.Append(pad).Append(' ').Append(Paint(o, Blue, "=")).Append(' ').Append(Paint(o, Bold, "note")).Append(": ").Append(n).Append('\n');
            }
        }

        return width;
    }

    private static void AppendSourceLine(
        StringBuilder sb, RenderOptions o, SourceText src, int line, int width,
        List<(Label Lbl, int StartCol, int EndCol)>? labels)
    {
        ReadOnlySpan<char> text = src.GetLineText(line);

        // Expand tabs so the underline columns line up, remembering where each code unit lands.
        var map = new int[text.Length + 1];
        var shownText = new StringBuilder(text.Length);
        int col = 0;
        for (int i = 0; i < text.Length; i++)
        {
            map[i] = col;
            if (text[i] == '\t')
            {
                int n = TabWidth - (col % TabWidth);
                shownText.Append(' ', n);
                col += n;
            }
            else if (char.IsLowSurrogate(text[i]) && i > 0 && char.IsHighSurrogate(text[i - 1]))
            {
                map[i] = col - 1;
                shownText.Append(text[i]);
            }
            else
            {
                shownText.Append(text[i]);
                col++;
            }
        }

        map[text.Length] = col;

        string display = shownText.ToString();
        string number = line.ToString(NumberFormatInfo.InvariantInfo).PadLeft(width);

        // The display columns each label covers on this line.
        var spans = new List<(Label Lbl, int S, int E)>();
        if (labels is not null)
        {
            foreach (var (label, startCol, endCol) in labels.OrderBy(l => l.StartCol).ThenBy(l => l.Lbl.IsPrimary ? 1 : 0))
            {
                int s = map[Math.Min(startCol, text.Length)];
                int e = map[Math.Min(Math.Max(endCol, startCol + 1), text.Length)];
                if (endCol > text.Length)
                {
                    e = map[text.Length] + (endCol - text.Length);
                }

                if (startCol >= text.Length)
                {
                    s = map[text.Length] + (startCol - text.Length);
                    e = s + 1;
                }

                spans.Add((label, s, Math.Max(e, s + 1)));
            }
        }

        // A line that fits is shown whole. A longer one is shown through windows: labels that fit together in one window
        // share it, and labels too far apart get a window each (the line is printed again under the same number), so every
        // underline sits under the text it labels. A single span wider than a window is cut at the window edge and says so.
        int limit = Math.Max(o.MaxLineWidth, 20);
        var groups = new List<List<(Label Lbl, int S, int E)>>();
        if (col <= limit || spans.Count == 0)
        {
            groups.Add(spans);
        }
        else
        {
            int lo = 0;
            int hi = 0;
            foreach (var sp in spans)
            {
                if (groups.Count > 0 && Math.Max(hi, sp.E) - lo <= limit - 8)
                {
                    groups[^1].Add(sp);
                    hi = Math.Max(hi, sp.E);
                }
                else
                {
                    groups.Add([sp]);
                    lo = sp.S;
                    hi = sp.E;
                }
            }
        }

        foreach (var group in groups)
        {
            int view = 0;
            int viewEnd = col;
            if (col > limit)
            {
                int gLo = group.Min(g => g.S);
                int gHi = group.Max(g => g.E);
                int pad = Math.Min(gLo, Math.Min(24, Math.Max(8, (limit - (gHi - gLo)) / 2)));
                view = Math.Max(0, gLo - pad);
                viewEnd = Math.Min(col, view + limit);
                view = Math.Max(0, viewEnd - limit);
            }

            bool cutLeft = view > 0;
            bool cutRight = viewEnd < col;
            string visible = RenderWindow(display, view, viewEnd);
            sb.Append(Paint(o, Blue, number)).Append(' ').Append(Paint(o, Blue, "|")).Append(' ')
              .Append(cutLeft ? "…" : "").Append(visible).Append(cutRight ? "…" : "").Append('\n');

            int shift = cutLeft ? 1 : 0;
            foreach (var (label, s0, e0) in group)
            {
                int s = Math.Max(s0, view);
                int e = cutRight ? Math.Min(e0, viewEnd) : e0;
                bool cut = cutRight && e0 > viewEnd;
                e = Math.Max(e, s + 1);
                char ch = label.IsPrimary ? '^' : '-';
                string mark = new(ch, Math.Max(1, e - s));
                if (cut)
                {
                    mark = mark[..^1] + "…";
                }

                sb.Append(new string(' ', width + 1)).Append(Paint(o, Blue, "|")).Append(' ')
                  .Append(' ', (s - view) + shift)
                  .Append(Paint(o, label.IsPrimary ? Red : Blue, mark));
                string message = label.Message;
                if (cut)
                {
                    message = message.Length > 0 ? message + " (underline cut at the window edge)" : "(underline cut at the window edge)";
                }

                if (message.Length > 0)
                {
                    sb.Append(' ').Append(Paint(o, label.IsPrimary ? Red : Blue, message));
                }

                sb.Append('\n');
            }
        }
    }

    private static string RenderWindow(string display, int view, int viewEnd) =>
        view == 0 && viewEnd >= display.Length ? display : display.Substring(view, Math.Min(viewEnd, display.Length) - view);

    private static string Paint(RenderOptions o, string style, string text) => o.Ansi ? style + text + Reset : text;
}
