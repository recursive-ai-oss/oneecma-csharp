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

        SourceText src = d.Source;
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
        LinePosition loc = d.Position;
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

        if (d.Help.Count + d.Notes.Count > 0)
        {
            sb.Append(pad).Append(' ').Append(bar).Append('\n');
            foreach (string h in d.Help)
            {
                sb.Append(pad).Append(' ').Append(Paint(o, Blue, "=")).Append(' ').Append(Paint(o, Green, "help")).Append(": ").Append(h).Append('\n');
            }

            foreach (string n in d.Notes)
            {
                sb.Append(pad).Append(' ').Append(Paint(o, Blue, "=")).Append(' ').Append(Paint(o, Bold, "note")).Append(": ").Append(n).Append('\n');
            }
        }
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

        int view = 0;
        int viewEnd = col;
        bool cutLeft = false;
        bool cutRight = false;
        if (col > o.MaxLineWidth)
        {
            int anchor = labels is { Count: > 0 } ? map[Math.Min(labels[0].StartCol, text.Length)] : 0;
            view = Math.Max(0, anchor - (o.MaxLineWidth / 3));
            viewEnd = Math.Min(col, view + o.MaxLineWidth);
            cutLeft = view > 0;
            cutRight = viewEnd < col;
        }

        string display = shownText.ToString();
        string visible = RenderWindow(display, view, viewEnd);
        string number = line.ToString(NumberFormatInfo.InvariantInfo).PadLeft(width);
        sb.Append(Paint(o, Blue, number)).Append(' ').Append(Paint(o, Blue, "|")).Append(' ')
          .Append(cutLeft ? "…" : "").Append(visible).Append(cutRight ? "…" : "").Append('\n');

        if (labels is null)
        {
            return;
        }

        int shift = cutLeft ? 1 : 0;
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

            if (e <= s)
            {
                e = s + 1;
            }

            s = Math.Max(s, view);
            e = Math.Min(Math.Max(e, s + 1), viewEnd + 1);
            string mark = new(label.IsPrimary ? '^' : '-', Math.Max(1, e - s));
            sb.Append(new string(' ', width + 1)).Append(Paint(o, Blue, "|")).Append(' ')
              .Append(' ', (s - view) + shift)
              .Append(Paint(o, label.IsPrimary ? Red : Blue, mark));
            if (label.Message.Length > 0)
            {
                sb.Append(' ').Append(Paint(o, label.IsPrimary ? Red : Blue, label.Message));
            }

            sb.Append('\n');
        }
    }

    private static string RenderWindow(string display, int view, int viewEnd) =>
        view == 0 && viewEnd >= display.Length ? display : display.Substring(view, Math.Min(viewEnd, display.Length) - view);

    private static string Paint(RenderOptions o, string style, string text) => o.Ansi ? style + text + Reset : text;
}
