using System.Runtime.CompilerServices;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma;

/// <summary>What the host boundary knows about a script error beyond the error object: extra report lines, the .NET frames and the original exception.</summary>
internal sealed class HostErrorInfo
{
    internal string? PrimaryLabel;
    internal List<string> Notes = [];
    internal List<string> Help = [];
    internal Exception? Cause;
    internal List<StackFrameInfo> HostFrames = [];
}

/// <summary>Side table from error objects made at the host boundary to their <see cref="HostErrorInfo"/>; the error object itself stays an ordinary Error.</summary>
internal static class HostErrors
{
    private static readonly ConditionalWeakTable<JsObject, HostErrorInfo> Table = new();

    internal static void Attach(JsObject error, HostErrorInfo info) => Table.AddOrUpdate(error, info);

    internal static Exception? CauseOf(JsValue thrown) => thrown.Ref is JsObject o && Table.TryGetValue(o, out HostErrorInfo? i) ? i.Cause : null;

    /// <summary>Adds the boundary's labels, help and .NET frames to a report built from <paramref name="thrown"/>.</summary>
    internal static void Decorate(Diagnostic d, JsValue thrown)
    {
        if (thrown.Ref is not JsObject o || !Table.TryGetValue(o, out HostErrorInfo? info))
        {
            return;
        }

        if (info.PrimaryLabel is not null)
        {
            d.PrimaryLabel = info.PrimaryLabel;
        }

        d.Notes.AddRange(info.Notes);
        d.Help.AddRange(info.Help);
        if (info.HostFrames.Count > 0)
        {
            var stack = new List<StackFrameInfo>(info.HostFrames.Count + d.Stack.Count);
            stack.AddRange(info.HostFrames);
            stack.AddRange(d.Stack);
            d.Stack = stack;
        }
    }

    /// <summary>
    /// Turns a .NET exception thrown by a host function into the script error: an <c>Error</c> whose message names the .NET type
    /// and carries its message, with the full type name in <c>dotnetType</c>. The original exception comes back as the
    /// <see cref="Exception.InnerException"/> if the error reaches C# uncaught.
    /// </summary>
    internal static JsException FromException(Exception e, string member)
    {
        string text = e.GetType().Name + ": " + e.Message;
        JsException je = Errors.Make("Error", text);
        var err = (JsObject)je.Value.Ref!;
        err.DefineDirect("dotnetType", JsValue.FromString(e.GetType().FullName ?? e.GetType().Name), PropFlags.Hidden);
        var info = new HostErrorInfo { Cause = e };
        info.HostFrames.AddRange(ParseFrames(e));
        if (info.HostFrames.Count == 0 && member.Length > 0)
        {
            info.HostFrames.Add(new StackFrameInfo(member, new SourceText(string.Empty, member), new Span(0, 0)) { IsHost = true });
        }

        info.Notes.Add("thrown by .NET code: " + (e.GetType().FullName ?? e.GetType().Name) + (member.Length > 0 ? " in " + member : string.Empty));
        Attach(err, info);
        return je;
    }

    /// <summary>Reads the frames of a .NET stack trace, innermost first, leaving out the engine's own frames, generated binding code and async plumbing.</summary>
    internal static List<StackFrameInfo> ParseFrames(Exception e)
    {
        var frames = new List<StackFrameInfo>();
        string? trace = e.StackTrace;
        if (trace is null)
        {
            return frames;
        }

        foreach (string raw in trace.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("at ", StringComparison.Ordinal))
            {
                continue;
            }

            line = line[3..];
            string file = string.Empty;
            int lineNo = 0;
            int inIdx = line.LastIndexOf(" in ", StringComparison.Ordinal);
            if (inIdx >= 0)
            {
                string loc = line[(inIdx + 4)..];
                line = line[..inIdx];
                int colon = loc.LastIndexOf(":line ", StringComparison.Ordinal);
                if (colon >= 0)
                {
                    _ = int.TryParse(loc.AsSpan(colon + 6), System.Globalization.NumberStyles.None, System.Globalization.NumberFormatInfo.InvariantInfo, out lineNo);
                    loc = loc[..colon];
                }

                int slash = Math.Max(loc.LastIndexOf('/'), loc.LastIndexOf('\\'));
                file = slash >= 0 ? loc[(slash + 1)..] : loc;
            }

            int paren = line.IndexOf('(', StringComparison.Ordinal);
            string method = paren >= 0 ? line[..paren] : line;
            method = CleanName(method);
            if (method.StartsWith("RecursiveAI.OneEcma.", StringComparison.Ordinal) || method.StartsWith("System.", StringComparison.Ordinal)
                || method.StartsWith("Microsoft.", StringComparison.Ordinal) || method.Contains("JsBinding", StringComparison.Ordinal) || method.Length == 0)
            {
                continue;
            }

            frames.Add(new StackFrameInfo(method, new SourceText(string.Empty, file.Length > 0 ? file : method), new Span(0, 0)) { IsHost = true, HostLine = lineNo });
            if (frames.Count >= 8)
            {
                break;
            }
        }

        return frames;
    }

    /// <summary>Turns <c>Ns.Type.&lt;RunAsync&gt;d__3.MoveNext</c> into <c>Ns.Type.RunAsync</c> and drops lambda display-class noise.</summary>
    private static string CleanName(string method)
    {
        int d = method.IndexOf(">d__", StringComparison.Ordinal);
        if (d > 0)
        {
            int open = method.LastIndexOf('<', d);
            if (open >= 0)
            {
                return method[..open].TrimEnd('.') is { } prefix && prefix.Length > 0 ? prefix + "." + method[(open + 1)..d] : method[(open + 1)..d];
            }
        }

        int dc = method.IndexOf(".<>c", StringComparison.Ordinal);
        return dc > 0 ? method[..dc] + ".<lambda>" : method;
    }
}

/// <summary>Wraps host callbacks so a .NET exception becomes a script error.</summary>
internal static class HostCalls
{
    internal static NativeFn Wrap(HostFunction fn, string member) => (t, a, nt) =>
    {
        try
        {
            return fn(t, a);
        }
        catch (Exception e) when (e is not (JsException or JsLimitException or OperationCanceledException))
        {
            throw HostErrors.FromException(e, member);
        }
    };
}
