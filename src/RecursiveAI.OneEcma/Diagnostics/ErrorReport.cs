namespace RecursiveAI.OneEcma.Diagnostics;

/// <summary>Builds the <see cref="Diagnostic"/> for a script error or a limit hit at the engine boundary.</summary>
internal static class ErrorReport
{
    private static readonly Text.SourceText Empty = new(string.Empty, "<unknown>");

    internal static Diagnostic ForException(JsException e, string code = "runtime")
    {
        JsValue v = e.Value;
        IReadOnlyList<StackFrameInfo> frames = e.Frames;
        string kind = "Error";
        string headline;
        if (v.Ref is JsError err)
        {
            kind = ErrorName(err);
            headline = ErrorMessage(err);
        }
        else
        {
            kind = ThrownTypeName(v);
            headline = "Uncaught " + Describe(v);
        }

        Diagnostic d = Build(kind, code, headline, frames);
        if (Agent.CurrentOrNull?.Trace is { } trace && v.Ref is JsError)
        {
            try
            {
                Explain.Apply(d, headline, trace);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not JsLimitException)
            {
                // An explanation is a bonus: if building it fails, the plain report stands.
            }
        }

        HostErrors.Decorate(d, v);
        return d;
    }

    internal static Diagnostic ForLimit(JsLimitException e) => Build("Limit", "limit:" + e.Kind.ToString().ToLowerInvariant(), e.Message, e.Frames);

    private static Diagnostic Build(string kind, string code, string headline, IReadOnlyList<StackFrameInfo> frames)
    {
        // Built-in functions are frames of err.stack, not of the report: the report points at the script code that called them.
        var script = new List<StackFrameInfo>(frames.Count);
        foreach (StackFrameInfo f in frames)
        {
            if (!f.IsNative)
            {
                script.Add(f);
            }
        }

        Text.SourceText src = script.Count > 0 ? script[0].Source : Empty;
        Text.Span span = script.Count > 0 ? script[0].Span : new Text.Span(0, 0);
        return new Diagnostic(kind, code, headline, src, span) { Stack = script };
    }

    /// <summary>The constructor name of a thrown non-Error object (for example a harness-defined error class), else "Error".</summary>
    private static string ThrownTypeName(JsValue v)
    {
        try
        {
            if (v.Ref is JsObject o && o.Get("constructor").Ref is JsObject c && c.Get("name") is { IsString: true } n && n.AsString.Length > 0)
            {
                return n.AsString;
            }
        }
        catch (JsException)
        {
            // A throwing getter: fall back to the generic kind.
        }

        return "Error";
    }

    private static string ErrorName(JsError e)
    {
        for (JsObject? q = e; q is not null; q = q.Proto)
        {
            int ns = q.Shape.Lookup("name", out PropFlags nf);
            if (ns >= 0 && (nf & PropFlags.Accessor) == 0 && q.Slots[ns].IsString)
            {
                return q.Slots[ns].AsString;
            }
        }

        return "Error";
    }

    private static string ErrorMessage(JsError e)
    {
        int s = e.Shape.Lookup("message", out PropFlags f);
        return s >= 0 && (f & PropFlags.Accessor) == 0 && e.Slots[s].IsString ? e.Slots[s].AsString : string.Empty;
    }

    private static string Describe(JsValue v)
    {
        try
        {
            if (v.Ref is JsObject o && v.Ref is not JsFunction && o.Get("message") is { IsString: true } m)
            {
                return ThrownTypeName(v) + ": " + m.AsString;
            }

            return v.IsString ? "'" + v.AsString + "'" : Ops.DisplayValue(v);
        }
        catch (JsException)
        {
            return "exception";
        }
    }
}
