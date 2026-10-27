using System.Runtime.CompilerServices;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

/// <summary>Thrown inside the parser to stop at a limit; always caught by <see cref="Parser.Run"/>.</summary>
internal sealed class ParseAbortException : Exception
{
}

/// <summary>
/// The recovering parser: recursive descent over <see cref="Lexer"/> tokens that records every syntax error,
/// continues after it, and builds scopes and checks the early errors of ECMA-262 as it goes.
/// </summary>
internal sealed partial class Parser
{
    private readonly SourceText _src;
    private readonly string _s;
    private readonly ParseOptions _opt;
    private readonly CancellationToken _ct;
    private readonly Lexer _lex;
    private readonly List<Diagnostic> _diags = [];
    private readonly HashSet<int> _errorStarts = [];
    private readonly bool _module;

    private Token _tok;
    private Token _next;
    private bool _hasNext;
    private int _prevEnd;
    private int _prevStart;
    private int _tokenCount;
    private int _prevAwaitStart = -1;
    private int _depth;
    private bool _strict;
    private Scope _scope = null!;
    private FnState _fn = null!;
    private RegExpLiteralValidator? _regExpValidator;
    private FunctionNode? _asyncBody;

    internal Parser(SourceText source, ParseOptions options, CancellationToken ct)
    {
        _src = source;
        _s = source.Text;
        _opt = options;
        _ct = ct;
        _module = options.Goal == ParseGoal.Module;
        _lex = new Lexer(_s, _module, LexError);
        _regExpValidator = options.RegExpValidator;
    }

    internal ParseResult Run()
    {
        Program program;
        try
        {
            program = ParseProgram();
        }
        catch (ParseAbortException)
        {
            program = new Program(0, _s.Length, [], _module, _strict, _scope ?? new Scope(ScopeKind.Global, null, 0));
        }

        SyntaxHints.Apply(_diags);
        return new ParseResult(program, _src, _diags, _asyncBody);
    }

    // ---- tokens ----------------------------------------------------------------------------

    private void Advance()
    {
        _prevEnd = _tok.End;
        _prevStart = _tok.Start;
        if (_sinceSyntaxError < 1000)
        {
            _sinceSyntaxError++;
        }
        _prevAwaitStart = _tok.Type == TokenType.Identifier && !_tok.Escaped && _tok.Text == "await" ? _tok.Start : -1;
        if (_hasNext)
        {
            _tok = _next;
            _hasNext = false;
        }
        else
        {
            _tok = _lex.Next();
        }

        if ((++_tokenCount & 0xFF) == 0)
        {
            _ct.ThrowIfCancellationRequested();
        }
    }

    private Token PeekToken()
    {
        if (!_hasNext)
        {
            _next = _lex.Next();
            _hasNext = true;
        }

        return _next;
    }

    private bool Is(TokenType t) => _tok.Type == t;

    private bool Eat(TokenType t)
    {
        if (_tok.Type == t)
        {
            Advance();
            return true;
        }

        return false;
    }

    /// <summary>True for the identifier token with exactly this name and no escapes (a contextual keyword).</summary>
    private bool IsContextual(string name) => _tok.Type == TokenType.Identifier && !_tok.Escaped && _tok.Text == name;

    private static bool IsContextual(in Token t, string name) => t.Type == TokenType.Identifier && !t.Escaped && t.Text == name;

    // ---- diagnostics -----------------------------------------------------------------------

    // Tokens after a lexical error are usually garbage (an unclosed string swallows the rest of the line,
    // typographic quotes come in pairs), so errors on the same and the next line are not reported.
    private int _suppressThroughLine = -1;

    private void LexError(int start, int end, string code, string message)
    {
        if (code == "unterminated-template")
        {
            Span last = LastContentSpan();
            start = Math.Min(start, last.Start);
            end = last.End;
        }

        if (Report(code, message, start, end, syntactic: true) is not null)
        {
            _suppressThroughLine = _src.GetPosition(start).Line + 1;
        }
    }

    private int _sinceSyntaxError = int.MaxValue / 2;
    private const int ErrorCooldownTokens = 3;
    private const int SameLineCooldownTokens = 12;
    private int _lastSyntaxErrorLine = -1;

    private Diagnostic? Report(string code, string message, int start, int end, bool syntactic = false)
    {
        // Errors within a few tokens of a parse failure are its fallout, not new mistakes.
        if (_sinceSyntaxError < ErrorCooldownTokens
            || (_sinceSyntaxError < SameLineCooldownTokens && code != "unexpected-end" && _src.GetPosition(start).Line == _lastSyntaxErrorLine))
        {
            return null;
        }

        // An early error (not a grammar error) a line or two after a syntax error is usually the same mistake seen again:
        // `def f(x):` followed by a `return` is one problem, not two.
        if (!syntactic && _lastSyntaxErrorLine > 0)
        {
            int line = _src.GetPosition(start).Line;
            if (line >= _lastSyntaxErrorLine && line - _lastSyntaxErrorLine <= 2)
            {
                return null;
            }
        }

        if (end < start)
        {
            end = start;
        }

        if (_suppressThroughLine >= 0 && _src.GetPosition(start).Line <= _suppressThroughLine)
        {
            return null;
        }

        // All end-of-input errors are one error however the parser words its position.
        if (!_errorStarts.Add(code == "unexpected-end" ? -1 : start))
        {
            return null;
        }

        var d = new Diagnostic("SyntaxError", code, message, _src, new Span(start, end));
        _diags.Add(d);
        if (syntactic)
        {
            _sinceSyntaxError = 0;
            _lastSyntaxErrorLine = _src.GetPosition(start).Line;
        }

        if (_diags.Count >= _opt.MaxErrors)
        {
            throw new ParseAbortException();
        }

        return d;
    }

    private Diagnostic? ReportSpan(string code, string message, Span span) => Report(code, message, span.Start, span.End);

    private Diagnostic? ReportAt(string code, string message, Node n) => Report(code, message, n.Start, n.End);

    /// <summary>V8's wording for an unexpected token (what <c>err.message</c> says).</summary>
    private string UnexpectedMessage(in Token t)
    {
        switch (t.Type)
        {
            case TokenType.EndOfInput:
                return Messages.UnexpectedEnd;
            case TokenType.Number:
            case TokenType.BigInt:
                return Messages.UnexpectedNumber;
            case TokenType.String:
                return Messages.UnexpectedString;
            case TokenType.Template:
                return Messages.UnexpectedTemplate;
            case TokenType.Enum:
                return Messages.UnexpectedReserved;
            case TokenType.RegExp:
                return "Unexpected regular expression";
            case TokenType.PrivateName:
                return "Unexpected identifier '#" + t.Text + "'";
            case TokenType.Identifier:
                if (_strict && Keywords.IsStrictReserved(t.Text!))
                {
                    return Messages.UnexpectedStrictReserved;
                }

                if (t.Text == "await" && (_module || _fn.IsAsync))
                {
                    return Messages.UnexpectedReserved;
                }

                return "Unexpected identifier '" + t.Text + "'";
            default:
                return string.Concat("Unexpected token '", _s.AsSpan(t.Start, t.End - t.Start), "'");
        }
    }

    /// <summary>Reports the current token as unexpected, unless the lexer already reported it.</summary>
    private Diagnostic? Unexpected() => Unexpected(_tok);

    private Diagnostic? Unexpected(in Token t)
    {
        if (t.Type == TokenType.Invalid)
        {
            return null;
        }

        if (_prevAwaitStart >= 0 && !t.NewlineBefore && t.Type != TokenType.EndOfInput && !_fn.IsAsync)
        {
            // `await x` outside an async function: V8 explains the likely intent rather than blaming `x`.
            return Report("await-outside-async", Messages.AwaitOutsideAsync, _prevAwaitStart, _prevAwaitStart + 5, syntactic: true);
        }

        if (t.Type == TokenType.EndOfInput)
        {
            // Point at the last line of code rather than at the empty position after the final newline.
            Span last = LastContentSpan();
            return Report("unexpected-end", Messages.UnexpectedEnd, last.Start, last.End, syntactic: true);
        }

        return Report("unexpected-token", UnexpectedMessage(t), t.Start, t.End, syntactic: true);
    }

    private Span LastContentSpan()
    {
        int end = _s.Length;
        while (end > 0 && (char.IsWhiteSpace(_s[end - 1]) || SourceText.IsLineTerminator(_s[end - 1])))
        {
            end--;
        }

        int start = end;
        while (start > 0 && !SourceText.IsLineTerminator(_s[start - 1]))
        {
            start--;
        }

        while (start < end && char.IsWhiteSpace(_s[start]))
        {
            start++;
        }

        return new Span(start, end);
    }

    /// <summary>Records that the parser carried on as if <paramref name="text"/> had been written at the current token.</summary>
    private static void NoteInserted(Diagnostic? d, int offset, string text) =>
        d?.Recovery.Add(new RecoveryAction(RecoveryKind.InsertedToken, new Span(offset, offset), text));

    private static void NoteSkipped(Diagnostic? d, int start, int end) =>
        d?.Recovery.Add(new RecoveryAction(RecoveryKind.SkippedTokens, new Span(start, end), ""));

    /// <summary>Consumes a token of the given type or reports it as missing and carries on as if it were there.</summary>
    private bool Expect(TokenType t)
    {
        if (_tok.Type == t)
        {
            Advance();
            return true;
        }

        Diagnostic? d = Unexpected();
        NoteInserted(d, _tok.Start, PunctText(t));
        return false;
    }

    /// <summary>
    /// Consumes the <c>)</c> that ends a statement head (<c>if (...)</c>, <c>while (...)</c>, <c>for (...)</c>, <c>with (...)</c>).
    /// When something else is there the error is reported once, and the rest of the head up to its matching <c>)</c> is skipped
    /// on bracket structure, so the body is parsed as a body instead of as the fallout of a broken head. The skip stays on
    /// the head's line, stops at a <c>{</c> or a closer that belongs to an enclosing construct, and never crosses a template.
    /// </summary>
    private void ExpectCloseParenResync()
    {
        if (Is(TokenType.RParen))
        {
            Advance();
            return;
        }

        Diagnostic? d = Unexpected();
        NoteInserted(d, _tok.Start, ")");
        int from = _tok.Start;
        int depth = 0;
        while (true)
        {
            TokenType t = _tok.Type;
            if (t is TokenType.EndOfInput or TokenType.Template or TokenType.Invalid || (_tok.NewlineBefore && from != _tok.Start))
            {
                break;
            }

            if (t is TokenType.LParen or TokenType.LBracket || (t == TokenType.LBrace && depth > 0))
            {
                depth++;
            }
            else if (t is TokenType.RParen or TokenType.RBracket or TokenType.RBrace)
            {
                if (depth == 0)
                {
                    if (t == TokenType.RParen)
                    {
                        NoteSkipped(d, from, _tok.End);
                        Advance();
                    }

                    break;
                }

                depth--;
            }
            else if (t == TokenType.LBrace && depth == 0)
            {
                break;
            }

            Advance();
        }
    }

    private static string PunctText(TokenType t) => t switch
    {
        TokenType.LBrace => "{",
        TokenType.RBrace => "}",
        TokenType.LParen => "(",
        TokenType.RParen => ")",
        TokenType.LBracket => "[",
        TokenType.RBracket => "]",
        TokenType.Semicolon => ";",
        TokenType.Comma => ",",
        TokenType.Colon => ":",
        TokenType.Arrow => "=>",
        TokenType.Assign => "=",
        _ => "",
    };

    /// <summary>ECMA-262 12.10 automatic semicolon insertion: a semicolon is optional before <c>}</c>, at the end of input, and after a line break.</summary>
    private void ConsumeSemicolon()
    {
        if (_tok.Type == TokenType.Semicolon)
        {
            Advance();
            return;
        }

        if (_tok.Type == TokenType.RBrace || _tok.Type == TokenType.EndOfInput || _tok.NewlineBefore)
        {
            return;
        }

        Diagnostic? d = Unexpected();
        NoteInserted(d, _prevEnd, ";");
    }

    private void Enter()
    {
        if (++_depth > _opt.MaxDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            Report("depth-limit", Messages.DepthLimit, _tok.Start, Math.Max(_tok.End, _tok.Start + 1));
            throw new ParseAbortException();
        }
    }

    private void Leave() => _depth--;

    // ---- scopes ----------------------------------------------------------------------------

    private Scope PushScope(ScopeKind kind, int start)
    {
        var s = new Scope(kind, _scope, start) { IsStrict = _strict };
        _scope?.Children.Add(s);
        _scope = s;
        return s;
    }

    private void PopScope()
    {
        Scope s = _scope;
        if (s.HoistCandidates is not null)
        {
            ResolveAnnexBHoisting(s);
        }

        _scope = s.Parent!;
    }

    private void MarkDirectEval()
    {
        for (Scope? s = _scope; s is not null && !s.ContainsDirectEval; s = s.Parent)
        {
            s.ContainsDirectEval = true;
        }

        for (FnState? f = _fn; f is not null; f = f.Parent)
        {
            f.HasDirectEval = true;
            if (!f.IsArrow)
            {
                break;
            }
        }
    }

    private void Redeclared(string name, Span span, Binding? first)
    {
        Diagnostic? d = ReportSpan("redeclaration", Messages.Format(Messages.DuplicateBinding, name), span);
        if (d is not null && first is not null)
        {
            d.SecondaryLabels.Add(new Label(first.Span, "first declared here"));
            d.PrimaryLabel = "declared again here";
        }
    }

    /// <summary>Declares a let, const, class or import binding in the current scope (ECMA-262 14.2.1, 15.1.1 and the matching Early Errors).</summary>
    private Binding? DeclareLexical(string name, BindingKind kind, Span span)
    {
        Scope s = _scope;
        Binding? existing = s.Lookup(name);
        if (existing is not null)
        {
            Redeclared(name, span, existing);
            return null;
        }

        if (s.VarThrough is { } through && through.Contains(name))
        {
            Redeclared(name, span, null);
            return null;
        }

        return s.Add(name, kind, span);
    }

    /// <summary>Declares a <c>var</c> (ECMA-262 14.3.2 Early Errors: no clash with lexical names of any enclosing block up to the function).</summary>
    private void DeclareVar(string name, Span span, bool forOfHead = false)
    {
        for (Scope s = _scope; ; s = s.Parent!)
        {
            Binding? existing = s.Lookup(name);
            if (s.IsVarScope)
            {
                if (existing is null)
                {
                    s.Add(name, BindingKind.Var, span);
                }
                else if (existing.IsLexical)
                {
                    Redeclared(name, span, existing);
                }
                else if (existing.Kind == BindingKind.Parameter)
                {
                    existing.AlsoVar = true;
                }

                return;
            }

            if (existing is not null)
            {
                // B.3.4: var may redeclare a simple catch parameter, except as the head of a for-of.
                if (existing.Kind == BindingKind.CatchParameter && s.SimpleCatchParameter && !forOfHead)
                {
                    // allowed
                }
                else
                {
                    Redeclared(name, span, existing);
                    return;
                }
            }

            (s.VarThrough ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
        }
    }

    private void DeclareParameter(string name, Span span)
    {
        // Duplicates are checked after the body is known (they depend on strictness and parameter kinds).
        _scope.Add(name, BindingKind.Parameter, span);
    }

    /// <summary>Declares a function declaration's name according to where it appears.</summary>
    private void DeclareFunctionName(FunctionDeclaration decl, string name, Span span, bool isPlain)
    {
        Scope s = _scope;
        if (s.IsVarScope && s.Kind != ScopeKind.Module)
        {
            Binding? existing = s.Lookup(name);
            if (existing is null)
            {
                existing = s.Add(name, BindingKind.Function, span);
            }
            else if (existing.IsLexical)
            {
                Redeclared(name, span, existing);
                return;
            }
            else if (existing.Kind == BindingKind.Var)
            {
                existing.Kind = BindingKind.Function;
            }
            else if (existing.Kind == BindingKind.Parameter)
            {
                existing.AlsoVar = true;
            }

            if (existing.Kind == BindingKind.Function)
            {
                existing.FunctionDeclaration = decl;
            }

            return;
        }

        Binding? prior = s.Lookup(name);
        if (prior is not null)
        {
            // B.3.2.4: in sloppy code two plain function declarations may share a name in one block.
            if (!(prior.Kind == BindingKind.LexicalFunction && prior.FunctionDeclaration is not null && !_strict && isPlain && IsPlainFunction(prior.FunctionDeclaration) && s.Kind != ScopeKind.Module))
            {
                Redeclared(name, span, prior);
                return;
            }

            prior.FunctionDeclaration = decl;
        }
        else
        {
            if (s.VarThrough is { } through && through.Contains(name))
            {
                Redeclared(name, span, null);
                return;
            }

            Binding b = s.Add(name, BindingKind.LexicalFunction, span);
            b.FunctionDeclaration = decl;
        }

        if (!_strict && isPlain && s.Kind != ScopeKind.Module)
        {
            Scope v = s;
            while (!v.IsVarScope)
            {
                v = v.Parent!;
            }

            (v.HoistCandidates ??= []).Add((decl, s, name, span));
        }
    }

    private static bool IsPlainFunction(FunctionDeclaration d) => !d.Function.IsAsync && !d.Function.IsGenerator;

    // B.3.2.1 Changes to FunctionDeclarationInstantiation: a block-level function also gets a var binding
    // unless a var of the same name would be an early error.
    private static void ResolveAnnexBHoisting(Scope varScope)
    {
        foreach (var (decl, block, name, span) in varScope.HoistCandidates!)
        {
            bool conflict = false;
            for (Scope s = block.Parent!; ; s = s.Parent!)
            {
                Binding? b = s.Lookup(name);
                if (b is not null)
                {
                    if (s.IsVarScope)
                    {
                        if (b.IsLexical)
                        {
                            conflict = true;
                        }
                    }
                    else if (b.Kind == BindingKind.CatchParameter)
                    {
                        if (!s.SimpleCatchParameter)
                        {
                            conflict = true;
                        }
                    }
                    else
                    {
                        conflict = true;
                    }
                }

                if (conflict || s.IsVarScope)
                {
                    break;
                }
            }

            if (conflict)
            {
                continue;
            }

            Binding? top = varScope.Lookup(name);
            if (top is { Kind: BindingKind.Parameter })
            {
                continue;
            }

            decl.IsAnnexBHoisted = true;
            if (top is null)
            {
                varScope.Add(name, BindingKind.AnnexBFunctionVar, span);
            }
        }

        varScope.HoistCandidates = null;
    }
}
