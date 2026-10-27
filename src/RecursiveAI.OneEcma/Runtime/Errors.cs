using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>A JavaScript exception in flight, or one that reached the host: carries the thrown value and, for Error objects, the captured frames.</summary>
public sealed class JsException : Exception
{
    /// <summary>Creates an exception that throws <paramref name="value"/> into script; host callbacks use it to throw a script value.</summary>
    /// <param name="value">The value to throw, usually an error made with <see cref="JsRealm.NewError"/>.</param>
    public JsException(JsValue value)
        : base(Describe(value))
    {
        Value = value;
    }

    internal JsException(JsValue value, Exception? inner)
        : base(Describe(value), inner)
    {
        Value = value;
    }

    /// <summary>The thrown value.</summary>
    public JsValue Value { get; }

    /// <summary>The diagnostic describing the failure, set when the exception leaves the engine.</summary>
    public Diagnostics.Diagnostic? Report { get; internal set; }

    /// <summary>Script frames where the error was created, innermost first (empty for non-errors).</summary>
    public IReadOnlyList<StackFrameInfo> Frames => (Value.Ref as JsError)?.Frames ?? [];

    private static string Describe(JsValue v)
    {
        if (v.Ref is JsError e)
        {
            string name = e.Shape.Lookup("name", out _) >= 0 || e.Proto is not null ? "Error" : "Error";
            string msg = "";
            if (e.Shape.Lookup("message", out _) is int s and >= 0 && e.Slots[s].IsString)
            {
                msg = e.Slots[s].AsString;
            }

            JsObject? p = e.Proto;
            if (p is not null && p.Shape.Lookup("name", out _) is int ns and >= 0 && p.Slots[ns].IsString)
            {
                name = p.Slots[ns].AsString;
            }

            return msg.Length == 0 ? name : name + ": " + msg;
        }

        return "Uncaught " + Ops.DebugString(v);
    }
}

/// <summary>Factory for the exceptions built-ins throw (the messages follow V8's wording).</summary>
internal static class Errors
{
    internal static JsException Type(string message) => Make("TypeError", message);

    internal static JsException Range(string message) => Make("RangeError", message);

    internal static JsException Reference(string message) => Make("ReferenceError", message);

    internal static JsException Syntax(string message) => Make("SyntaxError", message);

    internal static JsException Make(string kind, string message)
    {
        Realm r = Agent.Current.Realm;
        return new JsException(JsValue.FromObject(r.NewError(kind, message)));
    }

    /// <summary>Formats the V8 <c>stack</c> text: header line then "    at ..." frames.</summary>
    internal static string FormatStack(string header, IReadOnlyList<StackFrameInfo> frames)
    {
        var sb = new StringBuilder(header);
        foreach (StackFrameInfo f in frames)
        {
            sb.Append("\n    at ");
            Text.LinePosition p = f.Position;
            string loc = f.Source.Name + ":" + p.Line.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + ":" + p.Column.ToString(System.Globalization.NumberFormatInfo.InvariantInfo);
            string name = f.Function;
            if (f.IsConstruct)
            {
                name = "new " + (name.Length == 0 ? "<anonymous>" : name);
            }

            if (f.IsAsync && name.Length > 0)
            {
                name = "async " + name;
            }

            if (name.Length == 0)
            {
                sb.Append(loc);
            }
            else
            {
                sb.Append(name).Append(" (").Append(loc).Append(')');
            }
        }

        return sb.ToString();
    }
}
