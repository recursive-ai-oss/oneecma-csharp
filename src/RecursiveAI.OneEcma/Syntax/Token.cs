namespace RecursiveAI.OneEcma.Syntax;

/// <summary>Kinds of lexical token. Contextual keywords (<c>let</c>, <c>async</c>, <c>of</c>, <c>static</c>, <c>yield</c>, <c>await</c>...) are plain <see cref="Identifier"/> tokens; reserved words have their own kinds.</summary>
internal enum TokenType : byte
{
    /// <summary>End of input.</summary>
    EndOfInput,

    /// <summary>A character or sequence that starts no token; an error was already reported.</summary>
    Invalid,

    /// <summary>An IdentifierName that is not a reserved word (or one written with escapes).</summary>
    Identifier,

    /// <summary><c>#name</c>.</summary>
    PrivateName,

    /// <summary>A numeric literal (not BigInt).</summary>
    Number,

    /// <summary>A BigInt literal.</summary>
    BigInt,

    /// <summary>A string literal.</summary>
    String,

    /// <summary>A template chunk (head, middle, tail or no-substitution); see <see cref="TokenFlags.TemplateTail"/>.</summary>
    Template,

    /// <summary>A regular expression literal.</summary>
    RegExp,

    // Punctuators.
    LBrace, RBrace, LParen, RParen, LBracket, RBracket,
    Dot, Ellipsis, Semicolon, Comma,
    Lt, Gt, Le, Ge, EqEq, NotEq, EqEqEq, NotEqEq,
    Plus, Minus, Star, Slash, Percent, StarStar, PlusPlus, MinusMinus,
    Shl, Shr, UShr, Amp, Pipe, Caret, Bang, Tilde, AmpAmp, PipePipe, QuestionQuestion,
    Question, QuestionDot, Colon,
    Assign, PlusAssign, MinusAssign, StarAssign, SlashAssign, PercentAssign, StarStarAssign,
    ShlAssign, ShrAssign, UShrAssign, AmpAssign, PipeAssign, CaretAssign,
    AmpAmpAssign, PipePipeAssign, QuestionQuestionAssign,
    Arrow, At,

    // Reserved words (ECMA-262 12.7.2).
    Break, Case, Catch, Class, Const, Continue, Debugger, Default, Delete, Do, Else, Enum, Export,
    Extends, False, Finally, For, Function, If, Import, In, Instanceof, New, Null, Return, Super,
    Switch, This, Throw, True, Try, Typeof, Var, Void, While, With,
}

/// <summary>Extra facts about a token.</summary>
[Flags]
internal enum TokenFlags
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>A line terminator lies between the previous token and this one.</summary>
    NewlineBefore = 1,

    /// <summary>The identifier was written with a Unicode escape sequence.</summary>
    Escaped = 2,

    /// <summary>A legacy octal integer literal such as <c>017</c> (Annex B.1.1).</summary>
    LegacyOctal = 4,

    /// <summary>A decimal literal with a leading zero such as <c>089</c> (Annex B.1.1).</summary>
    LeadingZeroDecimal = 8,

    /// <summary>A string containing a legacy octal escape such as <c>\01</c> (Annex B.1.2).</summary>
    OctalEscape = 16,

    /// <summary>A string containing <c>\8</c> or <c>\9</c> (Annex B.1.2).</summary>
    NonOctalEscape = 32,

    /// <summary>A template chunk that ends the template (its closing backtick was seen).</summary>
    TemplateTail = 64,

    /// <summary>A template chunk with an invalid escape: its cooked value is undefined.</summary>
    InvalidEscape = 128,

    /// <summary>A string or template that reached the end of input or line without closing.</summary>
    Unterminated = 256,

    /// <summary>A numeric literal written with a numeric separator.</summary>
    HasSeparator = 512,
}

/// <summary>A lexical token. Values are decoded when the token is scanned.</summary>
internal readonly struct Token
{
    internal Token(TokenType type, int start, int end, TokenFlags flags, string? text = null, string? raw = null, double number = 0, string? aux = null, int auxStart = -1)
    {
        Type = type;
        Start = start;
        End = end;
        Flags = flags;
        Text = text;
        Raw = raw;
        Number = number;
        Aux = aux;
        AuxStart = auxStart;
    }

    /// <summary>The kind.</summary>
    public TokenType Type { get; }

    /// <summary>Offset of the first code unit.</summary>
    public int Start { get; }

    /// <summary>Offset one past the last code unit.</summary>
    public int End { get; }

    /// <summary>Extra facts.</summary>
    public TokenFlags Flags { get; }

    /// <summary>Identifier name, cooked string or template value (null for an invalid template escape), regular expression flags, or BigInt digits (with any radix prefix).</summary>
    public string? Text { get; }

    /// <summary>Template raw value (line endings normalised) or the regular expression body.</summary>
    public string? Raw { get; }

    /// <summary>The value of a <see cref="TokenType.Number"/>.</summary>
    public double Number { get; }

    /// <summary>For a template chunk with an invalid escape: V8's message for it.</summary>
    public string? Aux { get; }

    /// <summary>For a string: offset of its first legacy octal or <c>\8</c>/<c>\9</c> escape; for a template chunk: offset of the invalid escape; otherwise -1.</summary>
    public int AuxStart { get; }

    /// <summary>True when a line terminator precedes the token.</summary>
    public bool NewlineBefore => (Flags & TokenFlags.NewlineBefore) != 0;

    /// <summary>True for an identifier written with escapes.</summary>
    public bool Escaped => (Flags & TokenFlags.Escaped) != 0;
}
