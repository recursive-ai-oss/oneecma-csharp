using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Diagnostics;

/// <summary>How serious a diagnostic is.</summary>
public enum Severity
{
    /// <summary>The program is rejected or the run failed.</summary>
    Error,

    /// <summary>The program is accepted but something is likely a mistake.</summary>
    Warning,
}

/// <summary>A labelled range of source. The primary label marks where the problem is; secondary labels give context.</summary>
/// <param name="Span">The labelled range.</param>
/// <param name="Message">Short text shown beside the underline; may be empty.</param>
/// <param name="IsPrimary">True for the one primary label (underlined with <c>^</c>); secondary labels use <c>-</c>.</param>
public sealed record Label(Span Span, string Message, bool IsPrimary = false);

/// <summary>A single replacement in the source text: the range <c>[Start, Start+Length)</c> becomes <see cref="Text"/>.</summary>
/// <param name="Start">Offset of the first replaced code unit.</param>
/// <param name="Length">Number of code units replaced; 0 for a pure insertion.</param>
/// <param name="Text">The replacement; empty for a pure deletion.</param>
public sealed record TextEdit(int Start, int Length, string Text);

/// <summary>A proposed change to the source that would remove the error.</summary>
/// <param name="Description">What the change does, in plain words.</param>
/// <param name="Edits">The edits, in source order, none overlapping.</param>
public sealed record SuggestedFix(string Description, IReadOnlyList<TextEdit> Edits);

/// <summary>What the recovering parser did to continue after an error; the raw material for repair candidates.</summary>
public enum RecoveryKind
{
    /// <summary>The parser behaved as if a token were present at the offset.</summary>
    InsertedToken,

    /// <summary>The parser skipped the tokens in the range.</summary>
    SkippedTokens,
}

/// <summary>One recovery step recorded next to the error that caused it.</summary>
/// <param name="Kind">Whether a token was assumed or tokens were dropped.</param>
/// <param name="Span">Where: an empty span at the insertion point, or the skipped range.</param>
/// <param name="Text">For an insertion, the token text that was assumed (such as <c>)</c>); empty for a skip.</param>
public sealed record RecoveryAction(RecoveryKind Kind, Span Span, string Text);

/// <summary>
/// A report about one problem. The shape is the stable core of every report the engine produces: syntax
/// and early errors fill it now, and runtime reports add stack frames, locals and provenance as
/// further members later without changing what is here.
/// </summary>
public sealed class Diagnostic
{
    /// <summary>Creates a diagnostic.</summary>
    /// <param name="kind">The JavaScript error constructor name shown in <c>error[Kind]</c>, such as <c>SyntaxError</c>.</param>
    /// <param name="code">A stable machine-readable id such as <c>unexpected-token</c>.</param>
    /// <param name="headline">The message, word for word what V8 puts in <c>err.message</c>.</param>
    /// <param name="source">The source the spans refer to.</param>
    /// <param name="primary">The primary span.</param>
    /// <param name="severity">The severity.</param>
    public Diagnostic(string kind, string code, string headline, SourceText source, Span primary, Severity severity = Severity.Error)
    {
        Kind = kind;
        Code = code;
        Headline = headline;
        Source = source;
        PrimarySpan = primary;
        Severity = severity;
    }

    /// <summary>The error constructor name, such as <c>SyntaxError</c>.</summary>
    public string Kind { get; }

    /// <summary>A stable machine-readable id such as <c>unexpected-token</c>; never reworded once published.</summary>
    public string Code { get; }

    /// <summary>The headline: what V8 puts in <c>err.message</c> for the same mistake.</summary>
    public string Headline { get; }

    /// <summary>The severity.</summary>
    public Severity Severity { get; }

    /// <summary>The source every span in this diagnostic refers to.</summary>
    public SourceText Source { get; }

    /// <summary>
    /// The primary span: where the problem is. The location line of a report (<c>--&gt; file:line:col</c>) is the start of this span.
    /// </summary>
    public Span PrimarySpan { get; }

    /// <summary>
    /// What the primary underline says; may be empty. Supplied by the parser or a later hint pass.
    /// </summary>
    public string PrimaryLabel { get; set; } = "";

    /// <summary>Secondary labelled spans, in no particular order; the renderer sorts them.</summary>
    public List<Label> SecondaryLabels { get; } = [];

    /// <summary>Plain <c>= note:</c> lines.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Plain <c>= help:</c> lines.</summary>
    public List<string> Help { get; } = [];

    /// <summary>Proposed source changes; none is applied automatically by the parser.</summary>
    public List<SuggestedFix> Fixes { get; } = [];

    /// <summary>What the parser assumed or skipped to continue after this error.</summary>
    public List<RecoveryAction> Recovery { get; } = [];

    /// <summary>The 1-based line and column where the primary span starts.</summary>
    public LinePosition Position => Source.GetPosition(PrimarySpan.Start);

    /// <summary>Adds a secondary label and returns this diagnostic.</summary>
    public Diagnostic WithLabel(Span span, string message)
    {
        SecondaryLabels.Add(new Label(span, message));
        return this;
    }

    /// <summary>Renders this diagnostic as plain text.</summary>
    public override string ToString() => DiagnosticRenderer.Render(this);
}
