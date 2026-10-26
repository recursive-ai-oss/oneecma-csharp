namespace RecursiveAI.OneEcma.RegExp;

/// <summary>Per-node matching modes that the pattern modifiers <c>(?ims-ims:)</c> can change (22.2.2.7.4 UpdateModifiers).</summary>
[Flags]
internal enum ModeFlags
{
    None = 0,
    IgnoreCase = 1,
    Multiline = 2,
    DotAll = 4,
}

internal enum AssertKind
{
    LineStart,
    LineEnd,
    WordBoundary,
    NotWordBoundary,
}

/// <summary>A node of the parsed pattern. <see cref="CanBeEmpty"/> is computed once at construction.</summary>
internal abstract class Node
{
    /// <summary>True if the node can match without consuming input; decides where a loop needs an empty-iteration check.</summary>
    internal bool CanBeEmpty { get; init; }
}

internal sealed class EmptyNode : Node
{
    internal EmptyNode()
    {
        CanBeEmpty = true;
    }
}

internal sealed class CharNode : Node
{
    internal CharNode(int cp)
    {
        Cp = cp;
    }

    internal int Cp { get; }
}

internal sealed class AnyNode : Node;

/// <summary>A bracketed class, evaluated at compile time because folding depends on the flags in effect.</summary>
internal sealed class ClassNode : Node
{
    internal ClassNode(ClassExpr expr, bool negate, bool mayContainStrings)
    {
        Expr = expr;
        Negate = negate;
        CanBeEmpty = mayContainStrings;
    }

    internal ClassExpr Expr { get; }

    internal bool Negate { get; }
}

/// <summary>A class escape used as an atom: <c>\d \w \p{..}</c> and the like.</summary>
internal sealed class EscapeNode : Node
{
    internal EscapeNode(ClassEscapeExpr escape)
    {
        Escape = escape;
        CanBeEmpty = escape.Property is { IsStrings: true };
    }

    internal ClassEscapeExpr Escape { get; }
}

internal sealed class AssertNode : Node
{
    internal AssertNode(AssertKind kind)
    {
        Kind = kind;
        CanBeEmpty = true;
    }

    internal AssertKind Kind { get; }
}

internal sealed class GroupNode : Node
{
    internal GroupNode(int index, Node body)
    {
        Index = index;
        Body = body;
        CanBeEmpty = body.CanBeEmpty;
    }

    internal int Index { get; }

    internal Node Body { get; }
}

/// <summary>A non-capturing group, optionally with modifiers; plain <c>(?:..)</c> is represented by its body alone.</summary>
internal sealed class ModifierNode : Node
{
    internal ModifierNode(ModeFlags add, ModeFlags remove, Node body)
    {
        Add = add;
        Remove = remove;
        Body = body;
        CanBeEmpty = body.CanBeEmpty;
    }

    internal ModeFlags Add { get; }

    internal ModeFlags Remove { get; }

    internal Node Body { get; }
}

internal sealed class LookNode : Node
{
    internal LookNode(bool behind, bool negate, Node body)
    {
        Behind = behind;
        Negate = negate;
        Body = body;
        CanBeEmpty = true;
    }

    internal bool Behind { get; }

    internal bool Negate { get; }

    internal Node Body { get; }
}

/// <summary>A backreference by number or name. Named references are bound to group numbers after the whole pattern is parsed.</summary>
internal sealed class BackrefNode : Node
{
    internal BackrefNode(string? name, int[] groups)
    {
        Name = name;
        Groups = groups;
        CanBeEmpty = true;
    }

    internal string? Name { get; }

    internal int[] Groups { get; set; }
}

internal sealed class QuantNode : Node
{
    internal QuantNode(Node body, int min, int max, bool greedy, int firstGroup, int groupCount)
    {
        Body = body;
        Min = min;
        Max = max;
        Greedy = greedy;
        FirstGroup = firstGroup;
        GroupCount = groupCount;
        CanBeEmpty = min == 0 || body.CanBeEmpty;
    }

    internal Node Body { get; }

    internal int Min { get; }

    /// <summary>The maximum count, or <see cref="int.MaxValue"/> for unbounded.</summary>
    internal int Max { get; }

    internal bool Greedy { get; }

    /// <summary>Number of the first capture group inside the atom (CountLeftCapturingParensBefore + 1).</summary>
    internal int FirstGroup { get; }

    /// <summary>Number of capture groups inside the atom (CountLeftCapturingParensWithin).</summary>
    internal int GroupCount { get; }
}

internal sealed class AltNode : Node
{
    internal AltNode(List<Node> alternatives)
    {
        Alternatives = alternatives;
        CanBeEmpty = alternatives.Exists(static a => a.CanBeEmpty);
    }

    internal List<Node> Alternatives { get; }
}

internal sealed class SeqNode : Node
{
    internal SeqNode(List<Node> items)
    {
        Items = items;
        CanBeEmpty = items.TrueForAll(static a => a.CanBeEmpty);
    }

    internal List<Node> Items { get; }
}

// ---- character class expressions -------------------------------------------------------------------------

/// <summary>A class expression: the contents between brackets, or one operand inside a v-mode class.</summary>
internal abstract class ClassExpr
{
    /// <summary>MayContainStrings (22.2.1.8).</summary>
    internal bool MayContainStrings { get; init; }
}

internal sealed class ClassChar : ClassExpr
{
    internal ClassChar(int cp)
    {
        Cp = cp;
    }

    internal int Cp { get; }
}

internal sealed class ClassRange : ClassExpr
{
    internal ClassRange(int lo, int hi)
    {
        Lo = lo;
        Hi = hi;
    }

    internal int Lo { get; }

    internal int Hi { get; }
}

/// <summary><c>\d \D \s \S \w \W \p{..} \P{..}</c>.</summary>
internal sealed class ClassEscapeExpr : ClassExpr
{
    internal ClassEscapeExpr(char kind, PropertyRef? property)
    {
        Kind = kind;
        Property = property;
    }

    internal char Kind { get; }

    internal PropertyRef? Property { get; }
}

internal sealed class ClassUnion : ClassExpr
{
    internal ClassUnion(List<ClassExpr> items)
    {
        Items = items;
    }

    internal List<ClassExpr> Items { get; }
}

internal sealed class ClassIntersection : ClassExpr
{
    internal ClassIntersection(List<ClassExpr> items)
    {
        Items = items;
    }

    internal List<ClassExpr> Items { get; }
}

internal sealed class ClassSubtraction : ClassExpr
{
    internal ClassSubtraction(List<ClassExpr> items)
    {
        Items = items;
    }

    internal List<ClassExpr> Items { get; }
}

/// <summary>A nested class inside a v-mode class.</summary>
internal sealed class ClassNested : ClassExpr
{
    internal ClassNested(ClassExpr inner, bool negate)
    {
        Inner = inner;
        Negate = negate;
    }

    internal ClassExpr Inner { get; }

    internal bool Negate { get; }
}

/// <summary><c>\q{abc|d}</c>: each string is a list of code points.</summary>
internal sealed class ClassStrings : ClassExpr
{
    internal ClassStrings(List<int[]> strings)
    {
        Strings = strings;
    }

    internal List<int[]> Strings { get; }
}
