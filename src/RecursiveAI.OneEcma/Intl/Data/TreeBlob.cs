using System.Text;

namespace RecursiveAI.OneEcma.Intl.Data;

/// <summary>The kind of a value in a <see cref="TreeBlob"/>.</summary>
internal enum TreeKind
{
    /// <summary>No value at that path.</summary>
    None,

    /// <summary>A map from string keys to values.</summary>
    Object,

    /// <summary>An ordered list of values.</summary>
    Array,

    /// <summary>A string leaf. CLDR numbers and booleans are stored as their text.</summary>
    String,
}

/// <summary>
/// One string-keyed tree of CLDR data, read in place from the embedded bytes. Nothing is decoded until
/// asked for; lookups binary-search the sorted string table and then scan the (short) key list of the
/// object. Instances are immutable and safe to share between threads.
/// </summary>
internal sealed class TreeBlob
{
    private readonly byte[] _data;
    private readonly int _stringCount;
    private readonly int _tableOffset;
    private readonly int _dataOffset;
    private readonly int _nodesOffset;

    private TreeBlob(byte[] data)
    {
        _data = data;
        BlobReader.Open(data, BlobReader.KindTree);
        _stringCount = (int)ReadU32(8);
        _tableOffset = (int)ReadU32(12);
        _dataOffset = (int)ReadU32(16);
        _nodesOffset = (int)ReadU32(20);
        Root = (int)ReadU32(24);
    }

    /// <summary>Handle of the root object. Handles are opaque offsets valid for this blob only.</summary>
    internal int Root { get; }

    /// <summary>Opens a blob from its bytes.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a tree blob of a supported version.</exception>
    internal static TreeBlob Open(byte[] data) => new(data);

    /// <summary>The kind of the value behind <paramref name="node"/>.</summary>
    internal TreeKind KindOf(int node)
    {
        int h = PeekHeader(node);
        return (h & 3) switch
        {
            0 => TreeKind.Object,
            1 => TreeKind.Array,
            _ => TreeKind.String,
        };
    }

    /// <summary>Number of members of an object or items of an array; 0 for strings.</summary>
    internal int CountOf(int node)
    {
        int h = PeekHeader(node);
        return (h & 3) == 2 ? 0 : h >>> 2;
    }

    /// <summary>The text of a string node, or null when <paramref name="node"/> is not a string.</summary>
    internal string? StringOf(int node)
    {
        int h = PeekHeader(node);
        return (h & 3) == 2 ? GetString(h >>> 2) : null;
    }

    /// <summary>
    /// Finds a member of an object (by key) or an item of an array (by decimal index).
    /// </summary>
    /// <returns>True and the child handle when present.</returns>
    internal bool TryGetChild(int node, ReadOnlySpan<char> key, out int child)
    {
        child = 0;
        var r = new BlobReader(_data, _nodesOffset + node);
        int h = r.Varint();
        int tag = h & 3;
        int count = h >>> 2;
        int index = -1;
        if (tag == 0)
        {
            int id = FindString(key);
            if (id < 0)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if (r.Varint() == id)
                {
                    index = i;
                }
            }
        }
        else if (tag == 1)
        {
            if (!TryParseIndex(key, out index) || index >= count)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (index < 0)
        {
            return false;
        }

        for (int i = 0; i < index; i++)
        {
            r.Varint();
        }

        child = r.Varint();
        return true;
    }

    /// <summary>Appends the keys of an object, in string-table (UTF-8 byte) order, to <paramref name="into"/>.</summary>
    internal void AddKeys(int node, ICollection<string> into)
    {
        var r = new BlobReader(_data, _nodesOffset + node);
        int h = r.Varint();
        if ((h & 3) != 0)
        {
            return;
        }

        int count = h >>> 2;
        for (int i = 0; i < count; i++)
        {
            into.Add(GetString(r.Varint()));
        }
    }

    /// <summary>Handles of the items of an array, in order; empty for other kinds.</summary>
    internal int[] ArrayItems(int node)
    {
        var r = new BlobReader(_data, _nodesOffset + node);
        int h = r.Varint();
        if ((h & 3) != 1)
        {
            return [];
        }

        var items = new int[h >>> 2];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = r.Varint();
        }

        return items;
    }

    // Array items are addressed by plain decimal digits; parsing by hand keeps number parsing culture-free.
    private static bool TryParseIndex(ReadOnlySpan<char> key, out int index)
    {
        index = 0;
        if (key.Length is 0 or > 9)
        {
            return false;
        }

        foreach (char c in key)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            index = (index * 10) + (c - '0');
        }

        return true;
    }

    private int PeekHeader(int node)
    {
        var r = new BlobReader(_data, _nodesOffset + node);
        return r.Varint();
    }

    private uint ReadU32(int at) =>
        _data[at] | ((uint)_data[at + 1] << 8) | ((uint)_data[at + 2] << 16) | ((uint)_data[at + 3] << 24);

    private string GetString(int id)
    {
        int start = (int)ReadU32(_tableOffset + (id * 4));
        int end = (int)ReadU32(_tableOffset + ((id + 1) * 4));
        return Encoding.UTF8.GetString(_data, _dataOffset + start, end - start);
    }

    private ReadOnlySpan<byte> GetStringBytes(int id)
    {
        int start = (int)ReadU32(_tableOffset + (id * 4));
        int end = (int)ReadU32(_tableOffset + ((id + 1) * 4));
        return new ReadOnlySpan<byte>(_data, _dataOffset + start, end - start);
    }

    private int FindString(ReadOnlySpan<char> key)
    {
        Span<byte> buffer = key.Length <= 64 ? stackalloc byte[key.Length * 3] : new byte[Encoding.UTF8.GetMaxByteCount(key.Length)];
        int n = Encoding.UTF8.GetBytes(key, buffer);
        ReadOnlySpan<byte> needle = buffer[..n];
        int lo = 0;
        int hi = _stringCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = GetStringBytes(mid).SequenceCompareTo(needle);
            if (c == 0)
            {
                return mid;
            }

            if (c < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }
}
