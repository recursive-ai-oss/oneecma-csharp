namespace RecursiveAI.OneEcma.Intl.Data;

/// <summary>
/// Cursor over a locale-data blob: the container header shared by every blob kind plus the varint and
/// fixed-width primitives. The layout is specified in docs/locale-data.md.
/// </summary>
internal struct BlobReader
{
    /// <summary>Container version this reader understands; any other blob is rejected.</summary>
    internal const byte SupportedVersion = 1;

    internal const byte KindTree = 1;
    internal const byte KindPlurals = 2;
    internal const byte KindCollation = 3;
    internal const byte KindBreaks = 4;
    internal const byte KindNormalization = 5;

    private readonly byte[] _data;

    internal BlobReader(byte[] data, int position)
    {
        _data = data;
        Position = position;
    }

    /// <summary>Offset of the next byte to read.</summary>
    internal int Position { get; set; }

    /// <summary>True once every byte has been consumed.</summary>
    internal readonly bool AtEnd => Position >= _data.Length;

    /// <summary>Checks the 8-byte container header and returns a reader positioned after it.</summary>
    /// <exception cref="InvalidDataException">The magic, version or kind does not match.</exception>
    internal static BlobReader Open(byte[] data, byte expectedKind)
    {
        if (data.Length < 8 || data[0] != (byte)'O' || data[1] != (byte)'E' || data[2] != (byte)'D' || data[3] != (byte)'T')
        {
            throw new InvalidDataException("Not a locale-data blob.");
        }

        if (data[4] != SupportedVersion)
        {
            throw new InvalidDataException($"Unsupported locale-data format version {data[4]}.");
        }

        if (data[5] != expectedKind)
        {
            throw new InvalidDataException($"Locale-data blob has kind {data[5]}, expected {expectedKind}.");
        }

        return new BlobReader(data, 8);
    }

    /// <summary>Reads an unsigned LEB128 value of at most 32 bits.</summary>
    internal int Varint()
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = _data[Position++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return (int)result;
            }

            shift += 7;
            if (shift > 28)
            {
                throw new InvalidDataException("Varint too long.");
            }
        }
    }

    internal byte Byte() => _data[Position++];

    /// <summary>Reads a varint-length-prefixed UTF-8 string.</summary>
    internal string String()
    {
        int len = Varint();
        string s = System.Text.Encoding.UTF8.GetString(_data, Position, len);
        Position += len;
        return s;
    }
}
