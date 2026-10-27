namespace RecursiveAI.OneEcma.RegExp;

/// <summary>Opcodes of the backtracking matcher. Operand layouts are in docs/regexp.md.</summary>
internal static class Op
{
    internal const int Char = 1;
    internal const int Any = 2;
    internal const int Set = 3;
    internal const int Bol = 4;
    internal const int Eol = 5;
    internal const int WordBoundary = 6;
    internal const int Split = 7;
    internal const int Jmp = 8;
    internal const int Mark = 9;
    internal const int EndGroup = 10;
    internal const int Clear = 11;
    internal const int Backref = 12;
    internal const int LookStart = 13;
    internal const int LookEnd = 14;
    internal const int SetReg = 15;
    internal const int LoopSplit = 16;
    internal const int LoopEnd = 17;
    internal const int EmptyCheck = 18;
    internal const int Repeat = 19;
    internal const int Strings = 20;
    internal const int Match = 21;

    // Flag bits of the 'fl' operand of the character-consuming opcodes.
    internal const int FlagBack = 1;
    internal const int FlagIgnoreCase = 2;
    internal const int FlagDotAll = 4;

    // Kinds of single-character matcher used by Repeat.
    internal const int KindChar = 0;
    internal const int KindAny = 1;
    internal const int KindSet = 2;
}

/// <summary>A compiled character set with the flags that decide how a character is tested against it.</summary>
internal sealed class CharClass
{
    private readonly ulong _lo;
    private readonly ulong _hi;

    internal CharClass(RangeSet set, bool invert, bool ignoreCase)
    {
        Set = set;
        Invert = invert;
        IgnoreCase = ignoreCase;
        for (int c = 0; c < 64; c++)
        {
            if (set.Contains(c))
            {
                _lo |= 1UL << c;
            }

            if (set.Contains(c + 64))
            {
                _hi |= 1UL << c;
            }
        }
    }

    internal RangeSet Set { get; }

    internal bool Invert { get; }

    internal bool IgnoreCase { get; }

    /// <summary>CharacterSetMatcher steps k-n (22.2.2.7.1): canonicalize, look up, apply invert.</summary>
    internal bool Matches(int c, char[]? canon, bool unicode)
    {
        if (IgnoreCase)
        {
            c = c <= 0xFFFF ? canon![c] : (unicode ? RegExpUnicode.Canonicalize(c, true) : c);
        }

        bool found = c < 64 ? ((_lo >> c) & 1) != 0 : c < 128 ? ((_hi >> (c - 64)) & 1) != 0 : Set.Contains(c);
        return found != Invert;
    }
}

/// <summary>
/// A set of multi-character strings (a v-mode class with \q{..} or a property of strings), as a trie over
/// canonicalized code points so the matcher can find the longest string that fits at a position.
/// </summary>
internal sealed class StringTrie
{
    private readonly List<int[]> _keys = [];
    private readonly List<int[]> _next = [];
    private readonly List<bool> _terminal = [];

    private StringTrie()
    {
    }

    internal int NodeCount => _keys.Count;

    internal bool IsTerminal(int node) => _terminal[node];

    /// <summary>The child of <paramref name="node"/> under <paramref name="cp"/>, or -1.</summary>
    internal int Child(int node, int cp)
    {
        int[] k = _keys[node];
        int i = Array.BinarySearch(k, cp);
        return i >= 0 ? _next[node][i] : -1;
    }

    internal static StringTrie Build(string[] strings, bool ignoreCase, bool back)
    {
        var children = new List<SortedDictionary<int, int>> { new() };
        var terminal = new List<bool> { false };
        foreach (string s in strings)
        {
            var cps = new List<int>();
            for (int i = 0; i < s.Length; i++)
            {
                int cp = s[i];
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    cp = char.ConvertToUtf32(s[i], s[i + 1]);
                    i++;
                }

                cps.Add(ignoreCase ? RegExpUnicode.Canonicalize(cp, true) : cp);
            }

            if (back)
            {
                cps.Reverse();
            }

            int node = 0;
            foreach (int cp in cps)
            {
                if (!children[node].TryGetValue(cp, out int next))
                {
                    next = children.Count;
                    children[node][cp] = next;
                    children.Add([]);
                    terminal.Add(false);
                }

                node = next;
            }

            terminal[node] = true;
        }

        var t = new StringTrie();
        for (int n = 0; n < children.Count; n++)
        {
            t._keys.Add([.. children[n].Keys]);
            t._next.Add([.. children[n].Values]);
            t._terminal.Add(terminal[n]);
        }

        return t;
    }
}

/// <summary>The immutable result of compilation: bytecode plus the tables it refers to.</summary>
internal sealed class CompiledProgram
{
    internal CompiledProgram(int[] code, CharClass[] classes, StringTrie[] tries, int[][] backrefs, int regCount, int groupCount, bool unicode, bool usesIgnoreCase)
    {
        Code = code;
        Classes = classes;
        Tries = tries;
        Backrefs = backrefs;
        RegCount = regCount;
        GroupCount = groupCount;
        Unicode = unicode;
        UsesIgnoreCase = usesIgnoreCase;
    }

    internal int[] Code { get; }

    internal CharClass[] Classes { get; }

    internal StringTrie[] Tries { get; }

    internal int[][] Backrefs { get; }

    /// <summary>Registers: two per capture group (including group 0), then group-start temporaries and loop counters.</summary>
    internal int RegCount { get; }

    internal int GroupCount { get; }

    /// <summary>True for u and v patterns: the matcher reads code points.</summary>
    internal bool Unicode { get; }

    internal bool UsesIgnoreCase { get; }

    /// <summary>Reusable buffers, so repeated matching does not allocate its stack every call.</summary>
    internal sealed class Scratch
    {
        internal int[] Regs { get; set; } = [];

        internal int[] Stack { get; set; } = new int[256];
    }
}
