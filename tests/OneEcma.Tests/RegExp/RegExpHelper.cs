using System.Text;
using RecursiveAI.OneEcma.RegExp;

namespace RecursiveAI.OneEcma.Tests.RegExp;

/// <summary>Runs a pattern and renders the result the way the Node recorder does: a JSON array of captures, or null, or an error marker.</summary>
internal static class RegExpHelper
{
    internal static RegExpProgram Compile(string pattern, string flags)
    {
        var r = RegExpProgram.Compile(pattern, flags);
        Assert.True(r.Program is not null, r.Diagnostics.Count > 0 ? r.Diagnostics[0].Message : "no diagnostics");
        return r.Program!;
    }

    /// <summary>Exec from <paramref name="start"/>; the text is <c>[start,end,[g1s,g1e]|null,...]</c> in code units, <c>null</c> for no match, <c>LIMIT</c> for a limit.</summary>
    internal static string Render(RegExpProgram p, string input, int start = 0, bool sticky = false, FuelMeter? fuel = null)
    {
        var r = p.Exec(input, start, sticky, fuel);
        if (r.Outcome == RegExpOutcome.LimitExceeded)
        {
            return "LIMIT";
        }

        if (r.Match is null)
        {
            return "null";
        }

        var sb = new StringBuilder("[");
        for (int g = 0; g <= r.Match.GroupCount; g++)
        {
            if (g > 0)
            {
                sb.Append(',');
            }

            if (r.Match.IsGroupMatched(g))
            {
                sb.Append('[').Append(r.Match.GetGroupStart(g)).Append(',').Append(r.Match.GetGroupEnd(g)).Append(']');
            }
            else
            {
                sb.Append("null");
            }
        }

        return sb.Append(']').ToString();
    }

    internal static string Run(string pattern, string flags, string input, int start = 0)
    {
        var r = RegExpProgram.Compile(pattern, flags);
        return r.Program is null ? "ERR " + r.Diagnostics[0].Message : Render(r.Program, input, start, sticky: false);
    }
}
