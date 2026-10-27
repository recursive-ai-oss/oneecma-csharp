using System.Buffers;

namespace RecursiveAI.OneEcma.TextServices;

/// <summary>A growable list of code points backed by pooled arrays. Dispose returns the array.</summary>
internal sealed class CodePointBuffer : IDisposable
{
    private int[] _items;

    /// <summary>Creates an empty buffer.</summary>
    /// <param name="capacity">Initial capacity hint.</param>
    internal CodePointBuffer(int capacity = 64) => _items = ArrayPool<int>.Shared.Rent(Math.Max(capacity, 16));

    /// <summary>Number of code points held.</summary>
    internal int Count { get; set; }

    /// <summary>The code points held.</summary>
    internal Span<int> Span => _items.AsSpan(0, Count);

    /// <summary>Appends one code point.</summary>
    internal void Add(int cp)
    {
        if (Count == _items.Length)
        {
            Grow(Count + 1);
        }

        _items[Count++] = cp;
    }

    /// <summary>Appends several code points.</summary>
    internal void AddRange(ReadOnlySpan<int> cps)
    {
        if (Count + cps.Length > _items.Length)
        {
            Grow(Count + cps.Length);
        }

        cps.CopyTo(_items.AsSpan(Count));
        Count += cps.Length;
    }

    /// <summary>Removes every code point.</summary>
    internal void Clear() => Count = 0;

    private void Grow(int needed)
    {
        int[] bigger = ArrayPool<int>.Shared.Rent(Math.Max(needed, _items.Length * 2));
        Array.Copy(_items, bigger, Count);
        ArrayPool<int>.Shared.Return(_items);
        _items = bigger;
    }

    /// <summary>Returns the array to the pool.</summary>
    public void Dispose()
    {
        int[] items = _items;
        _items = [];
        if (items.Length > 0)
        {
            ArrayPool<int>.Shared.Return(items);
        }
    }
}

/// <summary>UTF-16 code point decoding that tolerates lone surrogates, which pass through as themselves.</summary>
internal static class Utf16
{
    /// <summary>Reads the code point at <paramref name="index"/>; a lone surrogate is returned as its own value.</summary>
    internal static int CodePointAt(ReadOnlySpan<char> text, int index, out int length)
    {
        char c = text[index];
        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            length = 2;
            return char.ConvertToUtf32(c, text[index + 1]);
        }

        length = 1;
        return c;
    }

    /// <summary>Reads the code point that ends just before <paramref name="index"/>.</summary>
    internal static int CodePointBefore(ReadOnlySpan<char> text, int index, out int length)
    {
        char c = text[index - 1];
        if (char.IsLowSurrogate(c) && index >= 2 && char.IsHighSurrogate(text[index - 2]))
        {
            length = 2;
            return char.ConvertToUtf32(text[index - 2], c);
        }

        length = 1;
        return c;
    }

    /// <summary>Number of UTF-16 units a code point takes.</summary>
    internal static int Length(int cp) => cp >= 0x10000 ? 2 : 1;

    /// <summary>Writes a code point; the caller has checked the room.</summary>
    internal static int Write(Span<char> dest, int cp)
    {
        if (cp >= 0x10000)
        {
            int v = cp - 0x10000;
            dest[0] = (char)(0xD800 + (v >> 10));
            dest[1] = (char)(0xDC00 + (v & 0x3FF));
            return 2;
        }

        dest[0] = (char)cp;
        return 1;
    }
}
