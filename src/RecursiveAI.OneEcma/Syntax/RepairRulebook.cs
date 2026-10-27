using System.Text;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

/// <summary>
/// The rules of the repair table (docs/repair-table.md), each producing zero or more candidate fixes for the first
/// error. A rule only proposes; the repairer applies a candidate when it is the single plausible one and the
/// result parses.
/// </summary>
internal static class RepairRulebook
{
    internal static List<RepairCandidate> Generate(RepairContext c)
    {
        var all = new List<RepairCandidate>();
        ExtraCloser(c, all);
        UnclosedBeforeSemicolon(c, all);
        TruncatedBrackets(c, all);
        SmartQuotes(c, all);
        MissingComma(c, all);
        PythonWords(c, all);
        TypeScript(c, all);
        ClassMemberComma(c, all);
        EscapedQuotes(c, all);
        HtmlEntities(c, all);
        LiteralNewlines(c, all);
        return all;
    }

    private static string Slice(string text, int start, int end) => text.Substring(start, end - start);

    private static bool EndsValue(TokenType t) => t is TokenType.Identifier or TokenType.String or TokenType.Number or TokenType.BigInt
        or TokenType.Template or TokenType.RegExp or TokenType.RParen or TokenType.RBracket or TokenType.RBrace
        or TokenType.True or TokenType.False or TokenType.Null or TokenType.This or TokenType.PlusPlus or TokenType.MinusMinus;

    private static bool StartsValue(TokenType t) => t is TokenType.Identifier or TokenType.String or TokenType.Number or TokenType.BigInt
        or TokenType.Template or TokenType.RegExp or TokenType.LBracket or TokenType.LBrace or TokenType.LParen
        or TokenType.True or TokenType.False or TokenType.Null or TokenType.This or TokenType.PrivateName
        or TokenType.New or TokenType.Function or TokenType.Class or TokenType.Ellipsis or TokenType.Star;

    // ---- R001: extra closing delimiter ------------------------------------------------------------

    private static void ExtraCloser(RepairContext c, List<RepairCandidate> all)
    {
        Token t = c.At(c.FirstIndex);
        if (t.Type is not (TokenType.RParen or TokenType.RBracket or TokenType.RBrace) || c.Match[c.FirstIndex] >= 0)
        {
            return;
        }

        all.Add(new RepairCandidate("R001", "removed an extra closing delimiter", "removed", new Span(t.Start, t.End), [new TextEdit(t.Start, t.End - t.Start, "")]));

        // Several unmatched closers in a row (`)))`, `}}}`) are removed together: taking out one leaves the parser stopped at the next, which
        // would look like no progress. Each length is a candidate, so only the count that makes the source parse is plausible.
        var run = new List<Token> { t };
        for (int j = c.FirstIndex + 1; j < c.Tokens.Count && j < c.Match.Length && run.Count < 8; j++)
        {
            Token n = c.Tokens[j];
            if (n.Type is not (TokenType.RParen or TokenType.RBracket or TokenType.RBrace) || c.Match[j] >= 0)
            {
                break;
            }

            run.Add(n);
            all.Add(new RepairCandidate("R001", "removed " + run.Count.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " extra closing delimiters", "removed", new Span(t.Start, t.End),
                run.ConvertAll(x => new TextEdit(x.Start, x.End - x.Start, ""))));
        }

        // Rival fixes: a missing opener instead of a surplus closer. Wrapping a whole expression statement in
        // parentheses means the same thing, so only the readings that would change the program count.
        string opener = t.Type switch { TokenType.RParen => "(", TokenType.RBracket => "[", _ => "{" };
        var rivals = new List<int>();
        if (t.Type == TokenType.RBrace)
        {
            // A body without braces: `if (x)`, `else`, `=>`, `do`, `try`, `finally` directly followed by a statement.
            for (int j = c.FirstIndex - 1, seen = 0; j > 0 && seen < 80; j--, seen++)
            {
                if (c.Tokens[j - 1].Type is TokenType.RParen or TokenType.Else or TokenType.Arrow or TokenType.Do or TokenType.Try or TokenType.Finally
                    && c.Tokens[j].Type != TokenType.LBrace)
                {
                    rivals.Add(c.Tokens[j].Start);
                }
            }
        }
        else
        {
            int start = StatementStart(c, c.FirstIndex);
            Token first = c.Tokens[start];
            bool wrapMeansTheSame = t.Type == TokenType.RParen && first.Type is not (TokenType.LBrace or TokenType.Function or TokenType.Class);
            if (!wrapMeansTheSame)
            {
                rivals.Add(first.Start);
            }
        }

        foreach (int at in rivals)
        {
            all.Add(new RepairCandidate("R001", "", "", new Span(at, at), [new TextEdit(at, 0, opener)], alternative: true));
        }
    }

    // ---- R012: `)` or `]` missing right before a `;` at the end of a line -----------------------------

    private static void UnclosedBeforeSemicolon(RepairContext c, List<RepairCandidate> all)
    {
        // The parser notices the problem at the last argument or at the `;` itself.
        int i = c.At(c.FirstIndex).Type == TokenType.Semicolon ? c.FirstIndex : c.FirstIndex + 1;
        Token semi = c.At(i);
        if (semi.Type != TokenType.Semicolon || i == 0)
        {
            return;
        }

        // Only a `;` that ends its line: text after it could be more arguments or another statement, and then where the closer goes is a guess.
        int line = c.Source.GetPosition(semi.Start).Line;
        ReadOnlySpan<char> after = c.Text.AsSpan(semi.End, c.Source.GetLineStart(line) + c.Source.GetLineText(line).Length - semi.End).Trim();
        if (!after.IsEmpty && !after.StartsWith("//", StringComparison.Ordinal))
        {
            return;
        }

        // Every group opened on this line and still open at the `;` gets its closer, innermost first. A `for (` header legitimately holds `;`.
        var closers = new StringBuilder();
        for (int open = c.Enclosing(i); open >= 0; open = c.Enclosing(open))
        {
            Token o = c.Tokens[open];
            if (o.Type is not (TokenType.LParen or TokenType.LBracket) || c.Source.GetPosition(o.Start).Line != line || (open > 0 && o.Type == TokenType.LParen && c.Tokens[open - 1].Type == TokenType.For))
            {
                break;
            }

            closers.Append(o.Type == TokenType.LParen ? ')' : ']');
        }

        if (closers.Length == 0)
        {
            return;
        }

        // A group left open by an enclosing brace on an earlier line is not this rule's business.
        Token prev = c.Tokens[i - 1];
        all.Add(new RepairCandidate("R012", "closed " + (closers.Length == 1 ? "a group" : closers.Length.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " groups") + " before the `;`",
            "inserted `" + closers + "` before this", new Span(semi.Start, semi.End), [new TextEdit(prev.End, 0, closers.ToString())]));
    }

    private static int StatementStart(RepairContext c, int i)
    {
        int depth = 0;
        int j = i - 1;
        for (; j >= 0; j--)
        {
            TokenType t = c.Tokens[j].Type;
            if (t is TokenType.RParen or TokenType.RBracket or TokenType.RBrace)
            {
                depth++;
            }
            else if (t is TokenType.LParen or TokenType.LBracket or TokenType.LBrace)
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (t == TokenType.Semicolon && depth == 0)
            {
                break;
            }
        }

        return Math.Min(j + 1, c.Tokens.Count - 1);
    }

    // ---- R002: brackets missing at the end of the input -------------------------------------------

    private static void TruncatedBrackets(RepairContext c, List<RepairCandidate> all)
    {
        int contentEnd = c.Text.AsSpan().TrimEnd().Length;
        foreach (Diagnostic e in c.Errors)
        {
            bool atEnd = e.Code == "unexpected-end" || e.Recovery.Exists(r => r.Kind == RecoveryKind.InsertedToken && r.Span.Start >= contentEnd);
            if (!atEnd)
            {
                return;
            }
        }

        // Nothing is repaired inside an unfinished string or template.
        int openTemplates = 0;
        foreach (Token t in c.Tokens)
        {
            if ((t.Flags & TokenFlags.Unterminated) != 0)
            {
                return;
            }

            if (t.Type == TokenType.Template)
            {
                bool continues = c.Text[t.Start] == '}';
                bool tail = (t.Flags & TokenFlags.TemplateTail) != 0;
                if (!continues && !tail)
                {
                    openTemplates++;
                }
                else if (continues && tail)
                {
                    openTemplates--;
                }
            }
        }

        if (openTemplates != 0)
        {
            return;
        }

        var open = new List<int>();
        for (int i = 0; i < c.Tokens.Count; i++)
        {
            if (c.Tokens[i].Type is TokenType.LParen or TokenType.LBracket or TokenType.LBrace && c.Match[i] < 0)
            {
                open.Add(i);
            }
        }

        if (open.Count == 0)
        {
            return;
        }

        // Indentation is evidence. A later line indented no deeper than the opener's own line means the bracket was
        // left open in the middle of the file, and where it closes is a guess.
        foreach (int i in open)
        {
            if (c.Tokens[i].Type == TokenType.LBrace && DedentLine(c, i) >= 0)
            {
                return;
            }
        }

        var ins = new StringBuilder();
        var shown = new StringBuilder();
        for (int k = open.Count - 1; k >= 0; k--)
        {
            Token o = c.Tokens[open[k]];
            char close = o.Type switch { TokenType.LParen => ')', TokenType.LBracket => ']', _ => '}' };
            shown.Append(close);
            if (close == '}')
            {
                ins.Append('\n').Append(RepairSites.IndentOfLine(c.Source, o.Start)).Append('}');
            }
            else
            {
                ins.Append(close);
            }
        }

        int at = c.Text.AsSpan().TrimEnd().Length;
        ReadOnlySpan<char> lastLine = c.Text.AsSpan(0, at);
        int lineStart = lastLine.LastIndexOfAny('\n', '\r') + 1;
        while (lineStart < at && char.IsWhiteSpace(c.Text[lineStart]))
        {
            lineStart++;
        }

        all.Add(new RepairCandidate("R002", "closed brackets that were still open at the end of the input",
            "inserted `" + shown + "` after this line", new Span(lineStart, at), [new TextEdit(at, 0, ins.ToString())]));
    }

    /// <summary>The offset of the first later line indented no deeper than the line of the opener at token <paramref name="openIndex"/> (and not a closer), or -1.</summary>
    internal static int DedentLine(RepairContext c, int openIndex)
    {
        SourceText src = c.Source;
        int openLine = src.GetPosition(c.Tokens[openIndex].Start).Line;
        int indent = RepairSites.IndentOfLine(src, c.Tokens[openIndex].Start).Length;
        for (int line = openLine + 1; line <= src.LineCount; line++)
        {
            ReadOnlySpan<char> text = src.GetLineText(line);
            int n = 0;
            while (n < text.Length && text[n] is ' ' or '\t')
            {
                n++;
            }

            if (n == text.Length || text[n] is '}' or ')' or ']' || (text.Length - n >= 2 && text[n] == '/' && text[n + 1] is '/' or '*'))
            {
                continue;
            }

            if (n <= indent)
            {
                return src.GetLineStart(line);
            }
        }

        return -1;
    }

    // ---- R004: typographic quotes ------------------------------------------------------------------

    private static void SmartQuotes(RepairContext c, List<RepairCandidate> all)
    {
        const string doubles = "\u201C\u201D\u201E\u201F";
        const string singles = "\u2018\u2019\u201A\u201B";
        for (int p = c.WindowStart; p < c.WindowEnd && p < c.Text.Length; p++)
        {
            char ch = c.Text[p];
            bool dbl = doubles.Contains(ch, StringComparison.Ordinal);
            if (!dbl && !singles.Contains(ch, StringComparison.Ordinal))
            {
                continue;
            }

            if (!RepairSites.InCode(c.Tokens, p))
            {
                continue;
            }

            string family = dbl ? doubles : singles;
            char straight = dbl ? '"' : '\'';
            int lineEnd = c.Source.GetLineStart(c.Source.GetPosition(p).Line) + c.Source.GetLineText(c.Source.GetPosition(p).Line).Length;
            int q = -1;
            for (int k = p + 1; k < lineEnd; k++)
            {
                if (family.Contains(c.Text[k], StringComparison.Ordinal) && RepairSites.InCode(c.Tokens, k))
                {
                    q = k;
                    break;
                }
            }

            // The quotes must pair up, and the text between them must not need escaping once they are straight.
            if (q < 0 || Slice(c.Text, p + 1, q).Contains(straight, StringComparison.Ordinal) || Slice(c.Text, p + 1, q).Contains('\\', StringComparison.Ordinal))
            {
                return;
            }

            all.Add(new RepairCandidate("R004", "replaced typographic quotes with straight quotes", "replaced", new Span(p, q + 1),
                [new TextEdit(p, 1, straight.ToString()), new TextEdit(q, 1, straight.ToString())]));
            return;
        }
    }

    // ---- R005: missing comma -----------------------------------------------------------------------

    private static void MissingComma(RepairContext c, List<RepairCandidate> all)
    {
        for (int j = c.FirstIndex; j <= c.FirstIndex + 1 && j < c.Tokens.Count; j++)
        {
            Token t = c.Tokens[j];
            if (j == 0 || !t.NewlineBefore || !StartsValue(t.Type) || !EndsValue(c.Tokens[j - 1].Type))
            {
                continue;
            }

            int o = c.Enclosing(j);
            if (o < 0 || !CommaSeparatedList(c, o))
            {
                continue;
            }

            Token prev = c.Tokens[j - 1];
            all.Add(new RepairCandidate("R005", "inserted a missing comma", "inserted `,` before this", c.First.PrimarySpan, [new TextEdit(prev.End, 0, ",")]));
            return;
        }
    }

    private static bool CommaSeparatedList(RepairContext c, int open)
    {
        Token o = c.Tokens[open];
        if (o.Type == TokenType.LBracket)
        {
            return true;
        }

        if (open == 0)
        {
            return false;
        }

        TokenType before = c.Tokens[open - 1].Type;
        if (o.Type == TokenType.LParen)
        {
            return before is TokenType.Identifier or TokenType.RParen or TokenType.RBracket;
        }

        // `{` is an object literal where an expression is expected; after `)` or `=>` it is a block.
        return before is TokenType.Assign or TokenType.LParen or TokenType.Comma or TokenType.Colon or TokenType.Return
            or TokenType.LBracket or TokenType.Question or TokenType.QuestionQuestion or TokenType.PipePipe or TokenType.AmpAmp
            or TokenType.Ellipsis or TokenType.PlusAssign or TokenType.QuestionQuestionAssign or TokenType.PipePipeAssign;
    }

    // ---- R006: Python words ------------------------------------------------------------------------

    private static void PythonWords(RepairContext c, List<RepairCandidate> all)
    {
        const string title = "replaced a Python keyword with its JavaScript form";
        for (int i = 1; i + 1 < c.Tokens.Count; i++)
        {
            Token t = c.Tokens[i];
            if (t.Start > c.WindowEnd)
            {
                break;
            }

            if (t.Type != TokenType.Identifier || t.Escaped || !c.InWindow(t.Start))
            {
                continue;
            }

            Token prev = c.Tokens[i - 1];
            Token next = c.Tokens[i + 1];
            string? replacement = null;
            if (t.Text == "elif" && prev.Type == TokenType.RBrace && next.Type == TokenType.LParen)
            {
                replacement = "else if";
            }
            else if (t.Text is "and" or "or" && EndsValue(prev.Type) && !t.NewlineBefore && StartsValue(next.Type) | next.Type is TokenType.Bang or TokenType.Minus)
            {
                replacement = t.Text == "and" ? "&&" : "||";
            }

            if (replacement is not null)
            {
                all.Add(new RepairCandidate("R006", title, "replaced", new Span(t.Start, t.End), [new TextEdit(t.Start, t.End - t.Start, replacement)]));
            }
        }
    }

    // ---- R007: TypeScript types --------------------------------------------------------------------

    private static void TypeScript(RepairContext c, List<RepairCandidate> all)
    {
        const string title = "removed TypeScript type syntax";
        int i = c.FirstIndex;
        Token t = c.At(i);
        if (i == 0)
        {
            return;
        }

        Token prev = c.Tokens[i - 1];

        // `const name: Type = value`: the parser stops at the name (a `const` needs an initializer), one token before the colon.
        if (t.Type == TokenType.Identifier && (prev.Type is TokenType.Const or TokenType.Var || (prev.Type == TokenType.Identifier && prev.Text == "let"))
            && c.At(i + 1).Type is TokenType.Colon && !c.At(i + 1).NewlineBefore)
        {
            int typeEnd = TypeEnd(c, i + 2);
            if (typeEnd > i + 2)
            {
                all.Add(new RepairCandidate("R007", title, "removed", new Span(c.At(i + 1).Start, c.At(i + 1).End), [new TextEdit(t.End, c.Tokens[typeEnd - 1].End - t.End, "")]));
            }

            return;
        }

        // `name: Type`, `name?: Type`, `): Type`
        bool optionalMark = t.Type == TokenType.Question && c.At(i + 1).Type == TokenType.Colon && prev.Type == TokenType.Identifier;
        if ((t.Type == TokenType.Colon && prev.Type is TokenType.Identifier or TokenType.RParen or TokenType.RBracket or TokenType.RBrace) || optionalMark)
        {
            int colon = optionalMark ? i + 1 : i;
            int end = TypeEnd(c, colon + 1);
            if (end <= colon + 1)
            {
                return;
            }

            var edits = new List<TextEdit> { new TextEdit(prev.End, c.Tokens[end - 1].End - prev.End, "") };
            int open = c.Enclosing(i);
            if (open >= 0 && c.Tokens[open].Type == TokenType.LParen && c.Match[open] > open)
            {
                AnnotationsAfter(c, open, end, edits);
            }

            all.Add(new RepairCandidate("R007", title, "removed", new Span(t.Start, t.End), edits));
            return;
        }

        // `value as Type`
        if (t.Type == TokenType.Identifier && t.Text == "as" && !t.Escaped && EndsValue(prev.Type))
        {
            int end = TypeEnd(c, i + 1);
            while (end > i + 1 && c.At(end).Type == TokenType.Identifier && c.At(end).Text == "as")
            {
                // `x as unknown as T` is one cast chain.
                int more = TypeEnd(c, end + 1);
                if (more <= end + 1)
                {
                    break;
                }

                end = more;
            }

            if (end > i + 1)
            {
                Remove(all, title, new Span(t.Start, t.End), prev.End, c.Tokens[end - 1].End);
            }

            return;
        }

        // `function f<T>(...)`
        if (t.Type == TokenType.Lt && prev.Type == TokenType.Identifier)
        {
            int end = AngleEnd(c, i);
            if (end > i)
            {
                Remove(all, title, new Span(t.Start, t.End), t.Start, c.Tokens[end].End);
            }

            return;
        }

        // `interface Name { ... }` and `type Name = ...;` at the start of a statement
        if (prev.Type == TokenType.Identifier && prev.Text is "interface" or "type" && t.Type == TokenType.Identifier
            && (i == 1 || prev.NewlineBefore || c.Tokens[i - 2].Type is TokenType.Semicolon or TokenType.RBrace))
        {
            int stop = -1;
            if (prev.Text == "interface")
            {
                int j = i + 1;
                while (j < c.Tokens.Count && c.Tokens[j].Type is not (TokenType.LBrace or TokenType.EndOfInput or TokenType.Semicolon))
                {
                    j++;
                }

                if (c.Tokens[j].Type == TokenType.LBrace && c.Match[j] > j)
                {
                    stop = c.Match[j] + 1;
                }
            }
            else
            {
                int j = i + 1;
                if (c.Tokens[j].Type == TokenType.Lt)
                {
                    int ae = AngleEnd(c, j);
                    j = ae > j ? ae + 1 : j;
                }

                if (c.Tokens[j].Type == TokenType.Assign)
                {
                    int te = TypeEnd(c, j + 1);
                    if (te > j + 1)
                    {
                        stop = c.Tokens[te].Type == TokenType.Semicolon ? te + 1 : te;
                    }
                }
            }

            if (stop > 0)
            {
                int from = prev.Start;
                int to = c.Tokens[stop - 1].End;
                int lineStart = c.Source.GetLineStart(c.Source.GetPosition(from).Line);
                if (c.Text.AsSpan(lineStart, from - lineStart).Trim().IsEmpty)
                {
                    from = lineStart;
                    int k = to;
                    while (k < c.Text.Length && c.Text[k] is ' ' or '\t')
                    {
                        k++;
                    }

                    if (k >= c.Text.Length)
                    {
                        to = k;
                    }
                    else if (c.Text[k] == '\r' && k + 1 < c.Text.Length && c.Text[k + 1] == '\n')
                    {
                        to = k + 2;
                    }
                    else if (SourceText.IsLineTerminator(c.Text[k]))
                    {
                        to = k + 1;
                    }
                }

                all.Add(new RepairCandidate("R007", title, "removed", new Span(prev.Start, prev.End), [new TextEdit(from, to - from, "")]));
            }
        }
    }

    /// <summary>
    /// Collects the rest of the annotations of one parameter list (the parameters after token <paramref name="from"/> and
    /// the return type after its <c>)</c>), so a signature is repaired as a unit.
    /// </summary>
    private static void AnnotationsAfter(RepairContext c, int open, int from, List<TextEdit> edits)
    {
        int close = c.Match[open];
        int j = from;
        while (j < close)
        {
            // Skip to the next top-level comma, then read one parameter: name, optional `?`, optional `: Type`.
            while (j < close && c.Tokens[j].Type != TokenType.Comma)
            {
                j = c.Tokens[j].Type is TokenType.LParen or TokenType.LBracket or TokenType.LBrace && c.Match[j] > j ? c.Match[j] + 1 : j + 1;
            }

            if (j >= close)
            {
                break;
            }

            int k = j + 1;
            if (k >= close)
            {
                break;
            }

            int nameEnd = k;
            if (c.Tokens[k].Type == TokenType.Identifier)
            {
                nameEnd = k + 1;
            }
            else if (c.Tokens[k].Type is TokenType.LBrace or TokenType.LBracket && c.Match[k] > k)
            {
                nameEnd = c.Match[k] + 1;
            }
            else
            {
                j = k;
                continue;
            }

            int colon = nameEnd;
            if (c.Tokens[colon].Type == TokenType.Question && c.Tokens[colon + 1].Type == TokenType.Colon)
            {
                colon++;
            }

            if (c.Tokens[colon].Type == TokenType.Colon)
            {
                int end = TypeEnd(c, colon + 1);
                if (end > colon + 1)
                {
                    edits.Add(new TextEdit(c.Tokens[nameEnd - 1].End, c.Tokens[end - 1].End - c.Tokens[nameEnd - 1].End, ""));
                    j = end;
                    continue;
                }
            }

            j = nameEnd;
        }

        if (c.At(close + 1).Type == TokenType.Colon)
        {
            int end = TypeEnd(c, close + 2);
            if (end > close + 2)
            {
                edits.Add(new TextEdit(c.Tokens[close].End, c.Tokens[end - 1].End - c.Tokens[close].End, ""));
            }
        }
    }

    private static void Remove(List<RepairCandidate> all, string title, Span primary, int from, int to) =>
        all.Add(new RepairCandidate("R007", title, "removed", primary, [new TextEdit(from, to - from, "")]));

    /// <summary>End of the balanced <c>&lt;...&gt;</c> that opens at token <paramref name="open"/>: the index of its closing token, or -1.</summary>
    private static int AngleEnd(RepairContext c, int open)
    {
        int depth = 0;
        for (int j = open; j < c.Tokens.Count && j < open + 200; j++)
        {
            switch (c.Tokens[j].Type)
            {
                case TokenType.Lt:
                    depth++;
                    break;
                case TokenType.Gt:
                    depth--;
                    break;
                case TokenType.Shr:
                    depth -= 2;
                    break;
                case TokenType.Semicolon or TokenType.LBrace or TokenType.EndOfInput:
                    return -1;
            }

            if (depth <= 0)
            {
                return depth == 0 ? j : -1;
            }
        }

        return -1;
    }

    /// <summary>
    /// Index of the first token after the TypeScript type that starts at token <paramref name="i"/>, or -1 if it does not
    /// look like a type. Covers names, qualified names, generics, arrays, unions, intersections, literals, object and
    /// tuple types and function types: enough for the annotations that appear in pasted code.
    /// </summary>
    private static int TypeEnd(RepairContext c, int i)
    {
        int j = i;
        while (true)
        {
            j = TypePrimary(c, j);
            if (j < 0)
            {
                return -1;
            }

            while (true)
            {
                Token t = c.At(j);
                if (t.Type == TokenType.Lt && !t.NewlineBefore)
                {
                    int e = AngleEnd(c, j);
                    if (e < 0)
                    {
                        return -1;
                    }

                    j = e + 1;
                }
                else if (t.Type == TokenType.LBracket && !t.NewlineBefore && c.Match[j] > j)
                {
                    j = c.Match[j] + 1;
                }
                else
                {
                    break;
                }
            }

            Token op = c.At(j);
            if (op.Type is TokenType.Pipe or TokenType.Amp)
            {
                j++;
                continue;
            }

            return j;
        }
    }

    private static int TypePrimary(RepairContext c, int j)
    {
        Token t = c.At(j);
        switch (t.Type)
        {
            case TokenType.Identifier when t.Text is "keyof" or "typeof" or "readonly" or "unique" or "infer":
                return TypePrimary(c, j + 1);
            case TokenType.Identifier or TokenType.Null or TokenType.Void or TokenType.This or TokenType.True or TokenType.False
                or TokenType.Number or TokenType.String:
                j++;
                while (c.At(j).Type == TokenType.Dot && c.At(j + 1).Type == TokenType.Identifier)
                {
                    j += 2;
                }

                return j;
            case TokenType.Minus when c.At(j + 1).Type == TokenType.Number:
                return j + 2;
            case TokenType.LBrace or TokenType.LBracket:
                return c.Match[j] > j ? c.Match[j] + 1 : -1;
            case TokenType.LParen:
                {
                    if (c.Match[j] < 0 || c.At(c.Match[j] + 1).Type != TokenType.Arrow)
                    {
                        return -1;
                    }

                    return TypePrimary(c, c.Match[j] + 2);
                }

            default:
                return -1;
        }
    }

    // ---- R008: comma between class members ---------------------------------------------------------

    private static void ClassMemberComma(RepairContext c, List<RepairCandidate> all)
    {
        Token t = c.At(c.FirstIndex);
        if (t.Type != TokenType.Comma || c.FirstIndex == 0)
        {
            return;
        }

        int o = c.Enclosing(c.FirstIndex);
        if (o < 0 || c.Tokens[o].Type != TokenType.LBrace || !IsClassBody(c, o))
        {
            return;
        }

        all.Add(new RepairCandidate("R008", "removed a comma between class members", "removed", new Span(t.Start, t.End), [new TextEdit(t.Start, t.End - t.Start, "")]));
    }

    private static bool IsClassBody(RepairContext c, int brace)
    {
        // `class Name extends Base {` : walk back over the heading to a `class` keyword at the same nesting.
        int depth = 0;
        for (int j = brace - 1; j >= 0 && j > brace - 200; j--)
        {
            TokenType t = c.Tokens[j].Type;
            if (t is TokenType.RParen or TokenType.RBracket or TokenType.RBrace)
            {
                depth++;
            }
            else if (t is TokenType.LParen or TokenType.LBracket or TokenType.LBrace)
            {
                if (depth == 0)
                {
                    return false;
                }

                depth--;
            }
            else if (depth == 0)
            {
                if (t == TokenType.Class)
                {
                    return true;
                }

                if (t is TokenType.Semicolon or TokenType.Assign or TokenType.Arrow)
                {
                    return false;
                }
            }
        }

        return false;
    }

    // ---- R009 / R011: backslashes -------------------------------------------------------------------

    private static void EscapedQuotes(RepairContext c, List<RepairCandidate> all)
    {
        Token t = c.At(c.FirstIndex);
        if (t.Type != TokenType.Invalid || t.End <= t.Start || c.Text[t.Start] != '\\' || t.Start + 1 >= c.Text.Length || c.Text[t.Start + 1] is not ('"' or '\''))
        {
            return;
        }

        int lineEnd = c.Source.GetLineStart(c.Source.GetPosition(t.Start).Line) + c.Source.GetLineText(c.Source.GetPosition(t.Start).Line).Length;
        List<int> at = RepairSites.EscapedQuotes(c.Text, t.Start, lineEnd);
        all.Add(new RepairCandidate("R009", "removed backslashes that escaped quotes outside a string", "removed", new Span(t.Start, t.Start + 1),
            at.Select(k => new TextEdit(k, 1, "")).ToList()));
    }

    private static void LiteralNewlines(RepairContext c, List<RepairCandidate> all)
    {
        Token t = c.At(c.FirstIndex);
        if (t.Type != TokenType.Invalid || c.Text[t.Start] != '\\' || t.Start + 1 >= c.Text.Length || c.Text[t.Start + 1] != 'n')
        {
            return;
        }

        List<int> at = RepairSites.LiteralNewlines(c.Text, c.Tokens);
        if (at.Count == 0)
        {
            return;
        }

        all.Add(new RepairCandidate("R011", "turned literal `\\n` sequences between statements into line breaks", "replaced", new Span(at[0], at[0] + 1),
            at.Select(k => new TextEdit(k, 2, "\n")).ToList()));
    }

    // ---- R010: HTML entities -----------------------------------------------------------------------

    private static void HtmlEntities(RepairContext c, List<RepairCandidate> all)
    {
        var entities = RepairSites.Entities(c.Text, c.Tokens);
        if (entities.Count == 0)
        {
            return;
        }

        bool onErrorLine = false;
        foreach ((int at, _, _) in entities)
        {
            onErrorLine |= c.InWindow(at);
        }

        if (!onErrorLine)
        {
            return;
        }

        all.Add(new RepairCandidate("R010", "decoded HTML entities in code", "decoded", new Span(entities[0].At, entities[0].At + entities[0].Length),
            entities.Select(e => new TextEdit(e.At, e.Length, e.Decoded)).ToList()));
    }
}
