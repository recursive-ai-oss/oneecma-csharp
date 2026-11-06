using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

/// <summary>Options for <see cref="ScriptEngine.StripTypes(string, TypeStripOptions?, CancellationToken)"/>.</summary>
public sealed class TypeStripOptions
{
    /// <summary>The file name shown in reports. Default <c>&lt;anonymous&gt;</c>.</summary>
    public string FileName { get; set; } = "<anonymous>";

    /// <summary>
    /// True (the default) when the file is a module, so <c>import</c> and <c>export</c> are allowed and the result is checked
    /// against the module goal; false for a classic script.
    /// </summary>
    public bool Module { get; set; } = true;
}

/// <summary>The outcome of <see cref="ScriptEngine.StripTypes(string, TypeStripOptions?, CancellationToken)"/>.</summary>
public sealed class TypeStripResult
{
    internal TypeStripResult(string? code, IReadOnlyList<Diagnostic> diagnostics)
    {
        Code = code;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// The JavaScript to run: the source with every erasable type construct replaced by spaces, so each line and column is the
    /// same as in the original and reports point at the original file. <see langword="null"/> when <see cref="Diagnostics"/> holds errors.
    /// Source that has nothing to erase comes back as the very same text.
    /// </summary>
    public string? Code { get; }

    /// <summary>Every construct that cannot be erased and every syntax error, in source order, with spans in the original text; empty on success.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>True when <see cref="Code"/> is available.</summary>
    public bool Success => Code is not null;
}
