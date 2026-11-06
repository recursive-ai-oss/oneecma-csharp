using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma;

public sealed partial class ScriptEngine
{
    /// <summary>
    /// Turns TypeScript that uses only erasable syntax into JavaScript. Type annotations, type parameters and arguments,
    /// <c>as</c>/<c>satisfies</c>/<c>!</c>, <c>interface</c> and <c>type</c> declarations, <c>declare</c> declarations, overload signatures,
    /// index signatures, <c>implements</c>, <c>abstract</c> members, accessibility modifiers, <c>import type</c>/<c>export type</c> and
    /// inline <c>type</c> specifiers are replaced by spaces. Every line break and every other character stays where it was, so a
    /// line and column in the result is the same line and column in the TypeScript source and reports need no source map.
    /// Where blanking alone would change meaning, one token is written into the erased text: a <c>;</c> where an erased
    /// <c>as T</c> would let the next line's <c>(</c>, <c>[</c> or template join the expression, and the arrow of an arrow function whose
    /// erased return type ran over a line break moves up next to its <c>)</c>.
    /// Constructs that need code generation are refused with a report each, and no code is returned: <c>enum</c>, namespaces that
    /// contain values, constructor parameter properties, <c>import x = require()</c>, <c>export =</c>, <c>&lt;T&gt;x</c> casts and parameter decorators.
    /// Decorators on classes and members pass through unchanged. Source with nothing to erase is returned as the same text.
    /// The result is parsed once more, so a JavaScript syntax error left in it is reported with a span in the original text.
    /// Type-checking is not done: types are skipped, never resolved. <c>.tsx</c> is not supported.
    /// </summary>
    /// <param name="source">The TypeScript source text.</param>
    /// <param name="options">Options; <see langword="null"/> means a module named <c>&lt;anonymous&gt;</c>.</param>
    /// <param name="cancellationToken">Polled while reading the source; cancelling throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The JavaScript, or the diagnostics that say why there is none. The nesting of the source is bounded, and deeper source yields a diagnostic.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static TypeStripResult StripTypes(string source, TypeStripOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new TypeStripOptions();
        var text = new SourceText(source, options.FileName);
        var parseOptions = new ParseOptions { Goal = options.Module ? ParseGoal.Module : ParseGoal.Script, FileName = options.FileName };

        var stripper = new TypeStripper(text, cancellationToken);
        stripper.Run();
        List<Diagnostic> diagnostics = stripper.Diagnostics;
        if (diagnostics.Count > 0)
        {
            // The reader knows less JavaScript than the parser. If it only stumbled and the source is valid as it stands, pass it through.
            if (!stripper.HasRefusals && !JsParser.Parse(text, parseOptions, cancellationToken).HasErrors)
            {
                return new TypeStripResult(source, []);
            }

            diagnostics.Sort(static (a, b) => a.PrimarySpan.Start.CompareTo(b.PrimarySpan.Start));
            return new TypeStripResult(null, diagnostics);
        }

        string code = stripper.HasEdits ? stripper.Apply() : source;
        ParseResult parsed = JsParser.Parse(code, parseOptions, cancellationToken);
        if (parsed.HasErrors)
        {
            var rebased = new List<Diagnostic>(parsed.Diagnostics.Count);
            foreach (Diagnostic d in parsed.Diagnostics)
            {
                rebased.Add(Rebase(d, text));
            }

            return new TypeStripResult(null, rebased);
        }

        return new TypeStripResult(code, []);
    }

    // The parser saw the blanked text; the report must show the original, whose line and column numbers are identical.
    private static Diagnostic Rebase(Diagnostic d, SourceText original)
    {
        var copy = new Diagnostic(d.Kind, d.Code, d.Headline, original, d.PrimarySpan, d.Severity) { PrimaryLabel = d.PrimaryLabel };
        copy.SecondaryLabels.AddRange(d.SecondaryLabels);
        copy.Notes.AddRange(d.Notes);
        copy.Help.AddRange(d.Help);
        return copy;
    }
}
