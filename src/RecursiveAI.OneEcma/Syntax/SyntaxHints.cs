using System.Text.RegularExpressions;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

/// <summary>
/// Adds explanations to syntax errors that come from a recognisable mistake: code written in another
/// language, typographic quotes, a stray closing bracket. A hint may move the primary span to the
/// construct that is wrong (V8 reports the first token it could not accept, which is often a symptom),
/// and adds a label, a help line and sometimes a note. The headline is never changed.
/// </summary>
internal static partial class SyntaxHints
{
    internal static void Apply(IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (Diagnostic d in diagnostics)
        {
            if (d.Severity != Severity.Error)
            {
                continue;
            }

            // These explain constructs the parser already labelled, so they run whatever the parser said.
            switch (d.Code)
            {
                case "missing-paren-after-args":
                    HintMissingParen(d);
                    continue;
                case "redeclaration":
                    HintRedeclaration(d);
                    continue;
                case "missing-catch-or-finally":
                    HintTryWithoutCatch(d);
                    continue;
            }

            if (d.PrimaryLabel.Length > 0)
            {
                continue;
            }

            TryHint(d);
        }

        NoteFollowingErrors(diagnostics);
    }

    private static string LineText(Diagnostic d, int offset) => d.Source.GetLineText(d.Source.GetPosition(offset).Line).ToString();

    private static Diagnostic? TryHint(Diagnostic d)
    {
        switch (d.Code)
        {
            case "unexpected-token":
            case "invalid-token":
                return HintToken(d);
            case "unexpected-end":
                HintEndOfInput(d);
                return d;
            case "illegal-return":
                HintIllegalReturn(d);
                return d;
            case "await-outside-async":
                d.PrimaryLabel = "`await` is only allowed in async functions and modules";
                d.Help.Add("wrap the code in `async function main() { ... }` and call it, or run it as a module");
                d.Notes.Add(NotRepaired + "wrapping changes the scope of every declaration");
                AddFix(d, "run the code inside an async function", new TextEdit(0, 0, "(async () => {\n"), new TextEdit(d.Source.Text.Length, 0, "\n})();"));
                return d;
            case "import-outside-module":
                d.PrimaryLabel = "`import` declarations need module mode";
                d.Help.Add("run the code as a module, or use the host-provided functions instead of importing");
                d.Notes.Add(NotRepaired + "module mode changes strictness and `this`");
                return d;
            case "missing-initializer" when d.Headline.Contains("const", StringComparison.Ordinal):
                HintConstWithoutValue(d);
                return d;
            case "invalid-assignment-target":
                HintLiteralAssignTarget(d);
                return d;
            case "unterminated-template":
                if (!HintFenceAtEnd(d))
                {
                    d.PrimaryLabel = "this template literal is never closed";
                }

                return d;
            default:
                return null;
        }
    }

    private static void HintEndOfInput(Diagnostic d)
    {
        string text = d.Source.Text;
        (char Open, int Start)? open = LastUnclosedOpener(text);
        if (open is not { } o)
        {
            return;
        }

        char close = o.Open switch { '{' => '}', '(' => ')', _ => ']' };
        d.PrimaryLabel = "input ends here with `" + o.Open + "` still open";
        int lineStart = text.LastIndexOfAny(['\n', '\r'], Math.Max(0, o.Start - 1)) + 1;
        while (lineStart < o.Start && char.IsWhiteSpace(text[lineStart]))
        {
            lineStart++;
        }

        d.SecondaryLabels.Add(new Label(new Span(lineStart, o.Start + 1), "this `" + o.Open + "` is never closed"));
        d.Help.Add("add `" + close + "` at the end of the file");
    }

    /// <summary>Finds the innermost bracket still open at the end of the text, skipping strings, comments and template text.</summary>
    private static (char Open, int Start)? LastUnclosedOpener(string text)
    {
        var stack = new List<(char C, int At)>();
        var lexer = new Lexer(text, module: false, (_, _, _, _) => { });
        int guard = 0;
        while (guard++ < 5_000_000)
        {
            Token t = lexer.Next();
            switch (t.Type)
            {
                case TokenType.EndOfInput:
                    return stack.Count > 0 && stack[^1].C != '$' ? (stack[^1].C, stack[^1].At) : null;
                case TokenType.LBrace:
                    stack.Add(('{', t.Start));
                    break;
                case TokenType.LParen:
                    stack.Add(('(', t.Start));
                    break;
                case TokenType.LBracket:
                    stack.Add(('[', t.Start));
                    break;
                case TokenType.RParen:
                case TokenType.RBracket:
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    break;
                case TokenType.RBrace:
                    if (stack.Count > 0)
                    {
                        (char c, _) = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        if (c == '$')
                        {
                            Token cont = lexer.RescanTemplateContinuation(t);
                            if ((cont.Flags & TokenFlags.TemplateTail) == 0)
                            {
                                stack.Add(('$', cont.Start));
                            }
                        }
                    }

                    break;
                case TokenType.Template when (t.Flags & TokenFlags.TemplateTail) == 0:
                    stack.Add(('$', t.Start));
                    break;
                case TokenType.Slash or TokenType.SlashAssign:
                    break;
            }
        }

        return null;
    }

    private static Diagnostic? HintToken(Diagnostic d)
    {
        SourceText src = d.Source;
        string text = src.Text;
        int s = d.PrimarySpan.Start;
        string line = LineText(d, s);
        int lineStart = src.GetLineStart(src.GetPosition(s).Line);
        int col = s - lineStart;
        string before = col <= line.Length ? line[..col] : line;

        if (d.Code == "invalid-token" && s < text.Length)
        {
            if (text[s] is '"' or '\'')
            {
                return HintUnterminatedString(d, text, s);
            }

            if (text[s] == '\\' && HintBackslash(d, text, s) is { } hinted)
            {
                return hinted;
            }
        }

        if (d.Code == "unexpected-token" && d.Headline.StartsWith("Unexpected identifier", StringComparison.Ordinal) && HintTemplateBacktick(d))
        {
            return d;
        }

        // Typographic quotes used as delimiters.
        if (s < text.Length && text[s] is '\u201C' or '\u2018')
        {
            return HintSmartQuotes(d, text, s, lineStart, line);
        }

        // `elif (cond) {`: the error is at the brace after the condition.
        Match m = ElifBefore().Match(before);
        if (m.Success)
        {
            int at = lineStart + m.Groups[1].Index;
            Relocate(d, new Span(at, at + 4), "`elif` is Python; JavaScript writes `else if`");
            d.Help.Add("replace `elif` with `else if`");
            return d;
        }

        // `def name(args):`
        m = DefBefore().Match(before);
        if (m.Success)
        {
            int at = lineStart + m.Groups[1].Index;
            Relocate(d, new Span(at, at + 3), "`def` is Python; JavaScript declares functions with `function`");
            string name = Regex.Match(line[col..], @"^[A-Za-z_$][\w$]*").Value;
            Match args = Regex.Match(line[m.Groups[1].Index..], @"^def\s+\w+\s*(\([^)]*\))\s*:\s*$");
            if (args.Success)
            {
                string body = NextLine(src, s);
                d.Help.Add(body.Length > 0
                    ? "write `function " + name + args.Groups[1].Value + " { " + body.TrimEnd(';') + "; }`"
                    : "write `function " + name + args.Groups[1].Value + " { ... }`");
                d.Notes.Add(NotRepaired + "the body needs braces and the colon form has no JavaScript equivalent");
                if (body.Length > 0)
                {
                    int bodyEnd = LineEndOffset(src, text.IndexOf(body, s, StringComparison.Ordinal));
                    AddFix(d, "write the function in JavaScript", new TextEdit(lineStart + m.Groups[1].Index, bodyEnd - (lineStart + m.Groups[1].Index), "function " + name + args.Groups[1].Value + " { " + body.TrimEnd(';') + "; }"));
                }
            }

            return d;
        }

        // `a and b`, `a or b`
        string word = s + 3 <= text.Length ? text.Substring(s, Math.Min(3, text.Length - s)) : "";
        if (d.Headline is "Unexpected identifier 'and'" or "Unexpected identifier 'or'")
        {
            string py = d.Headline.Contains("'and'", StringComparison.Ordinal) ? "and" : "or";
            string js = py == "and" ? "&&" : "||";
            d.PrimaryLabel = "`" + py + "` is Python; JavaScript writes `" + js + "`";
            d.Help.Add("replace `" + py + "` with `" + js + "`");
            return d;
        }

        _ = word;

        // `interface Item { ... }`
        if (d.Headline.StartsWith("Unexpected identifier", StringComparison.Ordinal) && InterfaceBefore().IsMatch(before))
        {
            int at = lineStart + before.LastIndexOf("interface", StringComparison.Ordinal);
            Relocate(d, new Span(at, at + 9), "`interface` declarations are TypeScript");
            d.Help.Add("delete the interface declaration");
            return d;
        }

        // `value as Type`
        if (d.Headline == "Unexpected identifier 'as'")
        {
            // A cast names a short type; look only a little way ahead so that one very long line costs the same for every error on it.
            Match asMatch = AsCast().Match(line.Substring(col, Math.Min(line.Length - col, 200)));
            d.PrimaryLabel = "`" + (asMatch.Success ? asMatch.Value : "as ...") + "` is a TypeScript cast";
            if (asMatch.Success)
            {
                d.Help.Add("remove ` " + asMatch.Value + "`");
            }

            return d;
        }

        // `for i in range(n):`
        if (d.Headline.StartsWith("Unexpected identifier", StringComparison.Ordinal))
        {
            m = PyFor().Match(line);
            if (m.Success && lineStart + m.Index + 3 <= s)
            {
                string v = m.Groups[1].Value;
                string n = m.Groups[2].Value;
                string loop = "for (let " + v + " = 0; " + v + " < " + n + "; " + v + "++)";
                int at = lineStart + m.Index;
                Relocate(d, new Span(at, at + m.Length - 1), "Python loop syntax; JavaScript writes `" + loop + "`");
                string body = NextLine(src, s);
                d.Help.Add("write `" + loop + (body.Length > 0 ? " { " + body.TrimEnd(';') + "; }" : " { ... }") + "`");
                d.Notes.Add(NotRepaired + "no mechanical rewrite keeps the meaning");
                if (body.Length > 0)
                {
                    int bodyEnd = LineEndOffset(src, text.IndexOf(body, s, StringComparison.Ordinal));
                    AddFix(d, "write the loop in JavaScript", new TextEdit(at, bodyEnd - at, loop + " { " + body.TrimEnd(';') + "; }"));
                }

                return d;
            }
        }

        // `&lt;`, `&gt;`, `&amp;`, `&quot;` pasted from HTML.
        m = HtmlEntity().Match(line);
        if (m.Success && s - lineStart >= m.Index && s - lineStart <= m.Index + m.Length + 8)
        {
            int at = lineStart + m.Index;
            Relocate(d, new Span(at, at + m.Length), "`" + m.Value + "` is an HTML entity, not `" + DecodeEntities(m.Value) + "`");
            int open = line.IndexOf('(', StringComparison.Ordinal);
            int close = line.LastIndexOf(')');
            string inner = open >= 0 && close > open ? line[(open + 1)..close] : line.Trim();
            d.Help.Add("decode the entities: `" + DecodeEntities(inner) + "`");
            return d;
        }

        // `function add(a: number)`: a type annotation.
        if (d.Headline == "Unexpected token ':'" && InsideParameterList(before))
        {
            d.PrimaryLabel = "type annotations are TypeScript, not JavaScript";
            Match sig = Regex.Match(line, @"^(\s*(?:async\s+)?function\s*\*?\s*[\w$]*\s*)\(([^)]*)\)");
            if (sig.Success)
            {
                string cleaned = Regex.Replace(sig.Groups[2].Value, @"\s*:\s*[A-Za-z_$][\w$<>\[\]|. ]*?(?=\s*,|\s*$)", "");
                d.Help.Add("remove the annotations: `" + sig.Groups[1].Value.Trim() + "(" + cleaned + ")`");
            }

            return d;
        }

        // A stray closing bracket.
        if (d.Headline is "Unexpected token ')'" or "Unexpected token ']'" or "Unexpected token '}'")
        {
            return HintStrayCloser(d, d.Headline[18]);
        }

        // `,` between class members.
        if (d.Headline == "Unexpected token ','" && s > 0 && InClassBody(text, s))
        {
            d.PrimaryLabel = "class members are not separated by commas";
            d.Help.Add("remove the `,`");
            return d;
        }

        return null;
    }

    private static bool InsideParameterList(string before)
    {
        int open = before.LastIndexOf('(');
        return open >= 0 && before.IndexOf(')', open) < 0 && Regex.IsMatch(before[..open], @"function\s*\*?\s*[\w$]*\s*$");
    }

    private static bool InClassBody(string text, int offset)
    {
        int depth = 0;
        for (int i = offset - 1; i >= 0; i--)
        {
            if (text[i] == '}')
            {
                depth++;
            }
            else if (text[i] == '{')
            {
                if (depth == 0)
                {
                    return Regex.IsMatch(text[Math.Max(0, i - 300)..i], @"\bclass\b[^{};]*$");
                }

                depth--;
            }
        }

        return false;
    }

    private static string NextLine(SourceText src, int offset)
    {
        int line = src.GetPosition(offset).Line + 1;
        return line <= src.LineCount ? src.GetLineText(line).ToString().Trim() : "";
    }

    private static void Relocate(Diagnostic d, Span span, string label)
    {
        d.MovePrimary(span);
        d.PrimaryLabel = label;
    }

    private static string DecodeEntities(string s) =>
        s.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal)
         .Replace("&quot;", "\"", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);

    private static Diagnostic HintSmartQuotes(Diagnostic d, string text, int s, int lineStart, string line)
    {
        char openQ = text[s];
        char closeQ = openQ == '\u201C' ? '\u201D' : '\u2019';
        char straight = openQ == '\u201C' ? '"' : '\'';
        int end = text.IndexOf(closeQ, s + 1);
        int lineEnd = lineStart + line.Length;
        if (end < 0 || end > lineEnd)
        {
            return d;
        }

        Relocate(d, new Span(s, end + 1),
            "these are typographic quotes (U+" + ((int)openQ).ToString("X4", System.Globalization.NumberFormatInfo.InvariantInfo) + ", U+"
            + ((int)closeQ).ToString("X4", System.Globalization.NumberFormatInfo.InvariantInfo) + "), not `" + straight + "`");
        string fixedLine = (text[lineStart..s] + straight + text.Substring(s + 1, end - s - 1) + straight + text[(end + 1)..lineEnd]).Trim();
        d.Help.Add("use straight quotes: `" + fixedLine + "`");
        d.Fixes.Add(new SuggestedFix("replace the typographic quotes with straight quotes",
            [new TextEdit(s, 1, straight.ToString()), new TextEdit(end, 1, straight.ToString())]));
        return d;
    }

    [GeneratedRegex(@"(?:^|\})\s*(elif)\s*\([^)]*\)\s*$")]
    private static partial Regex ElifBefore();

    [GeneratedRegex(@"^\s*(def)\s+$")]
    private static partial Regex DefBefore();

    [GeneratedRegex(@"(?:^|\s)interface\s*$")]
    private static partial Regex InterfaceBefore();

    [GeneratedRegex(@"\bfor\s+([A-Za-z_$][\w$]*)\s+in\s+range\(\s*(\w+)\s*\)\s*:")]
    private static partial Regex PyFor();

    [GeneratedRegex(@"^as\s+[A-Za-z_$][\w$<>\[\]|. ]{0,80}?(?=\s*[;,)]|\s*$)")]
    private static partial Regex AsCast();

    [GeneratedRegex(@"&(?:lt|gt|amp|quot);")]
    private static partial Regex HtmlEntity();
}
