using System.Text;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Cli;

/// <summary>
/// The read-eval-print loop. Input that stops in the middle of a construct (an open brace, bracket, string or template) continues on
/// the next line under a <c>... </c> prompt. <c>await</c> works at the top level: when a line only parses inside an async function, it
/// runs as one, so a <c>const</c> or <c>let</c> declared that way is local to that line. Errors print as the same reports <c>run</c> prints.
/// Commands: <c>.help</c>, <c>.exit</c>, <c>.clear</c> (drops a half-typed entry).
/// </summary>
internal sealed class Repl
{
    private const string Prompt = "> ";
    private const string Continue = "... ";

    private readonly ScriptEngine _engine;
    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly RenderOptions _render;
    private readonly bool _showPrompt;
    private int _entry;

    internal Repl(ScriptEngine engine, TextReader input, TextWriter output, TextWriter error, bool ansi, bool showPrompt)
    {
        _engine = engine;
        _in = input;
        _out = output;
        _err = error;
        _render = new RenderOptions { Ansi = ansi };
        _showPrompt = showPrompt;
    }

    /// <summary>Runs until <c>.exit</c> or the end of the input; returns the process exit code (0).</summary>
    internal int Run()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            if (_showPrompt)
            {
                _out.Write(buffer.Length == 0 ? Prompt : Continue);
                _out.Flush();
            }

            string? line = _in.ReadLine();
            if (line is null)
            {
                if (_showPrompt)
                {
                    _out.WriteLine();
                }

                return 0;
            }

            if (buffer.Length == 0 && line.StartsWith('.'))
            {
                string cmd = line.Trim();
                if (cmd == ".exit")
                {
                    return 0;
                }

                if (cmd == ".help")
                {
                    _out.WriteLine(".exit  leave the repl\n.clear drop the entry being typed\n.help  this text\nawait works at the top level; a multi-line entry continues until it is complete.");
                    continue;
                }

                if (cmd == ".clear")
                {
                    continue;
                }
            }
            else if (buffer.Length > 0 && line.Trim() == ".clear")
            {
                buffer.Clear();
                continue;
            }

            if (buffer.Length > 0)
            {
                buffer.Append('\n');
            }

            buffer.Append(line);
            string source = buffer.ToString();
            if (source.Trim().Length == 0)
            {
                buffer.Clear();
                continue;
            }

            if (IsIncomplete(source))
            {
                continue;
            }

            buffer.Clear();
            Evaluate(source);
        }
    }

    /// <summary>True when every syntax error is at the end of the input, so more text could still complete it.</summary>
    private static bool IsIncomplete(string source)
    {
        IReadOnlyList<Diagnostic> errors = ScriptEngine.Check(source);
        if (errors.Count == 0)
        {
            return false;
        }

        // An await at the top level is an error for a script but fine in the wrapper; judge completeness on the wrapped form.
        if (source.Contains("await", StringComparison.Ordinal) && ScriptEngine.Check(Wrap(source, false)).Count == 0)
        {
            return false;
        }

        int end = source.TrimEnd().Length;
        foreach (Diagnostic d in errors)
        {
            // V8's wording for input that stops early; the span rule covers the rest.
            bool atEnd = d.Headline is "Unexpected end of input" or "Unterminated template literal" || d.PrimarySpan.Start >= end;
            if (!atEnd)
            {
                return false;
            }
        }

        return true;
    }

    private static string Wrap(string source, bool asExpression) =>
        asExpression ? "(async () => (\n" + source + "\n))()" : "(async () => {\n" + source + "\n})()";

    private void Evaluate(string source)
    {
        string name = "repl:" + (++_entry).ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            JsValue result;
            if (ScriptEngine.Check(source, new ParseOptions { FileName = name }).Count == 0)
            {
                result = _engine.Evaluate(source, name);
            }
            else if (source.Contains("await", StringComparison.Ordinal))
            {
                // Top-level await: an expression yields its value, a statement list runs as an async function body.
                string expression = source.Trim().TrimEnd(';');
                string wrapped = ScriptEngine.Check(Wrap(expression, true)).Count == 0 ? Wrap(expression, true) : Wrap(source, false);
                JsValue promise = _engine.Evaluate(wrapped, name);
                result = _engine.Await(promise);
            }
            else
            {
                result = _engine.Evaluate(source, name);
            }

            if (!result.IsUndefined)
            {
                _out.WriteLine(Inspect(result));
            }
        }
        catch (JsException e)
        {
            _err.Write(e.Report is { } r ? DiagnosticRenderer.Render(r, _render) : e.Message + "\n");
        }
        catch (JsLimitException e)
        {
            _err.Write(e.Report is { } r ? DiagnosticRenderer.Render(r, _render) : e.Message + "\n");
        }
    }

    /// <summary>Shows a result the way a developer expects: strings quoted, objects as JSON when they have a JSON form.</summary>
    private string Inspect(JsValue v)
    {
        if (v.IsString)
        {
            return "'" + v.AsString.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
        }

        if (v.IsBigInt)
        {
            return _engine.ToDisplayString(v) + "n";
        }

        if (v.IsObject)
        {
            JsRealm realm = _engine.MainRealm;
            if (JsRealm.IsCallable(v))
            {
                return _engine.ToDisplayString(v).Split('\n')[0];
            }

            try
            {
                JsValue json = realm.Get(realm.Global, "JSON");
                JsValue stringify = realm.Get(json.AsObject!, "stringify");
                JsValue text = realm.Call(stringify, json, v, JsValue.Null, JsValue.FromNumber(2));
                if (text.IsString)
                {
                    return text.AsString;
                }
            }
            catch (JsException)
            {
                // Cyclic or throwing toJSON: fall through to the plain form.
            }
        }

        return _engine.ToDisplayString(v);
    }
}
