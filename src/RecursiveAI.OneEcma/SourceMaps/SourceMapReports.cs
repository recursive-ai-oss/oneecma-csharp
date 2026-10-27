using System.Globalization;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.SourceMaps;

/// <summary>
/// Moves diagnostics from generated code (a bundle, minified or transpiled output) to the source the author wrote, using
/// a decoded source map. Reports about generated code then show the original file, line, column and source line, and the generated position is
/// kept as a note and in <see cref="Diagnostic.Mapping"/>.
/// </summary>
public static class SourceMapReports
{
    /// <summary>
    /// Remaps one diagnostic. The primary span and the secondary spans are looked up with
    /// <see cref="SourceMap.GetOriginalPositions"/> at their start; the end is found from the last code unit of the span when that falls
    /// in the same original source after the start, else from the mapped name's length, else from the span's length.
    /// </summary>
    /// <param name="diagnostic">A diagnostic whose spans refer to the generated source.</param>
    /// <param name="map">The decoded source map of that generated source.</param>
    /// <param name="sourceContent">Supplies the text of an original source whose map entry has no <c>sourcesContent</c>; null means only the map's own contents are used.</param>
    /// <returns>
    /// A new diagnostic. When the primary span has no mapping, <paramref name="diagnostic"/> itself, unchanged. When it maps but
    /// the original text is unavailable, a copy that is still about the generated source with an <c>original position</c> note and
    /// <see cref="Diagnostic.Mapping"/> set. When the text is available, a diagnostic over the original source: spans mapped, secondary labels
    /// that fall in other sources turned into notes, and suggested fixes and recovery steps dropped because their offsets belong to the generated text.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static Diagnostic Remap(Diagnostic diagnostic, SourceMap map, SourceMapFetcher? sourceContent = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(map);
        return new Remapper(map, sourceContent).Remap(diagnostic);
    }

    /// <summary>Remaps every diagnostic of a list (see <see cref="Remap"/>), loading each original source once.</summary>
    /// <param name="diagnostics">Diagnostics about the generated source.</param>
    /// <param name="map">The decoded source map.</param>
    /// <param name="sourceContent">Supplies original source text missing from the map; may be null.</param>
    /// <returns>The remapped diagnostics in the same order.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<Diagnostic> RemapAll(IReadOnlyList<Diagnostic> diagnostics, SourceMap map, SourceMapFetcher? sourceContent = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(map);
        var r = new Remapper(map, sourceContent);
        var list = new List<Diagnostic>(diagnostics.Count);
        foreach (Diagnostic d in diagnostics)
        {
            list.Add(r.Remap(d));
        }

        return list;
    }

    private readonly record struct Mapped(int SourceIndex, int StartLine, int StartColumn, int EndLine, int EndColumn, string? Name);

    private sealed class Remapper(SourceMap map, SourceMapFetcher? fetch)
    {
        private readonly Dictionary<int, SourceText?> _texts = [];

        public Diagnostic Remap(Diagnostic d)
        {
            Mapped? primary = MapSpan(d.Source, d.PrimarySpan);
            if (primary is not { } p)
            {
                return d;
            }

            string genName = d.Source.Name;
            LinePosition gs = d.Source.GetPosition(d.PrimarySpan.Start);
            LinePosition ge = d.Source.GetPosition(Math.Max(d.PrimarySpan.End, d.PrimarySpan.Start));
            var generated = new MappedLocation(genName, gs.Line, gs.Column, ge.Line, ge.Column);
            string originalName = NameOf(p.SourceIndex);
            var original = new MappedLocation(originalName, p.StartLine + 1, p.StartColumn + 1, p.EndLine + 1, p.EndColumn + 1, p.Name);

            SourceText? text = TextOf(p.SourceIndex);
            if (text is null)
            {
                var copy = new Diagnostic(d.Kind, d.Code, d.Headline, d.Source, d.PrimarySpan, d.Severity) { PrimaryLabel = d.PrimaryLabel };
                copy.SecondaryLabels.AddRange(d.SecondaryLabels);
                copy.Notes.AddRange(d.Notes);
                copy.Help.AddRange(d.Help);
                copy.Fixes.AddRange(d.Fixes);
                copy.Recovery.AddRange(d.Recovery);
                copy.Notes.Add("original position: " + Format(original) + " (the original source text is not available)");
                copy.Mapping = new SourceMapping(generated, d.PrimarySpan, original, false);
                return copy;
            }

            var result = new Diagnostic(d.Kind, d.Code, d.Headline, text, ToSpan(text, p), d.Severity) { PrimaryLabel = d.PrimaryLabel };
            foreach (Label l in d.SecondaryLabels)
            {
                Mapped? m = MapSpan(d.Source, l.Span);
                if (m is { } ms && ms.SourceIndex == p.SourceIndex)
                {
                    result.SecondaryLabels.Add(new Label(ToSpan(text, ms), l.Message));
                }
                else if (m is { } other)
                {
                    result.Notes.Add(l.Message + " (" + NameOf(other.SourceIndex) + ":" + Pos(other) + ")");
                }
                else
                {
                    LinePosition lp = d.Source.GetPosition(l.Span.Start);
                    result.Notes.Add(l.Message + " (generated code at " + genName + ":" + lp.Line.ToString(NumberFormatInfo.InvariantInfo) + ":" + lp.Column.ToString(NumberFormatInfo.InvariantInfo) + ")");
                }
            }

            // Help that quotes code quotes the generated code, which is not what the author wrote.
            foreach (string h in d.Help)
            {
                result.Help.Add(h.Contains('`', StringComparison.Ordinal) ? h + " (in the generated code)" : h);
            }

            result.Notes.AddRange(d.Notes);
            result.Notes.Add("generated position: " + Format(generated));
            result.Mapping = new SourceMapping(generated, d.PrimarySpan, original, true);
            return result;
        }

        private static string Pos(Mapped m) => (m.StartLine + 1).ToString(NumberFormatInfo.InvariantInfo) + ":" + (m.StartColumn + 1).ToString(NumberFormatInfo.InvariantInfo);

        private static string Format(MappedLocation l) => l.Source + ":" + l.Line.ToString(NumberFormatInfo.InvariantInfo) + ":" + l.Column.ToString(NumberFormatInfo.InvariantInfo);

        private string NameOf(int sourceIndex) => map.Sources[sourceIndex].Url ?? "sources[" + sourceIndex.ToString(NumberFormatInfo.InvariantInfo) + "]";

        private SourceText? TextOf(int sourceIndex)
        {
            if (_texts.TryGetValue(sourceIndex, out SourceText? cached))
            {
                return cached;
            }

            SourceMapSource s = map.Sources[sourceIndex];
            string? content = s.Content ?? (s.Url is not null ? fetch?.Invoke(s.Url) : null);
            SourceText? text = content is null ? null : new SourceText(content, NameOf(sourceIndex));
            _texts[sourceIndex] = text;
            return text;
        }

        private static Span ToSpan(SourceText t, Mapped m)
        {
            int start = Offset(t, m.StartLine, m.StartColumn);
            int end = Offset(t, m.EndLine, m.EndColumn);
            return new Span(start, Math.Max(start, end));
        }

        private static int Offset(SourceText t, int line, int column)
        {
            if (line >= t.LineCount)
            {
                return t.Text.Length;
            }

            return t.GetLineStart(line + 1) + Math.Min(column, t.GetLineText(line + 1).Length);
        }

        private static bool Named(OriginalPosition p) => p.Name is { Length: > 0 };

        /// <summary>
        /// A mapping is a point, not a range, so the range is rebuilt: a mapped name gives the token's length (the generated
        /// identifier may have been renamed); otherwise code is assumed to be copied as written between two mappings, and the
        /// distance from the mapping's column carries over.
        /// </summary>
        private Mapped? MapSpan(SourceText gen, Span span)
        {
            LinePosition s = gen.GetPosition(span.Start);
            if (!map.TryGetOriginalPosition(s.Line - 1, s.Column - 1, out OriginalPosition o))
            {
                return null;
            }

            int startColumn = o.Column + (!Named(o) && o.GeneratedLine == s.Line - 1 ? (s.Column - 1) - o.GeneratedColumn : 0);
            int endLine = o.Line;
            int endColumn = startColumn;
            bool haveEnd = false;
            if (span.End > span.Start)
            {
                LinePosition last = gen.GetPosition(span.End - 1);
                if (map.TryGetOriginalPosition(last.Line - 1, last.Column - 1, out OriginalPosition oe)
                    && oe.SourceIndex == o.SourceIndex && oe.GeneratedLine == last.Line - 1)
                {
                    endLine = oe.Line;
                    endColumn = Named(oe) ? oe.Column + oe.Name!.Length : oe.Column + ((last.Column - 1) - oe.GeneratedColumn) + 1;
                    haveEnd = endLine > o.Line || (endLine == o.Line && endColumn > startColumn);
                }
            }

            if (!haveEnd)
            {
                endLine = o.Line;
                endColumn = Named(o) ? o.Column + o.Name!.Length : startColumn + Math.Max(1, span.Length);
            }

            return new Mapped(o.SourceIndex, o.Line, startColumn, endLine, endColumn, o.Name);
        }
    }
}
