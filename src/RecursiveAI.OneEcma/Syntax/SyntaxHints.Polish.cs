using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

// Explanations for the mistakes the repair table lists. The ones the repair pass can fix on its own are labelled
// the same way; the ones it must leave to the author carry a "not repaired automatically" note and, where a
// mechanical edit exists, a suggested fix (never applied by the parser).
internal static partial class SyntaxHints
{
    private const string NotRepaired = "not repaired automatically: ";

    private static string TrimmedLine(SourceText src, int offset) =>
        src.GetLineText(src.GetPosition(offset).Line).ToString().Trim();

    private static int LineEndOffset(SourceText src, int offset)
    {
        int line = src.GetPosition(offset).Line;
        return src.GetLineStart(line) + src.GetLineText(line).Length;
    }

    private static string Q(string s) => "`" + s + "`";

    private static void AddFix(Diagnostic d, string description, params TextEdit[] edits) =>
        d.Fixes.Add(new SuggestedFix(description, edits));

    // ---- stray closing bracket ---------------------------------------------------------------------

    private static Diagnostic HintStrayCloser(Diagnostic d, char c)
    {
        List<Token> toks = TokenScan.Of(d.Source);
        int s = d.PrimarySpan.Start;
        int i = TokenScan.IndexAtOrAfter(toks, s);
        d.PrimaryLabel = "this " + Q(c.ToString()) + " closes nothing";
        d.Help.Add("remove the extra " + Q(c.ToString()));
        AddFix(d, "remove the extra " + Q(c.ToString()), new TextEdit(s, 1, ""));
        if (i == 0)
        {
            return d;
        }

        int p = i - 1;
        TokenType want = c switch { ')' => TokenType.RParen, ']' => TokenType.RBracket, _ => TokenType.RBrace };
        int[] match = TokenScan.MatchOf(d.Source);
        if (toks[p].Type != want || match[p] < 0)
        {
            return d;
        }

        int open = match[p];
        int first = open;
        while (first > 0 && toks[first - 1].Type is TokenType.Identifier or TokenType.Dot or TokenType.QuestionDot or TokenType.This or TokenType.PrivateName)
        {
            first--;
        }

        bool callee = first < open;
        var span = new Span(toks[first].Start, toks[p].Start);
        switch (c)
        {
            case ')' when callee:
                d.PrimaryLabel = "this `)` closes nothing: the call already ended";
                d.WithLabel(span, "this call is already complete after the first `)`");
                break;
            case ')':
                d.WithLabel(span, "the parentheses opened here are already closed");
                break;
            case ']':
                d.WithLabel(span, callee ? "the index opened here is already closed" : "the array opened here is already closed");
                break;
            default:
                d.WithLabel(span, "the braces opened here are already closed");
                break;
        }

        return d;
    }

    // ---- strings -----------------------------------------------------------------------------------

    /// <summary>An unterminated string literal (S-STRING): the lexer stopped at the end of the line.</summary>
    private static Diagnostic HintUnterminatedString(Diagnostic d, string text, int s)
    {
        SourceText src = d.Source;
        char quote = text[s];
        int lineEnd = LineEndOffset(src, s);
        int line = src.GetPosition(s).Line;
        // A line break inside the string leaves a lone closing quote on the next line; a string that merely lacks its
        // quote is followed by lines with quotes in pairs.
        bool lineBreakInString = line < src.LineCount && src.GetLineText(line + 1).Count(quote) % 2 == 1;
        if (lineBreakInString)
        {
            int terminatorLength = lineEnd < text.Length && text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1;
            d.PrimaryLabel = "a string literal cannot contain a line break";
            d.Help.Add("write the break as `\\n` inside the string, or use a template literal with backticks");
            d.Notes.Add(NotRepaired + "both fixes give a different string than a reader might expect");
            AddFix(d, "write the line break as `\\n`", new TextEdit(lineEnd, Math.Min(terminatorLength, text.Length - lineEnd), "\\n"));
            int closing = text.IndexOf(quote, src.GetLineStart(line + 1));
            if (closing >= 0)
            {
                AddFix(d, "use a template literal", new TextEdit(s, 1, "`"), new TextEdit(closing, 1, "`"));
            }

            return d;
        }

        // Put the closing quote before the closers and the semicolon that end the statement.
        int at = lineEnd;
        while (at > s + 1 && text[at - 1] is ')' or ']' or '}' or ';' or ',' or ' ' or '\t')
        {
            at--;
        }

        string trailing = text[at..lineEnd];
        char closer = trailing.FirstOrDefault(ch => ch is ')' or ']' or '}');
        string lineText = TrimmedLine(src, s);
        int lineStart = src.GetLineStart(line);
        string closed = (text[lineStart..at] + quote + text[at..lineEnd]).Trim();
        d.PrimaryLabel = "this string is not closed before the end of the line";
        d.Help.Add("close the string: " + Q(closed));
        d.Notes.Add(NotRepaired + (closer == '\0'
            ? "the string may end anywhere on the line"
            : "the closing " + Q(quote.ToString()) + " could go before or after " + Q(closer.ToString())));
        AddFix(d, "close the string before the end of the statement", new TextEdit(at, 0, quote.ToString()));
        _ = lineText;
        return d;
    }

    // ---- backslashes outside strings (JSON-escaped code) -------------------------------------------

    private static Diagnostic? HintBackslash(Diagnostic d, string text, int s)
    {
        if (s + 1 >= text.Length)
        {
            return null;
        }

        SourceText src = d.Source;
        char next = text[s + 1];
        if (next is '"' or '\'')
        {
            int lineStart = src.GetLineStart(src.GetPosition(s).Line);
            int lineEnd = LineEndOffset(src, s);
            List<int> at = RepairSites.EscapedQuotes(text, s, lineEnd);
            var sb = new StringBuilder(text[lineStart..lineEnd]);
            for (int k = at.Count - 1; k >= 0; k--)
            {
                sb.Remove(at[k] - lineStart, 1);
            }

            d.PrimaryLabel = "a backslash outside a string starts nothing valid";
            d.Help.Add("remove the backslashes: " + Q(sb.ToString().Trim()));
            AddFix(d, "remove the backslashes that escape quotes", at.Select(k => new TextEdit(k, 1, "")).ToArray());
            return d;
        }

        if (next == 'n')
        {
            d.PrimaryLabel = "a literal `\\n` between statements; this was meant to be a line break";
            d.Help.Add("put each statement on its own line");
            List<int> at = RepairSites.LiteralNewlines(text, TokenScan.Of(src));
            if (at.Count > 0)
            {
                AddFix(d, "turn each literal `\\n` into a line break", at.Select(k => new TextEdit(k, 2, "\n")).ToArray());
            }

            return d;
        }

        return null;
    }

    // ---- Python f-strings --------------------------------------------------------------------------

    private static bool HintFString(Diagnostic d)
    {
        string text = d.Source.Text;
        int s = d.PrimarySpan.Start;
        if (s + 1 >= text.Length || text[s] is not ('f' or 'F') || text[s + 1] is not ('"' or '\'')
            || (s > 0 && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] is '_' or '$')))
        {
            return false;
        }

        List<Token> toks = TokenScan.Of(d.Source);
        int i = TokenScan.IndexAtOrAfter(toks, s + 1);
        if (toks[i].Type != TokenType.String || toks[i].Start != s + 1 || (toks[i].Flags & TokenFlags.Unterminated) != 0 || toks[i].End - toks[i].Start < 2)
        {
            return false;
        }

        Token str = toks[i];
        char quote = text[s + 1];
        string body = text.Substring(str.Start + 1, Math.Max(0, str.End - str.Start - 2));
        string template = "`" + Regex.Replace(body.Replace("`", "\\`", StringComparison.Ordinal), @"\{([^{}]*)\}", "${$1}") + "`";
        Match placeholder = Regex.Match(body, @"\{([^{}]*)\}");
        d.SecondaryLabels.Clear();
        d.MovePrimary(new Span(s, s + 1));
        d.PrimaryLabel = "an " + Q(text[s] + quote.ToString() + "..." + quote) + " string is Python; this string follows the identifier " + Q(text[s].ToString());
        int lineStart = d.Source.GetLineStart(d.Source.GetPosition(s).Line);
        // A string may contain U+2028, which ends a line for the line table, so take the line of its last character.
        int lineEnd = Math.Max(LineEndOffset(d.Source, s), LineEndOffset(d.Source, Math.Max(s, str.End - 1)));
        string line = text[lineStart..s] + template + text[str.End..lineEnd];
        d.Help.Clear();
        d.Help.Add("use a template literal: " + line.Trim());
        d.Notes.Add(NotRepaired + (placeholder.Success
            ? Q("{" + placeholder.Groups[1].Value + "}") + " must become " + Q("${" + placeholder.Groups[1].Value + "}") + " and the quotes must change"
            : "the quotes must change"));
        AddFix(d, "use a template literal", new TextEdit(s, str.End - s, template));
        return true;
    }

    // ---- backtick inside a template literal --------------------------------------------------------

    private static bool HintTemplateBacktick(Diagnostic d)
    {
        string text = d.Source.Text;
        int s = d.PrimarySpan.Start;
        if (s == 0 || text[s - 1] != '`')
        {
            return false;
        }

        List<Token> toks = TokenScan.Of(d.Source);
        int i = TokenScan.IndexAtOrAfter(toks, s);
        if (i == 0 || toks[i - 1].Type != TokenType.Template || toks[i - 1].End != s || (toks[i - 1].Flags & TokenFlags.TemplateTail) == 0)
        {
            return false;
        }

        Token tmpl = toks[i - 1];
        // The template that "ended early" is the one whose opening backtick starts the earlier chunk.
        d.PrimaryLabel = "the template literal ended at the backtick before this word";
        d.WithLabel(new Span(tmpl.Start, tmpl.Start + 1), "the template literal starts here");
        d.Help.Add("escape the inner backticks as \\` or remove them");
        d.Notes.Add(NotRepaired + "the closing backtick could also be the one that is missing");
        var edits = new List<TextEdit> { new TextEdit(s - 1, 0, "\\") };
        int nextTick = text.IndexOf('`', s);
        if (nextTick >= 0)
        {
            edits.Add(new TextEdit(nextTick, 0, "\\"));
        }

        AddFix(d, "escape the inner backticks", edits.ToArray());
        return true;
    }

    // ---- declarations ------------------------------------------------------------------------------

    /// <summary>End offset of the declarator that starts with the identifier ending at <paramref name="identEnd"/>: through its initializer, if any.</summary>
    private static int DeclaratorEnd(SourceText src, int identEnd)
    {
        List<Token> toks = TokenScan.Of(src);
        int i = TokenScan.IndexAtOrAfter(toks, identEnd);
        if (toks[i].Type != TokenType.Assign)
        {
            return identEnd;
        }

        int depth = 0;
        int end = toks[i].End;
        for (i++; i < toks.Count; i++)
        {
            Token t = toks[i];
            if (t.Type == TokenType.EndOfInput || (depth == 0 && (t.Type is TokenType.Comma or TokenType.Semicolon || (t.NewlineBefore && t.Type is not (TokenType.Dot or TokenType.QuestionDot or TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.AmpAmp or TokenType.PipePipe or TokenType.Question or TokenType.Colon)))))
            {
                break;
            }

            if (t.Type is TokenType.LParen or TokenType.LBracket or TokenType.LBrace)
            {
                depth++;
            }
            else if (t.Type is TokenType.RParen or TokenType.RBracket or TokenType.RBrace)
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }

            end = t.End;
        }

        return end;
    }

    private static string DeclarationKeyword(SourceText src, int identStart)
    {
        List<Token> toks = TokenScan.Of(src);
        int i = TokenScan.IndexAtOrAfter(toks, identStart);
        if (i > 0 && toks[i - 1].Type is TokenType.Const or TokenType.Var)
        {
            return toks[i - 1].Type == TokenType.Const ? "const" : "var";
        }

        if (i > 0 && toks[i - 1].Type == TokenType.Identifier && toks[i - 1].Text == "let")
        {
            return "let";
        }

        return "";
    }

    private static void HintRedeclaration(Diagnostic d)
    {
        SourceText src = d.Source;
        string keyword = DeclarationKeyword(src, d.PrimarySpan.Start);
        if (keyword.Length == 0)
        {
            return;
        }

        d.MovePrimary(new Span(d.PrimarySpan.Start, DeclaratorEnd(src, d.PrimarySpan.End)));
        for (int k = 0; k < d.SecondaryLabels.Count; k++)
        {
            Label l = d.SecondaryLabels[k];
            if (DeclarationKeyword(src, l.Span.Start).Length > 0)
            {
                d.SecondaryLabels[k] = l with { Span = new Span(l.Span.Start, DeclaratorEnd(src, l.Span.End)) };
            }
        }

        string name = src.Text[d.PrimarySpan.Start..Math.Min(d.PrimarySpan.End, src.Text.Length)];
        bool hasValue = name.Contains('=', StringComparison.Ordinal);
        string ident = hasValue ? name[..name.IndexOf('=', StringComparison.Ordinal)].Trim() : name.Trim();
        d.Help.Add(hasValue
            ? "rename one of them, or assign with " + Q(name) + " if " + Q(ident) + " should change"
            : "rename one of them");
        d.Notes.Add(NotRepaired + (keyword == "const"
            ? "removing `const` turns a declaration into an error for `const`"
            : "every fix changes what the program means"));
        if (keyword is "let" or "var")
        {
            int kw = src.Text.LastIndexOf(keyword, d.PrimarySpan.Start, StringComparison.Ordinal);
            if (kw >= 0)
            {
                AddFix(d, "assign to the existing variable instead of declaring it again", new TextEdit(kw, d.PrimarySpan.Start - kw, ""));
            }
        }
    }

    private static void HintConstWithoutValue(Diagnostic d)
    {
        SourceText src = d.Source;
        string text = src.Text;
        string name = text[d.PrimarySpan.Start..d.PrimarySpan.End];
        int end = d.PrimarySpan.End;
        if (end < text.Length && text[end] == ';')
        {
            d.MovePrimary(new Span(d.PrimarySpan.Start, end + 1));
        }

        d.PrimaryLabel = "`const` needs a value here";
        if (!Regex.IsMatch(name, @"^[A-Za-z_$][\w$]*$"))
        {
            d.Help.Add("give the declaration a value with `= ...`");
            return;
        }

        Match later = Regex.Match(text[end..], @"(?<![\w$.])" + Regex.Escape(name) + @"\s*=(?!=)\s*([^;\n]+?)\s*;");
        string value = later.Success ? later.Groups[1].Value : "<value>";
        d.Help.Add("write " + Q("const " + name + " = " + value + ";") + ", or use " + Q("let " + name + ";") + " if it is assigned later");
    }

    // ---- statements --------------------------------------------------------------------------------

    private static void HintTryWithoutCatch(Diagnostic d)
    {
        d.PrimaryLabel = "the `try` block ends here with no `catch` or `finally`";
        d.Help.Add("add `catch (err) { ... }` after the block");
        int at = d.PrimarySpan.End;
        AddFix(d, "add a catch clause", new TextEdit(at, 0, " catch (err) {\n}"));
    }

    private static void HintIllegalReturn(Diagnostic d)
    {
        SourceText src = d.Source;
        d.PrimaryLabel = "`return` is only allowed inside a function";
        int s = d.PrimarySpan.Start;
        string statement = Regex.Match(src.Text[s..LineEndOffset(src, s)], @"^return\b[^;]*;?").Value.Trim();
        d.Help.Add("put the code in a function, or replace " + Q(statement.Length > 0 ? statement : "return x;") + " with the value as the last expression");
        d.Notes.Add(NotRepaired + "the intended result depends on the host");
    }

    private static void HintLiteralAssignTarget(Diagnostic d)
    {
        SourceText src = d.Source;
        List<Token> toks = TokenScan.Of(src);
        int i = TokenScan.IndexAtOrAfter(toks, d.PrimarySpan.Start);
        if (toks[i].Type is not (TokenType.Number or TokenType.String or TokenType.True or TokenType.False or TokenType.Null)
            || toks[i + 1].Type != TokenType.Assign
            || toks[i + 2].Type != TokenType.Identifier)
        {
            return;
        }

        string left = src.Text[toks[i].Start..toks[i].End];
        string right = src.Text[toks[i + 2].Start..toks[i + 2].End];
        d.MovePrimary(new Span(toks[i].Start, toks[i + 2].End));
        d.PrimaryLabel = "a literal cannot be assigned to";
        bool isConst = false;
        for (int k = 0; k + 1 < i; k++)
        {
            if (toks[k].Type == TokenType.Const && toks[k + 1].Type == TokenType.Identifier && toks[k + 1].Text == right)
            {
                isConst = true;
                break;
            }
        }

        d.Help.Add("swap the sides: " + Q(right + " = " + left) + (isConst ? " (if " + Q(right) + " were not `const`)" : ""));
        AddFix(d, "swap the sides", new TextEdit(toks[i].Start, toks[i + 2].End - toks[i].Start, right + " = " + left));
    }

    private static bool HintFenceAtEnd(Diagnostic d)
    {
        SourceText src = d.Source;
        string text = src.Text;
        Span last = new(d.PrimarySpan.Start, d.PrimarySpan.End);
        string line = text[last.Start..last.End];
        if (!Regex.IsMatch(line, @"^```[A-Za-z0-9_-]*$"))
        {
            return false;
        }

        d.MovePrimary(new Span(last.Start, last.Start + 3));
        d.PrimaryLabel = "a markdown fence: this backtick starts a template literal that never ends";
        d.Help.Clear();
        d.Help.Add("remove the line that contains only ```");
        d.SecondaryLabels.Clear();
        AddFix(d, "remove the fence line", new TextEdit(src.GetLineStart(src.GetPosition(last.Start).Line), line.Length, ""));
        return true;
    }

    // ---- notes shared by several explanations ------------------------------------------------------

    private static void HintMissingParen(Diagnostic d)
    {
        if (HintFString(d))
        {
            return;
        }

        if (d.Help.Count > 0 && d.Help[0].StartsWith("add `)", StringComparison.Ordinal) && d.Help[0].Contains("` before `", StringComparison.Ordinal))
        {
            d.Notes.Add(ClosesBeforeLineEndingSemicolon(d)
                ? "repair is off by default; with it on, R012 closes it before the `;` that ends the line, though `)` could also belong after a later argument"
                : NotRepaired + "`)` could also belong after a later argument");
        }

        foreach (RecoveryAction r in d.Recovery)
        {
            if (r.Kind == RecoveryKind.InsertedToken && r.Text.Length > 0 && r.Text.All(ch => ch == ')'))
            {
                AddFix(d, "add the missing `" + r.Text + "`", new TextEdit(r.Span.Start, 0, r.Text));
                break;
            }
        }
    }

    /// <summary>
    /// True when the error is the one repair R012 handles: the argument list was opened on the same line as the `;` that ends the
    /// line, so the closer can only go right before the `;`. Mirrors that rule's guard; the repair itself also checks that the result parses.
    /// </summary>
    private static bool ClosesBeforeLineEndingSemicolon(Diagnostic d)
    {
        string text = d.Source.Text;
        // The parser points at the last argument or at the `;` itself.
        int semi = d.PrimarySpan.End - 1;
        if (semi >= 0 && semi < text.Length && text[semi] != ';')
        {
            semi = d.PrimarySpan.End;
            while (semi < text.Length && (text[semi] == ' ' || text[semi] == '	'))
            {
                semi++;
            }
        }

        if (semi < 0 || semi >= text.Length || text[semi] != ';' || d.SecondaryLabels.Count == 0)
        {
            return false;
        }

        int line = d.Source.GetPosition(semi).Line;
        foreach (Label l in d.SecondaryLabels)
        {
            if (d.Source.GetPosition(l.Span.Start).Line != line)
            {
                return false;
            }
        }

        int lineEnd = d.Source.GetLineStart(line) + d.Source.GetLineText(line).Length;
        ReadOnlySpan<char> after = text.AsSpan(semi + 1, Math.Max(0, lineEnd - semi - 1)).Trim();
        return after.IsEmpty || after.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>When several errors were found, the first one says so, so a reader does not stop after it.</summary>
    internal static void NoteFollowingErrors(IReadOnlyList<Diagnostic> diagnostics)
    {
        int errors = 0;
        foreach (Diagnostic x in diagnostics)
        {
            if (x.Severity == Severity.Error)
            {
                errors++;
            }
        }

        if (errors < 2 || diagnostics[0].Severity != Severity.Error)
        {
            return;
        }

        string more = errors == 2
            ? "a second error follows: both are reported from one parse"
            : "another " + (errors - 1).ToString(NumberFormatInfo.InvariantInfo) + " errors follow: all are reported from one parse";
        diagnostics[0].Notes.Add(more);
    }
}
