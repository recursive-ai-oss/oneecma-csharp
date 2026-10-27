using RecursiveAI.OneEcma.Diagnostics;
using System.Globalization;
using System.Numerics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

internal sealed partial class Parser
{
    /// <summary>
    /// Early errors that stay pending while an expression might still turn out to be a pattern
    /// (ECMA-262 13.15.5.1: <c>CoverInitializedName</c> is only valid in an assignment pattern).
    /// </summary>
    private sealed class CoverErrors
    {
        internal int ShorthandAssign = -1;
        internal int ShorthandAssignEnd = -1;
        internal int DoubleProto = -1;
    }

    // Array and object literals whose last element was `...x,` — an error only if reinterpreted as a pattern.
    private readonly HashSet<Node> _spreadWithTrailingComma = [];

    // Start of each `||`/`&&`/`??` operator token, by the logical expression it built, for mixing errors.
    private readonly Dictionary<LogicalExpression, int> _logicalOperatorStart = [];

    private void CheckExpressionErrors(CoverErrors ce)
    {
        if (ce.ShorthandAssign >= 0)
        {
            Report("invalid-shorthand-initializer", Messages.InvalidShorthandInit, ce.ShorthandAssign, ce.ShorthandAssignEnd);
            ce.ShorthandAssign = -1;
        }

        if (ce.DoubleProto >= 0)
        {
            Report("duplicate-proto", Messages.DuplicateProto, ce.DoubleProto, ce.DoubleProto + 1);
            ce.DoubleProto = -1;
        }
    }

    // ---- entry points ----------------------------------------------------------------------

    private Expression ParseExpression(bool noIn, CoverErrors? cover = null)
    {
        int start = _tok.Start;
        Expression first = ParseAssignment(noIn, cover);
        if (!Is(TokenType.Comma))
        {
            return first;
        }

        var list = new List<Expression> { first };
        while (Eat(TokenType.Comma))
        {
            list.Add(ParseAssignment(noIn, cover));
        }

        return new SequenceExpression(start, _prevEnd, list);
    }

    private static AssignOp? ToAssignOp(TokenType t) => t switch
    {
        TokenType.Assign => AssignOp.Assign,
        TokenType.PlusAssign => AssignOp.AddAssign,
        TokenType.MinusAssign => AssignOp.SubtractAssign,
        TokenType.StarAssign => AssignOp.MultiplyAssign,
        TokenType.SlashAssign => AssignOp.DivideAssign,
        TokenType.PercentAssign => AssignOp.RemainderAssign,
        TokenType.StarStarAssign => AssignOp.ExponentAssign,
        TokenType.ShlAssign => AssignOp.ShiftLeftAssign,
        TokenType.ShrAssign => AssignOp.ShiftRightAssign,
        TokenType.UShrAssign => AssignOp.UnsignedShiftRightAssign,
        TokenType.AmpAssign => AssignOp.BitwiseAndAssign,
        TokenType.PipeAssign => AssignOp.BitwiseOrAssign,
        TokenType.CaretAssign => AssignOp.BitwiseXorAssign,
        TokenType.AmpAmpAssign => AssignOp.AndAssign,
        TokenType.PipePipeAssign => AssignOp.OrAssign,
        TokenType.QuestionQuestionAssign => AssignOp.CoalesceAssign,
        _ => null,
    };

    /// <summary>ECMA-262 13.15 AssignmentExpression (including arrow functions and yield).</summary>
    private Expression ParseAssignment(bool noIn, CoverErrors? cover = null)
    {
        Enter();
        try
        {
            if (IsContextual("yield") && _fn.IsGenerator)
            {
                return ParseYield(noIn);
            }

            int start = _tok.Start;
            bool own = cover is null;
            CoverErrors ce = cover ?? new CoverErrors();
            int oldShorthand = ce.ShorthandAssign;
            int oldShorthandEnd = ce.ShorthandAssignEnd;
            int oldProto = ce.DoubleProto;
            if (!own)
            {
                ce.ShorthandAssign = -1;
                ce.DoubleProto = -1;
            }

            Expression left = ParseConditional(noIn, ce);
            if (left is FunctionNode { IsArrow: true, IsParenthesized: false })
            {
                RestoreCover(own, ce, oldShorthand, oldShorthandEnd, oldProto);
                return left;
            }

            AssignOp? op = ToAssignOp(_tok.Type);
            if (op is not null)
            {
                Token opTok = _tok;
                Node target;
                if (op == AssignOp.Assign)
                {
                    target = ToAssignableTarget(left, ce, isForHead: false);
                    if (ce.ShorthandAssign >= left.Start && left is ArrayLiteral or ObjectLiteral)
                    {
                        ce.ShorthandAssign = -1;
                    }

                    if (left is ArrayLiteral or ObjectLiteral && !left.IsParenthesized)
                    {
                        ce.DoubleProto = -1;
                    }
                }
                else
                {
                    target = left;
                    CheckSimpleTarget(left, Messages.InvalidAssignTarget, left.Start, left.End, allowCall: op is not (AssignOp.AndAssign or AssignOp.OrAssign or AssignOp.CoalesceAssign));
                }

                Advance();
                Expression right = ParseAssignment(noIn);
                RestoreCover(own, ce, oldShorthand, oldShorthandEnd, oldProto);
                _ = opTok;
                return new AssignmentExpression(start, right.End, op.Value, target, right);
            }

            if (own)
            {
                CheckExpressionErrors(ce);
            }
            else
            {
                MergeCover(ce, oldShorthand, oldShorthandEnd, oldProto);
            }

            return left;
        }
        finally
        {
            Leave();
        }
    }

    private static void RestoreCover(bool own, CoverErrors ce, int oldShorthand, int oldShorthandEnd, int oldProto)
    {
        if (own)
        {
            return;
        }

        MergeCover(ce, oldShorthand, oldShorthandEnd, oldProto);
    }

    // Keeps the earliest pending error of the enclosing level plus any found at this level.
    private static void MergeCover(CoverErrors ce, int oldShorthand, int oldShorthandEnd, int oldProto)
    {
        if (oldShorthand >= 0)
        {
            ce.ShorthandAssign = oldShorthand;
            ce.ShorthandAssignEnd = oldShorthandEnd;
        }

        if (oldProto >= 0)
        {
            ce.DoubleProto = oldProto;
        }
    }

    private YieldExpression ParseYield(bool noIn)
    {
        int start = _tok.Start;
        if (_fn.YieldPos < 0)
        {
            _fn.YieldPos = start;
        }

        Advance();
        bool delegating = false;
        Expression? arg = null;
        if (!_tok.NewlineBefore)
        {
            if (Eat(TokenType.Star))
            {
                delegating = true;
                arg = ParseAssignment(noIn);
            }
            else if (!(_tok.Type is TokenType.RParen or TokenType.RBracket or TokenType.RBrace or TokenType.Comma
                or TokenType.Colon or TokenType.Semicolon or TokenType.EndOfInput or TokenType.In))
            {
                arg = ParseAssignment(noIn);
            }
        }

        return new YieldExpression(start, _prevEnd, arg, delegating);
    }

    private Expression ParseConditional(bool noIn, CoverErrors ce)
    {
        int start = _tok.Start;
        Expression test = ParseBinary(0, noIn, ce);
        if (test is FunctionNode { IsArrow: true, IsParenthesized: false })
        {
            return test;
        }

        if (!Is(TokenType.Question))
        {
            return test;
        }

        Advance();
        Expression cons = ParseAssignment(noIn: false);
        Expect(TokenType.Colon);
        Expression alt = ParseAssignment(noIn);
        return new ConditionalExpression(start, _prevEnd, test, cons, alt);
    }

    // ---- binary operators ------------------------------------------------------------------

    private static int Precedence(TokenType t, bool noIn) => t switch
    {
        TokenType.QuestionQuestion => 1,
        TokenType.PipePipe => 2,
        TokenType.AmpAmp => 3,
        TokenType.Pipe => 4,
        TokenType.Caret => 5,
        TokenType.Amp => 6,
        TokenType.EqEq or TokenType.NotEq or TokenType.EqEqEq or TokenType.NotEqEq => 7,
        TokenType.Lt or TokenType.Gt or TokenType.Le or TokenType.Ge or TokenType.Instanceof => 8,
        TokenType.In => noIn ? -1 : 8,
        TokenType.Shl or TokenType.Shr or TokenType.UShr => 9,
        TokenType.Plus or TokenType.Minus => 10,
        TokenType.Star or TokenType.Slash or TokenType.Percent => 11,
        TokenType.StarStar => 12,
        _ => -1,
    };

    private static BinaryOp ToBinaryOp(TokenType t) => t switch
    {
        TokenType.Pipe => BinaryOp.BitwiseOr,
        TokenType.Caret => BinaryOp.BitwiseXor,
        TokenType.Amp => BinaryOp.BitwiseAnd,
        TokenType.EqEq => BinaryOp.Equal,
        TokenType.NotEq => BinaryOp.NotEqual,
        TokenType.EqEqEq => BinaryOp.StrictEqual,
        TokenType.NotEqEq => BinaryOp.StrictNotEqual,
        TokenType.Lt => BinaryOp.LessThan,
        TokenType.Gt => BinaryOp.GreaterThan,
        TokenType.Le => BinaryOp.LessThanOrEqual,
        TokenType.Ge => BinaryOp.GreaterThanOrEqual,
        TokenType.Instanceof => BinaryOp.Instanceof,
        TokenType.In => BinaryOp.In,
        TokenType.Shl => BinaryOp.ShiftLeft,
        TokenType.Shr => BinaryOp.ShiftRight,
        TokenType.UShr => BinaryOp.UnsignedShiftRight,
        TokenType.Plus => BinaryOp.Add,
        TokenType.Minus => BinaryOp.Subtract,
        TokenType.Star => BinaryOp.Multiply,
        TokenType.Slash => BinaryOp.Divide,
        TokenType.Percent => BinaryOp.Remainder,
        _ => BinaryOp.Exponent,
    };

    /// <summary>Precedence climbing over ECMA-262 13.6 - 13.13; only right-associative <c>**</c> recurses, under the depth guard.</summary>
    private Expression ParseBinary(int minPrec, bool noIn, CoverErrors ce)
    {
        int start = _tok.Start;
        Node left;
        if (Is(TokenType.PrivateName))
        {
            // 13.10: `#x in obj`
            Token pt = _tok;
            Advance();
            left = new PrivateIdentifier(pt.Start, pt.End, pt.Text!);
            RecordPrivateReference(pt.Text!, new Span(pt.Start, pt.End));
            if (!Is(TokenType.In) || noIn || minPrec > 8)
            {
                Unexpected();
                left = new ErrorExpression(pt.Start, pt.End);
            }
        }
        else
        {
            left = ParseUnary(ce);
            if (left is FunctionNode { IsArrow: true, IsParenthesized: false })
            {
                return (Expression)left;
            }
        }

        while (true)
        {
            TokenType t = _tok.Type;
            int prec = Precedence(t, noIn);
            if (prec < 0 || prec < minPrec)
            {
                break;
            }

            Token opTok = _tok;
            if (t == TokenType.StarStar && left is UnaryExpression or AwaitExpression && !((Expression)left).IsParenthesized)
            {
                Report("unary-before-exponent", Messages.UnaryBeforeExponent, left.Start, left.End);
            }

            Advance();
            Expression right;
            if (t == TokenType.StarStar)
            {
                Enter();
                right = ParseBinary(prec, noIn, ce);
                Leave();
            }
            else
            {
                right = ParseBinary(prec + 1, noIn, ce);
            }

            if (right is FunctionNode { IsArrow: true, IsParenthesized: false })
            {
                Report("unexpected-token", "Unexpected token '=>'", right.Start, right.Start + 1);
            }

            if (t is TokenType.QuestionQuestion or TokenType.PipePipe or TokenType.AmpAmp)
            {
                LogicalOp lop = t == TokenType.QuestionQuestion ? LogicalOp.Coalesce : t == TokenType.PipePipe ? LogicalOp.Or : LogicalOp.And;
                CheckLogicalMixing(lop, left, right, opTok);
                var le = new LogicalExpression(start, right.End, lop, (Expression)left, right);
                _logicalOperatorStart[le] = opTok.Start;
                left = le;
            }
            else
            {
                left = new BinaryExpression(start, right.End, ToBinaryOp(t), left, right);
            }
        }

        return (Expression)left;
    }

    // 13.13: `??` cannot be mixed with `||` or `&&` without parentheses.
    private void CheckLogicalMixing(LogicalOp op, Node left, Expression right, in Token opTok)
    {
        if (left is LogicalExpression { IsParenthesized: false } l && (op == LogicalOp.Coalesce) != (l.Operator == LogicalOp.Coalesce))
        {
            Report("logical-mixing", string.Concat("Unexpected token '", _s.AsSpan(opTok.Start, opTok.End - opTok.Start), "'"), opTok.Start, opTok.End);
        }

        if (right is LogicalExpression { IsParenthesized: false } r && (op == LogicalOp.Coalesce) != (r.Operator == LogicalOp.Coalesce))
        {
            int pos = _logicalOperatorStart.GetValueOrDefault(r, right.Start);
            string text = r.Operator switch { LogicalOp.Or => "||", LogicalOp.And => "&&", _ => "??" };
            Report("logical-mixing", "Unexpected token '" + text + "'", pos, pos + 2);
        }
    }

    // ---- unary and postfix -----------------------------------------------------------------

    private Expression ParseUnary(CoverErrors ce)
    {
        Enter();
        try
        {
            int start = _tok.Start;
            UnaryOp? op = _tok.Type switch
            {
                TokenType.Minus => UnaryOp.Minus,
                TokenType.Plus => UnaryOp.Plus,
                TokenType.Bang => UnaryOp.Not,
                TokenType.Tilde => UnaryOp.BitwiseNot,
                TokenType.Typeof => UnaryOp.Typeof,
                TokenType.Void => UnaryOp.Void,
                TokenType.Delete => UnaryOp.Delete,
                _ => null,
            };
            if (op is not null)
            {
                Advance();
                Expression arg = ParseUnary(new CoverErrors());
                if (op == UnaryOp.Delete)
                {
                    CheckDelete(arg);
                }

                return new UnaryExpression(start, arg.End, op.Value, arg);
            }

            if (Is(TokenType.PlusPlus) || Is(TokenType.MinusMinus))
            {
                bool inc = Is(TokenType.PlusPlus);
                Advance();
                Expression arg = ParseUnary(new CoverErrors());
                CheckSimpleTarget(arg, Messages.InvalidPrefixTarget, arg.Start, arg.End);
                return new UpdateExpression(start, arg.End, inc, true, arg);
            }

            if (IsContextual("await") && AwaitIsKeyword())
            {
                return ParseAwait();
            }

            Expression e = ParseLeftHandSide(ce);
            if ((Is(TokenType.PlusPlus) || Is(TokenType.MinusMinus)) && !_tok.NewlineBefore && e is not FunctionNode { IsArrow: true, IsParenthesized: false })
            {
                bool inc = Is(TokenType.PlusPlus);
                CheckSimpleTarget(e, Messages.InvalidPostfixTarget, e.Start, e.End);
                Advance();
                return new UpdateExpression(start, _prevEnd, inc, false, e);
            }

            return e;
        }
        finally
        {
            Leave();
        }
    }

    private bool AwaitIsKeyword() => _fn.IsAsync;

    private AwaitExpression ParseAwait()
    {
        int start = _tok.Start;
        if (_fn.AwaitPos < 0)
        {
            _fn.AwaitPos = start;
        }

        Advance();
        Expression arg = ParseUnary(new CoverErrors());
        return new AwaitExpression(start, arg.End, arg);
    }

    private void CheckDelete(Expression arg)
    {
        Expression e = arg;
        if (_strict && e is Identifier)
        {
            Report("strict-delete", Messages.StrictDelete, arg.Start, arg.End);
        }

        // 13.5.1.1: delete of a private reference, also through an optional chain or parentheses.
        Expression inner = e;
        if (inner is ChainExpression ch)
        {
            inner = ch.Expression;
        }

        if (inner is MemberExpression { Property: PrivateIdentifier })
        {
            Report("delete-private", Messages.DeletePrivate, arg.Start, arg.End);
        }
    }

    /// <summary>Whether an expression is a valid target of <c>++</c>, <c>--</c> or a compound assignment (ECMA-262 AssignmentTargetType is simple).</summary>
    private bool CheckSimpleTarget(Expression e, string message, int start, int end, bool allowCall = true)
    {
        switch (e)
        {
            case Identifier id:
                if (_strict && id.Name is "eval" or "arguments")
                {
                    Report("strict-eval-arguments", Messages.UnexpectedEvalOrArguments, id.Start, id.End);
                }

                return true;
            case MemberExpression:
                return true;
            case CallExpression when !_strict && allowCall:
                // Annex B (web compatibility): in sloppy code a call is accepted as a target and throws a ReferenceError when evaluated.
                return true;
            default:
                Report("invalid-assignment-target", message, start, end);
                return false;
        }
    }

    // ---- left-hand-side expressions --------------------------------------------------------

    private Expression ParseLeftHandSide(CoverErrors ce)
    {
        int start = _tok.Start;
        Expression e;
        switch (_tok.Type)
        {
            case TokenType.New:
                e = ParseNew();
                break;
            case TokenType.Super:
                e = ParseSuper();
                break;
            case TokenType.Import:
                e = ParseImportExpression();
                break;
            default:
                e = ParsePrimary(ce);
                if (e is FunctionNode { IsArrow: true, IsParenthesized: false })
                {
                    return e;
                }

                break;
        }

        return ParseSubscripts(e, start, noCalls: false);
    }

    private Expression ParseSubscripts(Expression expr, int start, bool noCalls)
    {
        bool optional = false;
        while (true)
        {
            switch (_tok.Type)
            {
                case TokenType.Dot:
                    {
                        Advance();
                        Node prop = ParseMemberName();
                        if (expr is SuperExpression && prop is PrivateIdentifier)
                        {
                            Report("super-private", Messages.SuperUnexpected, prop.Start, prop.End);
                        }

                        expr = new MemberExpression(start, _prevEnd, expr, prop, false, false);
                        continue;
                    }

                case TokenType.QuestionDot:
                    {
                        if (noCalls)
                        {
                            Report("invalid-optional-new", Messages.InvalidOptionalNew, _tok.Start, _tok.End);
                        }

                        Advance();
                        optional = true;
                        if (Is(TokenType.LParen))
                        {
                            var args = ParseArguments(out _, start);
                            expr = new CallExpression(start, _prevEnd, expr, args, true, false);
                        }
                        else if (Is(TokenType.LBracket))
                        {
                            Advance();
                            Expression idx = ParseExpression(noIn: false);
                            Expect(TokenType.RBracket);
                            expr = new MemberExpression(start, _prevEnd, expr, idx, true, true);
                        }
                        else if (Is(TokenType.Template))
                        {
                            Report("invalid-optional-template", Messages.InvalidOptionalTemplate, _tok.Start, _tok.End);
                            TemplateLiteral junk = ParseTemplate(tagged: true);
                            expr = new TaggedTemplate(start, _prevEnd, expr, junk);
                        }
                        else
                        {
                            Node prop = ParseMemberName();
                            expr = new MemberExpression(start, _prevEnd, expr, prop, false, true);
                        }

                        continue;
                    }

                case TokenType.LBracket:
                    {
                        Advance();
                        Expression idx = ParseExpression(noIn: false);
                        Expect(TokenType.RBracket);
                        expr = new MemberExpression(start, _prevEnd, expr, idx, true, false);
                        continue;
                    }

                case TokenType.LParen when !noCalls:
                    {
                        var args = ParseArguments(out _, start);
                        bool evalCandidate = expr is Identifier { Name: "eval" } && !expr.IsParenthesized;
                        if (evalCandidate)
                        {
                            MarkDirectEval();
                        }

                        expr = new CallExpression(start, _prevEnd, expr, args, false, evalCandidate);
                        continue;
                    }

                case TokenType.Template:
                    {
                        if (optional)
                        {
                            Report("invalid-optional-template", Messages.InvalidOptionalTemplate, _tok.Start, _tok.End);
                        }

                        TemplateLiteral quasi = ParseTemplate(tagged: true);
                        expr = new TaggedTemplate(start, _prevEnd, expr, quasi);
                        continue;
                    }
            }

            break;
        }

        if (optional)
        {
            expr = new ChainExpression(start, _prevEnd, expr);
        }

        return expr;
    }

    /// <summary>After <c>.</c> or <c>?.</c>: an IdentifierName (reserved words included) or a private name.</summary>
    private Node ParseMemberName()
    {
        Token t = _tok;
        if (t.Type == TokenType.PrivateName)
        {
            Advance();
            RecordPrivateReference(t.Text!, new Span(t.Start, t.End));
            return new PrivateIdentifier(t.Start, t.End, t.Text!);
        }

        if (IsIdentifierName(t))
        {
            Advance();
            return new Identifier(t.Start, t.End, NameOf(t));
        }

        Unexpected();
        return new Identifier(t.Start, t.Start, "");
    }

    private string NameOf(in Token t) => t.Text ?? _s.Substring(t.Start, t.End - t.Start);

    /// <summary>True for an Identifier token or a reserved word (an IdentifierName, 12.7).</summary>
    private static bool IsIdentifierName(in Token t) => t.Type == TokenType.Identifier || t.Type >= TokenType.Break;

    private List<Node> ParseArguments(out bool trailingComma, int calleeStart)
    {
        // `(` has not been consumed yet.
        trailingComma = false;
        var args = new List<Node>();
        Token open = _tok;
        Advance();
        while (!Is(TokenType.RParen))
        {
            if (Is(TokenType.EndOfInput))
            {
                break;
            }

            int before = _tok.Start;
            if (Is(TokenType.Ellipsis))
            {
                int s = _tok.Start;
                Advance();
                Expression a = ParseAssignment(noIn: false);
                args.Add(new SpreadElement(s, a.End, a));
            }
            else
            {
                args.Add(ParseAssignment(noIn: false));
            }

            if (Is(TokenType.RParen))
            {
                break;
            }

            if (!Is(TokenType.Comma))
            {
                ReportMissingParen(open, calleeStart, args[^1]);
                if (_tok.Start == before || _tok.Type is TokenType.Semicolon or TokenType.RBrace or TokenType.RBracket or TokenType.EndOfInput)
                {
                    return args;
                }

                // A value follows without a comma: carry on as if the comma were there.
                continue;
            }

            Advance();
            if (Is(TokenType.RParen))
            {
                trailingComma = true;
            }
        }

        Expect(TokenType.RParen);
        return args;
    }

    /// <summary>
    /// V8 reports a missing <c>)</c> or <c>,</c> in an argument list at the last token before the offending one.
    /// </summary>
    private void ReportMissingParen(in Token open, int calleeStart, Node previousArgument)
    {
        Token offending = _tok;
        if (offending.Type == TokenType.Invalid)
        {
            return;
        }

        Diagnostic? d = Report("missing-paren-after-args", Messages.MissingParenAfterArgs, _prevStart, _prevEnd, syntactic: true);
        if (d is null)
        {
            return;
        }

        bool valueFollows = offending.Type is TokenType.Identifier or TokenType.String or TokenType.Number or TokenType.BigInt or TokenType.Template
            or TokenType.True or TokenType.False or TokenType.Null or TokenType.This or TokenType.LBracket or TokenType.LBrace;
        string offendingText = _s.Substring(offending.Start, Math.Max(0, offending.End - offending.Start));
        if (valueFollows && offending.NewlineBefore)
        {
            d.PrimaryLabel = "expected `,` before this argument";
            d.SecondaryLabels.Add(new Label(previousArgument.Span, "the previous argument ends here"));
            d.Help.Add("add `,` after `" + SourceOf(previousArgument) + "`");
            NoteInserted(d, _prevEnd, ",");
        }
        else
        {
            d.PrimaryLabel = "expected `,` or `)` before this `" + offendingText + "`";
            d.SecondaryLabels.Add(new Label(new Span(calleeStart, open.End), "the argument list opened here is never closed"));
            if (offending.Type is TokenType.Semicolon or TokenType.RBrace or TokenType.RBracket or TokenType.EndOfInput)
            {
                d.Help.Add("add `)` before `" + offendingText + "`: `" + _s.Substring(calleeStart, offending.Start - calleeStart) + ")" + offendingText + "`");
            }

            NoteInserted(d, offending.Start, ")");
        }
    }

    private string SourceOf(Node n) => _s.Substring(n.Start, n.End - n.Start);

    private Expression ParseNew()
    {
        int start = _tok.Start;
        Advance(); // new
        if (Is(TokenType.Dot))
        {
            Advance();
            if (IsContextual("target"))
            {
                if (!_fn.AllowNewTarget)
                {
                    Report("new-target", Messages.NewTargetUnexpected, start, _tok.End);
                }

                MarkUsesThis();
                Advance();
                return new MetaProperty(start, _prevEnd, "new", "target");
            }

            Unexpected();
            return new ErrorExpression(start, _prevEnd);
        }

        int calleeStart = _tok.Start;
        Expression callee;
        if (Is(TokenType.New))
        {
            Enter();
            callee = ParseNew();
            Leave();
        }
        else if (Is(TokenType.Super))
        {
            callee = ParseSuper();
        }
        else if (Is(TokenType.Import))
        {
            Token next = PeekToken();
            if (next.Type == TokenType.LParen)
            {
                Unexpected();
                Advance();
                callee = new ErrorExpression(calleeStart, _prevEnd);
            }
            else
            {
                callee = ParseImportExpression();
            }
        }
        else
        {
            callee = ParsePrimary(new CoverErrors());
        }

        callee = ParseSubscripts(callee, calleeStart, noCalls: true);
        var args = new List<Node>();
        if (Is(TokenType.LParen))
        {
            args = ParseArguments(out _, calleeStart);
        }

        return new NewExpression(start, _prevEnd, callee, args);
    }

    private SuperExpression ParseSuper()
    {
        int start = _tok.Start;
        Token t = _tok;
        Advance();
        if (Is(TokenType.LParen))
        {
            if (!_fn.AllowSuperCall)
            {
                Report("super-unexpected", Messages.SuperUnexpected, t.Start, t.End);
            }
        }
        else if (Is(TokenType.Dot) || Is(TokenType.LBracket))
        {
            if (!_fn.AllowSuperProperty)
            {
                Report("super-unexpected", Messages.SuperUnexpected, t.Start, t.End);
            }
        }
        else
        {
            Report("super-unexpected", Messages.SuperUnexpected, t.Start, t.End);
        }

        MarkUsesThis();
        return new SuperExpression(start, t.End);
    }

    private Expression ParseImportExpression()
    {
        int start = _tok.Start;
        Advance(); // import
        if (Is(TokenType.Dot))
        {
            Advance();
            if (IsContextual("meta"))
            {
                if (!_module)
                {
                    Report("import-meta", Messages.ImportMetaOutsideModule, start, _tok.End);
                }

                Advance();
                return new MetaProperty(start, _prevEnd, "import", "meta");
            }

            Unexpected();
            return new ErrorExpression(start, _prevEnd);
        }

        if (!Is(TokenType.LParen))
        {
            Unexpected();
            return new ErrorExpression(start, _prevEnd);
        }

        Advance();
        Expression source = ParseAssignment(noIn: false);
        Expression? options = null;
        if (Eat(TokenType.Comma) && !Is(TokenType.RParen))
        {
            options = ParseAssignment(noIn: false);
            Eat(TokenType.Comma);
        }

        Expect(TokenType.RParen);
        return new ImportCall(start, _prevEnd, source, options);
    }

    private void MarkUsesThis()
    {
        for (FnState? f = _fn; f is not null; f = f.Parent)
        {
            f.UsesThis = true;
            if (!f.IsArrow)
            {
                break;
            }
        }
    }

    private void MarkUsesArguments()
    {
        for (FnState? f = _fn; f is not null; f = f.Parent)
        {
            f.UsesArguments = true;
            if (!f.IsArrow)
            {
                break;
            }
        }
    }

    // ---- identifiers -----------------------------------------------------------------------

    /// <summary>Checks an identifier token used as an IdentifierReference or label (ECMA-262 13.1.1 Early Errors).</summary>
    private void CheckIdentifierReference(in Token t)
    {
        if (t.Type != TokenType.Identifier)
        {
            return;
        }

        string name = t.Text!;
        if (t.Escaped && Keywords.IsReserved(name))
        {
            Report("escaped-keyword", Messages.EscapedKeyword, t.Start, t.End);
            return;
        }

        switch (name)
        {
            case "yield":
                if (_strict)
                {
                    Report("strict-reserved-word", Messages.UnexpectedStrictReserved, t.Start, t.End);
                }
                else if (_fn.IsGenerator)
                {
                    Report("unexpected-identifier", "Unexpected identifier 'yield'", t.Start, t.End);
                }

                break;
            case "await":
                if (_module || _fn.IsAsync || _fn.InStaticBlock)
                {
                    Report("reserved-word", Messages.UnexpectedReserved, t.Start, t.End);
                }
                else if (_fn.AwaitIdentPos < 0)
                {
                    _fn.AwaitIdentPos = t.Start;
                }

                break;
            case "implements" or "interface" or "let" or "package" or "private" or "protected" or "public" or "static":
                if (_strict)
                {
                    Report("strict-reserved-word", Messages.UnexpectedStrictReserved, t.Start, t.End);
                }

                break;
        }
    }

    /// <summary>Checks an identifier token used as a BindingIdentifier (adds the strict-mode <c>eval</c>/<c>arguments</c> rule, 13.1.1).</summary>
    private void CheckBindingIdentifier(in Token t)
    {
        CheckIdentifierReference(t);
        if (_strict && t.Type == TokenType.Identifier && t.Text is "eval" or "arguments")
        {
            Report("strict-eval-arguments", Messages.UnexpectedEvalOrArguments, t.Start, t.End);
        }
    }

    // ---- primary expressions ---------------------------------------------------------------

    private Expression ParsePrimary(CoverErrors ce)
    {
        Token t = _tok;
        int start = t.Start;
        switch (t.Type)
        {
            case TokenType.Identifier:
                return ParseIdentifierPrimary();
            case TokenType.Number:
                Advance();
                CheckNumberToken(t);
                return new NumberLiteral(start, t.End, t.Number);
            case TokenType.BigInt:
                Advance();
                return new BigIntLiteral(start, t.End, ParseBigInt(t.Text!));
            case TokenType.String:
                Advance();
                CheckStringToken(t);
                return new StringLiteral(start, t.End, t.Text!);
            case TokenType.Template:
                return ParseTemplate(tagged: false);
            case TokenType.True:
                Advance();
                return new BooleanLiteral(start, t.End, true);
            case TokenType.False:
                Advance();
                return new BooleanLiteral(start, t.End, false);
            case TokenType.Null:
                Advance();
                return new NullLiteral(start, t.End);
            case TokenType.This:
                Advance();
                MarkUsesThis();
                return new ThisExpression(start, t.End);
            case TokenType.Slash:
            case TokenType.SlashAssign:
                return ParseRegExp();
            case TokenType.LParen:
                return ParseParenthesized(ce);
            case TokenType.LBracket:
                return ParseArrayLiteral(ce);
            case TokenType.LBrace:
                return ParseObjectLiteral(ce);
            case TokenType.Function:
                return ParseFunctionExpression(start, isAsync: false);
            case TokenType.Class:
                return ParseClassExpression();
            case TokenType.New:
                return ParseNew();
            default:
                {
                    Diagnostic? d = Unexpected();
                    if (d is not null && t.Type != TokenType.EndOfInput && t.Type != TokenType.Invalid)
                    {
                        d.PrimaryLabel = string.Concat("expected an expression before `", _s.AsSpan(t.Start, t.End - t.Start), "`");
                        AddExpressionHelp(d);
                    }

                    return new ErrorExpression(start, start);
                }
        }
    }

    // Help for a missing operand: names what is incomplete.
    private void AddExpressionHelp(Diagnostic d)
    {
        if (_prevEnd <= 0)
        {
            return;
        }

        // Only a bounded window before the token is searched: scanning the whole prefix for every error
        // would make a long file full of errors quadratic. A leading newline stands for the file start.
        int from = Math.Max(0, _prevEnd - 300);
        string before = string.Concat(from == 0 ? "\n" : "", _s.AsSpan(from, _prevEnd - from));
        System.Text.RegularExpressions.Match decl = System.Text.RegularExpressions.Regex.Match(before, @"[;{}\n]\s*(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=$");
        if (decl.Success)
        {
            d.Help.Add("give `" + decl.Groups[1].Value + "` a value, or remove the declaration");
        }
        else if (_prevEnd - _prevStart is >= 1 and <= 3 && "+-*/%&|^<>=!?,:".Contains(_s[_prevStart], StringComparison.Ordinal))
        {
            d.Help.Add(string.Concat("complete the expression after `", _s.AsSpan(_prevStart, _prevEnd - _prevStart), "`"));
        }
    }

    private Expression ParseIdentifierPrimary()
    {
        Token t = _tok;
        int start = t.Start;
        if (!t.Escaped && t.Text == "async")
        {
            Token next = PeekToken();
            if (!next.NewlineBefore)
            {
                if (next.Type == TokenType.Function)
                {
                    Advance();
                    return ParseFunctionExpression(start, isAsync: true);
                }

                if (next.Type == TokenType.Identifier)
                {
                    // `async x => ...`
                    Advance();
                    return ParseAsyncArrowSingleParam(start);
                }

                if (next.Type == TokenType.LParen)
                {
                    return ParseAsyncCallOrArrow(start);
                }
            }
        }

        Token after = PeekToken();
        if (after.Type == TokenType.Arrow)
        {
            return ParseArrowFromIdentifier(start);
        }

        CheckIdentifierReference(t);
        Advance();
        if (t.Text == "arguments")
        {
            NoteArgumentsReference(t);
        }

        return new Identifier(start, t.End, t.Text!);
    }

    private void NoteArgumentsReference(in Token t)
    {
        if (_fn.InClassFieldInit)
        {
            Report("arguments-in-field", Messages.ArgumentsInClassField, t.Start, t.End);
        }

        MarkUsesArguments();
    }

    private void CheckNumberToken(in Token t)
    {
        if (_strict)
        {
            if ((t.Flags & TokenFlags.LegacyOctal) != 0)
            {
                Report("octal-literal", Messages.OctalLiteralStrict, t.Start, t.End);
            }
            else if ((t.Flags & TokenFlags.LeadingZeroDecimal) != 0)
            {
                Report("octal-literal", Messages.LeadingZeroStrict, t.Start, t.End);
            }
        }
    }

    private void CheckStringToken(in Token t)
    {
        if (_strict && t.AuxStart >= 0)
        {
            bool eightNine = (t.Flags & TokenFlags.NonOctalEscape) != 0 && (t.Flags & TokenFlags.OctalEscape) == 0;
            Report("octal-escape", eightNine ? Messages.EightNineStrict : Messages.OctalEscapeStrict, t.AuxStart, t.AuxStart + 2);
        }
    }

    private static BigInteger ParseBigInt(string text)
    {
        int radix = 10;
        int from = 0;
        if (text.Length > 2 && text[0] == '0' && (text[1] | 0x20) is 'x' or 'o' or 'b')
        {
            radix = (text[1] | 0x20) switch { 'x' => 16, 'o' => 8, _ => 2 };
            from = 2;
        }

        if (radix == 10)
        {
            return BigInteger.Parse(text, NumberStyles.None, NumberFormatInfo.InvariantInfo);
        }

        BigInteger v = BigInteger.Zero;
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            int d = c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
            v = (v * radix) + d;
        }

        return v;
    }

    private RegExpLiteral ParseRegExp()
    {
        Token slash = _tok;
        _hasNext = false;
        Token r = _lex.RescanRegExp(slash);
        _tok = r;
        Advance();
        string body = r.Raw!;
        string flags = r.Text!;
        if ((r.Flags & TokenFlags.Unterminated) == 0)
        {
            ValidateRegExp(r, body, flags);
        }

        return new RegExpLiteral(r.Start, r.End, body, flags);
    }

    private void ValidateRegExp(in Token r, string body, string flags)
    {
        bool flagsOk = true;
        int seen = 0;
        foreach (char c in flags)
        {
            int bit = c switch { 'd' => 1, 'g' => 2, 'i' => 4, 'm' => 8, 's' => 16, 'u' => 32, 'v' => 64, 'y' => 128, _ => 0 };
            if (bit == 0 || (seen & bit) != 0)
            {
                flagsOk = false;
                break;
            }

            seen |= bit;
        }

        if (flagsOk && (seen & 32) != 0 && (seen & 64) != 0)
        {
            flagsOk = false;
        }

        if (!flagsOk)
        {
            Report("invalid-regexp-flags", Messages.InvalidRegExpFlags, r.Start, r.End);
            return;
        }

        string? error = _regExpValidator?.Invoke(body, flags);
        if (error is not null)
        {
            Report("invalid-regexp", Messages.Format(Messages.InvalidRegExpPrefix, body, flags, error), r.Start, r.End);
        }
    }

    private TemplateLiteral ParseTemplate(bool tagged)
    {
        int start = _tok.Start;
        var quasis = new List<TemplateElement>();
        var exprs = new List<Expression>();
        while (true)
        {
            Token t = _tok;
            if ((t.Flags & TokenFlags.InvalidEscape) != 0 && !tagged)
            {
                Report("invalid-escape", t.Aux!, t.AuxStart, t.AuxStart + 2);
            }

            bool tail = (t.Flags & TokenFlags.TemplateTail) != 0;
            quasis.Add(new TemplateElement(t.Start, t.End, t.Text, t.Raw ?? "", tail));
            Advance();
            if (tail)
            {
                break;
            }

            exprs.Add(ParseExpression(noIn: false));
            if (!Is(TokenType.RBrace))
            {
                Diagnostic? d = Unexpected();
                NoteInserted(d, _tok.Start, "}");
                break;
            }

            _hasNext = false;
            _tok = _lex.RescanTemplateContinuation(_tok);
            if (_tok.Type != TokenType.Template)
            {
                break;
            }
        }

        return new TemplateLiteral(start, _prevEnd, quasis, exprs);
    }

    private ArrayLiteral ParseArrayLiteral(CoverErrors ce)
    {
        int start = _tok.Start;
        Advance();
        var elements = new List<Node?>();
        bool spreadTrailing = false;
        while (!Is(TokenType.RBracket))
        {
            if (Is(TokenType.EndOfInput))
            {
                break;
            }

            if (Is(TokenType.Comma))
            {
                Advance();
                elements.Add(null);
                continue;
            }

            int before = _tok.Start;
            Node el;
            if (Is(TokenType.Ellipsis))
            {
                int s = _tok.Start;
                Advance();
                Expression a = ParseAssignment(noIn: false, ce);
                el = new SpreadElement(s, a.End, a);
            }
            else
            {
                el = ParseAssignment(noIn: false, ce);
            }

            elements.Add(el);
            if (Is(TokenType.RBracket))
            {
                break;
            }

            if (!Is(TokenType.Comma))
            {
                Diagnostic? d = Unexpected();
                NoteInserted(d, _prevEnd, ",");
                if (_tok.Start == before || _tok.Type is TokenType.Semicolon or TokenType.RBrace or TokenType.RParen)
                {
                    break;
                }

                continue;
            }

            Advance();
            if (el is SpreadElement && !Is(TokenType.RBracket))
            {
                // `[...a, b]` is a fine literal but never a pattern; only a trailing comma right after is flagged below.
            }

            if (el is SpreadElement && Is(TokenType.RBracket))
            {
                spreadTrailing = true;
            }
        }

        Expect(TokenType.RBracket);
        var lit = new ArrayLiteral(start, _prevEnd, elements);
        if (spreadTrailing)
        {
            _spreadWithTrailingComma.Add(lit);
        }

        return lit;
    }
}
