using System.Text;

namespace RecursiveAI.OneEcma.RegExp;

/// <summary>The outcome of parsing: a tree, or the single error that stopped the parse.</summary>
internal sealed class ParseResult
{
    internal Node? Root { get; init; }

    internal int GroupCount { get; init; }

    /// <summary>Named groups in order of first appearance, each with the numbers of every group of that name.</summary>
    internal List<(string Name, List<int> Groups)> Names { get; init; } = [];

    internal bool HasError { get; init; }

    internal int ErrorStart { get; init; }

    internal int ErrorLength { get; init; }

    internal string ErrorReason { get; init; } = "";
}

/// <summary>
/// Parser for the pattern grammar of ECMA-262 22.2.1 and, for patterns without the u and v flags, the
/// legacy grammar of Annex B.1.2. It stops at the first error, as engines do, and reports an exact span.
/// </summary>
/// <remarks>
/// Nesting is bounded by <see cref="MaxDepth"/> so a hostile pattern cannot exhaust the host stack: groups,
/// lookarounds and nested v-mode classes all count.
/// </remarks>
internal sealed class RegExpParser
{
    /// <summary>Deepest nesting of groups and classes the parser accepts.</summary>
    internal const int MaxDepth = 200;

    private readonly string _s;
    private readonly bool _u;
    private readonly bool _v;
    private readonly bool _namedMode;
    private readonly int _totalGroups;
    private readonly List<(string Name, int Index, int[] Path, int Start, int Length)> _named = [];
    private readonly List<(BackrefNode Node, int Start, int Length)> _namedRefs = [];
    private readonly List<int> _path = [];
    private int _i;
    private int _groupCount;
    private int _depth;
    private int _disjunctions;
    private int _lastAlternative;

    private RegExpParser(string pattern, bool unicode, bool unicodeSets)
    {
        _s = pattern;
        _u = unicode || unicodeSets;
        _v = unicodeSets;
        PreScan(out _totalGroups, out bool hasNamed);
        _namedMode = _u || hasNamed;
    }

    private int Cur => _i < _s.Length ? _s[_i] : -1;

    /// <summary>Parses <paramref name="pattern"/> as a Pattern with the given mode flags.</summary>
    internal static ParseResult Parse(string pattern, bool unicode, bool unicodeSets)
    {
        var p = new RegExpParser(pattern, unicode, unicodeSets);
        try
        {
            Node root = p.ParseDisjunction();
            if (p._i < p._s.Length)
            {
                // Only an unbalanced ')' can stop a top-level disjunction early.
                throw p.Err(p._i, 1, "Unmatched ')'");
            }

            p.ResolveNamedReferences();
            var names = new List<(string, List<int>)>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (name, index, _, _, _) in p._named.OrderBy(n => n.Index))
            {
                if (seen.TryGetValue(name, out int at))
                {
                    names[at].Item2.Add(index);
                }
                else
                {
                    seen[name] = names.Count;
                    names.Add((name, [index]));
                }
            }

            return new ParseResult { Root = root, GroupCount = p._groupCount, Names = names };
        }
        catch (PatternException e)
        {
            return new ParseResult { HasError = true, ErrorStart = e.Start, ErrorLength = e.Length, ErrorReason = e.Reason };
        }
    }

    private PatternException Err(int start, int length, string reason) => new(start, Math.Max(1, Math.Min(length, Math.Max(1, _s.Length - start))), reason);

    // Counts capture groups and notices named ones before the real parse, because \N and \k depend on the
    // total in the whole pattern (Annex B.1.2: a decimal escape is a backreference only if it is <= that count).
    private void PreScan(out int groups, out bool hasNamed)
    {
        groups = 0;
        hasNamed = false;
        int classDepth = 0;
        for (int i = 0; i < _s.Length; i++)
        {
            char c = _s[i];
            if (c == '\\')
            {
                i++;
            }
            else if (classDepth > 0)
            {
                if (c == ']')
                {
                    classDepth--;
                }
                else if (c == '[' && _v)
                {
                    classDepth++;
                }
            }
            else if (c == '[')
            {
                classDepth = 1;
            }
            else if (c == '(')
            {
                if (i + 1 < _s.Length && _s[i + 1] == '?')
                {
                    if (i + 2 < _s.Length && _s[i + 2] == '<' && i + 3 < _s.Length && _s[i + 3] != '=' && _s[i + 3] != '!')
                    {
                        groups++;
                        hasNamed = true;
                    }
                }
                else
                {
                    groups++;
                }
            }
        }
    }

    // ---- disjunction, alternative, term ----------------------------------------------------------------

    private Node ParseDisjunction()
    {
        int id = ++_disjunctions;
        int altIndex = 0;
        _path.Add(id);
        _path.Add(0);
        List<Node>? alts = null;
        Node current = ParseAlternative();
        while (Cur == '|')
        {
            _i++;
            (alts ??= []).Add(current);
            altIndex++;
            _path[^1] = altIndex;
            current = ParseAlternative();
        }

        _path.RemoveRange(_path.Count - 2, 2);
        _lastAlternative = altIndex;
        if (alts is null)
        {
            return current;
        }

        alts.Add(current);
        return new AltNode(alts);
    }

    private Node ParseAlternative()
    {
        List<Node>? items = null;
        Node? only = null;
        while (_i < _s.Length && _s[_i] != '|' && _s[_i] != ')')
        {
            Node t = ParseTerm();
            if (only is null && items is null)
            {
                only = t;
            }
            else
            {
                if (items is null)
                {
                    items = [only!];
                    only = null;
                }

                items.Add(t);
            }
        }

        if (items is not null)
        {
            return new SeqNode(items);
        }

        return only ?? new EmptyNode();
    }

    private Node ParseTerm()
    {
        int start = _i;
        int c = _s[_i];
        int groupsBefore = _groupCount;
        Node atom;
        switch (c)
        {
            case '^':
                _i++;
                return new AssertNode(AssertKind.LineStart);
            case '$':
                _i++;
                return new AssertNode(AssertKind.LineEnd);
            case '(':
                return ParseGroupTerm(start, groupsBefore);
            case '.':
                _i++;
                atom = new AnyNode();
                break;
            case '[':
                atom = ParseClass();
                break;
            case '\\':
                atom = ParseAtomEscape(out bool quantifiable);
                if (!quantifiable)
                {
                    return atom;
                }

                break;
            case '*':
            case '+':
            case '?':
                throw Err(_i, 1, "Nothing to repeat");
            case '{':
                if (LooksLikeBracedQuantifier(_i))
                {
                    // ExtendedAtom :: InvalidBracedQuantifier (Annex B.1.2.1) and the u-mode grammar agree here.
                    throw Err(_i, 1, "Nothing to repeat");
                }

                if (_u)
                {
                    throw Err(_i, 1, "Lone quantifier brackets");
                }

                _i++;
                atom = new CharNode('{');
                break;
            case '}':
            case ']':
                if (_u)
                {
                    throw Err(_i, 1, "Lone quantifier brackets");
                }

                _i++;
                atom = new CharNode(c);
                break;
            default:
                atom = new CharNode(ReadSourceChar());
                break;
        }

        return ParseQuantifier(atom, groupsBefore);
    }

    private int ReadSourceChar()
    {
        char c = _s[_i++];
        if (_u && char.IsHighSurrogate(c) && _i < _s.Length && char.IsLowSurrogate(_s[_i]))
        {
            return char.ConvertToUtf32(c, _s[_i++]);
        }

        return c;
    }

    // A lookbehind (and, with u or v, a lookahead) cannot be quantified; V8 reads what follows as a quantifier first.
    private void CheckNoQuantifierAfterAssertion()
    {
        if (IsQuantifierStart())
        {
            throw Err(_i, 1, "Invalid quantifier");
        }

        if (_u && Cur == '{')
        {
            throw Err(_i, 1, "Incomplete quantifier");
        }
    }

    private bool IsQuantifierStart() => Cur is '*' or '+' or '?' || (Cur == '{' && LooksLikeBracedQuantifier(_i));

    private bool LooksLikeBracedQuantifier(int at)
    {
        int i = at + 1;
        int digits = 0;
        while (i < _s.Length && _s[i] is >= '0' and <= '9')
        {
            i++;
            digits++;
        }

        if (digits == 0)
        {
            return false;
        }

        if (i < _s.Length && _s[i] == ',')
        {
            i++;
            while (i < _s.Length && _s[i] is >= '0' and <= '9')
            {
                i++;
            }
        }

        return i < _s.Length && _s[i] == '}';
    }

    private Node ParseQuantifier(Node atom, int groupsBefore)
    {
        int qs = _i;
        int min;
        int max;
        switch (Cur)
        {
            case '*':
                min = 0;
                max = int.MaxValue;
                _i++;
                break;
            case '+':
                min = 1;
                max = int.MaxValue;
                _i++;
                break;
            case '?':
                min = 0;
                max = 1;
                _i++;
                break;
            case '{':
                if (!LooksLikeBracedQuantifier(_i))
                {
                    if (_u)
                    {
                        throw Err(_i, 1, "Incomplete quantifier");
                    }

                    return atom;
                }

                _i++;
                min = ReadSaturatedInt();
                max = min;
                if (Cur == ',')
                {
                    _i++;
                    max = Cur == '}' ? int.MaxValue : ReadSaturatedInt();
                }

                _i++; // '}'
                break;
            default:
                return atom;
        }

        bool greedy = true;
        if (Cur == '?')
        {
            _i++;
            greedy = false;
        }

        if (min > max)
        {
            throw Err(qs, _i - qs, "numbers out of order in {} quantifier");
        }

        return new QuantNode(atom, min, max, greedy, groupsBefore + 1, _groupCount - groupsBefore);
    }

    // Digits of a quantifier bound; values beyond int range saturate (the matcher treats them as unbounded counts).
    private int ReadSaturatedInt()
    {
        long v = 0;
        while (Cur is >= '0' and <= '9')
        {
            v = Math.Min(int.MaxValue - 1L, (v * 10) + (_s[_i] - '0'));
            _i++;
        }

        return (int)v;
    }

    // ---- groups ------------------------------------------------------------------------------------------

    private Node ParseGroupTerm(int open, int groupsBefore)
    {
        _i++; // '('
        if (Cur != '?')
        {
            int index = ++_groupCount;
            Node body = ParseGroupBody(open);
            return ParseQuantifier(new GroupNode(index, body), groupsBefore);
        }

        int k = _i + 1 < _s.Length ? _s[_i + 1] : -1;
        switch (k)
        {
            case '=':
            case '!':
            {
                _i += 2;
                Node body = ParseGroupBody(open);
                var look = new LookNode(false, k == '!', body);

                // Annex B.1.2: a lookahead is quantifiable in a pattern without u or v; otherwise it is an error.
                if (_u)
                {
                    CheckNoQuantifierAfterAssertion();
                    return look;
                }

                return ParseQuantifier(look, groupsBefore);
            }

            case ':':
            {
                _i += 2;
                Node body = ParseGroupBody(open);
                return ParseQuantifier(body, groupsBefore);
            }

            case '<':
            {
                int k2 = _i + 2 < _s.Length ? _s[_i + 2] : -1;
                if (k2 == '=' || k2 == '!')
                {
                    _i += 3;
                    Node body = ParseGroupBody(open);
                    CheckNoQuantifierAfterAssertion();
                    return new LookNode(true, k2 == '!', body);
                }

                _i++; // '?'
                string name = ParseGroupName(out int nameStart, out int nameLength);
                int index = ++_groupCount;
                int bodyId = _disjunctions + 1;
                Node body2 = ParseGroupBody(open);
                RegisterName(name, index, bodyId, nameStart, nameLength);
                return ParseQuantifier(new GroupNode(index, body2), groupsBefore);
            }

            default:
                return ParseModifierGroup(open, groupsBefore);
        }
    }

    // 22.2.1.4 MightBothParticipate: two groups are exclusive only when they sit in different alternatives of one disjunction.
    private static bool MightBothParticipate(int[] a, int[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int j = 0; j < n; j += 2)
        {
            if (a[j] != b[j])
            {
                return true;
            }

            if (a[j + 1] != b[j + 1])
            {
                return false;
            }
        }

        return true;
    }

    private Node ParseModifierGroup(int open, int groupsBefore)
    {
        _i++; // '?'
        ModeFlags add = ReadModifiers(open, out bool repeatedAdd);
        ModeFlags remove = ModeFlags.None;
        bool dash = false;
        bool repeatedRemove = false;
        if (Cur == '-')
        {
            dash = true;
            _i++;
            remove = ReadModifiers(open, out repeatedRemove);
        }

        if (Cur != ':')
        {
            throw Err(open, Math.Max(2, _i - open + 1), "Invalid group");
        }

        // 22.2.1.1 early errors: a letter may not repeat in a list or appear in both; '(?-:' is empty on both sides.
        if (repeatedAdd || repeatedRemove || (add & remove) != 0)
        {
            throw Err(open, _i - open + 1, "Repeated flag in flag group");
        }

        if (dash && add == ModeFlags.None && remove == ModeFlags.None)
        {
            throw Err(open, _i - open + 1, "Invalid flag group");
        }

        _i++; // ':'
        Node body = ParseGroupBody(open);
        return ParseQuantifier(new ModifierNode(add, remove, body), groupsBefore);
    }

    private ModeFlags ReadModifiers(int open, out bool repeated)
    {
        ModeFlags flags = ModeFlags.None;
        repeated = false;
        _ = open;
        while (true)
        {
            ModeFlags f = Cur switch
            {
                'i' => ModeFlags.IgnoreCase,
                'm' => ModeFlags.Multiline,
                's' => ModeFlags.DotAll,
                _ => ModeFlags.None,
            };
            if (f == ModeFlags.None)
            {
                return flags;
            }

            if ((flags & f) != 0)
            {
                repeated = true;
            }

            flags |= f;
            _i++;
        }
    }

    private Node ParseGroupBody(int open)
    {
        if (++_depth > MaxDepth)
        {
            throw Err(open, 1, "Regular expression too large");
        }

        Node body = ParseDisjunction();
        if (Cur != ')')
        {
            throw Err(open, 1, "Unterminated group");
        }

        _i++;
        _depth--;
        return body;
    }

    // GroupName: '<' RegExpIdentifierName '>' with the cursor on the '<'. Surrogate pairs and \u escapes are
    // always allowed in names, in every mode (22.2.1 RegExpIdentifierStart / RegExpIdentifierPart).
    private string ParseGroupName(out int start, out int length)
    {
        start = _i;
        const string reason = "Invalid capture group name";
        if (Cur != '<')
        {
            throw Err(_i, 1, "Invalid named reference");
        }

        _i++;
        var sb = new StringBuilder();
        bool first = true;
        while (true)
        {
            if (_i >= _s.Length)
            {
                throw Err(start, _i - start, reason);
            }

            int c = _s[_i];
            if (c == '>')
            {
                _i++;
                break;
            }

            int cp;
            if (c == '\\')
            {
                int esc = _i;
                _i++;
                if (Cur != 'u')
                {
                    throw Err(esc, 2, reason);
                }

                _i++;
                if (!TryReadUnicodeEscape(true, out cp))
                {
                    throw Err(esc, _i - esc, "Invalid Unicode escape");
                }
            }
            else
            {
                _i++;
                cp = c;
                if (char.IsHighSurrogate((char)c) && _i < _s.Length && char.IsLowSurrogate(_s[_i]))
                {
                    cp = char.ConvertToUtf32((char)c, _s[_i++]);
                }
            }

            if (cp is >= 0xD800 and <= 0xDFFF || !RegExpUnicode.IsIdentifierChar(cp, first))
            {
                throw Err(start, _i - start, reason);
            }

            sb.Append(char.ConvertFromUtf32(cp));
            first = false;
        }

        length = _i - start;
        if (sb.Length == 0)
        {
            throw Err(start, length, reason);
        }

        return sb.ToString();
    }

    // ECMA-262 22.2.1.1 allows a name to repeat only in different alternatives. V8 (observed, not in the spec text)
    // registers a named group when it closes and treats it as sitting in the last alternative of its own body, so
    // '(?<n>a(?<n>b)|c)' is accepted and '(?<n>a|c(?<n>b))' is not. The error is reported at that moment, so it
    // precedes any syntax error later in the pattern.
    private void RegisterName(string name, int index, int bodyId, int nameStart, int nameLength)
    {
        int[] path = [.. _path, bodyId, _lastAlternative];
        foreach (var earlier in _named)
        {
            if (earlier.Name == name && MightBothParticipate(path, earlier.Path))
            {
                throw Err(nameStart, nameLength, "Duplicate capture group name");
            }
        }

        _named.Add((name, index, path, nameStart, nameLength));
    }

    private void ResolveNamedReferences()
    {
        foreach (var (node, start, length) in _namedRefs)
        {
            var list = new List<int>();
            foreach (var (name, index, _, _, _) in _named.OrderBy(n => n.Index))
            {
                if (name == node.Name)
                {
                    list.Add(index);
                }
            }

            if (list.Count == 0)
            {
                throw Err(start, length, "Invalid named capture referenced");
            }

            node.Groups = [.. list];
        }
    }

    // ---- escapes -----------------------------------------------------------------------------------------

    private Node ParseAtomEscape(out bool quantifiable)
    {
        quantifiable = true;
        int bs = _i;
        _i++;
        if (_i >= _s.Length)
        {
            throw Err(bs, 1, "\\ at end of pattern");
        }

        int c = _s[_i];
        switch (c)
        {
            case 'b':
                _i++;
                quantifiable = false;
                return new AssertNode(AssertKind.WordBoundary);
            case 'B':
                _i++;
                quantifiable = false;
                return new AssertNode(AssertKind.NotWordBoundary);
            case 'd':
            case 'D':
            case 's':
            case 'S':
            case 'w':
            case 'W':
                _i++;
                return new EscapeNode(new ClassEscapeExpr((char)c, null));
            case 'p':
            case 'P':
                if (_u)
                {
                    _i++;
                    return new EscapeNode(ParseProperty(bs, c == 'P', inClass: false));
                }

                break;
            case 'k':
                if (_namedMode)
                {
                    _i++;
                    string name = ParseGroupName(out _, out _);
                    var node = new BackrefNode(name, []);
                    _namedRefs.Add((node, bs, _i - bs));
                    return node;
                }

                break;
            case >= '1' and <= '9':
            {
                int digitsStart = _i;
                long n = 0;
                while (Cur is >= '0' and <= '9')
                {
                    n = Math.Min(int.MaxValue, (n * 10) + (_s[_i] - '0'));
                    _i++;
                }

                if (n <= _totalGroups)
                {
                    return new BackrefNode(null, [(int)n]);
                }

                if (_u)
                {
                    throw Err(bs, _i - bs, "Invalid escape");
                }

                // Annex B.1.2: not a valid backreference, so it is a legacy octal escape or an identity escape.
                _i = digitsStart;
                break;
            }
        }

        return new CharNode(ParseCharacterEscapeValue(bs, inClass: false));
    }

    // CharacterEscape (22.2.1 and Annex B.1.2) with the cursor after the backslash; returns the character value.
    private int ParseCharacterEscapeValue(int bs, bool inClass)
    {
        int c = _s[_i];
        switch (c)
        {
            case 't':
                _i++;
                return 9;
            case 'n':
                _i++;
                return 10;
            case 'v':
                _i++;
                return 11;
            case 'f':
                _i++;
                return 12;
            case 'r':
                _i++;
                return 13;
            case 'c':
            {
                int n = _i + 1 < _s.Length ? _s[_i + 1] : -1;
                if (n is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                {
                    _i += 2;
                    return n % 32;
                }

                if (_u)
                {
                    throw Err(bs, 2, "Invalid Unicode escape");
                }

                // Annex B.1.2: ClassControlLetter inside a class; elsewhere the backslash stands for itself.
                if (inClass && (n is >= '0' and <= '9' || n == '_'))
                {
                    _i += 2;
                    return n % 32;
                }

                return '\\';
            }

            case 'x':
            {
                if (_i + 2 < _s.Length && HexValue(_s[_i + 1]) >= 0 && HexValue(_s[_i + 2]) >= 0)
                {
                    int v = (HexValue(_s[_i + 1]) << 4) | HexValue(_s[_i + 2]);
                    _i += 3;
                    return v;
                }

                if (_u)
                {
                    throw Err(bs, 2, "Invalid escape");
                }

                _i++;
                return 'x';
            }

            case 'u':
            {
                _i++;
                if (TryReadUnicodeEscape(_u, out int cp))
                {
                    return cp;
                }

                if (_u)
                {
                    throw Err(bs, _i - bs, "Invalid Unicode escape");
                }

                return 'u';
            }

            case '0':
            {
                int n = _i + 1 < _s.Length ? _s[_i + 1] : -1;
                if (n is not (>= '0' and <= '9'))
                {
                    _i++;
                    return 0;
                }

                if (_u)
                {
                    throw Err(bs, 3, "Invalid decimal escape");
                }

                return ReadLegacyOctal();
            }

            case >= '1' and <= '7':
                if (_u)
                {
                    throw Err(bs, 2, inClass ? "Invalid decimal escape" : "Invalid escape");
                }

                return ReadLegacyOctal();
            case '8':
            case '9':
                if (_u)
                {
                    throw Err(bs, 2, "Invalid escape");
                }

                _i++;
                return c;
            default:
                if (_u)
                {
                    if (c is '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|' or '/')
                    {
                        _i++;
                        return c;
                    }

                    if (inClass && c == '-')
                    {
                        _i++;
                        return '-';
                    }

                    throw Err(bs, 2, "Invalid escape");
                }

                if (c == 'k' && _namedMode)
                {
                    throw Err(bs, 2, "Invalid escape");
                }

                _i++;
                return c;
        }
    }

    // Annex B.1.2 LegacyOctalEscapeSequence: up to three octal digits, value at most 0377.
    private int ReadLegacyOctal()
    {
        int v = _s[_i++] - '0';
        if (Cur is >= '0' and <= '7')
        {
            int d = _s[_i] - '0';
            if (v <= 3)
            {
                v = (v * 8) + d;
                _i++;
                if (Cur is >= '0' and <= '7')
                {
                    v = (v * 8) + (_s[_i++] - '0');
                }
            }
            else
            {
                v = (v * 8) + d;
                _i++;
            }
        }

        return v;
    }

    private static int HexValue(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    // RegExpUnicodeEscapeSequence with the cursor after the 'u'. On failure the cursor is left after the 'u'.
    private bool TryReadUnicodeEscape(bool unicode, out int cp)
    {
        cp = 0;
        int after = _i;
        if (unicode && Cur == '{')
        {
            int i = _i + 1;
            long v = 0;
            int digits = 0;
            while (i < _s.Length && HexValue(_s[i]) >= 0)
            {
                v = Math.Min(0x110000, (v * 16) + HexValue(_s[i]));
                i++;
                digits++;
            }

            if (digits == 0 || i >= _s.Length || _s[i] != '}' || v > 0x10FFFF)
            {
                return false;
            }

            cp = (int)v;
            _i = i + 1;
            return true;
        }

        int lead = ReadHex4(_i);
        if (lead < 0)
        {
            return false;
        }

        _i += 4;
        cp = lead;
        if (unicode && lead is >= 0xD800 and <= 0xDBFF && _i + 1 < _s.Length && _s[_i] == '\\' && _s[_i + 1] == 'u')
        {
            int trail = ReadHex4(_i + 2);
            if (trail is >= 0xDC00 and <= 0xDFFF)
            {
                cp = char.ConvertToUtf32((char)lead, (char)trail);
                _i += 6;
            }
        }

        _ = after;
        return true;
    }

    private int ReadHex4(int at)
    {
        if (at + 4 > _s.Length)
        {
            return -1;
        }

        int v = 0;
        for (int k = 0; k < 4; k++)
        {
            int h = HexValue(_s[at + k]);
            if (h < 0)
            {
                return -1;
            }

            v = (v << 4) | h;
        }

        return v;
    }

    // \p{..} / \P{..} with the cursor after the 'p' or 'P' (22.2.1 UnicodePropertyValueExpression).
    private ClassEscapeExpr ParseProperty(int bs, bool negated, bool inClass)
    {
        string reason = inClass ? "Invalid property name in character class" : "Invalid property name";
        if (Cur != '{')
        {
            throw Err(bs, _i - bs, reason);
        }

        int close = _s.IndexOf('}', _i);
        if (close < 0)
        {
            throw Err(bs, _s.Length - bs, reason);
        }

        string text = _s.Substring(_i + 1, close - _i - 1);
        _i = close + 1;
        int eq = text.IndexOf('=', StringComparison.Ordinal);
        string name = eq < 0 ? text : text[..eq];
        string? value = eq < 0 ? null : text[(eq + 1)..];
        if (!RegExpUnicode.TryResolveProperty(name, value, _v, out PropertyRef r) || (negated && r.IsStrings))
        {
            throw Err(bs, _i - bs, reason);
        }

        return new ClassEscapeExpr(negated ? 'P' : 'p', r) { MayContainStrings = r.IsStrings };
    }

    // ---- character classes -------------------------------------------------------------------------------

    private ClassNode ParseClass()
    {
        int open = _i;
        _i++;
        bool negate = false;
        if (Cur == '^')
        {
            negate = true;
            _i++;
        }

        if (++_depth > MaxDepth)
        {
            throw Err(open, 1, "Regular expression too large");
        }

        ClassExpr expr = _v ? ParseClassSetContents(open) : ParseClassRanges(open);
        _depth--;
        if (negate && expr.MayContainStrings)
        {
            throw Err(open, _i - open, "Negated character class may contain strings");
        }

        return new ClassNode(expr, negate, expr.MayContainStrings);
    }

    // ClassContents[~UnicodeSetsMode]: NonemptyClassRanges and the Annex B.1.2 variants. Consumes the closing ']'.
    private ClassUnion ParseClassRanges(int open)
    {
        var items = new List<ClassExpr>();
        while (true)
        {
            if (_i >= _s.Length)
            {
                throw Err(open, 1, "Unterminated character class");
            }

            if (_s[_i] == ']')
            {
                _i++;
                return new ClassUnion(items);
            }

            int atomStart = _i;
            var (cp, esc) = ParseClassAtom();
            if (Cur == '-' && _i + 1 < _s.Length && _s[_i + 1] != ']')
            {
                int dash = _i;
                _i++;
                var (cp2, esc2) = ParseClassAtom();
                if (esc is not null || esc2 is not null)
                {
                    if (_u)
                    {
                        throw Err(atomStart, _i - atomStart, "Invalid character class");
                    }

                    // Annex B.1.2 CharacterRangeOrUnion: with a class escape on either side the '-' is literal.
                    items.Add((ClassExpr?)esc ?? new ClassChar(cp));
                    items.Add(new ClassChar('-'));
                    items.Add((ClassExpr?)esc2 ?? new ClassChar(cp2));
                    _ = dash;
                }
                else
                {
                    if (cp > cp2)
                    {
                        throw Err(atomStart, _i - atomStart, "Range out of order in character class");
                    }

                    items.Add(new ClassRange(cp, cp2));
                }
            }
            else
            {
                items.Add((ClassExpr?)esc ?? new ClassChar(cp));
            }
        }
    }

    private (int Cp, ClassEscapeExpr? Escape) ParseClassAtom()
    {
        if (_i >= _s.Length)
        {
            throw Err(_s.Length - 1, 1, "Unterminated character class");
        }

        if (_s[_i] != '\\')
        {
            return (ReadSourceChar(), null);
        }

        int bs = _i;
        _i++;
        if (_i >= _s.Length)
        {
            throw Err(bs, 1, "\\ at end of pattern");
        }

        int c = _s[_i];
        switch (c)
        {
            case 'b':
                _i++;
                return (8, null);
            case 'd':
            case 'D':
            case 's':
            case 'S':
            case 'w':
            case 'W':
                _i++;
                return (-1, new ClassEscapeExpr((char)c, null));
            case 'p':
            case 'P':
                if (_u)
                {
                    _i++;
                    return (-1, ParseProperty(bs, c == 'P', inClass: true));
                }

                break;
        }

        return (ParseCharacterEscapeValue(bs, inClass: true), null);
    }

    // ---- v-mode classes (ClassSetExpression) -------------------------------------------------------------

    private const string ReservedDouble = "&!#$%*+,.:;<=>?@^`~";
    private const string ReservedPunctuator = "&-!#%,:;<=>@`~";

    private ClassExpr ParseClassSetContents(int open)
    {
        var operands = new List<ClassExpr>();
        int mode = 0; // 0 union, 1 intersection, 2 subtraction
        bool hasRange = false;
        bool afterRange = false;
        while (true)
        {
            if (_i >= _s.Length)
            {
                throw Err(open, 1, "Unterminated character class");
            }

            char c = _s[_i];
            if (c == ']')
            {
                _i++;
                break;
            }

            bool isAnd = c == '&' && _i + 1 < _s.Length && _s[_i + 1] == '&';
            bool isSub = c == '-' && _i + 1 < _s.Length && _s[_i + 1] == '-';
            if (isSub && operands.Count == 0)
            {
                // A leading '--' is a ClassSetSyntaxCharacter used as an operand, not a set operation.
                throw Err(_i, 1, "Invalid character in character class");
            }

            if (isAnd || isSub)
            {
                int want = isAnd ? 1 : 2;
                if (operands.Count == 0 || hasRange || (mode == 0 && operands.Count != 1) || (mode != 0 && mode != want))
                {
                    throw Err(_i, 2, "Invalid set operation in character class");
                }

                if (isAnd && _i + 2 < _s.Length && _s[_i + 2] == '&')
                {
                    throw Err(_i, 3, "Invalid character in character class");
                }

                mode = want;
                _i += 2;
                ClassExpr rhs = ParseClassSetOperand(out _);
                operands.Add(rhs);
                continue;
            }

            if (mode != 0)
            {
                throw Err(_i, 1, "Invalid set operation in character class");
            }

            if (c == '-' && afterRange)
            {
                // A '-' right after a completed range, as in [a-z-]; V8 words it as a bad class, not a bad character.
                throw Err(_i, 1, "Invalid character class");
            }

            afterRange = false;
            ClassExpr first = ParseClassSetOperand(out int single);
            if (single >= 0 && _i + 1 < _s.Length && _s[_i] == '-' && _s[_i + 1] != '-')
            {
                int rangeStart = _i;
                _i++;
                ClassExpr second = ParseClassSetOperand(out int single2);
                if (single2 < 0)
                {
                    throw Err(rangeStart, _i - rangeStart, "Invalid character class");
                }

                if (single > single2)
                {
                    throw Err(rangeStart - 1, _i - rangeStart + 1, "Range out of order in character class");
                }

                operands.Add(new ClassRange(single, single2));
                hasRange = true;
                afterRange = true;
                _ = second;
                continue;
            }

            if (single < 0 && _i + 1 < _s.Length && _s[_i] == '-' && _s[_i + 1] != '-')
            {
                // A range needs a single character on its left.
                throw Err(_i, 1, "Invalid character class");
            }

            operands.Add(first);
        }

        return mode switch
        {
            1 => new ClassIntersection(operands) { MayContainStrings = operands.TrueForAll(static o => o.MayContainStrings) },
            2 => new ClassSubtraction(operands) { MayContainStrings = operands[0].MayContainStrings },
            _ => new ClassUnion(operands) { MayContainStrings = operands.Exists(static o => o.MayContainStrings) },
        };
    }

    // ClassSetOperand. 'single' is the character value when the operand is a lone ClassSetCharacter, else -1.
    private ClassExpr ParseClassSetOperand(out int single)
    {
        single = -1;
        if (_i >= _s.Length)
        {
            throw Err(_s.Length - 1, 1, "Unterminated character class");
        }

        char c = _s[_i];
        if (c == '[')
        {
            int open = _i;
            _i++;
            bool negate = false;
            if (Cur == '^')
            {
                negate = true;
                _i++;
            }

            if (++_depth > MaxDepth)
            {
                throw Err(open, 1, "Regular expression too large");
            }

            ClassExpr inner = ParseClassSetContents(open);
            _depth--;
            if (negate && inner.MayContainStrings)
            {
                throw Err(open, _i - open, "Negated character class may contain strings");
            }

            return new ClassNested(inner, negate) { MayContainStrings = !negate && inner.MayContainStrings };
        }

        if (c == '\\')
        {
            int bs = _i;
            _i++;
            if (_i >= _s.Length)
            {
                throw Err(bs, 1, "\\ at end of pattern");
            }

            char e = _s[_i];
            switch (e)
            {
                case 'q':
                    _i++;
                    return ParseClassStrings(bs);
                case 'd':
                case 'D':
                case 's':
                case 'S':
                case 'w':
                case 'W':
                    _i++;
                    return new ClassEscapeExpr(e, null);
                case 'p':
                case 'P':
                    _i++;
                    return ParseProperty(bs, e == 'P', inClass: true);
                case 'b':
                    _i++;
                    single = 8;
                    return new ClassChar(8);
            }

            if (ReservedPunctuator.Contains(e, StringComparison.Ordinal))
            {
                _i++;
                single = e;
                return new ClassChar(e);
            }

            single = ParseCharacterEscapeValue(bs, inClass: true);
            return new ClassChar(single);
        }

        if (c is '(' or ')' or '{' or '}' or '/' or '-' or '|' or ']')
        {
            throw Err(_i, 1, "Invalid character in character class");
        }

        if (ReservedDouble.Contains(c, StringComparison.Ordinal) && _i + 1 < _s.Length && _s[_i + 1] == c)
        {
            throw Err(_i, 2, "Invalid set operation in character class");
        }

        single = ReadSourceChar();
        return new ClassChar(single);
    }

    // \q{ClassString|ClassString...} with the cursor after the 'q'.
    private ClassStrings ParseClassStrings(int bs)
    {
        if (Cur != '{')
        {
            throw Err(bs, 2, "Invalid escape");
        }

        _i++;
        var strings = new List<int[]>();
        var current = new List<int>();
        bool mayContainStrings = false;
        while (true)
        {
            if (_i >= _s.Length)
            {
                throw Err(bs, _i - bs, "Unterminated character class");
            }

            char c = _s[_i];
            if (c == '}' || c == '|')
            {
                _i++;
                strings.Add([.. current]);
                if (current.Count != 1)
                {
                    mayContainStrings = true;
                }

                current.Clear();
                if (c == '}')
                {
                    return new ClassStrings(strings) { MayContainStrings = mayContainStrings };
                }

                continue;
            }

            if (c == '\\')
            {
                int esc = _i;
                _i++;
                if (_i >= _s.Length)
                {
                    throw Err(esc, 1, "\\ at end of pattern");
                }

                char e = _s[_i];
                if (e == 'b')
                {
                    _i++;
                    current.Add(8);
                }
                else if (ReservedPunctuator.Contains(e, StringComparison.Ordinal))
                {
                    _i++;
                    current.Add(e);
                }
                else
                {
                    current.Add(ParseCharacterEscapeValue(esc, inClass: true));
                }

                continue;
            }

            if (c is '(' or ')' or '[' or ']' or '{' or '/' or '-')
            {
                throw Err(_i, 1, "Invalid character in character class");
            }

            if (ReservedDouble.Contains(c, StringComparison.Ordinal) && _i + 1 < _s.Length && _s[_i + 1] == c)
            {
                throw Err(_i, 2, "Invalid set operation in character class");
            }

            current.Add(ReadSourceChar());
        }
    }
}

/// <summary>The one error a parse stops at; carries the exact span.</summary>
internal sealed class PatternException : Exception
{
    internal PatternException(int start, int length, string reason)
        : base(reason)
    {
        Start = start;
        Length = length;
        Reason = reason;
    }

    internal int Start { get; }

    internal int Length { get; }

    internal string Reason { get; }
}
