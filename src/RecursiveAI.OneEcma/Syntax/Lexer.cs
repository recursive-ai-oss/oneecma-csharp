using System.Globalization;
using System.Text;
using RecursiveAI.OneEcma.Unicode;

namespace RecursiveAI.OneEcma.Syntax;

/// <summary>Receives lexical errors as they are found; the lexer carries on with a best-effort token.</summary>
internal delegate void LexErrorHandler(int start, int end, string code, string message);

/// <summary>
/// Scans ECMAScript source (ECMA-262 12) into tokens on demand. The parser decides when a
/// <c>/</c> starts a regular expression and when a <c>}</c> continues a template, because only it
/// knows the grammar goal (12.1 InputElementDiv, InputElementRegExp, InputElementTemplateTail).
/// </summary>
internal sealed class Lexer
{
    private readonly string _s;
    private readonly bool _module;
    private readonly LexErrorHandler _error;
    private int _pos;
    private bool _lineStart = true;

    internal Lexer(string source, bool module, LexErrorHandler error)
    {
        _s = source;
        _module = module;
        _error = error;
        // Annex B / 12.5 Hashbang comment: only at the very start of the source.
        if (source.Length >= 2 && source[0] == '#' && source[1] == '!')
        {
            _pos = 2;
            SkipToLineEnd();
        }
    }

    internal int Position => _pos;

    /// <summary>Scans the next token in the InputElementDiv goal.</summary>
    internal Token Next()
    {
        TokenFlags flags = SkipTrivia();
        return ScanToken(flags);
    }

    /// <summary>Rescans a <c>/</c> or <c>/=</c> token as a regular expression literal.</summary>
    internal Token RescanRegExp(Token slash)
    {
        _pos = slash.Start + 1;
        int bodyStart = _pos;
        bool inClass = false;
        while (true)
        {
            if (_pos >= _s.Length || IsLineTerminator(_s[_pos]))
            {
                _error(slash.Start, _pos, "unterminated-regexp", Messages.UnterminatedRegExp);
                return new Token(TokenType.RegExp, slash.Start, _pos, slash.Flags | TokenFlags.Unterminated, "", _s.Substring(bodyStart, _pos - bodyStart));
            }

            char c = _s[_pos];
            if (c == '\\')
            {
                _pos++;
                if (_pos >= _s.Length || IsLineTerminator(_s[_pos]))
                {
                    continue;
                }

                _pos++;
                continue;
            }

            if (c == '[')
            {
                inClass = true;
            }
            else if (c == ']')
            {
                inClass = false;
            }
            else if (c == '/' && !inClass)
            {
                break;
            }

            _pos++;
        }

        string body = _s.Substring(bodyStart, _pos - bodyStart);
        _pos++;
        int flagsStart = _pos;
        bool bad = false;
        while (_pos < _s.Length)
        {
            int cp = CodePointAt(_pos);
            if (cp == '\\')
            {
                bad = true;
                _pos++;
                continue;
            }

            if (!CharClass.IsIdContinue(cp))
            {
                break;
            }

            _pos += cp > 0xFFFF ? 2 : 1;
        }

        string rflags = _s.Substring(flagsStart, _pos - flagsStart);
        if (bad)
        {
            _error(slash.Start, _pos, "invalid-regexp-flags", Messages.InvalidRegExpFlags);
        }

        return new Token(TokenType.RegExp, slash.Start, _pos, slash.Flags & TokenFlags.NewlineBefore, rflags, body);
    }

    /// <summary>Rescans from a <c>}</c> token as the continuation of a template literal.</summary>
    internal Token RescanTemplateContinuation(Token rbrace)
    {
        _pos = rbrace.Start + 1;
        return ScanTemplatePart(rbrace.Start, rbrace.Flags & TokenFlags.NewlineBefore);
    }

    private static bool IsLineTerminator(char c) => c is '\n' or '\r' or '\u2028' or '\u2029';

    private int CodePointAt(int i)
    {
        char c = _s[i];
        if (char.IsHighSurrogate(c) && i + 1 < _s.Length && char.IsLowSurrogate(_s[i + 1]))
        {
            return char.ConvertToUtf32(c, _s[i + 1]);
        }

        return c;
    }

    private void SkipToLineEnd()
    {
        while (_pos < _s.Length && !IsLineTerminator(_s[_pos]))
        {
            _pos++;
        }
    }

    // ECMA-262 12.4 Comments, 12.2 White Space, 12.3 Line Terminators, B.1.1 HTML-like Comments.
    private TokenFlags SkipTrivia()
    {
        TokenFlags flags = TokenFlags.None;
        while (_pos < _s.Length)
        {
            char c = _s[_pos];
            if (c == '\n' || c == '\r' || c == '\u2028' || c == '\u2029')
            {
                flags |= TokenFlags.NewlineBefore;
                _lineStart = true;
                _pos++;
            }
            else if (c == ' ' || c == '\t' || (c > 127 || c == '\v' || c == '\f') && CharClass.IsWhiteSpace(c))
            {
                _pos++;
            }
            else if (c == '/' && _pos + 1 < _s.Length && _s[_pos + 1] == '/')
            {
                SkipToLineEnd();
            }
            else if (c == '/' && _pos + 1 < _s.Length && _s[_pos + 1] == '*')
            {
                int start = _pos;
                _pos += 2;
                bool closed = false;
                while (_pos < _s.Length)
                {
                    char d = _s[_pos];
                    if (d == '*' && _pos + 1 < _s.Length && _s[_pos + 1] == '/')
                    {
                        _pos += 2;
                        closed = true;
                        break;
                    }

                    if (IsLineTerminator(d))
                    {
                        flags |= TokenFlags.NewlineBefore;
                        _lineStart = true;
                    }

                    _pos++;
                }

                if (!closed)
                {
                    _error(start, _pos, "invalid-token", Messages.InvalidToken);
                }
            }
            else if (!_module && c == '<' && string.CompareOrdinal(_s, _pos, "<!--", 0, 4) == 0)
            {
                // B.1.1 SingleLineHTMLOpenComment
                SkipToLineEnd();
            }
            else if (!_module && c == '-' && _lineStart && string.CompareOrdinal(_s, _pos, "-->", 0, 3) == 0)
            {
                // B.1.1 SingleLineHTMLCloseComment: only after a line terminator (or at the start).
                SkipToLineEnd();
            }
            else
            {
                break;
            }
        }

        _lineStart = false;
        return flags;
    }

    private Token ScanToken(TokenFlags flags)
    {
        if (_pos >= _s.Length)
        {
            return new Token(TokenType.EndOfInput, _s.Length, _s.Length, flags);
        }

        int start = _pos;
        char c = _s[_pos];
        switch (c)
        {
            case '{': _pos++; return Punct(TokenType.LBrace, start, flags);
            case '}': _pos++; return Punct(TokenType.RBrace, start, flags);
            case '(': _pos++; return Punct(TokenType.LParen, start, flags);
            case ')': _pos++; return Punct(TokenType.RParen, start, flags);
            case '[': _pos++; return Punct(TokenType.LBracket, start, flags);
            case ']': _pos++; return Punct(TokenType.RBracket, start, flags);
            case ';': _pos++; return Punct(TokenType.Semicolon, start, flags);
            case ',': _pos++; return Punct(TokenType.Comma, start, flags);
            case ':': _pos++; return Punct(TokenType.Colon, start, flags);
            case '~': _pos++; return Punct(TokenType.Tilde, start, flags);
            case '.':
                if (_pos + 1 < _s.Length && char.IsAsciiDigit(_s[_pos + 1]))
                {
                    return ScanNumber(flags);
                }

                if (Peek(1) == '.' && Peek(2) == '.')
                {
                    _pos += 3;
                    return Punct(TokenType.Ellipsis, start, flags);
                }

                _pos++;
                return Punct(TokenType.Dot, start, flags);
            case '<':
                _pos++;
                if (Eat('<'))
                {
                    return Punct(Eat('=') ? TokenType.ShlAssign : TokenType.Shl, start, flags);
                }

                return Punct(Eat('=') ? TokenType.Le : TokenType.Lt, start, flags);
            case '>':
                _pos++;
                if (Eat('>'))
                {
                    if (Eat('>'))
                    {
                        return Punct(Eat('=') ? TokenType.UShrAssign : TokenType.UShr, start, flags);
                    }

                    return Punct(Eat('=') ? TokenType.ShrAssign : TokenType.Shr, start, flags);
                }

                return Punct(Eat('=') ? TokenType.Ge : TokenType.Gt, start, flags);
            case '=':
                _pos++;
                if (Eat('>'))
                {
                    return Punct(TokenType.Arrow, start, flags);
                }

                if (Eat('='))
                {
                    return Punct(Eat('=') ? TokenType.EqEqEq : TokenType.EqEq, start, flags);
                }

                return Punct(TokenType.Assign, start, flags);
            case '!':
                _pos++;
                if (Eat('='))
                {
                    return Punct(Eat('=') ? TokenType.NotEqEq : TokenType.NotEq, start, flags);
                }

                return Punct(TokenType.Bang, start, flags);
            case '+':
                _pos++;
                if (Eat('+'))
                {
                    return Punct(TokenType.PlusPlus, start, flags);
                }

                return Punct(Eat('=') ? TokenType.PlusAssign : TokenType.Plus, start, flags);
            case '-':
                _pos++;
                if (Eat('-'))
                {
                    return Punct(TokenType.MinusMinus, start, flags);
                }

                return Punct(Eat('=') ? TokenType.MinusAssign : TokenType.Minus, start, flags);
            case '*':
                _pos++;
                if (Eat('*'))
                {
                    return Punct(Eat('=') ? TokenType.StarStarAssign : TokenType.StarStar, start, flags);
                }

                return Punct(Eat('=') ? TokenType.StarAssign : TokenType.Star, start, flags);
            case '/':
                _pos++;
                return Punct(Eat('=') ? TokenType.SlashAssign : TokenType.Slash, start, flags);
            case '%':
                _pos++;
                return Punct(Eat('=') ? TokenType.PercentAssign : TokenType.Percent, start, flags);
            case '&':
                _pos++;
                if (Eat('&'))
                {
                    return Punct(Eat('=') ? TokenType.AmpAmpAssign : TokenType.AmpAmp, start, flags);
                }

                return Punct(Eat('=') ? TokenType.AmpAssign : TokenType.Amp, start, flags);
            case '|':
                _pos++;
                if (Eat('|'))
                {
                    return Punct(Eat('=') ? TokenType.PipePipeAssign : TokenType.PipePipe, start, flags);
                }

                return Punct(Eat('=') ? TokenType.PipeAssign : TokenType.Pipe, start, flags);
            case '^':
                _pos++;
                return Punct(Eat('=') ? TokenType.CaretAssign : TokenType.Caret, start, flags);
            case '?':
                _pos++;
                if (Eat('?'))
                {
                    return Punct(Eat('=') ? TokenType.QuestionQuestionAssign : TokenType.QuestionQuestion, start, flags);
                }

                // 12.8 OptionalChainingPunctuator: ?. not followed by a decimal digit.
                if (Peek(0) == '.' && !(Peek(1) >= '0' && Peek(1) <= '9'))
                {
                    _pos++;
                    return Punct(TokenType.QuestionDot, start, flags);
                }

                return Punct(TokenType.Question, start, flags);
            case '"':
            case '\'':
                return ScanString(c, flags);
            case '`':
                _pos++;
                return ScanTemplatePart(start, flags);
            case '#':
                return ScanPrivateName(flags);
        }

        if (c >= '0' && c <= '9')
        {
            return ScanNumber(flags);
        }

        int cp = CodePointAt(_pos);
        if (c == '\\' || CharClass.IsIdStart(cp))
        {
            return ScanIdentifier(flags, privateName: false, start);
        }

        _pos += cp > 0xFFFF ? 2 : 1;
        _error(start, _pos, "invalid-token", Messages.InvalidToken);
        return new Token(TokenType.Invalid, start, _pos, flags);
    }

    private char Peek(int n) => _pos + n < _s.Length ? _s[_pos + n] : '\0';

    private bool Eat(char c)
    {
        if (_pos < _s.Length && _s[_pos] == c)
        {
            _pos++;
            return true;
        }

        return false;
    }

    private Token Punct(TokenType t, int start, TokenFlags flags) => new(t, start, _pos, flags);

    private Token ScanPrivateName(TokenFlags flags)
    {
        int start = _pos;
        _pos++;
        if (_pos < _s.Length && (_s[_pos] == '\\' || CharClass.IsIdStart(CodePointAt(_pos))))
        {
            return ScanIdentifier(flags, privateName: true, start);
        }

        _error(start, _pos, "invalid-token", Messages.InvalidToken);
        return new Token(TokenType.Invalid, start, _pos, flags);
    }

    // ECMA-262 12.7 Names and Keywords.
    private Token ScanIdentifier(TokenFlags flags, bool privateName, int tokenStart)
    {
        int nameStart = _pos;
        StringBuilder? sb = null;
        bool first = true;
        bool escaped = false;
        while (_pos < _s.Length)
        {
            char c = _s[_pos];
            if (c < 128 && c != '\\')
            {
                if (first ? !CharClass.IsIdStart(c) : !CharClass.IsIdContinue(c))
                {
                    break;
                }

                sb?.Append(c);
                _pos++;
            }
            else if (c == '\\')
            {
                if (sb is null)
                {
                    sb = new StringBuilder();
                    sb.Append(_s, nameStart, _pos - nameStart);
                }

                escaped = true;
                int escStart = _pos;
                int cp = ScanUnicodeEscapeInIdentifier(out bool ok);
                if (!ok)
                {
                    return new Token(TokenType.Invalid, tokenStart, _pos, flags);
                }

                if (first ? !CharClass.IsIdStart(cp) : !CharClass.IsIdContinue(cp))
                {
                    _error(escStart, _pos, "invalid-unicode-escape", Messages.InvalidToken);
                    return new Token(TokenType.Invalid, tokenStart, _pos, flags);
                }

                sb.Append(char.ConvertFromUtf32(cp));
            }
            else
            {
                int cp = CodePointAt(_pos);
                if (first ? !CharClass.IsIdStart(cp) : !CharClass.IsIdContinue(cp))
                {
                    break;
                }

                int n = cp > 0xFFFF ? 2 : 1;
                sb?.Append(_s, _pos, n);
                _pos += n;
            }

            first = false;
        }

        string name = sb?.ToString() ?? _s.Substring(nameStart, _pos - nameStart);
        if (privateName)
        {
            return new Token(TokenType.PrivateName, tokenStart, _pos, flags | (escaped ? TokenFlags.Escaped : 0), name);
        }

        if (escaped)
        {
            return new Token(TokenType.Identifier, tokenStart, _pos, flags | TokenFlags.Escaped, name);
        }

        return MakeWord(name, tokenStart, flags);
    }

    private Token MakeWord(string name, int start, TokenFlags flags)
    {
        TokenType t = Keywords.Lookup(name);
        return new Token(t == TokenType.EndOfInput ? TokenType.Identifier : t, start, _pos, flags, name);
    }

    // \uXXXX or \u{X...} inside an identifier (12.7 UnicodeEscapeSequence). Position is at the backslash.
    private int ScanUnicodeEscapeInIdentifier(out bool ok)
    {
        int start = _pos;
        ok = false;
        _pos++;
        if (!Eat('u'))
        {
            _error(start, _pos, "invalid-token", Messages.InvalidToken);
            return 0;
        }

        int cp = ScanUnicodeEscapeBody(out string? err);
        if (err is not null)
        {
            _error(start, _pos, "invalid-unicode-escape", err);
            return 0;
        }

        ok = true;
        return cp;
    }

    // After "\u": either {hex+} or four hex digits. Returns -1 with an error message when malformed.
    private int ScanUnicodeEscapeBody(out string? error)
    {
        error = null;
        if (Eat('{'))
        {
            int value = 0;
            int digits = 0;
            while (_pos < _s.Length && Uri.IsHexDigit(_s[_pos]))
            {
                value = (value * 16) + HexVal(_s[_pos]);
                if (value > 0x10FFFF)
                {
                    value = 0x110000;
                }

                digits++;
                _pos++;
            }

            if (digits == 0 || !Eat('}'))
            {
                error = Messages.InvalidUnicodeEscape;
                return -1;
            }

            if (value > 0x10FFFF)
            {
                error = Messages.UndefinedCodePoint;
                return -1;
            }

            return value;
        }

        int v = 0;
        for (int i = 0; i < 4; i++)
        {
            if (_pos >= _s.Length || !Uri.IsHexDigit(_s[_pos]))
            {
                error = Messages.InvalidUnicodeEscape;
                return -1;
            }

            v = (v * 16) + HexVal(_s[_pos]);
            _pos++;
        }

        return v;
    }

    private static int HexVal(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    // ECMA-262 12.9.3 Numeric Literals; B.1.1 legacy octal and leading-zero decimal literals.
    private Token ScanNumber(TokenFlags flags)
    {
        int start = _pos;
        char c = _s[_pos];
        TokenFlags f = flags;
        double value;
        if (c == '0' && (Peek(1) | 0x20) is 'x' or 'o' or 'b')
        {
            int radix = (Peek(1) | 0x20) switch { 'x' => 16, 'o' => 8, _ => 2 };
            _pos += 2;
            int digitsStart = _pos;
            if (!ScanDigits(radix, ref f))
            {
                _error(start, _pos, "invalid-token", Messages.InvalidToken);
                return new Token(TokenType.Invalid, start, _pos, flags);
            }

            string digits = _s.Substring(digitsStart, _pos - digitsStart).Replace("_", "", StringComparison.Ordinal);
            if (Peek(0) == 'n')
            {
                _pos++;
                return FinishNumeric(TokenType.BigInt, start, f, string.Concat(_s.AsSpan(start, 2), digits), 0);
            }

            value = ParseRadix(digits, radix);
            return FinishNumeric(TokenType.Number, start, f, null, value);
        }

        if (c == '0' && char.IsAsciiDigit(Peek(1)))
        {
            // Legacy octal (0 followed by octal digits only) or a leading-zero decimal.
            int p = _pos + 1;
            bool octal = true;
            while (p < _s.Length && char.IsAsciiDigit(_s[p]))
            {
                if (_s[p] >= '8')
                {
                    octal = false;
                }

                p++;
            }

            if (octal)
            {
                string digits = _s.Substring(start + 1, p - start - 1);
                _pos = p;
                if (Peek(0) == '_')
                {
                    _error(_pos, _pos + 1, "numeric-separator", Messages.NumericSeparatorAfterLeadingZero);
                }

                f |= TokenFlags.LegacyOctal;
                if (Peek(0) == 'n')
                {
                    _pos++;
                    _error(start, _pos, "invalid-token", Messages.InvalidToken);
                    return new Token(TokenType.Invalid, start, _pos, flags);
                }

                return FinishNumeric(TokenType.Number, start, f, null, ParseRadix(digits, 8));
            }

            f |= TokenFlags.LeadingZeroDecimal;
            // Falls through to decimal scanning: a fraction and exponent are allowed (08.5), a BigInt suffix is not.
        }

        bool isInteger = true;
        if (c != '.')
        {
            if (c == '0' && Peek(1) == '_')
            {
                _error(_pos + 1, _pos + 2, "numeric-separator", Messages.NumericSeparatorAfterLeadingZero);
            }

            ScanDecimalDigits(ref f);
        }

        if (Peek(0) == 'n' && (f & TokenFlags.LeadingZeroDecimal) == 0)
        {
            string digits = _s.Substring(start, _pos - start).Replace("_", "", StringComparison.Ordinal);
            _pos++;
            return FinishNumeric(TokenType.BigInt, start, f, digits, 0);
        }

        if (Peek(0) == '.')
        {
            isInteger = false;
            _pos++;
            if (Peek(0) == '_')
            {
                _error(_pos, _pos + 1, "numeric-separator", Messages.NumericSeparatorAfterLeadingZero);
            }

            if (char.IsAsciiDigit(Peek(0)))
            {
                ScanDecimalDigits(ref f);
            }
        }

        if ((Peek(0) | 0x20) == 'e')
        {
            int save = _pos;
            _pos++;
            if (Peek(0) is '+' or '-')
            {
                _pos++;
            }

            if (char.IsAsciiDigit(Peek(0)))
            {
                ScanDecimalDigits(ref f);
                isInteger = false;
            }
            else
            {
                _pos = save;
                _error(start, _pos + 1, "invalid-token", Messages.InvalidToken);
                _pos = save + 1;
                return new Token(TokenType.Invalid, start, _pos, flags);
            }
        }

        _ = isInteger;
        string text = _s.Substring(start, _pos - start);
        if ((f & TokenFlags.HasSeparator) != 0)
        {
            if ((f & TokenFlags.LeadingZeroDecimal) != 0)
            {
                _error(start, _pos, "numeric-separator", Messages.NumericSeparatorAfterLeadingZero);
            }

            text = text.Replace("_", "", StringComparison.Ordinal);
        }

        value = double.Parse(text, NumberStyles.Float, NumberFormatInfo.InvariantInfo);
        return FinishNumeric(TokenType.Number, start, f, null, value);
    }

    // 12.9.3: the source character immediately following a NumericLiteral must not be an IdentifierStart or a DecimalDigit.
    private Token FinishNumeric(TokenType type, int start, TokenFlags f, string? text, double value)
    {
        if (_pos < _s.Length)
        {
            int cp = CodePointAt(_pos);
            if (cp == '\\' || CharClass.IsIdStart(cp) || (cp >= '0' && cp <= '9'))
            {
                _error(start, _pos + 1, "invalid-token", Messages.InvalidToken);
                return new Token(TokenType.Invalid, start, _pos + 1, f & TokenFlags.NewlineBefore);
            }
        }

        return new Token(type, start, _pos, f, text, null, value);
    }

    private void ScanDecimalDigits(ref TokenFlags f)
    {
        ScanDigits(10, ref f, requireDigit: false);
    }

    // Scans digits of a radix with numeric separators (12.9.3 NumericLiteralSeparator). Returns false if no digit was found.
    private bool ScanDigits(int radix, ref TokenFlags f, bool requireDigit = true)
    {
        int count = 0;
        bool lastSep = false;
        while (_pos < _s.Length)
        {
            char c = _s[_pos];
            if (c == '_')
            {
                f |= TokenFlags.HasSeparator;
                if (count == 0 || lastSep)
                {
                    _error(_pos, _pos + 1, "numeric-separator", lastSep ? Messages.NumericSeparatorDouble : Messages.InvalidToken);
                    _pos++;
                    lastSep = true;
                    continue;
                }

                lastSep = true;
                _pos++;
                continue;
            }

            if (!IsDigit(c, radix))
            {
                break;
            }

            lastSep = false;
            count++;
            _pos++;
        }

        if (lastSep)
        {
            _error(_pos - 1, _pos, "numeric-separator", Messages.NumericSeparatorAtEnd);
        }

        return count > 0 || !requireDigit;
    }

    private static bool IsDigit(char c, int radix) => radix switch
    {
        2 => c is '0' or '1',
        8 => c >= '0' && c <= '7',
        10 => c >= '0' && c <= '9',
        _ => Uri.IsHexDigit(c),
    };

    /// <summary>Correctly rounded value of a digit string in radix 2, 8 or 16 (round half to even).</summary>
    private static double ParseRadix(string digits, int radix)
    {
        int bitsPer = radix == 16 ? 4 : radix == 8 ? 3 : 1;
        // Collect the significant bits, then round to 53.
        var bits = new List<bool>(digits.Length * bitsPer);
        foreach (char ch in digits)
        {
            int v = HexVal(ch);
            for (int b = bitsPer - 1; b >= 0; b--)
            {
                bits.Add(((v >> b) & 1) != 0);
            }
        }

        int first = 0;
        while (first < bits.Count && !bits[first])
        {
            first++;
        }

        int len = bits.Count - first;
        if (len == 0)
        {
            return 0;
        }

        if (len <= 53)
        {
            ulong m = 0;
            for (int i = first; i < bits.Count; i++)
            {
                m = (m << 1) | (bits[i] ? 1UL : 0UL);
            }

            return m;
        }

        ulong mant = 0;
        for (int i = 0; i < 53; i++)
        {
            mant = (mant << 1) | (bits[first + i] ? 1UL : 0UL);
        }

        bool half = bits[first + 53];
        bool sticky = false;
        for (int i = first + 54; i < bits.Count; i++)
        {
            if (bits[i])
            {
                sticky = true;
                break;
            }
        }

        if (half && (sticky || (mant & 1) == 1))
        {
            mant++;
        }

        return Math.ScaleB((double)mant, len - 53);
    }

    // ECMA-262 12.9.4 String Literals; B.1.2 legacy octal escapes.
    private Token ScanString(char quote, TokenFlags flags)
    {
        int start = _pos;
        _pos++;
        int chunk = _pos;
        StringBuilder? sb = null;
        TokenFlags f = flags;
        int firstOctal = -1;
        while (true)
        {
            if (_pos >= _s.Length)
            {
                _error(start, _pos, "invalid-token", Messages.InvalidToken);
                return new Token(TokenType.String, start, _pos, f | TokenFlags.Unterminated, (sb?.Append(_s, chunk, _pos - chunk).ToString()) ?? _s.Substring(chunk, _pos - chunk), null, 0, null, firstOctal);
            }

            char c = _s[_pos];
            if (c == quote)
            {
                string value = sb is null ? _s.Substring(chunk, _pos - chunk) : sb.Append(_s, chunk, _pos - chunk).ToString();
                _pos++;
                return new Token(TokenType.String, start, _pos, f, value, null, 0, null, firstOctal);
            }

            if (c == '\n' || c == '\r')
            {
                _error(start, _pos, "invalid-token", Messages.InvalidToken);
                string partial = sb is null ? _s.Substring(chunk, _pos - chunk) : sb.Append(_s, chunk, _pos - chunk).ToString();
                return new Token(TokenType.String, start, _pos, f | TokenFlags.Unterminated, partial, null, 0, null, firstOctal);
            }

            if (c == '\\')
            {
                sb ??= new StringBuilder();
                sb.Append(_s, chunk, _pos - chunk);
                int escStart = _pos;
                _pos++;
                ScanEscape(sb, ref f, inTemplate: false, escStart, out _, ref firstOctal);
                chunk = _pos;
                continue;
            }

            _pos++;
        }
    }

    // Scans one escape after the backslash (12.9.4 EscapeSequence). Appends the cooked value.
    // For templates, an invalid escape is reported through errorMessage instead of a diagnostic.
    private void ScanEscape(StringBuilder sb, ref TokenFlags f, bool inTemplate, int escStart, out string? errorMessage, ref int firstOctal)
    {
        errorMessage = null;
        if (_pos >= _s.Length)
        {
            return;
        }

        char c = _s[_pos++];
        switch (c)
        {
            case 'n': sb.Append('\n'); break;
            case 't': sb.Append('\t'); break;
            case 'r': sb.Append('\r'); break;
            case 'b': sb.Append('\b'); break;
            case 'f': sb.Append('\f'); break;
            case 'v': sb.Append('\v'); break;
            case '\r':
                Eat('\n');
                break;
            case '\n':
            case '\u2028':
            case '\u2029':
                break; // LineContinuation
            case 'x':
                if (_pos + 1 < _s.Length && Uri.IsHexDigit(_s[_pos]) && Uri.IsHexDigit(_s[_pos + 1]))
                {
                    sb.Append((char)((HexVal(_s[_pos]) * 16) + HexVal(_s[_pos + 1])));
                    _pos += 2;
                }
                else
                {
                    errorMessage = Messages.InvalidHexEscape;
                    ReportEscape(inTemplate, escStart, errorMessage);
                }

                break;
            case 'u':
                {
                    int cp = ScanUnicodeEscapeBody(out string? err);
                    if (err is not null)
                    {
                        errorMessage = err;
                        ReportEscape(inTemplate, escStart, err);
                    }
                    else
                    {
                        sb.Append(char.ConvertFromUtf32(cp is >= 0xD800 and <= 0xDFFF ? 0xFFFD : cp));
                        if (cp is >= 0xD800 and <= 0xDFFF)
                        {
                            sb.Length -= 1;
                            sb.Append((char)cp);
                        }
                    }

                    break;
                }

            case '0' when !char.IsAsciiDigit(Peek(0)):
                sb.Append('\0');
                break;
            case >= '0' and <= '7':
                {
                    // B.1.2 LegacyOctalEscapeSequence (up to \377).
                    int v = c - '0';
                    if (Peek(0) >= '0' && Peek(0) <= '7')
                    {
                        v = (v * 8) + (_s[_pos++] - '0');
                        if (c <= '3' && Peek(0) >= '0' && Peek(0) <= '7')
                        {
                            v = (v * 8) + (_s[_pos++] - '0');
                        }
                    }

                    sb.Append((char)v);
                    if (inTemplate)
                    {
                        errorMessage = Messages.OctalInTemplate;
                    }
                    else
                    {
                        f |= TokenFlags.OctalEscape;
                        firstOctal = firstOctal < 0 ? escStart : firstOctal;
                    }

                    break;
                }

            case '8':
            case '9':
                sb.Append(c);
                if (inTemplate)
                {
                    errorMessage = Messages.EightNineInTemplate;
                }
                else
                {
                    f |= TokenFlags.NonOctalEscape;
                    firstOctal = firstOctal < 0 ? escStart : firstOctal;
                }

                break;
            default:
                if (char.IsHighSurrogate(c) && _pos < _s.Length && char.IsLowSurrogate(_s[_pos]))
                {
                    sb.Append(c).Append(_s[_pos++]);
                }
                else
                {
                    sb.Append(c);
                }

                break;
        }
    }

    private void ReportEscape(bool inTemplate, int escStart, string message)
    {
        if (!inTemplate)
        {
            _error(escStart, _pos, "invalid-escape", message);
        }
    }

    // ECMA-262 12.9.6 Template Literal Lexical Components. Position is after the opening ` or }.
    private Token ScanTemplatePart(int start, TokenFlags flags)
    {
        var cooked = new StringBuilder();
        var raw = new StringBuilder();
        TokenFlags f = flags;
        string? escError = null;
        int escErrorStart = -1;
        int chunk = _pos;
        while (true)
        {
            if (_pos >= _s.Length)
            {
                _error(start, _pos, "unterminated-template", Messages.UnterminatedTemplate);
                raw.Append(_s, chunk, _pos - chunk);
                return new Token(TokenType.Template, start, _pos, f | TokenFlags.Unterminated | TokenFlags.TemplateTail, (f & TokenFlags.InvalidEscape) != 0 ? null : cooked.ToString(), raw.ToString(), 0, escError, escErrorStart);
            }

            char c = _s[_pos];
            if (c == '`')
            {
                cooked.Append(_s, chunk, _pos - chunk);
                raw.Append(_s, chunk, _pos - chunk);
                _pos++;
                f |= TokenFlags.TemplateTail;
                break;
            }

            if (c == '$' && Peek(1) == '{')
            {
                cooked.Append(_s, chunk, _pos - chunk);
                raw.Append(_s, chunk, _pos - chunk);
                _pos += 2;
                break;
            }

            if (c == '\\')
            {
                cooked.Append(_s, chunk, _pos - chunk);
                raw.Append(_s, chunk, _pos - chunk);
                int escStart = _pos;
                _pos++;
                int rawFrom = escStart;
                int unusedOctal = -1;
                ScanEscape(cooked, ref f, inTemplate: true, escStart, out string? err, ref unusedOctal);
                AppendRaw(raw, rawFrom, _pos);
                if (err is not null && escError is null)
                {
                    escError = err;
                    escErrorStart = escStart;
                    f |= TokenFlags.InvalidEscape;
                }

                chunk = _pos;
                continue;
            }

            if (c == '\r')
            {
                // 12.9.6 TV/TRV: CR and CR LF become LF.
                cooked.Append(_s, chunk, _pos - chunk).Append('\n');
                raw.Append(_s, chunk, _pos - chunk).Append('\n');
                _pos++;
                Eat('\n');
                chunk = _pos;
                continue;
            }

            _pos++;
        }

        return new Token(TokenType.Template, start, _pos, f, (f & TokenFlags.InvalidEscape) != 0 ? null : cooked.ToString(), raw.ToString(), 0, escError, escErrorStart);
    }

    private void AppendRaw(StringBuilder raw, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (_s[i] == '\r')
            {
                raw.Append('\n');
                if (i + 1 < to && _s[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                raw.Append(_s[i]);
            }
        }
    }
}
