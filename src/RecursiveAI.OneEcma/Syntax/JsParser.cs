using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

/// <summary>The grammar goal symbol to parse (ECMA-262 5.1.4).</summary>
public enum ParseGoal
{
    /// <summary>A classic script (<c>Script</c>), sloppy unless it starts with <c>"use strict"</c>.</summary>
    Script,

    /// <summary>A module (<c>Module</c>): always strict, <c>import</c>/<c>export</c> and top-level <c>await</c> allowed.</summary>
    Module,

    /// <summary>Eval code (19.2.1.1 PerformEval): a script whose context flags come from <see cref="ParseOptions"/>.</summary>
    Eval,

    /// <summary>
    /// The body of an async function (<c>AsyncFunctionBody</c>, the goal <c>new AsyncFunction(body)</c> parses), written without the surrounding function:
    /// <c>await</c> and <c>return</c> are allowed at the top level, positions are those of the source itself, and every declaration
    /// (<c>let</c>, <c>const</c>, <c>var</c>, functions, classes) belongs to the body. Sloppy unless the body starts with <c>"use strict"</c> or
    /// <see cref="ParseOptions.Strict"/> is set. <c>new.target</c> is allowed (and undefined); <c>super</c> is not.
    /// </summary>
    AsyncFunctionBody,
}

/// <summary>Called for every regular expression literal whose flags are valid, to check its pattern (ECMA-262 12.9.5 early errors).</summary>
/// <param name="body">The text between the slashes.</param>
/// <param name="flags">The flags, already checked to be valid and unique.</param>
/// <param name="bodyStart">Source offset of the first character of <paramref name="body"/>, so diagnostic spans can be shifted into the source.</param>
/// <returns>The problems found, with spans already in source offsets; empty when the pattern is valid. The parser reports the first one, with its message as the headline.</returns>
public delegate IReadOnlyList<RecursiveAI.OneEcma.RegExp.RegExpDiagnostic> RegExpLiteralValidator(string body, string flags, int bodyStart);

/// <summary>Options for <see cref="JsParser.Parse(string, ParseOptions?, CancellationToken)"/>.</summary>
public sealed class ParseOptions
{
    /// <summary>The goal symbol. Default <see cref="ParseGoal.Script"/>.</summary>
    public ParseGoal Goal { get; set; } = ParseGoal.Script;

    /// <summary>Treat the code as strict from the start (the caller's strictness for eval code; see ECMA-262 11.2.2). Modules are always strict.</summary>
    public bool Strict { get; set; }

    /// <summary>The name shown in reports. Default <c>&lt;anonymous&gt;</c>.</summary>
    public string FileName { get; set; } = "<anonymous>";

    /// <summary>
    /// Maximum nesting depth of statements, expressions, patterns and functions before the parser stops with a
    /// <c>depth-limit</c> diagnostic. Default 512; the parser also stops early if the thread's stack runs low, so the
    /// process never overflows its stack whatever the input.
    /// </summary>
    public int MaxDepth { get; set; } = 512;

    /// <summary>Stop after this many diagnostics. Default 100.</summary>
    public int MaxErrors { get; set; } = 100;

    /// <summary>Allow <c>return</c> outside functions (a host extension for evaluating function bodies). Default false.</summary>
    public bool AllowReturnOutsideFunction { get; set; }

    /// <summary>For eval code: <c>new.target</c> is allowed (the caller is a non-arrow function).</summary>
    public bool AllowNewTarget { get; set; }

    /// <summary>For eval code: <c>super.x</c> is allowed (the caller is a method).</summary>
    public bool AllowSuperProperty { get; set; }

    /// <summary>For eval code: <c>super()</c> is allowed (the caller is a derived constructor).</summary>
    public bool AllowSuperCall { get; set; }

    /// <summary>For eval code in a class field initializer: <c>arguments</c> is an error.</summary>
    public bool InClassFieldInitializer { get; set; }

    /// <summary>For eval code inside classes: the private names (without <c>#</c>) already declared by the enclosing classes.</summary>
    public IReadOnlyCollection<string>? PrivateNames { get; set; }

    /// <summary>
    /// Checks the pattern of every regular expression literal (the flags are always checked by the parser). The default is the
    /// engine's own validator, <see cref="RecursiveAI.OneEcma.RegExp.RegExpValidator.ValidateLiteral"/>; <see langword="null"/> accepts every pattern.
    /// </summary>
    public RegExpLiteralValidator? RegExpValidator { get; set; } = RecursiveAI.OneEcma.RegExp.RegExpValidator.ValidateLiteral;
}

/// <summary>The outcome of a parse: a tree and every diagnostic found.</summary>
public sealed class ParseResult
{
    internal ParseResult(Program program, SourceText source, IReadOnlyList<Diagnostic> diagnostics, FunctionNode? asyncBody = null)
    {
        AsyncBody = asyncBody;
        Program = program;
        Source = source;
        Diagnostics = diagnostics;
    }

    /// <summary>For <see cref="ParseGoal.AsyncFunctionBody"/>: the async function that wraps <see cref="Program"/>'s statements; null for other goals.</summary>
    internal FunctionNode? AsyncBody { get; }

    /// <summary>The tree. When <see cref="HasErrors"/> is true it is a best-effort tree that may contain <see cref="ErrorExpression"/> and <see cref="ErrorStatement"/> nodes and must not be executed.</summary>
    public Program Program { get; }

    /// <summary>The source parsed.</summary>
    public SourceText Source { get; }

    /// <summary>Every syntax and early error found, in source order of discovery; empty when the code is valid.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>True when any diagnostic is an error.</summary>
    public bool HasErrors
    {
        get
        {
            foreach (Diagnostic d in Diagnostics)
            {
                if (d.Severity == Severity.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>Parses ECMAScript source into a syntax tree.</summary>
public static class JsParser
{
    /// <summary>
    /// Parses source text. Never throws for malformed input: every problem becomes a <see cref="Diagnostic"/>,
    /// including nesting too deep for the configured limit.
    /// </summary>
    /// <param name="source">The source text.</param>
    /// <param name="options">Options; <see langword="null"/> means a sloppy classic script.</param>
    /// <param name="cancellationToken">Polled while scanning; cancelling throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The tree and diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static ParseResult Parse(string source, ParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new ParseOptions();
        return Parse(new SourceText(source, options.FileName), options, cancellationToken);
    }

    /// <summary>Parses a <see cref="SourceText"/>; see <see cref="Parse(string, ParseOptions?, CancellationToken)"/>.</summary>
    /// <param name="source">The source text.</param>
    /// <param name="options">Options; <see langword="null"/> means a sloppy classic script.</param>
    /// <param name="cancellationToken">Polled while scanning.</param>
    /// <returns>The tree and diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static ParseResult Parse(SourceText source, ParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Parser(source, options ?? new ParseOptions(), cancellationToken).Run();
    }
}
