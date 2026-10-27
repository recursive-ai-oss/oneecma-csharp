namespace RecursiveAI.OneEcma.Text;

/// <summary>
/// Source text with a name and a line map. Line terminators are the ECMAScript ones
/// (LF, CR, CR LF, U+2028, U+2029; ECMA-262 12.3), so lines and columns agree with what a
/// script's <c>Error.stack</c> reports. The line map is built on first use.
/// </summary>
public sealed class SourceText
{
    private int[]? _lineStarts;

    /// <summary>Creates a source text.</summary>
    /// <param name="text">The full text.</param>
    /// <param name="name">The name shown in reports, typically a file name.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SourceText(string text, string name = "<anonymous>")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);
        Text = text;
        Name = name;
    }

    /// <summary>The text.</summary>
    public string Text { get; }

    /// <summary>The name shown in reports.</summary>
    public string Name { get; }

    /// <summary>The number of lines (at least 1).</summary>
    public int LineCount => LineStarts.Length;

    private int[] LineStarts => _lineStarts ??= ComputeLineStarts(Text);

    /// <summary>True for the four ECMAScript line terminator code units.</summary>
    public static bool IsLineTerminator(char c) => c is '\n' or '\r' or '\u2028' or '\u2029';

    /// <summary>Converts an offset to a 1-based line and column. Offsets are clamped to the text.</summary>
    public LinePosition GetPosition(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        int[] starts = LineStarts;
        int lo = 0;
        int hi = starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (starts[mid] <= offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return new LinePosition(lo + 1, offset - starts[lo] + 1);
    }

    /// <summary>Offset of the first code unit of a 1-based line.</summary>
    public int GetLineStart(int line) => LineStarts[Math.Clamp(line, 1, LineStarts.Length) - 1];

    /// <summary>The text of a 1-based line without its terminator.</summary>
    public ReadOnlySpan<char> GetLineText(int line)
    {
        int[] starts = LineStarts;
        line = Math.Clamp(line, 1, starts.Length);
        int start = starts[line - 1];
        int end = line < starts.Length ? starts[line] : Text.Length;
        while (end > start && IsLineTerminator(Text[end - 1]))
        {
            end--;
        }

        return Text.AsSpan(start, end - start);
    }

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
                starts.Add(i + 1);
            }
            else if (IsLineTerminator(c))
            {
                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }
}
