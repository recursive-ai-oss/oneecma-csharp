namespace RecursiveAI.OneEcma;

/// <summary>How an exported type crosses the boundary.</summary>
public enum JsExportKind
{
    /// <summary>Records and structs are copied by value (<see cref="Value"/>); every other type is exposed by reference (<see cref="Reference"/>).</summary>
    Auto = 0,

    /// <summary>Script holds a handle to the .NET object and sees its exported methods, properties and fields. Interfaces and classes only.</summary>
    Reference = 1,

    /// <summary>The type is copied: from script as a plain object read property by property, to script as a new plain object. Only data members take part.</summary>
    Value = 2,
}

/// <summary>
/// Exports a .NET type, or renames one of its members, to script. The Roslyn generator that ships with this library writes the
/// binding at build time, so there is no reflection at run time. On a type it exports every public member (use <see cref="JsIgnoreAttribute"/>
/// to leave one out); on a member it only renames. See <c>docs/js-export.md</c> for the supported shapes.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum
    | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
public sealed class JsExportAttribute : Attribute
{
    /// <summary>The name in script. Default: the type name as written; members use camelCase of the .NET name (<c>GetOrder</c> becomes <c>getOrder</c>).</summary>
    public string? Name { get; set; }

    /// <summary>How the type crosses the boundary. Default <see cref="JsExportKind.Auto"/>. Ignored on members.</summary>
    public JsExportKind Kind { get; set; }

    /// <summary>
    /// For a by-value type: a script object with a property the type does not have is an error that suggests the nearest name,
    /// instead of the property being ignored. Default false.
    /// </summary>
    public bool RejectUnknownProperties { get; set; }
}

/// <summary>Leaves a public member out of an exported type.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Constructor | AttributeTargets.Event, AllowMultiple = false, Inherited = false)]
public sealed class JsIgnoreAttribute : Attribute
{
}
