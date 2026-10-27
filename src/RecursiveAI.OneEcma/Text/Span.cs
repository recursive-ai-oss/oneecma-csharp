namespace RecursiveAI.OneEcma.Text;

/// <summary>A half-open range <c>[Start, End)</c> of UTF-16 code unit offsets into a source text.</summary>
/// <param name="Start">Offset of the first code unit.</param>
/// <param name="End">Offset one past the last code unit; equal to <paramref name="Start"/> for an empty span.</param>
public readonly record struct Span(int Start, int End)
{
    /// <summary>The number of code units covered.</summary>
    public int Length => End - Start;

    /// <summary>True when the span covers nothing (a position, such as the end of input).</summary>
    public bool IsEmpty => End <= Start;

    /// <summary>The smallest span covering both spans.</summary>
    public Span Union(Span other) => new(Math.Min(Start, other.Start), Math.Max(End, other.End));

    /// <inheritdoc/>
    public override string ToString() => $"{Start}..{End}";
}

/// <summary>A 1-based line and column; the column counts UTF-16 code units, as V8's positions do.</summary>
/// <param name="Line">1-based line number.</param>
/// <param name="Column">1-based column in UTF-16 code units.</param>
public readonly record struct LinePosition(int Line, int Column);
