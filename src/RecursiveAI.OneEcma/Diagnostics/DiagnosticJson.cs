using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Diagnostics;

/// <summary>Options for <see cref="DiagnosticJson"/>.</summary>
public sealed class JsonRenderOptions
{
    /// <summary>Indent the output with two spaces. Off by default: one line is the most compact form for a machine reader or an LLM.</summary>
    public bool Indented { get; set; }
}

/// <summary>
/// Renders diagnostics and applied repairs as the versioned JSON report. The format, every field and the rules for adding
/// fields are in <c>docs/diagnostic-json.md</c>; the JSON Schema is <c>docs/diagnostic-report.schema.json</c>. Written with
/// <see cref="Utf8JsonWriter"/> only, so it needs no reflection and is safe for trimming and NativeAOT.
/// </summary>
public static class DiagnosticJson
{
    /// <summary>The format version written as <c>"version"</c>. Fields are only ever added within a version.</summary>
    public const int Version = 1;

    /// <summary>The value of the top-level <c>"format"</c> field.</summary>
    public const string Format = "oneecma.report";

    /// <summary>Renders diagnostics (and no repairs).</summary>
    /// <param name="diagnostics">The diagnostics, in report order.</param>
    /// <param name="options">Options; null means compact output.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is null.</exception>
    public static string Render(IReadOnlyList<Diagnostic> diagnostics, JsonRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return ToString(options, w => Write(w, diagnostics, [], null));
    }

    /// <summary>Renders diagnostics together with applied repairs, for hosts that post-process a repair result (for example by remapping its diagnostics through a source map).</summary>
    /// <param name="diagnostics">The diagnostics, in report order.</param>
    /// <param name="repairs">Applied repairs, first to last; may be empty.</param>
    /// <param name="repairedSource">The repaired source, or null when nothing was repaired.</param>
    /// <param name="options">Options; null means compact output.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> or <paramref name="repairs"/> is null.</exception>
    public static string Render(IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<AppliedRepair> repairs, string? repairedSource, JsonRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(repairs);
        return ToString(options, w => Write(w, diagnostics, repairs, repairedSource));
    }

    /// <summary>Renders a repair result: the repairs applied, the repaired source when anything was repaired, and the diagnostics that remain.</summary>
    /// <param name="result">The result of a repair run.</param>
    /// <param name="options">Options; null means compact output.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static string Render(RepairResult result, JsonRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return ToString(options, w => Write(w, result.Diagnostics, result.Repairs, result.Changed ? result.RepairedSource : null));
    }

    /// <summary>Writes the report to an existing writer, for hosts that embed it in a larger document.</summary>
    /// <param name="writer">The writer; it should use <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> for readable output.</param>
    /// <param name="diagnostics">The diagnostics.</param>
    /// <param name="repairs">Applied repairs, first to last; may be empty.</param>
    /// <param name="repairedSource">The repaired source, or null when nothing was repaired.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void Write(Utf8JsonWriter writer, IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<AppliedRepair> repairs, string? repairedSource)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(repairs);
        int errors = 0;
        int warnings = 0;
        foreach (Diagnostic d in diagnostics)
        {
            if (d.Severity == Severity.Error)
            {
                errors++;
            }
            else
            {
                warnings++;
            }
        }

        writer.WriteStartObject();
        writer.WriteNumber("version", Version);
        writer.WriteString("format", Format);
        writer.WriteStartObject("summary");
        writer.WriteNumber("errors", errors);
        writer.WriteNumber("warnings", warnings);
        writer.WriteNumber("repairs", repairs.Count);
        writer.WriteEndObject();

        if (repairs.Count > 0)
        {
            writer.WriteStartArray("repairs");
            foreach (AppliedRepair r in repairs)
            {
                WriteRepair(writer, r);
            }

            writer.WriteEndArray();
        }

        if (repairedSource is not null)
        {
            Str(writer, "repairedSource", repairedSource);
        }

        writer.WriteStartArray("diagnostics");
        foreach (Diagnostic d in diagnostics)
        {
            WriteDiagnostic(writer, d);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string ToString(JsonRenderOptions? options, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        var wo = new JsonWriterOptions { Indented = options?.Indented ?? false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var w = new Utf8JsonWriter(buffer, wo))
        {
            body(w);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteDiagnostic(Utf8JsonWriter w, Diagnostic d)
    {
        w.WriteStartObject();
        Str(w, "kind", d.Kind);
        Str(w, "code", d.Code);
        Str(w, "severity", d.Severity == Severity.Error ? "error" : "warning");
        Str(w, "headline", d.Headline);
        WriteSourceRef(w, "source", d.Source);
        WriteSpan(w, "span", d.Source, d.PrimarySpan);
        Str(w, "label", d.PrimaryLabel);

        w.WriteStartArray("secondary");
        foreach (Label l in d.SecondaryLabels)
        {
            w.WriteStartObject();
            WriteSpan(w, "span", d.Source, l.Span);
            Str(w, "message", l.Message);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        Strings(w, "notes", d.Notes);
        Strings(w, "help", d.Help);

        w.WriteStartArray("fixes");
        foreach (SuggestedFix f in d.Fixes)
        {
            w.WriteStartObject();
            Str(w, "description", f.Description);
            WriteEdits(w, d.Source, f.Edits);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartArray("recovery");
        foreach (RecoveryAction r in d.Recovery)
        {
            w.WriteStartObject();
            Str(w, "action", r.Kind == RecoveryKind.InsertedToken ? "insertedToken" : "skippedTokens");
            WriteSpan(w, "span", d.Source, r.Span);
            Str(w, "text", r.Text);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        if (d.Stack.Count > 0)
        {
            w.WriteStartArray("stack");
            foreach (StackFrameInfo fr in d.Stack)
            {
                w.WriteStartObject();
                Str(w, "function", fr.Function);
                WriteSourceRef(w, "source", fr.Source);
                WriteSpan(w, "span", fr.Source, fr.Span);
                w.WriteBoolean("async", fr.IsAsync);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (d.Mapping is { } m)
        {
            w.WriteStartObject("mapping");
            w.WriteBoolean("spansAreOriginal", m.SpansAreOriginal);
            WriteLocation(w, "original", m.Original);
            WriteLocation(w, "generated", m.Generated);
            w.WriteStartObject("generatedSpan");
            w.WriteNumber("start", m.GeneratedSpan.Start);
            w.WriteNumber("end", m.GeneratedSpan.End);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static void WriteLocation(Utf8JsonWriter w, string name, MappedLocation l)
    {
        w.WriteStartObject(name);
        Str(w, "source", l.Source);
        w.WriteNumber("line", l.Line);
        w.WriteNumber("column", l.Column);
        w.WriteNumber("endLine", l.EndLine);
        w.WriteNumber("endColumn", l.EndColumn);
        if (l.Name is not null)
        {
            Str(w, "name", l.Name);
        }

        w.WriteEndObject();
    }

    private static void WriteRepair(Utf8JsonWriter w, AppliedRepair r)
    {
        w.WriteStartObject();
        Str(w, "id", r.Id);
        Str(w, "title", r.Title);
        Str(w, "label", r.Label);
        WriteSourceRef(w, "source", r.Source);
        WriteSpan(w, "span", r.Source, r.Span);
        WriteEdits(w, r.Source, r.Edits);
        w.WriteEndObject();
    }

    private static void WriteEdits(Utf8JsonWriter w, SourceText source, IReadOnlyList<TextEdit> edits)
    {
        w.WriteStartArray("edits");
        foreach (TextEdit e in edits)
        {
            w.WriteStartObject();
            w.WriteNumber("start", e.Start);
            w.WriteNumber("length", e.Length);
            LinePosition p = source.GetPosition(e.Start);
            w.WriteNumber("line", p.Line);
            w.WriteNumber("column", p.Column);
            Str(w, "text", e.Text);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteSourceRef(Utf8JsonWriter w, string name, SourceText source)
    {
        w.WriteStartObject(name);
        Str(w, "name", source.Name);
        w.WriteNumber("length", source.Text.Length);
        w.WriteEndObject();
    }

    private static void WriteSpan(Utf8JsonWriter w, string name, SourceText source, Span span)
    {
        int end = Math.Max(span.End, span.Start);
        LinePosition s = source.GetPosition(span.Start);
        LinePosition e = source.GetPosition(end);
        w.WriteStartObject(name);
        w.WriteNumber("start", span.Start);
        w.WriteNumber("end", end);
        w.WriteNumber("line", s.Line);
        w.WriteNumber("column", s.Column);
        w.WriteNumber("endLine", e.Line);
        w.WriteNumber("endColumn", e.Column);
        w.WriteEndObject();
    }

    private static void Strings(Utf8JsonWriter w, string name, List<string> values)
    {
        w.WriteStartArray(name);
        foreach (string v in values)
        {
            w.WriteStringValue(Clean(v));
        }

        w.WriteEndArray();
    }

    private static void Str(Utf8JsonWriter w, string name, string value) => w.WriteString(name, Clean(value));

    /// <summary>JSON text is Unicode, so a lone UTF-16 surrogate in script source (legal in JavaScript) is written as U+FFFD.</summary>
    private static string Clean(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return Fix(s);
            }
        }

        return s;
    }

    private static string Fix(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(c).Append(s[++i]);
            }
            else
            {
                sb.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
            }
        }

        return sb.ToString();
    }
}
