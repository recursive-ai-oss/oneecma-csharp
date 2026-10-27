using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RecursiveAI.OneEcma.Generators;

/// <summary>Writes the binding class for one exported type.</summary>
internal sealed class BindingEmitter
{
    private readonly INamedTypeSymbol _type;
    private readonly ExportInfo _export;
    private readonly Classifier _cls;
    private readonly List<Diagnostic> _diags;
    private readonly StringBuilder _helpers = new();
    private readonly StringBuilder _body = new();
    private readonly Dictionary<string, string> _conv = [];
    private readonly Dictionary<string, string> _toJs = [];
    private int _n;
    private readonly string _typeCs;
    private readonly string _typeShort;

    internal BindingEmitter(INamedTypeSymbol type, ExportInfo export, Classifier cls, List<Diagnostic> diags)
    {
        _type = type;
        _export = export;
        _cls = cls;
        _diags = diags;
        _typeCs = type.ToDisplayString(Classifier.Cs);
        _typeShort = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
    }

    internal static string Lit(string s) => SymbolDisplay.FormatLiteral(s, true);

    private static string Ind(int n) => new(' ', n * 4);

    private void Report(DiagnosticDescriptor d, ISymbol at, params object[] args) =>
        _diags.Add(Diagnostic.Create(d, at.Locations.FirstOrDefault() ?? Location.None, args));

    internal static string JsNameOf(string dotnetName)
    {
        int i = 0;
        while (i < dotnetName.Length && dotnetName[i] >= 'A' && dotnetName[i] <= 'Z')
        {
            i++;
        }

        if (i == 0)
        {
            return dotnetName;
        }

        int lower = i == 1 ? 1 : (i == dotnetName.Length ? i : i - 1);
        var sb = new StringBuilder(dotnetName.Length);
        for (int k = 0; k < dotnetName.Length; k++)
        {
            char ch = dotnetName[k];
            sb.Append(k < lower ? (char)(ch + 32) : ch);
        }

        return sb.ToString();
    }

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || (s[0] >= '0' && s[0] <= '9'))
        {
            return false;
        }

        foreach (char c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$'))
            {
                return false;
            }
        }

        return true;
    }

    private string MemberJsName(ISymbol m)
    {
        AttributeData? a = Classifier.FindAttribute(m, Classifier.ExportAttr);
        if (a is not null)
        {
            foreach (KeyValuePair<string, TypedConstant> kv in a.NamedArguments)
            {
                if (kv.Key == "Name" && kv.Value.Value is string n && n.Length > 0)
                {
                    if (!IsIdentifier(n))
                    {
                        Report(Descriptors.InvalidName, m, n, m.Name);
                    }

                    return n;
                }
            }
        }

        return JsNameOf(m.Name);
    }

    // ---- converters -------------------------------------------------------------------------------

    /// <summary>Text for "must be X" messages.</summary>
    private string Expect(TypeDesc d)
    {
        switch (d.Kind)
        {
            case TK.Bool: return "a boolean";
            case TK.Int: return "an integer (" + SpecialName(d.Symbol) + ")";
            case TK.UInt64: return "a non-negative integer (System.UInt64)";
            case TK.Float: return "a number (System.Single)";
            case TK.Double: return "a number (System.Double)";
            case TK.Decimal: return "a finite number (System.Decimal)";
            case TK.BigInt: return "a BigInt (System.Numerics.BigInteger)";
            case TK.String: return "a string";
            case TK.Char: return "a one-character string (System.Char)";
            case TK.Enum:
                return "one of " + string.Join(", ", EnumMembers(d.Symbol).Select(m => "'" + m.Name + "'")) + " (" + d.Symbol.Name + ")";
            case TK.JsValue: return "any value";
            case TK.JsObject: return "an object";
            case TK.Nullable: return Expect(d.Inner!) + " or null";
            case TK.Array:
            case TK.Seq: return "an array (" + d.Symbol.ToDisplayString(Classifier.Short) + ")";
            case TK.Dict: return "an object whose values are " + Expect(d.Inner!) + " (" + d.Symbol.ToDisplayString(Classifier.Short) + ")";
            case TK.Func: return "a function";
            case TK.Host: return ("AEIOU".IndexOf(d.Export!.JsName[0]) >= 0 ? "an " : "a ") + d.Export.JsName + " object";
            case TK.Value: return "an " + d.Export!.JsName + " object " + ShapeOf(d.Export.Type);
            default: return "a supported value";
        }
    }

    private static string SpecialName(ITypeSymbol t) => "System." + t.Name;

    private string ShapeOf(INamedTypeSymbol t)
    {
        List<ValueMember> ms = ValueMembers(t, null);
        return "{ " + string.Join(", ", ms.Select(m => m.JsName + (m.Required ? string.Empty : "?"))) + " }";
    }

    private static IEnumerable<IFieldSymbol> EnumMembers(ITypeSymbol t) =>
        t.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue);

    private string Conv(TypeDesc d)
    {
        if (_conv.TryGetValue(d.Cs, out string? name))
        {
            return name;
        }

        name = "C" + (_n++).ToString(CultureInfo.InvariantCulture);
        _conv[d.Cs] = name;
        string sig = "private static bool " + name + "(JsValue v, ref JsConv c, out " + d.Cs + " r)";
        var h = new StringBuilder();
        switch (d.Kind)
        {
            case TK.Bool:
                h.Append("    ").Append(sig).Append(" => JsMarshal.TryBool(v, ref c, out r);\n\n");
                break;
            case TK.Int:
                {
                    string t = d.Symbol.ToDisplayString(Classifier.Cs);
                    h.Append("    ").Append(sig).Append("\n    {\n        if (JsMarshal.TryInteger(v, ref c, (long)").Append(t).Append(".MinValue, (long)").Append(t)
                        .Append(".MaxValue, ").Append(Lit(SpecialName(d.Symbol))).Append(", out long x))\n        {\n            r = (").Append(t).Append(")x;\n            return true;\n        }\n\n        r = default;\n        return false;\n    }\n\n");
                    break;
                }

            case TK.UInt64:
                h.Append("    ").Append(sig).Append("\n    {\n        if (JsMarshal.TryUnsigned(v, ref c, ulong.MaxValue, \"System.UInt64\", out ulong x))\n        {\n            r = x;\n            return true;\n        }\n\n        r = default;\n        return false;\n    }\n\n");
                break;
            case TK.Float:
                h.Append("    ").Append(sig).Append("\n    {\n        if (JsMarshal.TryDouble(v, ref c, \"System.Single\", out double x))\n        {\n            r = (float)x;\n            return true;\n        }\n\n        r = default;\n        return false;\n    }\n\n");
                break;
            case TK.Double:
                h.Append("    ").Append(sig).Append(" => JsMarshal.TryDouble(v, ref c, \"System.Double\", out r);\n\n");
                break;
            case TK.Decimal:
                h.Append("    ").Append(sig).Append(" => JsMarshal.TryDecimal(v, ref c, out r);\n\n");
                break;
            case TK.BigInt:
                h.Append("    ").Append(sig).Append(" => JsMarshal.TryBigInt(v, ref c, out r);\n\n");
                break;
            case TK.String:
                h.Append("    ").Append(sig).Append(" => JsMarshal.TryString(v, ref c, out r);\n\n");
                break;
            case TK.Char:
                h.Append("    ").Append(sig).Append(" => JsMarshal.TryChar(v, ref c, out r);\n\n");
                break;
            case TK.JsValue:
                h.Append("    ").Append(sig).Append("\n    {\n        r = v;\n        return true;\n    }\n\n");
                break;
            case TK.JsObject:
                h.Append("    ").Append(sig).Append(" => JsMarshal.TryObject(v, ref c, out r);\n\n");
                break;
            case TK.Enum:
                {
                    string names = "EN" + name;
                    string vals = "EV" + name;
                    var ms = EnumMembers(d.Symbol).ToList();
                    h.Append("    private static readonly string[] ").Append(names).Append(" = [").Append(string.Join(", ", ms.Select(m => Lit(EnumJsName(m))))).Append("];\n");
                    h.Append("    private static readonly long[] ").Append(vals).Append(" = [").Append(string.Join(", ", ms.Select(m => "(long)" + d.Symbol.ToDisplayString(Classifier.Cs) + "." + m.Name))).Append("];\n\n");
                    h.Append("    ").Append(sig).Append("\n    {\n        if (JsMarshal.TryEnum(v, ref c, ").Append(names).Append(", ").Append(vals).Append(", ").Append(Lit(d.Symbol.Name)).Append(", out long x))\n        {\n            r = (").Append(d.Cs).Append(")x;\n            return true;\n        }\n\n        r = default;\n        return false;\n    }\n\n");
                    break;
                }

            case TK.Nullable:
                {
                    string inner = Conv(d.Inner!);
                    string innerCs = d.Inner!.Cs;
                    h.Append("    ").Append(sig).Append("\n    {\n        if (v.IsNullish)\n        {\n            r = default;\n            return true;\n        }\n\n        if (").Append(inner).Append("(v, ref c, out ").Append(innerCs).Append(" x))\n        {\n            r = x;\n            return true;\n        }\n\n        r = default;\n        return false;\n    }\n\n");
                    break;
                }

            case TK.Array:
            case TK.Seq:
                {
                    string elem = Conv(d.Inner!);
                    h.Append("    ").Append(sig).Append("\n    {\n        if (JsMarshal.TryList<").Append(d.Inner!.Cs).Append(">(v, ref c, ").Append(elem).Append(", ").Append(Lit(Expect(d))).Append(", out var l))\n        {\n            r = l");
                    h.Append(d.IsArrayType ? ".ToArray()" : string.Empty).Append(";\n            return true;\n        }\n\n        r = default!;\n        return false;\n    }\n\n");
                    break;
                }

            case TK.Dict:
                {
                    string elem = Conv(d.Inner!);
                    h.Append("    ").Append(sig).Append("\n    {\n        if (JsMarshal.TryDictionary<").Append(d.Inner!.Cs).Append(">(v, ref c, ").Append(elem).Append(", ").Append(Lit(Expect(d))).Append(", out var l))\n        {\n            r = l;\n            return true;\n        }\n\n        r = default!;\n        return false;\n    }\n\n");
                    break;
                }

            case TK.Func:
                EmitFuncConverter(h, d, sig);
                break;
            case TK.Host:
                h.Append("    ").Append(sig).Append(" => JsMarshal.TryHost<").Append(d.Cs).Append(">(v, ref c, ").Append(Lit(Expect(d))).Append(", out r);\n\n");
                break;
            case TK.Value:
                h.Append("    ").Append(sig).Append(" => ").Append(d.Export!.Binding).Append(".TryFromJs(v, ref c, out r);\n\n");
                break;
        }

        _helpers.Append(h);
        return name;
    }

    private static string EnumJsName(IFieldSymbol m)
    {
        AttributeData? a = Classifier.FindAttribute(m, Classifier.ExportAttr);
        if (a is not null)
        {
            foreach (KeyValuePair<string, TypedConstant> kv in a.NamedArguments)
            {
                if (kv.Key == "Name" && kv.Value.Value is string n && n.Length > 0)
                {
                    return n;
                }
            }
        }

        return m.Name;
    }

    private void EmitFuncConverter(StringBuilder h, TypeDesc d, string sig)
    {
        var ps = d.FuncParams.Select((p, i) => "a" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        h.Append("    ").Append(sig).Append("\n    {\n");
        h.Append("        if (!JsMarshal.TryFunction(v, ref c, out JsObject fn))\n        {\n            r = default!;\n            return false;\n        }\n\n");
        h.Append("        JsRealm realm = c.Realm;\n        string cbName = c.Path;\n        string member = c.Member;\n        string signature = c.Signature;\n");
        h.Append("        r = (").Append(string.Join(", ", ps)).Append(") =>\n        {\n");
        string args = string.Join(", ", d.FuncParams.Select((p, i) => ToJs(p) + "(realm, a" + i.ToString(CultureInfo.InvariantCulture) + ")"));
        h.Append("            JsValue res = JsMarshal.CallBack(realm, fn").Append(args.Length > 0 ? ", " + args : string.Empty).Append(");\n");
        bool isAction = d.Symbol.OriginalDefinition.Name == "Action";
        if (isAction)
        {
            h.Append("            _ = res;\n");
        }
        else if (d.FuncAsync == 1)
        {
            h.Append("            return JsMarshal.AwaitPromise(realm, res);\n");
        }
        else
        {
            h.Append("            var cc = new JsConv(realm, member, signature);\n            cc.Result(cbName);\n");
            if (d.FuncAsync == 2)
            {
                h.Append("            return JsMarshal.AwaitPromise<").Append(d.FuncReturn!.Cs).Append(">(realm, res, cc, ").Append(Conv(d.FuncReturn)).Append(");\n");
            }
            else
            {
                h.Append("            if (!").Append(Conv(d.FuncReturn!)).Append("(res, ref cc, out ").Append(d.FuncReturn!.Cs).Append(" x))\n            {\n                throw cc.ToException();\n            }\n\n            return x;\n");
            }
        }

        h.Append("        };\n        return true;\n    }\n\n");
    }

    private string ToJs(TypeDesc d)
    {
        if (_toJs.TryGetValue(d.Cs, out string? name))
        {
            return name;
        }

        name = "J" + (_n++).ToString(CultureInfo.InvariantCulture);
        _toJs[d.Cs] = name;
        string sig = "private static JsValue " + name + "(JsRealm realm, " + d.Cs + " v)";
        var h = new StringBuilder();
        switch (d.Kind)
        {
            case TK.Bool:
                h.Append("    ").Append(sig).Append(" => JsValue.FromBoolean(v);\n\n");
                break;
            case TK.Int when d.Symbol.SpecialType == SpecialType.System_Int64:
                h.Append("    ").Append(sig).Append(" => JsMarshal.FromInt64(v);\n\n");
                break;
            case TK.Int:
            case TK.Float:
            case TK.Double:
            case TK.Decimal:
                h.Append("    ").Append(sig).Append(" => JsValue.FromNumber((double)v);\n\n");
                break;
            case TK.UInt64:
                h.Append("    ").Append(sig).Append(" => JsMarshal.FromUInt64(v);\n\n");
                break;
            case TK.BigInt:
                h.Append("    ").Append(sig).Append(" => JsValue.FromBigInt(v);\n\n");
                break;
            case TK.String:
                h.Append("    ").Append(sig).Append(" => JsMarshal.FromString(v);\n\n");
                break;
            case TK.Char:
                h.Append("    ").Append(sig).Append(" => JsValue.FromString(v.ToString());\n\n");
                break;
            case TK.JsValue:
                h.Append("    ").Append(sig).Append(" => v;\n\n");
                break;
            case TK.JsObject:
                h.Append("    ").Append(sig).Append(" => v is null ? JsValue.Null : JsValue.FromObject(v);\n\n");
                break;
            case TK.Enum:
                {
                    var ms = EnumMembers(d.Symbol).ToList();
                    string t = d.Symbol.ToDisplayString(Classifier.Cs);
                    h.Append("    ").Append(sig).Append("\n    {\n        switch (v)\n        {\n");
                    foreach (IFieldSymbol m in ms.GroupBy(m => m.ConstantValue).Select(g => g.First()))
                    {
                        h.Append("            case ").Append(t).Append('.').Append(m.Name).Append(":\n                return JsValue.FromString(").Append(Lit(EnumJsName(m))).Append(");\n");
                    }

                    h.Append("            default:\n                return JsValue.FromNumber((double)(long)v);\n        }\n    }\n\n");
                    break;
                }

            case TK.Nullable:
                {
                    string inner = ToJs(d.Inner!);
                    bool isValueType = d.Symbol.IsValueType;
                    h.Append("    ").Append(sig).Append(" => ").Append(isValueType ? "v.HasValue ? " + inner + "(realm, v.Value)" : "v is null ? JsValue.Null : " + inner + "(realm, v")
                        .Append(isValueType ? " : JsValue.Null;\n\n" : ");\n\n");
                    break;
                }

            case TK.Array:
            case TK.Seq:
                h.Append("    ").Append(sig).Append(" => JsMarshal.FromSequence<").Append(d.Inner!.Cs).Append(">(realm, v, ").Append(ToJs(d.Inner)).Append(");\n\n");
                break;
            case TK.Dict:
                h.Append("    ").Append(sig).Append(" => JsMarshal.FromDictionary<").Append(d.Inner!.Cs).Append(">(realm, v, ").Append(ToJs(d.Inner)).Append(");\n\n");
                break;
            case TK.Host:
                h.Append("    ").Append(sig).Append(" => realm.Wrap(v);\n\n");
                break;
            case TK.Value:
                h.Append("    ").Append(sig).Append(" => ").Append(d.Export!.Binding).Append(".ToJs(realm, v);\n\n");
                break;
        }

        _helpers.Append(h);
        return name;
    }

    // ---- kinds for overload resolution --------------------------------------------------------------------

    private static string KindClass(TypeDesc d) => d.Kind switch
    {
        TK.Bool => "bool",
        TK.Int => "int",
        TK.UInt64 => "int",
        TK.Float or TK.Double or TK.Decimal => "number",
        TK.BigInt => "bigint",
        TK.String or TK.Char => "string",
        TK.Enum => "enum",
        TK.JsValue => "any",
        TK.JsObject => "object",
        TK.Nullable => KindClass(d.Inner!),
        TK.Array or TK.Seq => "array",
        TK.Dict or TK.Value => "object",
        TK.Func => "function",
        TK.Host => "host:" + d.Symbol.ToDisplayString(),
        _ => "?",
    };

    private static string KindTest(TypeDesc d, string v)
    {
        string t = d.Kind switch
        {
            TK.Bool => v + ".IsBoolean",
            TK.Int or TK.UInt64 => "JsMarshal.IsInteger(" + v + ")",
            TK.Float or TK.Double or TK.Decimal => v + ".IsNumber",
            TK.BigInt => v + ".IsBigInt",
            TK.String or TK.Char => v + ".IsString",
            TK.Enum => "(" + v + ".IsString || " + v + ".IsNumber)",
            TK.JsValue => "true",
            TK.JsObject => v + ".IsObject",
            TK.Nullable => KindTest(d.Inner!, v),
            TK.Array or TK.Seq => "JsMarshal.IsArrayValue(" + v + ")",
            TK.Dict or TK.Value => "JsMarshal.IsPlainObject(" + v + ")",
            TK.Func => "JsMarshal.IsFunctionValue(" + v + ")",
            TK.Host => "JsMarshal.IsHost<" + d.Cs + ">(" + v + ")",
            _ => "false",
        };
        return d.Kind == TK.Nullable ? "(" + v + ".IsNullish || " + t + ")" : t;
    }

    private static int Specificity(TypeDesc d) => d.Kind switch
    {
        TK.Int or TK.UInt64 or TK.Bool or TK.Host => 3,
        TK.JsValue => 0,
        TK.Nullable => Specificity(d.Inner!),
        _ => 2,
    };

    // ---- method groups --------------------------------------------------------------------------------------

    private sealed class Param
    {
        internal IParameterSymbol Symbol = null!;
        internal TypeDesc Type = null!;
        internal bool Optional;
        internal bool IsParams;
        internal TypeDesc? ElemType;
    }

    private sealed class Overload
    {
        internal IMethodSymbol Method = null!;
        internal List<Param> Params = [];
        internal int Required;
        internal int Max;
        internal bool HasParams;
        internal int Score;
        internal TypeDesc? Ret;
        internal string RetKind = "plain";
        internal TypeDesc? RetElem;
    }

    private Overload? Analyze(IMethodSymbol m, bool isCtor)
    {
        var o = new Overload { Method = m };
        bool ok = true;
        for (int i = 0; i < m.Parameters.Length; i++)
        {
            IParameterSymbol p = m.Parameters[i];
            if (p.RefKind != RefKind.None)
            {
                Report(Descriptors.UnsupportedMember, m, MemberText(m), "parameter '" + p.Name + "' is " + p.RefKind.ToString().ToLowerInvariant(), "change it to a plain by-value parameter");
                ok = false;
                continue;
            }

            var prm = new Param { Symbol = p, IsParams = p.IsParams && p.Type is IArrayTypeSymbol };
            if (prm.IsParams)
            {
                prm.ElemType = _cls.Classify(((IArrayTypeSymbol)p.Type).ElementType);
                prm.Type = _cls.Classify(p.Type);
                o.HasParams = true;
                prm.Optional = true;
                if (!CheckType(prm.ElemType, m, p.Type))
                {
                    ok = false;
                }
            }
            else
            {
                prm.Type = _cls.Classify(p.Type);
                prm.Optional = p.HasExplicitDefaultValue;
                if (!CheckType(prm.Type, m, p.Type))
                {
                    ok = false;
                }
            }

            o.Params.Add(prm);
        }

        // Trailing nullable parameters may be left out from script.
        for (int i = o.Params.Count - 1; i >= 0; i--)
        {
            Param p = o.Params[i];
            if (p.Optional)
            {
                continue;
            }

            if (p.Type.Kind == TK.Nullable)
            {
                p.Optional = true;
            }
            else
            {
                break;
            }
        }

        o.Required = o.Params.Count(p => !p.Optional);
        o.Max = o.HasParams ? int.MaxValue : o.Params.Count;
        o.Score = o.Params.Sum(p => Specificity(p.IsParams ? p.ElemType! : p.Type));

        if (!isCtor)
        {
            ClassifyReturn(m, o, ref ok);
        }

        return ok ? o : null;
    }

    private void ClassifyReturn(IMethodSymbol m, Overload o, ref bool ok)
    {
        ITypeSymbol rt = m.ReturnType;
        if (m.ReturnsByRef || m.ReturnsByRefReadonly)
        {
            Report(Descriptors.UnsupportedMember, m, MemberText(m), "it returns by reference", "return by value");
            ok = false;
            return;
        }

        if (rt.SpecialType == SpecialType.System_Void)
        {
            o.RetKind = "void";
            return;
        }

        string def = rt.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        switch (def)
        {
            case "System.Threading.Tasks.Task":
                o.RetKind = "task";
                return;
            case "System.Threading.Tasks.ValueTask":
                o.RetKind = "valuetask";
                return;
            case "System.Threading.Tasks.Task<TResult>":
            case "System.Threading.Tasks.ValueTask<TResult>":
                o.RetKind = def.Contains("ValueTask") ? "valuetaskT" : "taskT";
                o.RetElem = _cls.Classify(((INamedTypeSymbol)rt).TypeArguments[0]);
                if (!CheckType(o.RetElem, m, ((INamedTypeSymbol)rt).TypeArguments[0]))
                {
                    ok = false;
                }

                return;
            case "System.Collections.Generic.IEnumerable<T>":
                o.RetKind = "iter";
                o.RetElem = _cls.Classify(((INamedTypeSymbol)rt).TypeArguments[0]);
                if (!CheckType(o.RetElem, m, ((INamedTypeSymbol)rt).TypeArguments[0]))
                {
                    ok = false;
                }

                return;
            case "System.Collections.Generic.IAsyncEnumerable<T>":
                o.RetKind = "aiter";
                o.RetElem = _cls.Classify(((INamedTypeSymbol)rt).TypeArguments[0]);
                if (!CheckType(o.RetElem, m, ((INamedTypeSymbol)rt).TypeArguments[0]))
                {
                    ok = false;
                }

                return;
        }

        o.Ret = _cls.Classify(rt);
        if (!CheckType(o.Ret, m, rt))
        {
            ok = false;
        }
    }

    private bool CheckType(TypeDesc d, ISymbol at, ITypeSymbol shown)
    {
        TypeDesc? bad = Classifier.FirstUnsupported(d);
        if (bad is null)
        {
            return true;
        }

        if (d.Kind == TK.Func && d.FuncParams.Concat(d.FuncReturn is null ? [] : [d.FuncReturn]).Any(x => Classifier.FirstUnsupported(x) is not null))
        {
            bad = d.FuncParams.Concat(d.FuncReturn is null ? [] : [d.FuncReturn]).Select(Classifier.FirstUnsupported).First(x => x is not null)!;
        }

        Report(Descriptors.UnsupportedType, at, MemberText(at), bad.Symbol.ToDisplayString(Classifier.Short), bad.Why ?? "unsupported", bad.Fix ?? "use a supported type");
        _ = shown;
        return false;
    }

    private string MemberText(ISymbol m) => _typeShort + "." + (m is IMethodSymbol { MethodKind: MethodKind.Constructor } ? _type.Name : m.Name);

    private string Signature(IMethodSymbol m)
    {
        string name = m.MethodKind == MethodKind.Constructor ? _type.Name : m.Name;
        return _typeShort + "." + name + "(" + string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString(Classifier.Short) + " " + p.Name)) + ")";
    }

    private static string DefaultOf(IParameterSymbol p, string cs)
    {
        if (!p.HasExplicitDefaultValue || p.ExplicitDefaultValue is null)
        {
            return "default!";
        }

        object value = p.ExplicitDefaultValue;
        if (value is string s)
        {
            return Lit(s);
        }

        if (value is bool b)
        {
            return b ? "true" : "false";
        }

        if (value is char ch)
        {
            return SymbolDisplay.FormatLiteral(ch, true);
        }

        return "(" + cs + ")" + SymbolDisplay.FormatPrimitive(value, false, false);
    }

    /// <summary>Writes the statements that convert arguments and call; <paramref name="invoke"/> builds the call expression from the argument names.</summary>
    private void EmitOverload(StringBuilder sb, int ind, Overload o, Func<string, string> invoke, bool isCtor)
    {
        string i0 = Ind(ind);
        var argNames = new List<string>();
        for (int i = 0; i < o.Params.Count; i++)
        {
            Param p = o.Params[i];
            string pn = "p" + i.ToString(CultureInfo.InvariantCulture);
            string idx = i.ToString(CultureInfo.InvariantCulture);
            if (p.IsParams)
            {
                string elemConv = Conv(p.ElemType!);
                sb.Append(i0).Append("var rest").Append(idx).Append(" = new global::System.Collections.Generic.List<").Append(p.ElemType!.Cs).Append(">();\n");
                sb.Append(i0).Append("for (int k = ").Append(idx).Append("; k < args.Length; k++)\n").Append(i0).Append("{\n");
                sb.Append(i0).Append("    c.Arg(").Append(Lit(p.Symbol.Name)).Append(" + \"[\" + (k - ").Append(idx).Append(").ToString() + \"]\");\n");
                sb.Append(i0).Append("    if (!").Append(elemConv).Append("(args[k], ref c, out ").Append(p.ElemType.Cs).Append(" e))\n").Append(i0).Append("    {\n").Append(i0).Append("        throw c.ToException();\n").Append(i0).Append("    }\n\n");
                sb.Append(i0).Append("    rest").Append(idx).Append(".Add(e);\n").Append(i0).Append("}\n\n");
                sb.Append(i0).Append(p.Type.Cs).Append(' ').Append(pn).Append(" = rest").Append(idx).Append(".ToArray();\n");
            }
            else if (p.Optional)
            {
                string conv = Conv(p.Type);
                sb.Append(i0).Append(p.Type.Cs).Append(' ').Append(pn).Append(" = ").Append(DefaultOf(p.Symbol, p.Type.Cs)).Append(";\n");
                sb.Append(i0).Append("if (args.Length > ").Append(idx).Append(" && !args[").Append(idx).Append("].IsUndefined)\n").Append(i0).Append("{\n");
                sb.Append(i0).Append("    c.Arg(").Append(Lit(p.Symbol.Name)).Append(");\n");
                sb.Append(i0).Append("    if (!").Append(conv).Append("(args[").Append(idx).Append("], ref c, out ").Append(pn).Append("))\n").Append(i0).Append("    {\n").Append(i0).Append("        throw c.ToException();\n").Append(i0).Append("    }\n").Append(i0).Append("}\n\n");
            }
            else
            {
                string conv = Conv(p.Type);
                sb.Append(i0).Append("c.Arg(").Append(Lit(p.Symbol.Name)).Append(");\n");
                sb.Append(i0).Append("if (args.Length <= ").Append(idx).Append(")\n").Append(i0).Append("{\n").Append(i0).Append("    c.Missing(").Append(Lit(Expect(p.Type))).Append(");\n").Append(i0).Append("    throw c.ToException();\n").Append(i0).Append("}\n\n");
                sb.Append(i0).Append("if (!").Append(conv).Append("(args[").Append(idx).Append("], ref c, out ").Append(p.Type.Cs).Append(' ').Append(pn).Append("))\n").Append(i0).Append("{\n").Append(i0).Append("    throw c.ToException();\n").Append(i0).Append("}\n\n");
            }

            argNames.Add(pn);
        }

        string call = invoke(string.Join(", ", argNames));
        string member = Lit(_typeShort + "." + (isCtor ? _type.Name : o.Method.Name));
        switch (o.RetKind)
        {
            case "void":
                sb.Append(i0).Append(call).Append(";\n").Append(i0).Append("return JsValue.Undefined;\n");
                break;
            case "task":
                sb.Append(i0).Append("return JsMarshal.FromTask(realm, ").Append(member).Append(", () => ").Append(call).Append(");\n");
                break;
            case "valuetask":
                sb.Append(i0).Append("return JsMarshal.FromTask(realm, ").Append(member).Append(", () => ").Append(call).Append(".AsTask());\n");
                break;
            case "taskT":
                sb.Append(i0).Append("return JsMarshal.FromTask<").Append(o.RetElem!.Cs).Append(">(realm, ").Append(member).Append(", () => ").Append(call).Append(", ").Append(ToJs(o.RetElem)).Append(");\n");
                break;
            case "valuetaskT":
                sb.Append(i0).Append("return JsMarshal.FromTask<").Append(o.RetElem!.Cs).Append(">(realm, ").Append(member).Append(", () => ").Append(call).Append(".AsTask(), ").Append(ToJs(o.RetElem)).Append(");\n");
                break;
            case "iter":
                sb.Append(i0).Append("return JsMarshal.FromEnumerable<").Append(o.RetElem!.Cs).Append(">(realm, ").Append(member).Append(", ").Append(call).Append(", ").Append(ToJs(o.RetElem)).Append(");\n");
                break;
            case "aiter":
                sb.Append(i0).Append("return JsMarshal.FromAsyncEnumerable<").Append(o.RetElem!.Cs).Append(">(realm, ").Append(member).Append(", ").Append(call).Append(", ").Append(ToJs(o.RetElem)).Append(");\n");
                break;
            default:
                if (isCtor)
                {
                    sb.Append(i0).Append("return JsValue.FromObject(realm.NewHostObject(").Append(call).Append(", proto));\n");
                }
                else
                {
                    sb.Append(i0).Append("return ").Append(ToJs(o.Ret!)).Append("(realm, ").Append(call).Append(");\n");
                }

                break;
        }
    }

    private static string Describe(Overload o) => string.Join(", ", o.Params.Select(p => p.Symbol.Type.ToDisplayString(Classifier.Short)));

    /// <summary>Emits the body of a method group (all overloads of one script name) into <paramref name="sb"/>.</summary>
    private void EmitGroup(StringBuilder sb, int ind, List<Overload> overloads, Func<Overload, Func<string, string>> invokeFor, bool isCtor)
    {
        string i0 = Ind(ind);
        Overload first = overloads[0];
        string memberName = _typeShort + "." + (isCtor ? _type.Name : first.Method.Name);
        string sigs = string.Join("; ", overloads.Select(o => Signature(o.Method)));
        sb.Append(i0).Append("var c = new JsConv(realm, ").Append(Lit(memberName)).Append(", ").Append(Lit(overloads.Count == 1 ? Signature(first.Method) : sigs)).Append(");\n\n");
        if (overloads.Count == 1)
        {
            EmitOverload(sb, ind, first, invokeFor(first), isCtor);
            return;
        }

        foreach (Overload o in overloads.OrderByDescending(x => x.Score))
        {
            var conds = new List<string>
            {
                "args.Length >= " + o.Required.ToString(CultureInfo.InvariantCulture),
            };
            if (!o.HasParams)
            {
                conds.Add("args.Length <= " + o.Params.Count.ToString(CultureInfo.InvariantCulture));
            }

            for (int i = 0; i < o.Params.Count; i++)
            {
                Param p = o.Params[i];
                string idx = i.ToString(CultureInfo.InvariantCulture);
                if (p.IsParams)
                {
                    break;
                }

                string test = KindTest(p.Type, "args[" + idx + "]");
                conds.Add(p.Optional ? "(args.Length <= " + idx + " || args[" + idx + "].IsUndefined || " + test + ")" : "(" + test + ")");
            }

            sb.Append(i0).Append("if (").Append(string.Join(" && ", conds)).Append(")\n").Append(i0).Append("{\n");
            EmitOverload(sb, ind + 1, o, invokeFor(o), isCtor);
            sb.Append(i0).Append("}\n\n");
        }

        sb.Append(i0).Append("throw JsMarshal.NoOverload(realm, ").Append(Lit(memberName)).Append(", args, new string[] { ").Append(string.Join(", ", overloads.Select(o => Lit(Signature(o.Method))))).Append(" });\n");
    }

    private void CheckAmbiguity(List<Overload> group)
    {
        for (int a = 0; a < group.Count; a++)
        {
            for (int b = a + 1; b < group.Count; b++)
            {
                Overload x = group[a];
                Overload y = group[b];
                int lo = Math.Max(x.Required, y.Required);
                int hi = Math.Min(x.Max, y.Max);
                for (int n = lo; n <= Math.Min(hi, 16); n++)
                {
                    bool same = true;
                    for (int i = 0; i < n && same; i++)
                    {
                        TypeDesc tx = i < x.Params.Count ? (x.Params[i].IsParams ? x.Params[i].ElemType! : x.Params[i].Type) : x.Params[x.Params.Count - 1].ElemType!;
                        TypeDesc ty = i < y.Params.Count ? (y.Params[i].IsParams ? y.Params[i].ElemType! : y.Params[i].Type) : y.Params[y.Params.Count - 1].ElemType!;
                        string kx = KindClass(tx);
                        string ky = KindClass(ty);
                        same = kx == ky || kx == "any" || ky == "any";
                    }

                    if (same)
                    {
                        Report(Descriptors.AmbiguousOverloads, y.Method, Signature(x.Method), Signature(y.Method), _typeShort + "." + x.Method.Name, n);
                        return;
                    }
                }
            }
        }
    }

    // ---- members -----------------------------------------------------------------------------------------------

    private static bool IsIgnored(ISymbol s) => Classifier.FindAttribute(s, Classifier.IgnoreAttr) is not null;

    private static readonly HashSet<string> SkippedMethods = ["Equals", "GetHashCode", "GetType", "Finalize", "MemberwiseClone", "Deconstruct", "PrintMembers", "<Clone>$", "ReferenceEquals"];

    private IEnumerable<ISymbol> CandidateMembers()
    {
        var seen = new List<INamedTypeSymbol> { _type };
        if (_type.TypeKind == TypeKind.Interface)
        {
            seen.AddRange(_type.AllInterfaces);
        }
        else
        {
            for (INamedTypeSymbol? b = _type.BaseType; b is not null && b.SpecialType != SpecialType.System_Object && b.SpecialType != SpecialType.System_ValueType; b = b.BaseType)
            {
                // A base class that is exported has its own binding; its members come through the prototype chain.
                if (Classifier.GetExport(b) is not null)
                {
                    break;
                }

                seen.Add(b);
            }
        }

        foreach (INamedTypeSymbol t in seen)
        {
            foreach (ISymbol m in t.GetMembers())
            {
                if ((m.IsImplicitlyDeclared && m is not IMethodSymbol { MethodKind: MethodKind.Constructor }) || m.DeclaredAccessibility != Accessibility.Public || IsIgnored(m))
                {
                    continue;
                }

                // An override is exported once, from the most derived declaration.
                if (m is IMethodSymbol { IsOverride: true } && !SymbolEqualityComparer.Default.Equals(t, _type) && seen.Any(s => !SymbolEqualityComparer.Default.Equals(s, t)))
                {
                    continue;
                }

                yield return m;
            }
        }
    }

    internal sealed class ValueMember
    {
        internal ISymbol Symbol = null!;
        internal string JsName = "";
        internal TypeDesc Type = null!;
        internal bool Required;
        internal bool Settable;
        internal bool InitOnly;
        internal IParameterSymbol? CtorParam;
        internal string Read => Symbol.Name;
    }

    private List<ValueMember> ValueMembers(INamedTypeSymbol t, List<Diagnostic>? diags)
    {
        var list = new List<ValueMember>();
        for (INamedTypeSymbol? cur = t; cur is not null && cur.SpecialType != SpecialType.System_Object && cur.SpecialType != SpecialType.System_ValueType; cur = cur.BaseType)
        {
            foreach (ISymbol m in cur.GetMembers())
            {
                if (m.IsImplicitlyDeclared || m.DeclaredAccessibility != Accessibility.Public || m.IsStatic || IsIgnored(m))
                {
                    continue;
                }

                if (m is IPropertySymbol { IsIndexer: false, GetMethod: not null } p && p.GetMethod.DeclaredAccessibility == Accessibility.Public)
                {
                    if (list.Any(x => x.Symbol.Name == p.Name))
                    {
                        continue;
                    }

                    list.Add(new ValueMember
                    {
                        Symbol = p,
                        JsName = diags is null ? JsNameOf(p.Name) : MemberJsName(p),
                        Type = _cls.Classify(p.Type),
                        Required = p.IsRequired,
                        Settable = p.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false },
                        InitOnly = p.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: true },
                    });
                }
                else if (m is IFieldSymbol { IsConst: false } f)
                {
                    if (list.Any(x => x.Symbol.Name == f.Name))
                    {
                        continue;
                    }

                    list.Add(new ValueMember
                    {
                        Symbol = f,
                        JsName = diags is null ? JsNameOf(f.Name) : MemberJsName(f),
                        Type = _cls.Classify(f.Type),
                        Required = f.IsRequired,
                        Settable = !f.IsReadOnly,
                    });
                }
            }
        }

        return list;
    }

    // ---- the binding class -----------------------------------------------------------------------------------------

    internal string Emit(out bool isInstalled)
    {
        isInstalled = true;
        if (_type.TypeKind == TypeKind.Enum)
        {
            return EmitEnum();
        }

        if (_export.IsValue)
        {
            isInstalled = false;
            return EmitValue();
        }

        return EmitReference();
    }

    private string Header(string kind)
    {
        var sb = new StringBuilder();
        sb.Append("// <auto-generated/>\n// Host binding for ").Append(_typeShort).Append(" (").Append(kind).Append("). Generated at build time; do not edit.\n#nullable enable\n#pragma warning disable\n\nusing RecursiveAI.OneEcma;\n\n");
        string? ns = _type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
        if (ns is not null)
        {
            sb.Append("namespace ").Append(ns).Append("\n{\n");
        }

        return sb.ToString();
    }

    private string Footer() => _type.ContainingNamespace is { IsGlobalNamespace: false } ? "}\n" : string.Empty;

    private string BindingVisibility()
    {
        for (INamedTypeSymbol? t = _type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility != Accessibility.Public)
            {
                return "internal";
            }
        }

        return "public";
    }

    internal bool IsPublic => BindingVisibility() == "public";

    private string Wrap(string kind, string classBody)
    {
        string name = ExportInfo.BindingNameOf(_type, false);
        return Header(kind) + "    " + BindingVisibility() + " static class " + name + "\n    {\n" + classBody + _helpers + "    }\n" + Footer();
    }

    private string EmitEnum()
    {
        var ms = EnumMembers(_type).ToList();
        var b = new StringBuilder();
        b.Append("        /// <summary>Installs the enum as a global object of its member names.</summary>\n");
        b.Append("        public static readonly JsHostType JsType = new(typeof(").Append(_typeCs).Append("), ").Append(Lit(_export.JsName)).Append(", realm =>\n        {\n            JsObject o = realm.NewObject();\n");
        foreach (IFieldSymbol m in ms)
        {
            b.Append("            realm.DefineData(o, ").Append(Lit(EnumJsName(m))).Append(", JsValue.FromString(").Append(Lit(EnumJsName(m))).Append("), JsAttributes.Enumerable);\n");
        }

        b.Append("            realm.DefineData(realm.Global, ").Append(Lit(_export.JsName)).Append(", JsValue.FromObject(o), JsAttributes.Method);\n            return o;\n        });\n\n");
        return Wrap("enum", b.ToString());
    }

    private string EmitValue()
    {
        List<ValueMember> members = ValueMembers(_type, _diags);
        CheckNames(members.Select(m => (m.Symbol, m.JsName)).ToList());
        bool fail = false;
        foreach (ValueMember m in members)
        {
            if (!CheckType(m.Type, m.Symbol, (m.Symbol as IPropertySymbol)?.Type ?? ((IFieldSymbol)m.Symbol).Type))
            {
                fail = true;
            }
        }

        // Choose how script objects become the type.
        IMethodSymbol? ctor = null;
        if (!fail && _type.TypeKind != TypeKind.Interface)
        {
            var candidates = _type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsImplicitlyDeclared || c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length > 0).ToList();
            IMethodSymbol? implicitDefault = _type.TypeKind == TypeKind.Struct ? null : _type.InstanceConstructors.FirstOrDefault(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 0);
            ctor = candidates
                .Where(c => c.Parameters.Length > 0
                    && !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, _type))
                    && c.Parameters.All(p => members.Any(m => string.Equals(m.Symbol.Name, p.Name, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(c => c.Parameters.Length)
                .FirstOrDefault();
            if (ctor is null && implicitDefault is null && _type.TypeKind != TypeKind.Struct)
            {
                Report(Descriptors.CannotBuildValueType, _type, _typeShort, "it has no public parameterless constructor and no public constructor whose parameters match its properties");
                fail = true;
            }
        }

        if (fail)
        {
            return Wrap("value", string.Empty);
        }

        foreach (ValueMember m in members)
        {
            IParameterSymbol? cp = ctor?.Parameters.FirstOrDefault(p => string.Equals(p.Name, m.Symbol.Name, StringComparison.OrdinalIgnoreCase));
            m.CtorParam = cp;
            if (cp is not null)
            {
                m.Required = !cp.HasExplicitDefaultValue && m.Type.Kind != TK.Nullable;
            }
        }

        var b = new StringBuilder();
        b.Append("        /// <summary>Reads a script object as ").Append(EscapeDoc(_typeShort)).Append(". Missing required properties and wrong types are reported with the property path.</summary>\n");
        b.Append("        public static bool TryFromJs(JsValue v, ref JsConv c, out ").Append(_typeCs).Append(" r)\n        {\n            r = default!;\n");
        b.Append("            if (!JsMarshal.IsPlainObject(v))\n            {\n                return c.Fail(").Append(Lit(Expect(new TypeDesc(TK.Value, _type, _typeCs) { Export = _export })) ).Append(", v);\n            }\n\n");
        b.Append("            JsObject o = v.AsObject!;\n");
        if (_export.RejectUnknown)
        {
            b.Append("            if (!JsMarshal.CheckKnown(o, Known, ref c))\n            {\n                return false;\n            }\n\n");
        }

        int idx = 0;
        foreach (ValueMember m in members)
        {
            string i = idx.ToString(CultureInfo.InvariantCulture);
            bool writable = m.CtorParam is not null || m.Settable || m.InitOnly || m.Symbol is IFieldSymbol { IsReadOnly: false };
            if (!writable)
            {
                idx++;
                continue;
            }

            string conv = Conv(m.Type);
            string dflt = m.CtorParam is not null ? DefaultOf(m.CtorParam, m.Type.Cs) : "default!";
            b.Append("            if (!JsMarshal.TryMember(o, ").Append(Lit(m.JsName)).Append(", ").Append(m.Required ? "true" : "false").Append(", ").Append(Lit(Expect(m.Type))).Append(", ref c, out JsValue mv").Append(i).Append("))\n            {\n                return false;\n            }\n\n");
            b.Append("            ").Append(m.Type.Cs).Append(" m").Append(i).Append(" = ").Append(dflt).Append(";\n            bool has").Append(i).Append(" = !mv").Append(i).Append(".IsUndefined;\n");
            b.Append("            if (has").Append(i).Append(" && !").Append(conv).Append("(mv").Append(i).Append(", ref c, out m").Append(i).Append("))\n            {\n                return c.InProperty(").Append(Lit(m.JsName)).Append(");\n            }\n\n");
            idx++;
        }

        var ctorArgs = new List<string>();
        var inits = new List<string>();
        var afters = new List<string>();
        idx = 0;
        if (ctor is not null)
        {
            foreach (IParameterSymbol p in ctor.Parameters)
            {
                int k = members.FindIndex(m => string.Equals(m.Symbol.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                ctorArgs.Add("m" + k.ToString(CultureInfo.InvariantCulture));
            }
        }

        for (int k = 0; k < members.Count; k++)
        {
            ValueMember m = members[k];
            if (m.CtorParam is not null)
            {
                continue;
            }

            string ks = k.ToString(CultureInfo.InvariantCulture);
            if (m.InitOnly || m.Required)
            {
                inits.Add(m.Read + " = m" + ks);
            }
            else if (m.Settable || m.Symbol is IFieldSymbol { IsReadOnly: false })
            {
                afters.Add("if (has" + ks + ") { r." + m.Read + " = m" + ks + "; }");
            }
        }

        b.Append("            r = new ").Append(_typeCs).Append('(').Append(string.Join(", ", ctorArgs)).Append(')');
        if (inits.Count > 0)
        {
            b.Append(" { ").Append(string.Join(", ", inits)).Append(" }");
        }

        b.Append(";\n");
        foreach (string a in afters)
        {
            b.Append("            ").Append(a).Append('\n');
        }

        b.Append("            return true;\n        }\n\n");

        b.Append("        /// <summary>Copies the value into a new plain script object.</summary>\n");
        b.Append("        public static JsValue ToJs(JsRealm realm, ").Append(_typeCs).Append(" v)\n        {\n            JsObject o = realm.NewObject();\n");
        foreach (ValueMember m in members)
        {
            b.Append("            realm.DefineData(o, ").Append(Lit(m.JsName)).Append(", ").Append(ToJs(m.Type)).Append("(realm, v.").Append(m.Read).Append("));\n");
        }

        b.Append("            return JsValue.FromObject(o);\n        }\n\n");
        if (_export.RejectUnknown)
        {
            b.Append("        private static readonly string[] Known = [").Append(string.Join(", ", members.Select(m => Lit(m.JsName)))).Append("];\n\n");
        }

        return Wrap("value", b.ToString());
    }

    private static string EscapeDoc(string s) => s.Replace("<", "&lt;").Replace(">", "&gt;");

    private void CheckNames(List<(ISymbol Symbol, string Name)> names)
    {
        foreach (IGrouping<string, (ISymbol Symbol, string Name)> g in names.GroupBy(n => n.Name))
        {
            List<(ISymbol Symbol, string Name)> items = g.ToList();
            if (items.Count > 1)
            {
                Report(Descriptors.NameCollision, items[1].Symbol, items[0].Symbol.Name, items[1].Symbol.Name, g.Key, _typeShort);
            }
        }
    }

    private string EmitReference()
    {
        bool isStatic = _type.IsStatic;
        bool isInterface = _type.TypeKind == TypeKind.Interface;
        var instanceMethods = new Dictionary<string, List<Overload>>();
        var staticMethods = new Dictionary<string, List<Overload>>();
        var instanceProps = new List<(string Js, ISymbol Sym, TypeDesc Type)>();
        var staticProps = new List<(string Js, ISymbol Sym, TypeDesc Type)>();
        var names = new List<(ISymbol Symbol, string Name)>();
        var staticNames = new List<(ISymbol Symbol, string Name)>();
        var consts = new List<(string Js, IFieldSymbol Field, TypeDesc Type)>();
        var ctors = new List<Overload>();

        foreach (ISymbol m in CandidateMembers())
        {
            switch (m)
            {
                case IMethodSymbol { MethodKind: MethodKind.Ordinary } method:
                    if (SkippedMethods.Contains(method.Name))
                    {
                        break;
                    }

                    if (method.IsGenericMethod)
                    {
                        Report(Descriptors.UnsupportedMember, m, MemberText(m), "it is generic", "add a non-generic overload that fixes the type arguments");
                        break;
                    }

                    if (Analyze(method, false) is { } ov)
                    {
                        string js = MemberJsName(method);
                        var dict = method.IsStatic ? staticMethods : instanceMethods;
                        if (!dict.TryGetValue(js, out List<Overload>? list))
                        {
                            dict[js] = list = [];
                            (method.IsStatic ? staticNames : names).Add((method, js));
                        }

                        list.Add(ov);
                    }

                    break;
                case IMethodSymbol { MethodKind: MethodKind.Constructor } ctor when !isStatic && !isInterface && !_type.IsAbstract:
                    if (Analyze(ctor, true) is { } co)
                    {
                        ctors.Add(co);
                    }

                    break;
                case IPropertySymbol prop:
                    if (prop.IsIndexer)
                    {
                        Report(Descriptors.UnsupportedMember, m, MemberText(m), "indexers have no script equivalent", "expose Get/Set methods instead");
                        break;
                    }

                    TypeDesc pt = _cls.Classify(prop.Type);
                    if (CheckType(pt, prop, prop.Type))
                    {
                        string js = MemberJsName(prop);
                        (prop.IsStatic ? staticProps : instanceProps).Add((js, prop, pt));
                        (prop.IsStatic ? staticNames : names).Add((prop, js));
                    }

                    break;
                case IFieldSymbol field:
                    TypeDesc ft = _cls.Classify(field.Type);
                    if (CheckType(ft, field, field.Type))
                    {
                        string js = MemberJsName(field);
                        if (field.IsConst)
                        {
                            consts.Add((js, field, ft));
                            staticNames.Add((field, js));
                        }
                        else
                        {
                            (field.IsStatic ? staticProps : instanceProps).Add((js, field, ft));
                            (field.IsStatic ? staticNames : names).Add((field, js));
                        }
                    }

                    break;
                case IEventSymbol:
                    Report(Descriptors.UnsupportedMember, m, MemberText(m), "events have no script equivalent", "expose a method that takes a callback (Action<...>) instead");
                    break;
            }
        }

        foreach (List<Overload> g in instanceMethods.Values.Concat(staticMethods.Values).Concat([ctors]))
        {
            if (g.Count > 1)
            {
                CheckAmbiguity(g);
            }
        }

        CheckNames(names);
        CheckNames(staticNames);
        if (_export.JsName == "constructor")
        {
            Report(Descriptors.InvalidName, _type, "constructor", _type.Name);
        }

        if (!IsIdentifier(_export.JsName))
        {
            Report(Descriptors.InvalidName, _type, _export.JsName, _type.Name);
        }

        var b = new StringBuilder();
        string typeLit = Lit(_export.JsName);
        b.Append("        /// <summary>Describes the type for <see cref=\"JsRealm.Install\"/>.</summary>\n");
        b.Append("        public static readonly JsHostType JsType = new(typeof(").Append(_typeCs).Append("), ").Append(typeLit).Append(", Install);\n\n");

        // Method bodies.
        var funcs = new StringBuilder();
        int fn = 0;
        var instMethodFns = new List<(string Js, string Fn, int Len)>();
        var statMethodFns = new List<(string Js, string Fn, int Len)>();
        foreach (var (dict, target, isStaticGroup) in new[] { (instanceMethods, instMethodFns, false), (staticMethods, statMethodFns, true) })
        {
            foreach (KeyValuePair<string, List<Overload>> kv in dict)
            {
                string fname = "M" + (fn++).ToString(CultureInfo.InvariantCulture);
                funcs.Append("        private static JsValue ").Append(fname).Append("(JsRealm realm, JsValue thisValue, global::System.ReadOnlySpan<JsValue> args)\n        {\n");
                if (!isStaticGroup)
                {
                    funcs.Append("            ").Append(_typeCs).Append(" self = JsMarshal.This<").Append(_typeCs).Append(">(thisValue, ").Append(Lit(_typeShort + "." + kv.Value[0].Method.Name)).Append(");\n");
                }

                bool stat = isStaticGroup;
                EmitGroup(funcs, 3, kv.Value, o => args => (stat ? _typeCs : "self") + "." + o.Method.Name + "(" + args + ")", false);
                funcs.Append("        }\n\n");
                target.Add((kv.Key, fname, kv.Value.Min(o => o.Required)));
            }
        }

        // Constructor.
        string? ctorFn = null;
        if (ctors.Count > 0)
        {
            ctorFn = "Construct";
            funcs.Append("        private static JsValue Construct(JsRealm realm, JsObject proto, global::System.ReadOnlySpan<JsValue> args)\n        {\n");
            EmitGroup(funcs, 3, ctors, o => args => "new " + _typeCs + "(" + args + ")", true);
            funcs.Append("        }\n\n");
        }

        // Accessors.
        var propFns = new StringBuilder();
        int pi = 0;
        var instAcc = new List<(string Js, string? Get, string? Set)>();
        var statAcc = new List<(string Js, string? Get, string? Set)>();
        foreach (var (list, target, isStaticProp) in new[] { (instanceProps, instAcc, false), (staticProps, statAcc, true) })
        {
            foreach (var (js, sym, type) in list)
            {
                string id = (pi++).ToString(CultureInfo.InvariantCulture);
                string recv = isStaticProp ? _typeCs : "self";
                string read = isStaticProp ? _typeCs + "." + sym.Name : "self." + sym.Name;
                string? getter = null;
                string? setter = null;
                bool canGet = sym is IFieldSymbol || (sym is IPropertySymbol { GetMethod: not null } gp && gp.GetMethod.DeclaredAccessibility == Accessibility.Public);
                bool canSet = sym is IFieldSymbol { IsReadOnly: false, IsConst: false } || (sym is IPropertySymbol { SetMethod: { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false } });
                if (canGet)
                {
                    getter = "G" + id;
                    propFns.Append("        private static JsValue G").Append(id).Append("(JsRealm realm, JsValue thisValue)\n        {\n");
                    if (!isStaticProp)
                    {
                        propFns.Append("            ").Append(_typeCs).Append(" self = JsMarshal.This<").Append(_typeCs).Append(">(thisValue, ").Append(Lit(_typeShort + "." + sym.Name)).Append(");\n");
                    }

                    propFns.Append("            return ").Append(ToJs(type)).Append("(realm, ").Append(read).Append(");\n        }\n\n");
                }

                if (canSet)
                {
                    setter = "S" + id;
                    propFns.Append("        private static JsValue S").Append(id).Append("(JsRealm realm, JsValue thisValue, global::System.ReadOnlySpan<JsValue> args)\n        {\n");
                    if (!isStaticProp)
                    {
                        propFns.Append("            ").Append(_typeCs).Append(" self = JsMarshal.This<").Append(_typeCs).Append(">(thisValue, ").Append(Lit(_typeShort + "." + sym.Name)).Append(");\n");
                    }

                    propFns.Append("            var c = new JsConv(realm, ").Append(Lit(_typeShort + "." + sym.Name)).Append(", ").Append(Lit(_typeShort + "." + sym.Name + " (setter)")).Append(");\n            c.Arg(").Append(Lit(js)).Append(");\n");
                    propFns.Append("            if (args.Length == 0 || !").Append(Conv(type)).Append("(args[0], ref c, out ").Append(type.Cs).Append(" value))\n            {\n                if (args.Length == 0)\n                {\n                    c.Missing(").Append(Lit(Expect(type))).Append(");\n                }\n\n                throw c.ToException();\n            }\n\n");
                    propFns.Append("            ").Append(recv).Append('.').Append(sym.Name).Append(" = value;\n            return JsValue.Undefined;\n        }\n\n");
                }

                target.Add((js, getter, setter));
            }
        }

        // Install.
        b.Append("        private static JsObject Install(JsRealm realm)\n        {\n");
        if (isStatic)
        {
            b.Append("            JsObject target = realm.NewObject();\n");
            b.Append("            realm.DefineData(realm.Global, ").Append(typeLit).Append(", JsValue.FromObject(target), JsAttributes.Method);\n");
            EmitDefinitions(b, "target", "target", statMethodFns, statAcc, consts, instMethodFns: null);
            b.Append("            return target;\n        }\n\n");
        }
        else
        {
            string? baseProto = null;
            if (_type.BaseType is { } bt && Classifier.GetExport(bt) is { IsValue: false } be && bt.TypeKind == TypeKind.Class)
            {
                baseProto = "realm.Install(" + be.Binding + ".JsType)";
            }

            b.Append("            JsObject? protoRef = null;\n");
            string construct = ctorFn is not null
                ? "(args, newTarget) => " + ctorFn + "(realm, JsRealm.PrototypeFor(newTarget, protoRef!), args)"
                : "(args, newTarget) => throw new global::System.InvalidOperationException(" + Lit(_export.JsName + " cannot be created from script: it has no public constructor") + ")";
            int ctorLen = ctors.Count == 0 ? 0 : ctors.Min(o => o.Required);
            b.Append("            JsObject ctor = realm.NewConstructor(").Append(typeLit).Append(", ").Append(ctorLen.ToString(CultureInfo.InvariantCulture)).Append(", ").Append(construct).Append(", out JsObject proto").Append(baseProto is null ? string.Empty : ", " + baseProto).Append(");\n");
            b.Append("            protoRef = proto;\n");
            b.Append("            realm.DefineData(realm.Global, ").Append(typeLit).Append(", JsValue.FromObject(ctor), JsAttributes.Method);\n");
            EmitDefinitions(b, "proto", "ctor", statMethodFns, statAcc, consts, instMethodFns);
            foreach (var (js, g, s) in instAcc)
            {
                EmitAccessor(b, "proto", js, g, s);
            }

            b.Append("            return proto;\n        }\n\n");
        }

        // The instance-method definitions need their fns; EmitDefinitions handled them above.
        b.Append(funcs).Append(propFns);
        return Wrap(isStatic ? "static class" : isInterface ? "interface" : "class", b.ToString());
    }

    private static void EmitAccessor(StringBuilder b, string target, string js, string? getter, string? setter)
    {
        b.Append("            realm.DefineAccessor(").Append(target).Append(", ").Append(Lit(js)).Append(", ");
        b.Append(getter is null ? "null" : "realm.NewFunction(" + Lit("get " + js) + ", 0, (t, a) => " + getter + "(realm, t))").Append(", ");
        b.Append(setter is null ? "null" : "realm.NewFunction(" + Lit("set " + js) + ", 1, (t, a) => " + setter + "(realm, t, a))");
        b.Append(", JsAttributes.Configurable | JsAttributes.Enumerable);\n");
    }

    private void EmitDefinitions(StringBuilder b, string instanceTarget, string staticTarget, List<(string Js, string Fn, int Len)> statMethods, List<(string Js, string? Get, string? Set)> statAcc, List<(string Js, IFieldSymbol Field, TypeDesc Type)> consts, List<(string Js, string Fn, int Len)>? instMethodFns)
    {
        if (instMethodFns is not null)
        {
            foreach (var (js, fnName, len) in instMethodFns)
            {
                b.Append("            realm.DefineData(").Append(instanceTarget).Append(", ").Append(Lit(js)).Append(", JsValue.FromObject(realm.NewFunction(").Append(Lit(js)).Append(", ").Append(len.ToString(CultureInfo.InvariantCulture)).Append(", (t, a) => ").Append(fnName).Append("(realm, t, a))), JsAttributes.Method);\n");
            }
        }

        foreach (var (js, fnName, len) in statMethods)
        {
            b.Append("            realm.DefineData(").Append(staticTarget).Append(", ").Append(Lit(js)).Append(", JsValue.FromObject(realm.NewFunction(").Append(Lit(js)).Append(", ").Append(len.ToString(CultureInfo.InvariantCulture)).Append(", (t, a) => ").Append(fnName).Append("(realm, t, a))), JsAttributes.Method);\n");
        }

        foreach (var (js, g, s) in statAcc)
        {
            EmitAccessor(b, staticTarget, js, g, s);
        }

        foreach (var (js, field, type) in consts)
        {
            b.Append("            realm.DefineData(").Append(staticTarget).Append(", ").Append(Lit(js)).Append(", ").Append(ToJs(type)).Append("(realm, ").Append(_typeCs).Append('.').Append(field.Name).Append("), JsAttributes.Enumerable);\n");
        }
    }
}
