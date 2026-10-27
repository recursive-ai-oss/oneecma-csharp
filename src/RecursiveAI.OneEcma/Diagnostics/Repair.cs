using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Diagnostics;

/// <summary>One automatic repair that was applied to the source: which table rule, what it changed and where.</summary>
public sealed class AppliedRepair
{
    internal AppliedRepair(string id, string title, string label, Span span, SourceText source, IReadOnlyList<TextEdit> edits)
    {
        Id = id;
        Title = title;
        Label = label;
        Span = span;
        Source = source;
        Edits = edits;
    }

    /// <summary>The id of the rule in the repair table, such as <c>R001</c>.</summary>
    public string Id { get; }

    /// <summary>What the rule did, in plain words (the headline of the repair block).</summary>
    public string Title { get; }

    /// <summary>Short text for the underline at <see cref="Span"/>, such as <c>removed</c>.</summary>
    public string Label { get; }

    /// <summary>The first place the repair touched, in the original source; the location line of the report points here.</summary>
    public Span Span { get; }

    /// <summary>The original source <see cref="Span"/> and <see cref="Edits"/> refer to.</summary>
    public SourceText Source { get; }

    /// <summary>
    /// Every edit the repair made, in source order, with offsets into the original source. Applying all repairs in
    /// the order they are listed to the original source gives <see cref="RepairResult.RepairedSource"/>.
    /// </summary>
    public IReadOnlyList<TextEdit> Edits { get; }
}

/// <summary>The outcome of <see cref="Syntax.SyntaxRepair.Repair(string, Syntax.ParseOptions?, Syntax.RepairOptions?, CancellationToken)"/>.</summary>
public sealed class RepairResult
{
    internal RepairResult(SourceText original, SourceText repaired, IReadOnlyList<AppliedRepair> repairs, IReadOnlyList<Diagnostic> diagnostics)
    {
        Original = original;
        Repaired = repaired;
        Repairs = repairs;
        Diagnostics = diagnostics;
    }

    /// <summary>The source as given.</summary>
    public SourceText Original { get; }

    /// <summary>The source after the repairs; the same text as <see cref="Original"/> when nothing was repaired.</summary>
    public SourceText Repaired { get; }

    /// <summary>The repaired source text.</summary>
    public string RepairedSource => Repaired.Text;

    /// <summary>The repairs that were applied, in the order they were applied; empty when nothing was repaired.</summary>
    public IReadOnlyList<AppliedRepair> Repairs { get; }

    /// <summary>True when at least one repair was applied.</summary>
    public bool Changed => Repairs.Count > 0;

    /// <summary>
    /// What is still wrong. Empty when the original parsed or the repairs fixed everything. When no safe repair
    /// exists these are the original errors, with <see cref="Diagnostic.Fixes"/> on the ones that have a suggestion
    /// (suggestions are never applied), and a <c>repair-limit</c> warning if a limit stopped the search.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>True when <see cref="RepairedSource"/> parses with no error.</summary>
    public bool Parses
    {
        get
        {
            foreach (Diagnostic d in Diagnostics)
            {
                if (d.Severity == Severity.Error)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
