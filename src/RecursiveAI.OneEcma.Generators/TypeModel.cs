using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace RecursiveAI.OneEcma.Generators;

/// <summary>How a .NET type is converted at the boundary.</summary>
internal enum TK
{
    Bool,
    Int,
    UInt64,
    Float,
    Double,
    Decimal,
    BigInt,
    String,
    Char,
    Enum,
    JsValue,
    JsObject,
    Nullable,
    Array,
    Seq,
    Dict,
    Func,
    Host,
    Value,
    Unsupported,
}

/// <summary>What <c>[JsExport]</c> says about a type.</summary>
internal sealed class ExportInfo
{
    internal ExportInfo(INamedTypeSymbol type, string jsName, bool isValue, bool rejectUnknown)
    {
        Type = type;
        JsName = jsName;
        IsValue = isValue;
        RejectUnknown = rejectUnknown;
    }

    internal INamedTypeSymbol Type { get; }

    internal string JsName { get; }

    internal bool IsValue { get; }

    internal bool RejectUnknown { get; }

    /// <summary>The generated binding class, fully qualified with <c>global::</c>.</summary>
    internal string Binding => BindingNameOf(Type, true);

    internal static string BindingNameOf(INamedTypeSymbol type, bool qualified)
    {
        string name = type.Name;
        for (INamedTypeSymbol? c = type.ContainingType; c is not null; c = c.ContainingType)
        {
            name = c.Name + "_" + name;
        }

        name += "JsBinding";
        if (!qualified || type.ContainingNamespace is null || type.ContainingNamespace.IsGlobalNamespace)
        {
            return qualified ? "global::" + name : name;
        }

        return "global::" + type.ContainingNamespace.ToDisplayString() + "." + name;
    }
}

/// <summary>A classified .NET type.</summary>
internal sealed class TypeDesc
{
    internal TypeDesc(TK kind, ITypeSymbol symbol, string cs)
    {
        Kind = kind;
        Symbol = symbol;
        Cs = cs;
    }

    internal TK Kind { get; }

    internal ITypeSymbol Symbol { get; }

    /// <summary>The C# spelling used in generated code.</summary>
    internal string Cs { get; }

    internal TypeDesc? Inner { get; set; }

    internal List<TypeDesc> FuncParams { get; } = [];

    internal TypeDesc? FuncReturn { get; set; }

    /// <summary>For delegates: 0 plain, 1 Task, 2 Task of T.</summary>
    internal int FuncAsync { get; set; }

    internal ExportInfo? Export { get; set; }

    internal string? Why { get; set; }

    internal string? Fix { get; set; }

    /// <summary>The element is a plain array (T[]) rather than a list-like interface.</summary>
    internal bool IsArrayType { get; set; }
}

/// <summary>Maps symbols to <see cref="TypeDesc"/>.</summary>
internal sealed class Classifier
{
    internal const string ExportAttr = "RecursiveAI.OneEcma.JsExportAttribute";
    internal const string IgnoreAttr = "RecursiveAI.OneEcma.JsIgnoreAttribute";

    internal static readonly SymbolDisplayFormat Cs = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    internal static readonly SymbolDisplayFormat Short = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private readonly Dictionary<string, TypeDesc> _cache = [];

    internal TypeDesc Classify(ITypeSymbol t)
    {
        string key = t.ToDisplayString(Cs);
        if (_cache.TryGetValue(key, out TypeDesc? hit))
        {
            return hit;
        }

        TypeDesc d = Build(t, key);
        _cache[key] = d;
        return d;
    }

    private static TypeDesc Unsupported(ITypeSymbol t, string key, string why, string fix) => new(TK.Unsupported, t, key) { Why = why, Fix = fix };

    private TypeDesc Build(ITypeSymbol t, string key)
    {
        if (t.IsReferenceType && t.NullableAnnotation == NullableAnnotation.Annotated && t.TypeKind != TypeKind.Error)
        {
            TypeDesc inner = Classify(t.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
            return new TypeDesc(TK.Nullable, t, key) { Inner = inner };
        }

        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nt)
        {
            return new TypeDesc(TK.Nullable, t, key) { Inner = Classify(nt.TypeArguments[0]) };
        }

        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean: return new TypeDesc(TK.Bool, t, key);
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
                return new TypeDesc(TK.Int, t, key);
            case SpecialType.System_UInt64: return new TypeDesc(TK.UInt64, t, key);
            case SpecialType.System_Single: return new TypeDesc(TK.Float, t, key);
            case SpecialType.System_Double: return new TypeDesc(TK.Double, t, key);
            case SpecialType.System_Decimal: return new TypeDesc(TK.Decimal, t, key);
            case SpecialType.System_String: return new TypeDesc(TK.String, t, key);
            case SpecialType.System_Char: return new TypeDesc(TK.Char, t, key);
            case SpecialType.System_Object:
                return Unsupported(t, key, "object has no fixed script shape", "use JsValue to receive any script value, or a specific type");
            case SpecialType.System_Void:
                return Unsupported(t, key, "void is not a value", "remove the parameter");
        }

        if (t.TypeKind == TypeKind.Enum)
        {
            return new TypeDesc(TK.Enum, t, key);
        }

        if (t is IArrayTypeSymbol arr)
        {
            if (arr.Rank != 1)
            {
                return Unsupported(t, key, "multi-dimensional arrays are not supported", "use a jagged array or a List<T>");
            }

            return new TypeDesc(TK.Array, t, key) { Inner = Classify(arr.ElementType), IsArrayType = true };
        }

        if (t is IPointerTypeSymbol || t is IFunctionPointerTypeSymbol)
        {
            return Unsupported(t, key, "pointers cannot cross the boundary", "use a managed type");
        }

        if (t is ITypeParameterSymbol)
        {
            return Unsupported(t, key, "generic type parameters have no fixed script shape", "use a closed type");
        }

        if (t is not INamedTypeSymbol named)
        {
            return Unsupported(t, key, "this kind of type is not supported", "use a supported type");
        }

        string full = named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        string def = named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        switch (full)
        {
            case "RecursiveAI.OneEcma.JsValue": return new TypeDesc(TK.JsValue, t, key);
            case "RecursiveAI.OneEcma.JsObject": return new TypeDesc(TK.JsObject, t, key);
            case "System.Numerics.BigInteger": return new TypeDesc(TK.BigInt, t, key);
        }

        switch (def)
        {
            case "System.Collections.Generic.List<T>":
            case "System.Collections.Generic.IList<T>":
            case "System.Collections.Generic.ICollection<T>":
            case "System.Collections.Generic.IEnumerable<T>":
            case "System.Collections.Generic.IReadOnlyList<T>":
            case "System.Collections.Generic.IReadOnlyCollection<T>":
                return new TypeDesc(TK.Seq, t, key) { Inner = Classify(named.TypeArguments[0]) };
            case "System.Collections.Generic.Dictionary<TKey, TValue>":
            case "System.Collections.Generic.IDictionary<TKey, TValue>":
            case "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>":
                if (named.TypeArguments[0].SpecialType != SpecialType.System_String)
                {
                    return Unsupported(t, key, "dictionary keys must be string because script object keys are strings", "use Dictionary<string, T>");
                }

                return new TypeDesc(TK.Dict, t, key) { Inner = Classify(named.TypeArguments[1]) };
        }

        if (named.TypeKind == TypeKind.Delegate)
        {
            return ClassifyDelegate(named, key, def);
        }

        ExportInfo? export = GetExport(named);
        if (export is not null)
        {
            return new TypeDesc(export.IsValue ? TK.Value : TK.Host, t, key) { Export = export };
        }

        if (def.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal) || def.StartsWith("System.Collections.Generic.IAsyncEnumerable", StringComparison.Ordinal))
        {
            return Unsupported(t, key, "tasks and async sequences can only be returned by a method, not passed or stored", "return it from a method, or await it in .NET first");
        }

        if (named.IsGenericType)
        {
            return Unsupported(t, key, "generic types are not supported", "use a closed non-generic type");
        }

        return Unsupported(t, key, "it is not exported", "add [JsExport] to " + named.Name + " (records and structs are copied by value, classes are shared by reference), or use a supported type");
    }

    private TypeDesc ClassifyDelegate(INamedTypeSymbol named, string key, string def)
    {
        bool isFunc = def.StartsWith("System.Func<", StringComparison.Ordinal);
        bool isAction = def == "System.Action" || def.StartsWith("System.Action<", StringComparison.Ordinal);
        if (!isFunc && !isAction)
        {
            return Unsupported(named, key, "only Func<> and Action<> callbacks are supported", "use Func<...> or Action<...>");
        }

        var d = new TypeDesc(TK.Func, named, key);
        int count = named.TypeArguments.Length - (isFunc ? 1 : 0);
        if (count > 6)
        {
            return Unsupported(named, key, "callbacks take at most 6 parameters", "pass an object instead");
        }

        for (int i = 0; i < count; i++)
        {
            TypeDesc p = Classify(named.TypeArguments[i]);
            if (p.Kind == TK.Unsupported)
            {
                return Unsupported(named, key, "callback parameter type " + named.TypeArguments[i].ToDisplayString(Short) + " " + p.Why, p.Fix ?? "use a supported type");
            }

            d.FuncParams.Add(p);
        }

        if (isFunc)
        {
            ITypeSymbol rt = named.TypeArguments[named.TypeArguments.Length - 1];
            string rdef = rt.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            if (rdef == "System.Threading.Tasks.Task")
            {
                d.FuncAsync = 1;
            }
            else if (rdef == "System.Threading.Tasks.Task<TResult>")
            {
                d.FuncAsync = 2;
                d.FuncReturn = Classify(((INamedTypeSymbol)rt).TypeArguments[0]);
            }
            else
            {
                d.FuncReturn = Classify(rt);
            }

            if (d.FuncReturn is { Kind: TK.Unsupported } bad)
            {
                return Unsupported(named, key, "callback result type " + bad.Symbol.ToDisplayString(Short) + " " + bad.Why, bad.Fix ?? "use a supported type");
            }
        }

        return d;
    }

    /// <summary>First unsupported part of a type (it may be nested in a list or dictionary), or null.</summary>
    internal static TypeDesc? FirstUnsupported(TypeDesc d)
    {
        if (d.Kind == TK.Unsupported)
        {
            return d;
        }

        if (d.Inner is not null && FirstUnsupported(d.Inner) is { } a)
        {
            return a;
        }

        return null;
    }

    internal static AttributeData? FindAttribute(ISymbol s, string fullName) =>
        s.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == fullName);

    internal static ExportInfo? GetExport(INamedTypeSymbol type)
    {
        AttributeData? a = FindAttribute(type, ExportAttr);
        if (a is null)
        {
            return null;
        }

        string? name = null;
        int kind = 0;
        bool reject = false;
        foreach (KeyValuePair<string, TypedConstant> kv in a.NamedArguments)
        {
            switch (kv.Key)
            {
                case "Name": name = kv.Value.Value as string; break;
                case "Kind": kind = kv.Value.Value is int k ? k : 0; break;
                case "RejectUnknownProperties": reject = kv.Value.Value is true; break;
            }
        }

        bool isValue = kind == 2 || (kind == 0 && (type.IsRecord || type.TypeKind == TypeKind.Struct));
        return new ExportInfo(type, string.IsNullOrEmpty(name) ? type.Name : name!, isValue, reject);
    }
}
