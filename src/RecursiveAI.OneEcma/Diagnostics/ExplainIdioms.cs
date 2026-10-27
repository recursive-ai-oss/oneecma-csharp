namespace RecursiveAI.OneEcma.Diagnostics;

// Help for method names that other languages and libraries have but JavaScript's built-ins do not. A near-miss by spelling
// (`sum` for `some`) is never what the author meant, so these are looked up before any edit-distance suggestion. The table is
// small on purpose; it is documented in docs/diagnostic-report.md.
internal static partial class Explain
{
    /// <summary>
    /// The JavaScript way to say what <paramref name="member"/> means on this receiver, or null when the table has no entry.
    /// <paramref name="found"/> is true when the receiver does have a property of that name (it is just not a function).
    /// </summary>
    private static string? IdiomHelp(JsValue receiver, string ownerText, string member, bool found)
    {
        string o = ownerText;
        if (found)
        {
            return member == "length" && (receiver.Ref is JsArray || receiver.IsString)
                ? "`length` is a property, not a method; write `" + o + ".length`"
                : member == "size" && receiver.Ref is JsMap or JsSet
                    ? "`size` is a property, not a method; write `" + o + ".size`"
                    : null;
        }

        if (receiver.Ref is JsArray)
        {
            return member switch
            {
                "sum" or "total" => "arrays have no `" + member + "`; add the items up with `" + o + ".reduce((a, b) => a + b, 0)`",
                "max" => "arrays have no `max`; use `Math.max(..." + o + ")`",
                "min" => "arrays have no `min`; use `Math.min(..." + o + ")`",
                "average" or "avg" or "mean" => "arrays have no `" + member + "`; use `" + o + ".reduce((a, b) => a + b, 0) / " + o + ".length`",
                "contains" or "has" => "arrays have no `" + member + "`; use `" + o + ".includes(value)`",
                "size" or "len" => "arrays have no `" + member + "`; the number of items is the property `" + o + ".length`",
                "count" => "arrays have no `count`; use the property `" + o + ".length`, or `" + o + ".filter(item => ...).length` to count matches",
                "first" => "arrays have no `first`; use `" + o + "[0]`",
                "last" => "arrays have no `last`; use `" + o + ".at(-1)`",
                "isEmpty" or "empty" => "arrays have no `" + member + "`; use `" + o + ".length === 0`",
                "append" or "add" => "arrays have no `" + member + "`; use `" + o + ".push(value)`",
                "remove" or "delete" => "arrays have no `" + member + "`; use `" + o + ".splice(index, 1)`, or `" + o + ".filter(item => ...)` to make a copy without it",
                _ => null,
            };
        }

        if (receiver.IsString)
        {
            return member switch
            {
                "contains" => "strings have no `contains`; use `" + o + ".includes(text)`",
                "len" or "size" => "strings have no `" + member + "`; the number of characters is the property `" + o + ".length`",
                "strip" => "strings have no `strip`; use `" + o + ".trim()`",
                "lstrip" => "strings have no `lstrip`; use `" + o + ".trimStart()`",
                "rstrip" => "strings have no `rstrip`; use `" + o + ".trimEnd()`",
                "toUpper" or "upper" => "strings have no `" + member + "`; use `" + o + ".toUpperCase()`",
                "toLower" or "lower" => "strings have no `" + member + "`; use `" + o + ".toLowerCase()`",
                "format" => "strings have no `format`; build the text with a template literal: `` `...${value}...` ``",
                _ => null,
            };
        }

        if (receiver.Ref is JsMap or JsSet)
        {
            string kind = receiver.Ref is JsMap ? "Map" : "Set";
            return member switch
            {
                "map" or "filter" or "reduce" or "some" or "every" or "find" =>
                    "a " + kind + " has no `" + member + "`; spread it into an array first: `[..." + o + "]." + member + "(...)`",
                "contains" => "a " + kind + " has no `contains`; use `" + o + ".has(" + (kind == "Map" ? "key" : "value") + ")`",
                "length" or "count" or "len" => "a " + kind + " has no `" + member + "`; the number of entries is the property `" + o + ".size`",
                _ => null,
            };
        }

        if (receiver.Ref is JsObject && receiver.Ref is not JsFunction && ConstructorNameOf((JsObject)receiver.Ref) == "Object")
        {
            return member switch
            {
                "keys" or "values" or "entries" => "`" + member + "` is a static function, not a method of objects; call `Object." + member + "(" + o + ")`",
                "items" => "plain objects have no `items`; use `Object.entries(" + o + ")` for [key, value] pairs",
                "hasKey" or "has" or "contains" => "plain objects have no `" + member + "`; use `key in " + o + "` or `Object.hasOwn(" + o + ", key)`",
                _ => null,
            };
        }

        return null;
    }
}
