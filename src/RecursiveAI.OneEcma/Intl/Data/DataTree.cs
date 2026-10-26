namespace RecursiveAI.OneEcma.Intl.Data;

/// <summary>
/// A read-only view of CLDR data made of a chain of <see cref="TreeBlob"/>s, most specific first
/// (for example en-GB, en-001, en, und). A blob stores only what differs from its parent, so a lookup
/// walks the chain until some blob has the value (UTS #35, Locale Inheritance). Paths are sequences of
/// keys, never dotted strings, because CLDR keys such as time zone ids contain '/' and '.'.
/// Array items are addressed by their decimal index.
/// </summary>
/// <remarks>
/// A value of <c>∅∅∅</c> is CLDR's explicit "nothing here" marker; it is returned as is and callers decide
/// (number symbols and some currency symbols use it to say a parent's value is removed).
/// Instances are immutable and thread-safe.
/// </remarks>
internal sealed class DataTree
{
    /// <summary>CLDR's marker for an intentionally empty value.</summary>
    internal const string EmptyMarker = "\u2205\u2205\u2205";

    private readonly TreeBlob[] _chain;

    internal DataTree(string id, TreeBlob[] chain)
    {
        Id = id;
        _chain = chain;
    }

    /// <summary>The stored locale id this view was built for (for example <c>en-GB</c>), or <c>supplemental</c>.</summary>
    internal string Id { get; }

    /// <summary>The string at <paramref name="path"/>, or null when absent or not a string.</summary>
    /// <param name="path">Keys from the root, for example <c>"numbers", "symbols-numberSystem-latn", "decimal"</c>.</param>
    internal string? GetString(params ReadOnlySpan<string> path)
    {
        foreach (TreeBlob blob in _chain)
        {
            if (TryWalk(blob, path, out int node))
            {
                string? s = blob.StringOf(node);
                if (s is not null)
                {
                    return s;
                }
            }
        }

        return null;
    }

    /// <summary>What kind of value sits at <paramref name="path"/> in the most specific blob that has one.</summary>
    internal TreeKind KindOf(params ReadOnlySpan<string> path)
    {
        foreach (TreeBlob blob in _chain)
        {
            if (TryWalk(blob, path, out int node))
            {
                return blob.KindOf(node);
            }
        }

        return TreeKind.None;
    }

    /// <summary>
    /// The distinct keys of the object at <paramref name="path"/>, merged across the chain and sorted by
    /// ordinal UTF-16 order. Empty when the path is not an object.
    /// </summary>
    internal string[] GetKeys(params ReadOnlySpan<string> path)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (TreeBlob blob in _chain)
        {
            if (TryWalk(blob, path, out int node))
            {
                blob.AddKeys(node, keys);
            }
        }

        return [.. keys];
    }

    /// <summary>
    /// The string items of the array at <paramref name="path"/> (from the most specific blob that has an
    /// array there); null when absent. Non-string items are returned as empty strings.
    /// </summary>
    internal string[]? GetStringArray(params ReadOnlySpan<string> path)
    {
        foreach (TreeBlob blob in _chain)
        {
            if (TryWalk(blob, path, out int node) && blob.KindOf(node) == TreeKind.Array)
            {
                int[] items = blob.ArrayItems(node);
                var result = new string[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    result[i] = blob.StringOf(items[i]) ?? "";
                }

                return result;
            }
        }

        return null;
    }

    /// <summary>Number of items of the array at <paramref name="path"/>, or -1 when there is no array.</summary>
    internal int ArrayLength(params ReadOnlySpan<string> path)
    {
        foreach (TreeBlob blob in _chain)
        {
            if (TryWalk(blob, path, out int node) && blob.KindOf(node) == TreeKind.Array)
            {
                return blob.CountOf(node);
            }
        }

        return -1;
    }

    private static bool TryWalk(TreeBlob blob, ReadOnlySpan<string> path, out int node)
    {
        node = blob.Root;
        foreach (string step in path)
        {
            if (!blob.TryGetChild(node, step, out node))
            {
                return false;
            }
        }

        return true;
    }
}
