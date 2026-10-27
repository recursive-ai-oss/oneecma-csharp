using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.ExecutorSpike;

/// <summary>Small pieces both compilers need.</summary>
internal static class CompileUtil
{
    /// <summary>
    /// How deeply non-flat expression and statement nesting may go before a compiler pass stops with a limit.
    /// The front end bounds nesting at 512 but accepts 100,000-link member and call chains, so each pass needs its own guard.
    /// </summary>
    internal const int MaxNesting = 1000;

    internal static SpikeLimitException TooNested() => new("source nested too deeply");

    internal static BinaryOp ToBinary(AssignOp op) => op switch
    {
        AssignOp.AddAssign => BinaryOp.Add,
        AssignOp.SubtractAssign => BinaryOp.Subtract,
        AssignOp.MultiplyAssign => BinaryOp.Multiply,
        AssignOp.DivideAssign => BinaryOp.Divide,
        AssignOp.RemainderAssign => BinaryOp.Remainder,
        AssignOp.ExponentAssign => BinaryOp.Exponent,
        AssignOp.ShiftLeftAssign => BinaryOp.ShiftLeft,
        AssignOp.ShiftRightAssign => BinaryOp.ShiftRight,
        AssignOp.UnsignedShiftRightAssign => BinaryOp.UnsignedShiftRight,
        AssignOp.BitwiseAndAssign => BinaryOp.BitwiseAnd,
        AssignOp.BitwiseOrAssign => BinaryOp.BitwiseOr,
        AssignOp.BitwiseXorAssign => BinaryOp.BitwiseXor,
        _ => throw new SpikeUnsupportedException("assignment operator " + op),
    };

    /// <summary>The property name of a non-computed member or object-literal key.</summary>
    internal static string KeyName(Node key) => key switch
    {
        Identifier id => string.Intern(id.Name),
        StringLiteral s => string.Intern(s.Value),
        NumberLiteral n => Conv.NumberToString(n.Value),
        _ => throw new SpikeUnsupportedException("property key " + key.Kind),
    };

    internal static List<FunctionDeclaration> HoistedFunctions(IEnumerable<Node> statements)
    {
        var list = new List<FunctionDeclaration>();
        foreach (Node s in statements)
        {
            if (s is FunctionDeclaration fd)
            {
                list.Add(fd);
            }
        }

        return list;
    }

    internal static JsValue ConstantOf(Expression e) => e switch
    {
        NumberLiteral n => JsValue.FromNumber(n.Value),
        StringLiteral s => JsValue.FromString(string.Intern(s.Value)),
        BooleanLiteral b => JsValue.FromBool(b.Value),
        NullLiteral => JsValue.Null,
        _ => throw new SpikeUnsupportedException("not a constant"),
    };

    internal static bool IsConstant(Expression e) => e is NumberLiteral or StringLiteral or BooleanLiteral or NullLiteral;

    /// <summary>The value a host sees: numbers, strings, booleans and null; objects as their string form.</summary>
    internal static object? ToHost(JsValue v)
    {
        if (v.IsUndefined)
        {
            return null;
        }

        if (v.IsNumber)
        {
            return v.Num;
        }

        if (v.IsBool)
        {
            return ReferenceEquals(v.Ref, JsValue.TrueTag);
        }

        if (v.IsNull)
        {
            return null;
        }

        return Conv.ToStr(v);
    }
}
