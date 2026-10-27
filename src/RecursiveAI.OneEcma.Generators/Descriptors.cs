using Microsoft.CodeAnalysis;

namespace RecursiveAI.OneEcma.Generators;

/// <summary>The build-time diagnostics of the host-binding generator. Ids are stable; each message says what to change.</summary>
internal static class Descriptors
{
    private const string Category = "RecursiveAI.OneEcma.HostBinding";

    internal static readonly DiagnosticDescriptor UnsupportedDeclaration = new(
        "OE1001", "Declaration cannot be exported",
        "'{0}' cannot be exported to script: {1}. Fix: {2}.",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor UnsupportedMember = new(
        "OE1002", "Member shape cannot be exported",
        "Member '{0}' cannot be exported to script: {1}. Fix: add [JsIgnore] to the member, or {2}.",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor UnsupportedType = new(
        "OE1003", "Type cannot cross the boundary",
        "'{0}' uses type '{1}', which cannot cross the boundary ({2}). Fix: {3}, or add [JsIgnore] to the member.",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor AmbiguousOverloads = new(
        "OE1004", "Overloads cannot be told apart from script",
        "Overloads '{0}' and '{1}' of '{2}' accept the same kinds of script values for {3} argument(s), so a call cannot pick one. Fix: give one a different script name with [JsExport(Name = \"...\")], make their parameter kinds differ, or add [JsIgnore] to one.",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor NameCollision = new(
        "OE1005", "Two members have the same script name",
        "'{0}' and '{1}' both become '{2}' in script on '{3}'. Fix: rename one with [JsExport(Name = \"...\")] or add [JsIgnore] to one.",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor CannotBuildValueType = new(
        "OE1006", "By-value type cannot be built from script",
        "'{0}' is exported by value but script objects cannot be turned into it: {1}. Fix: add a public parameterless constructor, or a public constructor whose parameters are named like the type's properties, or export it by reference with [JsExport(Kind = JsExportKind.Reference)].",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor InvalidName = new(
        "OE1007", "Script name is not an identifier",
        "'{0}' is not a valid script name for '{1}'. Fix: use letters, digits, '_' or '$', not starting with a digit.",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor ValueKindConflict = new(
        "OE1008", "Export kind does not fit the type",
        "'{0}' cannot use JsExportKind.{1}: {2}. Fix: {3}.",
        Category, DiagnosticSeverity.Error, true);
}
