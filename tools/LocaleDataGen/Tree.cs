using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneEcma.LocaleDataGen;

/// <summary>A node of the in-memory data tree: an object, an array or a string leaf.</summary>
internal abstract class Node
{
    /// <summary>Deep structural equality.</summary>
    public static bool Equal(Node a, Node b)
    {
        switch (a)
        {
            case Str sa:
                return b is Str sb && sa.Value == sb.Value;
            case Arr aa:
                if (b is not Arr ab || aa.Items.Count != ab.Items.Count)
                {
                    return false;
                }

                for (int i = 0; i < aa.Items.Count; i++)
                {
                    if (!Equal(aa.Items[i], ab.Items[i]))
                    {
                        return false;
                    }
                }

                return true;
            case Obj oa:
                if (b is not Obj ob || oa.Members.Count != ob.Members.Count)
                {
                    return false;
                }

                foreach (var (k, v) in oa.Members)
                {
                    if (!ob.Members.TryGetValue(k, out Node? other) || !Equal(v, other))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }
}

internal sealed class Str(string value) : Node
{
    public string Value { get; } = value;
}

internal sealed class Arr(List<Node> items) : Node
{
    public List<Node> Items { get; } = items;
}

internal sealed class Obj : Node
{
    public SortedDictionary<string, Node> Members { get; } = new(StringComparer.Ordinal);
}

/// <summary>Imports CLDR JSON into the tree and derives child-over-parent deltas.</summary>
internal static class TreeOps
{
    // Properties that only carry prose for humans or provenance; the engine never reads them.
    private static readonly HashSet<string> DroppedProperties = new(StringComparer.Ordinal)
    {
        "_description", "_since", "_reason", "_iso4217", "_comment", "_references", "_deprecated_since",
    };

    /// <summary>Converts a JSON element; numbers and booleans become their literal text.</summary>
    public static Node FromJson(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new Obj();
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    if (DroppedProperties.Contains(p.Name))
                    {
                        continue;
                    }

                    o.Members[p.Name] = FromJson(p.Value);
                }

                return o;
            case JsonValueKind.Array:
                var items = new List<Node>();
                foreach (JsonElement x in e.EnumerateArray())
                {
                    items.Add(FromJson(x));
                }

                return new Arr(items);
            case JsonValueKind.String:
                return new Str(e.GetString()!);
            default:
                return new Str(e.GetRawText());
        }
    }

    public static JsonElement Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.Clone();
    }

    /// <summary>Follows <paramref name="path"/> through objects; returns null when a step is missing.</summary>
    public static Node? Dig(Node root, params string[] path)
    {
        Node? n = root;
        foreach (string step in path)
        {
            if (n is not Obj o || !o.Members.TryGetValue(step, out n))
            {
                return null;
            }
        }

        return n;
    }

    /// <summary>
    /// What <paramref name="child"/> says that <paramref name="parent"/> does not: members that are new or
    /// different, recursively. Null when the child adds nothing. Arrays and strings are replaced whole.
    /// </summary>
    public static Node? Delta(Node child, Node? parent)
    {
        if (parent is null)
        {
            return child;
        }

        if (child is Obj c && parent is Obj p)
        {
            var result = new Obj();
            foreach (var (k, v) in c.Members)
            {
                p.Members.TryGetValue(k, out Node? pv);
                Node? d = Delta(v, pv);
                if (d is not null)
                {
                    result.Members[k] = d;
                }
            }

            return result.Members.Count == 0 ? null : result;
        }

        return Node.Equal(child, parent) ? null : child;
    }

    /// <summary>Removes members for numbering systems other than Latin (only Latin digits ship).</summary>
    public static void DropNonLatinNumberSystems(Node n)
    {
        if (n is not Obj o)
        {
            return;
        }

        foreach (string key in o.Members.Keys.ToList())
        {
            int i = key.IndexOf("-numberSystem-", StringComparison.Ordinal);
            if (i >= 0 && key[(i + 14)..] != "latn")
            {
                o.Members.Remove(key);
            }
            else
            {
                DropNonLatinNumberSystems(o.Members[key]);
            }
        }
    }
}

/// <summary>Little-endian primitives shared by every blob writer.</summary>
internal static class Bin
{
    public static void Varint(List<byte> buf, uint v)
    {
        while (v >= 0x80)
        {
            buf.Add((byte)(v | 0x80));
            v >>= 7;
        }

        buf.Add((byte)v);
    }

    public static void U32(List<byte> buf, uint v)
    {
        buf.Add((byte)v);
        buf.Add((byte)(v >> 8));
        buf.Add((byte)(v >> 16));
        buf.Add((byte)(v >> 24));
    }

    public static void Utf8(List<byte> buf, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        Varint(buf, (uint)b.Length);
        buf.AddRange(b);
    }

    public static void Magic(List<byte> buf, byte kind)
    {
        buf.AddRange("OEDT"u8.ToArray());
        buf.Add(Format.Version);
        buf.Add(kind);
        buf.Add(0);
        buf.Add(0);
    }
}

internal static class Format
{
    /// <summary>Version of the blob container; the reader rejects any other.</summary>
    public const byte Version = 1;

    public const byte KindTree = 1;
    public const byte KindPlurals = 2;
    public const byte KindCollation = 3;
    public const byte KindBreaks = 4;
}

/// <summary>Serializes a tree into the string-table-plus-nodes layout described in docs/locale-data.md.</summary>
internal static class TreeWriter
{
    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x!.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var h = new HashCode();
            h.AddBytes(obj);
            return h.ToHashCode();
        }
    }

    public static byte[] Write(Obj root)
    {
        // String table: every distinct key and value, sorted by UTF-8 bytes so the reader can
        // binary-search it and so the output does not depend on insertion order.
        var strings = new HashSet<string>(StringComparer.Ordinal);
        Collect(root, strings);
        var sorted = strings.Select(s => (Text: s, Bytes: Encoding.UTF8.GetBytes(s))).ToList();
        sorted.Sort((a, b) => a.Bytes.AsSpan().SequenceCompareTo(b.Bytes));
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < sorted.Count; i++)
        {
            ids[sorted[i].Text] = i;
        }

        // Nodes are written children-first; identical subtrees are written once and shared.
        var nodes = new List<byte>();
        var memo = new Dictionary<byte[], int>(new ByteArrayComparer());
        int rootOffset = WriteNode(root, ids, nodes, memo);

        var data = new List<byte>();
        var offsets = new List<byte>();
        uint pos = 0;
        foreach (var (_, bytes) in sorted)
        {
            Bin.U32(offsets, pos);
            data.AddRange(bytes);
            pos += (uint)bytes.Length;
        }

        Bin.U32(offsets, pos);

        const int headerSize = 4 + 4 + 5 * 4;
        uint tableOff = headerSize;
        uint dataOff = tableOff + (uint)offsets.Count;
        uint nodesOff = dataOff + (uint)data.Count;
        var file = new List<byte>();
        Bin.Magic(file, Format.KindTree);
        Bin.U32(file, (uint)sorted.Count);
        Bin.U32(file, tableOff);
        Bin.U32(file, dataOff);
        Bin.U32(file, nodesOff);
        Bin.U32(file, (uint)rootOffset);
        file.AddRange(offsets);
        file.AddRange(data);
        file.AddRange(nodes);
        return file.ToArray();
    }

    private static void Collect(Node n, HashSet<string> into)
    {
        switch (n)
        {
            case Str s:
                into.Add(s.Value);
                break;
            case Arr a:
                foreach (Node x in a.Items)
                {
                    Collect(x, into);
                }

                break;
            case Obj o:
                foreach (var (k, v) in o.Members)
                {
                    into.Add(k);
                    Collect(v, into);
                }

                break;
            default:
                break;
        }
    }

    // Header varint: (count << 2) | tag; tag 0 object, 1 array, 2 string (count is the string id).
    // Objects then list count key ids followed by count absolute child offsets; arrays only offsets.
    private static int WriteNode(Node n, Dictionary<string, int> ids, List<byte> nodes, Dictionary<byte[], int> memo)
    {
        var buf = new List<byte>();
        switch (n)
        {
            case Str s:
                Bin.Varint(buf, ((uint)ids[s.Value] << 2) | 2);
                break;
            case Arr a:
                var aoffs = a.Items.Select(x => WriteNode(x, ids, nodes, memo)).ToList();
                Bin.Varint(buf, ((uint)a.Items.Count << 2) | 1);
                foreach (int off in aoffs)
                {
                    Bin.Varint(buf, (uint)off);
                }

                break;
            case Obj o:
                var members = o.Members.Select(kv => (Key: ids[kv.Key], Value: kv.Value)).OrderBy(m => m.Key).ToList();
                var ooffs = members.Select(m => WriteNode(m.Value, ids, nodes, memo)).ToList();
                Bin.Varint(buf, ((uint)members.Count << 2) | 0);
                foreach (var m in members)
                {
                    Bin.Varint(buf, (uint)m.Key);
                }

                foreach (int off in ooffs)
                {
                    Bin.Varint(buf, (uint)off);
                }

                break;
            default:
                throw new InvalidOperationException();
        }

        byte[] key = buf.ToArray();
        if (memo.TryGetValue(key, out int existing))
        {
            return existing;
        }

        int at = nodes.Count;
        nodes.AddRange(key);
        memo[key] = at;
        return at;
    }
}
