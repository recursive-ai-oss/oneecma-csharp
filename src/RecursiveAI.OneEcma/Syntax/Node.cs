namespace RecursiveAI.OneEcma.Syntax;

/// <summary>
/// Base of every syntax node. Nodes are immutable once parsing returns; each carries the UTF-16 offsets
/// of its source range (convert with <see cref="Text.SourceText.GetPosition(int)"/>).
/// </summary>
public abstract class Node
{
    private protected Node(int start, int end)
    {
        Start = start;
        End = end;
    }

    /// <summary>Offset of the first code unit of the node.</summary>
    public int Start { get; }

    /// <summary>Offset one past the last code unit of the node.</summary>
    public int End { get; }

    /// <summary>The node's kind, for switching without type tests.</summary>
    public abstract NodeKind Kind { get; }

    /// <summary>The node's source range.</summary>
    public Text.Span Span => new(Start, End);
}

/// <summary>Base of expression nodes.</summary>
public abstract class Expression : Node
{
    private protected Expression(int start, int end)
        : base(start, end)
    {
    }

    /// <summary>True when the expression was written inside parentheses (this matters for <c>(a) = 1</c>, <c>(a, b) =&gt; x</c> and name inference).</summary>
    public bool IsParenthesized { get; internal set; }
}

/// <summary>Base of statement and declaration nodes.</summary>
public abstract class Statement : Node
{
    private protected Statement(int start, int end)
        : base(start, end)
    {
    }
}

/// <summary>Prefix operators of <see cref="UnaryExpression"/>.</summary>
public enum UnaryOp
{
    /// <summary>Unary <c>-</c>.</summary>
    Minus,

    /// <summary>Unary <c>+</c>.</summary>
    Plus,

    /// <summary><c>!</c>.</summary>
    Not,

    /// <summary><c>~</c>.</summary>
    BitwiseNot,

    /// <summary><c>typeof</c>.</summary>
    Typeof,

    /// <summary><c>void</c>.</summary>
    Void,

    /// <summary><c>delete</c>.</summary>
    Delete,
}

/// <summary>Operators of <see cref="BinaryExpression"/>.</summary>
public enum BinaryOp
{
    /// <summary><c>+</c>.</summary>
    Add,

    /// <summary><c>-</c>.</summary>
    Subtract,

    /// <summary><c>*</c>.</summary>
    Multiply,

    /// <summary><c>/</c>.</summary>
    Divide,

    /// <summary><c>%</c>.</summary>
    Remainder,

    /// <summary><c>**</c>.</summary>
    Exponent,

    /// <summary><c>==</c>.</summary>
    Equal,

    /// <summary><c>!=</c>.</summary>
    NotEqual,

    /// <summary><c>===</c>.</summary>
    StrictEqual,

    /// <summary><c>!==</c>.</summary>
    StrictNotEqual,

    /// <summary><c>&lt;</c>.</summary>
    LessThan,

    /// <summary><c>&lt;=</c>.</summary>
    LessThanOrEqual,

    /// <summary><c>&gt;</c>.</summary>
    GreaterThan,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterThanOrEqual,

    /// <summary><c>&lt;&lt;</c>.</summary>
    ShiftLeft,

    /// <summary><c>&gt;&gt;</c>.</summary>
    ShiftRight,

    /// <summary><c>&gt;&gt;&gt;</c>.</summary>
    UnsignedShiftRight,

    /// <summary><c>&amp;</c>.</summary>
    BitwiseAnd,

    /// <summary><c>|</c>.</summary>
    BitwiseOr,

    /// <summary><c>^</c>.</summary>
    BitwiseXor,

    /// <summary><c>in</c>.</summary>
    In,

    /// <summary><c>instanceof</c>.</summary>
    Instanceof,
}

/// <summary>Operators of <see cref="LogicalExpression"/>.</summary>
public enum LogicalOp
{
    /// <summary><c>&amp;&amp;</c>.</summary>
    And,

    /// <summary><c>||</c>.</summary>
    Or,

    /// <summary><c>??</c>.</summary>
    Coalesce,
}

/// <summary>Operators of <see cref="AssignmentExpression"/>.</summary>
public enum AssignOp
{
    /// <summary><c>=</c>.</summary>
    Assign,

    /// <summary><c>+=</c> (and the compound operators below, in the order of <see cref="BinaryOp"/>).</summary>
    AddAssign,

    /// <summary><c>-=</c>.</summary>
    SubtractAssign,

    /// <summary><c>*=</c>.</summary>
    MultiplyAssign,

    /// <summary><c>/=</c>.</summary>
    DivideAssign,

    /// <summary><c>%=</c>.</summary>
    RemainderAssign,

    /// <summary><c>**=</c>.</summary>
    ExponentAssign,

    /// <summary><c>&lt;&lt;=</c>.</summary>
    ShiftLeftAssign,

    /// <summary><c>&gt;&gt;=</c>.</summary>
    ShiftRightAssign,

    /// <summary><c>&gt;&gt;&gt;=</c>.</summary>
    UnsignedShiftRightAssign,

    /// <summary><c>&amp;=</c>.</summary>
    BitwiseAndAssign,

    /// <summary><c>|=</c>.</summary>
    BitwiseOrAssign,

    /// <summary><c>^=</c>.</summary>
    BitwiseXorAssign,

    /// <summary><c>&amp;&amp;=</c>.</summary>
    AndAssign,

    /// <summary><c>||=</c>.</summary>
    OrAssign,

    /// <summary><c>??=</c>.</summary>
    CoalesceAssign,
}

/// <summary>Kinds of <see cref="Property"/>.</summary>
public enum PropertyKind
{
    /// <summary><c>key: value</c> or shorthand.</summary>
    Init,

    /// <summary>A method <c>key() {}</c>.</summary>
    Method,

    /// <summary>A getter.</summary>
    Get,

    /// <summary>A setter.</summary>
    Set,
}

/// <summary>Kinds of <see cref="VariableDeclaration"/>.</summary>
public enum VarKind
{
    /// <summary><c>var</c>.</summary>
    Var,

    /// <summary><c>let</c>.</summary>
    Let,

    /// <summary><c>const</c>.</summary>
    Const,
}

/// <summary>Where a <see cref="FunctionNode"/> appears.</summary>
public enum FunctionFlavor
{
    /// <summary>A declaration or expression.</summary>
    Normal,

    /// <summary>An arrow function.</summary>
    Arrow,

    /// <summary>A method of an object literal or class.</summary>
    Method,

    /// <summary>A getter.</summary>
    Getter,

    /// <summary>A setter.</summary>
    Setter,

    /// <summary>The constructor of a class without <c>extends</c>.</summary>
    BaseConstructor,

    /// <summary>The constructor of a class with <c>extends</c>.</summary>
    DerivedConstructor,
}

/// <summary>Kinds of <see cref="MethodDefinition"/>.</summary>
public enum ClassMemberKind
{
    /// <summary>A method.</summary>
    Method,

    /// <summary>A getter.</summary>
    Get,

    /// <summary>A setter.</summary>
    Set,

    /// <summary>The <c>constructor</c>.</summary>
    Constructor,
}

/// <summary>Kinds of <see cref="ImportSpecifier"/>.</summary>
public enum ImportSpecifierKind
{
    /// <summary><c>import { a as b }</c>.</summary>
    Named,

    /// <summary><c>import a</c>.</summary>
    Default,

    /// <summary><c>import * as a</c>.</summary>
    Namespace,
}
